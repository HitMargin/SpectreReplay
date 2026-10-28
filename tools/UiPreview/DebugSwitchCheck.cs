// 调试开关的接线。
//
// 缺陷史（2026-10-28）：SpectreState 里 DebugMode / debug_skip_Verification /
// debug_hide_text / debug_no_encryption / debug_use_old_hit 五个字段从上游移植过来时
// **只搬了读取点、没搬开关**——全仓没有任何赋值点，所以恒为 false：
//   · 编辑器里永远跳过不了回放校验（帧时序一改就「校验未通过」）
//   · 想截干净画面时提示条关不掉
//   · 录音校验的哈希日志永远不打印
//   · 存不出不加密的 .psprp 供排查
//
// 修法按玩家侧需求定了两条约束：**不持久化**、**必须改源码才显示调试分区**。
// 判据全部落在形状上，因为这里最容易退化成的假修复是「加了个配置项」——
// 那不但没解决问题，还会把「调试构建」和「愿意看见调试开关」混成一件持久事实。
using System;

namespace UiPreview
{
    internal static class DebugSwitchCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 调试开关接线 ──");
            string main = SrcUtil.Source(projectRoot, "Main.cs");
            string state = SrcUtil.Source(projectRoot, "State/SpectreState.cs");
            string pages = SrcUtil.Source(projectRoot, "Pages/Pages.cs");
            string cfg = SrcUtil.Source(projectRoot, "State/ConfigManager.cs");
            int fail = 0;
            void Expect(bool ok, string msg)
            {
                if (ok) return;
                Console.WriteLine("    ✗ " + msg);
                fail++;
            }

            // ① DebugMode 必须有赋值点。这一条就是缺陷史本身：声明了、有人读、没人写
            //    = 五个开关全是死的，而读它们的代码看起来完全正常。
            int toggleAt = main.IndexOf("bool OnToggle(", StringComparison.Ordinal);
            Expect(toggleAt >= 0, "① 找不到 Main.OnToggle（装配收口被挪走了？）");
            if (toggleAt >= 0)
            {
                string body = main.Substring(toggleAt, Math.Min(8000, main.Length - toggleAt));
                Expect(body.Contains("SpectreState.DebugMode = ", StringComparison.Ordinal),
                    "① OnToggle 没给 SpectreState.DebugMode 赋值 —— 五个调试开关恒为 false（本缺陷史原状）");
            }

            // ② 四个行为开关每次装配都复位。它们是会话内的临时状态，不复位就等于
            //    跨装配隐式继承：上一次开着的「跳过校验」会静默留到下一次。
            if (toggleAt >= 0)
            {
                string body = main.Substring(toggleAt, Math.Min(8000, main.Length - toggleAt));
                string[] resets =
                {
                    "SpectreState.debug_skip_Verification = false;",
                    "SpectreState.debug_no_encryption = false;",
                    "SpectreState.debug_hide_text = false;",
                    "SpectreState.debug_use_old_hit = false;",
                };
                foreach (string r in resets)
                {
                    Expect(body.Contains(r, StringComparison.Ordinal),
                        "② 装配时没复位 " + r.Split(' ')[0] + "." + r.Split('.')[1].Split(' ')[0] + " —— 上次开着的开关会静默留到下一次");
                }
            }

            // ③ 界面上必须双重门控：DebugMode（构建类型）**且** ShowDebugSection（改源码）。
            //    只有 DebugMode 一道门，等于所有调试构建的玩家都能看见这些开关。
            int gateAt = pages.IndexOf("if (DebugMode && ShowDebugSection)", StringComparison.Ordinal);
            Expect(gateAt >= 0,
                "③ 关于页的调试分区不是双重门控 —— 缺 ShowDebugSection 就等于把调试开关暴露给所有调试构建");
            if (gateAt >= 0)
            {
                string after = pages.Substring(gateAt, Math.Min(400, pages.Length - gateAt));
                Expect(after.Contains("DrawDebugSection()", StringComparison.Ordinal),
                    "③ 门控条件在但没有调用 DrawDebugSection —— 分区永远不会被画出来");
            }

            // ④ 不持久化：配置层不得出现任何调试字段。这一条专门挡「顺手把它做成配置项」。
            string[] debugNames = { "DebugMode", "debug_skip_Verification", "debug_hide_text", "debug_no_encryption", "debug_use_old_hit" };
            foreach (string n in debugNames)
            {
                Expect(cfg.IndexOf(n, StringComparison.Ordinal) < 0,
                    "④ ConfigManager 引用了 " + n + " —— 调试开关一旦落盘就会变成玩家机器上「回放播不了」的持久故障");
            }

            // ⑤ 调试分区内部不得调 Commit()。Commit 就是「写进配置」，
            //    分区里出现它等于把上面那条约束从另一头破掉。
            int dbgAt = pages.IndexOf("private static void DrawDebugSection()", StringComparison.Ordinal);
            Expect(dbgAt >= 0, "⑤ 找不到 Pages.DrawDebugSection（调试分区被挪走/改名了？）");
            if (dbgAt >= 0)
            {
                int next = pages.IndexOf("private static ", dbgAt + 10, StringComparison.Ordinal);
                string body = next > dbgAt ? pages.Substring(dbgAt, next - dbgAt) : pages.Substring(dbgAt);
                Expect(body.IndexOf("Commit(", StringComparison.Ordinal) < 0,
                    "⑤ DrawDebugSection 里调了 Commit —— 调试开关被写进配置了，与「不持久化」冲突");
            }

            // ⑥ ShowDebugSection 默认必须是 false，且没有任何非源码的写入点。
            //    「默认 true」等于门控失效；「别处赋值」等于有人偷偷开了后门。
            int showDecl = state.IndexOf("ShowDebugSection = false", StringComparison.Ordinal);
            Expect(showDecl >= 0, "⑥ SpectreState.ShowDebugSection 的默认值不是 false —— 调试分区默认就该看不见");
            if (showDecl >= 0)
            {
                // 除声明外不应再出现赋值（读它是允许的：Pages 里要判断）。
                int first = state.IndexOf("ShowDebugSection =", StringComparison.Ordinal);
                int second = first >= 0 ? state.IndexOf("ShowDebugSection =", first + 1, StringComparison.Ordinal) : -1;
                Expect(second < 0, "⑥ ShowDebugSection 在 SpectreState 之外还有赋值点 —— 门控被绕过了");
            }

            if (fail == 0)
                Console.WriteLine("    ✓ DebugMode 有赋值 / 四开关装配复位 / 双重门控 / 不落盘 / 默认不可见");
            return fail;
        }
    }
}