using System.Reflection;
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
        QuizPanel.Create();
        VocabSettingsPanel.Create();
        WordBankEditorPanel.Create();
        WrongAnswerSummaryPanel.Create();
        RestSiteReviewPanel.Create();
        RunSummaryPanel.Create();
        FreePassButton.Create();
        FreePassPopup.Create();
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
