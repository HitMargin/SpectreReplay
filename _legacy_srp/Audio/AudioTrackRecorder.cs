using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Collections;

namespace SpectreReplay.Features.Audio
{
    /// <summary>
    /// 音频轨录制：**用 Unity 的 AudioRenderer 抓游戏最终混音总线**。
    ///
    /// ── 为什么不用 AudioSource.clip.GetData（原实现）──────────────
    /// 那个做法**原理上就不成立**。ADOFAI 的歌曲是流式播放的，
    /// AudioSource 上 `clip` 恒为 null（实测日志：
    /// `音频：clip 不可用（clip=null samples=0 frequency=0）`）。
    /// 于是原实现每局都拿不到采样，只能产出无声视频。
    ///
    /// Spectre 用的是 `VideoAudioTrack`（Features/VideoRender/），走
    /// `AudioRenderer.Start/Stop/GetSampleCountForCaptureFrame/Render`
    /// ——这是引擎内建的混音抓取（类型在 UnityEngine.AudioModule.dll 中，
    /// 反编译的 Assembly-CSharp 里没有，grep 不到属正常）。
    /// 它抓的是**最终混音总线**：歌曲、打击音、hold 音全在内，不需要自行混音。
    ///
    /// ── 帧栅格对齐 ────────────────────────────────────────────
    /// 每个渲染帧调一次 <see cref="CaptureFrame"/>，每次消费「自上次调用以来
    /// 累积的样本」。累积量随帧自动伸缩，所以第 N 次 CaptureFrame 抓到的音频
    /// 恰好对应第 N 帧图像，**无需重采样**。
    /// 音频总量 = Σ每帧消费量 = 帧数 / fps，与视频栅格天然同构。
    ///
    /// ── 静默自愈 ──────────────────────────────────────────────
    /// 抓不到样本时先重启 AudioRenderer（预算 3 次）而不是立刻判死：
    /// 一次静默可能是启动时序问题，重启就好。全程静默才判死——
    /// 直接判死会让「偶发的一帧空」毁掉整个成片。
    /// </summary>
    internal sealed class AudioTrackRecorder
    {
        private const int RestartBudget = 3;
        private const int GiveUpAfterSeconds = 5;
        private const long MaxWavBytes = 4294967296L - 36;   // uint.MaxValue - 36
        private bool _sizeWarned;

        private FileStream _file;
        private NativeArray<float> _buffer;
        private NativeArray<float> _exact;
        private float[] _managed;
        private byte[] _bytes;
        private bool _bucketedOk = true;

        private readonly int _fps;
        private readonly int _giveUpFrames;

        private long _written;
        private int _silentFrames;
        private int _silentSinceRestart;
        private int _restarts;
        private bool _capturing;
        private int _frameNo;

        internal int SampleRate { get; private set; }
        internal int Channels { get; private set; }

        /// <summary>已写入的音频秒数。渲染循环用它对账音画进度。</summary>
        internal double Seconds
            => _written / 4.0 / Math.Max(1, Channels) / Math.Max(1, SampleRate);

        internal bool Dead { get; private set; }
        internal string DeathReason { get; private set; } = "";

        internal AudioTrackRecorder(int fps)
        {
            _fps = Math.Max(1, fps);
            _giveUpFrames = Math.Max(1, _fps * GiveUpAfterSeconds);
        }

        /// <summary>
        /// 开始抓混音并打开 wav 文件。返回是否成功启动。
        ///
        /// 采样率与声道取自 <c>AudioSettings</c>（输出设备）而不是 clip——
        /// 抓的是混音总线，格式由输出端决定。
        /// </summary>
        internal bool Start(string wavPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(wavPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                SampleRate = AudioSettings.outputSampleRate;
                Channels = ChannelsOf(AudioSettings.speakerMode);
                _file = new FileStream(wavPath, FileMode.Create);
                WriteHeader(0);       // 占位，Seal() 时回写真实长度

                if (!AudioRenderer.Start())
                {
                    // 首次失败先 Stop 再重试一次：Start 与 Stop 不是严格互斥的，
                    // 上一次残留的钩子会让 Start 直接返回 false。
                    try { AudioRenderer.Stop(); } catch { }
                    if (!AudioRenderer.Start())
                    {
                        Fail("render.err.audio_start", "AudioRenderer.Start");
                        return false;
                    }
                    Debug.Log("[SpectreReplay] 混音：首次启动失败，重试成功");
                }
                _capturing = true;
                Debug.Log("[SpectreReplay] 混音已启动 采样率=" + SampleRate
                    + " 声道=" + Channels);
                return true;
            }
            catch (Exception ex)
            {
                Fail("render.err.audio_start", ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 判死。reason 是技术细节，会出现在玩家可见的失败提示里，
        /// 因此必须是文案键而不是字面量（英文语言的用户不该看到中文）。
        /// 括号里拼技术细节的做法照 Spectre 的 VideoRenderJob
        /// （"文案 + (DeathReason)"），它同样是本地化标签 + 括号细节。
        /// </summary>
        private void Fail(string reasonKey, string detail)
        {
            Dead = true;
            DeathReason = Ui.Loc.F(reasonKey, detail);
            Debug.LogError("[SpectreReplay] 混音失败：" + DeathReason);
        }

        /// <summary>
        /// 抓一帧的混音。必须在渲染循环里与 <c>frames.Grab(i)</c> 同一迭代内调用。
        /// </summary>
        internal void CaptureFrame()
        {
            if (Dead || !_capturing || _file == null)
            {
                return;
            }
            _frameNo++;
            int perChannel;
            try
            {
                perChannel = AudioRenderer.GetSampleCountForCaptureFrame();
            }
            catch (Exception ex)
            {
                Fail("render.err.audio_count", ex.Message);
                return;
            }
            if (perChannel <= 0)
            {
                OnSilence();
                return;
            }
            int floats = perChannel * Channels;
            SizeBuffers(floats);

            // 分桶缓冲只保证"容量足够"；万一某个 Unity 版本要求长度严格相等，
            // Render 会返回 false——此时永久退回精确尺寸，宁可每帧重分配
            // 也不能为性能优化搞崩整条渲染。
            NativeArray<float> target = _bucketedOk ? _buffer : ExactBuffer(floats);
            bool rendered;
            try
            {
                rendered = AudioRenderer.Render(target);
            }
            catch (Exception ex)
            {
                Fail("render.err.audio_render", ex.Message);
                return;
            }
            if (!rendered && _bucketedOk)
            {
                _bucketedOk = false;
                Debug.LogWarning("[SpectreReplay] 混音：分桶缓冲被拒，"
                    + "退回每帧精确尺寸");
                target = ExactBuffer(floats);
                try
                {
                    rendered = AudioRenderer.Render(target);
                }
                catch (Exception ex)
                {
                    Fail("render.err.audio_render", ex.Message);
                    return;
                }
            }
            if (!rendered)
            {
                Fail("render.err.audio_render", "frame " + _frameNo);
                return;
            }

            _silentFrames = 0;
            // 注意拷贝必须跟着**实际渲染目标**走：降级路径的数据在 _exact 里。
            target.CopyTo(_managed);
            Buffer.BlockCopy(_managed, 0, _bytes, 0, floats * 4);
            _file.Write(_bytes, 0, floats * 4);
            _written += floats * 4;
        }

        /// <summary>
        /// 分桶：向上取整到 4096 的整数倍复用，避免每帧重分配几百 KB。
        /// 原实现「尺寸不同就重分配」在 60fps 下会每帧进 LOH。
        /// </summary>
        private void SizeBuffers(int floats)
        {
            const int bucket = 4096;
            int want = (floats + bucket - 1) / bucket * bucket;
            if (_buffer.Length != want)
            {
                if (_buffer.IsCreated) _buffer.Dispose();
                _buffer = new NativeArray<float>(want, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                _managed = new float[want];
                _bytes = new byte[want * 4];
            }
        }

        private NativeArray<float> ExactBuffer(int floats)
        {
            if (_exact.IsCreated && _exact.Length == floats)
            {
                return _exact;
            }
            if (_exact.IsCreated) _exact.Dispose();
            _exact = new NativeArray<float>(floats, Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            if (_managed == null || _managed.Length < floats) _managed = new float[floats];
            if (_bytes == null || _bytes.Length < floats * 4) _bytes = new byte[floats * 4];
            return _exact;
        }

        /// <summary>
        /// 静默自愈：每满 1 秒重启一次 AudioRenderer，预算 3 次；
        /// 预算耗尽且再静默 5 秒才判死。
        /// </summary>
        private void OnSilence()
        {
            _silentFrames++;
            _silentSinceRestart++;
            if (_silentFrames == 1 || _silentFrames % _fps == 0)
            {
                Debug.Log("[SpectreReplay] 混音静默 " + _silentFrames + " 帧"
                    + "（已重启 " + _restarts + " 次）");
            }
            // 每满 1 秒重启一次（预算 3 次）。
            // 用独立的 _silentSinceRestart 而不是 _silentFrames：后者此刻
            // 刚被清零，拿它判死会在"刚重启完的同一帧"立刻触发，
            // 等于重启一次就宣判。
            if (_silentSinceRestart >= _fps)
            {
                _silentSinceRestart = 0;
                if (_restarts < RestartBudget)
                {
                    _restarts++;
                    try
                    {
                        AudioRenderer.Stop();
                        AudioRenderer.Start();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[SpectreReplay] 重启混音失败：" + ex.Message);
                    }
                }
            }
            // 预算耗尽后再连续静默 5 秒才判死
            if (_restarts >= RestartBudget && _silentSinceRestart >= _giveUpFrames)
            {
                Fail("render.err.audio_silent", _restarts.ToString());
            }
        }

        /// <summary>回写真实长度并关闭。回写后文件才是可读的 wav。</summary>
        internal bool Seal()
        {
            if (_file == null)
            {
                return !Dead;
            }
            try
            {
                _file.Seek(0, SeekOrigin.Begin);
                WriteHeader(_written);
                _file.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Fail("render.err.audio_seal", ex.Message);
                return false;
            }
            finally
            {
                try { _file.Dispose(); } catch { }
                _file = null;
                try { AudioRenderer.Stop(); } catch { }
                _capturing = false;
            }
        }

        /// <summary>
        /// 44 字节 float32 WAV 头（format tag = 3, IEEE float）。
        ///
        /// 长度字段是 32 位：48kHz 立体声 float32 ≈ 384KB/s，约 3 小时超 4GB。
        /// 直接写会**静默截断成低 32 位**，头里的长度与实际数据量不符，
        /// ffmpeg 按头读取会提前截断音频。所以显式钳到 4GB 上限。
        /// </summary>
        private void WriteHeader(long dataLen)
        {
            long riff = dataLen + 36;
            long declaredRiff = riff;
            long declaredData = dataLen;
            if (dataLen >= MaxWavBytes)
            {
                declaredRiff = 0xFFFFFFFFL;
                declaredData = 0xFFFFFFFFL;
                if (!_sizeWarned)
                {
                    _sizeWarned = true;
                    Debug.LogWarning("[SpectreReplay] 音频超过 4GB，"
                        + "WAV 头长度字段已钳到上限（ffmpeg 会按实际数据读取）");
                }
            }
            var b = new BinaryWriter(_file);
            b.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            b.Write((int)declaredRiff);
            b.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
            b.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            b.Write(16);
            b.Write((short)3);                    // IEEE float
            b.Write((short)Channels);
            b.Write(SampleRate);
            b.Write(SampleRate * Channels * 4);
            b.Write((short)(Channels * 4));       // BlockAlign
            b.Write((short)32);                   // BitsPerSample
            b.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            b.Write((int)declaredData);
            b.Flush();
        }

        /// <summary>
        /// 声道数映射。取自 AudioSettings.speakerMode，
        /// 与 Spectre 的 VideoAudioTrack.ChannelsOf 同一套语义。
        /// </summary>
        private static int ChannelsOf(AudioSpeakerMode mode)
        {
            switch (mode)
            {
                case AudioSpeakerMode.Mono: return 1;
                case AudioSpeakerMode.Quad: return 4;
                case AudioSpeakerMode.Surround: return 5;
                case AudioSpeakerMode.Mode5point1: return 6;
                case AudioSpeakerMode.Mode7point1: return 8;
                default: return 2;
            }
        }

        /// <summary>放弃并清理（取消渲染时）。</summary>
        internal void Discard()
        {
            _capturing = false;
            try { AudioRenderer.Stop(); } catch { }
            try { if (_file != null) { _file.Dispose(); _file = null; } }
            catch { }
            if (_buffer.IsCreated) _buffer.Dispose();
            if (_exact.IsCreated) _exact.Dispose();
        }
    }
}