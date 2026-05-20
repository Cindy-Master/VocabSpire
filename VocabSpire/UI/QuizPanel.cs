using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 答题弹窗面板 —— 支持选择题和拼写题两种模式。
/// </summary>
public partial class QuizPanel : Control
{
    public static QuizPanel? Instance { get; private set; }

    private Label _modeLabel = null!;
    private Label _promptLabel = null!;
    private Label _feedbackLabel = null!;
    private Label _statsLabel = null!;
    private VBoxContainer _optionsContainer = null!;
    private HBoxContainer _spellingContainer = null!;
    private LineEdit _spellingInput = null!;
    private Button _spellingSubmitBtn = null!;
    private Button _confirmButton = null!;
    private HBoxContainer _listenContainer = null!;
    private Button _listenBtn = null!;
    private Button _listenPlayTop = null!;
    private readonly List<Button> _optionButtons = new();

    private QuizQuestion? _currentQuestion;
    private Action<bool>? _onAnswered;
    private bool _answered;
    private bool _lastCorrect;
    private Button? _multiSubmitBtn;

    // 多选题状态
    private readonly HashSet<int> _multiSelected = new();
    private ulong _answeredAtMsec; // 防止回车双触发

    private static readonly Color BgColor = GameTheme.DarkBg;
    private static readonly Color AccentGold = GameTheme.Gold;
    private static readonly Color CorrectGreen = GameTheme.Green;
    private static readonly Color WrongRed = GameTheme.Red;
    private static readonly Color BtnNormal = new(0.12f, 0.12f, 0.18f);
    private static readonly Color BtnHover = new(0.2f, 0.2f, 0.28f);
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

        // 听力模式顶部播放按钮（替代文字 prompt）
        var listenTopCenter = new CenterContainer();
        mainVBox.AddChild(listenTopCenter);
        _listenPlayTop = GameTheme.MakeButton("  \uD83D\uDD0A  \u64AD\u653E\u53D1\u97F3  ", 22, GameTheme.Gold);
        _listenPlayTop.CustomMinimumSize = new Vector2(260, 54);
        _listenPlayTop.Visible = false;
        _listenPlayTop.Pressed += OnListenPressed;
        listenTopCenter.AddChild(_listenPlayTop);

        // 选择题选项
        _optionsContainer = new VBoxContainer();
        _optionsContainer.AddThemeConstantOverride("separation", 10);
        mainVBox.AddChild(_optionsContainer);

        for (var i = 0; i < QuizGenerator.MaxOptionCount; i++)
        {
            var btn = CreateOptionButton(i);
            _optionsContainer.AddChild(btn);
            _optionButtons.Add(btn);
        }

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

        // 多选提交按钮
        var multiCenter = new CenterContainer();
        mainVBox.AddChild(multiCenter);
        _multiSubmitBtn = GameTheme.MakeButton("  \u63D0\u4EA4\u591A\u9009\u7B54\u6848  ", 16, GameTheme.Gold);
        _multiSubmitBtn.CustomMinimumSize = new Vector2(200, 42);
        _multiSubmitBtn.Visible = false;
        _multiSubmitBtn.Pressed += OnMultiSubmit;
        multiCenter.AddChild(_multiSubmitBtn);

        // 听力播放按钮
        _listenContainer = new HBoxContainer { Visible = false };
        mainVBox.AddChild(_listenContainer);

        var listenCenter = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _listenContainer.AddChild(listenCenter);

        _listenBtn = GameTheme.MakeButton("  \uD83D\uDD0A  \u64AD\u653E\u53D1\u97F3  ", 20, GameTheme.Gold);
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
        var confirmStyle = MakeGoldButtonStyle(0.2f);
        _confirmButton.AddThemeStyleboxOverride("normal", confirmStyle);
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
            QuizModeFlags.EnglishToChinese => "\u82F1 \u2192 \u4E2D",
            QuizModeFlags.ChineseToEnglish => "\u4E2D \u2192 \u82F1",
            QuizModeFlags.SpellEnglish => "\u4E2D \u2192 \u82F1 (\u62FC\u5199)",
            QuizModeFlags.ListenToChinese => "\uD83D\uDD0A \u542C\u529B\u6A21\u5F0F",
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

        // 切换各模式 UI 可见性
        _spellingContainer.Visible = question.IsSpelling;

        if (question.IsSpelling)
        {
            _optionsContainer.Visible = false;
            _listenContainer.Visible = false;
            _spellingInput.Text = "";
            _spellingInput.Editable = true;
            _spellingSubmitBtn.Disabled = false;
            _spellingInput.CallDeferred(LineEdit.MethodName.GrabFocus);
        }
        else
        {
            // 选择题 / 听力题 —— 统一设置选项按钮
            _optionsContainer.Visible = true;

            var prefixes = new[] { "A", "B", "C", "D", "E", "F", "G", "H" };
            for (var i = 0; i < _optionButtons.Count; i++)
            {
                if (i < question.Options.Count)
                {
                    var prefix = i < prefixes.Length ? prefixes[i] : $"{i + 1}";
                    _optionButtons[i].Text = $"  {prefix}.  {question.Options[i]}";
                    _optionButtons[i].Visible = true;
                    _optionButtons[i].Disabled = false;
                    ResetButtonStyle(_optionButtons[i]);
                }
                else
                {
                    _optionButtons[i].Visible = false;
                }
            }

            if (question.IsListening)
            {
                _listenContainer.Visible = false;
                _listenPlayTop.Visible = true;
                // 多选听力：显示【多选题】提示
                if (question.IsMultiSelect)
                {
                    _promptLabel.Visible = true;
                    _promptLabel.Text = "\u3010\u591A\u9009\u9898\u3011";
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

        // 多选模式初始化
        _multiSelected.Clear();
        _multiSubmitBtn!.Visible = question.IsMultiSelect;

        _feedbackLabel.Text = "";
        _confirmButton.Visible = false;
        UpdateStats();
        Visible = true;
    }

    // ── 选择题作答 ──

    private void OnOptionSelected(int index)
    {
        if (_answered || _currentQuestion is null) return;

        // 多选模式：切换选中状态，不立即判定
        if (_currentQuestion.IsMultiSelect)
        {
            if (_multiSelected.Contains(index))
            {
                _multiSelected.Remove(index);
                ResetButtonStyle(_optionButtons[index]);
            }
            else
            {
                _multiSelected.Add(index);
                HighlightButton(index, new Color(0.3f, 0.5f, 0.8f)); // 蓝色表示选中
            }
            _multiSubmitBtn!.Text = "  \u63D0\u4EA4\u591A\u9009\u7B54\u6848  ";
            return;
        }

        // 单选模式
        _answered = true;
        _answeredAtMsec = Time.GetTicksMsec();
        _lastCorrect = _currentQuestion.CheckAnswer(index);
        VocabManager.Instance.RecordAnswer(_currentQuestion.TargetWord, _lastCorrect);

        if (_lastCorrect)
        {
            HighlightButton(index, CorrectGreen);
            ShowFeedback(true, null);
        }
        else
        {
            HighlightButton(index, WrongRed);
            HighlightButton(_currentQuestion.CorrectIndex, CorrectGreen);
            var correctText = _currentQuestion.Options[_currentQuestion.CorrectIndex];
            var userText = _currentQuestion.Options[index];
            // 听力模式答错时显示原始英文单词
            var extra = _currentQuestion.IsListening
                ? $"\n\u5355\u8BCD\uFF1A{_currentQuestion.TargetWord.English}"
                : "";
            ShowFeedback(false, correctText + extra);
            RecordWrong(userText, correctText,
                _currentQuestion.GetDetail(index) ?? "",
                _currentQuestion.GetDetail(_currentQuestion.CorrectIndex) ?? "");
        }

        // 记录到全局追踪
        RecordToRunTracker(_lastCorrect,
            _lastCorrect ? "" : _currentQuestion.Options[index],
            _currentQuestion.CorrectIndex >= 0 ? _currentQuestion.Options[_currentQuestion.CorrectIndex] : "");

        _listenContainer.Visible = false;
        _listenPlayTop.Visible = false;
        _promptLabel.Visible = true;
        if (_currentQuestion.IsListening)
            _promptLabel.Text = _currentQuestion.TargetWord.English;

        RevealOptionDetails();
        foreach (var btn in _optionButtons) btn.Disabled = true;
        UpdateStats();
        _confirmButton.Visible = true;
    }

    // ── 多选提交 ──

    private void OnMultiSubmit()
    {
        if (_answered || _currentQuestion is null || !_currentQuestion.IsMultiSelect) return;
        _answered = true;
        _answeredAtMsec = Time.GetTicksMsec();

        _lastCorrect = _currentQuestion.CheckMultiAnswer(_multiSelected);
        VocabManager.Instance.RecordAnswer(_currentQuestion.TargetWord, _lastCorrect);

        // 高亮正确/错误
        foreach (var ci in _currentQuestion.CorrectIndices)
            HighlightButton(ci, CorrectGreen);
        foreach (var si in _multiSelected)
        {
            if (!_currentQuestion.CorrectIndices.Contains(si))
                HighlightButton(si, WrongRed);
        }

        if (_lastCorrect)
        {
            ShowFeedback(true, null);
        }
        else
        {
            var correctTexts = _currentQuestion.CorrectIndices
                .Select(i => _currentQuestion.Options[i]);
            var extra = _currentQuestion.IsListening
                ? $"\n\u5355\u8BCD\uFF1A{_currentQuestion.TargetWord.English}"
                : "";
            ShowFeedback(false, string.Join(" | ", correctTexts) + extra);
            RecordWrong(
                string.Join("|", _multiSelected.Select(i => _currentQuestion.Options[i])),
                string.Join("|", correctTexts),
                "", _currentQuestion.TargetWord.English);
        }

        RecordToRunTracker(_lastCorrect, "", "");

        _listenContainer.Visible = false;
        _listenPlayTop.Visible = false;
        _promptLabel.Visible = true;
        if (_currentQuestion.IsListening)
            _promptLabel.Text = _currentQuestion.TargetWord.English;
        _multiSubmitBtn!.Visible = false;

        RevealOptionDetails();
        foreach (var btn in _optionButtons) btn.Disabled = true;
        UpdateStats();
        _confirmButton.Visible = true;
    }

    // ── 拼写题作答 ──

    private void OnSpellingSubmit()
    {
        if (_answered || _currentQuestion is null) return;

        var userInput = _spellingInput.Text.Trim();
        // 防止 IME 回车时空提交
        if (string.IsNullOrEmpty(userInput)) return;

        _answered = true;
        _answeredAtMsec = Time.GetTicksMsec();
        _lastCorrect = _currentQuestion.CheckSpelling(userInput);
        VocabManager.Instance.RecordAnswer(_currentQuestion.TargetWord, _lastCorrect);

        _spellingInput.Editable = false;
        _spellingSubmitBtn.Disabled = true;

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

    /// <summary>记录到全局追踪器（每次答题都调用，不只是错题）。</summary>
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

    /// <summary>答完后在每个选项后面追加显示对应的补充信息。</summary>
    private void RevealOptionDetails()
    {
        if (_currentQuestion is null) return;
        var prefixes = new[] { "A", "B", "C", "D", "E", "F" };
        for (var i = 0; i < _optionButtons.Count; i++)
        {
            if (i >= _currentQuestion.Options.Count) break;
            var detail = _currentQuestion.GetDetail(i);
            if (!string.IsNullOrEmpty(detail))
            {
                var prefix = i < prefixes.Length ? prefixes[i] : $"{i + 1}";
                _optionButtons[i].Text = $"  {prefix}.  {_currentQuestion.Options[i]}  →  {detail}";
            }
        }
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

        // 已作答：Enter 继续（至少等 500ms 防止 IME 回车双触发）
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
        if (idx >= 0 && idx < _currentQuestion.Options.Count)
        {
            OnOptionSelected(idx);
            GetViewport().SetInputAsHandled();
        }
    }

    private void OnListenPressed()
    {
        if (_currentQuestion is null) return;
        TtsService.Instance.Speak(_currentQuestion.TargetWord.English);
    }

    // ── UI 辅助 ──

    private Button CreateOptionButton(int index)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(540, 50),
            Alignment = HorizontalAlignment.Left
        };
        btn.AddThemeStyleboxOverride("normal", MakeBtnStyle(BtnNormal));
        btn.AddThemeStyleboxOverride("hover", MakeBtnStyle(BtnHover));
        btn.AddThemeStyleboxOverride("pressed", MakeBtnStyle(BtnHover));
        btn.AddThemeStyleboxOverride("disabled", MakeBtnStyle(BtnNormal));
        btn.AddThemeColorOverride("font_color", TextWhite);
        btn.AddThemeColorOverride("font_disabled_color", TextWhite);
        btn.AddThemeFontSizeOverride("font_size", 18);
        var i = index;
        btn.Pressed += () => OnOptionSelected(i);
        return btn;
    }

    private static StyleBoxFlat MakeBtnStyle(Color bg, Color? border = null)
    {
        var s = new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 16, ContentMarginRight = 16,
            ContentMarginTop = 10, ContentMarginBottom = 10
        };
        if (border.HasValue)
        {
            s.BorderWidthTop = 2; s.BorderWidthBottom = 2;
            s.BorderWidthLeft = 2; s.BorderWidthRight = 2;
            s.BorderColor = border.Value;
        }
        return s;
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

    private void ResetButtonStyle(Button btn)
    {
        btn.AddThemeStyleboxOverride("normal", MakeBtnStyle(BtnNormal));
        btn.AddThemeStyleboxOverride("disabled", MakeBtnStyle(BtnNormal));
    }

    private void HighlightButton(int index, Color color)
    {
        if (index < 0 || index >= _optionButtons.Count) return;
        var tinted = new Color(color.R, color.G, color.B, 0.18f);
        var style = MakeBtnStyle(tinted, color);
        _optionButtons[index].AddThemeStyleboxOverride("normal", style);
        _optionButtons[index].AddThemeStyleboxOverride("disabled", style);
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
