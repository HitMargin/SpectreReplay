using System.Collections.Generic;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 按歌曲时间查指针轨迹：二分定位样本区间、线性插值、首前/尾后夹取。
/// 纯静态、不依赖 Unity——渲染每帧查一次，离线闸门也可以直接验它。
/// </summary>
internal static class CursorTrackSampler
{
    /// <summary>
    /// 取 songPosition 时刻的归一化指针坐标。轨迹为空返回 false；
    /// 时间在首样本之前/尾样本之后取端点值（录制开始前指针在哪没记过，
    /// 夹取是最不惊讶的语义）。
    /// </summary>
    public static bool Sample(IReadOnlyList<CursorSample> track, double songPosition, out float x, out float y)
    {
        x = 0f;
        y = 0f;
        if (track == null || track.Count == 0) return false;

        int last = track.Count - 1;
        if (songPosition <= track[0].SongPosition)
        {
            x = track[0].X;
            y = track[0].Y;
            return true;
        }
        if (songPosition >= track[last].SongPosition)
        {
            x = track[last].X;
            y = track[last].Y;
            return true;
        }

        // 不变量：track[lo].SongPosition <= songPosition < track[hi].SongPosition
        int lo = 0, hi = last;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (track[mid].SongPosition <= songPosition) lo = mid;
            else hi = mid;
        }

        var a = track[lo];
        var b = track[hi];
        double span = b.SongPosition - a.SongPosition;
        // 同时刻的两个样本（理论上不会出现，防除零兜底）：取后者
        if (span <= 0d)
        {
            x = b.X;
            y = b.Y;
            return true;
        }
        double t = (songPosition - a.SongPosition) / span;
        x = (float)(a.X + (b.X - a.X) * t);
        y = (float)(a.Y + (b.Y - a.Y) * t);
        return true;
    }
}
