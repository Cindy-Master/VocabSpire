using VocabSpire.Models;

namespace VocabSpire.Services;

/// <summary>
/// 按题型计算难度加成系数。EnToCn 选择题最简单，拼写最难。
/// 系数应用于 RewardRule.Amount。
/// </summary>
public static class DifficultyScale
{
    /// <summary>根据题目模式与多释义计算系数（奖励）。</summary>
    public static float Compute(QuizQuestion question, RewardRule rule)
        => ComputeCore(question, rule.Mode, rule.DifficultyScaling, rule.MultiDefDouble);

    /// <summary>根据题目模式与多释义计算系数（惩罚）—— 同奖励逻辑。</summary>
    public static float Compute(QuizQuestion question, PunishmentRule rule)
        => ComputeCore(question, rule.Mode, rule.DifficultyScaling, rule.MultiDefDouble);

    private static float ComputeCore(QuizQuestion question, RewardTriggerMode mode, bool difficultyScaling, bool multiDefDouble)
    {
        var scale = 1.0f;
        if (difficultyScaling)
        {
            scale *= mode == RewardTriggerMode.Recurring
                ? RecurringByMode(question.Mode)
                : OnceByMode(question.Mode);
        }
        if (multiDefDouble && question.IsMultiSelect)
        {
            scale *= 2f;
        }
        return scale;
    }

    /// <summary>对最终奖励数量做缩放。</summary>
    public static int Scale(int amount, float scale)
    {
        if (scale <= 0) return 0;
        var v = (int)System.MathF.Round(amount * scale);
        return System.Math.Max(1, v);
    }

    private static float OnceByMode(QuizModeFlags mode) => mode switch
    {
        QuizModeFlags.EnglishToChinese => 1.0f,
        QuizModeFlags.ChineseToEnglish => 1.5f,
        QuizModeFlags.ListenToChinese  => 1.5f,
        QuizModeFlags.SpellEnglish     => 2.0f,
        _ => 1.0f
    };

    /// <summary>Recurring 模式系数更保守，避免无限叠加爆表。</summary>
    private static float RecurringByMode(QuizModeFlags mode) => mode switch
    {
        QuizModeFlags.EnglishToChinese => 1.0f,
        QuizModeFlags.ChineseToEnglish => 1.2f,
        QuizModeFlags.ListenToChinese  => 1.2f,
        QuizModeFlags.SpellEnglish     => 1.5f,
        _ => 1.0f
    };
}
