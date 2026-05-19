using System.Text.Json.Serialization;
using VocabSpire.Services;

namespace VocabSpire.Models;

/// <summary>奖励触发模式。</summary>
public enum RewardTriggerMode
{
    /// <summary>达到阈值触发一次后重置（连对 3 触发 → 重新从 0 计）。</summary>
    Once = 0,
    /// <summary>达到阈值后，之后每次答对都触发（连对 3 后每张牌都+1费）。</summary>
    Recurring = 1
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
    public RewardTriggerMode Mode { get; set; } = RewardTriggerMode.Once;

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
