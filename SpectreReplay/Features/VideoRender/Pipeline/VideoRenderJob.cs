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

    // ── 收尾后台化 ───────────────────────────────────────────────────
    // 收尾（排空编码器 + 合成音轨）是整场渲染里最长的一段同步等待，原先直接压在
    // 主线程上，而它偏偏发生在用户最想看到结果的那一刻：画面完全冻死。
    // 见 StartFinalizeOnWorker 的注释。
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
        cancelRequested = true;
        playbackRestarted = true;
        Debug.LogWarning("[SpectreReplay] render aborted: playback restarted during capture "
            + "(timeline anchor invalidated)");
    }

    private bool suppressExitPlaytest;
    private bool playbackRestarted;
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

        // **起点由玩家决定，不再强制跳到录像的 data.StartTile。**
        // 需求是「从任意位置开始渲染」，所以起点取玩家点渲染时所在的砖块
        // （requestedStartFloor，已在重启前抓取）。回放侧本来就支持任意起点：
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
            // 与本文件其余两处音频失败（L328/L352）保持同一写法：本地化标签 + 括号里的技术细节。
            // 原来这里直接拼 "AudioRenderer: " 前缀，而 DeathReason 本身多数以 "AudioRenderer." 开头，
            // 用户会看到 "AudioRenderer: AudioRenderer.Start returned false" 这样的重复英文。
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

        // **锚点直接取游戏自己的 songposition_minusi。**
        // 原来从 dspTime 反推：`(dspTime - dspTimeSong - calibration) * pitch - addoffset`。
        // 那个式子在「歌已经开始」的时刻是恒等的，但它引入了两个我们控制不了的量：
        // dspTimeSong 的检查点平移、以及 dspTime 的缓冲记账滞后。一旦推算时刻落
        // 在歌曲调度尚未定型的位置，锚点就会整体偏掉——实测偏出 44 秒，
        // 回放引擎于是一直等迟迟不来的命中，表现成「回放不播放」。
        // songposition_minusi 是游戏自己维护的权威值，跨检查点、跨 pitch 都已算好，
        // 直接拿来当锚点，两边永远同源。
        double audioAnchor = conductor.songposition_minusi;

        // ── 双轨时钟：Transpiler 可用走确定性 RenderClock，否则走旧音频游标 ──
        // 新轨优势：时钟完全由帧号驱动（帧号÷fps），不受音频缓冲量化抖动影响；
        //           Transpiler 把 scrConductor/scrCountdown 的 dspTime 也换成确定性值，
        //           倒计时/事件调度/直读 dspTime 的路径全部确定性化。
        // 旧轨兜底：Transpiler 挂载失败（游戏改了 Update 方法体）时保持原行为。
        //
        // **必须连 Settings.RenderClockOn 一起判，不能只看 TranspilerReady。**
        // TranspilerReady 是"transpiler 跑过"的一次性事实，全仓只有置 true、没有置回
        // false 的地方（补丁卸载不会清它）。而 2026-10-17 起这条补丁挂了 Feature 开关，
        // 于是存在一个真实的坏序：**开→渲染→关→再渲染**。此时补丁已卸载、
        // scrConductor.Update 恢复读真实 dspTime，而 TranspilerReady 仍是旧 true
        // ⇒ 作业会走确定性轨：一边用帧号算 PinnedSongPosition，一边游戏内部用真实
        // dspTime，两条时间轴混着用，比纯净兜底更糟。
        // 加了开关之后，"补丁当前是否真的挂着"由开关谓词决定，所以这里的就绪条件
        // 必须与那个谓词对齐（开关关 = 视为未就绪 = 走兜底）。
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
        captureClock = System.Diagnostics.Stopwatch.StartNew();
        VideoRender.Current.Detail = Path.GetFileName(outputPath);
        SetPhase(VideoRender.Phase.Capturing, "rendervideo.stage.rendering", VideoRender.Current.Detail);

        // 捕获真正开始：之后的 Start_Rewind 二次触发都视为时间轴被重置
        // （见 Patch_StartRewind → NotifyPlaybackRestarted）。
        VideoRender.CaptureInProgress = true;
        int frameIndex = 0;
        int hardStop = VideoRender.Current.FramesTotal + 60 * fps;
        int tailFrames = Mathf.CeilToInt(tailSeconds * fps);
        int endFrame = -1;
        bool wasPlaying = SpectreState.is_playing;

        while (frameIndex < hardStop && !cancelRequested)
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

                // ── 锚点自愈 ───────────────────────────────────────────
                // **检查点开局时，游戏会在捕获开始之后才把时间轴 seek 到检查点位置。**
                // 那一刻之前抓的锚点会整体偏掉，且偏多少恒定（= 检查点的歌曲时间）。
                // 2026-10-19 实测：pin=4.336 而游戏真实位置已到 49.112，差 44.8s ——
                // 回放的命中全排在几十秒之后，表现就是「画面在动、回放不播放」。
                //
                // 抓锚点时无法预知 seek 何时发生（它由游戏内部的 Play/StartMusic 异步
                // 完成），所以这里不猜时机，而是每帧对照游戏的真实位置：
                // 一旦偏差超过阈值就重算锚点，让钉扎值立刻贴回游戏的实际位置。
                //
                // **下面这条 dsp 公式只当「跳变量探针」，不是可靠时钟源。**
                // 它正是我们当初弃用的那个式子（引入了 dspTimeSong 的检查点平移与
                // dspTime 的缓冲记账滞后，正是它导致锚点抓偏）。这里只用它回答一个
                // 粗粒度问题——「游戏的位置是不是突然跳了一个检查点那么远」——
                // 0.75s 的阈值远大于它的全部误差（缓冲量化 ±3ms + 暖机滞后 ~150ms），
                // 所以它当判跳探针够用；**绝不能拿它去算锚点本身**（那会把误差固化）。
                // 锚点一旦重设，用的也是它——但这只在"已经确认跳变"时才发生，
                // 且随后每帧都会继续对照，误差不会被放大累积。
                //
                // 三个约束保证它不会乱跳：
                //   · 只在捕获前 10 秒内生效（seek 只发生在开局）；
                //   · 最多重锚 4 次；
                //   · 阈值 0.75s 远大于探针自身的误差量级。
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

            // 时钟巡检（每 5 秒一行）。唯一权威指标是 drift（音频游标 vs 帧号理想时间）：
            // 恒定 ±数 ms = 正常（dsp 缓冲量化抖动）；随时间增长 = 失锁，渲染不可信。
            // pin 与 dspPos 在起歌段可有恒定差（dspTime 暖机滞后于音频捕获的记账假象，
            // 实测不影响成片音画）——两者恒定即可，不要据此判故障
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

        // **必须在启动后台收尾之前把游戏交还回去。**
        //
        // 这是「收尾搬到后台」引入的一处回归：渲染期有三个虚拟时钟，全都只由上面的
        // 捕获循环推进（PinnedSongPosition / UnscaledDeltaPin / RenderClockSource）。
        // 原先收尾同步阻塞主线程，游戏根本没机会跑，解开钉扎时一切同时恢复；收尾搬到
        // 后台之后，主线程空出来带着三个**再也没人推进的冻结时钟**继续跑游戏循环
        // ⇒ 星球停住、unscaled 动画不动，而进度条已经走完。
        //
        // 收尾本身（ffmpeg 排空 + copy 合成）不读任何 Unity 状态，所以放手是安全的。
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

    // 等回放真正接管：is_playing 只有在 Start_Rewind 校验通过后才会置 true，
    // 以此为准可避免场景重载后控制器空状态（编辑模式等）被误判为已开始。
    //
    // 注意 is_playing 会闪：`Patch_StartRewind.Postfix` 每次都先调
    // `PlayActions.StopPlaying()` 把它清回 false，而编辑器重开关卡时
    // Start_Rewind 会连发多次。所以这里判的是「某一刻已经接管」，不做持续性
    // 假设——真要看它是否稳定，靠下面超时那条现场日志，不靠猜。
    //
    // ── 等待预算按「真实秒数」算，不按帧数 ──────────────────────────
    // 2026-10-20 实测（scnGame 重载 + 谱面自带间隔倒计时）：倒计时还没走完就报了
    // 「等待回放开始超时」，现场是 is_playing=True / songLive=True /
    // state=Countdown。倒计时本身没坏，是**预算算错了**：
    //   · SaveGlobalState 在等待之前就把 Time.captureFramerate=fps、
    //     vSyncCount=0、targetFrameRate≥1000 全设好，等待循环每帧只花亚毫秒级，
    //     旧版 `fps * 10` = 600 帧不到一秒就跑完了；
    //   · 而进 PlayerControl 的判决（scrController.Countdown_Update 要求
    //     conductor.beatNumber 追上 adjustedCountdownTicks）走的是**真实
    //     AudioSettings.dspTime**：dspTimeSong = dspTime + buffer(0.5s)，之后
    //     还要 4 拍（60/145 BPM ≈ 1.66 s），合计 2.1 s 以上。
    // 「按帧等」和「按秒走」对不上，必然误报。同一动作第二次触发（场景已加载、
    // Start_Rewind 更早）又能成功——典型的竞态，不是死锁。
    //
    // **计时器只能用 System.Diagnostics.Stopwatch。** Time.realtimeSinceStartup /
    // Time.unscaledTime(AsDouble) 已被 UnscaledDeltaPin 换成虚拟时钟，而推进它的
    // TickFrame() 只存在于捕获循环里——等待期读这几个值是冻住的，换成它们等于把
    // 「太快误报」改成「永不超时」。
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
            // ── 什么叫「回放真的接管了」─────────────────────────────
            //
            // **不能把成功闸定在 PlayerControl —— 倒计时整段都在它之前。**
            // scrController.Countdown_Update 要 conductor.beatNumber 追上
            // adjustedCountdownTicks（本谱 4 拍 / 145 BPM）才切 PlayerControl；
            // beatNumber 又是靠 songposition 越过 0、1 拍、2 拍、3 拍逐帧 +1
            // （scrConductor.Update:860-865，每帧最多一拍），所以切状态时
            // songposition 已经 ≈ 1.24 s —— 倒计时画面（GET READY / 3 2 1 / GO）
            // 和那 4 声提示音全在它前面。
            //
            // 2026-10-20 的成片 Render_20261020_023143_再生.mp4 开头没有倒计时，
            // 原因就是旧闸把捕获起点卡在 PlayerControl：日志 f=300 时
            // pin=6.236、已录 5.000 s ⇒ audioAnchor = 1.236 s，第一帧已经在
            // 倒计时结束之后。
            //
            // 现在分三条路放行（顺序即优先级）：
            //   ① 倒计时（state==Countdown，第 0 块开局）：**无条件提前放行**，锚点取
            //      当时的 songposition（这段时间它是负数，pin = 负锚点 + 已录秒数，
            //      正好在歌曲 0 s 处过零），倒计时整段进片，4 声提示音也一起进音轨。
            //   ② 歌已开始（conductor.hasSongStarted）：检查点开局唯一可行的闸门。
            //      从中间砖块起播时状态机走 Checkpoint 而不是 Countdown，倒计时在
            //      Checkpoint 里放完，等 Countdown 永远等不到。详见下方 ② 的说明。
            //   ③ PlayerControl + 连续两帧正位置：兜底。挡的是「按检查点 seek 还没把
            //      位置写回」的瞬间，也就是 hasSongStarted 已经置起、但 songposition
            //      仍是上一轮残留值的极端时序。正常上面两路先命中，走不到这里。
            //
            // **倒计时这条路不设任何绝对秒数窗口。** 长度由谱面决定：
            //     (countdownTicks - 1) 拍 × crotchetAtStart(60/BPM) ÷ countdownSpeedMultiplier
            // 本谱 4 拍 / 145 BPM ⇒ 3 × 0.4138 ≈ 1.24 s 后才切 PlayerControl；换 100 BPM
            // 就是 1.8 s，谱面 offset 与音频 buffer（0.5 s）还会把进入前的负段拉得更长。
            // 2026-10-20 那版把下界写死 -2.0 s：offset 一大或 BPM 一慢就晚放行，
            // GET READY 段被切掉（用户当场指出「倒计时不一定只有两秒」）。
            //
            // 真正可靠的信号是**活的状态机**：state==Countdown 由 Countdown_Update
            // 按 beatNumber 逐拍推进（scrConductor.Update:860-865，每帧最多一拍），
            // 最多持续到切换点，不会残留到下一轮；等待循环的轮询频率又远高于帧率，
            // 所以第一次看到它就等于倒计时刚开头。万一发现时已是末尾（场景加载慢），
            // 从当时开始录仍好过落到 PlayerControl 再录——少录一段，而不是整段丢失。
            // ① 倒计时开局（第 0 块，checkpointNum==0）
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

            // ② 任意砖块检查点开局（含「从中间渲染」）：**这条路上没有 States.Countdown。**
            //    scrController.OnMusicScheduled(:1599) 先把 conductor.hasSongStarted 置回
            //    false，检查点分支(:1611) 置 true，紧接着 Scrub(checkpointNum) 把音乐 seek
            //    到 `该砖块 entryTimeAfterExtraBeats − 4 拍`，然后直接
            //    ChangeState(States.Checkpoint)(:1613)。倒计时画面（GET READY / 3 2 1 / GO）
            //    与那 4 拍提示音**全在 Checkpoint 状态里放完**，状态机从不经过 Countdown，
            //    由 Checkpoint_Update 等到 songposition 追上该砖块 entryTime 才进 PlayerControl。
            //    所以用「等 Countdown」当闸门，在中间起播时永远等不到，只能退到
            //    PlayerControl 才放行 —— 那正好是倒计时结束的瞬间，成片开头必然没有倒计时。
            //    上游 Spectre 就是按 hasSongStarted 放行的（VideoRenderJob.cs:416），
            //    这里必须同样处理，否则「从中间渲染」必定丢倒计时。
            //    锚点在放行后仍取 conductor.songposition_minusi（见下方 audioAnchor 的计算），
            //    即 Scrub 之后的权威位置，所以放行点提前不会把时间轴拉歪。
            //    与最后一道兜底的差别只在「状态」：② 认 Checkpoint 和开局首秒，
            //    不认「歌已开始但位置还是上一轮残留值」这种假就绪。
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

        // 超时必须说清当时到底停在哪。没有这几项，日志里只剩一句
        // 「failed: 等待回放开始超时」，既看不出回放是被校验拒了、被别的
        // mod 阻断了，还是根本没进 PlayMode——三种情况的修法完全不同。
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

    private int EstimateFrames(int fps, float tailSeconds)
    {
        double songEnd = 60.0;
        var floors = ADOBase.lm?.listFloors;
        if (floors != null && floors.Count > 1)
        {
            int end = floors.Count - 1;
            if (SpectreState.data.EndTile is int et && et >= 0 && et < floors.Count)
                end = et;
            songEnd = floors[end].entryTime;
        }
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

    /// <summary>
    /// 解除渲染期对游戏的全部干预，把游戏完整交还给它自己。
    ///
    /// **为什么必须是「全部」而不是「一部分」：** 渲染期同时钉住了几套时间源，
    /// 而它们的推进**全都在捕获循环里**（TickFrame / clock.Advance / 给
    /// PinnedSongPosition 赋值）。捕获一停，它们就全冻住：
    ///   1. <c>VideoRender.PinnedSongPosition</c> —— songposition_minusi 的读写补丁
    ///      覆盖（Patch_VideoPinSet / Patch_VideoPinGet）。**星球位置是这个值的
    ///      函数，所以它冻结的直接表现就是星球停住。**
    ///   2. <c>UnscaledDeltaPin</c> —— 五个 Time.* getter 的原生 detour。
    ///   3. <c>RenderClockSource.ActiveClock</c> —— transpiler 改写的 dspTime 读取。
    ///   4. <c>Time.captureFramerate</c> / vSync / targetFrameRate / audioPause 等
    ///      全局状态（RestoreGlobalState 负责逐项兜底恢复）。
    ///   5. 倍速钉扎 —— 不清会泄漏到玩家之后的正常游玩里。
    ///
    /// **半解比没解更糟**：只断时钟却留着 detour，游戏就拿到
    /// 「真实 dspTime + 冻结 unscaled + 冻结 songposition」的混合时间源，
    /// 表现比单纯卡住更难定位。所以这几步必须在同一次调用里走完。
    ///
    /// **调用时机（这是修复的核心）**：捕获循环一结束就调，**早于**后台收尾启动。
    /// 原先一切正常只是因为收尾同步阻塞了主线程、游戏没机会跑；收尾搬到后台后，
    /// 主线程空出来带着冻结时钟继续跑，就出现了「渲染结束后星球停住」。
    ///
    /// 幂等：正常路径（Phases 里调用）与 TearDown 兜底各会调一次。
    /// </summary>
    private void ReleaseGameControl()
    {
        if (controlReleased) return;
        controlReleased = true;
        // 依次兜底：任一 API 抛异常都不能带走后面的项（渲染作业常在场景切换途中收尾）
        // **相机也要一起还。** 捕获期间三台相机的 targetTexture 被指到我们的
        // RenderTexture 上，屏幕上没有相机清屏：世界冻在最后一帧，IMGUI 每帧照常
        // 往上叠，半透明菜单一层层累加 ⇒ 越叠越亮、文字重影（"镜厅"）。
        // 这段窗口是后台收尾（ffmpeg 排空 + mux），几十秒到几分钟。
        // 原先只在 frames.Dispose()（TearDown 里）还原，太晚了。
        try { frames?.RestoreTargets(); } catch (Exception ex) { Debug.LogException(ex); }
        try { RenderClockSource.ActiveClock = null; } catch (Exception ex) { Debug.LogException(ex); }
        try { UnpinVideoPlayers(); } catch (Exception ex) { Debug.LogException(ex); }
        try { VideoRender.UnpinTimeline(); } catch (Exception ex) { Debug.LogException(ex); }
        try { VideoRender.SpeedOverrideArmed = false; } catch (Exception ex) { Debug.LogException(ex); }
        // UnscaledDeltaPin.Uninstall 与 captureFramerate / vSync / targetFrameRate /
        // runInBackground / audioPause / checkpointNum 的恢复都在这里，它自带逐项兜底。
        RestoreGlobalState();
        // **最后退出 playtest。** 渲染的第一步是把编辑器切到 playtest
        // （SwitchToPlayMode + editor.Play），但整条链原先没有任何一步退回去：
        // 渲染结束后游戏仍停在 playtest 里继续跑，表现就是「渲染完了还在播放」。
        // 放在时间源与相机都还原之后：SwitchToEditMode 内部会读 conductor/song，
        // 此时它们已恢复成真实值，不会拿到冻结的钉扎时钟。
        ExitPlaytest();
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

    /// <summary>
    /// 对象被销毁时的兜底收尾。
    ///
    /// 协程在本对象被销毁时会被 Unity 直接终止，<see cref="TearDown"/> 那一行之后
    /// 的代码不会执行 —— 典型触发：渲染到一半切场景 / 退出关卡 / 退主菜单。
    /// 后果有三样，且都很隐蔽：
    ///   1. ffmpeg 进程继续跑（无进程句柄回收），后台占 CPU；
    ///   2. 临时帧目录留在磁盘（几百 MB 起）；
    ///   3. VideoRender.Job 永远非 null —— 而它是"能否启动新渲染"的闸门，
    ///      于是**本局之后再也起不了任何渲染**，只能重启游戏。
    /// TearDown 已做幂等，正常收尾先执行时这里直接返回。
    /// </summary>
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
