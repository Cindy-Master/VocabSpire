using Godot;

namespace VocabSpire.Services;

/// <summary>答题界面能识别的手柄动作（与具体按键解耦）。</summary>
public enum PadAction
{
    None,
    Up,
    Down,
    Left,
    Right,
    /// <summary>A 键：选中 / 提交 / 翻面 / 继续。</summary>
    Accept,
    /// <summary>Y 键：多选题提交（单选也可用）。</summary>
    Submit,
    /// <summary>X 键：「忘了 / 没想起来」。</summary>
    Forgot
}

/// <summary>
/// 手柄输入翻译层 —— 把 Godot 的手柄事件转成答题界面的语义动作。
/// QuizPanel（战斗答题）与 RestSiteReviewPanel（篝火复习）共用，避免两份去抖逻辑。
///
/// 两件必须处理的事：
///   1. 摇杆是模拟量，InputEventJoypadMotion 每帧都发 —— 必须自己做「跨过阈值才算一次」的去抖，
///      否则轻推一下会连跳十几个选项。
///   2. 十字键在不同驱动下可能走 InputEventJoypadButton(DpadUp/Down)，也可能走轴 6/7 的 Motion，
///      两条路都要认。
/// </summary>
public static class GamepadInput
{
    /// <summary>摇杆推过这个值才算一次方向输入（Godot 轴值范围 -1..1）。</summary>
    private const float AxisPressThreshold = 0.6f;

    /// <summary>回中到这个值以内才允许再次触发（滞回，防抖动反复触发）。</summary>
    private const float AxisReleaseThreshold = 0.35f;

    // 每个轴的「当前是否处于按下状态」，用于滞回判定
    private static readonly Dictionary<long, int> AxisState = new();

    /// <summary>把一个输入事件翻译成语义动作；不是手柄事件或未越过阈值时返回 None。</summary>
    public static PadAction Translate(InputEvent e)
    {
        if (!VocabConfig.Instance.GamepadEnabled) return PadAction.None;

        if (e is InputEventJoypadButton { Pressed: true } btn)
        {
            return btn.ButtonIndex switch
            {
                JoyButton.DpadUp => PadAction.Up,
                JoyButton.DpadDown => PadAction.Down,
                JoyButton.DpadLeft => PadAction.Left,
                JoyButton.DpadRight => PadAction.Right,
                JoyButton.A => PadAction.Accept,
                JoyButton.Y => PadAction.Submit,
                JoyButton.X => PadAction.Forgot,
                _ => PadAction.None
            };
        }

        if (e is InputEventJoypadMotion motion)
        {
            var axis = motion.Axis;
            // 只认左摇杆（LeftX/LeftY）与部分驱动把十字键映射到的轴
            if (axis != JoyAxis.LeftX && axis != JoyAxis.LeftY) return PadAction.None;

            var v = motion.AxisValue;
            var key = (long)axis;
            AxisState.TryGetValue(key, out var state);   // -1 / 0 / +1

            // 已推起且尚未回中 → 不重复触发
            if (state != 0)
            {
                if (Mathf.Abs(v) < AxisReleaseThreshold) AxisState[key] = 0;
                return PadAction.None;
            }

            if (v <= -AxisPressThreshold)
            {
                AxisState[key] = -1;
                return axis == JoyAxis.LeftY ? PadAction.Up : PadAction.Left;
            }
            if (v >= AxisPressThreshold)
            {
                AxisState[key] = 1;
                return axis == JoyAxis.LeftY ? PadAction.Down : PadAction.Right;
            }
        }

        return PadAction.None;
    }

    /// <summary>当前是否插着手柄（没插就别给键鼠玩家显示手柄提示，白占一行）。</summary>
    public static bool IsPresent()
    {
        if (!VocabConfig.Instance.GamepadEnabled) return false;
        try { return Godot.Input.GetConnectedJoypads().Count > 0; }
        catch { return false; }
    }

    // 提示文案统一在这里生成，答题面板/篝火复习/各答题组件共用，改键位只改一处。
    // 用 [A] 而不是 Ⓐ：游戏字体不一定有 Enclosed Alphanumerics 字形，缺字会显示成方块。

    /// <summary>选择题提示。</summary>
    public static string HintChoice(bool multiSelect, bool showForgot)
    {
        var s = "🎮 ↑↓/摇杆 选项 · [A] " + (multiSelect ? "选中" : "选中再按提交");
        if (multiSelect) s += " · [Y] 提交";
        if (showForgot) s += " · [X] 忘了";
        return s;
    }

    /// <summary>回忆卡片提示（分翻面前/后）。</summary>
    public static string HintRecall(bool revealed) => revealed
        ? "🎮 [A] 想起来了 · [X] 没想起来"
        : "🎮 [A] 显示答案";

    /// <summary>拼写题提示 —— 手柄打不了字，只给「忘了」这一条出路。</summary>
    public static string HintSpelling(bool showForgot) => showForgot
        ? "🎮 拼写题需键盘输入 · [X] 忘了"
        : "🎮 拼写题需键盘输入";

    /// <summary>已作答（等待继续）时的提示。</summary>
    public static string HintContinue() => "🎮 [A] 继续";

    /// <summary>面板关闭时清掉摇杆状态，避免下次开面板时残留「还没回中」而吃掉第一次输入。</summary>
    public static void ResetAxisState() => AxisState.Clear();
}
