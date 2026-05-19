using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Potions;
using VocabSpire.Patches;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 免错券使用 popup —— 直接复用整个原版 NPotionPopup 场景节点。
/// - Patch NPotionPopup 的 _Ready/_Input/_ExitTree/Remove/DisconnectSignals 跳过原逻辑（避免 _holder NRE）
/// - 我们手动初始化按钮文字 / 信号 / 位置
/// - 描述卡：单独实例化 hover_tip.tscn 两次，放在 popup 右侧
/// - Container 大小自动由原版 anchor 布局决定（不再手设）
/// </summary>
public partial class FreePassPopup : Control
{
    public static FreePassPopup? Instance { get; private set; }

    private NPotionPopup? _native;
    private NPotionPopupButton? _useBtn;
    private NPotionPopupButton? _discardBtn;
    private Control? _hoverTip1;
    private Tween? _tween;

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;
        ZIndex = 60;
        MouseFilter = MouseFilterEnum.Stop;
        TopLevel = true;

        LoadNativePopup();
        LoadHoverTips();
    }

    private void LoadNativePopup()
    {
        try
        {
            var packed = ResourceLoader.Load<PackedScene>(
                "res://scenes/potions/potion_popup.tscn", null, ResourceLoader.CacheMode.Reuse);
            if (packed is null)
            {
                Log.Error("[VocabSpire] potion_popup.tscn load failed");
                return;
            }

            _native = packed.Instantiate<NPotionPopup>();
            PotionPopupHostPatch.Mark(_native);  // 让 patches 跳过原逻辑
            AddChild(_native);

            _useBtn = _native.FindChild("UseButton", recursive: true, owned: false) as NPotionPopupButton;
            _discardBtn = _native.FindChild("DiscardButton", recursive: true, owned: false) as NPotionPopupButton;

            if (_useBtn is not null) PotionPopupButtonPatch.Mark(_useBtn);
            if (_discardBtn is not null) PotionPopupButtonPatch.Mark(_discardBtn);

            Callable.From(InitNativeButtons).CallDeferred();
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire] LoadNativePopup failed: {ex}");
        }
    }

    private void InitNativeButtons()
    {
        try
        {
            Log.Info($"[VocabSpire DIAG] InitNativeButtons: native.Size={_native?.Size}");
            if (_useBtn is not null)
            {
                SetButtonText(_useBtn, "使用");
                HideButtonBackground(_useBtn);
                _useBtn.Connect(NClickableControl.SignalName.Released,
                    Callable.From<NButton>(_ => OnUse()));
                _useBtn.Enable();
            }
            if (_discardBtn is not null)
            {
                SetButtonText(_discardBtn, "丢弃");
                HideButtonBackground(_discardBtn);
                _discardBtn.Connect(NClickableControl.SignalName.Released,
                    Callable.From<NButton>(_ => OnDiscard()));
                _discardBtn.Enable();
            }
        }
        catch (System.Exception ex)
        {
            Log.Error($"[VocabSpire DIAG] InitNativeButtons EXCEPTION: {ex}");
        }
    }

    private void LoadHoverTips()
    {
        _hoverTip1 = LoadHoverTip("免错券", "下一张牌跳过答题直接打出（消耗 1 张）");
        if (_hoverTip1 is not null)
        {
            AddChild(_hoverTip1);
            _hoverTip1.TopLevel = true;
        }
    }

    private Control? LoadHoverTip(string title, string desc)
    {
        try
        {
            var packed = ResourceLoader.Load<PackedScene>(
                "res://scenes/ui/hover_tip.tscn", null, ResourceLoader.CacheMode.Reuse);
            if (packed is null) return null;
            var tip = packed.Instantiate<Control>(PackedScene.GenEditState.Disabled);
            if (tip is null) return null;

            Callable.From(() => SetHoverTipText(tip, title, desc)).CallDeferred();
            return tip;
        }
        catch { return null; }
    }

    private static void SetHoverTipText(Node tip, string title, string desc)
    {
        try
        {
            var titleNode = tip.GetNodeOrNull("%Title");
            if (titleNode is not null)
            {
                var m = titleNode.GetType().GetMethod("SetTextAutoSize", BindingFlags.Public | BindingFlags.Instance);
                m?.Invoke(titleNode, new object[] { title });
            }
            var descNode = tip.GetNodeOrNull("%Description");
            if (descNode is not null)
            {
                var prop = descNode.GetType().GetProperty("Text");
                prop?.SetValue(descNode, desc);
            }
            var iconNode = tip.GetNodeOrNull<TextureRect>("%Icon");
            if (iconNode is not null) iconNode.Visible = false;
        }
        catch { }
    }

    /// <summary>仿原版 NPotionPopup._Ready 定位公式。</summary>
    public void ShowAt(Control anchor)
    {
        if (_native is null) return;

        Refresh();

        // 原版公式：popup.GlobalPosition = anchor.GP + Down*Size.Y*1.5 + Right*Size*0.5 + Left*popupSize*0.5
        var pos = anchor.GlobalPosition
            + Vector2.Down * anchor.Size.Y * 1.5f
            + Vector2.Right * anchor.Size * 0.5f
            + Vector2.Left * _native.Size * 0.5f;

        Log.Info($"[VocabSpire DIAG] ShowAt: anchor.GP={anchor.GlobalPosition}, anchor.Size={anchor.Size}, native.Size={_native.Size}, target={pos}");

        _native.GlobalPosition = pos;

        // 描述卡放 popup 右侧，与 popup 垂直居中对齐
        var rightX = pos.X + _native.Size.X + 8;
        if (_hoverTip1 is not null)
        {
            _hoverTip1.GlobalPosition = new Vector2(rightX, pos.Y + _native.Size.Y * 0.25f);
            _hoverTip1.Visible = true;
        }

        Visible = true;
        _native.Modulate = Colors.Transparent;
        _tween?.Kill();
        _tween = CreateTween();
        _tween.TweenProperty(_native, "modulate", Colors.White, 0.12);
    }

    public new void Hide()
    {
        if (!Visible) return;
        Visible = false;
        if (_hoverTip1 is not null) _hoverTip1.Visible = false;
    }

    private void Refresh()
    {
        var stock = RunBattleState.Instance.GetStock();
        var armed = BattleStateTracker.Instance.FreePassArmed;
        if (_useBtn is not null)
        {
            if (stock > 0 && !armed) _useBtn.Enable();
            else _useBtn.Disable();
        }
        if (_discardBtn is not null)
        {
            if (stock > 0) _discardBtn.Enable();
            else _discardBtn.Disable();
        }
    }

    private static void SetButtonText(Node btn, string text)
    {
        try
        {
            var label = btn.FindChild("Label", recursive: false, owned: false);
            if (label is null) return;
            var m = label.GetType().GetMethod("SetTextAutoSize", BindingFlags.Public | BindingFlags.Instance);
            m?.Invoke(label, new object[] { text });
        }
        catch { }
    }

    private static void HideButtonBackground(Node btn)
    {
        try
        {
            var f = btn.GetType().GetField("_background", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f?.GetValue(btn) is TextureRect bg) bg.Visible = false;
        }
        catch { }
    }

    private void OnUse()
    {
        if (BattleStateTracker.Instance.TryArmFreePass())
            GameTheme.PlayClick();
        FreePassButton.Instance?.Refresh();
        Hide();
    }

    private void OnDiscard()
    {
        if (RunBattleState.Instance.GetStock() > 0)
        {
            RunBattleState.Instance.AddStock(-1);
            GameTheme.PlayClick();
        }
        FreePassButton.Instance?.Refresh();
        Hide();
    }

    public override void _Input(InputEvent ev)
    {
        if (!Visible) return;
        if (ev is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Hide();
            GetViewport().SetInputAsHandled();
        }
        else if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
        {
            if (_native is null) return;
            // 点击 popup 区域外则关闭
            if (!_native.GetGlobalRect().HasPoint(mb.GlobalPosition))
            {
                Hide();
            }
        }
    }

    public static void Create()
    {
        var root = GameBridge.GetUIRoot();
        if (root is null) return;
        root.AddChild(new FreePassPopup { Name = "VocabSpireFreePassPopup" });
    }
}
