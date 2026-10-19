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

    // 构造时抓到的相机引用。还原必须照这三台来，不能用 scrCamera.instance：
    // 渲染途中切场景后 instance 已指向新相机，照它还原会打偏。
    private readonly Camera camStatic;
    private readonly Camera camBackground;
    private readonly Camera camMain;

    // 渲染目标是否已还回去（幂等：正常路径与 Dispose 兜底各调一次）
    private bool targetsRestored;

    // 捕获期间的屏幕预览相机（仅 CleanCamera）。三台游戏相机被劫持到我们的纹理后，
    // backbuffer 没人清屏，需要它每帧清屏 + 把纹理贴回去，见 CreatePreviewCamera。
    private GameObject previewHost;
    private Camera previewCamera;

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
            camStatic = cam.Bgcamstatic;
            camBackground = cam.BGcam;
            camMain = cam.camobj;
            oldStatic = camStatic.targetTexture;
            oldBackground = camBackground.targetTexture;
            oldMain = camMain.targetTexture;

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

            // **捕获期间屏幕必须还有东西在画。** 三台相机被劫持到我们的
            // RenderTexture 之后，屏幕上没有任何相机清屏：世界冻在最后一帧，
            // 而 IMGUI 每帧照常往上叠，半透明菜单一层层累加 ⇒ 越叠越亮、
            // 文字重影（镜厅）。这不是收尾那一小段才有的问题——整个捕获期都这样。
            //
            // 加一台只负责清屏 + 把刚渲染的画面贴回屏幕的相机：
            // cullingMask = 0 渲染任何场景物体，depth 拉到最大排在所有相机之后，
            // OnPostRender 里把我们的纹理 Blit 到 backbuffer。
            CreatePreviewCamera();
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
    /// <summary>
    /// 建一台只负责「清屏 + 把当前捕获画面贴回屏幕」的相机。
    ///
    /// **为什么必须有它。** CleanCamera 模式把 scrCamera 的三台相机全部指向我们的
    /// RenderTexture。Unity 每帧只会清一次 backbuffer，靠的是最前面那台相机的
    /// clearFlags——三台都不是写给屏幕的了，backbuffer 就再也没人清。
    /// 结果：世界画面冻在最后一帧，而 IMGUI（UMM 窗口等）每帧照常往上叠。
    /// 半透明底色一层层累加 ⇒ 界面越来越亮、文字重影越叠越多（镜厅）。
    /// 这不是收尾那一小段，是整个捕获期都存在的问题。
    ///
    /// 预览相机参数的三处用意：
    ///   · <c>clearFlags = SolidColor</c> + 黑色背景：每帧把 backbuffer 擦干净，
    ///     否则上一帧的 IMGUI 会留下残影（这正是镜厅的来源）。
    ///   · <c>cullingMask = 0</c>：它只负责擦屏和贴图，不重复渲染场景。
    ///   · <c>depth</c> 拉到最大：必须排在所有游戏相机之后，否则它先清屏、
    ///     游戏相机再画到别处，屏幕还是空的。
    /// </summary>
    private void CreatePreviewCamera()
    {
        if (previewCamera != null) return;
        previewHost = new GameObject("Spectre.RenderPreview");
        Object.DontDestroyOnLoad(previewHost);
        previewCamera = previewHost.AddComponent<Camera>();
        previewCamera.clearFlags = CameraClearFlags.SolidColor;
        previewCamera.backgroundColor = Color.black;
        previewCamera.cullingMask = 0;
        previewCamera.depth = 1000f;
        previewCamera.orthographic = true;
        previewCamera.orthographicSize = 1f;
        // 不挂 AudioListener：场景里已经有一个，多一个会刷警告并改变混音
        previewCamera.allowHDR = false;
        previewCamera.allowMSAA = false;
        previewHost.AddComponent<PreviewBlitter>().Source = this;
    }

    // 独立组件而不是给 Camera 挂事件：相机组件自己不能实现 MonoBehaviour 回调，
    // OnPostRender 必须由同物体上的另一个脚本接收
    private sealed class PreviewBlitter : MonoBehaviour
    {
        internal VideoFrameSource Source;
        // 用 OnPostRender 而不是 OnRenderImage：后者要求相机带 image effect 语义
        // （需要 material 参数参与管线），而这里只是"画完把纹理盖上去"
        private void OnPostRender()
        {
            RenderTexture src = Source != null ? Source.texture : null;
            if (src == null) return;
            // 直接用 Graphics.Blit 画到 backbuffer。此时 backbuffer 已被本相机清成黑色，
            // 贴上去就是刚渲染好的那一帧——屏幕上看到的与成片一致（镜像翻转由
            // NeedsRowFlip 在编码侧处理，预览这里不需要，因为 Blit 本身就是屏幕朝向）
            Graphics.Blit(src, (RenderTexture)null);
        }
    }

    /// <summary>
    /// 把三台相机的渲染目标还回去。**捕获一结束就该调，不能等 Dispose。**
    ///
    /// 相机挂在我们的 RenderTexture 上期间，屏幕上没有相机清屏：世界停在最后一帧，
    /// 而 IMGUI 每帧照常往上叠。半透明菜单一层层累加 ⇒ 界面越来越亮、文字重影
    /// 越叠越多（「镜厅」）。这段窗口是后台收尾（ffmpeg 排空 + mux），几十秒到几分钟。
    ///
    /// 幂等：正常路径（ReleaseGameControl）与 Dispose 兜底各会调一次。
    /// </summary>
    internal void RestoreTargets()
    {
        if (targetsRestored) return;
        targetsRestored = true;
        // 预览相机先拆：它每帧都在往 backbuffer 贴我们的纹理，留着会让相机还原后
        // 屏幕被一张过期的捕获帧盖住。与相机还原放在同一次调用里，避免中间出现
        // 「游戏相机已写屏幕、预览又盖一层」的混合帧。
        if (previewHost != null)
        {
            Object.Destroy(previewHost);
            previewHost = null;
            previewCamera = null;
        }
        if (surface != VideoSurface.CleanCamera) return;
        // 逐项兜底：相机可能在渲染途中被销毁，任一抛异常都不能带走后面两台。
        // **用构造时抓到的引用**，不是 scrCamera.instance —— 渲染中切过场景的话，
        // instance 已经是新相机，照它还原会打偏，旧相机永远留在我们的纹理上。
        try { if (camStatic != null) camStatic.targetTexture = oldStatic; }
        catch (Exception ex) { Debug.LogException(ex); }
        try { if (camBackground != null) camBackground.targetTexture = oldBackground; }
        catch (Exception ex) { Debug.LogException(ex); }
        try { if (camMain != null) camMain.targetTexture = oldMain; }
        catch (Exception ex) { Debug.LogException(ex); }
    }

    internal void Dispose()
    {
        RestoreTargets();
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
