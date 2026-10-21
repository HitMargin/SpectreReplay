namespace SpectreReplay.Features.VideoRender;

/// <summary>
/// 一次抓取下来的系统指针位图：热点 + **预乘 alpha** 的 RGBA 像素。
///
/// **为什么单独成文件。** 这是纯数据（只有 int 和 byte[]），不碰 UnityEngine，
/// 所以 tools/UiPreview 能把它连同 <see cref="CursorOverlay"/> 一起链接进离线闸门，
/// 用合成位图真的验一遍预乘合成。抓取端 <see cref="SystemCursor"/> 依赖
/// UnityEngine.Application 做运行时平台判断，那部分离线跑不了。
///
/// **为什么是预乘 alpha。** 抓取走 <c>AlphaBlend</c> 语义（<c>DrawIconEx</c> 的
/// <c>DI_NORMAL</c>），目标是全零的 DIB，落下来就是 <c>dst = src*a + 0</c>，
/// 颜色已经乘过 alpha。合成时按 <c>out = srcPm + dst*(1-a)</c> 走，
/// **不要**再乘一次 alpha，否则边缘会出现明显暗边。
/// </summary>
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
