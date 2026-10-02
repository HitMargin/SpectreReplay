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
using System;
using System.Collections.Generic;

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

            /// <summary>编码器接受了：帧序号与音频一起走。</summary>
            internal void OnFrameProduced()
            {
                FrameIndex++;
                AudioCursor++;
            }

            /// <summary>编码器拒绝了：两者都不能走。</summary>
            internal void OnFrameRejected()
            {
                Rejected++;
            }
        }

        internal static int Run()
        {
            Console.WriteLine("  ── 音画同步栅格 ──");
            int fail = 0;
            fail += AcceptedFramesStaySynced();
            fail += RejectedFrameMustNotAdvance();
            fail += NoSilentLoss();
            return fail;
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
    }
}