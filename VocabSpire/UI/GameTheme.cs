using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;

namespace VocabSpire.UI;

/// <summary>
/// 游戏原生主题 —— 复用 STS2 字体、配色、音效、动画，保持视觉完全一致。
/// </summary>
public static class GameTheme
{
    // ── 直接引用游戏配色 ──
    public static readonly Color Cream = StsColors.cream;           // #FFF6E2 主文本
    public static readonly Color Gold = StsColors.gold;             // #EFC851 高亮/标题
    public static readonly Color Red = StsColors.red;               // #FF5555 错误
    public static readonly Color Green = StsColors.green;           // #7FFF00 正确
    public static readonly Color Aqua = StsColors.aqua;             // #2AEBBE 强调
    public static readonly Color LightGray = StsColors.lightGray;   // 次要文本
    public static readonly Color MidGray = new(0.45f, 0.45f, 0.5f); // 禁用
    public static readonly Color DarkBg = new(0.04f, 0.04f, 0.06f, 0.97f);
    public static readonly Color CardBg = new(0.07f, 0.07f, 0.1f);
    public static readonly Color BorderGold = new("B8962E");
    public static readonly Color Backdrop = new(0, 0, 0, 0.8f);    // 游戏原生遮罩

    // ── 按钮配色 ──
    public static readonly Color BtnBg = new(0.1f, 0.1f, 0.15f);
    public static readonly Color BtnHover = new(0.15f, 0.15f, 0.22f);
    public static readonly Color BtnBorder = new(0.35f, 0.32f, 0.25f);

    // ── 音效路径 ──
    private const string SfxClick = "event:/sfx/ui/clicks/ui_click";
    private const string SfxHover = "event:/sfx/ui/clicks/ui_hover";

    // ── 游戏图标 ──
    private static readonly Dictionary<string, Texture2D?> _texCache = new();

    // Stats screen atlas icons
    public static Texture2D? IconAchievement => Tex("res://images/atlases/stats_screen_atlas.sprites/stats_achievements.tres");
    public static Texture2D? IconCards => Tex("res://images/atlases/stats_screen_atlas.sprites/stats_cards.tres");
    public static Texture2D? IconQuestion => Tex("res://images/atlases/stats_screen_atlas.sprites/stats_questionmark.tres");
    public static Texture2D? IconChest => Tex("res://images/atlases/stats_screen_atlas.sprites/stats_chest.tres");
    public static Texture2D? IconSwords => Tex("res://images/atlases/stats_screen_atlas.sprites/stats_swords.tres");
    public static Texture2D? IconClock => Tex("res://images/atlases/stats_screen_atlas.sprites/stats_clock.tres");
    public static Texture2D? IconChain => Tex("res://images/atlases/stats_screen_atlas.sprites/stats_chain.tres");
    public static Texture2D? IconMonsters => Tex("res://images/atlases/stats_screen_atlas.sprites/stats_monsters.tres");

    // UI atlas icons
    public static Texture2D? IconCompendium => Tex("res://images/atlases/ui_atlas.sprites/compendium.tres");
    public static Texture2D? IconTopDeck => Tex("res://images/atlases/ui_atlas.sprites/top_bar/top_bar_deck.tres");

    // Sprite font icons (inline icons used in game text)
    public static Texture2D? IconStar => Tex("res://images/packed/sprite_fonts/star_icon.png");
    public static Texture2D? IconGold => Tex("res://images/packed/sprite_fonts/gold_icon.png");
    public static Texture2D? IconEnergy => Tex("res://images/packed/sprite_fonts/colorless_energy_icon.png")
        ?? Tex("res://images/packed/sprite_fonts/blue_energy_icon.png");
    public static Texture2D? IconCard => Tex("res://images/packed/sprite_fonts/card_icon.png");

    // Other UI
    public static Texture2D? IconLock => Tex("res://images/packed/unlock_icon.png");

    private static Texture2D? Tex(string path)
    {
        // 检查缓存是否有效（场景切换后 Godot 可能回收纹理）
        if (_texCache.TryGetValue(path, out var cached))
        {
            if (cached is not null && GodotObject.IsInstanceValid(cached))
                return cached;
            _texCache.Remove(path); // 已失效，移除重新加载
        }
        try
        {
            var tex = ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse);
            _texCache[path] = tex;
            return tex;
        }
        catch (Exception ex)
        {
            MegaCrit.Sts2.Core.Logging.Log.Warn($"[VocabSpire] Failed to load texture: {path} - {ex.Message}");
            return null;
        }
    }

    // ── 游戏字体 ──
    private static Font? _fontRegular;
    private static Font? _fontBold;

    public static Font? FontRegular
    {
        get
        {
            if (_fontRegular is not null && GodotObject.IsInstanceValid(_fontRegular)) return _fontRegular;
            _fontRegular = LoadFont("res://themes/fonts/zhs/noto_sans_mono_cjksc_regular_shared.tres");
            return _fontRegular;
        }
    }

    public static Font? FontBold
    {
        get
        {
            if (_fontBold is not null && GodotObject.IsInstanceValid(_fontBold)) return _fontBold;
            _fontBold = LoadFont("res://themes/fonts/zhs/source_han_serif_sc_bold_shared.tres");
            return _fontBold;
        }
    }

    private static Font? LoadFont(string path)
    {
        try { return ResourceLoader.Load<Font>(path, null, ResourceLoader.CacheMode.Reuse); }
        catch { return null; }
    }

    // ── 音效 ──

    public static void PlayClick() => SfxCmd.Play(SfxClick);
    public static void PlayHover() => SfxCmd.Play(SfxHover);

    // ── 动画（游戏原生按钮动画参数）──

    /// <summary>给按钮添加悬停/点击动画和音效。</summary>
    public static void AnimateButton(Button btn)
    {
        btn.MouseEntered += () =>
        {
            var tween = btn.CreateTween();
            tween.TweenProperty(btn, "scale", Vector2.One * 1.03f, 0.08f)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
            PlayHover();
        };
        btn.MouseExited += () =>
        {
            var tween = btn.CreateTween();
            tween.TweenProperty(btn, "scale", Vector2.One, 0.2f)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        };
        btn.ButtonDown += () =>
        {
            var tween = btn.CreateTween();
            tween.TweenProperty(btn, "scale", Vector2.One * 0.96f, 0.1f)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
            PlayClick();
        };
        btn.ButtonUp += () =>
        {
            var tween = btn.CreateTween();
            tween.TweenProperty(btn, "scale", Vector2.One, 0.15f)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        };
        // PivotOffset 需要在布局完成后设置
        btn.Resized += () => btn.PivotOffset = btn.Size / 2;
    }

    // ── 便捷构建方法 ──

    public static Label MakeLabel(string text, int size, Color color,
        HorizontalAlignment align = HorizontalAlignment.Left, bool bold = false)
    {
        var label = new Label { Text = text, HorizontalAlignment = align };
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeFontSizeOverride("font_size", size);
        var font = bold ? FontBold : FontRegular;
        if (font is not null) label.AddThemeFontOverride("font", font);
        return label;
    }

    public static Button MakeButton(string text, int fontSize = 16, Color? fontColor = null,
        bool animate = true)
    {
        var btn = new Button { Text = text };
        btn.AddThemeFontSizeOverride("font_size", fontSize);
        btn.AddThemeColorOverride("font_color", fontColor ?? Cream);
        btn.AddThemeColorOverride("font_hover_color", Gold);
        btn.AddThemeColorOverride("font_pressed_color", LightGray);
        if (FontRegular is not null) btn.AddThemeFontOverride("font", FontRegular);

        // 游戏风格按钮样式
        btn.AddThemeStyleboxOverride("normal", MakeButtonStyle(BtnBg));
        btn.AddThemeStyleboxOverride("hover", MakeButtonStyle(BtnHover, BtnBorder));
        btn.AddThemeStyleboxOverride("pressed", MakeButtonStyle(BtnBg, Gold));

        if (animate) AnimateButton(btn);
        return btn;
    }

    public static CheckButton MakeCheck(string text, bool pressed = false, int fontSize = 20)
    {
        var cb = new CheckButton { Text = text, ButtonPressed = pressed };
        cb.AddThemeFontSizeOverride("font_size", fontSize);
        cb.AddThemeColorOverride("font_color", Cream);
        cb.AddThemeColorOverride("font_hover_color", Gold);
        cb.AddThemeColorOverride("font_pressed_color", Gold);
        if (FontRegular is not null) cb.AddThemeFontOverride("font", FontRegular);
        return cb;
    }

    public static StyleBoxFlat MakePanelStyle(Color? bg = null, Color? border = null,
        int cornerRadius = 12, int borderWidth = 2, int padding = 24)
    {
        return new StyleBoxFlat
        {
            BgColor = bg ?? DarkBg,
            CornerRadiusTopLeft = cornerRadius, CornerRadiusTopRight = cornerRadius,
            CornerRadiusBottomLeft = cornerRadius, CornerRadiusBottomRight = cornerRadius,
            BorderWidthTop = borderWidth, BorderWidthBottom = borderWidth,
            BorderWidthLeft = borderWidth, BorderWidthRight = borderWidth,
            BorderColor = border ?? BorderGold,
            ContentMarginTop = padding, ContentMarginBottom = padding,
            ContentMarginLeft = padding, ContentMarginRight = padding
        };
    }

    public static StyleBoxFlat MakeButtonStyle(Color bg, Color? border = null)
    {
        var s = new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 12, ContentMarginRight = 12,
            ContentMarginTop = 6, ContentMarginBottom = 6
        };
        if (border.HasValue)
        {
            s.BorderWidthTop = 1; s.BorderWidthBottom = 1;
            s.BorderWidthLeft = 1; s.BorderWidthRight = 1;
            s.BorderColor = border.Value;
        }
        return s;
    }

    /// <summary>创建游戏原生风格的进度条。</summary>
    public static ProgressBar MakeProgressBar(int height = 20)
    {
        var bar = new ProgressBar
        {
            CustomMinimumSize = new Vector2(0, height),
            ShowPercentage = false
        };
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.1f, 0.15f),
            CornerRadiusTopLeft = height / 2, CornerRadiusTopRight = height / 2,
            CornerRadiusBottomLeft = height / 2, CornerRadiusBottomRight = height / 2
        });
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
        {
            BgColor = Gold,
            CornerRadiusTopLeft = height / 2, CornerRadiusTopRight = height / 2,
            CornerRadiusBottomLeft = height / 2, CornerRadiusBottomRight = height / 2
        });
        return bar;
    }

    /// <summary>递归为所有子控件应用游戏字体（包括 PopupMenu 弹出菜单）。</summary>
    public static void ApplyFontRecursive(Node node)
    {
        var font = FontRegular;
        if (font is null) return;
        if (node is Label lbl) lbl.AddThemeFontOverride("font", font);
        else if (node is OptionButton ob)
        {
            ob.AddThemeFontOverride("font", font);
            // OptionButton 的下拉菜单是 PopupMenu，也需要设置字体
            ob.GetPopup()?.AddThemeFontOverride("font", font);
            ob.GetPopup()?.AddThemeFontSizeOverride("font_size", 15);
        }
        else if (node is Button btn) btn.AddThemeFontOverride("font", font);
        else if (node is LineEdit le) le.AddThemeFontOverride("font", font);
        else if (node is SpinBox sb)
        {
            sb.GetLineEdit().AddThemeFontOverride("font", font);
        }
        foreach (var child in node.GetChildren()) ApplyFontRecursive(child);
    }

    public static void ApplyFont(this OptionButton btn, int fontSize = 15)
    {
        btn.AddThemeFontSizeOverride("font_size", fontSize);
        if (FontRegular is not null) btn.AddThemeFontOverride("font", FontRegular);
    }

    /// <summary>创建带游戏图标的行 (图标 + 文本)。</summary>
    public static HBoxContainer MakeIconRow(Texture2D? icon, string text, int fontSize,
        Color color, int iconSize = 24)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);

        if (icon is not null)
        {
            var texRect = new TextureRect
            {
                Texture = icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(iconSize, iconSize)
            };
            row.AddChild(texRect);
        }

        row.AddChild(MakeLabel(text, fontSize, color));
        return row;
    }

    /// <summary>创建带游戏图标的统计卡片。</summary>
    public static PanelContainer MakeStatCard(Texture2D? icon, string value,
        string label, Color valueColor, int iconSize = 32)
    {
        var card = new PanelContainer { CustomMinimumSize = new Vector2(150, 0) };
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = CardBg,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            ContentMarginTop = 14, ContentMarginBottom = 14,
            ContentMarginLeft = 16, ContentMarginRight = 16
        });

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        card.AddChild(vbox);

        if (icon is not null)
        {
            var texRect = new TextureRect
            {
                Texture = icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(iconSize, iconSize),
                LayoutMode = 1
            };
            vbox.AddChild(texRect);
        }

        vbox.AddChild(MakeLabel(value, 22, valueColor, HorizontalAlignment.Center, bold: true));
        vbox.AddChild(MakeLabel(label, 12, LightGray, HorizontalAlignment.Center));

        return card;
    }

    // ── 列表行构建辅助（图鉴/回顾共用）──

    /// <summary>添加图标到节点。</summary>
    public static void AddIcon(Node parent, Texture2D? icon, int size)
    {
        if (icon is null) return;
        parent.AddChild(new TextureRect
        {
            Texture = icon,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(size, size)
        });
    }

    /// <summary>固定宽度的 Label。</summary>
    public static Label SizedLabel(string text, int width, int fontSize, Color color, bool expand = false)
    {
        var l = MakeLabel(text, fontSize, color);
        if (width > 0) l.CustomMinimumSize = new Vector2(width, 0);
        if (expand) l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        l.ClipText = !expand;
        return l;
    }

    /// <summary>空白占位。</summary>
    public static Control Spacer(int width) => new Control { CustomMinimumSize = new Vector2(width, 0) };
}
