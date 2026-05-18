using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using VocabSpire.UI;

namespace VocabSpire.Patches;

/// <summary>
/// 在游戏设置页面的 General 面板底部注入 VocabSpire 入口按钮。
/// 点击后打开 VocabSpire 配置面板。
/// </summary>
[HarmonyPatch(typeof(NSettingsScreen), "_Ready")]
public static class SettingsScreenPatch
{
    private static readonly Color Gold = new(0.92f, 0.78f, 0.32f);
    private static readonly Color White = new(0.96f, 0.96f, 0.96f);
    private static readonly Color BtnBg = new(0.14f, 0.14f, 0.20f);
    private static readonly Color BtnHover = new(0.22f, 0.22f, 0.30f);

    public static void Postfix(NSettingsScreen __instance)
    {
        try
        {
            InjectVocabButton(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to inject settings button: {ex}");
        }
    }

    private static void InjectVocabButton(NSettingsScreen screen)
    {
        var generalPanel = screen.GetNodeOrNull<NSettingsPanel>("%GeneralSettings");
        if (generalPanel is null)
        {
            Log.Error("[VocabSpire] Could not find %GeneralSettings panel.");
            return;
        }

        var contentProp = typeof(NSettingsPanel).GetProperty("Content",
            BindingFlags.Public | BindingFlags.Instance);
        var vbox = contentProp?.GetValue(generalPanel) as VBoxContainer;
        if (vbox is null)
        {
            Log.Error("[VocabSpire] Could not get Content VBoxContainer.");
            return;
        }

        // 分隔线
        vbox.AddChild(new HSeparator());

        // VocabSpire 区域
        var section = new VBoxContainer();
        section.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(section);

        // 标题行
        var titleRow = new HBoxContainer();
        section.AddChild(titleRow);

        var title = new Label { Text = "VocabSpire 背单词" };
        title.AddThemeColorOverride("font_color", Gold);
        title.AddThemeFontSizeOverride("font_size", 18);
        titleRow.AddChild(title);

        titleRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var statusLabel = new Label
        {
            Text = Services.VocabConfig.Instance.Enabled ? "已启用" : "已关闭"
        };
        statusLabel.AddThemeColorOverride("font_color",
            Services.VocabConfig.Instance.Enabled ? new Color(0.4f, 0.85f, 0.4f) : new Color(0.7f, 0.7f, 0.7f));
        statusLabel.AddThemeFontSizeOverride("font_size", 14);
        titleRow.AddChild(statusLabel);

        // 按钮行
        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 12);
        section.AddChild(btnRow);

        var openBtn = CreateStyledButton("打开设置");
        openBtn.CustomMinimumSize = new Vector2(260, 42);
        openBtn.Pressed += () => VocabSettingsPanel.Instance?.ToggleVisible();
        btnRow.AddChild(openBtn);


        // 描述
        var desc = new Label
        {
            Text = "打出卡牌时弹出单词测验。按 F8 可快速打开设置面板。"
        };
        desc.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.6f));
        desc.AddThemeFontSizeOverride("font_size", 12);
        section.AddChild(desc);

        Log.Info("[VocabSpire] Settings button injected into General panel.");
    }

    private static Button CreateStyledButton(string text)
    {
        var btn = new Button { Text = $"  {text}  " };
        var normalStyle = new StyleBoxFlat
        {
            BgColor = BtnBg,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = Gold,
            ContentMarginLeft = 14, ContentMarginRight = 14,
            ContentMarginTop = 8, ContentMarginBottom = 8
        };
        var hoverStyle = new StyleBoxFlat
        {
            BgColor = BtnHover,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = Gold,
            ContentMarginLeft = 14, ContentMarginRight = 14,
            ContentMarginTop = 8, ContentMarginBottom = 8
        };
        btn.AddThemeStyleboxOverride("normal", normalStyle);
        btn.AddThemeStyleboxOverride("hover", hoverStyle);
        btn.AddThemeStyleboxOverride("pressed", hoverStyle);
        btn.AddThemeColorOverride("font_color", White);
        btn.AddThemeFontSizeOverride("font_size", 15);
        return btn;
    }
}
