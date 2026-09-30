// ffmpeg 参数的端到端验证。
//
// 参数拼错的后果不是"报错"而是**全程无声无息地失败**：没有视频、也不
// 一定在界面上留下像样的原因。所以这类问题只能靠真跑一遍来发现。
//
// 做法：链接真实的 FfmpegArgs（纯函数，不碰引擎），调用**游戏自带的那个
// ffmpeg.exe**，喂进真的 BGRA 帧，看每个容器组合是不是真能出片。
// 参数写得再"看起来对"，不如让 ffmpeg 自己判一次。
using System;
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
            fail += NegativeControl();
            fail += Mux();
            return fail;
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
                    QualityMode.Standard, container, "x" + ext), "-c:v");
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
                QualityMode.Standard, container, outPath))
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
                QualityMode.Standard, ContainerMode.Webm, "x.webm");
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
                QualityMode.Standard, ContainerMode.Mp4, "x.mp4");
            string fps = null;
            for (int i = 0; i < b.Length - 1; i++)
            {
                if (b[i] == "-framerate") fps = b[i + 1];
            }
            bool f2 = fps != null && !fps.Contains(",");
            Console.WriteLine($"    {(f2 ? "✓" : "✗")} ⑤ 帧率不含逗号（区域设置安全）"
                + $"    → {fps}");
            return ok ? 0 : 1;
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
