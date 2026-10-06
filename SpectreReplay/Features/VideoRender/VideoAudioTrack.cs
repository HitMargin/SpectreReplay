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
    private NativeArray<float> native;
    private NativeArray<float> _exact;
    private bool _bucketedOk = true;
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
        // 分桶缓冲的长度 ≥ 本帧样本数。Unity 侧按"容量足够"判定，正常直接可用；
        // 万一某个 Unity 版本要求长度严格相等，Render 会返回 false——此时永久
        // 退回精确尺寸（宁可回到旧的每帧重分配，也不能为性能优化搞崩整条渲染）。
        // 注意：降级路径的数据落在精确缓冲里，拷贝必须跟着"实际渲染目标"走。
        NativeArray<float> target = _bucketedOk ? native : ExactBuffer(floats);
        bool rendered = AudioRenderer.Render(target);
        if (!rendered && _bucketedOk)
        {
            _bucketedOk = false;
            Debug.LogWarning("[SpectreReplay] mixdown: bucketed render buffer rejected, "
                + "falling back to exact-size buffer per frame");
            target = ExactBuffer(floats);
            rendered = AudioRenderer.Render(target);
        }
        if (!rendered)
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

    // 精确尺寸容器（仅在分桶方案被拒后使用）
    private NativeArray<float> ExactBuffer(int floats)
    {
        if (_exact.IsCreated && _exact.Length == floats)
            return _exact;
        if (_exact.IsCreated) _exact.Dispose();
        _exact = new NativeArray<float>(floats, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        return _exact;
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
        if (native.IsCreated) native.Dispose();
        native = default;
        if (_exact.IsCreated) _exact.Dispose();
        _exact = default;
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

    private void SizeBuffers(int floats)
    {
        // GetSampleCountForCaptureFrame() 返回"自上次调用以来可用的每通道样本数"，
        // 会随 dsp 缓冲的整/半帧波动。原来的"尺寸不同就 dispose+重分配"会因此
        // 每帧重新分配几百 KB（还进 LOH），造成明显 GC 压力。改为向上取整到
        // 桶大小复用：只有需求超过当前容量才增长，缩小只改用前缀。
        const int bucketSamples = 4096;             // 4096 float = 16KB/声道
        int need = ((floats + bucketSamples - 1) / bucketSamples) * bucketSamples;
        if (managed != null && managed.Length >= floats && bytes != null
            && bytes.Length >= floats * 4 && native.IsCreated && native.Length >= floats)
        {
            return;
        }
        if (need < floats) need = floats;
        if (native.IsCreated) native.Dispose();
        // 分桶后分配已属罕见，ClearMemory 的 memset 成本可忽略；换来"即使 Unity
        // 只写了一部分也只读到静音"的安全底（ClearMemory 语义与原实现一致）
        native = new NativeArray<float>(need, Allocator.Persistent, NativeArrayOptions.ClearMemory);
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
