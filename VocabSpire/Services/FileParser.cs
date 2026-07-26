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

        [JsonPropertyName("answer")]
        public int? Answer { get; set; }
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
                        // 固定选择题条目：english=题干、options=选项、answer=正确索引；chinese 自动填正确答案文本
                        if (w.Options is { Count: > 0 } && w.Answer is int ans && ans >= 0 && ans < w.Options.Count)
                        {
                            return new WordEntry
                            {
                                English = w.English.Trim(),
                                Chinese = w.Options[ans].Trim(),
                                Definitions = new List<string> { w.Options[ans].Trim() },
                                Options = w.Options.Select(o => o.Trim()).ToList(),
                                FixedCorrectIndex = ans
                            };
                        }
                        var (chinese, defs) = ParseChineseField(w.Chinese);
                        return CreateWordEntry(w.English, chinese, w.Phonetic, defs);
                    })
                    .Where(w => !string.IsNullOrEmpty(w.English) && !string.IsNullOrEmpty(w.Chinese))
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
            var answerIdx = FindColumnIndex(header, "answer");

            // 选择题分列：optionA ~ optionE（或 option_a ~ option_e / a ~ e）
            var optionIndices = new List<int>();
            for (var oi = 0; oi < 5; oi++)
            {
                var letter = ((char)('a' + oi)).ToString();
                var upperLetter = ((char)('A' + oi)).ToString();
                var idx = FindColumnIndex(header, $"option{upperLetter}", $"option_{letter}", $"option{letter}", upperLetter);
                if (idx >= 0) optionIndices.Add(idx);
            }
            var isChoiceCsv = optionIndices.Count >= 2 && answerIdx >= 0;

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
                    // 选择题行：收集非空选项 + 映射答案索引
                    var opts = new List<string>();
                    foreach (var oi in optionIndices)
                    {
                        var t = oi < fields.Length ? fields[oi].Trim() : "";
                        if (t.Length > 0) opts.Add(t);
                    }
                    if (opts.Count < 2) continue;

                    var ansRaw = answerIdx < fields.Length ? fields[answerIdx].Trim() : "";
                    var ans = -1;
                    // 支持数字索引（0-based）或字母（A-E）
                    if (ansRaw.Length == 1 && ansRaw[0] >= 'A' && ansRaw[0] <= 'E')
                        ans = ansRaw[0] - 'A';
                    else if (ansRaw.Length == 1 && ansRaw[0] >= 'a' && ansRaw[0] <= 'e')
                        ans = ansRaw[0] - 'a';
                    else if (int.TryParse(ansRaw, out var ni))
                        ans = ni;
                    if (ans < 0 || ans >= opts.Count) continue;

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
