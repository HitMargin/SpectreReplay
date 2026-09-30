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
        private string _tempDir;

        internal bool IsCapturing => _capturing;

        /// <summary>全局是否有会话在跑。手动触发按钮据此决定要不要新开一个。</summary>
        internal static bool IsRunning => _instance != null && _instance._capturing;

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
                if (!string.IsNullOrEmpty(_rawVideoPath)
                    && System.IO.File.Exists(_rawVideoPath))
                {
                    System.IO.File.Delete(_rawVideoPath);
                }
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
                    (VideoEncoder.EncoderMode)Settings.Encoder,
                    (VideoEncoder.QualityMode)Settings.Quality,
                    (VideoEncoder.ContainerMode)Settings.Container,
                    _rawVideoPath, Settings.FfmpegPath);

                _frames.PendingSink = OnFrameReady;

                _clock.Restart();
                _startWall = Now();
                _frameIndex = 0;
                _duplicated = 0;
                _skipped = 0;
                _capturing = true;
                cancelled = false;

                RenderStatus.Current = RenderStatus.State.Live;
                RenderStatus.LastError = "";
                RenderStatus.Progress = 0;
                RenderStatus.FramesDone = 0;
                Debug.Log("[SpectreReplay] 开始实时录制 " + w + "x" + h + "@"
                    + Settings.Fps + " → " + _outputPath);
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
                    RenderStatus.LastError = _encoder?.Error ?? "编码器未正常退出";
                    Debug.LogError("[SpectreReplay] 编码失败：" + RenderStatus.LastError);
                    return;
                }
                RenderStatus.Current = RenderStatus.State.Done;
                RenderStatus.LastOutput = _outputPath;
                RenderStatus.FramesDone = _frameIndex;
                Debug.Log("[SpectreReplay] 录制完成：" + _outputPath
                    + "（" + _frameIndex + " 帧，补 " + _duplicated + " 帧，跳 " + _skipped + " 帧，"
                    + (int)_clock.Elapsed.TotalSeconds + "s）");
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
                _encoder.Submit(copy);
                _duplicated++;
                _frameIndex++;
            }
            else
            {
                _skipped++;
                _frameIndex++;   // 帧序号照走，音画栅格才不会错位
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
            _encoder.Submit(pixels);
            _frameIndex++;
            UpdateStatus();
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