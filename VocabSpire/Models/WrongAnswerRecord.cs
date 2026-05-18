namespace VocabSpire.Models;

public sealed record WrongAnswerRecord(
    WordEntry Word,
    QuizModeFlags Mode,
    string Prompt,
    string UserAnswer,
    string CorrectAnswer,
    string UserAnswerDetail = "",
    string CorrectAnswerDetail = ""
);
