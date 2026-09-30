using System;
using System.IO;
using UnityEngine;
using SpectreReplay.Patching;

namespace SpectreReplay.Features.VideoRender
{
    /// <summary>
    /// 离线渲染：重跑一遍回放，按目标帧率逐帧推进并逐帧捕获。
    ///
    /// ── 怎么钉住时间轴 ──────────────────────────────────────
    /// 游戏的歌曲位置是 dspTime 的纯函数（scrConductor.cs:852）：
    ///     songposition = (dspTime - dspTimeSong - calibration_i) * pitch - addoffset
    /// 所以要"把时间停在第 i/fps 秒"，唯一需要改的就是 dspTime：
    ///     dspTime = dspTimeSong + calibration_i + (i/fps + addoffset) / pitch
    /// 挂在 scrConductor.Update 的 Postfix 上——必须晚于游戏自己那行
    /// songposition 的赋值，早于本帧其余系统读取，否则钉不住或钉晚了。
    ///
    /// dspTime 同时被用来排程打击音效/hold 音（同样在 Update 里），
    /// 钉住它之后这些音效也会按目标时间排，判定与视听一致。
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
        private string _tempDir;
        private bool _cancelled;

        internal static bool IsRunning => _instance != null && _instance._capturing;

        /// <summary>钉住用的目标歌曲时间，由 PumpFrame 写入、补丁读取。</summary>
        internal static double PinnedSongPosition { get; set; }

        /// <summary>是否为钉住生效中。补丁据此决定是否接管 dspTime。</summary>
        internal static bool PinActive { get; private set; }

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
                RenderStatus.LastError = "请先进入关卡再开始离线渲染";
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

                string ext = Settings.Container == 1 ? "mkv"
                    : Settings.Container == 2 ? "webm" : "mp4";
                string stem = "offline_" + DateTime.Now.ToString("HHmmss");
                _rawVideoPath = Path.Combine(_tempDir, stem + "." + ext);
                _outputPath = Path.Combine(Settings.VideoDir, stem + "_final." + ext);

                _encoder = VideoEncoder.Start(_pool, w, h, Settings.Fps,
                    (VideoEncoder.EncoderMode)Settings.Encoder,
                    (VideoEncoder.QualityMode)Settings.Quality,
                    (VideoEncoder.ContainerMode)Settings.Container,
                    _rawVideoPath, Settings.FfmpegPath);
                _frames.PendingSink = OnFrameReady;

                _fps = Mathf.Max(1, Settings.Fps);
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
                PinActive = true;
                PinnedSongPosition = 0.0;
                RenderStatus.Current = RenderStatus.State.Offline;
                RenderStatus.FramesTotal = _totalFrames;
                RenderStatus.FramesDone = 0;
                RenderStatus.Progress = 0;
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
            // 歌曲已经放完/失败：剩余帧数按"已到结尾"处理，直接收尾
            double pos = 0;
            try
            {
                pos = scrConductor.instance.songposition_minusi;
            }
            catch
            {
                Stop(false);
                return;
            }
            if (pos < -0.5)
            {
                Stop(false);
                return;
            }
            PinnedSongPosition = _frameIndex / (double)_fps;
            _frameIndex++;
            if (_frames.Request())
            {
                // 已排队，等 GPU 回读
            }
            RenderStatus.FramesDone = _frameIndex;
            RenderStatus.Progress = (float)_frameIndex / _totalFrames;
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
                    RenderStatus.LastError = _encoder?.Error ?? "编码器未正常退出";
                    UnityEngine.Debug.LogError("[SpectreReplay] 离线编码失败："
                        + RenderStatus.LastError);
                    return;
                }
                string finalPath = _rawVideoPath;
                try
                {
                    File.Copy(_rawVideoPath, _outputPath, true);
                    finalPath = _outputPath;
                }
                catch
                {
                    finalPath = _rawVideoPath;
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
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }

    /// <summary>
    /// 钉时间轴的补丁。挂在 conductor.Update 之后：
    /// 那一行 songposition 的赋值已经跑完，本帧其余系统读到的就是钉好的值。
    /// </summary>
    internal static class Patch_OfflineClock
    {
        [GamePatch(typeof(scrConductor), "Update", PatchKind.Postfix, "render")]
        private static void PinPostfix()
        {
            if (!OfflineSession.PinActive)
            {
                return;
            }
            try
            {
                var c = scrConductor.instance;
                if (c == null || c.song == null)
                {
                    return;
                }
                double pitch = c.song.pitch <= 0.0001 ? 1.0 : c.song.pitch;
                // songposition = (dspTime - dspTimeSong - calibration_i) * pitch - addoffset
                // 反解 dspTime。calibration_i 是静态字段（全局校准值），不是实例字段。
                c.dspTime = c.dspTimeSong + scrConductor.calibration_i
                    + (OfflineSession.PinnedSongPosition + c.addoffset) / pitch;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[SpectreReplay] 钉时间轴失败：" + ex.Message);
            }
        }
    }
}