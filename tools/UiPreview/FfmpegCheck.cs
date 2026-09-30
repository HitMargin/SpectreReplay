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
            return fail;
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
                    // 喂真帧：BGRA，每帧底色渐变，省得全是黑看不出问题
                    byte[] frame = new byte[W * H * 4];
                    for (int f = 0; f < Frames; f++)
                    {
                        for (int i = 0; i < frame.Length; i += 4)
                        {
                            frame[i] = (byte)(f * 20);        // B
                            frame[i + 1] = (byte)(255 - f * 20); // G
                            frame[i + 2] = (byte)(f * 10);    // R
                            frame[i + 3] = 255;                // A
                        }
                        p.StandardInput.BaseStream.Write(frame, 0, frame.Length);
                    }
                    p.StandardInput.Close();
                    string err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(60000))
                    {
                        try { p.Kill(); } catch { }
                        Console.WriteLine($"    ✗ {label,-24} ffmpeg 超时");
                        return 1;
                    }
                    if (p.ExitCode != 0 || !File.Exists(outPath)
                        || new FileInfo(outPath).Length < 512)
                    {
                        Console.WriteLine($"    ✗ {label,-24} 退出码 {p.ExitCode}"
                            + $"  产物 {Describe(outPath)}");
                        Console.WriteLine("        ffmpeg: " + Tail(err));
                        return 1;
                    }
                    Console.WriteLine($"    ✓ {label,-24} 产出 {Describe(outPath)}");
                    return 0;
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
