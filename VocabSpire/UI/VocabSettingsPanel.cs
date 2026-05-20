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

        var newBankBtn = GameTheme.MakeButton("  新建词库  ", 14);
        newBankBtn.Pressed += () => WordBankEditorPanel.Instance?.Open();
        btnRow.AddChild(newBankBtn);

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

        // 容错总开关
        var tolEnableToggle = new CheckButton
        {
            Text = " 启用每回合容错（答错不扣费且回手）",
            ButtonPressed = cfg.ToleranceEnabled
        };
        tolEnableToggle.AddThemeFontSizeOverride("font_size", 14);
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
            Editable = cfg.ToleranceEnabled
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
            tolInput.Editable = on;
        };

        // 答错处理（互斥选项）
        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(actionRow);
        actionRow.AddChild(GameTheme.MakeLabel("答错时卡牌：", 18, White));

        var actionSelector = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
        actionSelector.AddItem("扣费 + 进弃牌堆 (默认)", 0);
        actionSelector.AddItem("扣费 + 返回手牌", 1);
        actionSelector.Selected = cfg.WrongCardReturnToHand ? 1 : 0;
        actionSelector.ItemSelected += idx =>
        {
            VocabConfig.Instance.WrongCardReturnToHand = idx == 1;
            VocabConfig.Instance.Save();
        };
        actionRow.AddChild(actionSelector);

        var battleDesc = GameTheme.MakeLabel(
            "说明：容错开启时优先生效（不扣费 + 回手）；超出容错次数后按上方选项处理。",
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
            "可添加多条规则，每条独立配置。模式：「一次性」仅 streak=阈值 时触发；「累积」streak≥阈值时每次答对都触发。\n" +
            "答错会重置 streak。例：5个+5HP / 10个+1能量 / 累积3之后每次+1费",
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
            (RewardType.Artifact, "人工制品")
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

        var modeSel = new OptionButton { CustomMinimumSize = new Vector2(90, 0) };
        modeSel.AddItem("一次性", 0);
        modeSel.AddItem("累积", 1);
        modeSel.Selected = (int)rule.Mode;
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

        // 行 2：难度加成 + 多义翻倍
        var r2 = new HBoxContainer();
        r2.AddThemeConstantOverride("separation", 16);
        v.AddChild(r2);

        var diffCb = new CheckBox { ButtonPressed = rule.DifficultyScaling, Text = " 难度加成（拼写×2/听力×1.5）" };
        diffCb.AddThemeFontSizeOverride("font_size", 12);
        diffCb.Toggled += on => { rule.DifficultyScaling = on; VocabConfig.Instance.Save(); };
        r2.AddChild(diffCb);

        var multiCb = new CheckBox { ButtonPressed = rule.MultiDefDouble, Text = " 多释义题翻倍" };
        multiCb.AddThemeFontSizeOverride("font_size", 12);
        multiCb.Toggled += on => { rule.MultiDefDouble = on; VocabConfig.Instance.Save(); };
        r2.AddChild(multiCb);

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
            "强制拼写：已掌握的词（答对 >2 次 + 正确率 >70%）额外 +20% 概率；\n" +
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
