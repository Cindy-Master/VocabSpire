using VocabSpire.Models;

namespace VocabSpire.Services;

public sealed class QuizGenerator
{
    private readonly Random _random = new();

    /// <summary>最近出过的单词队列，用于防止短期内重复出题。</summary>
    private readonly Queue<WordEntry> _recentWords = new();

    /// <summary>防重窗口大小：词库词数的 1/3，上限 20，下限 3。</summary>
    private static int GetCooldownSize(int bankSize) => Math.Clamp(bankSize / 3, 3, 20);

    /// <summary>
    /// 生成一道题。tier: 1-3 对应 Act 层级（难度递增）。
    /// </summary>
    public QuizQuestion? Generate(WordBank bank, QuizModeFlags enabledModes, int optionCount = 4, int tier = 1)
    {
        if (!bank.IsValid || enabledModes == QuizModeFlags.None) return null;

        var targetWord = SelectWeightedWord(bank.Words);
        var mode = PickMode(enabledModes, targetWord, tier);
        var effectiveOptionCount = GetEffectiveOptionCount(optionCount, tier);

        if (mode == QuizModeFlags.SpellEnglish)
            return GenerateSpellingQuestion(targetWord, tier);

        return GenerateMultipleChoiceQuestion(targetWord, bank, mode, effectiveOptionCount, tier);
    }

    /// <summary>
    /// 使用指定词池生成题目（用于拼写复习模式，词池为已测试过的词）。
    /// 干扰项仍从完整词库选取以保证多样性。
    /// </summary>
    public QuizQuestion? Generate(WordBank bank, List<WordEntry> wordPool, QuizModeFlags enabledModes, int optionCount = 4, int tier = 1)
    {
        if (wordPool.Count < 2 || enabledModes == QuizModeFlags.None) return null;

        var targetWord = SelectWeightedWord(wordPool);
        var mode = PickMode(enabledModes, targetWord, tier);
        var effectiveOptionCount = GetEffectiveOptionCount(optionCount, tier);

        if (mode == QuizModeFlags.SpellEnglish)
            return GenerateSpellingQuestion(targetWord, tier);

        return GenerateMultipleChoiceQuestion(targetWord, bank, mode, effectiveOptionCount, tier);
    }

    /// <summary>
    /// 为指定单词生成一道题（用于错题复习），支持选择和拼写模式。
    /// </summary>
    public QuizQuestion? GenerateForWord(WordEntry target, WordBank bank, QuizModeFlags mode, int optionCount = 4)
    {
        if (!bank.IsValid || target is null) return null;
        if (mode == QuizModeFlags.SpellEnglish)
            return GenerateSpellingQuestion(target, tier: 1);
        return GenerateMultipleChoiceQuestion(target, bank, mode, optionCount, tier: 1);
    }

    // ── 难度分层：模式选择 ──

    private QuizModeFlags PickMode(QuizModeFlags flags, WordEntry target, int tier)
    {
        var modes = new List<QuizModeFlags>();
        if (flags.HasFlag(QuizModeFlags.EnglishToChinese)) modes.Add(QuizModeFlags.EnglishToChinese);
        if (flags.HasFlag(QuizModeFlags.ChineseToEnglish)) modes.Add(QuizModeFlags.ChineseToEnglish);
        if (flags.HasFlag(QuizModeFlags.ListenToChinese)) modes.Add(QuizModeFlags.ListenToChinese);
        if (flags.HasFlag(QuizModeFlags.SpellEnglish)) modes.Add(QuizModeFlags.SpellEnglish);

        var chosen = modes[_random.Next(modes.Count)];

        // 难度递增：高层级有概率强制拼写
        if (tier >= 2 && chosen != QuizModeFlags.SpellEnglish)
        {
            var spellChance = tier >= 3 ? 0.70 : 0.40;

            // 已掌握的词更容易被强制拼写
            if (target.CorrectCount > 2 && target.Accuracy > 0.7f)
                spellChance += 0.20;

            if (_random.NextDouble() < spellChance)
                chosen = QuizModeFlags.SpellEnglish;
        }

        // Tier 3: 有概率反转模式（英→中 变 中→英）
        if (tier >= 3 && chosen != QuizModeFlags.SpellEnglish && _random.NextDouble() < 0.30)
        {
            chosen = chosen == QuizModeFlags.EnglishToChinese
                ? QuizModeFlags.ChineseToEnglish
                : QuizModeFlags.EnglishToChinese;
        }

        return chosen;
    }

    private int GetEffectiveOptionCount(int baseCount, int tier)
    {
        var extra = tier >= 3 ? 2 : tier >= 2 ? 1 : 0;
        return Math.Min(6, baseCount + extra);
    }

    // ── 拼写题生成 ──

    private QuizQuestion GenerateSpellingQuestion(WordEntry target, int tier)
    {
        // 拼写题显示全部释义（多义词显示所有义项）
        var prompt = target.HasMultipleDefinitions
            ? string.Join("\n", target.Definitions)
            : target.Chinese;

        // Tier 1 提示音标，Tier 2+ 不提示
        if (tier <= 1 && !string.IsNullOrWhiteSpace(target.Phonetic))
            prompt += $"\n{target.Phonetic}";

        return new QuizQuestion
        {
            TargetWord = target,
            Mode = QuizModeFlags.SpellEnglish,
            Prompt = prompt,
            Options = Array.Empty<string>(),
            CorrectIndex = -1,
            CorrectText = target.English
        };
    }

    // ── 选择题生成 ──

    private QuizQuestion GenerateMultipleChoiceQuestion(
        WordEntry target, WordBank bank, QuizModeFlags mode, int optionCount, int tier)
    {
        // 听力模式与英→中相同逻辑（选项是中文释义）
        var isEnToCn = mode == QuizModeFlags.EnglishToChinese || mode == QuizModeFlags.ListenToChinese;

        // 多义词 + 英→中/听力模式 → 有概率出多选题（不是每次都多选）
        if (isEnToCn && target.HasMultipleDefinitions && _random.NextDouble() < 0.4)
            return GenerateMultiSelectQuestion(target, bank, mode, optionCount, tier);

        var distractorCount = Math.Min(optionCount - 1, bank.Words.Count - 1);
        var distractorWords = SelectDistractorWords(bank.Words, target, distractorCount, isEnToCn, tier);

        // 多义词单选时随机取其中一个释义，不用合并的全文
        var correctChinese = (isEnToCn && target.HasMultipleDefinitions)
            ? target.Definitions[_random.Next(target.Definitions.Count)]
            : target.Chinese;
        var correctAnswer = isEnToCn ? correctChinese : target.English;
        var correctDetail = isEnToCn ? target.English : target.Chinese;

        var pairs = distractorWords
            .Select(w => (
                option: isEnToCn ? w.Chinese : w.English,
                detail: isEnToCn ? w.English : w.Chinese))
            .ToList();
        pairs.Add((option: correctAnswer, detail: correctDetail));
        Shuffle(pairs);

        var options = pairs.Select(p => p.option).ToList();
        var details = pairs.Select(p => p.detail).ToList();

        var prompt = mode == QuizModeFlags.ListenToChinese
            ? "\uD83D\uDD0A \u70B9\u51FB\u64AD\u653E\u53D1\u97F3"
            : isEnToCn ? FormatPrompt(target, tier) : target.Chinese;

        if (isEnToCn && target.HasMultipleDefinitions)
            prompt += "\n\u3010\u5355\u9009\u9898\u3011";

        return new QuizQuestion
        {
            TargetWord = target,
            Mode = mode,
            Prompt = prompt,
            Options = options.AsReadOnly(),
            OptionDetails = details.AsReadOnly(),
            CorrectIndex = options.IndexOf(correctAnswer),
            CorrectText = correctAnswer
        };
    }

    /// <summary>多选题：多义词拆分为多个正确选项 + 干扰项。</summary>
    private QuizQuestion GenerateMultiSelectQuestion(
        WordEntry target, WordBank bank, QuizModeFlags mode, int optionCount, int tier)
    {
        var definitions = target.Definitions;
        var correctCount = definitions.Count;

        // 干扰项数量 = 总选项数 - 正确答案数
        var distractorCount = Math.Min(optionCount + correctCount - 1, bank.Words.Count - 1);
        distractorCount = Math.Max(distractorCount - correctCount, 1);
        var distractorWords = SelectDistractorWords(bank.Words, target, distractorCount, true, tier);

        // 正确释义集合（用于排除重复的干扰项）
        var correctSet = new HashSet<string>(definitions);

        var pairs = distractorWords
            .Where(w => !correctSet.Contains(w.Chinese)) // 干扰项不能跟正确答案重复
            .Select(w => (option: w.Chinese, detail: w.English, isCorrect: false))
            .ToList();

        // 加入所有正确释义（去重）
        var addedCorrect = new HashSet<string>();
        foreach (var def in definitions)
        {
            if (addedCorrect.Add(def)) // 重复的释义不重复添加
                pairs.Add((option: def, detail: target.English, isCorrect: true));
        }

        // 实际正确选项不足2个，回退到单选
        if (addedCorrect.Count < 2)
            return GenerateMultipleChoiceQuestion(target, bank, mode, optionCount, tier);

        Shuffle(pairs);

        var options = pairs.Select(p => p.option).ToList();
        var details = pairs.Select(p => p.detail).ToList();
        var correctIndices = new List<int>();
        for (var i = 0; i < pairs.Count; i++)
        {
            if (pairs[i].isCorrect) correctIndices.Add(i);
        }

        var prompt = mode == QuizModeFlags.ListenToChinese
            ? "\uD83D\uDD0A \u70B9\u51FB\u64AD\u653E\u53D1\u97F3"
            : FormatPrompt(target, tier);

        prompt += "\n\u3010\u591A\u9009\u9898\u3011";

        return new QuizQuestion
        {
            TargetWord = target,
            Mode = mode,
            Prompt = prompt,
            Options = options.AsReadOnly(),
            OptionDetails = details.AsReadOnly(),
            CorrectIndex = correctIndices.Count > 0 ? correctIndices[0] : 0,
            CorrectIndices = correctIndices.AsReadOnly(),
            CorrectText = target.Chinese
        };
    }

    private static string FormatPrompt(WordEntry word, int tier)
    {
        var prompt = word.English;
        // Tier 1 显示音标，Tier 2+ 隐藏
        if (tier <= 1 && !string.IsNullOrWhiteSpace(word.Phonetic))
            prompt += $"  {word.Phonetic}";
        return prompt;
    }

    // ── 干扰项选择（核心难度差异）──

    private List<WordEntry> SelectDistractorWords(
        List<WordEntry> allWords, WordEntry target, int count, bool isEnToCn, int tier)
    {
        var candidates = allWords.Where(w => w != target).ToList();

        if (tier <= 1)
        {
            // Tier 1: 完全随机，按选项文本去重
            return candidates
                .OrderBy(_ => _random.Next())
                .GroupBy(w => isEnToCn ? w.Chinese : w.English)
                .Select(g => g.First())
                .Take(count)
                .ToList();
        }

        // Tier 2+: 按混淆度排序，取最容易混淆的
        var scored = candidates
            .Select(w => (word: w, score: ConfusionScore(target, w, isEnToCn, tier)))
            .OrderByDescending(x => x.score)
            .ThenBy(_ => _random.Next()) // 同分随机
            .ToList();

        return scored
            .GroupBy(x => isEnToCn ? x.word.Chinese : x.word.English)
            .Select(g => g.First().word)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// 混淆度评分 —— 越高越容易与目标混淆。
    /// Tier 2: 首字母、长度、同词根
    /// Tier 3: 编辑距离、同后缀、强同词根
    /// </summary>
    private static double ConfusionScore(WordEntry target, WordEntry candidate, bool isEnToCn, int tier)
    {
        double score = 0;

        // 同词根加分（对所有模式都有效，Tier 越高加分越多）
        var sameRoot = ShareRoot(target.English, candidate.English);
        if (sameRoot)
            score += tier >= 3 ? 12.0 : tier >= 2 ? 8.0 : 0;

        if (isEnToCn)
        {
            // 干扰项是中文释义
            var tc = target.Chinese;
            var cc = candidate.Chinese;

            // 同词根时：中文释义作为干扰项极具迷惑性
            if (sameRoot) score += 5.0;

            // 共享汉字
            foreach (var ch in cc)
                if (tc.Contains(ch) && ch != '.' && ch != ' ') score += 3.0;

            // 长度接近
            score += 2.0 / (1 + Math.Abs(tc.Length - cc.Length));

            // Tier 3: 有相同词性标记（n./v./adj.）
            if (tier >= 3 && tc.Length >= 2 && cc.Length >= 2 && tc[..2] == cc[..2])
                score += 4.0;
        }
        else
        {
            // 干扰项是英文单词
            var te = target.English.ToLowerInvariant();
            var ce = candidate.English.ToLowerInvariant();

            // 同词根时：英文选项极具迷惑性
            if (sameRoot) score += 5.0;

            // 同首字母
            if (te.Length > 0 && ce.Length > 0 && te[0] == ce[0])
                score += 3.0;

            // 长度接近
            score += 2.0 / (1 + Math.Abs(te.Length - ce.Length));

            // 共享字母比例
            var shared = te.Intersect(ce).Count();
            score += shared * 0.5;

            // Tier 3: 编辑距离（越小越混淆）
            if (tier >= 3)
            {
                var dist = LevenshteinDistance(te, ce);
                score += 6.0 / (1 + dist);

                // 同后缀
                if (te.Length >= 3 && ce.Length >= 3 && te[^3..] == ce[^3..])
                    score += 3.0;
            }
        }

        return score;
    }

    // ── 词根匹配 ──

    /// <summary>
    /// 判断两个英文单词是否同词根。
    /// 通过后缀剥离提取近似词根，再比较。
    /// 例: act/action/active/actor → 词根 "act"
    ///     happy/happiness/happily → 词根 "happi"/"happy"
    ///     create/creation/creative → 词根 "creat"
    /// </summary>
    private static bool ShareRoot(string a, string b)
    {
        var stemA = ExtractStem(a.ToLowerInvariant());
        var stemB = ExtractStem(b.ToLowerInvariant());

        if (stemA.Length < 3 || stemB.Length < 3) return false;

        // 完全匹配
        if (stemA == stemB) return true;

        // 一个是另一个的前缀（cover/discover 等）
        if (stemA.StartsWith(stemB) || stemB.StartsWith(stemA))
        {
            var shorter = Math.Min(stemA.Length, stemB.Length);
            var longer = Math.Max(stemA.Length, stemB.Length);
            // 前缀占比要足够高（避免 "in" 匹配 "interest"）
            return shorter >= 3 && (double)shorter / longer >= 0.6;
        }

        return false;
    }

    /// <summary>
    /// 英文后缀剥离 —— 简化版 Porter Stemmer。
    /// 依次尝试剥离最长后缀，保留至少3个字符的词根。
    /// </summary>
    private static string ExtractStem(string word)
    {
        // 按长度降序排列，优先匹配最长后缀
        ReadOnlySpan<string> suffixes = new[]
        {
            "ization", "ational", "fulness", "iveness", "ousness",
            "ation", "ition", "ement", "iness", "ness", "ment", "able", "ible",
            "tion", "sion", "ence", "ance", "ious", "eous", "ious", "ical",
            "ally", "ment", "less", "ness",
            "ful", "ous", "ive", "ize", "ise", "ity", "ant", "ent",
            "ing", "ely", "ion", "ial",
            "ed", "er", "or", "ly", "al", "en", "es",
            "s"
        };

        foreach (var suffix in suffixes)
        {
            if (word.Length > suffix.Length + 2 && word.EndsWith(suffix))
                return word[..^suffix.Length];
        }

        return word;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    // ── 加权选词（含防重复冷却）──

    private WordEntry SelectWeightedWord(List<WordEntry> words)
    {
        var cooldownSize = GetCooldownSize(words.Count);
        var recentSet = new HashSet<WordEntry>(_recentWords);

        var weights = words.Select(w =>
        {
            // 冷却窗口内的词权重设为 0，强制不重复
            if (recentSet.Contains(w)) return 0.0;

            var total = w.CorrectCount + w.WrongCount;
            if (total == 0) return 3.0;
            return Math.Max(0.5, 3.0 * (1.0 - w.Accuracy));
        }).ToList();

        var totalWeight = weights.Sum();

        // 如果所有词都在冷却中（词库极小），清空冷却重来
        if (totalWeight <= 0)
        {
            _recentWords.Clear();
            return SelectWeightedWord(words);
        }

        var roll = _random.NextDouble() * totalWeight;

        var cumulative = 0.0;
        WordEntry selected = words[^1];
        for (var i = 0; i < words.Count; i++)
        {
            cumulative += weights[i];
            if (roll <= cumulative)
            {
                selected = words[i];
                break;
            }
        }

        // 记入冷却队列
        _recentWords.Enqueue(selected);
        while (_recentWords.Count > cooldownSize)
            _recentWords.Dequeue();

        return selected;
    }

    private void Shuffle<T>(List<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
