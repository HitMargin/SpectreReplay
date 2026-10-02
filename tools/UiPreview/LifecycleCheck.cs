// 离开关卡时的拆除链（偏差 23）。
//
// ── 这一条守的是什么 ─────────────────────────────────────────
// `PlaybackEngine.Stop()` 原本只有三个调用点：界面上的"停止"按钮、
// 泵自己的异常兜底、离线渲染收尾。也就是说**只要玩家不点那个按钮，
// 回放就不会停**——哪怕他已经重开、换关、退回主菜单了。
// 而 `LevelWatcher.FinishCurrentLevel()` 只管录制与渲染，
// 它连 `PlaybackEngine.Stop()` 都不调（这条链本来就分给了两个东西）。
//
// 残留的具体后果：
//   · `RestoreGlobalState()` 没跑 → `noFail` 仍是 true、`GCS.hitMarginLimit`
//     仍是录制时那档，**之后正常游玩也跟着生效**；
//   · `KeyboardSimulation.ReleaseAll()` 没跑 → 回放中途按下的键一直卡着；
//   · `HitStateReplayer` 留着上一局的数据，跟着新关卡的 UpdateFreeroam
//     继续按上一局角度推进行星。
//
// ── 为什么拆成这些断言 ───────────────────────────────────────
// 上一轮的经验：**检查了一个名字在，没检查它还起没起作用**。
// "文件里有 Teardown()"这种判据全绿而拆除照样不生效的情况有三种，
// 这里各占一条：
//   · 空 Feature 被当成"功能名是空串"查表 → 查不到 → **一个补丁都不挂**，
//     而 Failures 是空的，日志只留下"补丁 N/N-4 已挂载"这种看着正常的计数；
//   · 挂点写成 Postfix → 场景已经切走，拆得太晚；
//   · 四个挂点各自复制一份拆除代码 → 改了三处忘了第四处。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class LifecycleCheck
    {
        /// <summary>
        /// 期望覆盖的退出入口。名字与真程序集里的方法名逐一对应。
        ///
        /// 为什么正好是这四个：这四个是真程序集里**所有**通往
        /// 「离开游玩状态」的公共入口。逐个核过：
        ///   · `scrLoader.LoadSceneWithTransition`（scrLoader.cs:107）是场景切换
        ///     的唯一实现，它的调用点只有 6 处，全都从这四个方法里出去
        ///     （scrController.cs:2300/2447/2626、scnEditor.cs:1605、
        ///       scnCLS.cs:2216、scnCalibration.cs:527）；
        ///   · `scrUIController.WipeToBlack`（scrUIController.cs:202）是 UI 级
        ///     擦黑，PauseMenu 换关、关卡选择界面回退都走它；
        ///   · `scnEditor.SwitchToEditMode`（scnEditor.cs:3156）**不换场景**，
        ///     只切游戏状态——所以任何"看场景名"的判据都发现不了这次离开。
        /// 少挂一个，表现都是"回放停不下来"且没有任何日志。
        /// </summary>
        private static readonly string[] HookTypes =
        {
            "scrUIController", "scrController", "scrController", "scnEditor"
        };
        private static readonly string[] HookMethods =
        {
            "WipeToBlack", "Restart", "QuitToMainMenu", "SwitchToEditMode"
        };

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 退出时的拆除链 ──");
            int fail = 0;
            fail += HooksArePrefixes(projectRoot);
            fail += HooksAreAlwaysMounted(projectRoot);
            fail += EmptyFeatureMeansUngated(projectRoot);
            fail += HooksShareOneTeardown(projectRoot);
            fail += TeardownIsGuarded(projectRoot);
            fail += TargetsExistInRealAssembly();
            fail += StopReleasesBeforeEarlyReturn(projectRoot);
            fail += HitLockKeepsTheTwoTracksApart(projectRoot);
            return fail;
        }

        // ── ① 四条退出挂点都在，且必须全是 Prefix ────────────────
        //
        // Postfix 不行：`scrLoader.LoadSceneWithTransition` 起一个 0.3 秒的
        // DOTween 擦黑，擦完才 `SceneManager.LoadScene`。Postfix 时旧场景的
        // scrController 还活着、按住的键还按着——拆得太晚。
        private static int HooksArePrefixes(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot,
                "Features/Playback/Patch_Lifecycle.cs");
            if (src.Length == 0)
            {
                Console.WriteLine("    ✗ ① 找不到 Features/Playback/Patch_Lifecycle.cs"
                    + "（退出时没人调 Stop：noFail 与判定窗口会一直留在录制时那档）");
                return 1;
            }
            int missing = 0;
            for (int i = 0; i < HookMethods.Length; i++)
            {
                var m = Regex.Match(src,
                    @"typeof\(\s*" + HookTypes[i] + @"\s*\)\s*,\s*"""
                    + HookMethods[i] + @"""\s*,\s*PatchKind\.(\w+)");
                if (!m.Success)
                {
                    Console.WriteLine("    ✗ ① 没有挂在 " + HookTypes[i]
                        + "." + HookMethods[i] + " 上");
                    missing++;
                    continue;
                }
                if (m.Groups[1].Value != "Prefix")
                {
                    Console.WriteLine("    ✗ ① " + HookTypes[i] + "." + HookMethods[i]
                        + " 挂成了 " + m.Groups[1].Value
                        + "：场景切换的擦黑有 0.3 秒，Postfix 时旧状态还在，拆得太晚");
                    missing++;
                }
            }
            if (missing > 0) return 1;
            Console.WriteLine("    ✓ ① 四条退出挂点齐全，且都是 Prefix"
                + "（WipeToBlack / Restart / QuitToMainMenu / SwitchToEditMode）");
            return 0;
        }

        // ── ② 挂点不能被功能开关吞掉 ─────────────────────────────
        //
        // 这是最容易踩且最难查的一处。`FeatureGate.IsEnabled("playback")`
        // 读的是 `PlaybackEngine.IsPlaying`——**没载入回放时它就是 false**，
        // 而 `PatchLoader.Refresh()` 在门控为假时直接 `continue`，
        // 连 TryApply 都不进、Failures 里也不会有它。
        //
        // 于是"载入过回放 → 退出去"能拆，"没载入过 → 退出去"拆不了，
        // 而日志里只有一行"补丁 N/N-4 已挂载（计数偏少是正常的）"——
        // 那句话本来是为 playback 组写的，反而替这个 bug 打了掩护。
        //
        // 正确写法是**不写第三参数**（Feature 留空 = 不设门控）。
        private static int HooksAreAlwaysMounted(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot,
                "Features/Playback/Patch_Lifecycle.cs");
            var attrs = Regex.Matches(src,
                @"\[GamePatch\((?:[^\][]|\[[^\]]*\])*\)");
            var hooks = new List<Match>();
            foreach (Match a in attrs)
            {
                bool isHook = false;
                foreach (string name in HookMethods)
                {
                    if (a.Value.Contains("\"" + name + "\""))
                    { isHook = true; break; }
                }
                if (isHook) hooks.Add(a);
            }
            if (hooks.Count < HookMethods.Length)
            {
                Console.WriteLine("    ✗ ② 只找到 " + hooks.Count + " 条退出挂点特性");
                return 1;
            }
            foreach (Match a in hooks)
            {
                // 特性实参：typeof(X), "Name", PatchKind.K [, "feature" [, types...]]
                // 第三段之后若还有字符串实参，那就是设了门控。
                var parts = SplitTopLevel(a.Value.Substring(
                    a.Value.IndexOf('(') + 1,
                    a.Value.LastIndexOf(')') - a.Value.IndexOf('(') - 1));
                if (parts.Count < 3)
                {
                    Console.WriteLine("    ✗ ② 解析不了退出挂点的特性实参：" + a.Value);
                    return 1;
                }
                if (parts.Count > 3)
                {
                    Console.WriteLine("    ✗ ② 退出挂点设了功能门控：" + parts[3]
                        + "\n      回放没载入时该开关就是 false，"
                        + "补丁压根不挂，而日志里一句失败都没有"
                        + "（还会被「playback 组计数偏少是正常的」那句掩盖）");
                    return 1;
                }
            }
            Console.WriteLine("    ✓ ② 退出挂点不设功能门控（Feature 留空 = 恒挂载）");
            return 0;
        }

        /// <summary>按顶层逗号切分，跳过 typeof(...) 里的括号与字符串里的逗号。</summary>
        private static List<string> SplitTopLevel(string args)
        {
            var parts = new List<string>();
            int depth = 0; bool inStr = false, inChar = false; int start = 0;
            for (int i = 0; i < args.Length; i++)
            {
                char c = args[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                if (inChar) { if (c == '\\') i++; else if (c == '\'') inChar = false; continue; }
                if (c == '"') { inStr = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(args.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            parts.Add(args.Substring(start).Trim());
            return parts;
        }

        // ── ③ 空 Feature 的语义必须是「恒挂载」 ──────────────────
        //
        // ② 之所以能成立，靠的就是这一条。这两件事必须成对出现：
        // 一边约定"留空 = 不设门控"，另一边就得真的把空串当 true。
        //
        // 原来的实现没有这个分支：空串被当成一个**功能名**去查表，
        // 查不到 → false。缺陷形状极其恶劣——补丁不挂载、不报失败、
        // 计数还"看起来正常"。所以这条断言盯的是 `IsEnabled` 本身。
        private static int EmptyFeatureMeansUngated(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot, "Patch/PatchLoader.cs");
            if (src.Length == 0)
            {
                Console.WriteLine("    ✗ ③ 找不到 Patch/PatchLoader.cs");
                return 1;
            }
            int sig = src.IndexOf("IsEnabled(string name)");
            if (sig < 0)
            {
                Console.WriteLine("    ✗ ③ FeatureGate.IsEnabled 签名变了，"
                    + "得重新确认空 Feature 的语义");
                return 1;
            }
            int loop = src.IndexOf("for (int i = 0", sig);
            int body = src.IndexOf('{', sig);
            int emptyGuard = src.IndexOf("string.IsNullOrEmpty(name)", body);
            if (emptyGuard < 0)
            {
                Console.WriteLine("    ✗ ③ IsEnabled 没有处理空 Feature"
                    + "：留空会被当成一个查不到的功能名 → false → "
                    + "所有不设门控的补丁（含退出拆除链）一个都挂不上，"
                    + "且 Failures 里一条都没有");
                return 1;
            }
            // 必须在查表循环**之前**返回 true，否则空串照样先被查一遍。
            if (loop >= 0 && emptyGuard > loop)
            {
                Console.WriteLine("    ✗ ③ 空 Feature 的早退写在查表循环之后，"
                    + "空串仍会先落到「没声明 → false」那条路上");
                return 1;
            }
            int ret = src.IndexOf("return true;", emptyGuard);
            int guardEnd = src.IndexOf('}', emptyGuard);
            if (ret < 0 || ret > guardEnd)
            {
                Console.WriteLine("    ✗ ③ 空 Feature 的分支里没有 return true");
                return 1;
            }
            // 反面判据：**非空**且没声明的名字仍然必须判关闭。
            // 少了这条，把整个函数改成 return true 也全绿——而那会让
            // 「recording」开关关掉之后补丁照旧挂着，用户界面全是假的。
            int undef = src.IndexOf("return false;", ret);
            if (undef < 0)
            {
                Console.WriteLine("    ✗ ③ 没声明的功能名不再判关闭："
                    + "把 IsEnabled 整个改成 return true 也能过这条断言，"
                    + "而那会让所有开关失效（界面显示已关、功能照常在跑）");
                return 1;
            }
            Console.WriteLine("    ✓ ③ 空 Feature = 恒挂载；未声明的非空名仍判关闭");
            return 0;
        }

        // ── ④ 四条挂点共用同一个拆除入口 ─────────────────────────
        //
        // 不要求"各自复制一份"：拆四处逻辑，漏改一处就出现"退主菜单会拆、
        // 重开关卡不会拆"这种只在某条路径上复现的问题，而且离线看不出。
        private static int HooksShareOneTeardown(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot,
                "Features/Playback/Patch_Lifecycle.cs");
            var attrs = Regex.Matches(src,
                @"\[GamePatch\((?:[^\][]|\[[^\]]*\])*\)");
            foreach (Match a in attrs)
            {
                bool isHook = false;
                foreach (string name in HookMethods)
                {
                    if (a.Value.Contains("\"" + name + "\"")) { isHook = true; break; }
                }
                if (!isHook) continue;
                int at = a.Index + a.Length;
                int sig = src.IndexOf("private static void", at);
                if (sig < 0 || sig - at > 200)
                {
                    Console.WriteLine("    ✗ ④ 挂点之后没有紧跟补丁方法：" + a.Value);
                    return 1;
                }
                // 方法体必须**就在**这个签名后面。
                //
                // 少了这一步，"方法体被删空"这种变异会一路找到**下一个**
                // 挂点的方法体，读到人家的 Teardown()，照样全绿。
                // 第一版就踩了这个：形状全对、一个补丁都不干活，闸门是绿的。
                int paramsEnd = src.IndexOf(')', sig);
                int bodyStart = paramsEnd < 0 ? -1 : paramsEnd + 1;
                while (bodyStart >= 0 && bodyStart < src.Length
                       && char.IsWhiteSpace(src[bodyStart]))
                {
                    bodyStart++;
                }
                if (bodyStart < 0 || src[bodyStart] != '{')
                {
                    Console.WriteLine("    ✗ ④ " + a.Value + " 的补丁方法体不在签名后面"
                        + "（方法体被删空/挪走时，判据会读到下一个挂点的体，"
                        + "于是「四条挂点都在」全绿而拆除一条都不发生）");
                    return 1;
                }
                string body = ExtractBody(src, bodyStart);
                var calls = Regex.Matches(body, @"\b(\w+)\s*\(\s*\)\s*;");
                if (calls.Count != 1 || calls[0].Groups[1].Value != "Teardown")
                {
                    var names = new List<string>();
                    foreach (Match c in calls) names.Add(c.Groups[1].Value);
                    Console.WriteLine("    ✗ ④ " + a.Value + " 的方法体不是只调 Teardown()，而是："
                        + (names.Count == 0 ? "空" : string.Join(" + ", names))
                        + "\n      四处各写一份拆除逻辑，漏改一处就会"
                        + "「退主菜单会拆、重开关卡不会拆」，且离线看不出来");
                    return 1;
                }
            }
            Console.WriteLine("    ✓ ④ 四条挂点的方法体都只有 Teardown() 一个调用");
            return 0;
        }

        /// <summary>取 `private static void X()` 的方法体（大括号配平）。</summary>
        private static string ExtractBody(string src, int sig)
        {
            int open = src.IndexOf('{', sig);
            if (open < 0) return "";
            int depth = 0; bool inStr = false, inChar = false;
            for (int i = open; i < src.Length; i++)
            {
                char c = src[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                if (inChar) { if (c == '\\') i++; else if (c == '\'') inChar = false; continue; }
                if (c == '"') { inStr = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return src.Substring(open, i - open + 1); }
            }
            return "";
        }

        // ── ⑤ 拆除入口必须整体兜住异常 ───────────────────────────
        //
        // 这四个挂点挂在游戏的场景切换入口上。补丁体抛出去会让**整个场景切换
        // 失败**——玩家点了退主菜单却没退，而日志里是 Harmony 的异常堆栈。
        // 所以 try/catch 不是保险起见，是这条挂点的必要条件。
        //
        // 同时确认它调的是 `PlaybackEngine.Stop()`：清数据与还原全局状态
        // （noFail / 判定窗口）只有那一条路径。
        private static int TeardownIsGuarded(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot,
                "Features/Playback/Patch_Lifecycle.cs");
            int at = src.IndexOf("Teardown()", src.IndexOf("Teardown()") + 1);
            if (at < 0)
            {
                Console.WriteLine("    ✗ ⑤ 找不到 Teardown 的定义");
                return 1;
            }
            // 取定义处那个（后面跟 { 的那个）
            int def = Regex.Match(src,
                @"internal static void Teardown\(\)").Index;
            if (def < 0)
            {
                Console.WriteLine("    ✗ ⑤ Teardown 的签名变了，得重新确认它被 try 包住");
                return 1;
            }
            string body = ExtractBody(src, def);
            int tryAt = body.IndexOf("try");
            int catchAt = body.IndexOf("catch");
            if (tryAt < 0 || catchAt < 0 || tryAt > catchAt)
            {
                Console.WriteLine("    ✗ ⑤ Teardown 没有被 try/catch 包住"
                    + "：它挂在场景切换入口上，抛出去会让玩家点了退主菜单却退不掉");
                return 1;
            }
            if (!Regex.IsMatch(body, @"try\s*\{[^}]*PlaybackEngine\.Stop\s*\(\s*\)"))
            {
                Console.WriteLine("    ✗ ⑤ try 里没有 PlaybackEngine.Stop()"
                    + "：noFail 与判定窗口不会被还原，录制时的档位会跟着正常游玩一直生效");
                return 1;
            }
            Console.WriteLine("    ✓ ⑤ Teardown 整体 try/catch，内调 PlaybackEngine.Stop()");
            return 0;
        }

        // ── ⑥ 四个目标方法在**真程序集**里确实存在，形状还对 ──────
        //
        // 前面五条都是源码级断言：它们只能证明"我们写了四条挂点"，
        // 证明不了"这四条挂得上"。游戏一旦改名或改签名，
        // `ResolveTarget` 返回 null → Failures 里多一行"（目标不存在）"，
        // 而拆除链就静静地少了一条。
        //
        // 所以这条**真的去反射游戏程序集**，形参个数也一并核对——
        // `Restart(bool)` 与无参的同名方法同时存在时，形状才是判据。
        private static int TargetsExistInRealAssembly()
        {
            string why;
            string dll = CompatCheck.FindGameAssembly(out why);
            if (dll == null)
            {
                Console.WriteLine("    ✗ ⑥ 找不到 Assembly-CSharp.dll（试过：" + why + "）");
                return 1;
            }
            Assembly game;
            try
            {
                game = Assembly.LoadFrom(dll);
            }
            catch (Exception e)
            {
                Console.WriteLine("    ✗ ⑥ 加载不了游戏程序集：" + e.GetType().Name);
                return 1;
            }
            int bad = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var pair in new[]
            {
                new[] { "scrUIController", "WipeToBlack", "3" },
                new[] { "scrController", "Restart", "1" },
                new[] { "scrController", "QuitToMainMenu", "0" },
                new[] { "scnEditor", "SwitchToEditMode", "1" },
            })
            {
                var t = game.GetType(pair[0], false);
                if (t == null)
                {
                    Console.WriteLine("    ✗ ⑥ 真程序集里没有 " + pair[0]);
                    bad++;
                    continue;
                }
                var hit = FindWithArity(t, pair[1], int.Parse(pair[2]));
                if (hit == null)
                {
                    Console.WriteLine("    ✗ ⑥ " + pair[0] + "." + pair[1]
                        + " 没有 " + pair[2] + " 参数的重载——"
                        + "该退出路径上我们没有挂点，回放停不下来"
                        + "（游戏改名/改签名时只会多一行「（目标不存在）」）");
                    bad++;
                    continue;
                }
                if (sb.Length > 0) sb.Append("，");
                sb.Append(pair[1]).Append('/').Append(pair[2]);
            }
            if (bad > 0) return 1;
            Console.WriteLine("    ✓ ⑥ 四个目标在真程序集里都存在且形状对 → " + sb);
            return 0;
        }

        private static MethodInfo FindWithArity(Type t, string name, int arity)
        {
            foreach (MethodInfo m in t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name == name && m.GetParameters().Length == arity) return m;
            }
            return null;
        }

        // ── ⑦ Stop() 的三件拆除必须在 `_data == null` 早退之前 ────
        //
        // 这是 Stop() 自己的老问题，但**有了退出钩子之后它从"少一条路"
        // 变成了"每次退出都要走这条路"**，所以得钉住。
        //
        // 早退之后的三件事都会被跳过：
        //   · `KeyboardSimulation.ReleaseAll()` —— 按下的键卡着，
        //     玩家的下一个物理按键只会被当成一次松开，之后判定全乱；
        //   · `HitStateReplayer.Unload()` —— 上一局的数据留着，
        //     跟着新关卡的 UpdateFreeroam 继续按上一局角度推进行星；
        //   · `RestoreGlobalState()` —— noFail / 判定窗口留在录制时那档。
        private static int StopReleasesBeforeEarlyReturn(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot,
                "Features/Playback/PlaybackEngine.cs");
            int at = src.IndexOf("internal static void Stop()");
            if (at < 0)
            {
                Console.WriteLine("    ✗ ⑦ 找不到 PlaybackEngine.Stop()");
                return 1;
            }
            string body = ExtractBody(src, at);
            int guard = body.IndexOf("_data == null");
            if (guard < 0)
            {
                Console.WriteLine("    ✗ ⑦ Stop() 里没有 `_data == null` 早退，"
                    + "得重新确认三件拆除都在它之前");
                return 1;
            }
            var must = new[]
            {
                new[] { "KeyboardSimulation.ReleaseAll", "松键" },
                new[] { "HitStateReplayer.Unload", "状态轨" },
                new[] { "RestoreGlobalState", "全局状态" },
            };
            foreach (var m in must)
            {
                int i = body.IndexOf(m[0]);
                if (i < 0)
                {
                    Console.WriteLine("    ✗ ⑦ Stop() 里没有 " + m[0]
                        + "（" + m[1] + "没释放）：退出关卡后残留到下一次游玩");
                    return 1;
                }
                if (i > guard)
                {
                    Console.WriteLine("    ✗ ⑦ " + m[0] + " 写在了 `_data == null` 早退**之后**"
                        + "（" + m[1] + "）：退出钩子必然走到这条早退路径——"
                        + "回放被中断时数据可能已清空，但那三样是我们自己持有的，必须照样释放");
                    return 1;
                }
            }
            Console.WriteLine("    ✓ ⑦ Stop() 的松键 / 卸状态轨 / 还原全局状态都在早退之前");
            return 0;
        }

        // ── ⑧ 双轨互锁必须掐掉原方法（不是"什么都不做"）──────────
        //
        // 这条是 ⑦ 的下游：状态轨播放时，键轨注入的按键会让游戏自己去调
        // `scrPlayer.Hit`。那次命中必须**掐掉**（前缀返回 false），
        // 因为它已由状态轨用录制数据主动复现过一次。
        //
        // 若前缀改成 `return true`（"别拦游戏自己的命中"），后果是**键轨的
        // 判定列表里混进整局回放产生的判定**——而 `AddHit` 的 Postfix
        // 不查 HitLocked，挡不住。于是新录的回放里多出一整层重复判定，
        // 回放它自己时判定条飘两次字、行星跳两格。
        //
        // 判据要落到 `return false` 这个字面量上：只看
        // "文件里出现过 HitLocked" 会全绿，而把 true 改成 false 正是
        // 唯一的关键区别。
        private static int HitLockKeepsTheTwoTracksApart(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot,
                "Features/Recording/Patch_HitRecord.cs");
            int lockAt = src.IndexOf("PlaybackEngine.HitLocked");
            if (lockAt < 0)
            {
                Console.WriteLine("    ✗ ⑧ scrPlayer.Hit 前缀里没有查 PlaybackEngine.HitLocked"
                    + "：状态轨播放时键轨注入的按键会被游戏再判一次，"
                    + "同一块砖判两次（判定条飘两次字、行星跳两格）");
                return 1;
            }
            // 从条件右括号之后开始看：判定的是「这层 if 的体里第一句话」，
            // 而不是「文件里出现过 return false」。
            int guard = src.IndexOf(')', lockAt);
            int brace = guard < 0 ? -1 : src.IndexOf('{', guard);
            if (brace < 0)
            {
                Console.WriteLine("    ✗ ⑧ HitLocked 后面没有跟着一个 if 体");
                return 1;
            }
            var ret = Regex.Match(src.Substring(brace, Math.Min(320, src.Length - brace)),
                @"^\s*\{\s*(return\s+(true|false)\s*;)\s*\}");
            if (!ret.Success)
            {
                Console.WriteLine("    ✗ ⑧ 判定不到 HitLocked 早退后的返回值"
                    + "（应为 `return false`，掐掉游戏自己那次命中）");
                return 1;
            }
            int falseAt = brace + ret.Groups[1].Index;
            string got = ret.Groups[2].Value;
            if (got != "false")
            {
                Console.WriteLine("    ✗ ⑧ HitLocked 早退返回的是 return " + got
                    + "：状态轨已经在用录制数据复现这次命中了，"
                    + "再让游戏判一次就是同一块砖判两遍；"
                    + "而 AddHit 的 Postfix 不查 HitLocked，挡不住"
                    + "→ 键轨判定列表里混进整局回放的判定");
                return 1;
            }
            // 确认 return false 确实是这个文件里唯一的一处，
            // 且不在 IsRecording 判断之后（双轨互锁必须第一个判）。
            int recAt = src.IndexOf("KeyRecorder.IsRecording");
            if (recAt >= 0 && recAt < brace)
            {
                Console.WriteLine("    ✗ ⑧ IsRecording 判断排在 HitLocked 互锁之前"
                    + "：回放时录制本来就关着（IsRecording=false 是常态），"
                    + "互锁永远走不到");
                return 1;
            }
            return NoOtherFalseReturn(src, falseAt);
        }

        /// <summary>
        /// 整个采集路径上只允许这一处 `return false`。
        ///
        /// 前缀返回 false = 跳过整个 `scrPlayer.Hit`。多一处就等于
        /// 某条早退会把游戏自己的命中掐掉——那是"游戏突然打不出判定"，
        /// 比记错一条数据严重得多。
        /// </summary>
        private static int NoOtherFalseReturn(string src, int allowedAt)
        {
            foreach (Match m in Regex.Matches(src, @"return\s+false\s*;"))
            {
                if (m.Index == allowedAt) continue;
                int line = src.LastIndexOf('\n', m.Index) + 1;
                Console.WriteLine("    ✗ ⑧ 采集路径上第 " + (m.Index + 1) + " 行还有一处 return false："
                    + src.Substring(line, src.IndexOf('\n', line) - line).Trim()
                    + "\n      前缀返回 false = 跳过整个 scrPlayer.Hit，"
                    + "多一处就是「游戏自己的命中被掐掉」，"
                    + "表现是玩家按键完全没有判定");
                return 1;
            }
            Console.WriteLine("    ✓ ⑧ HitLocked 早退 return false 且是唯一一处，"
                + "双轨不会互相重复判定");
            return 0;
        }
    }
}