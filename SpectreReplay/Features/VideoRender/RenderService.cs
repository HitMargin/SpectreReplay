using UnityEngine;

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
        internal static bool Busy { get; private set; }

        // ── 对外入口 ──────────────────────────────────────────
        internal static void StartManual()
        {
            if (Busy)
            {
                return;
            }
            if (Settings.Mode == Settings.RenderMode.Live)
            {
                // 实时模式不是"点一下就开始录"——它在进关时自动开。
                // 这里手动触发的是"用刚打完的这一次游玩重录"，
                // 也就是把实时会话重新拉起来，不重跑关卡。
                if (!LiveSession.IsRunning)
                {
                    LiveSession.Begin();
                }
            }
            else
            {
                OfflineSession.Start();
            }
        }

        internal static void Cancel()
        {
            // 判据用会话本身而不是 Busy 字段：Busy 只在 Begin/End 这条路径上被写，
            // 而实时会话可能在 Busy 还没被置位时就已经跑起来了（启动失败也会留下状态）。
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

        internal static void MarkBusy(bool busy)
        {
            Busy = busy;
            RenderStatus.Current = busy
                ? (Settings.Mode == Settings.RenderMode.Live
                    ? RenderStatus.State.Live
                    : RenderStatus.State.Offline)
                : RenderStatus.State.Idle;
        }
    }

    /// <summary>离线会话（重跑回放逐帧捕获）。本轮先落占位。</summary>
    internal static class OfflineSession
    {
        internal static void Start() { }
        internal static void Cancel() { }
    }
}
