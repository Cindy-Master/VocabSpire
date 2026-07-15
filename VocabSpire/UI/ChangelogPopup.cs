using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 版本更新弹窗 —— 复用游戏官方通用弹窗（NGenericPopup / NVerticalPopup，原生样式），
/// 显示本版更新要点 + 交流群；LastSeenChangelogVersion 记录已看过版本，每版只弹一次。
/// 官方调用姿势（NGame.cs:1042-1050）：Create() → NModalContainer.Instance.Add() → WaitForConfirmation()。
/// 文本用 NVerticalPopup.SetText(string,string) 裸字符串重载覆盖（LocString 只能读本地化表）。
/// </summary>
public static class ChangelogPopup
{
    /// <summary>本版更新要点 —— 每次发版从 CHANGELOG.md 对应版本段派生（发版流程步骤 1）。</summary>
    private static readonly string[] ChangelogLines =
    {
        "· 兼容游戏 v0.108 测试版（含手机移植版）：修复「DLL 加载失败」",
        "· 答错回手在 0.107 正式版与 0.108 测试版均可用",
        "· 单个功能与游戏版本不兼容时不再影响整个 mod 加载",
        "· 近期新增：托业 TOEIC 词库 / 多选题开关 / 选项发音小喇叭",
    };

    /// <summary>若当前版本还没看过更新说明，延迟到主菜单就绪后用官方弹窗弹一次。</summary>
    public static void MaybeShow()
    {
        try
        {
            var version = CurrentVersion();
            if (string.IsNullOrEmpty(version)) return;
            if (VocabConfig.Instance.LastSeenChangelogVersion == version) return;

            var root = GameBridge.GetUIRoot();
            if (root is null) return;

            // 首帧太早（还在「正在加载模组」画面），延迟数秒等主菜单完全就绪再弹
            root.GetTree().CreateTimer(4.0).Timeout += () => TryShow(version);
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire] ChangelogPopup.MaybeShow failed: {ex.Message}");
        }
    }

    private static void TryShow(string version)
    {
        try
        {
            var root = GameBridge.GetUIRoot();
            if (root is null) return;

            var popup = NGenericPopup.Create();
            if (popup is null) { Log.Warn("[VocabSpire] ChangelogPopup: NGenericPopup.Create() 返回 null"); return; }

            // 不走 NModalContainer.Add —— 其内部 ActiveScreenContext.Instance.Update() 在主菜单为 null → NRE。
            // 直接挂 UI 根，按钮自己接线，点掉即释放。
            root.AddChild(popup);

            var vp = popup.GetNodeOrNull<NVerticalPopup>("VerticalPopup")
                     ?? popup.GetChildren().OfType<NVerticalPopup>().FirstOrDefault();
            if (vp is null)
            {
                var childNames = string.Join(", ", popup.GetChildren().Select(c => c.Name.ToString()));
                Log.Error($"[VocabSpire] ChangelogPopup: 未找到 NVerticalPopup，children=[{childNames}]，放弃");
                popup.QueueFree();
                return;
            }

            var body = string.Join("\n", ChangelogLines)
                       + "\n\n反馈交流 QQ 群：750809524"
                       + "\n开源地址：github.com/Cindy-Master/VocabSpire";
            vp.SetText($"VocabSpire 已更新到 v{version}", body);   // 裸字符串重载（内部 EnsureNodesAreSet）
            vp.YesButton.IsYes = true;
            vp.YesButton.SetText("知道了");
            vp.HideNoButton();
            vp.YesButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
            {
                // 玩家点了「知道了」→ 记录已看过，本版本不再弹
                VocabConfig.Instance.LastSeenChangelogVersion = version;
                VocabConfig.Instance.Save();
                popup.QueueFree();
            }));
            Log.Info($"[VocabSpire] ChangelogPopup shown for v{version}.");
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire] ChangelogPopup.TryShow failed: {ex}");
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
}
