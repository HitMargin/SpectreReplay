using UnityEngine;
using SpectreReplay.Ui;

namespace SpectreReplay.Features.VideoRender
{
    /// <summary>
    /// 渲染服务门面。
    ///
    /// 两种模式在这里分叉：
    ///   实时（Live）    —— 进关时开录，逐帧喂给编码器，结算时收尾
    ///   离线（Offline） —— 重跑一遍回放，按固定帧率逐帧推进后喂给编码器
    /// 两者共用同一个 <see cref="FramePipeline"/>（取帧 + 编码），只是
    /// "谁来驱动、什么时候推进"不同。分叉点只有下面这几个方法。
    /// </summary>
    internal static class RenderService
    {
        // ── 对外入口 ──────────────────────────────────────────
        //
        // 这里原先有一个 `Busy` 属性和一个 `MarkBusy(bool)` 方法，全仓
        // **零调用点**——而 StartManual 开头的 `if (Busy) return;` 正靠它
        // 把门。结果那道门从来不开：两次点击「开始渲染」会真的开两个
        // 会话。现在两个都删了，把门换成**真的**判据（两个会话各自的
        // IsRunning），语义和现实就对上了。
        //
        // 返回值 = 这次点击有没有真的启动什么。UI 用它决定要不要提示。
        internal static bool StartManual()
        {
            if (Settings.Mode == Settings.RenderMode.Live)
            {
                // 实时模式不提供手动开始。进关那一刻 OnLevelStart() 已经
                // Begin() 了，所以"手动"这一下在正常流程里没有任何语义。
                // 保留这个分支是为了将来"用刚打完的这一次重录"这类需求；
                // 现在它如实报告"什么都没干"，而不是假装开了个会。
                return false;
            }

            // 离线模式需要一个回放文件。取最近那一个：离线渲染的前提本来
            // 就已经在关卡里，再让用户去挑文件是多余的一步。**UI 会把选中
            // 的文件名写出来**（Pages 里 Loc.F("render.offline_target", …)），
            // 所以"取最新"这个隐式选择现在是可见的。
            string recent = LatestReplay();
            if (string.IsNullOrEmpty(recent))
            {
                RenderStatus.Current = RenderStatus.State.Failed;
                RenderStatus.LastError = Loc.T("render.err.no_replay");
                return false;
            }
            if (OfflineSession.IsRunning || LiveSession.IsRunning)
            {
                return false;
            }
            return OfflineSession.Start(recent);
        }

        /// <summary>按修改时间取最新的 .srp；找不到返回空串。</summary>
        internal static string LatestReplay()
        {
            try
            {
                string dir = Settings.ReplayDir;
                if (!System.IO.Directory.Exists(dir))
                {
                    return "";
                }
                string best = null;
                long bestTime = long.MinValue;
                foreach (string f in System.IO.Directory.GetFiles(dir, "*.srp"))
                {
                    long t = System.IO.File.GetLastWriteTimeUtc(f).Ticks;
                    if (t > bestTime)
                    {
                        bestTime = t;
                        best = f;
                    }
                }
                return best ?? "";
            }
            catch
            {
                return "";
            }
        }

        internal static void Cancel()
        {
            // 判据用会话本身（IsRunning）。原先这里靠一个从不被调用的
            // Busy 字段，那道门恒开，所以取消永远会掉进 OfflineSession。
            if (LiveSession.IsRunning)
            {
                LiveSession.Cancel();
                return;
            }
            OfflineSession.Cancel();
        }

        // ── 模式钩子（由补丁/生命周期调用）────────────────────
        internal static void OnLevelStart()
        {
            if (!Settings.RenderOn || Settings.Mode != Settings.RenderMode.Live)
            {
                return;
            }
            LiveSession.Begin();
        }

        internal static void OnLevelEnd()
        {
            if (Settings.Mode == Settings.RenderMode.Live)
            {
                LiveSession.End();
            }
        }
    }
}
