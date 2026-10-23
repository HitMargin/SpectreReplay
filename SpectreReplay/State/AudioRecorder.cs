using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using ThreadPriority = System.Threading.ThreadPriority;
using Object = UnityEngine.Object;
using UnityEngine;

namespace SpectreReplay;

public class AudioRecorder : MonoBehaviour
{
    private static AudioRecorder _instance;

    [Header("录制配置")]
    [SerializeField]
    private int recordingFrequency = 44100;

    [SerializeField]
    private int maxRecordingLength = 10;

    [SerializeField]
    private int chunkSize = 4096;

    private HashAlgorithm hashAlgorithm;

    private readonly object hashLock = new object();

    private bool isHashFinalized = false;

    // 跨线程标志：主线程在 StopRecording 与 Cleanup 里置 false，
    // 处理线程在 StreamProcessingLoop 的 while 条件里读。**必须 volatile**——
    // 非 volatile 的字段读可以被 JIT 缓存/重排，处理线程可能迟迟看不到停止标志，
    // 于是 Join(1000) 超时后主线程还得干等它。
    //
    // **注意 volatile 只保证可见性，不保证互斥。** 真正避免"一边关流一边写流"
    // 的是 FinalizeWavFile 的调用点持有了 bufferLock（StopRecording 里
    // `lock (bufferLock) { ... FinalizeWavFile(); }`）——采集线程写盘也持这把锁，
    // 所以 Join 超时后主线程会在锁上等当前 chunk 做完，而不是并发写。
    // 采集线程每轮只睡 10ms、整个循环体有 try/catch 兜底，等待有上界，
    // 不会死锁。改这块时**别把 volatile 当成锁来用**。
    private volatile bool isRecording = false;

    // 同理：主线程赋值，处理线程在循环里判空后调用 GetData
    internal volatile AudioClip recordingClip;
    private static MethodInfo _getDataMethod;

    private string microphoneDevice;

    private Thread processingThread;

    private Thread initThread;

    private readonly object bufferLock = new object();

    private List<byte> wavDataBuffer = new List<byte>();

    // 复用缓冲区，避免后台音频线程每 chunk 分配（反射 Invoke 调用方式保持不变）
    private float[] _sampleBuffer;
    private byte[] _byteBuffer;
    private readonly object[] _getDataArgs = new object[2];

    private FileStream fileStream;

    private string currentFilePath;

    private long wavDataSizePosition;

    internal static AudioRecorder Instance
    {
        get
        {
            if (_instance == null)
            {
                GameObject val = new GameObject("AudioRecorder");
                _instance = val.AddComponent<AudioRecorder>();
                Object.DontDestroyOnLoad(val);
            }
            return _instance;
        }
    }

    internal string AudioHash { get; private set; }

    internal float ProcessingLatency { get; private set; }

    internal bool IsRecording => isRecording;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Object.Destroy(this.gameObject);
        }
        else
        {
            _instance = this;
        }
    }

    internal bool StartRecording(string filePath = null, string deviceName = null, int duration = 10, int frequency = 44100)
    {
        if (isRecording)
        {
            Debug.Log("已经在录制中");
            return false;
        }
        if (Microphone.devices.Length == 0)
        {
            Debug.Log("没有找到可用的麦克风设备");
            return false;
        }
        try
        {
            if (!string.IsNullOrEmpty(deviceName) && ValidateDevice(deviceName)
                && deviceName != microphoneDevice)
            {
                if (!string.IsNullOrEmpty(microphoneDevice) && Microphone.IsRecording(microphoneDevice))
                    Microphone.End(microphoneDevice);
                microphoneDevice = deviceName;
            }
            lock (bufferLock)
            {
                wavDataBuffer.Clear();
                // 丢弃旧的 WAV 流之前必须先关闭：InitializeWavFile 用 FileShare.Read 打开
                // （不含 Delete），直接置 null 会让句柄泄漏——那个文件此后删不掉
                // （Windows 上 File.Delete 抛 IOException）、也写不进去。
                // 走到这里说明上一次录音没走正常的 FinalizeWavFile/Cleanup 收尾
                // （例如 StartRecording 中途失败后重试、或异常中断）。
                if (fileStream != null)
                {
                    try
                    {
                        fileStream.Close();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[AudioRecorder] 关闭上一次残留的 WAV 流失败"
                            + "（旧的 tmp.wav 可能删不掉，本次录音会覆盖写入）: " + ex.Message);
                    }
                    fileStream = null;
                }
            }
            bool prewarmed = Microphone.IsRecording(microphoneDevice);
            currentFilePath = filePath;
            if (!prewarmed)
            {
                recordingFrequency = frequency;
                maxRecordingLength = duration;
            }
            if (!string.IsNullOrEmpty(filePath))
            {
                initThread = new Thread(InitializeWavFile)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                initThread.Start(filePath);
            }
            if (!prewarmed)
            {
                Debug.Log("未能预先开始录制，可能存在卡顿");
                startMicrophone(microphoneDevice);
            }
            if (!Microphone.IsRecording(microphoneDevice))
            {
                Debug.Log("开始录制失败！");
                // 走到这里时 initThread 可能已经打开了 WAV 流（它在上一步就启动了），
                // 直接 return 会把句柄泄漏在那里：文件删不掉（Windows 上 FileShare.Read
                // 不含 Delete），下次 StartRecording 也会撞上残留。收尾后再报失败。
                WaitForInitThread();
                CloseFileStreamQuietly();
                return false;
            }
            isRecording = true;
            processingThread = new Thread(StreamProcessingLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            processingThread.Start();
            // 打实际生效的采样率而非形参：预热路径下形参被忽略，打 frequency 会误导排查
            Debug.Log($"[AudioRecorder] 流式录制开始 - 设备: {microphoneDevice}, 采样率: {recordingFrequency}Hz");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            StopAllCoroutines();
            Cleanup();
            return false;
        }
    }

    private void StreamProcessingLoop()
    {
        // tempBuffer removed: reuse _sampleBuffer in ProcessRange
        AudioClip clip = recordingClip;
        int channels = ((clip == null) ? 1 : clip.channels);
        int lastPosition = -1;
        // 本线程是**后台线程**且没有外层兜底：未捕获的异常会一路逃出线程入口，
        // 按 CLR 语义直接终止进程 —— 一次采集期的偶发异常就会让游戏崩溃。
        // 这里兜住并退出循环（录音本来就该在 isRecording 变 false 时结束）。
        try
        {
            while (isRecording && recordingClip != null)
            {
                DateTime now = DateTime.Now;
                int position = Microphone.GetPosition(microphoneDevice);
                if (lastPosition == -1)
                {
                    lastPosition = position;
                }
                if (position < lastPosition)
                {
                    ProcessRange(lastPosition, recordingClip.samples, channels);
                    ProcessRange(0, position, channels);
                }
                else if (position > lastPosition)
                {
                    ProcessRange(lastPosition, position, channels);
                }
                lastPosition = position;
                ProcessingLatency = (float)(DateTime.Now - now).TotalMilliseconds;
                Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[AudioRecorder] 音频采集线程异常，采集提前结束: " + ex);
        }
    }

    private void ProcessRange(int startSample, int endSample, int channels)
    {
        int remaining = endSample - startSample;
        if (remaining > 0)
        {
            int sampleStart = startSample;
            while (remaining > 0)
            {
                int chunkLength = Mathf.Min(chunkSize, remaining);
                int needed = chunkLength * channels;
                if (_sampleBuffer == null || _sampleBuffer.Length < needed)
                    _sampleBuffer = new float[needed];
                if (_byteBuffer == null || _byteBuffer.Length < needed * 2)
                    _byteBuffer = new byte[needed * 2];
                if (_getDataMethod == null)
                    _getDataMethod = typeof(AudioClip).GetMethod("GetData", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(float[]), typeof(int) }, null);
                var clip = recordingClip;
                if (clip == null) break;
                _getDataArgs[0] = _sampleBuffer;
                _getDataArgs[1] = sampleStart;
                _getDataMethod.Invoke(clip, _getDataArgs);
                ProcessAudioChunk(_sampleBuffer, needed, channels);
                sampleStart += chunkLength;
                remaining -= chunkLength;
            }
        }
    }

    private void ProcessAudioChunk(float[] samples, int sampleCount, int channels)
    {
        byte[] bytes = _byteBuffer;
        // **必须下混成单声道。** 文件头由 CreateWAVHeader(1, ...) 固定声明为单声道，
        // 而 samples 是 AudioClip.GetData 出来的交织数据（channels 由麦克风决定）。
        // 原实现按 sampleCount 逐样本写盘，立体声设备下就产出"头是单声道、数据是
        // 双声道"的文件：播放时长只有一半（听感像 2 倍速）、音调被拉高。
        // 这里统一取各声道均值，让头部声明与落盘数据一致。
        // _byteBuffer 只增不减，实际有效字节以本次帧数为准；
        // 若按 bytes.Length 写盘/哈希，缓冲被大块撑大后每块都会把上一块的陈旧编码灌进文件
        if (channels < 1) channels = 1;
        int frames = sampleCount / channels;
        int byteCount = frames * 2;
        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * channels;
            float value;
            if (channels == 1)
            {
                value = samples[baseIndex];
            }
            else
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++) sum += samples[baseIndex + c];
                value = sum / channels;
            }
            short sample = (short)Mathf.Clamp(value * 32767f, -32768f, 32767f);
            bytes[f * 2] = (byte)(sample & 0xFF);
            bytes[f * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        lock (bufferLock)
        {
            if (fileStream != null && fileStream.CanWrite)
            {
                if (wavDataBuffer.Count != 0)
                {
                    byte[] buffered = wavDataBuffer.ToArray();
                    fileStream.Write(buffered, 0, buffered.Length);
                    lock (hashLock)
                    {
                        if (hashAlgorithm != null && !isHashFinalized)
                        {
                            hashAlgorithm.TransformBlock(buffered, 0, buffered.Length, null, 0);
                        }
                    }
                    wavDataBuffer.Clear();
                }
                fileStream.Write(bytes, 0, byteCount);
                lock (hashLock)
                {
                    if (hashAlgorithm != null && !isHashFinalized)
                    {
                        hashAlgorithm.TransformBlock(bytes, 0, byteCount, null, 0);
                    }
                }
            }
            else
            {
                // 文件未就绪的暂存路径同样只收有效字节
                //（此路径仅持续到 init 线程发布 fileStream，通常一两个块，按字节 Add 可接受）
                for (int i = 0; i < byteCount; i++) wavDataBuffer.Add(bytes[i]);
            }
        }
    }

    private void InitializeWavFile(object obj)
    {
        // 本方法跑在后台线程上，且下面全是会抛的文件系统操作（路径非法 / 目录无权限 /
        // FileMode.Create 撞上被占用的文件）。未捕获的异常逃出线程入口会按 CLR 语义
        // **终止整个进程**——即"录不上音"会变成"游戏直接崩"。
        // stream 必须在 try 外声明：它在 try 内创建（下方 new FileStream 那行），
        // 若之后的 CreateWAVHeader / Write 抛异常，未关闭的句柄会以 FileShare.Read
        // 占住文件 ——Windows 上 File.Delete 对这种句柄直接失败，"清理陈旧 tmp.wav"会连带失效。
        FileStream stream = null;
        try
        {
            if (obj is string)
            {
                string path = (string)obj;
                string directoryName = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
                {
                    Directory.CreateDirectory(directoryName);
                }
                stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                lock (hashLock)
                {
                    hashAlgorithm = SHA256.Create();
                    isHashFinalized = false;
                    AudioHash = null;
                }
                byte[] header = CreateWAVHeader(1, recordingFrequency, 16, 0);
                stream.Write(header, 0, header.Length);
                wavDataSizePosition = stream.Position - 4;
                // 44 字节头写完后再发布 fileStream：采集线程在 bufferLock 下读它，
                // 保证 PCM 数据不会先于文件头落盘
                lock (bufferLock)
                {
                    fileStream = stream;
                }
                // 所有权已转交给 fileStream（由 Cleanup / CloseFileStreamQuietly 负责）
                stream = null;
            }
        }
        catch (Exception ex)
        {
            // 只走到这里说明"已创建但尚未交接"——必须自己关掉，否则句柄泄漏
            if (stream != null)
            {
                try { stream.Close(); }
                catch { /* 关闭失败无可挽回，日志已由下面这行记录 */ }
            }
            // 记录并放弃本次初始化：fileStream 保持 null，采集线程会走"暂存到
            // wavDataBuffer"的分支，录音流程本身继续（只是这一段音频不会落盘）。
            Debug.LogError("[AudioRecorder] WAV 文件初始化失败，本次录音将不落盘: " + ex);
        }
    }

    /// <summary>
    /// 等 init 线程收尾（它会打开 WAV 流并发布到 fileStream）。失败路径上必须先等它
    /// 结束再关流，否则关的是一个还没被赋值的 null，真正的流仍会泄漏。
    /// </summary>
    private void WaitForInitThread()
    {
        Thread t = initThread;
        if (t == null || !t.IsAlive) return;
        try
        {
            // 有界等待：init 线程只做建目录 + 写 44 字节头，正常是毫秒级；
            // 极端情况下（网络盘/杀毒软件拦截）不拖死调用方
            t.Join(2000);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[AudioRecorder] 等待 WAV 初始化线程失败"
                + "（该次初始化的结果未知，录音可能不落盘）: " + ex.Message);
        }
        finally
        {
            initThread = null;
        }
    }

    private void CloseFileStreamQuietly()
    {
        lock (bufferLock)
        {
            if (fileStream == null) return;
            try
            {
                fileStream.Close();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AudioRecorder] 关闭 WAV 流失败（可能残留文件占用）: " + ex.Message);
            }
            fileStream = null;
        }
    }

    internal string StopRecording(bool End_microphone = false)
    {
        if (!isRecording)
        {
            return null;
        }
        isRecording = false;
        string audioHash = null;
        if (initThread != null && initThread.IsAlive)
        {
            initThread.Join(1000);
        }
        if (processingThread != null && processingThread.IsAlive)
        {
            processingThread.Join(1000);
        }
        try
        {
            lock (bufferLock)
            {
                if (fileStream != null)
                {
                    audioHash = FinalizeWavFile();
                }
            }
            Debug.Log(("[AudioRecorder] 录制停止，哈希: " + (audioHash ?? "无")));
        }
        catch (Exception ex)
        {
            Debug.Log(("停止录制出错: " + ex.Message));
        }
        finally
        {
            Cleanup(End_microphone);
        }
        return audioHash;
    }

    private string FinalizeWavFile()
    {
        if (fileStream == null)
        {
            return null;
        }
        // Length 取的是**逻辑长度**（FileStream 在 Write 时就已推进内部长度，
        // 不依赖缓冲区是否落盘），所以这里不是为了"让 Length 变准"。
        // 先 Flush 的理由是：回填头部要 seek 到文件头再写，若此时缓冲区里还压着
        // 未落盘的 PCM，后续任一步抛异常就会留下"长度字段没回填、数据也被截断"的
        // 文件；先落盘至少保证内容是完整的前缀。
        fileStream.Flush();
        long fileLength = fileStream.Length;
        long dataSizeLong = fileLength - 44;
        // RIFF 的两个长度字段是 32 位，读取端（WavLoader.ParseHeader）按有符号 int
        // 解析、负数直接判为"无效的块大小"。(int) 强转在超过 2GB 时会静默回绕成
        // 负数，等于把一个内容完好的文件写成损坏文件。这里显式夹住并报错：
        // 真正的 4GB+ 支持要换 RF64，不在本方法职责内，截断至少保证前 2GB 可读。
        if (dataSizeLong > int.MaxValue)
        {
            Debug.LogError($"[AudioRecorder] WAV 数据超过 32 位长度字段上限（{dataSizeLong} 字节），" +
                "头部按 2GB 截断，超出部分不会被加载器读取");
        }
        int dataSize = (int)Math.Min(Math.Max(0L, dataSizeLong), int.MaxValue);
        int riffSize = (int)Math.Min(Math.Max(0L, fileLength - 8), int.MaxValue);
        fileStream.Position = 4L;
        byte[] riffSizeBytes = BitConverter.GetBytes(riffSize);
        fileStream.Write(riffSizeBytes, 0, 4);
        fileStream.Position = 40L;
        byte[] dataSizeBytes = BitConverter.GetBytes(dataSize);
        fileStream.Write(dataSizeBytes, 0, 4);
        fileStream.Close();
        fileStream = null;
        lock (hashLock)
        {
            if (hashAlgorithm != null && !isHashFinalized)
            {
                hashAlgorithm.TransformFinalBlock(new byte[0], 0, 0);
                byte[] hash = hashAlgorithm.Hash;
                AudioHash = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                isHashFinalized = true;
                Debug.Log($"[AudioRecorder] WAV文件已保存: {currentFilePath}, 大小: {dataSize}字节, SHA256: {AudioHash}");
                return AudioHash;
            }
        }
        return "";
    }

    public static bool VerifyAudioFile(string filePath, string expectedHash)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError(("[AudioRecorder] 文件不存在: " + filePath));
            return false;
        }
        if (string.IsNullOrEmpty(expectedHash))
        {
            Debug.LogError("[AudioRecorder] 预期哈希值为空");
            return false;
        }
        try
        {
            using FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            using SHA256 sha = SHA256.Create();
            long length = fs.Length;
            const long headerSize = 44L;
            if (length <= headerSize)
            {
                Debug.LogError("[AudioRecorder] 文件太小，不包含音频数据");
                return false;
            }
            fs.Position = headerSize;
            byte[] buffer = new byte[65536];
            long totalBytes = 0L;
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                totalBytes += read;
            }
            sha.TransformFinalBlock(new byte[0], 0, 0);
            string actualHash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            bool matched = actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
            if (SpectreState.DebugMode)
            {
                Debug.Log(("[AudioRecorder] 文件验证: " + filePath + ", " + $"大小: {totalBytes}字节, " + "预期: " + expectedHash + ", 实际: " + actualHash + ", " + $"匹配: {matched}"));
            }
            return matched;
        }
        catch (Exception ex)
        {
            Debug.LogError(("[AudioRecorder] 验证文件时出错: " + ex.Message));
            return false;
        }
    }

    internal bool ValidateDevice(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            return false;
        }
        string[] devices = Microphone.devices;
        foreach (string device in devices)
        {
            if (device == deviceName)
            {
                return true;
            }
        }
        return false;
    }

    private void Cleanup(bool End_microphone = false)
    {
        isRecording = false;
        lock (hashLock)
        {
            if (hashAlgorithm != null)
            {
                if (!isHashFinalized)
                {
                    try
                    {
                        hashAlgorithm.TransformFinalBlock(new byte[0], 0, 0);
                    }
                    catch
                    {
                    }
                }
                hashAlgorithm.Dispose();
                hashAlgorithm = null;
            }
        }
        // 在 bufferLock 下关流：采集线程写盘（ProcessAudioChunk）也持这把锁。
        // 不加锁时它可能在 fileStream.CanWrite 检查通过之后、Write 之前被关掉，
        // 采集线程就撞 ObjectDisposedException。CloseFileStreamQuietly 内部已加锁并二次判空。
        CloseFileStreamQuietly();
        if (recordingClip != (UnityEngine.Object)null)
        {
            if (processingThread == null || !processingThread.IsAlive)
            {
                UnityEngine.Object.Destroy(recordingClip);
                recordingClip = null;
            }
        }
        if (End_microphone && !string.IsNullOrEmpty(microphoneDevice))
        {
            Microphone.End(microphoneDevice);
        }
        lock (bufferLock)
        {
            wavDataBuffer.Clear();
        }
        processingThread = null;
        initThread = null;
    }

    private byte[] CreateWAVHeader(int channels, int sampleRate, int bitDepth, int dataLength)
    {
        byte[] header = new byte[44];
        int byteRate = sampleRate * channels * (bitDepth / 8);
        int blockAlign = channels * (bitDepth / 8);
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BitConverter.GetBytes(36 + dataLength).CopyTo(header, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
        BitConverter.GetBytes(16).CopyTo(header, 16);
        BitConverter.GetBytes((short)1).CopyTo(header, 20);
        BitConverter.GetBytes((short)channels).CopyTo(header, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(header, 24);
        BitConverter.GetBytes(byteRate).CopyTo(header, 28);
        BitConverter.GetBytes((short)blockAlign).CopyTo(header, 32);
        BitConverter.GetBytes((short)bitDepth).CopyTo(header, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BitConverter.GetBytes(dataLength).CopyTo(header, 40);
        return header;
    }

    internal void startMicrophone(string deviceName = null)
    {
        if (!ValidateDevice(deviceName))
        {
            Debug.Log("device name illegal");
            return;
        }
        microphoneDevice = deviceName;
        if (!Microphone.IsRecording(deviceName))
        {
            recordingClip = Microphone.Start(deviceName, true, maxRecordingLength, recordingFrequency);
            Debug.Log(("Recording " + deviceName));
        }
        if (!Microphone.IsRecording(deviceName))
        {
            Debug.Log("FAIL");
        }
    }

    internal static void StopAllMicrophones()
    {
        if (Microphone.devices.Length == 0)
        {
            return;
        }
        string[] devices = Microphone.devices;
        foreach (string device in devices)
        {
            if (Microphone.IsRecording(device))
            {
                Microphone.End(device);
                Debug.Log(("[AudioRecorder] 已停止麦克风: " + device));
            }
        }
    }

    private void OnDestroy()
    {
        if (isRecording)
        {
            StopRecording(End_microphone: true);
        }
    }
}
