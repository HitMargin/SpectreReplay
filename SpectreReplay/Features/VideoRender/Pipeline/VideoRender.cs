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

    /// <summary>
    /// 上一次渲染的终态。
    ///
    /// **不能靠 <see cref="Snapshot.Output"/> 非空来推断「完成」**：Output 在渲染
    /// 一开始就写进去了（BuildOutputPath 之后立刻赋值），取消时它照样是非空的，
    /// 于是界面把「我取消了」显示成「渲染完成：xxx.mp4」。终态必须显式记录。
    /// </summary>
    internal enum Outcome { None, Completed, Failed, Canceled }

    // 渲染状态快照（全部主线程读写，无锁）
    internal sealed class Snapshot
    {
        internal string Stage = "";
        internal string Detail = "";
        internal string Output = "";
        internal string Error = "";
        internal Outcome Result = Outcome.None;
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

    // —— 渲染期倍速钉扎 ——
    // scnEditor.Play() 对程序化调用**无条件**把倍速重置成 1x（源码
    // scnEditor.cs:7204-7205：`holdingControl` 只有真按住 Ctrl 才为真），
    // 随后 RemakePath → SetupConductorWithLevelData 用这个值算 song.pitch。
    // 而 TryStartPlayback 的倍速校验拿它与回放记录比对——渲染自己重建关卡，
    // 条件就必须由渲染自己准备，不能要求玩家先按住 Ctrl 再点渲染。
    internal static bool SpeedOverrideArmed;
    internal static float SpeedOverride = 1f;
    internal static bool SpeedOverrideQuickPitched;

    // RemakePath 的 Prefix 与 Start_Rewind 的 Prefix 共用这一处写入。
    // 必须在 RemakePath 之前写回：pitch 是在 RemakePath 里推导的，
    // 晚于它的写入只会影响后续判定、救不回已经算错的时间轴。
    internal static void ApplySpeedOverrideTo(scnEditor editor)
    {
        if (!SpeedOverrideArmed || editor == null)
            return;
        editor.playbackSpeed = SpeedOverride;
        GCS.editorQuickPitchedPlaying = SpeedOverrideQuickPitched;
    }

    // 设置页「开始渲染」按钮入口
    internal static void StartRender()
    {
        if (Job != null)
            return;
        if (!Settings.RenderOn)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.disabled"), type: NotifType.Warning);
            return;
        }
        if (SpectreState.data == null || SpectreState.data.HitSnapshots.Count == 0)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.need_replay"), type: NotifType.Warning);
            return;
        }
        bool inLevel = scrController.instance != null && ADOBase.lm != null && ADOBase.lm.listFloors != null
            && ADOBase.lm.listFloors.Count > 1;
        bool inEditor = ADOBase.editor != null && ADOBase.editor.floors != null && ADOBase.editor.floors.Count > 1;
        if (!inLevel && !inEditor)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.need_level"), type: NotifType.Warning);
            return;
        }
        string encoderPath = VideoEncoder.LocateEncoder();
        if (encoderPath == "ffmpeg" && !VideoEncoder.PathProbeSucceeded())
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.ffmpeg_missing"), extra: 2f, type: NotifType.Warning);
            return;
        }
        ResetLeakedPins();
        ResetSnapshot();
        CurrentPhase = Phase.Restarting;
        Job = VideoRenderJob.Launch(encoderPath);
        SpectreAPI.RaiseRenderStarted();
    }

    internal static void CancelRender()
    {
        Job?.RequestCancel();
    }

    /// <summary>
    /// 玩家按 Esc 退出试玩 / 游戏把我们踢出 playtest 时调用（见 Patch_SwitchToEditMode）。
    ///
    /// **必须立刻、同步地把游戏交还回去**，而不是只给作业打个取消标记：
    /// Esc 触发的 <c>SwitchToEditMode</c> 会紧接着恢复相机、停掉 conductor，
    /// 而渲染作业每帧还在推进钉扎时钟、预览相机每帧还在往 backbuffer 贴旧帧。
    /// 两者叠加就是「镜厅特效 + 卡死」。
    /// </summary>
    internal static void CancelForSceneExit()
    {
        if (SuppressSceneExitCancel) return;
        Job?.CancelForSceneExit();
    }

    /// <summary>
    /// 我们自己主动调用 <c>SwitchToEditMode</c> 时置位，避免把正常收尾误判成
    /// 「玩家中途退出」而把一次成功的渲染改判成取消。
    /// 只在主线程读写。
    /// </summary>
    internal static bool SuppressSceneExitCancel;

    /// <summary>捕获循环是否正在跑。Start_Rewind 在捕获期间再次触发 = 时间轴被重置。</summary>
    internal static bool CaptureInProgress;

    /// <summary>捕获期间回放被重新开始（Start_Rewind 二次触发）：通知作业中止。</summary>
    internal static void NotifyPlaybackRestarted()
    {
        if (CaptureInProgress)
            Job?.AbortOnPlaybackRestart();
    }

    internal static void ResetLeakedPins()
    {
        UnpinTimeline();
        RenderClockSource.ActiveClock = null;
        SpeedOverrideArmed = false;
    }

    // 作业结束时的收尾（无论成败），由 VideoRenderJob 调用
    internal static void CloseJob()
    {
        UnpinTimeline();
        // 不复位的话，下一轮启动阶段的 Start_Rewind 会被 NotifyPlaybackRestarted
        // 误判成「捕获期重启」，作业刚起步就被自己掐掉。
        CaptureInProgress = false;
        Job = null;
        CurrentPhase = Phase.Idle;
    }

    private static void ResetSnapshot()
    {
        Current.Stage = "";
        Current.Detail = "";
        Current.Output = "";
        Current.Error = "";
        Current.Result = Outcome.None;
        Current.FramesDone = 0;
        Current.FramesTotal = 0;
        Current.EtaSeconds = -1;
    }
}
