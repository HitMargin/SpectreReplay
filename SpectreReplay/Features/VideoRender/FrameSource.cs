using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;      // 与 System.Object 撞名，显式绑定

namespace SpectreReplay.Features.VideoRender
{
    /// <summary>
    /// 画面源。两种取法：
    ///
    ///  CleanCamera —— 把游戏的官方相机链（BGCam/BGcamStatic/camobj）渲到一张自建
    ///    RenderTexture 上。分辨率可以任意设且是**原生超采样**（不是放大），
    ///    画面里没有 Screen-Space Overlay 的界面（进度条、菜单都不在）。
    ///
    ///  WholeScreen —— 在一帧的末尾抓最终合成结果。分辨率只能跟窗口，
    ///    但进度条那些 Overlay 界面在里头。
    ///
    /// 两种都用 AsyncGPUReadback 把 GPU 纹理读回 CPU：直接 ReadPixels 会
    ///  同步等 GPU，掉帧时整条流水线一起卡住；异步回读让等待发生在后面的帧里。
    /// </summary>
    internal sealed class FrameSource
    {
        private readonly FrameSurface _surface;
        private readonly int _width;
        private readonly int _height;
        private RenderTexture _rt;
        private Texture2D _readback;
        private bool _readbackPending;

        internal int Width => _width;
        internal int Height => _height;
        internal int FrameSize => _width * _height * 4;

        /// <summary>
        /// 编码时是否需要上下翻转。
        ///
        /// **两条取帧路都需要翻转。** 视频的原点在左上（第一行是画面顶部），
        /// 而 Unity 的 GPU 纹理与 ReadPixels 交出来的行序都是**自下而上**
        /// （第 0 行是画面底部）。不翻就是上下颠倒。
        ///
        /// 之前这里写的是 <c>_surface == FrameSurface.CleanCamera</c>，
        /// 依据是 Spectre 的 <c>NeedsRowFlip</c> 同名表达式。但两者的
        /// 整屏取帧方式根本不同：
        ///   Spectre  WholeScreen 走 ScreenCapture.CaptureScreenshotIntoRenderTexture
        ///   本模组 WholeScreen 走 Texture2D.ReadPixels
        /// 后者拿到的像素行序与渲染纹理一致（自下而上），于是"不翻"就是错的。
        /// 照抄那个表达式时没核对底层差异，视频就是倒着放的。
        ///
        /// 结论：**只要帧是从 GPU 纹理里读出来的就要翻**，与走哪条相机链无关。
        /// 哪天若改回 ScreenCapture 方案，必须重新核对这一行。
        /// </summary>
        internal bool NeedsRowFlip => true;

        internal FrameSource(FrameSurface surface, int width, int height)
        {
            _surface = surface;
            _width = width;
            _height = height;
            if (_surface == FrameSurface.CleanCamera)
            {
                _rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                {
                    antiAliasing = 1,
                };
                _rt.Create();
                _readback = new Texture2D(width, height, TextureFormat.RGBA32, false);
            }
            else
            {
                // 整屏模式的分辨率由窗口决定
                var w = Screen.width;
                var h = Screen.height;
                _widthField = w;
                _heightField = h;
            }
        }

        // 整屏模式要用实际窗口分辨率，而 Width/Height 是只读属性，
        // 所以尺寸在构造时定死、存在独立字段里供属性返回。
        private readonly int _widthField;
        private readonly int _heightField;

        internal void Dispose()
        {
            if (_rt != null)
            {
                _rt.Release();
                Object.DestroyImmediate(_rt);
                _rt = null;
            }
            if (_readback != null)
            {
                Object.DestroyImmediate(_readback);
                _readback = null;
            }
        }

        /// <summary>
        /// 请求一帧。返回 true 表示这次真的排上了一次回读；false 表示上一次
        /// 还没回来——调用方该跳过这一帧而不是等。
        /// </summary>
        internal bool Request()
        {
            if (_readbackPending)
            {
                return false;
            }
            if (_surface == FrameSurface.WholeScreen)
            {
                RequestWholeScreen();
            }
            else
            {
                RequestCleanCamera();
            }
            return true;
        }

        private void RequestCleanCamera()
        {
            var cams = GameCameras.Cameras();
            if (cams.Count == 0)
            {
                return;
            }
            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            GL.Clear(true, true, new Color(0f, 0f, 0f, 1f));
            foreach (var cam in cams)
            {
                if (cam != null)
                {
                    cam.targetTexture = _rt;
                    cam.Render();
                    cam.targetTexture = prev;
                }
            }
            RenderTexture.active = prev;
            AsyncGPUReadback.Request(_rt, 0, OnReadback);
            _readbackPending = true;
        }

        private void RequestWholeScreen()
        {
            int w = Screen.width;
            int h = Screen.height;
            _readbackRequestW = w;
            _readbackRequestH = h;
            _readbackTemp = new Texture2D(w, h, TextureFormat.RGBA32, false);
            _readbackTemp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            _readbackTemp.Apply();
            NativeArrayToBytes(_readbackTemp, OnWholeScreenReady);
        }

        private Texture2D _readbackTemp;
        private int _readbackRequestW;
        private int _readbackRequestH;

        private static void NativeArrayToBytes(Texture2D tex, System.Action<byte[], int, int> done)
        {
            var raw = tex.GetRawTextureData<byte>();
            byte[] bytes = new byte[raw.Length];
            // Span.CopyTo 在这个目标框架下不可用（netstandard2.1 + 游戏的
            // mscorlib 门面集），走一次托管拷贝。
            System.Buffer.BlockCopy(raw.ToArray(), 0, bytes, 0, bytes.Length);
            done(bytes, tex.width, tex.height);
            Object.DestroyImmediate(tex);
        }

        private void OnReadback(UnityEngine.Rendering.AsyncGPUReadbackRequest request)
        {
            _readbackPending = false;
            if (request.hasError || PendingSink == null)
            {
                return;
            }
            var data = request.GetData<byte>();
            byte[] bytes = new byte[data.Length];
            data.CopyTo(bytes);
            // **这里不翻转。** 渲染纹理的行序与视频相反，但翻转交给
            // ffmpeg 的 `-vf vflip` 做（见 NeedsRowFlip 与 FfmpegArgs）。
            //
            // 原先在这里 CPU 侧搬字节翻转，而 WholeScreen 那条路（ReadPixels）
            // 又不翻——两条路的行为不一致，视频的正反取决于选了哪个模式，
            // 看起来就是"有时上下颠倒"。Spectre 也是统一交给 ffmpeg：
            // VideoRenderJob 把 NeedsRowFlip 传给编码器，编码器拼进 -vf。
            PendingSink(bytes, _width, _height);
        }

        private void OnWholeScreenReady(byte[] bytes, int w, int h)
        {
            _readbackPending = false;
            PendingSink?.Invoke(bytes, w, h);
        }

        /// <summary>由捕获循环设置：一帧像素的出口。</summary>
        internal System.Action<byte[], int, int> PendingSink;

        /// <summary>上下翻转（渲染纹理原点在下，视频原点在上）。</summary>
        internal static void FlipVertical(byte[] buf, int w, int h)
        {
            int stride = w * 4;
            var tmp = new byte[stride];
            for (int y = 0; y < h / 2; y++)
            {
                int top = y * stride;
                int bottom = (h - 1 - y) * stride;
                System.Buffer.BlockCopy(buf, top, tmp, 0, stride);
                System.Buffer.BlockCopy(buf, bottom, buf, top, stride);
                System.Buffer.BlockCopy(tmp, 0, buf, bottom, stride);
            }
        }
    }

    /// <summary>游戏官方相机链的收集。取不到就退化成主相机。</summary>
    internal static class GameCameras
    {
        internal static List<Camera> _scratch = new List<Camera>(4);

        /// <summary>
        /// 取游戏自己的相机链，而不是 GameObject.Find。
        ///
        /// **Why 不是 Find：名字对不上且找不全。** scrCamera.cs:17-25 的字段名是
        /// Bgcamstatic / BGcam / camobj / Overlaycam，而场景里实际的 GameObject
        /// 名字未必是 "BGCamstatic"——我原来按名字 Find，结果只捡到其中一部分，
        /// 于是编辑器面板（Overlaycam 画的）漏进画面里。而 Find 找不到时静默
        /// 退回 Camera.main，把"没找对"变成了"看起来能用但内容不对"。
        ///
        /// 照 Spectre（VideoFrameSource 的构造函数）：直接读
        /// scrCamera.instance 上的三个相机字段。
        ///
        /// 注意**不包含 Overlaycam**：编辑器面板/暂停菜单是它画的，
        /// CleanCamera 模式的意义就是不要它们。Spectre 也只用三个。
        /// </summary>
        internal static List<Camera> Cameras()
        {
            var list = _scratch;
            list.Clear();
            try
            {
                // scrCamera 在**全局命名空间**（scrCamera.cs:6，class scrCamera）
                // 且继承 ADOBase，不在 ADOFAI 命名空间里。
                var cam = scrCamera.instance;
                if (cam != null)
                {
                    if (cam.Bgcamstatic != null) list.Add(cam.Bgcamstatic);
                    if (cam.BGcam != null) list.Add(cam.BGcam);
                    if (cam.camobj != null) list.Add(cam.camobj);
                }
            }
            catch (Exception)
            {
                // 关卡尚未生成相机：当作没有
            }
            if (list.Count == 0)
            {
                var main = Camera.main;
                if (main != null)
                {
                    list.Add(main);
                }
            }
            return list;
        }
    }
}