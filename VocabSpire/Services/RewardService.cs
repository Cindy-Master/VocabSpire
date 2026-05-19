using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace VocabSpire.Services;

/// <summary>
/// 奖励应用 —— 所有客户端都执行同样的 API 调用以保持联机同步。
/// 单条 = ApplyOneAsync；批量 = ApplyAllAsync(list)。
/// </summary>
public static class RewardService
{
    /// <summary>批量应用奖励列表（按顺序 await）。</summary>
    public static async Task ApplyAllAsync(Player owner, System.Collections.Generic.IReadOnlyList<(RewardType kind, int amount)> rewards)
    {
        if (owner is null || rewards.Count == 0) return;
        if (!CombatManager.Instance.IsInProgress) return;

        foreach (var (kind, amount) in rewards)
        {
            await ApplyOneAsync(owner, kind, amount);
        }
    }

    /// <summary>单条奖励。</summary>
    public static async Task ApplyOneAsync(Player owner, RewardType kind, int amount)
    {
        if (owner is null || amount <= 0 || kind == RewardType.None) return;
        if (!CombatManager.Instance.IsInProgress && kind != RewardType.Hp) return;

        try
        {
            switch (kind)
            {
                case RewardType.Hp:
                    await CreatureCmd.Heal(owner.Creature, amount);
                    break;
                case RewardType.Energy:
                    await PlayerCmd.GainEnergy(amount, owner);
                    break;
                case RewardType.Gold:
                    await PlayerCmd.GainGold(amount, owner);
                    break;
                case RewardType.Strength:
                    await PowerCmd.Apply<StrengthPower>(owner.Creature, amount, owner.Creature, null);
                    break;
                case RewardType.Dexterity:
                    await PowerCmd.Apply<DexterityPower>(owner.Creature, amount, owner.Creature, null);
                    break;
                case RewardType.Block:
                    await CreatureCmd.GainBlock(owner.Creature, amount, default, null);
                    break;
                case RewardType.Draw:
                    // 联机风险：抽牌走 Hook，会触发 ActionQueueSynchronizer。
                    // 暂以 ThrowingPlayerChoiceContext 单端调用——这里 Hook 只是 read，未走 player choice。
                    if (GameBridge.IsMultiplayer())
                    {
                        Log.Warn("[VocabSpire] Draw reward skipped in multiplayer (sync risk).");
                        break;
                    }
                    await CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), amount, owner);
                    break;
                case RewardType.Thorns:
                    await PowerCmd.Apply<ThornsPower>(owner.Creature, amount, owner.Creature, null);
                    break;
                case RewardType.Focus:
                    await PowerCmd.Apply<FocusPower>(owner.Creature, amount, owner.Creature, null);
                    break;
                case RewardType.Artifact:
                    await PowerCmd.Apply<ArtifactPower>(owner.Creature, amount, owner.Creature, null);
                    break;
            }
            Log.Info($"[VocabSpire] Reward applied: {kind} x{amount} to {owner.NetId}");
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire] Reward {kind} x{amount} failed: {ex.Message}");
        }
    }

}
