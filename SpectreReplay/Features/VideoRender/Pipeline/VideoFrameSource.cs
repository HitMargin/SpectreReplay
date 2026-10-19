using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SpectreReplay.Features.VideoRender;

// 画面源，两种取法：
// - CleanCamera：官方三相机链（Bgcamstatic/BGcam/camobj）渲染到自建纹理，分辨率任意
//   （原生超采样，不是放大），不含屏幕空间 UI；纹理行序与视频相反，编码时要翻转
// - WholeScreen：帧末截取最终合成画面，包含 Screen-Space Overlay（进度条等界面），
//   分辨率跟随游戏窗口；方向与视频一致，无需翻转
internal enum VideoSurface
{
    CleanCamera = 0,
    WholeScreen = 1
}

internal sealed class VideoFrameSource
{
    private struct Flight
    {
        public int Index;
        public AsyncGPUReadbackRequest Request;
    }

    private readonly VideoSurface surface;
    private readonly RenderTexture texture;
    private readonly Queue<Flight> flights = new Queue<Flight>();
    // 帧缓冲池必须线程安全：Recycle 由编码器泵线程回调（写完 stdin 后），
    // Rent 在主线程取用。普通 Queue 跨线程 Enqueue/Dequeue 会破坏内部环形结构，
    // 长渲染收尾阶段可能 Dequeue 出 null（曾致 CopyTo ArgumentNullException 崩溃）
    private readonly ConcurrentQueue<byte[]> spares = new ConcurrentQueue<byte[]>();
    // 池上限：默认 8（轻负载够用），渲染作业在 Begin 后按 队列容量+MaxInFlight
    // 抬高——满载时在飞 buffer 远超 8 个，池太小会每帧把几十 MB 大数组丢进 LOH
    private int spareLimit = 8;
    private readonly int frameBytes;
    private readonly int screenW;
    private readonly int screenH;

    // CleanCamera 模式保存的相机原渲染目标
    private readonly RenderTexture oldStatic;
    private readonly RenderTexture oldBackground;
    private readonly RenderTexture oldMain;

    internal int Width { get; }
    internal int Height { get; }
    internal int InFlight => flights.Count;
    internal bool NeedsRowFlip => surface == VideoSurface.CleanCamera;

    // 渲染中途改窗口尺寸会让帧序列无法延续，调用方应中止
    internal bool ScreenResized
        => surface == VideoSurface.WholeScreen && (Screen.width != screenW || Screen.height != screenH);

    // 在途回读按分辨率限制，防止内存失控
    internal int MaxInFlight
    {
        get
        {
            long pixels = (long)Width * Height;
            if (pixels >= 7_500_000) return 3;  // 4K
            if (pixels >= 3_500_000) return 4;  // 1440p
            return 6;
        }
    }

    internal VideoFrameSource(VideoSurface surface, int width, int height)
    {
        this.surface = surface;
        if (surface == VideoSurface.CleanCamera)
        {
            scrCamera cam = scrCamera.instance;
            if (cam == null || cam.camobj == null)
                throw new InvalidOperationException("scrCamera not available");
            Width = width;
            Height = height;

            // 三台相机由同一套补丁保证非空，但这里是跨版本接缝：任一为 null 就抛，
            // 而不是把 null 一起赋给 targetTexture（赋值本身不报错，之后渲染全黑）
            if (cam.Bgcamstatic == null || cam.BGcam == null)
                throw new InvalidOperationException("scrCamera sub-cameras not available");
            oldStatic = cam.Bgcamstatic.targetTexture;
            oldBackground = cam.BGcam.targetTexture;
            oldMain = cam.camobj.targetTexture;

            texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            // Create() 返回 false 表示显存/驱动拒绝（分辨率超限、显存耗尽、后台无设备）。
            // 原实现无视返回值，于是异步回读每帧都 hasError，成片是一路黑帧却"渲染成功"
            if (!texture.Create())
            {
                Object.Destroy(texture);
                throw new InvalidOperationException(
                    "RenderTexture.Create failed (" + width + "x" + height + ", CleanCamera)");
            }
            cam.Bgcamstatic.targetTexture = texture;
            cam.BGcam.targetTexture = texture;
            cam.camobj.targetTexture = texture;
        }
        else
        {
            Width = screenW = Screen.width;
            Height = screenH = Screen.height;
            // 截屏接口要求目标纹理不带深度缓冲
            texture = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32);
            if (!texture.Create())
            {
                Object.Destroy(texture);
                throw new InvalidOperationException(
                    "RenderTexture.Create failed (" + Width + "x" + Height + ", WholeScreen)");
            }
        }
        frameBytes = Width * Height * 4;
    }

    internal void Grab(int index)
    {
        if (surface == VideoSurface.WholeScreen)
            ScreenCapture.CaptureScreenshotIntoRenderTexture(texture);
        AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(
            texture, 0, TextureFormat.RGBA32, _ => { });
        flights.Enqueue(new Flight { Index = index, Request = request });
    }

    // 只看队首：完成或出错才交付，保证帧序
    internal bool TryTake(out int index, out byte[] pixels, out bool failed)
    {
        index = -1;
        pixels = null;
        failed = false;
        if (flights.Count == 0) return false;
        Flight head = flights.Peek();
        if (!head.Request.done && !head.Request.hasError)
            return false;
        flights.Dequeue();
        index = head.Index;
        if (head.Request.hasError)
        {
            failed = true;
            return true;
        }
        NativeArray<byte> data = head.Request.GetData<byte>();
        if (data.Length != frameBytes)
        {
            failed = true;
            return true;
        }
        pixels = Rent();
        data.CopyTo(pixels);
        return true;
    }

    // 由渲染作业在编码器启动后调用：上限只升不降，避免时序竞争下缩池
    internal void SetSparePoolLimit(int limit)
        => spareLimit = Math.Max(spareLimit, limit);

    internal void Recycle(byte[] pixels)
    {
        if (pixels != null && spares.Count < spareLimit)
            spares.Enqueue(pixels);
    }
    internal void Dispose()
    {
        if (surface == VideoSurface.CleanCamera)
        {
            scrCamera cam = scrCamera.instance;
            if (cam != null)
            {
                if (cam.Bgcamstatic != null) cam.Bgcamstatic.targetTexture = oldStatic;
                if (cam.BGcam != null) cam.BGcam.targetTexture = oldBackground;
                if (cam.camobj != null) cam.camobj.targetTexture = oldMain;
            }
        }
        flights.Clear();
        while (spares.TryDequeue(out _)) { } // net47 的 ConcurrentQueue 没有 Clear()
        if (texture != null)
        {
            texture.Release();
            Object.Destroy(texture);
        }
    }

    private byte[] Rent()
        => spares.TryDequeue(out byte[] buf) ? buf : new byte[frameBytes];
}
