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

    /// <summary>适用题型：None = 全部题型都触发；设为某题型则只在该题型答错时触发（与奖励对称）。</summary>
    [JsonPropertyName("quiz_type_filter")]
    public QuizModeFlags QuizTypeFilter { get; set; } = QuizModeFlags.None;

    // ── 高级：连错机制（每条规则独立，与奖励对称）──
    /// <summary>连错计数重算范围：永久（默认，= 老行为）/ 每场战斗 / 每回合。</summary>
    [JsonPropertyName("reset_scope")]
    public StreakResetScope ResetScope { get; set; } = StreakResetScope.Persistent;

    /// <summary>冷却：本规则触发后，需再答满 N 题才能再次触发。0 = 无冷却。</summary>
    [JsonPropertyName("cooldown")]
    public int Cooldown { get; set; }

    /// <summary>连错计数封顶：评估本规则时连错数最多按 N 算。0 = 不封顶。</summary>
    [JsonPropertyName("streak_cap")]
    public int StreakCap { get; set; }

    /// <summary>本重算周期内最多触发次数：达到后本周期不再触发，直到重算范围边界重置。0 = 无限。</summary>
    [JsonPropertyName("max_triggers")]
    public int MaxTriggers { get; set; }

    public PunishmentRule Clone() => new()
    {
        Enabled = Enabled,
        Kind = Kind,
        Streak = Streak,
        Amount = Amount,
        Mode = Mode,
        MultiDefDouble = MultiDefDouble,
        DifficultyScaling = DifficultyScaling,
        QuizTypeFilter = QuizTypeFilter,
        ResetScope = ResetScope,
        Cooldown = Cooldown,
        StreakCap = StreakCap,
        MaxTriggers = MaxTriggers
    };
}
