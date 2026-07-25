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

    // ── 选择题库模式 ──
    private const int MaxChoiceOptions = 5;          // A-E
    private bool _choiceMode;                        // 当前库类型（新建可切换；编辑按库内容判定）
    private CheckButton _choiceModeToggle = null!;
    private Label _headerLabel = null!;

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

    /// <summary>新建词库：空白表单（可切换 单词库 / 选择题库 模式）。</summary>
    public void Open()
    {
        _editingPath = null;
        _titleLabel.Text = "新建词库";
        Visible = true;
        _nameInput.Text = "";
        _descInput.Text = "";
        _choiceMode = false;
        _choiceModeToggle.SetPressedNoSignal(false);
        _choiceModeToggle.Disabled = false;
        _data.Clear();
        for (var i = 0; i < 3; i++) _data.Add(new WordData());
        _page = 0;
        RenderPage();
        _statusLabel.Text = "";
    }

    /// <summary>编辑已有词库：载入名称/描述/全部词条到表单，保存时写回原文件（同 Id 替换、保留进度）。
    /// 单词库与选择题题库均支持（按库内容自动切换行编辑模式）。</summary>
    public void Open(WordBank bank)
    {
        if (bank is null) { Open(); return; }
        _editingPath = bank.SourcePath;
        _titleLabel.Text = $"编辑词库：{bank.Name}";
        Visible = true;
        _nameInput.Text = bank.Name;
        _descInput.Text = bank.Description;

        // 库类型按内容判定并锁定（编辑时不允许切换，防误转丢数据）
        _choiceMode = bank.Words.Any(w => w.IsFixedChoice);
        _choiceModeToggle.SetPressedNoSignal(_choiceMode);
        _choiceModeToggle.Disabled = true;

        _data.Clear();
        foreach (var w in bank.Words)
        {
            if (w.IsFixedChoice)
            {
                var opts = new List<string>(w.Options);
                while (opts.Count < MaxChoiceOptions) opts.Add("");
                _data.Add(new WordData { IsChoice = true, En = w.English, Options = opts, Answer = w.FixedCorrectIndex });
            }
            else
            {
                // 多义项用 "; " 拼回（保存时会按 ';' 拆分还原成数组）
                var cnText = w.Definitions.Count > 0 ? string.Join("; ", w.Definitions) : w.Chinese;
                _data.Add(new WordData { En = w.English, Cn = cnText, Phon = w.Phonetic });
            }
        }
        if (_data.Count == 0) for (var i = 0; i < 3; i++) _data.Add(new WordData { IsChoice = _choiceMode });
        _page = 0;
        RenderPage();
        _statusLabel.Text = _choiceMode
            ? $"共 {_data.Count} 题 · 每页 {PageSize} 行 · 每题：题干 + 选项 + 正确答案下拉。"
            : $"共 {_data.Count} 词 · 每页 {PageSize} 行 · 改完点「保存并启用」写回该词库。";
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

        // 库类型切换（新建时可选；编辑已有库时按内容锁定）
        _choiceModeToggle = new CheckButton { Text = " 选择题题库模式（每行 = 题干 + 选项 + 正确答案）" };
        _choiceModeToggle.AddThemeFontSizeOverride("font_size", 13);
        _choiceModeToggle.Toggled += on =>
        {
            if (_choiceMode == on) return;
            _choiceMode = on;
            // 切换类型：现有行转换为新类型的空行（避免两种行混杂看不懂）
            _data.Clear();
            for (var i = 0; i < 3; i++) _data.Add(new WordData { IsChoice = _choiceMode });
            _page = 0;
            RenderPage();
        };
        vbox.AddChild(_choiceModeToggle);

        vbox.AddChild(new HSeparator());

        // 表头（按库类型动态更新）
        _headerLabel = GameTheme.MakeLabel("", 14, Gold);
        vbox.AddChild(_headerLabel);

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
        _headerLabel.Text = _choiceMode
            ? "每题：题干  |  正确答案(下拉)  |  ✕ 删除；下方 A-E 选项（留空的选项自动忽略）"
            : "英文  |  中文释义 (多个用 ; 分隔)  |  音标 (可选)";
        var total = _data.Count;
        var pageCount = System.Math.Max(1, (total + PageSize - 1) / PageSize);
        _page = System.Math.Clamp(_page, 0, pageCount - 1);
        var start = _page * PageSize;
        var end = System.Math.Min(start + PageSize, total);
        for (var di = start; di < end; di++)
        {
            var d = _data[di];
            if (d.IsChoice) AddChoiceRowWidget(d);
            else AddRowWidget(d.En, d.Cn, d.Phon);
        }
        _pageLabel.Text = $"第 {_page + 1} / {pageCount} 页 · 共 {total} {(_choiceMode ? "题" : "词")}";
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
            var row = _rows[i];
            var d = _data[di];
            d.En = row.En.Text;
            if (row.IsChoice)
            {
                d.Options = row.OptionInputs.Select(o => o.Text).ToList();
                d.Answer = row.AnswerSel?.Selected ?? -1;
            }
            else
            {
                d.Cn = row.Cn?.Text ?? "";
                d.Phon = row.Phon?.Text ?? "";
            }
        }
    }

    /// <summary>末尾追加 n 个空行（按当前库类型）并跳到最后一页。</summary>
    private void AddNewRows(int n)
    {
        FlushPage();
        for (var i = 0; i < n; i++) _data.Add(new WordData { IsChoice = _choiceMode });
        _page = (_data.Count - 1) / PageSize;
        RenderPage();
    }

    /// <summary>行内删除（两种行共用）。</summary>
    private Button MakeDeleteButton(Control row)
    {
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
        return delBtn;
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

        row.AddChild(en);
        row.AddChild(cn);
        row.AddChild(phon);
        row.AddChild(MakeDeleteButton(row));

        _rowsContainer.AddChild(row);
        _rows.Add(new RowWidgets { Row = row, En = en, Cn = cn, Phon = phon });

        // 动态新增的行也要套上游戏字体，否则中文会糊
        GameTheme.ApplyFontRecursive(row);
    }

    /// <summary>选择题行：上行 = 题干 + 正确答案下拉 + 删除；下行 = A-E 选项输入框。</summary>
    private void AddChoiceRowWidget(WordData d)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 4);

        var top = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        top.AddThemeConstantOverride("separation", 8);
        box.AddChild(top);

        var en = new LineEdit
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Text = d.En,
            PlaceholderText = "题干（案例题可用【案例】前缀 + \\n 换行）"
        };
        en.AddThemeFontSizeOverride("font_size", 14);
        top.AddChild(en);

        var ansSel = new OptionButton { CustomMinimumSize = new Vector2(96, 0), TooltipText = "正确答案" };
        for (var i = 0; i < MaxChoiceOptions; i++) ansSel.AddItem($"答案 {(char)('A' + i)}", i);
        ansSel.Selected = d.Answer >= 0 && d.Answer < MaxChoiceOptions ? d.Answer : 0;
        top.AddChild(ansSel);
        top.AddChild(MakeDeleteButton(box));

        var optRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        optRow.AddThemeConstantOverride("separation", 6);
        box.AddChild(optRow);

        var optionInputs = new List<LineEdit>();
        for (var i = 0; i < MaxChoiceOptions; i++)
        {
            var opt = new LineEdit
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Text = i < d.Options.Count ? d.Options[i] : "",
                PlaceholderText = $"{(char)('A' + i)} 选项"
            };
            opt.AddThemeFontSizeOverride("font_size", 13);
            optRow.AddChild(opt);
            optionInputs.Add(opt);
        }

        // 行间小分隔（选择题行较高，视觉分组）
        box.AddChild(new HSeparator());

        _rowsContainer.AddChild(box);
        _rows.Add(new RowWidgets { Row = box, IsChoice = true, En = en, OptionInputs = optionInputs, AnswerSel = ansSel });

        GameTheme.ApplyFontRecursive(box);
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
        var skippedChoice = 0;
        foreach (var r in _data)
        {
            var en = r.En.Trim();
            if (string.IsNullOrEmpty(en)) continue;

            if (r.IsChoice)
            {
                // 选择题行：压实非空选项，答案索引映射到压实后的位置
                var answerText = r.Answer >= 0 && r.Answer < r.Options.Count ? r.Options[r.Answer].Trim() : "";
                var opts = r.Options.Select(o => o.Trim()).Where(o => o.Length > 0).ToList();
                var ans = string.IsNullOrEmpty(answerText) ? -1 : opts.IndexOf(answerText);
                if (opts.Count < 2 || ans < 0) { skippedChoice++; continue; }   // 选项不足或答案指向空选项
                words.Add(new { english = en, chinese = opts[ans], options = opts, answer = ans });
                continue;
            }

            var cn = r.Cn.Trim();
            var phon = r.Phon.Trim();
            if (string.IsNullOrEmpty(cn)) continue;

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

        if (words.Count == 0)
        {
            _statusLabel.Text = _choiceMode
                ? $"没有有效题目（每题需 ≥2 个非空选项且答案指向非空选项；已跳过 {skippedChoice} 题）。"
                : "没有有效单词。";
            return;
        }
        if (!_choiceMode && words.Count < 4)
        {
            _statusLabel.Text = "至少需要 4 个有效单词（选择题干扰项需要）。";
            return;
        }
        if (skippedChoice > 0)
            Log.Warn($"[VocabSpire] 保存题库：{skippedChoice} 题因选项不足/答案无效被跳过。");

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
                // 保持当前多选激活集，把编辑/新建的库并入并重新解析（用新对象重建合并池）
                var ids = new List<string>(VocabConfig.Instance.ActiveBankIds);
                if (!ids.Contains(bank.Id)) ids.Add(bank.Id);
                VocabManager.Instance.SetActiveBanks(ids);
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

    /// <summary>全量数据项（纯字符串，不占 UI 节点，几千词/题也不卡）。</summary>
    private sealed class WordData
    {
        public string En = "";     // 单词 / 选择题题干
        public string Cn = "";
        public string Phon = "";

        // ── 固定选择题行 ──
        public bool IsChoice;
        public List<string> Options = new();   // 最多 MaxChoiceOptions 项（空位表示未填）
        public int Answer = -1;                // 正确选项索引（对应 Options 原始位置）
    }

    private sealed class RowWidgets
    {
        public Control Row = null!;
        public bool IsChoice;
        public LineEdit En = null!;                       // 单词 / 题干
        public LineEdit? Cn;                              // 普通行
        public LineEdit? Phon;                            // 普通行
        public List<LineEdit> OptionInputs = new();       // 选择题行：A-E 选项
        public OptionButton? AnswerSel;                   // 选择题行：正确答案下拉
    }
}
