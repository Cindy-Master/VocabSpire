using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;

namespace VocabSpire.Services;

/// <summary>奖励类型。0=无；1-5 基础；6+ 第二期。</summary>
public enum RewardType
{
    None      = 0,
    Hp        = 1,
    Energy    = 2,
    Gold      = 3,
    Strength  = 4,
    Dexterity = 5,
    Block     = 6,  // 覆甲
    Draw      = 7,  // 抽牌
    Thorns    = 8,  // 荆棘
    Focus     = 9,  // 集中
    Artifact  = 10  // 人工制品
}

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

    // ── 战斗惩罚/奖励设置 ──
    /// <summary>启用每回合容错。</summary>
    public bool ToleranceEnabled { get; set; }

    /// <summary>每回合容错次数：前 X 张牌答错不扣费且不进弃牌堆。</summary>
    public int ToleranceCount { get; set; } = 1;

    /// <summary>答错时（容错用完后）将卡牌返回手牌而非弃牌堆。</summary>
    public bool WrongCardReturnToHand { get; set; }

    /// <summary>实际是否应使用容错次数（开关 + 次数 &gt; 0）。</summary>
    public bool IsToleranceActive => ToleranceEnabled && ToleranceCount > 0;

    /// <summary>启用连续答对奖励总开关。</summary>
    public bool RewardEnabled { get; set; }

    /// <summary>奖励规则列表（原子化搭配）。</summary>
    public List<RewardRule> RewardRules { get; set; } = new();

    // ── 免错券机制 ──
    /// <summary>启用免错券。</summary>
    public bool FreePassEnabled { get; set; }

    /// <summary>累积一张券所需的连对次数。</summary>
    public int FreePassStreakCost { get; set; } = 3;

    /// <summary>最大持有数（防止过强）。</summary>
    public int FreePassMaxStock { get; set; } = 5;

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

            ToleranceEnabled = data.ToleranceEnabled;
            if (data.ToleranceCount > 0) ToleranceCount = data.ToleranceCount;
            WrongCardReturnToHand = data.WrongCardReturnToHand;
            RewardEnabled = data.RewardEnabled;

            // 多规则
            if (data.RewardRules is { Count: > 0 })
            {
                RewardRules = data.RewardRules;
            }
            else if (data.RewardKind > 0 && data.RewardAmount > 0)
            {
                // 旧配置迁移：单规则 → 多规则
                RewardRules = new List<RewardRule>
                {
                    new()
                    {
                        Enabled = true,
                        Kind = (RewardType)data.RewardKind,
                        Streak = data.RewardStreak > 0 ? data.RewardStreak : 5,
                        Amount = data.RewardAmount,
                        Mode = RewardTriggerMode.Once
                    }
                };
            }

            FreePassEnabled = data.FreePassEnabled;
            if (data.FreePassStreakCost > 0) FreePassStreakCost = data.FreePassStreakCost;
            if (data.FreePassMaxStock > 0) FreePassMaxStock = data.FreePassMaxStock;
            // FreePassStock 不在 config 中持久化——由 RunBattleState 按 Run 管理

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
                ToleranceEnabled = ToleranceEnabled,
                ToleranceCount = ToleranceCount,
                WrongCardReturnToHand = WrongCardReturnToHand,
                RewardEnabled = RewardEnabled,
                RewardRules = RewardRules,
                FreePassEnabled = FreePassEnabled,
                FreePassStreakCost = FreePassStreakCost,
                FreePassMaxStock = FreePassMaxStock,
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

        [JsonPropertyName("tolerance_enabled")]
        public bool ToleranceEnabled { get; set; }

        [JsonPropertyName("tolerance_count")]
        public int ToleranceCount { get; set; }

        [JsonPropertyName("wrong_card_return_to_hand")]
        public bool WrongCardReturnToHand { get; set; }

        [JsonPropertyName("reward_enabled")]
        public bool RewardEnabled { get; set; }

        [JsonPropertyName("reward_rules")]
        public List<RewardRule>? RewardRules { get; set; }

        // ── 旧版兼容字段（迁移后弃用）──
        [JsonPropertyName("reward_streak")]
        public int RewardStreak { get; set; }

        [JsonPropertyName("reward_kind")]
        public int RewardKind { get; set; } = -1;

        [JsonPropertyName("reward_amount")]
        public int RewardAmount { get; set; }

        [JsonPropertyName("free_pass_enabled")]
        public bool FreePassEnabled { get; set; }

        [JsonPropertyName("free_pass_streak_cost")]
        public int FreePassStreakCost { get; set; }

        [JsonPropertyName("free_pass_max_stock")]
        public int FreePassMaxStock { get; set; }

        [JsonPropertyName("total_answered")]
        public int TotalAnswered { get; set; }

        [JsonPropertyName("total_correct")]
        public int TotalCorrect { get; set; }
    }
}
