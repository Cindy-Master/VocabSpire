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

    // ── 连对机制：按重算范围分别维护「每回合 / 每场战斗」计数；CorrectStreak/WrongStreak 为「永久」范围 ──
    private int _correctTurn, _wrongTurn;       // 每回合范围（回合开始额外重置）
    private int _correctCombat, _wrongCombat;   // 每场战斗范围（战斗开始额外重置）
    private int _answerSeq;                      // 单调递增的累计答题数（冷却按此计）
    private int _turnPeriod, _combatPeriod;      // 周期编号（用于按范围重置「最多触发次数」）

    /// <summary>每条规则的运行时状态（冷却上次触发序号 + 本周期触发次数）。key = 规则对象。</summary>
    private sealed class RuleRuntime
    {
        public int LastTriggerSeq = int.MinValue / 2;
        public int TriggerCount;
        public int CountPeriod = -1;
    }
    private readonly Dictionary<object, RuleRuntime> _ruleRt = new();

    private BattleStateTracker() { }

    // ── 回合 / 战斗生命周期 ──

    public void OnSideTurnStart()
    {
        ToleranceUsedThisTurn = 0;
        _correctTurn = 0;
        _wrongTurn = 0;
        _turnPeriod++;
    }

    /// <summary>战斗开始时调用（CombatSetUp）：重置「每场战斗 / 每回合」范围计数与周期。
    /// 「永久」范围（CorrectStreak/WrongStreak）不动，跨战斗累积；每规则运行时用周期编号自然失效，不清空。</summary>
    public void OnCombatReset()
    {
        ToleranceUsedThisTurn = 0;
        _correctCombat = 0;
        _wrongCombat = 0;
        _correctTurn = 0;
        _wrongTurn = 0;
        _combatPeriod++;
        _turnPeriod++;
    }

    public void OnCombatEnd()
    {
        ToleranceUsedThisTurn = 0;
        CorrectStreak = 0;
        WrongStreak = 0;
        FreePassArmed = false;
    }

    // ── 连对机制辅助 ──

    /// <summary>按重算范围取原始连对/连错计数。correct=true 取连对，false 取连错。</summary>
    private int RawStreak(StreakResetScope scope, bool correct) => scope switch
    {
        StreakResetScope.Turn   => correct ? _correctTurn   : _wrongTurn,
        StreakResetScope.Combat => correct ? _correctCombat : _wrongCombat,
        _                       => correct ? CorrectStreak  : WrongStreak   // Persistent
    };

    private int PeriodOf(StreakResetScope scope) => scope switch
    {
        StreakResetScope.Turn   => _turnPeriod,
        StreakResetScope.Combat => _combatPeriod,
        _                       => 0
    };

    private RuleRuntime GetRuntime(object key)
    {
        if (!_ruleRt.TryGetValue(key, out var rt)) { rt = new RuleRuntime(); _ruleRt[key] = rt; }
        return rt;
    }

    /// <summary>综合判断一条规则是否可触发：阈值/模式 + 连对封顶 + 冷却 + 本周期最多触发次数。不提交（提交见 CommitTrigger）。</summary>
    private bool CanTrigger(object ruleKey, StreakResetScope scope, RewardTriggerMode mode,
        int threshold, int streakCap, int cooldown, int maxTriggers, int rawStreak)
    {
        var streak = rawStreak;
        if (streakCap > 0 && streak > streakCap) streak = streakCap;   // 连对计数封顶

        var baseTrig = mode switch
        {
            RewardTriggerMode.Once      => streak == threshold,
            RewardTriggerMode.Recurring => streak >= threshold,
            RewardTriggerMode.EveryN    => threshold > 0 && streak >= threshold && streak % threshold == 0,
            _ => false
        };
        if (!baseTrig) return false;

        var rt = GetRuntime(ruleKey);
        var period = PeriodOf(scope);
        if (rt.CountPeriod != period) { rt.CountPeriod = period; rt.TriggerCount = 0; }   // 跨周期→重置本周期触发次数

        if (cooldown > 0 && (_answerSeq - rt.LastTriggerSeq) < cooldown) return false;    // 冷却中
        if (maxTriggers > 0 && rt.TriggerCount >= maxTriggers) return false;              // 已达本周期上限
        return true;
    }

    /// <summary>确认奖励/惩罚实际发放后提交触发（记冷却序号 + 本周期次数+1）。</summary>
    private void CommitTrigger(object ruleKey)
    {
        var rt = GetRuntime(ruleKey);
        rt.LastTriggerSeq = _answerSeq;
        rt.TriggerCount++;
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
        _answerSeq++;   // 累计答题数（冷却按此计）

        if (!correct)
        {
            CorrectStreak = 0; _correctCombat = 0; _correctTurn = 0;
            WrongStreak++; _wrongCombat++; _wrongTurn++;
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

                    // 题型筛选：只对该题型答错时触发本条惩罚（对称于奖励）
                    if (rule.QuizTypeFilter != QuizModeFlags.None && rule.QuizTypeFilter != question.Mode)
                    {
                        MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (题型筛选 {rule.QuizTypeFilter} ≠ 本题 {question.Mode})");
                        continue;
                    }

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

        CorrectStreak++; _correctCombat++; _correctTurn++;
        WrongStreak = 0; _wrongCombat = 0; _wrongTurn = 0;

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

                // 题型筛选：规则设了适用题型时，只对该题型答对时触发（拼写题→+力量、听力题→+覆甲等）
                if (rule.QuizTypeFilter != QuizModeFlags.None && rule.QuizTypeFilter != question.Mode)
                {
                    MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (题型筛选 {rule.QuizTypeFilter} ≠ 本题 {question.Mode})");
                    continue;
                }

                // 连对机制：按重算范围取原始连对数，综合封顶/冷却/最多触发判定
                var rawStreak = RawStreak(rule.ResetScope, correct: true);
                if (!CanTrigger(rule, rule.ResetScope, rule.Mode, rule.Streak, rule.StreakCap, rule.Cooldown, rule.MaxTriggers, rawStreak))
                {
                    MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (未触发: scope={rule.ResetScope} rawStreak={rawStreak} cap={rule.StreakCap} cd={rule.Cooldown} max={rule.MaxTriggers})");
                    continue;
                }

                var scale = DifficultyScale.Compute(question, rule);
                var amount = DifficultyScale.Scale(rule.Amount, scale);
                if (amount <= 0) { MegaCrit.Sts2.Core.Logging.Log.Info($"{preLog} → SKIP (scaled amount={amount} <=0)"); continue; }

                CommitTrigger(rule);   // 确认发放后才提交触发（记冷却+周期次数）
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
