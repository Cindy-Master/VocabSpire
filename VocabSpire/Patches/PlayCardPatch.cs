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
    /// <summary>答错跳过卡牌效果时，附魔(Enchantment)/词缀(Affliction)的独立 OnPlay 也要跳过。
    /// 与 SkipEffect 同设，但生命周期覆盖整个 OnPlayWrapper —— 不被 OnPlaySkipPatch.Postfix
    /// 提前复位，因为附魔/词缀的 OnPlay 在 CardModel.OnPlay（及其 Postfix 复位）之后才执行。</summary>
    internal static bool SkipCardExtras;
    internal static bool NoCost;
    internal static bool ReturnToHand;
    internal static bool QuizActive;

    /// <summary>本次打牌完成后需要触发的奖励列表（联机通过 NetPlayCardAction 同步）。</summary>
    internal static readonly List<(byte Kind, int Amount)> PendingRewards = new();

    /// <summary>本次打牌完成后需要触发的惩罚列表（联机通过 NetPlayCardAction 同步）。</summary>
    internal static readonly List<(byte Kind, int Amount)> PendingPunishments = new();

    /// <summary>本次打牌的卡主（用于 OnPlay 完成后施加奖励 / 惩罚）。</summary>
    internal static Player? PendingRewardTarget;

    /// <summary>本次答对要给这张牌额外重放的次数（重放奖励）。由 ReplayCountPatch 在算 playCount 时 +N，
    /// 两端一致（联机经 NetPlayCardAction 同步）；series 末清零。</summary>
    internal static int PendingReplay;

    public static void ResetCardLevel()
    {
        SkipEffect = false;
        SkipCardExtras = false;
        NoCost = false;
        ReturnToHand = false;
        PendingReplay = 0;
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

        // 自动打出的牌（回合开始遗物/能力自动打出等）：开关关闭时不做题，直接正常打出。
        if (isAutoPlay && !VocabConfig.Instance.QuizOnAutoPlay)
        {
            Log.Info($"[VocabSpire][SP] Auto-played card '{cardName}' — quiz disabled by config, normal play.");
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
                ApplyAnswerEffects(card, question, correct, resources, isAutoPlay);
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
    internal static void ApplyAnswerEffects(CardModel card, Models.QuizQuestion question, bool correct, ResourceInfo resources = default, bool isAutoPlay = false)
    {
        var cfg = VocabConfig.Instance;
        QuizState.ResetCardLevel();

        // 计算奖励 + 更新 streak（仅卡主端，结果通过 NetPlayCardAction 同步）
        var outcome = BattleStateTracker.Instance.RecordAnswer(question, correct);

        if (correct)
        {
            foreach (var r in outcome.Rewards)
            {
                // 重放奖励特殊处理：不入 PendingRewards（RewardService 施加不了），累加到 PendingReplay，
                // 由 ReplayCountPatch 在算 playCount 时 +N，让这张牌用游戏原生循环多打 N 次。
                if (r.Kind == RewardType.Replay) { QuizState.PendingReplay += r.Amount; continue; }
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
        QuizState.SkipCardExtras = true; // 附魔/词缀效果随卡牌效果一并跳过（"伶俐"附魔的起防即在此触发）

        // 归堆补丁没挂上（游戏版本改了 API）→ 回手功能整体禁用：设了 ReturnToHand 却改不了归堆，
        // 只会造成「VFX 被跳过节点卡打出位 + 能力牌 None 被移出战斗」的半残状态（v0.109 实锤）。
        var canReturnToHand = ReturnPileState.PatchActive;
        if (!canReturnToHand && (cfg.WrongCardReturnToHand || BattleStateTracker.Instance.CanUseTolerance()))
        {
            Log.Warn("[VocabSpire] 归堆补丁未挂载（游戏版本不兼容），答错回手已临时禁用，牌走正常结果堆。");
        }

        if (BattleStateTracker.Instance.CanUseTolerance())
        {
            QuizState.NoCost = true;
            QuizState.ReturnToHand = canReturnToHand;
            BattleStateTracker.Instance.ConsumeTolerance();
            // 关键：能量已在 PlayCardAction.ExecuteAction 中扣过（SpendResources 在 OnPlayWrapper 之前），
            // 这里要把实际花费的能量+星费补回。
            RefundCardCost(card, resources);
            Log.Info($"[VocabSpire] Tolerance used ({BattleStateTracker.Instance.ToleranceUsedThisTurn}/{cfg.ToleranceCount}); refunded energy={resources.EnergySpent} stars={resources.StarsSpent}.");
        }
        else if (cfg.WrongCardReturnToHand)
        {
            QuizState.ReturnToHand = canReturnToHand;
        }

        // Sly(奇巧「被丢弃时自动打出」)等自动打出的牌本就不在手牌里 —— 回手会把它塞进手牌 = 凭空多一张。
        // 自动打出的牌答错不回手，让它走正常结果堆（如弃牌堆），效果照常跳过。
        if (isAutoPlay) QuizState.ReturnToHand = false;
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
/// <summary>
/// 联机自定义数据的协议头。
///
/// VocabSpire 把答题结果（跳过效果/免费/回手/奖惩/重放）作为 Postfix 追加在游戏原生
/// NetPlayCardAction 数据包的尾部，靠「写入顺序 = 读取顺序」这个隐式约定对齐位置。
/// 该约定有个前提：本 mod 的 Postfix 在收发两端的执行次序必须一致。
///
/// Harmony 对同一方法的多个 Postfix 按 priority 排序，**只有 priority 相同时才退化成按
/// mod 加载顺序**。此前本 mod 全部用默认 Normal，于是「谁先加载谁先写」——一旦和别的
/// mod（如把自己排到很前的 RitsuLib）同场，两端位置就可能对不上，读出的比特是别人的数据：
/// skip/returnhand 被误置、奖励数量读成离谱值，进而整包读爆。玩家实测「VocabSpire 排在
/// RitsuLib 前面才正常、排后面就暴毙」正是这个现象。
///
/// 修法两层：
///   ① Serialize / Deserialize 两侧的 Postfix 都声明 Priority.Last —— 无论加载顺序如何，
///      本 mod 永远最后写、最后读，位置在两端必然一致（这一层保证功能正常，不是降级）。
///   ② 数据前置魔数 —— 万一还有 mod 也占了 Last 导致位置仍不对，读端能立刻发现并
///      整段放弃，而不是把垃圾值当成答题结果应用到战斗里。
/// </summary>
internal static class NetProtocol
{
    /// <summary>自定义段起始标识：'V''S' + 协议版本。32 位是为了把「在别人的数据里被误匹配」
    /// 的概率压到 1/2^32（16 位在几百比特的包里并非不可能撞上）。改协议时递增末字节。</summary>
    internal const uint Magic = 0x56530001;
    internal const int MagicBits = 32;

    /// <summary>扫描上限。包里没有本 mod 数据时（对端没装 / 旧版），扫到上限即放弃，
    /// 不至于一路读到越界。正常情况下 0 bit 就命中，根本走不到这里。</summary>
    internal const int MaxScanBits = 4096;

    /// <summary>段标识位宽 —— 写入 NetCombatCard.CombatCardIndex（卡牌实例唯一 id，两端一致）。
    ///
    /// 为什么必须有：一个数据包里可能连续装多条 PlayCardAction（游戏用 ReadList 之类连续读）。
    /// 若某条动作的包里没有本 mod 的段（对端是旧版、或那张牌没触发答题），扫描就会一路扫过去
    /// **抓到下一条动作的段**，读走属于别人的答题结果、还把读取位置推到错误的地方。
    /// 加上这个标识后，每段都能自证「我属于哪条动作」：对不上就说明扫过界了，立即还原并跳过。</summary>
    internal const int TagBits = 32;
}

[HarmonyPatch(typeof(NetPlayCardAction), nameof(NetPlayCardAction.Serialize))]
public static class NetPlayCardSerializePatch
{
    // Priority.Last：本 Postfix 永远最后执行 → 自定义段总在包尾，且与读端次序一致。
    // 不声明的话就是默认 Normal，多 mod 同场时次序由加载顺序决定（见 NetProtocol 注释）。
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(ref NetPlayCardAction __instance, PacketWriter writer)
    {
        var startBit = writer.BitPosition;
        writer.WriteUInt(NetProtocol.Magic, NetProtocol.MagicBits);
        writer.WriteUInt(__instance.card.CombatCardIndex, NetProtocol.TagBits);   // 段归属标识
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

        // v2.7.14+ 新增：重放次数（重放奖励，联机同步两端 playCount；新字段 → 双端须同版本）
        writer.WriteUInt((uint)System.Math.Clamp(QuizState.PendingReplay, 0, 15), 4);

        // 诊断
        var rewardsDesc = rCount == 0 ? "none"
            : string.Join(",", QuizState.PendingRewards.Take(rCount).Select(r => $"{(RewardType)r.Kind}x{r.Amount}"));
        var punishDesc = pCount == 0 ? "none"
            : string.Join(",", QuizState.PendingPunishments.Take(pCount).Select(p => $"{(RewardType)p.Kind}x{p.Amount}"));
        Log.Info($"[VocabSpire][Net SEND] skip={QuizState.SkipEffect} nocost={QuizState.NoCost} " +
                 $"returnhand={QuizState.ReturnToHand} rewards=[{rewardsDesc}] punishments=[{punishDesc}] " +
                 $"(自定义段 bit {startBit}..{writer.BitPosition})");
    }
}

[HarmonyPatch(typeof(NetPlayCardAction), nameof(NetPlayCardAction.Deserialize))]
public static class NetPlayCardDeserializePatch
{
    /// <summary>
    /// 在数据包剩余部分里定位本 mod 的自定义段：用 32 位滑动窗口逐 bit 找魔数。
    /// 找到时 reader 恰好停在魔数之后（即 payload 起点），返回 true。
    ///
    /// 为什么要扫而不是直接读：本 mod 的数据是追加在包尾的，位置取决于自己的 Postfix
    /// 排在第几个执行。声明 Priority.Last 已能让两端次序一致，但万一还有别的 mod 也占了
    /// Last、两端加载顺序又不同，位置就会漂移 —— 扫描让读端无论对方插在前面还是后面
    /// 都能对齐，真正做到不依赖执行次序。
    ///
    /// 扫描会推进 reader 的共享位置，但读完后 Postfix 会把位置还原（见 RestoreBitPosition），
    /// 所以不会吃掉排在后面的 mod 尚未读取的数据 —— 本 mod 读自己的段，读完原样奉还。
    /// </summary>
    /// <summary>
    /// PacketReader.BitPosition 的写入器（该属性是 public int { get; private set; }，
    /// setter 方法存在、只是不可访问）。拿不到时为 null —— 那种情况下本 mod 就只能
    /// 依赖「自己是最后一个读的」，见 Postfix 里的降级处理。
    /// </summary>
    private static readonly Action<PacketReader, int>? SetBitPosition = CreateBitPositionSetter();

    private static Action<PacketReader, int>? CreateBitPositionSetter()
    {
        try
        {
            var setter = AccessTools.PropertySetter(typeof(PacketReader), nameof(PacketReader.BitPosition));
            if (setter is null)
            {
                Log.Warn("[VocabSpire][Net] 未找到 PacketReader.BitPosition 的 setter，" +
                         "读取位置将无法还原（本 mod 仍会最后读，不影响自身功能）。");
                return null;
            }
            return (Action<PacketReader, int>)setter.CreateDelegate(typeof(Action<PacketReader, int>));
        }
        catch (System.Exception ex)
        {
            Log.Warn($"[VocabSpire][Net] 创建 BitPosition 写入器失败（{ex.GetType().Name}: {ex.Message}），" +
                     "读取位置将无法还原。");
            return null;
        }
    }

    /// <summary>
    /// 把 reader 的读取位置还原到我们介入之前。
    ///
    /// ⚠ 只在「没找到自己的数据段」或「读取过程抛异常」时才可以调用。
    /// 成功读完自己的段时**绝不能**还原 —— PacketReader 是流式共享的，游戏用
    /// ReadList&lt;T&gt; 之类连续读多项，每一项都靠位置自然前进来定位下一项。
    /// 我们写进包里的自定义段必须被消费掉，位置停在段尾，游戏后续读取才正确；
    /// 一旦还原，游戏会把本 mod 的自定义段当成下一项数据重新解析，直接导致联机数据不同步。
    /// （v2.7.31 曾在 finally 里无条件还原，正是此因造成开启奖励后必然 desync。）
    /// </summary>
    private static void RestoreBitPosition(PacketReader reader, int bit)
    {
        try { SetBitPosition?.Invoke(reader, bit); }
        catch (System.Exception ex)
        {
            Log.Warn($"[VocabSpire][Net] 还原读取位置失败：{ex.Message}");
        }
    }

    private static bool TryLocateSegment(PacketReader reader, out int scannedBits)
    {
        scannedBits = 0;
        uint window = 0;

        for (var i = 0; i < NetProtocol.MagicBits; i++)
            window = (window << 1) | (reader.ReadBool() ? 1u : 0u);
        if (window == NetProtocol.Magic) return true;

        while (scannedBits < NetProtocol.MaxScanBits)
        {
            window = (window << 1) | (reader.ReadBool() ? 1u : 0u);
            scannedBits++;
            if (window == NetProtocol.Magic) return true;
        }
        return false;
    }

    [HarmonyPriority(Priority.Last)]
    public static void Postfix(ref NetPlayCardAction __instance, PacketReader reader)
    {
        // 进入时的位置：读完后要原样还原回去
        var entryBit = reader.BitPosition;
        try
        {
            QuizState.ResetCardLevel();

            var startBit = reader.BitPosition;
            if (!TryLocateSegment(reader, out var scanned))
            {
                // 没找到本 mod 的标识 —— 对端没装本 mod、或版本不一致。
                // 此时必须把位置还原：扫描已经推进了 reader，而这个包里根本没有我们的数据，
                // 一个 bit 都不该被我们消费掉，否则游戏读后续内容时会错位。
                RestoreBitPosition(reader, entryBit);
                Log.Warn($"[VocabSpire][Net RECV] 从 bit {startBit} 起扫描 {scanned} bit 未找到本 mod 标识" +
                         "：对端可能未装本 mod 或版本不一致。本次答题同步已跳过，读取位置已还原。");
                return;
            }
            // 段标识校验：确认扫到的这一段确实属于当前这条动作。
            // 一包多动作时，若本动作没有我们的段，扫描会撞上下一条动作的段 —— 靠这里挡住。
            var tag = reader.ReadUInt(NetProtocol.TagBits);
            var expected = __instance.card.CombatCardIndex;
            if (tag != expected)
            {
                RestoreBitPosition(reader, entryBit);
                Log.Warn($"[VocabSpire][Net RECV] 扫到的数据段不属于本动作（段标识 {tag}，本动作卡牌 {expected}）" +
                         "：本条动作没有携带答题数据（对端可能是旧版本），已跳过并还原读取位置。");
                return;
            }

            if (scanned > 0)
            {
                // 位置不在预期处但扫到了 —— 说明有别的 mod 也在这个包里写了数据、且它排在我们前面。
                // 靠扫描已经正确对齐，功能不受影响；记一笔便于日后排查。
                Log.Info($"[VocabSpire][Net RECV] 本 mod 数据段不在预期位置，向后扫描 {scanned} bit 后对齐" +
                         "（有其他 mod 也在此包内追加数据，属正常情况）。");
            }

            if (reader.ReadBool()) { QuizState.SkipEffect = true; QuizState.SkipCardExtras = true; }
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

            // v2.7.14+ 重放次数（重放奖励，联机同步）
            QuizState.PendingReplay = (int)reader.ReadUInt(4);
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
            // 读到一半出错：位置停在不确定的地方，还原回去交给游戏，别让它接着读错位的数据
            RestoreBitPosition(reader, entryBit);
            Log.Error($"[VocabSpire] Deserialize failed: {ex.Message}（读取位置已还原）");
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
/// 重放奖励 —— 答对且配了「重放本牌」奖励时，给该牌 GetEnchantedReplayCount 的结果 +N。
/// OnPlayWrapper 的 playCount = GetEnchantedReplayCount()+1，于是这张牌用游戏原生循环多打 N 次。
/// 该方法在单机/联机两端打牌时都会被 GeneratePlayCount 调一次算 playCount，两端加同样的 N（联机经
/// NetPlayCardAction 同步 PendingReplay），结果一致、不 desync。PendingReplay 在 series 末清零。含能力牌。
/// 用 TargetMethods 反射查方法：**若游戏版本没有该方法则不注册补丁、不崩**（防御式）。
/// </summary>
[HarmonyPatch]
public static class ReplayCountPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var m = typeof(CardModel).GetMethod("GetEnchantedReplayCount",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, System.Type.EmptyTypes, null);
        if (m is not null) { yield return m; }
        else Log.Warn("[VocabSpire] GetEnchantedReplayCount 未找到 —— 重放奖励禁用（不注册补丁、不崩）。");
    }

    public static void Postfix(ref int __result)
    {
        if (QuizState.PendingReplay > 0) __result += QuizState.PendingReplay;
    }
}

/// <summary>
/// 拦截"决定打完后归到哪个牌堆"的方法 —— 答错回手时强制返回 Hand。
/// 游戏逐版本改名（GetResultPileType → …ForCardPlay → …AndPositionForCardPlay，v0.109 又改），
/// 不再绑死方法名：模糊匹配「名字含 ResultPileType + 无参」，按返回类型分派给两个补丁类。
/// ReturnToHandPatchActive：任一归堆补丁成功挂上才为 true —— 未挂上时回手功能整体禁用
/// （ApplyAnswerEffects 不设 ReturnToHand），避免「跳过VFX节点卡打出位 + 牌 None 移出战斗」的半残状态。
/// </summary>
public static class ReturnPileState
{
    internal static bool PatchActive;
}

/// <summary>
/// 「这张牌打完去哪个牌堆」的游戏 API —— 游戏已经改过两次名字和签名：
///   ≤0.107   GetResultPileTypeForCardPlay()            → PileType
///    0.108   GetResultPileTypeAndPositionForCardPlay() → (PileType, CardPilePosition)
///   0.109+   GetResultLocationForCardPlay()            → CardLocation（含 pileType 字段）
///
/// 原本用「方法名含 ResultPileType」的模糊匹配来抗改名，实测抗不住：0.109 把名字里的
/// ResultPileType 整段换成了 ResultLocation，匹配数当场归零、答错回手静默失效。
/// 现改为逐代「精确方法名 + 精确返回类型」匹配 —— 命中哪代挂哪个补丁；三代全落空说明
/// 游戏又改了 API，由挂载审计明确报出来，而不是继续静默降级。
/// </summary>
internal static class ResultPileApi
{
    internal const string V0109 = "GetResultLocationForCardPlay";
    internal const string V0108 = "GetResultPileTypeAndPositionForCardPlay";
    internal const string V0107 = "GetResultPileTypeForCardPlay";

    /// <summary>在 CardModel 及其所有子类里找「该精确名字 + 无参 + 该返回类型」的方法（含子类 override）。</summary>
    internal static IEnumerable<MethodBase> Find(string exactName, System.Type returnType)
    {
        var baseType = typeof(CardModel);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic
                  | BindingFlags.Public | BindingFlags.DeclaredOnly;
        foreach (var type in baseType.Assembly.GetTypes())
        {
            if (!baseType.IsAssignableFrom(type)) continue;
            var m = type.GetMethod(exactName, flags, null, System.Type.EmptyTypes, null);
            if (m is null || m.ReturnType != returnType) continue;
            yield return m;
        }
    }
}

/// <summary>
/// 答错回手（v0.109+ 形态）—— 归堆方法改名为 GetResultLocationForCardPlay，
/// 返回 CardLocation（结构体，pileType 字段即牌堆）。与 0.108 / ≤0.107 两个补丁类共存，
/// 各自 TargetMethods 在别代游戏上为空 → Harmony 抛异常 → 由 Plugin 逐类隔离兜住。
/// </summary>
[HarmonyPatch]
public static class GetResultLocationPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var count = 0;
        foreach (var m in ResultPileApi.Find(ResultPileApi.V0109, typeof(CardLocation)))
        {
            count++;
            yield return m;
        }
        if (count > 0) ReturnPileState.PatchActive = true;
        PatchAudit.Record("答错回手", $"{ResultPileApi.V0109} (v0.109+ 形态)", count);
    }

    // Priority.Last：答错回手是玩家答错的强制结果，必须压过其他 mod 对归堆的修改
    // （如 RitsuLib 的 model capability 也 Postfix 改同一个 __result）。不声明的话
    // 谁生效取决于 mod 加载顺序。仅在 ReturnToHand=true 时改动，平时不干预任何人。
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(ref CardLocation __result)
    {
        if (QuizState.ReturnToHand)
        {
            // 同下：能力牌/复制牌原结果是 None，不改成 Hand 会被移出战斗（凭空消失）
            __result.pileType = PileType.Hand;
        }
    }
}

[HarmonyPatch]
public static class GetResultPileTypePatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var count = 0;
        foreach (var m in ResultPileApi.Find(ResultPileApi.V0107, typeof(PileType)))
        {
            count++;
            yield return m;
        }
        if (count > 0) ReturnPileState.PatchActive = true;
        PatchAudit.Record("答错回手", $"{ResultPileApi.V0107} (≤v0.107 形态)", count);
    }

    // Priority.Last：答错回手是玩家答错的强制结果，必须压过其他 mod 对归堆的修改
    // （如 RitsuLib 的 model capability 也 Postfix 改同一个 __result）。不声明的话
    // 谁生效取决于 mod 加载顺序。仅在 ReturnToHand=true 时改动，平时不干预任何人。
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(ref PileType __result)
    {
        if (QuizState.ReturnToHand)
        {
            // 答错回手：能力牌/复制牌(IsDupe)的原结果是 PileType.None —— OnPlayWrapper 会对 None
            // 调 RemoveFromCombat 把它移出战斗（=凭空消失）。开了回手就必须把 None 也改成 Hand，
            // 否则能力牌答错后既没上 buff 也没回手，直接消失。
            __result = PileType.Hand;
        }
    }
}

/// <summary>
/// 答错回手（v0.108+ 版本）—— 0.108 起 GetResultPileType(ForCardPlay) 改名为
/// GetResultPileTypeAndPositionForCardPlay 且返回 (PileType, CardPilePosition) 元组
/// （0.108 CardModel.cs:2077；子类 ParticleWall/ShiningStrike/TheBall 有 override）。
/// 与上面的 GetResultPileTypePatch(≤0.107) 共存：两类各自的 TargetMethods 在对方版本上为空
/// → Harmony 抛异常 → 由 Plugin 的逐类隔离兜住（仅日志），当前版本对应的那个类正常生效。
/// </summary>
[HarmonyPatch]
public static class GetResultPileTypeAndPositionPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var count = 0;
        foreach (var m in ResultPileApi.Find(ResultPileApi.V0108, typeof((PileType, CardPilePosition))))
        {
            count++;
            yield return m;
        }
        if (count > 0) ReturnPileState.PatchActive = true;
        PatchAudit.Record("答错回手", $"{ResultPileApi.V0108} (v0.108 形态)", count);
    }

    // Priority.Last：答错回手是玩家答错的强制结果，必须压过其他 mod 对归堆的修改
    // （如 RitsuLib 的 model capability 也 Postfix 改同一个 __result）。不声明的话
    // 谁生效取决于 mod 加载顺序。仅在 ReturnToHand=true 时改动，平时不干预任何人。
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(ref (PileType, CardPilePosition) __result)
    {
        if (QuizState.ReturnToHand)
        {
            __result.Item1 = PileType.Hand;   // 只改归堆，保留位置分量
        }
    }
}

/// <summary>
/// 能力牌答错回手的「空白牌」修复 —— 能力牌打出时 CardModel.PlayPowerCardFlyVfx() 会把卡牌视觉节点
/// 搬进 CombatVfxContainer 并交给「飞向能力区」动画消耗掉（CardModel.cs:1613）。若此时又强制把牌回手，
/// 牌 model 回到手牌但视觉节点已被消耗 → 手里是一张空白牌。
/// 答错回手(ReturnToHand)时跳过这段 VFX：节点留在桌上，随后正常移进手牌，牌面完整。
/// 答对(能力正常生效)时 ReturnToHand=false，VFX 照常播放，行为不变。
/// </summary>
[HarmonyPatch(typeof(CardModel), "PlayPowerCardFlyVfx")]
public static class PowerCardFlyVfxSkipPatch
{
    public static bool Prefix(ref Task __result)
    {
        // ReturnToHand 只会在归堆补丁真正挂上（ReturnPileState.PatchActive）时被设置，
        // 所以这里跳过 VFX 一定伴随成功回手，不会再出现「节点卡打出位 + 牌被移出战斗」的半残态。
        if (QuizState.ReturnToHand)
        {
            __result = Task.CompletedTask;
            return false; // 跳过原 VFX，保留卡牌节点（回手后牌面完整）
        }
        return true;
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

        PatchAudit.Record("答错跳过卡牌效果", "CardModel 子类 OnPlay", count, PatchImportance.Critical);
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

        // 重放奖励只用于本次 GeneratePlayCount（series 开始前已被消费），series 结束即清零，
        // 防止残留影响下一张牌的 playCount。
        QuizState.PendingReplay = 0;

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

/// <summary>
/// 答错跳过卡牌效果时，附魔(Enchantment)与词缀(Affliction)的 OnPlay 也要一并跳过。
/// 它们在 OnPlayWrapper 里独立于 CardModel.OnPlay 触发（"伶俐"附魔的起防即在此），
/// 只 patch CardModel.OnPlay 拦不住 —— 会出现"牌没打出却触发了附魔/词缀效果"。
/// 用 SkipCardExtras（而非 SkipEffect）判断：附魔/词缀 OnPlay 在 CardModel.OnPlay 之后执行，
/// 此时 SkipEffect 已被 OnPlaySkipPatch.Postfix 复位，SkipCardExtras 则覆盖整个 OnPlayWrapper。
/// </summary>
[HarmonyPatch]
public static class EnchantmentAfflictionSkipPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic
                  | BindingFlags.Public | BindingFlags.DeclaredOnly;
        var enchantBase = typeof(EnchantmentModel);
        var afflictBase = typeof(AfflictionModel);
        var enchantParams = new[] { typeof(PlayerChoiceContext), typeof(CardPlay) };
        var afflictParams = new[] { typeof(PlayerChoiceContext), typeof(Creature) };
        var count = 0;

        foreach (var type in enchantBase.Assembly.GetTypes())
        {
            MethodInfo? method = null;
            if (enchantBase.IsAssignableFrom(type))
                method = type.GetMethod("OnPlay", flags, null, enchantParams, null);
            else if (afflictBase.IsAssignableFrom(type))
                method = type.GetMethod("OnPlay", flags, null, afflictParams, null);
            if (method is null) continue;
            count++;
            yield return method;
        }

        PatchAudit.Record("答错跳过附魔/词缀效果", "Enchantment/Affliction OnPlay", count);
    }

    public static bool Prefix(object __instance, ref Task __result)
    {
        if (!QuizState.SkipCardExtras) return true;

        Log.Info($"[VocabSpire] Extra effect skipped: {__instance?.GetType().Name} (wrong answer).");
        __result = Task.CompletedTask;
        return false;
    }
}
