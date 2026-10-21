using System;

namespace SpectreReplay.Features.VideoRender;

/// <summary>
/// 把鼠标指针画进已回读的 RGBA32 帧缓冲（原地改像素）。
///
/// **为什么在像素层画，而不是在屏幕上放一个假光标。** 两种画面源都要覆盖：
/// CleanCamera 把三台游戏相机劫持到自建纹理（拍不到任何屏幕空间的东西），
/// WholeScreen 截的是最终屏幕合成。只有落到回读后的像素上，两边才会一致地带上指针；
/// 挂一个 Sprite/IMGUI 假光标，CleanCamera 根本看不见它。
///
/// **为什么指针位置要跟着帧走。** AsyncGPUReadback 是异步的：<c>Grab</c> 之后要两三帧
/// 才拿得到像素。若在交付时才现读 <c>Input.mousePosition</c>，画进去的是两三帧之后的
/// 位置，快速移动会明显甩尾。所以位置在 Grab 那一刻采样、随该帧一起排队。
///
/// **行序。** Unity 纹理回读的行首在**下**，视频要求行首在上。编码器只对
/// CleanCamera 做 vflip（见 <c>VideoFrameSource.NeedsRowFlip</c>），WholeScreen 截出来
/// 已经是上。所以 <paramref name="rowsBottomUp"/> 直接照 NeedsRowFlip 传。
/// </summary>
internal static class CursorOverlay
{
    // 经典箭头，单位是"设计像素"：热点 (0,0) 在箭尖，x 向右、y 向下。
    private static readonly float[] ShapeX = { 0f, 0f, 4f, 7f, 9f, 6f, 11f };
    private static readonly float[] ShapeY = { 0f, 16f, 12f, 18f, 17f, 11f, 11f };

    private const float DesignHeight = 18f;
    private const float DesignWidth = 11f;
    private const float Outline = 1f;

    /// <summary>
    /// 在 <paramref name="pixels"/> 上画一个箭头，箭尖落在 <paramref name="mouseX"/> /
    /// <paramref name="mouseY"/>。<paramref name="mouseX"/> 以屏幕左下为原点
    /// （即 <c>Input.mousePosition</c> 的坐标系），内部按纹理尺寸换算。
    /// </summary>
    internal static void Draw(byte[] pixels, int width, int height, bool rowsBottomUp,
        float mouseX, float mouseY, float screenWidth, float screenHeight)
    {
        if (pixels == null || width <= 0 || height <= 0) return;
        if (pixels.Length < width * height * 4) return;
        if (screenWidth <= 0f || screenHeight <= 0f) return;

        // 指针按输出高度等比缩放：1080p 上约 27 px，4K 上约 54 px；
        // 低于 720p 也不再缩小，否则会糊成一个点
        float scale = Math.Max(1f, height / 720f);
        float spriteW = DesignWidth * scale;
        float spriteH = DesignHeight * scale;

        // 屏幕坐标（左下原点）→ 纹理坐标。WholeScreen 两者相等；
        // CleanCamera 的输出分辨率与窗口无关，必须按比例缩
        float ox = mouseX * (width / screenWidth);
        float oy = mouseY * (height / screenHeight);

        // 指针整体在热点下方（屏幕 y 更小）、右侧
        int x0 = Math.Max(0, (int)Math.Floor(ox - 1f));
        int x1 = Math.Min(width - 1, (int)Math.Ceiling(ox + spriteW + 1f));
        int y0 = Math.Max(0, (int)Math.Floor(oy - spriteH - 1f));
        int y1 = Math.Min(height - 1, (int)Math.Ceiling(oy + 1f));
        if (x0 > x1 || y0 > y1) return;

        for (int sy = y0; sy <= y1; sy++)
        {
            int row = rowsBottomUp ? sy : height - 1 - sy;
            if (row < 0 || row >= height) continue;
            int rowBase = row * width * 4;
            for (int sx = x0; sx <= x1; sx++)
            {
                // 4x4 超采样：箭头边缘要抗锯齿，否则在 1080p 上是一排锯齿
                int ink = 0, edge = 0;
                for (int j = 0; j < 4; j++)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        float px = sx + (i + 0.5f) / 4f;
                        float py = sy + (j + 0.5f) / 4f;
                        float dx = (px - ox) / scale;
                        float dy = (oy - py) / scale;
                        if (Inside(dx, dy))
                            ink++;
                        else if (NearEdge(dx, dy))
                            edge++;
                    }
                }
                int inked = ink + edge;
                if (inked == 0) continue;

                float coverage = inked / 16f;
                float whiteShare = ink / (float)inked;
                byte tone = (byte)(255f * whiteShare);
                int idx = rowBase + sx * 4;
                float inv = 1f - coverage;
                pixels[idx] = (byte)(pixels[idx] * inv + tone * coverage);
                pixels[idx + 1] = (byte)(pixels[idx + 1] * inv + tone * coverage);
                pixels[idx + 2] = (byte)(pixels[idx + 2] * inv + tone * coverage);
                // 视频是 yuv420，没有 alpha 通道，写满更省得编码器猜
                pixels[idx + 3] = 255;
            }
        }
    }

    /// <summary>
    /// 用抓下来的系统指针位图取代手绘箭头：热点定位、双线性缩放、预乘 alpha 合成。
    ///
    /// **为什么按 width/screenWidth 而不是 height/720 缩放。** 位图是系统按当前 DPI
    /// 和「光标大小」渲染出来的物理像素；用户在屏幕上看到的指针就是这个像素尺寸。
    /// 成片分辨率与窗口不一致时（CleanCamera 可任意分辨率），只有按「纹理像素 /
    /// 屏幕像素」换算，指针在成片里的相对大小才和现场看到的一致。
    ///
    /// **预乘 alpha 只能乘一次。** 位图落下来已经是 src*a，这里只做
    /// out = src + dst*(1-a)；再乘一次 alpha 会让边缘发黑（见 SystemCursorImage）。
    /// </summary>
    internal static void Draw(byte[] pixels, int width, int height, bool rowsBottomUp,
        SystemCursorImage cursor, float mouseX, float mouseY, float screenWidth, float screenHeight)
    {
        if (cursor == null || cursor.Rgba == null) return;
        if (pixels == null || width <= 0 || height <= 0) return;
        if (pixels.Length < width * height * 4) return;
        if (screenWidth <= 0f || screenHeight <= 0f) return;
        int cw = cursor.Width;
        int ch = cursor.Height;
        if (cw <= 0 || ch <= 0) return;
        if (cursor.Rgba.Length < cw * ch * 4) return;

        float sx = width / screenWidth;
        float sy = height / screenHeight;
        if (sx <= 0f || sy <= 0f) return;

        // 位图左上角在纹理坐标（左下原点）里的位置：热点对齐指针位置
        float left = (mouseX - cursor.HotspotX) * sx;
        float top = (mouseY + cursor.HotspotY) * sy;

        int x0 = Math.Max(0, (int)Math.Floor(left));
        int x1 = Math.Min(width - 1, (int)Math.Ceiling(left + cw * sx));
        int y0 = Math.Max(0, (int)Math.Floor(top - ch * sy));
        int y1 = Math.Min(height - 1, (int)Math.Ceiling(top));
        if (x0 > x1 || y0 > y1) return;

        byte[] src = cursor.Rgba;
        for (int row = y0; row <= y1; row++)
        {
            int dstRow = rowsBottomUp ? row : height - 1 - row;
            if (dstRow < 0 || dstRow >= height) continue;
            int rowBase = dstRow * width * 4;
            for (int col = x0; col <= x1; col++)
            {
                // 目标像素中心（左下原点）→ 位图坐标（原点左上、y 向下）
                float u = (col + 0.5f - left) / sx;
                float v = (top - (row + 0.5f)) / sy;
                if (u <= 0f || v <= 0f || u >= cw || v >= ch) continue;

                float fu = u - 0.5f;
                float fv = v - 0.5f;
                int u0 = (int)Math.Floor(fu);
                int v0 = (int)Math.Floor(fv);
                float tu = fu - u0;
                float tv = fv - v0;

                float r = 0f, g = 0f, b = 0f, a = 0f;
                Accumulate(src, cw, ch, u0, v0, (1f - tu) * (1f - tv), ref r, ref g, ref b, ref a);
                Accumulate(src, cw, ch, u0 + 1, v0, tu * (1f - tv), ref r, ref g, ref b, ref a);
                Accumulate(src, cw, ch, u0, v0 + 1, (1f - tu) * tv, ref r, ref g, ref b, ref a);
                Accumulate(src, cw, ch, u0 + 1, v0 + 1, tu * tv, ref r, ref g, ref b, ref a);
                if (a < 0.5f) continue;

                int idx = rowBase + col * 4;
                float inv = 1f - a / 255f;
                if (inv < 0f) inv = 0f;
                pixels[idx] = ClampByte(pixels[idx] * inv + r);
                pixels[idx + 1] = ClampByte(pixels[idx + 1] * inv + g);
                pixels[idx + 2] = ClampByte(pixels[idx + 2] * inv + b);
                // 视频是 yuv420，没有 alpha 通道，写满更省得编码器猜
                pixels[idx + 3] = 255;
            }
        }
    }

    // 预乘 alpha 的双线性取样：权重只按几何覆盖算，颜色/alpha 一起插值
    private static void Accumulate(byte[] src, int cw, int ch, int u, int v, float w,
        ref float r, ref float g, ref float b, ref float a)
    {
        if (w <= 0f) return;
        if (u < 0 || u >= cw || v < 0 || v >= ch) return;
        int i = (v * cw + u) * 4;
        r += src[i] * w;
        g += src[i + 1] * w;
        b += src[i + 2] * w;
        a += src[i + 3] * w;
    }

    private static byte ClampByte(float value)
    {
        if (value <= 0f) return 0;
        if (value >= 255f) return 255;
        return (byte)value;
    }

    private static bool Inside(float x, float y)
    {
        bool inside = false;
        int n = ShapeX.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if ((ShapeY[i] > y) != (ShapeY[j] > y) &&
                x < (ShapeX[j] - ShapeX[i]) * (y - ShapeY[i]) / (ShapeY[j] - ShapeY[i]) + ShapeX[i])
                inside = !inside;
        }
        return inside;
    }

    private static bool NearEdge(float x, float y)
    {
        int n = ShapeX.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if (SegmentDistance(x, y, ShapeX[j], ShapeY[j], ShapeX[i], ShapeY[i]) <= Outline)
                return true;
        }
        return false;
    }

    private static float SegmentDistance(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float lenSq = dx * dx + dy * dy;
        float t = lenSq <= 0f ? 0f : ((px - ax) * dx + (py - ay) * dy) / lenSq;
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;
        float cx = ax + t * dx, cy = ay + t * dy;
        float ex = px - cx, ey = py - cy;
        return (float)Math.Sqrt(ex * ex + ey * ey);
    }
}