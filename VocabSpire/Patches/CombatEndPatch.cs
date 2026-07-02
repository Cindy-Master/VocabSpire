using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;
using VocabSpire.Services;
using VocabSpire.UI;

namespace VocabSpire.Patches;

/// <summary>
/// 战斗事件处理 —— 通过 CombatManager 事件订阅（非 Harmony 补丁）。
/// 战斗开始时清空本次战斗错题，战斗结束时显示错题总结。
/// </summary>
public static class CombatEndHandler
{
    public static void Subscribe()
    {
        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        CombatManager.Instance.CombatEnded += OnCombatEnded;
        Log.Info("[VocabSpire] Combat event handlers subscribed.");
    }

    private static void OnCombatSetUp(CombatState _)
    {
        WrongAnswerTracker.Instance.ClearCombatAnswers();
        // 前几局战斗开始时提示玩家：按快捷键可打开设置、配置奖励/惩罚/词库等。
        UI.EntryHintToast.MaybeShow();
    }

    private static void OnCombatEnded(CombatRoom _)
    {
        if (!VocabConfig.Instance.Enabled) return;
        if (!VocabConfig.Instance.ShowCombatSummary) return;

        var records = WrongAnswerTracker.Instance.FlushCombatAnswers();
        if (records.Count == 0) return;

        var panel = WrongAnswerSummaryPanel.Instance;
        if (panel is null) return;

        Log.Info($"[VocabSpire] Showing combat summary: {records.Count} wrong answers.");
        panel.ShowSummary(records);
    }
}
