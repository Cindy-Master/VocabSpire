using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using VocabSpire.Models;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 战斗画面「拉起卡牌」时的浮动提示 —— 在拉起的卡牌上方（类似怪物意图/攻势提示）显示：
/// 连对/连错次数 + 奖励规则距下次触发还差几题（达标一次已完成的显示✅已触发）。
/// 松手/放下卡牌后自动消失。
///
/// 实现：挂到 UI 根、每帧检测场景树中是否存在 NSelectedHandCardHolder（拉起卡牌时游戏自动创建、
/// 松手后自动销毁），有就定位到它上方显示，没有就隐藏。不 hook 游戏节点、无跨版本风险。
/// 战斗开始创建、战斗结束销毁。
/// </summary>
public partial class BattleStreakHud : Control
{
    public static BattleStreakHud? Instance { get; private set; }

    private Label _label = null!;
    private PanelContainer _panel = null!;

    public static void CreateIfNeeded()
    {
        if (Instance is not null && GodotObject.IsInstanceValid(Instance)) return;
        if (!VocabConfig.Instance.Enabled) return;
        var root = GameBridge.GetUIRoot();
        if (root is null) { MegaCrit.Sts2.Core.Logging.Log.Warn("[VocabSpire] BattleStreakHud: UI root null"); return; }
        MegaCrit.Sts2.Core.Logging.Log.Info("[VocabSpire] BattleStreakHud: creating");
        root.CallDeferred(Node.MethodName.AddChild, new BattleStreakHud());
    }

    public static void Remove()
    {
        if (Instance is not null && GodotObject.IsInstanceValid(Instance))
            Instance.QueueFree();
        Instance = null;
    }

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;
        TopLevel = true;
        ZIndex = 85;
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = true;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);   // 铺满，否则子节点可能因父尺寸0不渲染

        _panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, TopLevel = true };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.04f, 0.07f, 0.88f),
            BorderColor = GameTheme.BorderGold,
            ContentMarginLeft = 14, ContentMarginRight = 14,
            ContentMarginTop = 6, ContentMarginBottom = 6,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6
        };
        style.SetBorderWidthAll(1);
        _panel.AddThemeStyleboxOverride("panel", style);

        _label = GameTheme.MakeLabel("", 14, GameTheme.Cream, HorizontalAlignment.Center);
        _label.MouseFilter = MouseFilterEnum.Ignore;
        _panel.AddChild(_label);
        AddChild(_panel);

        GameTheme.ApplyFontRecursive(_panel);
        _panel.Visible = false;
    }

    private int _diagCount;

    public override void _Process(double delta)
    {
        if (!CombatManager.Instance.IsInProgress) { _panel.Visible = false; return; }

        var selected = FindSelectedHolder();
        if (selected is null)
        {
            _panel.Visible = false;
            return;
        }

        var diagThisFrame = _diagCount < 3;
        if (diagThisFrame) _diagCount++;

        UpdateContent();
        if (_label.Text.Length == 0) { _panel.Visible = false; return; }

        _panel.Visible = true;

        // 定位：拉起卡牌的正上方。holder.Size 常为 (0,0)（卡牌节点自己有尺寸），
        // 所以不依赖它 —— 用卡牌位置 + 固定偏移（卡牌高约 300，提示放其上方）。
        var cardPos = selected.GlobalPosition;
        var panelSize = _panel.Size;                    // 首帧可能为 0，下一帧就正常
        var vp = GetViewportRect().Size;
        var x = Mathf.Clamp(cardPos.X - panelSize.X / 2f, 8f, Mathf.Max(8f, vp.X - panelSize.X - 8f));
        var y = Mathf.Clamp(cardPos.Y - 320f, 8f, Mathf.Max(8f, vp.Y - panelSize.Y - 8f));
        _panel.GlobalPosition = new Vector2(x, y);

        if (diagThisFrame)
            MegaCrit.Sts2.Core.Logging.Log.Info(
                $"[VocabSpire] BattleStreakHud: card={cardPos} panelSize={panelSize} → panelPos=({x},{y}) " +
                $"vp={vp} visible={_panel.Visible} selfVisible={Visible} text='{_label.Text}' zIndex={ZIndex}");
    }

    private NPlayerHand? _cachedHand;

    /// <summary>
    /// 找到「正在拉起的卡牌」节点。
    /// 依据 NPlayerHand.cs:870-873（0.107/0.108 一致）：正常打牌拉起卡牌时
    /// holder.BeginDrag() + AddChildSafely(NCardPlay)，NCardPlay 挂在 NPlayerHand 下、
    /// 其 public Holder 属性即被拉起的卡牌 holder；松手后 NCardPlay 被销毁。
    /// （NSelectedHandCardHolder 只用于「选牌弹窗」，正常打牌不走那条路 —— 实测踩过的坑）
    /// </summary>
    private Control? FindSelectedHolder()
    {
        try
        {
            if (_cachedHand is null || !GodotObject.IsInstanceValid(_cachedHand))
                _cachedHand = FindNode<NPlayerHand>(GetTree()?.Root, 0, 15);
            if (_cachedHand is null) return null;

            foreach (var child in _cachedHand.GetChildren())
            {
                if (child is NCardPlay play && play.Holder is { } h && GodotObject.IsInstanceValid(h))
                    return h;
            }
            return null;
        }
        catch { return null; }
    }

    private static T? FindNode<T>(Node? node, int depth, int maxDepth) where T : Node
    {
        if (node is null || depth >= maxDepth) return null;
        if (node is T t) return t;
        foreach (var child in node.GetChildren())
        {
            var found = FindNode<T>(child, depth + 1, maxDepth);
            if (found is not null) return found;
        }
        return null;
    }

    private void UpdateContent()
    {
        var bt = BattleStateTracker.Instance;
        var cfg = VocabConfig.Instance;
        var parts = new List<string>();

        if (bt.CorrectStreak > 0) parts.Add($"🔥连对 {bt.CorrectStreak}");
        if (bt.WrongStreak > 0) parts.Add($"💔连错 {bt.WrongStreak}");

        if (cfg.RewardEnabled)
        {
            foreach (var rule in cfg.RewardRules)
            {
                if (!rule.Enabled || rule.Kind == RewardType.None || rule.Amount <= 0 || rule.Streak <= 0) continue;
                var streak = bt.CorrectStreak;
                var kindName = rule.Kind switch
                {
                    RewardType.Hp => "回血", RewardType.Energy => "能量", RewardType.Gold => "金币",
                    RewardType.Strength => "力量", RewardType.Dexterity => "敏捷", RewardType.Block => "覆甲",
                    RewardType.Draw => "抽牌", RewardType.Replay => "重放", _ => rule.Kind.ToString()
                };
                switch (rule.Mode)
                {
                    case RewardTriggerMode.Once:
                        var gapOnce = rule.Streak - streak;
                        if (gapOnce <= 0)
                            parts.Add($"✅{kindName} 已触发");
                        else
                            parts.Add($"{kindName} 还差{gapOnce}题");
                        break;
                    case RewardTriggerMode.Recurring:
                        var gapRec = rule.Streak - streak;
                        if (gapRec <= 0)
                            parts.Add($"✨{kindName}+{rule.Amount}");
                        else
                            parts.Add($"{kindName} 还差{gapRec}题");
                        break;
                    case RewardTriggerMode.EveryN:
                        if (rule.Streak <= 0) continue;
                        var rem = streak % rule.Streak;
                        var gapN = rem == 0 && streak > 0 ? rule.Streak : rule.Streak - rem;
                        parts.Add($"{kindName} 还差{gapN}题");
                        break;
                }
            }
        }

        _label.Text = parts.Count > 0 ? string.Join("  |  ", parts) : "";
    }
}
