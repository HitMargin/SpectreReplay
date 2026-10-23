namespace SpectreReplay.Features.Replay;

/// <summary>
/// 指针轨迹的一个采样点。
/// 坐标在录制时按当时的 Screen 尺寸归一化（左下原点、y 向上，0~1），
/// 渲染时乘回输出尺寸——成片分辨率、窗口大小怎么变都跟得上。
/// </summary>
public class CursorSample
{
    /// <summary>采样时刻的歌曲位置（songposition_minusi，倒计时期间为负）。</summary>
    public double SongPosition;

    public float X;

    public float Y;
}
