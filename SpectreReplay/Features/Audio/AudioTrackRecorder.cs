using System;
using System.IO;
using UnityEngine;

namespace SpectreReplay.Features.Audio
{
    /// <summary>
    /// 音频轨录制。
    ///
    /// 做法：读取歌曲 AudioClip 的原始采样，而不是拦截混音输出。
    /// 原因——这版 Unity 没有 <c>AudioListener.onAudioFilterRead</c>，
    /// 也没有可监听的 AudioMixerSnapshot；能拿到的通路只有 AudioClip.GetData。
    /// 而对"录一局游玩"这个用途来说，**只要歌曲音轨**就够：打击音效是
    /// 按谱面固定位置触发的，本来就能从谱面复现，混进去反而和画面不同步。
    ///
    /// 与视频栅格的对齐：每输出一帧，就补齐这一帧时长应有的采样数
    ///     need = round((frameIndex + 1) * rate / fps) - 已写入数
    /// 读不足就补零，读多了就截断。帧序号连续 → 采样数连续 → 音画同步，
    /// 这与视频侧的定速策略是同一个栅格。
    /// </summary>
    internal sealed class AudioTrackRecorder
    {
        private AudioClip _clip;
        private int _rate;
        private int _channels;
        private float[] _mix;
        private short[] _out;
        private long _written;
        private int _lastReadFrame;

        internal int SampleRate => _rate;
        internal int Channels => _channels;
        internal long FramesWritten => _written;
        internal bool IsValid => _clip != null && _rate > 0;

        /// <summary>绑定到当前正在播放的音源。返回是否可用。</summary>
        internal bool Bind(UnityEngine.AudioSource source)
        {
            _clip = null;
            if (source == null)
            {
                return false;
            }
            AudioClip clip;
            try
            {
                clip = source.clip;
            }
            catch
            {
                return false;
            }
            if (clip == null || clip.samples <= 0 || clip.frequency <= 0)
            {
                return false;
            }
            _clip = clip;
            _rate = clip.frequency;
            _channels = Mathf.Clamp(clip.channels, 1, 2);
            _mix = new float[Mathf.Max(1, _rate / 10)];   // 每次最多补 100ms
            _out = new short[_mix.Length * _channels];
            _written = 0;
            _lastReadFrame = 0;
            return true;
        }

        /// <summary>从 clip 的指定采样位置读一段。</summary>
        private int ReadFrom(int startSample, int wantSamples)
        {
            int total = _clip.samples;
            if (startSample >= total || wantSamples <= 0)
            {
                return 0;
            }
            int n = Math.Min(wantSamples, total - startSample);
            int frames = n / _channels;
            if (frames <= 0)
            {
                return 0;
            }
            if (_mix.Length < frames * _channels)
            {
                _mix = new float[frames * _channels];
                _out = new short[_mix.Length];
            }
            if (!_clip.GetData(_mix, startSample))
            {
                return 0;
            }
            int outLen = frames * _channels;
            for (int i = 0; i < outLen; i++)
            {
                float s = _mix[i];
                s = s < -1f ? -1f : (s > 1f ? 1f : s);
                _out[i] = (short)(s * 32767f);
            }
            return outLen;
        }

        /// <summary>
        /// 往轨道里补齐第 frameIndex 帧应有的采样。返回补了多少采样点。
        /// source 为 null（音源已销毁）时只补零，不抛。
        /// </summary>
        internal int AdvanceTo(UnityEngine.AudioSource source, int frameIndex, int fps,
            out int readPosOut)
        {
            readPosOut = _lastReadFrame;
            if (_rate <= 0 || fps <= 0)
            {
                return 0;
            }
            long want = (long)((frameIndex + 1) * (double)_rate / fps) * _channels;
            int need = (int)(want - _written);
            if (need <= 0)
            {
                return 0;
            }
            if (need > _out.Length)
            {
                // 一帧对应不到 100ms 以上（fps 异常高）时按批处理
                need = _out.Length;
            }

            int got = 0;
            if (source != null)
            {
                try
                {
                    int samplePos = source.timeSamples;
                    if (samplePos < 0)
                    {
                        samplePos = 0;
                    }
                    // 采样位置跳变（循环/切歌/进度条拖动）时从当前位置重接，
                    // 拿旧位置读会得到一段重复或空白。
                    if (samplePos < _lastReadFrame || samplePos > _lastReadFrame + _rate * 2)
                    {
                        _lastReadFrame = samplePos;
                    }
                    readPosOut = _lastReadFrame;
                    got = ReadFrom(_lastReadFrame, need);
                    _lastReadFrame += got;
                }
                catch
                {
                    got = 0;
                }
            }
            // 读到的不足就补零：宁可有一小段静音，也不能让音画错位
            for (int i = got; i < need; i++)
            {
                _out[i] = 0;
            }
            Append(_out, 0, need);
            _written += need;
            return need;
        }

        private MemoryStream _buf = new MemoryStream(1 << 18);

        private void Append(short[] src, int offset, int count)
        {
            // 手写小端字节序：BitConverter.GetBytes 在 Mono 上每次都会新分配
            // 一个 byte[]，这里复用一个暂存数组逐块写出。
            int need = count * 2;
            if (_scratch.Length < need)
            {
                _scratch = new byte[Mathf.NextPowerOfTwo(need)];
            }
            for (int i = 0; i < count; i++)
            {
                short v = src[offset + i];
                _scratch[i * 2] = (byte)(v & 0xFF);
                _scratch[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            _buf.Write(_scratch, 0, need);
        }

        private byte[] _scratch = new byte[1 << 16];

        /// <summary>落盘为 16-bit PCM WAV。</summary>
        internal bool Save(string path)
        {
            try
            {
                byte[] pcm = _buf.ToArray();
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs))
                {
                    int dataLen = pcm.Length;
                    int byteRate = _rate * _channels * 2;
                    w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                    w.Write(36 + dataLen);            // ChunkSize
                    w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
                    w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                    w.Write(16);                      // 子块大小
                    w.Write((short)1);                // PCM
                    w.Write((short)_channels);
                    w.Write(_rate);
                    w.Write(byteRate);
                    w.Write((short)(_channels * 2));  // BlockAlign
                    w.Write((short)16);               // BitsPerSample
                    w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                    w.Write(dataLen);
                    w.Write(pcm);
                }
                return true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[SpectreReplay] 音频轨保存失败：" + ex.Message);
                return false;
            }
        }
    }
}