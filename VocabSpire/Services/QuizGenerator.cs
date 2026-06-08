using VocabSpire.Models;

namespace VocabSpire.Services;

public sealed class QuizGenerator
{
    /// <summary>选项按钮硬上限（QuizPanel UI 渲染上限保持一致）。</summary>
    public const int MaxOptionCount = 8;

    private readonly Random _random = new();

    /// <summary>最近出过的单词队列，用于防止短期内重复出题。</summary>
    private readonly Queue<WordEntry> _recentWords = new();

    // mini-cooldown 防连续窗口由 VocabConfig.MiniCooldown 配置（设置面板可调，默认 3）。

    // 新词节流上限由 VocabConfig.NewWordLimit 配置（设置面板可调，默认 15）。

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
    /// 复习也允许多选题（多义词），调用方需正确处理 IsMultiSelect。
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
        var cfg = VocabConfig.Instance;

        // 强制拼写：仅当用户已勾选「拼写」题型时才允许把题改成拼写
        // —— 题型勾选范围 是 上界，强制拼写不能扩大用户的题型范围。
        if (cfg.EnableForceSpelling
            && flags.HasFlag(QuizModeFlags.SpellEnglish)
            && tier >= 2 && chosen != QuizModeFlags.SpellEnglish)
        {
            var pct = tier >= 3 ? cfg.ForceSpellingChanceAct3Percent : cfg.ForceSpellingChanceAct2Percent;

            // 用户显式设为 0% → 严格 0%，不再叠加任何隐藏加成。
            if (pct > 0)
            {
                var spellChance = pct / 100.0;
                // ④ 难度随掌握度：Box 越高（越接近掌握）越倾向升级为拼写（更难的产出性回忆）。
                if (target.Box >= 3)
                    spellChance += (target.Box - 2) * 0.15; // Box3 +15% / Box4 +30% / Box5 +45%
                if (_random.NextDouble() < spellChance)
                    chosen = QuizModeFlags.SpellEnglish;
            }
        }

        // 反转模式：仅当反转后的题型也在用户勾选范围内时才生效
        // —— 例如 chosen=英→中，反转目标=中→英；只在用户勾了"中→英"时才反。
        if (cfg.EnableReverseMode && tier >= 3 && chosen != QuizModeFlags.SpellEnglish
            && _random.NextDouble() < cfg.ReverseModeChancePercent / 100.0)
        {
            var reversed = chosen == QuizModeFlags.EnglishToChinese
                ? QuizModeFlags.ChineseToEnglish
                : chosen == QuizModeFlags.ChineseToEnglish
                    ? QuizModeFlags.EnglishToChinese
                    : chosen;
            if (reversed != chosen && flags.HasFlag(reversed))
                chosen = reversed;
        }

        return chosen;
    }

    private int GetEffectiveOptionCount(int baseCount, int tier)
    {
        if (!VocabConfig.Instance.EnableOptionCountScaling) return Math.Min(MaxOptionCount, baseCount);
        var extra = tier >= 3 ? 2 : tier >= 2 ? 1 : 0;
        return Math.Min(MaxOptionCount, baseCount + extra);
    }

    // ── 拼写题生成 ──

    private QuizQuestion GenerateSpellingQuestion(WordEntry target, int tier)
    {
        // 拼写题显示全部释义（多义词显示所有义项）
        var prompt = target.HasMultipleDefinitions
            ? string.Join("\n", target.Definitions)
            : target.Chinese;

        // Tier 1 默认显示音标，Tier 2+ 隐藏；AlwaysShowPhonetic 强制全部显示。
        var showPhonetic = VocabConfig.Instance.AlwaysShowPhonetic || tier <= 1;
        if (showPhonetic && !string.IsNullOrWhiteSpace(target.Phonetic))
            prompt += $"\n{target.Phonetic}";

        // 简单模式：在单词中间挖空，挖空数量按字母数而定。困难模式不给提示。
        var hint = VocabConfig.Instance.SpellingEasyMode
            ? BuildSpellingHint(target.English)
            : "";

        return new QuizQuestion
        {
            TargetWord = target,
            Mode = QuizModeFlags.SpellEnglish,
            Prompt = prompt,
            Options = Array.Empty<string>(),
            CorrectIndex = -1,
            CorrectText = target.English,
            SpellingHint = hint
        };
    }

    /// <summary>
    /// 构造简单模式掩码提示：保留首尾字母，随机挖掉中间的若干个字母位（用 "_" 表示）。
    /// 挖空数量约为字母数的 35%，至少 1 个，且不超过可挖的中间字母数。
    /// 非字母字符（空格、连字符）始终保留可见、不计入挖空。
    /// 例: cat→"c _ t"  cake→"c _ _ e"  beautiful→"b _ a _ t _ f u l"（位置随机）
    /// </summary>
    private string BuildSpellingHint(string word)
    {
        if (string.IsNullOrEmpty(word)) return "";
        var chars = word.ToCharArray();
        var n = chars.Length;
        if (n <= 2) return string.Join(" ", chars); // 太短无可挖位，原样显示

        // 可挖位置 = 中间区间 [1, n-2] 内的字母位
        var eligible = new List<int>();
        for (var i = 1; i < n - 1; i++)
            if (char.IsLetter(chars[i])) eligible.Add(i);
        if (eligible.Count == 0) return string.Join(" ", chars);

        var letterCount = chars.Count(char.IsLetter);
        var blankCount = Math.Clamp((int)Math.Round(letterCount * 0.35), 1, eligible.Count);

        // 随机选 blankCount 个可挖位
        for (var i = eligible.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (eligible[i], eligible[j]) = (eligible[j], eligible[i]);
        }
        var blanks = new HashSet<int>(eligible.Take(blankCount));

        var parts = new string[n];
        for (var i = 0; i < n; i++)
            parts[i] = blanks.Contains(i) ? "_" : chars[i].ToString();
        return string.Join(" ", parts);
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

        // 先决定本题的"正确答案显示文本"
        var correctChinese = (isEnToCn && target.HasMultipleDefinitions)
            ? target.Definitions[_random.Next(target.Definitions.Count)]
            : target.Chinese;
        var correctAnswer = isEnToCn ? correctChinese : target.English;
        var correctDetail = isEnToCn ? target.English : target.Chinese;

        // 排除文本集合：用来阻止 distractor 的 option 文字跟 correctAnswer 撞车。
        // 多义词时把 target 所有 definitions 也一并排除，避免 distractor 任一定义跟某条正确释义一样。
        var excluded = new HashSet<string> { correctAnswer };
        if (isEnToCn)
        {
            excluded.Add(target.Chinese);
            foreach (var def in target.Definitions) excluded.Add(def);
        }

        var distractorCount = Math.Min(optionCount - 1, bank.Words.Count - 1);
        var distractorWords = SelectDistractorWords(bank.Words, target, distractorCount, isEnToCn, tier, excluded, correctAnswer);

        // 干扰项与正确答案保持「单义项」粒度对齐：英→中/听力只取一个义项，
        // 不再用整串 w.Chinese（词库里有些词条是完整词典释义，含 adj./n./vt. 几十个义项，
        // 整串塞进一个选项会撑爆 UI、长度悬殊且泄露线索）。中→英 仍用单词本身（本就短）。
        var usedTexts = new HashSet<string> { correctAnswer };
        var pairs = new List<(string option, string detail)>();
        foreach (var w in distractorWords)
        {
            string optionText;
            if (isEnToCn)
            {
                var def = PickDistractorDefinition(w, usedTexts);
                if (def is null) continue;        // 该词所有义项都被占用 → 跳过
                usedTexts.Add(def);
                optionText = def;
            }
            else
            {
                if (!usedTexts.Add(w.English)) continue;
                optionText = w.English;
            }
            pairs.Add((option: optionText, detail: isEnToCn ? w.English : w.Chinese));
        }
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

        // 严格遵守用户设置的选项数（optionCount），不超过它
        // correctCount 至多 optionCount - 1（至少留 1 个干扰位）
        var correctCount = Math.Min(definitions.Count, optionCount - 1);

        // 不足 2 个正确释义 → 回退到单选
        if (correctCount < 2)
            return GenerateMultipleChoiceQuestion(target, bank, mode, optionCount, tier);

        // 总选项数 = 用户设置的 optionCount；distractor = 剩余
        var distractorCount = Math.Max(optionCount - correctCount, 1);
        distractorCount = Math.Min(distractorCount, bank.Words.Count - 1);

        // 正确释义集合（用于排除重复的干扰项）—— 这里既给 SelectDistractorWords 做过滤，
        // 也用于下方 pairs 再次防御性 Where。
        var correctSet = new HashSet<string>(definitions);
        correctSet.Add(target.Chinese);

        var distractorWords = SelectDistractorWords(bank.Words, target, distractorCount, true, tier, correctSet, target.Chinese);

        // 干扰项同样只取单义项（与正确义项粒度对齐），避免与正确义项 / 其它干扰义项撞车。
        var usedDefs = new HashSet<string>(correctSet);
        var pairs = new List<(string option, string detail, bool isCorrect)>();
        foreach (var w in distractorWords)
        {
            var def = PickDistractorDefinition(w, usedDefs);
            if (def is null) continue;
            usedDefs.Add(def);
            pairs.Add((option: def, detail: w.English, isCorrect: false));
        }

        // 加入所有正确释义（去重，但不超过 correctCount 上限以适配 UI 按钮数量）
        var addedCorrect = new HashSet<string>();
        foreach (var def in definitions)
        {
            if (addedCorrect.Count >= correctCount) break;
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

    /// <summary>
    /// 为英→中/听力干扰项挑一个「未被占用」的中文义项，使干扰项与正确答案保持单义项粒度。
    /// 优先在未用义项里随机取；该词所有义项都被占用时返回 null（调用方跳过该词）。
    /// 没有拆出义项时退回整串 Chinese（已被上游撞车过滤保护）。
    /// </summary>
    private string? PickDistractorDefinition(WordEntry w, HashSet<string> used)
    {
        var defs = w.Definitions;
        if (defs.Count == 0)
            return used.Contains(w.Chinese) ? null : w.Chinese;

        var avail = defs.Where(d => !used.Contains(d)).ToList();
        if (avail.Count == 0) return null;
        return avail[_random.Next(avail.Count)];
    }

    private static string FormatPrompt(WordEntry word, int tier)
    {
        var prompt = word.English;
        // Tier 1 默认显示音标，Tier 2+ 隐藏；AlwaysShowPhonetic 强制全部显示。
        var showPhonetic = VocabConfig.Instance.AlwaysShowPhonetic || tier <= 1;
        if (showPhonetic && !string.IsNullOrWhiteSpace(word.Phonetic))
            prompt += $"  {word.Phonetic}";
        return prompt;
    }

    // ── 干扰项选择（核心难度差异）──

    private List<WordEntry> SelectDistractorWords(
        List<WordEntry> allWords, WordEntry target, int count, bool isEnToCn, int tier,
        HashSet<string>? excludedOptionTexts = null, string? lengthAnchor = null)
    {
        // 候选过滤：排除目标本身 + 排除"显示文本"会跟正确答案撞车的词。
        // 撞车判定：
        //   英→中：候选的 Chinese 或任一 Definition 落在 excluded 集合里
        //   中→英：候选的 English 落在 excluded 集合里
        bool ShouldExclude(WordEntry w)
        {
            if (w == target) return true;

            // B：候选是目标词的屈折变形（run/running、try/tried）→ 排除，玩家眼里是同一个词。
            //    对所有模式生效（比较的是词条英文本身）。派生词（act/active）不命中，保留为难干扰。
            if (IsInflection(w.English, target.English)) return true;

            // A：英→中/听力，候选中文释义与正确释义字面高度重叠 → 疑似同义，排除。
            if (isEnToCn && lengthAnchor is { Length: > 0 })
            {
                if (IsSemanticOverlap(w.Chinese, lengthAnchor)) return true;
                foreach (var def in w.Definitions)
                    if (IsSemanticOverlap(def, lengthAnchor)) return true;
            }

            if (excludedOptionTexts is null) return false;
            if (isEnToCn)
            {
                if (excludedOptionTexts.Contains(w.Chinese)) return true;
                foreach (var def in w.Definitions)
                    if (excludedOptionTexts.Contains(def)) return true;
            }
            else
            {
                if (excludedOptionTexts.Contains(w.English)) return true;
            }
            return false;
        }

        var candidates = allWords.Where(w => !ShouldExclude(w)).ToList();

        // tier=1 或 关闭混淆度开关 → 随机选择
        if (tier <= 1 || !VocabConfig.Instance.EnableConfusionDistractor)
        {
            var deduped = candidates
                .GroupBy(w => isEnToCn ? w.Chinese : w.English)
                .Select(g => g.First());

            // 英→中 / 听力：干扰项释义长度贴近正确答案，消除"选最短/最长 = 正确"的线索。
            // （高频词正确释义往往最短，纯随机会让正确答案在选项里一眼可辨。）
            if (isEnToCn && lengthAnchor is { Length: > 0 })
            {
                var anchorLen = lengthAnchor.Length;
                return deduped
                    .OrderBy(w => Math.Abs(w.Chinese.Length - anchorLen)) // 长度近的优先
                    .ThenBy(_ => _random.Next())                          // 同长度差档内随机
                    .Take(count)
                    .ToList();
            }

            return deduped
                .OrderBy(_ => _random.Next())
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

    // ── 同义 / 变形干扰过滤（问题③ A+B）──

    private static readonly string[] InflectDirectSuffixes = { "s", "es", "ed", "d", "ing", "er", "est", "ly" };
    private static readonly string[] InflectDoubleSuffixes = { "ing", "ed", "er", "est" };
    private static readonly string[] InflectYSuffixes = { "ed", "es", "er", "est", "ly" };

    /// <summary>中文释义里的结构性虚词，计算字面重叠时剔除以免重叠率虚高。</summary>
    private static readonly HashSet<char> StopChars = new("的地得了着过之其所把被而或就也都等们与及和");

    /// <summary>
    /// B：判断 x、y 是否为屈折变形关系（一个是另一个加 -s/-ed/-ing/-er… 含双写尾辅音、y→i）。
    /// 屈折变形语义等同原词，当干扰最冤，应排除；派生词（act→active、create→creation）不命中，予以保留。
    /// </summary>
    private static bool IsInflection(string x, string y)
    {
        var a = x.ToLowerInvariant();
        var b = y.ToLowerInvariant();
        var (s, l) = a.Length <= b.Length ? (a, b) : (b, a);
        if (s.Length < 2 || l.Length <= s.Length) return false;

        // 直接加后缀：play→plays/played/playing/player、quick→quickly
        foreach (var suf in InflectDirectSuffixes)
            if (l == s + suf) return true;

        // 双写尾辅音：run→running、stop→stopped、big→bigger
        foreach (var suf in InflectDoubleSuffixes)
            if (l.Length == s.Length + 1 + suf.Length
                && l.EndsWith(suf)
                && l[..(s.Length + 1)] == s + s[^1])
                return true;

        // y→i：try→tried/tries、happy→happier/happiest
        if (s.EndsWith("y"))
        {
            var stem = s[..^1] + "i";
            foreach (var suf in InflectYSuffixes)
                if (l == stem + suf) return true;
        }

        return false;
    }

    /// <summary>
    /// A：判断两条中文释义是否字面高度重叠（疑似同义）。剔虚词后实义汉字交集 ≥2 且占较短集 ≥60%。
    /// 保守判定：只挡"巨大的/庞大的"这类字面同义；换词同义（漂亮/美丽）无字面交集抓不到（无语义库的天花板）。
    /// </summary>
    private static bool IsSemanticOverlap(string a, string b)
    {
        var sa = ContentChars(a);
        var sb = ContentChars(b);
        if (sa.Count == 0 || sb.Count == 0) return false;
        var inter = sa.Count(sb.Contains);
        if (inter < 2) return false; // 单字重叠不算同义（避免"大的/大象"误杀）
        return (double)inter / Math.Min(sa.Count, sb.Count) >= 0.6;
    }

    /// <summary>提取实义汉字集合（仅 CJK 且非停用词）。</summary>
    private static HashSet<char> ContentChars(string s)
    {
        var set = new HashSet<char>();
        foreach (var c in s)
            if (c >= '一' && c <= '鿿' && !StopChars.Contains(c))
                set.Add(c);
        return set;
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

    /// <summary>
    /// 间隔重复调度选词（① + ⑥扩展式 + ③新词节流）：
    /// 到期(DueTick≤tick)的词按 Box 低 + 过期久 加权优先 —— 没掌握的反复重现凑 6-10 次，
    /// 掌握的按扩展间隔少出；新词受节流，先巩固已学；mini-cooldown 只防连续两张同词。
    /// </summary>
    private WordEntry SelectWeightedWord(List<WordEntry> words)
    {
        long tick = VocabConfig.Instance.TotalAnswered;          // 全局调度时钟（题数，session 内）
        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); // 真实时间（毕业词跨天用）
        var recentSet = new HashSet<WordEntry>(_recentWords);

        // ③ 新词节流：学习中的词（见过但 Box<2 未掌握）达上限 → 暂不引入新词
        int learning = words.Count(w => w.Seen && w.Box < 2);
        bool allowNew = learning < VocabConfig.Instance.NewWordLimit;

        var weights = words.Select(w =>
        {
            if (recentSet.Contains(w)) return 0.0;               // 刚出过，防连续重复

            if (!w.Seen)
                return allowNew ? 2.0 : 0.0;                     // 新词：节流满时不引入

            // 毕业词（Box≥3）：宽容跨天——按真实天数判断「搁久了该复习」，到期不玩不堆债
            if (w.Box >= 3)
            {
                long daysSince = (nowSec - w.LastSeenDate) / 86400;
                int dueDays = VocabConfig.Instance.IntervalDaysFor(w.Box);
                if (daysSince >= dueDays)
                    return 4.0 + Math.Min((daysSince - dueDays) * 0.5, 4.0); // 搁够→优先重现，搁越久略高（不爆炸）
                return 0.02;                                     // 没到期：基本不出，让位给没掌握的词
            }

            // 学习中（Box<3）：session 内题数间隔，没掌握的反复重现凑 6-10 次
            long overdue = tick - w.DueTick;
            if (overdue >= 0)
            {
                double boxFactor = 6 - w.Box;                    // Box0→6 … Box2→4
                double overdueFactor = 1.0 + Math.Min(overdue / 5.0, 3.0);
                return boxFactor * overdueFactor;
            }
            return 0.05;                                         // 未到期：极低权重兜底
        }).ToList();

        var totalWeight = weights.Sum();
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

        // mini-cooldown 窗口=3，让「间隔重现」成为主力（而非原来 bankSize/3 上限20 的长屏蔽）
        _recentWords.Enqueue(selected);
        while (_recentWords.Count > VocabConfig.Instance.MiniCooldown)
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
