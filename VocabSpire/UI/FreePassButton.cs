using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Potions;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 免错券按钮 —— 放在药水栏右侧，圆形深色底+餐券图标+库存数字。
/// 启用时把 NTopBar 右侧的 RoomIcon/FloorIcon/BossIcon 整体向右推移，避免重叠。
/// 关闭时恢复原位。
/// </summary>
public partial class FreePassButton : Control
{
    public static FreePassButton? Instance { get; private set; }

    private const string TicketIconPath = "res://images/atlases/relic_atlas.sprites/meal_ticket.tres";
    private const float SlotWidth = 64f;
    private const float Spacing = 6f;
    /// <summary>需要向右推开的总宽度（含间距）。</summary>
    private const float ShiftPixels = SlotWidth + Spacing;

    private TextureRect _icon = null!;
    private Label _stockLabel = null!;
    private Panel _highlight = null!;
    private Godot.Timer _refreshTimer = null!;

    /// <summary>记录被推移的右侧节点和它们的原始 X 坐标，便于恢复。</summary>
    private readonly System.Collections.Generic.Dictionary<Control, float> _shiftedNodes = new();
    private bool _isShifted;

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;
        CustomMinimumSize = new Vector2(SlotWidth, SlotWidth);
        Size = new Vector2(SlotWidth, SlotWidth);
        MouseFilter = MouseFilterEnum.Stop;

        BuildUI();

        _refreshTimer = new Godot.Timer { WaitTime = 0.5, Autostart = true };
        _refreshTimer.Timeout += UpdateLayoutAndVisibility;
        AddChild(_refreshTimer);

        Refresh();
        Visible = false;
    }

    private void BuildUI()
    {
        // 不再画圆形深色底——奖券直接显示餐券图标，跟其他原生图标风格一致

        _highlight = new Panel
        {
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect,
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore
        };
        var hlStyle = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            BorderColor = GameTheme.Gold,
            BorderWidthTop = 3, BorderWidthBottom = 3,
            BorderWidthLeft = 3, BorderWidthRight = 3,
            CornerRadiusTopLeft = 32, CornerRadiusTopRight = 32,
            CornerRadiusBottomLeft = 32, CornerRadiusBottomRight = 32
        };
        _highlight.AddThemeStyleboxOverride("panel", hlStyle);
        AddChild(_highlight);

        _icon = new TextureRect
        {
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        try
        {
            _icon.Texture = ResourceLoader.Load<Texture2D>(TicketIconPath, null, ResourceLoader.CacheMode.Reuse);
        }
        catch (System.Exception ex)
        {
            Log.Warn($"[VocabSpire] FreePass icon load failed: {ex.Message}");
        }
        AddChild(_icon);

        _stockLabel = new Label
        {
            Text = "0",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _stockLabel.AddThemeFontSizeOverride("font_size", 18);
        _stockLabel.AddThemeColorOverride("font_color", GameTheme.Cream);
        _stockLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 1));
        _stockLabel.AddThemeConstantOverride("outline_size", 4);
        _stockLabel.OffsetRight = -4;
        _stockLabel.OffsetBottom = -2;
        AddChild(_stockLabel);

        GuiInput += OnGuiInput;
    }

    private void OnGuiInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            FreePassPopup.Instance?.ShowAt(this);
            GameTheme.PlayClick();
            AcceptEvent();
        }
    }

    public void Refresh()
    {
        var stock = RunBattleState.Instance.GetStock();
        _stockLabel.Text = stock.ToString();
        _highlight.Visible = BattleStateTracker.Instance.FreePassArmed;
        Modulate = stock > 0 || BattleStateTracker.Instance.FreePassArmed
            ? Colors.White
            : new Color(0.6f, 0.6f, 0.6f, 0.7f);
    }

    private void UpdateLayoutAndVisibility()
    {
        var cfg = VocabConfig.Instance;
        var pc = FindPotionContainer();

        if (!cfg.FreePassEnabled || pc is null || !GodotObject.IsInstanceValid(pc))
        {
            Visible = false;
            RestoreShifts();
            ResetToRoot();
            return;
        }

        // 第一次进入时撤销之前可能存在的图标推移
        if (_isShifted) RestoreShifts();

        // 定位 + reparent：与 BossIcon 同父，跟随 NTopBar 一起渲染/隐藏
        var topBar = FindAncestor<NTopBar>(pc);
        var boss = topBar?.BossIcon;
        if (boss is null || !GodotObject.IsInstanceValid(boss))
        {
            Visible = false;
            return;
        }

        var bossParent = boss.GetParent();
        if (bossParent is not null && GetParent() != bossParent)
        {
            // reparent 到 BossIcon 的父节点 → 与图标同步可见
            GetParent()?.RemoveChild(this);
            bossParent.AddChild(this);
        }

        // 用 BossIcon 的尺寸作为奖券尺寸，保持视觉一致
        var slotSize = boss.Size.X > 0 ? boss.Size : new Vector2(SlotWidth, SlotWidth);
        Size = slotSize;
        CustomMinimumSize = slotSize;
        // Position 是相对父节点的，参考 BossIcon.Position
        Position = boss.Position + new Vector2(boss.Size.X + Spacing, 0);

        Visible = true;
        Refresh();
    }

    /// <summary>关闭时把按钮重新挂回 UI 根，避免残留在 NTopBar 影响游戏。</summary>
    private void ResetToRoot()
    {
        var root = GameBridge.GetUIRoot();
        if (root is null) return;
        if (GetParent() != root)
        {
            GetParent()?.RemoveChild(this);
            root.AddChild(this);
        }
    }

    /// <summary>把 NTopBar 内 PotionContainer 右侧的图标整体向右推移。
    /// 进入战斗等场景切换时游戏会重置图标位置，所以每次检查都对齐到正确位置。</summary>
    private void ApplyShiftsIfNeeded(NPotionContainer pc)
    {
        var topBar = FindAncestor<NTopBar>(pc);
        if (topBar is null) return;

        var pcRight = pc.GlobalPosition.X + pc.Size.X;
        TryShift(topBar.RoomIcon, pcRight);
        TryShift(topBar.FloorIcon, pcRight);
        TryShift(topBar.BossIcon, pcRight);
        // Map/Deck/Pause 在屏幕右侧 anchor 锚定，但万一在 pc 右侧也推
        TryShift(topBar.Map, pcRight);
        TryShift(topBar.Deck, pcRight);
        TryShift(topBar.Pause, pcRight);
        _isShifted = true;
    }

    private void TryShift(Control? node, float pcRight)
    {
        if (node is null || !GodotObject.IsInstanceValid(node)) return;

        if (_shiftedNodes.TryGetValue(node, out var origX))
        {
            // 已记录：检查是否被游戏重置回原位，如果是则重新推
            var expectedX = origX + ShiftPixels;
            if (System.Math.Abs(node.Position.X - expectedX) > 1f)
            {
                node.Position = new Vector2(expectedX, node.Position.Y);
            }
            return;
        }

        // 首次：只推位于 PotionContainer 右侧的节点
        if (node.GlobalPosition.X < pcRight - 5) return;

        _shiftedNodes[node] = node.Position.X;
        node.Position = new Vector2(node.Position.X + ShiftPixels, node.Position.Y);
    }

    private void RestoreShifts()
    {
        if (!_isShifted) return;
        foreach (var (node, origX) in _shiftedNodes)
        {
            if (GodotObject.IsInstanceValid(node))
            {
                node.Position = new Vector2(origX, node.Position.Y);
            }
        }
        _shiftedNodes.Clear();
        _isShifted = false;
    }

    private static NPotionContainer? _cachedContainer;
    private static NPotionContainer? FindPotionContainer()
    {
        if (_cachedContainer is not null && GodotObject.IsInstanceValid(_cachedContainer))
            return _cachedContainer;
        _cachedContainer = null;
        var root = GameBridge.GetUIRoot();
        if (root is null) return null;
        _cachedContainer = FindChildOfType<NPotionContainer>(root);
        return _cachedContainer;
    }

    private static T? FindChildOfType<T>(Node node) where T : Node
    {
        if (node is T match) return match;
        foreach (var child in node.GetChildren())
        {
            var found = FindChildOfType<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    private static T? FindAncestor<T>(Node node) where T : Node
    {
        var cur = node.GetParent();
        while (cur is not null)
        {
            if (cur is T t) return t;
            cur = cur.GetParent();
        }
        return null;
    }

    public static void Create()
    {
        var root = GameBridge.GetUIRoot();
        if (root is null) return;
        root.AddChild(new FreePassButton
        {
            Name = "VocabSpireFreePass",
            ZIndex = 50
        });
    }
}
