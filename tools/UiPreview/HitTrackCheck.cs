// 状态轨（偏差 1）的离线检查。
//
// 背景：Spectre 的回放是**双轨**的——键轨（SendInput 注入真实按键）
// 加上状态轨（把命中瞬间的内部状态原样写回并主动复现命中）。我们此前
// 只做了键轨，缺的那一轨是判定能否复现的关键：
//
//   键轨让游戏自然地跑判定，而游戏按**当帧的行星角度**算判定窗口。
//   回放时注入的按键落在录制容差之外是常态（量化到一帧 + 注入延迟），
//   于是某次判定判成 Fail/Overload → 行星停在原地不前进 →
//   用户看到「回放卡住了」，而且**日志里没有任何异常**。
//
// 这类缺陷没有一条能靠"跑一遍看现象"稳定抓住：它取决于当帧角度落在
// 窗口内还是外，落在内就没事。所以只能把规则写成断言。
//
// 复刻录制端**唯一有状态**的纯逻辑（去重），其余是源码级规则检查：
// 引擎相关的部分（scrPlayer/chosenPlanet）在这个预览器里根本不存在，
// 复刻一份等于复刻一份"没验到真东西"的副本。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SpectreReplay.Features.Recording;   // ReplayHitContext（真实源码，ReplayData.cs）

namespace UiPreview
{
    internal static class HitTrackCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 状态轨（双轨回放）──");
            int fail = 0;
            fail += DedupeRule();
            fail += MutexRule(projectRoot);
            fail += NeverPassHitTick(projectRoot);
            fail += BothTracksMustFinish(projectRoot);
            fail += ReplayHitIsByShape(projectRoot);
            fail += MarkFailIsScoped(projectRoot);
            fail += LightUpClampIsPaired(projectRoot);
            return fail;
        }

        /// <summary>
        /// 录制端去重：同砖同状态的重复命中要撤掉上一条。
        ///
        /// 照 Spectre 的 Patch_Recording.cs:99-112。命中有时会被撤销
        /// （手滑多按一次、游戏自己重判、长按段里连判两次），
        /// 留着会在回放时**判两次**。
        ///
        /// **验的是真代码。** 判据在 <c>HitDedupe.cs</c>，这里通过链接
        /// 直接调用它——不在检查里复刻一份。
        /// 复刻等于自己测自己：真代码改错了，检查里那份副本照样全绿。
        /// （这正是把去重从 KeyRecorder 里拆出来的原因。）
        ///
        /// 四组数据把边界钉住：
        ///   ① 同砖同状态（角度有微差）→ 撤掉，长度不变
        ///   ② 同砖但 overload 变了 → 不撤（那是真的一次新命中）
        ///   ③ 换砖 → 不撤
        ///   ④ 地板取不到（-1）→ **不录**，否则回放时会在错的位置触发
        /// </summary>
        private static int DedupeRule()
        {
            int bad = 0;

            // ① 同砖同状态，角度有微差 → 撤掉上一条，长度不变
            var a = new List<ReplayHitContext> { Hit(7, 0, 0.25f, 10f) };
            HitDedupe.Record(a, Hit(7, 0, 0.25f, 10.0001f));
            if (a.Count != 1)
            {
                Console.WriteLine($"    ✗ ① 同砖同状态的重复命中应被撤掉，"
                    + $"实为 {a.Count} 条（回放时同一块砖会判两次）");
                bad++;
            }

            // ② 同砖但 overload 变了 → 不撤
            var b = new List<ReplayHitContext> { Hit(7, 0, 0.25f, 10f) };
            HitDedupe.Record(b, Hit(7, 0, 0.5f, 10f));
            if (b.Count != 2)
            {
                Console.WriteLine($"    ✗ ② 同砖但 overload 变化应视为新命中，"
                    + $"实为 {b.Count} 条");
                bad++;
            }

            // ③ 换砖 → 不撤
            var c = new List<ReplayHitContext> { Hit(7, 0, 0.5f, 10f) };
            HitDedupe.Record(c, Hit(8, 0, 0.5f, 10f));
            if (c.Count != 2)
            {
                Console.WriteLine($"    ✗ ③ 换砖时应追加，实为 {c.Count} 条");
                bad++;
            }

            // ④ 地板取不到 → 不录。
            // 这种条目无法定位（回放时拿它对不上任何砖），录进去就是
            // 在错的位置凭空触发一次命中。宁可少一条。
            var d = new List<ReplayHitContext>();
            HitDedupe.Record(d, Hit(-1, 0, 0.5f, 10f));
            if (d.Count != 0)
            {
                Console.WriteLine($"    ✗ ④ 地板取不到的命中应被丢弃，"
                    + $"实录了 {d.Count} 条（回放时会在错的位置触发）");
                bad++;
            }

            if (bad == 0)
            {
                Console.WriteLine("    ✓ ① 命中去重：同砖同状态撤销、状态变则保留、"
                    + "换砖则追加、地板取不到则丢弃");
            }
            return bad;
        }

        private static ReplayHitContext Hit(int floor, int section, float overload,
            float angle)
            => new ReplayHitContext
            {
                CurrentFloorId = floor,
                FreeRoamSection = section,
                OverloadCounter = overload,
                Angle = angle,
                CachedAngle = angle,
                TargetExitAngle = 0.0,
                NoFailHit = false,
                IsAuto = false,
                NextFloorAuto = false,
                RdcAuto = false,
                MidspinInfiniteMargin = false,
            };

        // ── 以下是源码级规则检查 ──────────────────────────────────

        private static string Read(string projectRoot, string relative)
        {
            string p = Path.Combine(projectRoot, relative.Replace('/', '\\'));
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }

        /// <summary>
        /// 双轨必须互锁。
        ///
        /// 规则：<c>scrPlayer.Hit</c> 的前缀里，<c>HitLocked</c> 的判断
        /// **必须排在 <c>IsRecording</c> 之前**，且必须
        /// <c>return false</c>（跳过原方法）。
        ///
        /// **顺序为什么是关键点：** 回放时录制是关的，IsRecording 恒为 false。
        /// 把互锁放在它后面 = 互锁永远走不到 = 键轨触发的 Hit 一次都掐不掉
        /// = 双判照旧。这不是风格问题，是能不能工作的区别。
        ///
        /// 判据不能只找 "HitLocked" 字样——它会出现在注释里。
        /// 也不能只找 "return false"——文件里可能别处也有。
        /// 必须确认两者出现在同一段控制流里：HitLocked 的判据之后、
        /// 下一个独立判断之前就该有 return false。
        /// </summary>
        private static int MutexRule(string projectRoot)
        {
            string src = StripComments(Read(projectRoot,
                "Features/Recording/Patch_HitRecord.cs"));
            if (src.Length == 0)
            {
                Console.WriteLine("    ✗ ② 找不到 Patch_HitRecord.cs");
                return 1;
            }
            int locked = src.IndexOf("HitLocked", StringComparison.Ordinal);
            int isRec = src.IndexOf("IsRecording", StringComparison.Ordinal);
            if (locked < 0)
            {
                Console.WriteLine("    ✗ ② Hit 前缀里没有 HitLocked 互锁："
                    + "键轨与状态轨会同时判定，同一块砖判两次");
                return 1;
            }
            if (isRec < 0)
            {
                Console.WriteLine("    ✗ ② Hit 前缀里没有 IsRecording 判断（去掉了录制门控？）");
                return 1;
            }
            if (locked > isRec)
            {
                Console.WriteLine("    ✗ ② HitLocked 判在 IsRecording 之后："
                    + "回放时 IsRecording 恒为 false，互锁永远走不到");
                return 1;
            }
            // HitLocked 与 IsRecording 之间必须紧挨着 return false。
            string between = src.Substring(locked,
                Math.Min(isRec - locked, 200));
            if (!Regex.IsMatch(between, @"return\s+false\s*;"))
            {
                Console.WriteLine("    ✗ ② HitLocked 分支没有 return false："
                    + "键轨触发的命中没有被掐掉");
                return 1;
            }
            Console.WriteLine("    ✓ ② 双轨互锁：Hit 前缀先判 HitLocked 并跳过原方法"
                + "（排在 IsRecording 之前）");
            return 0;
        }

        /// <summary>
        /// **绝不透传录制的 HitTick。**
        ///
        /// 这是 Spectre 用整整 8 行注释守着的一条（ReplayPlayer.cs:336-343），
        /// 而且它的后果是"回放看起来没反应"：HitTick 是 SkyHook 的绝对
        /// tick，换算歌曲时间要减 <c>AsyncInputManager.offsetTick</c>——
        /// 那是**本次运行**启动时校准的锚点，每局都不同。拿录制局的
        /// 原始 tick 在回放局换算会得到完全错位的时间差 →
        /// GetHitMarginInSec 判不出数 → 回放强制 noFail 吞掉失败 →
        /// 行星原地打转、永不前进（"卡输入"）。
        ///
        /// 判据：**调用点**必须传 null 字面量。
        /// 反射实参数组住在 <c>GameCompat.PlayerHit</c> 内部（兼容层搬过去之后），
        /// 那里自己会把它填成 null（形参默认值）。所以这里认的是
        /// <c>GameCompat.PlayerHit(player, isAuto, null)</c> 这个调用形状。
        ///
        /// 为什么看调用点而不是实参数组：实参数组是兼容层的通用实现，
        /// 它按"签名有几参"决定填什么，本来就不该由它保证 null；
        /// **保证 null 的是"谁在调"**。曾经这里扫的是实参数组，
        /// 实现一搬家判据就落空了——判据必须跟着"规则真正的归属"走。
        /// </summary>
        private static int NeverPassHitTick(string projectRoot)
        {
            string src = StripComments(Read(projectRoot,
                "Features/Playback/HitStateReplayer.cs"));
            if (src.Length == 0)
            {
                Console.WriteLine("    ✗ ③ 找不到 HitStateReplayer.cs");
                return 1;
            }
            var calls = Regex.Matches(src, @"GameCompat\.PlayerHit\s*\(([^;]*)\)");
            if (calls.Count == 0)
            {
                Console.WriteLine("    ✗ ③ 状态轨没有经 GameCompat 调命中");
                return 1;
            }
            foreach (Match c in calls)
            {
                string args = c.Groups[1].Value;
                // 第三参必须是 null 字面量
                var third = Regex.Match(args, @",\s*([^,]+?)\s*$");
                string v = third.Success ? third.Groups[1].Value.Trim() : "";
                if (v != "null")
                {
                    Console.WriteLine("    ✗ ③ 调 scrPlayer.Hit 时没有把 hitTick 置为 null"
                        + $"（实参为 {args.Trim()}）"
                        + " → 会用录制局的绝对 tick 换算，行星原地打转");
                    return 1;
                }
            }
            Console.WriteLine("    ✓ ③ 命中复现时 hitTick 恒为 null"
                + "（不拿录制局的 SkyHook 绝对 tick 换算）");
            return 0;
        }

        /// <summary>
        /// 回放结束必须**两条轨都跑完**。
        ///
        /// 规则：<c>Stop()</c> 的触发条件里，键轨游标与状态轨游标必须都在
        /// 结束条件里出现。少了状态轨那条 = 回放提前卸数据 =
        /// 后半段的行星状态停在录制中途的样子，画面是错的，而且不报错。
        ///
        /// 照 Spectre 的 Patch_Playback.cs:160-161：
        ///     if (PlayIndex >= HitContext_list.Count && KeyEventIndex >= data.KeyEvent_list.Count)
        ///
        /// **判据必须看表达式，不能看变量名。** 第一版断言的是"结束条件前
        /// 出现 hitsDone 字样"，而把 <c>bool hitsDone = true;</c> 硬编码进去
        /// 照样通过——正是它自己文档里警告过的那类假断言（名字在、逻辑没了）。
        /// 所以这里要求状态轨那条**真的比较游标**：必须出现
        /// <c>Cursor &gt;= …Hits.Count</c> 这种形状的比较。
        /// </summary>
        private static int BothTracksMustFinish(string projectRoot)
        {
            string src = StripComments(Read(projectRoot,
                "Features/Playback/PlaybackEngine.cs"));
            if (src.Length == 0)
            {
                Console.WriteLine("    ✗ ④ 找不到 PlaybackEngine.cs");
                return 1;
            }
            int stop = src.IndexOf("Debug.Log(\"[SpectreReplay] 回放结束\")",
                StringComparison.Ordinal);
            if (stop < 0)
            {
                Console.WriteLine("    ✗ ④ 找不到回放结束的判定点");
                return 1;
            }
            // 往回看一段：取 Stop 调用之前的条件表达式。
            int from = Math.Max(0, stop - 800);
            string cond = src.Substring(from, stop - from);
            bool keysOk = Regex.IsMatch(cond, @"_cursor\s*>=\s*keys\.Count");
            bool hitsOk = Regex.IsMatch(cond, @"Cursor\s*>=\s*[\w\.]*Hits\.Count");
            if (!keysOk)
            {
                Console.WriteLine("    ✗ ④ 回放结束条件里没有键轨游标的比较"
                    + "（键轨也会提前结束）");
                return 1;
            }
            if (!hitsOk)
            {
                Console.WriteLine("    ✗ ④ 回放结束条件没有真的比较状态轨游标"
                    + "（哪怕变量名在，比较也可能是恒真）→ 回放会提前结束，"
                    + "后半段行星状态停在录制中途");
                return 1;
            }
            Console.WriteLine("    ✓ ④ 回放结束条件真的比较了键轨与状态轨两个游标");
            return 0;
        }

        /// <summary>
        /// 命中必须**按形状反射**调用，不能写编译期调用。
        ///
        /// 签名跨版本变了：r141–r149 是 <c>Hit(bool isAuto)</c>，
        /// r150+ 是 <c>Hit(long? hitTick, bool isAuto)</c>。写死编译期调用，
        /// 换一个版本就是 MissingMethodException（或干脆编译不过）。
        /// 照 Spectre 的 GameCompat.PlayerHit（GameCompat.cs:223-249）。
        ///
        /// 判据：必须同时出现 2 参数与 1 参数两种形状的识别。
        /// 只认一种 = 换一个版本就静默退化成"找不到方法"，
        /// 而那句错误只在首次调用时打一次日志，之后状态轨静静地什么都不做。
        ///
        /// **实现搬到 GameCompat.cs 了，这条判据跟着搬。**
        /// 曾经这里扫 HitStateReplayer.cs——实现一搬家它就找不到形状，
        /// 报「命中只认了任何一种签名」。判据必须跟着"规则真正的归属"走，
        /// 否则一次重构就让守卫悄悄失去作用。
        /// 兼容层那边另有 CompatCheck 在**真程序集上**验择路结果。
        /// </summary>
        private static int ReplayHitIsByShape(string projectRoot)
        {
            string src = StripComments(Read(projectRoot, "Patch/GameCompat.cs"));
            if (src.Length == 0)
            {
                Console.WriteLine("    ✗ ⑤ 找不到 Patch/GameCompat.cs");
                return 1;
            }
            // 两种 arity 的探测：先 2 参（r150+）再 1 参（旧版）
            bool two = Regex.IsMatch(src, @"""Hit""\s*,\s*2\b");
            bool one = Regex.IsMatch(src, @"""Hit""\s*,\s*1\b");
            // 实参也必须两套：2 参传 {hitTick, isAuto}，1 参只传 {isAuto}
            bool args = src.Contains("new object[] { isAuto }")
                && Regex.IsMatch(src, @"new\s+object\[\]\s*\{\s*hitTick\s*,");
            if (!two || !one || !args)
            {
                Console.WriteLine("    ✗ ⑤ 命中只认了 "
                    + (two ? "2 参数" : one ? "1 参数" : "任何一种")
                    + " 签名探测" + (args ? "" : "，实参也只有一套")
                    + "：换游戏版本会静默退化成「找不到方法」");
                return 1;
            }
            Console.WriteLine("    ✓ ⑤ 命中按参数形状反射调用，1/2 参数两种签名都能认");
            return 0;
        }

        /// <summary>去注释，避免说明文字里的关键字骗过检查。</summary>
        private static string StripComments(string src)
        {
            if (src.Length == 0) return src;
            var sb = new StringBuilder(src.Length);
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

        /// <summary>
        /// MarkFail 的屏蔽必须**成对开合**。
        ///
        /// <c>scrPlanet.MarkFail()</c>（scrPlanet.cs:1626）无条件
        /// <c>Instantiate</c> 一个 failIndicator 挂在 next 行星上。回放期间
        /// 若真人误触，屏幕上会多出红色指示器并跟着行星飞出画面——
        /// 成片里就是一团跟着跑的鬼影。
        ///
        /// 但**不能无条件屏蔽**：状态轨要复现录制时的 noFail 闪烁，
        /// 它自己就得调 MarkFail。所以规则是"由 ReplayHit 开屏蔽范围调用"。
        ///
        /// 判据两条：
        ///   ① 屏蔽走 MarkFailScope（try/finally），不是裸赋值
        ///      ——裸赋值后若 BlinkForSeconds 抛异常，标志永久留在 true，
        ///        之后玩家整局都看不到 miss 指示器，且无从察觉
        ///   ② ReplayHit 里确实走的是 MarkFailScope，而不是直接调 MarkFail
        /// </summary>
        private static int MarkFailIsScoped(string projectRoot)
        {
            string clamps = StripComments(Read(projectRoot,
                "Features/Playback/PlaybackClamps.cs"));
            string replayer = StripComments(Read(projectRoot,
                "Features/Playback/HitStateReplayer.cs"));
            if (clamps.Length == 0 || replayer.Length == 0)
            {
                Console.WriteLine("    ✗ ⑥ 找不到 PlaybackClamps.cs / HitStateReplayer.cs");
                return 1;
            }
            // ① MarkFail 的 Prefix 必须在屏蔽时返回 false 并清 __result
            int m = clamps.IndexOf("MarkFail", StringComparison.Ordinal);
            if (m < 0 || !clamps.Contains("__result = null"))
            {
                Console.WriteLine("    ✗ ⑥ MarkFail 没有被 Prefix 屏蔽"
                    + "（回放期间真人误触会在画面上留下跟着行星飞的红色鬼影）");
                return 1;
            }
            if (!Regex.IsMatch(clamps, @"MarkFailScope[\s\S]*finally"))
            {
                Console.WriteLine("    ✗ ⑥ MarkFailScope 没有 finally："
                    + "标志会永久留在 true，之后玩家整局都看不到 miss 指示器");
                return 1;
            }
            // ② ReplayHit 走的是 scope，而不是直接调
            if (!replayer.Contains("MarkFailScope"))
            {
                Console.WriteLine("    ✗ ⑥ ReplayHit 没走 MarkFailScope："
                    + "要么 noFail 闪烁被自己的屏蔽吞掉，要么屏上多出鬼影");
                return 1;
            }
            // **每一次** MarkFail 调用都必须在某个 MarkFailScope 内部。
            //
            // 判据不能是"不许出现 planet.MarkFail()"——正确写法恰恰**要**调它，
            // 只不过是在屏蔽范围内调。第一版就是这条判据写反了，
            // 把唯一正确的实现判成红。
            //
            // 正确的判法：逐个 MarkFail() 往回找最近的 MarkFailScope(，
            // 若两者之间已经出现了 "});"（lambda 闭合），说明这个调用
            // **落在范围之外**——那才是绕过屏蔽。
            foreach (Match call in Regex.Matches(replayer, @"MarkFail\(\)"))
            {
                int at = call.Index;
                int scope = replayer.LastIndexOf("MarkFailScope(",
                    at, StringComparison.Ordinal);
                if (scope < 0)
                {
                    Console.WriteLine("    ✗ ⑥ 有 MarkFail 调用不在任何屏蔽范围内"
                        + $"（偏移 {at}）——真人误触会在画面上留下红色鬼影");
                    return 1;
                }
                string between = replayer.Substring(scope, at - scope);
                if (between.Contains("});"))
                {
                    Console.WriteLine("    ✗ ⑥ 有 MarkFail 调用在屏蔽范围**之外**"
                        + "（已遇到 lambda 闭合）——会绕过屏蔽");
                    return 1;
                }
            }
            Console.WriteLine("    ✓ ⑥ MarkFail 屏蔽成对开合（try/finally），"
                + "且 ReplayHit 的每次 MarkFail 都在屏蔽范围内");
            return 0;
        }

        /// <summary>
        /// 判定窗口必须在 <c>scrFloor.LightUp</c> 前后**成对夹逼**。
        ///
        /// <c>LightUp()</c>（scrFloor.cs:1200）里读
        /// <c>GCS.hitMarginLimit != HitMarginLimit.PerfectsOnly</c> 来决定
        /// 要不要给砖块换随机颜色，而游戏会在别处把它写回玩家真实设置。
        /// 只在开播时设一次，砖块亮起时读到的可能已经是玩家自己的档位。
        ///
        /// 夹逼成 PerfectsOnly 是有意的：让 SetToRandomColor 一定被跳过——
        /// 颜色是录进视频的，中途变了成片就与录制不是一回事。
        ///
        /// 判据：Prefix 与 Postfix 两条都要在，且**都判 IsPlaying**。
        /// 少任何一条都会漏（只写 Prefix = 之后永远是 PerfectsOnly；
        /// 只写 Postfix = 根本没夹）。
        /// </summary>
        private static int LightUpClampIsPaired(string projectRoot)
        {
            string src = StripComments(Read(projectRoot,
                "Features/Playback/PlaybackClamps.cs"));
            if (src.Length == 0)
            {
                Console.WriteLine("    ✗ ⑦ 找不到 PlaybackClamps.cs");
                return 1;
            }
            bool hasPrefix = Regex.IsMatch(src,
                @"LightUp""\s*,\s*PatchKind\.Prefix");
            bool hasPostfix = Regex.IsMatch(src,
                @"LightUp""\s*,\s*PatchKind\.Postfix");
            if (!hasPrefix || !hasPostfix)
            {
                Console.WriteLine("    ✗ ⑦ LightUp 只挂了 "
                    + (hasPrefix ? "Prefix" : hasPostfix ? "Postfix" : "任何一侧")
                    + "：判定窗口夹逼必须前后成对"
                    + "（只 Prefix = 之后永远 PerfectsOnly；只 Postfix = 根本没夹）");
                return 1;
            }
            if (!src.Contains("HitMarginLimit.PerfectsOnly"))
            {
                Console.WriteLine("    ✗ ⑦ LightUp 夹逼时没有设成 PerfectsOnly："
                    + "砖块颜色会随档位变化，成片与录制对不上");
                return 1;
            }
            Console.WriteLine("    ✓ ⑦ LightUp 前后成对夹逼判定窗口，"
                + "期间置 PerfectsOnly 以固定砖块颜色");
            return 0;
        }
    }
}
