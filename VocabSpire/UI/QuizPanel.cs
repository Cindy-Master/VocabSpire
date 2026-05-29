using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 答题弹窗面板 —— 支持选择题（含多选）和拼写题。
/// 选择题部分委托给 ChoiceAnswerWidget；本类只负责题目展示、反馈、统计、确认。
/// </summary>
public partial class QuizPanel : Control
{
    public static QuizPanel? Instance { get; private set; }

    private Label _modeLabel = null!;
    private Label _promptLabel = null!;
    private Label _feedbackLabel = null!;
    private Label _statsLabel = null!;
    private ChoiceAnswerWidget _choiceWidget = null!;
    private HBoxContainer _spellingContainer = null!;
    private Label _spellingHintLabel = null!;
    private LineEdit _spellingInput = null!;
    private Button _spellingSubmitBtn = null!;
    private Button _confirmButton = null!;
    private HBoxContainer _listenContainer = null!;
    private Button _listenBtn = null!;
    private Button _listenPlayTop = null!;

    private QuizQuestion? _currentQuestion;
    private Action<bool>? _onAnswered;
    private bool _answered;
    private bool _lastCorrect;
    private ulong _answeredAtMsec; // 防止 Enter 双触发

    private static readonly Color BgColor = GameTheme.DarkBg;
    private static readonly Color AccentGold = GameTheme.Gold;
    private static readonly Color CorrectGreen = GameTheme.Green;
    private static readonly Color WrongRed = GameTheme.Red;
    private static readonly Color TextWhite = GameTheme.Cream;
    private static readonly Color TextGrey = GameTheme.LightGray;

    public override void _Ready()
    {
        Instance = this;
        BuildUI();
        GameTheme.ApplyFontRecursive(this);
        Visible = false;
        ZIndex = 100;
        ProcessMode = ProcessModeEnum.Always;
        Log.Info("[VocabSpire] QuizPanel ready.");
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

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(620, 0) };
        var panelStyle = new StyleBoxFlat
        {
            BgColor = BgColor,
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderColor = AccentGold,
            ContentMarginTop = 28, ContentMarginBottom = 28,
            ContentMarginLeft = 36, ContentMarginRight = 36
        };
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        center.AddChild(panel);

        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 18);
        panel.AddChild(mainVBox);

        // 标题栏
        var titleBar = new HBoxContainer();
        mainVBox.AddChild(titleBar);
        titleBar.AddChild(GameTheme.MakeLabel("VocabSpire 背单词", 15, AccentGold));
        titleBar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _modeLabel = GameTheme.MakeLabel("", 14, TextGrey);
        titleBar.AddChild(_modeLabel);

        mainVBox.AddChild(new HSeparator());

        // 题目
        _promptLabel = GameTheme.MakeLabel("", 30, TextWhite, HorizontalAlignment.Center);
        _promptLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        mainVBox.AddChild(_promptLabel);

        // 听力模式顶部播放按钮
        var listenTopCenter = new CenterContainer();
        mainVBox.AddChild(listenTopCenter);
        _listenPlayTop = GameTheme.MakeButton("  🔊  播放发音  ", 22, GameTheme.Gold);
        _listenPlayTop.CustomMinimumSize = new Vector2(260, 54);
        _listenPlayTop.Visible = false;
        _listenPlayTop.Pressed += OnListenPressed;
        listenTopCenter.AddChild(_listenPlayTop);

        // 选择题答题区（共享组件 —— 单选/多选共用、提交按钮内置）
        _choiceWidget = new ChoiceAnswerWidget { Visible = false };
        mainVBox.AddChild(_choiceWidget);

        // 拼写简单模式掩码提示（如 "c _ _ e"）
        _spellingHintLabel = GameTheme.MakeLabel("", 32, AccentGold, HorizontalAlignment.Center);
        _spellingHintLabel.AddThemeConstantOverride("outline_size", 0);
        _spellingHintLabel.Visible = false;
        mainVBox.AddChild(_spellingHintLabel);

        // 拼写输入区
        _spellingContainer = new HBoxContainer { Visible = false };
        _spellingContainer.AddThemeConstantOverride("separation", 10);
        mainVBox.AddChild(_spellingContainer);

        _spellingInput = new LineEdit
        {
            PlaceholderText = "请输入英文单词...",
            CustomMinimumSize = new Vector2(400, 46),
            ProcessMode = ProcessModeEnum.Always
        };
        _spellingInput.AddThemeFontSizeOverride("font_size", 20);
        _spellingInput.TextSubmitted += _ => OnSpellingSubmit();
        _spellingContainer.AddChild(_spellingInput);

        _spellingSubmitBtn = new Button { Text = "  确认  " };
        _spellingSubmitBtn.AddThemeFontSizeOverride("font_size", 16);
        _spellingSubmitBtn.CustomMinimumSize = new Vector2(100, 46);
        _spellingSubmitBtn.Pressed += OnSpellingSubmit;
        _spellingContainer.AddChild(_spellingSubmitBtn);

        // 听力播放按钮（备用：题目区显示后再放一个）
        _listenContainer = new HBoxContainer { Visible = false };
        mainVBox.AddChild(_listenContainer);
        var listenCenter = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _listenContainer.AddChild(listenCenter);
        _listenBtn = GameTheme.MakeButton("  🔊  播放发音  ", 20, GameTheme.Gold);
        _listenBtn.CustomMinimumSize = new Vector2(240, 56);
        _listenBtn.Pressed += OnListenPressed;
        listenCenter.AddChild(_listenBtn);

        // 反馈
        _feedbackLabel = GameTheme.MakeLabel("", 20, TextWhite, HorizontalAlignment.Center);
        mainVBox.AddChild(_feedbackLabel);

        // 统计
        _statsLabel = GameTheme.MakeLabel("", 12, TextGrey, HorizontalAlignment.Center);
        mainVBox.AddChild(_statsLabel);

        // 继续按钮
        var confirmContainer = new CenterContainer();
        mainVBox.AddChild(confirmContainer);
        _confirmButton = new Button
        {
            Text = "  继续 (Enter)  ",
            CustomMinimumSize = new Vector2(200, 44),
            Visible = false
        };
        _confirmButton.AddThemeStyleboxOverride("normal", MakeGoldButtonStyle(0.2f));
        _confirmButton.AddThemeStyleboxOverride("hover", MakeGoldButtonStyle(0.35f));
        _confirmButton.AddThemeColorOverride("font_color", AccentGold);
        _confirmButton.AddThemeFontSizeOverride("font_size", 18);
        _confirmButton.Pressed += OnConfirmPressed;
        confirmContainer.AddChild(_confirmButton);
    }

    public void ShowQuiz(QuizQuestion question, Action<bool> onAnswered)
    {
        _currentQuestion = question;
        _onAnswered = onAnswered;
        _answered = false;
        _lastCorrect = false;

        var modeText = question.Mode switch
        {
            QuizModeFlags.EnglishToChinese => "英 → 中",
            QuizModeFlags.ChineseToEnglish => "中 → 英",
            QuizModeFlags.SpellEnglish => "中 → 英 (拼写)",
            QuizModeFlags.ListenToChinese => "🔊 听力模式",
            _ => ""
        };
        if (VocabConfig.Instance.EnableDifficultyScaling)
        {
            var tier = Math.Clamp(GameBridge.GetCurrentAct(), 1, 3);
            var tierName = tier switch { 1 => "基础", 2 => "进阶", _ => "挑战" };
            modeText += $"  [{tierName}]";
        }
        _modeLabel.Text = modeText;
        _promptLabel.Text = question.Prompt;

        _spellingContainer.Visible = question.IsSpelling;

        if (question.IsSpelling)
        {
            _choiceWidget.Hide();
            _listenContainer.Visible = false;
            _promptLabel.Visible = true;

            // 简单模式：显示中间挖空的掩码提示
            var hasHint = !string.IsNullOrEmpty(question.SpellingHint);
            _spellingHintLabel.Visible = hasHint;
            if (hasHint) _spellingHintLabel.Text = question.SpellingHint;

            // 朗读按钮（复用听力模式 TTS）：可选开关，不自动播放，由玩家点击
            _listenPlayTop.Visible = VocabConfig.Instance.SpellingPlayAudio;

            _spellingInput.Text = "";
            _spellingInput.Editable = true;
            _spellingSubmitBtn.Disabled = false;
            _spellingInput.CallDeferred(LineEdit.MethodName.GrabFocus);
        }
        else
        {
            // 选择题 / 听力题 —— 选项区交给共享组件
            _spellingHintLabel.Visible = false;
            _choiceWidget.ShowQuestion(question, OnChoiceAnswered);

            if (question.IsListening)
            {
                _listenContainer.Visible = false;
                _listenPlayTop.Visible = true;
                if (question.IsMultiSelect)
                {
                    _promptLabel.Visible = true;
                    _promptLabel.Text = "【多选题】";
                }
                else
                {
                    _promptLabel.Visible = false;
                }
                TtsService.Instance.Speak(question.TargetWord.English);
            }
            else
            {
                _promptLabel.Visible = true;
                _listenPlayTop.Visible = false;
            }
        }

        _feedbackLabel.Text = "";
        _confirmButton.Visible = false;
        UpdateStats();
        Visible = true;
    }

    // ── 选择题作答（由 ChoiceAnswerWidget 处理选项+提交，本处只做记账和反馈文案）──

    private void OnChoiceAnswered(bool correct, IReadOnlyCollection<int> selectedIndices)
    {
        if (_currentQuestion is null) return;

        _answered = true;
        _answeredAtMsec = Time.GetTicksMsec();
        _lastCorrect = correct;
        VocabManager.Instance.RecordAnswer(_currentQuestion.TargetWord, correct);

        var isMulti = _currentQuestion.IsMultiSelect;
        var correctText = isMulti
            ? string.Join(" | ", _currentQuestion.CorrectIndices.Select(i => _currentQuestion.Options[i]))
            : (_currentQuestion.CorrectIndex >= 0 ? _currentQuestion.Options[_currentQuestion.CorrectIndex] : "");
        var userText = selectedIndices.Count > 0
            ? string.Join("|", selectedIndices.Select(i => _currentQuestion.Options[i]))
            : "";

        if (correct)
        {
            ShowFeedback(true, null);
        }
        else
        {
            var extra = _currentQuestion.IsListening
                ? $"\n单词：{_currentQuestion.TargetWord.English}"
                : "";
            ShowFeedback(false, correctText + extra);

            // 错题详情：仅单选时取选项 detail；多选时不带 detail
            var userDetail = !isMulti && selectedIndices.Count == 1
                ? (_currentQuestion.GetDetail(selectedIndices.First()) ?? "")
                : "";
            var correctDetail = !isMulti && _currentQuestion.CorrectIndex >= 0
                ? (_currentQuestion.GetDetail(_currentQuestion.CorrectIndex) ?? "")
                : _currentQuestion.TargetWord.English;
            RecordWrong(userText, correctText, userDetail, correctDetail);
        }

        RecordToRunTracker(correct, correct ? "" : userText, correctText);

        _listenContainer.Visible = false;
        _listenPlayTop.Visible = false;
        _promptLabel.Visible = true;
        if (_currentQuestion.IsListening)
            _promptLabel.Text = _currentQuestion.TargetWord.English;

        UpdateStats();
        _confirmButton.Visible = true;
    }

    // ── 拼写题作答 ──

    private void OnSpellingSubmit()
    {
        if (_answered || _currentQuestion is null) return;

        var userInput = _spellingInput.Text.Trim();
        if (string.IsNullOrEmpty(userInput)) return;

        _answered = true;
        _answeredAtMsec = Time.GetTicksMsec();
        _lastCorrect = _currentQuestion.CheckSpelling(userInput);
        VocabManager.Instance.RecordAnswer(_currentQuestion.TargetWord, _lastCorrect);

        _spellingInput.Editable = false;
        _spellingSubmitBtn.Disabled = true;
        _spellingHintLabel.Visible = false;
        _listenPlayTop.Visible = false;

        if (_lastCorrect)
        {
            ShowFeedback(true, null);
        }
        else
        {
            ShowFeedback(false, _currentQuestion.CorrectText);
            RecordWrong(userInput, _currentQuestion.CorrectText,
                "", _currentQuestion.TargetWord.Chinese);
        }
        RecordToRunTracker(_lastCorrect, userInput, _currentQuestion.CorrectText);

        UpdateStats();
        _confirmButton.Visible = true;
    }

    // ── 反馈和错题记录 ──

    private void ShowFeedback(bool correct, string? correctAnswer)
    {
        if (correct)
        {
            _feedbackLabel.Text = "回答正确！";
            _feedbackLabel.AddThemeColorOverride("font_color", CorrectGreen);
        }
        else
        {
            _feedbackLabel.Text = $"回答错误！正确答案：{correctAnswer}";
            _feedbackLabel.AddThemeColorOverride("font_color", WrongRed);
        }
    }

    private void RecordWrong(string userAnswer, string correctAnswer,
        string userDetail = "", string correctDetail = "")
    {
        if (_currentQuestion is null) return;
        WrongAnswerTracker.Instance.RecordWrongAnswer(new WrongAnswerRecord(
            _currentQuestion.TargetWord,
            _currentQuestion.Mode,
            _currentQuestion.Prompt,
            userAnswer,
            correctAnswer,
            userDetail,
            correctDetail
        ));
    }

    private void RecordToRunTracker(bool correct, string userAnswer, string correctAnswer)
    {
        if (_currentQuestion is null) return;
        var energyCost = 0;
        if (!correct)
        {
            try { energyCost = _currentQuestion.TargetWord.EnergyLost; } catch { }
        }
        Services.RunQuizTracker.Instance.Record(new Models.RunQuizRecord
        {
            English = _currentQuestion.TargetWord.English,
            Chinese = _currentQuestion.TargetWord.Chinese,
            Mode = _currentQuestion.Mode.ToString(),
            Correct = correct,
            UserAnswer = userAnswer,
            CorrectAnswer = correctAnswer,
            EnergyCost = correct ? 0 : energyCost
        });
    }

    // ── 确认和关闭 ──

    private void OnConfirmPressed()
    {
        _confirmButton.Visible = false;
        CloseQuiz(_lastCorrect);
    }

    private void CloseQuiz(bool correct)
    {
        Visible = false;
        _onAnswered?.Invoke(correct);
        _currentQuestion = null;
        _onAnswered = null;
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;
        if (@event is not InputEventKey { Pressed: true } key) return;

        // 已作答：Enter 继续（至少 500ms 防 IME 双触发）
        if (_answered)
        {
            if (key.Keycode is Key.Enter or Key.Space or Key.KpEnter)
            {
                if (Time.GetTicksMsec() - _answeredAtMsec > 500)
                {
                    OnConfirmPressed();
                }
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (_currentQuestion is null) return;

        // 拼写模式不拦截字母键
        if (_currentQuestion.IsSpelling) return;

        // Enter 触发提交（前提是已经选中）
        if (key.Keycode is Key.Enter or Key.KpEnter)
        {
            if (_choiceWidget.TrySubmit())
            {
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        // A-H / 1-8 → 切换选项
        var idx = key.Keycode switch
        {
            Key.A or Key.Key1 => 0,
            Key.B or Key.Key2 => 1,
            Key.C or Key.Key3 => 2,
            Key.D or Key.Key4 => 3,
            Key.E or Key.Key5 => 4,
            Key.F or Key.Key6 => 5,
            Key.G or Key.Key7 => 6,
            Key.H or Key.Key8 => 7,
            _ => -1
        };
        if (idx >= 0 && _choiceWidget.HandleKeyOption(idx))
        {
            GetViewport().SetInputAsHandled();
        }
    }

    private void OnListenPressed()
    {
        if (_currentQuestion is null) return;
        TtsService.Instance.Speak(_currentQuestion.TargetWord.English);
    }

    private static StyleBoxFlat MakeGoldButtonStyle(float alpha)
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(AccentGold.R, AccentGold.G, AccentGold.B, alpha),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderColor = AccentGold,
            ContentMarginLeft = 16, ContentMarginRight = 16,
            ContentMarginTop = 8, ContentMarginBottom = 8
        };
    }

    private void UpdateStats()
    {
        var c = VocabConfig.Instance;
        var pct = c.TotalAnswered > 0 ? $"{c.OverallAccuracy:P0}" : "--";
        _statsLabel.Text = $"已答题：{c.TotalAnswered}  |  正确率：{pct}";
    }

    public static void Create()
    {
        var root = GameBridge.GetUIRoot();
        if (root is null) return;
        var panel = new QuizPanel
        {
            Name = "VocabSpireQuizPanel",
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        };
        root.AddChild(panel);
    }
}
