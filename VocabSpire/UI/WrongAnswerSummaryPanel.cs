using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;

namespace VocabSpire.UI;

/// <summary>
/// 战斗结���后的错题总结面板。
/// </summary>
public partial class WrongAnswerSummaryPanel : Control
{
    public static WrongAnswerSummaryPanel? Instance { get; private set; }

    private VBoxContainer _listContainer = null!;
    private Label _titleLabel = null!;
    private Action? _onDismiss;

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

        _titleLabel = GameTheme.MakeLabel("本次战斗错���回顾", 20, Gold, HorizontalAlignment.Center);
        mainVBox.AddChild(_titleLabel);
        mainVBox.AddChild(new HSeparator());

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(580, 300) };
        mainVBox.AddChild(scroll);

        _listContainer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _listContainer.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_listContainer);

        var btnCenter = new CenterContainer();
        mainVBox.AddChild(btnCenter);

        var dismissBtn = new Button
        {
            Text = "  继续 (Enter)  ",
            CustomMinimumSize = new Vector2(200, 44)
        };
        dismissBtn.AddThemeColorOverride("font_color", Gold);
        dismissBtn.AddThemeFontSizeOverride("font_size", 16);
        dismissBtn.Pressed += Dismiss;
        btnCenter.AddChild(dismissBtn);
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
        if (@event is InputEventKey { Pressed: true } key &&
            key.Keycode is Key.Enter or Key.Space or Key.KpEnter or Key.Escape)
        {
            Dismiss();
            GetViewport().SetInputAsHandled();
        }
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
