using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;
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
    private Label _titleLabel = null!;

    /// <summary>编辑模式：保存写回的原文件路径；null = 新建。</summary>
    private string? _editingPath;

    private readonly List<RowWidgets> _rows = new();

    // ── 分页（避免大词库一次性建几千行 UI 卡死；每页只渲染 PageSize 行）──
    private const int PageSize = 50;
    private readonly List<WordData> _data = new();   // 全量数据（纯字符串，不占 UI 节点）
    private int _page;
    private Label _pageLabel = null!;
    private Button _prevBtn = null!;
    private Button _nextBtn = null!;

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

    /// <summary>新建词库：空白表单。</summary>
    public void Open()
    {
        _editingPath = null;
        _titleLabel.Text = "新建词库";
        Visible = true;
        _nameInput.Text = "";
        _descInput.Text = "";
        _data.Clear();
        for (var i = 0; i < 3; i++) _data.Add(new WordData());
        _page = 0;
        RenderPage();
        _statusLabel.Text = "";
    }

    /// <summary>编辑已有词库：载入名称/描述/全部词条到表单，保存时写回原文件（同 Id 替换、保留进度）。</summary>
    public void Open(WordBank bank)
    {
        if (bank is null) { Open(); return; }
        _editingPath = bank.SourcePath;
        _titleLabel.Text = $"编辑词库：{bank.Name}";
        Visible = true;
        _nameInput.Text = bank.Name;
        _descInput.Text = bank.Description;
        _data.Clear();
        foreach (var w in bank.Words)
        {
            // 多义项用 "; " 拼回（保存时会按 ';' 拆分还原成数组）
            var cnText = w.Definitions.Count > 0 ? string.Join("; ", w.Definitions) : w.Chinese;
            _data.Add(new WordData { En = w.English, Cn = cnText, Phon = w.Phonetic });
        }
        if (_data.Count == 0) for (var i = 0; i < 3; i++) _data.Add(new WordData());
        _page = 0;
        RenderPage();
        _statusLabel.Text = $"共 {_data.Count} 词 · 每页 {PageSize} 行 · 改完点「保存并启用」写回该词库。";
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

        _titleLabel = GameTheme.MakeLabel("新建词库", 24, Gold);
        vbox.AddChild(_titleLabel);
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

        // 翻页
        var pageRow = new HBoxContainer();
        pageRow.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(pageRow);
        _prevBtn = GameTheme.MakeButton("  ◀ 上一页  ", 14);
        _prevBtn.Pressed += () => { FlushPage(); _page--; RenderPage(); };
        pageRow.AddChild(_prevBtn);
        _pageLabel = GameTheme.MakeLabel("", 15, Cream);
        pageRow.AddChild(_pageLabel);
        _nextBtn = GameTheme.MakeButton("  下一页 ▶  ", 14);
        _nextBtn.Pressed += () => { FlushPage(); _page++; RenderPage(); };
        pageRow.AddChild(_nextBtn);

        // 操作按钮
        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(btnRow);

        var addBtn = GameTheme.MakeButton("  添加一行  ", 14);
        addBtn.Pressed += () => AddNewRows(1);
        btnRow.AddChild(addBtn);

        var addManyBtn = GameTheme.MakeButton("  +10 行  ", 14);
        addManyBtn.Pressed += () => AddNewRows(10);
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

    /// <summary>渲染当前页：只为「当前页的数据切片」建 UI 行，其余仅存在 _data 里。</summary>
    private void RenderPage()
    {
        ClearRows();
        var total = _data.Count;
        var pageCount = System.Math.Max(1, (total + PageSize - 1) / PageSize);
        _page = System.Math.Clamp(_page, 0, pageCount - 1);
        var start = _page * PageSize;
        var end = System.Math.Min(start + PageSize, total);
        for (var di = start; di < end; di++)
        {
            var d = _data[di];
            AddRowWidget(d.En, d.Cn, d.Phon);
        }
        _pageLabel.Text = $"第 {_page + 1} / {pageCount} 页 · 共 {total} 词";
        _prevBtn.Disabled = _page <= 0;
        _nextBtn.Disabled = _page >= pageCount - 1;
    }

    /// <summary>把当前页 UI 行的文本写回 _data（翻页 / 增删 / 保存前必调，防丢失编辑）。</summary>
    private void FlushPage()
    {
        var start = _page * PageSize;
        for (var i = 0; i < _rows.Count; i++)
        {
            var di = start + i;
            if (di >= _data.Count) break;
            _data[di].En = _rows[i].En.Text;
            _data[di].Cn = _rows[i].Cn.Text;
            _data[di].Phon = _rows[i].Phon.Text;
        }
    }

    /// <summary>末尾追加 n 个空行并跳到最后一页。</summary>
    private void AddNewRows(int n)
    {
        FlushPage();
        for (var i = 0; i < n; i++) _data.Add(new WordData());
        _page = (_data.Count - 1) / PageSize;
        RenderPage();
    }

    private void AddRowWidget(string enText, string cnText, string phonText)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);

        var en = new LineEdit { CustomMinimumSize = new Vector2(180, 0), Text = enText };
        en.AddThemeFontSizeOverride("font_size", 14);

        var cn = new LineEdit { CustomMinimumSize = new Vector2(320, 0), Text = cnText };
        cn.AddThemeFontSizeOverride("font_size", 14);

        var phon = new LineEdit { CustomMinimumSize = new Vector2(160, 0), Text = phonText };
        phon.AddThemeFontSizeOverride("font_size", 14);

        var delBtn = new Button { Text = " ✕ ", CustomMinimumSize = new Vector2(36, 0) };
        delBtn.AddThemeFontSizeOverride("font_size", 14);
        delBtn.Pressed += () =>
        {
            FlushPage();
            var pos = _rows.FindIndex(r => r.Row == row);
            if (pos < 0) return;
            var di = _page * PageSize + pos;
            if (di < _data.Count) _data.RemoveAt(di);
            RenderPage();
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

        FlushPage();   // 先把当前页的编辑写回数据，再从全量 _data 收集
        var words = new List<object>();
        foreach (var r in _data)
        {
            var en = r.En.Trim();
            var cn = r.Cn.Trim();
            var phon = r.Phon.Trim();
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

            // 编辑模式写回原文件（同 Id 替换）；新建则按名称生成文件名。
            var path = !string.IsNullOrEmpty(_editingPath)
                ? _editingPath!
                : Path.Combine(VocabManager.Instance.GetWordBanksDirectory(), SanitizeFileName(name) + ".json");
            File.WriteAllText(path, json);

            var bank = VocabManager.Instance.ImportBank(path);
            if (bank is not null)
            {
                // 编辑后重新绑定进度，避免重解析出的新词条把已有掌握度归零。
                VocabManager.Instance.LoadProgress();
                VocabManager.Instance.SetActiveBank(bank.Id);
            }
            _statusLabel.Text = $"已保存: {path}";
            Log.Info($"[VocabSpire] Saved bank ({(_editingPath is null ? "new" : "edit")}): {path}");

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

    /// <summary>全量数据项（纯字符串，不占 UI 节点，几千词也不卡）。</summary>
    private sealed class WordData
    {
        public string En = "";
        public string Cn = "";
        public string Phon = "";
    }

    private sealed class RowWidgets
    {
        public HBoxContainer Row = null!;
        public LineEdit En = null!;
        public LineEdit Cn = null!;
        public LineEdit Phon = null!;
    }
}
