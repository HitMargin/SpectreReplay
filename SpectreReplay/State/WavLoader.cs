using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpectreReplay;

public static class WavLoader
{
    private struct WavHeader
    {
        public int FormatTag;

        public int Channels;

        public int SampleRate;

        public int BitsPerSample;

        public int DataOffset;

        public int DataSize;

        public int TotalSamples;

        public bool IsValid => FormatTag > 0 && Channels > 0 && SampleRate > 0 && (BitsPerSample == 8 || BitsPerSample == 16 || BitsPerSample == 24 || BitsPerSample == 32);
    }

    public static AudioClip loaded_clip;

    internal static AudioSource musicSource;

    internal static GameObject audioGO;

    internal static AudioClip LoadFromFile(string filePath, string expectedHash = null, bool streamAudio = false)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError(("[WavLoader] 文件不存在: " + filePath));
            return null;
        }
        if (!SpectreState.DebugMode || !SpectreState.debug_skip_Verification)
        {
            if (!string.IsNullOrEmpty(expectedHash))
            {
                if (!AudioRecorder.VerifyAudioFile(filePath, expectedHash))
                {
                    Debug.LogError(("[WavLoader] 文件哈希验证失败: " + filePath));
                    return null;
                }
                Debug.Log(("[WavLoader] 文件哈希验证通过: " + filePath));
            }
        }
        else
        {
            Debug.Log("Debug模式跳过音频校验");
        }
        try
        {
            byte[] wavData = File.ReadAllBytes(filePath);
            return LoadFromMemory(wavData, Path.GetFileNameWithoutExtension(filePath), streamAudio);
        }
        catch (Exception ex)
        {
            Debug.LogError(("[WavLoader] 读取文件失败: " + ex.Message));
            return null;
        }
    }

    internal static AudioClip LoadFromMemory(byte[] wavData, string clipName = "WAV_Audio", bool streamAudio = false)
    {
        if (wavData == null || wavData.Length < 44)
        {
            Debug.LogError("[WavLoader] 数据无效或过小");
            return null;
        }
        // clip 必须在 try 外声明：AudioClip 是原生资源，Create 之后的任何异常路径
        // （SetData 反射调用失败、ConvertWavToFloat 之后的意外）都要能销毁它，
        // 否则每次加载失败泄漏一份原生内存。原代码只在"SetData 返回 false"时
        // Destroy，"SetData 抛异常"时直接被下面的 catch 吞掉并 return null。
        AudioClip val = null;
        try
        {
            WavHeader header = ParseHeader(wavData);
            if (!header.IsValid)
            {
                Debug.LogError("[WavLoader] WAV 头部解析失败");
                return null;
            }
            Debug.Log($"[WavLoader] 加载 WAV: {header.Channels}ch, {header.SampleRate}Hz, {header.BitsPerSample}bit, {header.TotalSamples} 样本");
            float[] array = ConvertWavToFloat(wavData, header);
            val = AudioClip.Create(clipName, header.TotalSamples, header.Channels, header.SampleRate, streamAudio);
            var setDataMethod = typeof(AudioClip).GetMethod("SetData", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(float[]), typeof(int) }, null);
            if (setDataMethod == null)
            {
                // 走不到 Invoke（那会是 NullReferenceException，错误信息毫无指向性）
                Debug.LogError("[WavLoader] 找不到 AudioClip.SetData(float[], int)，加载中止");
                Object.Destroy(val);
                return null;
            }
            if (!(bool)setDataMethod.Invoke(val, new object[] { array, 0 }))
            {
                Debug.LogError("[WavLoader] SetData 失败");
                Object.Destroy(val);
                return null;
            }
            return val;
        }
        catch (Exception ex)
        {
            // 走到这里说明 Create 已经成功、但后续步骤抛了异常 → 必须销毁，
            // 否则每次加载失败泄漏一份原生内存
            if (val != null) Object.Destroy(val);
            Debug.LogError(("[WavLoader] 加载失败: " + ex.Message));
            return null;
        }
    }

    private static WavHeader ParseHeader(byte[] data)
    {
        WavHeader result = default(WavHeader);
        using (MemoryStream memoryStream = new MemoryStream(data))
        {
            using BinaryReader binaryReader = new BinaryReader(memoryStream);
            string text = new string(binaryReader.ReadChars(4));
            if (text != "RIFF")
            {
                Debug.LogError(("[WavLoader] 不是 RIFF 格式: " + text));
                return result;
            }
            binaryReader.ReadInt32();
            string text2 = new string(binaryReader.ReadChars(4));
            if (text2 != "WAVE")
            {
                Debug.LogError(("[WavLoader] 不是 WAVE 格式: " + text2));
                return result;
            }
            while (memoryStream.Position < memoryStream.Length - 8)
            {
                string text3 = new string(binaryReader.ReadChars(4));
                int num = binaryReader.ReadInt32();
                if (num < 0 || memoryStream.Position + num > memoryStream.Length)
                {
                    Debug.LogError($"[WavLoader] 无效的块大小: {num}");
                    return result;
                }
                if (text3 == "fmt ")
                {
                    if (num < 16)
                    {
                        Debug.LogError($"[WavLoader] fmt 块太小: {num}");
                        return result;
                    }
                    int num2 = binaryReader.ReadInt16();
                    result.FormatTag = num2;
                    if (num2 != 1 && num2 != 3)
                    {
                        Debug.LogError($"[WavLoader] 不支持的音频格式: {num2}（仅支持 PCM）");
                        return result;
                    }
                    result.Channels = binaryReader.ReadInt16();
                    result.SampleRate = binaryReader.ReadInt32();
                    binaryReader.ReadInt32();
                    binaryReader.ReadInt16();
                    result.BitsPerSample = binaryReader.ReadInt16();
                    // 声道数/位深先校验：data 块里要用它们做除法算样本数，
                    // 0 声道或 <8 位深（内层 /8 得 0）会直接抛 DivideByZeroException，
                    // 后续的 TotalSamples<=0 守卫根本来不及生效。
                    if (result.Channels <= 0)
                    {
                        Debug.LogError($"[WavLoader] 声道数无效: {result.Channels}");
                        return result;
                    }
                    if (result.BitsPerSample != 8 && result.BitsPerSample != 16
                        && result.BitsPerSample != 24 && result.BitsPerSample != 32)
                    {
                        // 不在支持列表（如 12/20/64 位）时 ConvertWavToFloat 的 switch
                        // 会落到 default 把每个样本写成 0 —— 整段静默且无任何提示。
                        Debug.LogError($"[WavLoader] 不支持的位深: {result.BitsPerSample}（支持 8/16/24/32）");
                        return result;
                    }
                    if (num > 16)
                    {
                        binaryReader.ReadBytes(num - 16);
                    }
                }
                else
                {
                    if (text3 == "data")
                    {
                        if (num <= 0)
                        {
                            Debug.LogError($"[WavLoader] data 块大小无效: {num}");
                            return result;
                        }
                        result.DataSize = num;
                        result.DataOffset = (int)memoryStream.Position;
                        // data 块可能排在 fmt 之前（畸形文件），此时 Channels/
                        // BitsPerSample 仍是 0，除法会炸——显式判一次
                        if (result.Channels <= 0 || result.BitsPerSample < 8)
                        {
                            Debug.LogError("[WavLoader] data 块出现在 fmt 块之前，无法确定样本格式");
                            return result;
                        }
                        result.TotalSamples = num / (result.Channels * (result.BitsPerSample / 8));
                        if (result.TotalSamples <= 0)
                        {
                            Debug.LogError($"[WavLoader] data 块数据不足以构成任何样本: {num} 字节");
                            return result;
                        }
                        break;
                    }
                    binaryReader.ReadBytes(num);
                }
            }
        }
        return result;
    }

    private static float[] ConvertWavToFloat(byte[] wavData, WavHeader header)
    {
        int num = header.BitsPerSample / 8;
        int num2 = header.DataSize / num;
        // 帧长为 channels*num，data 块大小不一定是整数帧（截断/编辑过的文件）。
        // 此时按字节算出的样本数会超过 AudioClip 长度，SetData 因长度不匹配直接返回
        // false → 整个音频加载失败。夹到整帧数，只丢尾部残帧即可正常加载。
        int frames = header.TotalSamples;
        if (num > 0 && frames > 0)
        {
            int frameSamples = frames * header.Channels;
            if (num2 > frameSamples) num2 = frameSamples;
        }
        float[] array = new float[num2];
        for (int i = 0; i < num2; i++)
        {
            long longOffset = header.DataOffset + (long)i * num;
            if (longOffset + num > wavData.Length)
            {
                break;
            }
            int num3 = (int)longOffset;
            switch (header.BitsPerSample)
            {
                case 8:
                    array[i] = (float)(wavData[num3] - 128) / 128f;
                    break;
                case 16:
                    {
                        short num5 = (short)(wavData[num3] | (wavData[num3 + 1] << 8));
                        array[i] = (float)num5 / 32768f;
                        break;
                    }
                case 24:
                    {
                        int num6 = wavData[num3] | (wavData[num3 + 1] << 8) | (wavData[num3 + 2] << 16);
                        if ((num6 & 0x800000) != 0)
                        {
                            num6 |= -16777216;
                        }
                        array[i] = (float)num6 / 8388608f;
                        break;
                    }
                case 32:
                    {
                        if (header.FormatTag == 3)
                        {
                            array[i] = BitConverter.ToSingle(wavData, num3);
                            break;
                        }
                        int num4 = BitConverter.ToInt32(wavData, num3);
                        array[i] = (float)num4 / 2.1474836E+09f;
                        break;
                    }
                default:
                    array[i] = 0f;
                    break;
            }
        }
        return array;
    }

    internal static void Play_keybdsound(AudioClip snd, float volume)
    {
        musicSource.clip = snd;
        musicSource.volume = volume;
        musicSource.Play();
        Debug.Log("开始播放！");
    }

    internal static void Stop_keybdsound()
    {
        musicSource.Stop();
    }

    internal static void reset()
    {
        if (musicSource == null)
        {
            audioGO = new GameObject("Spectre_AudioSource");
            audioGO.hideFlags = (HideFlags)61;
            Object.DontDestroyOnLoad(audioGO);
            musicSource = audioGO.AddComponent<AudioSource>();
            musicSource.playOnAwake = false;
        }
    }
}
