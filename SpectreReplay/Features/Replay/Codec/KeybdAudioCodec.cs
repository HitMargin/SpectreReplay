using System;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 键盘音压缩编解码器：WAV 文件字节 ↔ <see cref="KeybdAudioBlob"/>。
///
/// <para><b>为什么要单独做一层压缩，而不是把 wav 原样塞进回放。</b>
/// 键盘音是麦克风实录：mono 16bit 44.1kHz 约 88KB/s，一段 9 分钟的回放
/// 就能带 48MB 音频，比整份回放数据大两个数量级。而键盘音真正有效的
/// 内容只有「按下的一瞬间」——绝大部分时间轴是静音。所以这里做三件事：
/// 降采样（砍掉键盘声根本不需要的高频）、静音门（把静音压成整片 0）、
/// 4bit ADPCM（同一段波形的熵再降一半），再给纯静音块打标记——整块 1024
/// 个零样本只占 4 字节，长静音因此在外层 Brotli 里近乎不花字节。</para>
///
/// <para><b>为什么不复用 <c>WavLoader</c>。</b>它的 ParseHeader 是 private，
/// 而且解析结果直接喂给 <c>UnityEngine.AudioClip</c>；编解码器则要能在
/// 「没有 Unity 运行时」的环境里跑（离线自测、后台线程、纯数据导出）。
/// 把核心逻辑建在 Unity 类型上等于顺手把可测性锁死，所以这里独立实现
/// 一份最小解析——只认 RIFF/WAVE 的 chunk 结构，不碰任何 Unity API。</para>
///
/// <para><b>线程约定：</b>本类全是纯计算、无可变静态状态，可以在任意线程
/// 上跑；但一次 9 分钟的编码峰值内存约是样本数的 5 倍 float（几十到
/// 两百 MB），调用方应放到后台线程，别在主线程同步调。</para>
/// </summary>
internal static class KeybdAudioCodec
{
    // ── 契约常量 ───────────────────────────────────────────────────────
    // 这三个数会写进回放文件，**发布后就不能改**：改了以后旧文件会被解成噪声。

    /// <summary>IMA/DVI ADPCM，4bit。本工程实际产出的格式。</summary>
    internal const int CodecAdpcm = 1;

    /// <summary>G.711 mu-law，8bit。见 <c>DecodeMuLaw</c> 的说明。</summary>
    internal const int CodecMuLaw = 2;

    /// <summary>16bit PCM 原文。只在压缩路径意外失败时兜底。</summary>
    internal const int CodecPcm16 = 3;

    /// <summary>极限压缩：8kHz。键盘声的能量集中在低频敲击，8k 已经够听。</summary>
    internal const int QualityExtreme = 0;

    /// <summary>均衡：16kHz。默认档，敲击的「脆」还在。</summary>
    internal const int QualityBalanced = 1;

    /// <summary>高保真：22.05kHz。留给人耳挑剔或后期再处理。</summary>
    internal const int QualityHigh = 2;

    // ── 实现参数 ───────────────────────────────────────────────────────

    /// <summary>
    /// 静音门阈值，约 -58 dBFS。取这个值是为了「压掉房间底噪、留住呼吸声」：
    /// 麦克风实录的空场底噪通常在 -60 dBFS 附近，低于它的样本基本是噪声，
    /// 但再往上调就会切掉敲击尾部自然的衰减。
    /// </summary>
    private const float SilenceGate = 0.0013f;

    /// <summary>采样率合理性上限。44.1k/48k/96k/192k 都远低于它，但畸形文件可以写 int.MaxValue。</summary>
    private const int MaxSampleRate = 768000;

    /// <summary>声道数上限。键盘音实际恒为 1，这里只是防止畸形文件把下混循环撑爆。</summary>
    private const int MaxChannels = 16;

    /// <summary>
    /// 样本数硬上限（100M）。这是「荒谬值」的闸门：不设它的话，一份几字节的
    /// 坏负载声称 20 亿个样本，就能让解码端直接申请几个 GB。
    /// 9 分钟的 44.1kHz 实录只有 2400 万样本，离这个上限还很远。
    /// </summary>
    private const int MaxSampleCount = 100 * 1000 * 1000;

    /// <summary>ADPCM 每块样本数（约 64ms @16kHz）。见 <c>EncodeAdpcm</c> 的块结构说明。</summary>
    private const int AdpcmBlockSamples = 1024;

    /// <summary>ADPCM 块头：int16 预测器 + uint8 步长索引 + uint8 标记。</summary>
    private const int AdpcmBlockHeader = 4;

    /// <summary>块标记：整块都是静音，负载里不跟任何 nibble，解码端直接填 0。</summary>
    private const byte AdpcmFlagSilent = 1;

    /// <summary>
    /// 块标记：本块的 nibble 个数是奇数。
    ///
    /// <para><b>为什么需要这个 bit。</b>4bit 打包让「末块 928 个样本」和
    /// 「929 个样本」都只占 464 字节（少一个样本时最后一个 nibble 用不上，
    /// 字节数不变）。于是光比对负载长度，发现不了 SampleCount 被改掉 1。
    /// 把「nibble 数奇偶」写进块头，解码端就能反推出末块样本数的奇偶，
    /// 和 SampleCount 对不上就判坏——一个 bit 换掉一整类静默错位。</para>
    /// </summary>
    private const byte AdpcmFlagOddNibbles = 2;

    /// <summary>IMA/DVI 标准步长表。索引 0..88，最后一项 32767 正好顶到 int16 满量程。</summary>
    private static readonly int[] AdpcmSteps =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253,
        279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166,
        1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428,
        4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289,
        16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    };

    /// <summary>IMA/DVI 索引步进表（按量化后幅度的低 3 位索引）。</summary>
    private static readonly int[] AdpcmIndexStep = { -1, -1, -1, -1, 2, 4, 6, 8 };

    // ── 对外 API ───────────────────────────────────────────────────────

    /// <summary>
    /// 把一份 WAV 文件字节编码成压缩块。失败返回 <c>default</c> 并通过
    /// <paramref name="error"/> 给出中文短句；**任何情况下都不抛异常**。
    /// </summary>
    internal static KeybdAudioBlob Encode(byte[] wavFile, int quality, out string error)
    {
        error = null;

        WavFormat fmt;
        try
        {
            if (!TryParseWav(wavFile, out fmt, out error))
                return default;
        }
        catch (Exception ex)
        {
            error = "解析 WAV 头失败: " + ex.Message;
            return default;
        }

        try
        {
            float[] samples = ReadMono(wavFile, fmt);
            samples = Resample(samples, fmt.SampleRate, TargetRateFor(quality), out int rate);
            GateSilence(samples);
            if (samples.Length == 0)
            {
                error = "音频过短，降采样后没有任何样本";
                return default;
            }

            short[] pcm = ToPcm16(samples);
            byte[] payload = EncodeAdpcm(pcm);
            return new KeybdAudioBlob
            {
                Codec = CodecAdpcm,
                SampleRate = rate,
                Channels = 1,
                SampleCount = pcm.Length,
                Payload = payload,
            };
        }
        catch (Exception ex)
        {
            // 走到这里说明压缩路径本身出了意外（内存不足之类）。键盘音是
            // 「录一次就没了」的数据：宁可把体积换回来，也不能让整段音频
            // 跟着一起丢。所以退到最笨的 PCM16 原文，它只依赖已经验证过的
            // WAV 解析和取整，不再依赖任何状态机。
            string primary = ex.Message;
            try
            {
                float[] mono = ReadMono(wavFile, fmt);
                GateSilence(mono);
                if (mono.Length == 0)
                {
                    error = "音频为空";
                    return default;
                }

                short[] pcm = ToPcm16(mono);
                byte[] payload = new byte[pcm.Length * 2];
                for (int i = 0; i < pcm.Length; i++)
                {
                    payload[i * 2] = (byte)pcm[i];
                    payload[i * 2 + 1] = (byte)(pcm[i] >> 8);
                }

                return new KeybdAudioBlob
                {
                    Codec = CodecPcm16,
                    SampleRate = fmt.SampleRate,
                    Channels = 1,
                    SampleCount = pcm.Length,
                    Payload = payload,
                };
            }
            catch (Exception ex2)
            {
                error = "编码失败: " + primary + "；兜底也失败: " + ex2.Message;
                return default;
            }
        }
    }

    /// <summary>
    /// 解码成 float 样本（-1..1）与采样率。失败返回 false 并给中文短句，
    /// 同样不抛异常。
    ///
    /// <para><b>必须按 <see cref="KeybdAudioBlob.SampleCount"/> 精确校验。</b>
    /// 样本数是「调用方要拿去建 AudioClip 的长度」，多一个少一个都会让
    /// SetData 直接失败；而且用样本数反推负载长度，能在分配任何大数组之前
    /// 先把畸形负载挡掉。</para>
    /// </summary>
    internal static bool TryDecodeToSamples(
        KeybdAudioBlob blob, out float[] samples, out int sampleRate, out string error)
    {
        samples = null;
        sampleRate = 0;
        error = null;

        if (blob.Payload == null || blob.Payload.Length == 0)
        {
            error = "音频负载为空";
            return false;
        }

        if (blob.SampleRate <= 0 || blob.SampleRate > MaxSampleRate)
        {
            error = "采样率无效";
            return false;
        }

        if (blob.Channels < 1 || blob.Channels > MaxChannels)
        {
            error = "声道数无效";
            return false;
        }

        // 先卡上限再分配：下面的各解码分支还会用「负载字节数」反推样本数，
        // 但上限这一道必须独立存在——它是唯一不依赖负载内容的闸门。
        if (blob.SampleCount <= 0 || blob.SampleCount > MaxSampleCount)
        {
            error = "样本数无效";
            return false;
        }

        try
        {
            short[] pcm;
            switch (blob.Codec)
            {
                case CodecAdpcm:
                    pcm = DecodeAdpcm(blob.Payload, blob.SampleCount);
                    if (pcm == null)
                    {
                        error = "ADPCM 负载损坏或与样本数不符";
                        return false;
                    }

                    break;

                case CodecMuLaw:
                    pcm = DecodeMuLaw(blob.Payload, blob.SampleCount);
                    if (pcm == null)
                    {
                        error = "mu-law 负载长度与样本数不符";
                        return false;
                    }

                    break;

                case CodecPcm16:
                    pcm = DecodePcm16(blob.Payload, blob.SampleCount);
                    if (pcm == null)
                    {
                        error = "PCM16 负载长度与样本数不符";
                        return false;
                    }

                    break;

                default:
                    error = "未知的编码格式: " + blob.Codec;
                    return false;
            }

            float[] result = new float[pcm.Length];
            for (int i = 0; i < pcm.Length; i++)
                result[i] = pcm[i] / 32768f;

            samples = result;
            sampleRate = blob.SampleRate;
            return true;
        }
        catch (Exception ex)
        {
            samples = null;
            sampleRate = 0;
            error = "解码失败: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 解码并封装成标准 WAV 文件字节（PCM16 mono，44 字节规范头），
    /// 供「导出为音频文件」用。失败返回 null。
    /// </summary>
    internal static byte[] DecodeToWav(KeybdAudioBlob blob)
    {
        if (!TryDecodeToSamples(blob, out float[] samples, out int sampleRate, out _))
            return null;

        try
        {
            return BuildWav(samples, sampleRate);
        }
        catch (Exception)
        {
            // 这里唯一的失败原因是内存不够；导出是锦上添花的功能，
            // 不该把异常抛到 UI 事件里。
            return null;
        }
    }

    // ── WAV 解析 ───────────────────────────────────────────────────────

    /// <summary>从 RIFF/WAVE 里抠出来的、解码真正需要的几个字段。</summary>
    private struct WavFormat
    {
        internal int FormatTag;      // 1=PCM, 3=IEEE float
        internal int Channels;
        internal int SampleRate;
        internal int BitsPerSample;
        internal int DataOffset;     // data 块 payload 的起止
        internal int DataSize;
        internal int FrameCount;     // 完整帧数（已按声道/位深折算）
    }

    private static bool TryParseWav(byte[] wav, out WavFormat fmt, out string error)
    {
        fmt = default;
        error = null;

        if (wav == null || wav.Length < 12)
        {
            error = "音频数据为空或过短";
            return false;
        }

        if (!IsTag(wav, 0, 'R', 'I', 'F', 'F') || !IsTag(wav, 8, 'W', 'A', 'V', 'E'))
        {
            error = "不是 RIFF/WAVE 格式";
            return false;
        }

        bool hasFmt = false;
        bool hasData = false;

        // 逐块扫描。**不假设 fmt 一定在 data 前面**：有些工具会把 LIST/PEAK
        // 甚至 data 写到前面，若像 WavLoader 那样边扫边算，遇到 data 先到就
        // 只能报错。这里先把两块都记下来，最后再统一校验，顺序无关。
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            bool isFmt = IsTag(wav, pos, 'f', 'm', 't', ' ');
            bool isData = IsTag(wav, pos, 'd', 'a', 't', 'a');
            uint size = ReadU32(wav, pos + 4);
            long body = pos + 8L;
            long avail = wav.Length - body;

            if (isFmt)
            {
                if (size < 16 || avail < 16)
                {
                    error = "fmt 块不完整";
                    return false;
                }

                fmt.FormatTag = ReadU16(wav, (int)body);
                fmt.Channels = ReadU16(wav, (int)body + 2);
                fmt.SampleRate = ReadI32(wav, (int)body + 4);
                fmt.BitsPerSample = ReadU16(wav, (int)body + 14);
                hasFmt = true;
            }
            else if (isData)
            {
                if (size == 0)
                {
                    error = "data 块为空";
                    return false;
                }

                // 声明的长度超过文件实际剩余时按实际截断：录到一半崩溃的
                // 文件尾部会缺帧，但前面录到的部分仍然是有效音频，整段
                // 丢掉没有任何好处。
                long take = Math.Min((long)size, avail);
                if (take <= 0)
                {
                    error = "data 块为空";
                    return false;
                }

                fmt.DataOffset = (int)body;
                fmt.DataSize = (int)take;
                hasData = true;
            }

            // 块链按 4 字节对齐（RIFF 规定奇数长度的块后补一个填充字节）。
            long next = body + size + (size & 1);
            if (size > avail || next <= pos || next > int.MaxValue)
                break;

            pos = (int)next;
        }

        if (!hasFmt)
        {
            error = "缺少 fmt 块";
            return false;
        }

        if (!hasData)
        {
            error = "缺少 data 块";
            return false;
        }

        if (fmt.SampleRate <= 0 || fmt.SampleRate > MaxSampleRate)
        {
            error = "采样率无效";
            return false;
        }

        if (fmt.Channels <= 0 || fmt.Channels > MaxChannels)
        {
            error = "声道数无效";
            return false;
        }

        if (fmt.FormatTag == 1)
        {
            if (fmt.BitsPerSample != 8 && fmt.BitsPerSample != 16
                && fmt.BitsPerSample != 24 && fmt.BitsPerSample != 32)
            {
                error = "不支持的位深: " + fmt.BitsPerSample;
                return false;
            }
        }
        else if (fmt.FormatTag == 3)
        {
            if (fmt.BitsPerSample != 32)
            {
                error = "浮点 WAV 只支持 32 位";
                return false;
            }
        }
        else
        {
            // WAVE_FORMAT_EXTENSIBLE(0xFFFE) 之类也走这里：与其猜它里面装的是
            // PCM 还是 float，不如明确拒绝，免得解出一段噪声却没人发现。
            error = "不支持的音频格式: " + fmt.FormatTag;
            return false;
        }

        // 帧长自己算，不信 fmt 里的 blockAlign：编辑过的文件常有 blockAlign=0。
        int frameBytes = (fmt.BitsPerSample >> 3) * fmt.Channels;
        long frames = fmt.DataSize / frameBytes;
        if (frames <= 0)
        {
            error = "data 块不足以构成任何样本";
            return false;
        }

        if (frames > MaxSampleCount)
        {
            error = "音频过长";
            return false;
        }

        fmt.FrameCount = (int)frames;
        return true;
    }

    /// <summary>把整段音频解成单声道 float（多声道取平均），采样率保持源采样率。</summary>
    private static float[] ReadMono(byte[] wav, WavFormat fmt)
    {
        int bytesPerSample = fmt.BitsPerSample >> 3;
        int frameBytes = bytesPerSample * fmt.Channels;
        float[] mono = new float[fmt.FrameCount];

        for (int f = 0; f < fmt.FrameCount; f++)
        {
            // f*frameBytes 不会溢出：帧数由 DataSize/frameBytes 得出，
            // 乘积天然被 DataSize（int 范围内）限住。
            int frameOffset = fmt.DataOffset + f * frameBytes;
            if (fmt.Channels == 1)
            {
                mono[f] = ReadSample(wav, frameOffset, fmt);
                continue;
            }

            float sum = 0f;
            for (int c = 0; c < fmt.Channels; c++)
                sum += ReadSample(wav, frameOffset + c * bytesPerSample, fmt);

            mono[f] = sum / fmt.Channels;
        }

        return mono;
    }

    /// <summary>读一个样本并归一到 -1..1。调用方保证 <paramref name="offset"/> 处有完整样本。</summary>
    private static float ReadSample(byte[] wav, int offset, WavFormat fmt)
    {
        if (fmt.FormatTag == 3)
        {
            // WAV 是小端；Unity 只跑在小端平台，所以直接用 BitConverter，
            // 不做字节序检测（检测了也没有可用的大端运行时去跑）。
            float v = BitConverter.ToSingle(wav, offset);

            // 浮点 WAV 里真的会出现 NaN/Inf（某些 DAW 的导出缺陷）。
            // 它们会一路污染重采样和静音门，必须在这里掐掉。
            if (float.IsNaN(v))
                return 0f;

            if (v > 1f)
                return 1f;

            if (v < -1f)
                return -1f;

            return v;
        }

        switch (fmt.BitsPerSample)
        {
            case 8:
                // 8bit WAV 是**无符号**的，128 是零点。写成带符号会整段偏直流。
                return (wav[offset] - 128) / 128f;

            case 16:
                return (short)(wav[offset] | (wav[offset + 1] << 8)) / 32768f;

            case 24:
            {
                int v = wav[offset] | (wav[offset + 1] << 8) | (wav[offset + 2] << 16);
                if ((v & 0x800000) != 0)
                    v |= unchecked((int)0xFF000000);

                return v / 8388608f;
            }

            default:
                return ReadI32(wav, offset) / 2147483648f;
        }
    }

    // ── 重采样 ─────────────────────────────────────────────────────────

    /// <summary>目标采样率。未知档位按均衡处理，不报错——多一个返回值没用，静默降级更稳。</summary>
    private static int TargetRateFor(int quality)
    {
        switch (quality)
        {
            case QualityExtreme:
                return 8000;

            case QualityHigh:
                return 22050;

            case QualityBalanced:
            default:
                return 16000;
        }
    }

    /// <summary>
    /// 降采样到 <paramref name="dstRate"/>，返回新数组（源采样率本来就低时
    /// 原样返回，并通过 <paramref name="rate"/> 告诉调用方实际采样率）。
    ///
    /// <para><b>为什么必须先抗混叠。</b>直接每隔几个样本取一个，会让 8kHz
    /// 以上的内容折回到可听频段，形成一层跟着敲击走的「沙沙」噪声；这种
    /// 噪声一旦混进来，后面怎么压缩都去不掉。这里用滑动平均把奈奎斯特
    /// 以上的能量先压下去，再线性插值到目标网格。</para>
    ///
    /// <para><b>为什么滑动平均要做两遍。</b>单个矩形窗（滑动平均的频响是
    /// sinc/sin 形）在奈奎斯特附近的衰减只有 -4dB 左右，差不多等于没滤；
    /// 级联两遍相当于三角形窗，同样窗口长度下衰减到 -8~-9dB、12kHz 处
    /// 已过 -25dB，代价只是一次 O(n) 遍历和一块临时数组。</para>
    /// </summary>
    private static float[] Resample(float[] src, int srcRate, int dstRate, out int rate)
    {
        if (dstRate <= 0 || srcRate <= dstRate)
        {
            // **不上采样**：插值变不出真实信息，只会让样本数和压缩负载凭空变大。
            rate = srcRate;
            return src;
        }

        rate = dstRate;

        int window = (int)Math.Round((double)srcRate / dstRate);
        if (window < 3)
            window = 3;

        // 偶数窗没有对称中心：上面 MovingAverage 的 [i-left, i+right] 只能是
        // 左短右长，于是整体带半个样本的群延迟——等于把整段时间轴平移了一点点。
        // 对 SNR 是实打实的误差，对听感是没必要的时间偏移，用奇数窗直接消掉。
        if ((window & 1) == 0)
            window++;

        // 两次滤波乒乓复用两块数组：第一遍 src→scratch，第二遍 scratch→src。
        // 不能就地做第二遍——那时 src 里已是新值，读到的会是半滤波的数据。
        float[] scratch = new float[src.Length];
        MovingAverage(src, scratch, window);
        MovingAverage(scratch, src, window);

        double step = (double)srcRate / dstRate;
        int count = (int)((long)src.Length * dstRate / srcRate);
        float[] dst = new float[count];
        int last = src.Length - 1;
        for (int i = 0; i < count; i++)
        {
            double pos = i * step;
            int i0 = (int)pos;
            if (i0 >= last)
            {
                dst[i] = src[last];
                continue;
            }

            float frac = (float)(pos - i0);
            dst[i] = src[i0] + (src[i0 + 1] - src[i0]) * frac;
        }

        return dst;
    }

    /// <summary>
    /// 居中滑动平均，窗口 [i-left, i+right]，越界按边缘复制（等价于补零后再
    /// 归一化，但没有边界处的整体衰减）。
    ///
    /// <para>用双精度累加器：一千万级的样本一路 float 累加，误差会攒到
    /// 可见的程度（前缀和方案还要多一块 8 字节/样本的数组，这里两个指针
    /// 滑窗更省）。</para>
    /// </summary>
    private static void MovingAverage(float[] src, float[] dst, int window)
    {
        int n = src.Length;
        if (window <= 1 || n == 0)
        {
            Array.Copy(src, dst, n);
            return;
        }

        int left = (window - 1) >> 1;
        int right = window - 1 - left;
        int lo = 0;
        int hi = -1;
        double sum = 0;

        for (int i = 0; i < n; i++)
        {
            int wantLo = i - left;
            if (wantLo < 0)
                wantLo = 0;

            int wantHi = i + right;
            if (wantHi > n - 1)
                wantHi = n - 1;

            while (hi < wantHi)
            {
                hi++;
                sum += src[hi];
            }

            while (lo < wantLo)
            {
                sum -= src[lo];
                lo++;
            }

            dst[i] = (float)(sum / (hi - lo + 1));
        }
    }

    /// <summary>
    /// 静音门。低于阈值的样本直接写 0，长段静音就变成一整片 0x00，
    /// 外层 Brotli 只需几个字节就能表达。
    /// </summary>
    private static void GateSilence(float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];

            // 用两个反向比较而不是 Math.Abs：NaN 与任何数比较都是 false，
            // 这个写法顺手把 NaN 也一起清零；Math.Abs(NaN) 是 NaN，会漏过去。
            if (!(v >= SilenceGate) && !(v <= -SilenceGate))
                samples[i] = 0f;
        }
    }

    private static short[] ToPcm16(float[] samples)
    {
        short[] pcm = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i] * 32768f;
            if (v > 32767f)
                v = 32767f;
            else if (v < -32768f)
                v = -32768f;

            pcm[i] = (short)Math.Round(v);
        }

        return pcm;
    }

    // ── ADPCM ─────────────────────────────────────────────────────────

    /// <summary>
    /// 编码成 IMA/DVI ADPCM。
    ///
    /// <para><b>为什么是「分块 + 每块 4 字节头」而不是一路连续编码。</b>
    /// 纯连续的 ADPCM 状态完全由前面解出来的样本决定：一段长静音之后步长
    /// 索引会掉到 0（步长 7），紧接着的敲击要花十来毫秒才把步长追上来，
    /// 起音就被削软了——而键盘音的全部信息几乎都在起音上。每块重新写一次
    /// 「预测器 + 步长索引」，块首样本零误差落地，起音就完整保留；顺带
    /// 让坏掉一个字节只影响 64ms，而不是把后面整条流带歪。开销是 4 字节
    /// /1024 样本（0.39%）。</para>
    ///
    /// <para><b>块布局：</b>int16 预测器（= 块首样本，直接写原值）+
    /// uint8 步长索引 + uint8 标记 + ceil((count-1)/2) 字节的 4bit nibble。
    /// nibble 从低半字节开始装，每块单独重新对齐字节边界。标记字节里
    /// bit0 = 整块静音（<c>AdpcmFlagSilent</c>），bit1 = nibble 数为奇数
    /// （<c>AdpcmFlagOddNibbles</c>）。</para>
    ///
    /// <para><b>整块静音只写 4 字节。</b>键盘音 95% 以上的时间轴是静音，
    /// 而 ADPCM 对「已经是 0」的输入仍会吐出一串 nibble（步长要慢慢收敛）。
    /// 静音块标记让这一段既精确等于 0、又把负载从 516 字节压到 4 字节。
    /// 这一点对「长段静音在外层 Brotli 里近乎为零」是决定性的：连续静音
    /// 变成一串完全相同的 4 字节块，比一串随步长衰减的 nibble 好压得多。</para>
    /// </summary>
    private static byte[] EncodeAdpcm(short[] pcm)
    {
        int n = pcm.Length;

        // 先按「一个静音块都没有」的上界分配，编码完再裁掉尾部没用到的部分。
        // 这样不用为了算精确长度多扫一遍样本。
        byte[] payload = new byte[AdpcmPayloadUpperBound(n)];
        int outPos = 0;
        int sampleIndex = 0;

        while (sampleIndex < n)
        {
            int count = Math.Min(AdpcmBlockSamples, n - sampleIndex);

            if (IsAllZero(pcm, sampleIndex, count))
            {
                payload[outPos] = 0;
                payload[outPos + 1] = 0;
                payload[outPos + 2] = 0;
                payload[outPos + 3] = AdpcmFlagSilent;
                outPos += AdpcmBlockHeader;
                sampleIndex += count;
                continue;
            }

            int pred = pcm[sampleIndex];
            int index = ChooseStartIndex(pcm, sampleIndex, count);

            payload[outPos] = (byte)pred;
            payload[outPos + 1] = (byte)(pred >> 8);
            payload[outPos + 2] = (byte)index;
            // 奇偶标记：让解码端能校验末块样本数，见 AdpcmFlagOddNibbles。
            payload[outPos + 3] = ((count - 1) & 1) != 0 ? AdpcmFlagOddNibbles : (byte)0;
            outPos += AdpcmBlockHeader;

            int nibblePos = 0;
            for (int i = 1; i < count; i++)
            {
                int step = AdpcmSteps[index];
                int diff = pcm[sampleIndex + i] - pred;

                int sign;
                int mag;
                if (diff < 0)
                {
                    sign = 8;
                    mag = -diff;
                }
                else
                {
                    sign = 0;
                    mag = diff;
                }

                // 量化成 3 位幅度：依次和 step / step/2 / step/4 比大小。
                int nib = 0;
                int threshold = step;
                if (mag >= threshold)
                {
                    nib |= 4;
                    mag -= threshold;
                }

                threshold >>= 1;
                if (mag >= threshold)
                {
                    nib |= 2;
                    mag -= threshold;
                }

                threshold >>= 1;
                if (mag >= threshold)
                    nib |= 1;

                // 用**量化后的重建值**更新预测器，而不是用原始样本：
                // 编解码两端必须走完全相同的状态轨迹，否则误差会越飘越远。
                int diffq = step >> 3;
                if ((nib & 4) != 0)
                    diffq += step;

                if ((nib & 2) != 0)
                    diffq += step >> 1;

                if ((nib & 1) != 0)
                    diffq += step >> 2;

                pred = sign != 0 ? pred - diffq : pred + diffq;
                if (pred > 32767)
                    pred = 32767;
                else if (pred < -32768)
                    pred = -32768;

                index += AdpcmIndexStep[nib];
                if (index < 0)
                    index = 0;
                else if (index >= AdpcmSteps.Length)
                    index = AdpcmSteps.Length - 1;

                int value = sign | nib;
                int bytePos = outPos + (nibblePos >> 1);
                if ((nibblePos & 1) == 0)
                    payload[bytePos] = (byte)value;
                else
                    payload[bytePos] |= (byte)(value << 4);

                nibblePos++;
            }

            outPos += count >> 1;
            sampleIndex += count;
        }

        // 静音块比 nibble 块短得多，按上界分配通常会剩一大截，裁掉。
        if (outPos != payload.Length)
            Array.Resize(ref payload, outPos);

        return payload;
    }

    /// <summary>整块是否全为 0（静音门之后的长静音就是这样）。</summary>
    private static bool IsAllZero(short[] pcm, int start, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (pcm[start + i] != 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 每块开头用的步长索引。取块首至多 64 个样本的一阶差分均值 ×2 去逼近
    /// 步长表：差分才是 ADPCM 真正要跟的东西（一段响亮的低频正弦，相邻样本
    /// 差可能还没一段安静的高频噪声大一—按幅度定步长会全错）。
    ///
    /// <para>只看块首 64 个样本而不是整块：键盘音块内大部分是静音，整块
    /// 取均值会把「块尾那记敲击」的步长需求抹平；而步长本身的 1:1 自适应
    /// 在十来个样本内就能追上突变，起点只是为了别一上来就爆。</para>
    /// </summary>
    private static int ChooseStartIndex(short[] pcm, int start, int count)
    {
        int probe = Math.Min(count, 64);
        if (probe <= 1)
            return 0;

        long sum = 0;
        for (int i = 1; i < probe; i++)
        {
            int d = pcm[start + i] - pcm[start + i - 1];
            if (d < 0)
                d = -d;

            sum += d;
        }

        int target = (int)(sum / (probe - 1)) * 2;
        int best = 0;
        int bestErr = int.MaxValue;
        for (int i = 0; i < AdpcmSteps.Length; i++)
        {
            int err = Math.Abs(AdpcmSteps[i] - target);
            if (err < bestErr)
            {
                bestErr = err;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// ADPCM 负载的**上界**（假设一个静音块都没有）。编码端按它分配再裁尾，
    /// 不需要为了算精确长度多扫一遍样本。
    ///
    /// <para>解码端不能再用这个公式校验长度——静音块只有 4 字节，实际长度
    /// 取决于内容。校验改成了「边走边检查不越界 + 结束时正好用光」，
    /// 并用密度上界（每 4 字节最多 1024 样本）挡住荒谬的样本数声明。</para>
    /// </summary>
    private static int AdpcmPayloadUpperBound(int sampleCount)
    {
        int full = sampleCount / AdpcmBlockSamples;
        int rest = sampleCount - full * AdpcmBlockSamples;
        long size = (long)full * (AdpcmBlockHeader + (AdpcmBlockSamples >> 1));
        if (rest > 0)
            size += AdpcmBlockHeader + (rest >> 1);

        return (int)size;
    }

    /// <summary>
    /// ADPCM 解码。负载截断、内容不自洽、末尾多出字节时返回 null（调用方判失败）。
    ///
    /// <para><b>先卡「密度上界」再分配数组。</b>静音块让 4 个字节就能代表
    /// 1024 个样本，所以「样本数 ≤ 负载长度/4 × 1024」是这类负载的物理上限；
    /// 超出它的声明根本不可能是本编码器写出来的，直接拒绝，绝不为它预先
    /// 申请一块 float 数组。</para>
    /// </summary>
    private static short[] DecodeAdpcm(byte[] payload, int sampleCount)
    {
        long densityLimit = (long)(payload.Length / AdpcmBlockHeader) * AdpcmBlockSamples;
        if (sampleCount > densityLimit)
            return null;

        short[] pcm = new short[sampleCount];
        int inPos = 0;
        int outIndex = 0;

        while (outIndex < sampleCount)
        {
            int count = Math.Min(AdpcmBlockSamples, sampleCount - outIndex);
            if (inPos + AdpcmBlockHeader > payload.Length)
                return null;

            byte flags = payload[inPos + 3];

            // 未知标记位：宁可判坏，也不猜它的含义——猜错会解出一段噪声
            // 却没人发现，比明确失败糟糕得多。
            if ((flags & ~(AdpcmFlagSilent | AdpcmFlagOddNibbles)) != 0)
                return null;

            // 静音块：负载里没有 nibble，样本本来就是 0，跳过即可。
            if ((flags & AdpcmFlagSilent) != 0)
            {
                inPos += AdpcmBlockHeader;
                outIndex += count;
                continue;
            }

            // 奇偶校验：块头记的 nibble 数奇偶必须与 SampleCount 推算的一致。
            // 对不上说明末块被少算/多算了一个样本（4bit 打包本身区分不出 928/929）。
            bool oddNibbles = ((count - 1) & 1) != 0;
            if (oddNibbles != ((flags & AdpcmFlagOddNibbles) != 0))
                return null;

            int pred = ReadI16(payload, inPos);

            // 坏的索引会直接越界读步长表（IndexOutOfRange），先钳住。
            int index = payload[inPos + 2];
            if (index >= AdpcmSteps.Length)
                index = AdpcmSteps.Length - 1;

            inPos += AdpcmBlockHeader;

            if (inPos + (count >> 1) > payload.Length)
                return null;

            pcm[outIndex++] = (short)pred;

            for (int i = 1; i < count; i++)
            {
                int nibblePos = i - 1;
                byte packed = payload[inPos + (nibblePos >> 1)];
                int value = (nibblePos & 1) == 0 ? packed & 0x0F : (packed >> 4) & 0x0F;

                int step = AdpcmSteps[index];
                int diffq = step >> 3;
                if ((value & 4) != 0)
                    diffq += step;

                if ((value & 2) != 0)
                    diffq += step >> 1;

                if ((value & 1) != 0)
                    diffq += step >> 2;

                pred = (value & 8) != 0 ? pred - diffq : pred + diffq;
                if (pred > 32767)
                    pred = 32767;
                else if (pred < -32768)
                    pred = -32768;

                index += AdpcmIndexStep[value & 7];
                if (index < 0)
                    index = 0;
                else if (index >= AdpcmSteps.Length)
                    index = AdpcmSteps.Length - 1;

                pcm[outIndex++] = (short)pred;
            }

            inPos += count >> 1;
        }

        // 必须正好用光：剩下的字节说明负载被拼接/篡改过，按坏块处理。
        return outIndex == sampleCount && inPos == payload.Length ? pcm : null;
    }

    // ── mu-law / PCM16 ────────────────────────────────────────────────

    /// <summary>
    /// G.711 mu-law 解码。
    ///
    /// <para><b>编码端不用它，但解码端必须认它。</b>Codec 字段是**文件里的
    /// 数据**，不是只有本进程写过的值：手改过的、或将来某个版本为省事改用
    /// mu-law 写出来的回放，都会走到这里。少了这个分支，一份其实完好的音频
    /// 会整段「未知格式」掉——保留几十行解码远比事后排查便宜。</para>
    /// </summary>
    private static short[] DecodeMuLaw(byte[] payload, int sampleCount)
    {
        if (payload.Length != sampleCount)
            return null;

        short[] pcm = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            byte u = (byte)~payload[i];
            int magnitude = ((u & 0x0F) << 3) + 0x84;
            magnitude <<= (u & 0x70) >> 4;
            magnitude -= 0x84;
            pcm[i] = (short)((u & 0x80) != 0 ? -magnitude : magnitude);
        }

        return pcm;
    }

    private static short[] DecodePcm16(byte[] payload, int sampleCount)
    {
        // 先用 long 比较再分配：sampleCount*2 在 int 上可能溢出成负数，
        // 那样一个畸形块就能靠「负数 == 负载长度」骗过校验。
        if ((long)sampleCount * 2 != payload.Length)
            return null;

        short[] pcm = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            pcm[i] = ReadI16(payload, i * 2);

        return pcm;
    }

    // ── WAV 封装 ───────────────────────────────────────────────────────

    /// <summary>生成 44 字节规范头的 PCM16 mono WAV。字段长度必须自洽，否则各种播放器都拒收。</summary>
    private static byte[] BuildWav(float[] samples, int sampleRate)
    {
        int dataSize = samples.Length * 2;
        byte[] wav = new byte[44 + dataSize];

        wav[0] = (byte)'R';
        wav[1] = (byte)'I';
        wav[2] = (byte)'F';
        wav[3] = (byte)'F';
        WriteI32(wav, 4, 36 + dataSize);        // RIFF 长度 = 文件长 - 8
        wav[8] = (byte)'W';
        wav[9] = (byte)'A';
        wav[10] = (byte)'V';
        wav[11] = (byte)'E';
        wav[12] = (byte)'f';
        wav[13] = (byte)'m';
        wav[14] = (byte)'t';
        wav[15] = (byte)' ';
        WriteI32(wav, 16, 16);                  // fmt 块长度
        WriteI16(wav, 20, 1);                   // PCM
        WriteI16(wav, 22, 1);                   // mono
        WriteI32(wav, 24, sampleRate);
        WriteI32(wav, 28, sampleRate * 2);      // 字节率 = 采样率 × 帧长
        WriteI16(wav, 32, 2);                   // 帧长
        WriteI16(wav, 34, 16);                  // 位深
        wav[36] = (byte)'d';
        wav[37] = (byte)'a';
        wav[38] = (byte)'t';
        wav[39] = (byte)'a';
        WriteI32(wav, 40, dataSize);

        int o = 44;
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i] * 32768f;
            if (v > 32767f)
                v = 32767f;
            else if (v < -32768f)
                v = -32768f;

            WriteI16(wav, o, (short)Math.Round(v));
            o += 2;
        }

        return wav;
    }

    // ── 小工具 ─────────────────────────────────────────────────────────

    private static bool IsTag(byte[] b, int offset, char c0, char c1, char c2, char c3)
        => b[offset] == (byte)c0
           && b[offset + 1] == (byte)c1
           && b[offset + 2] == (byte)c2
           && b[offset + 3] == (byte)c3;

    /// <summary>读有符号 16 位（音频样本用，必须带符号扩展）。</summary>
    private static short ReadI16(byte[] b, int offset)
        => (short)(b[offset] | (b[offset + 1] << 8));

    /// <summary>读无符号 16 位（fmt 里的 tag/声道/位深用：0xFFFE 这种值不能被读成负数）。</summary>
    private static int ReadU16(byte[] b, int offset)
        => b[offset] | (b[offset + 1] << 8);

    private static int ReadI32(byte[] b, int offset)
        => b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24);

    private static uint ReadU32(byte[] b, int offset)
        => (uint)(b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24));

    private static void WriteI16(byte[] b, int offset, short value)
    {
        b[offset] = (byte)value;
        b[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteI32(byte[] b, int offset, int value)
    {
        b[offset] = (byte)value;
        b[offset + 1] = (byte)(value >> 8);
        b[offset + 2] = (byte)(value >> 16);
        b[offset + 3] = (byte)(value >> 24);
    }
}