using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

namespace VocabSpire.Services;

/// <summary>
/// 游戏 API 桥接层 —— 基于 sts2.dll 反编译结果实现。
/// </summary>
public static class GameBridge
{
    /// <summary>当前是否为联机模式（联机时 mod 自动禁用以防断线）。</summary>
    public static bool IsMultiplayer()
    {
        try
        {
            return RunManager.Instance.NetService.Type.IsMultiplayer();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取当前 Act（层）编号，1-indexed。无运行中的局时返回 1。
    /// </summary>
    public static int GetCurrentAct()
    {
        try
        {
            if (!RunManager.Instance.IsInProgress) return 1;
            var state = RunManager.Instance.DebugOnlyGetState();
            return (state?.CurrentActIndex ?? 0) + 1;
        }
        catch
        {
            return 1;
        }
    }

    public static Node? GetUIRoot()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        return tree?.Root;
    }

    /// <summary>
    /// 暂停/恢复游戏。
    /// </summary>
    public static void SetGamePaused(bool paused)
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree is null) return;
        tree.Paused = paused;
        KeepControllerPolling(paused);
    }

    // 暂停前 ControllerManager 的 ProcessMode；null = 当前没有被我们改过
    private static Node.ProcessModeEnum? _controllerModeBeforePause;

    /// <summary>
    /// 暂停期间让游戏的手柄管理器照常轮询，恢复时还原。
    ///
    /// 游戏自己从不暂停场景树，所以 game.tscn 里 InputManager/ControllerManager 没设 process_mode
    /// （继承根节点 = Pausable）。我们一暂停它的 _Process 就停，而 Steam Input 模式下手柄按键全靠
    /// 它每帧 SteamInput.RunFrame() 轮询后再注入事件 —— 于是答题面板一个手柄键都收不到；
    /// 只有 Godot 原生手柄驱动那条路（原始事件不经它）不受影响。
    /// 放开后它注入的事件先到我们的面板（ProcessMode=Always）；没处理的落到 NInputManager._UnhandledInput，
    /// 而 NInputManager 仍处于暂停，不会翻译成 ui_select 之类漏给牌桌。
    /// </summary>
    private static void KeepControllerPolling(bool paused)
    {
        try
        {
            var manager = NControllerManager.Instance;
            if (manager is null || !GodotObject.IsInstanceValid(manager)) return;

            if (paused)
            {
                _controllerModeBeforePause ??= manager.ProcessMode;
                manager.ProcessMode = Node.ProcessModeEnum.Always;
            }
            else if (_controllerModeBeforePause is { } previous)
            {
                manager.ProcessMode = previous;
                _controllerModeBeforePause = null;
            }
        }
        catch (Exception ex)
        {
            // 只影响 Steam Input 手柄，绝不能拖累暂停本身
            Log.Warn($"[VocabSpire] 暂停期间保持手柄轮询失败（不影响游戏）: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
