// 帧来源的请求/回调配对。
//
// 这里守的是两类**不报错也不停机**的缺陷：
//
// 甲、相机列表为空（切场景 / 编辑器 / 关卡加载中）或窗口尺寸为 0 时，
//     RequestCleanCamera / RequestWholeScreen 会提前返回，**没有排任何回读**。
//     调用方若据此认定"已排队，等回调"，那一帧就永远等不到回调：
//     画面停住、音频照走、帧栅格不动，而日志里什么都没有。
//
// 乙、整屏取帧那条路的回调是**同步**的（ReadPixels 当场算完）。
//     若"在飞"的置位写在发起请求之后，置位就落在回调清位之后 ——
//     再没有人清它，闸门永久卡死。第 2 帧起一帧都排不上：
//     Live 模式成片是**第一帧的静止画面**（时长与音轨都对，看着能用、
//     内容全错），离线模式只产出 1 帧。
//
// 甲是返回值取错，能靠结构断言守；乙是纯时序，源码里怎么看都像对的，
// 只能**真的按同步顺序跑一遍**。所以时序逻辑抽成了不依赖 UnityEngine 的
// ReadbackGate（链接真实源码），本文件的判据一半是行为、一半是接线：
// 行为验"闸门本身对不对"，接线验"取帧路真的走了这个闸门"——
// 少了任何一半，都能造出一个"闸门很完美但没人用 / 用了但锁死"的产品。
using System;
using System.IO;
using SpectreReplay.Features.VideoRender;

namespace UiPreview
{
    internal static class FrameSourceCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 帧来源请求配对 ──");
            int fail = 0;
            fail += SyncCallbackReleasesGate();
            fail += NotQueuedDoesNotLock();
            fail += AsyncHoldsGateUntilCallback();
            fail += ThrowingRequestReleasesGate();
            fail += GateIsWiredIn(projectRoot);
            return fail;
        }

        // ===== 行为：闸门本身（用真代码按真实时序跑） =====

        /// <summary>
        /// 同步回调之后闸门必须已经放开 —— 这条就是整屏模式的命门。
        ///
        /// 复刻真实时序：request 内部当场回调（gate.Complete），然后才返回 true。
        /// 原来的写法（先发请求、回来再按 queued 置位）在这里会留下 Pending=true，
        /// 于是"下一帧还能不能排上"立刻变成 false。
        /// </summary>
        private static int SyncCallbackReleasesGate()
        {
            var gate = new ReadbackGate();
            int delivered = 0;
            bool queued = gate.TryStart(() =>
            {
                delivered++;                  // 回调在 request() 还没返回时就跑完了
                gate.Complete();
                return true;
            });
            if (!queued)
            {
                Console.WriteLine("    ✗ ① 同步回调那次请求根本没排上 —— 第 1 帧就丢");
                return 1;
            }
            if (delivered != 1)
            {
                Console.WriteLine("    ✗ ① 同步回调没有被调用 —— 这一帧根本没交付");
                return 1;
            }
            if (gate.Pending)
            {
                Console.WriteLine("    ✗ ① 同步回调之后闸门还锁着（置位落在清位之后）："
                    + "第 2 帧起一帧都排不上。Live 成片是第一帧的静止画面"
                    + "（时长与音轨都对、看着能用、内容全错），离线只产出 1 帧，"
                    + "全程不报错、不停机");
                return 1;
            }
            // 顺序要紧：**先**判闸门是否已放开，**再**发起下一帧。
            // 反过来的话，下一帧自己会在飞，闸门当然是 pending ——
            // 断言会把"正常在飞"误判成"锁死"（本检查第一版就这么错过）。
            bool next = gate.TryStart(() => true);
            if (!next)
            {
                Console.WriteLine("    ✗ ① 同步回调之后下一帧排不上");
                return 1;
            }
            Console.WriteLine("    ✓ ① 同步完成的回调（整屏取帧）之后闸门已放开，"
                + "下一帧照常排得上");
            return 0;
        }

        /// <summary>
        /// "什么都没排上"时返回 false 且不锁闸门。
        /// 相机列表为空 / 窗口尺寸为 0 都走这条路。
        /// </summary>
        private static int NotQueuedDoesNotLock()
        {
            var gate = new ReadbackGate();
            bool queued = gate.TryStart(() => false);
            if (queued)
            {
                Console.WriteLine("    ✗ ② 请求回的是「没排上」，TryStart 却回了 true："
                    + "调用方会去等一个永远不会来的回调"
                    + "（画面停住、音频照走、日志全无）");
                return 1;
            }
            // 先判闸门、再发起下一帧（理由同 ①）
            if (gate.Pending)
            {
                Console.WriteLine("    ✗ ② 没排上之后闸门被锁死：下一帧同样排不上，"
                    + "录制永久停在那一帧");
                return 1;
            }
            bool next = gate.TryStart(() => true);
            if (!next)
            {
                Console.WriteLine("    ✗ ② 没排上之后下一帧排不上");
                return 1;
            }
            Console.WriteLine("    ✓ ② 没排上回读时返回 false 且不锁闸门");
            return 0;
        }

        /// <summary>
        /// 上一路没回来之前，第二路必须排不上 —— 这是帧序的**结构性**保证。
        ///
        /// 本模组不排队列，靠"并发为零"免掉乱序（GPU 完成顺序不保证提交顺序）。
        /// 回调到达后必须立刻恢复。
        /// </summary>
        private static int AsyncHoldsGateUntilCallback()
        {
            var gate = new ReadbackGate();
            int calls = 0;
            bool first = gate.TryStart(() => { calls++; return true; });
            bool second = gate.TryStart(() => { calls++; return true; });
            if (!first || second || calls != 1 || !gate.Pending)
            {
                Console.WriteLine("    ✗ ③ 上一路还没回来时又排了一路："
                    + "GPU 完成顺序不保证提交顺序，帧序会乱");
                return 1;
            }
            gate.Complete();
            bool third = gate.TryStart(() => { calls++; return true; });
            if (!third || calls != 2)
            {
                Console.WriteLine("    ✗ ③ 回调之后闸门没有放开，下一帧排不上");
                return 1;
            }
            Console.WriteLine("    ✓ ③ 一回在飞时排不上第二回（并发为零 → 帧序天然正确），"
                + "回调到达即恢复");
            return 0;
        }

        /// <summary>请求体抛异常：异常要上抛，闸门不能留在锁死状态。</summary>
        private static int ThrowingRequestReleasesGate()
        {
            var gate = new ReadbackGate();
            bool threw = false;
            try
            {
                gate.TryStart(() => { throw new InvalidOperationException("请求体抛了"); });
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            if (!threw)
            {
                Console.WriteLine("    ✗ ④ 请求体的异常被吞了：真正的错误会被藏起来");
                return 1;
            }
            // 先判闸门、再发起下一帧（理由同 ①）
            if (gate.Pending)
            {
                Console.WriteLine("    ✗ ④ 请求抛异常之后闸门留在锁死状态："
                    + "一次异常就永久停止出帧");
                return 1;
            }
            bool next = gate.TryStart(() => true);
            if (!next)
            {
                Console.WriteLine("    ✗ ④ 请求抛异常之后下一帧排不上");
                return 1;
            }
            Console.WriteLine("    ✓ ④ 请求体抛异常时异常上抛，且闸门不会留在锁死状态");
            return 0;
        }

        // ===== 接线：真实取帧路必须真的走这个闸门 =====

        private static int GateIsWiredIn(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot, "Features/VideoRender/FrameSource.cs");
            if (src.Length == 0)
            {
                Console.WriteLine("    ✗ ⑤ 找不到 FrameSource.cs");
                return 1;
            }
            int fail = 0;

            // 在飞状态只许住在闸门里。FrameSource 自己再留一个标记，
            // 就等于又有了第二份真相 —— 两份迟早不一致，
            // 而原来的 bug 正是这么来的（置位与清位分居两处）。
            if (!src.Contains("new ReadbackGate()"))
            {
                Console.WriteLine("    ✗ ⑤ FrameSource 没有持有 ReadbackGate："
                    + "闸门被测得再好，取帧路也没走它");
                fail++;
            }
            if (src.Contains("_readbackPending"))
            {
                Console.WriteLine("    ✗ ⑤ FrameSource 里又出现了自己的 pending 标记："
                    + "在飞状态有了第二份真相，迟早不一致");
                fail++;
            }

            string req = MethodBody(src, "internal bool Request()");
            if (req == null)
            {
                Console.WriteLine("    ✗ ⑤ 找不到 Request() 的方法体");
                fail++;
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(req, @"return\s+true\s*;"))
            {
                Console.WriteLine("    ✗ ⑤ Request() 里仍有无条件 return true："
                    + "相机列表为空 / 窗口尺寸为 0 那一帧会被当成「已排队」，"
                    + "调用方去等一个永远不会来的回调"
                    + "（画面停在黑帧、音频照走、日志全无）");
                fail++;
            }
            else if (!req.Contains("_gate.TryStart"))
            {
                Console.WriteLine("    ✗ ⑤ Request() 不经闸门："
                    + "这次到底排上了没有、能不能并发，都没人把关");
                fail++;
            }
            else if (!req.Contains("RequestWholeScreen")
                || !req.Contains("RequestCleanCamera"))
            {
                Console.WriteLine("    ✗ ⑤ Request() 没有把两条取帧路都交给闸门");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ⑤ Request() 的两条取帧路都经过闸门，"
                    + "返回值来自被调方，不再有无条件 return true");
            }

            // 两个回调都必须通知闸门 —— 少通知哪一条都是永久锁死，
            // 只是死的时机不同：少异步那条 = 一次回读之后就死；
            // 少同步那条 = 整屏模式第 2 帧就死。
            fail += CallbackCompletesGate(src, "private void OnReadback(",
                "异步回读回调 OnReadback");
            fail += CallbackCompletesGate(src, "private void OnWholeScreenReady(",
                "整屏取帧的同步回调 OnWholeScreenReady");
            return fail;
        }

        private static int CallbackCompletesGate(string src, string signature,
            string what)
        {
            string body = MethodBody(src, signature);
            if (body == null)
            {
                Console.WriteLine("    ✗ ⑥ 找不到 " + what + " 的方法体");
                return 1;
            }
            if (!body.Contains("_gate.Complete()"))
            {
                Console.WriteLine("    ✗ ⑥ " + what + " 里没有通知闸门："
                    + "这一次回读之后闸门再也不会放开，录制停在第 N 帧"
                    + "且不报错、不停机");
                return 1;
            }
            Console.WriteLine("    ✓ ⑥ " + what + " 会通知闸门放开");
            return 0;
        }

        /// <summary>取签名之后第一对花括号里的内容（够用，不解析 C#）。</summary>
        private static string MethodBody(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }
            int open = src.IndexOf('{', at);
            if (open < 0)
            {
                return null;
            }
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{')
                {
                    depth++;
                }
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return src.Substring(open, i - open + 1);
                    }
                }
            }
            return null;
        }
    }
}
