using System;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using VocabSpire.Services;
using VocabSpire.UI;

namespace VocabSpire.Patches;

/// <summary>
/// 篝火（休息点）补丁 —— 到达篝火时弹出错题复习。
/// </summary>
[HarmonyPatch(typeof(NRestSiteRoom), "_Ready")]
public static class RestSitePatch
{
    /// <summary>篝火复习默认软上限（认知负荷 ~7±2），用户未设 ReviewMaxCount 时生效。</summary>
    private const int DefaultReviewCap = 7;

    public static void Postfix()
    {
        try
        {
            if (!VocabConfig.Instance.Enabled) return;
            if (!VocabConfig.Instance.ShowRestSiteReview) return;

            var allRecords = WrongAnswerTracker.Instance.FlushSegmentAnswers();
            if (allRecords.Count == 0) return;

            // ② 篝火复习「分散 + 限量 + 优先级」：同词去重 → 按「最该复习」排序（Box 低=没掌握、
            // 错得多 优先）→ 限量（默认软上限 ~7，认知负荷上限；用户设了 ReviewMaxCount 则用它）。
            // 超出的不在篝火堆着，靠间隔重复引擎在后续战斗中按 Box 自然重现（spacing），不再一次 20+。
            int cap = VocabConfig.Instance.ReviewMaxCount > 0
                ? VocabConfig.Instance.ReviewMaxCount
                : DefaultReviewCap;
            var records = allRecords
                .GroupBy(r => r.Word.English.ToLowerInvariant())
                .Select(g => g.First())
                .OrderBy(r => r.Word.Box)
                .ThenByDescending(r => r.Word.WrongCount)
                .Take(cap)
                .ToList()
                .AsReadOnly();

            var panel = RestSiteReviewPanel.Instance;
            if (panel is null) return;

            Log.Info($"[VocabSpire] Rest review: showing {records.Count} (deduped from {allRecords.Count} wrong answers).");
            panel.ShowReview(records);
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] RestSitePatch error: {ex}");
        }
    }
}
