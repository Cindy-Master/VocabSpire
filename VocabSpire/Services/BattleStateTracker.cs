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

    /// <summary>玩家级别连续答错计数（跨回合累积，答对重置）。镜像 CorrectStreak。</summary>
    public int WrongStreak { get; private set; }

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
        WrongStreak = 0;
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
        public List<(RewardType Kind, int Amount)> Punishments { get; } = new();
        public int FreePassGained { get; set; }
    }

    /// <summary>
    /// 记录一次答题结果并计算应触发的奖励 / 惩罚列表（不实际应用）。
    /// 答对：CorrectStreak++、WrongStreak=0、按 RewardRules 触发奖励、检查免错券累积。
    /// 答错：WrongStreak++、CorrectStreak=0、按 PunishmentRules 触发惩罚。
    /// </summary>
    public AnswerOutcome RecordAnswer(QuizQuestion question, bool correct)
    {
        var outcome = new AnswerOutcome();
        var cfg = VocabConfig.Instance;

        if (!correct)
        {
            CorrectStreak = 0;
            WrongStreak++;
            MegaCrit.Sts2.Core.Logging.Log.Info(
                $"[VocabSpire][Punish] RecordAnswer: WRONG streak={WrongStreak} " +
                $"PunishmentEnabled={cfg.PunishmentEnabled} PunishmentRules.Count={cfg.PunishmentRules.Count}");

            if (cfg.PunishmentEnabled)
            {
                for (var idx = 0; idx < cfg.PunishmentRules.Count; idx++)
                {
                    var rule = cfg.PunishmentRules[idx];
                    var preLog = $"[VocabSpire][Punish]   rule[{idx}] Kind={rule.Kind}({(int)rule.Kind}) Enabled={rule.Enabled} Streak={rule.Streak} Amount={rule.Amount} Mode={rule.Mode}";

                    if (!rule.Enabled) { MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (disabled)"); continue; }
                    if (rule.Kind == RewardType.None) { MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (kind=None)"); continue; }
                    if (rule.Amount <= 0) { MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (amount<=0)"); continue; }
                    if (rule.Streak <= 0) { MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (streak<=0)"); continue; }

                    bool triggered = rule.Mode switch
                    {
                        RewardTriggerMode.Once      => WrongStreak == rule.Streak,
                        RewardTriggerMode.Recurring => WrongStreak >= rule.Streak,
                        RewardTriggerMode.EveryN    => WrongStreak >= rule.Streak && WrongStreak % rule.Streak == 0,
                        _ => false
                    };
                    if (!triggered)
                    {
                        MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (triggered=false: wrong_streak={WrongStreak} vs threshold={rule.Streak} mode={rule.Mode})");
                        continue;
                    }

                    var scaleP = DifficultyScale.Compute(question, rule);
                    var amountP = DifficultyScale.Scale(rule.Amount, scaleP);
                    if (amountP <= 0) { MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (scaled amount={amountP} <=0)"); continue; }

                    MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → ✓ TRIGGERED (scaled={amountP} via scale={scaleP:F2})");
                    outcome.Punishments.Add((rule.Kind, amountP));
                }
            }

            return outcome;
        }

        CorrectStreak++;
        WrongStreak = 0;

        MegaCrit.Sts2.Core.Logging.Log.Info(
            $"[VocabSpire][Reward] RecordAnswer: CORRECT streak={CorrectStreak} " +
            $"RewardEnabled={cfg.RewardEnabled} RewardRules.Count={cfg.RewardRules.Count}");

        // 奖励规则
        if (cfg.RewardEnabled)
        {
            for (var idx = 0; idx < cfg.RewardRules.Count; idx++)
            {
                var rule = cfg.RewardRules[idx];
                var preLog = $"[VocabSpire][Reward]   rule[{idx}] Kind={rule.Kind}({(int)rule.Kind}) Enabled={rule.Enabled} Streak={rule.Streak} Amount={rule.Amount} Mode={rule.Mode}";

                if (!rule.Enabled) { MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (disabled)"); continue; }
                if (rule.Kind == RewardType.None) { MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (kind=None)"); continue; }
                if (rule.Amount <= 0) { MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (amount<=0)"); continue; }
                if (rule.Streak <= 0) { MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (streak<=0)"); continue; }

                bool triggered = rule.Mode switch
                {
                    RewardTriggerMode.Once      => CorrectStreak == rule.Streak,
                    RewardTriggerMode.Recurring => CorrectStreak >= rule.Streak,
                    RewardTriggerMode.EveryN    => CorrectStreak >= rule.Streak && CorrectStreak % rule.Streak == 0,
                    _ => false
                };
                if (!triggered)
                {
                    MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (triggered=false: streak={CorrectStreak} vs threshold={rule.Streak} mode={rule.Mode})");
                    continue;
                }

                var scale = DifficultyScale.Compute(question, rule);
                var amount = DifficultyScale.Scale(rule.Amount, scale);
                if (amount <= 0) { MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (scaled amount={amount} <=0)"); continue; }

                MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → ✓ TRIGGERED (scaled={amount} via scale={scale:F2})");
                outcome.Rewards.Add((rule.Kind, amount));
            }
        }
        else
        {
            MegaCrit.Sts2.Core.Logging.Log.Info("[VocabSpire][Reward] RewardEnabled=false, no rules processed.");
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
