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
    private RecallCardWidget _recallWidget = null!;
    private HBoxContainer _spellingContainer = null!;
    private Label _spellingHintLabel = null!;
    private LineEdit _spellingInput = null!;
    private Button _spellingSubmitBtn = null!;
    private Button _spellingForgotBtn = null!;
    private Button _confirmButton = null!;
    private Label _padHint = null!;      // 拼写题 / 已作答时的手柄提示（选择题与回忆卡片由各自组件负责）
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

        // 回忆卡片答题区（共享组件 —— 翻面 + 自评）
        _recallWidget = new RecallCardWidget { Visible = false };
        mainVBox.AddChild(_recallWidget);

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

        // 拼写题的「忘了」：拼不出来时直接认错看答案（与选择题的忘了按钮同一开关）
        _spellingForgotBtn = new Button { Text = "  🤔 忘了  " };
        _spellingForgotBtn.AddThemeFontSizeOverride("font_size", 16);
        _spellingForgotBtn.CustomMinimumSize = new Vector2(110, 46);
        _spellingForgotBtn.FocusMode = FocusModeEnum.None;   // 不抢输入框焦点
        _spellingForgotBtn.TooltipText = "拼不出来时点这里：直接判错并显示正确拼写，不用瞎填。可在设置中关闭。";
        _spellingForgotBtn.Pressed += OnSpellingForgot;
        _spellingContainer.AddChild(_spellingForgotBtn);

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

        // 手柄提示（没插手柄时整行隐藏）
        _padHint = GameTheme.MakeLabel("", 12, GameTheme.MidGray, HorizontalAlignment.Center);
        _padHint.Visible = false;
        mainVBox.AddChild(_padHint);

        // 继续按钮
        var confirmContainer = new CenterContainer();
        mainVBox.AddChild(confirmContainer);
        _confirmButton = new Button
        {
            Text = "  继续  ",
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
        // 应用界面字体倍率（幂等，按基准字号重算；保证没开过设置面板也生效）
        GameTheme.ApplyFontScaleRecursive(this, VocabConfig.Instance.UiFontScale);

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
            QuizModeFlags.RecallCard => "🧠 回忆卡片",
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

        if (question.IsRecall)
        {
            // 回忆卡片：正面只有单词，翻面后自评（无选项、无输入）
            _choiceWidget.Hide();
            _spellingHintLabel.Visible = false;
            _listenContainer.Visible = false;
            _promptLabel.Visible = true;
            _listenPlayTop.Visible = VocabConfig.Instance.EnToCnPlayAudio;  // 单词就在正面，朗读不算泄题
            _recallWidget.ShowQuestion(question, OnRecallAnswered);
        }
        else if (question.IsSpelling)
        {
            _choiceWidget.Hide();
            _recallWidget.Hide();
            _spellingForgotBtn.Visible = VocabConfig.Instance.ShowForgotButton;
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
            _spellingForgotBtn.Disabled = false;
            _spellingInput.CallDeferred(LineEdit.MethodName.GrabFocus);
        }
        else
        {
            // 选择题 / 听力题 —— 选项区交给共享组件
            _spellingHintLabel.Visible = false;
            _recallWidget.Hide();
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
                // 英→中选择题：可选朗读按钮（复用听力 TTS，不自动播放，玩家点击才发音）。
                // 中→英不显示——题目是中文、答案才是英文，播放会直接读出答案。
                _listenPlayTop.Visible = question.Mode == QuizModeFlags.EnglishToChinese
                                         && VocabConfig.Instance.EnToCnPlayAudio
                                         && !question.IsFixedChoice;   // 固定选择题题干是中文，不显示朗读
            }
        }

        _padHint.Visible = question.IsSpelling && Services.GamepadInput.IsPresent();
        if (_padHint.Visible)
            _padHint.Text = Services.GamepadInput.HintSpelling(VocabConfig.Instance.ShowForgotButton);

        _feedbackLabel.Text = "";
        _confirmButton.Text = $"  继续 ({KeyBindButton.KeyName(VocabConfig.Instance.ContinueKey)})  ";
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
        var forgot = _choiceWidget.LastAnswerWasForgot;
        var userText = selectedIndices.Count > 0
            ? string.Join("|", selectedIndices.Select(i => _currentQuestion.Options[i]))
            : (forgot ? "（忘了）" : "");

        if (correct)
        {
            ShowFeedback(true, null);
        }
        else
        {
            var extra = _currentQuestion.IsListening
                ? $"\n单词：{_currentQuestion.TargetWord.English}"
                : "";
            ShowFeedback(false, correctText + extra, forgot);

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
        ShowContinuePadHint();
        _confirmButton.Visible = true;
    }

    // ── 回忆卡片作答（自评）──

    /// <summary>玩家翻面后自评。remembered=true 记作答对，false 记作答错（与其他题型同一套记忆引擎记账）。</summary>
    private void OnRecallAnswered(bool remembered)
    {
        if (_currentQuestion is null) return;

        _answered = true;
        _answeredAtMsec = Time.GetTicksMsec();
        _lastCorrect = remembered;
        VocabManager.Instance.RecordAnswer(_currentQuestion.TargetWord, remembered);

        _listenPlayTop.Visible = false;

        if (remembered)
        {
            _feedbackLabel.Text = "✅ 记住了";
            _feedbackLabel.AddThemeColorOverride("font_color", CorrectGreen);
        }
        else
        {
            ShowFeedback(false, _currentQuestion.CorrectText, forgot: true);
            RecordWrong("（没想起来）", _currentQuestion.CorrectText,
                "", _currentQuestion.TargetWord.English);
        }

        RecordToRunTracker(remembered, remembered ? "" : "（没想起来）", _currentQuestion.CorrectText);

        UpdateStats();
        ShowContinuePadHint();
        _confirmButton.Visible = true;
    }

    // ── 拼写题作答 ──

    /// <summary>拼写题点「忘了」：不填直接判错并显示正确拼写。</summary>
    private void OnSpellingForgot()
    {
        if (_answered || _currentQuestion is null) return;

        _answered = true;
        _answeredAtMsec = Time.GetTicksMsec();
        _lastCorrect = false;
        VocabManager.Instance.RecordAnswer(_currentQuestion.TargetWord, false);

        if (VocabConfig.Instance.AutoSpeakOnAnswer)
            TtsService.Instance.Speak(_currentQuestion.TargetWord.English);

        _spellingInput.Editable = false;
        _spellingSubmitBtn.Disabled = true;
        _spellingForgotBtn.Disabled = true;
        _spellingHintLabel.Visible = false;
        _listenPlayTop.Visible = false;

        ShowFeedback(false, _currentQuestion.CorrectText, forgot: true);
        RecordWrong("（忘了）", _currentQuestion.CorrectText, "", _currentQuestion.TargetWord.Chinese);
        RecordToRunTracker(false, "（忘了）", _currentQuestion.CorrectText);

        UpdateStats();
        ShowContinuePadHint();
        _confirmButton.Visible = true;
    }

    private void OnSpellingSubmit()
    {
        if (_answered || _currentQuestion is null) return;

        var userInput = _spellingInput.Text.Trim();
        if (string.IsNullOrEmpty(userInput)) return;

        _answered = true;
        _answeredAtMsec = Time.GetTicksMsec();
        _lastCorrect = _currentQuestion.CheckSpelling(userInput);
        VocabManager.Instance.RecordAnswer(_currentQuestion.TargetWord, _lastCorrect);

        // 答完自动朗读本题单词（拼写题）
        if (VocabConfig.Instance.AutoSpeakOnAnswer)
            TtsService.Instance.Speak(_currentQuestion.TargetWord.English);

        _spellingInput.Editable = false;
        _spellingSubmitBtn.Disabled = true;
        _spellingForgotBtn.Disabled = true;
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
        ShowContinuePadHint();
        _confirmButton.Visible = true;
    }

    // ── 反馈和错题记录 ──

    /// <summary>forgot=true 表示玩家主动点了「忘了 / 没想起来」——按答错处理，但文案不说「回答错误」。</summary>
    private void ShowFeedback(bool correct, string? correctAnswer, bool forgot = false)
    {
        if (correct)
        {
            _feedbackLabel.Text = "回答正确！";
            _feedbackLabel.AddThemeColorOverride("font_color", CorrectGreen);
        }
        else
        {
            _feedbackLabel.Text = forgot
                ? $"没关系，记住它：{correctAnswer}"
                : $"回答错误！正确答案：{correctAnswer}";
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
        _padHint.Visible = false;
        _onAnswered?.Invoke(correct);
        _currentQuestion = null;
        _onAnswered = null;
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;

        // 手柄优先：面板显示期间独占手柄输入，别让按键漏到背后的牌桌
        var pad = Services.GamepadInput.Translate(@event);
        if (pad != PadAction.None && HandlePad(pad))
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is not InputEventKey { Pressed: true } key) return;

        // 已作答：Enter 继续（至少 500ms 防 IME 双触发）
        if (_answered)
        {
            if (VocabConfig.KeyMatches(key.Keycode, VocabConfig.Instance.ContinueKey))
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

        // 提交键：回忆卡片 → 翻面；选择题 → 提交
        if (VocabConfig.KeyMatches(key.Keycode, VocabConfig.Instance.SubmitKey))
        {
            var handled = _currentQuestion.IsRecall ? _recallWidget.TryReveal() : _choiceWidget.TrySubmit();
            if (handled) GetViewport().SetInputAsHandled();
            return;
        }

        // 0 → 「忘了」（选择题；回忆卡片用自评按钮，不需要）
        if (key.Keycode is Key.Key0 or Key.Kp0 && !_currentQuestion.IsRecall)
        {
            if (_choiceWidget.TryForgot()) GetViewport().SetInputAsHandled();
            return;
        }

        // A-H / 1-8 → 切换选项（回忆卡片：翻面后 1/A=想起来了，2/B=没想起来）
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
        if (idx < 0) return;
        var consumed = _currentQuestion.IsRecall
            ? _recallWidget.HandleKeyOption(idx)
            : _choiceWidget.HandleKeyOption(idx);
        if (consumed) GetViewport().SetInputAsHandled();
    }

    /// <summary>
    /// 手柄操作。拼写题需要打字，手柄只提供「忘了」这一条出路（X 键），其余方向/确认不接管。
    /// </summary>
    private bool HandlePad(PadAction pad)
    {
        // 已作答：A 键 = 继续（沿用键盘那套 500ms 防连发，避免自评/提交那一下被吃掉当成继续）
        if (_answered)
        {
            if (pad is PadAction.Accept or PadAction.Submit)
            {
                if (Time.GetTicksMsec() - _answeredAtMsec > 500) OnConfirmPressed();
                return true;
            }
            return false;
        }

        if (_currentQuestion is null) return false;

        if (_currentQuestion.IsRecall)
        {
            return pad switch
            {
                PadAction.Accept or PadAction.Left => _recallWidget.PadAccept(),
                PadAction.Forgot or PadAction.Right => _recallWidget.PadForgot(),
                _ => false
            };
        }

        if (_currentQuestion.IsSpelling)
        {
            if (pad == PadAction.Forgot && VocabConfig.Instance.ShowForgotButton)
            {
                OnSpellingForgot();
                return true;
            }
            return false;   // 拼写题得打字，方向键/A 不接管
        }

        return pad switch
        {
            PadAction.Up => _choiceWidget.MoveCursor(-1),
            PadAction.Down => _choiceWidget.MoveCursor(1),
            PadAction.Accept => _choiceWidget.PadAccept(),
            PadAction.Submit => _choiceWidget.TrySubmit(),
            PadAction.Forgot => _choiceWidget.TryForgot(),
            _ => false
        };
    }

    /// <summary>作答完成后把提示切成「[A] 继续」（三种题型共用）。</summary>
    private void ShowContinuePadHint()
    {
        _padHint.Visible = Services.GamepadInput.IsPresent();
        if (_padHint.Visible) _padHint.Text = Services.GamepadInput.HintContinue();
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
        var bt = BattleStateTracker.Instance;
        var parts = new List<string>();

        // 连对 / 连错
        if (bt.CorrectStreak > 0) parts.Add($"🔥连对 {bt.CorrectStreak}");
        if (bt.WrongStreak > 0) parts.Add($"💔连错 {bt.WrongStreak}");

        // 奖励规则：算出每条还差几次触发（只显示已启用 + 距离最近的前 2 条）
        if (c.RewardEnabled)
        {
            var hints = new List<(int gap, string text)>();
            foreach (var rule in c.RewardRules)
            {
                if (!rule.Enabled || rule.Kind == RewardType.None || rule.Amount <= 0 || rule.Streak <= 0) continue;
                var streak = bt.CorrectStreak;
                int gap;
                switch (rule.Mode)
                {
                    case Models.RewardTriggerMode.Once:
                        gap = rule.Streak - streak;
                        if (gap <= 0) continue;
                        break;
                    case Models.RewardTriggerMode.Recurring:
                        gap = rule.Streak - streak;
                        if (gap <= 0) gap = 0;
                        break;
                    case Models.RewardTriggerMode.EveryN:
                        gap = rule.Streak <= 0 ? 999 : rule.Streak - (streak % rule.Streak);
                        if (gap == rule.Streak && streak > 0 && streak % rule.Streak == 0) gap = rule.Streak;
                        break;
                    default: continue;
                }
                var kindName = rule.Kind switch
                {
                    RewardType.Hp => "回血", RewardType.Energy => "能量", RewardType.Gold => "金币",
                    RewardType.Strength => "力量", RewardType.Dexterity => "敏捷", RewardType.Block => "覆甲",
                    RewardType.Draw => "抽牌", RewardType.Replay => "重放", _ => rule.Kind.ToString()
                };
                if (gap <= 0)
                    hints.Add((0, $"✨{kindName}+{rule.Amount}"));
                else
                    hints.Add((gap, $"{kindName} 还差{gap}题"));
            }
            hints.Sort((a, b) => a.gap.CompareTo(b.gap));
            foreach (var h in hints.Take(2)) parts.Add(h.text);
        }

        parts.Add($"已答题：{c.TotalAnswered}");
        parts.Add($"正确率：{pct}");
        _statsLabel.Text = string.Join("  |  ", parts);
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
