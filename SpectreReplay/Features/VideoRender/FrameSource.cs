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

        /// <summary>
        /// 回读的"在飞"闸门。**多路回读的互斥、以及"这次到底排上了吗"的判定
        /// 都在它里面**（见 ReadbackGate 的说明：整屏取帧的回调是同步的，
        /// 置位与清位的先后顺序错了就会永久卡死，且不报错、不停机）。
        /// </summary>
        private readonly ReadbackGate _gate = new ReadbackGate();

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
            // **置位与判定都在闸门里，这里不再自己碰"在飞"状态。**
            //
            // 原来这里是这个形状：
            //     bool queued = _surface == FrameSurface.WholeScreen
            //         ? RequestWholeScreen() : RequestCleanCamera();
            //     if (queued) { _readbackPending = true; }
            // 它在**异步**那条路（CleanCamera → AsyncGPUReadback.Request）
            // 上是对的，在**同步**那条路（WholeScreen → ReadPixels）上则整个反了：
            // RequestWholeScreen() 内部会同步调到 OnWholeScreenReady，
            // 回调里先把 _readbackPending 清成 false，等它返回 true、
            // 回到这里，置位才发生 —— 置位落在清位之后，再没有人清它。
            //
            // 后果：第 2 帧起 Request() 恒返回 false。Live 模式成片是
            // **第一帧的静止画面**（时长与音轨都对，看着能用、内容全错），
            // 离线模式编码器只收到 1 帧。全程不报错、不停机、日志全无。
            //
            // 闸门先置位再发请求，同步与异步两种回调顺序都成立；
            // 请求返回 false（相机列表为空 / 窗口尺寸为 0，即"什么都没排上"）
            // 时它会把置位收回去 —— 那种情况下调用方也不会去等回调。
            if (_surface == FrameSurface.WholeScreen)
            {
                return _gate.TryStart(RequestWholeScreen);
            }
            return _gate.TryStart(RequestCleanCamera);
        }

        private bool RequestCleanCamera()
        {
            var cams = GameCameras.Cameras();
            if (cams.Count == 0)
            {
                return false;
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
            return true;
        }

        private bool RequestWholeScreen()
        {
            int w = Screen.width;
            int h = Screen.height;
            // 窗口尺寸为 0 时（比如正在切场景）ReadPixels 会拿到空区域。
            // 与上面的相机列表为 0 是同一类"这次什么都没排上"，必须一起返回 false，
            // 否则调用方会等一个不会来的回调。
            if (w <= 0 || h <= 0)
            {
                return false;
            }
            _readbackRequestW = w;
            _readbackRequestH = h;
            _readbackTemp = new Texture2D(w, h, TextureFormat.RGBA32, false);
            _readbackTemp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            _readbackTemp.Apply();
            NativeArrayToBytes(_readbackTemp, OnWholeScreenReady);
            return true;
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
            // 先放开闸门再交帧：交帧本身可能抛（PendingSink 是外部代码），
            // 把闸门留在锁死状态就等于整个录制停在第 N 帧。
            _gate.Complete();
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
            // **这条回调是同步的**：RequestWholeScreen 还没返回，
            // 这里就已经跑完了。闸门因此必须先置位再发请求（见 ReadbackGate）。
            _gate.Complete();
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