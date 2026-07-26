using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;

namespace VocabSpire.Services;

/// <summary>
/// 把 Anki .apkg 词库导入为 VocabSpire 词库（WordBank）。
///
/// 流程：apkg(zip) → 取 collection 数据库 → <see cref="MiniSqliteReader"/> 读 col/notes
/// → 解析 note type 字段定义 → 自动判定哪个字段是「单词 / 释义 / 音标·读音」
/// （字段名多语言启发式 + 内容检测：长度/词数定单词、CJK 或最长列定释义、IPA/拼音/假名定读音）
/// → 清洗 HTML 标签与 [sound:] 标记 → 同一单词的多条 note 聚合成多义项。
/// 支持任意语种词库（法→中、法→英、日→英、德→中…），不再局限「英文单词 + 中文释义」。
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

    // 多语言「字段角色」名 —— 只放角色词，不放具体语言名（如 english/chinese），避免
    // 「英→中的 English 列」与「法→英的 English 列」角色相反时误判。语言本身交给内容检测。
    private static readonly string[] WordNames =
    {
        "word", "term", "headword", "expression", "vocab", "spelling", "lemma",
        "front", "question", "recto", "vorderseite", "mot", "palabra", "parola",
        "wort", "vokabel", "単語", "단어", "单词", "词条", "生词", "正面", "词头"
    };
    private static readonly string[] WordExclude =
    {
        "password", "keyword", "wordid", "reword", "audio", "sound", "image", "media", "example"
    };
    private static readonly string[] DefNames =
    {
        "definition", "meaning", "translation", "translat", "back", "answer",
        "sense", "gloss", "explanation", "notes", "verso", "ruckseite", "rückseite",
        "traduction", "signification", "bedeutung", "ubersetzung", "übersetzung",
        "significado", "意味", "定義", "意思", "解释", "释义", "翻译", "义项",
        "含义", "注释", "背面", "訳", "裏", "뜻", "의미"
    };
    private static readonly string[] DefExclude =
    {
        "audio", "ipa", "sound", "image", "url", "媒体", "video", "phonetic", "pron"
    };
    private static readonly string[] PhoneticNames =
    {
        "ipa", "phonetic", "pronunciation", "prononciation", "aussprache",
        "pronunciacion", "pronunciación", "音标", "発音", "拼音", "pinyin",
        "reading", "読み", "kana", "仮名", "furigana", "romaji", "注音", "yomi"
    };
    private static readonly string[] PhoneticExclude =
    {
        "audio", "sound", "example", "passage", "url", "媒体", "video", "image"
    };

    // 题库字段名匹配（Question/Options/Answers 及中文变体）
    private static readonly string[] QuestionNames =
        { "question", "题干", "题目", "问题", "stem" };
    private static readonly string[] OptionsNames =
        { "options", "option", "选项", "choices", "choice" };
    private static readonly string[] AnswersNames =
        { "answers", "answer", "答案", "correct", "正确答案" };
    private static readonly string[] RemarksNames =
        { "remarks", "remark", "备注", "解析", "explanation", "解答", "notes" };

    /// <summary>导入 apkg，返回 WordBank。失败抛异常（调用方负责 Log）。</summary>
    public static WordBank Import(string apkgPath)
    {
        byte[] dbBytes = ExtractCollectionDb(apkgPath);
        var reader = new MiniSqliteReader(dbBytes);

        // note type 字段定义：旧版(anki2/anki21)在 col.models(JSON)，
        // 新版(anki21b)在 notetypes+fields 表
        var modelFields = GetModelFields(reader);
        if (modelFields.Count == 0)
        {
            // 诊断：列出 DB 里有哪些表、col.models 长什么样、notetypes/fields 表有几行
            var tables = new[] { "col", "notes", "notetypes", "fields" }; // 只列关键表
            var col = reader.ReadTable("col");
            var modelsRaw = col.Count > 0 && col[0].TryGetValue("models", out var mv) ? (mv as string ?? "(非字符串)") : "(无)";
            var ntCount = reader.ReadTable("notetypes").Count;
            var fldsCount = reader.ReadTable("fields").Count;
            MegaCrit.Sts2.Core.Logging.Log.Error(
                $"[VocabSpire] apkg 字段定义诊断: 表=[{string.Join(",", tables)}] " +
                $"col.models前100字={modelsRaw[..Math.Min(100, modelsRaw.Length)]} " +
                $"notetypes行数={ntCount} fields行数={fldsCount}");
            throw new InvalidDataException("apkg 缺少 note type 字段定义（col.models 与 notetypes 表均为空）。详细诊断已写入日志。");
        }

        // 所有 note：mid（note type id）+ flds（各字段值，\x1f 分隔）
        var notes = reader.ReadTable("notes");
        if (notes.Count == 0)
            throw new InvalidDataException("apkg 内没有任何卡片（notes 表为空）。");

        // ── AnkiChinas 加密检测与解密 ──
        bool encrypted = AnkiChinasDecryptor.IsEncrypted(notes);
        Log.Info($"[VocabSpire] 加密检测: encrypted={encrypted}, notes={notes.Count}");
        if (encrypted)
        {
            var encParams = ExtractEncryptionParams(reader);
            if (encParams == null)
                throw new InvalidDataException("检测到加密内容但无法提取解密参数（_ck/cpk/spk 缺失）。");

            Log.Info($"[VocabSpire] 加密参数提取成功: ck={encParams.ClientKey[..8]}..., iv={encParams.AesIv}, url={encParams.ApiBaseUrl}");
            string aesKey = AnkiChinasDecryptor.FetchAesKey(encParams);
            Log.Info($"[VocabSpire] AES密钥获取成功: key_len={aesKey.Length}");
            AnkiChinasDecryptor.DecryptNotes(notes, aesKey, encParams.AesIv);
            Log.Info("[VocabSpire] 解密完成");
        }

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
        var quizWords = new List<WordEntry>();

        foreach (var (mid, rows) in byModel)
        {
            var fnames = modelFields.TryGetValue(mid, out var fn) && fn.Count > 0
                ? fn
                : DefaultFieldNames(rows);

            // ── 优先尝试题库格式（Question/Options/Answers） ──
            var quiz = TryParseQuizBank(fnames, rows);
            if (quiz != null)
            {
                Log.Info($"[VocabSpire] 题库格式识别成功: model={mid}, 题数={quiz.Count}");
                quizWords.AddRange(quiz);
                continue;
            }
            Log.Info($"[VocabSpire] 非题库格式 model={mid}, fnames=[{string.Join(", ", fnames)}], 尝试词汇模式");

            var (en, cn, ph) = DetectRoles(fnames, rows);
            if (en < 0 || cn < 0) continue;

            foreach (var parts in rows)
            {
                int need = Math.Max(en, Math.Max(cn, ph));
                if (need >= parts.Length) continue;

                string word = Clean(parts[en]);
                string cdef = Clean(parts[cn]);
                if (word.Length == 0 || cdef.Length == 0) continue;
                if (word == cdef) continue;

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

        var words = new List<WordEntry>(order.Count + quizWords.Count);
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
        words.AddRange(quizWords);

        if (words.Count < 2)
        {
            // 详细诊断日志：帮定位为什么识别不出
            var diag = new System.Text.StringBuilder();
            diag.AppendLine("=== ApkgImporter 导入失败诊断 ===");
            diag.AppendLine($"  notes 总数: {notes.Count} | modelFields 数: {modelFields.Count}");
            foreach (var (mid, fn) in modelFields)
                diag.AppendLine($"  model {mid}: 字段名=[{string.Join(", ", fn)}]");
            foreach (var (mid, rows) in byModel)
            {
                var fn = modelFields.TryGetValue(mid, out var f) ? f : DefaultFieldNames(rows);
                diag.AppendLine($"  model {mid}: 行数={rows.Count} 字段数={fn.Count}");
                if (rows.Count > 0)
                {
                    var sample = rows[0];
                    for (var ci = 0; ci < Math.Min(fn.Count, sample.Length); ci++)
                    {
                        var raw = sample[ci];
                        var isEnc = raw.Contains("≯#") || raw.Contains("#≮");
                        var preview = raw.Length > 60 ? raw[..60] + "…" : raw;
                        diag.AppendLine($"    [{fn[ci]}] len={raw.Length} enc={isEnc} → {preview}");
                    }
                }
            }
            MegaCrit.Sts2.Core.Logging.Log.Error($"[VocabSpire] {diag}");
            throw new InvalidDataException(
                "未能从该 apkg 提取出有效词条（无法识别「单词 + 释义」或「题干 + 选项 + 答案」）。详细诊断已写入日志。");
        }

        string id = Path.GetFileNameWithoutExtension(apkgPath);
        string desc = quizWords.Count > 0
            ? $"从 Anki 题库 {Path.GetFileName(apkgPath)} 导入（{words.Count} 题）。"
            : $"从 Anki 词库 {Path.GetFileName(apkgPath)} 导入（{words.Count} 词）。";

        return new WordBank
        {
            Id = id,
            Name = id.Replace('_', ' ').Replace('-', ' '),
            Description = desc,
            SourcePath = apkgPath,
            Words = words
        };
    }

    /// <summary>
    /// 尝试按题库格式（Question/Options/Answers）解析。
    /// 字段名匹配到 question + options + answers 即识别为题库，返回固定选择题列表；否则 null。
    /// </summary>
    private static List<WordEntry>? TryParseQuizBank(List<string> fnames, List<string[]> rows)
    {
        var low = fnames.ConvertAll(f => f.ToLowerInvariant());
        int qi = NameHitExact(low, QuestionNames);
        int oi = NameHitExact(low, OptionsNames);
        int ai = NameHitExact(low, AnswersNames);
        Log.Info($"[VocabSpire] TryParseQuizBank: qi={qi} oi={oi} ai={ai} (fnames=[{string.Join(",", low)}])");
        if (qi < 0 || oi < 0 || ai < 0) return null;

        if (rows.Count > 0)
        {
            var sample = rows[0];
            string sq = qi < sample.Length ? sample[qi] : "N/A";
            string so = oi < sample.Length ? sample[oi] : "N/A";
            string sa = ai < sample.Length ? sample[ai] : "N/A";
            Log.Info($"[VocabSpire] 题库首条采样: Q=[{sq[..Math.Min(60, sq.Length)]}] O=[{so[..Math.Min(60, so.Length)]}] A=[{sa}]");
        }

        int ri = NameHitExact(low, RemarksNames);
        var result = new List<WordEntry>(rows.Count);

        foreach (var parts in rows)
        {
            if (qi >= parts.Length || oi >= parts.Length || ai >= parts.Length) continue;

            string question = Clean(parts[qi]);
            string rawOpts = Clean(parts[oi]);
            string rawAns = Clean(parts[ai]);
            if (question.Length == 0 || rawOpts.Length == 0 || rawAns.Length == 0) continue;

            // 选项分隔：||  或  \n  或  <br>（AnkiChinas 用 ||）
            var opts = rawOpts.Contains("||")
                ? rawOpts.Split("||", StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList()
                : rawOpts.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

            if (opts.Count < 2) continue;

            // 答案解析：数字（1-based 或 0-based）、字母（A/B/C/D）
            int ansIdx = ParseAnswerIndex(rawAns, opts.Count);
            if (ansIdx < 0 || ansIdx >= opts.Count) continue;

            string remark = ri >= 0 && ri < parts.Length ? Clean(parts[ri]) : "";
            string correctText = opts[ansIdx];
            var defs = new List<string> { correctText };
            if (remark.Length > 0) defs.Add(remark);

            result.Add(new WordEntry
            {
                English = question,
                Chinese = correctText,
                Definitions = defs,
                Options = opts,
                FixedCorrectIndex = ansIdx
            });
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>解析答案索引：支持数字（1-based）和字母（A=0, B=1, ...）。</summary>
    private static int ParseAnswerIndex(string raw, int optCount)
    {
        raw = raw.Trim();
        if (raw.Length == 1 && char.IsLetter(raw[0]))
        {
            char c = char.ToUpper(raw[0]);
            return c - 'A';
        }
        if (int.TryParse(raw, out int num))
        {
            // 1-based 更常见（AnkiChinas 用 1-based），但也兼容 0-based
            if (num >= 1 && num <= optCount) return num - 1;
            if (num >= 0 && num < optCount) return num;
        }
        return -1;
    }

    /// <summary>精确字段名匹配（不含 exclude 检查的简化版本）。</summary>
    private static int NameHitExact(List<string> low, string[] pats)
    {
        for (int i = 0; i < low.Count; i++)
        {
            string f = low[i];
            foreach (var p in pats)
                if (f.Contains(p)) return i;
        }
        return -1;
    }

    private sealed class Agg
    {
        public string English = "";
        public readonly List<string> Defs = new();
        public string Phonetic = "";
    }

    /// <summary>
    /// 从多个来源提取 AnkiChinas 加密参数：
    /// 1. 新版 anki21b：notetypes.config blob（protobuf 内嵌 JS）
    /// 2. 旧版 anki2/anki21：col.models JSON（模板 CSS/qfmt/afmt 内嵌 JS）
    /// </summary>
    private static AnkiChinasDecryptor.EncryptionParams? ExtractEncryptionParams(MiniSqliteReader reader)
    {
        // 来源 1：notetypes.config blob（新版 anki21b）
        var notetypes = reader.ReadTable("notetypes");
        foreach (var nt in notetypes)
        {
            if (nt.GetValueOrDefault("config") is byte[] blob && blob.Length > 1000)
            {
                var p = AnkiChinasDecryptor.ExtractParams(blob);
                if (p != null) return p;
            }
        }

        // 来源 2：col.models JSON 内的模板内容（旧版 anki2/anki21）
        var col = reader.ReadTable("col");
        if (col.Count > 0 && col[0].GetValueOrDefault("models") is string modelsJson && modelsJson.Length > 100)
        {
            var p = AnkiChinasDecryptor.ExtractParams(Encoding.UTF8.GetBytes(modelsJson));
            if (p != null) return p;
        }

        return null;
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

    private sealed class ColStat { public double Cjk, Asc, Ipa, AvgLen, Tokens; public bool Any; }

    /// <summary>
    /// 从 N 个字段中判定（单词列 en, 释义列 cn, 音标/读音列 ph）。语种无关：
    /// 字段名多语言启发式优先，否则用内容特征（长度/词数/IPA/CJK），兼容任意语种词库。
    /// </summary>
    private static (int en, int cn, int ph) DetectRoles(List<string> fnames, List<string[]> rows)
    {
        int nc = fnames.Count;
        if (nc < 2) return (-1, -1, -1); // 单字段（如填空卡）无法拆「单词 / 释义」
        var low = fnames.ConvertAll(f => f.ToLowerInvariant());
        var stat = new ColStat[nc];
        int sample = Math.Min(rows.Count, 300);
        for (int c = 0; c < nc; c++) stat[c] = ColStats(rows, c, sample);

        // 1) 音标/读音列：字段名（IPA/拼音/假名/読み…多语言）命中，否则 IPA 字符占比高且内容短
        int ph = NameHit(low, PhoneticNames, PhoneticExclude);
        if (ph < 0)
        {
            double best = 0.4; int bi = -1;
            for (int c = 0; c < nc; c++)
                if (stat[c].Any && stat[c].Cjk < 0.2 && stat[c].Ipa > best && stat[c].AvgLen < 40)
                { best = stat[c].Ipa; bi = c; }
            ph = bi;
        }

        // 2) 释义列：字段名（definition/翻译/traduction/Bedeutung…）命中 > 中文(CJK)列 > 第4步兜底。
        //    CJK 优先是为兼容最常见的「外语→中文」词库，准确率高。
        int cn = NameHit(low, DefNames, DefExclude);
        if (cn < 0 || cn == ph)
        {
            double best = 0.3; int bi = -1;
            for (int c = 0; c < nc; c++)
                if (c != ph && stat[c].Any && stat[c].Cjk > best) { best = stat[c].Cjk; bi = c; }
            cn = bi;
        }

        // 3) 单词列：字段名（word/term/front/mot/単語…）命中，否则「除释义/音标外，最短 + 词数最少 + 越靠前」。
        //    不再要求 ASCII —— 法语重音、俄/日/阿拉伯等任意文字的单词都能选中。
        int en = NameHit(low, WordNames, WordExclude);
        if (en < 0 || en == cn || en == ph)
        {
            double bestScore = double.NegativeInfinity; int bi = -1;
            for (int c = 0; c < nc; c++)
            {
                if (c == ph || c == cn || !stat[c].Any) continue;
                double score = -stat[c].AvgLen - stat[c].Tokens * 4 - c * 0.5; // 越短/词越少/越靠前越像单词
                if (score > bestScore) { bestScore = score; bi = c; }
            }
            en = bi;
        }

        // 4) 释义仍未定（无字段名、无中文）→ 取「除单词/音标外」最长列，支持 法→英 / 日→英 等任意语种对
        if (cn < 0)
        {
            double bestLen = -1; int bi = -1;
            for (int c = 0; c < nc; c++)
            {
                if (c == en || c == ph || !stat[c].Any) continue;
                if (stat[c].AvgLen > bestLen) { bestLen = stat[c].AvgLen; bi = c; }
            }
            cn = bi;
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
            st.Tokens += v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        }
        if (n > 0)
        {
            st.Any = true;
            st.Cjk /= n; st.Asc /= n; st.Ipa /= n; st.AvgLen /= n; st.Tokens /= n;
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
