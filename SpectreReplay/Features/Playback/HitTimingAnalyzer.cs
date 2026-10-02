using System;
using System.Collections.Generic;

namespace SpectreReplay.Features.Playback
{
    /// <summary>
    /// 一次命中的毫秒误差（正 = 晚按）。
    /// </summary>
    internal struct HitTiming
    {
        internal float Ms;
        /// <summary>该击所在地板序号，取不到时 -1。</summary>
        internal int FloorId;
    }

    /// <summary>
    /// 回放离线误差分析——从已录的**状态轨**还原每次命中的毫秒误差。
    ///
    /// 照 Spectre 的 ReplayAnalyzer（ReplayAnalyzer.cs:9-16 的口径、
    /// :91-104 的逆运算式）。Spectre 那份还顺带算 Perfect/Pure 窗口宽度
    /// 供散点图归一化，那依赖 Spectre 的 CustomJudge.BoundaryRaw——
    /// 我们没有自定义判定，那两个窗口无从谈起，所以只保留误差本身。
    ///
    /// **为什么值得做**：状态轨里本来就存了
    /// <c>CachedAngle − TargetExitAngle</c>（偏差 1/1b 补的），
    /// 而那个差值此前**只被回放用来推进行星，从没被读出来看过**。
    /// 它其实是"这一击偏了多少"，正是玩家最想知道的那件事：
    /// 是自己整体偏早、还是某几处单独拖后腿。
    ///
    /// **纯计算，不碰引擎**：所以能被离线闸门直接链接验算，
    /// 不用进游戏（见 tools\UiPreview\TimingCheck.cs）。
    /// </summary>
    internal static class HitTimingAnalyzer
    {
        /// <summary>
        /// 逆运算式（照 Spectre ReplayAnalyzer.cs:95-96）：
        ///
        ///     ms = 角差(rad) × 60 / (π × pitch × bpm×speed) × 1000
        ///
        /// 角差已按行星转向取过正负，所以"正 = 晚按"直接成立，
        /// 不要再乘一次方向。
        ///
        /// <paramref name="angleDeltaRad"/> 是
        /// <c>CachedAngle − TargetExitAngle</c>；
        /// <paramref name="bpmTimesSpeed"/> 是 <c>bpm × 该砖 speed</c>
        /// （逼近目标出角期间的角速度由**被离开的那块砖**决定）。
        /// </summary>
        internal static double MsFromAngle(double angleDeltaRad,
            double bpmTimesSpeed, double pitch)
        {
            return angleDeltaRad * 60.0
                / (Math.PI * pitch * bpmTimesSpeed) * 1000.0;
        }

        /// <summary>
        /// 整批命中换算成毫秒误差，并顺带给出统计量。
        ///
        /// 跳过规则照 Spectre（:84-98），理由逐条：
        ///  · <c>IsAuto</c> / <c>MidspinInfiniteMargin</c> 没有时间含义
        ///    （自动命中不是玩家按的；midspin 无限判定本就不判时间）
        ///  · <c>bpm×speed</c> 趋零 → 除零，得无穷/NaN
        ///  · 结果本身是 NaN/Infinity → 不能进统计
        /// </summary>
        /// <param name="angles">
        /// 与 <paramref name="hits"/> 平行的角差（弧度，已取正负）。
        /// </param>
        /// <param name="bpmTimesSpeedPerFloor">
        /// 逐砖的 <c>bpm × speed</c>；某砖取不到时传 <b>0</b>（该砖会被跳过）。
        ///
        /// 用 int 是有意的：0 在这里是"取不到"的哨兵，
        /// 而 double 的 0.0 与 0.0 没法区分"正好为 0"和"取不到"——
        /// 前者其实该按趋零处理（同样跳过），所以不区分也无害。
        /// 调用方要自己保证精度：<b>必须 Math.Round 而不是强转</b>，
        /// 直接 (int) 会截断掉小数（bpm=120.5、speed=1 → 120，
        /// 误差 0.4%，累积成一整局的系统性偏差）。
        /// </param>
        internal static HitTimingReport Analyze(IReadOnlyList<double> angles,
            IReadOnlyList<int> bpmTimesSpeedPerFloor, double pitch)
        {
            var r = new HitTimingReport();
            if (angles == null)
            {
                return r;
            }
            var samples = new List<float>(angles.Count);
            for (int i = 0; i < angles.Count; i++)
            {
                if (bpmTimesSpeedPerFloor == null || i >= bpmTimesSpeedPerFloor.Count)
                {
                    break;
                }
                double bts = bpmTimesSpeedPerFloor[i];
                // |bpm×speed| 太小 → 除出来的数没有意义（谱面极端值）
                if (Math.Abs(bts) < 1e-6)
                {
                    continue;
                }
                double ms = MsFromAngle(angles[i], bts, pitch);
                if (double.IsNaN(ms) || double.IsInfinity(ms))
                {
                    continue;
                }
                samples.Add((float)ms);
            }
            r.TotalHits = angles.Count;
            r.AnalyzedHits = samples.Count;
            if (samples.Count == 0)
            {
                return r;
            }
            var sorted = new List<float>(samples);
            sorted.Sort();
            double sum = 0;
            foreach (float v in samples)
            {
                sum += v;
            }
            r.MeanMs = (float)(sum / samples.Count);
            double varSum = 0;
            foreach (float v in samples)
            {
                double d = v - r.MeanMs;
                varSum += d * d;
            }
            // 总体标准差（除 N 而不是 N-1），照 Spectre 的 Average(x => (x-mean)^2)。
            // 与"样本标准差"差一个 √(N/(N-1))，用于玩家自看趋势够用。
            r.SigmaMs = (float)Math.Sqrt(varSum / samples.Count);
            r.MedianMs = sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (float)((sorted[sorted.Count / 2 - 1]
                    + sorted[sorted.Count / 2]) * 0.5);
            r.MinMs = sorted[0];
            r.MaxMs = sorted[sorted.Count - 1];
            foreach (float v in samples)
            {
                if (v < 0) r.EarlyCount++;
                else r.LateCount++;
            }
            r.SamplesMs = samples;
            return r;
        }
    }

    /// <summary>误差统计结果（字段名照 Spectre 的 ReplayAnalyzer.Report）。</summary>
    internal sealed class HitTimingReport
    {
        internal int TotalHits;      // 状态轨里的命中总数
        internal int AnalyzedHits;   // 实际纳入统计的（被跳过的不算）
        internal int EarlyCount;     // 负值 = 偏早
        internal int LateCount;      // 非负 = 偏晚
        internal float MedianMs;
        internal float MeanMs;
        internal float SigmaMs;
        internal float MinMs;
        internal float MaxMs;
        internal List<float> SamplesMs;
    }
}