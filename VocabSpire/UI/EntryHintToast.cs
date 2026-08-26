using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 进入战斗时的一次性提示条 —— 顶部居中，淡入、停留几秒、淡出后自动销毁。
/// 告诉新玩家用哪个键打开设置、能配置奖励/惩罚等。仅前几局显示，之后不再打扰。
/// 自包含：MaybeShow() 按需创建节点挂到 UI 根，无需预先在 CreateUI 里创建。
/// </summary>
public partial class EntryHintToast : Control
{
    private const int MaxShowTimes = 3;    // 前 3 次战斗提示，之后永久不再显示
    private const float StaySeconds = 7f;  // 停留时长

    /// <summary>自定义两行文案（留空则用默认的「按 F8 打开设置」引导）。</summary>
    public string Line1Text = "";
    public string Line2Text = "";

    /// <summary>若还没到显示上限，弹出一次提示（战斗开始时调用）。</summary>
    public static void MaybeShow()
    {
        try
        {
            var cfg = VocabConfig.Instance;
            if (!cfg.Enabled) return;
            if (cfg.EntryHintShownCount >= MaxShowTimes) return;

            var root = GameBridge.GetUIRoot();
            if (root is null) return;

            root.CallDeferred(Node.MethodName.AddChild, new EntryHintToast());

            cfg.EntryHintShownCount++;
            cfg.Save();
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire] EntryHintToast.MaybeShow failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 第一次检测到手柄时，弹一次手柄键位引导（只弹一次，之后靠答题面板底部的常驻提示）。
    /// 与「按 F8 打开设置」的引导错开：那条还在显示的前几局就不弹，免得两条提示条在顶部叠一起。
    /// </summary>
    public static void MaybeShowGamepadHint()
    {
        try
        {
            var cfg = VocabConfig.Instance;
            if (!cfg.Enabled || cfg.GamepadHintShown) return;
            if (cfg.EntryHintShownCount < MaxShowTimes) return;   // 让位给设置引导
            if (!GamepadInput.IsPresent()) return;                // 没插手柄不提

            var root = GameBridge.GetUIRoot();
            if (root is null) return;

            root.CallDeferred(Node.MethodName.AddChild, new EntryHintToast
            {
                Line1Text = "🎮 检测到手柄 —— 答题面板支持手柄操作",
                Line2Text = "↑↓/摇杆 选项 · [A] 选中或提交 · [Y] 提交多选 · [X] 忘了 · 答完 [A] 继续（拼写题仍需键盘打字）"
            });

            cfg.GamepadHintShown = true;
            cfg.Save();
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire] EntryHintToast.MaybeShowGamepadHint failed: {ex.Message}");
        }
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        TopLevel = true;
        ZIndex = 90;
        MouseFilter = MouseFilterEnum.Ignore;   // 全程不挡点击
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var key = KeyBindButton.KeyName(VocabConfig.Instance.SettingsHotkey);

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.04f, 0.06f, 0.94f),
            BorderColor = GameTheme.BorderGold,
            ContentMarginLeft = 24, ContentMarginRight = 24,
            ContentMarginTop = 12, ContentMarginBottom = 12,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8
        };
        style.SetBorderWidthAll(2);

        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        vbox.AddThemeConstantOverride("separation", 4);
        panel.AddChild(vbox);

        var text1 = string.IsNullOrEmpty(Line1Text)
            ? $"💡 VocabSpire 已启用 —— 按  [ {key} ]  打开设置"
            : Line1Text;
        var text2 = string.IsNullOrEmpty(Line2Text)
            ? "答对奖励 / 答错惩罚 / 词库 / 题型 / 记忆难度 都能在里面自定义"
            : Line2Text;

        var line1 = GameTheme.MakeLabel(text1, 20, GameTheme.Gold, HorizontalAlignment.Center, bold: true);
        var line2 = GameTheme.MakeLabel(text2, 15, GameTheme.Cream, HorizontalAlignment.Center);
        line1.MouseFilter = MouseFilterEnum.Ignore;
        line2.MouseFilter = MouseFilterEnum.Ignore;
        vbox.AddChild(line1);
        vbox.AddChild(line2);

        AddChild(panel);

        // 顶部居中（延迟到布局尺寸就绪后定位）
        Callable.From(() => CenterTop(panel)).CallDeferred();
        panel.Resized += () => CenterTop(panel);

        // 淡入 → 停留 → 淡出 → 释放
        Modulate = new Color(1, 1, 1, 0);
        var tween = CreateTween();
        tween.TweenProperty(this, "modulate:a", 1f, 0.4f);
        tween.TweenInterval(StaySeconds);
        tween.TweenProperty(this, "modulate:a", 0f, 0.6f);
        tween.TweenCallback(Callable.From(QueueFree));
    }

    private void CenterTop(Control panel)
    {
        var vpWidth = GetViewportRect().Size.X;
        panel.Position = new Vector2((vpWidth - panel.Size.X) / 2f, 72);
    }
}
