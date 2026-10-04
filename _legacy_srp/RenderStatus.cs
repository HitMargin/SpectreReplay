using SpectreReplay.Ui;
using UnityEngine;

namespace SpectreReplay.Features.VideoRender
{
    /// <summary>
    /// 渲染状态的对外快照。UI 只读这里，不直接问服务——
    /// 这样 UI 的刷新频率与服务内部的状态变更频率解耦。
    /// </summary>
    public static class RenderStatus
    {
        public enum State
        {
            Idle,
            /// <summary>边玩边录中。</summary>
            Live,
            /// <summary>离线渲染中。</summary>
            Offline,
            Finalizing,
            Done,
            Failed,
            Cancelled,
        }

        public static State Current = State.Idle;

        /// <summary>0..1。实时录制里总帧数事先未知，按已录时长与目标帧率估算。</summary>
        public static float Progress;

        public static int FramesDone;

        /// <summary>
        /// 离线渲染的总帧数。实时录制是"边玩边出"、总长事先未知，
        /// 所以**恒为 0**（LiveSession 开录与收尾各清一次）。
        ///
        /// 界面靠这个字段判空来决定画不画进度条：
        /// <c>FramesTotal &gt; 0</c> 才有"还剩多少"可言。
        ///
        /// 这里原先还有一个 <c>BarFill</c> 属性做那个二选一
        /// （有总帧数就用帧数比、没有就用 Progress），但它**零调用点**——
        /// 于是离线渲染的进度条从来没被画出来过。更糟的是那个二选一在
        /// Live 模式下会给出**恒为满格**的结果：实时的 Progress 是
        /// "出帧跟上了目标帧率没有"（正常恒为 1），不是进度。
        /// 现在判据留在调用方（Pages.DrawRenderProgress），语义写在那边。
        /// </summary>
        public static int FramesTotal;

        public static string LastOutput = "";
        public static string LastError = "";

        /// <summary>
        /// 离线渲染的预计剩余秒数；负数表示还没算出来。
        ///
        /// 原先**全仓没有一处写它**，于是 Describe() 里那句
        /// <c>EtaSeconds &gt;= 0 ? "ETA …"</c> 永远不成立。
        /// 唯一写点：OfflineSession.Update。
        /// </summary>
        public static double EtaSeconds = -1;

        // 状态文字的挂载改由 Pages.BuildRender 通过 PanelHost.RegisterLive 完成

        public static string Describe()
        {
            switch (Current)
            {
                case State.Idle:
                    return "—";
                case State.Live:
                    return Loc.T("render.state.live") + "  "
                        + Span(FramesDone / (float)System.Math.Max(1, Settings.Fps));
                case State.Offline:
                    return Loc.T("render.state.offline") + "  "
                        + FramesDone + " / " + FramesTotal
                        + (EtaSeconds >= 0 ? "   ETA " + Span((float)EtaSeconds) : "");
                case State.Finalizing:
                    return Loc.T("render.state.finalizing");
                case State.Done:
                    return Loc.T("render.state.done") + "  " + LastOutput;
                case State.Failed:
                    return Loc.T("render.state.failed") + "  " + LastError;
                case State.Cancelled:
                    return Loc.T("render.state.cancelled");
                default:
                    return "—";
            }
        }

        private static string Span(float seconds)
        {
            var ts = System.TimeSpan.FromSeconds(System.Math.Max(0f, seconds));
            return ts.TotalMinutes >= 1
                ? (int)ts.TotalMinutes + "m" + ts.Seconds.ToString("00") + "s"
                : (int)ts.TotalSeconds + "s";
        }
    }
}
