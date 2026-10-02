// 离线渲染的时间钉扎（偏差 21）。
//
// ── 这一条守的是什么 ─────────────────────────────────────────
// 我们原先在 `scrConductor.Update` 的 Postfix 上改写 `conductor.dspTime`，
// 以为这样就把歌曲时间轴钉住了。实测**一帧都没钉住**，而且没有任何症状：
//
//   · `scrConductor.Update` 内部 :852 就把 songposition 算完了
//     `songposition_minusi = (dspTime - dspTimeSong - calibration_i) * pitch - addoffset;`
//     我们挂在 Postfix 上，等我们改 dspTime 时这个值早已定死；
//   · 下一帧 :779 `dspTime = AudioSettings.dspTime` 无条件覆盖回去。
//
// 画面照出，只是"离线"其实按真实时间在跑——掉帧时音画就错开，
// 而日志里什么都没有。这类"看起来在工作、其实没在工作"的缺陷，
// 只能靠断言把它钉死。
//
// ── 为什么必须读写两端都换 ───────────────────────────────────
// `songposition_minusi` 是属性：getter 读 `_songposition_minusi`（:184），
// setter 写它，而全仓库唯一写入点是 `Update` 内的 :852/:856。
//   · 只换 Get（Postfix）：内部字段仍是真实时间，下一次
//     :858 `deltaSongPos = new - old` 会变成"钉扎 − 真实"的巨大跳变；
//   · 只换 Set（Prefix）：所有读点仍拿真值，画面还是按真实时间走。
// 所以两端缺一不可，缺哪一端都退回"没钉住"。
//
// ── 值域：为什么不乘 pitch ────────────────────────────────────
// Spectre 的 `PinnedSongPosition = anchorSongPosition + clock.Time * pitch`
// 是乘 pitch 的（钉的是 dspTime 那条路上的世界时间）；
// 我们钉的是 `songposition_minusi` 本身，而谱面里每个砖的 `entryTime`
// 与它**同一域**（都已经是 pitch 缩放过的歌曲时间）。
// 若在这里再乘一次 pitch，pitch=1.5 的曲子每一帧都会比真实位置快 50%。
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class ClockPinCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 离线渲染时间钉扎 ──");
            int fail = 0;
            fail += AccessorIsPublicAndResolved(projectRoot);
            fail += BothEndsArePinned(projectRoot);
            fail += StaleDspTimeWriteIsGone(projectRoot);
            fail += PinnedValueStaysInSongDomain(projectRoot);
            fail += FinishTestUsesTheRealCopy(projectRoot);
            return fail;
        }

        /// <summary>
        /// 属性访问器必须能被具名特性实参写出来。
        ///
        /// 这条看着像编译器的活（CS0617 确实会报），但**报错信息完全指不到真因**：
        /// "Accessor 不是有效的命名特性参数"，读起来像"这个特性不支持具名参数"，
        /// 而真因只是访问级别。所以把判据写成断言，省掉一次排查。
        ///
        /// 另一半是 ResolveTarget 真的会用 Get/GetSet 取访问器——
        /// 少了这一步，补丁会挂到"同名方法"上去或者静默失败，
        /// 而日志里只留一行"（目标不存在）"。
        /// </summary>
        private static int AccessorIsPublicAndResolved(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot, "Patch/PatchLoader.cs");
            if (src.Length == 0)
            {
                Console.WriteLine("    ✗ ① 找不到 Patch/PatchLoader.cs");
                return 1;
            }
            // 必须 public：具名特性实参只能写 public 的可写字段
            bool publicField = Regex.IsMatch(src,
                @"public\s+PropertyAccessor\s+Accessor\s*;");
            if (!publicField)
            {
                Console.WriteLine("    ✗ ① GamePatchAttribute.Accessor 不是 public 字段"
                    + "（internal 会报 CS0617「不是有效的命名特性参数」，"
                    + "而那句话完全指不到访问级别这一层）");
                return 1;
            }
            bool enumHasThree = Regex.IsMatch(src,
                @"enum\s+PropertyAccessor\s*\{[^}]*\bNone\b[^}]*\bGet\b[^}]*\bSet\b[^}]*\}",
                RegexOptions.Singleline);
            if (!enumHasThree)
            {
                Console.WriteLine("    ✗ ① PropertyAccessor 缺成员（应为 None/Get/Set）："
                    + "缺 None 就无法表达「按普通方法处理」，"
                    + "所有补丁都会被当成属性补丁去反射");
                return 1;
            }
            bool resolved = src.Contains("a.Target.GetProperties(flags)")
                && src.Contains("p.GetGetMethod(true)")
                && src.Contains("p.GetSetMethod(true)");
            if (!resolved)
            {
                Console.WriteLine("    ✗ ① ResolveTarget 没有把属性名翻译成访问器："
                    + "补丁会挂不上，日志只留一行「（目标不存在）」");
                return 1;
            }
            Console.WriteLine("    ✓ ① 属性访问器可被具名实参写出，"
                + "ResolveTarget 走 GetProperties + Get/GetSetMethod");
            return 0;
        }

        /// <summary>
        /// songposition_minusi 的读写两端都必须被接管。
        ///
        /// 判据落在**属性实参与形参**上，而不是"代码里出现过 Get"：
        /// Get 侧必须是 Postfix + `ref double __result`，
        /// Set 侧必须是 Prefix + `ref double value`——
        /// 挂错一侧时，Harmony 会在应用补丁时报参数不匹配，
        /// 失败被 MAX_HARMS 吃掉之后补丁被永久跳过（仍无用户可见症状）。
        ///
        /// 另外 Set 侧必须在替换 value **之前**抄下真实值：
        /// 结束判据要靠它（见 ⑤），抄晚了就只能读到钉扎值。
        /// </summary>
        private static int BothEndsArePinned(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot,
                "Features/VideoRender/OfflineSession.cs");
            if (src.Length == 0)
            {
                Console.WriteLine("    ✗ ② 找不到 OfflineSession.cs");
                return 1;
            }
            int getAt = src.IndexOf("\"songposition_minusi\"");
            if (getAt < 0)
            {
                Console.WriteLine("    ✗ ② 没有补丁挂在 songposition_minusi 上"
                    + "（钉 dspTime 是无效的：:852 先算完、下一帧 :779 又覆盖回去）");
                return 1;
            }
            // Get 侧：PatchKind.Postfix + Accessor=Get + ref double __result
            int getOk = FindAccessorPatch(src, getAt, "Get", "PatchKind.Postfix",
                @"ref\s+double\s+__result");
            // Set 侧：PatchKind.Prefix + Accessor=Set + ref double value
            int setAt = src.IndexOf("\"songposition_minusi\"", getAt + 1);
            int setOk = setAt < 0 ? -1
                : FindAccessorPatch(src, setAt, "Set", "PatchKind.Prefix",
                    @"ref\s+double\s+value");
            if (getOk < 0)
            {
                Console.WriteLine("    ✗ ② 读端没被正确接管"
                    + "（应为 Postfix + Accessor=Get + ref double __result）"
                    + "：所有读点仍拿真实歌曲位置，画面照旧按真实时间走");
                return 1;
            }
            if (setOk < 0)
            {
                Console.WriteLine("    ✗ ② 写端没被正确接管"
                    + "（应为 Prefix + Accessor=Set + ref double value）"
                    + "：:852 每次都把钉扎值覆盖回真实值，"
                    + "且 :858 的 deltaSongPos 变成「钉扎 − 真实」的巨大跳变");
                return 1;
            }
            int copy = src.IndexOf("LastRealSongPosition = value", setOk);
            int replace = src.IndexOf("value = OfflineSession.PinnedSongPosition", setOk);
            if (copy < 0 || replace < 0 || copy > replace)
            {
                Console.WriteLine("    ✗ ② 写端没有在替换 value **之前**抄下真实位置"
                    + "：渲染的结束判据问不到「歌放完没有」，收不了尾");
                return 1;
            }
            Console.WriteLine("    ✓ ② songposition_minusi 读写两端都接管"
                + "（Get=Postfix/__result，Set=Prefix/value，且先抄真值再替换）");
            return 0;
        }

        /// <summary>
        /// 找一处 `Accessor = PropertyAccessor.X` 且 kind 对得上、形参对得上的补丁。
        /// 返回该补丁方法体的起始偏移，找不到返回 -1。
        /// </summary>
        private static int FindAccessorPatch(string src, int attrAt, string accessor,
            string kind, string paramPattern)
        {
            // 从特性实参里反查：Accessor 出现的位置必须在 kind 之前或之后不远
            int acc = src.IndexOf("Accessor = PropertyAccessor." + accessor, attrAt);
            if (acc < 0 || acc - attrAt > 400)
            {
                return -1;
            }
            // 取这个补丁的方法体：属性实参之后第一个 "private static void Name("
            int sig = src.IndexOf("private static void", acc);
            if (sig < 0 || sig - acc > 400)
            {
                return -1;
            }
            int brace = src.IndexOf('{', sig);
            if (brace < 0)
            {
                return -1;
            }
            // kind 与形参都要落在同一个补丁方法里（往回看到特性起点之前）
            string head = src.Substring(Math.Max(0, acc - 400), Math.Min(400, acc));
            string body = src.Substring(sig, Math.Min(600, src.Length - sig));
            if (!head.Contains(kind) || !Regex.IsMatch(body, paramPattern))
            {
                return -1;
            }
            return brace;
        }

        /// <summary>
        /// 不许再有 `dspTime = ` 这类赋值。
        ///
        /// 这条是**反面判据**：把 ② 的正面判据反过来查，
        /// 用来堵"改 dspTime 与改 songposition 同时留着"的情况——
        /// 那种代码能编译、能跑，钉扎也确实生效了，但没人说得清
        /// 到底是谁在起作用；下一次游戏版本把 :779 挪个位置，
        /// 就会变成"一半生效一半不生效"，比完全没钉还难查。
        ///
        /// 注释里解释"为什么不改 dspTime"的那段会被 StripComments 先剥掉，
        /// 所以这里看到的都是真代码。
        /// </summary>
        private static int StaleDspTimeWriteIsGone(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot,
                "Features/VideoRender/OfflineSession.cs");
            var hits = Regex.Matches(src,
                @"(?:conductor|c|instance)\s*\.\s*dspTime\s*=");
            if (hits.Count > 0)
            {
                int at = src.LastIndexOf('\n', hits[0].Index) + 1;
                string line = src.Substring(at, src.IndexOf('\n', at) - at).Trim();
                Console.WriteLine("    ✗ ③ 仍有对 dspTime 的赋值：" + line
                    + "——它在 :852 之后生效、且下一帧被 :779 覆盖，"
                    + "留着只会让「到底哪一处在钉」说不清");
                return 1;
            }
            Console.WriteLine("    ✓ ③ 没有残留的 dspTime 赋值（钉点唯一）");
            return 0;
        }

        /// <summary>
        /// 钉扎值必须留在**歌曲时间域**，不乘 pitch。
        ///
        /// `songposition_minusi` 与谱面里每个砖的 `entryTime` 同一域
        /// （都已经按 pitch 缩放过）。Spectre 那条
        /// `anchorSongPosition + clock.Time * pitch` 乘 pitch，
        /// 因为它钉的是 dspTime 路上的世界时间，锚点也是从
        /// `(AudioSettings.dspTime - dspTimeSong - calibration_i) * pitch - addoffset`
        /// 换算来的——**把那边的公式搬到这边就会多乘一次**。
        ///
        /// pitch=1.5 的曲子会每一帧都快 50%，pitch≠1 的曲子全中，
        /// 而画面看上去"基本对得上"，最难被发现。
        /// </summary>
        private static int PinnedValueStaysInSongDomain(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot,
                "Features/VideoRender/OfflineSession.cs");
            var assign = Regex.Matches(src,
                @"PinnedSongPosition\s*=\s*([^;]+);");
            if (assign.Count == 0)
            {
                Console.WriteLine("    ✗ ④ 找不到 PinnedSongPosition 的赋值"
                    + "（钉扎值不更新 = 从第一帧起一直停在 0）");
                return 1;
            }
            foreach (Match m in assign)
            {
                string rhs = m.Groups[1].Value.Trim();
                if (rhs.Contains("pitch"))
                {
                    Console.WriteLine("    ✗ ④ 钉扎值乘了 pitch：" + rhs
                        + "——songposition_minusi 与砖的 entryTime 同一域，"
                        + "再乘一次会让 pitch≠1 的曲子整体偏快/偏慢"
                        + "（看着基本对得上，最难发现）");
                    return 1;
                }
            }
            if (!Regex.IsMatch(src,
                @"PinnedSongPosition\s*=\s*_frameIndex\s*/\s*\(\s*double\s*\)\s*_fps"))
            {
                Console.WriteLine("    ✗ ④ 钉扎值不是「第 i 帧恒为 i/fps 秒」："
                    + "改坏了会让离线渲染的时间轴与帧栅格不同步，"
                    + "而画面仍照出");
                return 1;
            }
            Console.WriteLine("    ✓ ④ 钉扎值 = i/fps，不乘 pitch"
                + "（与砖的 entryTime 同一域）");
            return 0;
        }

        /// <summary>
        /// 结束判据必须读**抄下来的真实位置**，不能读 songposition_minusi。
        ///
        /// 读端已被钉住接管之后，从外面读到的永远是钉扎值
        /// （i/fps，单调递增、不可能小于 -0.5），于是 `pos < -0.5`
        /// 永远为假 → 歌曲早就放完了渲染还在跑，
        /// 直到帧数耗尽才收尾（多出一大段静止画面）。
        ///
        /// 另外初值必须是 NaN：判据要能区分"还没收到"和"位置是 0"。
        /// 给 0 会在第一帧就判成已结束（成片只有第一帧），
        /// 给 -1000 同样（更隐蔽，表现为"立刻结束"）。
        /// </summary>
        private static int FinishTestUsesTheRealCopy(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot,
                "Features/VideoRender/OfflineSession.cs");
            var read = Regex.Matches(src,
                @"(\w+)\s*=\s*(?:scrConductor\.instance\s*\.\s*)?songposition_minusi");
            foreach (Match m in read)
            {
                // 只有 Update() 里那个 pos 局部变量是被当成结束判据用的
                if (m.Groups[1].Value == "pos")
                {
                    Console.WriteLine("    ✗ ⑤ 结束判据读的是被钉住接管后的 songposition_minusi"
                        + "：读出来永远是 i/fps，`pos < -0.5` 永远为假，"
                        + "歌曲放完后渲染还会一直跑到帧数耗尽");
                    return 1;
                }
            }
            if (!src.Contains("OfflineSession.LastRealSongPosition"))
            {
                Console.WriteLine("    ✗ ⑤ 没有用 LastRealSongPosition 判结束");
                return 1;
            }
            bool nanGuard = Regex.IsMatch(src, @"!double\.IsNaN\([^)]+\)\s*&&");
            bool nanInit = Regex.IsMatch(src,
                @"LastRealSongPosition\s*=\s*double\.NaN\s*;");
            if (!nanGuard || !nanInit)
            {
                Console.WriteLine("    ✗ ⑤ 结束判据没兜住「还没收到过真实位置」"
                    + "(判据侧 NaN 保护:" + (nanGuard ? "有" : "缺")
                    + "，初值 NaN:" + (nanInit ? "有" : "缺")
                    + ")：初值给 0 或负数会在第一帧就判成已结束，成片只剩一帧");
                return 1;
            }
            Console.WriteLine("    ✓ ⑤ 结束判据读抄下来的真实位置（NaN = 还没收到）");
            return 0;
        }
    }
}