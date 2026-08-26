using Godot;
using VocabSpire.Models;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 选择题答题区 —— 选项按钮 + 选中状态 + 提交按钮 + 高亮反馈 + 详情展开。
/// 单选/多选共用：点击只是切换选中，按"提交答案"才判定。
/// QuizPanel 和 RestSiteReviewPanel 都嵌入它，避免两份代码。
/// </summary>
public partial class ChoiceAnswerWidget : VBoxContainer
{
    private VBoxContainer _optionsContainer = null!;
    private readonly List<Button> _optionButtons = new();
    private readonly List<HBoxContainer> _optionRows = new();     // 每个选项一行（选项按钮 + 小喇叭）
    private readonly List<Button> _optionSpeakers = new();        // 每个选项的发音按钮
    private Button _submitBtn = null!;
    private Button _forgotBtn = null!;
    private readonly HashSet<int> _selected = new();

    private QuizQuestion? _currentQuestion;
    private Action<bool, IReadOnlyCollection<int>>? _onAnswered;
    private bool _answered;
    private bool _wasForgot;
    private int _cursor = -1;          // 手柄游标（键盘/鼠标操作时保持 -1，不显示）
    private ulong _answeredAtMsec;

    private static readonly Color CorrectGreen = GameTheme.Green;
    private static readonly Color WrongRed = GameTheme.Red;
    private static readonly Color BtnNormal = new(0.12f, 0.12f, 0.18f);
    private static readonly Color BtnHover = new(0.2f, 0.2f, 0.28f);
    private static readonly Color TextWhite = GameTheme.Cream;
    private static readonly Color SelectedBlue = new(0.3f, 0.5f, 0.8f);
    private static readonly Color CursorGold = GameTheme.Gold;   // 手柄游标停留处的描边

    private static readonly string[] Prefixes = { "A", "B", "C", "D", "E", "F", "G", "H" };

    public bool IsAnswered => _answered;
    public bool HasSelection => _selected.Count > 0;
    public ulong AnsweredAtMsec => _answeredAtMsec;

    /// <summary>本题是否由玩家点「忘了」结束（而非提交了选项）—— 供调用方写错题本文案。</summary>
    public bool LastAnswerWasForgot => _wasForgot;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 12);
        BuildUI();
    }

    private void BuildUI()
    {
        _optionsContainer = new VBoxContainer();
        _optionsContainer.AddThemeConstantOverride("separation", 10);
        AddChild(_optionsContainer);

        for (var i = 0; i < QuizGenerator.MaxOptionCount; i++)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);

            var btn = CreateOptionButton(i);
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(btn);

            var spk = CreateSpeakerButton(i);
            row.AddChild(spk);

            _optionsContainer.AddChild(row);
            _optionButtons.Add(btn);
            _optionSpeakers.Add(spk);
            _optionRows.Add(row);
        }

        var submitRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        submitRow.AddThemeConstantOverride("separation", 12);
        AddChild(submitRow);

        _submitBtn = GameTheme.MakeButton($"  提交答案 ({KeyBindButton.KeyName(VocabConfig.Instance.SubmitKey)})  ", 16, GameTheme.Gold);
        _submitBtn.CustomMinimumSize = new Vector2(220, 42);
        _submitBtn.Visible = false;
        _submitBtn.Pressed += OnSubmit;
        submitRow.AddChild(_submitBtn);

        // 「忘了」：不瞎蒙、直接认错并看答案。蒙对会让记忆引擎误判已掌握，主动认错的数据才准。
        _forgotBtn = GameTheme.MakeButton("  🤔 忘了 (0)  ", 16, GameTheme.LightGray);
        _forgotBtn.CustomMinimumSize = new Vector2(150, 42);
        _forgotBtn.Visible = false;
        _forgotBtn.TooltipText = "想不起来时点这里：直接判错并显示正确答案，不用瞎猜。可在设置中关闭。";
        _forgotBtn.Pressed += OnForgot;
        submitRow.AddChild(_forgotBtn);
    }

    /// <summary>显示一道选择题。onAnswered(correct, selectedIndices) 在用户按提交后被调用。</summary>
    public void ShowQuestion(QuizQuestion question, Action<bool, IReadOnlyCollection<int>> onAnswered)
    {
        _currentQuestion = question;
        _onAnswered = onAnswered;
        _answered = false;
        _selected.Clear();

        // 只有中→英题的选项是英文单词，才显示发音小喇叭（且开关开启）；固定选择题选项是中文，不显示
        var showSpeakers = VocabConfig.Instance.OptionPlayAudio
                           && question.Mode == QuizModeFlags.ChineseToEnglish
                           && !question.IsFixedChoice;
        for (var i = 0; i < _optionButtons.Count; i++)
        {
            if (i < question.Options.Count)
            {
                _optionButtons[i].Text = $"  {Prefixes[i]}.  {question.Options[i]}";
                _optionButtons[i].Disabled = false;
                ResetStyle(_optionButtons[i]);
                _optionSpeakers[i].Visible = showSpeakers;
                _optionRows[i].Visible = true;
            }
            else
            {
                _optionRows[i].Visible = false;
            }
        }

        _submitBtn.Visible = true;
        _submitBtn.Disabled = false;
        var submitKey = KeyBindButton.KeyName(VocabConfig.Instance.SubmitKey);
        _submitBtn.Text = question.IsMultiSelect
            ? $"  提交多选答案 ({submitKey})  "
            : $"  提交答案 ({submitKey})  ";
        _forgotBtn.Visible = VocabConfig.Instance.ShowForgotButton;
        _forgotBtn.Disabled = false;
        _wasForgot = false;
        _cursor = -1;
        Services.GamepadInput.ResetAxisState();
        Visible = true;
    }

    public new void Hide()
    {
        Visible = false;
        _currentQuestion = null;
        _onAnswered = null;
        _selected.Clear();
        _answered = false;
        _wasForgot = false;
        _cursor = -1;
        _submitBtn.Visible = false;
        _forgotBtn.Visible = false;
    }

    /// <summary>父面板键盘事件转发：A-H / 1-8 → 切换对应选项。返回是否处理。</summary>
    public bool HandleKeyOption(int index)
    {
        if (_answered || _currentQuestion is null) return false;
        if (index < 0 || index >= _currentQuestion.Options.Count) return false;
        OnOptionClicked(index);
        return true;
    }

    /// <summary>父面板键盘事件转发：Enter → 触发提交。返回是否处理。</summary>
    public bool TrySubmit()
    {
        if (_answered || _selected.Count == 0 || _currentQuestion is null) return false;
        OnSubmit();
        return true;
    }

    private void OnOptionClicked(int index)
    {
        if (_answered || _currentQuestion is null) return;

        if (_currentQuestion.IsMultiSelect)
        {
            if (_selected.Contains(index))
            {
                _selected.Remove(index);
                ResetStyle(_optionButtons[index]);
            }
            else
            {
                _selected.Add(index);
                HighlightBtn(index, SelectedBlue);
            }
        }
        else
        {
            foreach (var prev in _selected) ResetStyle(_optionButtons[prev]);
            _selected.Clear();
            _selected.Add(index);
            HighlightBtn(index, SelectedBlue);
        }
    }

    /// <summary>手柄方向键：移动游标。单选题游标即选中项（与鼠标点选视觉一致）；多选题游标只是停留标记。</summary>
    public bool MoveCursor(int delta)
    {
        if (_answered || _currentQuestion is null) return false;
        var n = _currentQuestion.Options.Count;
        if (n <= 0) return false;

        _cursor = _cursor < 0
            ? (delta > 0 ? 0 : n - 1)
            : ((_cursor + delta) % n + n) % n;

        if (_currentQuestion.IsMultiSelect)
        {
            RefreshCursorVisual();
        }
        else
        {
            OnOptionClicked(_cursor);   // 单选：移动即选中，少按一次键
        }
        return true;
    }

    /// <summary>手柄 A 键：单选 = 未选中先选中、已选中则提交；多选 = 切换该项选中。</summary>
    public bool PadAccept()
    {
        if (_answered || _currentQuestion is null) return false;
        if (_cursor < 0) return MoveCursor(1);          // 还没移动过 → 先落到第一项

        if (_currentQuestion.IsMultiSelect)
        {
            OnOptionClicked(_cursor);
            RefreshCursorVisual();
            return true;
        }

        if (_selected.Contains(_cursor)) { OnSubmit(); return true; }
        OnOptionClicked(_cursor);
        return true;
    }

    /// <summary>多选题游标可见化：已选中用蓝色，游标停留处用金色描边（未选中时）。</summary>
    private void RefreshCursorVisual()
    {
        if (_currentQuestion is null) return;
        for (var i = 0; i < _optionButtons.Count && i < _currentQuestion.Options.Count; i++)
        {
            if (_selected.Contains(i)) HighlightBtn(i, SelectedBlue);
            else if (i == _cursor) HighlightBtn(i, CursorGold);
            else ResetStyle(_optionButtons[i]);
        }
    }

    /// <summary>父面板键盘事件转发：0 → 「忘了」（直接认错看答案）。返回是否处理。</summary>
    public bool TryForgot()
    {
        if (_answered || _currentQuestion is null || !VocabConfig.Instance.ShowForgotButton) return false;
        OnForgot();
        return true;
    }

    /// <summary>玩家点「忘了」：不猜、直接判错并揭示正确答案。</summary>
    private void OnForgot()
    {
        if (_answered || _currentQuestion is null) return;

        _answered = true;
        _wasForgot = true;
        _answeredAtMsec = Time.GetTicksMsec();
        HideActionButtons();

        // 清掉可能已选中的项（没作数），只把正确答案标绿
        foreach (var prev in _selected) ResetStyle(_optionButtons[prev]);
        _selected.Clear();
        if (_currentQuestion.IsMultiSelect)
        {
            foreach (var ci in _currentQuestion.CorrectIndices) HighlightBtn(ci, CorrectGreen);
        }
        else if (_currentQuestion.CorrectIndex >= 0)
        {
            HighlightBtn(_currentQuestion.CorrectIndex, CorrectGreen);
        }

        FinishAnswer(false, Array.Empty<int>());
    }

    private void HideActionButtons()
    {
        _submitBtn.Visible = false;
        _forgotBtn.Visible = false;
    }

    private void OnSubmit()
    {
        if (_answered || _currentQuestion is null || _selected.Count == 0) return;

        _answered = true;
        _answeredAtMsec = Time.GetTicksMsec();
        HideActionButtons();

        bool correct;
        if (_currentQuestion.IsMultiSelect)
        {
            correct = _currentQuestion.CheckMultiAnswer(_selected);
            foreach (var ci in _currentQuestion.CorrectIndices)
                HighlightBtn(ci, CorrectGreen);
            foreach (var si in _selected)
                if (!_currentQuestion.CorrectIndices.Contains(si))
                    HighlightBtn(si, WrongRed);
        }
        else
        {
            var picked = _selected.First();
            correct = _currentQuestion.CheckAnswer(picked);
            if (correct)
            {
                HighlightBtn(picked, CorrectGreen);
            }
            else
            {
                HighlightBtn(picked, WrongRed);
                if (_currentQuestion.CorrectIndex >= 0)
                    HighlightBtn(_currentQuestion.CorrectIndex, CorrectGreen);
            }
        }

        // 拷贝一份给回调，避免外部使用我们后续清理的集合。
        FinishAnswer(correct, _selected.ToArray());
    }

    /// <summary>提交与「忘了」共用的收尾：揭示详情、锁定选项、朗读、回调。</summary>
    private void FinishAnswer(bool correct, int[] selectedSnapshot)
    {
        if (_currentQuestion is null) return;

        RevealDetails();
        foreach (var btn in _optionButtons) btn.Disabled = true;

        // 答完自动朗读本题单词（选择题：QuizPanel 与篝火复习共用此处）；固定选择题题干是中文不朗读
        if (VocabConfig.Instance.AutoSpeakOnAnswer && !_currentQuestion.IsFixedChoice)
            TtsService.Instance.Speak(_currentQuestion.TargetWord.English);

        _onAnswered?.Invoke(correct, selectedSnapshot);
    }

    private void RevealDetails()
    {
        if (_currentQuestion is null) return;
        for (var i = 0; i < _optionButtons.Count; i++)
        {
            if (i >= _currentQuestion.Options.Count) break;
            var detail = _currentQuestion.GetDetail(i);
            if (!string.IsNullOrEmpty(detail))
            {
                _optionButtons[i].Text = $"  {Prefixes[i]}.  {_currentQuestion.Options[i]}  →  {detail}";
            }
        }
    }

    private Button CreateSpeakerButton(int index)
    {
        var spk = GameTheme.MakeButton(" 🔊 ", 18, GameTheme.Gold);
        spk.CustomMinimumSize = new Vector2(52, 50);
        spk.FocusMode = Control.FocusModeEnum.None;   // 不抢键盘焦点，不影响 A-H/1-8 选项快捷键
        spk.Visible = false;
        var idx = index;
        spk.Pressed += () =>
        {
            if (_currentQuestion is not null && idx < _currentQuestion.Options.Count)
                TtsService.Instance.Speak(_currentQuestion.Options[idx]);   // 播该英文选项发音
        };
        return spk;
    }

    private Button CreateOptionButton(int index)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(0, 50),
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
        btn.Pressed += () => OnOptionClicked(i);
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

    private static void ResetStyle(Button btn)
    {
        btn.AddThemeStyleboxOverride("normal", MakeBtnStyle(BtnNormal));
        btn.AddThemeStyleboxOverride("disabled", MakeBtnStyle(BtnNormal));
    }

    private void HighlightBtn(int index, Color color)
    {
        if (index < 0 || index >= _optionButtons.Count) return;
        var tinted = new Color(color.R, color.G, color.B, 0.18f);
        var style = MakeBtnStyle(tinted, color);
        _optionButtons[index].AddThemeStyleboxOverride("normal", style);
        _optionButtons[index].AddThemeStyleboxOverride("disabled", style);
    }
}
