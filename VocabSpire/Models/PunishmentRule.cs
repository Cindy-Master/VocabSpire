using System.Text.Json.Serialization;
using VocabSpire.Services;

namespace VocabSpire.Models;

/// <summary>
/// 单条惩罚规则。按 WrongStreak（连错计数）触发，效果与 RewardRule 反向：
/// HP 直接掉血 / Energy 扣费 / Gold 扣金 / Power（力量/敏捷/荆棘/集中/人工）应用负值
/// / Block 直接扣格挡 / Draw 转化为随机弃手牌 N 张。
/// 字段完全对称 RewardRule，方便复用 UI（BuildRuleRow）。
/// </summary>
public sealed class PunishmentRule
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("kind")]
    public RewardType Kind { get; set; } = RewardType.Hp;

    [JsonPropertyName("streak")]
    public int Streak { get; set; } = 1;

    [JsonPropertyName("amount")]
    public int Amount { get; set; } = 3;

    [JsonPropertyName("mode")]
    public RewardTriggerMode Mode { get; set; } = RewardTriggerMode.Recurring;

    /// <summary>多释义题答错时惩罚翻倍（跟奖励对称）。</summary>
    [JsonPropertyName("multi_def_double")]
    public bool MultiDefDouble { get; set; }

    /// <summary>启用难度加成（按题型权重缩放，复用 DifficultyScale.Compute）。</summary>
    [JsonPropertyName("difficulty_scaling")]
    public bool DifficultyScaling { get; set; }

    public PunishmentRule Clone() => new()
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
