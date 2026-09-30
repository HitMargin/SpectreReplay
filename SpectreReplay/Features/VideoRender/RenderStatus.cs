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
        /// 所以留 0，由 <see cref="BarFill"/> 判空后走另一条算法。
        /// </summary>
        public static int FramesTotal;

        public static string LastOutput = "";
        public static string LastError = "";

        /// <summary>离线渲染的预计剩余秒数；负数表示还没算出来。</summary>
        public static double EtaSeconds = -1;

        public static float BarFill =>
            FramesTotal > 0
                ? System.Math.Min(1f, (float)FramesDone / FramesTotal)
                : Progress;


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
