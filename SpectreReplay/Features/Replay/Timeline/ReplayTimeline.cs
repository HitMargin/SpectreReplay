using System;
using System.Collections.Generic;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 时间轴的只读索引。歌曲时间是唯一主时间，按键和楼层只提供查询入口。
/// </summary>
internal sealed class ReplayTimelineIndex
{
    private readonly int[] _keyOrder;
    private readonly double[] _keyTimes;
    private readonly double[] _floorTimes;

    internal int KeyCount => _keyOrder.Length;
    internal int FloorCount => _floorTimes.Length;
    internal IReadOnlyList<double> KeyTimes => _keyTimes;
    internal IReadOnlyList<double> FloorTimes => _floorTimes;
    internal double Duration { get; }

    internal ReplayTimelineIndex(ReplayData source, IReadOnlyList<double> floorTimes)
    {
        var keys = source?.KeyEvents ?? new List<KeyEvent>();
        var entries = new List<TimelineKey>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            double time = keys[i]?.SongPosition ?? 0.0;
            if (double.IsNaN(time) || double.IsInfinity(time))
                continue;
            entries.Add(new TimelineKey(time, i));
        }

        entries.Sort((a, b) =>
        {
            int byTime = a.Time.CompareTo(b.Time);
            return byTime != 0 ? byTime : a.Index.CompareTo(b.Index);
        });

        _keyOrder = new int[entries.Count];
        _keyTimes = new double[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            _keyOrder[i] = entries[i].Index;
            _keyTimes[i] = entries[i].Time;
        }

        int floorCount = floorTimes?.Count ?? 0;
        _floorTimes = new double[floorCount];
        double maxTime = 0.0;
        for (int i = 0; i < floorCount; i++)
        {
            double time = floorTimes[i];
            if (double.IsNaN(time) || double.IsInfinity(time))
                time = 0.0;
            _floorTimes[i] = time;
            if (time > maxTime)
                maxTime = time;
        }

        if (_keyTimes.Length > 0 && _keyTimes[_keyTimes.Length - 1] > maxTime)
            maxTime = _keyTimes[_keyTimes.Length - 1];
        Duration = maxTime;
    }

    internal double Clamp(double time)
    {
        if (double.IsNaN(time) || double.IsInfinity(time))
            return 0.0;
        return Math.Max(0.0, Math.Min(Duration, time));
    }

    /// <summary>返回第一条歌曲时间不早于目标时间的原始按键索引；没有则返回 Count。</summary>
    internal int FindKeyIndexAtOrAfter(double time)
    {
        int sorted = LowerBound(_keyTimes, Clamp(time));
        return sorted >= _keyOrder.Length ? KeyCount : _keyOrder[sorted];
    }

    /// <summary>返回最后一条严格早于目标时间的原始按键索引；没有则返回 -1。</summary>
    internal int FindKeyIndexBefore(double time)
    {
        int sorted = LowerBound(_keyTimes, Clamp(time)) - 1;
        return sorted < 0 ? -1 : _keyOrder[sorted];
    }

    /// <summary>返回最后一个不晚于目标时间的楼层；没有则返回 0。</summary>
    internal int FindFloorIndexAtOrBefore(double time)
    {
        int index = UpperBound(_floorTimes, Clamp(time)) - 1;
        return index < 0 ? 0 : index;
    }

    private static int LowerBound(double[] values, double target)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (values[mid] < target)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    private static int UpperBound(double[] values, double target)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (values[mid] <= target)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    private readonly struct TimelineKey
    {
        internal readonly double Time;
        internal readonly int Index;

        internal TimelineKey(double time, int index)
        {
            Time = time;
            Index = index;
        }
    }
}

/// <summary>
/// 动态选择区间。它是会话视图状态，不写回 ReplayData。
/// </summary>
internal readonly struct TimelineSelection
{
    internal const double DefaultMinDuration = 1.0 / 60.0;

    internal readonly double Start;
    internal readonly double End;

    internal double Duration => End - Start;

    internal TimelineSelection(double start, double end, double minDuration = DefaultMinDuration)
    {
        if (double.IsNaN(start) || double.IsInfinity(start))
            start = 0.0;
        if (double.IsNaN(end) || double.IsInfinity(end))
            end = start + minDuration;
        if (minDuration <= 0.0)
            minDuration = DefaultMinDuration;
        if (end < start)
        {
            double swap = start;
            start = end;
            end = swap;
        }
        if (end - start < minDuration)
            end = start + minDuration;
        Start = Math.Max(0.0, start);
        End = Math.Max(Start + minDuration, end);
    }

    internal static TimelineSelection Full(double duration, double minDuration = DefaultMinDuration)
    {
        if (duration <= 0.0)
            duration = minDuration;
        return new TimelineSelection(0.0, duration, minDuration);
    }

    internal TimelineSelection Clamp(double duration, double minDuration = DefaultMinDuration)
    {
        if (duration <= 0.0)
            duration = minDuration;
        if (minDuration <= 0.0)
            minDuration = DefaultMinDuration;
        double start = Math.Max(0.0, Math.Min(Start, duration));
        double end = Math.Max(start + minDuration, Math.Min(End, duration));
        if (end > duration)
        {
            start = Math.Max(0.0, duration - minDuration);
            end = duration;
        }
        return new TimelineSelection(start, end, minDuration);
    }

    internal TimelineSelection MoveStart(double value, double duration, double minDuration = DefaultMinDuration)
        => new TimelineSelection(Math.Max(0.0, Math.Min(value, End - minDuration)), End, minDuration)
            .Clamp(duration, minDuration);

    internal TimelineSelection MoveEnd(double value, double duration, double minDuration = DefaultMinDuration)
        => new TimelineSelection(Start, Math.Max(Start + minDuration, Math.Min(value, duration)), minDuration)
            .Clamp(duration, minDuration);
}

/// <summary>
/// 时间轴会话状态：播放头、选择区间和可视窗口。
/// </summary>
internal sealed class ReplayTimelineState
{
    internal ReplayData Source { get; private set; }
    internal ReplayTimelineIndex Index { get; private set; }
    internal TimelineSelection Selection { get; private set; }
    internal double Cursor { get; private set; }
    internal double ViewStart { get; private set; }
    internal double ViewSpan { get; private set; }
    internal double Duration => Index?.Duration ?? 0.0;
    internal double ViewEnd => ViewStart + ViewSpan;

    internal ReplayTimelineState()
    {
        Source = null;
        Index = new ReplayTimelineIndex(null, null);
        Selection = TimelineSelection.Full(0.0);
        Cursor = 0.0;
        ViewStart = 0.0;
        ViewSpan = 1.0;
    }

    internal void Load(ReplayData source, IReadOnlyList<double> floorTimes)
    {
        Source = source;
        Index = new ReplayTimelineIndex(source, floorTimes);
        double duration = Index.Duration;
        Selection = TimelineSelection.Full(duration);
        Cursor = 0.0;
        ViewStart = 0.0;
        ViewSpan = Math.Max(duration, TimelineSelection.DefaultMinDuration);
    }

    /// <summary>
    /// 关卡载入后补充楼层时间，同时保留玩家已经选择的区间和视图。
    /// </summary>
    internal void UpdateFloorTimes(IReadOnlyList<double> floorTimes)
    {
        if (Source == null)
            return;
        double oldDuration = Duration;
        bool wasFull = Selection.Start <= 1e-9 && Selection.End >= oldDuration - 1e-9;
        Index = new ReplayTimelineIndex(Source, floorTimes);
        Selection = wasFull ? TimelineSelection.Full(Duration) : Selection.Clamp(Duration);
        if (wasFull)
            Fit();
        else
            ClampView();
    }

    internal void Clear()
    {
        Source = null;
        Index = new ReplayTimelineIndex(null, null);
        Selection = TimelineSelection.Full(0.0);
        Cursor = 0.0;
        ViewStart = 0.0;
        ViewSpan = 1.0;
    }

    internal void SetCursor(double time)
    {
        Cursor = Index == null ? 0.0 : Index.Clamp(time);
    }

    internal void SetSelection(double start, double end)
    {
        Selection = new TimelineSelection(start, end).Clamp(Duration);
    }

    internal void MoveSelectionStart(double time)
    {
        Selection = Selection.MoveStart(time, Duration);
    }

    internal void MoveSelectionEnd(double time)
    {
        Selection = Selection.MoveEnd(time, Duration);
    }

    internal void Zoom(double factor, double anchorTime)
    {
        if (Duration <= 0.0 || factor <= 0.0 || double.IsNaN(factor) || double.IsInfinity(factor))
            return;
        double anchor = Index.Clamp(anchorTime);
        double minSpan = Math.Min(Duration, TimelineSelection.DefaultMinDuration);
        double oldSpan = Math.Max(ViewSpan, minSpan);
        double newSpan = Math.Max(minSpan, Math.Min(Duration, oldSpan * factor));
        double anchorRatio = oldSpan <= 0.0 ? 0.5 : (anchor - ViewStart) / oldSpan;
        ViewStart = anchor - (newSpan * anchorRatio);
        ViewSpan = newSpan;
        ClampView();
    }

    internal void Pan(double deltaSeconds)
    {
        ViewStart += deltaSeconds;
        ClampView();
    }

    internal void Fit()
    {
        ViewStart = 0.0;
        ViewSpan = Math.Max(Duration, TimelineSelection.DefaultMinDuration);
    }

    internal double TimeToRatio(double time)
    {
        if (ViewSpan <= 0.0)
            return 0.0;
        return Math.Max(0.0, Math.Min(1.0, (time - ViewStart) / ViewSpan));
    }

    internal double RatioToTime(double ratio)
    {
        return ViewStart + (Math.Max(0.0, Math.Min(1.0, ratio)) * ViewSpan);
    }

    private void ClampView()
    {
        if (ViewSpan > Duration)
            ViewSpan = Math.Max(Duration, TimelineSelection.DefaultMinDuration);
        if (ViewStart < 0.0)
            ViewStart = 0.0;
        if (ViewStart + ViewSpan > Duration)
            ViewStart = Math.Max(0.0, Duration - ViewSpan);
    }
}
