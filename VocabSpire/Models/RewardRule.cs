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

/// <summary>连对/连错计数的重算范围（在答相反结果重置之外，额外的边界重置）。</summary>
public enum StreakResetScope
{
    /// <summary>永久：只在答相反结果时重置，跨回合跨战斗累积（= 老版本行为，默认）。</summary>
    Persistent = 0,
    /// <summary>每场战斗：战斗开始时也重置。</summary>
    Combat = 1,
    /// <summary>每回合：己方回合开始时也重置。</summary>
    Turn = 2
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

    /// <summary>适用题型：None = 全部题型都触发；设为某题型（如拼写 SpellEnglish）则只在该题型答对时触发。
    /// 用于「拼写题→+力量、听力题→+覆甲」这类按题型定制的独立奖励。</summary>
    [JsonPropertyName("quiz_type_filter")]
    public QuizModeFlags QuizTypeFilter { get; set; } = QuizModeFlags.None;

    // ── 高级：连对机制（每条规则独立）──
    /// <summary>连对计数重算范围：永久（默认，= 老行为）/ 每场战斗 / 每回合。</summary>
    [JsonPropertyName("reset_scope")]
    public StreakResetScope ResetScope { get; set; } = StreakResetScope.Persistent;

    /// <summary>冷却：本规则触发后，需再答满 N 题才能再次触发。0 = 无冷却。</summary>
    [JsonPropertyName("cooldown")]
    public int Cooldown { get; set; }

    /// <summary>连对计数封顶：评估本规则时连对数最多按 N 算（防 Recurring/EveryN 无限放大）。0 = 不封顶。</summary>
    [JsonPropertyName("streak_cap")]
    public int StreakCap { get; set; }

    /// <summary>本重算周期内最多触发次数：达到后本周期不再触发，直到重算范围边界重置。0 = 无限。</summary>
    [JsonPropertyName("max_triggers")]
    public int MaxTriggers { get; set; }

    public RewardRule Clone() => new()
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
