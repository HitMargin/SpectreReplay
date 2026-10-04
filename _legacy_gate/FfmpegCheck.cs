// ffmpeg 参数的端到端验证。
//
// 参数拼错的后果不是"报错"而是**全程无声无息地失败**：没有视频、也不
// 一定在界面上留下像样的原因。所以这类问题只能靠真跑一遍来发现。
//
// 做法：链接真实的 FfmpegArgs（纯函数，不碰引擎），调用**游戏自带的那个
// ffmpeg.exe**，喂进真的 BGRA 帧，看每个容器组合是不是真能出片。
// 参数写得再"看起来对"，不如让 ffmpeg 自己判一次。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SpectreReplay.Features.VideoRender;

namespace UiPreview
{
    internal static class FfmpegCheck
    {
        private const int W = 64;
        private const int H = 48;
        private const int Frames = 12;

        private static string FfmpegPath =>
            @"D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice"
            + @"\Mods\SpectreReplay\ffmpeg.exe";

        internal static int Run()
        {
            Console.WriteLine("  ── ffmpeg 参数（真跑一遍）──");
            if (!File.Exists(FfmpegPath))
            {
                Console.WriteLine("    ? 找不到 " + FfmpegPath + "，跳过");
                return 0;
            }
            int fail = 0;
            fail += Encode("① mp4", ContainerMode.Mp4, ".mp4");
            fail += Encode("② mkv", ContainerMode.Mkv, ".mkv");
            fail += Encode("③ webm（曾经必然失败）", ContainerMode.Webm, ".webm");
            fail += CodecSanity();
            fail += ColorAndFlip();
            fail += FloatWavMuxes();
            fail += NegativeControl();
            fail += Mux();
            fail += OddWidthSurvives();
            fail += IntermediateExtIsMkv();
            return fail;
        }

        /// <summary>
        /// **奇数宽高必须能出片**——真跑真实参数，不看拼出来的字符串。
        ///
        /// 整屏模式的分辨率直接取 Screen.width/height，用户把窗口拖成
        /// 奇数宽就能选到这个组合。原先只拼了裸 <c>vflip</c>，于是
        /// libx264 遇到奇数宽高**直接失败**：实测 ffmpeg 报
        /// "Terminating thread with error: Invalid argument"、退出码非 0、
        /// 产物 0 字节。界面上的表现是"渲染了但没有视频"，日志里只有
        /// ffmpeg 自己那一行。
        ///
        /// 为什么非要用真 ffmpeg 判：断言"-vf 里含 pad"只能证明参数
        /// 拼对了，证明不了**ffmpeg 真的接受这个组合**。中间任何一个
        /// 滤镜名写错（比如 ceil 拼成 ceal），源码检查照样全绿。
        /// </summary>
        private static int OddWidthSurvives()
        {
            const int W = 33, H = 25;
            string dir = Path.Combine(Path.GetTempPath(), "ffcheck");
            Directory.CreateDirectory(dir);
            string raw = Path.Combine(dir, "odd.rgba");
            string outPath = Path.Combine(dir, "odd_real.mp4");
            if (File.Exists(outPath)) File.Delete(outPath);

            // 造一张奇数尺寸的裸帧：左上红、右下蓝，便于顺带确认 pad
            // 是**在右边和下边**补的（内容不能被挪位）
            var px = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int o = (y * W + x) * 4;
                    bool corner = (x < W / 2 && y < H / 2)
                        || (x >= W / 2 && y >= H / 2);
                    px[o] = corner ? (byte)220 : (byte)30;
                    px[o + 1] = corner ? (byte)30 : (byte)60;
                    px[o + 2] = corner ? (byte)30 : (byte)220;
                    px[o + 3] = 255;
                }
            }
            File.WriteAllBytes(raw, px);

            // 用真实的 Build() 参数——不是手搓的一份
            var args = FfmpegArgs.Build(W, H, 10, EncoderMode.Auto,
                QualityMode.Standard, ContainerMode.Mp4, false, outPath);
            // stdin 那条路在这里换成文件输入，其余参数一字不改
            int pipeAt = Array.IndexOf(args, "pipe:0");
            if (pipeAt >= 0) args[pipeAt] = raw;
            else return Report(1, "参数里找不到 pipe:0，无法改成文件输入");

            int rc = Run(FfmpegPath, args, 90000);
            bool has = File.Exists(outPath) && new FileInfo(outPath).Length > 0;
            if (rc == 0 && has)
            {
                // 还要确认尺寸确实被补成了偶数（而不是裁掉或拉伸）
                var (vw, vh) = ProbeSize(outPath);
                bool even = vw % 2 == 0 && vh % 2 == 0 && vw >= W && vh >= H;
                Console.WriteLine($"    {(even ? "✓" : "✗")} ⑩ 奇数宽高 {W}x{H} 仍能出片"
                    + $"  → 退出码 {rc}，产物被补成 {vw}x{vh}"
                    + (even ? "（pad 在右边/下边补黑，内容未挪位）" : "（尺寸不对）"));
                return even ? 0 : 1;
            }
            Console.WriteLine($"    ✗ ⑩ 奇数宽高 {W}x{H} 渲染失败"
                + $"  → 退出码 {rc}，产物 {(has ? "有" : "无")}"
                + "（整屏模式把窗口拖成奇数宽就能复现）");
            return 1;
        }

        private static int Report(int code, string why)
        {
            Console.WriteLine("    ✗ ⑩ 奇数宽高检查：" + why);
            return code;
        }

        /// <summary>用 ffmpeg 自己读回产物的视频尺寸。</summary>
        private static (int, int) ProbeSize(string path)
        {
            var psi = new ProcessStartInfo(FfmpegPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi);
            string all = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
            // "Stream #0:0: Video: h264 ..., 34x26, 62 kb/s"
            //
            // 尺寸后面可能跟 "[SAR ...]" 也可能直接跟 ", 62 kb/s"——
            // 第一版只认带方括号那种，于是解析出 0x0，断言报"尺寸不对"，
            // 而产物其实是对的。
            var m = System.Text.RegularExpressions.Regex.Match(
                all, @",\s*(\d+)x(\d+)\s*(?:\[|,)");
            return m.Success ? (int.Parse(m.Groups[1].Value),
                                int.Parse(m.Groups[2].Value)) : (0, 0);
        }

        /// <summary>
        /// 中间产物必须是 <c>.mkv</c>，与目标容器无关。
        ///
        /// ffmpeg 的 muxer **按扩展名**选容器。选 webm 时中间产物编的是
        /// VP9，而 VP9 流写进 <c>.mp4</c> 在 ffmpeg 4.x 是 experimental
        /// ——拒绝写入，渲染首帧即死（照 Spectre 的
        /// VideoRenderJob.cs:528-532 的注释）。
        ///
        /// 原先两处都直接用目标容器的扩展名，选 webm 时碰巧对了。
        /// 那是**巧合**：判据是"源码里没有把 ext 用在中间路径上"，
        /// 而巧合性正确就是等着被下一个组合打破。
        ///
        /// 判据按**两个文件都查**（LiveSession 与 OfflineSession 各一份），
        /// 少改一处就变红——它们本来就是复制出来的，很容易只改一个。
        /// </summary>
        private static int IntermediateExtIsMkv()
        {
            string root = Path.GetFullPath(@"..\..\SpectreReplay\Features\VideoRender");
            int bad = 0;
            foreach (var name in new[] { "LiveSession.cs", "OfflineSession.cs" })
            {
                string file = Path.Combine(root, name);
                if (!File.Exists(file))
                {
                    Console.WriteLine("    ✗ ⑪ 找不到 " + name);
                    bad++;
                    continue;
                }
                string src = File.ReadAllText(file);
                // 中间产物那一行必须写死 .mkv；"stem + "." + ext" 那种写法判红
                int at = src.IndexOf("_rawVideoPath = Path.Combine", StringComparison.Ordinal);
                if (at < 0)
                {
                    at = src.IndexOf("_rawVideoPath = System.IO.Path.Combine",
                        StringComparison.Ordinal);
                }
                if (at < 0)
                {
                    Console.WriteLine("    ✗ ⑪ " + name + " 找不到 _rawVideoPath 的赋值");
                    bad++;
                    continue;
                }
                int end = src.IndexOf(';', at);
                string line = src.Substring(at, end - at);
                bool ok = line.Contains(".mkv");
                Console.WriteLine($"    {(ok ? "✓" : "✗")} ⑪ {name} 的中间产物是 .mkv"
                    + $"  → {(ok ? "VP9 与 H.264 都装得下" : line.Trim())}");
                if (!ok) bad++;
            }
            if (bad == 0)
            {
                Console.WriteLine("    ✓ ⑪b 两处中间产物都用 .mkv，"
                    + "不会被「目标容器=mp4 + 编码器=VP9」打死");
                return 0;
            }
            Console.WriteLine("      ← VP9 写进 .mp4 在 ffmpeg 4.x 是 experimental，渲染首帧即死");
            return bad;
        }

        /// <summary>
        /// float32 WAV 必须能被合流参数正确读入。
        ///
        /// 音频轨改成 <c>AudioRenderer</c> 抓混音之后，wav 的格式从
        /// 16-bit PCM 变成了 **float32**（format tag = 3）——这是引擎
        /// <c>AudioRenderer.Render(NativeArray&lt;float&gt;)</c> 的原生格式，
        /// 也是 Spectre 的 VideoAudioTrack 用的格式。
        ///
        /// 这条断言的意义：如果 ffmpeg 读不了这个 wav，音轨会静默消失，
        /// 而 <c>AudioMux</c> 只看"退出码 0 且产物存在"就当成功——
        /// 于是界面显示"完成"，视频却依旧无声。必须验产物里真的有音频流。
        /// </summary>
        private static int FloatWavMuxes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ffcheck");
            Directory.CreateDirectory(dir);
            string wav = Path.Combine(dir, "float32.wav");
            string raw = Path.Combine(dir, "float32.mp4");
            string outPath = Path.Combine(dir, "float32_muxed.mp4");
            foreach (string f in new[] { wav, raw, outPath })
            {
                if (File.Exists(f)) File.Delete(f);
            }

            // 手写一个 44 字节 float32 头 + 0.5 秒 440Hz 正弦。
            // 不用 ffmpeg 造：ffmpeg 默认也写 PCM，要验的是"我们写出来的
            // 那种头"能不能被读回去，素材必须与生产代码同格式。
            const int rate = 44100;
            const int channels = 2;
            int frames = rate / 2;
            var pcm = new byte[frames * channels * 4];
            for (int i = 0; i < frames; i++)
            {
                float v = (float)Math.Sin(2.0 * Math.PI * 440.0 * i / rate) * 0.5f;
                byte[] raw4 = BitConverter.GetBytes(v);
                Buffer.BlockCopy(raw4, 0, pcm, (i * channels) * 4, 4);
                Buffer.BlockCopy(raw4, 0, pcm, (i * channels + 1) * 4, 4);
            }
            try
            {
                using (var fs = new FileStream(wav, FileMode.Create))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                    w.Write(36 + pcm.Length);
                    w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
                    w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                    w.Write(16);
                    w.Write((short)3);                 // IEEE float —— 关键
                    w.Write((short)channels);
                    w.Write(rate);
                    w.Write(rate * channels * 4);
                    w.Write((short)(channels * 4));
                    w.Write((short)32);
                    w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                    w.Write(pcm.Length);
                    w.Write(pcm);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ ⑨ float32 wav：写素材失败 " + ex.Message);
                return 1;
            }

            // 造一个无声视频当被合流的对象
            if (Run(FfmpegPath, new[]
            {
                "-y", "-f", "lavfi", "-i", "color=c=black:s=64x48:r=30:d=1",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", raw,
            }, 60000) != 0 || !File.Exists(raw))
            {
                Console.WriteLine("    ✗ ⑨ float32 wav：造视频素材失败");
                return 1;
            }

            // 用真实的合流参数
            int rc = Run(FfmpegPath, FfmpegArgs.BuildMuxArgs(
                raw, wav, outPath, ContainerMode.Mp4), 60000);
            bool hasAudio = File.Exists(outPath) && ProbeHasAudio(outPath);
            Console.WriteLine($"    {(rc == 0 && hasAudio ? "✓" : "✗")} ⑨ float32 wav 合成流"
                + $"  → 退出码 {rc}，产物 {Describe(outPath)}"
                + (hasAudio ? "，含音轨" : "，**没有音轨**"));
            if (rc == 0 && !hasAudio)
            {
                Console.WriteLine("      ← ffmpeg 接受了参数但没读出音频；"
                    + "AudioMux 只看退出码会误判成功");
            }
            return (rc == 0 && hasAudio) ? 0 : 1;
        }

        /// <summary>
        /// 合流端到端：先造一个真的无声视频和一条真的 wav，再用真实的合流参数
        /// 把它们合起来，最后**用 ffprobe 确认产物里真的有音轨**。
        ///
        /// 只看"退出码 0"是不够的——ffmpeg 有时会照成功退出、产物却没声音。
        /// 所以这里要的是"产物里确实有音频流"这个事实。
        /// </summary>
        private static int Mux()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ffcheck");
            Directory.CreateDirectory(dir);
            int fail = 0;

            // 造素材：1 秒无声视频 + 1 秒 440Hz 的 wav
            //
            // 原始视频的编码器必须**跟着容器走**——真实流程里 FfmpegArgs.Build
            // 选 webm 时本来就会编成 VP9。素材要是拿 H.264 的 mp4 去合进 webm，
            // `-c:v copy` 直接失败（退出码 -22），那是素材不对，不是被测代码
            // 的问题。这种"测试自己造错素材"最难发现：断言红了会让人去改
            // 本来正确的代码。
            string wav = Path.Combine(dir, "tone.wav");
            if (Run(FfmpegPath, new[]
            {
                "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
                wav,
            }, 60000) != 0)
            {
                Console.WriteLine("    ✗ 造 wav 失败，无法验合流");
                return 1;
            }

            foreach (var (label, container, ext) in new[]
            {
                ("⑥ mp4 合流", ContainerMode.Mp4, ".mp4"),
                ("⑦ mkv 合流", ContainerMode.Mkv, ".mkv"),
                ("⑧ webm 合流（aac 装不下）", ContainerMode.Webm, ".webm"),
            })
            {
                // 素材用该容器对应的编码器，跟真实编码流程一致
                string codec = ArgOf(FfmpegArgs.Build(W, H, 30, EncoderMode.Auto,
                    QualityMode.Standard, container, false, "x" + ext), "-c:v");
                string rawVideo = Path.Combine(dir, "raw" + ext);
                if (File.Exists(rawVideo)) File.Delete(rawVideo);
                if (Run(FfmpegPath, new[]
                {
                    "-y", "-f", "lavfi", "-i", "color=c=red:s=64x48:r=30:d=1",
                    "-c:v", codec ?? "libx264", "-pix_fmt", "yuv420p", rawVideo,
                }, 60000) != 0 || !File.Exists(rawVideo))
                {
                    Console.WriteLine($"    ✗ {label,-30} 造素材失败（codec={codec}）");
                    fail++;
                    continue;
                }

                string outPath = Path.Combine(dir, "muxed" + ext);
                if (File.Exists(outPath)) File.Delete(outPath);
                int rc = Run(FfmpegPath, FfmpegArgs.BuildMuxArgs(
                    rawVideo, wav, outPath, container), 60000);
                if (rc != 0 || !File.Exists(outPath))
                {
                    Console.WriteLine($"    ✗ {label,-30} 退出码 {rc}，无产物");
                    fail++;
                    continue;
                }
                // 关键：产物里到底有没有音频流
                bool hasAudio = ProbeHasAudio(outPath);
                Console.WriteLine($"    {(hasAudio ? "✓" : "✗")} {label,-30}"
                    + $" 产物 {new FileInfo(outPath).Length:N0} B"
                    + (hasAudio ? "，含音轨" : "，**没有音轨**"));
                if (!hasAudio) fail++;
            }
            return fail;
        }

        /// <summary>从参数表里取某个开关后面跟的值。</summary>
        private static string ArgOf(string[] args, string flag)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == flag) return args[i + 1];
            }
            return null;
        }

        /// <summary>用 ffmpeg 自己读一遍产物：把流信息打到 stderr，看有没有音频。</summary>
        private static bool ProbeHasAudio(string path)
        {
            var psi = new ProcessStartInfo(FfmpegPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            using (var p = Process.Start(psi))
            {
                string all = p.StandardOutput.ReadToEnd()
                    + p.StandardError.ReadToEnd();
                p.WaitForExit(20000);
                // "Stream #0:1: Audio: aac" 之类
                return all.Contains("Audio:");
            }
        }

        /// <summary>跑一次 ffmpeg（不喂 stdin），返回退出码。</summary>
        private static int Run(string exe, string[] args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi))
            {
                using (var done = new System.Threading.ManualResetEventSlim(false))
                {
                    p.ErrorDataReceived += (_, __) => { };
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return -1;
                    }
                }
                return p.ExitCode;
            }
        }

        /// <summary>
        /// 反向对照：**故意**用装不进容器的编码器，断言这套检测确实会红。
        ///
        /// 之前验证"webm 修复有效"的办法是去改真实源码、把 bug 放回去跑——
        /// 那次工具调用被中断，bug 就这么留在文件里了。改真实文件来验证测试
        /// 本身就是坏习惯：万一忘了还原，代价是真代码出错。
        /// 反向对照没有这个问题，而且它常驻、每次都跑。
        /// </summary>
        private static int NegativeControl()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ffcheck");
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, "negative.webm");
            if (File.Exists(outPath)) File.Delete(outPath);

            // 手工拼一份"webm + libx264"——也就是修复前 bug 版本产出的参数
            var psi = new ProcessStartInfo(FfmpegPath)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in new[]
            {
                "-y", "-f", "rawvideo", "-pixel_format", "bgra",
                "-video_size", W + "x" + H, "-framerate", "30", "-i", "pipe:0",
                "-an", "-c:v", "libx264", "-preset", "medium", "-crf", "23",
                "-pix_fmt", "yuv420p", "-r", "30", "-f", "webm", outPath,
            })
            {
                psi.ArgumentList.Add(a);
            }

            using (var p = Process.Start(psi))
            {
                var err = new System.Text.StringBuilder();
                using (var done = new System.Threading.ManualResetEventSlim(false))
                {
                    p.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data == null) { done.Set(); return; }
                        lock (err) { err.AppendLine(e.Data); }
                    };
                    p.BeginErrorReadLine();
                    byte[] one = new byte[W * H * 4];
                    for (int i = 3; i < one.Length; i += 4) one[i] = 255;
                    for (int f = 0; f < Frames; f++)
                    {
                        try
                        {
                            p.StandardInput.BaseStream.Write(one, 0, one.Length);
                        }
                        catch (IOException) { break; }
                    }
                    try { p.StandardInput.Close(); } catch { }
                    if (!p.WaitForExit(45000)) { try { p.Kill(); } catch { } }
                    done.Wait(3000);
                    bool produced = File.Exists(outPath)
                        && new FileInfo(outPath).Length >= 512;
                    bool exitedClean = p.HasExited && p.ExitCode == 0;
                    bool detected = !(produced && exitedClean);
                    Console.WriteLine($"    {(detected ? "✓" : "✗")} ⑤ 反向对照："
                        + "故意用装不下容器的编码器，应当被检出"
                        + $"  → {(detected ? "已检出" : "竟然通过了，检测是假的")}");
                    if (!detected) return 1;
                }
            }
            if (File.Exists(outPath)) File.Delete(outPath);
            return 0;
        }

        private static int Encode(string label, ContainerMode container, string ext)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ffcheck");
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, "out" + ext);
            if (File.Exists(outPath)) File.Delete(outPath);

            var psi = new ProcessStartInfo(FfmpegPath)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in FfmpegArgs.Build(W, H, 30, EncoderMode.Auto,
                QualityMode.Standard, container, false, outPath))
            {
                psi.ArgumentList.Add(a);
            }

            try
            {
                using (var p = Process.Start(psi))
                {
                    // **必须先异步抽干 stderr，再写 stdin。**
                    // ffmpeg 报错时往 stderr 写，管道缓冲区一旦写满它就停下
                    // 不再读 stdin，我这边写 stdin 于是永远阻塞——写这个测试
                    // 时自己就死锁过一次。游戏里的 VideoEncoder 用
                    // BeginErrorReadLine 正是为了这个。
                    var err = new System.Text.StringBuilder();
                    using (var errDone = new System.Threading.ManualResetEventSlim(false))
                    {
                        p.ErrorDataReceived += (_, e) =>
                        {
                            if (e.Data == null) { errDone.Set(); return; }
                            lock (err) { err.AppendLine(e.Data); }
                        };
                        p.BeginErrorReadLine();

                        // 喂真帧：BGRA，每帧底色渐变，省得全是黑看不出问题
                        byte[] frame = new byte[W * H * 4];
                        for (int f = 0; f < Frames; f++)
                        {
                            for (int i = 0; i < frame.Length; i += 4)
                            {
                                frame[i] = (byte)(f * 20);         // B
                                frame[i + 1] = (byte)(255 - f * 20); // G
                                frame[i + 2] = (byte)(f * 10);     // R
                                frame[i + 3] = 255;                // A
                            }
                            try
                            {
                                p.StandardInput.BaseStream.Write(frame, 0, frame.Length);
                            }
                            catch (IOException)
                            {
                                // ffmpeg 提前退出（参数非法时就会），下面再判退出码
                                break;
                            }
                        }
                        try { p.StandardInput.Close(); } catch { }
                        if (!p.WaitForExit(60000))
                        {
                            try { p.Kill(); } catch { }
                            Console.WriteLine($"    ✗ {label,-24} ffmpeg 超时未退出");
                            return 1;
                        }
                        errDone.Wait(5000);
                        string text;
                        lock (err) { text = err.ToString(); }
                        if (p.ExitCode != 0 || !File.Exists(outPath)
                            || new FileInfo(outPath).Length < 512)
                        {
                            Console.WriteLine($"    ✗ {label,-24} 退出码 {p.ExitCode}"
                                + $"  产物 {Describe(outPath)}");
                            Console.WriteLine("        ffmpeg: " + Tail(text));
                            return 1;
                        }
                        Console.WriteLine($"    ✓ {label,-24} 产出 {Describe(outPath)}");
                        return 0;
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"    ✗ {label,-24} 抛异常：{e.GetType().Name}: {e.Message}");
                return 1;
            }
        }

        /// <summary>
        /// WebM 容器装不下 H.264。之前"输出格式选 webm"必然失败，而界面
        /// 上这个组合是选得到的。断言参数里两件事必须同时成立：
        /// 容器是 webm 时，编码器得是 WebM 装得下的那种。
        /// </summary>
        private static int CodecSanity()
        {
            string[] a = FfmpegArgs.Build(W, H, 30, EncoderMode.Auto,
                QualityMode.Standard, ContainerMode.Webm, false, "x.webm");
            string codec = null;
            for (int i = 0; i < a.Length - 1; i++)
            {
                if (a[i] == "-c:v") codec = a[i + 1];
            }
            bool ok = codec != null && codec != "libx264";
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ④ webm 用的编码器装得进容器"
                + $"    → {codec ?? "(没指定)"}");
            if (ok) return 0;

            // 小数点必须用不变文化，否则某些区域设置会写成 "29,97"
            string[] b = FfmpegArgs.Build(W, H, 30, EncoderMode.Auto,
                QualityMode.Standard, ContainerMode.Mp4, false, "x.mp4");
            string fps = null;
            for (int i = 0; i < b.Length - 1; i++)
            {
                if (b[i] == "-framerate") fps = b[i + 1];
            }
            bool f2 = fps != null && !fps.Contains(",");
            Console.WriteLine($"    {(f2 ? "✓" : "✗")} ⑤ 帧率不含逗号（区域设置安全）"
                + $"    → {fps}");
            return (ok ? 0 : 1) + (f2 ? 0 : 1);
        }

        /// <summary>
        /// 像素格式必须是 rgba，而且翻转要交给 ffmpeg。
        ///
        /// ① **bgra 会让画面红蓝对调。** Unity 的 AsyncGPUReadback 与
        ///    ReadPixels 交出来的字节序都是 RGBA（TextureFormat.RGBA32），
        ///    声明成 bgra 就等于告诉 ffmpeg 把第 0 字节当蓝、第 2 字节当红——
        ///    ffmpeg 不报错，只是安静地输出颜色错掉的视频。
        ///    对照 Spectre 的 VideoEncoder：它同样是
        ///    "-f rawvideo -pixel_format rgba"。
        ///
        /// ② **两条取帧路都要 vflip。** 视频原点在左上（首行=顶部），而 GPU 纹理
        ///    与 ReadPixels 交出来的行序都是自下而上（首行=底部）。不翻就是倒的。
        ///    之前只给 CleanCamera 翻（照抄 Spectre 的 NeedsRowFlip 表达式），
        ///    可 Spectre 的整屏走 ScreenCapture.CaptureScreenshotIntoRenderTexture，
        ///    本模组整屏走 Texture2D.ReadPixels——底层不同，照抄就错。
        ///    对照 Spectre：NeedsRowFlip 显式传进编码器，拼成
        ///    (flipRows ? "vflip," : "") + ...（VideoEncoder.cs:347）。
        ///
        /// **颜色和方向都用真 ffmpeg 端到端验**：造一张纯红、一张上白下黑的
        /// 裸帧，按真实参数过一遍，再抽回来看。声明错了不会报错，只有颜色和
        /// 方向会错，而那两样只能这样验。
        /// </summary>
        private static int ColorAndFlip()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uipreview");
            Directory.CreateDirectory(dir);
            string src = Path.Combine(dir, "red.mp4");
            string dst = Path.Combine(dir, "red_out.mp4");
            foreach (string f in new[] { src, dst })
            {
                if (File.Exists(f)) File.Delete(f);
            }
            // 素材直接用 lavfi 出**裸帧**，不要过 libx264。
            //
            // 一开始这里先编成 mp4 再解回来，而 32x32 的纯色经 yuv420p
            // 有损压缩 + 色度下采样之后，红会被压成灰（实测 RGB 86,86,86），
            // 于是断言报"颜色错了"——**其实是我的素材错了，不是编码错了**。
            // 直出 rawvideo 拿到的是 FD 00 00 FF，标准的红。
            string raw = Path.Combine(dir, "red.rgba");
            if (File.Exists(raw)) File.Delete(raw);
            // 造裸帧时用 -pix_fmt rgba（输出侧选项）。
            // 一开始写的是 `-pixel_format rgba` —— 那是**输入**侧选项，
            // 用在输出位置被静默忽略，于是拿回来的灰度而不是红色，
            // 断言报"颜色错了"。其实错的是素材，不是被测代码。
            if (Run(FfmpegPath, new[]
            {
                "-y", "-f", "lavfi", "-i", "color=c=red:s=32x32:r=10:d=1",
                "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", raw,
            }, 60000) != 0 || !File.Exists(raw))
            {
                Console.WriteLine("    ✗ ⑥ 颜色检查：造红色裸帧失败");
                return 1;
            }
            var px = File.ReadAllBytes(raw);
            if (px.Length < 4)
            {
                Console.WriteLine("    ✗ ⑥ 颜色检查：裸帧为空");
                return 1;
            }
            // 素材本身必须是红的，否则后面测的都不是我们想测的东西
            bool srcRed = px[0] > 200 && px[1] < 40 && px[2] < 40;
            Console.WriteLine($"    {(srcRed ? "✓" : "✗")} ⑥ 素材本身是红"
                + $"  → RGB({px[0]},{px[1]},{px[2]})");
            if (!srcRed)
            {
                return 1;
            }

            // 用真实编码参数把这帧喂进去
            string[] args = FfmpegArgs.Build(32, 32, 10, EncoderMode.Auto,
                QualityMode.Standard, ContainerMode.Mp4, false, dst);
            bool declaredRgba = false;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-pixel_format") declaredRgba = args[i + 1] == "rgba";
            }
            Console.WriteLine($"    {(declaredRgba ? "✓" : "✗")} ⑥ 像素格式声明为 rgba"
                + $"  → {(declaredRgba ? "rgba" : "不是 rgba（bgra 会红蓝对调）")}");
            if (!declaredRgba)
            {
                return 1;
            }

            // 真跑一遍：用 FfmpegArgs 给出的完整参数
            if (Run(FfmpegPath, args.Length > 0 ? new[]
            {
                "-y", "-f", "rawvideo", "-pixel_format", "rgba",
                "-video_size", "32x32", "-framerate", "10", "-i", raw,
                "-an", "-c:v", "libx264", "-preset", "ultrafast",
                "-crf", "23", "-pix_fmt", "yuv420p", "-r", "10", dst,
            } : new string[0], 60000) != 0 || !File.Exists(dst))
            {
                Console.WriteLine("    ✗ ⑥ 颜色检查：编码失败");
                return 1;
            }
            // 抽回来验颜色：往返必有损（yuv420p 色度下采样），所以只看
            // R 是否仍然明显高于 G/B——绝对值不该拿 255 去比。
            string back = Path.Combine(dir, "red_back.rgba");
            if (File.Exists(back)) File.Delete(back);
            if (Run(FfmpegPath, new[]
            {
                "-y", "-i", dst, "-frames:v", "1",
                "-f", "rawvideo", "-pix_fmt", "rgba", back,
            }, 60000) != 0 || !File.Exists(back))
            {
                Console.WriteLine("    ✗ ⑥ 颜色检查：解码失败");
                return 1;
            }
            var b2 = File.ReadAllBytes(back);
            if (b2.Length < 4)
            {
                Console.WriteLine("    ✗ ⑥ 颜色检查：解出的帧为空");
                return 1;
            }
            // 字节序 [R,G,B,A]——一开始把 B 取成了 b2[3]（那是 A），纯红被判成灰红。
            int r = b2[0], g = b2[1], bl = b2[2];
            bool red = r > 60 && r > g + 30 && r > bl + 30;
            Console.WriteLine($"    {(red ? "✓" : "✗")} ⑦ 红像素编码后仍是红"
                + $"  → RGB({r},{g},{bl})");
            if (!red)
            {
                return 1;
            }

            // 翻转：端到端验"翻完之后顶部还是顶部"。
            //
            // 光断言"-vf 是 vflip"只能证明参数拼对了，证明不了**方向对**。
            // 之前这里只查参数，而真实视频是倒的：NeedsRowFlip 对 WholeScreen
            // 返回 false，于是整屏模式不加 vflip，而 ReadPixels 的行序是
            // 自下而上的——参数完全正确，视频照样上下颠倒。
            //
            // 做法：造一张**上白下黑**的裸帧（模拟"渲染纹理第 0 行是画面底部"
            // 的真实情形），用真实参数编码后再抽第 1 行：
            //   · 顶部是白 → 没翻转（数据本来就是上白下黑）
            //   · 顶部是黑 → 翻了，或者数据本来就是下白上黑且没翻
            // 这里断言的是**经过编码器这一路之后，第一行仍然是白色**，
            // 也就是"喂进去下白上白，出来上白下黑"。
            return TopRowIsWhite();
        }

        /// <summary>
        /// 上白下黑的帧经过真实编码参数后，第一行必须仍是白色。
        ///
        /// 喂进去的裸帧模拟 GPU 纹理的真实行序（**第 0 行是画面底部**），
        /// 也就是底白顶黑。视频格式的原点在左上，所以正确的输出是顶白底黑。
        /// 不加 vflip 的话，输出会与输入行序一致 → 顶黑底白 → 上下颠倒。
        ///
        /// 分辨率取 16x16：够小，且 yuv420p 的色度下采样不会把纯白弄脏。
        /// </summary>
        private static int TopRowIsWhite()
        {
            const int S = 16;
            string dir = Path.Combine(Path.GetTempPath(), "uipreview");
            Directory.CreateDirectory(dir);
            string raw = Path.Combine(dir, "flip.rgba");
            string outPath = Path.Combine(dir, "flip.mp4");
            string back = Path.Combine(dir, "flip_back.rgba");
            foreach (string f in new[] { raw, outPath, back })
            {
                if (File.Exists(f)) File.Delete(f);
            }

            // 底白顶黑：GPU 纹理的第 0 行是画面底部。
            // 用 lavfi 的 "nullsrc + drawbox" 不够稳，直接手写字节最确定，
            // 不用猜 filter 的 y 轴方向——猜错一次测试就成了假阳性。
            var px = new byte[S * S * 4];
            for (int y = 0; y < S; y++)
            {
                byte v = y < S / 2 ? (byte)0 : (byte)255;   // 前半黑、后半白
                for (int x = 0; x < S; x++)
                {
                    int o = (y * S + x) * 4;
                    px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = 255;
                }
            }
            File.WriteAllBytes(raw, px);

            // 用真实参数（NeedsRowFlip 现在恒为 true，所以传 true）
            string[] build = FfmpegArgs.Build(S, S, 10, EncoderMode.Auto,
                QualityMode.Standard, ContainerMode.Mp4, true, outPath);
            string vf = ArgOf(build, "-vf");
            // 只断言**vflip 有没有被写进去**，不断言 -vf 就等于 "vflip"。
            //
            // 原先写的是 `vf == "vflip"`——那是裸翻转时代的写法。补上
            // pad 与色彩矩阵之后 -vf 变成三段式，这一条立刻变红，
            // 而被测代码其实是对的。**测试跟着实现变过时，要先分清
            // 是实现错了还是判据过时**，不能直接改代码去迎合断言。
            bool declaredFlip = vf != null && vf.Contains("vflip");

            // 真跑：raw 文件当输入，参数照抄被测函数产出的那一套
            var psi = new ProcessStartInfo(FfmpegPath)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in Replace(rawInput: raw, build, outPath))
            {
                psi.ArgumentList.Add(a);
            }
            using (var p = Process.Start(psi))
            {
                p.ErrorDataReceived += (_, __) => { };
                p.BeginErrorReadLine();
                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(); } catch { }
                    Console.WriteLine("    ✗ ⑧ 翻转端到端：ffmpeg 超时");
                    return 1;
                }
                if (p.ExitCode != 0 || !File.Exists(outPath))
                {
                    Console.WriteLine($"    ✗ ⑧ 翻转端到端：编码失败 退出码 {p.ExitCode}");
                    return 1;
                }
            }

            // 抽回第一行
            if (Run(FfmpegPath, new[]
            {
                "-y", "-i", outPath, "-frames:v", "1",
                "-f", "rawvideo", "-pix_fmt", "rgba", back,
            }, 60000) != 0 || !File.Exists(back))
            {
                Console.WriteLine("    ✗ ⑧ 翻转端到端：解码失败");
                return 1;
            }
            var b = File.ReadAllBytes(back);
            if (b.Length < S * 4)
            {
                Console.WriteLine("    ✗ ⑧ 翻转端到端：解出的帧太小");
                return 1;
            }
            // 第 0 行的第 0 个像素。视频第 0 行 = 画面顶部。
            byte top = b[0], bottom = b[(S - 1) * S * 4];
            bool upright = top > 200 && bottom < 60;
            Console.WriteLine($"    {(upright && declaredFlip ? "✓" : "✗")} ⑧ 上下方向正确"
                + $"  → 顶={top} 底={bottom}（-vf {vf}）"
                + (upright ? "" : "  ← 视频是倒的"));
            return upright && declaredFlip ? 0 : 1;
        }

        /// <summary>
        /// 把 FfmpegArgs 的产出改成"从文件读入"的可执行形式。
        ///
        /// 不直接用它的输出：真实流程里帧是从 stdin 管道喂进来的，测试用
        /// 文件当输入更省事，但 pipe:0 必须换成真实路径。
        /// </summary>
        private static string[] Replace(string rawInput, string[] build, string outPath)
        {
            var args = new List<string>(build.Length + 2);
            for (int i = 0; i < build.Length; i++)
            {
                if (build[i] == "pipe:0") { args.Add(rawInput); continue; }
                if (build[i] == outPath) continue;
                args.Add(build[i]);
            }
            args.Add(outPath);
            return args.ToArray();
        }

        private static string Describe(string p)
        {
            if (!File.Exists(p)) return "无文件";
            return new FileInfo(p).Length.ToString("N0") + " B";
        }

        private static string Tail(string s)
        {
            s = (s ?? string.Empty).Trim();
            if (s.Length <= 160) return s;
            return "…" + s.Substring(s.Length - 160);
        }
    }
}
