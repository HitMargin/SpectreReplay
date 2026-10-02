using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;      // 与 System.Diagnostics.Debug 撞名，显式绑定

namespace SpectreReplay.Features.VideoRender
{
    /// <summary>
    /// 实时录制会话：边玩边录，不需要重跑关卡。
    ///
    /// ── 为什么必须按墙钟而不是"每个渲染帧录一张" ──────────────
    /// 假设游戏掉到 50 FPS 而输出目标是 60 FPS。按渲染帧录会得到 50 张/秒，
    /// 而音频是按真实时间以 60 FPS 的栅格混音的——于是视频相对音频**越走越快**，
    /// 3 分钟下来结尾能差出好几秒。
    ///
    /// 所以改成按墙钟定点：第 i 帧的预定时刻是 t0 + i/fps。每一帧检查
    /// "现在到点了吗"，到点才录。输出帧数恒等于 真实时长 × fps，
    /// 音频栅格与视频栅格对齐，A/V 全程同步。
    ///
    /// ── 跟不上怎么办 ──────────────────────────────────────────
    /// 到点了但这一帧还没准备好（GPU 回读没回来 / 编码器队列满）：
    ///   DuplicateWhenLate = true  → 复制上一帧，流畅，但那一小段内容是静止的
    ///   DuplicateWhenLate = false → 跳过这一帧，忠实，但视频会有一次轻微卡顿
    /// 两者都不会破坏音画同步，因为帧序号仍然是连续的。
    ///
    /// ── 时钟为什么用 Stopwatch 而不用 Time.realtimeSinceStartup ───
    /// 实时录制必须在暂停/失去焦点/加载场景时继续走准。Unity 的 realtimeSinceStartup
    /// 跟随 Time.timeScale（暂停时停、慢动作时变慢），一首歌里出现一次慢动作
    /// 视频就会和音频错开。Stopwatch 是纯墙钟，不受游戏时间影响。
    /// </summary>
    internal sealed class LiveSession : MonoBehaviour
    {
        private FrameSource _frames;
        private VideoEncoder _encoder;
        private FrameBufferPool _pool;
        private readonly Stopwatch _clock = new Stopwatch();

        private double _startWall;          // 会话开始的墙钟秒
        private int _frameIndex;             // 已产出的帧数（= 输出文件的帧序号）
        private int _duplicated;            // 补了多少帧
        private int _skipped;
        private bool _capturing;
        private byte[] _lastFrame;          // 复制的来源
        private string _outputPath;
        private string _rawVideoPath;
        private string _audioPath;
        private string _tempDir;

        // 音频与视频走同一个帧栅格：每产出一帧就抓一次混音，
        // 抓到的量自适应，保证音频总长 = 帧数 / fps。
        //
        // 惰性创建：构造器要知道 fps（静默重启的节流按秒算），
        // 而 fps 要等 StartCapture 才从 Settings 读到。
        private Audio.AudioTrackRecorder _audio;

        internal bool IsCapturing => _capturing;

        /// <summary>全局是否有会话在跑。手动触发按钮据此决定要不要新开一个。</summary>
        internal static bool IsRunning => _instance != null && _instance._capturing;

        private bool _sawRewind;

        /// <summary>
        /// 问一句"这次 Start_Rewind 是不是本会话的第一次"，并把答案记掉。
        ///
        /// **为什么需要它：** scrController.Start_Rewind 不只表示"玩家按了重开"，
        /// 它在关卡刚加载完时也会被调一次（scrController.cs:1480，
        /// WaitForStartCo 协程里）。而渲染会话是在**进入关卡**时开启的，
        /// 那次加载完成的 rewind 紧跟其后——此时会话正在跑，
        /// 于是"看到会话在跑就收尾"的判据照样会把它结束掉。
        /// 现象是玩家刚开始玩就显示"渲染完成"（实测 27 帧 / 3 秒）。
        ///
        /// 返回 true 表示"跳过一次，别收尾"。之后玩家真按 R 重开时
        /// 返回 false，收尾照常进行。
        /// </summary>
        internal static bool ConsumeFirstRewind()
        {
            var s = _instance;
            if (s == null)
            {
                // 会话都不在（没开渲染，或已被别处收掉）：这次 rewind 与它无关，
                // 交给调用方按正常重开处理。
                return false;
            }
            if (s._sawRewind)
            {
                return false;
            }
            s._sawRewind = true;
            return true;
        }

        // ── 生命周期 ────────────────────────────────────────────
        internal static void Begin()
        {
            if (_instance != null)
            {
                return;
            }
            _instance = new GameObject("SpectreReplayLiveCapture").AddComponent<LiveSession>();
            UnityEngine.Object.DontDestroyOnLoad(_instance.gameObject);
            _instance.StartCapture();
        }

        private static LiveSession _instance;

        internal static void End()
        {
            if (_instance == null)
            {
                return;
            }
            _instance.StopCapture();
            var dead = _instance;
            _instance = null;
            UnityEngine.Object.Destroy(dead.gameObject);
        }

        internal static void Cancel()
        {
            if (_instance == null)
            {
                return;
            }
            var dead = _instance;
            _instance = null;
            dead.cancelled = true;
            dead.Discard();
            UnityEngine.Object.Destroy(dead.gameObject);
        }

        /// <summary>取消：停录、丢弃中间产物、删掉临时文件，不产出成品。</summary>
        private void Discard()
        {
            _capturing = false;
            try
            {
                _encoder?.Finish(3000);
                _frames?.Dispose();
                _frames = null;
                // 混音钩子必须卸：AudioRenderer 是引擎级的全局钩子，
                // 不 Stop 的话它会一直抓混音，直到下一次渲染重新 Start——
                // 期间玩家的正常游玩音频被反复抓取，白白吃 CPU。
                _audio?.Discard();
                _audio = null;
                CleanupTemp();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[SpectreReplay] 取消时清理出错：" + ex.Message);
            }
            finally
            {
                _encoder?.Dispose();
                _encoder = null;
            }
            RenderStatus.Current = RenderStatus.State.Cancelled;
        }

        private bool cancelled;

        /// <summary>
        /// 第二次 ffmpeg：把音频贴到已编码好的视频上。
        /// <c>-c:v copy</c> 是关键——不重编码画面，只是重新封装，
        /// 所以这一步是秒级而不是分钟级。
        /// </summary>
        private static string MuxAudio(string video, string audio, string outPath)
            => AudioMux.Mux(Settings.FfmpegPath, video, audio, outPath,
                (ContainerMode)Settings.Container);

        private void CleanupTemp()
        {
            foreach (string p in new[] { _rawVideoPath, _audioPath })
            {
                try
                {
                    if (!string.IsNullOrEmpty(p) && System.IO.File.Exists(p))
                    {
                        System.IO.File.Delete(p);
                    }
                }
                catch
                {
                    // 临时文件删不掉不影响成品
                }
            }
        }

        // ── 录制 ────────────────────────────────────────────────
        private void StartCapture()
        {
            try
            {
                _tempDir = System.IO.Path.Combine(Settings.VideoDir, "_tmp");
                System.IO.Directory.CreateDirectory(_tempDir);
                System.IO.Directory.CreateDirectory(Settings.VideoDir);

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

                var ext = Settings.Container == 1 ? "mkv"
                    : Settings.Container == 2 ? "webm" : "mp4";
                _rawVideoPath = System.IO.Path.Combine(_tempDir,
                    "live_" + System.DateTime.Now.ToString("HHmmss") + "." + ext);
                _outputPath = System.IO.Path.Combine(Settings.VideoDir,
                    System.IO.Path.GetFileNameWithoutExtension(_rawVideoPath) + "_final." + ext);

                // 编码器约定的帧尺寸。任何尺寸不一致的帧都必须丢弃——把半张或错尺寸的帧
                // 喂进 ffmpeg 会让整条流错位，产出的文件从那一帧起全是花的。
                _poolFrameSize = _frames.FrameSize;
                _pool = new FrameBufferPool(_poolFrameSize, null);
                _encoder = VideoEncoder.Start(_pool, w, h, Settings.Fps,
                    (EncoderMode)Settings.Encoder,
                    (QualityMode)Settings.Quality,
                    (ContainerMode)Settings.Container,
                    _frames.NeedsRowFlip,
                    _rawVideoPath, Settings.FfmpegPath);

                _frames.PendingSink = OnFrameReady;

                // 音频：抓游戏最终混音总线。绑不上就只出无声视频，
                // 而不是让整场录制失败——画面本身是有价值的。
                _audioPath = System.IO.Path.Combine(_tempDir,
                    System.IO.Path.GetFileNameWithoutExtension(_rawVideoPath) + ".wav");
                _audio = new Audio.AudioTrackRecorder(Settings.Fps);
                if (!_audio.Start(_audioPath))
                {
                    UnityEngine.Debug.LogWarning(
                        "[SpectreReplay] 抓不到混音，本次将产出无声视频："
                        + _audio.DeathReason);
                }

                _clock.Restart();
                _startWall = Now();
                _frameIndex = 0;
                _duplicated = 0;
                _skipped = 0;
                _capturing = true;
                cancelled = false;
                _sawRewind = false;   // 新会话：重新开始数"第一次 rewind"

                RenderStatus.Current = RenderStatus.State.Live;
                RenderStatus.LastError = "";
                RenderStatus.Progress = 0;
                RenderStatus.FramesDone = 0;
                Debug.Log("[SpectreReplay] 开始实时录制 " + w + "x" + h + "@"
                    + Settings.Fps + " → " + _outputPath);
                Ui.Status.Say("status.render_start");
            }
            catch (Exception ex)
            {
                Debug.LogError("[SpectreReplay] 实时录制启动失败：" + ex);
                RenderStatus.Current = RenderStatus.State.Failed;
                RenderStatus.LastError = ex.Message;
                _capturing = false;
            }
        }

        private void StopCapture()
        {
            if (!_capturing)
            {
                return;
            }
            _capturing = false;
            if (cancelled)
            {
                // 取消路径走 Discard()，这里再被调一次是兜底：只清不报完成
                Discard();
                return;
            }
            RenderStatus.Current = RenderStatus.State.Finalizing;
            try
            {
                bool ok = _encoder != null && _encoder.Finish();
                _frames?.Dispose();
                _frames = null;
                if (!ok)
                {
                    RenderStatus.Current = RenderStatus.State.Failed;
                    RenderStatus.LastError = _encoder?.Error ?? Ui.Loc.T("render.err.encoder_exit");
                    Debug.LogError("[SpectreReplay] 编码失败：" + RenderStatus.LastError);
                    return;
                }

                // 音频封口 + 合流。Seal() 回写真实 wav 长度（不封口的话头里的长度是
                // 占位的 0，ffmpeg 按头读会认为音频是空的）。合流走第二次
                // ffmpeg，-c:v copy 不重编码，只是把音轨贴到已有视频流上。
                string finalPath = _rawVideoPath;
                bool audioOk = _audio != null
                    && !_audio.Dead
                    && _audio.Seal()
                    && _audio.Seconds > 0.05;
                if (audioOk)
                {
                    finalPath = MuxAudio(_rawVideoPath, _audioPath, _outputPath);
                    Debug.Log("[SpectreReplay] 音频已合入，时长 "
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
                    // 没音频时直接用裸视频改名，不留 _final 这种中间名
                    try
                    {
                        System.IO.File.Copy(_rawVideoPath, _outputPath, true);
                    }
                    catch
                    {
                        finalPath = _rawVideoPath;
                    }
                }
                CleanupTemp();

                RenderStatus.Current = RenderStatus.State.Done;
                RenderStatus.LastOutput = finalPath;
                RenderStatus.FramesDone = _frameIndex;
                Debug.Log("[SpectreReplay] 录制完成：" + finalPath
                    + "（" + _frameIndex + " 帧，补 " + _duplicated + " 帧，跳 " + _skipped + " 帧，"
                    + (int)_clock.Elapsed.TotalSeconds + "s）");
                Ui.Status.Say("status.render_done",
                    System.IO.Path.GetFileName(finalPath),
                    Ui.Status.Level.Info, 8f);
            }
            catch (Exception ex)
            {
                RenderStatus.Current = RenderStatus.State.Failed;
                RenderStatus.LastError = ex.Message;
                Debug.LogError("[SpectreReplay] 收尾失败：" + ex);
            }
            finally
            {
                _encoder?.Dispose();
                _encoder = null;
            }
        }

        // ── 每帧推进 ────────────────────────────────────────────
        private void LateUpdate()
        {
            if (!_capturing)
            {
                return;
            }
            double target = _startWall + _frameIndex / (double)Settings.Fps;
            double now = Now();

            // 落后太多（切换场景、大掉帧）时重新对时，避免一次性补几百帧。
            // 允许的最大落后是 0.5 秒——再多就不是"卡了一下"而是"出了别的事"。
            if (now - target > 0.5)
            {
                _startWall = now - (_frameIndex / (double)Settings.Fps);
                target = now;
            }

            if (now < target)
            {
                return;
            }
            if (_frames.Request())
            {
                // 已排队，等 OnFrameReady 回调
                return;
            }
            // 到点了但这一帧拿不到 → 补帧策略
            if (Settings.DuplicateWhenLate && _lastFrame != null)
            {
                var copy = _pool.Rent();
                System.Buffer.BlockCopy(_lastFrame, 0, copy, 0, _lastFrame.Length);
                if (_encoder.Submit(copy))
                {
                    _duplicated++;
                    _frameIndex++;
                    PushAudioForFrame();
                }
                else
                {
                    // 入队失败（ffmpeg 退出或等待超时）：这一帧没有进编码器，
                    // 帧序号与音频栅格都不能动——否则音画从此错位。
                    // 下一帧会重试，真正放弃交给 Finish 的看门狗与退出码判定。
                    _pool.Return(copy);
                    Debug.LogError("[SpectreReplay] 编码器已停止接收帧，本次录制中止："
                        + _encoder.Error);
                    StopCapture();
                    return;
                }
            }
            else
            {
                _skipped++;
                _frameIndex++;   // 帧序号照走，音画栅格才不会错位
                PushAudioForFrame();
            }
            UpdateStatus();
        }

        private void OnFrameReady(byte[] pixels, int w, int h)
        {
            if (!_capturing)
            {
                return;
            }
            if (pixels.Length != _poolFrameSize)
            {
                // 整屏模式下窗口尺寸可能变了；这种帧直接丢，别让尺寸不一致的
                // 帧进编码器把整条流搅坏。
                return;
            }
            _lastFrame = pixels;
            // Submit 失败 = 这一帧没能进编码器。帧序号与音频栅格都不能推进，
            // 否则视频会缺一张对应的图、音频却照常前进 → 永久错位。
            if (!_encoder.Submit(pixels))
            {
                Debug.LogError("[SpectreReplay] 编码器已停止接收帧，本次录制中止："
                    + _encoder.Error);
                StopCapture();
                return;
            }
            _frameIndex++;
            PushAudioForFrame();
            UpdateStatus();
        }

        /// <summary>
        /// 抓这一帧的混音。
        ///
        /// **不再有"按需补齐采样"这回事。** AudioRenderer 抓的是混音总线，
        /// 它按真实时间累积样本；每个渲染帧消费一次"自上次调用以来的全部累积量"，
        /// 累积量随帧自动伸缩。于是音频总量 = Σ每帧消费量 = 帧数 / fps，
        /// 与视频栅格天然同构，不需要任何对齐计算。
        ///
        /// （旧实现要按 frameIndex 反推该补多少采样，那是 AudioSource.clip
        /// 路径的产物——而 clip 在流式播放下恒为 null，那条路本来就走不通。）
        ///
        /// 调用的时机很重要：必须与 frames.Grab(i) 在**同一协程迭代内**，
        /// 保证"第 N 次抓到的音频恰好对应第 N 帧图像"。
        /// </summary>
        private void PushAudioForFrame()
        {
            _audio.CaptureFrame();
        }

        // 当前歌曲的 AudioSource。由 LiveSession 持有但对 OfflineSession 也开放
        // ——曾经只有 LiveSession 有这个方法，OfflineSession 想录音频时就得复制
        // 一份，两份迟早会走偏。
        internal static UnityEngine.AudioSource CurrentSongSource()
        {
            try
            {
                return scrConductor.instance?.song;
            }
            catch
            {
                return null;
            }
        }

        private int _poolFrameSize;

        private void UpdateStatus()
        {
            RenderStatus.FramesDone = _frameIndex;
            double elapsed = Now() - _startWall;
            RenderStatus.Progress = elapsed <= 0 ? 0
                : (float)Math.Min(1.0, _frameIndex / (elapsed * Settings.Fps));
        }

        private void OnDestroy()
        {
            _encoder?.Dispose();
            _frames?.Dispose();
        }

        /// <summary>纯墙钟秒。不受 Time.timeScale / 暂停 / 慢动作影响。</summary>
        private static double Now()
            => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }
}