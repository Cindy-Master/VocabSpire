using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using VocabSpire.Models;

namespace VocabSpire.Services;

public sealed class VocabManager
{
    public static VocabManager Instance { get; } = new();

    private readonly List<WordBank> _banks = new();
    private readonly QuizGenerator _quizGenerator = new();
    private WordBank? _activeBank;

    // ── 本局已测试词追踪（用于拼写复习模式）──
    private readonly HashSet<string> _testedWordsThisRun = new();
    private bool _wasInRun;

    public IReadOnlyList<WordBank> Banks => _banks.AsReadOnly();
    public WordBank? ActiveBank => _activeBank;
    public bool HasActiveBank => _activeBank is { IsValid: true };

    private VocabManager() { }

    public string GetWordBanksDirectory()
    {
        var modDir = Path.GetDirectoryName(typeof(VocabManager).Assembly.Location) ?? ".";
        var wordbanksDir = Path.Combine(modDir, "wordbanks");

        if (!Directory.Exists(wordbanksDir))
        {
            Directory.CreateDirectory(wordbanksDir);
        }

        return wordbanksDir;
    }

    public void LoadAllBanks()
    {
        _banks.Clear();
        var dir = GetWordBanksDirectory();

        Log.Info($"[VocabSpire] Loading word banks from: {dir}");

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            if (Path.GetFileName(file).StartsWith("_")) continue;
            var bank = FileParser.ParseJson(file);
            if (bank is not null)
            {
                _banks.Add(bank);
                Log.Info($"[VocabSpire] Loaded: {bank.Name} ({bank.TotalWords} words)");
            }
        }

        foreach (var file in Directory.GetFiles(dir, "*.csv"))
        {
            if (Path.GetFileName(file).StartsWith("_")) continue;
            var bank = FileParser.ParseCsv(file);
            if (bank is not null)
            {
                _banks.Add(bank);
                Log.Info($"[VocabSpire] Loaded: {bank.Name} ({bank.TotalWords} words)");
            }
        }

        Log.Info($"[VocabSpire] Total word banks: {_banks.Count}");

        var activeBankId = VocabConfig.Instance.ActiveBankId;
        if (!string.IsNullOrEmpty(activeBankId))
        {
            SetActiveBank(activeBankId);
        }
        else if (_banks.Count > 0)
        {
            SetActiveBank(_banks[0].Id);
        }

        // 加载持久化的单词进度
        LoadProgress();
    }

    public void SetActiveBank(string bankId)
    {
        _activeBank = _banks.FirstOrDefault(b => b.Id == bankId);
        if (_activeBank is not null)
        {
            VocabConfig.Instance.ActiveBankId = bankId;
            VocabConfig.Instance.Save();
            Log.Info($"[VocabSpire] Active bank: {_activeBank.Name}");
        }
    }

    public WordBank? ImportBank(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        // apkg 特殊处理：解析为词条后序列化成 json 存入 wordbanks
        // （apkg 是二进制，LoadAllBanks 只扫描 json/csv，必须先转 json）
        if (ext == ".apkg")
            return ImportApkg(filePath);

        var bank = ext switch
        {
            ".json" => FileParser.ParseJson(filePath),
            ".csv" => FileParser.ParseCsv(filePath),
            _ => null
        };

        if (bank is null)
        {
            Log.Error($"[VocabSpire] Failed to import: {filePath}");
            return null;
        }

        var destPath = Path.Combine(GetWordBanksDirectory(), Path.GetFileName(filePath));
        if (!string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(filePath, destPath, overwrite: true);
        }

        var existingIdx = _banks.FindIndex(b => b.Id == bank.Id);
        if (existingIdx >= 0)
        {
            _banks[existingIdx] = bank;
        }
        else
        {
            _banks.Add(bank);
        }

        Log.Info($"[VocabSpire] Imported: {bank.Name} ({bank.TotalWords} words)");
        return bank;
    }

    /// <summary>
    /// 导入 Anki .apkg 词库：用纯托管 reader 解析后序列化成 VocabSpire json 存入 wordbanks 目录，
    /// 再按普通 json 加载（保证与其它词库行为一致，且下次启动可直接扫描到）。
    /// </summary>
    private WordBank? ImportApkg(string apkgPath)
    {
        try
        {
            var parsed = ApkgImporter.Import(apkgPath);

            // 多义项写成数组，单义项写成字符串，与现有词库 json 格式一致
            var dto = new
            {
                name = parsed.Name,
                description = parsed.Description,
                words = parsed.Words.Select(w => new
                {
                    english = w.English,
                    chinese = w.Definitions.Count > 1
                        ? (object)w.Definitions
                        : (w.Definitions.Count == 1 ? w.Definitions[0] : w.Chinese),
                    phonetic = w.Phonetic
                })
            };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            var jsonPath = Path.Combine(GetWordBanksDirectory(), parsed.Id + ".json");
            File.WriteAllText(jsonPath, json);

            var bank = FileParser.ParseJson(jsonPath) ?? parsed;
            var idx = _banks.FindIndex(b => b.Id == bank.Id);
            if (idx >= 0) _banks[idx] = bank; else _banks.Add(bank);

            Log.Info($"[VocabSpire] Imported apkg: {bank.Name} ({bank.TotalWords} words) -> {Path.GetFileName(jsonPath)}");
            return bank;
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] apkg import failed: {apkgPath} - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 导出词库模板文件到 wordbanks 目录。
    /// </summary>
    public string ExportTemplate()
    {
        var template = new
        {
            name = "我的词库",
            description = "在此填写词库描述",
            words = new object[]
            {
                new { english = "apple", chinese = "n. 苹果", phonetic = "/ˈæp.əl/" },
                new { english = "run", chinese = new[] { "v. 跑步", "vi. 运转", "n. 竞赛" }, phonetic = "/rʌn/" },
                new { english = "book", chinese = "n. 书; v. 预订", phonetic = "/bʊk/" }
            }
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(template, options);

        var path = Path.Combine(GetWordBanksDirectory(), "_TEMPLATE.json");
        File.WriteAllText(path, json);
        Log.Info($"[VocabSpire] Template exported to: {path}");
        return path;
    }

    public QuizQuestion? GenerateQuiz()
    {
        if (_activeBank is null || !_activeBank.IsValid) return null;

        // 检测新局开始，清空已测试词记录
        DetectRunBoundary();

        var tier = VocabConfig.Instance.EnableDifficultyScaling
            ? Math.Clamp(GameBridge.GetCurrentAct(), 1, 3)
            : 1;

        var cfg = VocabConfig.Instance;
        var modes = cfg.GetModesForAct(tier);

        // 拼写复习模式：Act2+ 且开启了"仅复习已测词"
        if (cfg.SpellingReviewOnly && tier >= 2 && modes.HasFlag(QuizModeFlags.SpellEnglish))
        {
            var reviewPool = GetReviewWordPool();
            if (reviewPool.Count >= 4)
            {
                return _quizGenerator.Generate(
                    _activeBank, reviewPool, modes,
                    cfg.OptionCount, tier);
            }
        }

        return _quizGenerator.Generate(
            _activeBank, modes, cfg.OptionCount, tier);
    }

    public void RecordAnswer(WordEntry word, bool correct)
    {
        VocabConfig.Instance.TotalAnswered++;          // 全局 tick 前进（复用为间隔重复调度时钟）
        if (correct) VocabConfig.Instance.TotalCorrect++;
        long tick = VocabConfig.Instance.TotalAnswered;

        if (correct)
        {
            word.CorrectCount++;
            word.Streak++;
            word.Box = Math.Min(5, word.Box + 1);      // 升盒 → 拉长复习间隔
        }
        else
        {
            word.WrongCount++;
            word.Streak = 0;                           // 答错归零
            word.Box = Math.Max(0, word.Box - 2);      // 降盒 → 很快重现
        }
        word.DueTick = tick + WordEntry.Interval(word.Box);

        _testedWordsThisRun.Add(word.English.ToLowerInvariant());

        VocabConfig.Instance.Save();

        // 持久化单词进度
        SaveProgress();
    }

    // ── 单词进度持久化 ──

    private string ProgressFilePath
    {
        get
        {
            var modDir = Path.GetDirectoryName(typeof(VocabManager).Assembly.Location) ?? ".";
            return Path.Combine(modDir, "_word_progress.json");
        }
    }

    public void SaveProgress()
    {
        try
        {
            var data = new Dictionary<string, long[]>();
            foreach (var bank in _banks)
            {
                foreach (var w in bank.Words)
                {
                    if (w.CorrectCount == 0 && w.WrongCount == 0 && w.EnergyLost == 0 && w.Box == 0 && w.DueTick == 0) continue;
                    var key = w.English.ToLowerInvariant();
                    data[key] = new long[] { w.CorrectCount, w.WrongCount, w.EnergyLost, w.Streak, w.Box, w.DueTick };
                }
            }
            var json = System.Text.Json.JsonSerializer.Serialize(data,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ProgressFilePath, json);
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to save progress: {ex.Message}");
        }
    }

    public void LoadProgress()
    {
        try
        {
            if (!File.Exists(ProgressFilePath)) return;

            var json = File.ReadAllText(ProgressFilePath);
            var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long[]>>(json);
            if (data is null) return;

            foreach (var bank in _banks)
            {
                foreach (var w in bank.Words)
                {
                    var key = w.English.ToLowerInvariant();
                    if (!data.TryGetValue(key, out var stats)) continue;
                    w.CorrectCount = stats.Length > 0 ? (int)stats[0] : 0;
                    w.WrongCount = stats.Length > 1 ? (int)stats[1] : 0;
                    w.EnergyLost = stats.Length > 2 ? (int)stats[2] : 0;
                    w.Streak = stats.Length > 3 ? (int)stats[3] : 0;
                    w.Box = stats.Length > 4 ? (int)stats[4] : 0;       // 旧 progress 缺此字段 → 默认 0（视为到期，重新纳入调度）
                    w.DueTick = stats.Length > 5 ? stats[5] : 0;
                }
            }

            Log.Info($"[VocabSpire] Loaded progress for {data.Count} words.");
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to load progress: {ex.Message}");
        }
    }

    private void DetectRunBoundary()
    {
        try
        {
            var inRun = RunManager.Instance.IsInProgress;
            if (inRun && !_wasInRun)
            {
                _testedWordsThisRun.Clear();
                RunQuizTracker.Instance.Reset(); // 新局开始，重置追踪
            }
            _wasInRun = inRun;
        }
        catch
        {
            // RunManager 不可用时忽略
        }
    }

    /// <summary>
    /// 获取本局已测试过的词（用于拼写复习）。
    /// 若已测试词不足则回退到完整词库。
    /// </summary>
    private List<WordEntry> GetReviewWordPool()
    {
        if (_activeBank is null) return new();
        if (_testedWordsThisRun.Count < 4) return _activeBank.Words;

        var filtered = _activeBank.Words
            .Where(w => _testedWordsThisRun.Contains(w.English.ToLowerInvariant()))
            .ToList();

        return filtered.Count >= 4 ? filtered : _activeBank.Words;
    }
}
