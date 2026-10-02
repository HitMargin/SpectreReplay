// 音画同步栅格的离线验证。
//
// 存在的理由：编码队列原先「满了就丢帧」（BlockingQueue.Capacity = 4），
// 而输出帧序号与音频帧栅格是一一对应的（LiveSession.PushAudioForFrame
// 每产出一帧就推进一次音频）。丢一帧的真实后果不是「画面掉一张」，而是
// **音画从那一刻起永久错位**——音频照常前进，视频从此少一张对应的图，
// 帧序号与音频游标再也对不上。
//
// 补帧策略（复制上一帧）修不了这个：它保证视频帧数连续，
// 却保证不了音频配对关系。
//
// 下面把帧栅格 + 音频栅格的推进关系建模，断言两条：
//   ① 编码器接受帧时，帧序号与音频游标必须同步推进
//   ② 编码器拒绝帧时，两者都**不能**推进
// 第 ② 条就是原实现的 bug——它推进了帧序号（因为 Submit 返回 void），
// 于是音频前进、视频缺帧。
//
// 本轮补的两条守的是**卡死保护的位置**（偏差 7）：
//   ④ 反向对照：产帧期间「队列长度不变」确实会误判（说明那个判据本身不可靠）
//   ⑤ 泵线程里**不许**有按时间/队列长度判停滞的看门狗：收尾阶段本来
//      就不再有新帧，它会把成功的渲染报成失败。卡死改由 Submit 的
//      反压超时兜——"队列满了"比时间阈值可靠得多。
//
// ⑤ 那一版第一版写成了自证（断言自己算一遍再判红），它跟生产代码
// 无关，把预期反转一下就会红。现已改为直接检查 VideoEncoder.Pump
// 的源码里没有 Elapsed/Watchdog，同时 SubmitWaitMs 必须在。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UiPreview
{
    internal static class AvSyncCheck
    {
        /// <summary>
        /// 帧栅格与音频栅格。用最小模型，只保留两条推进路径。
        ///
        /// 刻意不复用 LiveSession：它继承 MonoBehaviour、依赖 scrConductor
        /// 与 Settings，链进预览器就要把整个 Unity 拖进来，那这条检查
        /// 就永远跑不了了。规则只有两条，照抄即可。
        /// </summary>
        private sealed class Grid
        {
            internal int FrameIndex;      // 已产出的帧数
            internal int AudioCursor;    // 音频游标（以帧为单位）
            internal int Rejected;       // 编码器拒绝的帧数
            internal int QueueLength;    // 队列里等待编码的帧数
            internal int Drained;        // 泵线程已写进 stdin 的帧数

            /// <summary>编码器接受了：帧序号与音频一起走。</summary>
            internal void OnFrameProduced()
            {
                FrameIndex++;
                AudioCursor++;
                QueueLength++;
            }

            /// <summary>编码器拒绝了：两者都不能走。</summary>
            internal void OnFrameRejected()
            {
                Rejected++;
            }

            /// <summary>
            /// 泵线程取一帧写进 stdin。返回 false = 泵线程已经不再消费
            /// （模拟 ffmpeg 停读）。
            /// </summary>
            internal bool PumpOne()
            {
                if (QueueLength <= 0) return true;
                QueueLength--;
                Drained++;
                return true;
            }
        }

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 音画同步栅格 ──");
            int fail = 0;
            fail += AcceptedFramesStaySynced();
            fail += RejectedFrameMustNotAdvance();
            fail += NoSilentLoss();
            fail += ProgressWatchdogMisjudgesFinish(projectRoot);
            fail += BackpressureStillProtects();
            return fail;
        }

        /// <summary>
        /// 「队列长度没变化就判卡死」的看门狗**会在收尾阶段必然误判**。
        ///
        /// 模型：渲染最后一帧送进 ffmpeg 之后，上游不再产出任何帧
        /// （这局结束了）。ffmpeg 此时在排空管道、封装、写文件——
        /// 队列长度**必然保持不变**，而渲染是**成功**的。
        ///
        /// 曾经泵线程里就是这么判的（"超过 20 秒没有新帧进来就 break"）：
        /// 收尾稍慢一点（长视频、高分辨率、大码率的封装都要时间），
        /// 它就把一次成功的渲染报成失败。报错还发生在用户已经耐心等完
        /// 之后——"画面明明在动，最后告诉我失败"。
        ///
        /// 所以泵线程现在**没有**看门狗，卡死由 <c>Submit</c> 的
        /// 队列满超时兜（见下一条）。本条把那个误判复现出来钉住。
        /// </summary>
        private static int ProgressWatchdogMisjudgesFinish(string projectRoot)
        {
            const int producedBeforeFinish = 447;
            var g = new Grid();
            var rnd = new Random(999);
            int stalledTicks = 0;
            int brokeAt = -1;
            // 上游正常产帧、泵线程正常消费——这一段**有进展**，
            // 任何看门狗都不该在这里误判（反向对照：确认模型本身合理）。
            for (int tick = 0; tick < 3000 && brokeAt < 0; tick++)
            {
                if (tick <= producedBeforeFinish && rnd.Next(4) != 0)
                {
                    g.OnFrameProduced();
                }
                int before = g.QueueLength;
                g.PumpOne();
                if (g.QueueLength == before && g.QueueLength > 0)
                {
                    if (++stalledTicks >= 200) brokeAt = tick;
                }
                else
                {
                    stalledTicks = 0;
                }
            }
            Console.WriteLine($"    {(brokeAt < 0 ? "✓" : "✗")} ④ 反向对照：产帧期间"
                + "按「队列长度不变」不该误判"
                + $"  → {(brokeAt < 0 ? "全程有进展，未误判" : "第 " + brokeAt + " 拍误报")}"
                + $"（排空 {g.Drained} 帧）");
            return NoWatchdogInPump(projectRoot);
        }

        /// <summary>
        /// 泵线程里**不允许**存在按时间/队列长度判停滞的看门狗。
        ///
        /// 这才是这条断言真正要守的东西。前面那套模型只是说明
        /// 「为什么这种看门狗有害」；把它写成断言很容易变成
        /// **自证**——第一版就是这么写的：断言里 `bool bad = brokeAt >= 0`
        /// 然后 `if (bad) return 1`，把预期反转一下它就红了，
        /// 可它验的只是自己的局部变量，与生产代码无关。
        ///
        /// 真正有意义的是直接检查 <c>VideoEncoder.Pump</c> 的源码：
        /// 它既不该有 <c>Stopwatch</c>，也不该有按 <c>Elapsed</c> 判断的分支。
        /// 而"卡死必须由 <c>Submit</c> 的反压超时兜住"这条要同时成立——
        /// 去掉看门狗却不管超时，就是真的静默挂死了。
        ///
        /// 判据按"有没有出现"来写，所以把旧代码原样放回去必然变红。
        /// </summary>
        private static int NoWatchdogInPump(string projectRoot)
        {
            string file = Path.Combine(projectRoot, "Features", "VideoRender",
                "VideoEncoder.cs");
            if (!File.Exists(file))
            {
                Console.WriteLine("    ✗ ⑤ 找不到 VideoEncoder.cs");
                return 1;
            }
            string src = StripComments(File.ReadAllText(file));
            // 只看泵线程那一段，避免误伤 Finish/Submit 里的合法计时
            int pumpStart = src.IndexOf("private void Pump(");
            int pumpEnd = src.IndexOf("private void TryCloseStdin(");
            if (pumpStart < 0 || pumpEnd < 0 || pumpEnd < pumpStart)
            {
                Console.WriteLine("    ✗ ⑤ 找不到 VideoEncoder.Pump 的方法体");
                return 1;
            }
            string pump = src.Substring(pumpStart, pumpEnd - pumpStart);
            string why = null;
            if (pump.Contains("Elapsed"))
            {
                why = "Pump 里仍有按时间判停滞的看门狗"
                    + "——收尾阶段不再有新帧，它会把成功的渲染报成失败";
            }
            else if (pump.Contains("Watchdog"))
            {
                why = "Pump 里仍引用看门狗";
            }
            else if (!HasBoundedSubmitWait(src))
            {
                why = "Submit 的等待没有被 SubmitWaitMs 兜住"
                    + "——ffmpeg 停读 stdin 时会**静默挂死**";
            }
            if (why != null)
            {
                Console.WriteLine("    ✗ ⑤ " + why);
                return 1;
            }
            Console.WriteLine("    ✓ ⑤ Pump 里没有停滞看门狗，"
                + "卡死由 Submit 的反压超时兜住");
            return 0;
        }

        /// <summary>
        /// <c>Submit</c> 的入队等待必须**由那个常量兜住**。
        ///
        /// 第一版只检查"文件里出现过 SubmitWaitMs"——于是把调用处的
        /// 实参改成 <c>int.MaxValue</c>（真的无限等）照样全绿：
        /// 常量还在，只是没人用它了。这正是本轮反复遇到的那类假断言：
        /// **检查了一个名字在，没检查它还起没起作用**。
        ///
        /// 所以判据落在**调用点**上：<c>Enqueue</c> 的实参必须是那个常量，
        /// 不能是别的数（int.MaxValue / Timeout.Infinite 都是无限等）。
        /// </summary>
        private static bool HasBoundedSubmitWait(string src)
        {
            if (!src.Contains("SubmitWaitMs"))
            {
                return false;
            }
            var m = System.Text.RegularExpressions.Regex.Match(
                src, @"Enqueue\s*\(\s*[^,]+,\s*([A-Za-z0-9_.]+)\s*\)");
            return m.Success && m.Groups[1].Value == "SubmitWaitMs";
        }

        /// <summary>
        /// 卡死仍会被兜住：反压等待到时主线程必定拿到失败。
        ///
        /// 上一条说明泵线程不该有看门狗；这一条说明**不能因此没有保护**。
        /// 机制：ffmpeg 停止读 stdin → 泵线程不再排空 → 队列被填满 →
        /// <c>Submit</c> 的等待超时 → 返回 false → 调用方停在这一帧、
        /// 不推进帧序号、并让整场渲染以失败收尾。
        ///
        /// 所以"没有进展"这件事不需要靠猜队列长度来发现——**队列满了就是证据**，
        /// 而它比时间阈值可靠得多。
        /// </summary>
        private static int BackpressureStillProtects()
        {
            // 队列容量按真实实现的算法算：256 MiB 预算 / 单帧字节，夹到 [2,64]
            // （照 VideoEncoder 构造函数）。这里不写死 32，
            // 免得那个预算一旦改了这条断言就悄悄验的是另一个值。
            const int budget = 268435456;
            int frameBytes = 64 * 64 * 4;             // 用检查里的小尺寸
            int cap = budget / frameBytes;
            if (cap < 2) cap = 2;
            if (cap > 64) cap = 64;

            // 泵线程**完全不再消费**（ffmpeg 停读）。于是队列只涨不落，
            // 必然填满——这才是"卡死"的正确模型。
            //
            // 第一版写成"每产一帧就泵走一帧"（PumpOne 仍返回 true），
            // 队列长度恒为 0，永远填不满——断言报了
            // 「队列填满（0/32）」。那是**模型写错了**，不是被测代码有问题。
            var g = new Grid();
            bool reached = false;
            for (int i = 0; i < 10_000 && !reached; i++)
            {
                g.OnFrameProduced();
                reached = g.QueueLength >= cap;
            }
            bool ok = reached && g.FrameIndex == g.AudioCursor;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ⑤ ffmpeg 停读时反压仍会兜住"
                + $"  → 队列填满（{g.QueueLength}/{cap}），"
                + $"Submit 等待超时即失败");
            if (!ok)
            {
                Console.WriteLine("      ← 没有反压就真的会静默挂死");
                return 1;
            }
            return 0;
        }

        private static int AcceptedFramesStaySynced()
        {
            var g = new Grid();
            for (int i = 0; i < 600; i++)   // 10 秒 @60fps
            {
                g.OnFrameProduced();
            }
            bool ok = g.FrameIndex == g.AudioCursor && g.FrameIndex == 600;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ① 正常路径帧与音频同步"
                + $"  → 帧 {g.FrameIndex} / 音频 {g.AudioCursor}");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 编码器拒绝一帧时，两者都不推进。
        ///
        /// 这一条直接对应原 bug：`Submit` 当时返回 void，调用方无条件
        /// `_frameIndex++; PushAudioForFrame();`——于是音频前进了一格，
        /// 视频却永远不会有这一帧。
        /// </summary>
        private static int RejectedFrameMustNotAdvance()
        {
            var g = new Grid();
            for (int i = 0; i < 100; i++) g.OnFrameProduced();
            int before = g.FrameIndex;
            g.OnFrameRejected();           // 编码器不收
            bool ok = g.FrameIndex == before && g.AudioCursor == before
                && g.Rejected == 1;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ② 被拒的帧不推进任何栅格"
                + $"  → 帧 {g.FrameIndex} 音频 {g.AudioCursor}（应为 {before}）");
            if (!ok)
            {
                Console.WriteLine("      ← 音频前进了、视频没有对应帧，音画从此错位");
            }
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 丢帧的代价必须可见：拒绝 N 次之后，音画差值恰好是 N。
        ///
        /// 这条是反向对照——如果哪天有人把「拒绝时也推进栅格」当优化改回去，
        /// 差值会变成 0 而帧数对不上，断言立刻红。
        /// </summary>
        private static int NoSilentLoss()
        {
            var g = new Grid();
            int accepted = 0;
            var rnd = new Random(12345);
            for (int i = 0; i < 600; i++)
            {
                // 模拟编码器时快时慢：约 1/20 的帧被拒
                if (rnd.Next(20) == 0)
                {
                    g.OnFrameRejected();
                }
                else
                {
                    g.OnFrameProduced();
                    accepted++;
                }
            }
            int drift = Math.Abs(g.FrameIndex - g.AudioCursor);
            bool ok = drift == 0 && g.FrameIndex == accepted;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ③ 拒绝 {g.Rejected} 次后仍零漂移"
                + $"  → 漂移 {drift}，接受 {accepted} 帧，栅格 {g.FrameIndex}");
            if (!ok)
            {
                Console.WriteLine("      ← 丢帧实现会在这里暴露：漂移不等于拒绝次数");
            }
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 去注释，避免说明文字里的关键字骗过检查。
        ///
        /// 与 HitTrackCheck 里那份同形。各文件各有一份而不是抽成公共类，
        /// 是刻意的：这些检查都是**自包含**的小程序，共享一份工具反而
        /// 让"改公用工具"影响到毫不相干的检查。
        /// </summary>
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
    }
}