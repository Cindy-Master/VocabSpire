using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Potions;

namespace VocabSpire.Patches;

/// <summary>
/// 让我们复用的 NPotionPopupButton（免错券 popup 里的）保持背景常驻可见，
/// 而不影响原版药水的 popup 按钮（通过 Meta "vocab_force_visible" 区分）。
/// </summary>
public static class PotionPopupButtonPatch
{
    private const string MetaKey = "vocab_force_visible";

    /// <summary>给按钮打标记，后续 Patch 才会针对它生效。</summary>
    public static void Mark(Node btn)
    {
        if (btn is not null) btn.SetMeta(MetaKey, true);
    }

    public static bool IsMarked(Node btn) => btn.HasMeta(MetaKey);

    public static TextureRect? GetBackground(NPotionPopupButton btn)
    {
        var f = typeof(NPotionPopupButton).GetField("_background",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return f?.GetValue(btn) as TextureRect;
    }
}

/// <summary>_Ready Postfix：标记的按钮背景立即设为全可见。</summary>
[HarmonyPatch(typeof(NPotionPopupButton), "_Ready")]
public static class PpButtonReadyPatch
{
    public static void Postfix(NPotionPopupButton __instance)
    {
        if (!PotionPopupButtonPatch.IsMarked(__instance))
        {
            Log.Info($"[VocabSpire DIAG] PpButton._Ready (unmarked, name={__instance.Name})");
            return;
        }
        var bg = PotionPopupButtonPatch.GetBackground(__instance);
        Log.Info($"[VocabSpire DIAG] PpButton._Ready (MARKED) - bg={(bg is null ? "null" : "ok")}, bgTex={(bg?.Texture is null ? "null" : "ok")}");
        if (bg is not null) bg.Modulate = Colors.White;
    }
}

/// <summary>OnFocus：标记的按钮 → 直接 White（覆盖原 tween 到 0.25）。</summary>
[HarmonyPatch(typeof(NPotionPopupButton), "OnFocus")]
public static class PpButtonOnFocusPatch
{
    public static bool Prefix(NPotionPopupButton __instance)
    {
        if (!PotionPopupButtonPatch.IsMarked(__instance)) return true;
        var bg = PotionPopupButtonPatch.GetBackground(__instance);
        Log.Info($"[VocabSpire DIAG] PpButton.OnFocus (MARKED) - bg={(bg is null ? "null" : "ok")}");
        if (bg is not null) bg.Modulate = Colors.White;
        return false;
    }
}

/// <summary>OnUnfocus：标记的按钮 → 保持 alpha=0.85（仍然可见但稍弱）。</summary>
[HarmonyPatch(typeof(NPotionPopupButton), "OnUnfocus")]
public static class PpButtonOnUnfocusPatch
{
    public static bool Prefix(NPotionPopupButton __instance)
    {
        if (!PotionPopupButtonPatch.IsMarked(__instance)) return true;
        var bg = PotionPopupButtonPatch.GetBackground(__instance);
        if (bg is not null) bg.Modulate = new Color(1, 1, 1, 0.85f);
        return false;
    }
}

/// <summary>OnEnable：标记的按钮 → 直接全可见。</summary>
[HarmonyPatch(typeof(NPotionPopupButton), "OnEnable")]
public static class PpButtonOnEnablePatch
{
    public static bool Prefix(NPotionPopupButton __instance)
    {
        if (!PotionPopupButtonPatch.IsMarked(__instance)) return true;
        var bg = PotionPopupButtonPatch.GetBackground(__instance);
        Log.Info($"[VocabSpire DIAG] PpButton.OnEnable (MARKED) - bg={(bg is null ? "null" : "ok")}");
        if (bg is not null) bg.Modulate = Colors.White;
        return false;
    }
}

/// <summary>OnDisable：标记的按钮 → 灰色，保留禁用视觉。</summary>
[HarmonyPatch(typeof(NPotionPopupButton), "OnDisable")]
public static class PpButtonOnDisablePatch
{
    public static bool Prefix(NPotionPopupButton __instance)
    {
        if (!PotionPopupButtonPatch.IsMarked(__instance)) return true;
        var bg = PotionPopupButtonPatch.GetBackground(__instance);
        if (bg is not null) bg.Modulate = new Color(0.4f, 0.4f, 0.4f, 0.5f);
        return false;
    }
}

// ============================================================
// 跳过 NPotionPopup 自己的 _Ready/_Input/_ExitTree/Remove，
// 让我们手动复用整个 popup 节点（含 Container + 按钮 + 原 anchor 布局）。
// ============================================================

public static class PotionPopupHostPatch
{
    private const string MetaKey = "vocab_popup_host";
    public static void Mark(Node popup) { if (popup is not null) popup.SetMeta(MetaKey, true); }
    public static bool IsMarked(Node popup) => popup.HasMeta(MetaKey);
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup), "_Ready")]
public static class PpReadyPatch
{
    public static bool Prefix(MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup __instance)
    {
        if (!PotionPopupHostPatch.IsMarked(__instance)) return true;
        Log.Info("[VocabSpire DIAG] PpReady SKIPPED for our popup");
        return false;
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup), "_Input")]
public static class PpInputPatch
{
    public static bool Prefix(MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup __instance)
    {
        return !PotionPopupHostPatch.IsMarked(__instance);
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup), "_ExitTree")]
public static class PpExitTreePatch
{
    public static bool Prefix(MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup __instance)
    {
        return !PotionPopupHostPatch.IsMarked(__instance);
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup), "Remove")]
public static class PpRemovePatch
{
    public static bool Prefix(MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup __instance)
    {
        return !PotionPopupHostPatch.IsMarked(__instance);
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup), "DisconnectSignals")]
public static class PpDisconnectPatch
{
    public static bool Prefix(MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup __instance)
    {
        return !PotionPopupHostPatch.IsMarked(__instance);
    }
}
