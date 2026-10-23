namespace VocabSpire.Models;

public sealed class WordEntry
{
    /// <summary>题库内稳定条目 ID；用于区分题干相同但选项/答案不同的固定题。</summary>
    public string EntryId { get; init; } = "";

    public string English { get; init; } = "";
    public string Chinese { get; init; } = "";
    public string Phonetic { get; init; } = "";
    public List<string> Definitions { get; init; } = new();

    public bool HasMultipleDefinitions => Definitions.Count > 1;
    public bool HasPhonetic => !string.IsNullOrWhiteSpace(Phonetic);

    // ── 固定选择题（题库模式）：Options 非空 = 本词条是一道自带选项的固定选择题 ──
    /// <summary>固定选择题的选项列表（A/B/C…的文本，不含字母前缀）。普通单词条目为空。</summary>
    public List<string> Options { get; init; } = new();

    /// <summary>固定选择题的正确选项索引（指向 Options）。普通单词条目为 -1。</summary>
    public int FixedCorrectIndex { get; init; } = -1;

    /// <summary>固定多选题的全部正确选项索引。单选题可为空，继续使用 FixedCorrectIndex 兼容旧词库。</summary>
    public List<int> FixedCorrectIndices { get; init; } = new();

    /// <summary>作答后展示的题目解析。普通单词条目为空。</summary>
    public string Explanation { get; init; } = "";

    /// <summary>题目所属章节/知识点路径。</summary>
    public string Category { get; init; } = "";

    /// <summary>固定题题干中的本地图片路径。旧题库为空，不进入图片加载路径。</summary>
    public List<string> QuestionImages { get; init; } = new();

    /// <summary>固定题每个选项对应的本地图片路径列表，与 Options 按索引对齐。</summary>
    public List<List<string>> OptionImages { get; init; } = new();

    /// <summary>固定题解析中的本地图片路径。</summary>
    public List<string> ExplanationImages { get; init; } = new();

    /// <summary>固定题的有效正确答案索引；兼容旧版仅含 FixedCorrectIndex 的题库。</summary>
    public IReadOnlyList<int> EffectiveFixedCorrectIndices => FixedCorrectIndices.Count > 0
        ? FixedCorrectIndices
        : (FixedCorrectIndex >= 0 ? new[] { FixedCorrectIndex } : Array.Empty<int>());

    /// <summary>是否为固定选择题条目（题干存 English、正确答案文本存 Chinese）。</summary>
    public bool IsFixedChoice => Options.Count > 0
        && EffectiveFixedCorrectIndices.Count > 0
        && EffectiveFixedCorrectIndices.All(i => i >= 0 && i < Options.Count);

    public bool IsFixedMultiSelect => IsFixedChoice && EffectiveFixedCorrectIndices.Count > 1;

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
