using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace VocabSpire.Services;

/// <summary>
/// 一次性记录：弹题拦截点上都挂了谁的 Prefix、Harmony 实际按什么顺序执行它们。
///
/// 为什么需要：Harmony 2.4.2 实测，同一方法上一个 bool Prefix 返回 false 后，排在它后面的 bool Prefix
/// 全部被跳过；同优先级时先后又由 mod 加载顺序决定。于是「别的 mod 先加载 → 我们的弹题被吃掉」这类
/// 问题只在特定 mod 组合 + 特定加载顺序下出现，光看我们自己的代码永远查不出来。
/// 把真实执行顺序写进日志，任何人报「不弹题」时，一份日志就能看出是谁排在前面、能不能拦截。
///
/// 时机：在首次打牌（拦截点首次被调用）时跑，而不是在 Initialize 里 —— 那时排在我们后面加载的 mod
/// 还没挂补丁，看到的顺序是不完整的。
/// </summary>
internal static class PatchOrderAudit
{
    internal const string OurOwner = "com.vocabspire.mod";

    private static int _done;

    internal static void RunOnce()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        try
        {
            Report(AccessTools.Method(typeof(CardModel), nameof(CardModel.TryManualPlay)), "联机弹题");
            Report(AccessTools.Method(typeof(CardModel), nameof(CardModel.OnPlayWrapper)), "单机弹题");
        }
        catch (Exception ex)
        {
            // 审计是锦上添花，绝不能影响打牌本身
            Log.Warn($"[VocabSpire][补丁顺序] 审计失败（不影响游戏）: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Report(MethodBase? method, string role)
    {
        if (method is null) return;
        var info = Harmony.GetPatchInfo(method);
        if (info is null) return;

        var prefixes = info.Prefixes.ToArray();
        if (prefixes.Length == 0) return;

        // 用 Harmony 自己的排序（优先级 + before/after 声明 + 注册顺序），而不是自己按优先级排 ——
        // 自己排会漏掉 before/after，得出的「执行顺序」可能是错的。
        var sorted = PatchProcessor.GetSortedPatchMethods(method, prefixes);
        var byMethod = prefixes.ToDictionary(p => p.PatchMethod);
        var ordered = sorted.Where(byMethod.ContainsKey).Select(m => byMethod[m]).ToList();

        var name = $"{method.DeclaringType?.Name}.{method.Name}";
        var lines = ordered.Select((p, i) =>
            $"{i + 1}) {Describe(p)}{(p.owner == OurOwner ? " ←本 mod" : "")}");
        var text = $"[VocabSpire][补丁顺序] {name}（{role}）的 Prefix 实际执行顺序，共 {ordered.Count} 个：" +
                   string.Join("；", lines);
        Log.Info(text);
        DiagLog.Write(text);

        // 排在我们前面、且能拦截原方法的他人 Prefix —— 它一旦返回 false，我们的弹题就被跳过
        var ourIndex = ordered.FindIndex(p => p.owner == OurOwner);
        if (ourIndex <= 0) return;
        foreach (var p in ordered.Take(ourIndex))
        {
            if (p.owner == OurOwner || p.PatchMethod.ReturnType != typeof(bool)) continue;
            var warn = $"[VocabSpire][补丁顺序] ⚠ {Describe(p)} 排在本 mod 的{role}拦截之前且能拦截原方法：" +
                       "它返回 false 时题目不会弹出。";
            Log.Warn(warn);
            DiagLog.Write(warn);
        }
    }

    private static string Describe(Patch p)
    {
        var kind = p.PatchMethod.ReturnType == typeof(bool) ? "可拦截" : "void";
        return $"{p.owner} {p.PatchMethod.DeclaringType?.Name}.{p.PatchMethod.Name} [优先级 {p.priority}, {kind}]";
    }
}
