using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using VocabSpire.Services;

namespace VocabSpire.Patches;

/// <summary>
/// 回合开始时重置容错计数（仅本地玩家所在 side）。
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeSideTurnStart))]
public static class TurnStartPatch
{
    public static void Prefix(CombatSide side)
    {
        // 仅玩家 side 回合开始时重置
        if (side == CombatSide.Player)
        {
            BattleStateTracker.Instance.OnSideTurnStart();
        }
    }
}
