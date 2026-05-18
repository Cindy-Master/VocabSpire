using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 设置面板 —— 按 F8 打开/关闭。
/// </summary>
public partial class VocabSettingsPanel : Control
{
    public static VocabSettingsPanel? Instance { get; private set; }

    private CheckButton _enableToggle = null!;
    private OptionButton _bankSelector = null!;
    private CheckButton _modeEnToCn = null!;
    private CheckButton _modeCnToEn = null!;
    private CheckButton _modeSpell = null!;
    private CheckButton _modeListen = null!;
    private OptionButton _optionCountSelector = null!;
    private CheckButton _combatSummaryToggle = null!;
    private CheckButton _restReviewToggle = null!;
    private CheckButton _difficultyToggle = null!;
    private Label _bankAnalysisLabel = null!;
    private Label _sampleWordsLabel = null!;
    private Label _statsLabel = null!;
    private Label _templatePathLabel = null!;
    private FileDialog _fileDialog = null!;

    // ── 分层模式 UI ──
    private CheckButton _usePerActToggle = null!;
    private VBoxContainer _perActContainer = null!;
    private VBoxContainer _globalModeContainer = null!;
    private CheckButton[,] _actModeChecks = new CheckButton[3, 4];
    private CheckButton _spellingReviewToggle = null!;

    private static readonly Color Gold = GameTheme.Gold;
    private static readonly Color Bg = GameTheme.DarkBg;
    private static readonly Color White = GameTheme.Cream;
    private static readonly Color Grey = GameTheme.LightGray;
    private static readonly Color DimGrey = GameTheme.MidGray;
    private static readonly Color SectionColor = GameTheme.Gold;

    public override void _Ready()
    {
        Instance = this;
        BuildUI();
        GameTheme.ApplyFontRecursive(this);
        RefreshUI();
        Visible = false;
        ZIndex = 101;
        ProcessMode = ProcessModeEnum.Always;
        Log.Info("[VocabSpire] Settings panel ready.");
    }

    public void ToggleVisible()
    {
        Visible = !Visible;
        if (Visible) RefreshUI();
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

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(780, 0) };
        var style = new StyleBoxFlat
        {
            BgColor = Bg,
            CornerRadiusTopLeft = 16, CornerRadiusTopRight = 16,
            CornerRadiusBottomLeft = 16, CornerRadiusBottomRight = 16,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderColor = Gold,
            ContentMarginTop = 30, ContentMarginBottom = 30,
            ContentMarginLeft = 40, ContentMarginRight = 40
        };
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(700, 620),
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        panel.AddChild(scroll);

        var vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(vbox);

        BuildTitleSection(vbox);
        BuildEnableSection(vbox);
        BuildBankSection(vbox);
        BuildAnalysisSection(vbox);
        BuildQuizSettingsSection(vbox);
        BuildFeatureSection(vbox);
        BuildStatsSection(vbox);
        BuildHelpSection(vbox);
        BuildFileDialog();
    }

    private void BuildTitleSection(VBoxContainer vbox)
    {
        var titleRow = new HBoxContainer();
        vbox.AddChild(titleRow);
        titleRow.AddChild(GameTheme.MakeLabel("VocabSpire \u8BBE\u7F6E", 26, Gold));
        titleRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        // 快捷键选择
        var hotkeyRow = new HBoxContainer();
        hotkeyRow.AddThemeConstantOverride("separation", 6);
        titleRow.AddChild(hotkeyRow);
        hotkeyRow.AddChild(GameTheme.MakeLabel("\u5FEB\u6377\u952E:", 12, Grey));
        var hotkeySelector = new OptionButton { CustomMinimumSize = new Vector2(80, 0) };
        var keys = new[] { Key.F5, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10, Key.F11 };
        var currentIdx = 3; // default F8
        for (var i = 0; i < keys.Length; i++)
        {
            hotkeySelector.AddItem($"F{i + 5}", i);
            if (keys[i] == VocabConfig.Instance.SettingsHotkey) currentIdx = i;
        }
        hotkeySelector.Selected = currentIdx;
        hotkeySelector.ItemSelected += idx =>
        {
            VocabConfig.Instance.SettingsHotkey = keys[idx];
            VocabConfig.Instance.Save();
        };
        hotkeyRow.AddChild(hotkeySelector);

        hotkeyRow.AddChild(GameTheme.MakeLabel(" / Esc \u5173\u95ED", 12, Grey));
        vbox.AddChild(new HSeparator());
    }

    private void BuildEnableSection(VBoxContainer vbox)
    {
        var row = new HBoxContainer();
        vbox.AddChild(row);
        row.AddChild(GameTheme.MakeLabel("启用答题：", 20, White));
        _enableToggle = new CheckButton { ButtonPressed = VocabConfig.Instance.Enabled };
        _enableToggle.Toggled += on =>
        {
            VocabConfig.Instance.Enabled = on;
            VocabConfig.Instance.Save();
        };
        row.AddChild(_enableToggle);
    }

    private void BuildBankSection(VBoxContainer vbox)
    {
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 词库管理 --", 22, SectionColor));

        var bankRow = new HBoxContainer();
        vbox.AddChild(bankRow);
        bankRow.AddChild(GameTheme.MakeLabel("当前词库：", 20, White));
        _bankSelector = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        _bankSelector.ItemSelected += OnBankSelected;
        bankRow.AddChild(_bankSelector);

        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(btnRow);

        var importBtn = GameTheme.MakeButton("  导入词库  ", 14);
        importBtn.Pressed += () => _fileDialog.PopupCentered();
        btnRow.AddChild(importBtn);

        var refreshBtn = GameTheme.MakeButton("  刷新  ", 14);
        refreshBtn.Pressed += () =>
        {
            VocabManager.Instance.LoadAllBanks();
            RefreshUI();
        };
        btnRow.AddChild(refreshBtn);

        var templateBtn = GameTheme.MakeButton("  导出模板  ", 14);
        templateBtn.Pressed += OnExportTemplate;
        btnRow.AddChild(templateBtn);

        _templatePathLabel = GameTheme.MakeLabel("", 16, DimGrey);
        _templatePathLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(_templatePathLabel);
    }

    private void BuildAnalysisSection(VBoxContainer vbox)
    {
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 词库分析 --", 22, SectionColor));

        _bankAnalysisLabel = GameTheme.MakeLabel("", 13, Grey);
        _bankAnalysisLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(_bankAnalysisLabel);

        _sampleWordsLabel = GameTheme.MakeLabel("", 12, DimGrey);
        _sampleWordsLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(_sampleWordsLabel);
    }

    private void BuildQuizSettingsSection(VBoxContainer vbox)
    {
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 答题模式 --", 22, SectionColor));

        var cfg = VocabConfig.Instance;

        // 分层模式开关
        _usePerActToggle = new CheckButton
        {
            Text = " 按层(Act)独立配置答题模式",
            ButtonPressed = cfg.UsePerActModes
        };
        _usePerActToggle.Toggled += on =>
        {
            VocabConfig.Instance.UsePerActModes = on;
            VocabConfig.Instance.Save();
            _globalModeContainer.Visible = !on;
            _perActContainer.Visible = on;
        };
        vbox.AddChild(_usePerActToggle);

        // 全局模式区域（分层关闭时显示）
        _globalModeContainer = new VBoxContainer();
        _globalModeContainer.AddThemeConstantOverride("separation", 4);
        _globalModeContainer.Visible = !cfg.UsePerActModes;
        vbox.AddChild(_globalModeContainer);

        _modeEnToCn = new CheckButton { Text = " 英 → 中 (选择题)", ButtonPressed = cfg.QuizModes.HasFlag(QuizModeFlags.EnglishToChinese) };
        _modeEnToCn.Toggled += _ => SaveQuizModes();
        _globalModeContainer.AddChild(_modeEnToCn);

        _modeCnToEn = new CheckButton { Text = " 中 → 英 (选择题)", ButtonPressed = cfg.QuizModes.HasFlag(QuizModeFlags.ChineseToEnglish) };
        _modeCnToEn.Toggled += _ => SaveQuizModes();
        _globalModeContainer.AddChild(_modeCnToEn);

        _modeSpell = new CheckButton { Text = " 中 → 英 (拼写)", ButtonPressed = cfg.QuizModes.HasFlag(QuizModeFlags.SpellEnglish) };
        _modeSpell.Toggled += _ => SaveQuizModes();
        _globalModeContainer.AddChild(_modeSpell);

        _modeListen = new CheckButton { Text = " \uD83D\uDD0A \u542C\u529B\u6A21\u5F0F (\u542C\u53D1\u97F3\u9009\u91CA\u4E49)", ButtonPressed = cfg.QuizModes.HasFlag(QuizModeFlags.ListenToChinese) };
        _modeListen.Toggled += _ => SaveQuizModes();
        _globalModeContainer.AddChild(_modeListen);

        // 分层模式区域（分层开启时显示）
        _perActContainer = new VBoxContainer();
        _perActContainer.AddThemeConstantOverride("separation", 6);
        _perActContainer.Visible = cfg.UsePerActModes;
        vbox.AddChild(_perActContainer);

        var actNames = new[] { "Act 1 (基础)", "Act 2 (进阶)", "Act 3 (挑战)" };
        var actModes = new[] { cfg.Act1Modes, cfg.Act2Modes, cfg.Act3Modes };
        var modeLabels = new[] { "\u82F1\u2192\u4E2D", "\u4E2D\u2192\u82F1", "\u62FC\u5199", "\u542C\u529B" };

        for (var act = 0; act < 3; act++)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            _perActContainer.AddChild(row);
            row.AddChild(GameTheme.MakeLabel($"{actNames[act]}:", 13, White));

            for (var m = 0; m < 4; m++)
            {
                var flag = (QuizModeFlags)(1 << m);
                var cb = new CheckButton
                {
                    Text = $" {modeLabels[m]}",
                    ButtonPressed = actModes[act].HasFlag(flag)
                };
                cb.AddThemeFontSizeOverride("font_size", 12);
                var actIdx = act;
                cb.Toggled += _ => SavePerActModes(actIdx);
                row.AddChild(cb);
                _actModeChecks[act, m] = cb;
            }
        }

        // 拼写复习开关
        _spellingReviewToggle = new CheckButton
        {
            Text = " 拼写模式仅复习已测词 (Act 2+)",
            ButtonPressed = cfg.SpellingReviewOnly
        };
        _spellingReviewToggle.AddThemeFontSizeOverride("font_size", 13);
        _spellingReviewToggle.Toggled += on =>
        {
            VocabConfig.Instance.SpellingReviewOnly = on;
            VocabConfig.Instance.Save();
        };
        _perActContainer.AddChild(_spellingReviewToggle);

        var reviewDesc = GameTheme.MakeLabel(
            "开启后 Act 2+ 的拼写题只从本局已出过的词中选取", 16, DimGrey);
        _perActContainer.AddChild(reviewDesc);

        // 难度设置
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 难度设置 --", 22, SectionColor));

        var countRow = new HBoxContainer();
        vbox.AddChild(countRow);
        countRow.AddChild(GameTheme.MakeLabel("选项数：", 20, White));
        _optionCountSelector = new OptionButton { CustomMinimumSize = new Vector2(100, 0) };
        for (var i = 2; i <= 6; i++)
            _optionCountSelector.AddItem($"{i}", i);
        _optionCountSelector.Selected = cfg.OptionCount - 2;
        _optionCountSelector.ItemSelected += idx =>
        {
            VocabConfig.Instance.OptionCount = (int)idx + 2;
            VocabConfig.Instance.Save();
        };
        countRow.AddChild(_optionCountSelector);
        countRow.AddChild(GameTheme.MakeLabel("  (2=简单, 6=困难)", 12, DimGrey));
    }

    private void BuildFeatureSection(VBoxContainer vbox)
    {
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 功能设置 --", 22, SectionColor));

        var cfg = VocabConfig.Instance;

        _combatSummaryToggle = new CheckButton { Text = " 战斗结束后显示错题回顾", ButtonPressed = cfg.ShowCombatSummary };
        _combatSummaryToggle.Toggled += on =>
        {
            VocabConfig.Instance.ShowCombatSummary = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(_combatSummaryToggle);

        _restReviewToggle = new CheckButton { Text = " 篝火时复习错题", ButtonPressed = cfg.ShowRestSiteReview };
        _restReviewToggle.Toggled += on =>
        {
            VocabConfig.Instance.ShowRestSiteReview = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(_restReviewToggle);

        _difficultyToggle = new CheckButton { Text = " 按层数递增难度 (基础→进阶→挑战)", ButtonPressed = cfg.EnableDifficultyScaling };
        _difficultyToggle.Toggled += on =>
        {
            VocabConfig.Instance.EnableDifficultyScaling = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(_difficultyToggle);

        var diffDesc = GameTheme.MakeLabel(
            "第一层：随机干扰项，显示音标\n" +
            "第二层：相似混淆干扰项，+1选项，隐藏音标，40%强制拼写\n" +
            "第三层：高混淆度干扰项，+2选项，70%强制拼写，随机反转模式",
            11, DimGrey);
        diffDesc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(diffDesc);

        // ── 篝火复习设置 ──
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 篝火复习设置 --", 22, SectionColor));

        var reviewModeRow = new HBoxContainer();
        reviewModeRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(reviewModeRow);
        reviewModeRow.AddChild(GameTheme.MakeLabel("复习模式：", 18, White));

        var reviewModeSelector = new OptionButton { CustomMinimumSize = new Vector2(180, 0) };
        reviewModeSelector.AddItem("英 \u2192 中", 0);
        reviewModeSelector.AddItem("中 \u2192 英", 1);
        reviewModeSelector.AddItem("拼写", 2);
        var currentReviewMode = cfg.ReviewQuizMode switch
        {
            QuizModeFlags.ChineseToEnglish => 1,
            QuizModeFlags.SpellEnglish => 2,
            _ => 0
        };
        reviewModeSelector.Selected = currentReviewMode;
        reviewModeSelector.ItemSelected += idx =>
        {
            VocabConfig.Instance.ReviewQuizMode = idx switch
            {
                1 => QuizModeFlags.ChineseToEnglish,
                2 => QuizModeFlags.SpellEnglish,
                _ => QuizModeFlags.EnglishToChinese
            };
            VocabConfig.Instance.Save();
        };
        reviewModeRow.AddChild(reviewModeSelector);

        var reviewCountRow = new HBoxContainer();
        reviewCountRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(reviewCountRow);
        reviewCountRow.AddChild(GameTheme.MakeLabel("\u590D\u4E60\u9898\u6570\uFF1A", 18, White));

        var reviewCountInput = new SpinBox
        {
            MinValue = 0,
            MaxValue = 99,
            Step = 1,
            Value = cfg.ReviewMaxCount,
            CustomMinimumSize = new Vector2(100, 0)
        };
        reviewCountInput.GetLineEdit().AddThemeFontSizeOverride("font_size", 14);
        reviewCountInput.ValueChanged += val =>
        {
            VocabConfig.Instance.ReviewMaxCount = (int)val;
            VocabConfig.Instance.Save();
        };
        reviewCountRow.AddChild(reviewCountInput);
        reviewCountRow.AddChild(GameTheme.MakeLabel("  (0 = \u5168\u90E8\u9519\u9898)", 12, DimGrey));

        // 掌握阈值
        var masteryRow = new HBoxContainer();
        masteryRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(masteryRow);
        masteryRow.AddChild(GameTheme.MakeLabel("\u638C\u63E1\u9600\u503C\uFF1A", 18, White));

        var masteryInput = new SpinBox
        {
            MinValue = 1,
            MaxValue = 20,
            Step = 1,
            Value = cfg.MasteryStreak,
            CustomMinimumSize = new Vector2(100, 0)
        };
        masteryInput.GetLineEdit().AddThemeFontSizeOverride("font_size", 14);
        masteryInput.ValueChanged += val =>
        {
            VocabConfig.Instance.MasteryStreak = (int)val;
            VocabConfig.Instance.Save();
        };
        masteryRow.AddChild(masteryInput);
        masteryRow.AddChild(GameTheme.MakeLabel("  (\u8FDE\u7EED\u7B54\u5BF9\u6B21\u6570)", 16, DimGrey));

        // 听力音量
        var volRow = new HBoxContainer();
        volRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(volRow);
        volRow.AddChild(GameTheme.MakeLabel("\u542C\u529B\u97F3\u91CF\uFF1A", 18, White));

        var volSlider = new HSlider
        {
            MinValue = 0,
            MaxValue = 100,
            Step = 5,
            Value = cfg.TtsVolume,
            CustomMinimumSize = new Vector2(200, 0)
        };
        volSlider.ValueChanged += val =>
        {
            VocabConfig.Instance.TtsVolume = (int)val;
            VocabConfig.Instance.Save();
        };
        volRow.AddChild(volSlider);

        var volLabel = GameTheme.MakeLabel($"{cfg.TtsVolume}%", 16, Grey);
        volRow.AddChild(volLabel);
        volSlider.ValueChanged += val => volLabel.Text = $"{(int)val}%";
    }

    private void BuildStatsSection(VBoxContainer vbox)
    {
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 统计 --", 22, SectionColor));

        _statsLabel = GameTheme.MakeLabel("", 14, Grey);
        vbox.AddChild(_statsLabel);
    }

    private void BuildHelpSection(VBoxContainer vbox)
    {
        vbox.AddChild(new HSeparator());
        var help = GameTheme.MakeLabel(
            "将 .json 或 .csv 词库文件放入 mods/VocabSpire/wordbanks/ 目录后点击刷新。\n" +
            "也可以点击「导入词库」直接选择文件导入。\n" +
            "点击「导出模板」可获取 JSON 词库模板文件。\n" +
            "JSON 格式支持 \"chinese\" 为字符串或字符串数组（多释义）。",
            11, DimGrey);
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(help);
    }

    private void BuildFileDialog()
    {
        _fileDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "导入词库",
            Size = new Vector2I(800, 500)
        };
        _fileDialog.AddFilter("*.json", "JSON 词库文件");
        _fileDialog.AddFilter("*.csv", "CSV 词库文件");
        _fileDialog.FileSelected += path =>
        {
            var bank = VocabManager.Instance.ImportBank(path);
            if (bank is not null)
            {
                VocabManager.Instance.SetActiveBank(bank.Id);
                RefreshUI();
            }
        };
        AddChild(_fileDialog);
    }

    private void SaveQuizModes()
    {
        var flags = QuizModeFlags.None;
        if (_modeEnToCn.ButtonPressed) flags |= QuizModeFlags.EnglishToChinese;
        if (_modeCnToEn.ButtonPressed) flags |= QuizModeFlags.ChineseToEnglish;
        if (_modeSpell.ButtonPressed) flags |= QuizModeFlags.SpellEnglish;
        if (_modeListen.ButtonPressed) flags |= QuizModeFlags.ListenToChinese;

        // 至少保留一个模式
        if (flags == QuizModeFlags.None)
        {
            flags = QuizModeFlags.EnglishToChinese;
            _modeEnToCn.ButtonPressed = true;
        }

        VocabConfig.Instance.QuizModes = flags;
        VocabConfig.Instance.Save();
    }

    private void SavePerActModes(int actIndex)
    {
        var flags = QuizModeFlags.None;
        for (var m = 0; m < 4; m++)
        {
            if (_actModeChecks[actIndex, m].ButtonPressed)
                flags |= (QuizModeFlags)(1 << m);
        }

        // 至少保留一个模式
        if (flags == QuizModeFlags.None)
        {
            flags = QuizModeFlags.EnglishToChinese;
            _actModeChecks[actIndex, 0].ButtonPressed = true;
        }

        var cfg = VocabConfig.Instance;
        switch (actIndex)
        {
            case 0: cfg.Act1Modes = flags; break;
            case 1: cfg.Act2Modes = flags; break;
            case 2: cfg.Act3Modes = flags; break;
        }
        cfg.Save();
    }

    private void RefreshUI()
    {
        _bankSelector.Clear();
        var banks = VocabManager.Instance.Banks;
        var selectedIdx = 0;

        for (var i = 0; i < banks.Count; i++)
        {
            _bankSelector.AddItem($"{banks[i].Name} ({banks[i].TotalWords} 词)", i);
            if (banks[i].Id == VocabConfig.Instance.ActiveBankId)
                selectedIdx = i;
        }
        if (banks.Count > 0)
            _bankSelector.Selected = selectedIdx;

        RefreshBankAnalysis();

        var cfg = VocabConfig.Instance;
        var pct = cfg.TotalAnswered > 0 ? $"{cfg.OverallAccuracy:P0}" : "--";
        _statsLabel.Text = $"总答题：{cfg.TotalAnswered}  |  正确：{cfg.TotalCorrect}  |  正确率：{pct}";
    }

    private void RefreshBankAnalysis()
    {
        var bank = VocabManager.Instance.ActiveBank;
        if (bank is null)
        {
            _bankAnalysisLabel.Text = "未加载词库。请导入或将文件放入 wordbanks/ 目录。";
            _sampleWordsLabel.Text = "";
            return;
        }

        _bankAnalysisLabel.Text =
            $"名称：{bank.Name}\n" +
            $"描述：{bank.Description}\n" +
            $"单词总数：{bank.TotalWords}\n" +
            $"含音标：{bank.WordsWithPhonetic}/{bank.TotalWords}\n" +
            $"多释义：{bank.WordsWithMultiDefs}/{bank.TotalWords}";

        var samples = bank.Words.Take(5)
            .Select(w =>
            {
                var phon = w.HasPhonetic ? $" {w.Phonetic}" : "";
                var defs = w.HasMultipleDefinitions
                    ? string.Join("; ", w.Definitions)
                    : w.Chinese;
                return $"  {w.English}{phon} - {defs}";
            });
        _sampleWordsLabel.Text = "示例：\n" + string.Join("\n", samples);
    }

    private void OnBankSelected(long index)
    {
        var banks = VocabManager.Instance.Banks;
        if (index >= 0 && index < banks.Count)
        {
            VocabManager.Instance.SetActiveBank(banks[(int)index].Id);
            RefreshBankAnalysis();
        }
    }

    private void OnExportTemplate()
    {
        try
        {
            var path = VocabManager.Instance.ExportTemplate();
            _templatePathLabel.Text = $"模板已保存：{path}";
        }
        catch (Exception ex)
        {
            _templatePathLabel.Text = $"导出失败：{ex.Message}";
        }
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
        root.AddChild(new VocabSettingsPanel
        {
            Name = "VocabSpireSettings",
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        });
    }
}
