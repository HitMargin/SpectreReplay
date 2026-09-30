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
        internal static string[] Build(int width, int height, int fps,
            EncoderMode encoder, QualityMode quality, ContainerMode container,
            string outPath)
        {
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
                "-pixel_format", "bgra",
                "-video_size", width + "x" + height,
                "-framerate", fpsText,
                "-i", "pipe:0",
                "-an",                       // 音频由单独的混音轨合进来
                "-c:v", codec,
                "-preset", preset,
            };
            // libvpx-vp9 用 -crf 表达质量，libx264 也是，语义一致。
            args.Add("-crf");
            args.Add(crf);
            args.Add("-pix_fmt");
            args.Add("yuv420p");
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
    }
}
