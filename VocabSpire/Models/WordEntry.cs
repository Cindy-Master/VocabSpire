namespace VocabSpire.Models;

public sealed class WordEntry
{
    public string English { get; init; } = "";
    public string Chinese { get; init; } = "";
    public string Phonetic { get; init; } = "";
    public List<string> Definitions { get; init; } = new();

    public bool HasMultipleDefinitions => Definitions.Count > 1;
    public bool HasPhonetic => !string.IsNullOrWhiteSpace(Phonetic);

    public int CorrectCount { get; set; }
    public int WrongCount { get; set; }

    /// <summary>当前连续答对次数（答错归零）。</summary>
    public int Streak { get; set; }

    /// <summary>因答错该词损失的总能量。</summary>
    public int EnergyLost { get; set; }

    // ── 间隔重复调度状态（v2.7 记忆引擎）──
    /// <summary>掌握盒 0-5：0=生词/刚答错，5=已牢固。决定下次复习间隔。</summary>
    public int Box { get; set; }

    /// <summary>下次该复习的全局序号（GlobalTick）。学习阶段(Box&lt;3)用：tick 到达即「到期」。</summary>
    public long DueTick { get; set; }

    /// <summary>上次遇到该词的真实时间（Unix 秒）。毕业词(Box≥3)按真实天数判断「搁久了该复习」。</summary>
    public long LastSeenDate { get; set; }

    /// <summary>是否已答过（区分 新词 / 学习中 / 已掌握）。</summary>
    public bool Seen => CorrectCount + WrongCount > 0;

    public float Accuracy => (CorrectCount + WrongCount) == 0
        ? 0f
        : (float)CorrectCount / (CorrectCount + WrongCount);

}
