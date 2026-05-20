using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
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
/// PowerCmd.Apply 在不同游戏版本签名不同 (release 5 参数 / beta v0.105+ 6 参数带
/// PlayerChoiceContext) —— 用反射动态适配两版，源代码同时兼容两边。
/// </summary>
public static class RewardService
{
    // ── PowerCmd.Apply 反射缓存（按 Power 类型缓存找到的泛型方法）──
    private static readonly System.Collections.Generic.Dictionary<System.Type, MethodInfo?> _applyCache = new();
    private static readonly object _applyCacheLock = new();

    /// <summary>
    /// 找到 PowerCmd.Apply&lt;T&gt;(... Creature target, decimal amount, ...) 单目标重载。
    /// release: Apply&lt;T&gt;(Creature, decimal, Creature?, CardModel?, bool)
    /// beta:    Apply&lt;T&gt;(PlayerChoiceContext, Creature, decimal, Creature?, CardModel?, bool)
    /// </summary>
    private static MethodInfo? FindApplyMethodOpenGeneric()
    {
        foreach (var m in typeof(PowerCmd).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Apply" || !m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            // 排除批量 IEnumerable<Creature> 那个重载
            var hasSingleCreature = false;
            foreach (var p in ps)
                if (p.ParameterType == typeof(Creature)) { hasSingleCreature = true; break; }
            if (!hasSingleCreature) continue;
            // 排除返回 IReadOnlyList<T>（这通常对应 IEnumerable<Creature> 那版）
            if (m.ReturnType.IsGenericType &&
                m.ReturnType.GetGenericTypeDefinition().Name.StartsWith("Task`") &&
                m.ReturnType.GetGenericArguments()[0].IsGenericType)
            {
                continue;
            }
            return m;
        }
        return null;
    }

    private static async Task ApplyPowerAsync<T>(Creature target, decimal amount, Creature? applier, CardModel? cardSource)
        where T : PowerModel
    {
        MethodInfo? open;
        lock (_applyCacheLock)
        {
            if (!_applyCache.TryGetValue(typeof(T), out open))
            {
                var baseOpen = FindApplyMethodOpenGeneric();
                open = baseOpen?.MakeGenericMethod(typeof(T));
                _applyCache[typeof(T)] = open;
            }
        }
        if (open is null)
        {
            Log.Warn($"[VocabSpire] PowerCmd.Apply<{typeof(T).Name}> not found on this game version.");
            return;
        }

        var ps = open.GetParameters();
        object?[] args;
        if (ps.Length == 6 && ps[0].ParameterType.Name == "PlayerChoiceContext")
        {
            // beta：PlayerChoiceContext, Creature, decimal, Creature?, CardModel?, bool
            args = new object?[] { new ThrowingPlayerChoiceContext(), target, amount, applier, cardSource, false };
        }
        else if (ps.Length == 5)
        {
            // release：Creature, decimal, Creature?, CardModel?, bool
            args = new object?[] { target, amount, applier, cardSource, false };
        }
        else
        {
            Log.Warn($"[VocabSpire] PowerCmd.Apply<{typeof(T).Name}> has unexpected signature ({ps.Length} params); skipping.");
            return;
        }

        var task = (Task?)open.Invoke(null, args);
        if (task != null) await task;
    }

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
                    await ApplyPowerAsync<StrengthPower>(owner.Creature, amount, owner.Creature, null);
                    break;
                case RewardType.Dexterity:
                    await ApplyPowerAsync<DexterityPower>(owner.Creature, amount, owner.Creature, null);
                    break;
                case RewardType.Block:
                    await CreatureCmd.GainBlock(owner.Creature, amount, default, null);
                    break;
                case RewardType.Draw:
                    // 联机风险：抽牌走 Hook，会触发 ActionQueueSynchronizer。
                    if (GameBridge.IsMultiplayer())
                    {
                        Log.Warn("[VocabSpire] Draw reward skipped in multiplayer (sync risk).");
                        break;
                    }
                    await CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), amount, owner);
                    break;
                case RewardType.Thorns:
                    await ApplyPowerAsync<ThornsPower>(owner.Creature, amount, owner.Creature, null);
                    break;
                case RewardType.Focus:
                    await ApplyPowerAsync<FocusPower>(owner.Creature, amount, owner.Creature, null);
                    break;
                case RewardType.Artifact:
                    await ApplyPowerAsync<ArtifactPower>(owner.Creature, amount, owner.Creature, null);
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
