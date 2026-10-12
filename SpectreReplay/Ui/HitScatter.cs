using System;
using System.Collections.Generic;
using UnityEngine;
using SpectreReplay.Features.Replay;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 命中分布柱状图（回放分析用）：把每一击的时间偏差画成一根柱子，
    /// 按当时那一击的 Perfect / Pure 窗口宽度染色。
    ///
    /// **参考上游 Spectre 的 <c>BuildScatter</c>，但实现完全不同。**
    /// 上游是 Quartz uGUI 页面：new GameObject + RectTransform + LayoutElement
    /// + RawImage，把贴图挂成 UI 节点。我们是 UMM 的 IMGUI 页面，**那套对象
    /// 一个都用不上**——IMGUI 里只需要一张贴图，用 `GUI.DrawTexture` 画进
    /// 一个 `GUILayoutUtility.GetRect` 出来的矩形。（这正是"只参考不复制"：
    /// 学它"按窗口宽度分色"的取舍，不搬它的对象树。）
    ///
    /// **贴图必须复用同一个对象。** 尺寸固定，所以只建一次、之后
    /// `SetPixels32` 覆盖。每帧 new 会持续泄漏显存（Unity 贴图不受 GC 管理），
    /// 而这里是每帧都要画的。同理不做 Destroy 重建——那会在"正在绘制"的
    /// 同一帧销毁贴图。
    /// </summary>
    internal static class HitScatter
    {
        private const int W = 512;      // 足够细：一屏最多画 512 根柱，再密也看不出
        private const int H = 120;
        /// <summary>纵轴满量程（毫秒）。超出这个范围的偏差一律截到边缘。</summary>
        private const float WindowMs = 150f;

        private static Texture2D _tex;
        private static object _builtFrom;
        private static int _builtCount = -1;

        // 配色（与 Gui 的语义色一致：绿=好、黄=中、红=差）
        private static readonly Color32 Bg     = new Color32(20, 22, 28, 255);
        private static readonly Color32 Axis   = new Color32(120, 120, 120, 255);
        private static readonly Color32 Good   = new Color32(64, 217, 115, 255);
        private static readonly Color32 Warn   = new Color32(242, 204, 51, 255);
        private static readonly Color32 Bad    = new Color32(230, 77, 77, 255);

        /// <summary>
        /// 取（必要时重建）贴图。<paramref name="source"/> 用来判"换了另一份回放"：
        /// 传 <c>SpectreState.data</c> 即可，它换实例就说明换了回放。
        /// </summary>
        internal static Texture2D Get(ReplayAnalyzer.Report rep, object source)
        {
            if (rep == null || rep.SamplesMs == null || rep.SamplesMs.Count == 0)
            {
                return null;
            }
            // 缓存判据：同一份回放 + 同样的样本数。样本数变了说明数据变了。
            if (_tex != null && ReferenceEquals(_builtFrom, source)
                && _builtCount == rep.SamplesMs.Count)
            {
                return _tex;
            }
            Build(rep, source);
            return _tex;
        }

        /// <summary>丢弃缓存（换回放 / 关页面时调用）。</summary>
        internal static void Reset()
        {
            if (_tex != null)
            {
                UnityEngine.Object.Destroy(_tex);
                _tex = null;
            }
            _builtFrom = null;
            _builtCount = -1;
        }

        private static void Build(ReplayAnalyzer.Report rep, object source)
        {
            if (_tex == null)
            {
                _tex = new Texture2D(W, H, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,   // 柱状图不能模糊
                    wrapMode = TextureWrapMode.Clamp,
                };
            }

            var px = new Color32[W * H];
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = Bg;
            }

            int mid = H / 2;
            for (int x = 0; x < W; x++)
            {
                px[mid * W + x] = Axis;
            }

            int n = rep.SamplesMs.Count;
            // 样本多于像素时抽稀：每 step 个取一个，保证柱宽至少 1px
            int step = Math.Max(1, (n + W - 1) / W);
            int drawn = (n + step - 1) / step;
            int barW = Math.Max(1, W / Math.Max(1, drawn));

            int k = 0;
            for (int i = 0; i < n; i += step)
            {
                float ms = Mathf.Clamp(rep.SamplesMs[i], -WindowMs, WindowMs);
                float abs = Math.Abs(ms);
                int barH = Math.Max(1, (int)(H / 2f * (abs / WindowMs)));

                float pwin = i < rep.PerfectWinMs.Count ? rep.PerfectWinMs[i] : 0f;
                float qwin = i < rep.PureWinMs.Count ? rep.PureWinMs[i] : 0f;
                Color32 c = pwin > 0f && qwin > 0f
                    ? (abs <= pwin ? Good : abs <= qwin ? Warn : Bad)
                    : (abs <= 25f ? Good : abs <= 60f ? Warn : Bad);

                // 柱心对齐到该样本的横向位置，负值向下画、正值向上画
                int x0 = (int)(W * ((k + 0.5f) / Math.Max(1, drawn))) - barW / 2;
                int y0 = ms >= 0f ? mid : mid - barH;
                int xEnd = Math.Min(W, x0 + barW);
                int yEnd = Math.Min(H, y0 + barH);
                for (int x = Math.Max(0, x0); x < xEnd; x++)
                {
                    for (int y = Math.Max(0, y0); y < yEnd; y++)
                    {
                        px[y * W + x] = c;
                    }
                }
                k++;
            }

            _tex.SetPixels32(px);
            _tex.Apply(false, false);
            _builtFrom = source;
            _builtCount = n;
        }
    }
}
