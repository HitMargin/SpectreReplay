namespace SpectreReplay.Features.VideoRender;

internal sealed class SystemCursorImage
{
    internal int Width;
    internal int Height;
    /// <summary>热点（箭尖）在位图里的坐标，原点左上、y 向下。</summary>
    internal int HotspotX;
    internal int HotspotY;
    /// <summary>长度 = W*H*4，字节序 R,G,B,A，预乘 alpha。</summary>
    internal byte[] Rgba;
}
