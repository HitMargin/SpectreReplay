using System;
using System.IO;
using UnityEngine;
using SpectreReplay.Patching;
using SpectreReplay.Ui;

namespace SpectreReplay.Features.VideoRender
{
    /// <summary>
    /// 离线渲染：重跑一遍回放，按目标帧率逐帧推进并逐帧捕获。
    ///
    /// ── 怎么钉住时间轴 ──────────────────────────────────────
    /// 目标是让所有系统每帧读到同一个歌曲位置：第 i 帧恒为 i/fps 秒。
    /// 钉点是 <c>scrConductor.songposition_minusi</c> 的**属性访问器**
    /// （读写两端都换），不是 dspTime——原因见 Patch_OfflineClock 的注释：
    /// 改 dspTime 既赶不上 :852 的赋值，又会被下一帧 :779 覆盖回去。
    ///
    /// dspTime 仍被游戏用来排程打击音效/hold 音，那些音效按真实时间发，
    /// 但它们最终落到 AudioRenderer 混音总线，而混音总线的采样节奏
    /// 由 AudioRenderer 自己按帧栅格消费，与画面帧号天然同构。
    ///
    /// ── 为什么这个模式还存在 ──────────────────────────────────
    /// 实时录制已经覆盖了"边玩边出"的场景。离线模式的价值是：
    ///   · 画面与回放完全一致（不依赖当时的掉帧情况）
    ///   · 可以超过实时速度——渲染 3 分钟的曲子不必等 3 分钟
    ///   · 录完可以回看修正，不满意再录一遍
    /// </summary>
    internal sealed class OfflineSession : MonoBehaviour
    {
        private static OfflineSession _instance;

        private FrameSource _frames;
        private VideoEncoder _encoder;
        private FrameBufferPool _pool;
        private int _poolFrameSize;
        private byte[] _lastFrame;
        private bool _capturing;
        private int _frameIndex;
        private int _fps;
        private int _totalFrames;
        private string _outputPath;
        private string _rawVideoPath;
        private string _audioPath;
        private string _tempDir;
        private bool _cancelled;

        /// <summary>
        /// 开录时的墙上时钟（Time.realtimeSinceStartup）。
        ///
        /// 只用来算 ETA，所以允许用 Unity 的时间——这个模式不是实时录制，
        /// 不存在编辑器暂停导致的漂移问题（那才是 LiveSession 坚持用
        /// Stopwatch 的原因，见 LiveSession.cs 的文件头）。
        /// </summary>
        private float _startWall;

        /// <summary>
        /// 离线渲染的音频。编码器是共享的 -an（视频轨不带声音），
        /// 所以必须自己录一份再合回去——**之前这里什么都没有**，
        /// 于是离线渲染出来的视频是彻底无声的，而界面还显示"完成"。
        /// </summary>
        /// 惰性创建：构造器要知道 fps（静默重启的节流按秒算），
        /// 而 fps 要等 Start 才从 Settings 读到。
        private Audio.AudioTrackRecorder _audio;

        internal static bool IsRunning => _instance != null && _instance._capturing;

        /// <summary>钉住用的目标歌曲时间，由 PumpFrame 写入、补丁读取。</summary>
        internal static double PinnedSongPosition { get; set; }

        /// <summary>是否为钉住生效中。补丁据此决定是否接管歌曲位置。</summary>
        internal static bool PinActive { get; private set; }

        /// <summary>
        /// 最近一次游戏**自己算出来的**真实歌曲位置（秒）。
        ///
        /// 钉住之后 <c>scrConductor.songposition_minusi</c> 的读端已被接管，
        /// 从外面读到的一律是钉扎值，"歌放完了没有"这个判断就问不出来了。
        /// 但写端收到的 <c>value</c> 正是游戏本帧按 dspTime 算出的真实位置
        /// （scrConductor.cs:852），在写补丁里把它抄一份下来就够了。
        /// </summary>
        internal static double LastRealSongPosition { get; set; }

        // ── 启动 ────────────────────────────────────────────────
        internal static bool Start(string replayPath)
        {
            if (IsRunning)
            {
                return false;
            }
            if (scrController.instance == null || scrConductor.instance == null
                || scrConductor.instance.song == null)
            {
                RenderStatus.Current = RenderStatus.State.Failed;
                RenderStatus.LastError = Loc.T("render.err.need_level");
                return false;
            }
            if (!Playback.PlaybackEngine.Load(replayPath))
            {
                RenderStatus.Current = RenderStatus.State.Failed;
                RenderStatus.LastError = Playback.PlaybackEngine.LastError;
                return false;
            }
            _instance = new GameObject("SpectreReplayOfflineRender").AddComponent<OfflineSession>();
            UnityEngine.Object.DontDestroyOnLoad(_instance.gameObject);
            return _instance.Setup();
        }

        private bool Setup()
        {
            try
            {
                _tempDir = Path.Combine(Settings.VideoDir, "_tmp");
                Directory.CreateDirectory(_tempDir);
                Directory.CreateDirectory(Settings.VideoDir);

                int w = Settings.Width;
                int h = Settings.Height;
                if ((FrameSurface)Settings.Surface == FrameSurface.WholeScreen)
                {
                    w = Screen.width;
                    h = Screen.height;
                }
                _frames = new FrameSource((FrameSurface)Settings.Surface, w, h);
                w = _frames.Width;
                h = _frames.Height;
                _poolFrameSize = _frames.FrameSize;
                _pool = new FrameBufferPool(_poolFrameSize, null);

                // **目标容器**决定最终产物（用户选的那个）。
                string ext = Settings.Container == 1 ? "mkv"
                    : Settings.Container == 2 ? "webm" : "mp4";
                string stem = "offline_" + DateTime.Now.ToString("HHmmss");
                // **中间产物一律 .mkv**，与目标容器无关。
                //
                // 理由照 Spectre 的 VideoRenderJob.cs:528-532：muxer 按
                // **扩展名**选容器，而选 webm 时中间产物编的是 VP9——
                // VP9 流写进 `.mp4` 在 ffmpeg 4.x 是 experimental
                // （拒绝写入，渲染首帧即死）。`.mkv` 通吃 VP9 与 H.264。
                // 详见 LiveSession 里的同一段说明。
                _rawVideoPath = Path.Combine(_tempDir, stem + ".mkv");
                _outputPath = Path.Combine(Settings.VideoDir, stem + "_final." + ext);

                _encoder = VideoEncoder.Start(_pool, w, h, Settings.Fps,
                    (EncoderMode)Settings.Encoder,
                    (QualityMode)Settings.Quality,
                    (ContainerMode)Settings.Container,
                    _frames.NeedsRowFlip,
                    _rawVideoPath, Settings.FfmpegPath);
                _frames.PendingSink = OnFrameReady;

                // 音频：抓游戏最终混音总线。抓不上就只出无声视频，
                // 而不是让整场渲染失败。
                _fps = Mathf.Max(1, Settings.Fps);
                _audioPath = Path.Combine(_tempDir, stem + ".wav");
                _audio = new Audio.AudioTrackRecorder(_fps);
                if (!_audio.Start(_audioPath))
                {
                    UnityEngine.Debug.LogWarning(
                        "[SpectreReplay] 抓不到混音，本次将产出无声视频："
                        + _audio.DeathReason);
                }

                // 总帧数 = 回放长度 × 帧率，再加收尾几秒
                double len = Playback.PlaybackEngine.ReplayLength + Settings.TailSeconds;
                _totalFrames = Math.Max(1, (int)(len * _fps));

                // 从头重开，让回放从第 0 帧开始
                Playback.PlaybackEngine.ResetCursor();
                try
                {
                    scrController.instance.Start_Rewind();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning("[SpectreReplay] 重开失败，按当前进度开始："
                        + ex.Message);
                }

                _frameIndex = 0;
                _capturing = true;
                _cancelled = false;
                _startWall = Time.realtimeSinceStartup;
                PinActive = true;
                PinnedSongPosition = 0.0;
                // NaN = 还没收到过本帧真实位置。判据要能认出它：
                // 给 0 会被当成"已经结束"，给 -1000 也会（一上来就收尾）。
                LastRealSongPosition = double.NaN;
                RenderStatus.Current = RenderStatus.State.Offline;
                RenderStatus.FramesTotal = _totalFrames;
                RenderStatus.FramesDone = 0;
                RenderStatus.Progress = 0;
                RenderStatus.EtaSeconds = -1;
                RenderStatus.LastError = "";
                UnityEngine.Debug.Log("[SpectreReplay] 离线渲染开始：" + _outputPath
                    + "（" + _totalFrames + " 帧 @ " + _fps + "fps）");
                return true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[SpectreReplay] 离线渲染启动失败：" + ex);
                RenderStatus.Current = RenderStatus.State.Failed;
                RenderStatus.LastError = ex.Message;
                Dispose();
                return false;
            }
        }

        // ── 每帧推进 ────────────────────────────────────────────
        private void Update()
        {
            if (!_capturing)
            {
                return;
            }
            if (_cancelled)
            {
                Stop(true);
                return;
            }
            if (_frameIndex >= _totalFrames)
            {
                Stop(false);
                return;
            }
            // 歌曲已经放完/失败：剩余帧数按"已到结尾"处理，直接收尾。
            // 注意读的是 LastRealSongPosition 而不是 songposition_minusi——
            // 后者的读端已被钉住补丁接管，读出来永远是钉扎值，
            // 这个判断会永远为 false，渲染就收不了尾。
            // NaN 表示这一帧游戏还没写过位置（还没起歌）：不是结束，继续。
            double pos = OfflineSession.LastRealSongPosition;
            if (!double.IsNaN(pos) && pos < -0.5)
            {
                Stop(false);
                return;
            }
            PinnedSongPosition = _frameIndex / (double)_fps;
            PushAudioForFrame();
            _frameIndex++;
            if (_frames.Request())
            {
                // 已排队，等 GPU 回读
            }
            RenderStatus.FramesDone = _frameIndex;
            RenderStatus.Progress = (float)_frameIndex / _totalFrames;
            // 预计剩余秒数：按已跑的平均每帧耗时外推。
            // 这个字段原先**全仓没有任何一处写它**，于是 RenderStatus.Describe()
            // 里那句 `EtaSeconds >= 0 ? "ETA …"` 永远不成立，离线渲染看不到
            // 任何剩余时间。离线一局几分钟，明知道还要等却看不到还剩多久，
            // 是这类工具最典型的"功能在、但没用"。
            double elapsed = Time.realtimeSinceStartup - _startWall;
            RenderStatus.EtaSeconds = elapsed > 0.05
                ? elapsed / Math.Max(1, _frameIndex)
                    * Math.Max(0, _totalFrames - _frameIndex)
                : -1;
        }

        /// <summary>
        /// 抓这一帧的混音。
        ///
        /// AudioRenderer 按真实时间累积样本，每帧消费一次累积量——
        /// 音频总量 = 帧数 / fps，与视频栅格天然同构，不需要对齐计算。
        /// 必须与帧捕获在同一迭代内，保证第 N 次抓到的对应第 N 帧图像。
        /// </summary>
        private void PushAudioForFrame()
        {
            _audio?.CaptureFrame();
        }

        private void OnFrameReady(byte[] pixels, int w, int h)
        {
            if (!_capturing || pixels.Length != _poolFrameSize)
            {
                return;
            }
            _lastFrame = pixels;
            _encoder.Submit(pixels);
        }

        // ── 收尾 ────────────────────────────────────────────────
        private void Stop(bool cancelled)
        {
            if (!_capturing)
            {
                return;
            }
            _capturing = false;
            PinActive = false;
            Playback.PlaybackEngine.Stop();
            RenderStatus.Current = cancelled ? RenderStatus.State.Cancelled
                : RenderStatus.State.Finalizing;
            try
            {
                bool ok = _encoder != null && _encoder.Finish();
                _frames?.Dispose();
                _frames = null;
                if (!ok)
                {
                    RenderStatus.Current = RenderStatus.State.Failed;
                    RenderStatus.LastError = _encoder?.Error ?? Ui.Loc.T("render.err.encoder_exit");
                    UnityEngine.Debug.LogError("[SpectreReplay] 离线编码失败："
                        + RenderStatus.LastError);
                    return;
                }
                // 音频封口 + 合流。Seal() 回写真实 wav 长度（头里的长度是占位 0 时，
                // ffmpeg 按头读会认为音频是空的）。
                string finalPath;
                bool audioOk = _audio != null
                    && !_audio.Dead
                    && _audio.Seal()
                    && _audio.Seconds > 0.05;
                if (audioOk)
                {
                    finalPath = AudioMux.Mux(Settings.FfmpegPath, _rawVideoPath,
                        _audioPath, _outputPath, (ContainerMode)Settings.Container);
                    UnityEngine.Debug.Log("[SpectreReplay] 音频已合入，时长 "
                        + _audio.Seconds.ToString("0.00") + "s");
                }
                else
                {
                    if (_audio != null && _audio.Dead)
                    {
                        UnityEngine.Debug.LogWarning(
                            "[SpectreReplay] 混音失败，本次产出无声视频："
                            + _audio.DeathReason);
                    }
                    try
                    {
                        File.Copy(_rawVideoPath, _outputPath, true);
                        finalPath = _outputPath;
                    }
                    catch
                    {
                        finalPath = _rawVideoPath;
                    }
                }
                if (cancelled)
                {
                    RenderStatus.Current = RenderStatus.State.Cancelled;
                }
                else
                {
                    RenderStatus.Current = RenderStatus.State.Done;
                    RenderStatus.LastOutput = finalPath;
                }
                UnityEngine.Debug.Log("[SpectreReplay] 离线渲染结束：" + finalPath
                    + "（" + _frameIndex + " 帧）");
            }
            catch (Exception ex)
            {
                RenderStatus.Current = RenderStatus.State.Failed;
                RenderStatus.LastError = ex.Message;
                UnityEngine.Debug.LogError("[SpectreReplay] 离线收尾失败：" + ex);
            }
            finally
            {
                Dispose();
            }
        }

        internal static void Cancel()
        {
            if (_instance == null)
            {
                return;
            }
            _instance._cancelled = true;
        }

        private void OnDestroy() => Dispose();

        private void Dispose()
        {
            PinActive = false;
            try
            {
                _encoder?.Dispose();
                _encoder = null;
                _frames?.Dispose();
                _frames = null;
            }
            catch
            {
                // 析构路径不抛
            }
            // 删掉中间产物。原先这里什么都不做，于是**每一次离线渲染都在
            // _tmp 里留下一个原始视频**（现在还多一个 wav）——渲染几十次
            // 就是几十 GB，而且用户完全不知道那些是什么。成品已经拷出去了，
            // 中间文件没有留着的理由。
            foreach (string p in new[] { _rawVideoPath, _audioPath })
            {
                if (string.IsNullOrEmpty(p))
                {
                    continue;
                }
                try
                {
                    if (File.Exists(p))
                    {
                        File.Delete(p);
                    }
                }
                catch
                {
                    // 删不掉就算了，下一轮用的是新文件名
                }
            }
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }

    /// <summary>
    /// 钉时间轴的补丁。
    ///
    /// ── 为什么钉的是 songposition_minusi，而不是 dspTime ────────────
    /// 游戏的歌曲位置是 dspTime 的纯函数（scrConductor.cs:852）：
    ///     songposition = (dspTime - dspTimeSong - calibration_i) * pitch - addoffset
    /// 看上去改 dspTime 就够了，但**那是无效的**，实测：
    ///   · :852 那次赋值在 <c>Update</c> **内部**，我们挂在 Postfix 上，
    ///     改 dspTime 时 songposition_minusi 已经算完了；
    ///   · 下一帧 :779 <c>dspTime = AudioSettings.dspTime</c> 会把我们
    ///     写的值原样覆盖回去。
    /// 结果：改写 dspTime 一帧歌曲位置都没钉住，而且没有任何症状——
    /// 画面照出，只是"离线"这件事其实是按真实时间在跑。
    ///
    /// 正确的钉点就是歌曲位置本身：**属性访问器**。
    ///   Set（Prefix）把游戏写进去的值换成钉扎值，否则 :852 每次都覆盖；
    ///   Get（Postfix）把所有读点（行星角度、判定、节拍、回放调度）换掉。
    /// 两端都做是必须的：只做 Get 的话内部字段仍是真实时间，
    /// :858 的 <c>deltaSongPos = new - old</c> 会变成"钉扎 - 真实"的巨大跳变。
    ///
    /// 与 Spectre 的 <c>VideoRenderPatches.cs</c> 同解。
    ///
    /// ── 剩下的一处真实时间读点 ──────────────────────────────────
    /// <c>scrConductor.Update</c> 开头 :777-781 读 <c>AudioSettings.dspTime</c>
    /// 并回写 dspTime，这是引擎原生值，我们不碰；它只影响 dspTime 字段本身
    /// 与音效排程，而歌曲位置已经全部走钉扎值了。
    /// </summary>
    internal static class Patch_OfflineClock
    {
        [GamePatch(typeof(scrConductor), "songposition_minusi", PatchKind.Postfix, "render",
            Accessor = PropertyAccessor.Get)]
        private static void PinGetPostfix(ref double __result)
        {
            if (!OfflineSession.PinActive)
            {
                return;
            }
            try
            {
                if (scrConductor.instance != null && scrConductor.instance.song != null)
                {
                    __result = OfflineSession.PinnedSongPosition;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[SpectreReplay] 钉歌曲位置（读）失败：" + ex.Message);
            }
        }

        [GamePatch(typeof(scrConductor), "songposition_minusi", PatchKind.Prefix, "render",
            Accessor = PropertyAccessor.Set)]
        private static void PinSetPrefix(ref double value)
        {
            if (!OfflineSession.PinActive)
            {
                return;
            }
            try
            {
                if (scrConductor.instance != null && scrConductor.instance.song != null)
                {
                    // value 是游戏本帧算出的真实位置，先抄下来（结束判据要用），
                    // 再换成钉扎值。
                    OfflineSession.LastRealSongPosition = value;
                    value = OfflineSession.PinnedSongPosition;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[SpectreReplay] 钉歌曲位置（写）失败：" + ex.Message);
            }
        }
    }
}