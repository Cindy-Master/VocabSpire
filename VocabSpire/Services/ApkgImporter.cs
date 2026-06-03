using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VocabSpire.Models;

namespace VocabSpire.Services;

/// <summary>
/// 把 Anki .apkg 词库导入为 VocabSpire 词库（WordBank）。
///
/// 流程：apkg(zip) → 取 collection 数据库 → <see cref="MiniSqliteReader"/> 读 col/notes
/// → 解析 note type 字段定义 → 自动判定哪个字段是「英文词 / 中文释义 / 音标」
/// （内容检测：ASCII=英文、CJK=中文、IPA 字符=音标，叠加字段名启发式）
/// → 清洗 HTML 标签与 [sound:] 标记 → 同一英文词的多条 note 聚合成多义项。
///
/// 目前支持 collection.anki2 / collection.anki21（纯 SQLite）。新版 collection.anki21b
/// （zstd 压缩）会抛出友好提示，建议用 Anki 旧版格式重新导出。
/// </summary>
public static class ApkgImporter
{
    private const int MaxDefsPerWord = 8;

    private static readonly Regex SoundTag = new(@"\[sound:[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex BrTag = new(@"<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnyTag = new(@"<[^>]+>", RegexOptions.Compiled);

    private static readonly char[] IpaChars =
        "ˈˌːɪɛæʌɑɒɔəɜʊθðʃʒŋɡɹɝɚɫʔˑʰ".ToCharArray();

    /// <summary>导入 apkg，返回 WordBank。失败抛异常（调用方负责 Log）。</summary>
    public static WordBank Import(string apkgPath)
    {
        byte[] dbBytes = ExtractCollectionDb(apkgPath);
        var reader = new MiniSqliteReader(dbBytes);

        // note type 字段定义：旧版(anki2/anki21)在 col.models(JSON)，
        // 新版(anki21b)在 notetypes+fields 表
        var modelFields = GetModelFields(reader);
        if (modelFields.Count == 0)
            throw new InvalidDataException("apkg 缺少 note type 字段定义（col.models 与 notetypes 表均为空）。");

        // 所有 note：mid（note type id）+ flds（各字段值，\x1f 分隔）
        var notes = reader.ReadTable("notes");
        if (notes.Count == 0)
            throw new InvalidDataException("apkg 内没有任何卡片（notes 表为空）。");

        // 按 model 分组
        var byModel = new Dictionary<string, List<string[]>>();
        foreach (var n in notes)
        {
            string mid = Convert.ToInt64(n.GetValueOrDefault("mid") ?? 0L).ToString();
            string flds = n.GetValueOrDefault("flds") as string ?? "";
            if (flds.Length == 0) continue;
            if (!byModel.TryGetValue(mid, out var list)) byModel[mid] = list = new List<string[]>();
            list.Add(flds.Split('\x1f'));
        }

        // 聚合：english.lower → 条目（保持首次出现顺序）
        var order = new List<string>();
        var agg = new Dictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);

        foreach (var (mid, rows) in byModel)
        {
            var fnames = modelFields.TryGetValue(mid, out var fn) && fn.Count > 0
                ? fn
                : DefaultFieldNames(rows);

            var (en, cn, ph) = DetectRoles(fnames, rows);
            if (en < 0 || cn < 0) continue; // 这个 note type 找不到英文词或中文释义，跳过

            foreach (var parts in rows)
            {
                int need = Math.Max(en, Math.Max(cn, ph));
                if (need >= parts.Length) continue;

                string word = Clean(parts[en]);
                string cdef = Clean(parts[cn]);
                if (word.Length == 0 || cdef.Length == 0) continue;
                if (HasCjk(word)) continue; // 英文词字段不该含中文（过滤表头/脏行/方向错的列）

                // 表头行特征：英文词字段的值恰好等于它的字段名（如值="Word"）。
                // 用「值==字段名」精确判定，避免把 term/word/front 这类真实单词误当表头。
                if (string.Equals(word, fnames[en], StringComparison.OrdinalIgnoreCase)) continue;
                string lower = word.ToLowerInvariant();

                string phon = ph >= 0 && ph < parts.Length ? Clean(parts[ph]) : "";

                if (!agg.TryGetValue(lower, out var a))
                {
                    a = new Agg { English = word };
                    agg[lower] = a;
                    order.Add(lower);
                }
                if (a.Defs.Count < MaxDefsPerWord && !a.Defs.Contains(cdef))
                    a.Defs.Add(cdef);
                if (a.Phonetic.Length == 0 && phon.Length > 0)
                    a.Phonetic = phon.StartsWith("/") ? phon : $"/{phon}/";
            }
        }

        var words = new List<WordEntry>(order.Count);
        foreach (var key in order)
        {
            var a = agg[key];
            if (a.Defs.Count == 0) continue;
            words.Add(new WordEntry
            {
                English = a.English,
                Chinese = string.Join("; ", a.Defs),
                Definitions = a.Defs,
                Phonetic = a.Phonetic
            });
        }

        if (words.Count < 2)
            throw new InvalidDataException(
                "未能从该 apkg 提取出有效词条（可能字段不是「英文单词 + 中文释义」结构）。");

        string id = Path.GetFileNameWithoutExtension(apkgPath);
        return new WordBank
        {
            Id = id,
            Name = id.Replace('_', ' ').Replace('-', ' '),
            Description = $"从 Anki 词库 {Path.GetFileName(apkgPath)} 导入（{words.Count} 词）。",
            SourcePath = apkgPath,
            Words = words
        };
    }

    private sealed class Agg
    {
        public string English = "";
        public readonly List<string> Defs = new();
        public string Phonetic = "";
    }

    // ── apkg 解压 ───────────────────────────────────────────────────────────

    private static byte[] ExtractCollectionDb(string apkgPath)
    {
        using var zip = ZipFile.OpenRead(apkgPath);

        // 新版 Anki 2.1.50+：collection.anki21b 是 zstd 压缩的 SQLite
        var b21 = zip.GetEntry("collection.anki21b");
        if (b21 != null)
        {
            using var cs = b21.Open();
            using var compressed = new MemoryStream();
            cs.CopyTo(compressed);
            compressed.Position = 0;
            using var ds = new ZstdSharp.DecompressionStream(compressed);
            using var outMs = new MemoryStream();
            ds.CopyTo(outMs);
            return outMs.ToArray();
        }

        var entry = zip.GetEntry("collection.anki21") ?? zip.GetEntry("collection.anki2")
            ?? throw new InvalidDataException("apkg 内未找到 collection 数据库。");

        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>取每个 note type 的字段名列表：优先旧版 col.models(JSON)，回退新版 notetypes+fields 表。</summary>
    private static Dictionary<string, List<string>> GetModelFields(MiniSqliteReader reader)
    {
        var col = reader.ReadTable("col");
        if (col.Count > 0 && col[0].TryGetValue("models", out var m) && m is string js && js.Trim().Length > 2)
        {
            var fromJson = ParseModelFieldsJson(js);
            if (fromJson.Count > 0) return fromJson;
        }
        return ParseModelFieldsTables(reader);
    }

    private static Dictionary<string, List<string>> ParseModelFieldsJson(string modelsJson)
    {
        var result = new Dictionary<string, List<string>>();
        try
        {
            using var doc = JsonDocument.Parse(modelsJson);
            foreach (var model in doc.RootElement.EnumerateObject())
            {
                if (!model.Value.TryGetProperty("flds", out var flds) || flds.ValueKind != JsonValueKind.Array)
                    continue;
                var named = new List<(int ord, string name)>();
                foreach (var f in flds.EnumerateArray())
                {
                    string name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    int ord = f.TryGetProperty("ord", out var o) && o.ValueKind == JsonValueKind.Number
                        ? o.GetInt32() : named.Count;
                    named.Add((ord, name));
                }
                named.Sort((a, b) => a.ord.CompareTo(b.ord));
                result[model.Name] = named.ConvertAll(x => x.name);
            }
        }
        catch (JsonException) { /* 不是合法 models JSON，回退到表方式 */ }
        return result;
    }

    /// <summary>新版 anki21b：字段定义在 notetypes(id,name) + fields(ntid,ord,name) 两张表。</summary>
    private static Dictionary<string, List<string>> ParseModelFieldsTables(MiniSqliteReader reader)
    {
        var result = new Dictionary<string, List<string>>();
        var notetypes = reader.ReadTable("notetypes");
        var fields = reader.ReadTable("fields");
        if (notetypes.Count == 0 || fields.Count == 0) return result;

        var byNtid = new Dictionary<string, List<(long ord, string name)>>();
        foreach (var f in fields)
        {
            string ntid = Convert.ToInt64(f.GetValueOrDefault("ntid") ?? 0L).ToString();
            long ord = Convert.ToInt64(f.GetValueOrDefault("ord") ?? 0L);
            string name = f.GetValueOrDefault("name") as string ?? "";
            if (!byNtid.TryGetValue(ntid, out var l)) byNtid[ntid] = l = new List<(long, string)>();
            l.Add((ord, name));
        }
        foreach (var nt in notetypes)
        {
            string id = Convert.ToInt64(nt.GetValueOrDefault("id") ?? 0L).ToString();
            if (!byNtid.TryGetValue(id, out var l)) continue;
            l.Sort((a, b) => a.ord.CompareTo(b.ord));
            result[id] = l.ConvertAll(x => x.name);
        }
        return result;
    }

    private static List<string> DefaultFieldNames(List<string[]> rows)
    {
        int n = rows.Count > 0 ? rows[0].Length : 0;
        var names = new List<string>(n);
        for (int i = 0; i < n; i++) names.Add($"#{i}");
        return names;
    }

    // ── 字段角色检测 ────────────────────────────────────────────────────────

    private sealed class ColStat { public double Cjk, Asc, Ipa, AvgLen; public bool Any; }

    private static (int en, int cn, int ph) DetectRoles(List<string> fnames, List<string[]> rows)
    {
        int nc = fnames.Count;
        var low = fnames.ConvertAll(f => f.ToLowerInvariant());
        var stat = new ColStat[nc];
        int sample = Math.Min(rows.Count, 300);
        for (int c = 0; c < nc; c++) stat[c] = ColStats(rows, c, sample);

        // 音标：字段名命中 ipa/phonetic/pronunciation（排除 audio/sound/example/url），否则 IPA 字符占比高的列
        int ph = NameHit(low, new[] { "ipa", "phonetic", "pronunciation", "音标" },
                              new[] { "audio", "sound", "example", "url", "媒体" });
        if (ph < 0)
        {
            double best = 0.4; int bi = -1;
            for (int c = 0; c < nc; c++)
                if (stat[c].Any && stat[c].Cjk < 0.2 && stat[c].Ipa > best) { best = stat[c].Ipa; bi = c; }
            ph = bi;
        }

        // 中文释义：字段名优先中文翻译列，否则 CJK 占比最高的列
        int cn = NameHit(low, new[] { "definitiontr", "transcn", "translation", "释义", "中文", "翻译", "义项", "解释" },
                              new[] { "audio", "ipa" });
        if (cn < 0 || !stat[cn].Any || stat[cn].Cjk <= 0.3)
        {
            double best = 0.3; int bi = -1;
            for (int c = 0; c < nc; c++)
                if (stat[c].Any && stat[c].Cjk > best) { best = stat[c].Cjk; bi = c; }
            cn = bi;
        }

        // 英文词：字段名优先 word/headword/spelling/term，否则 ASCII 主导且短、非 CJK、非音标的列
        int en = NameHit(low, new[] { "headword", "wordhead", "word", "spelling", "term", "单词", "lemma" },
                              new[] { "password", "keyword", "wordid", "reword" });
        if (en < 0)
        {
            double bestLen = double.MaxValue; int bi = -1;
            for (int c = 0; c < nc; c++)
            {
                if (c == ph || c == cn || !stat[c].Any) continue;
                if (stat[c].Asc > 0.45 && stat[c].Cjk < 0.1 && stat[c].AvgLen < 30 && stat[c].AvgLen < bestLen)
                { bestLen = stat[c].AvgLen; bi = c; }
            }
            en = bi;
        }

        return (en, cn, ph);
    }

    private static ColStat ColStats(List<string[]> rows, int col, int sample)
    {
        var st = new ColStat();
        int n = 0;
        for (int r = 0; r < sample; r++)
        {
            if (col >= rows[r].Length) continue;
            string v = Clean(rows[r][col]);
            if (v.Length == 0) continue;
            n++;
            if (HasCjk(v)) st.Cjk += 1;
            int letters = 0;
            foreach (char ch in v) if (ch < 128 && char.IsLetter(ch)) letters++;
            st.Asc += (double)letters / v.Length;
            foreach (char ch in v) if (Array.IndexOf(IpaChars, ch) >= 0) { st.Ipa += 1; break; }
            st.AvgLen += v.Length;
        }
        if (n > 0)
        {
            st.Any = true;
            st.Cjk /= n; st.Asc /= n; st.Ipa /= n; st.AvgLen /= n;
        }
        return st;
    }

    private static int NameHit(List<string> low, string[] pats, string[] exclude)
    {
        for (int i = 0; i < low.Count; i++)
        {
            string f = low[i];
            bool hit = false;
            foreach (var p in pats) if (f.Contains(p)) { hit = true; break; }
            if (!hit) continue;
            bool ex = false;
            foreach (var e in exclude) if (f.Contains(e)) { ex = true; break; }
            if (!ex) return i;
        }
        return -1;
    }

    // ── 文本清洗 ────────────────────────────────────────────────────────────

    private static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = SoundTag.Replace(s, "");
        s = BrTag.Replace(s, "; ");
        s = AnyTag.Replace(s, "");
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
             .Replace("&gt;", ">").Replace("&#39;", "'").Replace("&quot;", "\"");
        return string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static bool HasCjk(string s)
    {
        foreach (char c in s) if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
    }
}
