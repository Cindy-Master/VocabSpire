namespace VocabSpire.Models;

/// <summary>
/// 单局游戏中一次答题的记录。
/// </summary>
public sealed class RunQuizRecord
{
    public string English { get; init; } = "";
    public string Chinese { get; init; } = "";
    public string Mode { get; init; } = "";
    public bool Correct { get; init; }
    public string UserAnswer { get; init; } = "";
    public string CorrectAnswer { get; init; } = "";
    public int EnergyCost { get; init; }
}

/// <summary>
/// 单局游戏的答题汇总。
/// </summary>
public sealed class RunQuizSummary
{
    public string Timestamp { get; init; } = "";
    public string Seed { get; set; } = "";
    public long StartTime { get; set; }
    public int TotalQuestions { get; set; }
    public int CorrectCount { get; set; }
    public int WrongCount { get; set; }
    public int TotalEnergyLost { get; set; }
    public List<RunQuizRecord> Records { get; init; } = new();

    public float Accuracy => TotalQuestions > 0 ? (float)CorrectCount / TotalQuestions : 0f;

    /// <summary>按单词分组的错题统计。</summary>
    public List<WordErrorStat> GetWordErrorStats()
    {
        var stats = new Dictionary<string, WordErrorStat>();
        foreach (var r in Records)
        {
            if (r.Correct) continue;
            var key = r.English.ToLowerInvariant();
            if (!stats.TryGetValue(key, out var stat))
            {
                stat = new WordErrorStat { English = r.English, Chinese = r.Chinese };
                stats[key] = stat;
            }
            stat.ErrorCount++;
            stat.EnergyLost += r.EnergyCost;
        }
        return stats.Values.OrderByDescending(s => s.ErrorCount).ThenByDescending(s => s.EnergyLost).ToList();
    }
}

public sealed class WordErrorStat
{
    public string English { get; init; } = "";
    public string Chinese { get; init; } = "";
    public int ErrorCount { get; set; }
    public int EnergyLost { get; set; }
}
