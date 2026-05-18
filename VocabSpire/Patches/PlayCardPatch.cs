using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using VocabSpire.Services;
using VocabSpire.UI;

namespace VocabSpire.Patches;

/// <summary>
/// 共享状态 —— 两个拦截点共用。
/// </summary>
public static class QuizState
{
    internal static bool Bypass;
    internal static bool SkipEffect;
    internal static bool QuizActive;
}

/// <summary>
/// 单人模式拦截 —— OnPlayWrapper（暂停游戏，体验好）。
/// 联机时不触发（由 TryManualPlay 处理）。
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
        if (GameBridge.IsMultiplayer()) return true; // 联机走 TryManualPlay
        if (QuizState.Bypass) { QuizState.Bypass = false; return true; }
        if (!CombatManager.Instance.IsInProgress) return true;
        if (!VocabManager.Instance.HasActiveBank) return true;

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

            QuizState.Bypass = true;
            if (!correct)
            {
                Log.Info("[VocabSpire] Wrong answer — card effect skipped.");
                QuizState.SkipEffect = true;

                try
                {
                    var cost = card.EnergyCost.GetResolved();
                    question.TargetWord.EnergyLost += cost;
                }
                catch { }
            }

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
}

/// <summary>
/// 联机模式拦截 —— TryManualPlay（不暂停游戏，不卡对方）。
/// 单人时不触发（由 OnPlayWrapper 处理）。
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.TryManualPlay))]
public static class MultiPlayerPatch
{
    public static bool Prefix(CardModel __instance, Creature? target, ref bool __result)
    {
        if (!VocabConfig.Instance.Enabled) return true;
        if (!GameBridge.IsMultiplayer()) return true; // 单人走 OnPlayWrapper
        if (QuizState.Bypass) { QuizState.Bypass = false; return true; }
        if (QuizState.QuizActive) { __result = false; return false; }
        if (!CombatManager.Instance.IsInProgress) return true;
        if (!VocabManager.Instance.HasActiveBank) return true;

        var quiz = QuizPanel.Instance;
        if (quiz is null) return true;

        var question = VocabManager.Instance.GenerateQuiz();
        if (question is null) return true;

        __result = false;
        QuizState.QuizActive = true;

        quiz.ShowQuiz(question, correct =>
        {
            QuizState.QuizActive = false;

            if (!correct)
            {
                Log.Info("[VocabSpire] Wrong answer — card effect will be skipped.");
                QuizState.SkipEffect = true;

                try
                {
                    var cost = __instance.EnergyCost.GetResolved();
                    question.TargetWord.EnergyLost += cost;
                }
                catch { }
            }

            QuizState.Bypass = true;
            __instance.TryManualPlay(target);
        });

        return false;
    }
}

/// <summary>
/// 联机同步 —— NetPlayCardAction 序列化附带 skip 标记。
/// </summary>
[HarmonyPatch(typeof(NetPlayCardAction), nameof(NetPlayCardAction.Serialize))]
public static class NetPlayCardSerializePatch
{
    public static void Postfix(PacketWriter writer)
    {
        writer.WriteBool(QuizState.SkipEffect);
    }
}

[HarmonyPatch(typeof(NetPlayCardAction), nameof(NetPlayCardAction.Deserialize))]
public static class NetPlayCardDeserializePatch
{
    public static void Postfix(PacketReader reader)
    {
        try
        {
            if (reader.ReadBool())
            {
                QuizState.SkipEffect = true;
                Log.Info("[VocabSpire] Received skip flag from network.");
            }
        }
        catch { }
    }
}

/// <summary>
/// 拦截所有 CardModel 子类的 OnPlay —— 答错时跳过效果。
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

        QuizState.SkipEffect = false;
        Log.Info($"[VocabSpire] OnPlay skipped for {__instance?.GetType().Name} (wrong answer).");
        __result = Task.CompletedTask;
        return false;
    }
}
