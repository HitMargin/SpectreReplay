using System;
using System.Runtime.InteropServices;

namespace SpectreReplay.Features.VideoRender;

/// <summary>
/// 抓「用户此刻真正在用的系统指针」位图，供 <see cref="CursorOverlay"/> 画进视频帧。
///
/// **为什么不用自绘箭头。** 用户要的是和桌面上一模一样的指针：他换过的主题、
/// 系统光标大小、抗锯齿边缘。自绘多边形永远只是示意；<c>Input.mousePosition</c>
/// 只给位置，不给外观，外观只能从系统取。
///
/// **两条取图路径，都验 alpha。**
/// <list type="number">
/// <item><c>GetDIBits(hbmColor)</c>：现代主题的指针是 32 位 ARGB，直读最忠实，
///   连半透明阴影都在。</item>
/// <item><c>DrawIconEx</c> 到全零 DIB：直读拿不到（位深不足、alpha 全零）时兜底，
///   让 GDI 自己做 mask/alpha 合成。</item>
/// </list>
/// 两条都拿不到有效 alpha 就放弃、回退到手绘箭头。单色光标（<c>hbmColor</c> 为空）
/// 靠 AND/XOR 掩码反转背景显形，没有可用的颜色与 alpha，直接放弃。
///
/// **缓存按 HCURSOR 句柄。** 位图只在「换成另一种指针」时变（箭头→文本→手型），
/// 同一句柄每帧重建是纯浪费。动画光标（.ani）同一句柄下换帧，这里停在抓到的
/// 那一帧——视频里要的是稳定指针，不是闪烁。
/// </summary>
internal static class SystemCursor
{
    private static IntPtr cachedHandle;
    private static SystemCursorImage cached;
    private static bool loggedOnce;

    // 工程 DefineConstants 只有 DEBUG;TRACE（见 csproj）——mod 由独立 csproj 编译，
    // 不吃 Unity 的 UNITY_STANDALONE_WIN 宏。所以平台判断必须在运行时做：
    // 用 #if 会永远走进空分支，光标静默失效；而直接调 P/Invoke 会在 Mac 上抛
    // DllNotFoundException。
    private static bool IsWindowsPlatform()
    {
        UnityEngine.RuntimePlatform p = UnityEngine.Application.platform;
        return p == UnityEngine.RuntimePlatform.WindowsPlayer
            || p == UnityEngine.RuntimePlatform.WindowsEditor;
    }

    /// <summary>
    /// 取当前系统指针位图。返回 false 表示取不到（非 Windows、无鼠标、单色光标、
    /// GDI 失败），调用方应回退到手绘箭头。
    /// **必须主线程调用**：GDI 句柄与 <c>GetCursorInfo</c> 都绑定窗口站。
    /// </summary>
    internal static bool TryAcquire(out SystemCursorImage image)
    {
        image = null;
        if (!IsWindowsPlatform()) return false;

        CURSORINFO info = default;
        info.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
        if (!GetCursorInfo(ref info)) return false;
        // flags 的 CURSOR_SHOWING 位为 0 说明指针被隐藏（全屏视频、游戏自绘光标），
        // 这时候画上去反而是凭空多一个箭头
        if (info.hCursor == IntPtr.Zero) return false;
        if ((info.flags & CURSOR_SHOWING) == 0) return false;

        if (info.hCursor == cachedHandle && cached != null)
        {
            image = cached;
            return true;
        }

        SystemCursorImage built = Build(info.hCursor);
        if (built == null) return false;

        cachedHandle = info.hCursor;
        cached = built;
        image = built;
        if (!loggedOnce)
        {
            loggedOnce = true;
            UnityEngine.Debug.Log("[SpectreReplay] system cursor captured: "
                + built.Width + "x" + built.Height
                + " hotspot=(" + built.HotspotX + "," + built.HotspotY + ")");
        }
        return true;
    }

    private static SystemCursorImage Build(IntPtr hCursor)
    {
        ICONINFO icon;
        if (!GetIconInfo(hCursor, out icon)) return null;
        try
        {
            // 单色光标没有 ARGB 位图，见类注释
            if (icon.hbmColor == IntPtr.Zero) return null;

            int w, h;
            if (!Measure(icon, out w, out h)) return null;
            // 上限防呆：坏主题会返回离谱尺寸，别为它分配几百 MB
            if (w <= 0 || h <= 0 || w > 512 || h > 512) return null;

            int byteCount = w * h * 4;
            byte[] rgba = null;

            // 路径 1：直读颜色位图
            byte[] direct = ReadColorBitmap(icon.hbmColor, w, h);
            if (direct != null && HasVisibleAlpha(direct)) rgba = direct;

            // 路径 2：让 GDI 合成一次
            if (rgba == null)
            {
                byte[] blended = DrawWithGdi(hCursor, w, h);
                if (blended != null && HasVisibleAlpha(blended)) rgba = blended;
            }

            if (rgba == null) return null;

            return new SystemCursorImage
            {
                Width = w,
                Height = h,
                HotspotX = icon.xHotspot,
                HotspotY = icon.yHotspot,
                Rgba = rgba,
            };
        }
        finally
        {
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
        }
    }

    // 尺寸以图标自己的位图为准：用户的 DPI 与「光标大小」设置已经体现在里面。
    // GetSystemMetrics(SM_CXCURSOR) 只给标称值，画出来会偏小
    private static bool Measure(ICONINFO icon, out int w, out int h)
    {
        w = 0;
        h = 0;
        BITMAP bm = default;
        int size = Marshal.SizeOf(typeof(BITMAP));
        if (icon.hbmColor != IntPtr.Zero
            && GetObject(icon.hbmColor, size, ref bm) > 0
            && bm.bmWidth > 0 && bm.bmHeight > 0)
        {
            w = bm.bmWidth;
            h = bm.bmHeight;
            return true;
        }
        if (icon.hbmMask != IntPtr.Zero
            && GetObject(icon.hbmMask, size, ref bm) > 0
            && bm.bmWidth > 0 && bm.bmHeight >= 2)
        {
            // 掩码是 AND+XOR 上下拼在一起的，高度是光标的两倍
            w = bm.bmWidth;
            h = bm.bmHeight / 2;
            return true;
        }
        w = GetSystemMetrics(SM_CXCURSOR);
        h = GetSystemMetrics(SM_CYCURSOR);
        return w > 0 && h > 0;
    }

    // ── 路径 1：GetDIBits 直读，BGRA → 预乘 RGBA ────────────────────────
    private static byte[] ReadColorBitmap(IntPtr hbmColor, int w, int h)
    {
        BITMAP bm = default;
        if (GetObject(hbmColor, Marshal.SizeOf(typeof(BITMAP)), ref bm) <= 0) return null;
        // 非 32 位没有 alpha 通道，交给路径 2
        if (bm.bmBitsPixel != 32) return null;

        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        IntPtr bits = IntPtr.Zero;
        try
        {
            BITMAPINFOHEADER header = default;
            header.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            header.biWidth = w;
            // 负高度 = 自上而下存行
            header.biHeight = -h;
            header.biPlanes = 1;
            header.biBitCount = 32;
            header.biCompression = BI_RGB;

            int byteCount = w * h * 4;
            bits = Marshal.AllocHGlobal(byteCount);
            if (GetDIBits(dc, hbmColor, 0, (uint)h, bits, ref header, DIB_RGB_COLORS) != h)
                return null;

            byte[] bgra = new byte[byteCount];
            Marshal.Copy(bits, bgra, 0, byteCount);
            return ToPremultipliedRgba(bgra);
        }
        finally
        {
            if (bits != IntPtr.Zero) Marshal.FreeHGlobal(bits);
            DeleteDC(dc);
        }
    }

    // ── 路径 2：DrawIconEx 到全零 DIB，结果本身就是预乘的 ────────────────
    private static byte[] DrawWithGdi(IntPtr hCursor, int w, int h)
    {
        IntPtr screenDc = CreateCompatibleDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        IntPtr memDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return null;

            BITMAPINFOHEADER header = default;
            header.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            header.biWidth = w;
            header.biHeight = -h;
            header.biPlanes = 1;
            header.biBitCount = 32;
            header.biCompression = BI_RGB;

            IntPtr bits;
            dib = CreateDIBSection(memDc, ref header, DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            oldObj = SelectObject(memDc, dib);

            // 必须全零：DrawIconEx 往目标上叠，残留内容会漏进 alpha 通道。
            // 用 Marshal.Copy 而不是 unsafe：插件工程没开 AllowUnsafeBlocks
            int byteCount = w * h * 4;
            Marshal.Copy(new byte[byteCount], 0, bits, byteCount);

            if (!DrawIconEx(memDc, 0, 0, hCursor, w, h, 0, IntPtr.Zero, DI_NORMAL))
                return null;

            byte[] bgra = new byte[byteCount];
            Marshal.Copy(bits, bgra, 0, byteCount);
            // DrawIconEx 的结果已经是预乘的（目标全零），只需换通道序
            return ToRgbaInPlace(bgra);
        }
        finally
        {
            if (memDc != IntPtr.Zero)
            {
                if (oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
                DeleteDC(memDc);
            }
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(screenDc);
        }
    }

    // 直读出来的颜色是**非**预乘的（ARGB 直存），这里顺手乘掉
    private static byte[] ToPremultipliedRgba(byte[] bgra)
    {
        int n = bgra.Length;
        byte[] rgba = new byte[n];
        for (int i = 0; i < n; i += 4)
        {
            byte b = bgra[i];
            byte g = bgra[i + 1];
            byte r = bgra[i + 2];
            byte a = bgra[i + 3];
            if (a != 255 && a != 0)
            {
                r = (byte)((r * a + 127) / 255);
                g = (byte)((g * a + 127) / 255);
                b = (byte)((b * a + 127) / 255);
            }
            else if (a == 0)
            {
                r = g = b = 0;
            }
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = a;
        }
        return rgba;
    }

    private static byte[] ToRgbaInPlace(byte[] bgra)
    {
        int n = bgra.Length;
        byte[] rgba = new byte[n];
        for (int i = 0; i < n; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }
        return rgba;
    }

    private static bool HasVisibleAlpha(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 0) return true;
        return false;
    }

    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;
    private const int DI_NORMAL = 0x0003;
    private const int SM_CXCURSOR = 13;
    private const int SM_CYCURSOR = 14;
    private const int CURSOR_SHOWING = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public short bmPlanes;
        public short bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi,
        uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        IntPtr lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);
}
