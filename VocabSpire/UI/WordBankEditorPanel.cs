using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 可视化新建/编辑词库面板。
/// 用户可在表格中逐行填写单词，保存后写入 wordbanks/<id>.json 并自动激活。
/// </summary>
public partial class WordBankEditorPanel : Control
{
    public static WordBankEditorPanel? Instance { get; private set; }

    private LineEdit _nameInput = null!;
    private LineEdit _descInput = null!;
    private VBoxContainer _rowsContainer = null!;
    private Label _statusLabel = null!;

    private readonly List<RowWidgets> _rows = new();

    private static readonly Color Gold = GameTheme.Gold;
    private static readonly Color Cream = GameTheme.Cream;
    private static readonly Color DimGrey = GameTheme.MidGray;

    public override void _Ready()
    {
        Instance = this;
        BuildUI();
        GameTheme.ApplyFontRecursive(this);
        Visible = false;
        ZIndex = 102;
        ProcessMode = ProcessModeEnum.Always;
    }

    public void Open()
    {
        Visible = true;
        _nameInput.Text = "";
        _descInput.Text = "";
        ClearRows();
        for (var i = 0; i < 3; i++) AddRow();
        _statusLabel.Text = "";
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

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(820, 0) };
        var style = new StyleBoxFlat
        {
            BgColor = GameTheme.DarkBg,
            CornerRadiusTopLeft = 16, CornerRadiusTopRight = 16,
            CornerRadiusBottomLeft = 16, CornerRadiusBottomRight = 16,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderColor = Gold,
            ContentMarginTop = 28, ContentMarginBottom = 28,
            ContentMarginLeft = 36, ContentMarginRight = 36
        };
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 12);
        panel.AddChild(vbox);

        vbox.AddChild(GameTheme.MakeLabel("新建词库", 24, Gold));
        vbox.AddChild(new HSeparator());

        // 名称
        var nameRow = new HBoxContainer();
        nameRow.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(nameRow);
        nameRow.AddChild(GameTheme.SizedLabel("词库名称：", 100, 16, Cream));
        _nameInput = new LineEdit { CustomMinimumSize = new Vector2(540, 0), PlaceholderText = "例：我的精选词表" };
        _nameInput.AddThemeFontSizeOverride("font_size", 14);
        nameRow.AddChild(_nameInput);

        // 描述
        var descRow = new HBoxContainer();
        descRow.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(descRow);
        descRow.AddChild(GameTheme.SizedLabel("描述：", 100, 16, Cream));
        _descInput = new LineEdit { CustomMinimumSize = new Vector2(540, 0), PlaceholderText = "可选词库描述" };
        _descInput.AddThemeFontSizeOverride("font_size", 14);
        descRow.AddChild(_descInput);

        vbox.AddChild(new HSeparator());

        // 表头
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(header);
        header.AddChild(GameTheme.SizedLabel("英文", 180, 14, Gold));
        header.AddChild(GameTheme.SizedLabel("中文释义 (多个用 ; 分隔)", 320, 14, Gold));
        header.AddChild(GameTheme.SizedLabel("音标 (可选)", 160, 14, Gold));

        // 滚动区
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(740, 360),
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        vbox.AddChild(scroll);

        _rowsContainer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rowsContainer.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_rowsContainer);

        // 操作按钮
        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(btnRow);

        var addBtn = GameTheme.MakeButton("  添加一行  ", 14);
        addBtn.Pressed += () => AddRow();
        btnRow.AddChild(addBtn);

        var addManyBtn = GameTheme.MakeButton("  +10 行  ", 14);
        addManyBtn.Pressed += () => { for (var i = 0; i < 10; i++) AddRow(); };
        btnRow.AddChild(addManyBtn);

        btnRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var saveBtn = GameTheme.MakeButton("  保存并启用  ", 14, Gold);
        saveBtn.Pressed += OnSave;
        btnRow.AddChild(saveBtn);

        var cancelBtn = GameTheme.MakeButton("  取消  ", 14);
        cancelBtn.Pressed += () => Visible = false;
        btnRow.AddChild(cancelBtn);

        _statusLabel = GameTheme.MakeLabel("", 13, DimGrey);
        _statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(_statusLabel);
    }

    private void AddRow()
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);

        var en = new LineEdit { CustomMinimumSize = new Vector2(180, 0) };
        en.AddThemeFontSizeOverride("font_size", 14);

        var cn = new LineEdit { CustomMinimumSize = new Vector2(320, 0) };
        cn.AddThemeFontSizeOverride("font_size", 14);

        var phon = new LineEdit { CustomMinimumSize = new Vector2(160, 0) };
        phon.AddThemeFontSizeOverride("font_size", 14);

        var delBtn = new Button { Text = " ✕ ", CustomMinimumSize = new Vector2(36, 0) };
        delBtn.AddThemeFontSizeOverride("font_size", 14);
        delBtn.Pressed += () =>
        {
            var idx = _rows.FindIndex(r => r.Row == row);
            if (idx >= 0)
            {
                _rows.RemoveAt(idx);
                row.QueueFree();
            }
        };

        row.AddChild(en);
        row.AddChild(cn);
        row.AddChild(phon);
        row.AddChild(delBtn);

        _rowsContainer.AddChild(row);
        _rows.Add(new RowWidgets { Row = row, En = en, Cn = cn, Phon = phon });

        // 动态新增的行也要套上游戏字体，否则中文会糊
        GameTheme.ApplyFontRecursive(row);
    }

    private void ClearRows()
    {
        foreach (var r in _rows) r.Row.QueueFree();
        _rows.Clear();
    }

    private void OnSave()
    {
        var name = _nameInput.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _statusLabel.Text = "请填写词库名称。";
            return;
        }

        var words = new List<object>();
        foreach (var r in _rows)
        {
            var en = r.En.Text.Trim();
            var cn = r.Cn.Text.Trim();
            var phon = r.Phon.Text.Trim();
            if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(cn)) continue;

            if (cn.Contains(';'))
            {
                var defs = cn.Split(';', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
                words.Add(new { english = en, chinese = defs, phonetic = phon });
            }
            else
            {
                words.Add(new { english = en, chinese = cn, phonetic = phon });
            }
        }

        if (words.Count < 4)
        {
            _statusLabel.Text = "至少需要 4 个有效单词（选择题需要）。";
            return;
        }

        try
        {
            var data = new
            {
                name,
                description = _descInput.Text.Trim(),
                words
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

            var fileName = SanitizeFileName(name) + ".json";
            var path = Path.Combine(VocabManager.Instance.GetWordBanksDirectory(), fileName);
            File.WriteAllText(path, json);

            var bank = VocabManager.Instance.ImportBank(path);
            if (bank is not null)
            {
                VocabManager.Instance.SetActiveBank(bank.Id);
            }
            _statusLabel.Text = $"已保存: {path}";
            Log.Info($"[VocabSpire] Saved new bank: {path}");

            // 通知父面板刷新
            VocabSettingsPanel.Instance?.NotifyBanksChanged();
            Visible = false;
        }
        catch (System.Exception ex)
        {
            _statusLabel.Text = $"保存失败: {ex.Message}";
            Log.Error($"[VocabSpire] Save bank failed: {ex}");
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return string.IsNullOrEmpty(clean) ? "custom_bank" : clean;
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Visible = false;
            GetViewport().SetInputAsHandled();
        }
    }

    public static void Create()
    {
        var root = GameBridge.GetUIRoot();
        if (root is null) return;
        root.AddChild(new WordBankEditorPanel
        {
            Name = "VocabSpireWordBankEditor",
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        });
    }

    private sealed class RowWidgets
    {
        public HBoxContainer Row = null!;
        public LineEdit En = null!;
        public LineEdit Cn = null!;
        public LineEdit Phon = null!;
    }
}
