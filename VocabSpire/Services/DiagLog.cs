using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace VocabSpire.Services;

/// <summary>
/// 诊断落盘 —— 把 mod 初始化过程与异常写到「外部能读到」的文件。
///
/// 为什么需要它：游戏的 godot.log 在 user:// 下，Android 上那是应用私有目录
/// （/data/data/&lt;包名&gt;/files/）。手机没 root、apk 又不是 debuggable 时，
/// adb 的 run-as 和 ls 全被拒，玩家和开发者都拿不到日志 —— 于是「装上就闪退」这种问题
/// 完全无从下手（实测某 v0.99 移植版就卡在这：进程 4 秒后消失，
/// 而 logcat 里既无 am_crash 也无 tombstone，游戏自己一条日志都不进 logcat）。
///
/// 而 Android 的「应用专属外部目录」(/storage/emulated/0/Android/data/&lt;包名&gt;/files/)
/// 写入不需要任何权限，且 adb shell 与文件管理器都能读。把诊断写到那儿，问题就可查了。
/// PC 上没有这个概念，退化为写 mod 目录，同样方便。
/// </summary>
public static class DiagLog
{
    private const string FileName = "vocabspire_diag.txt";

    private static string? _path;
    private static bool _failed;

    /// <summary>写一行诊断（带时间戳）。任何失败都静默 —— 诊断设施本身绝不能成为故障源。</summary>
    public static void Write(string line)
    {
        if (_failed) return;
        try
        {
            var path = ResolvePath();
            if (path is null) { _failed = true; return; }
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{System.Environment.NewLine}");
        }
        catch
        {
            _failed = true;   // 写不了就彻底放弃，别每次都抛
        }
    }

    /// <summary>新一轮启动：截断旧内容并写文件头，避免越积越长看不出哪次是本次。</summary>
    public static void BeginSession(string modVersion)
    {
        try
        {
            var path = ResolvePath();
            if (path is null) { _failed = true; return; }
            File.WriteAllText(path,
                $"=== VocabSpire 诊断日志 ==={System.Environment.NewLine}" +
                $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{System.Environment.NewLine}" +
                $"mod 版本: {modVersion}{System.Environment.NewLine}" +
                $"平台: {OS.GetName()}{System.Environment.NewLine}" +
                $"user:// 实际路径: {SafeUserDir()}{System.Environment.NewLine}" +
                $"mod 目录: {VocabManager.ModDirectory}{System.Environment.NewLine}" +
                $"本文件: {path}{System.Environment.NewLine}" +
                $"――――――――――――――――――――{System.Environment.NewLine}");
            Log.Info($"[VocabSpire] 诊断日志写入: {path}");
        }
        catch (Exception ex)
        {
            _failed = true;
            try { Log.Warn($"[VocabSpire] 诊断日志不可用: {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
    }

    /// <summary>
    /// 选一个「外部可读」的落盘位置。
    /// Android：从 user:// 的真实路径里抠出包名，拼出应用专属外部目录 —— 不硬编码包名，
    /// 手机版换了包名也照样工作。拿不到就退回 mod 目录（PC 情形）。
    /// </summary>
    private static string? ResolvePath()
    {
        if (_path is not null) return _path;

        foreach (var dir in CandidateDirs())
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, FileName);
                // 真写一次才算可用 —— 目录存在不代表有写权限
                File.AppendAllText(probe, "");
                _path = probe;
                return _path;
            }
            catch { /* 换下一个候选 */ }
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirs()
    {
        var pkg = AndroidPackageName();
        if (pkg is not null)
        {
            // 这两个路径指向同一处，不同 ROM/挂载点下可用性不同，都试
            yield return $"/storage/emulated/0/Android/data/{pkg}/files";
            yield return $"/sdcard/Android/data/{pkg}/files";
        }
        yield return VocabManager.ModDirectory;   // PC：就放 mod 目录，玩家能直接看
    }

    /// <summary>从 user:// 的真实路径推包名：Android 上形如 /data/user/0/&lt;包名&gt;/files。</summary>
    private static string? AndroidPackageName()
    {
        var userDir = SafeUserDir();
        if (string.IsNullOrEmpty(userDir)) return null;
        var parts = userDir.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            // 包名必然含点且不是 files/data 这类目录名
            if (parts[i].Contains('.') && !parts[i].Equals("files", StringComparison.OrdinalIgnoreCase))
                return parts[i];
        }
        return null;
    }

    private static string SafeUserDir()
    {
        try { return OS.GetUserDataDir() ?? ""; } catch { return ""; }
    }
}
