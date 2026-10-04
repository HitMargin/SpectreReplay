using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using SpectreReplay.API;
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
        if (!playbackLive)
        {
            NotifyFailed(LocalizationManager.GetLocalizedText("rendervideo.wait_timeout"));
            yield break;
        }
        if (cancelRequested) { NotifyCanceled(); yield break; }

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
        RenderClock clock = null;
        bool useRenderClock = RenderClockSource.TranspilerReady;
        if (useRenderClock)
        {
            clock = new RenderClock(fps);
            clock.AnchorDsp(AudioSettings.dspTime);
            RenderClockSource.ActiveClock = clock;
            Debug.Log("[Spectre] renderClock: deterministic RenderClock path (transpiler active)");
        }
        else
        {
            Debug.Log("[Spectre] renderClock: audio-cursor Pin path (transpiler unavailable, fallback)");
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
                double unpinned = (AudioSettings.dspTime - conductor.dspTimeSong - (double)scrConductor.calibration_i)
                                  * conductor.song.pitch - conductor.addoffset;
                Debug.Log($"[Spectre] renderClock f={frameIndex} audio={clockSeconds:0.0000}s ideal={ideal:0.0000}s "
                          + $"drift={(clockSeconds - ideal) * 1000.0:0.0}ms sr={mixdown.SampleRate} "
                          + $"pitch={conductor.song.pitch:0.00} pin={VideoRender.PinnedSongPosition:0.000} dspPos={unpinned:0.000}");
            }

            Flush();

            // 回放事件耗尽时回放引擎会自动停止，以此判定进入结尾
            bool playing = SpectreState.is_playing;
            if (endFrame < 0 && wasPlaying && !playing)
            {
                endFrame = frameIndex + tailFrames;
                Debug.Log("[Spectre] replay exhausted at frame " + frameIndex + ", tail to " + endFrame);
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

        // 合成（编码器收尾会阻塞主线程，此时画面已不再变化）
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
        encoder.FinishVideo();
        try
        {
            encoder.MuxWith(wavPath);
        }
        catch (Exception muxEx)
        {
            // mux 失败 ≠ 全损：视频流此刻已完整编码（捕获耗时的部分已经花完了）。
            // 把 raw 视频 + wav 抢救到输出目录并附手动合成命令，再让异常走正常失败上报
            RescueMuxFailure(muxEx);
            throw;
        }
        TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.done") + "\n" + outputPath, 5f);
        Debug.Log("[Spectre] replay video complete: " + outputPath);
        ReportEnd(SpectreAPI.RenderEndStatus.Completed, outputPath, null);
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
            Debug.Log("[Spectre] mux 失败但已抢救原始流: " + videoCopy + "\n  " + audioCopy
                + "\n  手动合成: ffmpeg -i \"" + videoCopy + "\" -i \"" + audioCopy
                + "\" -c:v copy -c:a aac -shortest \"" + target + "\""
                + "\n  失败原因: " + cause.Message);
        }
        catch (Exception ex)
        {
            Debug.Log("[Spectre] mux 抢救也失败了（原始流随临时目录清理丢失）: " + ex.Message);
        }
    }

    // 等回放真正接管：is_playing 只有在 Start_Rewind 校验通过后才会置 true，
    // 以此为准可避免场景重载后控制器空状态（编辑模式等）被误判为已开始
    private IEnumerator AwaitReplayAlive(int fps)
    {
        int limit = Math.Max(30, fps * 10);
        for (int i = 0; i < limit; i++)
        {
            if (cancelRequested)
                yield break;
            if (!SpectreState.is_playing)
            {
                yield return null;
                continue;
            }
            scrConductor conductor = ADOBase.conductor;
            scrController controller = scrController.instance;
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
    }

    // 按序交付已完成的帧；失败的帧用黑帧顶替
    private void Flush()
    {
        while (frames.TryTake(out int index, out byte[] pixels, out bool failed))
        {
            if (failed || pixels == null)
            {
                Debug.LogWarning("[Spectre] readback failed at frame " + index + ", black frame substituted");
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
            Debug.Log("[Spectre] 渲染输出目录无法创建: \"" + dir + "\" —— "
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
            Debug.Log("[Spectre] 渲染临时目录重置失败: \"" + tempDir + "\" —— "
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
        UnscaledDeltaPin.Uninstall();
        Time.captureFramerate = savedCaptureFramerate;
        Application.targetFrameRate = savedTargetFrameRate;
        QualitySettings.vSyncCount = savedVSyncCount;
        Application.runInBackground = savedRunInBackground;
        AudioListener.pause = savedAudioPause;
        GCS.checkpointNum = savedCheckpointNum;
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
        Debug.Log("[Spectre] replay render failed: " + message);
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

    private void TearDown()
    {
        // 幂等：OnDestroy 兜底与协程正常收尾都会走到这里，重复执行会二次
        // Delete 临时目录、二次 Destroy(gameObject)
        if (tornDown) return;
        tornDown = true;
        // 新时钟路径：先断开 ActiveClock（Transpiler 的替换函数即刻回落原始值），
        // 再走常规清理——顺序错了收尾期间游戏会多跑一帧确定性时间
        RenderClockSource.ActiveClock = null;
        UnpinVideoPlayers();
        VideoRender.UnpinTimeline();
        try { frames?.Dispose(); } catch (Exception ex) { Debug.LogException(ex); }
        try { mixdown?.Dispose(); } catch (Exception ex) { Debug.LogException(ex); }
        try { encoder?.Dispose(); } catch (Exception ex) { Debug.LogException(ex); }
        frames = null;
        mixdown = null;
        encoder = null;
        RestoreGlobalState();
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
                Debug.Log("[Spectre] render temp cleanup failed: " + ex.Message);
            }
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
        // FindObjectsByType(FindObjectsSortMode.None)：不去重排序（我们只调速不依赖顺序），
        // 顺带消掉 FindObjectsOfType 的过时警告。此处仍需覆盖未激活对象——视频背景
        // 可能在接管时刻尚未 SetActive(true)，漏掉就会在首次显示时失去调速控制。
        foreach (UnityEngine.Video.VideoPlayer vp in FindObjectsByType<UnityEngine.Video.VideoPlayer>(
                     FindObjectsSortMode.None))
        {
            if (vp == null) continue;
            pinnedVideosWerePlaying.Add(vp.isPlaying);
            if (!vp.isPlaying)
                vp.Play();
            pinnedVideos.Add(vp);
        }
        videoRealClock = System.Diagnostics.Stopwatch.StartNew();
        if (pinnedVideos.Count > 0)
            Debug.Log("[Spectre] renderClock: " + pinnedVideos.Count + " VideoPlayer(s) speed-matched to render clock");
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
