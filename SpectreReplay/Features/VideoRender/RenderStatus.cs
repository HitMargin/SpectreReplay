using SpectreReplay.Ui;
using UnityEngine;

namespace SpectreReplay.Features.VideoRender
{
    /// <summary>
    /// 渲染状态快照。UI 只读这里，不直接问服务——
    /// 这样 UI 刷新频率与服务内部状态变更频率解耦。
    /// </summary>
    internal static class RenderStatus
    {
        internal enum State
        {
            Idle,
            Live,
            Offline,
            Finalizing,
            Done,
            Failed,
            Cancelled,
        }

        internal static State Current = State.Idle;
        internal static float Progress;         // 0..1，实时模式里没有明确总帧数
        internal static int FramesDone;
        internal static int FramesTotal;
        internal static string LastOutput = "";
        internal static string LastError = "";
        internal static double EtaSeconds = -1;

        /// <summary>进度条：实时模式下总帧数随时间增长，用「已录秒数 / 目标秒数」代替。</summary>
        internal static float BarFill =>
            FramesTotal > 0
                ? System.Math.Min(1f, (float)FramesDone / FramesTotal)
                : 0f;

        /// <summary>在页面上挂一个会自己刷新的状态行。</summary>
        internal static void Bind(Transform row)
        {
            LiveLabel.Create(row, Describe, 0.2f);
        }

        internal static string Describe()
        {
            switch (Current)
            {
                case State.Idle:
                    return "—";
                case State.Live:
                    return Loc.T("render.state.live") + "  "
                        + TimeSpanOf(FramesDone / (float)System.Math.Max(1, Settings.Fps));
                case State.Offline:
                    return Loc.T("render.state.offline") + "  "
                        + FramesDone + " / " + FramesTotal
                        + (EtaSeconds >= 0 ? "   ETA " + TimeSpanOf((float)EtaSeconds) : "");
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

        private static string TimeSpanOf(float seconds)
        {
            var ts = System.TimeSpan.FromSeconds(System.Math.Max(0, seconds));
            return ts.TotalMinutes >= 1
                ? (int)ts.TotalMinutes + "m" + ts.Seconds.ToString("00") + "s"
                : (int)ts.TotalSeconds + "s";
        }
    }
}
