using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;

namespace VocabSpire.UI;

/// <summary>
/// 战斗结束后的错题总结面板。
/// </summary>
public partial class WrongAnswerSummaryPanel : Control
{
    public static WrongAnswerSummaryPanel? Instance { get; private set; }

    private VBoxContainer _listContainer = null!;
    private Label _titleLabel = null!;
    private ScrollContainer _scroll = null!;
    private Button _dismissBtn = null!;
    private Label _padHint = null!;
    private Action? _onDismiss;

    /// <summary>手柄上下键每次滚动的像素数 —— 约一条错题记录的高度。</summary>
    private const int PadScrollStep = 90;

    private static readonly Color BgColor = GameTheme.DarkBg;
    private static readonly Color Gold = GameTheme.Gold;
    private static readonly Color White = GameTheme.Cream;
    private static readonly Color Grey = GameTheme.LightGray;
    private static readonly Color WrongRed = GameTheme.Red;
    private static readonly Color CorrectGreen = GameTheme.Green;

    public override void _Ready()
    {
        Instance = this;
        BuildUI();
        GameTheme.ApplyFontRecursive(this);
        Visible = false;
        ZIndex = 100;
        ProcessMode = ProcessModeEnum.Always;
    }

    private void BuildUI()
    {
        var overlay = new ColorRect
        {
            Color = GameTheme.Backdrop,
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        };
        AddChild(overlay);

        var center = new CenterContainer
        {
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        };
        AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(650, 0) };
        var style = new StyleBoxFlat
        {
            BgColor = BgColor,
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderColor = Gold,
            ContentMarginTop = 24, ContentMarginBottom = 24,
            ContentMarginLeft = 32, ContentMarginRight = 32
        };
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 14);
        panel.AddChild(mainVBox);

        _titleLabel = GameTheme.MakeLabel("本次战斗错题回顾", 20, Gold, HorizontalAlignment.Center);
        mainVBox.AddChild(_titleLabel);
        mainVBox.AddChild(new HSeparator());

        _scroll = new ScrollContainer { CustomMinimumSize = new Vector2(580, 300) };
        mainVBox.AddChild(_scroll);

        _listContainer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _listContainer.AddThemeConstantOverride("separation", 8);
        _scroll.AddChild(_listContainer);

        var btnCenter = new CenterContainer();
        mainVBox.AddChild(btnCenter);

        _dismissBtn = new Button
        {
            Text = "  继续 (Enter)  ",
            CustomMinimumSize = new Vector2(200, 44)
        };
        _dismissBtn.AddThemeColorOverride("font_color", Gold);
        _dismissBtn.AddThemeFontSizeOverride("font_size", 16);
        _dismissBtn.Pressed += Dismiss;
        btnCenter.AddChild(_dismissBtn);

        // 手柄键位提示：没插手柄时整行隐藏、不占版面（与答题面板一致）
        _padHint = GameTheme.MakeLabel("", 12, GameTheme.MidGray, HorizontalAlignment.Center);
        _padHint.Visible = false;
        mainVBox.AddChild(_padHint);
    }

    public void ShowSummary(IReadOnlyList<WrongAnswerRecord> records, Action? onDismiss = null)
    {
        _onDismiss = onDismiss;

        // 清空旧内容
        foreach (var child in _listContainer.GetChildren())
            child.QueueFree();

        _titleLabel.Text = $"本次战斗错题回顾 ({records.Count} 题)";

        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var row = new VBoxContainer();
            row.AddThemeConstantOverride("separation", 2);

            var wordLabel = GameTheme.MakeLabel($"{i + 1}. {r.Word.English}  —  {r.Word.Chinese}", 16, White);
            wordLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            row.AddChild(wordLabel);

            var modeText = r.Mode switch
            {
                QuizModeFlags.SpellEnglish => "拼写",
                QuizModeFlags.ChineseToEnglish => "中→英",
                QuizModeFlags.ListenToChinese => "听力",
                QuizModeFlags.RecallCard => "回忆卡",
                _ => "英→中"
            };
            var userPart = string.IsNullOrEmpty(r.UserAnswerDetail)
                ? r.UserAnswer
                : $"{r.UserAnswer} ({r.UserAnswerDetail})";
            var correctPart = string.IsNullOrEmpty(r.CorrectAnswerDetail)
                ? r.CorrectAnswer
                : $"{r.CorrectAnswer} ({r.CorrectAnswerDetail})";
            var detailLabel = GameTheme.MakeLabel(
                $"   [{modeText}] 你的回答：{userPart}  |  正确答案：{correctPart}",
                13, Grey);
            detailLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            row.AddChild(detailLabel);

            _listContainer.AddChild(row);

            if (i < records.Count - 1)
                _listContainer.AddChild(new HSeparator());
        }

        // 每次弹出都从头看起，免得沿用上一场战斗的滚动位置
        _scroll.ScrollVertical = 0;
        Services.GamepadInput.ResetAxisState();

        var padPresent = Services.GamepadInput.IsPresent();
        _padHint.Visible = padPresent;
        _padHint.Text = padPresent ? "🎮 ↑↓ / 左摇杆 滚动列表 · [A] 继续" : "";
        _dismissBtn.Text = padPresent ? "  继续 (Enter / [A])  " : "  继续 (Enter)  ";

        Visible = true;
    }

    private void Dismiss()
    {
        Visible = false;
        _onDismiss?.Invoke();
        _onDismiss = null;
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;

        // 手柄：本面板是战斗结束后强制弹出的，手柄玩家绕不开 —— 既要能滚动看完错题，也要能关掉
        var pad = Services.GamepadInput.Translate(@event);
        switch (pad)
        {
            case Services.PadAction.Up:
                ScrollByPad(-PadScrollStep);
                GetViewport().SetInputAsHandled();
                return;
            case Services.PadAction.Down:
                ScrollByPad(PadScrollStep);
                GetViewport().SetInputAsHandled();
                return;
            case Services.PadAction.Accept:
            case Services.PadAction.Submit:
                Dismiss();
                GetViewport().SetInputAsHandled();
                return;
        }

        if (@event is InputEventKey { Pressed: true } key &&
            key.Keycode is Key.Enter or Key.Space or Key.KpEnter or Key.Escape)
        {
            Dismiss();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>手柄滚动错题列表。ScrollContainer.ScrollVertical 是像素值，负数无意义故夹到 0。</summary>
    private void ScrollByPad(int delta)
    {
        _scroll.ScrollVertical = Mathf.Max(0, _scroll.ScrollVertical + delta);
    }

    public static void Create()
    {
        var root = Services.GameBridge.GetUIRoot();
        if (root is null) return;
        root.AddChild(new WrongAnswerSummaryPanel
        {
            Name = "VocabSpireSummary",
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        });
    }
}
