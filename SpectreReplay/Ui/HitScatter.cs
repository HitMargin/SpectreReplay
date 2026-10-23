using System;
using UnityEngine;
using SpectreReplay.Features.Replay;

namespace SpectreReplay.Ui
{
    internal static class HitScatter
    {
        private const int W = 512;      // 足够细：一屏最多画 512 根柱，再密也看不出
        private const int H = 120;
        /// <summary>
        /// 纵轴满量程 = 偏离较紧窗口的**倍数**。2.0 = 偏差达到窗口 2 倍时顶到边缘。
        /// 取 2 的理由：一倍左右是「还判得进」的边界，再往外基本是明显失误，
        /// 二者要能分开；但也不该让 1.2 倍就顶格（那样看不出轻重）。
        /// </summary>
        private const float FullScale = 2.0f;

        private static Texture2D _tex;
        private static object _builtFrom;
        private static int _builtCount = -1;

        // 配色（与 Gui 的语义色一致：绿=好、黄=中、红=差）
        private static readonly Color32 Bg = new Color32(20, 22, 28, 255);
        private static readonly Color32 Axis = new Color32(120, 120, 120, 255);
        private static readonly Color32 Good = new Color32(64, 217, 115, 255);
        private static readonly Color32 Warn = new Color32(242, 204, 51, 255);
        private static readonly Color32 Bad = new Color32(230, 77, 77, 255);

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
            int step = Math.Max(1, (n + W - 1) / W);
            int drawn = (n + step - 1) / step;
            int barW = Math.Max(1, W / Math.Max(1, drawn));

            // 传数组而不是逐项取：交给纯数学层判「这一格画哪个样本」。
            var samples = rep.SamplesMs.ToArray();
            var perfect = rep.PerfectWinMs.ToArray();
            var pure = rep.PureWinMs.ToArray();

            for (int k = 0; k < drawn; k++)
            {
                // 每格取 |ms| 最大的那个（原实现取每格第一个，丢了三分之二样本）。
                int pick = HitScatterMath.PickExtreme(samples, k * step, (k + 1) * step);
                if (pick < 0) break;

                float ms = samples[pick];
                float abs = Math.Abs(ms);

                float pwin = pick < perfect.Length ? perfect[pick] : 0f;
                float qwin = pick < pure.Length ? pure[pick] : 0f;
                HitScatterMath.Windows(pwin, qwin, out float tight, out float wide);

                int barH = HitScatterMath.BarHeight(abs, tight, mid, FullScale);

                // 颜色与高度同源：都在「窗口倍数」这一把尺子上。
                HitScatterMath.Band band = HitScatterMath.Classify(abs, tight, wide);
                Color32 c = band == HitScatterMath.Band.Best ? Good
                    : band == HitScatterMath.Band.Middle ? Warn : Bad;

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
            }

            _tex.SetPixels32(px);
            _tex.Apply(false, false);
            _builtFrom = source;
            _builtCount = n;
        }
    }
}