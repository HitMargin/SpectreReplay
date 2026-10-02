// 特性对照：Spectre 剩下的六个目录（Other / Editor / CustomJudge /
// JudgeBar / ResultsPlus / Perf）我们为什么**不**补。
//
// ── 为什么需要这一组断言 ────────────────────────────────────────
// 「看过一遍觉得不用补」不是结论，是印象。印象会随时间褪色：
// 半年后有人（或有人工智能）看到 Spectre 里有一个
// `scrMisc.GetHitMarginInSec` 的补丁、或者一个
// `scrController.FailAction` 的屏蔽，就会想"这个我们好像没有"，
// 于是补一段进去。而补进去的后果不是少一个功能，是：
//
//   · 改判定的补丁一旦接上，**录下来的回放在别的机器上就飘**，
//     因为判定窗口不再是游戏自己那套；
//   · 屏蔽 FailAction 的补丁会让"玩家按错了键"不再失败，
//     录出来的按键全是命中的，回放看起来"很完美"但与真人对不上。
//
// 所以这里把六条结论钉成断言。**断言的方向是反的**：
// 不是"必须有 X"，而是"**不许出现** X"。
// 这样任何人想补这些东西时，闸门会立刻红，并告诉他为什么。
//
// ── 三种写法，和它们各自能骗过什么 ─────────────────────────────
//   · 只查"我们的源码里没有 scrMisc" → 有人写 `typeof(scrMisc)`
//     到一个字符串里、或把判定改写藏进 GameCompat，就全绿。
//     所以下面 ① 用**去注释后的**源码全文扫（注释里的引用不算，
//     但代码里的字符串字面量算）。
//   · 只查名字不查形状 → 把 `IsCounted` 拼成字符串传给
//     AccessTools.Method 照样全绿。所以 ② 真去反射真程序集。
//   · 只查"我们没这个开关" → 用户可以在设置里打开它。
//     所以 ③ 查的是**开关本身不存在**，而不是它的默认值。
//
// ── 这一组断言**能**证明什么、**不能**证明什么 ────────────────
// 能：六条"不补"的结论在**代码层面**没被绕过。
// 不能：证明 Spectre 那些功能对我们没用——那是判断，不是可测事实。
//     判定的依据写在每条断言的注释里（读的是真程序集与真源码行号）。
// 界面几何一概不在本组断言范围内：`GUILayout` 是声明式的，
// 离线复刻等于重写一遍引擎（见 Driver 头注）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class FeatureParityCheck
    {
        private const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static;

        /// <summary>
        /// 六目录的封禁词。每一项后面写清"它属于哪个目录、为什么封"。
        ///
        /// **判据是"去注释后的代码里出现即红"**，不是"出现即可疑"：
        /// 注释里提到某个 API 是必要的（要说明为什么不补），
        /// 代码里出现就是真的要动那条链路。
        ///
        /// 刻意**不含** `SwitchChosen`：我们有 `GameCompat.SwitchChosen`
        /// （Patch\GameCompat.cs:140），但全仓零调用点（实测 grep
        /// `GameCompat.SwitchChosen` = 0 命中），且我们从不改它的返回值。
        /// 把名字封了会逼出一个"删掉死代码"的额外改动，与本组目标无关。
        /// </summary>
        private static readonly (string Token, string Dir, string Why)[] Bans =
        {
            // ── CustomJudge：唯一会改判定本身的一组 ──────────────
            ("IsValidHit", "CustomJudge",
                "仅判定模式把'命中是否算数'整个重写；我们靠游戏自己的判定，"
                + "回放才有意义"),
            ("HitMarginHelper", "CustomJudge",
                "同上（r150 的替身入口）"),
            ("GetHitMarginInDeg", "CustomJudge",
                "按角度重算判定档位：接上就等于换掉判定窗口"),
            ("GetHitMarginInSec", "CustomJudge",
                "按时间重算判定档位（异步输入路径）：同上"),
            ("scrMisc", "CustomJudge",
                "三个判定重写入口全在这个类上"),

            // ── JudgeBar：判定条相关的读写 ───────────────────────
            ("scrHitErrorMeter", "JudgeBar",
                "判定条的布局与刻度绘制；我们没有判定条可改"),
            ("ErrorMeterAddHit", "JudgeBar",
                "往判定条塞刻度（含合成的失败刻度）"),
            ("ErrorMeterTick", "JudgeBar",
                "单个刻度对象的绘制钩子"),
            ("DetermineDifficultyUIMode", "JudgeBar",
                "解锁 Strict 难度的判定条 UI 模式；我们不改难度选项"),
            ("DifficultyUIMode", "JudgeBar",
                "上面那个枚举；我们从不解它"),

            // ── ResultsPlus：结算页与误差采集 ─────────────────────
            ("keyFrequency", "ResultsPlus",
                "结算页按键统计的去重逻辑；不影响录制与回放"),
            ("CalibrationAdvisor", "ResultsPlus",
                "校准建议；只读命中误差，不改任何链路"),

            // ── Editor：编辑器内的事件/时间轴改写 ────────────────
            ("ApplyEventsToFloors", "Editor",
                "改写编辑器事件后重刷砖块；只在编辑器时间轴上有意义"),
            ("ScrubMusicToTime", "Editor",
                "拖时间轴时对齐歌曲；拖动时才可达"),
            ("ScrubMusic", "Editor",
                "上面那个的调用面；我们从不拖动时间轴"),
            ("turnaround", "Other",
                "转向角：只影响暂停动画时长"),

            // ── Editor/Other：判死与飘字的屏蔽 ───────────────────
            ("FailAction", "Editor",
                "BGA 模式在失败入口 return false（永不失败）；"
                + "我们的死亡屏障挂在更上游的 scrPlayer.Die 上，不依赖它"),
            ("ShowHitText", "Editor",
                "BGA 模式屏蔽判定飘字；飘字是录进视频里的画面，屏蔽它"
                + "会让成片与录制不一致"),
            ("TogglePauseGame", "Other",
                "Spectre 只在'编辑器 playMode + auto + 回放中'拦暂停；"
                + "我们的时钟是墙钟，暂停期间照样推进，见 ⑤"),

            // ── Perf：只数自己的补丁声明 ─────────────────────────
            ("SpectrePatch", "Perf",
                "性能统计扫的是 Spectre 自身程序集，不挂游戏方法"),
        };

        /// <summary>
        /// 开关封禁词：**设置里不许有**这些名字。
        ///
        /// 与 BanTokens 的区别：这一组只看 <c>Config\Settings.cs</c> 一个文件，
        /// 而且判据是"声明了"而不是"默认关"——因为"默认关"是一个会被
        /// 改掉的默认值，而 Spectre 的 BGAMode / CustomJudge / JudgeBar
        /// 三组补丁**一旦打开就会改判死与判定**，属于使用约束而非可关的选项。
        /// </summary>
        private static readonly (string Token, string Dir, string Why)[] BanSwitches =
        {
            ("BGAMode", "Editor",
                "8 处补丁，含 FailAction 屏蔽与飘字屏蔽；录制期间打开会"
                + "让'失败'不再发生"),
            ("BGA", "Editor", "同上（FeatureKeys.BGAMode 的前缀形式）"),
            ("CustomJudge", "CustomJudge", "整套判定改写的总开关"),
            ("CustomRealJudge", "CustomJudge", "按角度/时间重算判定"),
            ("CustomOnly", "CustomJudge", "仅判定模式：非最内档一律算未命中"),
            ("TierJudge", "CustomJudge", "分档判定的状态"),
            ("CustomTier", "CustomJudge", "分档档位表（TierJudge 依赖它）"),
            ("JudgeBar", "JudgeBar", "判定条与其失败带（14 处补丁）"),
            ("ResultsPlus", "ResultsPlus", "结算页增强"),
        };

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 六目录特性对照（Other / Editor / CustomJudge /"
                + " JudgeBar / ResultsPlus / Perf） ──");
            int fail = 0;
            fail += NoBannedApiInOurCode(projectRoot);
            fail += JudgeRewriteTargetsAreShapedAsSpectreAssumed();
            fail += NoParitySwitches(projectRoot);
            // JudgeBar 不单列：它的每一处游戏侧入口都在 ① 的封禁表里
            // （scrHitErrorMeter / ErrorMeterAddHit / ErrorMeterTick /
            // DifficultyUIMode）。剩下那两个只读文件
            // （FailureJudgement / JudgeBarWindows）读的是它自己的状态，
            // JudgeBarMeter 改的 sensitivity/tickLife 是它自己的组件，
            // 都不碰游戏 API —— 没有可以写成"我们没碰"的东西。
            fail += OurHitAccountingIsUnpolluted(projectRoot);
            fail += NoPerfSelfScan(projectRoot);
            fail += EditorPauseDoesNotDesync(projectRoot);
            return fail;
        }

        // ── ① 我们的代码里不许碰那六组的 API ────────────────────
        //
        // 扫**去注释后**的全部 .cs：注释提到是必要的（说明为什么不补），
        // 代码里出现才是真的要动那条链路。
        //
        // 为什么扫全文而不是只扫补丁声明：改判定可以完全不写
        // `[GamePatch]`——写在 `GameCompat` 里、用 `FindByArity` 反射找
        // 方法再 `Invoke`，是本项目自己用过的写法（Patch\GameCompat.cs）。
        // 只扫补丁声明的话，那条路是敞开的。
        private static int NoBannedApiInOurCode(string projectRoot)
        {
            var hits = new List<string>();
            int scanned = 0;
            foreach (var file in Directory.GetFiles(
                projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
                // 预览器自己的桩件不在被检查的目录里
                if (file.Contains("tools")) continue;
                scanned++;
                string code = SrcUtil.Clean(File.ReadAllText(file));
                foreach (var (token, dir, _) in Bans)
                {
                    // **不区分大小写**：目标名几乎一定会被写成字符串再反射
                    // （`AccessTools.Method(typeof(scrMisc), "GetHitMarginInSec")`
                    // 这种），而那时大小写由敲代码的人随手决定。
                    // 实测过：改成忽略大小写后全仓仍零命中（仅有的三处
                    // 都在注释里，而注释已被剥掉），所以这条更严的判据
                    // 不产生误伤。
                    var rx = new Regex(@"\b" + Regex.Escape(token) + @"\b",
                        RegexOptions.IgnoreCase);
                    Match m = rx.Match(code);
                    if (!m.Success) continue;
                    int line = 1;
                    for (int i = 0; i < m.Index; i++)
                    {
                        if (code[i] == '\n') line++;
                    }
                    hits.Add($"{Path.GetFileName(file)}:{line}  {token}"
                        + $"  ← {dir}：{WhyOf(token)}");
                }
            }
            if (hits.Count > 0)
            {
                foreach (var h in hits) Console.WriteLine("    ✗ ① " + h);
                Console.WriteLine("    ✗ ① 这些是 Spectre 六目录里的东西。"
                    + "补进来不是多一个功能：改判定的会让回放判定窗口不再是"
                    + "游戏自己那套，屏蔽 FailAction 的会让'按错键'不再失败");
                return 1;
            }
            Console.WriteLine($"    ✓ ① 扫了 {scanned} 个文件：六组 API 一个都没碰");
            return 0;
        }

        private static string WhyOf(string token)
        {
            foreach (var (t, _, why) in Bans)
            {
                if (t == token) return why;
            }
            return "";
        }

        // ── ② 真程序集里，那几个目标确实是 Spectre 假定的形状 ───
        //
        // 「不补」这个结论有个前提：**补了才有意义**。
        // 如果 Spectre 的补丁目标在真游戏版本上根本不存在
        // （`scrMisc.IsValidHit` 在 r150 已被删除），那"不补"就更是对的，
        // 而且这条事实会随游戏更新失效——所以每次闸门都要重新量一遍，
        // 而不是把结论写死成注释。
        //
        // 同时核对**我们唯一用到的那条判定链**还在：
        // `scrPlayer.Hit` 仍是 `Hit(long?, bool) -> bool`。
        // 这条断了，状态轨就没有可靠入口，回放会整条瘫掉。
        private static int JudgeRewriteTargetsAreShapedAsSpectreAssumed()
        {
            string why;
            string dll = CompatCheck.FindGameAssembly(out why);
            if (dll == null)
            {
                Console.WriteLine("    ✗ ② 找不到 Assembly-CSharp.dll（试过：" + why + "）");
                return 1;
            }
            Assembly game;
            try
            {
                game = Assembly.LoadFrom(dll);
            }
            catch (Exception e)
            {
                Console.WriteLine("    ✗ ② 加载不了游戏程序集：" + e.GetType().Name);
                return 1;
            }
            int fail = 0;

            // ②a 自定义判定的四个目标：三个在、IsValidHit 不在。
            fail += Expect(game, "scrMisc", "GetHitMarginInDeg", 7, true);
            fail += Expect(game, "scrMisc", "GetHitMarginInSec", 5, true);
            fail += Expect(game, "HitMarginHelper", "IsCounted", 2, true);
            // r141–r149 的老入口在 r150 已被删除：Spectre 的
            // `MaxVersion = 149` 那两条（GetHitMargin / IsValidHit）
            // 在这个版本上根本挂不上，"不补"因此没有成本。
            fail += Expect(game, "scrMisc", "GetHitMargin", 6, false);
            fail += Expect(game, "scrMisc", "IsValidHit", 1, false);

            // ②b 我们自己的判定入口必须还在。
            var player = game.GetType("scrPlayer", false);
            if (player == null)
            {
                Console.WriteLine("    ✗ ② 真程序集里没有 scrPlayer");
                return fail + 1;
            }
            var hits = Names(player, "Hit");
            if (!hits.Contains("Hit/2"))
            {
                Console.WriteLine("    ✗ ② scrPlayer 没有 Hit/2 了 ——"
                    + " 状态轨没有可靠入口，回放会整条瘫掉");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ②b scrPlayer.Hit/2 还在（我们唯一依赖的判定入口）");
            }
            return fail;
        }

        /// <summary>核对某类型上某方法的元数，返回 0/1。</summary>
        private static int Expect(Assembly game, string typeName,
            string method, int arity, bool mustExist)
        {
            var t = game.GetType(typeName, false);
            if (t == null)
            {
                if (mustExist) return ReportAbsent(typeName, method);
                Console.WriteLine($"    ✓ ② {typeName} 整个类型都不在了"
                    + $"（Spectre 的 {method} 补丁无从挂载）");
                return 0;
            }
            bool found = Names(t, method).Contains(method + "/" + arity);
            // 通过时也要打印：不打印的绿断言与"根本没跑"在输出里长得一样，
            // 而这一组的全部价值就是"游戏升级后这五个目标变了没有"，
            // 变没变必须看得见。
            if (found == mustExist)
            {
                if (mustExist)
                {
                    Console.WriteLine($"    ✓ ② {typeName}.{method}/{arity} 在"
                        + "（Spectre 的自定义判定入口，我们的封禁表盯着它）");
                }
                else
                {
                    Console.WriteLine($"    ✓ ② {typeName}.{method} 确实不在这个版本上"
                        + "（Spectre 的 r141–r149 分支本来就挂不上）");
                }
                return 0;
            }
            if (mustExist)
            {
                var all = Names(t, method);
                Console.WriteLine($"    ✗ ② {typeName}.{method} 不是 {arity} 参了"
                    + $"（实际：{(all.Count == 0 ? "没有这个方法" : string.Join(" ", all))}）"
                    + " —— 结论「不补」可能要重新评估");
                return 1;
            }
            Console.WriteLine($"    ✗ ② {typeName}.{method} 居然又出现了"
                + " —— Spectre 的 r141–r149 分支在本版本上重新挂得上，"
                + "「不补」的依据变了，这条断言要重新评估");
            return 1;
        }


        private static int ReportAbsent(string typeName, string method)
        {
            Console.WriteLine($"    ✗ ② {typeName} 上没有 {method} ——"
                + " Spectre 那组补丁的前提没了，「不补」的结论要重新评估");
            return 1;
        }

        private static List<string> Names(Type t, string method)
        {
            var list = new List<string>();
            foreach (MethodInfo m in t.GetMethods(Any))
            {
                if (m.Name == method)
                {
                    list.Add($"{m.Name}/{m.GetParameters().Length}");
                }
            }
            return list;
        }

        // ── ③ 设置里不许有那六个目录的开关 ──────────────────────
        //
        // 只看 `Config\Settings.cs`，判据是"声明了"，不是"默认关"。
        // 理由在 BanSwitches 的注释里：这三组补丁一旦打开就会改判死与判定，
        // 不是可以后置一个默认值来兜住的选项。
        private static int NoParitySwitches(string projectRoot)
        {
            string path = Path.Combine(projectRoot,
                "Config", "Settings.cs");
            if (!File.Exists(path))
            {
                Console.WriteLine("    ✗ ③ 找不到 Config\\Settings.cs");
                return 1;
            }
            string code = SrcUtil.Clean(File.ReadAllText(path));
            var hits = new List<string>();
            foreach (var (token, dir, why) in BanSwitches)
            {
                // 只要"声明了就行"：字段、属性、方法名里出现即算
                if (Regex.IsMatch(code, @"\b" + Regex.Escape(token) + @"\b"))
                {
                    hits.Add($"{token}  ← {dir}：{why}");
                }
            }
            if (hits.Count > 0)
            {
                foreach (var h in hits)
                {
                    Console.WriteLine("    ✗ ③ 设置里出现了 " + h);
                }
                return 1;
            }
            Console.WriteLine("    ✓ ③ 设置里没有那六组的开关（只有 5 个自有开关）");
            return 0;
        }

        // ── ④ 判定记账不被我们的触碰污染 ────────────────────────
        //
        // 这一条不是"不许碰"，是"碰了必须是只读"。判据有两半：
        //
        //   · 我们的 `scrMarginTracker.AddHit` 只有一个挂点，
        //     而且是 Postfix（只读）。
        //   · 全仓**没有任何** `.AddHit(` 调用。
        //
        // 为什么这条重要：`AddHit` 是判定的记账入口。Judgement 条带
        // `scrPlayer.Die` 的 Postfix 往判定条塞合成 tick 时会碰到它，
        // 而 Spectre 自己的 Patch_FailureJudgement.cs:89 就是这么干的。
        // 一旦我们也去调它（哪怕是"顺手修一下"），录下来的
        // `hitMargins` 就与真实判定对不上，回放里的判定条飘字全错，
        // 而日志里一句异常都没有。
        private static int OurHitAccountingIsUnpolluted(string projectRoot)
        {
            string recording = Path.Combine(projectRoot,
                "Features", "Recording", "Patch_Recording.cs");
            if (!File.Exists(recording))
            {
                Console.WriteLine("    ✗ ④ 找不到 Patch_Recording.cs");
                return 1;
            }
            string code = SrcUtil.Clean(File.ReadAllText(recording));
            int fail = 0;

            // ④a 唯一的挂点必须是只读 Postfix，且方法体里不调 AddHit
            if (!Regex.IsMatch(code,
                @"\[GamePatch\(\s*typeof\(scrMarginTracker\)\s*,\s*""AddHit"""
                + @"\s*,\s*PatchKind\.Postfix"))
            {
                Console.WriteLine("    ✗ ④ scrMarginTracker.AddHit 上的挂点不见了"
                    + "（应当是只读 Postfix）—— 是拆了，还是被改成了别的形状？");
                fail++;
            }
            // 前一个字符是 \w 或 . 时不算调用（GetMethod("AddHit") 之类）
            else if (Regex.IsMatch(code, "(?<![\\w.])AddHit\\s*\\("))
            {
                Console.WriteLine("    ✗ ④ Patch_Recording.cs 里出现了 AddHit 的**调用**"
                    + " —— 记账入口被动过，录下来的判定会与真实判定对不上");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ④ AddHit 只有只读 Postfix，没有调用");
            }

            // ④b 全仓扫：任何文件里都不许有 .AddHit( 调用
            foreach (var file in Directory.GetFiles(
                projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
                if (file.Contains("tools")) continue;
                string src = SrcUtil.Clean(File.ReadAllText(file));
                foreach (Match m in Regex.Matches(src, @"\.\s*AddHit\s*\("))
                {
                    Console.WriteLine($"    ✗ ④ {Path.GetFileName(file)} 调了 .AddHit("
                        + " —— 判定记账被动过");
                    fail++;
                    break;
                }
            }
            return fail;
        }

        // ── ⑤ Perf 目录：它压根不挂游戏方法 ─────────────────────
        //
        // Spectre 的 `FeaturePatchCost.cs` 只反射**自身程序集**数
        // `[SpectrePatch]` 声明数，好让设置界面显示"补丁开销"。
        // 它不碰游戏，也不碰我们。
        //
        // 这条断言的价值在于**它的形状很特别**：判据不是禁词，
        // 而是要确认"我们没有把任何按 Spectre 名字计数的逻辑搬过来"。
        // 换句话说，它是防"抄了一个纯装饰性的统计"——
        // 那类代码不改变任何行为，但会让"特性对照"看起来像有遗漏。
        private static int NoPerfSelfScan(string projectRoot)
        {
            var hits = new List<string>();
            foreach (var file in Directory.GetFiles(
                projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
                if (file.Contains("tools")) continue;
                string src = File.ReadAllText(file);
                if (!Regex.IsMatch(src, @"Assembly\s*\.\s*GetTypes|"
                    + @"Assembly\s*\.\s*Load(?:From)?\s*\(")) continue;
                hits.Add(Path.GetFileName(file));
            }
            if (hits.Count > 0)
            {
                foreach (var h in hits)
                {
                    Console.WriteLine("    ✗ ⑤ " + h + " 反射加载了程序集"
                        + " —— 要么是抄了 Perf 目录那个自我统计，要么是"
                        + "在运行时动态取补丁目标。两者都该走源码级判据");
                }
                return 1;
            }
            Console.WriteLine("    ✓ ⑤ 没有反射加载程序集的自我统计（Perf 目录那一类）");
            return 0;
        }

        // ── ⑤b 编辑器内回放被暂停：时钟与帧栅格会不会错位 ────────
        //
        // 这条是**唯一一条"我们不补 Spectre 的东西"的理由**：
        // Spectre 在 `scrController.TogglePauseGame` 上挂了前缀，
        // 在"编辑器 playMode + auto + 回放中"时把暂停按掉
        // （Spectre\Features\Replay\Patch_Misc.cs:11-31）。
        // 我们没有这个挂点——所以要确认"没有它"确实是安全的。
        //
        // 两个时钟：
        //   · **帧栅格**是墙钟（LiveSession:374 `_startWall + _frameIndex / fps`，
        //     时钟是 Stopwatch），暂停期间照样推进 —— 这是**故意的**，
        //     见 LiveSession:26-29 的说明（timeScale 会跟着暂停停，
        //     一次慢动作就够音画错位）。
        //   · **回放游标**读的是 `conductor.songposition_minusi`，暂停时
        //     游戏停了 → 游标不动 → 按键不注入。
        //
        // 于是暂停期间：墙钟走、游标停。
        //   · 对**离线渲染**（OfflineSession）无害：它把歌曲位置钉在
        //     `_frameIndex / fps`（OfflineSession:225），暂停也不改这个值，
        //     渲染照样逐帧推进 —— 这正是"离线"该有的行为。
        //   · 对**实时录制**只是多录一段静止画面，且编辑器的
        //     `pausedInPlayMode` 还会顺带关掉相机自定义帧率
        //     （scnEditor.cs:1052-1056 `SetCustomFrameRate(enable: false)`），
        //     那只影响游戏自己往四边形上贴的纹理，不影响我们
        //     `cam.Render()` 到 RenderTexture 的逐帧捕获。
        //
        // 所以"不补 TogglePauseGame"是有依据的，不是漏抄。
        // 断言钉的是这个依据：帧栅格必须用墙钟，不能改成
        // `Time.realtimeSinceStartup`（那个跟随 timeScale，暂停即停）。
        private static int EditorPauseDoesNotDesync(string projectRoot)
        {
            string rel = "Features/VideoRender/LiveSession.cs";
            string src = SrcUtil.Source(projectRoot, rel);
            if (src.Length == 0)
            {
                Console.WriteLine("    ✗ ⑥ 找不到 " + rel);
                return 1;
            }
            int fail = 0;

            // ⑥a 时钟必须是 Stopwatch 墙钟
            if (!Regex.IsMatch(src, @"new\s+Stopwatch\s*\("))
            {
                Console.WriteLine("    ✗ ⑥ 实时录制的时钟不是 Stopwatch 墙钟"
                    + " —— 换成 Time.realtimeSinceStartup 的话，游戏一暂停"
                    + "（编辑器内回放就可能被暂停）帧栅格就停，音画从此错位");
                fail++;
            }
            else if (Regex.IsMatch(src,
                @"Time\s*\.\s*(realtimeSinceStartup|unscaledTime|deltaTime|time)\b"))
            {
                Console.WriteLine("    ✗ ⑥ 帧栅格里出现了 Time.* 读点"
                    + " —— 它们跟随 timeScale，暂停/慢动作会让音画永久错位");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ⑥ 实时录制的帧栅格走墙钟（Stopwatch），"
                    + "暂停与慢动作都不影响");
            }

            // ⑥b 离线渲染把歌曲位置钉在帧序号上
            string off = SrcUtil.Source(projectRoot,
                "Features/VideoRender/OfflineSession.cs");
            if (!Regex.IsMatch(off,
                @"PinnedSongPosition\s*=\s*_frameIndex\s*/\s*\(\s*double\s*\)\s*_fps"))
            {
                Console.WriteLine("    ✗ ⑥ 离线渲染的钉扎值不再由帧序号算出"
                    + " —— 改回读真实歌曲位置的话，暂停/掉帧会让"
                    + "「离线」变成按真实时间在跑，而画面照出、无任何症状");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ⑥ 离线渲染的歌曲位置钉在帧序号上（与墙钟无关）");
            }
            return fail;
        }
    }
}
