// 语言映射的**双向一致性**验证。
//
// 背景：界面文案（Loc.cs + LocData.cs）和运行时通知（LocalizationManager.cs）
// 是两套并行的东西：前者给面板标签，后者给游戏里的 toast。它们各自维护一份
// 「游戏语言 → 语言代码」的映射，值必须完全一样——否则玩家把游戏设成日语时，
// 面板是日语、而弹出来的通知是英语，且没有任何报错。
//
// 这类不一致**测不出来**：两套代码各自自洽，编译通过，运行正常，
// 只是特定语言组合下文案语言不同。所以必须用判据把两张表拉在一起比。
//
// 另外还有一个更隐蔽的坑：**桩件的枚举值可能与游戏真值不同**。
// 2026-10-19 实测过——tools\UiPreview\LocStubs.cs 里原先写
// ChineseSimplified = 41 / ChineseTraditional = 42，而游戏里是 40 / 41。
// 桩件与 Loc.cs 的常量同名，于是离线套件用错值也全绿（它俩一起错，自洽了），
// 真实游戏里则表现成「简中玩家看到繁中」。判据 ⑲a 重新解析游戏
// UnityEngine.CoreModule.dll 里的 SystemLanguage 枚举，逐项比对桩件。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class LocLanguageCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 语言映射一致性 ──");
            int fail = 0;

            // ① Loc.cs 的 CodeByLanguage 与 LocalizationManager.langlist 必须一致。
            //
            //    只查「两边都出现了 14 个语言代码」是不够的：两边各写一份
            //    `{(SystemLanguage)41, "zh-t"}` 会双双命中。必须按**枚举值 → 语言代码**
            //    成对比较，才能发现「同一枚举值在两处映射到不同语言」或
            //    「某语言在一处映射到 40、另一处映射到 41」。
            var loc = ParseLocCodeByLanguage(projectRoot);
            var mgrNums = ParseManagerLanglist(projectRoot);
            var mgr = new Dictionary<string, string>(StringComparer.Ordinal);
            var stubVals = StubEnumValues();
            foreach (var kv in mgrNums)
            {
                string name = null;
                foreach (var sv in stubVals) if (sv.Value == kv.Key) { name = sv.Key; break; }
                mgr[name ?? ("enum(" + kv.Key + ")")] = kv.Value;
            }
            if (loc.Count == 0)
            {
                Console.WriteLine("    ✗ 解析不出 Loc.cs 的 CodeByLanguage（形状变了？）");
                fail++;
            }
            if (mgrNums.Count == 0)
            {
                Console.WriteLine("    ✗ 解析不出 LocalizationManager.langlist（形状变了？）");
                fail++;
            }
            if (loc.Count > 0 && mgr.Count > 0)
            {
                foreach (var kv in loc)
                {
                    if (!mgr.TryGetValue(kv.Key, out string mgrCode))
                    {
                        Console.WriteLine($"    ✗ 语言枚举 {kv.Key} 在 Loc 里映射为 \"{kv.Value}\"，"
                            + "但 LocalizationManager.langlist 里根本没有这个枚举值");
                        fail++;
                    }
                    else if (mgrCode != kv.Value)
                    {
                        Console.WriteLine($"    ✗ 语言枚举 {kv.Key} 两边映射不同："
                            + $"Loc=\"{kv.Value}\"，LocalizationManager=\"{mgrCode}\"");
                        fail++;
                    }
                }
                foreach (var kv in mgr)
                {
                    if (!loc.ContainsKey(kv.Key))
                    {
                        Console.WriteLine($"    ✗ 语言枚举 {kv.Key} 只在 LocalizationManager 里出现"
                            + $"（\"{kv.Value}\"），Loc 里没有 → 该语言面板会落回英文");
                        fail++;
                    }
                }
                if (fail == 0)
                {
                    Console.WriteLine($"    ✓ 两套映射完全一致（各 {loc.Count} 种语言）");
                }
            }

            // ② LocData 的表必须覆盖 Loc 声明的每一种语言。
            //
            //    两张表分居不同文件（Loc.cs 是逻辑、LocData.cs 是数据，
            //    后者由 _gen_locdata.py 生成）。生成脚本漏掉一种语言时，
            //    Loc.cs 照样会 `TryGetValue` 失败并静默回落到英文——
            //    玩家看到英文界面，没有任何报错。
            var dataLangs = ParseLocDataLanguages(projectRoot);
            if (dataLangs.Count == 0)
            {
                Console.WriteLine("    ✗ 解析不出 LocData.cs 的语言清单（形状变了？）");
                fail++;
            }
            else if (loc.Count > 0)
            {
                var declared = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kv in loc) declared.Add(kv.Value);
                foreach (string code in declared)
                {
                    if (!dataLangs.Contains(code))
                    {
                        Console.WriteLine($"    ✗ Loc 声明了语言 \"{code}\"，但 LocData.cs 里没有这张表"
                            + " → 该语言下界面会静默回落到英文");
                        fail++;
                    }
                }
                foreach (string code in dataLangs)
                {
                    if (!declared.Contains(code))
                    {
                        Console.WriteLine($"    ✗ LocData.cs 有 \"{code}\" 表，但 Loc 不会选到它"
                            + " → 死数据（多半是生成脚本与 Loc.cs 脱节）");
                        fail++;
                    }
                }
                if (fail == 0)
                {
                    Console.WriteLine($"    ✓ LocData 覆盖 Loc 声明的全部 {dataLangs.Count} 种语言");
                }
            }

            // ③ 桩件的 SystemLanguage 枚举值必须与游戏真值一致。
            //
            //    只在能读到游戏程序集时才验（CI 里可能没有），读不到就明确说
            //    "跳过"，**不能静默通过**——静默通过等于这条判据不存在。
            fail += CheckStubEnumValues();

            return fail;
        }

        /// <summary>
        /// 解析 Loc.cs 里的 CodeByLanguage 字典，返回 (枚举名 → 语言代码)。
        ///
        /// **键用枚举名而不是数字。** 名字是本文件唯一不依赖外部值的东西：
        /// 换成数字就要先读桩件换算，而桩件本身也可能错（本次修的正是它）。
        /// 判据 ③ 单独验"名字 → 数字"这一层。
        /// </summary>
        private static Dictionary<string, string> ParseLocCodeByLanguage(string projectRoot)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            string code = SrcUtil.Source(projectRoot, @"Ui\Loc.cs");
            if (code.Length == 0) return result;

            int start = code.IndexOf("CodeByLanguage", StringComparison.Ordinal);
            if (start < 0) return result;
            // 字典项形如 { SystemLanguage.English, "en" },
            var hits = Regex.Matches(code.Substring(start),
                @"\{\s*SystemLanguage\.(\w+)\s*,\s*""([a-z\-]+)""\s*\}");
            foreach (Match m in hits)
            {
                result[m.Groups[1].Value] = m.Groups[2].Value;
            }
            return result;
        }

        /// <summary>
        /// 解析 LocalizationManager.langlist，返回 (枚举值 → 语言代码)。
        /// 那里的写法是 `{ (SystemLanguage)40, "zh" },`（裸数字）。
        /// </summary>
        private static Dictionary<int, string> ParseManagerLanglist(string projectRoot)
        {
            var result = new Dictionary<int, string>();
            string code = SrcUtil.Source(projectRoot, @"State\LocalizationManager.cs");
            if (code.Length == 0) return result;

            int start = code.IndexOf("langlist", StringComparison.Ordinal);
            if (start < 0) return result;
            // 只取 langlist 之后到下一个 `};` 之前那一段
            int end = code.IndexOf("};", start, StringComparison.Ordinal);
            if (end < 0) end = code.Length;
            string body = code.Substring(start, end - start);

            var hits = Regex.Matches(body,
                @"\{\s*\(SystemLanguage\)(\d+)\s*,\s*""([a-z\-]+)""\s*\}");
            foreach (Match m in hits)
            {
                result[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value;
            }
            return result;
        }

        /// <summary>解析 LocData.cs 里出现的语言代码集合。</summary>
        private static HashSet<string> ParseLocDataLanguages(string projectRoot)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            string code = SrcUtil.Source(projectRoot, @"Ui\LocData.cs");
            if (code.Length == 0) return set;
            foreach (Match m in Regex.Matches(code,
                @"\[\s*""([a-z\-]+)""\s*\]\s*=\s*new\s+System\.Collections\.Generic\.Dictionary"))
            {
                set.Add(m.Groups[1].Value);
            }
            return set;
        }

        /// <summary>
        /// 从桩件 LocStubs.cs 里读 SystemLanguage 的取值（名字 → 数字）。
        ///
        /// **读桩件而不是读游戏 dll**：Loc.CodeByLanguage 里写的是枚举**名字**
        /// （SystemLanguage.English），要把它换算成数字才能与 langlist 里的
        /// 裸数字比较。而"名字 → 数字"这层换算，编译期用的是桩件或真实
        /// UnityEngine——两者必须一致，否则同一份 Loc.cs 在离线与真机上
        /// 会选中不同语言。⑲a 的第 ③ 步就是验这一点。
        /// </summary>
        private static Dictionary<string, int> StubEnumValues()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "UiPreview", "LocStubs.cs");
            // 更可靠的定位：从仓库根推。但本函数在 LocCheck 之前也可能被调，
            // 所以两条路都试。
            foreach (string p in new[] { path,
                Path.Combine(RepoRoot(), "tools", "UiPreview", "LocStubs.cs") })
            {
                if (!File.Exists(p)) continue;
                string src = SrcUtil.Clean(File.ReadAllText(p));
                foreach (Match m in Regex.Matches(src, @"(\w+)\s*=\s*(\d+)\s*,"))
                {
                    string name = m.Groups[1].Value;
                    int num = int.Parse(m.Groups[2].Value);
                    if (!map.ContainsKey(name)) map[name] = num;
                }
                foreach (Match m in Regex.Matches(src, @"(\w+)\s*=\s*(\d+)\s*\n"))
                {
                    string name = m.Groups[1].Value;
                    int num = int.Parse(m.Groups[2].Value);
                    if (!map.ContainsKey(name)) map[name] = num;
                }
                break;
            }
            return map;
        }

        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "SpectreReplay"))
                    && File.Exists(Path.Combine(dir, "SpectreReplay.slnx")))
                    return dir;
                var parent = Directory.GetParent(dir.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (parent == null) break;
                dir = parent.FullName;
            }
            return Directory.GetCurrentDirectory();
        }

        /// <summary>
        /// 桩件的 SystemLanguage 取值是否与游戏真实程序集一致。
        /// 读不到游戏程序集时打印「跳过」并**不判红**——CI 里可能没装游戏，
        /// 而这条判据的目的是防"桩件与真值脱节"，不是要求每台机器都有游戏。
        /// 但跳过必须**说出口**，不能静默。
        /// </summary>
        private static int CheckStubEnumValues()
        {
            string dll = SrcUtil.GameAssembly(
                Path.Combine("..", "..", "UnityEngine.CoreModule.dll"));
            // GameAssembly 拼的是 adofai-libs 下的名字；UnityEngine 在游戏
            // Managed 目录，不在 adofai-libs。这里按绝对路径找。
            string candidates = @"D:\Program Files (x86)\Steam\steamapps\common\"
                + @"A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed\UnityEngine.CoreModule.dll";
            if (!File.Exists(candidates))
            {
                Console.WriteLine("    · 找不到游戏 UnityEngine.CoreModule.dll，跳过桩件枚举值比对");
                Console.WriteLine("      （桩件与真值脱节时，离线套件会自洽地全绿——这条判据只在有游戏的机器上生效）");
                return 0;
            }

            var stub = StubEnumValues();
            if (stub.Count == 0)
            {
                Console.WriteLine("    ✗ 解析不出桩件的 SystemLanguage 取值");
                return 1;
            }

            int fail = 0;
            try
            {
                // 在**独立** AssemblyLoadContext 里读，避免把 UnityEngine
                // 真程序集加载进本进程（会与桩件的 UnityEngine 命名空间冲突）。
                var asm = Assembly.LoadFrom(candidates);
                var t = asm.GetType("UnityEngine.SystemLanguage");
                if (t == null)
                {
                    Console.WriteLine("    ✗ 游戏程序集里找不到 UnityEngine.SystemLanguage");
                    return 1;
                }
                foreach (string name in stub.Keys)
                {
                    object val = Enum.Parse(t, name, false);
                    int real = Convert.ToInt32(val);
                    if (stub[name] != real)
                    {
                        Console.WriteLine($"    ✗ 桩件 SystemLanguage.{name} = {stub[name]}，"
                            + $"游戏真值 {real} → 离线全绿而真机选错语言");
                        fail++;
                    }
                }
                if (fail == 0)
                {
                    Console.WriteLine($"    ✓ 桩件 {stub.Count} 个枚举值与游戏真值一致");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("    ✗ 读取游戏枚举失败：" + e.GetType().Name + "：" + e.Message);
                fail++;
            }
            return fail;
        }
    }
}