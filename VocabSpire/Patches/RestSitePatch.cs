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
    public static void Postfix()
    {
        try
        {
            if (!VocabConfig.Instance.Enabled) return;
            if (!VocabConfig.Instance.ShowRestSiteReview) return;

            var allRecords = WrongAnswerTracker.Instance.FlushSegmentAnswers();
            if (allRecords.Count == 0) return;

            // 按配置限制复习题数
            var maxCount = VocabConfig.Instance.ReviewMaxCount;
            var records = (maxCount > 0 && allRecords.Count > maxCount)
                ? allRecords.OrderBy(_ => Guid.NewGuid()).Take(maxCount).ToList().AsReadOnly()
                : allRecords;

            var panel = RestSiteReviewPanel.Instance;
            if (panel is null) return;

            Log.Info($"[VocabSpire] Showing rest site review: {records.Count}/{allRecords.Count} wrong answers.");
            panel.ShowReview(records);
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] RestSitePatch error: {ex}");
        }
    }
}
