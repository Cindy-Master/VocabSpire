using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using VocabSpire.UI;

namespace VocabSpire.Patches;

/// <summary>
/// 图鉴菜单补丁 —— 复制游戏原生 NCompendiumBottomButton 创建词汇图鉴入口。
/// </summary>
[HarmonyPatch(typeof(NCompendiumSubmenu), "_Ready")]
public static class CompendiumPatch
{
    private static VocabCollectionPanel? _vocabPanel;
    private static readonly FieldInfo? StackField = typeof(NSubmenu).GetField(
        "_stack", BindingFlags.NonPublic | BindingFlags.Instance);

    public static void Postfix(NCompendiumSubmenu __instance)
    {
        try
        {
            InjectVocabButton(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] CompendiumPatch error: {ex}");
        }
    }

    private static void InjectVocabButton(NCompendiumSubmenu submenu)
    {
        // 复制游戏原生的排行榜按钮（NCompendiumBottomButton，始终隐藏不用）
        Node? templateBtn = null;
        try { templateBtn = submenu.GetNode("%LeaderboardsButton"); } catch { }

        if (templateBtn is null)
        {
            // 回退：尝试统计按钮
            try { templateBtn = submenu.GetNode("%StatisticsButton"); } catch { }
        }

        var bottomContainer = templateBtn?.GetParent() ?? submenu;

        if (templateBtn is not null)
        {
            // 复制原生按钮 → 完全一致的外观
            var vocabBtn = (Control)templateBtn.Duplicate();
            vocabBtn.Name = "VocabCollectionButton";
            vocabBtn.Visible = true;

            // 修改标签文字
            try
            {
                var label = vocabBtn.GetNode("Label");
                if (label is not null)
                {
                    // MegaLabel 的文字设置
                    label.Call("SetTextAutoSize", "\u8BCD\u6C47\u56FE\u9274");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[VocabSpire] Could not set label: {ex.Message}");
            }

            // 不修改图标 — 保留从模板按钮继承的原生图标
            // 如果要自定义图标，可放置 PNG 到 mods/VocabSpire/ 目录
            try
            {
                var iconRect = vocabBtn.GetNodeOrNull<TextureRect>("Icon");
                if (iconRect is not null)
                {
                    // 尝试加载自定义图标（用户可替换）
                    var modDir = System.IO.Path.GetDirectoryName(
                        typeof(CompendiumPatch).Assembly.Location) ?? ".";
                    var customPath = System.IO.Path.Combine(modDir, "vocab_icon.png");
                    if (System.IO.File.Exists(customPath))
                    {
                        var img = new Image();
                        img.Load(customPath);
                        var tex = ImageTexture.CreateFromImage(img);
                        iconRect.Texture = tex;
                    }
                }
            }
            catch { }

            // 绑定点击事件（NButton 使用 Released 信号）
            try
            {
                vocabBtn.Connect("Released",
                    Callable.From<Godot.GodotObject>(_ => OpenVocabCollection(submenu)));
            }
            catch
            {
                // 如果信号连接失败，尝试用 Pressed
                if (vocabBtn is BaseButton bb)
                    bb.Pressed += () => OpenVocabCollection(submenu);
            }

            bottomContainer.AddChild(vocabBtn);
            Log.Info("[VocabSpire] Duplicated native compendium button for vocab collection.");
        }
        else
        {
            // 完全回退：用 GameTheme 创建
            var fallbackBtn = GameTheme.MakeButton("  \u8BCD\u6C47\u56FE\u9274  ", 16);
            fallbackBtn.CustomMinimumSize = new Vector2(160, 48);
            fallbackBtn.Pressed += () => OpenVocabCollection(submenu);
            bottomContainer.AddChild(fallbackBtn);
            Log.Warn("[VocabSpire] Used fallback button (no template found).");
        }
    }

    private static void OpenVocabCollection(NCompendiumSubmenu submenu)
    {
        var stack = StackField?.GetValue(submenu) as NSubmenuStack;
        if (stack is null)
        {
            Log.Error("[VocabSpire] Cannot open vocab collection: _stack is null");
            return;
        }

        if (_vocabPanel is null)
        {
            _vocabPanel = VocabCollectionPanel.CreateInstance();
            _vocabPanel.Visible = false;
            stack.AddChild(_vocabPanel);
            Log.Info("[VocabSpire] VocabCollectionPanel created.");
        }

        stack.Push(_vocabPanel);
    }
}
