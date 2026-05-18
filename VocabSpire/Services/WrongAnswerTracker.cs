using VocabSpire.Models;

namespace VocabSpire.Services;

/// <summary>
/// 错题追踪器 —— 按战斗和篝火区间分别记录错题。
/// </summary>
public sealed class WrongAnswerTracker
{
    public static WrongAnswerTracker Instance { get; } = new();

    private readonly List<WrongAnswerRecord> _combatRecords = new();
    private readonly List<WrongAnswerRecord> _segmentRecords = new();

    private WrongAnswerTracker() { }

    public void RecordWrongAnswer(WrongAnswerRecord record)
    {
        _combatRecords.Add(record);
        _segmentRecords.Add(record);
    }

    public void ClearCombatAnswers() => _combatRecords.Clear();

    public IReadOnlyList<WrongAnswerRecord> FlushCombatAnswers()
    {
        var copy = _combatRecords.ToList().AsReadOnly();
        _combatRecords.Clear();
        return copy;
    }

    public IReadOnlyList<WrongAnswerRecord> FlushSegmentAnswers()
    {
        var copy = _segmentRecords.ToList().AsReadOnly();
        _segmentRecords.Clear();
        return copy;
    }
}
