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
    private readonly List<WordBank> _activeBanks = new();   // 已激活的库（按激活顺序，可多选）
    private WordBank? _mergedBank;                            // 合并去重后的出题池（缓存）

    // ── 本局已测试词追踪（用于拼写复习模式）──
    private readonly HashSet<string> _testedWordsThisRun = new();
    private bool _wasInRun;

    public IReadOnlyList<WordBank> Banks => _banks.AsReadOnly();
    public IReadOnlyList<WordBank> ActiveBanks => _activeBanks.AsReadOnly();
    public WordBank? ActiveBank => _mergedBank;              // 出题/展示统一用合并库
    public bool HasActiveBank => _mergedBank is { IsValid: true };
    public bool IsBankActive(string bankId) => _activeBanks.Any(b => b.Id == bankId);

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

        // 恢复激活库（多选优先）；都没有则默认第一个库
        var ids = VocabConfig.Instance.ActiveBankIds;
        if (ids is { Count: > 0 })
        {
            SetActiveBanks(ids);
        }
        else if (!string.IsNullOrEmpty(VocabConfig.Instance.ActiveBankId))
        {
            SetActiveBank(VocabConfig.Instance.ActiveBankId);
        }
        else if (_banks.Count > 0)
        {
            SetActiveBank(_banks[0].Id);
        }

        // 加载持久化的单词进度
        LoadProgress();
    }

    /// <summary>设为单一激活库（兼容旧调用：清空后只激活这一个）。</summary>
    public void SetActiveBank(string bankId) => SetActiveBanks(new[] { bankId });

    /// <summary>按 Id 列表设置激活库（保持顺序、去掉不存在的、去重）。</summary>
    public void SetActiveBanks(IEnumerable<string> bankIds)
    {
        _activeBanks.Clear();
        foreach (var id in bankIds)
        {
            var b = _banks.FirstOrDefault(x => x.Id == id);
            if (b is not null && !_activeBanks.Contains(b)) _activeBanks.Add(b);
        }
        PersistActiveIds();
        RebuildMergedBank();
        Log.Info($"[VocabSpire] Active banks: {string.Join(", ", _activeBanks.Select(b => b.Name))}");
    }

    /// <summary>勾选/取消一个库（多选）。至少保留一个：取消最后一个时忽略。</summary>
    public void ToggleActiveBank(string bankId, bool active)
    {
        var b = _banks.FirstOrDefault(x => x.Id == bankId);
        if (b is null) return;
        if (active)
        {
            if (!_activeBanks.Contains(b)) _activeBanks.Add(b);
        }
        else
        {
            if (_activeBanks.Count <= 1) return;
            _activeBanks.Remove(b);
        }
        PersistActiveIds();
        RebuildMergedBank();
    }

    private void PersistActiveIds()
    {
        var ids = _activeBanks.Select(b => b.Id).ToList();
        VocabConfig.Instance.ActiveBankIds = ids;
        VocabConfig.Instance.ActiveBankId = ids.Count > 0 ? ids[0] : "";   // 兼容旧字段
        VocabConfig.Instance.Save();
    }

    /// <summary>把所有激活库合并成一个去重出题池：按英文去重；激活顺序第一个库拥有该词（进度归它的 WordEntry 对象，
    /// 保证 SaveProgress 按该库 Id 存盘）；其余库的释义合并进来（去重、幂等，不影响进度）。</summary>
    private void RebuildMergedBank()
    {
        var seen = new Dictionary<string, WordEntry>();
        var order = new List<WordEntry>();
        foreach (var bank in _activeBanks)
        {
            foreach (var w in bank.Words)
            {
                var key = w.English.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(key)) continue;
                if (seen.TryGetValue(key, out var owner))
                {
                    foreach (var d in w.Definitions)
                        if (!owner.Definitions.Contains(d)) owner.Definitions.Add(d);
                }
                else
                {
                    seen[key] = w;
                    order.Add(w);
                }
            }
        }
        _mergedBank = new WordBank
        {
            Id = "__merged__",
            Name = _activeBanks.Count == 1 ? _activeBanks[0].Name : $"合并词库（{_activeBanks.Count} 个）",
            Words = order
        };
        Log.Info($"[VocabSpire] Merged pool: {order.Count} unique words from {_activeBanks.Count} bank(s).");
    }

    /// <summary>上一次导入失败的原因（UI 层读取后弹窗展示）。</summary>
    public string? LastImportError { get; private set; }

    public WordBank? ImportBank(string filePath)
    {
        LastImportError = null;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

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
            LastImportError = $"无法解析文件（格式不支持或内容为空）：\n{Path.GetFileName(filePath)}";
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

            var wordDtos = parsed.Words.Select<WordEntry, object>(w =>
            {
                if (w.IsFixedChoice)
                {
                    return new Dictionary<string, object>
                    {
                        ["english"] = w.English,
                        ["chinese"] = w.Chinese,
                        ["options"] = w.Options,
                        ["answer"] = w.FixedCorrectIndex
                    };
                }
                return new Dictionary<string, object>
                {
                    ["english"] = w.English,
                    ["chinese"] = w.Definitions.Count > 1
                        ? (object)w.Definitions
                        : (w.Definitions.Count == 1 ? w.Definitions[0] : w.Chinese),
                    ["phonetic"] = w.Phonetic
                };
            }).ToList();

            var dto = new Dictionary<string, object>
            {
                ["name"] = parsed.Name,
                ["description"] = parsed.Description,
                ["words"] = wordDtos
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
            LastImportError = ex.Message;
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
        if (_mergedBank is null || !_mergedBank.IsValid) return null;

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
                    _mergedBank, reviewPool, modes,
                    cfg.OptionCount, tier);
            }
        }

        return _quizGenerator.Generate(
            _mergedBank, modes, cfg.OptionCount, tier);
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
        word.DueTick = tick + VocabConfig.Instance.IntervalFor(word.Box);
        word.LastSeenDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();  // 记真实时间（毕业词跨天复习用）

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

    /// <summary>进度存档 key：按「词库 Id + 单词」隔离，避免多词库同名词（cet4/专升本都有 "core"）
    /// 共用一条记录、互相覆盖。:: 作分隔符（不会出现在词库 id 或单词里）。</summary>
    private static string ProgressKey(WordBank bank, WordEntry w)
        => $"{bank.Id}::{w.English.ToLowerInvariant()}";

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
                    var key = ProgressKey(bank, w);
                    data[key] = new long[] { w.CorrectCount, w.WrongCount, w.EnergyLost, w.Streak, w.Box, w.DueTick, w.LastSeenDate };
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
                    var key = ProgressKey(bank, w);
                    // 优先按「词库::单词」隔离 key 读；找不到再回退旧版全局 english key（兼容 v2.7.4 及更早存档）
                    if (!data.TryGetValue(key, out var stats)
                        && !data.TryGetValue(w.English.ToLowerInvariant(), out stats)) continue;
                    w.CorrectCount = stats.Length > 0 ? (int)stats[0] : 0;
                    w.WrongCount = stats.Length > 1 ? (int)stats[1] : 0;
                    w.EnergyLost = stats.Length > 2 ? (int)stats[2] : 0;
                    w.Streak = stats.Length > 3 ? (int)stats[3] : 0;
                    w.Box = stats.Length > 4 ? (int)stats[4] : 0;       // 旧 progress 缺此字段 → 默认 0（视为到期，重新纳入调度）
                    w.DueTick = stats.Length > 5 ? stats[5] : 0;
                    w.LastSeenDate = stats.Length > 6 ? stats[6] : 0;
                }
            }

            Log.Info($"[VocabSpire] Loaded progress for {data.Count} words.");
            RepairScheduleClock();
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to load progress: {ex.Message}");
        }
    }

    /// <summary>
    /// 修复「调度时钟落后于进度数据」的错位 —— 进度冻结（已掌握量不再增长）的根因。
    ///
    /// DueTick 是「答到第几题时该复习」的绝对刻度，基准是 VocabConfig.TotalAnswered。
    /// 但两者存在**不同文件**里（进度在 _word_progress.json、时钟在 vocabspire_config.json），
    /// 手动只拷进度、或重装 mod 导致 config 被重置时就会错位。实测案例：DueTick 已到 7942
    /// 而时钟只有 36 → 所有词判「还没到期」（权重降到 0.02/0.05）+ 学习中词数超过新词节流上限
    /// → 新词权重为 0 永不引入 → 看起来就是"进度一直不涨"。
    ///
    /// 修法：把时钟推进到 max(DueTick)。相对间隔不变、到期判定立刻恢复正常；
    /// 且跨设备累计答题数本就该是较大的那个，统计显示也更接近真实。
    /// </summary>
    private void RepairScheduleClock()
    {
        long maxDue = 0;
        foreach (var bank in _banks)
            foreach (var w in bank.Words)
                if (w.DueTick > maxDue) maxDue = w.DueTick;

        var cfg = VocabConfig.Instance;
        if (maxDue <= cfg.TotalAnswered) return;

        var repaired = (int)Math.Min(maxDue, int.MaxValue);
        Log.Warn($"[VocabSpire] 检测到调度时钟落后于进度数据（TotalAnswered={cfg.TotalAnswered} < maxDueTick={maxDue}）"
               + $"，已自动推进到 {repaired} 修复。常见于跨设备手动拷贝 _word_progress.json 或重装 mod 后配置被重置；"
               + "若不修复，所有词会被判成「还没到期」且新词被节流挡住，掌握量将停止增长。");
        cfg.TotalAnswered = repaired;
        cfg.Save();
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
        if (_mergedBank is null) return new();
        if (_testedWordsThisRun.Count < 4) return _mergedBank.Words;

        var filtered = _mergedBank.Words
            .Where(w => _testedWordsThisRun.Contains(w.English.ToLowerInvariant()))
            .ToList();

        return filtered.Count >= 4 ? filtered : _mergedBank.Words;
    }
}
