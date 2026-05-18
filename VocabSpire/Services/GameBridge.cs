using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Game;
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
        if (tree is not null)
            tree.Paused = paused;
    }
}
