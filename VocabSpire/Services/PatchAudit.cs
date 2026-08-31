using System.Text;
using MegaCrit.Sts2.Core.Logging;

namespace VocabSpire.Services;

/// <summary>补丁重要性。核心补丁挂不上 = mod 主体功能不可用，值得大声报；可选的安静降级即可。</summary>
public enum PatchImportance
{
    /// <summary>核心：挂不上就没法用（打牌拦截、答错跳过效果）。</summary>
    Critical,
    /// <summary>可选：挂不上只损失单个玩法（回手、重放、篝火复习入口……）。</summary>
    Optional
}

/// <summary>
/// 补丁挂载审计 —— 把「哪个功能、挂在哪个游戏方法上、命中几个、成没成」收成一张表，
/// 启动时一次性打出来。
///
/// 为什么需要：此前所有补丁走同一个 foreach + try/catch，核心补丁失败和边缘补丁失败
/// 长得一模一样（都只有一行 Warn），玩家看不出 mod 已经半残 —— 0.109 归堆改名导致
/// 答错回手静默失效整整几个版本没被发现，就是这么来的。
///
/// 设计参考 STS2-RitsuLib 的 ModPatcher（Critical/Optional 分级 + 结构化挂载报告），
/// 但不引入它那套 IPatchMethod/PatchTarget 抽象 —— 本 mod 只有 9 个补丁文件，
/// 用一个 attribute + 一张表足够，多一层抽象只是负担。
/// </summary>
public static class PatchAudit
{
    public sealed record Entry(string Feature, string Target, int Hits, PatchImportance Importance, string? Error);

    private static readonly List<Entry> Entries = new();
    private static readonly object Lock = new();

    /// <summary>补丁类在 TargetMethods 里自报：这个功能挂在哪个游戏方法上、命中几个。</summary>
    public static void Record(string feature, string target, int hits,
        PatchImportance importance = PatchImportance.Optional)
    {
        lock (Lock) Entries.Add(new(feature, target, hits, importance, null));
    }

    /// <summary>Plugin 逐类挂载时捕获到异常的补丁类。</summary>
    public static void RecordFailure(string feature, string target, string error,
        PatchImportance importance = PatchImportance.Optional)
    {
        lock (Lock) Entries.Add(new(feature, target, 0, importance, error));
    }

    /// <summary>同一功能可能有多代兼容补丁（如答错回手的三代 API），只要有一代命中就算可用。</summary>
    public static bool IsFeatureAvailable(string feature)
    {
        lock (Lock) return Entries.Any(e => e.Feature == feature && e.Hits > 0);
    }

    /// <summary>打出挂载报告。多代兼容补丁按功能归组，避免「三代里两代为 0」被误读成故障。</summary>
    public static void LogReport()
    {
        lock (Lock)
        {
            if (Entries.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine("[VocabSpire] ── 补丁挂载报告 ──");

            var unavailable = new List<string>();
            foreach (var group in Entries.GroupBy(e => e.Feature))
            {
                var hit = group.Where(e => e.Hits > 0).ToList();
                var importance = group.Any(e => e.Importance == PatchImportance.Critical)
                    ? PatchImportance.Critical : PatchImportance.Optional;
                var tag = importance == PatchImportance.Critical ? "核心" : "可选";

                if (hit.Count > 0)
                {
                    var detail = string.Join(" + ", hit.Select(e => $"{e.Target} ×{e.Hits}"));
                    sb.AppendLine($"  [OK]   {tag}  {group.Key}: {detail}");
                }
                else
                {
                    // 全代落空：把尝试过的目标和错误都列出来，便于定位是改名还是别的原因
                    var tried = string.Join(" / ", group.Select(e => e.Target));
                    var err = group.Select(e => e.Error).FirstOrDefault(x => !string.IsNullOrEmpty(x));
                    sb.AppendLine($"  [FAIL] {tag}  {group.Key}: 未挂载（尝试过 {tried}）" +
                                  (err is null ? "" : $" — {err}"));
                    unavailable.Add($"{group.Key}({tag})");
                }
            }

            if (unavailable.Count == 0)
            {
                sb.Append("  结论：全部功能可用。");
                Log.Info(sb.ToString());
            }
            else
            {
                sb.Append($"  结论：{unavailable.Count} 项功能不可用 —— {string.Join("、", unavailable)}。" +
                          "多为游戏版本更新改了对应 API；该功能已自动禁用，其余功能不受影响。");
                // 有核心功能挂掉才用 Error 级别，可选功能降级用 Warn，别让玩家把正常降级当故障
                if (Entries.Any(e => e.Importance == PatchImportance.Critical && !IsFeatureAvailable(e.Feature)))
                    Log.Error(sb.ToString());
                else
                    Log.Warn(sb.ToString());
            }
        }
    }
}
