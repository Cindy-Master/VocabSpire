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

    /// <summary>扩展式复习间隔（题为单位）：Box 越高间隔越长（间隔效应 + 扩展提取，
    /// 词量大时扩展式更优）。答对升盒拉长间隔，答错回 Box0 很快重现。</summary>
    public static long Interval(int box) => box switch
    {
        <= 0 => 3,
        1 => 8,
        2 => 20,
        3 => 50,
        4 => 120,
        _ => 300
    };

    /// <summary>毕业词(Box≥3)的跨天复习间隔（真实天数）：搁够这么多天就该复习。
    /// 宽容式——到期不玩不堆债，下次进游戏自然优先重现，不催不惩罚。</summary>
    public static int IntervalDays(int box) => box switch
    {
        3 => 1,
        4 => 3,
        _ => 7
    };
}
