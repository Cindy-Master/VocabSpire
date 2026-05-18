using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;

namespace VocabSpire.Services;

public sealed class VocabConfig
{
    public static VocabConfig Instance { get; } = new();

    public bool Enabled { get; set; } = true;
    public string ActiveBankId { get; set; } = "";

    /// <summary>设置面板快捷键（默认 F8）。</summary>
    public Key SettingsHotkey { get; set; } = Key.F8;
    public QuizModeFlags QuizModes { get; set; } = QuizModeFlags.EnglishToChinese | QuizModeFlags.ChineseToEnglish;
    public int OptionCount { get; set; } = 4;
    public bool ShowCombatSummary { get; set; } = true;
    public bool ShowRestSiteReview { get; set; } = true;
    public bool EnableDifficultyScaling { get; set; } = true;

    // ── 分层模式配置 ──
    public bool UsePerActModes { get; set; }
    public QuizModeFlags Act1Modes { get; set; } = QuizModeFlags.EnglishToChinese | QuizModeFlags.ChineseToEnglish;
    public QuizModeFlags Act2Modes { get; set; } = QuizModeFlags.ChineseToEnglish | QuizModeFlags.SpellEnglish;
    public QuizModeFlags Act3Modes { get; set; } = QuizModeFlags.SpellEnglish;

    /// <summary>拼写模式(Act2+)仅从本局已出过的词中选取。</summary>
    public bool SpellingReviewOnly { get; set; }

    // ── 篝火复习设置 ──
    /// <summary>掌握判定：连续答对次数阈值（默认3）。</summary>
    public int MasteryStreak { get; set; } = 3;

    /// <summary>听力发音音量（0-100，独立于游戏音量）。</summary>
    public int TtsVolume { get; set; } = 80;

    /// <summary>篝火复习的答题模式（默认英→中）。</summary>
    public QuizModeFlags ReviewQuizMode { get; set; } = QuizModeFlags.EnglishToChinese;

    /// <summary>篝火复习最大题数（0=全部错题）。</summary>
    public int ReviewMaxCount { get; set; }

    public int TotalAnswered { get; set; }
    public int TotalCorrect { get; set; }

    /// <summary>获取指定 Act 的有效答题模式。</summary>
    public QuizModeFlags GetModesForAct(int act)
    {
        if (!UsePerActModes) return QuizModes;
        var modes = act switch
        {
            1 => Act1Modes,
            2 => Act2Modes,
            _ => Act3Modes
        };
        return modes == QuizModeFlags.None ? QuizModes : modes;
    }

    public float OverallAccuracy => TotalAnswered == 0
        ? 0f
        : (float)TotalCorrect / TotalAnswered;

    private string ConfigPath
    {
        get
        {
            var modDir = Path.GetDirectoryName(typeof(VocabConfig).Assembly.Location) ?? ".";
            return Path.Combine(modDir, "vocabspire_config.json");
        }
    }

    private VocabConfig() { }

    public void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;

            var json = File.ReadAllText(ConfigPath);
            var data = JsonSerializer.Deserialize<ConfigData>(json);
            if (data is null) return;

            Enabled = data.Enabled;
            ActiveBankId = data.ActiveBankId ?? "";
            if (data.SettingsHotkey > 0) SettingsHotkey = (Key)data.SettingsHotkey;
            OptionCount = Math.Clamp(data.OptionCount, 2, 6);
            TotalAnswered = data.TotalAnswered;
            TotalCorrect = data.TotalCorrect;
            ShowCombatSummary = data.ShowCombatSummary;
            ShowRestSiteReview = data.ShowRestSiteReview;
            EnableDifficultyScaling = data.EnableDifficultyScaling;

            UsePerActModes = data.UsePerActModes;
            if (data.Act1Modes > 0) Act1Modes = (QuizModeFlags)data.Act1Modes;
            if (data.Act2Modes > 0) Act2Modes = (QuizModeFlags)data.Act2Modes;
            if (data.Act3Modes > 0) Act3Modes = (QuizModeFlags)data.Act3Modes;
            SpellingReviewOnly = data.SpellingReviewOnly;
            if (data.ReviewQuizMode > 0) ReviewQuizMode = (QuizModeFlags)data.ReviewQuizMode;
            ReviewMaxCount = Math.Max(0, data.ReviewMaxCount);
            if (data.MasteryStreak > 0) MasteryStreak = data.MasteryStreak;
            if (data.TtsVolume >= 0) TtsVolume = Math.Clamp(data.TtsVolume, 0, 100);

            // 迁移旧配置：quiz_mode (单选) → quiz_mode_flags (多选)
            if (data.QuizModeFlags > 0)
            {
                QuizModes = (QuizModeFlags)data.QuizModeFlags;
            }
            else
            {
                QuizModes = data.QuizMode switch
                {
                    0 => QuizModeFlags.EnglishToChinese,
                    1 => QuizModeFlags.ChineseToEnglish,
                    _ => QuizModeFlags.EnglishToChinese | QuizModeFlags.ChineseToEnglish
                };
            }

            if (QuizModes == QuizModeFlags.None)
                QuizModes = QuizModeFlags.EnglishToChinese | QuizModeFlags.ChineseToEnglish;

            Log.Info("[VocabSpire] Config loaded.");
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to load config: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            var data = new ConfigData
            {
                Enabled = Enabled,
                ActiveBankId = ActiveBankId,
                SettingsHotkey = (int)SettingsHotkey,
                QuizModeFlags = (int)QuizModes,
                OptionCount = OptionCount,
                ShowCombatSummary = ShowCombatSummary,
                ShowRestSiteReview = ShowRestSiteReview,
                EnableDifficultyScaling = EnableDifficultyScaling,
                UsePerActModes = UsePerActModes,
                Act1Modes = (int)Act1Modes,
                Act2Modes = (int)Act2Modes,
                Act3Modes = (int)Act3Modes,
                SpellingReviewOnly = SpellingReviewOnly,
                ReviewQuizMode = (int)ReviewQuizMode,
                ReviewMaxCount = ReviewMaxCount,
                MasteryStreak = MasteryStreak,
                TtsVolume = TtsVolume,
                TotalAnswered = TotalAnswered,
                TotalCorrect = TotalCorrect
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to save config: {ex.Message}");
        }
    }

    private sealed class ConfigData
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("active_bank_id")]
        public string? ActiveBankId { get; set; }

        [JsonPropertyName("settings_hotkey")]
        public int SettingsHotkey { get; set; }

        [JsonPropertyName("quiz_mode")]
        public int QuizMode { get; set; } = 2;

        [JsonPropertyName("quiz_mode_flags")]
        public int QuizModeFlags { get; set; }

        [JsonPropertyName("option_count")]
        public int OptionCount { get; set; } = 4;

        [JsonPropertyName("show_combat_summary")]
        public bool ShowCombatSummary { get; set; } = true;

        [JsonPropertyName("show_rest_site_review")]
        public bool ShowRestSiteReview { get; set; } = true;

        [JsonPropertyName("enable_difficulty_scaling")]
        public bool EnableDifficultyScaling { get; set; } = true;

        [JsonPropertyName("use_per_act_modes")]
        public bool UsePerActModes { get; set; }

        [JsonPropertyName("act1_modes")]
        public int Act1Modes { get; set; }

        [JsonPropertyName("act2_modes")]
        public int Act2Modes { get; set; }

        [JsonPropertyName("act3_modes")]
        public int Act3Modes { get; set; }

        [JsonPropertyName("spelling_review_only")]
        public bool SpellingReviewOnly { get; set; }

        [JsonPropertyName("review_quiz_mode")]
        public int ReviewQuizMode { get; set; }

        [JsonPropertyName("review_max_count")]
        public int ReviewMaxCount { get; set; }

        [JsonPropertyName("mastery_streak")]
        public int MasteryStreak { get; set; }

        [JsonPropertyName("tts_volume")]
        public int TtsVolume { get; set; } = 80;

        [JsonPropertyName("total_answered")]
        public int TotalAnswered { get; set; }

        [JsonPropertyName("total_correct")]
        public int TotalCorrect { get; set; }
    }
}
