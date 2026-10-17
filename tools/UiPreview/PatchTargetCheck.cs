// 补丁目标预检：把主项目里每一个 [SpectrePatch] 声明的目标，
// 拿到 **真游戏程序集** 上逐个反射核对是否存在、形参个数是否对。
//
// 为什么值得单独一条：补丁挂不上的失败方式不是报错，而是
//   「探针就绪但功能没生效」——日志里只多一行（目标不存在），
// 而玩家看到的是"某个开关点了没反应"。上一轮 24 轮偏差核对全白做，
// 真实原因就是模组压根没被加载（To skip (disabled)）。
//
// 本检查只做静态部分（离屏、不起游戏）：
//   ① 每个 [SpectrePatch] 的 ClassType 能在 Assembly-CSharp 里找到
//   ② MethodName 能在该类型（含基类）里找到，且重载可消歧
//   ③ PropertyAccessor 指定时，属性存在且有对应的 get/set 访问器
//   ④ 补丁方法自己的形参名能在目标重载上找到（DisambiguateByPatchParams 的输入）
// 版本门控（MinVersion/MaxVersion）按 r150 判定，与 GameVersion 一致。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace UiPreview
{
    internal static class PatchTargetCheck
    {
        private const int GameRelease = 150;

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 补丁目标预检（真程序集） ──");
            string dll = FindGameAssembly();
            if (dll == null)
            {
                Console.WriteLine("    · 找不到游戏程序集 Assembly-CSharp.dll，跳过");
                return 0;
            }
            Assembly game;
            try
            {
                game = Assembly.LoadFrom(dll);
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ 加载游戏程序集失败：" + ex.Message);
                return 1;
            }

            // 扫主项目源码收集 [SpectrePatch(...)] 声明
            var specs = CollectSpecs(projectRoot);
            if (specs.Count == 0)
            {
                Console.WriteLine("    ✗ 一条 [SpectrePatch] 都没收集到（扫描失效）");
                return 1;
            }

            int fail = 0, skipped = 0;

            // ② **覆盖率自检**：收集到的条数必须等于"剥注释后源码里
            //    [SpectrePatch( 的出现次数"。少一条就说明解析有盲区
            //    （第一版漏了三条 nameof(...) 形状的——包括偏差 21 的修复本身）。
            //    没有这条，解析漏项会静默变成"未校验"，比报错更危险。
            int rawDecls = 0;
            foreach (var f in Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                if (f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                if (f.Contains(Path.DirectorySeparatorChar + "tools" + Path.DirectorySeparatorChar)) continue;
                foreach (var line in SrcUtil.Clean(File.ReadAllText(f)).Split('\n'))
                    if (line.Contains("[SpectrePatch(", StringComparison.Ordinal)) rawDecls++;
            }
            if (specs.Count != rawDecls)
            {
                Console.WriteLine($"    ✗ 解析覆盖不全：源码里 {rawDecls} 条声明，只解析出 {specs.Count} 条"
                    + "  ← 有补丁没被校验（静默漏检比报错更危险）");
                fail++;
            }

            foreach (var s in specs)
            {
                // 版本门控：与 Resolve() 同序（Min/Max 都不满足即跳过，不算失败）
                if (s.MinVersion > 0 && GameRelease < s.MinVersion) { skipped++; continue; }
                if (s.MaxVersion > 0 && GameRelease > s.MaxVersion) { skipped++; continue; }

                Type t = game.GetType(s.ClassName);
                if (t == null)
                {
                    Console.WriteLine("    ✗ 目标类型不存在：" + s.ClassName + "  ← 补丁挂不上，功能静默失效");
                    fail++;
                    continue;
                }

                MethodBase target = FindTarget(t, s);
                if (target == null)
                {
                    Console.WriteLine("    ✗ 目标方法不存在或重载无法消歧：" + s.ClassName + "." + s.MethodName
                        + "  ← 补丁挂不上，功能静默失效");
                    fail++;
                    continue;
                }

                // 补丁方法的普通参数名必须能在目标上找到（DisambiguateByPatchParams 的输入）。
                // **Transpiler 豁免**：它的入参是 IEnumerable<CodeInstruction>（IL 流），
                // 不是游戏方法的形参，本来就无键可对。SpectrePatchHub 对 Transpiler
                // 也是直接取唯一目标、不走按名消歧。
                if (s.OrdinaryParams.Count > 0 && !s.IsTranspiler)
                {
                    var names = target.GetParameters().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                    foreach (string p in s.OrdinaryParams)
                    {
                        if (!names.Contains(p))
                        {
                            Console.WriteLine("    ✗ " + s.ClassName + "." + s.MethodName
                                + " 上没有名为 " + p + " 的形参（按名消歧会失败）");
                            fail++;
                        }
                    }
                }
            }

            Console.WriteLine($"    声明 {specs.Count} 条；版本跳过 {skipped} 条；核对 {specs.Count - skipped} 条");
            if (fail == 0)
                Console.WriteLine("    ✓ 所有生效的补丁目标在真程序集里都存在且形参对得上");
            return fail;
        }

        /// <summary>按括号深度扫一行，更新 depth / started / closed。</summary>
        private static void ScanDepth(string s, ref int depth, ref bool started, ref bool closed)
        {
            foreach (char c in s)
            {
                if (c == '(') { depth++; started = true; }
                else if (c == ')')
                {
                    depth--;
                    if (started && depth == 0) { closed = true; return; }
                }
            }
        }

        private sealed class Spec
        {
            public string ClassName;
            public string MethodName;
            public int MinVersion, MaxVersion;
            public bool IsAccessor;
            public bool AccessorGet;
            public bool IsTranspiler;
            public List<string> OrdinaryParams = new();
        }

        private static List<Spec> CollectSpecs(string projectRoot)
        {
            var list = new List<Spec>();
            foreach (var f in Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                if (f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                if (f.Contains(Path.DirectorySeparatorChar + "tools" + Path.DirectorySeparatorChar)) continue;

                // **必须先剥注释。** SpectrePatch.cs 的属性文档注释里有一整段
                // [SpectrePatch(...)] 的**示例代码**（typeof(scrFollower) /
                // PlayCoroutine 都在那里）。不剥的话示例被当成真声明，
                // 预检报一堆"目标不存在"——全是误报（第一版就这么栽了）。
                string whole = SrcUtil.Clean(string.Join("\n", File.ReadAllLines(f)));
                string[] lines = whole.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    int at = lines[i].IndexOf("[SpectrePatch(", StringComparison.Ordinal);
                    if (at < 0) continue;

                    // 属性的实参可能跨多行，按括号深度把整段拼出来再解析。
                    // 不用 goto：跳出条件写成一个显式的终止索引，避免标签跳过初始化。
                    var sb = new System.Text.StringBuilder();
                    sb.Append(lines[i].Substring(at));
                    int depth = 0; bool started = false; bool closed = false;
                    ScanDepth(sb.ToString(), ref depth, ref started, ref closed);
                    if (!closed)
                    {
                        for (int j = i + 1; j < lines.Length; j++)
                        {
                            sb.Append(' ').Append(lines[j].Trim());
                            ScanDepth(lines[j], ref depth, ref started, ref closed);
                            if (closed) break;
                        }
                    }
                    string attr = sb.ToString();

                    var s = new Spec();
                    var mt = System.Text.RegularExpressions.Regex.Match(attr,
                        @"typeof\(\s*([\w\.]+)\s*\)");
                    if (mt.Success) s.ClassName = mt.Groups[1].Value;
                    else
                    {
                        var cn = System.Text.RegularExpressions.Regex.Match(attr, @"ClassName\s*=\s*""([^""]+)""");
                        if (cn.Success) s.ClassName = cn.Groups[1].Value;
                    }
                    var mn = System.Text.RegularExpressions.Regex.Match(attr, @"""([^""]+)""");
                    // MethodName 常写作位置参数：PatchType 之后第一个字符串。
                    // 更稳的判法：取所有字符串实参里不属于枚举名的那个。
                    var names = System.Text.RegularExpressions.Regex.Matches(attr, @"""([^""]+)""")
                        .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
                    var enumWords = new HashSet<string>(StringComparer.Ordinal)
                        { "set_songposition_minusi" };
                    foreach (var n in names)
                    {
                        if (n.Contains('.')) continue;
                        // 属性访问器的实参是 PropertyAccessor.Get/Set，这不是方法名
                        if (attr.Contains("PropertyAccessor." + n)) continue;
                        s.MethodName = n;
                        break;
                    }
                    // **nameof(...) 形状必须单独解析。** VideoRenderPatches 的三个补丁
                    // 写的是 nameof(scrConductor.songposition_minusi) / nameof(AsyncInputUtils.AdjustAngle)，
                    // 不是字符串字面量。只认字符串的话这三条被静默漏掉——
                    // 而它们正是偏差 21「钉时间轴从未生效」的修复所在，最不该漏。
                    if (string.IsNullOrEmpty(s.MethodName))
                    {
                        var nm = System.Text.RegularExpressions.Regex.Match(attr, @"nameof\(\s*([\w\.]+)\s*\)");
                        if (nm.Success)
                        {
                            // 取最后一段：nameof(scrConductor.songposition_minusi) → songposition_minusi
                            string full = nm.Groups[1].Value;
                            s.MethodName = full.Substring(full.LastIndexOf('.') + 1);
                        }
                    }
                    s.MinVersion = IntOf(attr, "MinVersion");
                    s.MaxVersion = IntOf(attr, "MaxVersion");
                    s.IsAccessor = attr.Contains("PropertyAccessor.Get") || attr.Contains("PropertyAccessor.Set");
                    s.AccessorGet = attr.Contains("PropertyAccessor.Get");
                    s.IsTranspiler = attr.Contains("PatchType.Transpiler");

                    // 找紧随其后的补丁方法签名，收集普通参数名
                    for (int j = i + 1; j < lines.Length && j < i + 6; j++)
                    {
                        var pm = System.Text.RegularExpressions.Regex.Match(lines[j],
                            @"internal\s+static\s+[\w\<\>\[\]\?\.]+\s+\w+\s*\(([^)]*)\)");
                        if (!pm.Success) continue;
                        foreach (var p in pm.Groups[1].Value.Split(','))
                        {
                            string q = p.Trim();
                            if (q.Length == 0) continue;
                            // 去掉默认值再取名：`bool isAuto = false` 的形参名是 isAuto，
                            // 不是 false。第一版没切，于是拿 "false"/"null" 去目标上找形参，
                            // 报了一堆"没有名为 false 的形参"的误报。
                            int eq = q.IndexOf('=');
                            if (eq >= 0) q = q.Substring(0, eq).Trim();
                            var tn = System.Text.RegularExpressions.Regex.Match(q, @"\s([A-Za-z_]\w*)\s*$");
                            if (tn.Success && !tn.Groups[1].Value.StartsWith("__", StringComparison.Ordinal))
                                s.OrdinaryParams.Add(tn.Groups[1].Value);
                        }
                        break;
                    }

                    if (!string.IsNullOrEmpty(s.ClassName) && !string.IsNullOrEmpty(s.MethodName))
                        list.Add(s);
                }
            }
            return list;
        }

        private static int IntOf(string attr, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(attr, key + @"\s*=\s*(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        private static MethodBase FindTarget(Type t, Spec s)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                    | BindingFlags.Instance | BindingFlags.Static;
            if (s.IsAccessor)
            {
                PropertyInfo pi = null;
                for (Type c = t; c != null && pi == null; c = c.BaseType)
                    pi = c.GetProperty(s.MethodName, flags | BindingFlags.DeclaredOnly);
                if (pi == null) return null;
                var m = s.AccessorGet ? pi.GetGetMethod(true) : pi.GetSetMethod(true);
                return m;
            }
            // 收集全部同名重载（含基类），按最派生去重
            var cands = new List<MethodInfo>();
            for (Type c = t; c != null; c = c.BaseType)
                foreach (var m in c.GetMethods(flags | BindingFlags.DeclaredOnly))
                    if (m.Name == s.MethodName) cands.Add(m);
            var seen = new HashSet<MethodInfo>();
            var dedup = new List<MethodInfo>();
            foreach (var m in cands)
                if (seen.Add(m.GetBaseDefinition())) dedup.Add(m);
            if (dedup.Count == 0) return null;
            if (dedup.Count == 1) return dedup[0];
            if (s.OrdinaryParams.Count == 0) return null; // 多个重载且无法消歧 → 算失败
            MethodInfo best = null; int bestExtra = int.MaxValue;
            foreach (var m in dedup)
            {
                var ps = m.GetParameters();
                int matched = s.OrdinaryParams.Count(p => ps.Any(x => x.Name == p));
                if (matched < s.OrdinaryParams.Count) continue;
                int extra = ps.Length - matched;
                if (extra < bestExtra) { bestExtra = extra; best = m; }
            }
            return best;
        }

        private static string FindGameAssembly()
        {
            string[] candidates =
            {
                @"D:\Projects\adofai-libs\Assembly-CSharp.dll",
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }
    }
}
