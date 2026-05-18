using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;
using VocabSpire.Models;
using VocabSpire.Services;
using VocabSpire.UI;

namespace VocabSpire.Patches;

/// <summary>
/// 历史记录页面补丁 —— 每次 DisplayRun 时更新词汇回顾按钮。
/// 按钮绑定当前查看的那一局，通过 Seed+StartTime 匹配。
/// </summary>
[HarmonyPatch(typeof(NRunHistory), "_Ready")]
public static class RunHistoryReadyPatch
{
    internal static Button? ReviewBtn;
    internal static RunQuizSummary? CurrentSummary;

    public static void Postfix(NRunHistory __instance)
    {
        try
        {
            var btn = GameTheme.MakeButton("  \u8BCD\u6C47\u56DE\u987E  ", 20);
            btn.CustomMinimumSize = new Vector2(200, 55);
            btn.Pressed += OnPressed;
            btn.Visible = false; // 等 DisplayRun 时根据是否有数据决定显示

            // 加到 NRunHistory 自身（不受 ScreenContents 滚动影响）
            __instance.AddChild(btn);

            // 定位到右上角
            btn.LayoutMode = 1;
            btn.AnchorLeft = 1f; btn.AnchorRight = 1f;
            btn.AnchorTop = 0f; btn.AnchorBottom = 0f;
            btn.OffsetLeft = -220; btn.OffsetRight = -20;
            btn.OffsetTop = 10; btn.OffsetBottom = 65;
            btn.ZIndex = 10;

            GameTheme.ApplyFontRecursive(btn);
            ReviewBtn = btn;

            Log.Info("[VocabSpire] Vocab review button added to run history.");
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] RunHistoryReadyPatch error: {ex}");
        }
    }

    private static void OnPressed()
    {
        if (CurrentSummary is null) return;

        if (RunSummaryPanel.Instance is null || !GodotObject.IsInstanceValid(RunSummaryPanel.Instance))
            RunSummaryPanel.Create();
        RunSummaryPanel.Instance?.ShowSummary(CurrentSummary);
    }
}

/// <summary>
/// 每次加载一局历史时，查找对应的词汇回顾并更新按钮状态。
/// </summary>
[HarmonyPatch(typeof(NRunHistory), "DisplayRun")]
public static class RunHistoryDisplayPatch
{
    public static void Postfix(RunHistory history)
    {
        try
        {
            var btn = RunHistoryReadyPatch.ReviewBtn;
            if (btn is null || !GodotObject.IsInstanceValid(btn)) return;

            // 用 Seed + StartTime 匹配
            var summary = RunQuizTracker.FindBySeedAndTime(
                history.Seed ?? "", history.StartTime);

            RunHistoryReadyPatch.CurrentSummary = summary;

            if (summary is not null && summary.TotalQuestions > 0)
            {
                btn.Text = $"  \u8BCD\u6C47\u56DE\u987E ({summary.TotalQuestions}\u9898)  ";
                btn.Visible = true;
            }
            else
            {
                btn.Visible = false; // 这局没有词汇数据
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] RunHistoryDisplayPatch error: {ex}");
        }
    }
}
