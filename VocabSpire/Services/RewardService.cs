using System.Linq;
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
        MethodInfo? chosen = null;
        var allApply = typeof(PowerCmd).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Apply").ToList();
        Log.Info($"[VocabSpire][Reward] FindApplyMethodOpenGeneric: total Apply overloads={allApply.Count}");

        foreach (var m in allApply)
        {
            var ps = m.GetParameters();
            var paramSig = string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"));
            var retName = m.ReturnType.IsGenericType
                ? $"{m.ReturnType.GetGenericTypeDefinition().Name}<{string.Join(",", m.ReturnType.GetGenericArguments().Select(t => t.Name))}>"
                : m.ReturnType.Name;
            Log.Info($"[VocabSpire][Reward]   candidate: Apply<{(m.IsGenericMethodDefinition ? "T" : "_")}>({paramSig}) → {retName}");

            if (!m.IsGenericMethodDefinition) { Log.Info("[VocabSpire][Reward]     ✗ skipped: not generic"); continue; }

            var hasSingleCreature = ps.Any(p => p.ParameterType == typeof(Creature));
            if (!hasSingleCreature) { Log.Info("[VocabSpire][Reward]     ✗ skipped: no single Creature param"); continue; }

            // 排除返回 Task<IReadOnlyList<T>> 等批量返回
            if (m.ReturnType.IsGenericType &&
                m.ReturnType.GetGenericTypeDefinition().Name.StartsWith("Task`") &&
                m.ReturnType.GetGenericArguments()[0].IsGenericType)
            {
                Log.Info("[VocabSpire][Reward]     ✗ skipped: return type is Task<generic> (batch)");
                continue;
            }

            if (chosen is null)
            {
                chosen = m;
                Log.Info($"[VocabSpire][Reward]     ✓ CHOSEN as PowerCmd.Apply<T> single-target overload");
            }
            else
            {
                Log.Warn($"[VocabSpire][Reward]     ⚠ AMBIGUOUS: another candidate passed all filters! Sticking with first.");
            }
        }
        return chosen;
    }

    private static async Task ApplyPowerAsync<T>(Creature target, decimal amount, Creature? applier, CardModel? cardSource)
        where T : PowerModel
    {
        var typeName = typeof(T).Name;
        MethodInfo? open;
        lock (_applyCacheLock)
        {
            if (!_applyCache.TryGetValue(typeof(T), out open))
            {
                var baseOpen = FindApplyMethodOpenGeneric();
                open = baseOpen?.MakeGenericMethod(typeof(T));
                _applyCache[typeof(T)] = open;
                Log.Info($"[VocabSpire][Reward] ApplyPowerAsync<{typeName}>: method resolved (params={open?.GetParameters().Length})");
            }
        }
        if (open is null)
        {
            Log.Warn($"[VocabSpire][Reward] PowerCmd.Apply<{typeName}> not found on this game version.");
            return;
        }

        var ps = open.GetParameters();
        object?[] args;
        if (ps.Length == 6 && ps[0].ParameterType.Name == "PlayerChoiceContext")
        {
            args = new object?[] { new ThrowingPlayerChoiceContext(), target, amount, applier, cardSource, false };
        }
        else if (ps.Length == 5)
        {
            args = new object?[] { target, amount, applier, cardSource, false };
        }
        else
        {
            Log.Warn($"[VocabSpire][Reward] PowerCmd.Apply<{typeName}> unexpected signature ({ps.Length} params); skipping.");
            return;
        }

        Log.Info($"[VocabSpire][Reward] ApplyPowerAsync<{typeName}>: invoking (target={target?.GetType().Name} amount={amount} applier={applier?.GetType().Name} cardSource={cardSource?.GetType().Name ?? "null"})");
        Task? task;
        try
        {
            task = (Task?)open.Invoke(null, args);
        }
        catch (Exception ex)
        {
            // 反射调用本身抛（如 TargetInvocationException 包装游戏内部异常）
            var inner = ex.InnerException ?? ex;
            Log.Error($"[VocabSpire][Reward] ApplyPowerAsync<{typeName}>: Invoke threw {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}");
            throw; // 让 ApplyAllAsync 的外层 catch 接住，不影响后续 reward
        }
        if (task is null)
        {
            Log.Warn($"[VocabSpire][Reward] ApplyPowerAsync<{typeName}>: invoke returned null Task");
            return;
        }
        try
        {
            await task;
            Log.Info($"[VocabSpire][Reward] ApplyPowerAsync<{typeName}>: task awaited OK");
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire][Reward] ApplyPowerAsync<{typeName}>: await threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            throw; // 同上：让外层 catch 决定是否继续
        }
    }

    /// <summary>批量应用奖励列表（按顺序 await）。</summary>
    public static async Task ApplyAllAsync(Player owner, System.Collections.Generic.IReadOnlyList<(RewardType kind, int amount)> rewards)
    {
        if (owner is null || rewards.Count == 0) return;
        if (!CombatManager.Instance.IsInProgress) return;

        Log.Info($"[VocabSpire][Reward] ApplyAllAsync start: count={rewards.Count} list=[{string.Join(",", rewards.Select(r => $"{r.kind}x{r.amount}"))}]");
        for (var i = 0; i < rewards.Count; i++)
        {
            var (kind, amount) = rewards[i];
            Log.Info($"[VocabSpire][Reward] [{i + 1}/{rewards.Count}] -> ApplyOneAsync(kind={kind} amount={amount})");
            try
            {
                await ApplyOneAsync(owner, kind, amount);
                Log.Info($"[VocabSpire][Reward] [{i + 1}/{rewards.Count}] <- ApplyOneAsync returned (kind={kind})");
            }
            catch (Exception ex)
            {
                // 兜底：单条 reward 抛异常不阻塞后面其余 reward
                Log.Error($"[VocabSpire][Reward] [{i + 1}/{rewards.Count}] EXCEPTION at ApplyOneAsync(kind={kind} amount={amount}): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
        Log.Info($"[VocabSpire][Reward] ApplyAllAsync end (all {rewards.Count} processed)");
    }

    /// <summary>单条奖励。</summary>
    public static async Task ApplyOneAsync(Player owner, RewardType kind, int amount)
    {
        if (owner is null || amount <= 0 || kind == RewardType.None)
        {
            Log.Info($"[VocabSpire][Reward] skip {kind} x{amount} (owner null / amount<=0 / kind=None)");
            return;
        }
        if (!CombatManager.Instance.IsInProgress && kind != RewardType.Hp)
        {
            Log.Info($"[VocabSpire][Reward] skip {kind} x{amount} (combat not in progress)");
            return;
        }

        try
        {
            Log.Info($"[VocabSpire][Reward] enter switch: kind={kind} amount={amount}");
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
