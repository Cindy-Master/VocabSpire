using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 版本更新弹窗 —— 进游戏（主界面）弹一次本版更新内容 + 交流群；
/// 用 LastSeenChangelogVersion 记录已看过的版本，同版本只弹一次。
/// 自包含：MaybeShow() 按需创建挂到 UI 根；点「知道了」关闭并记录版本。
/// 注意：这里是普通 Control 面板，不是内嵌 Window/Popup（启动时建子窗口会在无焦点下原生崩溃）。
/// </summary>
public partial class ChangelogPopup : Control
{
    /// <summary>本版更新要点 —— 每次发版更新这里（与 manifest 更新日志同步，只写玩家关心的）。</summary>
    private static readonly string[] ChangelogLines =
    {
        "· 新增内置「托业 TOEIC 核心词汇」词库（1633 词）",
        "· 新增「出多选题」开关：不想做多选可在设置里关掉",
        "· 中→英题每个英文选项加 🔊 小喇叭，点击听发音（可关）",
        "· 词汇图鉴改分页，多词库合并上万词也不卡",
        "· 「重放本牌」奖励：答对把这张牌额外再打 N 次（含能力牌）",
    };

    /// <summary>若当前版本还没看过更新说明，弹一次（UI 创建完成后调用）。</summary>
    public static void MaybeShow()
    {
        try
        {
            var version = CurrentVersion();
            if (string.IsNullOrEmpty(version)) return;
            if (VocabConfig.Instance.LastSeenChangelogVersion == version) return;

            var root = GameBridge.GetUIRoot();
            if (root is null) return;

            root.CallDeferred(Node.MethodName.AddChild, new ChangelogPopup { _version = version });
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire] ChangelogPopup.MaybeShow failed: {ex.Message}");
        }
    }

    /// <summary>从 mod 目录的 VocabSpire.json 读当前版本号（发版只改 manifest，无需双维护）。</summary>
    private static string CurrentVersion()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(ChangelogPopup).Assembly.Location) ?? ".";
            var json = File.ReadAllText(Path.Combine(dir, "VocabSpire.json"));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("version").GetString() ?? "";
        }
        catch { return ""; }
    }

    private string _version = "";

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        TopLevel = true;
        ZIndex = 95;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // 半透明遮罩（挡住误点，但不暂停游戏）
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.05f, 0.08f, 0.98f),
            BorderColor = GameTheme.BorderGold,
            ContentMarginLeft = 32, ContentMarginRight = 32,
            ContentMarginTop = 20, ContentMarginBottom = 20,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10
        };
        style.SetBorderWidthAll(2);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(560, 0) };
        panel.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        panel.AddChild(vbox);

        vbox.AddChild(GameTheme.MakeLabel($"VocabSpire 已更新到 v{_version}",
            24, GameTheme.Gold, HorizontalAlignment.Center, bold: true));
        vbox.AddChild(new HSeparator());

        foreach (var line in ChangelogLines)
        {
            var l = GameTheme.MakeLabel(line, 16, GameTheme.Cream);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            vbox.AddChild(l);
        }

        vbox.AddChild(new HSeparator());
        vbox.AddChild(GameTheme.MakeLabel("反馈交流 QQ 群：750809524", 17, GameTheme.Gold, HorizontalAlignment.Center));
        vbox.AddChild(GameTheme.MakeLabel("开源地址：github.com/Cindy-Master/VocabSpire", 13, GameTheme.MidGray, HorizontalAlignment.Center));

        var btnCenter = new CenterContainer();
        var okBtn = GameTheme.MakeButton("  知道了  ", 18, GameTheme.Gold);
        okBtn.CustomMinimumSize = new Vector2(160, 44);
        okBtn.Pressed += () =>
        {
            // 记录已看过 → 本版本不再弹
            VocabConfig.Instance.LastSeenChangelogVersion = _version;
            VocabConfig.Instance.Save();
            QueueFree();
        };
        btnCenter.AddChild(okBtn);
        vbox.AddChild(btnCenter);

        AddChild(panel);
        GameTheme.ApplyFontRecursive(panel);

        // 居中（延迟到布局尺寸就绪后定位）
        Callable.From(() => CenterPanel(panel)).CallDeferred();
        panel.Resized += () => CenterPanel(panel);

        // 淡入
        Modulate = new Color(1, 1, 1, 0);
        var tween = CreateTween();
        tween.TweenProperty(this, "modulate:a", 1f, 0.3f);
    }

    private void CenterPanel(Control panel)
    {
        var vp = GetViewportRect().Size;
        panel.Position = new Vector2((vp.X - panel.Size.X) / 2f, (vp.Y - panel.Size.Y) / 2f);
    }
}
