// 版本兼容层（偏差 15）的检查。
//
// 关键点：这条检查**真的去反射游戏程序集**，不是读源码猜。
// 理由——兼容层的全部价值就是"在真程序集上选对重载"，那么唯一有意义的
// 验证方式就是把同一套选择算法跑在真元数据上，看它挑出来的那个方法
// 与游戏自己的定义一致。只断言源码里"有按参数个数分支"是没用的：
// 分支写对了但探测条件写错了（比如按名字取第一个而不看返回类型），
// 源码检查照样全绿。
//
// 这也是本项目少有的能"离线跑真东西"的地方：反射只需要元数据，
// 而 ADOBase 之类的基类解析用不到（GetMethods 返回的 ParameterInfo
// 上只挂着类型名，实测在 .NET 上不需要加载 UnityEngine 即可枚举）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class CompatCheck
    {
        private const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static;

        /// <summary>
        /// 定位游戏的 Assembly-CSharp.dll。
        ///
        /// internal 而不是 private：LifecycleCheck 也要在真程序集上核对
        /// 那四个退出入口还在不在。两处各找一次，路径漂了就有一处失配。
        ///
        /// 相对路径从 tools\UiPreview 起算：…\SpectreReplay\tools\UiPreview
        /// → 上三级是 D:\Projects，再拼 adofai-libs。
        /// 与 SpectreReplay.csproj 里的 HintPath 保持同一份拷贝，
        /// 免得两个地方指向不同的 dll。
        /// </summary>
        internal static string FindGameAssembly(out string why)
        {
            var candidates = new List<string>
            {
                Path.GetFullPath(@"..\..\..\adofai-libs\Assembly-CSharp.dll"),
                Path.GetFullPath(@"..\..\adofai-libs\Assembly-CSharp.dll"),
            };
            // 兜底：向上找 adofai-libs 目录（布局变了也不至于直接失效）
            var dir = new DirectoryInfo(Path.GetFullPath(".."));
            for (int i = 0; i < 5 && dir != null; i++)
            {
                candidates.Add(Path.Combine(dir.FullName, "adofai-libs",
                    "Assembly-CSharp.dll"));
                dir = dir.Parent;
            }
            foreach (var c in candidates)
            {
                if (File.Exists(c))
                {
                    why = c;
                    return c;
                }
            }
            why = string.Join(" / ", candidates);
            return null;
        }

        /// <summary>
        /// 与 GameCompat.FindByArity 完全同形的选择算法。
        ///
        /// 刻意**复刻**而不是链接——本项目链接不了（那个文件要 Unity 类型）。
        /// 复刻的代价是两边可能走偏，所以下面第 ③ 条断言源码里的
        /// GameCompat 也确实按这个形状在找。
        /// </summary>
        private static MethodInfo FindByArity(Type type, string name, int arity,
            Func<MethodInfo, bool> accept)
        {
            foreach (MethodInfo m in type.GetMethods(Any))
            {
                if (m.Name != name) continue;
                if (m.GetParameters().Length != arity) continue;
                if (accept != null && !accept(m)) continue;
                return m;
            }
            return null;
        }

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 版本兼容层 ──");
            int fail = 0;

            string why;
            string dll = FindGameAssembly(out why);
            if (dll == null)
            {
                Console.WriteLine("    ✗ ① 找不到 Assembly-CSharp.dll（试过：" + why + "）");
                return 1;
            }
            Assembly game;
            try
            {
                game = Assembly.LoadFrom(dll);
            }
            catch (Exception e)
            {
                Console.WriteLine("    ✗ ① 加载不了游戏程序集：" + e.GetType().Name);
                return 1;
            }

            fail += PlayerHitShape(game);
            fail += HitFiresInputEventsShape(game);
            fail += SwitchChosenShape(game);
            fail += HitInputEventShape(game, projectRoot);
            fail += VersionProbe(game, projectRoot);
            fail += SingleSourceOfTruth(projectRoot);
            return fail;
        }

        // ── ① scrPlayer.Hit ─────────────────────────────────────
        //
        // 本版本只有 2 参数那个重载（实测）。选择算法必须挑中它，
        // 且必须**不**挑中别的同名同参的东西。
        //
        // 判据同时看形状与返回值：只按 arity 挑是不够的——
        // 将来若游戏加一个 private 的 Hit(long?, bool) 辅助，
        // 按名字取第一个可能挑错。
        private static int PlayerHitShape(Assembly game)
        {
            var t = game.GetType("scrPlayer", false);
            if (t == null)
            {
                Console.WriteLine("    ✗ ① 游戏程序集里没有 scrPlayer");
                return 1;
            }
            var picked = FindByArity(t, "Hit", 2,
                    m => m.ReturnType == typeof(bool)
                         && m.GetParameters()[1].ParameterType == typeof(bool))
                ?? FindByArity(t, "Hit", 1,
                    m => m.ReturnType == typeof(bool)
                         && m.GetParameters()[0].ParameterType == typeof(bool));
            var ps = picked?.GetParameters();
            if (picked == null)
            {
                Console.WriteLine("    ✗ ① 兼容层在真程序集上找不到 scrPlayer.Hit"
                    + " ——状态轨推不动行星，回放会卡住");
                return 1;
            }
            bool ok = ps.Length == 2
                && ps[0].ParameterType == typeof(long?)
                && ps[1].ParameterType == typeof(bool)
                && picked.ReturnType == typeof(bool);
            if (!ok)
            {
                Console.WriteLine("    ✗ ① 挑中的 Hit 形状不对：" + Describe(picked));
                return 1;
            }
            Console.WriteLine("    ✓ ① scrPlayer.Hit 按形状选中 r150 重载 → "
                + Describe(picked));
            return 0;
        }

        // ── ② HitFiresInputEvents ────────────────────────────────
        //
        // Spectre 用它判断"Hit 内部是否已自调 HitInputEvent"。
        // 判据是"存在首参为 long? 的 Hit 重载"。本版本必须判 true。
        private static int HitFiresInputEventsShape(Assembly game)
        {
            var t = game.GetType("scrPlayer", false);
            var hit = FindByArity(t, "Hit", 2,
                m => m.ReturnType == typeof(bool)
                     && m.GetParameters()[0].ParameterType == typeof(long?));
            if (hit == null)
            {
                Console.WriteLine("    ✗ ② 探测不到首参为 long? 的 Hit ——"
                    + "本版本是 r150+（Hit 内部自调 HitInputEvent），"
                    + "判成 false 会导致将来代发时双触发");
                return 1;
            }
            Console.WriteLine("    ✓ ② HitFiresInputEvents 形状探测为 true"
                + "（与 scrPlayer.cs:754 自调 HitInputEvent 一致）");
            return 0;
        }

        // ── ③ scrPlanet.SwitchChosen ─────────────────────────────
        //
        // r150 起多一个 long? hitTick。两参择路必须挑中它。
        private static int SwitchChosenShape(Assembly game)
        {
            var t = game.GetType("scrPlanet", false);
            if (t == null)
            {
                Console.WriteLine("    ✗ ③ 游戏程序集里没有 scrPlanet");
                return 1;
            }
            // 返回类型拿**真实的 scrPlanet 类型**比对，而不是字符串名字。
            // 预览器不链接游戏程序集，但 Assembly.GetType 拿得到，
            // 所以 typeof(scrPlanetRef) 那层占位完全没必要——它反而
            // 逼出一句永远为 false 的判断（FindByArity 的 accept 永远不成立，
            // 于是永远落到 0 参那条分支，在 r150 上是错的）。
            var picked = FindByArity(t, "SwitchChosen", 0, null)
                ?? FindByArity(t, "SwitchChosen", 1,
                    m => m.ReturnType == t);
            if (picked == null)
            {
                Console.WriteLine("    ✗ ③ 找不到 scrPlanet.SwitchChosen");
                return 1;
            }
            int n = picked.GetParameters().Length;
            if (n != 1 || picked.ReturnType != t)
            {
                Console.WriteLine("    ✗ ③ SwitchChosen 形状不对：" + Describe(picked));
                return 1;
            }
            Console.WriteLine("    ✓ ③ scrPlanet.SwitchChosen 按形状选中 r150 重载 → "
                + Describe(picked));
            return 0;
        }

        // ── ④ scrPlayer.HitInputEvent ───────────────────────────
        //
        // r150 是 3 参（多 handleAll）。必须挑中 3 参那个，
        // 因为我们传 handleAll:false——与游戏内部调用严格一致。
        private static int HitInputEventShape(Assembly game, string projectRoot)
        {
            var t = game.GetType("scrPlayer", false);
            var picked = FindByArity(t, "HitInputEvent", 3, null)
                ?? FindByArity(t, "HitInputEvent", 2, null);
            if (picked == null)
            {
                Console.WriteLine("    ✗ ④ 找不到 scrPlayer.HitInputEvent");
                return 1;
            }
            int n = picked.GetParameters().Length;
            if (n != 3)
            {
                Console.WriteLine("    ✗ ④ HitInputEvent 形状不对（应为 3 参）："
                    + Describe(picked));
                return 1;
            }
            Console.WriteLine("    ✓ ④ scrPlayer.HitInputEvent 选中 3 参重载 → "
                + Describe(picked) + "（可传 handleAll:false）");
            return HandleAllIsFalse(projectRoot);
        }

        /// <summary>
        /// <c>handleAll</c> 必须传 <c>false</c>。
        ///
        /// 与游戏自身 <c>scrPlayer.Hit</c> 内的调用严格一致
        /// （scrPlayer.cs:754：<c>HitInputEvent(isAuto, InputEventState.Down,
        /// handleAll: false)</c>）。传 true 会让**一次按键触发多个
        /// inputEventFfx 目标**，而原版只发首个——表现是回放里输入特效
        /// 重复出现，且与录制不符。
        ///
        /// 这条断言是变异测试补的：把 false 改成 true 之后，上面的
        /// 形状检查（③④）依旧全绿——形状对了不等于**参数**对了。
        /// </summary>
        private static int HandleAllIsFalse(string projectRoot)
        {
            string file = Path.Combine(projectRoot, "Patch", "GameCompat.cs");
            if (!File.Exists(file))
            {
                Console.WriteLine("    ✗ ④ 找不到 Patch/GameCompat.cs");
                return 1;
            }
            string src = StripComments(File.ReadAllText(file));
            var m = Regex.Match(src, @"new\s+object\[\]\s*\{\s*isAuto\s*,\s*state\s*,\s*(\w+)\s*\}");
            if (!m.Success)
            {
                Console.WriteLine("    ✗ ④ 找不到 HitInputEvent 的三参实参数组");
                return 1;
            }
            string v = m.Groups[1].Value;
            if (v != "false")
            {
                Console.WriteLine("    ✗ ④ handleAll 传了 " + v
                    + "，应为 false：传 true 会让一次按键触发多个 inputEventFfx 目标"
                    + "，回放里输入特效会重复");
                return 1;
            }
            Console.WriteLine("    ✓ ④b handleAll 传 false"
                + "（与 scrPlayer.cs:754 的游戏内部调用一致）");
            return 0;
        }

        // ── ⑤ 版本探测读的是哪个字段 ────────────────────────────
        //
        // 这是本轮实测纠正的一处：**GCNS 存在但没有版本字段**，
        // 真正的版本在 Releases.releaseNumber。
        //
        // 若照抄"第一个存在的类型里找 releaseNumber"，
        // 一旦 GCNS 哪天加上同名字段就会取错——而版本取错的后果是
        // 升级之后反而走进旧路径（比取不到更糟）。
        private static int VersionProbe(Assembly game, string projectRoot)
        {
            var bad = new List<string>();
            foreach (var tn in new[] { "Releases", "GCNS" })
            {
                var t = game.GetType(tn, false);
                if (t == null)
                {
                    bad.Add(tn + " 不存在");
                    continue;
                }
                var f = t.GetField("releaseNumber", Any);
                if (f == null)
                {
                    bad.Add(tn + " 无 releaseNumber");
                }
                else if (f.FieldType != typeof(int))
                {
                    bad.Add(tn + ".releaseNumber 不是 int（" + f.FieldType.Name + "）");
                }
            }
            var releases = game.GetType("Releases", false);
            var gcns = game.GetType("GCNS", false);
            bool releasesHas = releases != null
                && releases.GetField("releaseNumber", Any) != null;
            bool gcnsHas = gcns != null && gcns.GetField("releaseNumber", Any) != null;
            if (!releasesHas)
            {
                Console.WriteLine("    ✗ ⑤ Releases.releaseNumber 不存在，"
                    + "版本探测会永远返回未知");
                return 1;
            }
            if (gcnsHas)
            {
                Console.WriteLine("    ✗ ⑤ GCNS 也有 releaseNumber——"
                    + "探测顺序必须以 Releases 优先，否则两版取值可能分叉");
                return 1;
            }
            int rel = Convert.ToInt32(
                releases.GetField("releaseNumber", Any).GetValue(null));
            if (rel < 140 || rel > 200)
            {
                Console.WriteLine("    ✗ ⑤ 取到的 releaseNumber 不像真实版本：" + rel);
                return 1;
            }
            Console.WriteLine("    ✓ ⑤ 版本探测命中 Releases.releaseNumber = " + rel
                + "（GCNS 存在但无版本字段，故必须 Releases 优先）");
            return ProbeOrderIsReleasesFirst(projectRoot);
        }

        /// <summary>
        /// 探测顺序必须 **Releases 在前**。
        ///
        /// 这一条是补上的：第一版只断言"Releases 有 releaseNumber 字段"，
        /// 于是把源码里的顺序改成 GCNS 优先（也就是 Spectre 的写法）照样全绿
        /// ——变异测试当场抓到了这个假断言。
        ///
        /// 为什么顺序要紧：<c>GCNS</c> 在 r150 就**存在**却不含版本字段，
        /// 而 <c>Releases.releaseNumber</c> 才是真版本。今天两者不一致，
        /// 所以两种顺序都能取到 150；但 GCNS 一旦加上同名字段，
        /// GCNS 优先就会取到另一个值，而版本取错的后果是
        /// **升级之后反而走进旧路径**（比取不到更糟）。
        ///
        /// 判据按**下标顺序**确认 Releases 排在前面，而不是"两个都出现"。
        /// </summary>
        private static int ProbeOrderIsReleasesFirst(string projectRoot)
        {
            string file = Path.Combine(projectRoot, "Patch", "GameCompat.cs");
            if (!File.Exists(file))
            {
                Console.WriteLine("    ✗ ⑤ 找不到 Patch/GameCompat.cs");
                return 1;
            }
            string src = StripComments(File.ReadAllText(file));
            var m = Regex.Match(src,
                @"new\s*\[\s*\]\s*\{\s*""(\w+)""\s*,\s*""(\w+)""\s*\}");
            if (!m.Success)
            {
                Console.WriteLine("    ✗ ⑤ 找不到版本探测的类型顺序数组");
                return 1;
            }
            string first = m.Groups[1].Value, second = m.Groups[2].Value;
            if (first != "Releases" || second != "GCNS")
            {
                Console.WriteLine("    ✗ ⑤ 版本探测顺序应为 Releases → GCNS，实际是 "
                    + first + " → " + second
                    + "；GCNS 一旦加上同名字段就会取错版本，"
                    + "而版本取错 = 升级后走旧路径");
                return 1;
            }
            Console.WriteLine("    ✓ ⑤b 版本探测顺序为 Releases → GCNS"
                + "（GCNS 迟早会有同名字段，顺序不能靠巧合）");
            return 0;
        }

        // ── ⑥ 唯一真源：不允许各处各写一份反射 ──────────────────
        //
        // 本轮开工时 `HitStateReplayer` 里有一份自己的 FindHitMethod，
        // 与兼容层的 FindByArity 是同一件事的两份实现。两份必然走偏：
        // 兼容层改了择路顺序，那份不会跟着改，而它恰好是**调用命中**的地方——
        // 走偏的表现是"回放卡住且日志无异常"。
        //
        // 判据：找反射调用 Hit 的地方，必须全部经过 GameCompat.PlayerHit。
        private static int SingleSourceOfTruth(string projectRoot)
        {
            var offenders = new List<string>();
            foreach (var file in Directory.GetFiles(
                Path.Combine(projectRoot, "Features"), "*.cs",
                SearchOption.AllDirectories))
            {
                if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
                if (Path.GetFileName(file) == "GameCompat.cs") continue;
                string src = StripComments(File.ReadAllText(file));
                // 自己缓存 MethodInfo 再 Invoke 的痕迹
                if (src.Contains("typeof(scrPlayer).GetMethods"))
                {
                    offenders.Add(Path.GetFileName(file));
                }
            }
            if (offenders.Count > 0)
            {
                Console.WriteLine("    ✗ ⑥ 这些文件自己反射 scrPlayer.Hit，"
                    + "与兼容层各写一份：" + string.Join(" / ", offenders)
                    + "——择路走偏时只有调用处受影响，且日志无异常");
                return 1;
            }
            string replayer = Path.Combine(projectRoot, "Features",
                "Playback", "HitStateReplayer.cs");
            if (!File.Exists(replayer)
                || !File.ReadAllText(replayer).Contains("GameCompat.PlayerHit"))
            {
                Console.WriteLine("    ✗ ⑥ 状态轨没有走 GameCompat.PlayerHit");
                return 1;
            }
            Console.WriteLine("    ✓ ⑥ 命中反射只有兼容层一份实现，状态轨经 GameCompat 调用");
            return 0;
        }

        private static string Describe(MethodInfo m)
        {
            var ps = m.GetParameters();
            var parts = new List<string>();
            foreach (var p in ps)
            {
                parts.Add(p.ParameterType.Name + " " + p.Name);
            }
            return m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", parts) + ")";
        }

        private static string StripComments(string src)
        {
            var sb = new System.Text.StringBuilder(src.Length);
            bool inBlock = false, inStr = false, inChar = false, esc = false;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (inBlock)
                {
                    if (c == '*' && i + 1 < src.Length && src[i + 1] == '/')
                    { inBlock = false; i++; }
                    else if (c == '\n') sb.Append(c);
                    continue;
                }
                if (!inStr && !inChar && c == '/' && i + 1 < src.Length && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != '\n') i++;
                    sb.Append('\n');
                    continue;
                }
                if (!inStr && !inChar && c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                { inBlock = true; i++; continue; }
                if (esc) { esc = false; sb.Append(c); continue; }
                if (c == '\\') { esc = true; sb.Append(c); continue; }
                if (c == '"') inStr = !inStr;
                else if (c == '\'') inChar = !inChar;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}