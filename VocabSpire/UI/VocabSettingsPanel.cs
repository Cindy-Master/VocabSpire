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
    private VBoxContainer _bankChecksContainer = null!;
    private CheckButton _modeEnToCn = null!;
    private CheckButton _modeCnToEn = null!;
    private CheckButton _modeSpell = null!;
    private CheckButton _modeListen = null!;
    private OptionButton _optionCountSelector = null!;
    private CheckButton _combatSummaryToggle = null!;
    private CheckButton _restReviewToggle = null!;
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

    /// <summary>由 WordBankEditorPanel 新建词库后调用，刷新词库下拉。</summary>
    public void NotifyBanksChanged() => RefreshUI();

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
        BuildBattleSection(vbox);
        BuildRewardSection(vbox);
        BuildPunishmentSection(vbox);
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
        var hotkeyBind = new KeyBindButton();
        hotkeyBind.Setup(VocabConfig.Instance.SettingsHotkey, k =>
        {
            VocabConfig.Instance.SettingsHotkey = k;
            VocabConfig.Instance.Save();
        }, k => VocabConfig.CheckKeyConflict(BindAction.OpenSettings, k));
        hotkeyRow.AddChild(hotkeyBind);

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

        // 自动打出的牌是否也做题（回合开始遗物/能力自动打出等）
        var autoRow = new HBoxContainer();
        vbox.AddChild(autoRow);
        var autoToggle = new CheckButton
        {
            Text = " 自动打出的牌也做题",
            ButtonPressed = VocabConfig.Instance.QuizOnAutoPlay
        };
        autoToggle.AddThemeFontSizeOverride("font_size", 14);
        autoToggle.Toggled += on =>
        {
            VocabConfig.Instance.QuizOnAutoPlay = on;
            VocabConfig.Instance.Save();
        };
        autoRow.AddChild(autoToggle);
        vbox.AddChild(GameTheme.MakeLabel(
            "（关掉后，回合开始遗物/能力等自动打出的牌不弹题、直接生效）", 14, DimGrey));
    }

    private void BuildBankSection(VBoxContainer vbox)
    {
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 词库管理 --", 22, SectionColor));

        vbox.AddChild(GameTheme.MakeLabel("激活词库（可多选，合并去重出题）：", 20, White));
        var bankScroll = new ScrollContainer { CustomMinimumSize = new Vector2(380, 132) };
        _bankChecksContainer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _bankChecksContainer.AddThemeConstantOverride("separation", 2);
        bankScroll.AddChild(_bankChecksContainer);
        vbox.AddChild(bankScroll);

        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(btnRow);

        var importBtn = GameTheme.MakeButton("  导入词库  ", 14);
        importBtn.Pressed += () => _fileDialog.PopupCentered();
        btnRow.AddChild(importBtn);

        var newBankBtn = GameTheme.MakeButton("  新建词库  ", 14);
        newBankBtn.Pressed += () => WordBankEditorPanel.Instance?.Open();
        btnRow.AddChild(newBankBtn);

        var editBankBtn = GameTheme.MakeButton("  编辑词库  ", 14);
        editBankBtn.Pressed += () =>
        {
            var active = VocabManager.Instance.ActiveBanks;
            if (active.Count == 0) return;
            if (active.Count == 1) { WordBankEditorPanel.Instance?.Open(active[0]); return; }
            // 懒创建 PopupMenu —— 绝不能在面板构造时创建：PopupMenu 是内嵌子窗口，
            // 启动时主窗口若无焦点，构造子窗口会原生崩溃。按下时窗口必有焦点 → 安全。
            var popup = new PopupMenu { ProcessMode = ProcessModeEnum.Always };
            for (var i = 0; i < active.Count; i++)
                popup.AddItem($"{active[i].Name} ({active[i].TotalWords} 词)", i);
            popup.IdPressed += id =>
            {
                var banks = VocabManager.Instance.ActiveBanks;
                if (id >= 0 && id < banks.Count) WordBankEditorPanel.Instance?.Open(banks[(int)id]);
            };
            popup.PopupHide += () => popup.QueueFree();   // 关闭即释放，不常驻
            editBankBtn.AddChild(popup);
            popup.ResetSize();
            popup.Position = (Vector2I)editBankBtn.GetScreenPosition() + new Vector2I(0, (int)editBankBtn.Size.Y);
            popup.Popup();
        };
        btnRow.AddChild(editBankBtn);

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

        // 答题按键（自定义提交 / 继续键；选项键固定 A-H / 1-8）
        vbox.AddChild(GameTheme.MakeLabel("-- 答题按键 --", 22, SectionColor));

        var submitRow = new HBoxContainer();
        submitRow.AddThemeConstantOverride("separation", 8);
        submitRow.AddChild(GameTheme.MakeLabel("提交答案:", 14, White));
        var submitBind = new KeyBindButton();
        submitBind.Setup(VocabConfig.Instance.SubmitKey, k =>
        {
            VocabConfig.Instance.SubmitKey = k;
            VocabConfig.Instance.Save();
        }, k => VocabConfig.CheckKeyConflict(BindAction.Submit, k));
        submitRow.AddChild(submitBind);
        vbox.AddChild(submitRow);

        var continueRow = new HBoxContainer();
        continueRow.AddThemeConstantOverride("separation", 8);
        continueRow.AddChild(GameTheme.MakeLabel("下一题 / 继续:", 14, White));
        var continueBind = new KeyBindButton();
        continueBind.Setup(VocabConfig.Instance.ContinueKey, k =>
        {
            VocabConfig.Instance.ContinueKey = k;
            VocabConfig.Instance.Save();
        }, k => VocabConfig.CheckKeyConflict(BindAction.Continue, k));
        continueRow.AddChild(continueBind);
        vbox.AddChild(continueRow);

        vbox.AddChild(GameTheme.MakeLabel("（选项键固定为 A-H / 1-8，不可改）", 11, Grey));

        // 拼写设置（始终可见，不随分层开关变化）
        vbox.AddChild(GameTheme.MakeLabel("-- 拼写设置 --", 22, SectionColor));

        var spellAudioToggle = new CheckButton
        {
            Text = " 🔊 拼写题显示朗读按钮（点击播放发音）",
            ButtonPressed = cfg.SpellingPlayAudio
        };
        spellAudioToggle.AddThemeFontSizeOverride("font_size", 13);
        spellAudioToggle.Toggled += on =>
        {
            VocabConfig.Instance.SpellingPlayAudio = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(spellAudioToggle);

        var enToCnAudioToggle = new CheckButton
        {
            Text = " 🔊 英→中选择题显示朗读按钮（点击播放发音）",
            ButtonPressed = cfg.EnToCnPlayAudio
        };
        enToCnAudioToggle.AddThemeFontSizeOverride("font_size", 13);
        enToCnAudioToggle.Toggled += on =>
        {
            VocabConfig.Instance.EnToCnPlayAudio = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(enToCnAudioToggle);

        var spellEasyToggle = new CheckButton
        {
            Text = " 拼写简单模式（中间挖空填字，挖空数按字母数）",
            ButtonPressed = cfg.SpellingEasyMode
        };
        spellEasyToggle.AddThemeFontSizeOverride("font_size", 13);
        spellEasyToggle.Toggled += on =>
        {
            VocabConfig.Instance.SpellingEasyMode = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(spellEasyToggle);

        var optionAudioToggle = new CheckButton
        {
            Text = " 🔊 中→英题每个英文选项显示发音小喇叭",
            ButtonPressed = cfg.OptionPlayAudio
        };
        optionAudioToggle.AddThemeFontSizeOverride("font_size", 13);
        optionAudioToggle.Toggled += on =>
        {
            VocabConfig.Instance.OptionPlayAudio = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(optionAudioToggle);

        var multiSelectToggle = new CheckButton
        {
            Text = " 出多选题（多义词有概率变多选；关掉则只出单选）",
            ButtonPressed = cfg.EnableMultiSelect
        };
        multiSelectToggle.AddThemeFontSizeOverride("font_size", 13);
        multiSelectToggle.Toggled += on =>
        {
            VocabConfig.Instance.EnableMultiSelect = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(multiSelectToggle);

        vbox.AddChild(GameTheme.MakeLabel(
            "简单模式：显示如 \"c _ _ e\" 的提示，仍需输入完整单词；困难模式：仅给中文释义从零拼写", 16, DimGrey));

        // 难度设置
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 难度设置 --", 22, SectionColor));

        var countRow = new HBoxContainer();
        vbox.AddChild(countRow);
        countRow.AddChild(GameTheme.MakeLabel("选项数：", 20, White));
        _optionCountSelector = new OptionButton { CustomMinimumSize = new Vector2(100, 0) };
        for (var i = 2; i <= 6; i++)
            _optionCountSelector.AddItem($"{i}", i);
        _optionCountSelector.Selected = Math.Clamp(cfg.OptionCount, 2, 6) - 2;
        _optionCountSelector.ItemSelected += idx =>
        {
            VocabConfig.Instance.OptionCount = (int)idx + 2;
            VocabConfig.Instance.Save();
        };
        countRow.AddChild(_optionCountSelector);
        countRow.AddChild(GameTheme.MakeLabel("  (2=简单, 6=困难；启用选项递增后 Act3 自动 +2，最多 8)", 12, DimGrey));
    }

    private void BuildBattleSection(VBoxContainer vbox)
    {
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 战斗惩罚设置 --", 22, SectionColor));

        var cfg = VocabConfig.Instance;

        // ── 总开关：答错跳过卡牌效果 ──
        var skipEffectToggle = new CheckButton
        {
            Text = " 答错跳过卡牌效果（同时影响下面的容错和回手互斥选项）",
            ButtonPressed = cfg.WrongAnswerSkipEffect
        };
        skipEffectToggle.AddThemeFontSizeOverride("font_size", 14);
        skipEffectToggle.TooltipText =
            "开启（默认）：答错时该卡牌效果跳过，能量照扣，按下方互斥选项进弃牌堆或回手；容错可生效。\n" +
            "关闭：答错时卡牌照常生效，不强制 NoCost/回手，容错也不触发。答错的代价改靠下方「答错惩罚」承担。";
        vbox.AddChild(skipEffectToggle);

        // 容错总开关
        var tolEnableToggle = new CheckButton
        {
            Text = " 启用每回合容错（答错不扣费且回手）",
            ButtonPressed = cfg.ToleranceEnabled
        };
        tolEnableToggle.AddThemeFontSizeOverride("font_size", 14);
        tolEnableToggle.Disabled = !cfg.WrongAnswerSkipEffect;
        vbox.AddChild(tolEnableToggle);

        // 容错次数（受开关控制）
        var tolRow = new HBoxContainer();
        tolRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(tolRow);
        tolRow.AddChild(GameTheme.MakeLabel("    容错次数：", 16, White));

        var tolInput = new SpinBox
        {
            MinValue = 1,
            MaxValue = 10,
            Step = 1,
            Value = Math.Max(1, cfg.ToleranceCount),
            CustomMinimumSize = new Vector2(100, 0),
            Editable = cfg.ToleranceEnabled && cfg.WrongAnswerSkipEffect
        };
        tolInput.GetLineEdit().AddThemeFontSizeOverride("font_size", 14);
        tolInput.ValueChanged += val =>
        {
            VocabConfig.Instance.ToleranceCount = (int)val;
            VocabConfig.Instance.Save();
        };
        tolRow.AddChild(tolInput);
        tolRow.AddChild(GameTheme.MakeLabel("  (每回合前 X 张答错牌免惩罚)", 12, DimGrey));

        tolEnableToggle.Toggled += on =>
        {
            VocabConfig.Instance.ToleranceEnabled = on;
            VocabConfig.Instance.Save();
            tolInput.Editable = on && VocabConfig.Instance.WrongAnswerSkipEffect;
        };

        // 答错处理（互斥选项）
        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(actionRow);
        actionRow.AddChild(GameTheme.MakeLabel("答错时卡牌：", 18, White));

        var actionSelector = new OptionButton
        {
            CustomMinimumSize = new Vector2(220, 0),
            Disabled = !cfg.WrongAnswerSkipEffect
        };
        actionSelector.AddItem("扣费 + 进弃牌堆 (默认)", 0);
        actionSelector.AddItem("扣费 + 返回手牌", 1);
        actionSelector.Selected = cfg.WrongCardReturnToHand ? 1 : 0;
        actionSelector.ItemSelected += idx =>
        {
            VocabConfig.Instance.WrongCardReturnToHand = idx == 1;
            VocabConfig.Instance.Save();
        };
        actionRow.AddChild(actionSelector);

        // 总开关 toggle 联动其余 UI 置灰
        skipEffectToggle.Toggled += on =>
        {
            VocabConfig.Instance.WrongAnswerSkipEffect = on;
            VocabConfig.Instance.Save();
            tolEnableToggle.Disabled = !on;
            tolInput.Editable = on && VocabConfig.Instance.ToleranceEnabled;
            actionSelector.Disabled = !on;
        };

        var battleDesc = GameTheme.MakeLabel(
            "说明：容错开启时优先生效（不扣费 + 回手）；超出容错次数后按上方选项处理。\n" +
            "若关闭「答错跳过卡牌效果」，以上容错和互斥选项失效，答错时卡牌正常生效，靠下方「答错惩罚」体现代价。",
            11, DimGrey);
        battleDesc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(battleDesc);
    }

    private VBoxContainer _rewardRulesContainer = null!;

    private void BuildRewardSection(VBoxContainer vbox)
    {
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 连对奖励设置 --", 22, SectionColor));

        var cfg = VocabConfig.Instance;

        var enableToggle = new CheckButton
        {
            Text = " 启用连续答对奖励（总开关）",
            ButtonPressed = cfg.RewardEnabled
        };
        enableToggle.AddThemeFontSizeOverride("font_size", 14);
        enableToggle.Toggled += on =>
        {
            VocabConfig.Instance.RewardEnabled = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(enableToggle);

        var hint = GameTheme.MakeLabel(
            "可添加多条规则，每条独立配置。模式说明：\n" +
            "• 达标一次：连胜恰好达到阈值那一刻触发一次。之后再答对不再触发，要等答错重置后重新累到阈值。\n" +
            "    例：阈值 5 → 第 5 次答对给一次，第 6/7… 都不给，直到答错重新连胜到 5。\n" +
            "• 持续生效：连胜达到阈值后，之后每次答对都触发。\n" +
            "    例：阈值 1 → 每次答对都给（最常用，等价于「每次答对都奖励」）。\n" +
            "    例：阈值 3 → 连胜到 3 之后每次答对都给。\n" +
            "• 每 N 次：连胜达到阈值的整数倍时触发。\n" +
            "    例：阈值 5 → 第 5、10、15、20… 次答对各给一次。\n" +
            "答错会重置连胜计数。",
            11, DimGrey);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(hint);

        _rewardRulesContainer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rewardRulesContainer.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(_rewardRulesContainer);

        RebuildRewardRules();

        var addBtn = GameTheme.MakeButton("  + 添加奖励规则  ", 14);
        addBtn.Pressed += () =>
        {
            VocabConfig.Instance.RewardRules.Add(new Models.RewardRule());
            VocabConfig.Instance.Save();
            RebuildRewardRules();
        };
        vbox.AddChild(addBtn);

        // ── 免错券独立 section ──
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 免错券（连对累积，主动使用）--", 22, SectionColor));

        var fpEnable = new CheckButton
        {
            Text = " 启用免错券（连对累积，下张牌不出题直接打出）",
            ButtonPressed = cfg.FreePassEnabled
        };
        fpEnable.AddThemeFontSizeOverride("font_size", 14);
        fpEnable.Toggled += on =>
        {
            VocabConfig.Instance.FreePassEnabled = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(fpEnable);

        var fpRow = new HBoxContainer();
        fpRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(fpRow);
        fpRow.AddChild(GameTheme.MakeLabel("累积阈值：", 16, White));
        var fpCost = new SpinBox { MinValue = 1, MaxValue = 20, Step = 1, Value = cfg.FreePassStreakCost, CustomMinimumSize = new Vector2(90, 0) };
        fpCost.GetLineEdit().AddThemeFontSizeOverride("font_size", 14);
        fpCost.ValueChanged += val => { VocabConfig.Instance.FreePassStreakCost = (int)val; VocabConfig.Instance.Save(); };
        fpRow.AddChild(fpCost);
        fpRow.AddChild(GameTheme.MakeLabel("（连对 N 次 → +1 券）", 12, DimGrey));

        var fpMaxRow = new HBoxContainer();
        fpMaxRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(fpMaxRow);
        fpMaxRow.AddChild(GameTheme.MakeLabel("最大持有：", 16, White));
        var fpMax = new SpinBox { MinValue = 1, MaxValue = 99, Step = 1, Value = cfg.FreePassMaxStock, CustomMinimumSize = new Vector2(90, 0) };
        fpMax.GetLineEdit().AddThemeFontSizeOverride("font_size", 14);
        fpMax.ValueChanged += val => { VocabConfig.Instance.FreePassMaxStock = (int)val; VocabConfig.Instance.Save(); };
        fpMaxRow.AddChild(fpMax);
        fpMaxRow.AddChild(GameTheme.MakeLabel($"  当前局库存：{RunBattleState.Instance.GetStock()}（不跨局继承）", 12, Gold));
    }

    private void RebuildRewardRules()
    {
        foreach (var c in _rewardRulesContainer.GetChildren()) c.QueueFree();
        var rules = VocabConfig.Instance.RewardRules;
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            _rewardRulesContainer.AddChild(BuildRuleRow(rule, i));
        }
        GameTheme.ApplyFontRecursive(_rewardRulesContainer);
    }

    /// <summary>给规则行加「题型」下拉（奖励/惩罚共用）。current=当前值，onChange=写回。</summary>
    private void AddQuizTypeSelector(HBoxContainer row, QuizModeFlags current, System.Action<QuizModeFlags> onChange)
    {
        row.AddChild(GameTheme.MakeLabel("题型", 12, Grey));
        var sel = new OptionButton { CustomMinimumSize = new Vector2(100, 0) };
        var types = new (QuizModeFlags, string)[]
        {
            (QuizModeFlags.None, "全部"),
            (QuizModeFlags.EnglishToChinese, "英→中"),
            (QuizModeFlags.ChineseToEnglish, "中→英"),
            (QuizModeFlags.ListenToChinese, "听力"),
            (QuizModeFlags.SpellEnglish, "拼写")
        };
        var pick = 0;
        for (var i = 0; i < types.Length; i++)
        {
            sel.AddItem(types[i].Item2, (int)types[i].Item1);
            if (types[i].Item1 == current) pick = i;
        }
        sel.Selected = pick;
        sel.TooltipText = "只对该题型答对/答错时触发本条规则（如「拼写→+力量」「听力→+覆甲」）。选「全部」则不限题型。";
        sel.ItemSelected += i => { onChange((QuizModeFlags)sel.GetItemId((int)i)); VocabConfig.Instance.Save(); };
        row.AddChild(sel);
    }

    /// <summary>给规则加「高级 ▾」折叠区：重算范围 / 冷却 / 连对封顶 / 最多触发（奖励/惩罚共用）。</summary>
    private void AddAdvancedFold(VBoxContainer parent,
        Models.StreakResetScope scope, System.Action<Models.StreakResetScope> setScope,
        int cooldown, System.Action<int> setCooldown,
        int streakCap, System.Action<int> setCap,
        int maxTriggers, System.Action<int> setMax)
    {
        var foldBtn = new Button { Text = "高级 ▾", Flat = true, CustomMinimumSize = new Vector2(70, 0) };
        foldBtn.AddThemeFontSizeOverride("font_size", 12);
        parent.AddChild(foldBtn);

        var adv = new HBoxContainer { Visible = false };
        adv.AddThemeConstantOverride("separation", 12);
        parent.AddChild(adv);
        foldBtn.Pressed += () => { adv.Visible = !adv.Visible; foldBtn.Text = adv.Visible ? "高级 ▲" : "高级 ▾"; };

        adv.AddChild(GameTheme.MakeLabel("重算", 12, Grey));
        var scopeSel = new OptionButton { CustomMinimumSize = new Vector2(92, 0) };
        scopeSel.AddItem("永久", 0);
        scopeSel.AddItem("每场战斗", 1);
        scopeSel.AddItem("每回合", 2);
        scopeSel.Selected = (int)scope;
        scopeSel.TooltipText = "连对/连错计数的重算范围：永久（跨战斗累积，=老行为）/ 每场战斗开始重置 / 每回合开始重置。";
        scopeSel.ItemSelected += i => { setScope((Models.StreakResetScope)(int)i); VocabConfig.Instance.Save(); };
        adv.AddChild(scopeSel);

        adv.AddChild(GameTheme.MakeLabel("冷却", 12, Grey));
        var cdSpin = new SpinBox { MinValue = 0, MaxValue = 99, Step = 1, Value = cooldown, CustomMinimumSize = new Vector2(62, 0) };
        cdSpin.GetLineEdit().AddThemeFontSizeOverride("font_size", 12);
        cdSpin.TooltipText = "触发后需再答满 N 题才能再次触发。0=无冷却。";
        cdSpin.ValueChanged += v => { setCooldown((int)v); VocabConfig.Instance.Save(); };
        adv.AddChild(cdSpin);

        adv.AddChild(GameTheme.MakeLabel("封顶", 12, Grey));
        var capSpin = new SpinBox { MinValue = 0, MaxValue = 99, Step = 1, Value = streakCap, CustomMinimumSize = new Vector2(62, 0) };
        capSpin.GetLineEdit().AddThemeFontSizeOverride("font_size", 12);
        capSpin.TooltipText = "连对/连错计数最多按 N 算，防 Recurring/EveryN 无限放大。0=不封顶。";
        capSpin.ValueChanged += v => { setCap((int)v); VocabConfig.Instance.Save(); };
        adv.AddChild(capSpin);

        adv.AddChild(GameTheme.MakeLabel("上限", 12, Grey));
        var maxSpin = new SpinBox { MinValue = 0, MaxValue = 99, Step = 1, Value = maxTriggers, CustomMinimumSize = new Vector2(62, 0) };
        maxSpin.GetLineEdit().AddThemeFontSizeOverride("font_size", 12);
        maxSpin.TooltipText = "本重算周期内最多触发次数，到点本周期不再触发。0=无限。";
        maxSpin.ValueChanged += v => { setMax((int)v); VocabConfig.Instance.Save(); };
        adv.AddChild(maxSpin);
    }

    private Control BuildRuleRow(Models.RewardRule rule, int idx)
    {
        var box = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.07f, 0.1f),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            BorderColor = new Color(0.3f, 0.3f, 0.4f),
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            ContentMarginTop = 6, ContentMarginBottom = 6, ContentMarginLeft = 10, ContentMarginRight = 10
        };
        box.AddThemeStyleboxOverride("panel", style);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        box.AddChild(v);

        // 行 1：开关 / 类型 / 阈值 / 数量 / 模式 / 删除
        var r1 = new HBoxContainer();
        r1.AddThemeConstantOverride("separation", 6);
        v.AddChild(r1);

        var enableCb = new CheckBox { ButtonPressed = rule.Enabled };
        enableCb.Toggled += on => { rule.Enabled = on; VocabConfig.Instance.Save(); };
        r1.AddChild(enableCb);

        var kindSel = new OptionButton { CustomMinimumSize = new Vector2(120, 0) };
        var kinds = new (RewardType, string)[]
        {
            (RewardType.Hp, "回血"),
            (RewardType.Energy, "能量"),
            (RewardType.Gold, "金币"),
            (RewardType.Strength, "力量"),
            (RewardType.Dexterity, "敏捷"),
            (RewardType.Block, "覆甲"),
            (RewardType.Draw, "抽牌"),
            (RewardType.Thorns, "荆棘"),
            (RewardType.Focus, "集中"),
            (RewardType.Artifact, "人工制品"),
            (RewardType.Replay, "重放本牌")
        };
        var selIdx = 0;
        for (var i = 0; i < kinds.Length; i++)
        {
            kindSel.AddItem(kinds[i].Item2, (int)kinds[i].Item1);
            if (kinds[i].Item1 == rule.Kind) selIdx = i;
        }
        kindSel.Selected = selIdx;
        kindSel.ItemSelected += i =>
        {
            rule.Kind = (RewardType)kindSel.GetItemId((int)i);
            VocabConfig.Instance.Save();
        };
        r1.AddChild(kindSel);

        r1.AddChild(GameTheme.MakeLabel("阈值", 13, Grey));
        var streak = new SpinBox { MinValue = 1, MaxValue = 99, Step = 1, Value = rule.Streak, CustomMinimumSize = new Vector2(70, 0) };
        streak.GetLineEdit().AddThemeFontSizeOverride("font_size", 13);
        streak.ValueChanged += v => { rule.Streak = (int)v; VocabConfig.Instance.Save(); };
        r1.AddChild(streak);

        r1.AddChild(GameTheme.MakeLabel("数量", 13, Grey));
        var amt = new SpinBox { MinValue = 1, MaxValue = 999, Step = 1, Value = rule.Amount, CustomMinimumSize = new Vector2(80, 0) };
        amt.GetLineEdit().AddThemeFontSizeOverride("font_size", 13);
        amt.ValueChanged += v => { rule.Amount = (int)v; VocabConfig.Instance.Save(); };
        r1.AddChild(amt);

        var modeSel = new OptionButton { CustomMinimumSize = new Vector2(130, 0) };
        modeSel.AddItem("达标一次", 0);
        modeSel.AddItem("持续生效", 1);
        modeSel.AddItem("每 N 次", 2);
        modeSel.Selected = (int)rule.Mode;
        modeSel.TooltipText =
            "达标一次：连胜恰好等于阈值那一刻触发一次（之后不再触发，直到答错重置后重新累到阈值）。\n" +
            "持续生效：连胜 ≥ 阈值时每次答对都触发。阈值 1 = 每次答对都给奖励。\n" +
            "每 N 次：连胜达到阈值的整数倍时触发（阈值 5 → 5/10/15… 各给一次）。";
        modeSel.ItemSelected += i => { rule.Mode = (Models.RewardTriggerMode)(int)i; VocabConfig.Instance.Save(); };
        r1.AddChild(modeSel);

        var delBtn = new Button { Text = " ✕ ", CustomMinimumSize = new Vector2(36, 0) };
        delBtn.AddThemeFontSizeOverride("font_size", 13);
        delBtn.Pressed += () =>
        {
            VocabConfig.Instance.RewardRules.RemoveAt(idx);
            VocabConfig.Instance.Save();
            RebuildRewardRules();
        };
        r1.AddChild(delBtn);

        // 行 2：题型 + 难度加成 + 多义翻倍
        var r2 = new HBoxContainer();
        r2.AddThemeConstantOverride("separation", 16);
        v.AddChild(r2);

        AddQuizTypeSelector(r2, rule.QuizTypeFilter, val => rule.QuizTypeFilter = val);

        var diffCb = new CheckBox { ButtonPressed = rule.DifficultyScaling, Text = " 难度加成（拼写×2/听力×1.5）" };
        diffCb.AddThemeFontSizeOverride("font_size", 12);
        diffCb.Toggled += on => { rule.DifficultyScaling = on; VocabConfig.Instance.Save(); };
        r2.AddChild(diffCb);

        var multiCb = new CheckBox { ButtonPressed = rule.MultiDefDouble, Text = " 多释义题翻倍" };
        multiCb.AddThemeFontSizeOverride("font_size", 12);
        multiCb.Toggled += on => { rule.MultiDefDouble = on; VocabConfig.Instance.Save(); };
        r2.AddChild(multiCb);

        AddAdvancedFold(v,
            rule.ResetScope, val => rule.ResetScope = val,
            rule.Cooldown, val => rule.Cooldown = val,
            rule.StreakCap, val => rule.StreakCap = val,
            rule.MaxTriggers, val => rule.MaxTriggers = val);

        return box;
    }

    // ──────────────────────── 惩罚 section（跟奖励对称）────────────────────────
    private VBoxContainer _punishmentRulesContainer = null!;

    private void BuildPunishmentSection(VBoxContainer vbox)
    {
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 答错惩罚设置 --", 22, SectionColor));

        var cfg = VocabConfig.Instance;

        var enableToggle = new CheckButton
        {
            Text = " 启用答错惩罚（总开关）",
            ButtonPressed = cfg.PunishmentEnabled
        };
        enableToggle.AddThemeFontSizeOverride("font_size", 14);
        enableToggle.Toggled += on =>
        {
            VocabConfig.Instance.PunishmentEnabled = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(enableToggle);

        var hint = GameTheme.MakeLabel(
            "按「连错」次数（WrongStreak）触发，答对会重置连错。规则字段、模式跟奖励完全对称。\n" +
            "效果反向：回血→直接掉血（无视格挡）；能量/金币→扣；力量/敏捷/荆棘/集中/人工→扣层数；覆甲→扣格挡；抽牌→随机弃手中 N 张。",
            11, DimGrey);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(hint);

        _punishmentRulesContainer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _punishmentRulesContainer.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(_punishmentRulesContainer);

        RebuildPunishmentRules();

        var addBtn = GameTheme.MakeButton("  + 添加惩罚规则  ", 14);
        addBtn.Pressed += () =>
        {
            VocabConfig.Instance.PunishmentRules.Add(new Models.PunishmentRule());
            VocabConfig.Instance.Save();
            RebuildPunishmentRules();
        };
        vbox.AddChild(addBtn);
    }

    private void RebuildPunishmentRules()
    {
        foreach (var c in _punishmentRulesContainer.GetChildren()) c.QueueFree();
        var rules = VocabConfig.Instance.PunishmentRules;
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            _punishmentRulesContainer.AddChild(BuildPunishmentRuleRow(rule, i));
        }
        GameTheme.ApplyFontRecursive(_punishmentRulesContainer);
    }

    private Control BuildPunishmentRuleRow(Models.PunishmentRule rule, int idx)
    {
        var box = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.07f, 0.07f),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            BorderColor = new Color(0.4f, 0.3f, 0.3f),
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            ContentMarginTop = 6, ContentMarginBottom = 6, ContentMarginLeft = 10, ContentMarginRight = 10
        };
        box.AddThemeStyleboxOverride("panel", style);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        box.AddChild(v);

        var r1 = new HBoxContainer();
        r1.AddThemeConstantOverride("separation", 6);
        v.AddChild(r1);

        var enableCb = new CheckBox { ButtonPressed = rule.Enabled };
        enableCb.Toggled += on => { rule.Enabled = on; VocabConfig.Instance.Save(); };
        r1.AddChild(enableCb);

        var kindSel = new OptionButton { CustomMinimumSize = new Vector2(120, 0) };
        // 惩罚名称强调反向语义
        var kinds = new (RewardType, string)[]
        {
            (RewardType.Hp, "掉血"),
            (RewardType.Energy, "扣能量"),
            (RewardType.Gold, "扣金币"),
            (RewardType.Strength, "-力量"),
            (RewardType.Dexterity, "-敏捷"),
            (RewardType.Block, "扣格挡"),
            (RewardType.Draw, "随机弃牌"),
            (RewardType.Thorns, "-荆棘"),
            (RewardType.Focus, "-集中"),
            (RewardType.Artifact, "-人工制品")
        };
        var selIdx = 0;
        for (var i = 0; i < kinds.Length; i++)
        {
            kindSel.AddItem(kinds[i].Item2, (int)kinds[i].Item1);
            if (kinds[i].Item1 == rule.Kind) selIdx = i;
        }
        kindSel.Selected = selIdx;
        kindSel.ItemSelected += i =>
        {
            rule.Kind = (RewardType)kindSel.GetItemId((int)i);
            VocabConfig.Instance.Save();
        };
        r1.AddChild(kindSel);

        r1.AddChild(GameTheme.MakeLabel("阈值", 13, Grey));
        var streak = new SpinBox { MinValue = 1, MaxValue = 99, Step = 1, Value = rule.Streak, CustomMinimumSize = new Vector2(70, 0) };
        streak.GetLineEdit().AddThemeFontSizeOverride("font_size", 13);
        streak.ValueChanged += val => { rule.Streak = (int)val; VocabConfig.Instance.Save(); };
        r1.AddChild(streak);

        r1.AddChild(GameTheme.MakeLabel("数量", 13, Grey));
        var amt = new SpinBox { MinValue = 1, MaxValue = 999, Step = 1, Value = rule.Amount, CustomMinimumSize = new Vector2(80, 0) };
        amt.GetLineEdit().AddThemeFontSizeOverride("font_size", 13);
        amt.ValueChanged += val => { rule.Amount = (int)val; VocabConfig.Instance.Save(); };
        r1.AddChild(amt);

        var modeSel = new OptionButton { CustomMinimumSize = new Vector2(130, 0) };
        modeSel.AddItem("达标一次", 0);
        modeSel.AddItem("持续生效", 1);
        modeSel.AddItem("每 N 次", 2);
        modeSel.Selected = (int)rule.Mode;
        modeSel.TooltipText =
            "达标一次：连错恰好等于阈值那一刻触发一次。\n" +
            "持续生效：连错 ≥ 阈值时每次答错都触发。阈值 1 = 每次答错都惩罚。\n" +
            "每 N 次：连错达到阈值的整数倍时触发。";
        modeSel.ItemSelected += i => { rule.Mode = (Models.RewardTriggerMode)(int)i; VocabConfig.Instance.Save(); };
        r1.AddChild(modeSel);

        var delBtn = new Button { Text = " ✕ ", CustomMinimumSize = new Vector2(36, 0) };
        delBtn.AddThemeFontSizeOverride("font_size", 13);
        delBtn.Pressed += () =>
        {
            VocabConfig.Instance.PunishmentRules.RemoveAt(idx);
            VocabConfig.Instance.Save();
            RebuildPunishmentRules();
        };
        r1.AddChild(delBtn);

        var r2 = new HBoxContainer();
        r2.AddThemeConstantOverride("separation", 16);
        v.AddChild(r2);

        AddQuizTypeSelector(r2, rule.QuizTypeFilter, val => rule.QuizTypeFilter = val);

        var diffCb = new CheckBox { ButtonPressed = rule.DifficultyScaling, Text = " 难度加成（拼写×2/听力×1.5）" };
        diffCb.AddThemeFontSizeOverride("font_size", 12);
        diffCb.Toggled += on => { rule.DifficultyScaling = on; VocabConfig.Instance.Save(); };
        r2.AddChild(diffCb);

        var multiCb = new CheckBox { ButtonPressed = rule.MultiDefDouble, Text = " 多释义题翻倍" };
        multiCb.AddThemeFontSizeOverride("font_size", 12);
        multiCb.Toggled += on => { rule.MultiDefDouble = on; VocabConfig.Instance.Save(); };
        r2.AddChild(multiCb);

        AddAdvancedFold(v,
            rule.ResetScope, val => rule.ResetScope = val,
            rule.Cooldown, val => rule.Cooldown = val,
            rule.StreakCap, val => rule.StreakCap = val,
            rule.MaxTriggers, val => rule.MaxTriggers = val);

        return box;
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

        BuildDifficultySubSection(vbox, cfg);

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
        reviewCountRow.AddChild(GameTheme.MakeLabel("\u7BDD\u706B\u590D\u4E60\u4E0A\u9650\uFF1A", 18, White));

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
        reviewCountRow.AddChild(GameTheme.MakeLabel("  (0 = \u9ED8\u8BA4 7\uFF0C\u81EA\u5DF1\u586B\u60F3\u590D\u4E60\u51E0\u4E2A)", 12, DimGrey));

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
        masteryRow.AddChild(GameTheme.MakeLabel("  (\u8FDE\u7EED\u7B54\u5BF9\u6B21\u6570\uFF1B\u6BD5\u4E1A\u8BCD\u4E0D\u4F1A\u56E0\u5355\u6B21\u7B54\u9519\u6389\u51FA\u638C\u63E1)", 16, DimGrey));

        // 每局新词上限（新词节流，v2.7 记忆引擎）
        var newWordRow = new HBoxContainer();
        newWordRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(newWordRow);
        newWordRow.AddChild(GameTheme.MakeLabel("新词上限：", 18, White));

        var newWordInput = new SpinBox
        {
            MinValue = 3,
            MaxValue = 50,
            Step = 1,
            Value = cfg.NewWordLimit,
            CustomMinimumSize = new Vector2(100, 0)
        };
        newWordInput.GetLineEdit().AddThemeFontSizeOverride("font_size", 14);
        newWordInput.ValueChanged += val =>
        {
            VocabConfig.Instance.NewWordLimit = (int)val;
            VocabConfig.Instance.Save();
        };
        newWordRow.AddChild(newWordInput);
        newWordRow.AddChild(GameTheme.MakeLabel("  (同时学习中的新词上限，满了先巩固)", 16, DimGrey));

        // ── 高级：间隔参数（v2.7 记忆引擎曲线，逗号分隔）──
        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("-- 高级：间隔参数 --", 22, SectionColor));

        var stepRow = new HBoxContainer();
        stepRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(stepRow);
        stepRow.AddChild(GameTheme.MakeLabel("题数间隔：", 18, White));
        var stepInput = new LineEdit
        {
            Text = string.Join(",", cfg.IntervalSteps),
            CustomMinimumSize = new Vector2(220, 0)
        };
        stepInput.AddThemeFontSizeOverride("font_size", 14);
        stepInput.TextSubmitted += t =>
        {
            var arr = ParseIntCsv(t, VocabConfig.Instance.IntervalSteps);
            VocabConfig.Instance.IntervalSteps = arr;
            VocabConfig.Instance.Save();
            stepInput.Text = string.Join(",", arr);
        };
        stepRow.AddChild(stepInput);
        stepRow.AddChild(GameTheme.MakeLabel("  (Box0-5 题数，回车保存)", 12, DimGrey));

        var dayRow = new HBoxContainer();
        dayRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(dayRow);
        dayRow.AddChild(GameTheme.MakeLabel("跨天间隔：", 18, White));
        var dayInput = new LineEdit
        {
            Text = string.Join(",", cfg.IntervalDaysSteps),
            CustomMinimumSize = new Vector2(160, 0)
        };
        dayInput.AddThemeFontSizeOverride("font_size", 14);
        dayInput.TextSubmitted += t =>
        {
            var arr = ParseIntCsv(t, VocabConfig.Instance.IntervalDaysSteps);
            VocabConfig.Instance.IntervalDaysSteps = arr;
            VocabConfig.Instance.Save();
            dayInput.Text = string.Join(",", arr);
        };
        dayRow.AddChild(dayInput);
        dayRow.AddChild(GameTheme.MakeLabel("  (Box3-5 天数，回车保存)", 12, DimGrey));

        var cdRow = new HBoxContainer();
        cdRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(cdRow);
        cdRow.AddChild(GameTheme.MakeLabel("防连续窗口：", 18, White));
        var cdInput = new SpinBox
        {
            MinValue = 0, MaxValue = 30, Step = 1,
            Value = cfg.MiniCooldown,
            CustomMinimumSize = new Vector2(100, 0)
        };
        cdInput.GetLineEdit().AddThemeFontSizeOverride("font_size", 14);
        cdInput.ValueChanged += val =>
        {
            VocabConfig.Instance.MiniCooldown = (int)val;
            VocabConfig.Instance.Save();
        };
        cdRow.AddChild(cdInput);
        cdRow.AddChild(GameTheme.MakeLabel("  (最近几张不重复同词)", 12, DimGrey));

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

    /// <summary>5 个独立难度开关 + 2 个概率 SpinBox + 始终显示音标 toggle。</summary>
    private void BuildDifficultySubSection(VBoxContainer vbox, VocabConfig cfg)
    {
        vbox.AddChild(GameTheme.MakeLabel("• 难度递增 (可独立开关)", 14, Gold));

        // 1. 启用混淆度干扰项
        var confusionTg = new CheckButton
        {
            Text = " 启用混淆度干扰项（Act2+ 用近形/近义/近音词作干扰）",
            ButtonPressed = cfg.EnableConfusionDistractor
        };
        confusionTg.Toggled += on =>
        {
            VocabConfig.Instance.EnableConfusionDistractor = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(confusionTg);

        // 2. 启用选项数量递增
        var optScaleTg = new CheckButton
        {
            Text = " 启用选项数量递增（Act2 +1、Act3 +2，最多 8）",
            ButtonPressed = cfg.EnableOptionCountScaling
        };
        optScaleTg.Toggled += on =>
        {
            VocabConfig.Instance.EnableOptionCountScaling = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(optScaleTg);

        // 3. 启用强制拼写（Act2 / Act3 概率 SpinBox）
        var spellTg = new CheckButton
        {
            Text = " 启用强制拼写（Act2/Act3 概率把选择题变拼写题；需在「题型」里勾选拼写）",
            ButtonPressed = cfg.EnableForceSpelling
        };
        vbox.AddChild(spellTg);

        var spellRow = new HBoxContainer();
        spellRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(spellRow);
        spellRow.AddChild(GameTheme.MakeLabel("    Act2 强制率(%)：", 13, White));
        var spell2 = new SpinBox
        {
            MinValue = 0, MaxValue = 100, Step = 5,
            Value = cfg.ForceSpellingChanceAct2Percent,
            CustomMinimumSize = new Vector2(80, 0),
            Editable = cfg.EnableForceSpelling
        };
        spell2.GetLineEdit().AddThemeFontSizeOverride("font_size", 13);
        spell2.ValueChanged += v => { VocabConfig.Instance.ForceSpellingChanceAct2Percent = (int)v; VocabConfig.Instance.Save(); };
        spellRow.AddChild(spell2);
        spellRow.AddChild(GameTheme.MakeLabel("    Act3 强制率(%)：", 13, White));
        var spell3 = new SpinBox
        {
            MinValue = 0, MaxValue = 100, Step = 5,
            Value = cfg.ForceSpellingChanceAct3Percent,
            CustomMinimumSize = new Vector2(80, 0),
            Editable = cfg.EnableForceSpelling
        };
        spell3.GetLineEdit().AddThemeFontSizeOverride("font_size", 13);
        spell3.ValueChanged += v => { VocabConfig.Instance.ForceSpellingChanceAct3Percent = (int)v; VocabConfig.Instance.Save(); };
        spellRow.AddChild(spell3);
        spellTg.Toggled += on =>
        {
            VocabConfig.Instance.EnableForceSpelling = on;
            VocabConfig.Instance.Save();
            spell2.Editable = on;
            spell3.Editable = on;
        };

        // 4. 启用反转模式（Act3 概率 SpinBox）
        var reverseTg = new CheckButton
        {
            Text = " 启用反转模式（Act3 概率把英↔中互换；需要两个方向都已勾选）",
            ButtonPressed = cfg.EnableReverseMode
        };
        vbox.AddChild(reverseTg);

        var revRow = new HBoxContainer();
        revRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(revRow);
        revRow.AddChild(GameTheme.MakeLabel("    Act3 反转率(%)：", 13, White));
        var revSb = new SpinBox
        {
            MinValue = 0, MaxValue = 100, Step = 5,
            Value = cfg.ReverseModeChancePercent,
            CustomMinimumSize = new Vector2(80, 0),
            Editable = cfg.EnableReverseMode
        };
        revSb.GetLineEdit().AddThemeFontSizeOverride("font_size", 13);
        revSb.ValueChanged += v => { VocabConfig.Instance.ReverseModeChancePercent = (int)v; VocabConfig.Instance.Save(); };
        revRow.AddChild(revSb);
        reverseTg.Toggled += on =>
        {
            VocabConfig.Instance.EnableReverseMode = on;
            VocabConfig.Instance.Save();
            revSb.Editable = on;
        };

        // 5. 始终显示音标（独立 toggle，与 Act 无关）
        var phoneticTg = new CheckButton
        {
            Text = " 始终显示音标（默认仅 Act1 显示，开启后所有层都显示）",
            ButtonPressed = cfg.AlwaysShowPhonetic
        };
        phoneticTg.Toggled += on =>
        {
            VocabConfig.Instance.AlwaysShowPhonetic = on;
            VocabConfig.Instance.Save();
        };
        vbox.AddChild(phoneticTg);

        var diffDesc = GameTheme.MakeLabel(
            "提示：所有开关独立生效。即使全部关闭，第 1/2/3 层仍按基础规则出题。\n" +
            "强制拼写：越掌握的词越容易升级为拼写题——毕业词(记忆盒 Box≥3)按掌握度\n" +
            "         额外加成拼写概率（Box 3/4/5 各 +15%/+30%/+45%）；\n" +
            "         但若你把概率设为 0%，则严格 0%，加成不生效。\n" +
            "反转模式：严格按概率，无额外加成。",
            11, DimGrey);
        diffDesc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(diffDesc);
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
            "也可以点击「导入词库」直接选择文件导入（支持 .json / .csv / Anki .apkg）。\n" +
            "点击「导出模板」可获取 JSON 词库模板文件。\n" +
            "JSON 格式支持 \"chinese\" 为字符串或字符串数组（多释义）。",
            11, DimGrey);
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(help);
    }

    /// <summary>解析逗号分隔的正整数（如 "3,8,20"），失败或空则返回 fallback。</summary>
    private static int[] ParseIntCsv(string s, int[] fallback)
    {
        try
        {
            var parts = s.Split(',');
            var list = new System.Collections.Generic.List<int>();
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0 && int.TryParse(t, out var n) && n > 0) list.Add(n);
            }
            return list.Count > 0 ? list.ToArray() : fallback;
        }
        catch { return fallback; }
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
        _fileDialog.AddFilter("*.apkg", "Anki 词库 (.apkg)");
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
        RebuildBankChecks();
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

    private void RebuildBankChecks()
    {
        foreach (var c in _bankChecksContainer.GetChildren()) c.QueueFree();
        foreach (var b in VocabManager.Instance.Banks)
        {
            var id = b.Id;
            var cb = new CheckBox
            {
                Text = $" {b.Name} ({b.TotalWords} 词)",
                ButtonPressed = VocabManager.Instance.IsBankActive(id)
            };
            cb.AddThemeFontSizeOverride("font_size", 15);
            cb.Toggled += on =>
            {
                VocabManager.Instance.ToggleActiveBank(id, on);
                // ToggleActiveBank 会拒绝取消最后一个 → 用实际状态回写勾选框
                cb.SetPressedNoSignal(VocabManager.Instance.IsBankActive(id));
                RefreshBankAnalysis();
            };
            _bankChecksContainer.AddChild(cb);
        }
        GameTheme.ApplyFontRecursive(_bankChecksContainer);
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
        // KeyBindButton 捕获模式按 Esc 取消时会先 SetInputAsHandled，此处不再误关面板
        if (GetViewport().IsInputHandled()) return;
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
