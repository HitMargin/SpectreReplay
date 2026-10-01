using System.Collections.Generic;
using System.Globalization;

namespace SpectreReplay.Features.VideoRender
{
    // 三个枚举跟着 FfmpegArgs 走：它们描述的就是"要传给 ffmpeg 的什么"，
    // 放这里才能让这段纯函数被离线测试直接链接。
    internal enum EncoderMode { Auto = 0, Fastest = 1, Quality = 2, Cpu = 3 }
    internal enum QualityMode { Low = 0, Standard = 1, High = 2, Extreme = 3 }
    internal enum ContainerMode { Mp4 = 0, Mkv = 1, Webm = 2 }
    /// <summary>
    /// ffmpeg 命令行的拼装。**纯函数，不碰任何引擎类型**——
    /// 这样能脱离游戏直接验：参数拼错的话，渲染全程无声无息地失败，
    /// 而那种错误只有在真机上产出不了视频时才看得见。
    ///
    /// 单独拆出来还有第二个理由：调用点要喂进 <c>ProcessStartInfo</c>，
    /// 里面的引号与转义规则和这里手写的字符串是两回事。把"我们要什么参数"
    /// 和"怎么交给进程"分开，才不会在真机上才看出是哪一层出的问题。
    /// </summary>
    internal static class FfmpegArgs
    {
        /// <summary>
        /// 拼编码参数。
        ///
        /// <paramref name="flipRows"/> 决定要不要 <c>-vf vflip</c>：只有
        /// CleanCamera 采集的帧需要（渲染纹理原点在左下，视频在左上），
        /// 整屏模式方向本来就对。
        ///
        /// **翻转统一交给 ffmpeg，不在 CPU 侧搬字节。** 之前是两条路各翻各的
        /// （CleanCamera 翻、ReadPixels 不翻），于是正反取决于选了哪个模式。
        /// Spectre 也是统一交给编码器：VideoRenderJob 把 NeedsRowFlip
        /// 传进来，VideoEncoder 拼成 <c>(flipRows ? "vflip," : "") + ...</c>。
        /// </summary>
        internal static string[] Build(int width, int height, int fps,
            EncoderMode encoder, QualityMode quality, ContainerMode container,
            bool flipRows, string outPath)
        {
            string vflip = flipRows ? "vflip" : "null";
            // 容器决定能用哪些编码：WebM 只能装 VP8/VP9/AV1。
            // 之前无论选什么编码器都是 libx264，于是"输出格式选 webm"会
            // 让 ffmpeg 直接失败——而这个组合在界面上是能选到的。
            bool webm = container == ContainerMode.Webm;
            string codec = webm ? "libvpx-vp9" : "libx264";
            string preset =
                encoder == EncoderMode.Fastest ? "ultrafast" :
                encoder == EncoderMode.Quality ? "slow" :
                encoder == EncoderMode.Cpu ? "veryfast" :
                "medium";
            string crf =
                quality == QualityMode.Low ? "28" :
                quality == QualityMode.Standard ? "23" :
                quality == QualityMode.High ? "19" : "16";
            // 固定用不变文化写数字：某些区域设置的 ToString() 会把小数点写成
            // 逗号，"-framerate 29,97" 对 ffmpeg 来说就是非法参数。
            string fpsText = fps.ToString(CultureInfo.InvariantCulture);

            var args = new List<string>
            {
                "-y",
                "-f", "rawvideo",
                // **必须是 rgba，不是 bgra。** Unity 的 AsyncGPUReadback 与
                // ReadPixels 交出来的字节序都是 RGBA（TextureFormat.RGBA32）。
                // 这里原先写 bgra，R 与 B 被互换——**画面整体红蓝对调**，
                // 而 ffmpeg 不会报错，只是安静地给出颜色错掉的视频。
                // 对照 Spectre 的 VideoEncoder.cs：它同样是
                // "-f rawvideo -pixel_format rgba"。
                "-pixel_format", "rgba",
                "-video_size", width + "x" + height,
                "-framerate", fpsText,
                "-i", "pipe:0",
                "-an",                       // 音频由单独的混音轨合进来
                "-vf", vflip,
                "-c:v", codec,
                "-preset", preset,
            };
            // libvpx-vp9 用 -crf 表达质量，libx264 也是，语义一致。
            args.Add("-crf");
            args.Add(crf);
            args.Add("-pix_fmt");
            args.Add("yuv420p");
            // **色彩空间必须显式声明。** 缺了它 ffmpeg 按 BT.601 解读 RGB，
            // 而游戏输出的是 BT.709 —— 结果是高饱和色（红蓝）整体偏色。
            // 对照 Spectre 的 VideoEncoder.Begin()：它写着
            // "-pix_fmt yuv420p -colorspace bt709 -color_primaries bt709
            //   -color_trc bt709"。
            args.Add("-colorspace");
            args.Add("bt709");
            args.Add("-color_primaries");
            args.Add("bt709");
            args.Add("-color_trc");
            args.Add("bt709");
            args.Add("-r");
            args.Add(fpsText);

            if (container == ContainerMode.Mkv)
            {
                args.Add("-f");
                args.Add("matroska");
            }
            else if (webm)
            {
                args.Add("-f");
                args.Add("webm");
            }
            args.Add(outPath);
            return args.ToArray();
        }

        /// <summary>
        /// 把落盘的 wav 音轨合进视频。
        ///
        /// 容器同样决定音频编码器：WebM 装不下 AAC（只收 Opus/Vorbis）。
        /// 这跟"WebM 装不下 H.264"是同一类错——而"输出格式"和"视频渲染"
        /// 这两项在界面上是可以自由组合的，选 webm 的人一定会踩到。
        /// </summary>
        internal static string[] BuildMuxArgs(string video, string audio,
            string outPath, ContainerMode container)
        {
            bool webm = container == ContainerMode.Webm;
            return new[]
            {
                "-y",
                "-i", video,
                "-i", audio,
                "-c:v", "copy",        // 画面直接复制，不重编码
                "-c:a", webm ? "libopus" : "aac",
                "-b:a", webm ? "128k" : "192k",
                "-shortest",           // 以最短的流为准，避免尾部空音频
                outPath,
            };
        }
    }
}
