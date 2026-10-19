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
        // **只在录像确实处于飙速态时才套用倍速。** 校验式的期望值就是
        // `QuickPitched ? PlaybackSpeed : 1.0`：非飙速录像若把 playerSpeed
        // 写成 1.5，校验仍按 1.0 通过，但 song.pitch 已被拉高——时间轴与录像
        // 对不上，成片从头到尾音画不同步。
        bool quickPitched = SpectreState.data.bools.TryGetValue(QuickPitched, out bool qp) && qp;
        VideoRender.SpeedOverrideQuickPitched = quickPitched;
        VideoRender.SpeedOverride = quickPitched
            && SpectreState.data.doubles.TryGetValue(PlaybackSpeed, out double spd) && spd > 0.0
            ? (float)spd
            : 1f;
        VideoRender.SpeedOverrideArmed = true;

        int startTile = SpectreState.data.ints.TryGetValue(StartTile, out int st) ? st : 0;
        if (ADOBase.editor != null && ADOBase.editor.floors != null && ADOBase.editor.floors.Count > 1)
        {
            // 编辑器统一走官方 Play 路径；Play() 以当前选中砖块为起点，先选中回放起始砖块
            int floor = Mathf.Clamp(startTile, 0, ADOBase.editor.floors.Count - 1);
            ADOBase.editor.SelectFloor(ADOBase.editor.floors[floor], cameraJump: false);
            GCS.checkpointNum = floor;
            ADOBase.editor.Play();
        }
        else
        {
            int floorLimit = ADOBase.lm != null && ADOBase.lm.listFloors != null ? ADOBase.lm.listFloors.Count - 1 : 0;
            GCS.checkpointNum = Mathf.Clamp(startTile, 0, Math.Max(0, floorLimit));
        }

        SetPhase(VideoRender.Phase.WaitingPlayback, "rendervideo.stage.waiting", "");
        playbackLive = false;
        yield return AwaitReplayAlive(fps);
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

        // 播放 schedule 完成后锚定时间线，之后时钟跟随混音游标推进（音画同源）。
        // 锚点从音频时钟直算（与捕获的 WAV 同域），不用 conductor.songposition_minusi——
        // 那是 DesyncFix 平滑过的内部时钟，锚点若落在其收敛途中会带进临时偏差；
        // 实测两源在锚点时刻相差 ≤10ms，此式更稳。
        // 注意：此式复刻自游戏 conductor 的换算（内部 dspTime 换成 AudioSettings.dspTime），
        // 游戏更新若改动该式需同步此处。起歌段 AudioSettings.dspTime 相对音频捕获有
        // 暖机滞后（~150ms 记账假象，实测不影响成片音画，判读见 renderClock 注释）
        double audioAnchor = (AudioSettings.dspTime - conductor.dspTimeSong - (double)scrConductor.calibration_i)
                            * conductor.song.pitch - conductor.addoffset;

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
                {
                    // 确定性路径：帧号驱动一切，时钟严格 = N/fps（零抖动）
                    clock.Advance();
                    // Pin 用确定性时钟（安全网——任何未被 Transpiler 覆盖的 songposition
                    // 读取仍被钉住）；音频游标只用于下面的诊断记账
                    VideoRender.PinnedSongPosition = anchorSongPosition + clock.Time * conductor.song.pitch;
                    // 视频背景确定性化：按渲染时钟/真实耗时比值连续调速（VideoPlayer
                    // 原生走实时时钟，渲染慢于实时时背景视频会跑 ahead）
                    StepPinnedVideos(clock.Time);
                }
                else
                {
                    // 旧路径：时钟跟随混音游标（±2.7ms 为 dsp 缓冲量化抖动）
                    VideoRender.PinnedSongPosition = anchorSongPosition + clockSeconds * conductor.song.pitch;
                }
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
    private IEnumerator AwaitReplayAlive(int fps)
    {
        int limit = Math.Max(30, fps * 10);
        for (int i = 0; i < limit; i++)
        {
            if (cancelRequested)
                yield break;

            // 关卡已经在跑（歌已开始 / 已进 PlayerControl）而回放仍未接管：
            // 这是**确定的失败**，不是「还没就绪」。继续空转到 10 秒只会把
            // 真正的现场拖丢，还让用户以为渲染卡住。立即停下并报明原因。
            //
            // **三个条件必须同时成立才早退**：关卡已在跑、回放仍未接管、
            // 且确实有一条校验拒绝记录。少任何一条都保持原语义继续等：
            // Start_Rewind 会连发多次（前一发被拒、后一发成功是常态），
            // 只看其中一条会误杀本来能成功的那些轮次。
            scrConductor conductor = ADOBase.conductor;
            scrController controller = scrController.instance;
            bool levelRunning = (conductor != null && conductor.hasSongStarted)
                || (controller != null
                    && (controller.state == States.Countdown || controller.state == States.PlayerControl));
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
            if (conductor != null && conductor.hasSongStarted)
            {
                playbackLive = true;
                yield break;
            }
            if (controller != null
                && (controller.state == States.Countdown || controller.state == States.PlayerControl))
            {
                playbackLive = true;
                yield break;
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

    private int EstimateFrames(int fps, float tailSeconds)
    {
        double songEnd = 60.0;
        var floors = ADOBase.lm?.listFloors;
        if (floors != null && floors.Count > 1)
        {
            int end = floors.Count - 1;
            if (SpectreState.data.ints.TryGetValue(EndTile, out int et) && et >= 0 && et < floors.Count)
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
        string song = SpectreState.data.strings.TryGetValue(SongName, out string s) && !string.IsNullOrEmpty(s)
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
        try { RenderClockSource.ActiveClock = null; } catch (Exception ex) { Debug.LogException(ex); }
        try { UnpinVideoPlayers(); } catch (Exception ex) { Debug.LogException(ex); }
        try { VideoRender.UnpinTimeline(); } catch (Exception ex) { Debug.LogException(ex); }
        try { VideoRender.SpeedOverrideArmed = false; } catch (Exception ex) { Debug.LogException(ex); }
        // UnscaledDeltaPin.Uninstall 与 captureFramerate / vSync / targetFrameRate /
        // runInBackground / audioPause / checkpointNum 的恢复都在这里，它自带逐项兜底。
        RestoreGlobalState();
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
