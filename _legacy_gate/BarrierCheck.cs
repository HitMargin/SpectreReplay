// 回放期间不许游戏自己判死（偏差 24）。
//
// ── 这一条守的是什么 ─────────────────────────────────────────
// 回放靠 `PlaybackEngine.ClampGlobalState`（PlaybackEngine.cs:306-321）
// 强制 `controller.noFail = true` 来保证"不会误判失败"。
// 这个假设是真的吗？逐行读过 `scrPlayer.Die`：
//
//     public void Die(bool overload = false, bool multipress = false,
//                      string failMessage = "", bool hitbox = false)
//     {
//         if (!hitbox)                      // ← 整圈保护包在这个 if 里
//         {
//             bool flag2 = ADOBase.controller.noFail
//                      || (currFloor.isSafe && GCS.hitMarginLimit == HitMarginLimit.None)
//                      || missCooldownActive;          // scrPlayer.cs:331
//             ...
//             if (flag2 || num > 0) { ...; return; }   // :348/:356/:369
//         }
//         planetarySystem.Die(deathAnimation, ADOBase.controller.Fail2Action);  // :380
//         alive = false;                                                    // :382
//         ADOBase.controller.OnPlayerDied(overload, multipress,
//                                          failMessage, hitbox);            // :383
//         ...
//     }
//
// `hitbox == true` 时那圈保护被**整段跳过**，直接进失败状态机：
// `OnPlayerDied` → `scrController.FailAction`（scrController.cs:597）→
// :609 `ChangeState(States.Fail)` + :634 `conductor.song.Stop()`。
// noFail 在这条路上**一个字都不管用**。
//
// 而这条路并不冷清：`scrPlayer.DieByHitbox()`（scrPlayer.cs:317-320）
// 转发 `hitbox: true`，游戏里有 8 个调用点
// （scrDecoration.cs:453 / LaserSpawner.cs:154 / Mawaru.cs:818 /
//   PuzzleRoom.cs:81 / DivineIntervention.cs:1082/1874 / scrPlanet.cs:1249），
// 外加自定义判定（Spectre 的 Patch_RealJudge.cs:142 就写着
// `player.Die(false,false,"",true)`）。
//
// 现象：**成片在这一砖断掉，且日志里一句异常都没有**——
// 与用户报过的"渲染卡住/不出片"是同一种沉默。
//
// ── 为什么这些断言是这个形状 ─────────────────────────────────
// 一条"这里要挡 hitbox 死亡"的口号，可以用三种错误写法糊弄过去：
//   · 写成 `if (!hitbox) return false;`（判据反了）→ 正常死亡全被跳过，
//     玩家自己按键打出的失败判定也没了；
//   · 漏掉 `IsPlaying` → 回放不在的时候也拦，把真人游玩打死了；
//   · 判据只看"文件里出现过 IsPlaying" → 把它挪进别的条件里照样全绿。
// 所以这里三条断言分别盯这三个形状，加两条盯挂载与真程序集。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class BarrierCheck
    {
        private const string ClampsRel = "Features/Playback/PlaybackClamps.cs";

        /// <summary>回放判定用的判据：只在回放运行中拦。</summary>
        private const string ReplayOnlyGuard =
            @"if\s*\(\s*!\s*PlaybackEngine\.IsPlaying\s*\|\|\s*!\s*hitbox\s*\)\s*\{";

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 回放期的死亡屏障 ──");
            int fail = 0;
            fail += DiePrefixIsMounted(projectRoot);
            fail += GuardIsReplayOnlyAndCorrectlyOrdered(projectRoot);
            fail += EveryFalseReturnIsNarrowed(projectRoot);
            fail += IgnoreMarkFailIsRestored(projectRoot);
            fail += PlanetExplosionIsNotBlocked(projectRoot);
            fail += TargetsExistInRealAssembly();
            return fail;
        }

        // ── ① 屏障挂上了吗 ────────────────────────────────────────
        //
        // 特性本身先查：类型、方法名、PatchKind 三样都要对。
        // 写成 Postfix 也得红——`Die` 跑完就 `ChangeState(Fail)` 了，
        // Postfix 时状态机已经在走了。
        private static int DiePrefixIsMounted(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot, ClampsRel);
            if (src.Length == 0)
            {
                Console.WriteLine("    ✗ ① 找不到 " + ClampsRel);
                return 1;
            }
            var attr = Regex.Match(src,
                @"\[GamePatch\(\s*typeof\(\s*scrPlayer\s*\)\s*,\s*""Die""\s*,"
                + @"\s*PatchKind\.Prefix\s*(?<gate>[^\]]*)\)\s*\]");
            if (!attr.Success)
            {
                Console.WriteLine("    ✗ ① scrPlayer.Die 上没有前缀屏障"
                    + "（应为 [GamePatch(typeof(scrPlayer), \"Die\", PatchKind.Prefix, ...)]）"
                    + "\n      noFail 挡不住 hitbox 死亡：Die 里那圈 if (!hitbox) 保护"
                    + "会被整段跳过 → 直接 ChangeState(States.Fail) + song.Stop()，"
                    + "成片在这一砖断掉且日志无异常");
                return 1;
            }
            // 功能门控必须是 playback：屏障只在真的有回放时挂载，
            // 没载入回放时连调用都不进，代价为 0。
            if (!Regex.IsMatch(attr.Groups["gate"].Value, @"""playback"""))
            {
                Console.WriteLine("    ✗ ① scrPlayer.Die 屏障的门控不是 \"playback\"："
                    + "「" + attr.Groups["gate"].Value.Trim() + "」"
                    + "\n      没有回放时它也会被调，每次真人死亡都过一次我们的代码");
                return 1;
            }
            // 方法名可以改，但必须紧跟特性、且是静态方法返回 bool。
            int after = attr.Index + attr.Length;
            int brace = src.IndexOf('{', after);
            int sig = src.LastIndexOf("static bool", brace < 0 ? src.Length : brace);
            if (sig < 0 || sig > after + 400)
            {
                Console.WriteLine("    ✗ ① scrPlayer.Die 屏障的方法体不在紧跟特性的位置");
                return 1;
            }
            Console.WriteLine("    ✓ ① scrPlayer.Die 前缀屏障在位，门控 playback");
            return 0;
        }

        // ── ② 判据方向对，且真的判在回放上 ───────────────────────
        //
        // 判据是一个字面量形状，不是"出现过 IsPlaying"：
        //   `if (!IsPlaying || !hitbox) return true;` … `return false;`
        // 两个坑各占一半：
        //   · `!hitbox` 漏了 → 普通死亡（玩家打错键）也被跳过，
        //     失败判定整个消失，录制出来的判定窗跟画面不是一回事；
        //   · `IsPlaying` 漏了 → 玩家自己玩的时候被我们的补丁挡掉死亡。
        // 所以两边都要在，且顺序（|| 无所谓，但都得在同一个 if 上）要可判。
        private static int GuardIsReplayOnlyAndCorrectlyOrdered(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot, ClampsRel);
            var attr = Regex.Match(src,
                @"\[GamePatch\(\s*typeof\(\s*scrPlayer\s*\)\s*,\s*""Die""\s*,"
                + @"\s*PatchKind\.Prefix");
            if (!attr.Success)
            {
                Console.WriteLine("    ✗ ② 找不到屏障（① 已经报了同样的事）");
                return 1;
            }
            int brace = src.IndexOf('{', attr.Index + attr.Length);
            int sig = src.LastIndexOf("static bool", brace);
            if (sig < 0)
            {
                Console.WriteLine("    ✗ ② 屏障的签名变了，得重新确认判据");
                return 1;
            }
            string body = ExtractBody(src, sig);
            var guard = Regex.Match(body, ReplayOnlyGuard);
            if (!guard.Success)
            {
                Console.WriteLine("    ✗ ② 屏障的首个判据不是 `"
                    + @"if (!PlaybackEngine.IsPlaying || !hitbox)" + "`"
                    + "\n      缺 IsPlaying：玩家正常游玩时死亡也会被我们挡掉；"
                    + "缺 !hitbox：打错键的正常失败判定被跳过，"
                    + "录出来的画面与判定对不上");
                return 1;
            }
            // 判据的 if 体就在 guard 的 `{` 之后：它第一句必须是 return true（放行）。
            int bodyStart = guard.Index + guard.Length;
            var ret = Regex.Match(body.Substring(bodyStart),
                @"^\s*(return\s+(true|false)\s*;)\s*\}");
            if (!ret.Success)
            {
                Console.WriteLine("    ✗ ② 判据后面没有 `return true;`（放行）"
                    + "：非回放或非 hitbox 的死亡会整个被跳过");
                return 1;
            }
            if (ret.Groups[2].Value != "true")
            {
                Console.WriteLine("    ✗ ② 判据分支返回的是 return " + ret.Groups[2].Value
                    + "，应为 true（放行）：`!IsPlaying || !hitbox` 成立时"
                    + "必须让游戏照常走 Die");
                return 1;
            }
            // 方法体剩下的部分必须以 return false 收尾——
            // 挡下来之后不给任何返回值，Die 整个不执行。
            string tail = body.Substring(bodyStart + ret.Length);
            var tailRet = Regex.Match(tail, @"return\s+(true|false)\s*;");
            if (!tailRet.Success || tailRet.Groups[1].Value != "false")
            {
                Console.WriteLine("    ✗ ② 挡下之后没有 `return false`："
                    + "只挡了副作用却让 Die 跑完，ChangeState(States.Fail) 照走，"
                    + "成片还是断在这一砖");
                return 1;
            }
            Console.WriteLine("    ✓ ② 判据 = `!IsPlaying || !hitbox` 放行，其余整段 return false");
            return 0;
        }

        // ── ③ 这个文件里的每一处 return false 都必须有收窄的门 ────
        //
        // 这个文件里有两处会跳过原方法的前缀，各有各的理由：
        //   · `DiePrefix` —— 只在回放期且 hitbox 时跳（偏差 24，本条守的）；
        //   · `MarkFailPrefix` —— 只在 `IgnoreMarkFail` 为真时跳，
        //     而那个标志由 `HitStateReplayer.ReplayHit` 在复现 miss 时
        //     经 `MarkFailScope` 开合。
        //
        // 前缀返回 false = 跳过整个原方法。多一处、或者一处的门写松了，
        // 现象都是"某样东西在不该消失的时候整个消失"，且不报错。
        // 所以判据是**每一处都归到一道收窄的门下**，而不是"只允许一处"
        // （后者会把 MarkFailPrefix 误判成违规——写断言时自己踩过一次）。
        private static int EveryFalseReturnIsNarrowed(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot, ClampsRel);
            var offenders = new List<string>();
            foreach (Match m in Regex.Matches(src, @"return\s+false\s*;"))
            {
                var sig = EnclosingMethod(src, m.Index);
                if (sig.Name.Length == 0)
                {
                    offenders.Add("第 " + (m.Index + 1) + " 行 return false 找不到所属方法");
                    continue;
                }
                string body = ExtractBody(src, sig.Sig);
                int falseAt = body.IndexOf("return false", StringComparison.Ordinal);
                if (falseAt < 0) falseAt = 0;
                // 两道门都是**先放行、再挡下**的形状：
                //     if (…IsPlaying…/…IgnoreMarkFail…) { return true; }  ← 门
                //     …（日志等副作用）…
                //     return false;                                        ← 跳过原方法
                // 条件本身可以是"坏条件放行"（DiePrefix 的 `!IsPlaying || !hitbox`）
                // 也可以是"好条件放行"（MarkFailPrefix 的 `!IgnoreMarkFail`）。
                // 两种都认——写死其中一种就会把另一种误判成违规（已踩过），
                // 但条件里必须出现这两个门名之一，这才是"收窄"。
                var gate = Regex.Match(body.Substring(0, falseAt),
                    @"if\s*\([^)]*(PlaybackEngine\.IsPlaying|IgnoreMarkFail)[^)]*\)"
                    + @"\s*\{\s*return\s+true\s*;\s*\}");
                if (gate.Success) continue;
                int line = src.LastIndexOf('\n', m.Index) + 1;
                offenders.Add(sig.Name + "()（第 " + line + " 行）"
                    + " return false 之前没有收窄的门"
                    + "（`if (…IsPlaying…/…IgnoreMarkFail…) { return true; }`）");
            }
            if (offenders.Count > 0)
            {
                Console.WriteLine("    ✗ ③ 有 return false 没有收窄的门：" +
                    string.Join("；", offenders)
                    + "\n      前缀返回 false = 跳过整个原方法。"
                    + "两道门只认这两种形状：`if (…IsPlaying…/…IgnoreMarkFail…) { return true; }`"
                    + "写在 return false 之前——"
                    + "别的条件收不住自己，症状是某样东西在不该消失时整个消失");
                return 1;
            }
            Console.WriteLine("    ✓ ③ 两处 return false 各自收在 IsPlaying / IgnoreMarkFail 门下");
            return 0;
        }

        // ── ④ IgnoreMarkFail 必须被 finally 还原 ─────────────────
        //
        // `MarkFailScope` 的注释写明了为什么用 try/finally：`MarkFail` 之后
        // 还有 `BlinkForSeconds`，那一行抛异常（协程启动失败等）时标志会
        // **永久留在 true**——之后玩家整局游玩都看不到 miss 指示器，
        // 而且完全无从察觉。
        //
        // 这条与偏差 24 的屏障共享同一个标志，标志卡在 true 的后果
        // 现在多了一条：miss 指示器在成片里也不出现。
        private static int IgnoreMarkFailIsRestored(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot, ClampsRel);
            var def = Regex.Match(src,
                @"internal static void MarkFailScope\(bool ignore, Action body\)");
            if (!def.Success)
            {
                Console.WriteLine("    ✗ ④ 找不到 MarkFailScope 的定义"
                    + "：IgnoreMarkFail 就没人开合了");
                return 1;
            }
            string body = ExtractBody(src, def.Index);
            int fin = body.IndexOf("finally");
            if (fin < 0)
            {
                Console.WriteLine("    ✗ ④ MarkFailScope 没有 finally"
                    + "：MarkFail 之后的 BlinkForSeconds 一抛异常，"
                    + "IgnoreMarkFail 就永久留在 true——之后玩家整局都看不到"
                    + "miss 指示器，成片里也少一次红闪，且完全无从察觉");
                return 1;
            }
            string finBody = body.Substring(fin);
            if (!Regex.IsMatch(finBody, @"finally\s*\{[^}]*IgnoreMarkFail\s*=\s*backup"))
            {
                Console.WriteLine("    ✗ ④ finally 里没有把 IgnoreMarkFail 还原成 backup"
                    + "：标志卡在 true 就是 miss 指示器永久消失");
                return 1;
            }
            // 标志本体只能由 MarkFailScope 写。反过来（谁都能置 true）
            // 就等于"多一道能把它卡住的门"。
            var writers = Regex.Matches(src, @"IgnoreMarkFail\s*=");
            if (writers.Count != 2)
            {
                Console.WriteLine("    ✗ ④ IgnoreMarkFail 出现了 " + writers.Count
                    + " 处赋值，应为 2 处（MarkFailScope 里的存与还原）"
                    + "：多处赋值就意味着有一处不会还原");
                return 1;
            }
            Console.WriteLine("    ✓ ④ IgnoreMarkFail 只在 MarkFailScope 里开合，且 finally 还原");
            return 0;
        }

        // ── ⑤ 行星爆炸不许一起拦掉 ───────────────────────────────
        //
        // 录像必须复现。命中链路里 `scrPlayer.Hit` 会走到
        // `scrPlanet.Die`（scrPlanet.cs:1542-1550：dead = true;
        // hittable = false; planetRenderer.Explode(...)），
        // 那是"打中了这块砖"的画面，跟 scrPlayer.Die 不是同一条链
        // （全仓 planetarySystem.Die( 只有 scrController.cs:594
        // 和 scrPlayer.cs:381 两处）。
        //
        // 判据反过来写：**如果**有人顺手把 scrPlanet.Die 也拦了，
        // 画面上就永远少一块爆炸。这里扫全文件确认没有对
        // scrPlanet.Die 的 GamePatch。
        private static int PlanetExplosionIsNotBlocked(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot, ClampsRel);
            var hits = new List<string>();
            foreach (Match m in Regex.Matches(src,
                @"typeof\(\s*(scrPlanet|scrSpike)\s*\)\s*,\s*""Die"""))
            {
                hits.Add(m.Value);
            }
            if (hits.Count > 0)
            {
                Console.WriteLine("    ✗ ⑤ 行星爆炸也被拦了：" + string.Join(" / ", hits)
                    + "\n      scrPlanet.Die 是命中链路的画面（行星爆开），"
                    + "不是玩家死亡；拦掉它成片里就永远少一次爆炸");
                return 1;
            }
            Console.WriteLine("    ✓ ⑤ 只拦玩家死亡，行星爆炸照旧");
            return 0;
        }

        // ── ⑥ 屏障的目标在**真程序集**里存在，且形状对 ────────────
        //
        // 前面五条都是源码级断言：只能证明"我们写了屏障"，
        // 证明不了"屏障挂得上"。游戏改名或改签名时
        // `ResolveTarget` 返回 null → Failures 多一行"（目标不存在）"，
        // 而死亡屏障就静静地没了——现象与没写一模一样。
        //
        // 所以这里真的去反射 Assembly-CSharp.dll，并且核对
        // 第四个形参的**名字**必须是 `hitbox`：
        // 我们前缀绑定的是具名实参，名字对不上 Harmony 直接
        // 抛"参数未找到"，屏障挂不上。
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
            var player = game.GetType("scrPlayer", false);
            if (player == null)
            {
                Console.WriteLine("    ✗ ⑥ 真程序集里没有 scrPlayer");
                return 1;
            }
            MethodInfo die = null;
            foreach (MethodInfo m in player.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name == "Die" && m.GetParameters().Length == 4) { die = m; break; }
            }
            if (die == null)
            {
                Console.WriteLine("    ✗ ⑥ 真程序集里没有 4 参数的 scrPlayer.Die"
                    + "——死亡屏障挂不上，回放里的 hitbox 死亡照旧会打断成片");
                return 1;
            }
            var ps = die.GetParameters();
            var names = new List<string>();
            foreach (var p in ps) names.Add(p.Name + ":" + p.ParameterType.Name);
            if (ps[3].Name != "hitbox" || ps[3].ParameterType != typeof(bool))
            {
                Console.WriteLine("    ✗ ⑥ scrPlayer.Die 的第 4 个形参是 `"
                    + names[3] + "`，不是 `hitbox:Boolean`"
                    + "\n      我们前缀绑的是具名实参：名字对不上 Harmony 抛"
                    + "「参数未找到」，屏障挂不上（现象与没写完全一样）；"
                    + "类型变了 `|| !hitbox` 也编译不过");
                return 1;
            }
            Console.WriteLine("    ✓ ⑥ scrPlayer.Die/4 的第 4 参就叫 hitbox:Boolean → "
                + string.Join("，", names));
            return 0;
        }

        /// <summary>取 `private static ... X(...)` 的方法体（大括号配平）。</summary>
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

        private static bool IsControlKeyword(string name)
        {
            switch (name)
            {
                case "if":
                case "else":
                case "for":
                case "foreach":
                case "while":
                case "switch":
                case "catch":
                case "lock":
                case "using":
                case "fixed":
                    return true;
                default:
                    return false;
            }
        }

        private struct Enclosing
        {
            public string Name;
            public int Sig;
        }

        /// <summary>
        /// 找出 `at` 位置落在哪个方法里。
        ///
        /// 靠扫描全部大括号（跳过字符串/字符字面量与注释——`SrcUtil.Source`
        /// 已经剥过注释）维护嵌套深度：每遇到 `{` 就看它前面那段是不是
        /// 方法签名（`... 名字(...)`），是就压栈。
        ///
        /// 不能用 `LastIndexOf("static")` 那种偷懒写法：`return false` 在
        /// 嵌套的 if 里面，而 if 的 `{` 前面是 `if (...)`，不是方法签名——
        /// 偷懒能蒙对，但换个方法（签名跨行、有泛型、有属性）就悄悄指错。
        /// </summary>
        private static Enclosing EnclosingMethod(string src, int at)
        {
            var stack = new List<Enclosing>();
            bool inStr = false, inChar = false;
            int lineStart = 0;
            for (int i = 0; i < at && i < src.Length; i++)
            {
                char c = src[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                if (inChar) { if (c == '\\') i++; else if (c == '\'') inChar = false; continue; }
                if (c == '"') { inStr = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '\n') { lineStart = i + 1; continue; }
                if (c == '{')
                {
                    // 头部从上一个 `;` / `{` / `}` 之后开始；**换行不算**——
                    // 项目的代码是 Allman 风格（`{` 独占一行），
                    // 把换行当头界会让每个方法体的头部都是空的。
                    int head = 0;
                    for (int k = i - 1; k >= 0; k--)
                    {
                        if (src[k] == ';' || src[k] == '{' || src[k] == '}')
                        { head = k + 1; break; }
                    }
                    string header = src.Substring(head, i - head);
                    var m = Regex.Match(header,
                        @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<args>[^()]*)\)\s*$");
                    // `if (...)` / `for (...)` 也符合"名字 + 括号"的形状。不排掉的话
                    // `return false` 的"所属方法"会算成 if 块——而 if 块的
                    // ExtractBody 从 `{` 起算，判据就是它自己，
                    // 于是每一条都判成"没有收窄的门"。
                    if (m.Success && !IsControlKeyword(m.Groups["name"].Value))
                    {
                        stack.Add(new Enclosing
                        { Name = m.Groups["name"].Value, Sig = head });
                    }
                    else
                    {
                        stack.Add(new Enclosing { Name = "", Sig = -1 });
                    }
                    continue;
                }
            }
            // `return false` 通常写在**嵌套的 if 块**里，所以栈顶是 if，不是方法。
            // 真正要的是最内层的**方法**帧（Sig >= 0）——控制块已经用
            // Sig = -1 标记掉了，从栈顶往下找第一个能用的即可。
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                if (stack[i].Sig >= 0) return stack[i];
            }
            return new Enclosing { Name = "", Sig = -1 };
        }
    }
}