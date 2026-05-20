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
    private Button _submitBtn = null!;
    private readonly HashSet<int> _selected = new();

    private QuizQuestion? _currentQuestion;
    private Action<bool, IReadOnlyCollection<int>>? _onAnswered;
    private bool _answered;
    private ulong _answeredAtMsec;

    private static readonly Color CorrectGreen = GameTheme.Green;
    private static readonly Color WrongRed = GameTheme.Red;
    private static readonly Color BtnNormal = new(0.12f, 0.12f, 0.18f);
    private static readonly Color BtnHover = new(0.2f, 0.2f, 0.28f);
    private static readonly Color TextWhite = GameTheme.Cream;
    private static readonly Color SelectedBlue = new(0.3f, 0.5f, 0.8f);

    private static readonly string[] Prefixes = { "A", "B", "C", "D", "E", "F", "G", "H" };

    public bool IsAnswered => _answered;
    public bool HasSelection => _selected.Count > 0;
    public ulong AnsweredAtMsec => _answeredAtMsec;

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
            var btn = CreateOptionButton(i);
            _optionsContainer.AddChild(btn);
            _optionButtons.Add(btn);
        }

        var submitCenter = new CenterContainer();
        AddChild(submitCenter);
        _submitBtn = GameTheme.MakeButton("  提交答案 (Enter)  ", 16, GameTheme.Gold);
        _submitBtn.CustomMinimumSize = new Vector2(220, 42);
        _submitBtn.Visible = false;
        _submitBtn.Pressed += OnSubmit;
        submitCenter.AddChild(_submitBtn);
    }

    /// <summary>显示一道选择题。onAnswered(correct, selectedIndices) 在用户按提交后被调用。</summary>
    public void ShowQuestion(QuizQuestion question, Action<bool, IReadOnlyCollection<int>> onAnswered)
    {
        _currentQuestion = question;
        _onAnswered = onAnswered;
        _answered = false;
        _selected.Clear();

        for (var i = 0; i < _optionButtons.Count; i++)
        {
            if (i < question.Options.Count)
            {
                _optionButtons[i].Text = $"  {Prefixes[i]}.  {question.Options[i]}";
                _optionButtons[i].Visible = true;
                _optionButtons[i].Disabled = false;
                ResetStyle(_optionButtons[i]);
            }
            else
            {
                _optionButtons[i].Visible = false;
            }
        }

        _submitBtn.Visible = true;
        _submitBtn.Disabled = false;
        _submitBtn.Text = question.IsMultiSelect
            ? "  提交多选答案 (Enter)  "
            : "  提交答案 (Enter)  ";
        Visible = true;
    }

    public new void Hide()
    {
        Visible = false;
        _currentQuestion = null;
        _onAnswered = null;
        _selected.Clear();
        _answered = false;
        _submitBtn.Visible = false;
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

    private void OnSubmit()
    {
        if (_answered || _currentQuestion is null || _selected.Count == 0) return;

        _answered = true;
        _answeredAtMsec = Time.GetTicksMsec();
        _submitBtn.Visible = false;

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

        RevealDetails();
        foreach (var btn in _optionButtons) btn.Disabled = true;

        // 拷贝一份给回调，避免外部使用我们后续清理的集合。
        var snapshot = _selected.ToArray();
        _onAnswered?.Invoke(correct, snapshot);
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
