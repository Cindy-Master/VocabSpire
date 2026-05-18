using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using VocabSpire.Models;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 词汇图鉴面板 —— 继承 NSubmenu，原生注入游戏图鉴。
/// 包含: 进度总览、词库切换、单词列表(带图标/错题统计/能量损失)。
/// </summary>
public partial class VocabCollectionPanel : NSubmenu
{
    public static VocabCollectionPanel? Instance { get; private set; }

    private Button _backBtn = null!;
    private Label _titleLabel = null!;
    private Label _progressLabel = null!;
    private ProgressBar _progressBar = null!;
    private Label _masteredVal = null!;
    private Label _learningVal = null!;
    private Label _lockedVal = null!;
    private Label _energyLostVal = null!;
    private Label _remainLabel = null!;
    private OptionButton _bankSelector = null!;
    private OptionButton _filterSelector = null!;
    private OptionButton _exportSortSelector = null!;
    private VBoxContainer _wordListContainer = null!;

    private static int MasteryThreshold => VocabConfig.Instance.MasteryStreak;

    protected override Control? InitialFocusedControl => _backBtn;

    public override void _Ready()
    {
        Instance = this;
        BuildUI();
        GameTheme.ApplyFontRecursive(this);
    }

    protected override void ConnectSignals() { }

    public override void OnSubmenuOpened()
    {
        base.OnSubmenuOpened();
        RefreshBankSelector();
        Refresh();
    }

    private void BuildUI()
    {
        // 全屏深色背景
        AddChild(new ColorRect
        {
            Color = GameTheme.DarkBg,
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        });

        var margin = new MarginContainer
        {
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        };
        margin.AddThemeConstantOverride("margin_top", 36);
        margin.AddThemeConstantOverride("margin_bottom", 36);
        margin.AddThemeConstantOverride("margin_left", 50);
        margin.AddThemeConstantOverride("margin_right", 50);
        AddChild(margin);

        var root = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 16);
        margin.AddChild(root);

        BuildHeader(root);
        BuildProgressSection(root);
        BuildListSection(root);
    }

    // ── 顶部：返回 + 标题 + 词库选择器 ──
    private void BuildHeader(VBoxContainer parent)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        parent.AddChild(row);

        _backBtn = GameTheme.MakeButton("  \u2190  \u8FD4\u56DE  ", 14, GameTheme.LightGray);
        _backBtn.CustomMinimumSize = new Vector2(110, 38);
        _backBtn.Pressed += () => _stack?.Pop();
        row.AddChild(_backBtn);

        // 图鉴图标
        GameTheme.AddIcon(row, GameTheme.IconCompendium, 32);

        _titleLabel = GameTheme.MakeLabel("\u8BCD\u6C47\u56FE\u9274", 26, GameTheme.Gold, bold: true);
        row.AddChild(_titleLabel);

        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        // 词库切换
        row.AddChild(GameTheme.MakeLabel("\u8BCD\u5E93:", 18, GameTheme.LightGray));
        _bankSelector = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
        _bankSelector.ItemSelected += OnBankChanged;
        row.AddChild(_bankSelector);

        parent.AddChild(new HSeparator());
    }

    // ── 中部：统计卡片（横向 4 个）+ 进度条 ──
    private void BuildProgressSection(VBoxContainer parent)
    {
        try { BuildProgressSectionInner(parent); }
        catch (System.Exception ex)
        {
            MegaCrit.Sts2.Core.Logging.Log.Error($"[VocabSpire] BuildProgressSection failed: {ex}");
            // 回退：用纯文字替代
            _progressLabel = GameTheme.MakeLabel("", 16, GameTheme.Cream);
            parent.AddChild(_progressLabel);
            _progressBar = new ProgressBar { CustomMinimumSize = new Vector2(0, 16) };
            parent.AddChild(_progressBar);
            _masteredVal = new Label(); _learningVal = new Label();
            _lockedVal = new Label(); _energyLostVal = new Label();
            _remainLabel = GameTheme.MakeLabel("", 13, GameTheme.LightGray);
            parent.AddChild(_remainLabel);
        }
    }

    private void BuildProgressSectionInner(VBoxContainer parent)
    {
        // 四个统计卡片并排
        var statsRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        statsRow.AddThemeConstantOverride("separation", 12);
        parent.AddChild(statsRow);

        (_masteredVal, var masteredCard) = MakeStatCardWithIcon(GameTheme.IconAchievement, "0", "\u5DF2\u638C\u63E1", GameTheme.Green);
        statsRow.AddChild(masteredCard);

        (_learningVal, var learningCard) = MakeStatCardWithIcon(GameTheme.IconSwords, "0", "\u5B66\u4E60\u4E2D", GameTheme.Gold);
        statsRow.AddChild(learningCard);

        (_lockedVal, var lockedCard) = MakeStatCardWithIcon(GameTheme.IconQuestion, "0", "\u672A\u89E3\u9501", GameTheme.MidGray);
        statsRow.AddChild(lockedCard);

        (_energyLostVal, var energyCard) = MakeStatCardWithIcon(GameTheme.IconEnergy, "0", "\u635F\u5931\u80FD\u91CF", GameTheme.Red);
        statsRow.AddChild(energyCard);

        // 进度条 + 百分比
        var progressRow = new HBoxContainer();
        progressRow.AddThemeConstantOverride("separation", 12);
        parent.AddChild(progressRow);

        GameTheme.AddIcon(progressRow, GameTheme.IconStar, 20);

        _progressLabel = GameTheme.MakeLabel("0%", 15, GameTheme.Gold);
        progressRow.AddChild(_progressLabel);

        _progressBar = GameTheme.MakeProgressBar(16);
        _progressBar.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        progressRow.AddChild(_progressBar);

        _remainLabel = GameTheme.MakeLabel("", 13, GameTheme.LightGray);
        progressRow.AddChild(_remainLabel);
    }

    // ── 下部：筛选 + 导出 + 滚动单词列表 ──
    private void BuildListSection(VBoxContainer parent)
    {
        parent.AddChild(new HSeparator());

        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 10);
        parent.AddChild(filterRow);

        GameTheme.AddIcon(filterRow, GameTheme.IconCards, 18);
        filterRow.AddChild(GameTheme.MakeLabel("\u7B5B\u9009", 18, GameTheme.Cream));

        _filterSelector = new OptionButton { CustomMinimumSize = new Vector2(140, 0) };
        _filterSelector.AddItem("\u5168\u90E8", 0);
        _filterSelector.AddItem("\u5DF2\u638C\u63E1", 1);
        _filterSelector.AddItem("\u5B66\u4E60\u4E2D", 2);
        _filterSelector.AddItem("\u672A\u89E3\u9501", 3);
        _filterSelector.AddItem("\u9519\u9898\u672C", 4);
        _filterSelector.Selected = 0;
        _filterSelector.ItemSelected += _ => RefreshWordList();
        filterRow.AddChild(_filterSelector);

        filterRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        // 导出按钮组
        _exportSortSelector = new OptionButton { CustomMinimumSize = new Vector2(130, 0) };
        _exportSortSelector.AddItem("\u6309\u9519\u8BEF\u6B21\u6570", 0);
        _exportSortSelector.AddItem("\u6309\u635F\u5931\u80FD\u91CF", 1);
        _exportSortSelector.AddItem("\u6309\u6B63\u786E\u7387\u4F4E", 2);
        _exportSortSelector.Selected = 0;
        _exportSortSelector.ItemSelected += _ => RefreshWordList(); // 排序也刷新列表
        filterRow.AddChild(_exportSortSelector);

        var exportCsvBtn = GameTheme.MakeButton("  \u5BFC\u51FA CSV  ", 12);
        exportCsvBtn.CustomMinimumSize = new Vector2(90, 32);
        exportCsvBtn.Pressed += () => ExportErrorBook("csv");
        filterRow.AddChild(exportCsvBtn);

        var exportJsonBtn = GameTheme.MakeButton("  \u5BFC\u51FA JSON  ", 12);
        exportJsonBtn.CustomMinimumSize = new Vector2(90, 32);
        exportJsonBtn.Pressed += () => ExportErrorBook("json");
        filterRow.AddChild(exportJsonBtn);

        // 列表表头
        var headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", 8);
        parent.AddChild(headerRow);
        headerRow.AddChild(GameTheme.Spacer(28));
        headerRow.AddChild(GameTheme.SizedLabel("\u5355\u8BCD", 160, 18, GameTheme.MidGray));
        headerRow.AddChild(GameTheme.SizedLabel("\u91CA\u4E49", 0, 18, GameTheme.MidGray, expand: true));
        headerRow.AddChild(GameTheme.SizedLabel("\u6B63\u786E\u7387", 65, 18, GameTheme.MidGray));
        headerRow.AddChild(GameTheme.SizedLabel("\u8FDE\u5BF9", 45, 18, GameTheme.MidGray));
        headerRow.AddChild(GameTheme.SizedLabel("\u9519\u8BEF", 45, 18, GameTheme.MidGray));
        headerRow.AddChild(GameTheme.SizedLabel("\u635F\u5931", 55, 18, GameTheme.MidGray));

        // 滚动列表
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        parent.AddChild(scroll);

        _wordListContainer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _wordListContainer.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_wordListContainer);
    }

    // ── 刷新逻辑 ──

    private void RefreshBankSelector()
    {
        _bankSelector.Clear();
        var banks = VocabManager.Instance.Banks;
        var activeId = VocabConfig.Instance.ActiveBankId;
        for (var i = 0; i < banks.Count; i++)
        {
            _bankSelector.AddItem($"{banks[i].Name} ({banks[i].TotalWords})", i);
            if (banks[i].Id == activeId) _bankSelector.Selected = i;
        }
    }

    private void OnBankChanged(long idx)
    {
        var banks = VocabManager.Instance.Banks;
        if (idx >= 0 && idx < banks.Count)
        {
            VocabManager.Instance.SetActiveBank(banks[(int)idx].Id);
            Refresh();
        }
    }

    private void Refresh()
    {
        var bank = VocabManager.Instance.ActiveBank;
        if (bank is null)
        {
            _titleLabel.Text = "\u8BCD\u6C47\u56FE\u9274";
            _progressLabel.Text = "--";
            _progressBar.Value = 0;
            _masteredVal.Text = "-"; _learningVal.Text = "-";
            _lockedVal.Text = "-"; _energyLostVal.Text = "-";
            _remainLabel.Text = "";
            return;
        }

        var total = bank.Words.Count;
        int mastered = 0, learning = 0, totalEnergyLost = 0;
        foreach (var w in bank.Words)
        {
            var attempts = w.CorrectCount + w.WrongCount;
            if (attempts == 0) continue;
            if (w.Streak >= MasteryThreshold) mastered++;
            else learning++;
            totalEnergyLost += w.EnergyLost;
        }
        var locked = total - mastered - learning;
        var pct = total > 0 ? (float)mastered / total * 100f : 0f;

        _titleLabel.Text = $"\u8BCD\u6C47\u56FE\u9274 \u2014 {bank.Name}";
        _progressLabel.Text = $"{pct:F1}%";
        _progressBar.MaxValue = total;
        _progressBar.Value = mastered;
        _masteredVal.Text = mastered.ToString();
        _learningVal.Text = learning.ToString();
        _lockedVal.Text = locked.ToString();
        _energyLostVal.Text = totalEnergyLost.ToString();
        _remainLabel.Text = $"\u5269\u4F59 {total - mastered}";

        RefreshWordList();
    }

    private void RefreshWordList()
    {
        foreach (var child in _wordListContainer.GetChildren()) child.QueueFree();
        var bank = VocabManager.Instance.ActiveBank;
        if (bank is null) return;

        var filter = _filterSelector.Selected;
        var sortMode = _exportSortSelector.Selected;

        // 筛选
        var filtered = new List<(WordEntry w, bool mastered, bool learning, bool locked)>();
        foreach (var w in bank.Words)
        {
            var attempts = w.CorrectCount + w.WrongCount;
            var isMastered = w.Streak >= MasteryThreshold;
            var isLearning = attempts > 0 && !isMastered;
            var isLocked = attempts == 0;

            var show = filter switch
            {
                1 => isMastered,
                2 => isLearning,
                3 => isLocked,
                4 => w.WrongCount > 0,
                _ => true
            };
            if (show) filtered.Add((w, isMastered, isLearning, isLocked));
        }

        // 排序（已解锁的按指定方式排，未解锁的排在最后）
        var sorted = sortMode switch
        {
            0 => filtered.OrderByDescending(x => x.w.WrongCount).ThenByDescending(x => x.w.CorrectCount + x.w.WrongCount),
            1 => filtered.OrderByDescending(x => x.w.EnergyLost).ThenByDescending(x => x.w.WrongCount),
            2 => filtered.OrderBy(x => x.locked ? 999f : (x.w.CorrectCount + x.w.WrongCount > 0 ? x.w.Accuracy : 999f)),
            _ => filtered.AsEnumerable()
        };

        foreach (var (w, mastered, learning, locked) in sorted)
            _wordListContainer.AddChild(BuildWordRow(w, mastered, learning, locked));
    }

    private Control BuildWordRow(WordEntry w, bool mastered, bool learning, bool locked)
    {
        var row = new PanelContainer { CustomMinimumSize = new Vector2(0, 40) };
        var bgColor = locked ? new Color(0.04f, 0.04f, 0.06f) : mastered
            ? new Color(0.06f, 0.08f, 0.06f) : new Color(0.06f, 0.06f, 0.09f);
        row.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = bgColor,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            ContentMarginLeft = 8, ContentMarginRight = 8,
            ContentMarginTop = 4, ContentMarginBottom = 4
        });

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 8);
        row.AddChild(hbox);

        // 状态图标
        var icon = mastered ? GameTheme.IconAchievement
                 : learning ? GameTheme.IconSwords
                 : GameTheme.IconQuestion;
        GameTheme.AddIcon(hbox, icon, 22);

        if (locked)
        {
            var lockLabel = GameTheme.MakeLabel("? ? ?", 18, GameTheme.MidGray);
            lockLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            hbox.AddChild(lockLabel);
        }
        else
        {
            // 英文
            hbox.AddChild(GameTheme.SizedLabel(w.English, 160, 20, mastered ? GameTheme.Cream : GameTheme.LightGray));

            // 释义
            var cnLabel = GameTheme.MakeLabel(w.Chinese, 17, mastered ? GameTheme.LightGray : GameTheme.MidGray);
            cnLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            cnLabel.ClipText = true;
            hbox.AddChild(cnLabel);

            // 正确率
            var attempts = w.CorrectCount + w.WrongCount;
            var accText = attempts > 0 ? $"{w.Accuracy:P0}" : "--";
            var accColor = w.Accuracy >= 0.8f ? GameTheme.Green
                         : w.Accuracy >= 0.5f ? GameTheme.Gold : GameTheme.Red;
            hbox.AddChild(GameTheme.SizedLabel(accText, 65, 17, accColor));

            // 连续答对
            var streakColor = w.Streak >= MasteryThreshold ? GameTheme.Green
                            : w.Streak > 0 ? GameTheme.Gold : GameTheme.MidGray;
            hbox.AddChild(GameTheme.SizedLabel(w.Streak.ToString(), 45, 17, streakColor));

            // 错误次数
            var wrongColor = w.WrongCount > 0 ? GameTheme.Red : GameTheme.MidGray;
            hbox.AddChild(GameTheme.SizedLabel(w.WrongCount.ToString(), 45, 17, wrongColor));

            // 能量损失（纯文字，与表头严格对齐）
            var energyText = w.EnergyLost > 0 ? w.EnergyLost.ToString() : "";
            var energyColor = w.EnergyLost > 0 ? GameTheme.Red : GameTheme.MidGray;
            hbox.AddChild(GameTheme.SizedLabel(energyText, 55, 17, energyColor));
        }

        return row;
    }

    // ── 辅助方法 ──

    private static (Label valueLabel, PanelContainer card) MakeStatCardWithIcon(
        Texture2D? icon, string value, string label, Color valueColor)
    {
        var card = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 70)
        };
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = GameTheme.CardBg,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(0.2f, 0.2f, 0.25f),
            ContentMarginTop = 10, ContentMarginBottom = 10,
            ContentMarginLeft = 12, ContentMarginRight = 12
        });

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 10);
        card.AddChild(hbox);

        if (icon is not null)
        {
            hbox.AddChild(new TextureRect
            {
                Texture = icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(28, 28)
            });
        }

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 2);
        hbox.AddChild(vbox);

        var valLabel = GameTheme.MakeLabel(value, 24, valueColor, bold: true);
        vbox.AddChild(valLabel);
        vbox.AddChild(GameTheme.MakeLabel(label, 14, GameTheme.LightGray));

        return (valLabel, card);
    }

    // ── 导出错题本 ──

    private void ExportErrorBook(string format)
    {
        var bank = VocabManager.Instance.ActiveBank;
        if (bank is null) return;

        var sortMode = _exportSortSelector.Selected;
        var errors = bank.Words
            .Where(w => w.WrongCount > 0)
            .OrderByDescending(w => sortMode switch
            {
                1 => w.EnergyLost,
                2 => w.WrongCount + w.CorrectCount > 0 ? (int)((1f - w.Accuracy) * 1000) : 0,
                _ => w.WrongCount
            })
            .ToList();

        if (errors.Count == 0) return;

        var dir = VocabManager.Instance.GetWordBanksDirectory();
        var timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = $"_error_book_{timestamp}.{format}";
        var path = System.IO.Path.Combine(dir, fileName);

        try
        {
            if (format == "csv")
                ExportCsv(path, errors);
            else
                ExportJson(path, errors);

            _remainLabel.Text = $"\u5DF2\u5BFC\u51FA: {fileName}";
            _remainLabel.AddThemeColorOverride("font_color", GameTheme.Green);
        }
        catch (System.Exception ex)
        {
            _remainLabel.Text = $"\u5BFC\u51FA\u5931\u8D25: {ex.Message}";
            _remainLabel.AddThemeColorOverride("font_color", GameTheme.Red);
        }
    }

    private static void ExportCsv(string path, List<WordEntry> words)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("rank,english,chinese,wrong_count,correct_count,accuracy,energy_lost");
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var acc = (w.CorrectCount + w.WrongCount) > 0 ? w.Accuracy : 0f;
            var chinese = w.Chinese.Replace("\"", "\"\"");
            sb.AppendLine($"{i + 1},\"{w.English}\",\"{chinese}\",{w.WrongCount},{w.CorrectCount},{acc:F2},{w.EnergyLost}");
        }
        System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
    }

    private static void ExportJson(string path, List<WordEntry> words)
    {
        var items = words.Select((w, i) => new
        {
            rank = i + 1,
            english = w.English,
            chinese = w.Chinese,
            wrong_count = w.WrongCount,
            correct_count = w.CorrectCount,
            accuracy = (w.CorrectCount + w.WrongCount) > 0 ? w.Accuracy : 0f,
            energy_lost = w.EnergyLost
        });
        var json = System.Text.Json.JsonSerializer.Serialize(items,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllText(path, json, System.Text.Encoding.UTF8);
    }

    /// <summary>ESC 键返回上一级（与游戏原生行为一致）。</summary>
    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;
        if (@event is InputEventKey { Pressed: true } key && key.Keycode == Key.Escape)
        {
            _stack?.Pop();
            GetViewport().SetInputAsHandled();
        }
    }

    public static VocabCollectionPanel CreateInstance()
    {
        return new VocabCollectionPanel
        {
            Name = "VocabSpireCollection",
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect,
            Visible = false
        };
    }
}
