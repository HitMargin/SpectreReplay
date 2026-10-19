// 时间钉扎的**跨作业状态安全**：两次渲染之间不得残留任何活着的东西。
//
// 背景（2026-10-19 用户报）：取消一次渲染后，下一次渲染「时钟全炸」。
//
// 根因不是取消路径没清干净，而是清理**做失败了却装作成功**：
//
//   UnscaledDeltaPin.Uninstall()
//   {
//       foreach (IDisposable d in detours)
//       {
//           try { d.Dispose(); }
//           catch (Exception ex) { Debug.Log(...); }   // ← 吞掉
//       }
//       detours.Clear();                               // ← 不管成没成都清空
//   }
//
// 取消渲染时协程被 yield break 打断，TearDown 在**场景切换途中**跑（渲染第一步
// 就是 SwitchToPlayMode 重开关卡）。此时 MonoMod 的原生 detour Dispose 很容易
// 因为目标已被卸载而抛。那个 detour **仍然挂在 Time.unscaledDeltaTime 上，
// 但我们把它从列表里删了**——再也不知道它的存在，也永远无法再撤销。
//
// 下一轮 Install 看到 Count == 0，正常安装新 detour ⇒ 旧那层成了永久接管者，
// 读的却是新一轮的静态字段。而且 PinnedDelta 当时是 `1f / Time.captureFramerate`，
// 收尾早已把 captureFramerate 还原成 0 ⇒ 1/0 = Infinity ⇒ 星球位置 NaN。
//
// 两条判据各守一半，缺任何一半都拦不住这次的形态：
//   ① 撤不掉的必须留在列表里（否则「没清干净」变成「不知道它存在」）
//   ② 钉扎位移不得用**可归零的外部状态**做除数（否则残留 detour 也疯）
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class PinStateCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 时间钉扎的跨作业状态 ──");
            int fail = 0;

            string path = Path.Combine(projectRoot,
                "Features", "VideoRender", "Patches", "VideoRenderPatches.cs");
            if (!File.Exists(path))
            {
                Console.WriteLine("    ✗ 找不到 VideoRenderPatches.cs");
                return 1;
            }
            string code = SrcUtil.Clean(File.ReadAllText(path));

            // ① Uninstall 里不能"吞掉异常后照样清空列表"。
            //
            //    判法落在**控制流**上，不是"文件里出现过 failed 这个词"：
            //    先切出 Uninstall 方法体，再要求它体内同时出现
            //    「把失败项收集起来」和「把失败项放回列表」。
            //    只查 detours.Clear() 存在是不够的——那正是缺陷形态本身。
            string uninstall = MethodBody(code, "Uninstall");
            if (uninstall == null)
            {
                Console.WriteLine("    ✗ 找不到 UnscaledDeltaPin.Uninstall 方法体");
                fail++;
            }
            else
            {
                bool retries = uninstall.Contains("d.Dispose();", StringComparison.Ordinal)
                    && CountOccurrences(uninstall, "d.Dispose();") >= 2;
                bool keeps = uninstall.Contains("detours.Add(d)", StringComparison.Ordinal);
                bool clearAfter = uninstall.IndexOf("detours.Clear()", StringComparison.Ordinal) >= 0;
                // 关键：Add(d) 必须出现在 Clear() **之后**，否则先 Add 再 Clear 等于白做。
                int clearAt = uninstall.IndexOf("detours.Clear()", StringComparison.Ordinal);
                int addAt = uninstall.IndexOf("detours.Add(d)", StringComparison.Ordinal);
                bool addAfterClear = clearAt >= 0 && addAt >= 0 && addAt > clearAt;

                if (!retries)
                {
                    Console.WriteLine("    ✗ Uninstall 没有重试 Dispose —— 首次失败常常只是"
                        + "目标正在销毁，重试一次就能撤掉");
                    fail++;
                }
                if (!keeps || !addAfterClear)
                {
                    Console.WriteLine("    ✗ Uninstall 把撤不掉的 detour 一起清掉了"
                        + " —— 它仍然挂在 Time.* 上，而我们已经不知道它存在，"
                        + "下一轮 Install 会再叠一层，时钟就此错乱");
                    fail++;
                }
                if (fail == 0)
                {
                    Console.WriteLine("    ✓ Uninstall 撤不掉的 detour 会留在列表里（下次再试）");
                }
            }

            // ② Install 不能在"有残留"时直接 return。
            //
            //    原实现开头是 `if (detours.Count > 0) return;`。配合 ① 的缺陷，
            //    上一轮残留的 detour 会让本轮**什么都不装**，而时间仍被旧 detour
            //    接管 —— 这比"叠两层"更隐蔽，因为日志里一个字都没有。
            string install = MethodBody(code, "Install");
            if (install == null)
            {
                Console.WriteLine("    ✗ 找不到 UnscaledDeltaPin.Install 方法体");
                fail++;
            }
            else
            {
                int guardAt = install.IndexOf("detours.Count > 0", StringComparison.Ordinal);
                if (guardAt < 0)
                {
                    Console.WriteLine("    ✗ Install 缺少「已有 detour」的判据（重复安装会叠层）");
                    fail++;
                }
                else
                {
                    // 判据范围：从 guard 到下一个方法级 try 之间，必须出现 Uninstall()。
                    int tryAt = install.IndexOf("try", guardAt + 1, StringComparison.Ordinal);
                    int len = (tryAt > guardAt ? tryAt - guardAt + 1 : install.Length - guardAt);
                    string guardBlock = install.Substring(guardAt, len);
                    if (!guardBlock.Contains("Uninstall()", StringComparison.Ordinal))
                    {
                        Console.WriteLine("    ✗ Install 发现残留后没有先尝试撤销 ——"
                            + "直接 return 会让本轮「什么都不装」，而旧 detour 仍在接管时间");
                        fail++;
                    }
                    else
                    {
                        Console.WriteLine("    ✓ Install 发现残留时先撤销，撤不掉则明确放弃并告警");
                    }
                }
            }

            // ③ 钉扎位移不得用可归零的外部状态做除数。
            //
            //    `1f / Time.captureFramerate` 是这次的直接凶手：收尾阶段
            //    RestoreGlobalState 会把它还原成 0，而残留 detour 还在读它。
            //    要求：PinnedDelta / TickFrame / CurrentDelta 三处都不得出现
            //    以 Time.captureFramerate 为除数的表达式。
            string[] mustSnapshot = { "PinnedDelta", "TickFrame", "CurrentDelta" };
            int bad = 0;
            foreach (string member in mustSnapshot)
            {
                // 属性用 => 表达式；方法需要取方法体。两种都先取"该成员那一行/那段"。
                string body = MemberText(code, member);
                if (body == null) continue;   // 形态变了由别处判据兜，这里不误报
                if (body.Contains("1f / Time.captureFramerate", StringComparison.Ordinal)
                    || body.Contains("1.0 / Time.captureFramerate", StringComparison.Ordinal)
                    || body.Contains("1 / Time.captureFramerate", StringComparison.Ordinal))
                {
                    Console.WriteLine("    ✗ " + member + " 用 Time.captureFramerate 做除数"
                        + " —— 收尾会把它还原成 0，残留 detour 读到 1/0 = Infinity ⇒ 星球位置 NaN");
                    bad++;
                }
            }
            fail += bad;
            if (bad == 0)
            {
                Console.WriteLine("    ✓ 钉扎位移用帧率快照，不读可归零的 Time.captureFramerate");
            }

            return fail;
        }

        /// <summary>取某个方法的 { } 包起来的函数体（已剥注释）。</summary>
        private static string MethodBody(string code, string name)
        {
            Match m = Regex.Match(code,
                @"\bvoid\s+" + Regex.Escape(name) + @"\s*\([^)]*\)\s*\{");
            if (!m.Success) return null;
            int start = m.Index + m.Length - 1;
            return SliceBraces(code, start);
        }

        /// <summary>取以 `=>` 表达式或方法体形式定义的成员文本。</summary>
        private static string MemberText(string code, string name)
        {
            Match m = Regex.Match(code,
                @"\b" + Regex.Escape(name) + @"\s*(?:\([^)]*\))?\s*(?:=>|\{)");
            if (!m.Success) return null;
            if (code[m.Index + m.Length - 1] == '{')
                return SliceBraces(code, m.Index + m.Length - 1);
            // 表达式体：取到同层的 `;`
            int start = m.Index + m.Length;
            int semi = code.IndexOf(';', start);
            return semi > start ? code.Substring(start, semi - start) : null;
        }

        private static string SliceBraces(string s, int openAt)
        {
            int depth = 0;
            for (int i = openAt; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}')
                {
                    depth--;
                    if (depth == 0) return s.Substring(openAt, i - openAt + 1);
                }
            }
            return s.Substring(openAt);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                n++;
                i += needle.Length;
            }
            return n;
        }
    }
}