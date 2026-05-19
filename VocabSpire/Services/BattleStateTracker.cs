using System.Collections.Generic;
using VocabSpire.Models;

namespace VocabSpire.Services;

/// <summary>
/// 战斗状态追踪：每回合容错使用计数 + 玩家级别连续答对计数 + 免错券激活状态。
/// 仅在卡牌主人（本地玩家）端维护。
/// </summary>
public sealed class BattleStateTracker
{
    public static BattleStateTracker Instance { get; } = new();

    /// <summary>本回合已使用的容错次数。</summary>
    public int ToleranceUsedThisTurn { get; private set; }

    /// <summary>玩家级别连续答对计数（跨回合累积，答错重置）。</summary>
    public int CorrectStreak { get; private set; }

    /// <summary>免错券是否已激活（玩家点过按钮）。</summary>
    public bool FreePassArmed { get; private set; }

    private BattleStateTracker() { }

    // ── 回合 / 战斗生命周期 ──

    public void OnSideTurnStart()
    {
        ToleranceUsedThisTurn = 0;
    }

    public void OnCombatEnd()
    {
        ToleranceUsedThisTurn = 0;
        CorrectStreak = 0;
        FreePassArmed = false;
    }

    // ── 容错 ──

    public bool CanUseTolerance()
    {
        var cfg = VocabConfig.Instance;
        return cfg.IsToleranceActive && ToleranceUsedThisTurn < cfg.ToleranceCount;
    }

    public void ConsumeTolerance()
    {
        ToleranceUsedThisTurn++;
    }

    // ── 免错券 ──

    /// <summary>玩家点击免错券按钮时调用。返回是否成功激活。</summary>
    public bool TryArmFreePass()
    {
        if (FreePassArmed) return false;
        if (RunBattleState.Instance.GetStock() <= 0) return false;
        FreePassArmed = true;
        return true;
    }

    /// <summary>取消激活（例如玩家再点一次按钮）。</summary>
    public void DisarmFreePass()
    {
        FreePassArmed = false;
    }

    /// <summary>消耗一张已激活的券（在打牌瞬间）。</summary>
    public void ConsumeArmedFreePass()
    {
        if (!FreePassArmed) return;
        FreePassArmed = false;
        RunBattleState.Instance.AddStock(-1);
    }

    /// <summary>累积一张券（达到阈值时调用）。</summary>
    private void GainOneFreePass()
    {
        if (!VocabConfig.Instance.FreePassEnabled) return;
        RunBattleState.Instance.AddStock(+1);
    }

    // ── 答题结果 → 奖励计算 ──

    public sealed class AnswerOutcome
    {
        public List<(RewardType Kind, int Amount)> Rewards { get; } = new();
        public int FreePassGained { get; set; }
    }

    /// <summary>
    /// 记录一次答题结果并计算应触发的奖励列表（不实际应用）。
    /// 答错：重置 Streak，无奖励。
    /// 答对：累加 Streak、按规则触发奖励（一次 / 累积）、检查免错券累积。
    /// </summary>
    public AnswerOutcome RecordAnswer(QuizQuestion question, bool correct)
    {
        var outcome = new AnswerOutcome();
        if (!correct)
        {
            CorrectStreak = 0;
            return outcome;
        }

        CorrectStreak++;
        var cfg = VocabConfig.Instance;

        // 奖励规则
        if (cfg.RewardEnabled)
        {
            foreach (var rule in cfg.RewardRules)
            {
                if (!rule.Enabled || rule.Kind == RewardType.None || rule.Amount <= 0) continue;
                if (rule.Streak <= 0) continue;

                bool triggered = rule.Mode switch
                {
                    RewardTriggerMode.Once      => CorrectStreak == rule.Streak,
                    RewardTriggerMode.Recurring => CorrectStreak >= rule.Streak,
                    _ => false
                };
                if (!triggered) continue;

                var scale = DifficultyScale.Compute(question, rule);
                var amount = DifficultyScale.Scale(rule.Amount, scale);
                if (amount <= 0) continue;

                outcome.Rewards.Add((rule.Kind, amount));
            }
            // streak 不重置：Once 用 ==，Recurring 用 >=；玩家可配置 5/10/15... 多档奖励
        }

        // 免错券累积
        if (cfg.FreePassEnabled && cfg.FreePassStreakCost > 0
            && CorrectStreak > 0 && CorrectStreak % cfg.FreePassStreakCost == 0)
        {
            GainOneFreePass();
            outcome.FreePassGained = 1;
        }

        return outcome;
    }
}
