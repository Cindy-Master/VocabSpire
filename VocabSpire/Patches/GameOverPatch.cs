using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using VocabSpire.Models;
using VocabSpire.Services;
using VocabSpire.UI;

namespace VocabSpire.Patches;

/// <summary>
/// Game Over 页面补丁 —— 显示浮动的"词汇回顾"按钮。
/// 按钮挂在 SceneTree 根节点上（不受游戏 UI 容器限制）。
/// </summary>
[HarmonyPatch(typeof(NGameOverScreen), nameof(NGameOverScreen.AfterOverlayOpened))]
public static class GameOverPatch
{
    private static Button? _floatingBtn;
    private static RunQuizSummary? _pendingSummary;

    public static void Postfix(NGameOverScreen __instance)
    {
        try
        {
            _pendingSummary = RunQuizTracker.Instance.FinishRun();

            // 移除旧按钮
            if (_floatingBtn is not null && GodotObject.IsInstanceValid(_floatingBtn))
                _floatingBtn.QueueFree();

            var root = GameBridge.GetUIRoot();
            if (root is null) return;

            _floatingBtn = GameTheme.MakeButton("  \u8BCD\u6C47\u56DE\u987E  ", 22);
            _floatingBtn.CustomMinimumSize = new Vector2(220, 60);
            _floatingBtn.ZIndex = 105;
            _floatingBtn.ProcessMode = Node.ProcessModeEnum.Always;

            // 锚点定位到屏幕底部中央偏右
            _floatingBtn.LayoutMode = 1;
            _floatingBtn.AnchorsPreset = (int)Control.LayoutPreset.CenterBottom;
            _floatingBtn.AnchorLeft = 0.5f;
            _floatingBtn.AnchorRight = 0.5f;
            _floatingBtn.AnchorTop = 1f;
            _floatingBtn.AnchorBottom = 1f;
            _floatingBtn.OffsetLeft = 140;
            _floatingBtn.OffsetRight = 360;
            _floatingBtn.OffsetTop = -85;
            _floatingBtn.OffsetBottom = -25;

            _floatingBtn.Pressed += OnReviewPressed;
            root.AddChild(_floatingBtn);
            GameTheme.ApplyFontRecursive(_floatingBtn);

            // 游戏退出结算页时自动移除按钮
            __instance.TreeExiting += () =>
            {
                if (_floatingBtn is not null && GodotObject.IsInstanceValid(_floatingBtn))
                    _floatingBtn.QueueFree();
                _floatingBtn = null;
            };

            Log.Info("[VocabSpire] Floating vocab review button created.");
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] GameOverPatch error: {ex}");
        }
    }

    private static void OnReviewPressed()
    {
        // Instance 可能已被 Godot 销毁（场景切换），需要检查有效性
        if (RunSummaryPanel.Instance is null || !GodotObject.IsInstanceValid(RunSummaryPanel.Instance))
        {
            RunSummaryPanel.Create();
        }
        RunSummaryPanel.Instance?.ShowSummary(_pendingSummary);
    }
}
