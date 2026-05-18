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

    public float Accuracy => (CorrectCount + WrongCount) == 0
        ? 0f
        : (float)CorrectCount / (CorrectCount + WrongCount);
}
