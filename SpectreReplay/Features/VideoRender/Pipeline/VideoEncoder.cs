using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SpectreReplay.Features.VideoRender;

// ffmpeg 管道编码器：主线程只入队帧数据，stdin 写入在后台线程完成；
// 帧队列有界形成反压 —— 编码跟不上时渲染自然等待，不影响成片时间轴。
internal sealed class VideoEncoder : IDisposable
{
    internal enum Quality { Balanced = 0, Fastest = 1, Sharpest = 2, CpuOnly = 3 }

    // 输出容器。webm 只认 VP8/VP9 视频 + Vorbis/Opus 音频，走 libvpx-vp9 重编码；
    // 其余容器沿用 H.264（NVENC 探测可用时）+ AAC。
    internal enum OutContainer { Mp4 = 0, Mkv = 1, WebM = 2, Mov = 3 }

    internal static string ExtensionOf(OutContainer c) => c switch
    {
        OutContainer.Mkv => ".mkv",
        OutContainer.WebM => ".webm",
        OutContainer.Mov => ".mov",
        _ => ".mp4",
    };

    private sealed class PendingFrame
    {
        public byte[] Data;
        public int Length;
        public int Repeat;
        public Action<byte[]> Recycle;
    }

    private const int ProbeRetryMs = 30_000;
    private static readonly object ProbeLock = new object();
    // 探测缓存策略（设置页每个 IMGUI 帧都会询问，冷却期挡住反复拉起探测进程）：
    // - PATH 定位 / PATH 探测：负结果（没找到/超时）冷却 ProbeRetryMs 后重试，
    //   用户中途安装 ffmpeg 无需重启游戏；正结果会话内有效
    // - NVENC：结果与 exePath 绑定，换编码器路径自动失效。负结果不设冷却重试——
    //   它意味着当前驱动没有硬件编码能力，会话内不会改变
    private static bool nvencWorks;
    private static string nvencProbedExe;
    private static bool pathWorks;
    private static int pathFailTick = int.MinValue;
    // File.Exists 定位缓存（调用方均在主线程）：具体路径是正结果直接复用；
    // "ffmpeg" 是 PATH 回退的负结果，进冷却期
    private static string locatedEncoder;
    private static int locateFailTick = int.MinValue;

    // 负缓存是否仍在冷却期（TickCount 差值按无符号比较，回绕安全）
    private static bool Cooling(int failTick)
        => failTick != int.MinValue && (uint)(Environment.TickCount - failTick) < (uint)ProbeRetryMs;

    private readonly string exePath;
    private readonly string rawVideoPath;
    private readonly string finalVideoPath;
    private readonly int width;
    private readonly int height;
    private readonly int fps;
    private readonly Quality quality;
    private readonly int qualityTier;
    private readonly bool flipRows;
    private readonly OutContainer container;

    private Process pipe;
    private BlockingCollection<PendingFrame> queue;
    private Thread pump;
    // **必须 volatile**：泵线程写、主线程在 Submit 里读。非 volatile 时主线程
    // 可能一直读到缓存里的 null，于是一个已经死掉的泵还会被继续 Submit
    // （虽然 pipe.HasExited 与队列 CompleteAdding 最终会兜住，但那已经是
    // 又等了一个 SubmitStallTimeoutMs=120s 之后的事）。
    private volatile Exception pumpFailure;
    // ffmpeg 的真实失败原因只出现在 stderr（未知编码器/参数不支持/文件被占用）。
    // 泵线程与主线程都可能读取，追加加锁、只保留尾部若干行。
    private StringBuilder pipeStderr;
    private readonly object stderrLock = new object();

    // 帧队列容量（Begin 时按 256 MiB 预算定）：调用方据此对齐回读缓冲池上限，
    // 否则编码满载时每帧回收的 30+ 个大数组只有 8 个进池、其余全丢 LOH
    internal int QueueCapacity { get; private set; }

    internal VideoEncoder(string exePath, string rawVideoPath, string finalVideoPath,
        int width, int height, int fps, Quality quality, int qualityTier, bool flipRows,
        OutContainer container = OutContainer.Mp4)
    {
        this.exePath = exePath;
        this.rawVideoPath = rawVideoPath;
        this.finalVideoPath = finalVideoPath;
        this.width = width;
        this.height = height;
        this.fps = Math.Max(1, fps);
        this.quality = quality;
        this.qualityTier = Math.Max(0, Math.Min(3, qualityTier));
        this.flipRows = flipRows;
        this.container = container;
    }

    // 依次探测模组目录、游戏根目录；找不到再交给系统 PATH（结果缓存，见 locatedEncoder）
    internal static string LocateEncoder()
    {
        if (locatedEncoder != null)
        {
            bool cachedNegative = locatedEncoder == "ffmpeg";
            if (!cachedNegative || Cooling(locateFailTick))
                return locatedEncoder;
        }
        try
        {
            string modDir = Main.mod.Path;
            string gameRoot = Path.GetFullPath(Path.Combine(modDir, "..", ".."));
            string[] candidates =
            {
                Path.Combine(modDir, "ffmpeg.exe"),
                Path.Combine(gameRoot, "ffmpeg.exe")
            };
            foreach (string candidate in candidates)
                if (File.Exists(candidate))
                {
                    locatedEncoder = candidate;
                    return locatedEncoder;
                }
        }
        catch (Exception ex)
        {
            Debug.Log("[SpectreReplay] encoder locate failed: " + ex.Message);
        }
        locatedEncoder = "ffmpeg";
        locateFailTick = Environment.TickCount;
        return locatedEncoder;
    }

    // PATH 上是否有可用的 ffmpeg（探测结果缓存）
    internal static bool PathProbeSucceeded()
    {
        lock (ProbeLock)
        {
            if (pathWorks)
                return true;
            if (Cooling(pathFailTick))
                return false;
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(info))
                {
                    // WaitForExit(timeout) 返回 false 表示超时未退出，此时读 ExitCode 会抛异常；
                    // 超时按失败计并进冷却期，稍后重试而不是整个会话判死（慢盘/杀软扫描场景）
                    bool exited = p != null && p.WaitForExit(5000);
                    if (!exited)
                    {
                        try { p?.Kill(); } catch { }
                    }
                    pathWorks = exited && p.ExitCode == 0;
                }
            }
            catch
            {
                pathWorks = false;
            }
            if (!pathWorks)
                pathFailTick = Environment.TickCount;
            return pathWorks;
        }
    }

    internal void Begin()
    {
        string args = "-y -f rawvideo -pixel_format rgba"
            + " -video_size " + width + "x" + height
            + " -framerate " + fps
            + " -i - -an -vf " + Quote(RowFilter())
            + " " + EncoderArguments()
            + " -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 "
            + Quote(rawVideoPath);
        pipe = Spawn(args, redirectInput: true);
        // 帧队列缓存上限约 256 MiB
        QueueCapacity = Math.Max(2, Math.Min(64, 268435456 / (width * height * 4)));
        queue = new BlockingCollection<PendingFrame>(QueueCapacity);
        pumpFailure = null;
        pump = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "SpectreReplay.VideoEncoderPipe"
        };
        pump.Start();
    }

    // 队列满时自旋等待；取消检查交给回调。
    // 另加卡死看门狗：ffmpeg 若停止读 stdin（磁盘满/编码器挂起），泵线程会堵在
    // 管道 Write 上，队列随之填满——此时若用户不点取消，Submit 会无限自旋，
    // 协程永远到不了带超时的 FinishVideo，整次渲染静默挂死（与本文件其余路径
    // 的"超时即失败上报"策略不一致）。连续入队失败超过阈值即判定编码器卡死。
    private const int SubmitStallTimeoutMs = 120_000;
    private const int SubmitRetryMs = 50;

    internal void Submit(byte[] data, int length, int repeat, Action<byte[]> recycle, Func<bool> canceled)
    {
        var frame = new PendingFrame
        {
            Data = data,
            Length = length,
            Repeat = Math.Max(1, repeat),
            Recycle = recycle
        };
        var waitedMs = 0;
        while (true)
        {
            if (canceled != null && canceled())
                throw new OperationCanceledException("canceled");
            if (pipe == null || pipe.HasExited)
                throw new InvalidOperationException("encoder process exited unexpectedly"
                    + StderrSuffix());
            if (pumpFailure != null)
                throw new InvalidOperationException("encoder pump failed" + StderrSuffix(), pumpFailure);
            if (queue.TryAdd(frame, SubmitRetryMs))
                return;
            waitedMs += SubmitRetryMs;
            if (waitedMs >= SubmitStallTimeoutMs)
                throw new TimeoutException("encoder stalled: frame queue full for "
                    + (SubmitStallTimeoutMs / 1000) + "s (ffmpeg stopped consuming stdin?)");
        }
    }

    // 关闭管道收尾：等待写线程排空并让 ffmpeg 正常退出
    internal void FinishVideo()
    {
        if (pipe == null)
            return;
        queue.CompleteAdding();
        // 泵线程排空队列；若 ffmpeg 停止读 stdin，泵会堵在满管道的 Write 上，
        // 无限 Join 会把主线程挂死——限时轮询逃生（见 JoinPump）
        if (!JoinPump(TimeSpan.FromMinutes(10)))
        {
            try { pipe.Kill(); } catch { }
            try { pump.Join(3000); } catch { }
            throw new InvalidOperationException("video encode stalled while draining (timeout)");
        }
        if (pumpFailure != null)
            throw new InvalidOperationException("encoder pump failed" + StderrSuffix(), pumpFailure);
        pipe.StandardInput.Close();
        // 收尾只剩尾部 GOP flush + trailer，正常秒级完成；10 分钟兜底防 ffmpeg 卡死
        // （磁盘满/编码器挂起）把主线程永久挂死，超时 Kill 走既有失败上报链路
        if (!pipe.WaitForExit(600_000))
        {
            try { pipe.Kill(); } catch { }
            try { pipe.WaitForExit(3000); } catch { }
            throw new InvalidOperationException("video encode stalled at finalize (timeout)");
        }
        if (pipe.ExitCode != 0)
            throw new InvalidOperationException("video encode failed, exit code " + pipe.ExitCode
                + StderrSuffix());
        pipe.Dispose();
        pipe = null;
        queue.Dispose();
        queue = null;
        pump = null;
    }

    // 限时等待泵线程排空退出：ffmpeg 已死时管道断开、阻塞中的 Write 随即抛错，
    // 泵必然很快收场（再宽限 3 秒）；活着但停止消费（锁文件/驱动挂起等）只能
    // 等限时到头，由调用方 Kill 解除泵的阻塞再走失败链路
    private bool JoinPump(TimeSpan limit)
    {
        DateTime deadline = DateTime.UtcNow + limit;
        while (!pump.Join(1000))
        {
            if (pipe.HasExited)
                return pump.Join(3000);
            if (DateTime.UtcNow >= deadline)
                return false;
        }
        return true;
    }

    // 视频轨与混音轨合成最终文件。webm 只能 Opus/Vorbis 音频（强制 48k，Opus 不收其他采样率）。
    //
    // **不再带 -movflags +faststart。** 那个旗标要求 moov atom 前置，而编码时它被写在
    // 文件末尾——ffmpeg 只能把整个成片重写一遍搬过去。对本地文件它毫无收益：
    // faststart 是为「边下边播」的 HTTP 场景服务的，播放器从本地磁盘读不需要它。
    // 代价却是实打实的：几百 MB 的成片多一趟全量读写，而合成本身已经要读一遍写一遍了。
    internal void MuxWith(string wavPath)
    {
        if (!File.Exists(wavPath))
            throw new InvalidOperationException("mixdown file missing: " + wavPath);
        bool webm = container == OutContainer.WebM;
        string audio = webm ? "-c:a libopus -b:a 192k -ar 48000" : "-c:a aac -b:a 320k";
        // **-shortest**：视频与音频长度由两条独立采集链决定，正常只差几十毫秒，
        // 但音频采集重启/丢帧后可能明显短于视频。没有它时 ffmpeg 会把短的一轨
        // 补静音/黑帧到另一轨长度，成片尾部出现一段无声空转；有它则按短轨收尾。
        string args = "-y -i " + Quote(rawVideoPath) + " -i " + Quote(wavPath)
            + " -map 0:v:0 -map 1:a:0 -c:v copy " + audio + " -ac 2 -shortest"
            + " " + Quote(finalVideoPath);
        RunAndWait(exePath, args);
    }

    public void Dispose()
    {
        if (pipe != null)
        {
            try
            {
                if (!pipe.HasExited)
                {
                    // Kill 异步：等它真正退出，否则临时文件仍被占用
                    pipe.Kill();
                    pipe.WaitForExit(3000);
                }
            }
            catch { }
            pipe.Dispose();
            pipe = null;
        }
        if (queue != null)
        {
            if (!queue.IsAddingCompleted) queue.CompleteAdding();
            queue.Dispose();
            queue = null;
        }
    }

    private void PumpLoop()
    {
        try
        {
            foreach (PendingFrame frame in queue.GetConsumingEnumerable())
            {
                try
                {
                    for (int i = 0; i < frame.Repeat; i++)
                        pipe.StandardInput.BaseStream.Write(frame.Data, 0, frame.Length);
                }
                finally
                {
                    frame.Recycle?.Invoke(frame.Data);
                }
            }
        }
        catch (Exception ex)
        {
            pumpFailure = ex;
        }
    }

    // 相机纹理回读的行序与视频相反时补一次翻转；pad 把奇数宽高补偶数（yuv420p 要求）；
    // RGB→YUV 显式走 BT.709 矩阵——swscale 对无标记 RGB 默认 BT.601，HD 播放器按 709 解读会偏色
    private string RowFilter()
    {
        const string pad = "pad=ceil(iw/2)*2:ceil(ih/2)*2";
        const string color = ",scale=out_color_matrix=bt709,format=yuv420p";
        return (flipRows ? "vflip," : "") + pad + color;
    }

    /// <summary>本次渲染会用到的编解码器。**唯一判定来源**：
    /// EncoderArguments 拼参数与 DescribeEncoder 给 UI 显示都从这里取，
    /// 两边各写一份判定的话，将来加编码器只改一处会让 UI 显示出错的档位。</summary>
    private enum Codec { Vp9, Nvenc, X264 }

    private static Codec PickCodec(Quality quality, OutContainer container)
    {
        // webm 容器只认 VP8/VP9 + Vorbis/Opus：NVENC 无 VP9 编码器，固定走 libvpx-vp9。
        if (container == OutContainer.WebM) return Codec.Vp9;
        if (quality != Quality.CpuOnly && NvencWorksFor(LocateEncoder())) return Codec.Nvenc;
        return Codec.X264;
    }

    internal static string DescribeEncoder(Quality quality, OutContainer container)
    {
        switch (PickCodec(quality, container))
        {
            case Codec.Vp9: return "libvpx-vp9 (CPU)";
            case Codec.Nvenc: return "h264_nvenc (GPU)";
            default: return "libx264 (CPU)";
        }
    }

    private string EncoderArguments()
    {
        int gop = Math.Max(1, fps * 2);
        // cpu-used 档位映射画质档（越大越快越糙）；-row-mt 多线程编码，不开会慢数倍。
        // 直出有意走码率模式而非转换器的 crf 质量模式：逐帧喂流的场景下
        // 码率三件套让体积/耗时可控可预估，且随画质档与帧率同比缩放
        switch (PickCodec(quality, container))
        {
            case Codec.Vp9:
                {
                    string cpuUsed = quality == Quality.Fastest ? "5" : quality == Quality.Sharpest ? "2" : "4";
                    return "-c:v libvpx-vp9 -deadline good -cpu-used " + cpuUsed + " -row-mt 1 "
                        + RateControl(false) + " -g " + gop;
                }
            case Codec.Nvenc:
                {
                    string nvPreset = quality == Quality.Fastest ? "p1" : quality == Quality.Sharpest ? "p6" : "p4";
                    string tune = quality == Quality.Fastest ? "ll" : "hq";
                    return "-c:v h264_nvenc -preset " + nvPreset + " -tune " + tune
                        + " " + RateControl(true) + " -g " + gop;
                }
            default:
                {
                    string x264Preset = quality == Quality.Fastest ? "ultrafast" : quality == Quality.Sharpest ? "fast" : "veryfast";
                    return "-c:v libx264 -preset " + Quote(x264Preset) + " " + RateControl(false) + " -g " + gop;
                }
        }
    }

    // 码率按像素量估算（1080p60 标准档约 20 Mbps），画质档位乘系数（低0.5/标准1/高1.6/极致2.4），
    // 低帧率打七五折；极致档上限放宽到 160M
    private string RateControl(bool nvenc)
    {
        double megapixels = width * (double)height / 1000000.0;
        double scale = qualityTier == 0 ? 0.5 : qualityTier == 2 ? 1.6 : qualityTier == 3 ? 2.4 : 1.0;
        int target = (int)Math.Round(megapixels * 10.0 * scale);
        if (fps <= 30) target = target * 3 / 4;
        else if (fps > 60) target = target * fps / 60; // 高帧率码率同比放大，保持单帧画质
        int floor = qualityTier == 0 ? 4 : 8;
        int cap = qualityTier == 3 ? 160 : 100;
        target = Math.Max(floor, Math.Min(cap, target));
        int ceiling = target * 3 / 2;
        int bucket = target * 2;
        if (nvenc)
        {
            int cq = qualityTier == 0 ? 23 : qualityTier == 2 ? 18 : qualityTier == 3 ? 16 : 20;
            return "-rc vbr -cq " + cq + " -b:v " + target + "M -maxrate " + ceiling + "M -bufsize " + bucket + "M";
        }
        return "-b:v " + target + "M -maxrate " + ceiling + "M -bufsize " + bucket + "M";
    }

    /// <summary>按 ffmpeg 路径探测 NVENC。静态版供 UI 侧 DescribeEncoder 复用同一份缓存，
    /// 避免为了显示一行提示就起第二个探测（探测本身要起进程、等最多 5 秒）。</summary>
    private static bool NvencWorksFor(string exe)
    {
        lock (ProbeLock)
        {
            if (nvencProbedExe == exe)
                return nvencWorks;
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-hide_banner -loglevel error -y -f lavfi"
                        + " -i color=size=256x256:rate=1:duration=0.1"
                        + " -c:v h264_nvenc -frames:v 1 -f null -",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (Process probe = Process.Start(info))
                {
                    if (probe == null)
                        nvencWorks = false;
                    else if (!probe.WaitForExit(5000))
                    {
                        try { probe.Kill(); } catch { }
                        nvencWorks = false;
                    }
                    else
                        nvencWorks = probe.ExitCode == 0;
                }
            }
            catch
            {
                nvencWorks = false;
            }
            nvencProbedExe = exe;
            return nvencWorks;
        }
    }

    // 跑一条 ffmpeg 命令并等待退出，非零/启动失败抛异常（stderr 附在消息里）。
    // 静态供 VideoConverter 复用同一套错误处理
    // ffmpeg 调用的超时上限。转码是离线操作（输入已在本地、无网络依赖），
    // 正常耗时与文件大小成正比；30 分钟对应数 GB 的 4K VP9 重编码仍有极大余量，
    // 超时基本只可能是"真卡住了"（磁盘满/解码器死锁/IO 阻塞）
    private const int RunAndWaitTimeoutMs = 30 * 60 * 1000;

    internal static void RunAndWait(string exePath, string arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "-hide_banner -loglevel warning " + arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        StringBuilder stderr = new StringBuilder();
        using (Process mux = Process.Start(info))
        {
            if (mux == null)
                throw new InvalidOperationException("failed to start encoder");
            mux.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            mux.OutputDataReceived += (_, e) => { };
            mux.BeginErrorReadLine();
            mux.BeginOutputReadLine();
            // 必须带超时：WaitForExit() 无参会无限等待。ffmpeg 卡死（磁盘满/解码器
            // 死锁/IO 阻塞）时工作线程会永久阻塞在这里，ConvertState.Running 永远
            // 不会置 false —— UI 停在"转换中"且 VideoConverter 没有取消通道，
            // 用户只能重启游戏。渲染管线的同类调用都带超时，这里补齐。
            if (!mux.WaitForExit(RunAndWaitTimeoutMs))
            {
                try { mux.Kill(); } catch { }
                try { mux.WaitForExit(5000); } catch { }
                throw new TimeoutException("ffmpeg timed out after "
                    + (RunAndWaitTimeoutMs / 60000) + " min (mux/convert)");
            }
            if (mux.ExitCode != 0)
                throw new InvalidOperationException("mux failed, exit code " + mux.ExitCode + ": " + stderr);
        }
    }

    private Process Spawn(string arguments, bool redirectInput)
    {
        var info = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "-hide_banner -loglevel warning " + arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            // **必须重定向 stderr。** 原实现只重定向 stdin，管道里 ffmpeg 因
            // 未知编码器/参数不支持/文件被占用而立即退出时，用户只能看到
            // "encoder process exited unexpectedly"，真实原因全被丢掉——
            // 与 RunAndWait（mux/转换路径）捕获 stderr 的做法不一致。
            RedirectStandardError = true
        };
        Process started = Process.Start(info);
        if (started == null)
            throw new InvalidOperationException("failed to start encoder");
        pipeStderr = new StringBuilder();
        started.ErrorDataReceived += (_, e) => AppendStderr(e.Data);
        started.BeginErrorReadLine();
        return started;
    }

    // 只留尾部：ffmpeg 的失败原因通常在最后几行，而正常运行时 warning 也可能很多。
    private const int StderrCapChars = 4096;

    private void AppendStderr(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (stderrLock)
        {
            if (pipeStderr == null) return;
            pipeStderr.AppendLine(line);
            if (pipeStderr.Length > StderrCapChars)
                pipeStderr.Remove(0, pipeStderr.Length - StderrCapChars);
        }
    }

    // 供异常消息拼接：读当前累计的 stderr（可能为空）
    private string StderrTail()
    {
        lock (stderrLock)
            return pipeStderr == null ? "" : pipeStderr.ToString().Trim();
    }

    // 异常消息后缀：stderr 为空时不加任何东西，保持原消息干净
    private string StderrSuffix()
    {
        string tail = StderrTail();
        return tail.Length == 0 ? "" : "\nffmpeg: " + tail;
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"") + "\"";
}
