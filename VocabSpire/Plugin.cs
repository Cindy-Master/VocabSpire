using System.Reflection;
using System.Runtime.Loader;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Patches;
using VocabSpire.Services;
using VocabSpire.UI;

namespace VocabSpire;

[ModInitializer(nameof(Initialize))]
public static class Plugin
{
    private static Harmony? _harmony;
    private static InputListener? _inputListener;

    public static void Initialize()
    {
        RegisterDependencyResolver(); // 必须最先 —— 让随包的第三方依赖(ZstdSharp 等)能从 mod 目录加载
        Log.Info("[VocabSpire] Initializing...");

        VocabConfig.Instance.Load();
        VocabManager.Instance.LoadAllBanks();

        _harmony = new Harmony("com.vocabspire.mod");
        _harmony.PatchAll(Assembly.GetExecutingAssembly());

        CombatEndHandler.Subscribe();

        var root = GameBridge.GetUIRoot();
        if (root is not null)
        {
            _inputListener = new InputListener();
            root.CallDeferred(Node.MethodName.AddChild, _inputListener);
        }

        Log.Info($"[VocabSpire] Loaded! Banks: {VocabManager.Instance.Banks.Count}, " +
                 $"Active: {VocabManager.Instance.ActiveBank?.Name ?? "none"}, " +
                 $"Enabled: {VocabConfig.Instance.Enabled}");
    }

    public static void Unload()
    {
        _harmony?.UnpatchAll("com.vocabspire.mod");
        VocabConfig.Instance.Save();
        Log.Info("[VocabSpire] Unloaded.");
    }

    /// <summary>
    /// 让 mod 能加载随包部署的第三方依赖 dll（如 ZstdSharp —— 解压新版 anki21b 词库用）。
    /// Godot 的 .NET mod 加载器只加载主程序集 VocabSpire.dll，不会自动解析它依赖的第三方
    /// 程序集：即使 ZstdSharp.dll 就在 mod 目录，运行时仍报「Could not load assembly 'ZstdSharp'」，
    /// 导致 apkg 导入（哪怕是不需要 zstd 的 anki2，JIT 时也要解析该类型）整个失败。
    /// 这里 hook 加载本程序集的 AssemblyLoadContext 的 Resolving 事件，解析失败时从 mod 目录按名补加载。
    /// </summary>
    private static void RegisterDependencyResolver()
    {
        try
        {
            var self = Assembly.GetExecutingAssembly();
            var ctx = AssemblyLoadContext.GetLoadContext(self);
            var modDir = Path.GetDirectoryName(self.Location);
            if (ctx is null || string.IsNullOrEmpty(modDir)) return;

            ctx.Resolving += (loadContext, name) =>
            {
                try
                {
                    var dll = Path.Combine(modDir, name.Name + ".dll");
                    if (File.Exists(dll))
                    {
                        Log.Info($"[VocabSpire] Resolving dependency '{name.Name}' from mod dir → {dll}");
                        return loadContext.LoadFromAssemblyPath(dll);
                    }
                    Log.Warn($"[VocabSpire] Dependency '{name.Name}' not found in mod dir.");
                }
                catch (System.Exception ex)
                {
                    Log.Error($"[VocabSpire] Dependency resolve failed for '{name.Name}': {ex.Message}");
                }
                return null;
            };
            Log.Info($"[VocabSpire] Dependency resolver registered (mod dir: {modDir}).");
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to register dependency resolver: {ex.Message}");
        }
    }
}

/// <summary>
/// 输入监听节点 —— 挂到场景树上监听快捷键，并在首帧延迟创建 UI。
/// </summary>
public partial class InputListener : Node
{
    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        CallDeferred(MethodName.CreateUI);
    }

    private void CreateUI()
    {
        static void Safe(string name, System.Action create)
        {
            try { Log.Info($"[VocabSpire] CreateUI → {name}"); create(); }
            catch (System.Exception ex) { Log.Error($"[VocabSpire] CreateUI {name} FAILED: {ex}"); }
        }
        Safe("QuizPanel", QuizPanel.Create);
        Safe("VocabSettingsPanel", VocabSettingsPanel.Create);
        Safe("WordBankEditorPanel", WordBankEditorPanel.Create);
        Safe("WrongAnswerSummaryPanel", WrongAnswerSummaryPanel.Create);
        Safe("RestSiteReviewPanel", RestSiteReviewPanel.Create);
        Safe("RunSummaryPanel", RunSummaryPanel.Create);
        Safe("FreePassButton", FreePassButton.Create);
        Safe("FreePassPopup", FreePassPopup.Create);
        // VocabCollectionPanel 由 CompendiumPatch 按需创建（原生注入）
        Log.Info("[VocabSpire] UI panels created.");
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key) return;

        var hotkey = VocabConfig.Instance.SettingsHotkey;
        if (key.Keycode == hotkey)
        {
            VocabSettingsPanel.Instance?.ToggleVisible();
            GetViewport().SetInputAsHandled();
        }
    }
}
