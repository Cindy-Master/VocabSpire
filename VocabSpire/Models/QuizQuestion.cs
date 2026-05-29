namespace VocabSpire.Models;

[Flags]
public enum QuizModeFlags
{
    None = 0,
    EnglishToChinese = 1 << 0,  // 英→中 选择题
    ChineseToEnglish = 1 << 1,  // 中→英 选择题
    SpellEnglish     = 1 << 2,  // 中→英 拼写
    ListenToChinese  = 1 << 3,  // 听力→中 选择题（听发音选释义）
}

public sealed class QuizQuestion
{
    public required WordEntry TargetWord { get; init; }
    public required QuizModeFlags Mode { get; init; }
    public required string Prompt { get; init; }
    public required IReadOnlyList<string> Options { get; init; }
    public required int CorrectIndex { get; init; }

    /// <summary>拼写模式的正确答案文本。</summary>
    public string CorrectText { get; init; } = "";

    /// <summary>拼写简单模式的掩码提示（如 "c _ _ e"）。为空表示困难模式（从零拼写，无提示）。</summary>
    public string SpellingHint { get; init; } = "";

    /// <summary>每个选项对应的补充信息（英→中时为英文单词，中→英时为中文释义）。</summary>
    public IReadOnlyList<string> OptionDetails { get; init; } = Array.Empty<string>();

    /// <summary>多选题：所有正确选项的索引。为空则为单选。</summary>
    public IReadOnlyList<int> CorrectIndices { get; init; } = Array.Empty<int>();

    public bool IsSpelling => Mode == QuizModeFlags.SpellEnglish;
    public bool IsListening => Mode == QuizModeFlags.ListenToChinese;
    public bool IsMultiSelect => CorrectIndices.Count > 1;

    public bool CheckAnswer(int selectedIndex) => selectedIndex == CorrectIndex;

    /// <summary>多选：检查选中的索引集合是否完全匹配所有正确答案。</summary>
    public bool CheckMultiAnswer(HashSet<int> selected) =>
        selected.Count == CorrectIndices.Count && CorrectIndices.All(selected.Contains);

    public bool CheckSpelling(string input) =>
        string.Equals(input?.Trim(), CorrectText, StringComparison.OrdinalIgnoreCase);

    /// <summary>获取指定选项的补充详情（若有）。</summary>
    public string? GetDetail(int index) =>
        index >= 0 && index < OptionDetails.Count ? OptionDetails[index] : null;
}
