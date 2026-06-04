using Godot;

namespace VocabSpire.UI;

/// <summary>
/// 按键绑定控件：按钮显示当前绑定的键，点击后进入「按任意键」捕获模式，
/// 捕获下一个按下的键作为新绑定（Esc 取消）。可选 validate 做冲突检测——
/// 返回非 null 冲突名时拒绝绑定并短暂提示。
/// </summary>
public partial class KeyBindButton : Button
{
    private Key _current = Key.None;
    private bool _capturing;
    private System.Action<Key>? _onRebind;
    private System.Func<Key, string?>? _validate;

    /// <param name="current">当前绑定键</param>
    /// <param name="onRebind">绑定新键时回调</param>
    /// <param name="validate">可选冲突检测：返回非 null 冲突名则拒绝绑定</param>
    public void Setup(Key current, System.Action<Key> onRebind, System.Func<Key, string?>? validate = null)
    {
        _current = current;
        _onRebind = onRebind;
        _validate = validate;
        CustomMinimumSize = new Vector2(150, 0);
        AddThemeFontSizeOverride("font_size", 13);
        ProcessMode = ProcessModeEnum.Always;
        RefreshText();
        Pressed += StartCapture;
    }

    private void StartCapture()
    {
        if (_capturing) return;
        _capturing = true;
        Text = "  按任意键…(Esc取消)  ";
    }

    private void RefreshText() => Text = $"  {KeyName(_current)}  ";

    public override void _Input(InputEvent @event)
    {
        if (!_capturing) return;
        if (@event is not InputEventKey { Pressed: true } key) return;

        // 捕获到了——拦截这次按键，结束捕获
        GetViewport().SetInputAsHandled();
        _capturing = false;

        if (key.Keycode == Key.Escape) { RefreshText(); return; } // 取消，保持原绑定

        // 冲突检测：拒绝绑定，短暂提示后恢复原键
        var conflict = _validate?.Invoke(key.Keycode);
        if (conflict != null)
        {
            Text = $"  ✗ 与{conflict}冲突  ";
            GetTree().CreateTimer(1.5).Timeout += RefreshText;
            return;
        }

        _current = key.Keycode;
        RefreshText();
        _onRebind?.Invoke(_current);
    }

    /// <summary>把 Key 枚举转成易读名称（数字键去掉 Key 前缀，常用键给中文/简称）。</summary>
    public static string KeyName(Key key) => key switch
    {
        Key.Enter => "Enter",
        Key.KpEnter => "小键盘Enter",
        Key.Space => "Space",
        Key.Escape => "Esc",
        Key.Key0 => "0", Key.Key1 => "1", Key.Key2 => "2", Key.Key3 => "3", Key.Key4 => "4",
        Key.Key5 => "5", Key.Key6 => "6", Key.Key7 => "7", Key.Key8 => "8", Key.Key9 => "9",
        Key.None => "未设置",
        _ => key.ToString()
    };
}
