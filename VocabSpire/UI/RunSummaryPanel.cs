using System.Linq;
using Godot;
using VocabSpire.Models;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 全局回顾面板 —— 滚动列表展示单局所有答题记录。
/// </summary>
public partial class RunSummaryPanel : Control
{
    public static RunSummaryPanel? Instance { get; private set; }

    private Label _titleLabel = null!;
    private Label _statsLabel = null!;
    private VBoxContainer _listContainer = null!;
    private OptionButton _filterSelector = null!;
    private Button _closeBtn = null!;

    private RunQuizSummary? _summary;
    private System.Collections.Generic.List<RunQuizRecord> _filteredRecords = new();

    public override void _Ready()
    {
        Instance = this;
        BuildUI();
        GameTheme.ApplyFontRecursive(this);
        Visible = false;
        ZIndex = 110;
        ProcessMode = ProcessModeEnum.Always;
    }

    public new void Show() { Visible = true; }

    public void ShowSummary(RunQuizSummary? summary)
    {
        if (!IsInsideTree()) return;
        _summary = summary ?? new RunQuizSummary { Timestamp = "N/A" };
        RefreshFilter();
        Visible = true;
    }

    private void BuildUI()
    {
        AddChild(new ColorRect
        {
            Color = GameTheme.DarkBg,
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        });

        var margin = new MarginContainer
        {
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        };
        margin.AddThemeConstantOverride("margin_top", 36);
        margin.AddThemeConstantOverride("margin_bottom", 36);
        margin.AddThemeConstantOverride("margin_left", 50);
        margin.AddThemeConstantOverride("margin_right", 50);
        AddChild(margin);

        var root = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        BuildHeader(root);
        BuildStatsCards(root);
        BuildFilterBar(root);
        BuildList(root);
    }

    private void BuildHeader(VBoxContainer parent)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        parent.AddChild(row);

        GameTheme.AddIcon(row, GameTheme.IconCards, 32);
        _titleLabel = GameTheme.MakeLabel("\u672C\u5C40\u8BCD\u6C47\u56DE\u987E", 26, GameTheme.Gold, bold: true);
        row.AddChild(_titleLabel);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        _closeBtn = GameTheme.MakeButton("  \u5173\u95ED  ", 16);
        _closeBtn.Pressed += () => Visible = false;
        row.AddChild(_closeBtn);

        parent.AddChild(new HSeparator());
    }

    private void BuildStatsCards(VBoxContainer parent)
    {
        _statsLabel = GameTheme.MakeLabel("", 16, GameTheme.LightGray);
        _statsLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        parent.AddChild(_statsLabel);
    }

    private void BuildFilterBar(VBoxContainer parent)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        parent.AddChild(row);

        GameTheme.AddIcon(row, GameTheme.IconSwords, 20);
        row.AddChild(GameTheme.MakeLabel("\u7B5B\u9009", 18, GameTheme.Cream));

        _filterSelector = new OptionButton { CustomMinimumSize = new Vector2(120, 0) };
        _filterSelector.AddItem("\u5168\u90E8", 0);
        _filterSelector.AddItem("\u9519\u9898", 1);
        _filterSelector.AddItem("\u6B63\u786E", 2);
        _filterSelector.Selected = 0;
        _filterSelector.ItemSelected += _ => RefreshFilter();
        row.AddChild(_filterSelector);

        // 表头
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", 8);
        parent.AddChild(headerRow);
        headerRow.AddChild(GameTheme.Spacer(28));
        headerRow.AddChild(GameTheme.SizedLabel("\u5355\u8BCD", 160, 18, GameTheme.MidGray));
        headerRow.AddChild(GameTheme.SizedLabel("\u91CA\u4E49", 0, 18, GameTheme.MidGray, expand: true));
        headerRow.AddChild(GameTheme.SizedLabel("\u6A21\u5F0F", 60, 18, GameTheme.MidGray));
        headerRow.AddChild(GameTheme.SizedLabel("\u7ED3\u679C", 50, 18, GameTheme.MidGray));
        headerRow.AddChild(GameTheme.SizedLabel("\u4F60\u7684\u7B54\u6848", 0, 18, GameTheme.MidGray, expand: true));
    }

    private void BuildList(VBoxContainer parent)
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        parent.AddChild(scroll);

        _listContainer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _listContainer.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_listContainer);
    }

    private void RefreshFilter()
    {
        if (_summary is null) { _filteredRecords = new(); RefreshList(); return; }

        var filter = _filterSelector.Selected;
        _filteredRecords = filter switch
        {
            1 => _summary.Records.Where(r => !r.Correct).ToList(),
            2 => _summary.Records.Where(r => r.Correct).ToList(),
            _ => _summary.Records.ToList()
        };

        _titleLabel.Text = $"\u672C\u5C40\u8BCD\u6C47\u56DE\u987E  \u2014  {_summary.Timestamp}";

        var pct = _summary.TotalQuestions > 0 ? $"{_summary.Accuracy:P0}" : "--";
        var parts = $"\u2605 \u603B\u9898: {_summary.TotalQuestions}    " +
                    $"\u2714 \u6B63\u786E: {_summary.CorrectCount}    " +
                    $"\u2718 \u9519\u8BEF: {_summary.WrongCount}    " +
                    $"\u6B63\u786E\u7387: {pct}";
        if (_summary.TotalEnergyLost > 0)
            parts += $"    \u635F\u5931\u80FD\u91CF: {_summary.TotalEnergyLost}";
        _statsLabel.Text = parts;

        RefreshList();
    }

    private void RefreshList()
    {
        foreach (var child in _listContainer.GetChildren()) child.QueueFree();

        foreach (var r in _filteredRecords)
            _listContainer.AddChild(BuildRow(r));

        if (!_filteredRecords.Any())
            _listContainer.AddChild(GameTheme.MakeLabel(
                "\u6682\u65E0\u8BB0\u5F55", 18, GameTheme.MidGray, HorizontalAlignment.Center));
    }

    private Control BuildRow(RunQuizRecord r)
    {
        var row = new PanelContainer { CustomMinimumSize = new Vector2(0, 40) };
        var bg = r.Correct ? new Color(0.05f, 0.08f, 0.05f) : new Color(0.08f, 0.05f, 0.05f);
        row.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            ContentMarginLeft = 8, ContentMarginRight = 8,
            ContentMarginTop = 5, ContentMarginBottom = 5
        });

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 8);
        row.AddChild(hbox);

        GameTheme.AddIcon(hbox, r.Correct ? GameTheme.IconAchievement : GameTheme.IconSwords, 20);

        hbox.AddChild(GameTheme.SizedLabel(r.English, 160, 20, GameTheme.Cream));

        var cnLabel = GameTheme.MakeLabel(r.Chinese, 17, GameTheme.LightGray);
        cnLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        cnLabel.ClipText = true;
        hbox.AddChild(cnLabel);

        var modeShort = r.Mode switch
        {
            var m when m.Contains("Listen") => "\uD83D\uDD0A",
            var m when m.Contains("Spell") => "\u270D",
            var m when m.Contains("ChineseToEnglish") => "\u4E2D\u2192\u82F1",
            _ => "\u82F1\u2192\u4E2D"
        };
        hbox.AddChild(GameTheme.SizedLabel(modeShort, 60, 17, GameTheme.MidGray));

        var resultColor = r.Correct ? GameTheme.Green : GameTheme.Red;
        hbox.AddChild(GameTheme.SizedLabel(r.Correct ? "\u2714" : "\u2718", 50, 20, resultColor));

        // 答案列用 expand 撑满剩余空间
        if (!r.Correct && !string.IsNullOrEmpty(r.UserAnswer))
        {
            var ansLabel = GameTheme.MakeLabel(r.UserAnswer, 16, GameTheme.Red);
            ansLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            ansLabel.ClipText = true;
            hbox.AddChild(ansLabel);
        }
        else
        {
            var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            hbox.AddChild(spacer);
        }

        return row;
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;
        if (@event is InputEventKey { Pressed: true } key && key.Keycode == Key.Escape)
        {
            Visible = false;
            GetViewport().SetInputAsHandled();
        }
    }

    public static void Create()
    {
        var root = GameBridge.GetUIRoot();
        if (root is null) return;
        root.AddChild(new RunSummaryPanel
        {
            Name = "VocabSpireRunSummary",
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        });
    }
}
