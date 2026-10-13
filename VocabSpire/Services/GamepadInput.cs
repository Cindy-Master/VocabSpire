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
/// 手柄输入会从三条通道进来，三条都要认：
///   1. 原始按键 InputEventJoypadButton —— Godot 自己的手柄驱动（游戏的 Godot 兜底策略）。
///   2. 原始摇杆 InputEventJoypadMotion —— 模拟量每帧都发，必须自己做「跨过阈值才算一次」的去抖，
///      否则轻推一下会连跳十几个选项。
///   3. 游戏注入的 InputEventAction（动作名为物理键，如 controller_face_button_south）——
///      Steam Input 模式下手柄按键「只」走这条：SteamControllerInputStrategy 每帧轮询 SteamInput，
///      再 Input.ParseInputEvent 注入；不认它，Steam Input 玩家按什么都没反应，
///      而且没被处理的事件会被 NInputManager._UnhandledInput 翻译成 ui_select / ui_confirm（兼 ui_end_turn）
///      漏给背后的牌桌。
///
/// 同一次物理按压可能经多条通道各来一次（Steam：摇杆同时注入 JoypadMotion 与 controller_d_pad_up；
/// Godot 兜底：原始摇杆 + 注入的 controller_d_pad_up + controller_l_stick_up），
/// 所以按「来源」记录谁按住了哪个语义动作：已被别的来源按住时，后来者不再触发。
/// 不用时间窗去重 —— 慢推摇杆时两条通道越过各自阈值可能相隔几百毫秒，时间窗兜不住。
/// </summary>
public static class GamepadInput
{
    /// <summary>摇杆推过这个值才算一次方向输入（Godot 轴值范围 -1..1）。</summary>
    private const float AxisPressThreshold = 0.6f;

    /// <summary>回中到这个值以内才允许再次触发（滞回，防抖动反复触发）。</summary>
    private const float AxisReleaseThreshold = 0.35f;

    // 每个轴的「当前是否处于按下状态」，用于滞回判定
    private static readonly Dictionary<long, int> AxisState = new();

    // 每个语义动作当前被哪些来源按住：键 = 来源（通道 + 具体键/轴/动作名），值 = 「此刻是否仍按着」的实时查询。
    // 面板一隐藏就不再收事件（_Input 开头 if (!Visible) return），按 A 继续时那次松开必然丢失；
    // 判重前先用实时查询剔除已松开的残留，否则残留会把另一条通道的下一次按压永远当成重复吃掉。
    private static readonly Dictionary<PadAction, Dictionary<string, Func<bool>>> Holders = new();

    /// <summary>
    /// 游戏手柄层注入的物理键动作名 → 语义动作。名字取自 MegaCrit.Sts2.Core.ControllerInput.Controller 的常量：
    /// v0.111 十字键叫 controller_d_pad_up/down/left/right，并多出 controller_l_stick_*；
    /// v0.99（手机老版）十字键叫 controller_d_pad_north/south/west/east。两套都收，一份代码两版通用。
    /// 只收物理名、不收 ui_select 这类逻辑名：逻辑名是 NInputManager 在「没人处理物理名」之后才翻译出来的，
    /// 我们处理了物理名它就不会出现；两种都收反而会在没处理时重复触发。
    /// </summary>
    private static readonly Dictionary<string, PadAction> ControllerActions = new()
    {
        ["controller_face_button_south"] = PadAction.Accept,
        ["controller_face_button_north"] = PadAction.Submit,
        ["controller_face_button_west"] = PadAction.Forgot,
        ["controller_d_pad_up"] = PadAction.Up,
        ["controller_d_pad_down"] = PadAction.Down,
        ["controller_d_pad_left"] = PadAction.Left,
        ["controller_d_pad_right"] = PadAction.Right,
        ["controller_l_stick_up"] = PadAction.Up,
        ["controller_l_stick_down"] = PadAction.Down,
        ["controller_l_stick_left"] = PadAction.Left,
        ["controller_l_stick_right"] = PadAction.Right,
        ["controller_d_pad_north"] = PadAction.Up,
        ["controller_d_pad_south"] = PadAction.Down,
        ["controller_d_pad_west"] = PadAction.Left,
        ["controller_d_pad_east"] = PadAction.Right,
    };

    /// <summary>把一个输入事件翻译成语义动作；不是手柄事件、松开、未越过阈值或属于重复通道时返回 None。</summary>
    public static PadAction Translate(InputEvent e)
    {
        if (!VocabConfig.Instance.GamepadEnabled) return PadAction.None;

        return e switch
        {
            InputEventJoypadButton btn => FromButton(btn),
            InputEventJoypadMotion motion => FromAxis(motion),
            InputEventAction action => FromAction(action),
            _ => PadAction.None
        };
    }

    private static PadAction FromButton(InputEventJoypadButton btn)
    {
        var action = btn.ButtonIndex switch
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
        var device = btn.Device;
        var index = btn.ButtonIndex;
        return Track(action, $"btn:{device}:{(int)index}", btn.Pressed,
            () => Input.IsJoyButtonPressed(device, index));
    }

    private static PadAction FromAction(InputEventAction e)
    {
        StringName name = e.Action;
        ControllerActions.TryGetValue(name.ToString(), out var action);
        // 动作状态由 Input 单例在分发前更新，跟哪个节点处理了事件无关，可作实时查询
        return Track(action, $"act:{name}", e.Pressed,
            () => InputMap.HasAction(name) && Input.IsActionPressed(name));
    }

    private static PadAction FromAxis(InputEventJoypadMotion motion)
    {
        var axis = motion.Axis;
        // 只认左摇杆（LeftX/LeftY）与部分驱动把十字键映射到的轴
        if (axis != JoyAxis.LeftX && axis != JoyAxis.LeftY) return PadAction.None;

        var v = motion.AxisValue;
        var key = (long)axis;
        var source = $"axis:{key}";
        AxisState.TryGetValue(key, out var state);   // -1 / 0 / +1

        // 已推起且尚未回中 → 不重复触发；回中时释放按住记录
        if (state != 0)
        {
            if (Mathf.Abs(v) < AxisReleaseThreshold)
            {
                AxisState[key] = 0;
                Track(AxisDirection(axis, state), source, pressed: false, () => false);
            }
            return PadAction.None;
        }

        var dir = v <= -AxisPressThreshold ? -1 : v >= AxisPressThreshold ? 1 : 0;
        if (dir == 0) return PadAction.None;
        AxisState[key] = dir;
        // 摇杆的「仍按着」用自己的滞回状态：Steam Input 注入的是合成 Motion，不依赖 Input.GetJoyAxis 是否随之更新
        return Track(AxisDirection(axis, dir), source, pressed: true,
            () => AxisState.TryGetValue(key, out var s) && s == dir);
    }

    private static PadAction AxisDirection(JoyAxis axis, int dir) => axis == JoyAxis.LeftY
        ? (dir < 0 ? PadAction.Up : PadAction.Down)
        : (dir < 0 ? PadAction.Left : PadAction.Right);

    /// <summary>
    /// 记录 source 对 action 的按下/松开。按下时若 action 仍被「别的」来源按住，说明是同一次物理按压
    /// 从另一条通道又来了一次 → 返回 None；同一来源再按照常触发（真·连按不受影响）。
    /// </summary>
    private static PadAction Track(PadAction action, string source, bool pressed, Func<bool> stillHeld)
    {
        if (action == PadAction.None) return PadAction.None;
        if (!Holders.TryGetValue(action, out var holders))
        {
            holders = new Dictionary<string, Func<bool>>();
            Holders[action] = holders;
        }

        if (!pressed)
        {
            holders.Remove(source);
            return PadAction.None;
        }

        // 剔除松开事件丢失的残留（见 Holders 注释）
        foreach (var stale in holders.Where(h => h.Key != source && !IsHeld(h.Value)).Select(h => h.Key).ToList())
            holders.Remove(stale);

        var duplicate = holders.Keys.Any(h => h != source);
        holders[source] = stillHeld;
        return duplicate ? PadAction.None : action;
    }

    private static bool IsHeld(Func<bool> query)
    {
        try { return query(); }
        catch { return false; }   // 查询失败当作已松开：宁可偶尔多触发一次，也不能把按键永久吃掉
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

    /// <summary>
    /// 面板开关时清掉摇杆与按住状态，避免残留「还没回中 / 还按着」而吃掉下一次输入
    /// （例如按 A 继续时面板随即关闭，那次松开事件就收不到了）。
    /// </summary>
    public static void ResetAxisState()
    {
        AxisState.Clear();
        Holders.Clear();
    }
}
