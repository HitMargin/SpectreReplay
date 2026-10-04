using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SpectreReplay.Features.VideoRender;

// 已渲染视频的批量转换：mp4/mkv/webm/mov 互转。
// 快路径优先——mkv 什么都能装、movenc 族（mp4/mov）之间整流 copy，秒级完成；
// 非同族源进 mp4/mov 时音频一律转 AAC（源容器编解码不可知，Opus-in-mp4 播放器兼容性差）；
// webm 目标必须重编码（libvpx-vp9 + Opus）。
// 转换在后台线程执行（VP9 重编码可达分钟级，不能占主线程），状态经 volatile 字段暴露给 UI 轮询。
internal static class VideoConverter
{
    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".webm", ".mov" };

    internal sealed class ConvertState
    {
        // volatile：工作线程写、UI 线程读；Running=false 的写读对保证其余字段可见
        internal volatile bool Running = true;
        internal volatile int Total;
        internal volatile int Done;
        internal volatile int Converted;
        internal volatile int Skipped;
        internal volatile int Failed;
        internal Exception Error;
        internal readonly List<string> Messages = new(); // 仅在 Running=false 后由 UI 读取
    }

    internal static ConvertState Start(string dir, VideoEncoder.OutContainer target, string exePath)
    {
        var state = new ConvertState();
        var worker = new Thread(() =>
        {
            try
            {
                Run(dir, target, exePath, state);
            }
            catch (Exception ex)
            {
                state.Error = ex;
                Debug.Log("[Spectre] video convert failed: " + ex);
            }
            finally
            {
                state.Running = false;
            }
        })
        {
            IsBackground = true,
            Name = "Spectre.VideoConvert"
        };
        worker.Start();
        return state;
    }

    private static void Run(string dir, VideoEncoder.OutContainer target, string exePath, ConvertState state)
    {
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(dir)
                .Where(p => VideoExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return; // 目录不可读 → 零文件，UI 以“未找到”提示
        }
        state.Total = files.Length;
        foreach (string path in files)
        {
            try
            {
                ConvertOne(path, dir, target, exePath, state);
            }
            catch (Exception ex)
            {
                state.Failed++;
                state.Messages.Add(Path.GetFileName(path) + ": " + ex.Message);
            }
            state.Done++;
        }
    }

    private static void ConvertOne(string path, string dir, VideoEncoder.OutContainer target,
        string exePath, ConvertState state)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string targetExt = VideoEncoder.ExtensionOf(target);
        if (ext == targetExt)
        {
            state.Skipped++;
            return;
        }
        string baseName = Path.GetFileNameWithoutExtension(path);
        string targetPath = Path.Combine(dir, baseName + targetExt);
        if (File.Exists(targetPath))
        {
            state.Skipped++;
            state.Messages.Add(baseName + targetExt + " ← " + ext + ": exists");
            return;
        }

        string videoArgs;
        string audioArgs;
        switch (target)
        {
            case VideoEncoder.OutContainer.WebM:
                // VP9 重编码：-crf 30 -b:v 0 = 纯质量模式；row-mt 多线程，不开慢数倍
                videoArgs = "-c:v libvpx-vp9 -deadline good -cpu-used 4 -row-mt 1 -crf 30 -b:v 0 -pix_fmt yuv420p";
                audioArgs = "-c:a libopus -b:a 192k -ar 48000";
                break;
            case VideoEncoder.OutContainer.Mkv:
                // mkv 对编解码几乎不设限：整流 copy
                videoArgs = "-c:v copy";
                audioArgs = "-c:a copy";
                break;
            case VideoEncoder.OutContainer.Mp4:
            case VideoEncoder.OutContainer.Mov:
                if (ext == ".mp4" || ext == ".mov")
                {
                    // movenc 族内互换：通常整流 copy
                    videoArgs = "-c:v copy";
                    audioArgs = "-c:a copy";
                }
                else
                {
                    // webm/mkv 源编解码不可知：VP9 视频流可 copy（tag vp09），
                    // 音频可能是 Opus——in-mp4 兼容性差，统一转 AAC 保播放兼容
                    videoArgs = "-c:v copy";
                    audioArgs = "-c:a aac -b:a 320k";
                }
                break;
            default:
                videoArgs = "-c:v copy";
                audioArgs = "-c:a copy";
                break;
        }

        // 与渲染管线一致：movenc 系目标补 faststart（moov 前置，秒开/拖动不整段下载）；
        // mkv/webm 无此旗标
        string faststart = target == VideoEncoder.OutContainer.Mp4 || target == VideoEncoder.OutContainer.Mov
            ? " -movflags +faststart" : "";
        string args = "-y -i " + Quote(path) + " " + videoArgs + " " + audioArgs + faststart
            + " " + Quote(targetPath);
        bool ok = TryRun(exePath, args);
        // 二次尝试仅用于同族 copy 被个别流卡住的情形（如 PCM 音频）；非同族分支已是 AAC，无需重试
        if (!ok && audioArgs == "-c:a copy")
        {
            string retryArgs = "-y -i " + Quote(path) + " " + videoArgs + " -c:a aac -b:a 320k"
                + faststart + " " + Quote(targetPath);
            ok = TryRun(exePath, retryArgs);
        }
        if (!ok)
        {
            // 失败清理：半截目标文件会被下次 File.Exists 判 “exists” 永久跳过。
            // 注意所有失败路径（含无重试资格的 webm/AAC 分支）都必须落到这里，
            // 否则会误计 Converted 并残留半截文件
            try { File.Delete(targetPath); } catch { }
            state.Failed++;
            state.Messages.Add(Path.GetFileName(path));
            return;
        }
        state.Converted++;
    }

    private static bool TryRun(string exePath, string arguments)
    {
        try
        {
            VideoEncoder.RunAndWait(exePath, arguments);
            return true;
        }
        catch (Exception ex)
        {
            Debug.Log("[Spectre] video convert attempt failed: " + ex.Message);
            return false;
        }
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"") + "\"";
}
