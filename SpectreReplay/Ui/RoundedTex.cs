using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 程序化生成的圆角矩形贴图 + 缓存。
    ///
    /// **为什么需要它。** UMM 把设置页交给我们时，皮肤里的 button / slider / box
    /// 全是 Unity 默认那套方角贴图（UMM 的 ScaleGUI 只改它们的 padding / fixedHeight，
    /// 从不换贴图），所以光靠改颜色永远做不出圆角——圆角必须由贴图本身带。
    /// 参考 ADOFAIMacro 的 Material 3 观感（**只参考思路，未复制其代码**：
    /// 那份是 AGPL-3.0，本项目无 LICENSE，抄代码会有许可问题）。
    ///
    /// **两个关键取舍：**
    ///
    /// 1. **用 9-slice（<see cref="GUIStyle.border"/>）而不是直接拉伸。**
    ///    直接把一个 64×64 的圆角贴图拉进 200×28 的按钮里，圆角会被横向拉成
    ///    椭圆（横向 ×3.1、纵向 ×0.44），看着像两头尖的胶囊而不是圆角矩形。
    ///    设了 border 之后 Unity 只拉伸中间那一段，四角按原始像素画，圆角恒定。
    ///
    /// 2. **贴图必须缓存，且尺寸要可控。** 每帧 new Texture2D 会持续泄漏显存
    ///    （Unity 的贴图不受 GC 管理）。缓存键含尺寸/半径/颜色/圆角组合，
    ///    命中就复用。所以调用方应当传**固定小尺寸**（如 64×64）再由 9-slice
    ///    撑开，而不是按控件实际大小生成——后者会让缓存随窗口尺寸无限增长。
    ///
    /// 贴图带 <see cref="HideFlags.HideAndDontSave"/>：切场景时不被销毁
    /// （销毁了缓存里就是一堆假 null，下一帧全部重建）。
    /// </summary>
    internal static class RoundedTex
    {
        /// <summary>缓存键。手写 Equals/GetHashCode：Color 参与默认结构相等会走反射，太慢。</summary>
        private struct Key : IEquatable<Key>
        {
            internal int W, H, R;
            internal uint Col;      // RGBA 各 8 位打包
            internal byte Corners;  // bit0=左上 bit1=右上 bit2=左下 bit3=右下

            public bool Equals(Key o)
                => W == o.W && H == o.H && R == o.R && Col == o.Col && Corners == o.Corners;

            public override bool Equals(object o) => o is Key k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = W * 397;
                    h = h * 397 ^ H;
                    h = h * 397 ^ R;
                    h = h * 397 ^ (int)Col;
                    h = h * 397 ^ Corners;
                    return h;
                }
            }
        }

        private static readonly Dictionary<Key, Texture2D> Cache = new Dictionary<Key, Texture2D>();

        private static int To8(float v) => Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        /// <summary>把颜色打包成 32 位整数（只用于缓存键，不参与绘制）。</summary>
        private static uint Pack(Color c)
            => (uint)(To8(c.r) | (To8(c.g) << 8) | (To8(c.b) << 16) | (To8(c.a) << 24));

        /// <summary>
        /// 取一张圆角矩形贴图。四个 <c>bool</c> 决定哪几个角要圆——
        /// 分段控件/滑条填充这类"只圆左边两个角"的形状靠它。
        /// </summary>
        internal static Texture2D Get(int w, int h, int radius, Color c,
            bool tl = true, bool tr = true, bool bl = true, bool br = true)
        {
            w = Mathf.Clamp(w, 2, 512);
            h = Mathf.Clamp(h, 2, 512);
            // 半径不能超过短边的一半：超了相邻两个圆角会互相重叠，形状会崩。
            radius = Mathf.Clamp(radius, 0, Mathf.Min(w, h) / 2);

            byte corners = (byte)((tl ? 1 : 0) | (tr ? 2 : 0) | (bl ? 4 : 0) | (br ? 8 : 0));
            var key = new Key { W = w, H = h, R = radius, Col = Pack(c), Corners = corners };
            if (Cache.TryGetValue(key, out Texture2D hit) && hit != null)
            {
                return hit;
            }

            Texture2D tex = Build(w, h, radius, c, tl, tr, bl, br);
            Cache[key] = tex;
            return tex;
        }

        private static Texture2D Build(int w, int h, int r, Color c,
            bool tl, bool tr, bool bl, bool br)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var pix = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float cx, cy;
                    if (!CornerCenter(x, y, w, h, r, tl, tr, bl, br, out cx, out cy))
                    {
                        pix[y * w + x] = c;
                        continue;
                    }
                    // 到圆心的距离 → 覆盖率。半径外全透明、边界上按 0.5px 过渡，
                    // 这样 1×1 的锯齿被抹掉（不抗锯齿的圆角在深色底上像被啃过）。
                    float dx = cx - x, dy = cy - y;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(r + 0.5f - d);
                    pix[y * w + x] = new Color(c.r, c.g, c.b, c.a * a);
                }
            }

            tex.SetPixels(pix);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>
        /// 像素落在哪个圆角区域里；是圆角则输出该角的圆心。
        /// 返回 false = 不在任何圆角区域，照实心画。
        ///
        /// 坐标按 Unity 贴图约定：y=0 是**下**边。
        /// </summary>
        private static bool CornerCenter(int x, int y, int w, int h, int r,
            bool tl, bool tr, bool bl, bool br, out float cx, out float cy)
        {
            cx = 0f;
            cy = 0f;
            if (r <= 0)
            {
                return false;
            }
            bool left = x < r;
            bool right = x >= w - r;
            bool bottom = y < r;
            bool top = y >= h - r;

            if (left && bottom) { cx = r; cy = r; return bl; }
            if (right && bottom) { cx = w - 1 - r; cy = r; return br; }
            if (left && top) { cx = r; cy = h - 1 - r; return tl; }
            if (right && top) { cx = w - 1 - r; cy = h - 1 - r; return tr; }
            return false;
        }
    }
}