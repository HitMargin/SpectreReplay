using System;
using UnityEngine;
using SpectreReplay.API;
using static SpectreReplay.SpectreState;
using static SpectreReplay.Features.Replay.ReplayKeys;

namespace SpectreReplay.Features.VideoRender;

// 回放视频渲染门面：UI 与补丁只跟这个类打交道，实际渲染流程由 VideoRenderJob 承载。
// 阶段与状态快照都集中在这里，方便设置页随时读取进度。
internal static class VideoRender
{
    internal enum Phase { Idle, Restarting, WaitingPlayback, Capturing, Finalizing }

    // 渲染状态快照（全部主线程读写，无锁）
    internal sealed class Snapshot
    {
        internal string Stage = "";
        internal string Detail = "";
        internal string Output = "";
        internal string Error = "";
        internal int FramesDone;
        internal int FramesTotal;
        internal float Progress => FramesTotal > 0
            ? Mathf.Clamp01((float)FramesDone / FramesTotal)
            : 0f;
        // 捕获阶段预计剩余秒数；<0 = 暂不可估（捕获未开始/已进入收尾）
        internal double EtaSeconds = -1;
    }

    internal static Phase CurrentPhase { get; set; } = Phase.Idle;
    internal static Snapshot Current { get; } = new Snapshot();
    internal static VideoRenderJob Job { get; private set; }
    internal static bool Active => Job != null;

    // —— 时间钉扎 ——
    // 作业锚定时间线后置位，VideoRenderPatches 把 conductor 的读写都钉在这一个值上，
    // 游戏各系统（轨道推进、判定、回放调度）就按输出帧率逐帧推进，与真实音频时钟脱钩。
    internal static double PinnedSongPosition;
    internal static bool TimePinned;

    internal static bool TryReadPinnedPosition(out double position)
    {
        position = PinnedSongPosition;
        return TimePinned && Active;
    }

    internal static void PinTimeline(double songPosition)
    {
        PinnedSongPosition = songPosition;
        TimePinned = true;
    }

    internal static void UnpinTimeline()
    {
        TimePinned = false;
        PinnedSongPosition = 0.0;
    }

    // 设置页「开始渲染」按钮入口
    internal static void StartRender()
    {
        if (Job != null)
            return;
        if (SpectreState.data == null || SpectreState.data.HitContext_list.Count == 0)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.need_replay"), 3f, NotifType.Warning);
            return;
        }
        bool inLevel = scrController.instance != null && ADOBase.lm != null && ADOBase.lm.listFloors != null
            && ADOBase.lm.listFloors.Count > 1;
        bool inEditor = ADOBase.editor != null && ADOBase.editor.floors != null && ADOBase.editor.floors.Count > 1;
        if (!inLevel && !inEditor)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.need_level"), 3f, NotifType.Warning);
            return;
        }
        string encoderPath = VideoEncoder.LocateEncoder();
        if (encoderPath == "ffmpeg" && !VideoEncoder.PathProbeSucceeded())
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.ffmpeg_missing"), 5f, NotifType.Warning);
            return;
        }
        ResetSnapshot();
        CurrentPhase = Phase.Restarting;
        Job = VideoRenderJob.Launch(encoderPath);
        SpectreAPI.RaiseRenderStarted();
    }

    internal static void CancelRender()
    {
        Job?.RequestCancel();
    }

    // 作业结束时的收尾（无论成败），由 VideoRenderJob 调用
    internal static void CloseJob()
    {
        UnpinTimeline();
        Job = null;
        CurrentPhase = Phase.Idle;
    }

    private static void ResetSnapshot()
    {
        Current.Stage = "";
        Current.Detail = "";
        Current.Output = "";
        Current.Error = "";
        Current.FramesDone = 0;
        Current.FramesTotal = 0;
        Current.EtaSeconds = -1;
    }
}
