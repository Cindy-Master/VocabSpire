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
    private HBoxContainer _spellingContainer = null!;
    private LineEdit _spellingInput = null!;
    private Button _spellingSubmitBtn = null!;
    private Button _nextBtn = null!;
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

        // 选择题答题区（共享组件 —— 单选/多选共用、提交按钮内置）
        _choiceWidget = new ChoiceAnswerWidget { Visible = false };
        mainVBox.AddChild(_choiceWidget);

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

        _feedbackLabel = GameTheme.MakeLabel("", 18, White, HorizontalAlignment.Center);
        mainVBox.AddChild(_feedbackLabel);

        var btnCenter = new CenterContainer();
        mainVBox.AddChild(btnCenter);
        _nextBtn = new Button
        {
            Text = "  下一题 (Enter)  ",
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

                if (quiz.IsSpelling)
                {
                    _choiceWidget.Hide();
                    _spellingContainer.Visible = true;
                    _spellingInput.Text = "";
                    _spellingInput.Editable = true;
                    _spellingSubmitBtn.Disabled = false;
                    _spellingInput.CallDeferred(LineEdit.MethodName.GrabFocus);
                }
                else
                {
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

        _feedbackLabel.Text = correct ? "回答正确！" : "回答错误！";
        _feedbackLabel.AddThemeColorOverride("font_color", correct ? CorrectGreen : WrongRed);

        _nextBtn.Visible = true;
        _nextBtn.Text = _currentIndex >= _records.Count - 1
            ? "  完成复习 (Enter)  "
            : "  下一题 (Enter)  ";
    }

    private void OnSpellingSubmit()
    {
        if (_answered || _currentReviewQuiz is null) return;

        var userInput = _spellingInput.Text.Trim();
        if (string.IsNullOrEmpty(userInput)) return;

        _answered = true;
        var correct = _currentReviewQuiz.CheckSpelling(userInput);

        _spellingInput.Editable = false;
        _spellingSubmitBtn.Disabled = true;

        if (correct)
        {
            _feedbackLabel.Text = "回答正确！";
            _feedbackLabel.AddThemeColorOverride("font_color", CorrectGreen);
        }
        else
        {
            _feedbackLabel.Text = $"回答错误！正确答案：{_currentReviewQuiz.CorrectText}";
            _feedbackLabel.AddThemeColorOverride("font_color", WrongRed);
        }

        _nextBtn.Visible = true;
        _nextBtn.Text = _currentIndex >= _records.Count - 1
            ? "  完成复习 (Enter)  "
            : "  下一题 (Enter)  ";
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
        _onComplete?.Invoke();
        _onComplete = null;
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;
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

        // 提交键 → 提交
        if (VocabConfig.KeyMatches(key.Keycode, VocabConfig.Instance.SubmitKey))
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
