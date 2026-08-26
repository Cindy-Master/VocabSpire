using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 篝火（休息点）错题复习面板 —— 逐题重练上一区间的错题。
/// 选择题部分委托给 ChoiceAnswerWidget；本类只负责骨架（标题、上次错误提示、跳过、下一题）。
/// </summary>
public partial class RestSiteReviewPanel : Control
{
    public static RestSiteReviewPanel? Instance { get; private set; }

    private Label _titleLabel = null!;
    private Label _promptLabel = null!;
    private Label _feedbackLabel = null!;
    private ChoiceAnswerWidget _choiceWidget = null!;
    private RecallCardWidget _recallWidget = null!;
    private Button _spellingForgotBtn = null!;
    private HBoxContainer _spellingContainer = null!;
    private LineEdit _spellingInput = null!;
    private Button _spellingSubmitBtn = null!;
    private Button _nextBtn = null!;
    private Label _padHint = null!;
    private Button _skipBtn = null!;
    private Label _skipConfirmLabel = null!;
    private Button _skipConfirmYes = null!;
    private Button _skipConfirmNo = null!;
    private Control _skipConfirmGroup = null!;

    private IReadOnlyList<WrongAnswerRecord> _records = Array.Empty<WrongAnswerRecord>();
    private int _currentIndex;
    private bool _answered;
    private Action? _onComplete;
    private QuizQuestion? _currentReviewQuiz;

    private static readonly Color BgColor = GameTheme.DarkBg;
    private static readonly Color Gold = GameTheme.Gold;
    private static readonly Color White = GameTheme.Cream;
    private static readonly Color Grey = GameTheme.LightGray;
    private static readonly Color CorrectGreen = GameTheme.Green;
    private static readonly Color WrongRed = GameTheme.Red;
    private static readonly Color SkipColor = GameTheme.MidGray;

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
            Color = new Color(0, 0, 0, 0.55f),
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
        var style = new StyleBoxFlat
        {
            BgColor = BgColor,
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderColor = Gold,
            ContentMarginTop = 28, ContentMarginBottom = 28,
            ContentMarginLeft = 36, ContentMarginRight = 36
        };
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 16);
        panel.AddChild(mainVBox);

        _titleLabel = GameTheme.MakeLabel("篝火错题复习", 20, Gold, HorizontalAlignment.Center);
        mainVBox.AddChild(_titleLabel);
        mainVBox.AddChild(new HSeparator());

        _promptLabel = GameTheme.MakeLabel("", 28, White, HorizontalAlignment.Center);
        _promptLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        mainVBox.AddChild(_promptLabel);

        // 发音按钮：随时点随时读本题单词（复用 TTS）
        var speakCenter = new CenterContainer();
        mainVBox.AddChild(speakCenter);
        var speakBtn = GameTheme.MakeButton("  🔊 发音  ", 16, Gold);
        speakBtn.CustomMinimumSize = new Vector2(140, 44);
        speakBtn.FocusMode = FocusModeEnum.None;   // 不抢键盘焦点（拼写输入/快捷键不受影响）
        speakBtn.Pressed += () =>
        {
            var en = _currentReviewQuiz?.TargetWord.English
                     ?? (_currentIndex >= 0 && _currentIndex < _records.Count ? _records[_currentIndex].Word.English : null);
            if (!string.IsNullOrEmpty(en)) TtsService.Instance.Speak(en);
        };
        speakCenter.AddChild(speakBtn);

        // 选择题答题区（共享组件 —— 单选/多选共用、提交按钮内置）
        _choiceWidget = new ChoiceAnswerWidget { Visible = false };
        mainVBox.AddChild(_choiceWidget);

        // 回忆卡片答题区（共享组件 —— 翻面 + 自评）
        _recallWidget = new RecallCardWidget { Visible = false };
        mainVBox.AddChild(_recallWidget);

        // 拼写输入区（拼写模式时显示）
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

        // 拼写复习的「忘了」：拼不出来直接认错看答案（与战斗答题面板同一开关）
        _spellingForgotBtn = new Button { Text = "  🤔 忘了  " };
        _spellingForgotBtn.AddThemeFontSizeOverride("font_size", 16);
        _spellingForgotBtn.CustomMinimumSize = new Vector2(110, 46);
        _spellingForgotBtn.FocusMode = FocusModeEnum.None;
        _spellingForgotBtn.Pressed += OnSpellingForgot;
        _spellingContainer.AddChild(_spellingForgotBtn);

        _feedbackLabel = GameTheme.MakeLabel("", 18, White, HorizontalAlignment.Center);
        mainVBox.AddChild(_feedbackLabel);

        _padHint = GameTheme.MakeLabel("", 12, GameTheme.MidGray, HorizontalAlignment.Center);
        _padHint.Visible = false;
        mainVBox.AddChild(_padHint);

        var btnCenter = new CenterContainer();
        mainVBox.AddChild(btnCenter);
        _nextBtn = new Button
        {
            Text = "  下一题  ",
            CustomMinimumSize = new Vector2(200, 44),
            Visible = false
        };
        _nextBtn.AddThemeColorOverride("font_color", Gold);
        _nextBtn.AddThemeFontSizeOverride("font_size", 16);
        _nextBtn.Pressed += ShowNextWord;
        btnCenter.AddChild(_nextBtn);

        BuildSkipSection();
    }

    private void BuildSkipSection()
    {
        var skipContainer = new VBoxContainer
        {
            LayoutMode = 1,
            AnchorLeft = 1f, AnchorRight = 1f,
            AnchorTop = 0f, AnchorBottom = 0f,
            OffsetLeft = -180, OffsetRight = -20,
            OffsetTop = 20, OffsetBottom = 120
        };
        skipContainer.AddThemeConstantOverride("separation", 6);
        AddChild(skipContainer);

        _skipBtn = new Button
        {
            Text = "  跳过复习  ",
            CustomMinimumSize = new Vector2(140, 36)
        };
        _skipBtn.AddThemeFontSizeOverride("font_size", 16);
        _skipBtn.AddThemeColorOverride("font_color", SkipColor);
        _skipBtn.Pressed += OnSkipPressed;
        skipContainer.AddChild(_skipBtn);

        _skipConfirmGroup = new VBoxContainer { Visible = false };
        ((VBoxContainer)_skipConfirmGroup).AddThemeConstantOverride("separation", 4);
        skipContainer.AddChild(_skipConfirmGroup);

        _skipConfirmLabel = GameTheme.MakeLabel("确定跳过？", 12, WrongRed, HorizontalAlignment.Center);
        _skipConfirmGroup.AddChild(_skipConfirmLabel);

        var confirmRow = new HBoxContainer();
        confirmRow.AddThemeConstantOverride("separation", 8);
        _skipConfirmGroup.AddChild(confirmRow);

        _skipConfirmYes = new Button { Text = " 确定 ", CustomMinimumSize = new Vector2(60, 30) };
        _skipConfirmYes.AddThemeFontSizeOverride("font_size", 12);
        _skipConfirmYes.AddThemeColorOverride("font_color", WrongRed);
        _skipConfirmYes.Pressed += Complete;
        confirmRow.AddChild(_skipConfirmYes);

        _skipConfirmNo = new Button { Text = " 取消 ", CustomMinimumSize = new Vector2(60, 30) };
        _skipConfirmNo.AddThemeFontSizeOverride("font_size", 12);
        _skipConfirmNo.Pressed += CancelSkip;
        confirmRow.AddChild(_skipConfirmNo);
    }

    private void OnSkipPressed()
    {
        _skipBtn.Visible = false;
        _skipConfirmGroup.Visible = true;
    }

    private void CancelSkip()
    {
        _skipConfirmGroup.Visible = false;
        _skipBtn.Visible = true;
    }

    public void ShowReview(IReadOnlyList<WrongAnswerRecord> records, Action? onComplete = null)
    {
        _records = records;
        _onComplete = onComplete;
        _currentIndex = 0;
        _skipBtn.Visible = true;
        _skipConfirmGroup.Visible = false;
        Visible = true;
        ShowCurrentWord();
    }

    private void ShowCurrentWord()
    {
        if (_currentIndex >= _records.Count)
        {
            Complete();
            return;
        }

        _answered = false;
        var record = _records[_currentIndex];
        var bank = VocabManager.Instance.ActiveBank;

        _titleLabel.Text = $"篝火错题复习  ({_currentIndex + 1}/{_records.Count})";
        _promptLabel.Text = $"{record.Word.English}\n{record.Word.Chinese}";

        // 答题前只提示「这是错题」，绝不显示正确答案 —— 复习选项里就含正确答案，
        // 提前显示等于直接把答案告诉玩家。上次答错详情留到答题后再展示。
        _feedbackLabel.Text = "这是你之前答错的单词，再做一次 ✍️";
        _feedbackLabel.AddThemeColorOverride("font_color", Grey);

        _currentReviewQuiz = null;
        if (bank is not null && bank.IsValid)
        {
            var reviewMode = VocabConfig.Instance.ReviewQuizMode;
            var quiz = new QuizGenerator().GenerateForWord(
                record.Word, bank, reviewMode, VocabConfig.Instance.OptionCount);
            if (quiz is not null)
            {
                _currentReviewQuiz = quiz;
                _promptLabel.Text = quiz.Prompt;

                if (quiz.IsRecall)
                {
                    _padHint.Visible = false;
                    _choiceWidget.Hide();
                    _spellingContainer.Visible = false;
                    _recallWidget.ShowQuestion(quiz, OnReviewRecallAnswered);
                }
                else if (quiz.IsSpelling)
                {
                    _choiceWidget.Hide();
                    _recallWidget.Hide();
                    _spellingContainer.Visible = true;
                    _spellingInput.Text = "";
                    _spellingInput.Editable = true;
                    _spellingSubmitBtn.Disabled = false;
                    _spellingForgotBtn.Visible = VocabConfig.Instance.ShowForgotButton;
                    _spellingForgotBtn.Disabled = false;
                    _padHint.Visible = Services.GamepadInput.IsPresent();
                    if (_padHint.Visible)
                        _padHint.Text = Services.GamepadInput.HintSpelling(VocabConfig.Instance.ShowForgotButton);
                    _spellingInput.CallDeferred(LineEdit.MethodName.GrabFocus);
                }
                else
                {
                    _padHint.Visible = false;
                    _recallWidget.Hide();
                    _spellingContainer.Visible = false;
                    _choiceWidget.ShowQuestion(quiz, OnReviewChoiceAnswered);
                }
            }
        }

        _nextBtn.Visible = false;
        CancelSkip();
    }

    private void OnReviewChoiceAnswered(bool correct, IReadOnlyCollection<int> selectedIndices)
    {
        if (_currentReviewQuiz is null) return;
        _answered = true;

        // 篝火复习也是一次真实的「提取练习」——必须回写记忆引擎（升/降 Box、Streak、DueTick），
        // 否则复习答对了掌握度纹丝不动、词永远当错题反复出，复习等于白做。
        VocabManager.Instance.RecordAnswer(_currentReviewQuiz.TargetWord, correct);

        if (correct)
            SetFeedback("回答正确！", true);
        else if (_choiceWidget.LastAnswerWasForgot)
            SetFeedback($"没关系，记住它：{_currentReviewQuiz.CorrectText}", false);
        else
            SetFeedback("回答错误！", false);

        ShowNextButton();
    }

    /// <summary>回忆卡片复习：玩家翻面后自评，结果同样回写记忆引擎。</summary>
    private void OnReviewRecallAnswered(bool remembered)
    {
        if (_currentReviewQuiz is null) return;
        _answered = true;

        VocabManager.Instance.RecordAnswer(_currentReviewQuiz.TargetWord, remembered);

        SetFeedback(remembered ? "✅ 记住了" : $"没关系，记住它：{_currentReviewQuiz.CorrectText}", remembered);
        ShowNextButton();
    }

    private void SetFeedback(string text, bool positive)
    {
        _feedbackLabel.Text = text;
        _feedbackLabel.AddThemeColorOverride("font_color", positive ? CorrectGreen : WrongRed);
    }

    private void ShowNextButton()
    {
        _padHint.Visible = Services.GamepadInput.IsPresent();
        if (_padHint.Visible) _padHint.Text = Services.GamepadInput.HintContinue();
        _nextBtn.Visible = true;
        var contKey = KeyBindButton.KeyName(VocabConfig.Instance.ContinueKey);
        _nextBtn.Text = _currentIndex >= _records.Count - 1
            ? $"  完成复习 ({contKey})  "
            : $"  下一题 ({contKey})  ";
    }

    /// <summary>拼写复习点「忘了」：不填直接判错并显示正确拼写。</summary>
    private void OnSpellingForgot()
    {
        if (_answered || _currentReviewQuiz is null) return;
        _answered = true;

        VocabManager.Instance.RecordAnswer(_currentReviewQuiz.TargetWord, false);
        if (VocabConfig.Instance.AutoSpeakOnAnswer)
            TtsService.Instance.Speak(_currentReviewQuiz.TargetWord.English);

        _spellingInput.Editable = false;
        _spellingSubmitBtn.Disabled = true;
        _spellingForgotBtn.Disabled = true;

        SetFeedback($"没关系，记住它：{_currentReviewQuiz.CorrectText}", false);
        ShowNextButton();
    }

    private void OnSpellingSubmit()
    {
        if (_answered || _currentReviewQuiz is null) return;

        var userInput = _spellingInput.Text.Trim();
        if (string.IsNullOrEmpty(userInput)) return;

        _answered = true;
        var correct = _currentReviewQuiz.CheckSpelling(userInput);

        // 同上：拼写复习也回写记忆引擎。
        VocabManager.Instance.RecordAnswer(_currentReviewQuiz.TargetWord, correct);

        // 答完自动朗读（篝火拼写题；选择题在 ChoiceAnswerWidget 内统一处理）
        if (VocabConfig.Instance.AutoSpeakOnAnswer)
            TtsService.Instance.Speak(_currentReviewQuiz.TargetWord.English);

        _spellingInput.Editable = false;
        _spellingSubmitBtn.Disabled = true;
        _spellingForgotBtn.Disabled = true;

        SetFeedback(correct ? "回答正确！" : $"回答错误！正确答案：{_currentReviewQuiz.CorrectText}", correct);

        ShowNextButton();
    }

    private void ShowNextWord()
    {
        _currentIndex++;
        if (_currentIndex >= _records.Count)
            Complete();
        else
            ShowCurrentWord();
    }

    private void Complete()
    {
        Visible = false;
        _padHint.Visible = false;
        _onComplete?.Invoke();
        _onComplete = null;
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;

        var pad = Services.GamepadInput.Translate(@event);
        if (pad != PadAction.None && HandlePad(pad))
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is not InputEventKey { Pressed: true } key) return;

        if (_answered && _nextBtn.Visible)
        {
            if (VocabConfig.KeyMatches(key.Keycode, VocabConfig.Instance.ContinueKey))
            {
                ShowNextWord();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (_answered || _currentReviewQuiz is null) return;
        if (_currentReviewQuiz.IsSpelling) return; // 让 LineEdit 处理

        // 提交键：回忆卡片 → 翻面；选择题 → 提交
        if (VocabConfig.KeyMatches(key.Keycode, VocabConfig.Instance.SubmitKey))
        {
            var submitted = _currentReviewQuiz.IsRecall ? _recallWidget.TryReveal() : _choiceWidget.TrySubmit();
            if (submitted) GetViewport().SetInputAsHandled();
            return;
        }

        // 0 → 「忘了」（选择题）
        if (key.Keycode is Key.Key0 or Key.Kp0 && !_currentReviewQuiz.IsRecall)
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
        var consumed = _currentReviewQuiz.IsRecall
            ? _recallWidget.HandleKeyOption(idx)
            : _choiceWidget.HandleKeyOption(idx);
        if (consumed) GetViewport().SetInputAsHandled();
    }

    /// <summary>手柄操作（与战斗答题面板同一套键位）。拼写复习只提供 X =「忘了」。</summary>
    private bool HandlePad(PadAction pad)
    {
        if (_answered && _nextBtn.Visible)
        {
            if (pad is PadAction.Accept or PadAction.Submit) { ShowNextWord(); return true; }
            return false;
        }
        if (_answered || _currentReviewQuiz is null) return false;

        if (_currentReviewQuiz.IsRecall)
        {
            return pad switch
            {
                PadAction.Accept or PadAction.Left => _recallWidget.PadAccept(),
                PadAction.Forgot or PadAction.Right => _recallWidget.PadForgot(),
                _ => false
            };
        }

        if (_currentReviewQuiz.IsSpelling)
        {
            if (pad == PadAction.Forgot && VocabConfig.Instance.ShowForgotButton)
            {
                OnSpellingForgot();
                return true;
            }
            return false;
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

    public static void Create()
    {
        var root = Services.GameBridge.GetUIRoot();
        if (root is null) return;
        root.AddChild(new RestSiteReviewPanel
        {
            Name = "VocabSpireRestReview",
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        });
    }
}
