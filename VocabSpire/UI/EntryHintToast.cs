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

        var line1 = GameTheme.MakeLabel($"💡 VocabSpire 已启用 —— 按  [ {key} ]  打开设置",
            20, GameTheme.Gold, HorizontalAlignment.Center, bold: true);
        var line2 = GameTheme.MakeLabel("答对奖励 / 答错惩罚 / 词库 / 题型 / 记忆难度 都能在里面自定义",
            15, GameTheme.Cream, HorizontalAlignment.Center);
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
