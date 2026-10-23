using System;
using System.IO;
using Unity.Collections;
using UnityEngine;

namespace SpectreReplay.Features.VideoRender;

// 混音轨：AudioRenderer 按帧抓取游戏最终混音（歌曲、打击音、hold 音、键盘录音），
// 直接写 float32 WAV。渲染时钟优先跟随这里的累计时长，音画对齐由构造保证。
// 注意：GetSampleCountForCaptureFrame 返回的是每通道样本数；Start/Render 都可能失败，
// 静默时先重启再判死，避免生成全程无声的视频。
internal sealed class VideoAudioTrack : IDisposable
{
    private const int RestartBudget = 3;
    private const int GiveUpAfterSeconds = 5;

    private readonly int fps;
    private readonly int giveUpFrames;

    private FileStream file;
    // 渲染目标。长度必须**恰为**本帧每通道样本数 × 声道数：Unity 按传入数组的
    // 长度从采集环形缓冲里消费数据，长度大于可用样本会把读指针推过写指针。
    private NativeArray<float> _render;
    // 托管侧暂存：分桶复用（避免每帧 new float[]/byte[] 进 LOH）。这两个数组只承接
    // 拷贝与落盘，**与 Render 的长度语义无关**，所以可以安全分桶。
    private float[] managed;
    private byte[] bytes;
    private long written;
    private int silentFrames;
    private int restarts;
    private bool capturing;
    private int frameNo;

    internal int SampleRate { get; }
    internal int Channels { get; }

    internal double Seconds
        => written / 4.0 / Channels / SampleRate;

    internal bool Dead { get; private set; }

    internal string DeathReason { get; private set; } = "";

    internal VideoAudioTrack(string wavPath, int framesPerSecond)
    {
        fps = Math.Max(1, framesPerSecond);
        giveUpFrames = Math.Max(1, fps * GiveUpAfterSeconds);
        SampleRate = AudioSettings.outputSampleRate;
        Channels = ChannelsOf(AudioSettings.speakerMode);
        file = new FileStream(wavPath, FileMode.Create);
        WriteHeader(0); // 占位，结束时回写真实长度
        if (!AudioRenderer.Start())
        {
            try { AudioRenderer.Stop(); } catch { }
            if (!AudioRenderer.Start())
            {
                Dead = true;
                DeathReason = "AudioRenderer.Start returned false";
                return;
            }
            Debug.Log("[SpectreReplay] mixdown: first start failed once, retry succeeded");
        }
        capturing = true;
        Debug.Log("[SpectreReplay] mixdown started. sampleRate=" + SampleRate
            + " channels=" + Channels
            + " dspBufferSize=" + AudioSettings.GetConfiguration().dspBufferSize);
    }

    internal void CaptureFrame()
    {
        if (Dead || !capturing) return;
        frameNo++;
        int perChannel = AudioRenderer.GetSampleCountForCaptureFrame();
        int floats = Math.Max(0, perChannel) * Channels;
        if (floats == 0)
        {
            OnSilence();
            return;
        }
        SizeBuffers(floats);
        NativeArray<float> target = RenderBuffer(floats);
        if (!AudioRenderer.Render(target))
        {
            Dead = true;
            DeathReason = "AudioRenderer.Render returned false at frame " + frameNo;
            return;
        }
        silentFrames = 0;
        target.CopyTo(managed);
        Buffer.BlockCopy(managed, 0, bytes, 0, floats * 4);
        file.Write(bytes, 0, floats * 4);
        written += floats * 4;
    }

    // 渲染缓冲：长度严格等于本帧样本数。尺寸随 dsp 缓冲的量化抖动在 128/256/512 间
    // 波动，变化时重分配（NativeArray 走非托管内存、不进 GC 堆，代价远小于它换来的
    // 正确性）。绝不为了"复用"而放大长度——见 CaptureFrame 的根因说明。
    private NativeArray<float> RenderBuffer(int floats)
    {
        if (_render.IsCreated && _render.Length == floats)
            return _render;
        if (_render.IsCreated) _render.Dispose();
        _render = new NativeArray<float>(floats, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        return _render;
    }

    // 回写文件头，WAV 才能被读取
    internal void Seal()
    {
        if (file == null) return;
        file.Seek(0, SeekOrigin.Begin);
        WriteHeader(written);
        file.Flush();
    }

    public void Dispose()
    {
        if (capturing)
        {
            try { AudioRenderer.Stop(); } catch { }
            capturing = false;
        }
        if (_render.IsCreated) _render.Dispose();
        _render = default;
        file?.Dispose();
        file = null;
    }

    private void OnSilence()
    {
        silentFrames++;
        if (silentFrames == 1 || silentFrames % fps == 0)
            Debug.Log("[SpectreReplay] mixdown silent. frame=" + frameNo + " consecutive=" + silentFrames);
        // 每静默满 1 秒重启一次，预算内不判死
        if (silentFrames >= fps && restarts < RestartBudget)
        {
            restarts++;
            silentFrames = 0;
            try { AudioRenderer.Stop(); } catch { }
            if (!AudioRenderer.Start())
            {
                if (restarts >= RestartBudget)
                {
                    Dead = true;
                    DeathReason = "AudioRenderer restart failed";
                }
                return;
            }
            Debug.Log("[SpectreReplay] mixdown restarted, attempt " + restarts);
            return;
        }
        if (restarts >= RestartBudget && silentFrames >= giveUpFrames)
        {
            Dead = true;
            DeathReason = "mixdown silent after " + restarts + " restarts";
        }
    }

    // 托管侧暂存的分桶增长：只增不减，避免每帧 new float[]/byte[] 进 LOH。
    // 这里的分桶是安全的——managed/bytes 只承接拷贝与落盘，长度不参与 Render 语义。
    private void SizeBuffers(int floats)
    {
        if (managed != null && managed.Length >= floats && bytes != null && bytes.Length >= floats * 4)
            return;
        const int bucketSamples = 4096;             // 4096 float = 16KB/声道
        int need = Math.Max(floats, ((floats + bucketSamples - 1) / bucketSamples) * bucketSamples);
        if (managed == null || managed.Length < need) managed = new float[need];
        if (bytes == null || bytes.Length < need * 4) bytes = new byte[need * 4];
    }

    // 44 字节 float32 WAV 头（format tag 3, IEEE float）
    // 长度字段是 32 位：超过 4GB（48kHz 立体声 float32 ≈ 384KB/s，约 3 小时）后
    // 必须截断。之前直接 GetBytes((long)) 会被静默截成低 32 位，头里的长度与实际
    // 数据量不符 → ffmpeg 按头读取会提前截断音频。这里显式钳到 uint.MaxValue
    // （WAV 惯例的"超出 4GB"标记）并告警一次。
    private void WriteHeader(long dataSize)
    {
        int channels = Channels;
        int sampleRate = SampleRate;
        int blockAlign = channels * 4;
        const long riffMax = uint.MaxValue - 36L;
        uint riffSize = (uint)Math.Min(36L + dataSize, riffMax);
        uint dataField = (uint)Math.Min(dataSize, uint.MaxValue);
        if (dataSize + 36 > riffMax && !_sizeWarned)
        {
            _sizeWarned = true;
            Debug.LogWarning("[SpectreReplay] mixdown: WAV 超过 4GB 上限（" + (dataSize / 1024 / 1024) + "MB），"
                + "头部长度字段已钳到上限，音频尾部可能被截断");
        }
        file.Write(BitConverter.GetBytes(0x46464952), 0, 4);             // "RIFF"
        file.Write(BitConverter.GetBytes(riffSize), 0, 4);
        file.Write(BitConverter.GetBytes(0x45564157), 0, 4);             // "WAVE"
        file.Write(BitConverter.GetBytes(0x20746d66), 0, 4);             // "fmt "
        file.Write(BitConverter.GetBytes(16), 0, 4);
        file.Write(BitConverter.GetBytes((short)3), 0, 2);               // IEEE float
        file.Write(BitConverter.GetBytes((short)channels), 0, 2);
        file.Write(BitConverter.GetBytes(sampleRate), 0, 4);
        file.Write(BitConverter.GetBytes(sampleRate * blockAlign), 0, 4);
        file.Write(BitConverter.GetBytes((short)blockAlign), 0, 2);
        file.Write(BitConverter.GetBytes((short)32), 0, 2);              // bits per sample
        file.Write(BitConverter.GetBytes(0x61746164), 0, 4);             // "data"
        file.Write(BitConverter.GetBytes(dataField), 0, 4);
    }

    private bool _sizeWarned;

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
}
