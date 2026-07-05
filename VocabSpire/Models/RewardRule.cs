using System.Text.Json.Serialization;
using VocabSpire.Services;

namespace VocabSpire.Models;

/// <summary>奖励触发模式。</summary>
public enum RewardTriggerMode
{
    /// <summary>达标一次：连胜恰好等于阈值时触发一次，之后不再触发，直到答错重置后重新累到阈值。</summary>
    Once = 0,
    /// <summary>持续生效：连胜 ≥ 阈值时每次答对都触发。阈值 1 = 每次答对都给。</summary>
    Recurring = 1,
    /// <summary>每 N 次：连胜达到阈值的整数倍时触发（阈值 5 → 5/10/15…）。</summary>
    EveryN = 2
}

/// <summary>
/// 单条奖励规则。多条规则可独立配置，原子化搭配。
/// </summary>
public sealed class RewardRule
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("kind")]
    public RewardType Kind { get; set; } = RewardType.Gold;

    [JsonPropertyName("streak")]
    public int Streak { get; set; } = 3;

    [JsonPropertyName("amount")]
    public int Amount { get; set; } = 5;

    [JsonPropertyName("mode")]
    public RewardTriggerMode Mode { get; set; } = RewardTriggerMode.Recurring;

    /// <summary>多释义题答对时奖励翻倍。</summary>
    [JsonPropertyName("multi_def_double")]
    public bool MultiDefDouble { get; set; }

    /// <summary>启用难度加成（按题型权重缩放）。</summary>
    [JsonPropertyName("difficulty_scaling")]
    public bool DifficultyScaling { get; set; }

    public RewardRule Clone() => new()
    {
        Enabled = Enabled,
        Kind = Kind,
        Streak = Streak,
        Amount = Amount,
        Mode = Mode,
        MultiDefDouble = MultiDefDouble,
        DifficultyScaling = DifficultyScaling
    };
}
