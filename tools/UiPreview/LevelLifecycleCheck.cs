// 关卡生命周期的离线验证。
//
// 存在的理由：「刚开始玩就显示渲染完成」这个 bug 已经反复出现过两轮，
// 两轮的根因还各不相同：
//   第一次  补丁挂在 Start_Rewind 上，无条件收尾——而 Start_Rewind 在
//           **关卡刚加载完**时也会被调（scrController.cs:1480，WaitForStartCo）
//   第二次  改成"会话在跑才收尾"——可进关那一刻会话**确实正在跑**
//
// 两轮都是"进关"被误判成"重开"。区别只在细节，所以必须把判据本身
// 写成可测的纯逻辑，而不是每次靠读时序猜。
//
// 这次能离线验的部分：进关序列里 rewind 与"开始渲染"的先后关系。
// 真机上的时序无法离线复现，但**判定规则**可以：给定一串事件，
// 问"哪几次该收尾"，答案必须只有通关那一次。
using System;
using System.Collections.Generic;

namespace UiPreview
{
    /// <summary>
    /// 复刻 LiveSession 的"第一次 rewind"判定，与真机同一套规则。
    ///
    /// **刻意不复用真代码**：真代码里它依赖 Unity 的 MonoBehaviour 实例
    /// 和 GameObject，链进预览器就要把整个 Unity 塞进来——那会让这条
    /// 检查变得跑不起来，而跑不起来的检查等于没有。
    /// 规则只有三条，照抄过来即可；下面的反向对照保证这份复刻没有
    /// 在某个方向上抄错。
    /// </summary>
    internal static class LevelLifecycleCheck
    {
        /// <summary>与 LiveSession.ConsumeFirstRewind 同一份规则。</summary>
        private sealed class Session
        {
            internal bool Running;
            internal bool SawRewind;

            internal void Begin()
            {
                Running = true;
                SawRewind = false;
            }

            /// <summary>返回 true 表示"这次是进关，跳过"。</summary>
            internal bool ConsumeFirstRewind()
            {
                if (Running == false) return false;   // 会话不在：与它无关
                if (SawRewind) return false;           // 已经跳过一次了
                SawRewind = true;
                return true;
            }
        }

        internal static int Run()
        {
            Console.WriteLine("  ── 关卡生命周期 ──");
            int fail = 0;
            fail += FirstRewindIsEntry();
            fail += SecondRewindIsRestart();
            fail += RestartWithoutSession();
            return fail;
        }

        /// <summary>
        /// 进关序列：开始渲染 → Start_Rewind（关卡加载完成）→ …… → 通关。
        ///
        /// 期望：**只有通关那一次**收尾，进关那次 rewind 不收尾。
        /// 这正是「刚开始玩就显示渲染完成」要防的场景。
        /// </summary>
        private static int FirstRewindIsEntry()
        {
            var s = new Session();
            var closed = new List<string>();

            s.Begin();                                   // 进入关卡 → 开录
            if (!s.ConsumeFirstRewind() && s.Running)
            {
                // 进关那次 rewind 应当被跳过，走到这里就是 bug
                closed.Add("进关 rewind");
            }

            // 玩了一分钟之后玩家按 R 重开
            if (!s.ConsumeFirstRewind() && s.Running) closed.Add("玩家重开");

            // 通关
            if (s.Running) closed.Add("通关");

            bool ok = closed.Count == 2
                && closed[0] == "玩家重开" && closed[1] == "通关";
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ① 进关那次 rewind 不收尾"
                + $"  → 收尾于 {string.Join("、", closed)}");
            if (!ok)
            {
                Console.WriteLine("      ← 进关被当成重开，玩家刚开始玩就显示渲染完成");
            }
            return ok ? 0 : 1;
        }

        /// <summary>第二次及以后的 rewind 必须是真重开，要收尾。</summary>
        private static int SecondRewindIsRestart()
        {
            var s = new Session();
            s.Begin();
            int skipped = 0;
            for (int i = 0; i < 3; i++)
            {
                if (s.ConsumeFirstRewind()) skipped++;
            }
            bool ok = skipped == 1;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ② 三次 rewind 只跳过一次"
                + $"  → 跳过 {skipped} 次");
            if (!ok)
            {
                Console.WriteLine("      ← 多次重开会让后面几次也跳过，视频永远收不了尾");
            }
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 会话还没开就收到 rewind（没开渲染，或已被别处收掉）。
        ///
        /// 期望：返回 false，让调用方按正常流程处理——
        /// **不能**因为"跳过了一次"就把后续真正的重开也一并放过。
        /// </summary>
        private static int RestartWithoutSession()
        {
            var s = new Session();
            // 没 Begin：会话不存在
            bool first = s.ConsumeFirstRewind();
            s.Begin();
            bool second = s.ConsumeFirstRewind();
            bool ok = !first && second;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ③ 会话开始前的 rewind 不被吞掉"
                + $"  → 无会话={first} 有会话={second}");
            return ok ? 0 : 1;
        }
    }
}