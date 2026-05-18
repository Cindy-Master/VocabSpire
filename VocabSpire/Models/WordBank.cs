namespace VocabSpire.Models;

public sealed class WordBank
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public List<WordEntry> Words { get; init; } = new();

    public int TotalWords => Words.Count;
    public bool IsValid => Words.Count >= 2;
    public int WordsWithPhonetic => Words.Count(w => w.HasPhonetic);
    public int WordsWithMultiDefs => Words.Count(w => w.HasMultipleDefinitions);
}
