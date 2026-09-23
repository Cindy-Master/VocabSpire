using System.Reflection;
using System.Text;

// 用法: apicheck <老版 sts2.dll> <新版 sts2.dll>
// 逐项检查 VocabSpire 依赖的游戏 API 在两个版本里的存在性与签名，输出对比表。
// 只读元数据（MetadataLoadContext），不执行任何游戏代码。

Console.OutputEncoding = Encoding.UTF8;
if (args.Length < 2) { Console.WriteLine("用法: apicheck <老 sts2.dll> <新 sts2.dll>"); return 1; }

var oldAsm = Load(args[0], out var oldCtx);
var newAsm = Load(args[1], out var newCtx);
Console.WriteLine($"老版: {args[0]}");
Console.WriteLine($"新版: {args[1]}\n");


// 查询模式: --grep <类型名片段>  / --members <类型全名>
if (args.Length >= 3 && args[2] == "--grep")
{
    var pat = args[3];
    foreach (var (tag, asm) in new[] { ("v0.99.1", oldAsm), ("v0.111", newAsm) })
    {
        Console.WriteLine($"--- {tag} 匹配 \"{pat}\" 的类型 ---");
        foreach (var t in asm.GetTypes())
            if (t.Name.Contains(pat, StringComparison.OrdinalIgnoreCase)) Console.WriteLine("   " + t.FullName);
        Console.WriteLine();
    }
    return 0;
}
if (args.Length >= 3 && args[2] == "--members")
{
    var tn = args[3];
    var filter = args.Length >= 5 ? args[4] : "";
    foreach (var (tag, asm) in new[] { ("v0.99.1", oldAsm), ("v0.111", newAsm) })
    {
        var t = asm.GetType(tn, false);
        Console.WriteLine($"--- {tag} {tn} ---");
        if (t is null) { Console.WriteLine("   ✗ 类型不存在"); continue; }
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        // 数据类（manifest 之类）的内容在字段/属性上，不列出来就什么也看不到
        foreach (var f in t.GetFields(flags).OrderBy(x => x.Name))
        {
            if (filter.Length > 0 && !f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine($"   [字段]    {f.FieldType.Name,-22} {f.Name}");
        }
        foreach (var pr in t.GetProperties(flags).OrderBy(x => x.Name))
        {
            if (filter.Length > 0 && !pr.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine($"   [属性]    {pr.PropertyType.Name,-22} {pr.Name}");
        }
        foreach (var m in t.GetMethods(flags).OrderBy(m => m.Name))
        {
            if (filter.Length > 0 && !m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var vis = m.IsPublic ? "public" : m.IsFamily ? "protected" : m.IsAssembly ? "internal" : "private";
            Console.WriteLine($"   {vis,-9} {m.ReturnType.Name,-22} {m.Name}({string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name))})");
        }
        Console.WriteLine();
    }
    return 0;
}

// ── mod 按名字反射定位的成员（编译期查不出来的那部分）──
var checks = new (string what, Func<Assembly, string> probe)[]
{
    ("类型 CardLocation",                     a => TypeInfo2(a, "MegaCrit.Sts2.Core.Entities.Cards.CardLocation")),
    ("类型 PileType",                          a => TypeInfo2(a, "MegaCrit.Sts2.Core.Entities.Cards.PileType")),
    ("CardModel.OnPlayWrapper",                a => MethodInfo2(a, "MegaCrit.Sts2.Core.Models.CardModel", "OnPlayWrapper")),
    ("CardModel.OnPlay",                       a => MethodInfo2(a, "MegaCrit.Sts2.Core.Models.CardModel", "OnPlay")),
    ("CardModel.GetEnchantedReplayCount",      a => MethodInfo2(a, "MegaCrit.Sts2.Core.Models.CardModel", "GetEnchantedReplayCount")),
    ("归堆 v0.107 GetResultPileTypeForCardPlay",            a => FindAnywhere(a, "GetResultPileTypeForCardPlay")),
    ("归堆 v0.108 GetResultPileTypeAndPositionForCardPlay",  a => FindAnywhere(a, "GetResultPileTypeAndPositionForCardPlay")),
    ("归堆 v0.109 GetResultLocationForCardPlay",             a => FindAnywhere(a, "GetResultLocationForCardPlay")),
    ("PacketReader.BitPosition (含 setter 可见性)", a => PropInfo2(a, "MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketReader", "BitPosition")),
    ("PacketWriter 类型",                      a => TypeInfo2(a, "MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter")),
    ("NetPlayCardAction.Serialize",            a => MethodInfo2(a, "MegaCrit.Sts2.Core.GameActions.NetPlayCardAction", "Serialize")),
    ("NetPlayCardAction.Deserialize",          a => MethodInfo2(a, "MegaCrit.Sts2.Core.GameActions.NetPlayCardAction", "Deserialize")),
    ("NPotionPopupButton._background",         a => FieldInfo2(a, "MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopupButton", "_background")),
    ("NSettingsPanel.Content",                 a => PropInfo2(a, "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NSettingsPanel", "Content")),
    ("RunManager._startTime",                  a => FieldInfo2(a, "MegaCrit.Sts2.Core.Runs.RunManager", "_startTime")),
    ("RunManager.IsInProgress",                a => PropInfo2(a, "MegaCrit.Sts2.Core.Runs.RunManager", "IsInProgress")),
    ("NSubmenu.InitialFocusedControl",         a => PropInfo2(a, "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenu", "InitialFocusedControl")),
    ("NSubmenu.ConnectSignals",                a => MethodInfo2(a, "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenu", "ConnectSignals")),
    ("NSubmenu.OnSubmenuOpened",               a => MethodInfo2(a, "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenu", "OnSubmenuOpened")),
    ("NGenericPopup.Create",                   a => MethodInfo2(a, "MegaCrit.Sts2.Core.Nodes.Multiplayer.NGenericPopup", "Create")),
    ("NVerticalPopup.SetText",                  a => MethodInfo2(a, "MegaCrit.Sts2.Core.Nodes.CommonUi.NVerticalPopup", "SetText")),
    ("SetTextAutoSize (任意类型)",              a => FindAnywhere(a, "SetTextAutoSize")),
    ("CardModel 子类数(OnPlay 重写)",           a => CountOnPlayOverrides(a)),
};

Console.WriteLine($"{"检查项",-52} {"v0.99.1",-46} {"v0.111",-46}");
Console.WriteLine(new string('-', 148));
int diff = 0;
foreach (var (what, probe) in checks)
{
    string o = Safe(() => probe(oldAsm)), n = Safe(() => probe(newAsm));
    bool same = o == n;
    if (!same) diff++;
    Console.WriteLine($"{(same ? "  " : "▲ ")}{what,-50} {Cut(o, 45),-46} {Cut(n, 45),-46}");
}
Console.WriteLine(new string('-', 148));
Console.WriteLine($"\n共 {checks.Length} 项，{diff} 项存在差异（▲ 标出）");

oldCtx.Dispose(); newCtx.Dispose();
return 0;

// ── helpers ──

static Assembly Load(string path, out MetadataLoadContext ctx)
{
    // 目标目录优先。游戏安装目录自带一整套 .NET 运行时程序集，若再把宿主的 TPA 混进来，
    // 同一个 simple name（mscorlib 等）会出现两个候选文件，PathAssemblyResolver 直接抛
    // 「has already been loaded」。所以 TPA 只用来补目标目录里没有的文件名。
    var full = Path.GetFullPath(path);
    var dir = Path.GetDirectoryName(full)!;
    var paths = Directory.GetFiles(dir, "*.dll").ToList();
    var have = paths.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var p in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
    {
        if (!p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
        if (have.Add(Path.GetFileName(p))) paths.Add(p);
    }
    ctx = new MetadataLoadContext(new PathAssemblyResolver(paths));
    return ctx.LoadFromAssemblyPath(full);
}

static string Safe(Func<string> f) { try { return f(); } catch (Exception e) { return "ERR:" + e.GetType().Name; } }
static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

static Type Find(Assembly a, string full) => a.GetType(full, false);

static string TypeInfo2(Assembly a, string full)
{
    var t = Find(a, full);
    if (t is null) return "✗ 不存在";
    var kind = t.IsEnum ? "enum" : t.IsValueType ? "struct" : t.IsInterface ? "interface" : "class";
    return $"✓ {kind}";
}

const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                       | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

static string MethodInfo2(Assembly a, string typeFull, string name)
{
    var t = Find(a, typeFull);
    if (t is null) return "✗ 类型不存在";
    var ms = t.GetMethods(All).Where(m => m.Name == name).ToList();
    if (ms.Count == 0) return "✗ 方法不存在";
    var m = ms[0];
    var vis = m.IsPublic ? "public" : m.IsFamily ? "protected" : m.IsAssembly ? "internal" : "private";
    var ps = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name));
    return $"✓ {vis}{(ms.Count > 1 ? $" ×{ms.Count}" : "")} ({ps})→{m.ReturnType.Name}";
}

static string PropInfo2(Assembly a, string typeFull, string name)
{
    var t = Find(a, typeFull);
    if (t is null) return "✗ 类型不存在";
    var p = t.GetProperties(All).FirstOrDefault(x => x.Name == name);
    if (p is null) return "✗ 属性不存在";
    var g = p.GetMethod; var s = p.SetMethod;
    string V(MethodInfo m) => m is null ? "无"
        : m.IsPublic ? "public" : m.IsFamily ? "protected" : m.IsAssembly ? "internal" : "private";
    return $"✓ {p.PropertyType.Name} get={V(g)} set={V(s)}";
}

static string FieldInfo2(Assembly a, string typeFull, string name)
{
    var t = Find(a, typeFull);
    if (t is null) return "✗ 类型不存在";
    var f = t.GetFields(All).FirstOrDefault(x => x.Name == name);
    return f is null ? "✗ 字段不存在" : $"✓ {f.FieldType.Name}";
}

/// <summary>全程序集按方法名搜索（归堆 API 跨版本改过所在类型，不能写死类型名）。</summary>
static string FindAnywhere(Assembly a, string methodName)
{
    foreach (var t in a.GetTypes())
    {
        MethodInfo[] ms;
        try { ms = t.GetMethods(All); } catch { continue; }
        foreach (var m in ms)
            if (m.Name == methodName)
            {
                var ps = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name));
                return $"✓ {t.Name}({ps})→{m.ReturnType.Name}";
            }
    }
    return "✗ 全程序集找不到";
}

static string CountOnPlayOverrides(Assembly a)
{
    var cardModel = Find(a, "MegaCrit.Sts2.Core.Models.CardModel");
    if (cardModel is null) return "✗ CardModel 不存在";
    int n = 0;
    foreach (var t in a.GetTypes())
    {
        try
        {
            if (!t.IsClass || t == cardModel) continue;
            var b = t.BaseType; bool isSub = false;
            while (b is not null) { if (b.FullName == cardModel.FullName) { isSub = true; break; } b = b.BaseType; }
            if (!isSub) continue;
            if (t.GetMethods(All).Any(m => m.Name == "OnPlay")) n++;
        }
        catch { }
    }
    return $"✓ {n} 个";
}
