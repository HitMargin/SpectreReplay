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

    // ── 静默诊断 ──────────────────────────────────────────────────────
    // 「mixdown silent」是一类症状而不是一个原因：AudioRenderer 抓不到混音，
    // 可能是游戏被静音、输出设备被独占（同时开麦克风录音最典型）、蓝牙在
    // A2DP/HFP 之间切换导致设备重建，也可能是 dsp 时钟压根没推进。
    // 这些的处置完全不同，而日志里只有一句 "silent" 时全都无法区分——
    // 实测反馈就是这样：用户只看到「音频捕获失败」，不知道要改什么。
    //
    // 所以静默期间持续记录三组量，判死时一并写进 DeathReason：
    //   ① 每帧拿到的样本数（恒 0 = 根本没有可捕获的混音，不是"抓到静音"）
    //   ② AudioSettings.dspTime 是否在推进（不推进 = 音频系统整体停了）
    //   ③ 设备/声道/音量快照（被独占或切设备会在这里体现）
    private int firstSilentSampleCount = -1;   // 首次静默时 GetSampleCount 的返回值
    private int maxSilentSampleCount;          // 静默期间见过的最大样本数（>0 说明偶尔有数据）
    private double firstSilentDsp = double.NaN;
    private double lastSilentDsp = double.NaN;
    private bool dspAdvanced;

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
        // 启动时把设备快照整条打出来：故障反馈里最常见的一句是「以前能渲染，现在不行」，
        // 而"现在"与"以前"的差别往往就在这几项（换了耳机、插了 HDMI、改了采样率）。
        // 有了这一行，两次日志可以直接对拍。
        string cfgText;
        try
        {
            AudioConfiguration cfg = AudioSettings.GetConfiguration();
            cfgText = "dspBufferSize=" + cfg.dspBufferSize
                + " numRealVoices=" + cfg.numRealVoices
                + " numVirtualVoices=" + cfg.numVirtualVoices
                + " cfgSampleRate=" + cfg.sampleRate
                + " speakerMode=" + cfg.speakerMode;
        }
        catch (Exception ex)
        {
            cfgText = "cfgUnreadable=" + ex.GetType().Name + ":" + ex.Message;
        }
        Debug.Log("[SpectreReplay] mixdown started. sampleRate=" + SampleRate
            + " channels=" + Channels
            + " " + cfgText
            + " dspNow=" + AudioSettings.dspTime.ToString("0.0000"));
    }

    internal void CaptureFrame()
    {
        if (Dead || !capturing) return;
        frameNo++;
        int perChannel = AudioRenderer.GetSampleCountForCaptureFrame();
        int floats = Math.Max(0, perChannel) * Channels;
        if (floats == 0)
        {
            OnSilence(perChannel);
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

    private void OnSilence(int perChannel)
    {
        // 采样诊断：只在静默期记账，正常帧（:88 的 silentFrames = 0）不会走到这里。
        if (firstSilentSampleCount < 0)
        {
            firstSilentSampleCount = perChannel;
            firstSilentDsp = AudioSettings.dspTime;
        }
        if (perChannel > maxSilentSampleCount)
            maxSilentSampleCount = perChannel;
        double now = AudioSettings.dspTime;
        if (!double.IsNaN(firstSilentDsp) && now - firstSilentDsp > 0.001)
            dspAdvanced = true;
        lastSilentDsp = now;

        silentFrames++;
        if (silentFrames == 1 || silentFrames % fps == 0)
            Debug.Log("[SpectreReplay] mixdown silent. frame=" + frameNo
                + " consecutive=" + silentFrames
                + " samples=" + perChannel
                + " dsp=" + now.ToString("0.0000")
                + " dspAdvancing=" + dspAdvanced);
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
                    DeathReason = "AudioRenderer restart failed" + DescribeSilence();
                }
                return;
            }
            Debug.Log("[SpectreReplay] mixdown restarted, attempt " + restarts
                + " (silent for " + (fps) + " frames, last samples=" + perChannel + ")");
            return;
        }
        if (restarts >= RestartBudget && silentFrames >= giveUpFrames)
        {
            Dead = true;
            DeathReason = "mixdown silent after " + restarts + " restarts" + DescribeSilence();
        }
    }

    /// <summary>
    /// 把静默期间采到的诊断量拼成一句话，供 <see cref="DeathReason"/> 用（会进 UI 提示）。
    ///
    /// 文案按「哪种原因最可能、用户该做什么」排序，而不是按采集顺序堆字段：
    /// <list type="bullet">
    /// <item><c>samples=0</c> 恒成立且 dsp 不推进 → 音频系统整体停了（设备被占用/静音）。</item>
    /// <item><c>samples=0</c> 但 dsp 在推进 → 有音频时钟但没有混音输出，多为输出设备被独占。</item>
    /// <item><c>samples&gt;0</c> 却仍判静默 → 偶发拿不到，属于时序抖动而非设备故障。</item>
    /// </list>
    /// </summary>
    private string DescribeSilence()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("; samples=").Append(firstSilentSampleCount < 0 ? 0 : firstSilentSampleCount);
        if (maxSilentSampleCount > 0)
            sb.Append("(max ").Append(maxSilentSampleCount).Append(')');
        sb.Append(" channels=").Append(Channels);
        sb.Append(" rate=").Append(SampleRate);
        sb.Append(" dspAdvancing=").Append(dspAdvanced);
        if (!double.IsNaN(firstSilentDsp) && !double.IsNaN(lastSilentDsp))
            sb.Append(" dspDelta=").Append((lastSilentDsp - firstSilentDsp).ToString("0.00"));
        try
        {
            AudioConfiguration cfg = AudioSettings.GetConfiguration();
            sb.Append(" dspBuffer=").Append(cfg.dspBufferSize);
            sb.Append(" actualRate=").Append(AudioSettings.outputSampleRate);
            sb.Append(" speakerMode=").Append(AudioSettings.speakerMode);
        }
        catch (Exception ex)
        {
            sb.Append(" cfgUnreadable=").Append(ex.GetType().Name);
        }
        // 只在「拿不到样本」时给行动建议：偶发抖动不该让用户去翻设备设置。
        // **建议不在这里拼中文**：这段会进 UI 提示，而玩家可见文案必须走 Loc 表
        // （硬写中文等于只服务中文用户，闸门的「硬编码文案」检查也会红）。
        // 原因定性用机器可读的短标记，完整排查提示交给 Diagnose() 写进 Player.log。
        if (firstSilentSampleCount <= 0)
            sb.Append(dspAdvanced ? "; cause=dsp-advancing-but-no-mix" : "; cause=dsp-frozen");
        return sb.ToString();
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

    /// <summary>
    /// 判死时的完整诊断串，专供 Player.log（比 DeathReason 更全：含时长与帧数）。
    /// 与 <see cref="DescribeSilence"/> 分开是因为后者要进 UI 提示，必须短；
    /// 日志这条可以尽情长，反正用户只负责整段贴出来。
    /// </summary>
    internal string Diagnose()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("frames=").Append(frameNo);
        sb.Append(" written=").Append(written).Append("B(").Append(Seconds.ToString("0.000")).Append("s)");
        sb.Append(" silenceFirstSamples=").Append(firstSilentSampleCount);
        sb.Append(" silenceMaxSamples=").Append(maxSilentSampleCount);
        sb.Append(" restarts=").Append(restarts);
        sb.Append(" dspAdvancing=").Append(dspAdvanced);
        if (!double.IsNaN(firstSilentDsp) && !double.IsNaN(lastSilentDsp))
        {
            sb.Append(" dspFirst=").Append(firstSilentDsp.ToString("0.0000"));
            sb.Append(" dspLast=").Append(lastSilentDsp.ToString("0.0000"));
            sb.Append(" dspDelta=").Append((lastSilentDsp - firstSilentDsp).ToString("0.000"));
        }
        sb.Append(" channels=").Append(Channels);
        sb.Append(" rate=").Append(SampleRate);
        try
        {
            AudioConfiguration cfg = AudioSettings.GetConfiguration();
            sb.Append(" dspBuffer=").Append(cfg.dspBufferSize);
            sb.Append(" numBuffers=").Append(cfg.numRealVoices).Append('/').Append(cfg.numVirtualVoices);
            sb.Append(" speakerMode=").Append(cfg.speakerMode);
            sb.Append(" sampleRate=").Append(cfg.sampleRate);
        }
        catch (Exception ex)
        {
            sb.Append(" cfg=").Append(ex.GetType().Name).Append(':').Append(ex.Message);
        }
        sb.Append(" reason=").Append(DeathReason);
        return sb.ToString();
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
