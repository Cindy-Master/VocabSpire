using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;

namespace VocabSpire.Services;

/// <summary>
/// 单局答题追踪器 —— 记录每局的所有答题，局结束后持久化。
/// </summary>
public sealed class RunQuizTracker
{
    public static RunQuizTracker Instance { get; } = new();

    private readonly List<RunQuizRecord> _currentRunRecords = new();
    private RunQuizSummary? _lastRunSummary;

    private RunQuizTracker() { }

    public bool HasCurrentRunData => _currentRunRecords.Count > 0;
    public RunQuizSummary? LastRunSummary => _lastRunSummary;

    /// <summary>记录一次答题。</summary>
    public void Record(RunQuizRecord record)
    {
        _currentRunRecords.Add(record);
    }

    /// <summary>局结束时调用，生成汇总并持久化。</summary>
    public RunQuizSummary? FinishRun()
    {
        if (_currentRunRecords.Count == 0) return null;

        // 从游戏获取当前局的唯一标识 (Seed + StartTime)
        var seed = "";
        long startTime = 0;
        try
        {
            var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
            var state = rm.DebugOnlyGetState();
            if (state is not null)
                seed = state.Rng?.StringSeed ?? "";
            // _startTime 是 private，通过反射获取
            var field = typeof(MegaCrit.Sts2.Core.Runs.RunManager).GetField("_startTime",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field is not null)
                startTime = (long)(field.GetValue(rm) ?? 0L);
        }
        catch { }

        var summary = new RunQuizSummary
        {
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Seed = seed,
            StartTime = startTime,
            Records = new List<RunQuizRecord>(_currentRunRecords)
        };

        foreach (var r in summary.Records)
        {
            summary.TotalQuestions++;
            if (r.Correct) summary.CorrectCount++;
            else
            {
                summary.WrongCount++;
                summary.TotalEnergyLost += r.EnergyCost;
            }
        }

        _lastRunSummary = summary;
        _currentRunRecords.Clear();

        // 持久化到历史
        SaveToHistory(summary);

        Log.Info($"[VocabSpire] Run finished: {summary.TotalQuestions} questions, " +
                 $"{summary.CorrectCount} correct, {summary.WrongCount} wrong.");
        return summary;
    }

    /// <summary>清空当前局记录（新局开始时）。</summary>
    public void Reset()
    {
        _currentRunRecords.Clear();
    }

    // ── 历史持久化 ──

    private static string HistoryPath
    {
        get
        {
            var modDir = Path.GetDirectoryName(typeof(RunQuizTracker).Assembly.Location) ?? ".";
            return Path.Combine(modDir, "_run_history.json");
        }
    }

    private static void SaveToHistory(RunQuizSummary summary)
    {
        try
        {
            var history = LoadHistory();
            history.Add(summary);

            // 全部保留，不限制数量

            var json = JsonSerializer.Serialize(history,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(HistoryPath, json);
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to save run history: {ex.Message}");
        }
    }

    /// <summary>根据游戏 RunHistory 的 Seed+StartTime 查找对应的词汇回顾。</summary>
    public static RunQuizSummary? FindBySeedAndTime(string seed, long startTime)
    {
        var history = LoadHistory();
        return history.FirstOrDefault(s =>
            s.Seed == seed && s.StartTime == startTime);
    }

    public static List<RunQuizSummary> LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return new();
            var json = File.ReadAllText(HistoryPath);
            return JsonSerializer.Deserialize<List<RunQuizSummary>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
