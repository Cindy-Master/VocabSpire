using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 篝火（休息点）错题复习面板 —— 逐题重练上一区间的错题。
/// </summary>
public partial class RestSiteReviewPanel : Control
{
    public static RestSiteReviewPanel? Instance { get; private set; }

    private Label _titleLabel = null!;
    private Label _promptLabel = null!;
    private Label _feedbackLabel = null!;
    private VBoxContainer _optionsContainer = null!;
    private HBoxContainer _spellingContainer = null!;
    private LineEdit _spellingInput = null!;
    private Button _spellingSubmitBtn = null!;
    private Button _nextBtn = null!;
    private Button _skipBtn = null!;
    private Label _skipConfirmLabel = null!;
    private Button _skipConfirmYes = null!;
    private Button _skipConfirmNo = null!;
    private Control _skipConfirmGroup = null!;
    private readonly List<Button> _optionButtons = new();

    private IReadOnlyList<WrongAnswerRecord> _records = Array.Empty<WrongAnswerRecord>();
    private int _currentIndex;
    private bool _answered;
    private Action? _onComplete;

    private static readonly Color BgColor = GameTheme.DarkBg;
    private static readonly Color Gold = GameTheme.Gold;
    private static readonly Color White = GameTheme.Cream;
    private static readonly Color Grey = GameTheme.LightGray;
    private static readonly Color CorrectGreen = GameTheme.Green;
    private static readonly Color WrongRed = GameTheme.Red;
    private static readonly Color BtnNormal = new(0.12f, 0.12f, 0.18f);
    private static readonly Color BtnHover = new(0.2f, 0.2f, 0.28f);
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

        // ── 主面板（居中）──
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

        _titleLabel = GameTheme.MakeLabel("\u7BDD\u706B\u9519\u9898\u590D\u4E60", 20, Gold, HorizontalAlignment.Center);
        mainVBox.AddChild(_titleLabel);
        mainVBox.AddChild(new HSeparator());

        _promptLabel = GameTheme.MakeLabel("", 28, White, HorizontalAlignment.Center);
        _promptLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        mainVBox.AddChild(_promptLabel);

        _optionsContainer = new VBoxContainer();
        _optionsContainer.AddThemeConstantOverride("separation", 10);
        mainVBox.AddChild(_optionsContainer);

        for (var i = 0; i < 4; i++)
        {
            var btn = CreateOptionButton(i);
            _optionsContainer.AddChild(btn);
            _optionButtons.Add(btn);
        }

        // 拼写输入区（拼写模式时显示）
        _spellingContainer = new HBoxContainer { Visible = false };
        _spellingContainer.AddThemeConstantOverride("separation", 10);
        mainVBox.AddChild(_spellingContainer);

        _spellingInput = new LineEdit
        {
            PlaceholderText = "\u8BF7\u8F93\u5165\u82F1\u6587\u5355\u8BCD...",
            CustomMinimumSize = new Vector2(400, 46),
            ProcessMode = ProcessModeEnum.Always
        };
        _spellingInput.AddThemeFontSizeOverride("font_size", 20);
        _spellingInput.TextSubmitted += _ => OnSpellingSubmit();
        _spellingContainer.AddChild(_spellingInput);

        _spellingSubmitBtn = new Button { Text = "  \u786E\u8BA4  " };
        _spellingSubmitBtn.AddThemeFontSizeOverride("font_size", 16);
        _spellingSubmitBtn.CustomMinimumSize = new Vector2(100, 46);
        _spellingSubmitBtn.Pressed += OnSpellingSubmit;
        _spellingContainer.AddChild(_spellingSubmitBtn);

        _feedbackLabel = GameTheme.MakeLabel("", 18, White, HorizontalAlignment.Center);
        mainVBox.AddChild(_feedbackLabel);

        // 下一题/完成按钮（居中，底部）
        var btnCenter = new CenterContainer();
        mainVBox.AddChild(btnCenter);

        _nextBtn = new Button
        {
            Text = "  \u4E0B\u4E00\u9898 (Enter)  ",
            CustomMinimumSize = new Vector2(200, 44),
            Visible = false
        };
        _nextBtn.AddThemeColorOverride("font_color", Gold);
        _nextBtn.AddThemeFontSizeOverride("font_size", 16);
        _nextBtn.Pressed += ShowNextWord;
        btnCenter.AddChild(_nextBtn);

        // ── 跳过按钮放在最后添加（保证在主面板之上不被遮挡）──
        BuildSkipSection();
    }

    /// <summary>右上角独立的跳过区域，带二次确认。</summary>
    private void BuildSkipSection()
    {
        // 容器固定在右上角
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

        // 第一步：跳过按钮
        _skipBtn = new Button
        {
            Text = "  \u8DF3\u8FC7\u590D\u4E60  ",
            CustomMinimumSize = new Vector2(140, 36)
        };
        _skipBtn.AddThemeFontSizeOverride("font_size", 16);
        _skipBtn.AddThemeColorOverride("font_color", SkipColor);
        _skipBtn.Pressed += OnSkipPressed;
        skipContainer.AddChild(_skipBtn);

        // 第二步：确认组（初始隐藏）
        _skipConfirmGroup = new VBoxContainer { Visible = false };
        ((VBoxContainer)_skipConfirmGroup).AddThemeConstantOverride("separation", 4);
        skipContainer.AddChild(_skipConfirmGroup);

        _skipConfirmLabel = GameTheme.MakeLabel("\u786E\u5B9A\u8DF3\u8FC7\uFF1F", 12, WrongRed, HorizontalAlignment.Center);
        _skipConfirmGroup.AddChild(_skipConfirmLabel);

        var confirmRow = new HBoxContainer();
        confirmRow.AddThemeConstantOverride("separation", 8);
        _skipConfirmGroup.AddChild(confirmRow);

        _skipConfirmYes = new Button { Text = " \u786E\u5B9A ", CustomMinimumSize = new Vector2(60, 30) };
        _skipConfirmYes.AddThemeFontSizeOverride("font_size", 12);
        _skipConfirmYes.AddThemeColorOverride("font_color", WrongRed);
        _skipConfirmYes.Pressed += Complete;
        confirmRow.AddChild(_skipConfirmYes);

        _skipConfirmNo = new Button { Text = " \u53D6\u6D88 ", CustomMinimumSize = new Vector2(60, 30) };
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

        _titleLabel.Text = $"\u7BDD\u706B\u9519\u9898\u590D\u4E60  ({_currentIndex + 1}/{_records.Count})";
        _promptLabel.Text = $"{record.Word.English}\n{record.Word.Chinese}";

        var userDisp = string.IsNullOrEmpty(record.UserAnswerDetail)
            ? record.UserAnswer
            : $"{record.UserAnswer} ({record.UserAnswerDetail})";
        var correctDisp = string.IsNullOrEmpty(record.CorrectAnswerDetail)
            ? record.CorrectAnswer
            : $"{record.CorrectAnswer} ({record.CorrectAnswerDetail})";
        _feedbackLabel.Text = $"\u4E0A\u6B21\u9519\u8BEF\uFF1A\u4F60\u7B54\u4E86\u300C{userDisp}\u300D\uFF0C\u6B63\u786E\u7B54\u6848\u662F\u300C{correctDisp}\u300D";
        _feedbackLabel.AddThemeColorOverride("font_color", Grey);

        // 为当前错词生成一道专属题复习（使用配置的复习模式）
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
                    // 拼写模式
                    _optionsContainer.Visible = false;
                    _spellingContainer.Visible = true;
                    _spellingInput.Text = "";
                    _spellingInput.Editable = true;
                    _spellingSubmitBtn.Disabled = false;
                    _spellingInput.CallDeferred(LineEdit.MethodName.GrabFocus);
                }
                else
                {
                    // 选择题模式
                    _optionsContainer.Visible = true;
                    _spellingContainer.Visible = false;

                    var prefixes = new[] { "A", "B", "C", "D", "E", "F" };
                    for (var i = 0; i < _optionButtons.Count; i++)
                    {
                        if (i < quiz.Options.Count)
                        {
                            _optionButtons[i].Text = $"  {prefixes[i]}.  {quiz.Options[i]}";
                            _optionButtons[i].Visible = true;
                            _optionButtons[i].Disabled = false;
                            ResetStyle(_optionButtons[i]);
                        }
                        else
                        {
                            _optionButtons[i].Visible = false;
                        }
                    }
                    _correctReviewIndex = quiz.CorrectIndex;
                }
            }
        }

        _nextBtn.Visible = false;
        // 重置跳过确认状态
        CancelSkip();
    }

    private int _correctReviewIndex;
    private QuizQuestion? _currentReviewQuiz;

    private void OnReviewOptionSelected(int index)
    {
        if (_answered) return;
        _answered = true;

        var correct = index == _correctReviewIndex;
        if (correct)
        {
            HighlightBtn(index, CorrectGreen);
            _feedbackLabel.Text = "\u56DE\u7B54\u6B63\u786E\uFF01";
            _feedbackLabel.AddThemeColorOverride("font_color", CorrectGreen);
        }
        else
        {
            HighlightBtn(index, WrongRed);
            HighlightBtn(_correctReviewIndex, CorrectGreen);
            _feedbackLabel.Text = "\u56DE\u7B54\u9519\u8BEF\uFF01";
            _feedbackLabel.AddThemeColorOverride("font_color", WrongRed);
        }

        // 答完后显示每个选项对应的补充信息
        RevealOptionDetails();

        foreach (var btn in _optionButtons) btn.Disabled = true;

        _nextBtn.Visible = true;
        _nextBtn.Text = _currentIndex >= _records.Count - 1
            ? "  \u5B8C\u6210\u590D\u4E60 (Enter)  "
            : "  \u4E0B\u4E00\u9898 (Enter)  ";
    }

    private void RevealOptionDetails()
    {
        if (_currentReviewQuiz is null) return;
        var prefixes = new[] { "A", "B", "C", "D", "E", "F" };
        for (var i = 0; i < _optionButtons.Count; i++)
        {
            if (i >= _currentReviewQuiz.Options.Count) break;
            var detail = _currentReviewQuiz.GetDetail(i);
            if (!string.IsNullOrEmpty(detail))
            {
                var prefix = i < prefixes.Length ? prefixes[i] : $"{i + 1}";
                _optionButtons[i].Text = $"  {prefix}.  {_currentReviewQuiz.Options[i]}  \u2192  {detail}";
            }
        }
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
            _feedbackLabel.Text = "\u56DE\u7B54\u6B63\u786E\uFF01";
            _feedbackLabel.AddThemeColorOverride("font_color", CorrectGreen);
        }
        else
        {
            _feedbackLabel.Text = $"\u56DE\u7B54\u9519\u8BEF\uFF01\u6B63\u786E\u7B54\u6848\uFF1A{_currentReviewQuiz.CorrectText}";
            _feedbackLabel.AddThemeColorOverride("font_color", WrongRed);
        }

        _nextBtn.Visible = true;
        _nextBtn.Text = _currentIndex >= _records.Count - 1
            ? "  \u5B8C\u6210\u590D\u4E60 (Enter)  "
            : "  \u4E0B\u4E00\u9898 (Enter)  ";
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
            if (key.Keycode is Key.Enter or Key.Space or Key.KpEnter)
            {
                ShowNextWord();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        // 拼写模式时不拦截键盘（让 LineEdit 处理输入）
        if (!_answered && _currentReviewQuiz is not null && !_currentReviewQuiz.IsSpelling)
        {
            var idx = key.Keycode switch
            {
                Key.A or Key.Key1 => 0,
                Key.B or Key.Key2 => 1,
                Key.C or Key.Key3 => 2,
                Key.D or Key.Key4 => 3,
                _ => -1
            };
            if (idx >= 0 && idx < _optionButtons.Count && _optionButtons[idx].Visible)
            {
                OnReviewOptionSelected(idx);
                GetViewport().SetInputAsHandled();
            }
        }
    }

    // ── 辅助 ──

    private Button CreateOptionButton(int index)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(540, 46),
            Alignment = HorizontalAlignment.Left
        };
        var normalStyle = MakeFlatStyle(BtnNormal);
        btn.AddThemeStyleboxOverride("normal", normalStyle);
        btn.AddThemeStyleboxOverride("hover", MakeFlatStyle(BtnHover));
        btn.AddThemeStyleboxOverride("pressed", MakeFlatStyle(BtnHover));
        btn.AddThemeStyleboxOverride("disabled", normalStyle);
        btn.AddThemeColorOverride("font_color", White);
        btn.AddThemeColorOverride("font_disabled_color", White);
        btn.AddThemeFontSizeOverride("font_size", 17);
        var i = index;
        btn.Pressed += () => OnReviewOptionSelected(i);
        return btn;
    }

    private void ResetStyle(Button btn)
    {
        btn.AddThemeStyleboxOverride("normal", MakeFlatStyle(BtnNormal));
        btn.AddThemeStyleboxOverride("disabled", MakeFlatStyle(BtnNormal));
    }

    private void HighlightBtn(int index, Color color)
    {
        if (index < 0 || index >= _optionButtons.Count) return;
        var tinted = new Color(color.R, color.G, color.B, 0.18f);
        var s = MakeFlatStyle(tinted, color);
        _optionButtons[index].AddThemeStyleboxOverride("normal", s);
        _optionButtons[index].AddThemeStyleboxOverride("disabled", s);
    }

    private static StyleBoxFlat MakeFlatStyle(Color bg, Color? border = null)
    {
        var s = new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 16, ContentMarginRight = 16,
            ContentMarginTop = 8, ContentMarginBottom = 8
        };
        if (border.HasValue)
        {
            s.BorderWidthTop = 2; s.BorderWidthBottom = 2;
            s.BorderWidthLeft = 2; s.BorderWidthRight = 2;
            s.BorderColor = border.Value;
        }
        return s;
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
