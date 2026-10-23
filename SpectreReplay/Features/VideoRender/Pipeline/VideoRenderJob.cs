using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using SpectreReplay.API;
using SpectreReplay.Features.Replay;
using static SpectreReplay.SpectreState;
using static SpectreReplay.Features.Replay.ReplayKeys;

namespace SpectreReplay.Features.VideoRender;

// 单次渲染作业：隐藏宿主组件，协程分阶段推进。
// 阶段：重启回放 → 等待回放接管 → 钉扎时间线逐帧捕获 → 合成收尾。
// 回放引擎（按键/命中）全部按 songposition 驱动，时间被钉扎后行为与正常回放一致，
// 只是按输出帧率确定性逐帧推进。
internal sealed class VideoRenderJob : MonoBehaviour
{
    private string encoderPath;

    private bool cancelRequested;
    private VideoFrameSource frames;
    private VideoAudioTrack mixdown;
    private VideoEncoder encoder;
    private string tempDir;
    private string rawVideoPath;
    private string wavPath;
    private byte[] blackout;
    private int delivered;
    // VideoPlayer 接管（视频背景确定性化）：渲染期间 Unity VideoPlayer 仍走实时播放，
    // 渲染比实时慢时背景跑 ahead——暂停后每帧按 RenderClock 定位
    private readonly List<UnityEngine.Video.VideoPlayer> pinnedVideos = new();
    private readonly List<bool> pinnedVideosWerePlaying = new();
    // 捕获起点计时：ETA 按平均交付速率外推（渲染稳态后速率相当平稳，整体均值足够）
    private System.Diagnostics.Stopwatch captureClock;
    private bool playbackLive;
    // 回放没能接管时的具体原因（被校验拒 / 关卡已跑但回放未启动）。
    // 有它才能把「等待回放开始超时」换成一条能指向修法的失败信息。
    private string playbackRejectReason;
    private double anchorSongPosition;
    private double clockSeconds;
    // 终态先记录、后上报：统一在 TearDown 的 CloseJob 之后触发事件。
    // 若在 Phases 体内同步触发，订阅者回调里 TryStartRender 会撞上 Job != null 被静默
    // 跳过却返回 true——链式渲染的调用方以为新作业已启动、实际永远等不到下一个事件
    private bool endDetermined;
    private SpectreAPI.RenderEndStatus endStatus;
    private string endOutput;
    private string endError;
    // TearDown 是否已执行。OnDestroy 兜底与协程正常收尾共用，故需幂等
    private bool tornDown;

    private System.Threading.Thread finalizeWorker;
    // volatile：工作线程写、主线程读
    private volatile bool finalizeDone;
    private volatile Exception finalizeError;
    // 失败发生在 mux 阶段还是排空阶段：两者的补救方式不同（mux 失败时视频流已完整，
    // 值得把 raw 流抢救出来；排空失败则没有可救的成品）
    private volatile bool finalizeErrorFromMux;

    // 保存并恢复的全局状态
    private int savedCaptureFramerate;
    private int savedTargetFrameRate;
    private int savedVSyncCount;
    private bool savedRunInBackground;
    private bool savedAudioPause;
    private int savedCheckpointNum;

    internal static VideoRenderJob Launch(string encoderPath)
    {
        var host = new GameObject("Spectre.VideoRenderJob");
        DontDestroyOnLoad(host);
        VideoRenderJob job = host.AddComponent<VideoRenderJob>();
        // 不在这里直接 StartCoroutine：首个阶段会同步触发场景重载，
        // 在 OnGUI 布局中途换场景会打乱 IMGUI 布局组，推迟到下一帧 Start()
        job.encoderPath = encoderPath;
        return job;
    }

    internal void RequestCancel()
    {
        cancelRequested = true;
    }

    /// <summary>
    /// 玩家按 Esc / 游戏把我们踢出试玩时，**同步**把游戏交还回去。
    ///
    /// 只打一个取消标记是不够的：<c>SwitchToEditMode</c> 会立刻恢复相机、停掉
    /// conductor，而我们的捕获循环还在推进钉扎时钟、预览相机还在往屏幕贴旧帧。
    /// 两者叠加就是「镜厅特效 + 卡死」。所以这里立刻走完整的释放链，
    /// 并抑制 <see cref="ExitPlaytest"/>——调用方本身就是 <c>SwitchToEditMode</c>，
    /// 再调一次会把编辑器状态重复重置。
    /// </summary>
    internal void CancelForSceneExit()
    {
        if (cancelRequested) return;
        cancelRequested = true;
        suppressExitPlaytest = true;
        ReleaseGameControl();
        Debug.Log("[SpectreReplay] render canceled by leaving playtest");
    }

    /// <summary>
    /// 捕获期间回放被重新开始（<c>Start_Rewind</c> 二次触发）= 时间轴被重置，
    /// 之前算好的锚点立即失效。不中止的话会一直钉在错误位置，表现为「回放不动」。
    /// </summary>
    internal void AbortOnPlaybackRestart()
    {
        if (cancelRequested) return;
        // **已经进入收尾就不再算「被重新开始」。**
        //
        // 官方关卡跑完会自动通关转场回 scnLevelSelect，这次场景切换照样触发
        // Start_Rewind。若把它当中止，整份视频会在 mux 前一刻被丢掉——
        // 玩家看到的就是「渲染跑满 100%、然后什么都没留下」。
        // 2026-10-23 实测：frame 1192 判定到终点、进 tail，转场在 tail 期间发生，
        // 日志随即出现 aborted。时间轴既已走完，后面的重启只可能是关卡自己结束。
        // 判据一：已经定下收尾帧（在录尾巴）。
        // 判据二：钉扎位置已越过回放终点——即便终点判定还没来得及在这一帧执行，
        //         回放本身事实上已经走完。两个都看，避免「退出恰好早一帧」漏判。
        if (endingCapture
            || (replayEndSongPosition > 0.0
                && VideoRender.PinnedSongPosition >= replayEndSongPosition - 0.001))
        {
            // **不是忽略，而是就地收尾。** 关卡已经自己结束，后续帧会是
            // 关卡选择界面/转场，继续采进去等于把界面录进成片。
            sceneExited = true;
            Debug.Log("[SpectreReplay] level finished (Start_Rewind during tail) at song="
                + VideoRender.PinnedSongPosition.ToString("0.000") + "s — stopping capture");
            return;
        }
        cancelRequested = true;
        playbackRestarted = true;
        Debug.LogWarning("[SpectreReplay] render aborted: playback restarted during capture "
            + "(timeline anchor invalidated)");
    }

    private bool suppressExitPlaytest;
    private bool playbackRestarted;
    // 是否已判定进入收尾（到达回放终点，正在录尾巴）。
    // 用于区分「用户在渲染中途重开了回放」与「关卡自己跑完了自动转场」——
    // 两者都会触发 Start_Rewind，但只有前者该中止作业。
    private bool endingCapture;
    // 回放终点的绝对歌曲时间。字段而非局部变量：AbortOnPlaybackRestart 也要用它
    // 判断「这次 Start_Rewind 是关卡自己跑完了，不是用户重开」。
    private double replayEndSongPosition;
    // 关卡已自行结束（非编辑器关卡通关后 Won_Update 会主动退出关卡）。
    // 置位后捕获循环立刻停：后续帧是关卡选择界面/转场，采进成片就是脏画面。
    private bool sceneExited;
    // 锚点自愈的已重锚次数（见捕获循环里的锚点自愈段）
    private int reAnchors;
    // AwaitReplayAlive 用：上一帧是否已看到正的 songposition（连续两帧才算稳）
    private bool sawPositivePosition;

    // 渲染起点：玩家点「渲染」时所在砖块。必须在重启/切模式之前抓取。
    private int requestedStartFloor;

    /// <summary>
    /// 解析「玩家想从哪里开始渲染」。
    ///
    /// **为什么不能读 GCS.checkpointNum。** 它只在 <c>scnEditor.Play()</c>
    /// （scnEditor.cs:7270）里被写入，单纯点选地板不会更新它。首次打开关卡时
    /// 它是 0，导致首次渲染从第 0 块开始；必须先在选中的地板上播一次、退出，
    /// 渲染才正确。玩家表达意图的字段是 <c>editor.selectedFloors</c>。
    ///
    /// 判定与 <c>scnEditor.Play()</c>（scnEditor.cs:7237-7251）逐条照搬，
    /// **不做任何自己的发挥**：
    ///   - <c>SelectionIsSingle()</c> → 用 <c>selectedFloors[0].seqID</c>
    ///   - 否则                      → 0（和 Play() 的 else 一致）
    ///
    /// Play() 还有第三个分支（选中装饰物时用 lastSelectedFloor.seqID），但那
    /// 字段是 private 够不着；且玩家点渲染时选中的是装饰物的话，本来也没有
    /// 「从哪块砖开始」的意图，落到 0 与手动播放的行为一致。
    ///
    /// **绝不回退 GCS.checkpointNum。** 那是 Play() 写入的「上次播过哪里」的
    /// 残留值，不是玩家当前的选择——首次打开关卡时它为 0，正是「首次渲染从
    /// 第 0 块开始、必须先播一次才正常」的成因。
    /// </summary>
    private int ResolveRequestedStartFloor()
    {
        try
        {
            if (ADOBase.editor != null && ADOBase.editor.SelectionIsSingle())
            {
                return ADOBase.editor.selectedFloors[0].seqID;
            }
        }
        catch (Exception e)
        {
            try { Main.mod?.Logger?.Log("[SpectreReplay] ResolveRequestedStartFloor 读取选区失败，按 Play() 的 else 分支取 0: " + e.Message); } catch { }
        }

        return 0;
    }

    private void Start()
    {
        StartCoroutine(Drive());
    }

    // 外层手动驱动内层协程并兜异常（C# 不允许 try/catch 块体内 yield）
    private IEnumerator Drive()
    {
        IEnumerator inner = Phases();
        Exception failure = null;
        while (true)
        {
            object current;
            try
            {
                if (!inner.MoveNext())
                    break;
                current = inner.Current;
            }
            catch (Exception ex)
            {
                failure = ex;
                break;
            }
            yield return current;
        }
        if (failure != null)
        {
            if (failure is OperationCanceledException || cancelRequested)
            {
                NotifyCanceled();
            }
            else
            {
                Debug.LogException(failure);
                NotifyFailed(failure.Message);
            }
        }
        TearDown();
    }

    private IEnumerator Phases()
    {
        int fps = Math.Max(1, Settings.RenderFps);
        // 起点 = **编辑器里当前选中的地板**，必须在下面的重启/切模式之前抓取。
        //
        // 不能用 GCS.checkpointNum：它只在 scnEditor.Play()（:7270）里被写入，
        // 单纯点选地板**不会**更新它。首次打开关卡时它是 0，于是首次渲染会
        // 从第 0 块开始，必须「先在选中的地板上播一次、再退出」才正确。
        // 玩家真正表达意图的字段是 editor.selectedFloors。
        requestedStartFloor = ResolveRequestedStartFloor();
        float tailSeconds = Mathf.Clamp(Settings.RenderTailSeconds, 0f, 15f);
        var container = (VideoEncoder.OutContainer)Mathf.Clamp(Settings.RenderOutputFormat, 0, 3);
        string outputPath = BuildOutputPath(container);
        VideoRender.Current.Output = outputPath;

        SetPhase(VideoRender.Phase.Restarting, "rendervideo.stage.restarting", "");
        PrepareWorkspace(container);
        SaveGlobalState(fps);

        // 走既有回放启动链：重启关卡后 Start_Rewind 补丁会做完整性校验并自动开始回放
        SpectreState.PlayActions.SwitchToPlayMode();
        Patch_StartRewind.ResetRejectLog();

        // 把回放记录的倍速交给渲染钩子（见 VideoRender.SpeedOverride 与
        // Patch_VideoSpeedOverride）。**这一步不能省**：scnEditor.Play() 对
        // 程序化调用会把倍速压成 1x，然后立刻用它推导 song.pitch；不改回来，
        // TryStartPlayback 的倍速校验必然拒绝，回放永远不接管——现象就是
        // 「等待回放开始超时」而画面照常跑。录像若没记倍速则保持 1x。
        // 套用条件与 Start_Rewind 的校验期望严格同式：`QuickPitched ? PlaybackSpeed : 1.0`
        // （Patch_Lifecycle.cs:298）。两者必须一致，否则期望=1.0 而实际=1.5，
        // 校验拒绝、回放永不接管。
        // 注意 QuickPitched 与 playbackSpeed 在 scnEditor.Play()（:7233-7234）
        // 是成对写入的，录制侧（Patch_Lifecycle.cs:378/:437）也是同一时刻采样，
        // 所以不存在「非飙速却存了 1.5」的组合。
        bool quickPitched = SpectreState.data.QuickPitched is bool qp && qp;
        VideoRender.SpeedOverrideQuickPitched = quickPitched;
        VideoRender.SpeedOverride = quickPitched
            && SpectreState.data.PlaybackSpeed is double spd && spd > 0.0
            ? (float)spd
            : 1f;
        VideoRender.SpeedOverrideArmed = true;

        // 起点由玩家决定，不强制跳到录像的 data.StartTile。
        // 起点取玩家点渲染时所在的砖块（requestedStartFloor，已在重启前抓取）。
        // 回放侧支持任意起点：
        // Patch_Lifecycle 用 GCS.checkpointNum 调 ReplayPlayer.FastForward
        // 对齐按键游标，并且已放开对应的区间校验。
        //
        // 注意 Play() 的起点推导（scnEditor.cs:7237-7251）：只有「已选中砖块」
        // 才会用它的 seqID，否则走 else 落到 0——直接不传起点反而会强制回第 0 块。
        // 所以这里必须显式选中目标砖块，而不是「什么都不做」。
        if (ADOBase.editor != null && ADOBase.editor.floors != null && ADOBase.editor.floors.Count > 1)
        {
            int floor = Mathf.Clamp(requestedStartFloor, 0, ADOBase.editor.floors.Count - 1);
            ADOBase.editor.SelectFloor(ADOBase.editor.floors[floor], cameraJump: false);
            GCS.checkpointNum = floor;
            ADOBase.editor.Play();
        }
        else
        {
            int floorLimit = ADOBase.lm != null && ADOBase.lm.listFloors != null ? ADOBase.lm.listFloors.Count - 1 : 0;
            GCS.checkpointNum = Mathf.Clamp(requestedStartFloor, 0, Math.Max(0, floorLimit));
        }

        SetPhase(VideoRender.Phase.WaitingPlayback, "rendervideo.stage.waiting", "");
        playbackLive = false;
        yield return AwaitReplayAlive();
        // 顺序有意义：AwaitReplayAlive 在取消时是直接 return 的，playbackLive 会停在 false。
        // 若先判 !playbackLive，用户点了取消却收到"等待回放开始超时"的失败通知
        if (cancelRequested) { NotifyCanceled(); yield break; }
        if (!playbackLive)
        {
            // 有具体原因就报原因：「等待回放开始超时」这句话对玩家没有信息量，
            // 而真正的原因（倍速不符 / 被别的 mod 阻断）直接指向修法。
            NotifyFailed(string.IsNullOrEmpty(playbackRejectReason)
                ? LocalizationManager.GetLocalizedText("rendervideo.wait_timeout")
                : LocalizationManager.GetLocalizedText("rendervideo.wait_timeout")
                    + "：" + playbackRejectReason);
            yield break;
        }

        scrConductor conductor = ADOBase.conductor;
        if (conductor == null || conductor.song == null)
        {
            NotifyFailed(LocalizationManager.GetLocalizedText("rendervideo.conductor_missing"));
            yield break;
        }

        // 就位：画面源 + 混音轨 + 编码器
        frames = new VideoFrameSource((VideoSurface)Settings.RenderCaptureMode, Settings.RenderWidth, Settings.RenderHeight);
        // 指针是否进成片，由设置决定；具体在哪一层画见 CursorOverlay 与 VideoFrameSource.Flight。
        frames.DrawCursor = Settings.RenderCursorOn;
        mixdown = new VideoAudioTrack(wavPath, fps);
        if (mixdown.Dead)
        {
            // 本地化标签 + 括号里的技术细节，与本文件其余两处音频失败一致。
            // DeathReason 本身多数以 "AudioRenderer." 开头，不能再拼同样的前缀。
            NotifyFailed(LocalizationManager.GetLocalizedText("rendervideo.audio_failed")
                + " (" + mixdown.DeathReason + ")");
            yield break;
        }
        encoder = new VideoEncoder(encoderPath, rawVideoPath, outputPath,
            frames.Width, frames.Height, fps,
            (VideoEncoder.Quality)Settings.RenderEncoderMode, Settings.RenderQualityMode, frames.NeedsRowFlip,
            container);
        blackout = new byte[frames.Width * frames.Height * 4];
        encoder.Begin();
        // 缓冲池对齐满载深度（队列容量 + 在途回读），消除编码满载期的 LOH 重复分配
        frames.SetSparePoolLimit(encoder.QueueCapacity + frames.MaxInFlight);

        double audioAnchor = conductor.songposition_minusi;

        RenderClock clock = null;
        bool useRenderClock = RenderClockSource.TranspilerReady && Settings.RenderClockOn;
        if (useRenderClock)
        {
            clock = new RenderClock(fps);
            clock.AnchorDsp(AudioSettings.dspTime);
            RenderClockSource.ActiveClock = clock;
            Debug.Log("[SpectreReplay] renderClock: deterministic RenderClock path (transpiler active)");
        }
        else
        {
            Debug.Log("[SpectreReplay] renderClock: audio-cursor Pin path (transpiler unavailable, fallback)");
        }

        VideoRender.PinTimeline(audioAnchor);

        // 接管视频背景（确定性步进，见 PinVideoPlayers 注释）
        if (useRenderClock)
            PinVideoPlayers();
        anchorSongPosition = audioAnchor;
        clockSeconds = 0.0;
        delivered = 0;
        VideoRender.Current.FramesTotal = EstimateFrames(fps, tailSeconds);
        // 回放终点的绝对歌曲时间。**结束判定必须同时看它**，不能只等
        // is_playing 翻假——见循环里 endFrame 的说明。
        double replayEndSong = ReplayEndSongPosition();
        replayEndSongPosition = replayEndSong;   // AbortOnPlaybackRestart 也要用
        if (replayEndSong <= 0.0) replayEndSong = double.MaxValue;
        captureClock = System.Diagnostics.Stopwatch.StartNew();
        VideoRender.Current.Detail = Path.GetFileName(outputPath);
        SetPhase(VideoRender.Phase.Capturing, "rendervideo.stage.rendering", VideoRender.Current.Detail);

        // 捕获真正开始：之后的 Start_Rewind 二次触发都视为时间轴被重置
        // （见 Patch_StartRewind → NotifyPlaybackRestarted）。
        VideoRender.CaptureInProgress = true;
        int frameIndex = 0;
        // hardStop 是防死循环的保险丝，**不能比「跑到回放终点 + 60s」更紧**。
        // 原先只按 FramesTotal 算，而 FramesTotal 是估算值：估短了就会在
        // 正常收尾前把渲染判成 overrun 失败。时间轴终点是硬事实，以它为准。
        double endSongForLimit = ReplayEndSongPosition();
        double anchorForLimit = anchorSongPosition;
        double pitchForLimit = conductor != null && conductor.song != null
            ? Math.Abs(conductor.song.pitch) : 1.0;
        if (pitchForLimit < 0.01) pitchForLimit = 1.0;
        int timelineStop = endSongForLimit > anchorForLimit
            ? Mathf.CeilToInt((float)((endSongForLimit - anchorForLimit) / pitchForLimit * fps))
            : 0;
        int hardStop = Math.Max(VideoRender.Current.FramesTotal, timelineStop) + 60 * fps;
        int tailFrames = Mathf.CeilToInt(tailSeconds * fps);
        int endFrame = -1;
        bool wasPlaying = SpectreState.is_playing;

        while (frameIndex < hardStop && !cancelRequested && !sceneExited)
        {
            // 回读在途满时先排空再继续（反压）
            while (frames.InFlight >= frames.MaxInFlight && !cancelRequested)
            {
                Flush();
                if (frames.InFlight < frames.MaxInFlight)
                    break;
                yield return null;
            }
            if (cancelRequested) break;

            yield return new WaitForEndOfFrame();

            // 编辑器/后台聚焦策略可能暂停全局音频，渲染期间强制开启（离线混音依赖它）
            AudioListener.pause = false;

            // ── 手动提前结束（渲染页的「提前结束键」）──────────────────────
            // 与 cancelRequested 是**两件不同的事**：后者是「中止、不留成品」
            // （Esc / 离开试玩 / 时间轴被重置），走 NotifyCanceled 丢弃一切；
            // 这里玩家要的恰恰是成品——按到哪就导出到哪，状态必须是 Completed。
            // 所以只钉 endFrame（循环的唯一下一出口），绝不碰 cancelRequested。
            //
            // 钉在当前帧而不是加尾巴：设置项的语义是「导出到按键那一刻」，
            // 加 tailSeconds 会让成片比玩家看到的终点长几秒，无从解释。
            // 这一帧本身仍会被 CaptureFrame/Grab 采到（endFrame 在 :482 才生效）。
            if (StopRequestedByHotkey())
            {
                endFrame = frameIndex;
                Debug.Log("[SpectreReplay] render stopped by hotkey at frame " + frameIndex
                    + " (" + (frameIndex / (double)fps).ToString("0.00") + "s)");
            }

            // 结算转场等场景切换后相机已销毁，提前收尾
            if (scrCamera.instance == null && endFrame < 0)
                endFrame = frameIndex + Mathf.Min(tailFrames, fps);

            // 整屏模式下窗口尺寸变化即中止（帧序列无法延续）
            if (frames.ScreenResized)
            {
                NotifyFailed(LocalizationManager.GetLocalizedText("rendervideo.resolution_changed"));
                yield break;
            }

            // conductor 是开局时抓的局部引用，但捕获循环跨结算转场持续运行：转场期间
            // conductor 或其 song 可能先被销毁（下面读 pitch 会 NRE，且协程异常会绕过
            // NotifyFailed，让作业卡在 Captifying 直到用户手动取消）。与相机同理按
            // "提前收尾"处理，不当作致命错误；这一帧照常推进时钟与捕获，画面已是结算。
            if (conductor != null && conductor.song != null)
            {
                if (useRenderClock)
                    clock.Advance();
                double elapsed = useRenderClock ? clock.Time : clockSeconds;
                double pitch = conductor.song.pitch;

                if (frameIndex < fps * 10 && reAnchors < 4)
                {
                    double unpinnedNow = (AudioSettings.dspTime - conductor.dspTimeSong
                        - (double)scrConductor.calibration_i) * pitch - conductor.addoffset;
                    double wantPin = anchorSongPosition + elapsed * pitch;
                    if (Math.Abs(wantPin - unpinnedNow) > 0.75)
                    {
                        anchorSongPosition = unpinnedNow - elapsed * pitch;
                        reAnchors++;
                        Debug.Log("[SpectreReplay] timeline re-anchored: was " + wantPin.ToString("0.000")
                            + "s, game at " + unpinnedNow.ToString("0.000") + "s (attempt " + reAnchors + ")");
                    }
                }

                VideoRender.PinnedSongPosition = anchorSongPosition + elapsed * pitch;
                // 视频背景确定性化：按渲染时钟/真实耗时比值连续调速（VideoPlayer
                // 原生走实时时钟，渲染慢于实时时背景视频会跑 ahead）
                if (useRenderClock)
                    StepPinnedVideos(clock.Time);
            }
            else
            {
                if (endFrame < 0)
                    endFrame = frameIndex + Mathf.Min(tailFrames, fps);
                if (useRenderClock)
                    clock.Advance();
            }

            mixdown.CaptureFrame();
            frames.Grab(frameIndex);
            frameIndex++;

            // 音频游标（真实捕获进度）单独记账：它是 drift 巡检的**唯一权威指标**。
            // 时钟（clockSeconds）无论来自确定性时钟还是音频游标，都不能兼作诊断基准
            // ——否则 drift 退化成"时钟减自己"的恒等式，永远 0ms，失锁将无法被发现。
            double audioSeconds = mixdown.Seconds;
            if (audioSeconds <= 0.0)
                audioSeconds = frameIndex / (double)fps;
            else if (audioSeconds < clockSeconds - 0.25)
                audioSeconds = clockSeconds;
            clockSeconds = Math.Max(clockSeconds, audioSeconds);

            UnscaledDeltaPin.TickFrame(); // 虚拟时钟推进（见 VideoRenderPatches.UnscaledDeltaPin）

            if (frameIndex % (fps * 5) == 0)
            {
                double ideal = frameIndex / (double)fps;
                // **这一段必须自己做空值防护。** 上面 :261 已经因为"跨结算转场时
                // conductor/song 会被销毁"而加了同样的判断，这里漏了：一旦转场后
                // 恰好撞上 5 秒巡检，NRE 会被 Drive() 接住并转成 NotifyFailed ——
                // 结果是**一行诊断日志把整场渲染判死**，而且用户看到的是一句英文
                // "Object reference not set..."，跟渲染本身毫无关系。
                // 诊断只该是诊断：取不到就少打两个字段，绝不该影响作业。
                bool live = conductor != null && conductor.song != null;
                double unpinned = live
                    ? (AudioSettings.dspTime - conductor.dspTimeSong - (double)scrConductor.calibration_i)
                    * conductor.song.pitch - conductor.addoffset
                    : double.NaN;
                Debug.Log($"[SpectreReplay] renderClock f={frameIndex} audio={clockSeconds:0.0000}s ideal={ideal:0.0000}s "
                        + $"drift={(clockSeconds - ideal) * 1000.0:0.0}ms sr={mixdown.SampleRate} "
                        + (live
                            ? $"pitch={conductor.song.pitch:0.00} pin={VideoRender.PinnedSongPosition:0.000} dspPos={unpinned:0.000}"
                            : $"pitch=- pin={VideoRender.PinnedSongPosition:0.000} dspPos=n/a (conductor gone)"));
            }

            Flush();

            // 回放事件耗尽时回放引擎会自动停止，以此判定进入结尾
            bool playing = SpectreState.is_playing;
            if (endFrame < 0 && wasPlaying && !playing)
            {
                endFrame = frameIndex + tailFrames;
                Debug.Log("[SpectreReplay] replay exhausted at frame " + frameIndex + ", tail to " + endFrame);
            }
            wasPlaying = playing;

            // ── 时间轴终点兜底（**正常收尾的唯一可靠出口**）────────────────
            //
            // 上面那个 is_playing 翻假依赖 PumpInput 把最后一条事件灌完，而
            // PumpInput 挂在 PlayerControl_Update / UpdateInput 上——关卡**通关后
            // 这两条链就不再运行**（进结算态）。于是 is_playing 永远不会翻假，
            // 循环一路跑到 hardStop，用户看到的就是「进度 100% 了一直不结束、
            // 也不保存」。2026-10-23 实测：预估 19831 帧，实际跑到 23526 帧
            // 仍未收尾，最后靠手动停止键才保存。
            //
            // 所以按**时间轴**判定：钉扎位置越过回放终点（+尾巴）就收尾。
            // 这个判据不依赖任何 Update 链是否还在跑。
            if (endFrame < 0 && VideoRender.PinnedSongPosition >= replayEndSong)
            {
                endFrame = frameIndex + tailFrames;
                Debug.Log("[SpectreReplay] replay reached end of timeline at frame " + frameIndex
                    + " (song=" + VideoRender.PinnedSongPosition.ToString("0.000")
                    + "s, end=" + replayEndSong.ToString("0.000") + "s), tail to " + endFrame);
            }

            // 一旦定下收尾帧就置位：之后的 Start_Rewind（官方关卡通关自动转场）
            // 属于自然结束，不能被当作「回放被重新开始」而中止作业。
            if (endFrame >= 0)
                endingCapture = true;

            if (endFrame >= 0 && frameIndex >= endFrame)
                break;
            if (mixdown.Dead)
            {
                NotifyFailed(LocalizationManager.GetLocalizedText("rendervideo.audio_failed")
                    + " (" + mixdown.DeathReason + ")");
                yield break;
            }
        }

        // 捕获循环已结束：之后再有 Start_Rewind 属于正常收尾/重开，不该再中止作业
        VideoRender.CaptureInProgress = false;

        // 排空残余在途回读
        int drainGuard = fps * 5;
        while (frames.InFlight > 0 && drainGuard-- > 0 && !cancelRequested)
        {
            Flush();
            if (frames.InFlight > 0)
                yield return null;
        }

        if (cancelRequested) { NotifyCanceled(); yield break; }

        // hardStop 是防死循环的保险丝（FramesTotal + 60s），不是正常出口。
        // 走到这里说明回放始终没有发出停止信号，endFrame 一直没定下来，循环被硬砍。
        // 若不拦截，下面会照常 mux 出一段**提前截断**的视频，并上报 Completed ——
        // 用户拿到一个看起来成功、实际缺了结尾的文件。宁可明确失败。
        if (frameIndex >= hardStop)
        {
            NotifyFailed(LocalizationManager.GetLocalizedText("rendervideo.capture_overrun")
                + " (" + frameIndex + " / " + VideoRender.Current.FramesTotal + " frames)");
            yield break;
        }

        // ── 合成收尾：**搬到后台线程** ────────────────────────────────────
        //
        // 这一段是整个渲染里最长的同步等待，而它偏偏发生在用户最想看到结果
        // 的那一刻。两处耗时：
        //
        //   ○ FinishVideo 排空：queue.CompleteAdding() 之后要等泵线程把**积压在
        //     队列里的所有帧**全写进 ffmpeg 管道。队列上限约 256 MiB，主线程此时
        //     已停止送帧、无事可做，纯等 —— 编码器跟不上时就是数秒冻结。
        //   ○ MuxWith：起第二个 ffmpeg 把视频与音频合成。即便 -c:v copy 不重编码，
        //     也要把整个成片读一遍、写一遍，几百 MB 就是几秒。
        //
        // 两者都不碰 Unity API（纯 Process / File），所以技术上可以离开主线程。
        // 主线程只保留三件必须在主线程做的事：mixdown.Seal()（写 WAV 头，很快）、
        // 阶段显示、以及结束上报（要调 Notification / 本地化）。
        //
        // **为什么不在这里 yield 分帧而是整段交给工作线程**：排空是「等别人干完活」，
        // 不是「我们自己有活要分摊」。分帧让出只是把冻结切成许多 16ms 的小卡顿，
        // 用户看到的仍是界面不响应；而工作线程能真正做到并行。
        captureClock = null;
        VideoRender.Current.EtaSeconds = -1;
        SetPhase(VideoRender.Phase.Finalizing, "rendervideo.stage.finalizing", Path.GetFileName(outputPath));
        mixdown.Seal();
        if (mixdown.Dead)
        {
            NotifyFailed(LocalizationManager.GetLocalizedText("rendervideo.audio_failed")
                + " (" + mixdown.DeathReason + ")");
            yield break;
        }

        ReleaseGameControl();

        StartFinalizeOnWorker();

        // 等后台收尾。**必须继续驱动协程而不是阻塞等待**：主线程若在这里 Join，
        // 卡顿原样回来了，只是从 ffmpeg 的等待换成了我们自己的等待。
        //
        // 取消**不中断**已在跑的收尾：此时帧已全部写入，杀掉 ffmpeg 会留下一个
        // 损坏的成片，比多等几秒更糟。编码器自身的 10 分钟超时会兜住真的卡死。
        // 所以这里对 cancelRequested 不做任何事——等它自然结束后再按取消上报。
        while (!finalizeDone)
            yield return null;

        if (finalizeError != null)
        {
            // mux 失败 ≠ 全损：视频流此刻已完整编码（捕获耗时的部分已经花完了）。
            // 把 raw 视频 + wav 抢救到输出目录并附手动合成命令，再让异常走正常失败上报。
            // **必须在主线程做**：RescueMuxFailure 读 VideoRender.Current.Output 并写
            // Debug.Log，两者都不是线程安全的。
            if (finalizeErrorFromMux)
                RescueMuxFailure(finalizeError);
            // **不能 `throw finalizeError;`**——那样会把堆栈重置成这一行，
            // 真正出错的位置（在 ffmpeg 排空还是合成里）就丢了。
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(finalizeError).Throw();
        }

        if (cancelRequested) { NotifyCanceled(); yield break; }

        TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.done") + "\n" + outputPath, 5f);
        Debug.Log("[SpectreReplay] replay video complete: " + outputPath);
        ReportEnd(SpectreAPI.RenderEndStatus.Completed, outputPath, null);
    }

    /// <summary>
    /// 把「排空编码器 + 合成音轨」交给工作线程。
    ///
    /// **为什么可以离开主线程**：FinishVideo 与 MuxWith 只碰 Process / File / 队列，
    /// 不读 Unity 的任何状态（相机、时间、音频都已不再需要）。这是把它搬走的
    /// 前提，也是唯一的依据——不是「看起来可以」。
    ///
    /// **为什么用裸 Thread 而不是 Task.Run**：主项目的目标框架是 netstandard2.1，
    /// 而游戏跑在 Mono 上；Thread 是这里唯一确定可用的原语，且我们不需要 Task
    /// 的延续调度。线程是后台线程，游戏退出时不会拖住进程。
    /// </summary>
    private void StartFinalizeOnWorker()
    {
        finalizeDone = false;
        finalizeError = null;
        finalizeErrorFromMux = false;
        VideoEncoder enc = encoder;
        string wav = wavPath;
        finalizeWorker = new System.Threading.Thread(() =>
        {
            try
            {
                enc.FinishVideo();
                try
                {
                    enc.MuxWith(wav);
                }
                catch (Exception muxEx)
                {
                    finalizeErrorFromMux = true;
                    throw muxEx;
                }
            }
            catch (Exception ex)
            {
                finalizeError = ex;
            }
            finally
            {
                finalizeDone = true;
            }
        })
        {
            IsBackground = true,
            Name = "SpectreReplay.VideoFinalize"
        };
        finalizeWorker.Start();
    }

    // mux 失败的止损：TearDown 会无条件清掉临时目录，不抢救就把已编码成品陪葬
    private void RescueMuxFailure(Exception cause)
    {
        try
        {
            string target = VideoRender.Current.Output;
            string dir = Path.GetDirectoryName(target) ?? "";
            string stem = Path.GetFileNameWithoutExtension(target);
            string videoCopy = Path.Combine(dir, stem + "_videoonly" + Path.GetExtension(rawVideoPath));
            string audioCopy = Path.Combine(dir, stem + "_audioonly" + Path.GetExtension(wavPath));
            File.Copy(rawVideoPath, videoCopy, overwrite: true);
            File.Copy(wavPath, audioCopy, overwrite: true);
            // 手动合成命令必须与 MuxWith 的实际参数一致：
            // · webm 只认 Vorbis/Opus，照抄 aac 会让用户拿到 "Could not find tag for codec aac"
            // · 用实际定位到的编码器路径，而不是假定 ffmpeg 在 PATH 上
            bool webm = string.Equals(Path.GetExtension(target), ".webm",
                StringComparison.OrdinalIgnoreCase);
            string audio = webm ? "libopus -b:a 192k -ar 48000" : "aac -b:a 320k";
            Debug.Log("[SpectreReplay] mux 失败但已抢救原始流: " + videoCopy + "\n  " + audioCopy
                + "\n  手动合成: \"" + encoderPath + "\" -i \"" + videoCopy + "\" -i \"" + audioCopy
                + "\" -c:v copy -c:a " + audio + " -ac 2 -shortest \"" + target + "\""
                + "\n  失败原因: " + cause.Message);
        }
        catch (Exception ex)
        {
            Debug.Log("[SpectreReplay] mux 抢救也失败了（原始流随临时目录清理丢失）: " + ex.Message);
        }
    }

    private IEnumerator AwaitReplayAlive()
    {
        // 10 秒真实预算：正常倒计时约 2.1 s，余量留给场景加载慢 / 外部 mod 抢时序
        const double WaitBudgetSeconds = 10.0;
        System.Diagnostics.Stopwatch waitClock = System.Diagnostics.Stopwatch.StartNew();
        while (waitClock.Elapsed.TotalSeconds < WaitBudgetSeconds)
        {
            if (cancelRequested)
                yield break;

            // 编辑器/后台聚焦策略可能暂停全局音频。倒计时与歌曲调度全靠音频时钟，
            // 等待期就把这条按住（捕获循环里同样每帧这么做）
            AudioListener.pause = false;

            scrConductor conductor = ADOBase.conductor;
            scrController controller = scrController.instance;
            bool counting = controller != null && controller.state == States.Countdown;
            bool inPlay = controller != null && controller.state == States.PlayerControl;
            // 检查点开局的状态：从中间砖块起播时，倒计时整段都在这个状态里放完
            // （scrController.OnMusicScheduled 的检查点分支不经过 States.Countdown）
            bool checkpointing = controller != null && controller.state == States.Checkpoint;
            double songNow = conductor != null ? conductor.songposition_minusi : 0.0;

            // 关卡已经在跑（歌已开始 / 已进 PlayerControl）而回放仍未接管：
            // 这是**确定的失败**，不是「还没就绪」。继续空转到 10 秒只会把
            // 真正的现场拖丢，还让用户以为渲染卡住。立即停下并报明原因。
            //
            // **三个条件必须同时成立才早退**：关卡已在跑、回放仍未接管、
            // 且确实有一条校验拒绝记录。少任何一条都保持原语义继续等：
            // Start_Rewind 会连发多次（前一发被拒、后一发成功是常态），
            // 只看其中一条会误杀本来能成功的那些轮次。
            // 检查点开局没有 States.Countdown 这一段（见下方 ② 的说明），把关卡
            // 「已经在跑」的判断补上 Checkpoint，否则从中间起播时回放若始终没接管，
            // 这里会把一次确定的失败拖成 10 秒空转才报超时。
            bool levelRunning = (conductor != null && conductor.hasSongStarted) || counting || inPlay || checkpointing;
            if (!SpectreState.is_playing)
            {
                if (levelRunning && !string.IsNullOrEmpty(Patch_StartRewind.LastReject))
                {
                    playbackRejectReason = Patch_StartRewind.LastReject;
                    yield break;
                }
                yield return null;
                continue;
            }
            if (conductor != null && counting)
            {
                // 下面两个数只进日志、不进判断：报出这段倒计时的长度，方便复测者
                // 核对成片开头是否真的覆盖整段（负数 songposition = 还在歌曲 0 s 之前）。
                int countdownTickCount = Mathf.Max(1, conductor.countdownTicks);
                float countdownSpeed = Mathf.Max(0.01f, conductor.countdownSpeedMultiplier);
                double countdownEnd = (countdownTickCount - 1) * conductor.crotchetAtStart / countdownSpeed;
                Debug.Log("[SpectreReplay] render starts during countdown: songposition=" + songNow.ToString("0.000")
                    + "s countdownEnd=" + countdownEnd.ToString("0.000") + "s ticks=" + countdownTickCount
                    + " (state=Countdown)");
                playbackLive = true;
                yield break;
            }

            bool freshStartPosition = songNow >= 0.0 && songNow < 1.0;
            if (conductor != null && conductor.hasSongStarted
                && (checkpointing || inPlay || freshStartPosition))
            {
                Debug.Log("[SpectreReplay] render starts at song live: songposition=" + songNow.ToString("0.000")
                    + "s state=" + (controller != null ? controller.state.ToString() : "<无控制器>")
                    + (checkpointing ? " (检查点前导倒计时，非 Countdown 状态)" : ""));
                playbackLive = true;
                yield break;
            }

            bool positiveNow = conductor != null && songNow > 0.0;
            if (inPlay && positiveNow)
            {
                if (sawPositivePosition)
                {
                    playbackLive = true;
                    yield break;
                }
                sawPositivePosition = true;
            }
            else
            {
                // 允许一次抖动：只在"回到非正/非播放态"时清零，避免被单帧
                // State 切换（PlayerControl 与 Countdown 都可能闪一下）骗过去
                sawPositivePosition = false;
            }
            yield return null;
        }

        if (string.IsNullOrEmpty(playbackRejectReason))
            playbackRejectReason = LocalizationManager.GetLocalizedText("rendervideo.replay_not_taken");
        var blocks = SpectreAPI.SnapshotBlocks();
        var blockText = new System.Text.StringBuilder();
        foreach (var kv in blocks)
        {
            if (blockText.Length > 0) blockText.Append("; ");
            blockText.Append(kv.Key).Append(':').Append(kv.Value);
        }
        Debug.Log("[SpectreReplay] 等待回放开始超时，现场状态："
            + " waited=" + waitClock.Elapsed.TotalSeconds.ToString("F1") + "s"
            + " PlayMode=" + SpectreState.PlayMode
            + " RecordMode=" + SpectreState.RecordMode
            + " is_playing=" + SpectreState.is_playing
            + " songLive=" + (ADOBase.conductor != null && ADOBase.conductor.hasSongStarted)
            + " state=" + (scrController.instance != null ? scrController.instance.state.ToString() : "<无控制器>")
            + " blocks=" + (blockText.Length == 0 ? "无" : blockText.ToString()));
    }

    // 按序交付已完成的帧；失败的帧用黑帧顶替
    private void Flush()
    {
        while (frames.TryTake(out int index, out byte[] pixels, out bool failed))
        {
            if (failed || pixels == null)
            {
                Debug.LogWarning("[SpectreReplay] readback failed at frame " + index + ", black frame substituted");
                encoder.Submit(blackout, blackout.Length, 1, null, () => cancelRequested);
            }
            else
            {
                encoder.Submit(pixels, pixels.Length, 1, frames.Recycle, () => cancelRequested);
            }
            VideoRender.Current.FramesDone = ++delivered;
            // 剩余时间 = 未交付帧数 ÷ 捕获以来平均交付速率；
            // 前半秒速率未成形（首帧含回读建立开销）时不给估计，避免开局跳变
            if (captureClock != null && delivered > 0)
            {
                double elapsedSeconds = captureClock.Elapsed.TotalSeconds;
                double rate = delivered / elapsedSeconds;
                VideoRender.Current.EtaSeconds = elapsedSeconds >= 0.5 && rate > 0.01
                    ? Math.Max(0.0, (VideoRender.Current.FramesTotal - delivered) / rate)
                    : -1;
            }
        }
    }

    /// <summary>
    /// 渲染中「提前结束键」是否在本帧被按下。
    ///
    /// 键值来自配置（<see cref="Settings.RenderStopKey"/>），可能被手改成任意整数，
    /// 所以**先 <see cref="Enum.IsDefined"/> 再转换**：裸 <c>(KeyCode)int</c> 转出来的
    /// 越界枚举喂进 <c>Input.GetKeyDown</c> 会抛 <c>ArgumentException</c>，而这里在捕获
    /// 协程里——异常会被 Drive() 接住转成 NotifyFailed，用户看到一句与键位毫无关系的
    /// 报错，整场渲染白跑。宁可当「没按」处理（返回 false），设置页里也能重新选一次。
    ///
    /// 不额外排除 Escape：它根本走不到这里。游戏按 Esc 会走 <c>scnEditor.SwitchToEditMode</c>，
    /// 被 <see cref="SpectreReplay.Features.Replay.Patch_SwitchToEditMode"/> 转成
    /// <see cref="CancelForSceneExit"/> 置上 <c>cancelRequested</c>，而捕获循环在轮询本方法
    /// **之前**（:334）就按取消跳出了。也就是说 Esc 永远是「中止、不留成品」。
    /// 改绑界面同样绑不上它：<c>OnGui.RowHotkey</c> 在等待按键期间把 Esc 当「取消」吃掉。
    /// </summary>
    private static bool StopRequestedByHotkey()
    {
        if (!Settings.RenderStopOn)
            return false;
        int key = Settings.RenderStopKey;
        if (!Enum.IsDefined(typeof(KeyCode), key))
            return false;
        return Input.GetKeyDown((KeyCode)key);
    }

    /// <summary>
    /// 回放终点的歌曲时间（绝对秒）。取两者较大值：
    ///   ① 谱面终点砖的 <c>entryTime</c>（<c>data.EndTile</c>，缺省用最后一砖）；
    ///   ② 最后一个按键事件的 <c>SongPosition</c>。
    ///
    /// <para><b>② 不能省。</b>长条/间奏的尾巴不在 <c>entryTime</c> 里，而
    /// <c>EndTile</c> 也可能早于实际最后按键。只按 ① 算会让终点偏早——
    /// 这正是「进度条 100% 了还在渲染」的成因之一。</para>
    /// </summary>
    private static double ReplayEndSongPosition()
    {
        double songEnd = 0.0;
        try
        {
            var floors = ADOBase.lm?.listFloors;
            if (floors != null && floors.Count > 1)
            {
                int end = floors.Count - 1;
                if (SpectreState.data.EndTile is int et && et >= 0 && et < floors.Count)
                    end = et;
                songEnd = floors[end].entryTime;
            }
            var keys = SpectreState.data?.KeyEvents;
            if (keys != null && keys.Count > 0)
                songEnd = Math.Max(songEnd, keys[keys.Count - 1].SongPosition);
        }
        catch
        {
            // 纯估算：取不到就沿用已有值
        }
        return songEnd;
    }

    private int EstimateFrames(int fps, float tailSeconds)
    {
        double songEnd = ReplayEndSongPosition();
        if (songEnd <= 0.0) songEnd = 60.0;
        float pitch = ADOBase.conductor?.song != null ? ADOBase.conductor.song.pitch : 1f;
        double start = ADOBase.conductor != null ? ADOBase.conductor.songposition_minusi : 0.0;
        double gameSeconds = Math.Max(1.0, (songEnd - start) / Math.Max(0.01f, pitch)) + tailSeconds + 0.5;
        return Math.Max(1, Mathf.CeilToInt((float)gameSeconds * fps));
    }

    private string BuildOutputPath(VideoEncoder.OutContainer container)
    {
        string dir = string.IsNullOrEmpty(Settings.RenderOutputDir)
            ? Path.Combine(Settings.SavePath, "RenderVideos")
            : Settings.RenderOutputDir;
        // 原为 catch { } 完全静默：输出目录不可创建/不可写（用户自填路径、权限不足、
        // 路径含非法字符）时这里什么都不说，最终用户只看到"渲染失败"的通知，
        // 根因要靠猜。渲染本身会带着 ffmpeg 的错误结束，流程照旧往下走，
        // 这里只负责把根因写进日志。
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            Debug.Log("[SpectreReplay] 渲染输出目录无法创建: \"" + dir + "\" —— "
                + ex.GetType().Name + ": " + ex.Message
                + "（请检查 设置→视频渲染 的输出目录是否可写、路径是否合法）");
        }
        string song = SpectreState.data.SongName is string s && !string.IsNullOrEmpty(s)
            ? s : "Replay";
        // 谱面作者常用 TMP 富文本做渐变歌名（逐字母 <color=#…>）——必须先剥标签再消毒：
        // 否则标签括号被逐字符替换成下划线，文件名膨胀数百字符，超出 MAX_PATH 后 ffmpeg
        // 打开输出直接 EINVAL(-22)（真实案例：~600 字符名 → mux 失败）。与 CreateFileName 同序
        song = System.Text.RegularExpressions.Regex.Replace(song, "<[^>]*>", "");
        song = Replay.ReplayManager.RemoveInvalidPathChars(song).Trim('_', ' ');
        if (song.Length == 0) song = "Replay";
        string ext = VideoEncoder.ExtensionOf(container);
        // MAX_PATH 防线：总路径预算 250，扣除目录/时间戳前缀/扩展名/冲突后缀占位
        int budget = 250 - dir.Length - ext.Length - 40;
        if (budget < 16) budget = 16;
        if (song.Length > budget)
            song = song.Substring(0, budget).TrimEnd('_', ' ');
        string name = "Render_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + song;
        string path = Path.Combine(dir, name + ext);
        int suffix = 1;
        while (File.Exists(path))
            path = Path.Combine(dir, name + "_" + suffix++ + ext);
        return path;
    }

    private void PrepareWorkspace(VideoEncoder.OutContainer container)
    {
        tempDir = Path.Combine(Main.mod.Path, "RenderTemp");
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);
        }
        catch (Exception ex)
        {
            Debug.Log("[SpectreReplay] 渲染临时目录重置失败: \"" + tempDir + "\" —— "
                + ex.GetType().Name + ": " + ex.Message);
        }
        // 中间文件按容器选扩展：muxer 按扩展名选，VP9 流写进 .mp4 在 ffmpeg 4.x 是
        // experimental（拒绝写入，渲染首帧即死）。.mkv（matroska）VP9/H.264 通吃；
        // 非 webm 目标编码器写的是 H.264，.mp4 中间文件各版本皆宜
        rawVideoPath = Path.Combine(tempDir,
            container == VideoEncoder.OutContainer.WebM ? "temp_video.mkv" : "temp_video.mp4");
        wavPath = Path.Combine(tempDir, "audio.wav");
    }

    private void SaveGlobalState(int fps)
    {
        savedCaptureFramerate = Time.captureFramerate;
        savedTargetFrameRate = Application.targetFrameRate;
        savedVSyncCount = QualitySettings.vSyncCount;
        savedRunInBackground = Application.runInBackground;
        savedAudioPause = AudioListener.pause;
        savedCheckpointNum = GCS.checkpointNum;

        // 固定步长 + 解除垂直同步 + 后台继续运行（渲染耗时较长，切窗口不中断）
        Time.captureFramerate = fps;
        UnscaledDeltaPin.Install(); // unscaledDeltaTime 钉到虚拟帧长（见 VideoRenderPatches）
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = Math.Max(1000, fps * 4);
        Application.runInBackground = true;
        AudioListener.pause = false;
    }

    private void RestoreGlobalState()
    {
        // **逐项兜底，不能串行裸调。** 这里恢复的全是 Unity 全局状态，而渲染作业常在
        // 场景切换途中收尾，任一 API 抛异常都会让后面的项整批跳过：比如卡在
        // AudioListener.pause 之前就留下永久静音，卡在 vSyncCount 之前就留下异常帧率，
        // 且都没人再恢复。TearDown 只能兜住"整个方法抛了"，兜不住这种中途截断。
        void Try(string name, Action restore)
        {
            try { restore(); }
            catch (Exception ex)
            {
                Debug.Log("[SpectreReplay] render global state restore failed at "
                    + name + ": " + ex.Message);
            }
        }
        Try("clock", () => UnscaledDeltaPin.Uninstall());
        Try("captureFramerate", () => Time.captureFramerate = savedCaptureFramerate);
        Try("targetFrameRate", () => Application.targetFrameRate = savedTargetFrameRate);
        Try("vSyncCount", () => QualitySettings.vSyncCount = savedVSyncCount);
        Try("runInBackground", () => Application.runInBackground = savedRunInBackground);
        Try("audioPause", () => AudioListener.pause = savedAudioPause);
        Try("checkpointNum", () => GCS.checkpointNum = savedCheckpointNum);
    }

    private void SetPhase(VideoRender.Phase phase, string stageKey, string detail)
    {
        VideoRender.CurrentPhase = phase;
        VideoRender.Current.Stage = LocalizationManager.GetLocalizedText(stageKey);
        VideoRender.Current.Detail = detail;
    }

    private void NotifyFailed(string message)
    {
        VideoRender.Current.Error = message;
        TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.failed") + "\n" + message, 5f, NotifType.Warning);
        Debug.Log("[SpectreReplay] replay render failed: " + message);
        ReportEnd(SpectreAPI.RenderEndStatus.Failed, null, message);
    }

    private void NotifyCanceled()
    {
        // **捕获中途回放被重置（关卡重开）不是「取消」。** 这一条必须在唯一的
        // 收口处判，而不是散在四个调用点：所有取消路径最终都走到这里。
        // 报成「已取消」会让用户以为是自己点了什么，日志里也对不上现场。
        if (playbackRestarted)
        {
            string reason = LocalizationManager.GetLocalizedText("rendervideo.playback_restarted");
            VideoRender.Current.Error = reason;
            TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.failed") + "\n" + reason,
                5f, NotifType.Warning);
            ReportEnd(SpectreAPI.RenderEndStatus.Failed, null, reason);
            return;
        }
        TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.canceled"), 3f, NotifType.Warning);
        ReportEnd(SpectreAPI.RenderEndStatus.Canceled, null, null);
    }

    // 终态恰好记录一次；TearDown 里 CloseJob 之后统一触发（Job 已清空，回调可安全续启新作业）
    private void ReportEnd(SpectreAPI.RenderEndStatus status, string output, string error)
    {
        if (endDetermined) return;
        endDetermined = true;
        endStatus = status;
        endOutput = output;
        endError = error;
        // 把终态同步进快照，界面据此显示「完成 / 失败 / 已取消」。
        // **必须在唯一的收口处写**：四个取消点 + 多个失败点，散着写必漏。
        // 成功时 Output 才有意义；取消/失败一律清空，否则界面会拿一个
        // 根本没生成的路径显示「渲染完成」。
        VideoRender.Current.Result = status switch
        {
            SpectreAPI.RenderEndStatus.Completed => VideoRender.Outcome.Completed,
            SpectreAPI.RenderEndStatus.Canceled => VideoRender.Outcome.Canceled,
            _ => VideoRender.Outcome.Failed,
        };
        if (status != SpectreAPI.RenderEndStatus.Completed)
            VideoRender.Current.Output = "";
        else
            VideoRender.Current.Output = output ?? "";
        if (!string.IsNullOrEmpty(error))
            VideoRender.Current.Error = error;
    }

    // ReleaseGameControl 是否已执行（幂等）
    private bool controlReleased;

    private void ReleaseGameControl()
    {
        if (controlReleased) return;
        controlReleased = true;

        // **必须停掉回放，否则游戏收不到任何真实键盘输入。**
        //
        // 渲染是走既有回放链启动的（<see cref="SpectreState.PlayActions.SwitchToPlayMode"/>）
        // ——那一开始 is_playing=true，而本作业此前**从不**把它关回去。
        //
        // 后果不是「回放还在跑」这么轻：<c>ManagedInput.Active => is_playing</c>，
        // 而 Patch_RDInput 的 GetMain Postfix 只要 Active 就返回回放数据、
        // 直接改写 __result。于是游戏读到的永远是「回放里最后那一刻的按键」，
        // 真实键盘被完全屏蔽——玩家按什么都没反应，连结算画面「按任意键继续」
        // 都触发不了，只能强退。2026-10-23 实测：渲染成功后卡在关卡里出不去。
        //
        // 位置也必须在这里：要在 ExitPlaytest 之前，因为退编辑器本身
        // 也要读输入。顺序错了照样卡住。
        try { SpectreState.PlayActions.StopPlaying(); }
        catch (Exception ex) { Debug.LogException(ex); }

        // **还要把游戏的输入源切回真实键盘。** 这一步与上面的 StopPlaying 是两件事：
        // 前者让我们的 RDInput 覆盖失效，后者恢复游戏自己的输入源标志。
        //
        // 游戏在 <c>AsyncInputManager.Update</c> 里按状态切换：
        //     bool flag = scrController.instance.state == States.PlayerControl;
        //     keyboardInput.isActive = !flag;      // PlayControl 时【关掉】真实键盘
        //     asyncKeyboard.isActive = flag;       // 同时打开异步键盘
        // 渲染全程都处于 PlayerControl，所以真实键盘被置成 isActive=false。
        // 收尾时若那条 Update 没能把标志改回来（场景切换中 scrController.instance
        // 短暂为空、或异步钩子已停），真实键盘就一直是关的——**而 mouseInput
        // 的 isActive 不在那套切换里，所以鼠标照常可用**。
        // 2026-10-23 实测正是这个组合：鼠标能退出关卡，键盘完全无反应。
        try { RestoreNativeInputSource(); }
        catch (Exception ex) { Debug.LogException(ex); }

        try { frames?.RestoreTargets(); } catch (Exception ex) { Debug.LogException(ex); }
        try { RenderClockSource.ActiveClock = null; } catch (Exception ex) { Debug.LogException(ex); }
        try { UnpinVideoPlayers(); } catch (Exception ex) { Debug.LogException(ex); }
        try { VideoRender.UnpinTimeline(); } catch (Exception ex) { Debug.LogException(ex); }
        try { VideoRender.SpeedOverrideArmed = false; } catch (Exception ex) { Debug.LogException(ex); }
        // UnscaledDeltaPin.Uninstall 与 captureFramerate / vSync / targetFrameRate /
        // runInBackground / audioPause / checkpointNum 的恢复都在这里，它自带逐项兜底。
        RestoreGlobalState();
        ExitPlaytest();
    }

    /// <summary>
    /// 把游戏的输入源切回真实键盘，并清掉异步层里残留的按住状态。
    ///
    /// <para><b>为什么必须显式做。</b>游戏在 <c>AsyncInputManager.Update</c> 里
    /// 按 <c>state == States.PlayerControl</c> 切换输入源：进入玩法时把
    /// <c>keyboardInput.isActive</c> 置假、<c>asyncKeyboard.isActive</c> 置真；
    /// 离开玩法时反过来。渲染全程钉在玩法态，收尾又发生在场景切换途中
    /// （此时 <c>scrController.instance</c> 可能已为空，那条 Update 直接跳过），
    /// 于是开关会停在「真实键盘关闭、异步键盘开启」这一侧。</para>
    ///
    /// <para><b>外观上的症状很具迷惑性</b>：<c>mouseInput.isActive</c> 不在那套
    /// 切换里，所以鼠标完全正常，只有键盘失灵——玩家会以为「键盘卡了」，
    /// 而不是「输入源没切回来」。2026-10-23 实测：只能靠鼠标退出关卡。</para>
    ///
    /// <para>同时要清 <c>keyMask / keyDownMask / keyUpMask</c>：那是回放期间
    /// 合成事件的落脚点，渲染最后一帧按住的键会留在里面。只切 isActive 的话，
    /// 那些键在异步层看来仍「按着」，下一次进入玩法会被当成一直按住。</para>
    /// </summary>
    private static void RestoreNativeInputSource()
    {
        if (RDInput.keyboardInput != null) RDInput.keyboardInput.isActive = true;
        if (RDInput.keyboardLeft != null) RDInput.keyboardLeft.isActive = true;
        if (RDInput.keyboardRight != null) RDInput.keyboardRight.isActive = true;
        if (RDInput.asyncKeyboard != null) RDInput.asyncKeyboard.isActive = false;
        if (RDInput.asyncKeyboardLeft != null) RDInput.asyncKeyboardLeft.isActive = false;
        if (RDInput.asyncKeyboardRight != null) RDInput.asyncKeyboardRight.isActive = false;
        // 残留的合成按键（含只按下没抬起的孤立事件）必须清掉，否则下次进玩法
        // 会被当成「一直按住」
        try { AsyncInputManager.ClearKeys(); }
        catch (Exception ex) { Debug.LogException(ex); }
    }

    /// <summary>
    /// 退出试玩，回到编辑器。
    ///
    /// **为什么需要它。** 渲染的第一步是把编辑器切进 playtest（<see cref="Phases"/>
    /// 开头的 <c>SwitchToPlayMode</c> + <c>editor.Play()</c>），而收尾链里原本
    /// 没有任何一步把它退回来。于是无论渲染成功、失败还是取消，游戏都留在
    /// playtest 里继续跑——玩家看到的正是「渲染结束了，还在播放」。
    ///
    /// 走的是游戏自己的官方入口：<c>scnEditor.Update</c> 里玩家按 Esc 时调用的
    /// 就是 <c>SwitchToEditMode()</c>。照抄同一条路径，而不是自己拼还原动作
    /// （编辑器要恢复的状态远不止播放标志：UI、音频、控制台、自动播放等）。
    ///
    /// **只在该退的时候退：**
    ///   · <c>playMode</c> 为假 = 玩家已经在渲染期间自己按过 Esc，此时再调一次
    ///     会把编辑器状态重置一遍；
    ///   · <c>ADOBase.editor</c> 为空 = 本次渲染不是在编辑器里发起的（关卡内渲染），
    ///     没有可供退回的编辑器。
    /// </summary>
    private void ExitPlaytest()
    {
        // 玩家已经自己按 Esc 退出（CancelForSceneExit 会置这个标志）：
        // 再调一次 SwitchToEditMode 会把编辑器状态重复重置，而且那次调用
        // 又会反过来触发我们的 Patch_SwitchToEditMode.Prefix ⇒ 递归。
        if (suppressExitPlaytest) return;
        VideoRender.SuppressSceneExitCancel = true;
        try
        {
            scnEditor editor = ADOBase.editor;
            if (editor != null && editor.playMode)
                editor.SwitchToEditMode();
        }
        catch (Exception ex)
        {
            // 退回编辑器失败不该影响渲染终态上报：成片已经合成完了
            Debug.LogException(ex);
        }
        finally
        {
            VideoRender.SuppressSceneExitCancel = false;
        }
    }

    private void TearDown()
    {
        // 幂等：OnDestroy 兜底与协程正常收尾都会走到这里，重复执行会二次
        // Delete 临时目录、二次 Destroy(gameObject)
        if (tornDown) return;
        tornDown = true;
        // **先把游戏交还回去再看收尾。** 正常路径下协程走到这里时已经释放过（幂等），
        // 但 OnDestroy 兜底路径是从半途掐断的——此时游戏可能正带着冻结的钉扎时钟跑，
        // 早一帧释放就少一帧"星球不动"。
        ReleaseGameControl();
        // **先等工作线程收场。** 收尾现在跑在后台线程上（见 StartFinalizeOnWorker）：
        // 它会读 tempDir 里的中间文件、写最终成片。TearDown 若在它还在跑时删临时目录
        // 或 Dispose 编码器，结果是成片写到一半被打断、或者干脆删掉了正在被读的文件。
        //
        // 正常路径走到这里时 finalizeDone 已经是 true（协程的等待循环保证了这点），
        // 这个 Join 立即返回。它兜的是异常路径：用户在 Finalizing 阶段切场景/退关卡，
        // OnDestroy 把协程掐断、而工作线程还在跑。
        //
        // **限时 5 秒，不是 12 分钟。** 这里在主线程上，等 12 分钟等于把
        // 「收尾卡顿」换成「整个游戏假死」——比原问题更糟。正常的收尾（排空 + copy 合成）
        // 是秒级到十几秒；若 5 秒还没完，说明它撞上了慢盘或 ffmpeg 真卡住，
        // 这时正确做法是**放手**：跳过与它冲突的清理，让它自己在后台跑完。
        bool workerStillRunning = false;
        try
        {
            if (finalizeWorker != null && finalizeWorker.IsAlive)
            {
                Debug.Log("[SpectreReplay] teardown: 后台收尾仍在进行，等待最多 5 秒");
                workerStillRunning = !finalizeWorker.Join(TimeSpan.FromSeconds(5));
                if (workerStillRunning)
                    Debug.LogWarning("[SpectreReplay] teardown: 收尾线程未在 5 秒内结束——"
                        + "跳过临时目录清理与编码器释放，交给该线程自己收场");
            }
        }
        catch (Exception ex)
        {
            Debug.Log("[SpectreReplay] teardown: 等待收尾线程失败: " + ex.Message);
        }
        ReleaseGameControl();
        try { frames?.Dispose(); } catch (Exception ex) { Debug.LogException(ex); }
        try { mixdown?.Dispose(); } catch (Exception ex) { Debug.LogException(ex); }
        // **编码器不能在工作线程还在用它时 Dispose。** Dispose 会 Kill ffmpeg 进程，
        // 而那个线程正卡在 FinishVideo/MuxWith 里等同一个进程——提前杀等于把成片毁掉。
        // 工作线程还在跑时把引用交给延迟清理，否则就地释放。
        VideoEncoder deferredEncoder = null;
        string deferredTempDir = null;
        if (!workerStillRunning)
        {
            try { encoder?.Dispose(); } catch (Exception ex) { Debug.LogException(ex); }
        }
        else
        {
            // **必须在这里抓引用。** 下面几行会把字段置 null，而延迟清理是在
            // 工作线程结束之后才跑的——它读字段只会读到 null，于是编码器永远不释放，
            // ffmpeg 进程与帧队列一起泄漏。
            deferredEncoder = encoder;
            deferredTempDir = tempDir;
        }
        frames = null;
        mixdown = null;
        encoder = null;
        // 全局状态（Time.captureFramerate / vSync / targetFrameRate / audioPause /
        // checkpointNum）已在上面 ReleaseGameControl 里随 UnscaledDeltaPin 一并恢复，
        // 且那一步是逐项兜底的——这里不再重复，重复只会让"同一状态恢复两次"。
        // 临时目录同理：里面正是工作线程还在读的 raw 视频与 wav。它对 tempDir 的
        // 使用一直持续到 RunAndWait 返回，提前 Delete 会让合成中途失败。
        // 留一个 RenderTemp 目录（几百 MB）远好过毁掉用户等了很久的成片。
        if (!workerStillRunning)
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch
            {
                try
                {
                    // 文件句柄释放可能有延迟，隔半秒重试一次
                    System.Threading.Thread.Sleep(500);
                    Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    Debug.Log("[SpectreReplay] render temp cleanup failed: " + ex.Message);
                }
            }
        }
        else
        {
            // **跳出前面那条“放手”路径后，清理不能就这么算了。** 编码器持有 ffmpeg
            // 进程与帧队列，临时目录里是它正在读的 raw 视频——这些迟早要释放，
            // 只是不能在它还在跑的时候释放。
            //
            // 做法：把清理**挂到工作线程结束之后**，在另一个后台线程里做。
            // 这样既不阻塞主线程，也不泄漏进程/磁盘。
            // （不在这里直接 Kill：工作线程可能正等着这个进程写出成片，
            //   杀掉就是毁掉用户等了很久的结果。）
            DeferCleanupAfterWorker(deferredEncoder, deferredTempDir);
        }
        VideoRender.CloseJob();
        // 兜底：未报过终态的按失败；事件在 Job 清空之后触发，订阅者可安全启动下一个渲染
        ReportEnd(SpectreAPI.RenderEndStatus.Failed, null, null);
        SpectreAPI.RaiseRenderFinished(endStatus, endOutput, endError);
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        TearDown();
    }

    /// <summary>
    /// 主线程等不及收尾线程时的善后：等工作线程结束，再释放编码器、删临时目录。
    ///
    /// **为什么不能就地做**：调用方已经判定工作线程还活着，此刻 Dispose 编码器会
    /// Kill 掉它正在等的那条 ffmpeg 管道，删临时目录会让它正在读的 raw 视频消失
    /// ——两条都是把快做好的成片毁掉。而**为什么不能不做**：编码器持有进程与帧队列，
    /// 临时目录里是几百 MB 数据，留给 GC 是泄漏，不是清理。
    ///
    /// 所以挂到工作线程后面：主线程立刻返回，回收在它结束后于后台完成。
    /// </summary>
    private void DeferCleanupAfterWorker(VideoEncoder deferredEncoder, string deferredTempDir)
    {
        // **必须抓住收尾线程的引用。** 要等的是它，不是这个清理线程自己——
        // 写成 waiter.Join(...) 是让线程 Join 自己，直接死锁（或抛 ThreadStateException），
        // 回收永远不会发生，而这一整条路径正是「主线程等不及」时才走的。
        System.Threading.Thread target = finalizeWorker;
        if (target == null)
        {
            // 理论上不可达（workerStillRunning 为真时它必然存在），但清理路径
            // 不能假设，否则一个空引用就让编码器与临时目录双双泄漏。
            try { deferredEncoder?.Dispose(); } catch (Exception ex) { Debug.LogException(ex); }
            return;
        }
        var waiter = new System.Threading.Thread(() =>
        {
            try
            {
                // 上限与编码器自身的超时对齐：FinishVideo 的 WaitForExit 是 10 分钟，
                // MuxWith 走 RunAndWait 的 30 分钟上限，最坏组合留 40 分钟余量。
                if (!target.Join(TimeSpan.FromMinutes(40)))
                {
                    Debug.LogWarning("[SpectreReplay] 延迟清理：收尾线程超时未结束，放弃回收");
                    return;
                }
                try { deferredEncoder?.Dispose(); } catch (Exception ex) { Debug.LogException(ex); }
                if (!string.IsNullOrEmpty(deferredTempDir))
                {
                    try
                    {
                        if (Directory.Exists(deferredTempDir))
                            Directory.Delete(deferredTempDir, true);
                    }
                    catch (Exception ex)
                    {
                        Debug.Log("[SpectreReplay] 延迟清理临时目录失败: " + ex.Message);
                    }
                }
                Debug.Log("[SpectreReplay] 延迟清理完成（收尾线程已结束）");
            }
            catch (Exception ex)
            {
                // 这个线程不再有任何观察者，异常绝不能逃出去
                Debug.Log("[SpectreReplay] 延迟清理异常: " + ex.Message);
            }
        })
        {
            IsBackground = true,
            Name = "SpectreReplay.RenderCleanup"
        };
        waiter.Start();
    }

    // ── VideoPlayer 接管（视频背景确定性化）────────────────────────────────
    // Unity VideoPlayer 走自身实时时钟，不受 Time.captureFramerate / RenderClock 控制。
    //
    // 方案（v4）：连续调速——用渲染时钟 / 真实耗时 的比值驱动 playbackSpeed，
    // 视频以与渲染完全一致的速度播放（渲染慢 3 倍 → 视频 0.33x），零 seek 零跳变。
    //
    // v1（Pause + time seek）：暂停态纹理不更新 → 黑屏
    // v2（Play + 每帧 frame setter）：seek 塞满解码器 → 冻结
    // v3（Play + 偏差>1s 才 seek）：渲染慢于实时时周期性跳回 → 卡帧
    // v4（Play + playbackSpeed 调速）：连续匹配渲染速率 → 平滑

    private System.Diagnostics.Stopwatch videoRealClock;

    private void PinVideoPlayers()
    {
        pinnedVideos.Clear();
        pinnedVideosWerePlaying.Clear();
        // FindObjectsByType(FindObjectsInactive.Include, FindObjectsSortMode.None)：
        // 第二个参数不去重排序（我们只调速不依赖顺序）。
        // FindObjectsInactive.Include 不可省——单参重载默认 Exclude，而视频背景
        // 可能在接管时刻尚未 SetActive(true)，漏掉就会在首次显示时失去调速控制。
        foreach (UnityEngine.Video.VideoPlayer vp in FindObjectsByType<UnityEngine.Video.VideoPlayer>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (vp == null) continue;
            pinnedVideosWerePlaying.Add(vp.isPlaying);
            if (!vp.isPlaying)
                vp.Play();
            pinnedVideos.Add(vp);
        }
        videoRealClock = System.Diagnostics.Stopwatch.StartNew();
        if (pinnedVideos.Count > 0)
            Debug.Log("[SpectreReplay] renderClock: " + pinnedVideos.Count + " VideoPlayer(s) speed-matched to render clock");
    }

    private void StepPinnedVideos(double gameSeconds)
    {
        if (pinnedVideos.Count == 0 || videoRealClock == null) return;
        double realSeconds = videoRealClock.Elapsed.TotalSeconds;
        if (realSeconds < 0.1) return; // 起步阶段比值不稳定，先不动

        // 渲染时钟 / 真实耗时：渲染慢于实时 → <1（视频减速），快于实时 → >1（视频加速）
        float speed = Mathf.Clamp((float)(gameSeconds / realSeconds), 0.1f, 10f);

        for (int i = 0; i < pinnedVideos.Count; i++)
        {
            UnityEngine.Video.VideoPlayer vp = pinnedVideos[i];
            if (vp == null) continue;
            try
            {
                // 平滑过渡（避免速率突变造成视觉跳动）
                float current = vp.playbackSpeed;
                float smoothed = Mathf.Lerp(current, speed, 0.1f);
                if (Mathf.Abs(smoothed - current) > 0.001f)
                    vp.playbackSpeed = smoothed;
            }
            catch { /* 纯装饰，失败静默 */ }
        }
    }

    private void UnpinVideoPlayers()
    {
        for (int i = 0; i < pinnedVideos.Count; i++)
        {
            UnityEngine.Video.VideoPlayer vp = pinnedVideos[i];
            if (vp == null) continue;
            try
            {
                vp.playbackSpeed = 1f; // 恢复正常速度
                if (pinnedVideosWerePlaying.Count > i && !pinnedVideosWerePlaying[i] && vp.isPlaying)
                    vp.Pause();
            }
            catch { /* 纯装饰恢复，失败静默 */ }
        }
        pinnedVideos.Clear();
        pinnedVideosWerePlaying.Clear();
    }
}
