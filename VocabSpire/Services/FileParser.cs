using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;

namespace VocabSpire.Services;

public static class FileParser
{
    private sealed class JsonWordBankDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("words")]
        public List<JsonWordEntryDto> Words { get; set; } = new();
    }

    private sealed class JsonWordEntryDto
    {
        [JsonPropertyName("english")]
        public string English { get; set; } = "";

        [JsonPropertyName("chinese")]
        public JsonElement Chinese { get; set; }

        [JsonPropertyName("phonetic")]
        public string Phonetic { get; set; } = "";

        // ── 固定选择题扩展（题库模式）：options 非空即为固定选择题，english=题干 ──
        [JsonPropertyName("options")]
        public List<string>? Options { get; set; }

        /// <summary>正确答案：数字索引（0-based；等于选项数时按 1-based 容错）或字母 "A"-"H"。</summary>
        [JsonPropertyName("answer")]
        public JsonElement Answer { get; set; }
    }

    public static WordBank? ParseJson(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            var dto = JsonSerializer.Deserialize<JsonWordBankDto>(json);
            if (dto is null) return null;

            var id = Path.GetFileNameWithoutExtension(filePath);
            return new WordBank
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(dto.Name) ? id : dto.Name,
                Description = dto.Description,
                SourcePath = filePath,
                Words = dto.Words
                    .Select(w =>
                    {
                        // 固定选择题条目：english=题干、options=选项、answer=正确答案；chinese 自动填正确答案文本
                        if (w.Options is { Count: >= 2 })
                        {
                            var opts = w.Options.Select(o => o.Trim()).Where(o => o.Length > 0).ToList();
                            var ans = ParseJsonAnswer(w.Answer, opts.Count);
                            if (ans >= 0)
                            {
                                return new WordEntry
                                {
                                    English = w.English.Trim(),
                                    Chinese = opts[ans],
                                    Definitions = new List<string> { opts[ans] },
                                    Options = opts,
                                    FixedCorrectIndex = ans
                                };
                            }
                            // 有 options 但答案无效 → 是坏掉的题，不要掉进单词解析产生垃圾词条
                            Log.Warn($"[VocabSpire] 跳过无效选择题（answer 解析失败）：{Truncate(w.English, 40)}");
                            return null;
                        }
                        var (chinese, defs) = ParseChineseField(w.Chinese);
                        return CreateWordEntry(w.English, chinese, w.Phonetic, defs);
                    })
                    .Where(w => w is not null && !string.IsNullOrEmpty(w.English) && !string.IsNullOrEmpty(w.Chinese))
                    .Select(w => w!)
                    .ToList()
            };
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to parse JSON: {filePath} - {ex.Message}");
            return null;
        }
    }

    public static WordBank? ParseCsv(string filePath)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            if (lines.Length < 2) return null;

            var header = ParseCsvLine(lines[0]);
            var englishIdx = FindColumnIndex(header, "english", "en", "word", "question");
            var chineseIdx = FindColumnIndex(header, "chinese", "cn", "zh", "meaning");
            var phoneticIdx = FindColumnIndex(header, "phonetic", "pronunciation");
            var answerIdx = FindColumnIndex(header, "answer", "answers");

            // 方案 B：选择题分列 optionA ~ optionH
            var optionIndices = new List<int>();
            for (var oi = 0; oi < 8; oi++)
            {
                var letter = ((char)('a' + oi)).ToString();
                var upperLetter = ((char)('A' + oi)).ToString();
                var idx = FindColumnIndex(header, $"option{upperLetter}", $"option_{letter}", $"option{letter}", upperLetter);
                if (idx >= 0) optionIndices.Add(idx);
            }
            // 方案 A：选项合并在一列（Options，用 || 或换行分隔）
            var optionsMergedIdx = optionIndices.Count < 2
                ? FindColumnIndex(header, "options", "choices")
                : -1;
            var isChoiceCsv = (optionIndices.Count >= 2 || optionsMergedIdx >= 0) && answerIdx >= 0;

            if (englishIdx < 0 || (!isChoiceCsv && chineseIdx < 0))
            {
                Log.Error($"[VocabSpire] CSV missing required columns (english/chinese or english/optionA-E/answer): {filePath}");
                return null;
            }

            var words = new List<WordEntry>();
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var fields = ParseCsvLine(line);
                if (fields.Length <= englishIdx) continue;

                var english = fields[englishIdx].Trim();
                if (string.IsNullOrEmpty(english)) continue;

                if (isChoiceCsv)
                {
                    // 选择题行：方案 B（分列）或方案 A（合并列，|| 或换行分隔）
                    var opts = new List<string>();
                    if (optionsMergedIdx >= 0 && optionsMergedIdx < fields.Length)
                    {
                        var raw = fields[optionsMergedIdx].Trim();
                        var split = raw.Contains("||")
                            ? raw.Split("||", System.StringSplitOptions.RemoveEmptyEntries)
                            : raw.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
                        foreach (var s in split) { var t = s.Trim(); if (t.Length > 0) opts.Add(t); }
                    }
                    else
                    {
                        foreach (var oi in optionIndices)
                        {
                            var t = oi < fields.Length ? fields[oi].Trim() : "";
                            if (t.Length > 0) opts.Add(t);
                        }
                    }
                    if (opts.Count < 2) continue;

                    var ansRaw = answerIdx < fields.Length ? fields[answerIdx].Trim() : "";
                    var ans = ParseAnswerIndex(ansRaw, opts.Count);
                    if (ans < 0) continue;

                    words.Add(new WordEntry
                    {
                        English = english,
                        Chinese = opts[ans],
                        Definitions = new List<string> { opts[ans] },
                        Options = opts,
                        FixedCorrectIndex = ans
                    });
                    continue;
                }

                // 普通单词行
                if (chineseIdx < 0 || fields.Length <= chineseIdx) continue;
                var chinese = fields[chineseIdx].Trim();
                if (string.IsNullOrEmpty(chinese)) continue;

                var phonetic = phoneticIdx >= 0 && phoneticIdx < fields.Length
                    ? fields[phoneticIdx].Trim()
                    : "";

                words.Add(CreateWordEntry(english, chinese, phonetic));
            }

            var id = Path.GetFileNameWithoutExtension(filePath);
            return new WordBank
            {
                Id = id,
                Name = id.Replace('_', ' ').Replace('-', ' '),
                Description = $"Imported from {Path.GetFileName(filePath)}",
                SourcePath = filePath,
                Words = words
            };
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to parse CSV: {filePath} - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 解析 JSON 的 chinese 字段，支持字符串或字符串数组。
    /// </summary>
    private static (string combined, List<string> definitions) ParseChineseField(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var defs = element.EnumerateArray()
                .Select(e => e.GetString()?.Trim() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
            return (string.Join("; ", defs), defs);
        }

        var str = element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim() ?? ""
            : "";
        return (str, SplitDefinitions(str));
    }

    /// <summary>
    /// 将中文释义字符串按分号拆分为多个释义。
    /// </summary>
    private static List<string> SplitDefinitions(string chinese)
    {
        if (string.IsNullOrWhiteSpace(chinese)) return new List<string>();

        var parts = chinese.Split(new[] { ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        return parts.Count > 0 ? parts : new List<string> { chinese.Trim() };
    }

    private static WordEntry CreateWordEntry(string english, string chinese, string phonetic,
        List<string>? definitions = null)
    {
        return new WordEntry
        {
            English = english.Trim(),
            Chinese = chinese.Trim(),
            Phonetic = phonetic.Trim(),
            Definitions = definitions ?? SplitDefinitions(chinese)
        };
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = "";
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current += '"';
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current);
                current = "";
            }
            else
            {
                current += c;
            }
        }
        fields.Add(current);
        return fields.ToArray();
    }

    /// <summary>
    /// 解析 JSON 的 answer 字段 → 0-based 选项索引。
    /// 规范是 0-based（本 mod 的工具与内置题库均按此生成）；但对人手写的 1-based 做容错：
    /// answer 恰好等于选项数时（0-based 下必然越界）按 1-based 解释。也接受字符串 "A"-"H" / 数字串。
    /// </summary>
    private static int ParseJsonAnswer(JsonElement answer, int optionCount)
    {
        if (optionCount <= 0) return -1;
        switch (answer.ValueKind)
        {
            case JsonValueKind.Number when answer.TryGetInt32(out var n):
                if (n >= 0 && n < optionCount) return n;          // 0-based（规范）
                if (n == optionCount) return n - 1;               // 只可能是 1-based 的最后一项
                return -1;
            case JsonValueKind.String:
                return ParseAnswerIndex(answer.GetString() ?? "", optionCount);
            default:
                return -1;
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// 解析选择题答案列 → 0-based 选项索引。与 ApkgImporter.ParseAnswerIndex 保持一致的语义：
    /// 字母 A-H（大小写皆可）按 0-based；数字**优先按 1-based**（题库最常见，如「5」= 第5个选项 E），
    /// 仅当按 1-based 越界时才退回 0-based。返回 -1 表示无法解析。
    /// （踩过的坑：曾把数字当 0-based，导致整库答案偏移一位、最后一个选项的题被整题丢弃。）
    /// </summary>
    private static int ParseAnswerIndex(string raw, int optionCount)
    {
        raw = raw.Trim();
        if (raw.Length == 0 || optionCount <= 0) return -1;

        if (raw.Length == 1)
        {
            var c = char.ToUpperInvariant(raw[0]);
            if (c >= 'A' && c <= 'H')
            {
                var li = c - 'A';
                return li < optionCount ? li : -1;
            }
        }

        if (int.TryParse(raw, out var num))
        {
            if (num >= 1 && num <= optionCount) return num - 1;   // 1-based（优先）
            if (num >= 0 && num < optionCount) return num;        // 0-based（退路）
        }
        return -1;
    }

    private static int FindColumnIndex(string[] header, params string[] names)
    {
        for (var i = 0; i < header.Length; i++)
        {
            var h = header[i].Trim().ToLowerInvariant();
            if (names.Any(n => h == n.ToLowerInvariant()))
                return i;
        }
        return -1;
    }
}
