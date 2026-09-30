using System;
using System.Diagnostics;
using System.Text;

namespace SpectreReplay.Features.VideoRender
{
    /// <summary>
    /// 把音频贴到已编码好的视频上。**两条渲染路径共用这一份实现。**
    ///
    /// 之前合流只写在 LiveSession 里，OfflineSession 直接把无声视频当作成品
    /// ——离线渲染出来的东西**完全没有声音**，而界面上的状态还写着
    /// "离线渲染 完成"。这类缺失不会报错，只会让用户以为功能坏了。
    ///
    /// 放在这里还有一个理由：<c>-c:v copy</c> 不重编码画面，只是重新封装，
    /// 所以这一步是秒级而不是分钟级——但前提是它**一定**会被调用。
    /// 只有一份实现，就不可能再出现"一条合流一条不合"。
    /// </summary>
    internal static class AudioMux
    {
        private const int TimeoutMs = 180_000;

        /// <summary>
        /// 合流。成功返回 outPath；失败则退而求其次把无声视频复制过去，
        /// 并至少保证调用方拿到一个存在的文件。
        /// </summary>
        internal static string Mux(string ffmpegPath, string video, string audio,
            string outPath, ContainerMode container)
        {
            if (!System.IO.File.Exists(audio))
            {
                UnityEngine.Debug.LogWarning(
                    "[SpectreReplay] 没有音频文件，保留无声视频");
                return Fallback(video, outPath);
            }
            try
            {
                var psi = new ProcessStartInfo(ffmpegPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                foreach (string a in FfmpegArgs.BuildMuxArgs(
                    video, audio, outPath, container))
                {
                    psi.ArgumentList.Add(a);
                }
                using (var p = Process.Start(psi))
                {
                    // **先异步抽干 stderr，再等退出。**
                    // 写成 `ReadToEnd()` 之后 `WaitForExit(180_000)` 的话：
                    // ReadToEnd 要一直读到流关闭才返回，ffmpeg 一旦卡住就永远
                    // 阻塞在那儿——**那 180 秒的看门狗根本轮不到执行**，
                    // 整个游戏会冻在结算画面上出不来。
                    var err = new StringBuilder();
                    using (var errDone = new System.Threading.ManualResetEventSlim(false))
                    {
                        p.ErrorDataReceived += (_, e) =>
                        {
                            if (e.Data == null) { errDone.Set(); return; }
                            lock (err) { err.AppendLine(e.Data); }
                        };
                        p.BeginErrorReadLine();

                        if (!p.WaitForExit(TimeoutMs))
                        {
                            try { p.Kill(); } catch { }
                            UnityEngine.Debug.LogWarning(
                                "[SpectreReplay] 合流超时，已中止，保留无声视频");
                        }
                        else if (p.ExitCode == 0 && System.IO.File.Exists(outPath))
                        {
                            return outPath;
                        }
                        else
                        {
                            string text;
                            lock (err) { text = err.ToString(); }
                            UnityEngine.Debug.LogWarning(
                                "[SpectreReplay] 合流失败，保留无声视频：" + text);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[SpectreReplay] 合流出错：" + ex.Message);
            }
            return Fallback(video, outPath);
        }

        /// <summary>合流没成时，至少把无声视频交出去，别让用户什么都拿不到。</summary>
        private static string Fallback(string video, string outPath)
        {
            try
            {
                System.IO.File.Copy(video, outPath, true);
            }
            catch
            {
                // 连复制都失败就返回原路径
            }
            return System.IO.File.Exists(outPath) ? outPath : video;
        }
    }
}
