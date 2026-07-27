using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;

namespace VocabSpire.Services;

/// <summary>
/// 跨设备学习进度导出 / 导入（.vsprog 文件，本质是带标识头的 JSON）。
///
/// 为什么不能直接拷 _word_progress.json（踩过的三个坑）：
///   1. 那里的 key 是「词库文件名::单词」，两台设备词库文件名不同 → 全部对不上、掌握度整个丢失；
///   2. 复习调度依赖 VocabConfig.TotalAnswered 这个时钟，它在 vocabspire_config.json 里，只拷进度会错位；
///   3. 游戏运行时每答一题就整份重写进度文件，运行中拷进去会被内存里的旧数据覆盖。
///
/// 本格式对症下药：bank 与 word 分字段存（词库名对不上可按单词兜底匹配）、带上调度时钟、
/// 导入前自动备份、支持「合并」模式（双端都练过时按更高掌握度取，不会互相冲掉）。
/// </summary>
public static class ProgressTransfer
{
    public const string FormatId = "vocabspire-progress";
    public const int FormatVersion = 1;
    public const string FileExtension = ".vsprog";

    /// <summary>导入模式。</summary>
    public enum ImportMode
    {
        /// <summary>合并：逐词取「更靠前的掌握度」，双端进度都不丢（推荐）。</summary>
        Merge = 0,
        /// <summary>覆盖：完全用文件里的进度替换本机（本机独有的词保留不动）。</summary>
        Overwrite = 1
    }

    public sealed class Entry
    {
        [JsonPropertyName("bank")] public string Bank { get; set; } = "";
        [JsonPropertyName("word")] public string Word { get; set; } = "";
        [JsonPropertyName("correct")] public int Correct { get; set; }
        [JsonPropertyName("wrong")] public int Wrong { get; set; }
        [JsonPropertyName("energyLost")] public int EnergyLost { get; set; }
        [JsonPropertyName("streak")] public int Streak { get; set; }
        [JsonPropertyName("box")] public int Box { get; set; }
        [JsonPropertyName("dueTick")] public long DueTick { get; set; }
        [JsonPropertyName("lastSeen")] public long LastSeen { get; set; }
    }

    public sealed class Payload
    {
        [JsonPropertyName("format")] public string Format { get; set; } = FormatId;
        [JsonPropertyName("version")] public int Version { get; set; } = FormatVersion;
        [JsonPropertyName("exportedAt")] public string ExportedAt { get; set; } = "";
        [JsonPropertyName("modVersion")] public string ModVersion { get; set; } = "";
        [JsonPropertyName("totalAnswered")] public int TotalAnswered { get; set; }
        [JsonPropertyName("totalCorrect")] public int TotalCorrect { get; set; }
        [JsonPropertyName("entries")] public List<Entry> Entries { get; set; } = new();
    }

    /// <summary>导出全部词库的学习进度到 .vsprog 文件，返回文件路径。</summary>
    public static string Export()
    {
        var cfg = VocabConfig.Instance;
        var payload = new Payload
        {
            ExportedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ModVersion = ReadModVersion(),
            TotalAnswered = cfg.TotalAnswered,
            TotalCorrect = cfg.TotalCorrect
        };

        foreach (var bank in VocabManager.Instance.Banks)
        {
            foreach (var w in bank.Words)
            {
                // 与 SaveProgress 一致：全零的词不占空间
                if (w.CorrectCount == 0 && w.WrongCount == 0 && w.EnergyLost == 0 && w.Box == 0 && w.DueTick == 0)
                    continue;
                payload.Entries.Add(new Entry
                {
                    Bank = bank.Id,
                    Word = w.English,
                    Correct = w.CorrectCount,
                    Wrong = w.WrongCount,
                    EnergyLost = w.EnergyLost,
                    Streak = w.Streak,
                    Box = w.Box,
                    DueTick = w.DueTick,
                    LastSeen = w.LastSeenDate
                });
            }
        }

        var dir = VocabManager.Instance.GetWordBanksDirectory();
        var name = $"_progress_{DateTime.Now:yyyyMMdd_HHmmss}{FileExtension}";
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }));

        Log.Info($"[VocabSpire] 进度已导出：{payload.Entries.Count} 词 → {path}");
        return path;
    }

    public sealed class ImportResult
    {
        public int Applied;        // 实际写入的词数
        public int Skipped;        // 本机找不到对应单词
        public int FuzzyMatched;   // 词库名对不上、按单词兜底匹配成功
        public string BackupPath = "";
        public string Message = "";
    }

    /// <summary>从 .vsprog 文件导入进度。导入前自动备份当前进度文件。</summary>
    public static ImportResult Import(string filePath, ImportMode mode)
    {
        var result = new ImportResult();

        var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(filePath))
            ?? throw new InvalidDataException("文件内容为空或不是合法 JSON。");
        if (payload.Format != FormatId)
            throw new InvalidDataException($"不是 VocabSpire 进度文件（format={payload.Format}）。");
        if (payload.Version > FormatVersion)
            throw new InvalidDataException($"文件版本 v{payload.Version} 高于当前 mod 支持的 v{FormatVersion}，请先更新 mod。");
        if (payload.Entries.Count == 0)
            throw new InvalidDataException("文件里没有任何进度记录。");

        // 导入前备份，出问题能回滚
        result.BackupPath = BackupCurrentProgress();

        // 建索引：bank.Id → (word.lower → WordEntry)，以及 word.lower → 所有同名词条（跨库兜底）
        var byBank = new Dictionary<string, Dictionary<string, WordEntry>>(StringComparer.OrdinalIgnoreCase);
        var byWord = new Dictionary<string, List<WordEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var bank in VocabManager.Instance.Banks)
        {
            var map = new Dictionary<string, WordEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in bank.Words)
            {
                var key = w.English.Trim();
                if (key.Length == 0) continue;
                map[key] = w;
                if (!byWord.TryGetValue(key, out var list)) byWord[key] = list = new List<WordEntry>();
                list.Add(w);
            }
            byBank[bank.Id] = map;
        }

        foreach (var e in payload.Entries)
        {
            var word = e.Word.Trim();
            if (word.Length == 0) continue;

            var targets = new List<WordEntry>();
            if (byBank.TryGetValue(e.Bank, out var map) && map.TryGetValue(word, out var exact))
            {
                targets.Add(exact);          // 词库名对得上 → 精确匹配
            }
            else if (byWord.TryGetValue(word, out var loose))
            {
                targets.AddRange(loose);     // 词库名变了（改文件名/重新导入）→ 按单词兜底
                result.FuzzyMatched++;
            }

            if (targets.Count == 0) { result.Skipped++; continue; }

            foreach (var t in targets) Apply(t, e, mode);
            result.Applied++;
        }

        // 调度时钟：取两边较大值，避免导入后所有词都被判成"还没到期"而不再出现
        var cfg = VocabConfig.Instance;
        cfg.TotalAnswered = Math.Max(cfg.TotalAnswered, payload.TotalAnswered);
        cfg.TotalCorrect = Math.Max(cfg.TotalCorrect, payload.TotalCorrect);
        cfg.Save();

        VocabManager.Instance.SaveProgress();

        result.Message = $"导入完成：{result.Applied} 词已更新" +
                         (result.FuzzyMatched > 0 ? $"（其中 {result.FuzzyMatched} 词按单词兜底匹配）" : "") +
                         (result.Skipped > 0 ? $"，{result.Skipped} 词本机词库里没有、已跳过" : "") +
                         $"。原进度已备份：{Path.GetFileName(result.BackupPath)}";
        Log.Info($"[VocabSpire] {result.Message}");
        return result;
    }

    /// <summary>把一条记录写进词条。Merge 模式下按「谁更靠前」取，Overwrite 模式直接覆盖。</summary>
    private static void Apply(WordEntry w, Entry e, ImportMode mode)
    {
        if (mode == ImportMode.Overwrite)
        {
            w.CorrectCount = e.Correct;
            w.WrongCount = e.Wrong;
            w.EnergyLost = e.EnergyLost;
            w.Streak = e.Streak;
            w.Box = e.Box;
            w.DueTick = e.DueTick;
            w.LastSeenDate = e.LastSeen;
            return;
        }

        // Merge：掌握度以 Box 为主、Streak 次之（这也是 IsMastered 的判据）。
        // 更靠前的一方整体胜出（Box/Streak/DueTick 是一套相互关联的调度状态，不能拆开各取最大）。
        var incomingAhead = e.Box > w.Box || (e.Box == w.Box && e.Streak > w.Streak);
        if (incomingAhead)
        {
            w.Streak = e.Streak;
            w.Box = e.Box;
            w.DueTick = e.DueTick;
        }

        // 累计类计数取较大值：两端可能重复练过同一批词，求和会虚高，取 max 是安全下界。
        w.CorrectCount = Math.Max(w.CorrectCount, e.Correct);
        w.WrongCount = Math.Max(w.WrongCount, e.Wrong);
        w.EnergyLost = Math.Max(w.EnergyLost, e.EnergyLost);
        w.LastSeenDate = Math.Max(w.LastSeenDate, e.LastSeen);
    }

    /// <summary>备份当前 _word_progress.json，返回备份路径（没有原文件则返回空串）。</summary>
    private static string BackupCurrentProgress()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(ProgressTransfer).Assembly.Location) ?? ".";
            var src = Path.Combine(dir, "_word_progress.json");
            if (!File.Exists(src)) return "";
            var dst = Path.Combine(dir, $"_word_progress.backup_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.Copy(src, dst, overwrite: true);
            return dst;
        }
        catch (Exception ex)
        {
            Log.Warn($"[VocabSpire] 备份原进度失败（继续导入）：{ex.Message}");
            return "";
        }
    }

    private static string ReadModVersion()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(ProgressTransfer).Assembly.Location) ?? ".";
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "VocabSpire.json")));
            return doc.RootElement.GetProperty("version").GetString() ?? "";
        }
        catch { return ""; }
    }
}
