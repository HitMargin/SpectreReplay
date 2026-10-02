// 帧来源的请求/回调配对（偏差 6 复核时发现的一处真 bug）。
//
// 问题：`Request()` **无条件**返回 true，由调用方据此认定"已排队"，
// 而 `RequestCleanCamera` 开头有一条提前返回——游戏相机列表为空
// （切场景瞬间、切到编辑器、关卡加载中都会出现）就 `return`，
// **没有排任何回读**。
//
// 后果链条：那一帧永远不会有回调，而 `_readbackPending` 也没被置上，
// 所以下一帧 `Request()` 还能排上——整个录制**不会停**，但每一帧都在
// 等一个不会来的回调。表现是画面停在黑帧、音频照走、帧栅格不动，
// 成片是黑的，而日志里什么都没有。
//
// 这类"不报错也不停"的缺陷最难发现：一切都在正常推进，只有画面不对。
// 所以判据选在**配对关系**上，而不是"代码里有没有 return false"。
using System;
using System.IO;

namespace UiPreview
{
    internal static class FrameSourceCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 帧来源请求配对 ──");
            int fail = 0;
            fail += PendingOnlyWhenQueued(projectRoot);
            fail += InFlightIsAtMostOne(projectRoot);
            return fail;
        }

        /// <summary>
        /// `_readbackPending` 只能在**真的排上了回读**之后置位。
        ///
        /// 判据不是"文件里出现过 return false"——那证明不了配对关系。
        /// 真正要验的是：`Request()` 的返回值由**被调方的回传**决定，
        /// 而不是自己无条件写死 true。
        ///
        /// 因此这里查两处，且缺一不可：
        ///   ① `Request()` 里没有"先置 pending 再无条件 return true"；
        ///   ② 置位语句在**回传结果的条件里**（`if (queued)` 之类），
        ///      而 `queued` 来自两个 Request* 的返回值。
        ///
        /// 少任何一条都能造出同一个 bug：
        ///   - 只有 ①：不置 pending → 允许并发多路回读 → 乱序
        ///   - 只有 ②：pending 仍在 Request() 里无条件置 → 提前返回时卡死
        /// </summary>
        private static int PendingOnlyWhenQueued(string projectRoot)
        {
            string file = Path.Combine(projectRoot, "Features", "VideoRender",
                "FrameSource.cs");
            if (!File.Exists(file))
            {
                Console.WriteLine("    ✗ ① 找不到 FrameSource.cs");
                return 1;
            }
            string src = StripComments(File.ReadAllText(file));
            int start = src.IndexOf("internal bool Request()");
            int end = src.IndexOf("private bool RequestCleanCamera()");
            if (start < 0 || end < 0 || end < start)
            {
                Console.WriteLine("    ✗ ① 找不到 Request() 的方法体");
                return 1;
            }
            string body = src.Substring(start, end - start);

            // ① 不许有无条件 return true（那正是原 bug 的形状）
            if (System.Text.RegularExpressions.Regex.IsMatch(
                body, @"return\s+true\s*;"))
            {
                Console.WriteLine("    ✗ ① Request() 里仍有无条件 return true："
                    + "相机列表为空时它提前返回、根本没排回读，"
                    + "而调用方会等一个永远不会来的回调"
                    + "（画面停在黑帧、音频照走、无任何日志）");
                return 1;
            }

            // ② 置位必须在条件里，且条件来自被调方回传
            if (!body.Contains("_readbackPending = true"))
            {
                Console.WriteLine("    ✗ ① Request() 里不再置 _readbackPending："
                    + "会允许多路回读并发，帧序乱掉");
                return 1;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                body, @"if\s*\(\s*queued\s*\)\s*\{[^}]*_readbackPending\s*=\s*true"))
            {
                Console.WriteLine("    ✗ ① _readbackPending 不是在「真的排上了」"
                    + "的条件下置的——它仍是无条件置位，等于原 bug");
                return 1;
            }
            bool fed = body.Contains("RequestWholeScreen()")
                && body.Contains("RequestCleanCamera()")
                && System.Text.RegularExpressions.Regex.IsMatch(
                    body, @"bool\s+queued\s*=\s*_surface");
            if (!fed)
            {
                Console.WriteLine("    ✗ ① queued 不是由两个 Request* 的返回值决定");
                return 1;
            }
            Console.WriteLine("    ✓ ① _readbackPending 只在真的排上回读之后置位"
                + "（相机列表为空 / 窗口尺寸为 0 都不再挂起）");
            return 0;
        }

        /// <summary>
        /// 同时最多一路回读在飞——这是帧序正确的**结构性**保证。
        ///
        /// Spectre 用「回读队列只看队首」来保序（VideoFrameSource.cs:112-137）：
        /// GPU 完成顺序不保证提交顺序，所以只能按提交序交付。
        ///
        /// 我们的做法不同但同样成立，而且更简单：**根本不允许并发**——
        /// `Request()` 开头看到 `_readbackPending` 就返回 false，
        /// 一次只排一路，回调到达才允许下一路。
        ///
        /// 并发为零时"乱序"在定义上就不可能发生，所以不需要队列、
        /// 也不需要"只看队首"。同理帧缓冲池与编码队列都用 `lock` 保护，
        /// 比 Spectre 的 `ConcurrentQueue` 更保守但一样安全
        /// （池的 Rent 在主线程、Return 在泵线程）。
        ///
        /// 判据：`Request()` 的 pending 检查必须排在任何排回读之前。
        /// </summary>
        private static int InFlightIsAtMostOne(string projectRoot)
        {
            string file = Path.Combine(projectRoot, "Features", "VideoRender",
                "FrameSource.cs");
            string src = StripComments(File.ReadAllText(file));
            int start = src.IndexOf("internal bool Request()");
            if (start < 0)
            {
                Console.WriteLine("    ✗ ② 找不到 Request()");
                return 1;
            }
            int head = src.IndexOf('{', start);
            int check = src.IndexOf("_readbackPending", head);
            int firstReq = src.IndexOf("RequestWholeScreen()", head);
            if (check < 0 || firstReq < 0)
            {
                Console.WriteLine("    ✗ ② Request() 里缺少 pending 检查或回读调用");
                return 1;
            }
            if (check > firstReq)
            {
                Console.WriteLine("    ✗ ② pending 检查排在排回读之后："
                    + "同一帧会排上多路回读，"
                    + "而 GPU 完成顺序不保证提交顺序 → 画面乱序");
                return 1;
            }
            Console.WriteLine("    ✓ ② 同时最多一路回读在飞"
                + "（并发为零则乱序不可能，无需「只看队首」的队列）");
            return 0;
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
                if (!inStr && !inChar && c == '/' && i + 1 < src.Length
                    && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != '\n') i++;
                    sb.Append('\n');
                    continue;
                }
                if (!inStr && !inChar && c == '/' && i + 1 < src.Length
                    && src[i + 1] == '*')
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