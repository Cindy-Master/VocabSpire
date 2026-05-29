using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace VocabSpire.Services;

/// <summary>
/// 惩罚应用 —— 跟 RewardService 对称，但效果反向：
/// Hp 直接掉血 / Energy 扣费 / Gold 扣金 / Block 扣格挡 / Power 应用负值 / Draw 转为随机弃手牌。
/// 所有调用使用 OnPlay 真实的 PlayerChoiceContext（不再用 ThrowingPlayerChoiceContext）。
/// 联机时双端共同执行同一份 punishments 列表（通过 NetPlayCardAction 序列化），
/// 确定性 RNG 保证随机弃牌双端选到同一张。
/// </summary>
public static class PunishmentService
{
    /// <summary>批量应用惩罚（按顺序 await）。</summary>
    public static async Task ApplyAllAsync(
        Player owner,
        System.Collections.Generic.IReadOnlyList<(RewardType kind, int amount)> punishments,
        PlayerChoiceContext choiceContext)
    {
        if (owner is null || punishments.Count == 0) return;
        if (!CombatManager.Instance.IsInProgress) return;

        Log.Info($"[VocabSpire][Punish] ApplyAllAsync start: count={punishments.Count} list=[{string.Join(",", punishments.Select(p => $"{p.kind}x{p.amount}"))}]");
        for (var i = 0; i < punishments.Count; i++)
        {
            var (kind, amount) = punishments[i];
            Log.Info($"[VocabSpire][Punish] [{i + 1}/{punishments.Count}] -> ApplyOneAsync(kind={kind} amount={amount})");
            try
            {
                await ApplyOneAsync(owner, kind, amount, choiceContext);
                Log.Info($"[VocabSpire][Punish] [{i + 1}/{punishments.Count}] <- ApplyOneAsync returned (kind={kind})");
            }
            catch (Exception ex)
            {
                Log.Error($"[VocabSpire][Punish] [{i + 1}/{punishments.Count}] EXCEPTION at ApplyOneAsync(kind={kind} amount={amount}): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
        Log.Info($"[VocabSpire][Punish] ApplyAllAsync end (all {punishments.Count} processed)");
    }

    /// <summary>单条惩罚（kind 复用 RewardType 枚举，效果反向）。</summary>
    public static async Task ApplyOneAsync(Player owner, RewardType kind, int amount, PlayerChoiceContext choiceContext)
    {
        if (owner is null || amount <= 0 || kind == RewardType.None)
        {
            Log.Info($"[VocabSpire][Punish] skip {kind} x{amount} (invalid)");
            return;
        }
        if (!CombatManager.Instance.IsInProgress && kind != RewardType.Hp)
        {
            Log.Info($"[VocabSpire][Punish] skip {kind} x{amount} (combat not in progress)");
            return;
        }

        try
        {
            Log.Info($"[VocabSpire][Punish] enter switch: kind={kind} amount={amount}");
            switch (kind)
            {
                case RewardType.Hp:
                    // 直接掉血，不受格挡保护（语义=惩罚而非攻击）
                    if (owner.Creature is { } c) c.LoseHpInternal(amount, default);
                    break;

                case RewardType.Energy:
                    if (owner.PlayerCombatState is { } pcs) pcs.LoseEnergy(amount);
                    break;

                case RewardType.Gold:
                    await PlayerCmd.LoseGold(amount, owner);
                    break;

                case RewardType.Strength:
                    await RewardService.ApplyPowerAsync<StrengthPower>(
                        choiceContext, owner.Creature, -amount, owner.Creature, null);
                    break;

                case RewardType.Dexterity:
                    await RewardService.ApplyPowerAsync<DexterityPower>(
                        choiceContext, owner.Creature, -amount, owner.Creature, null);
                    break;

                case RewardType.Block:
                    // 直接扣格挡（LoseBlockInternal 内部 clamp 到 >= 0）
                    owner.Creature.LoseBlockInternal(amount);
                    break;

                case RewardType.Draw:
                    // Draw 反向 = 随机弃手中 N 张。双端用确定性 RNG (CombatCardSelection) 选同一批牌。
                    await RandomDiscardAsync(owner, amount, choiceContext);
                    break;

                case RewardType.Thorns:
                    await RewardService.ApplyPowerAsync<ThornsPower>(
                        choiceContext, owner.Creature, -amount, owner.Creature, null);
                    break;

                case RewardType.Focus:
                    await RewardService.ApplyPowerAsync<FocusPower>(
                        choiceContext, owner.Creature, -amount, owner.Creature, null);
                    break;

                case RewardType.Artifact:
                    await RewardService.ApplyPowerAsync<ArtifactPower>(
                        choiceContext, owner.Creature, -amount, owner.Creature, null);
                    break;
            }
            Log.Info($"[VocabSpire] Punishment applied: {kind} x{amount} to {owner.NetId}");
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire] Punishment {kind} x{amount} failed: {ex.Message}");
        }
    }

    /// <summary>随机弃手牌 N 张。RNG 用 owner.RunState.Rng.CombatCardSelection（双端确定性同步）。</summary>
    private static async Task RandomDiscardAsync(Player owner, int n, PlayerChoiceContext choiceContext)
    {
        var hand = PileType.Hand.GetPile(owner);
        if (hand.Cards.Count == 0)
        {
            Log.Info("[VocabSpire][Punish] RandomDiscard: hand empty, nothing to discard");
            return;
        }

        var toDiscardCount = System.Math.Min(n, hand.Cards.Count);
        var rng = owner.RunState.Rng.CombatCardSelection;
        var toDiscard = hand.Cards.TakeRandom(toDiscardCount, rng).ToList();

        Log.Info($"[VocabSpire][Punish] RandomDiscard: discarding {toDiscard.Count}/{hand.Cards.Count} cards: " +
                 string.Join(",", toDiscard.Select(c => c.GetType().Name)));

        await CardCmd.Discard(choiceContext, toDiscard);
    }
}
