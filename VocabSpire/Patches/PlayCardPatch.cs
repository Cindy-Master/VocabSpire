using System.Collections.Generic;
using System.Linq;
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

    /// <summary>本次打牌完成后需要触发的惩罚列表（联机通过 NetPlayCardAction 同步）。</summary>
    internal static readonly List<(byte Kind, int Amount)> PendingPunishments = new();

    /// <summary>本次打牌的卡主（用于 OnPlay 完成后施加奖励 / 惩罚）。</summary>
    internal static Player? PendingRewardTarget;

    public static void ResetCardLevel()
    {
        SkipEffect = false;
        NoCost = false;
        ReturnToHand = false;
        PendingRewards.Clear();
        PendingPunishments.Clear();
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
        var cardName = __instance.GetType().Name;
        var isMp = GameBridge.IsMultiplayer();
        Log.Info($"[VocabSpire][SP] OnPlayWrapper Prefix ENTER: card={cardName} isMp={isMp} " +
                 $"Bypass={QuizState.Bypass} QuizActive={QuizState.QuizActive} " +
                 $"CombatInProgress={CombatManager.Instance.IsInProgress} Enabled={VocabConfig.Instance.Enabled}");

        if (!VocabConfig.Instance.Enabled) { Log.Info("[VocabSpire][SP] → SKIP: Enabled=false"); return true; }
        if (isMp) { Log.Info("[VocabSpire][SP] → SKIP: IsMultiplayer=true (走 MP 路径)"); return true; }
        if (QuizState.Bypass) { Log.Info("[VocabSpire][SP] → BYPASS consumed"); QuizState.Bypass = false; return true; }
        if (!CombatManager.Instance.IsInProgress) { Log.Info("[VocabSpire][SP] → SKIP: combat not in progress"); return true; }
        if (!VocabManager.Instance.HasActiveBank) { Log.Info("[VocabSpire][SP] → SKIP: no active wordbank"); return true; }

        // 横祸/嵌套触发：第 1 张牌的 quiz 还在显示时，第 2 张牌（横祸触发的额外打牌）
        // 进入 OnPlayWrapper —— 此时若再开一个 quiz 会覆盖 callback，导致第 1 张
        // 的 tcs 永不结算 → 游戏卡死。让这种"嵌套牌"跳过答题、按原版正常打出。
        if (QuizState.QuizActive)
        {
            Log.Info($"[VocabSpire][SP] Quiz active — skip nested card '{cardName}' (no quiz, normal play).");
            return true;
        }

        // 免错券激活 → 跳过答题，直接正常打出
        if (BattleStateTracker.Instance.FreePassArmed)
        {
            BattleStateTracker.Instance.ConsumeArmedFreePass();
            SafeRefreshFreePassButton();
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
        // 标记：quiz 已激活。任何嵌套触发的牌（横祸/横扫/任何会引发额外 OnPlayWrapper
        // 的卡牌效果，跨所有角色）进入 SinglePlayerPatch.Prefix 时检测到这个标志会
        // 直接跳过答题让原版执行，不会覆盖当前 quiz 的 callback。
        QuizState.QuizActive = true;
        GameBridge.SetGamePaused(true);

        quiz.ShowQuiz(question, correct =>
        {
            // 包整个 callback 在 try/catch 里，避免任何异常导致 tcs 永不结算（游戏卡死）。
            try
            {
                GameBridge.SetGamePaused(false);
                ApplyAnswerEffects(card, question, correct, resources);
                QuizState.Bypass = true;
                // 注意：QuizActive 不在这里复位 —— 二次 OnPlayWrapper 内部如果触发
                // 嵌套牌（横祸/横扫/任何角色任何会引发额外打牌的卡），
                // 嵌套牌进 Prefix 时检测 QuizActive=true → 直接跳过答题让原版执行，
                // 不会覆盖当前 callback 导致 tcs 永不结算。QuizActive 等到本张牌的
                // OnPlayWrapper 完全结束后再复位。

                var task = card.OnPlayWrapper(
                    choiceContext, target, isAutoPlay, resources, skipCardPileVisuals);
                task.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Log.Error($"[VocabSpire] OnPlayWrapper faulted: {t.Exception?.GetBaseException()}");
                    QuizState.QuizActive = false; // 二次 OnPlayWrapper 完全结束后才复位
                    // 兜底：二次 OnPlayWrapper 已 await 完成（含重放循环 + 奖惩 chain），
                    // 此处复位所有卡级标志，防止重放中途玩家死亡 early-return 导致 SkipEffect 残留到下一张牌。
                    QuizState.ResetCardLevel();
                    tcs.SetResult();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (System.Exception ex)
            {
                Log.Error($"[VocabSpire] Answer callback failed: {ex}");
                try { GameBridge.SetGamePaused(false); } catch { }
                QuizState.ResetCardLevel();
                QuizState.Bypass = false;
                QuizState.QuizActive = false;
                tcs.SetResult(); // 保底：保证游戏继续，不卡死
            }
        });

        await tcs.Task;
    }

    /// <summary>核心：根据答题结果设置所有打牌标志（容错/回手/扣费/奖励）。</summary>
    internal static void ApplyAnswerEffects(CardModel card, Models.QuizQuestion question, bool correct, ResourceInfo resources = default)
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
            SafeRefreshFreePassButton();
            return;
        }

        // 答错：先记账
        try
        {
            var cost = card.EnergyCost.GetResolved();
            question.TargetWord.EnergyLost += cost;
        }
        catch { }

        // 把惩罚写入 PendingPunishments（不论 SkipEffect 开关，惩罚都生效——是独立维度）
        foreach (var p in outcome.Punishments)
        {
            QuizState.PendingPunishments.Add(((byte)p.Kind, p.Amount));
        }
        if (QuizState.PendingPunishments.Count > 0 && QuizState.PendingRewardTarget is null)
        {
            QuizState.PendingRewardTarget = card.Owner;
        }

        // "答错跳过卡牌效果" 总开关：关闭则牌正常生效，不强制 NoCost/ReturnToHand，容错也不触发。
        if (!cfg.WrongAnswerSkipEffect)
        {
            Log.Info("[VocabSpire] Wrong answer: SkipEffect disabled by config — card plays normally; punishment(s) only.");
            return;
        }

        QuizState.SkipEffect = true;

        if (BattleStateTracker.Instance.CanUseTolerance())
        {
            QuizState.NoCost = true;
            QuizState.ReturnToHand = true;
            BattleStateTracker.Instance.ConsumeTolerance();
            // 关键：能量已在 PlayCardAction.ExecuteAction 中扣过（SpendResources 在 OnPlayWrapper 之前），
            // 这里要把实际花费的能量+星费补回。
            RefundCardCost(card, resources);
            Log.Info($"[VocabSpire] Tolerance used ({BattleStateTracker.Instance.ToleranceUsedThisTurn}/{cfg.ToleranceCount}); refunded energy={resources.EnergySpent} stars={resources.StarsSpent}.");
        }
        else if (cfg.WrongCardReturnToHand)
        {
            QuizState.ReturnToHand = true;
        }
    }

    /// <summary>退还本次 SpendResources 已扣的能量和星费。</summary>
    private static void RefundCardCost(CardModel card, ResourceInfo resources)
    {
        try
        {
            var pcs = card.Owner?.PlayerCombatState;
            if (pcs is null) return;
            if (resources.EnergySpent > 0) pcs.GainEnergy(resources.EnergySpent);
            if (resources.StarsSpent > 0) pcs.GainStars(resources.StarsSpent);
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire] Refund failed: {ex.Message}");
        }
    }

    /// <summary>安全刷新免错券按钮 UI —— 防止 stale Godot 引用导致异常。</summary>
    internal static void SafeRefreshFreePassButton()
    {
        try
        {
            var btn = FreePassButton.Instance;
            if (btn is null || !Godot.GodotObject.IsInstanceValid(btn)) return;
            btn.Refresh();
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire] FreePassButton.Refresh failed (stale ref?): {ex.Message}");
            FreePassButton.ClearInstance();
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
        // 入口诊断：让我们一眼看到 TryManualPlay 是否被触发，以及被各种条件 short-circuit
        var cardName = __instance.GetType().Name;
        var isMp = GameBridge.IsMultiplayer();
        Log.Info($"[VocabSpire][MP] TryManualPlay Prefix ENTER: card={cardName} isMp={isMp} " +
                 $"Bypass={QuizState.Bypass} QuizActive={QuizState.QuizActive} " +
                 $"CombatInProgress={CombatManager.Instance.IsInProgress} " +
                 $"HasBank={VocabManager.Instance.HasActiveBank} " +
                 $"FreePassArmed={BattleStateTracker.Instance.FreePassArmed} " +
                 $"Enabled={VocabConfig.Instance.Enabled}");

        if (!VocabConfig.Instance.Enabled) { Log.Info("[VocabSpire][MP] → SKIP: VocabConfig.Enabled=false"); return true; }
        if (!isMp) { Log.Info("[VocabSpire][MP] → SKIP: IsMultiplayer()=false (走 SinglePlayer 路径)"); return true; }
        if (QuizState.Bypass) { Log.Info("[VocabSpire][MP] → BYPASS consumed"); QuizState.Bypass = false; return true; }
        if (QuizState.QuizActive) { Log.Info("[VocabSpire][MP] → BLOCKED: QuizActive=true (其它 quiz 进行中)"); __result = false; return false; }
        if (!CombatManager.Instance.IsInProgress) { Log.Info("[VocabSpire][MP] → SKIP: combat not in progress"); return true; }
        if (!VocabManager.Instance.HasActiveBank) { Log.Info("[VocabSpire][MP] → SKIP: no active wordbank"); return true; }

        // 免错券（联机）：跳过题目直接正常打出（联机透明：其他端看到无标志位）
        if (BattleStateTracker.Instance.FreePassArmed)
        {
            BattleStateTracker.Instance.ConsumeArmedFreePass();
            SinglePlayerPatch.SafeRefreshFreePassButton();
            Log.Info("[VocabSpire][MP] → Free pass consumed — skipping quiz.");
            return true;
        }

        var quiz = QuizPanel.Instance;
        if (quiz is null) { Log.Warn("[VocabSpire][MP] → SKIP: QuizPanel.Instance is null"); return true; }

        var question = VocabManager.Instance.GenerateQuiz();
        if (question is null) { Log.Warn("[VocabSpire][MP] → SKIP: GenerateQuiz returned null"); return true; }

        Log.Info($"[VocabSpire][MP] → ✓ SHOWING quiz: word='{question.TargetWord.English}' mode={question.Mode}");
        __result = false;
        QuizState.QuizActive = true;

        quiz.ShowQuiz(question, correct =>
        {
            try
            {
                Log.Info($"[VocabSpire][MP] quiz callback: correct={correct}");
                QuizState.QuizActive = false;
                SinglePlayerPatch.ApplyAnswerEffects(__instance, question, correct);
                QuizState.Bypass = true;
                __instance.TryManualPlay(target);
            }
            catch (System.Exception ex)
            {
                Log.Error($"[VocabSpire][MP] answer callback failed: {ex}");
                QuizState.QuizActive = false;
                QuizState.ResetCardLevel();
                QuizState.Bypass = false;
            }
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

        var rCount = System.Math.Min(QuizState.PendingRewards.Count, 15);
        writer.WriteUInt((uint)rCount, 4);
        for (var i = 0; i < rCount; i++)
        {
            var (kind, amount) = QuizState.PendingRewards[i];
            writer.WriteUInt(kind, 4);
            writer.WriteInt(amount, 16);
        }

        // v2.5+ 新增：惩罚列表（旧版 mod 无此字段，跨版本联机协议会读偏 → 强制版本匹配）
        var pCount = System.Math.Min(QuizState.PendingPunishments.Count, 15);
        writer.WriteUInt((uint)pCount, 4);
        for (var i = 0; i < pCount; i++)
        {
            var (kind, amount) = QuizState.PendingPunishments[i];
            writer.WriteUInt(kind, 4);
            writer.WriteInt(amount, 16);
        }

        // 诊断
        var rewardsDesc = rCount == 0 ? "none"
            : string.Join(",", QuizState.PendingRewards.Take(rCount).Select(r => $"{(RewardType)r.Kind}x{r.Amount}"));
        var punishDesc = pCount == 0 ? "none"
            : string.Join(",", QuizState.PendingPunishments.Take(pCount).Select(p => $"{(RewardType)p.Kind}x{p.Amount}"));
        Log.Info($"[VocabSpire][Net SEND] skip={QuizState.SkipEffect} nocost={QuizState.NoCost} " +
                 $"returnhand={QuizState.ReturnToHand} rewards=[{rewardsDesc}] punishments=[{punishDesc}]");
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

            var rCount = (int)reader.ReadUInt(4);
            for (var i = 0; i < rCount; i++)
            {
                var kind = (byte)reader.ReadUInt(4);
                var amount = (int)reader.ReadInt(16);
                QuizState.PendingRewards.Add((kind, amount));
            }

            // v2.5+ 惩罚列表
            var pCount = (int)reader.ReadUInt(4);
            for (var i = 0; i < pCount; i++)
            {
                var kind = (byte)reader.ReadUInt(4);
                var amount = (int)reader.ReadInt(16);
                QuizState.PendingPunishments.Add((kind, amount));
            }
            // PendingRewardTarget 由 OnPlay Postfix 从 __instance.Owner 推断

            var rewardsDesc = rCount == 0 ? "none"
                : string.Join(",", QuizState.PendingRewards.Take(rCount).Select(r => $"{(RewardType)r.Kind}x{r.Amount}"));
            var punishDesc = pCount == 0 ? "none"
                : string.Join(",", QuizState.PendingPunishments.Take(pCount).Select(p => $"{(RewardType)p.Kind}x{p.Amount}"));
            Log.Info($"[VocabSpire][Net RECV] skip={QuizState.SkipEffect} nocost={QuizState.NoCost} " +
                     $"returnhand={QuizState.ReturnToHand} rewards=[{rewardsDesc}] punishments=[{punishDesc}]");
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
/// 拦截"决定打完后归到哪个牌堆"的方法 —— 答错回手时强制返回 Hand。
/// 兼容 release (GetResultPileType) 和 beta v0.105+ (GetResultPileTypeForCardPlay)。
/// </summary>
[HarmonyPatch]
public static class GetResultPileTypePatch
{
    /// <summary>release 旧名 + beta 新名都试一遍。</summary>
    private static readonly string[] CandidateMethodNames =
    {
        "GetResultPileTypeForCardPlay", // beta v0.105.1+
        "GetResultPileType"             // release / 旧版
    };

    static IEnumerable<MethodBase> TargetMethods()
    {
        var baseType = typeof(CardModel);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic
                  | BindingFlags.Public | BindingFlags.DeclaredOnly;
        var count = 0;

        foreach (var type in baseType.Assembly.GetTypes())
        {
            if (!baseType.IsAssignableFrom(type)) continue;
            foreach (var name in CandidateMethodNames)
            {
                var method = type.GetMethod(name, flags, null, System.Type.EmptyTypes, null);
                if (method is null) continue;
                count++;
                yield return method;
                break; // 同一类型同时存在新旧名时只 patch 一个
            }
        }

        Log.Info($"[VocabSpire] Patched {count} GetResultPileType(ForCardPlay) methods.");
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

    /// <summary>Postfix 用于在 OnPlay 完成（或被跳过）后触发批量奖励 / 惩罚。
    /// 通过 Harmony 按参数名注入 PlayerChoiceContext —— 它是 OnPlay 的第 1 个参数，
    /// 双端确定性的真 choice context，传给后续 reward / draw / power apply / discard 使用。
    /// __1 = OnPlay 第 2 个参数 CardPlay（按位置注入，避免各子类参数名不一致）。</summary>
    public static void Postfix(object __instance, ref Task __result, PlayerChoiceContext choiceContext, CardPlay __1)
    {
        // 重放守卫：OnPlayWrapper 内部用 for 循环重复调 OnPlay（playCount = ReplayCount + 1，
        // 例如华彩 SwordSagePower 给主权之刃加重放）。若在第一次 OnPlay 后就复位 SkipEffect，
        // 第 2 次起的重放会因标志被清而正常生效 —— 答错却跳不掉效果。
        // 因此：中间的重放直接 return，保持 SkipEffect / NoCost / ReturnToHand 不变；
        // 只在最后一次重放（IsLastInSeries）才复位标志并结算一次奖惩。
        if (__1 is { IsLastInSeries: false }) return;

        var hasReward = QuizState.PendingRewards.Count > 0;
        var hasPunishment = QuizState.PendingPunishments.Count > 0;
        if (!hasReward && !hasPunishment)
        {
            QuizState.SkipEffect = false;
            QuizState.NoCost = false;
            QuizState.ReturnToHand = false;
            return;
        }

        // 取出快照（共享 List 下一次打牌前可能被重置）
        var rewards = new List<(RewardType, int)>(QuizState.PendingRewards.Count);
        foreach (var (k, a) in QuizState.PendingRewards) rewards.Add(((RewardType)k, a));

        var punishments = new List<(RewardType, int)>(QuizState.PendingPunishments.Count);
        foreach (var (k, a) in QuizState.PendingPunishments) punishments.Add(((RewardType)k, a));

        var target = QuizState.PendingRewardTarget
            ?? (__instance is CardModel cm ? cm.Owner : null);

        QuizState.SkipEffect = false;
        QuizState.NoCost = false;
        QuizState.ReturnToHand = false;
        QuizState.PendingRewards.Clear();
        QuizState.PendingPunishments.Clear();
        QuizState.PendingRewardTarget = null;

        var original = __result;
        __result = ChainEffects(original, target, rewards, punishments, choiceContext);
    }

    private static async Task ChainEffects(Task original, Player? target,
        List<(RewardType Kind, int Amount)> rewards,
        List<(RewardType Kind, int Amount)> punishments,
        PlayerChoiceContext choiceContext)
    {
        try { await original; } catch { }
        if (target is null) return;
        if (rewards.Count > 0)
            await RewardService.ApplyAllAsync(target, rewards, choiceContext);
        if (punishments.Count > 0)
            await PunishmentService.ApplyAllAsync(target, punishments, choiceContext);
    }
}
