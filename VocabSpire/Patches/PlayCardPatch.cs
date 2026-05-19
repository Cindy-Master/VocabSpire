using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using VocabSpire.Services;
using VocabSpire.UI;

namespace VocabSpire.Patches;

/// <summary>
/// 共享状态 —— 答题结果通过这些标志驱动各处补丁；联机通过 NetPlayCardAction 同步。
/// </summary>
public static class QuizState
{
    internal static bool Bypass;
    internal static bool SkipEffect;
    internal static bool NoCost;
    internal static bool ReturnToHand;
    internal static bool QuizActive;

    /// <summary>本次打牌完成后需要触发的奖励列表（联机通过 NetPlayCardAction 同步）。</summary>
    internal static readonly List<(byte Kind, int Amount)> PendingRewards = new();

    /// <summary>本次打牌的卡主（用于 OnPlay 完成后施加奖励）。</summary>
    internal static Player? PendingRewardTarget;

    public static void ResetCardLevel()
    {
        SkipEffect = false;
        NoCost = false;
        ReturnToHand = false;
        PendingRewards.Clear();
        PendingRewardTarget = null;
    }
}

/// <summary>
/// 单人模式拦截 —— OnPlayWrapper（暂停游戏，体验好）。
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
public static class SinglePlayerPatch
{
    public static bool Prefix(
        CardModel __instance,
        ref Task __result,
        PlayerChoiceContext choiceContext,
        Creature? target,
        bool isAutoPlay,
        ResourceInfo resources,
        bool skipCardPileVisuals)
    {
        if (!VocabConfig.Instance.Enabled) return true;
        if (GameBridge.IsMultiplayer()) return true;
        if (QuizState.Bypass) { QuizState.Bypass = false; return true; }
        if (!CombatManager.Instance.IsInProgress) return true;
        if (!VocabManager.Instance.HasActiveBank) return true;

        // 免错券激活 → 跳过答题，直接正常打出
        if (BattleStateTracker.Instance.FreePassArmed)
        {
            BattleStateTracker.Instance.ConsumeArmedFreePass();
            FreePassButton.Instance?.Refresh();
            Log.Info("[VocabSpire] Free pass consumed — skipping quiz.");
            return true;
        }

        var quiz = QuizPanel.Instance;
        if (quiz is null) return true;

        var question = VocabManager.Instance.GenerateQuiz();
        if (question is null) return true;

        var tcs = new TaskCompletionSource();

        __result = RunQuizAsync(
            tcs, quiz, question, __instance,
            choiceContext, target, isAutoPlay, resources, skipCardPileVisuals);

        return false;
    }

    private static async Task RunQuizAsync(
        TaskCompletionSource tcs,
        QuizPanel quiz,
        Models.QuizQuestion question,
        CardModel card,
        PlayerChoiceContext choiceContext,
        Creature? target,
        bool isAutoPlay,
        ResourceInfo resources,
        bool skipCardPileVisuals)
    {
        GameBridge.SetGamePaused(true);

        quiz.ShowQuiz(question, correct =>
        {
            GameBridge.SetGamePaused(false);
            ApplyAnswerEffects(card, question, correct);
            QuizState.Bypass = true;

            var task = card.OnPlayWrapper(
                choiceContext, target, isAutoPlay, resources, skipCardPileVisuals);
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Log.Error($"[VocabSpire] OnPlayWrapper faulted: {t.Exception?.GetBaseException()}");
                tcs.SetResult();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        });

        await tcs.Task;
    }

    /// <summary>核心：根据答题结果设置所有打牌标志（容错/回手/扣费/奖励）。</summary>
    internal static void ApplyAnswerEffects(CardModel card, Models.QuizQuestion question, bool correct)
    {
        var cfg = VocabConfig.Instance;
        QuizState.ResetCardLevel();

        // 计算奖励 + 更新 streak（仅卡主端，结果通过 NetPlayCardAction 同步）
        var outcome = BattleStateTracker.Instance.RecordAnswer(question, correct);

        if (correct)
        {
            foreach (var r in outcome.Rewards)
            {
                QuizState.PendingRewards.Add(((byte)r.Kind, r.Amount));
            }
            if (QuizState.PendingRewards.Count > 0)
            {
                QuizState.PendingRewardTarget = card.Owner;
            }
            FreePassButton.Instance?.Refresh();
            return;
        }

        // 答错：先记账
        try
        {
            var cost = card.EnergyCost.GetResolved();
            question.TargetWord.EnergyLost += cost;
        }
        catch { }

        QuizState.SkipEffect = true;

        if (BattleStateTracker.Instance.CanUseTolerance())
        {
            QuizState.NoCost = true;
            QuizState.ReturnToHand = true;
            BattleStateTracker.Instance.ConsumeTolerance();
            Log.Info($"[VocabSpire] Tolerance used ({BattleStateTracker.Instance.ToleranceUsedThisTurn}/{cfg.ToleranceCount}).");
        }
        else if (cfg.WrongCardReturnToHand)
        {
            QuizState.ReturnToHand = true;
        }
    }
}

/// <summary>
/// 联机模式拦截 —— TryManualPlay。
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.TryManualPlay))]
public static class MultiPlayerPatch
{
    public static bool Prefix(CardModel __instance, Creature? target, ref bool __result)
    {
        if (!VocabConfig.Instance.Enabled) return true;
        if (!GameBridge.IsMultiplayer()) return true;
        if (QuizState.Bypass) { QuizState.Bypass = false; return true; }
        if (QuizState.QuizActive) { __result = false; return false; }
        if (!CombatManager.Instance.IsInProgress) return true;
        if (!VocabManager.Instance.HasActiveBank) return true;

        // 免错券（联机）：跳过题目直接正常打出（联机透明：其他端看到无标志位）
        if (BattleStateTracker.Instance.FreePassArmed)
        {
            BattleStateTracker.Instance.ConsumeArmedFreePass();
            FreePassButton.Instance?.Refresh();
            Log.Info("[VocabSpire] Free pass consumed (MP) — skipping quiz.");
            return true;
        }

        var quiz = QuizPanel.Instance;
        if (quiz is null) return true;

        var question = VocabManager.Instance.GenerateQuiz();
        if (question is null) return true;

        __result = false;
        QuizState.QuizActive = true;

        quiz.ShowQuiz(question, correct =>
        {
            QuizState.QuizActive = false;
            SinglePlayerPatch.ApplyAnswerEffects(__instance, question, correct);
            QuizState.Bypass = true;
            __instance.TryManualPlay(target);
        });

        return false;
    }
}

/// <summary>
/// 联机同步 —— NetPlayCardAction 序列化附带答题标志位 + 变长奖励数组。
/// 协议：[skip][nocost][returnhand][rewardCount:4 bit] [(kind:4, amount:16) × N]
/// </summary>
[HarmonyPatch(typeof(NetPlayCardAction), nameof(NetPlayCardAction.Serialize))]
public static class NetPlayCardSerializePatch
{
    public static void Postfix(PacketWriter writer)
    {
        writer.WriteBool(QuizState.SkipEffect);
        writer.WriteBool(QuizState.NoCost);
        writer.WriteBool(QuizState.ReturnToHand);

        var count = System.Math.Min(QuizState.PendingRewards.Count, 15);
        writer.WriteUInt((uint)count, 4);
        for (var i = 0; i < count; i++)
        {
            var (kind, amount) = QuizState.PendingRewards[i];
            writer.WriteUInt(kind, 4);
            writer.WriteInt(amount, 16);
        }
    }
}

[HarmonyPatch(typeof(NetPlayCardAction), nameof(NetPlayCardAction.Deserialize))]
public static class NetPlayCardDeserializePatch
{
    public static void Postfix(PacketReader reader)
    {
        try
        {
            QuizState.ResetCardLevel();
            if (reader.ReadBool()) QuizState.SkipEffect = true;
            if (reader.ReadBool()) QuizState.NoCost = true;
            if (reader.ReadBool()) QuizState.ReturnToHand = true;

            var count = (int)reader.ReadUInt(4);
            for (var i = 0; i < count; i++)
            {
                var kind = (byte)reader.ReadUInt(4);
                var amount = (int)reader.ReadInt(16);
                QuizState.PendingRewards.Add((kind, amount));
            }
            // PendingRewardTarget 由 OnPlay Postfix 从 __instance.Owner 推断
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire] Deserialize failed: {ex.Message}");
        }
    }
}

/// <summary>
/// 拦截 SpendResources —— 容错触发时直接返回 (0,0) 不扣费。
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.SpendResources))]
public static class SpendResourcesPatch
{
    public static bool Prefix(ref Task<(int, int)> __result)
    {
        if (!QuizState.NoCost) return true;
        __result = Task.FromResult((0, 0));
        Log.Info("[VocabSpire] SpendResources skipped (tolerance).");
        return false;
    }
}

/// <summary>
/// 拦截 GetResultPileType —— 答错回手时强制返回 Hand。
/// </summary>
[HarmonyPatch]
public static class GetResultPileTypePatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var baseType = typeof(CardModel);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic
                  | BindingFlags.Public | BindingFlags.DeclaredOnly;

        foreach (var type in baseType.Assembly.GetTypes())
        {
            if (!baseType.IsAssignableFrom(type)) continue;
            var method = type.GetMethod("GetResultPileType", flags, null, System.Type.EmptyTypes, null);
            if (method is null) continue;
            yield return method;
        }
    }

    public static void Postfix(ref PileType __result)
    {
        if (QuizState.ReturnToHand && __result != PileType.None)
        {
            __result = PileType.Hand;
        }
    }
}

/// <summary>
/// 拦截所有 CardModel 子类的 OnPlay —— 答错跳过；正确（有奖励）则施加奖励。
/// </summary>
[HarmonyPatch]
public static class OnPlaySkipPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var baseType = typeof(CardModel);
        var paramTypes = new[] { typeof(PlayerChoiceContext), typeof(CardPlay) };
        var flags = BindingFlags.Instance | BindingFlags.NonPublic
                  | BindingFlags.Public | BindingFlags.DeclaredOnly;
        var count = 0;

        foreach (var type in baseType.Assembly.GetTypes())
        {
            if (!baseType.IsAssignableFrom(type)) continue;
            var method = type.GetMethod("OnPlay", flags, null, paramTypes, null);
            if (method is null) continue;
            count++;
            yield return method;
        }

        Log.Info($"[VocabSpire] Patched {count} OnPlay methods.");
    }

    public static bool Prefix(object __instance, ref Task __result)
    {
        if (!QuizState.SkipEffect) return true;

        Log.Info($"[VocabSpire] OnPlay skipped for {__instance?.GetType().Name} (wrong answer).");
        __result = Task.CompletedTask;
        return false;
    }

    /// <summary>Postfix 用于在 OnPlay 完成（或被跳过）后触发批量奖励。</summary>
    public static void Postfix(object __instance, ref Task __result)
    {
        var hasReward = QuizState.PendingRewards.Count > 0;
        if (!hasReward)
        {
            QuizState.SkipEffect = false;
            QuizState.NoCost = false;
            QuizState.ReturnToHand = false;
            return;
        }

        // 取出快照（PendingRewards 是共享 List，下一次打牌前可能被重置）
        var rewards = new List<(RewardType, int)>(QuizState.PendingRewards.Count);
        foreach (var (k, a) in QuizState.PendingRewards) rewards.Add(((RewardType)k, a));

        var target = QuizState.PendingRewardTarget
            ?? (__instance is CardModel cm ? cm.Owner : null);

        QuizState.SkipEffect = false;
        QuizState.NoCost = false;
        QuizState.ReturnToHand = false;
        QuizState.PendingRewards.Clear();
        QuizState.PendingRewardTarget = null;

        var original = __result;
        __result = ChainRewards(original, target, rewards);
    }

    private static async Task ChainRewards(Task original, Player? target, List<(RewardType Kind, int Amount)> rewards)
    {
        try { await original; } catch { }
        if (target is null || rewards.Count == 0) return;
        await RewardService.ApplyAllAsync(target, rewards);
    }
}
