using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Logging;

namespace VocabSpire.Services;

/// <summary>
/// 跟随当前 Run（Seed + StartTime）的战斗状态：免错券库存。
/// 新局自动从 0 开始；同一 Run 中途退出再回来可恢复；不同 Run 互不干扰。
/// 联机：每个客户端各自的 Run 标识不同，库存天然隔离。
/// </summary>
public sealed class RunBattleState
{
    public static RunBattleState Instance { get; } = new();

    private string _currentSeed = "";
    private long _currentStartTime;
    private int _stock;

    public int FreePassStock => _stock;

    /// <summary>当前 Run 标识可读字符串（调试用）。</summary>
    public string CurrentRunId => string.IsNullOrEmpty(_currentSeed) ? "(no-run)" : $"{_currentSeed}@{_currentStartTime}";

    private RunBattleState() { }

    /// <summary>
    /// 检测当前 Run，必要时切换：
    /// - 与上次记录的 Run 一致 → 不动
    /// - Run 切换 → 加载该 Run 的持久化库存（默认 0）
    /// - 无 Run（在主菜单等）→ 清零
    /// </summary>
    public void DetectAndSwitchRun()
    {
        var (seed, startTime) = GetCurrentRunIdSafe();
        if (string.IsNullOrEmpty(seed))
        {
            // 不在 Run 中
            if (!string.IsNullOrEmpty(_currentSeed))
            {
                Log.Info($"[VocabSpire] RunBattleState: left run {CurrentRunId} → reset stock");
                _currentSeed = "";
                _currentStartTime = 0;
                _stock = 0;
            }
            return;
        }

        if (seed == _currentSeed && startTime == _currentStartTime) return;

        _currentSeed = seed;
        _currentStartTime = startTime;
        _stock = LoadFromDisk(seed, startTime);
        Log.Info($"[VocabSpire] RunBattleState: switched to run {CurrentRunId} (stock={_stock})");
    }

    /// <summary>变更库存（自动 clamp 到 [0, MaxStock]）并持久化。</summary>
    public void AddStock(int delta)
    {
        DetectAndSwitchRun();
        if (string.IsNullOrEmpty(_currentSeed)) return; // 不在 Run 中，忽略

        var max = VocabConfig.Instance.FreePassMaxStock;
        var prev = _stock;
        _stock = System.Math.Clamp(_stock + delta, 0, max);
        if (_stock != prev)
        {
            SaveToDisk();
        }
    }

    /// <summary>直接读取（调用前自动同步 Run）。</summary>
    public int GetStock()
    {
        DetectAndSwitchRun();
        return _stock;
    }

    // ── 持久化 ──

    private static string DataPath
    {
        get
        {
            var modDir = Path.GetDirectoryName(typeof(RunBattleState).Assembly.Location) ?? ".";
            return Path.Combine(modDir, "_free_pass.json");
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var data = new PersistData
            {
                Seed = _currentSeed,
                StartTime = _currentStartTime,
                Stock = _stock
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(DataPath, json);
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire] FreePass save failed: {ex.Message}");
        }
    }

    private static int LoadFromDisk(string seed, long startTime)
    {
        try
        {
            if (!File.Exists(DataPath)) return 0;
            var json = File.ReadAllText(DataPath);
            var data = JsonSerializer.Deserialize<PersistData>(json);
            if (data is null) return 0;
            if (data.Seed == seed && data.StartTime == startTime) return data.Stock;
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>获取当前 Run 的 (seed, startTime)。无 Run 时返回 ("", 0)。</summary>
    private static (string Seed, long StartTime) GetCurrentRunIdSafe()
    {
        try
        {
            var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
            if (rm is null || !rm.IsInProgress) return ("", 0);
            var state = rm.DebugOnlyGetState();
            var seed = state?.Rng?.StringSeed ?? "";
            long startTime = 0;
            var field = typeof(MegaCrit.Sts2.Core.Runs.RunManager).GetField("_startTime",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field is not null)
                startTime = (long)(field.GetValue(rm) ?? 0L);
            return (seed, startTime);
        }
        catch
        {
            return ("", 0);
        }
    }

    private sealed class PersistData
    {
        [JsonPropertyName("seed")] public string Seed { get; set; } = "";
        [JsonPropertyName("start_time")] public long StartTime { get; set; }
        [JsonPropertyName("stock")] public int Stock { get; set; }
    }
}
