using Godot;
using VocabSpire.Models;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 回忆卡片答题区（墨墨背单词那种）——两阶段：
///   ① 正面只有单词，玩家在心里回忆释义，点「显示答案」翻面；
///   ② 背面显示音标 + 全部释义，玩家自评「✅ 想起来了 / ❌ 没想起来」。
/// 自评结果直接当作答对/答错回传，记忆引擎（Box/Streak/DueTick）照常记账。
///
/// 与 ChoiceAnswerWidget 一样是共享组件：QuizPanel（战斗答题）和
/// RestSiteReviewPanel（篝火复习）都嵌入它，避免两份实现。
/// </summary>
public partial class RecallCardWidget : VBoxContainer
{
    private Label _hintLabel = null!;
    private Label _answerLabel = null!;
    private Button _revealBtn = null!;
    private HBoxContainer _rateRow = null!;
    private Button _rememberedBtn = null!;
    private Button _forgotBtn = null!;

    private QuizQuestion? _question;
    private Action<bool>? _onAnswered;
    private bool _revealed;
    private bool _answered;
    private ulong _answeredAtMsec;

    /// <summary>已翻面（答案已展示）。</summary>
    public bool IsRevealed => _revealed;

    /// <summary>已自评完成。</summary>
    public bool IsAnswered => _answered;

    public ulong AnsweredAtMsec => _answeredAtMsec;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 14);
        BuildUI();
    }

    private void BuildUI()
    {
        _hintLabel = GameTheme.MakeLabel("先在心里回忆这个词的意思，再翻面对答案",
            14, GameTheme.LightGray, HorizontalAlignment.Center);
        AddChild(_hintLabel);

        _answerLabel = GameTheme.MakeLabel("", 22, GameTheme.Cream, HorizontalAlignment.Center);
        _answerLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _answerLabel.Visible = false;
        AddChild(_answerLabel);

        var revealCenter = new CenterContainer();
        AddChild(revealCenter);
        _revealBtn = GameTheme.MakeButton("  👀  显示答案  ", 18, GameTheme.Gold);
        _revealBtn.CustomMinimumSize = new Vector2(260, 50);
        _revealBtn.Pressed += Reveal;
        revealCenter.AddChild(_revealBtn);

        _rateRow = new HBoxContainer { Visible = false, Alignment = BoxContainer.AlignmentMode.Center };
        _rateRow.AddThemeConstantOverride("separation", 20);
        AddChild(_rateRow);

        _rememberedBtn = GameTheme.MakeButton("  ✅  想起来了  ", 18, GameTheme.Green);
        _rememberedBtn.CustomMinimumSize = new Vector2(220, 52);
        _rememberedBtn.Pressed += () => SelfRate(true);
        _rateRow.AddChild(_rememberedBtn);

        _forgotBtn = GameTheme.MakeButton("  ❌  没想起来  ", 18, GameTheme.Red);
        _forgotBtn.CustomMinimumSize = new Vector2(220, 52);
        _forgotBtn.Pressed += () => SelfRate(false);
        _rateRow.AddChild(_forgotBtn);
    }

    /// <summary>显示一张回忆卡片。onAnswered(remembered) 在玩家自评后调用。</summary>
    public void ShowQuestion(QuizQuestion question, Action<bool> onAnswered)
    {
        _question = question;
        _onAnswered = onAnswered;
        _revealed = false;
        _answered = false;

        _hintLabel.Visible = true;
        _answerLabel.Visible = false;
        _answerLabel.Text = "";

        var key = KeyBindButton.KeyName(VocabConfig.Instance.SubmitKey);
        _revealBtn.Text = $"  👀  显示答案 ({key})  ";
        _revealBtn.Visible = true;
        _revealBtn.Disabled = false;
        _rememberedBtn.Text = "  ✅  想起来了 (1)  ";
        _forgotBtn.Text = "  ❌  没想起来 (2)  ";
        _rateRow.Visible = false;
        Services.GamepadInput.ResetAxisState();

        Visible = true;
    }

    public new void Hide()
    {
        Visible = false;
        _question = null;
        _onAnswered = null;
        _revealed = false;
        _answered = false;
        _rateRow.Visible = false;
        _revealBtn.Visible = false;
    }

    /// <summary>父面板转发提交键：未翻面则翻面。返回是否处理。</summary>
    public bool TryReveal()
    {
        if (_answered || _revealed || _question is null) return false;
        Reveal();
        return true;
    }

    /// <summary>父面板转发选项键：翻面后 1/A→想起来了，2/B→没想起来。返回是否处理。</summary>
    public bool HandleKeyOption(int index)
    {
        if (_answered || !_revealed || _question is null) return false;
        if (index != 0 && index != 1) return false;
        SelfRate(index == 0);
        return true;
    }

    /// <summary>手柄：A 键 = 未翻面则翻面、已翻面则「想起来了」。</summary>
    public bool PadAccept()
    {
        if (_answered || _question is null) return false;
        if (!_revealed) { Reveal(); return true; }
        SelfRate(true);
        return true;
    }

    /// <summary>手柄：X 键 = 「没想起来」（未翻面时先翻面，避免没看答案就判错）。</summary>
    public bool PadForgot()
    {
        if (_answered || _question is null) return false;
        if (!_revealed) { Reveal(); return true; }
        SelfRate(false);
        return true;
    }

    private void Reveal()
    {
        if (_answered || _revealed || _question is null) return;
        _revealed = true;

        _answerLabel.Text = _question.CorrectText;
        _answerLabel.Visible = true;
        _hintLabel.Visible = false;
        _revealBtn.Visible = false;
        _rateRow.Visible = true;
    }

    private void SelfRate(bool remembered)
    {
        if (_answered || !_revealed || _question is null) return;
        _answered = true;
        _answeredAtMsec = Time.GetTicksMsec();

        _rememberedBtn.Disabled = true;
        _forgotBtn.Disabled = true;
        _rateRow.Visible = false;

        // 与选择题一致：判定完成后自动朗读本词（固定选择题条目不会走回忆卡片，无需判 IsFixedChoice）
        if (VocabConfig.Instance.AutoSpeakOnAnswer)
            TtsService.Instance.Speak(_question.TargetWord.English);

        _onAnswered?.Invoke(remembered);
    }
}
