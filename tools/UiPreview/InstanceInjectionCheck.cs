// 补丁形参注入的正确性：`__instance` 只能用于**实例方法**。
//
// 为什么值得一条断言（2026-10-06 抓到一处，继承自 Spectre 的潜伏缺陷）：
// Harmony 的 `__instance` 只在原方法**非静态**时才会注入——它内部是个
// **合成形参**（`MethodBodyReader+ThisParameter`，派生自 ParameterInfo，
// 已在真 0Harmony.dll 2.3.6 里核实），不是按名字匹配的。
// 在**静态方法**上声明 `__instance` ⇒ 它恒拿到 null ⇒ 补丁照样挂得上、照样跑，
// 只是永远拿到空值。**编译期零警告，运行期零异常**，是最难发现的那类。
//
// 实测抓到的那处：`Patch_RenderClockAsyncPosition` 给
// `AsyncInputUtils.GetSongPosition(scrConductor conductor, long nowTick)`
// （**静态**）打 Prefix，形参写 `__instance` ⇒ 恒 null ⇒ 走 `__result = 0.0` 分支
// ⇒ GetSongPosition 恒返回 0。目前被另一条补丁（渲染期禁用 AdjustAngle）挡着
// 不发作，但属于潜伏缺陷。
//
// 判据：每个声明了 `__instance` 的补丁方法，其目标方法在**真程序集**上必须是
// 实例方法。反射用真 Assembly-CSharp.dll，不靠源码文本猜。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace UiPreview
{
    internal static class InstanceInjectionCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 补丁形参注入（__instance 只对实例方法有效） ──");
            string dll = @"D:\Projects\adofai-libs\Assembly-CSharp.dll";
            if (!File.Exists(dll))
            {
                Console.WriteLine("    · 找不到游戏程序集，跳过");
                return 0;
            }
            Assembly game;
            try { game = Assembly.LoadFrom(dll); }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ 加载游戏程序集失败：" + ex.Message);
                return 1;
            }

            var rows = new List<(string file, string target, bool isStatic, bool found)>();
            foreach (var f in Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                if (f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                if (f.Contains(Path.DirectorySeparatorChar + "_legacy" + Path.DirectorySeparatorChar)) continue;
                string text = File.ReadAllText(f);
                if (!text.Contains("SpectrePatch(", StringComparison.Ordinal)) continue;

                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    int at = lines[i].IndexOf("[SpectrePatch(", StringComparison.Ordinal);
                    if (at < 0) continue;
                    string attr = lines[i].Substring(at);
                    if (!attr.Contains(",") && i + 1 < lines.Length) attr += " " + lines[i + 1];

                    var mt = System.Text.RegularExpressions.Regex.Match(attr, @"typeof\(\s*([\w\.]+)\s*\)");
                    if (!mt.Success) continue;
                    string typeName = mt.Groups[1].Value;

                    string method = "";
                    var mn = System.Text.RegularExpressions.Regex.Match(attr, @"""([^""]+)""");
                    if (mn.Success) method = mn.Groups[1].Value;
                    else
                    {
                        var nn = System.Text.RegularExpressions.Regex.Match(attr, @"nameof\(\s*[\w\.]+\.(\w+)\s*\)");
                        if (nn.Success) method = nn.Groups[1].Value;
                    }
                    if (method.Length == 0) continue;

                    // 找紧随的补丁方法签名
                    string sig = null;
                    for (int j = i + 1; j < lines.Length && j < i + 6; j++)
                    {
                        var sm = System.Text.RegularExpressions.Regex.Match(lines[j],
                            @"internal\s+static\s+[\w\<\>\[\]\?\.]+\s+\w+\s*\(([^)]*)\)");
                        if (sm.Success) { sig = sm.Groups[1].Value; break; }
                    }
                    if (sig == null || !sig.Contains("__instance")) continue;

                    Type t = game.GetType(typeName);
                    bool found = false, isStatic = false;
                    if (t != null)
                    {
                        var mi = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.Instance | BindingFlags.Static);
                        if (mi != null) { found = true; isStatic = mi.IsStatic; }
                    }
                    rows.Add((Path.GetFileName(f), typeName + "." + method, isStatic, found));
                }
            }

            int fail = 0;
            foreach (var r in rows)
            {
                if (!r.found)
                {
                    Console.WriteLine($"    ✗ {r.file} 的 {r.target} 在真程序集里找不到"
                        + "（改名了？这条补丁会静默挂不上）");
                    fail++;
                    continue;
                }
                if (r.isStatic)
                {
                    Console.WriteLine($"    ✗ {r.file} 给**静态方法** {r.target} 配了 __instance"
                        + " —— 静态方法上 __instance 恒为 null，补丁照跑但拿到空值");
                    fail++;
                }
            }
            if (rows.Count == 0)
            {
                Console.WriteLine("    · 没有声明 __instance 的补丁（形状变了？同步更新本检查）");
                return 0;
            }
            if (fail == 0)
                Console.WriteLine($"    ✓ {rows.Count} 处 __instance 全部作用于实例方法");
            return fail;
        }
    }
}
