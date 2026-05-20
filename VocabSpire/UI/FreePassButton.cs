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
    private static FreePassButton? _instance;
    public static FreePassButton? Instance
    {
        get
        {
            if (_instance is null) return null;
            if (!GodotObject.IsInstanceValid(_instance))
            {
                _instance = null;
                return null;
            }
            return _instance;
        }
        private set => _instance = value;
    }

    /// <summary>当被检测到实例失效时由外部调用，触发 Plugin 在下个 tick 重建。</summary>
    public static void ClearInstance() => _instance = null;

    private const string TicketIconPath = "res://images/atlases/relic_atlas.sprites/meal_ticket.tres";
    private const float SlotWidth = 64f;
    private const float Spacing = 6f;

    private TextureRect _icon = null!;
    private Label _stockLabel = null!;
    private Panel _highlight = null!;
    private Godot.Timer _refreshTimer = null!;

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;
        TopLevel = true;  // 永远基于全局坐标定位，避免父节点 transform 干扰
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
        if (!cfg.FreePassEnabled)
        {
            Visible = false;
            return;
        }

        var pc = FindPotionContainer();
        if (pc is null || !GodotObject.IsInstanceValid(pc))
        {
            Visible = false;
            return;
        }

        var topBar = FindAncestor<NTopBar>(pc);
        var boss = topBar?.BossIcon;
        if (boss is null || !GodotObject.IsInstanceValid(boss))
        {
            Visible = false;
            return;
        }

        // 关键修复：不再 reparent —— 保持挂在 UI Root 下，避免被游戏场景切换释放。
        // 使用 TopLevel + GlobalPosition 跟踪 BossIcon 位置。
        EnsureRootedAtUiRoot();

        // 用 BossIcon 的尺寸作为奖券尺寸，保持视觉一致
        var slotSize = boss.Size.X > 0 ? boss.Size : new Vector2(SlotWidth, SlotWidth);
        Size = slotSize;
        CustomMinimumSize = slotSize;
        // 全局坐标紧贴 BossIcon 右侧
        GlobalPosition = boss.GlobalPosition + new Vector2(boss.Size.X + Spacing, 0);

        // 与 BossIcon 的可见性同步（NTopBar 在主菜单等场景会隐藏）
        Visible = boss.IsVisibleInTree();
        try { Refresh(); } catch (System.Exception ex) { Log.Warn($"[VocabSpire] FreePassButton Refresh in tick failed: {ex.Message}"); }
    }

    private void EnsureRootedAtUiRoot()
    {
        var root = GameBridge.GetUIRoot();
        if (root is null) return;
        var parent = GetParent();
        if (parent == root) return;
        // 已经在别处（例如老版本 reparent 留下来的）→ 移回 UI Root
        try
        {
            parent?.RemoveChild(this);
            root.AddChild(this);
        }
        catch (System.Exception ex)
        {
            Log.Warn($"[VocabSpire] reparent-to-root failed: {ex.Message}");
        }
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
