using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SpectreReplay.Ui.Timeline;

/// <summary>
/// 运行时生成圆角矩形贴图（4x 超采样 + 抗锯齿边），
/// 配合 Image.type=Sliced 九宫格拉伸，替代默认的方角 Image。
/// 参考 StutterFix.UiDraw.RoundSprite 的实现方式。
/// </summary>
internal static class RoundedSprite
{
    private static readonly Dictionary<int, Sprite> _fill = new();
    private static readonly Dictionary<int, Sprite> _outline = new();
    private const int Supersample = 4;

    /// <summary>获取圆角填充矩形 Sprite。key = 半径 × 2（与 StutterFix 一致）。</summary>
    internal static Sprite Fill(int radiusKey)
    {
        if (!_fill.TryGetValue(radiusKey, out var s) || s == null)
        {
            s = Make(radiusKey, outline: false);
            _fill[radiusKey] = s;
        }
        return s;
    }

    /// <summary>获取圆角描边矩形 Sprite。</summary>
    internal static Sprite Outline(int radiusKey)
    {
        if (!_outline.TryGetValue(radiusKey, out var s) || s == null)
        {
            s = Make(radiusKey, outline: true);
            _outline[radiusKey] = s;
        }
        return s;
    }

    private static Sprite Make(int key, bool outline)
    {
        float radius = key / 2f * Supersample;
        int half = Mathf.CeilToInt(radius);
        int texSize = half * 2 + 2;
        var tex = new Texture2D(texSize, texSize, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };
        var pixels = new Color32[texSize * texSize];
        float center = texSize / 2f;
        float ss = Supersample;
        for (int y = 0; y < texSize; y++)
        {
            for (int x = 0; x < texSize; x++)
            {
                float dx = Mathf.Max(Mathf.Abs(x + 0.5f - center) - (center - radius), 0f);
                float dy = Mathf.Max(Mathf.Abs(y + 0.5f - center) - (center - radius), 0f);
                float d = Mathf.Sqrt(dx * dx + dy * dy) - radius;
                float alpha = Mathf.Clamp01(0.5f - d);
                if (outline)
                    alpha *= Mathf.Clamp01(d + ss + 0.5f);
                pixels[y * texSize + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        float border = half;
        return Sprite.Create(tex, new Rect(0, 0, texSize, texSize), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
            new Vector4(border, border, border, border));
    }

    /// <summary>给 Image 设置圆角填充。</summary>
    internal static void ApplyFill(Image img, float radius)
    {
        int key = Mathf.Max(1, Mathf.RoundToInt(radius * 2f));
        img.sprite = Fill(key);
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = Supersample;
        img.fillCenter = true;
    }

    /// <summary>给 Image 设置圆角描边。</summary>
    internal static void ApplyOutline(Image img, float radius)
    {
        int key = Mathf.Max(1, Mathf.RoundToInt(radius * 2f));
        img.sprite = Outline(key);
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = Supersample;
        img.fillCenter = false;
    }
}
