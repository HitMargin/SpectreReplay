using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using SpectreReplay.Features.Compat;
using static SpectreReplay.Features.Replay.ReplayKeys;

namespace SpectreReplay.Features.Replay;

// 回放离线误差分析：不进游戏、不回放，直接从已加载回放的 HitSnapshot 还原每次命中的
// 毫秒误差，换算公式：
//   ms = 角差(rad) × 60 / (π × pitch × bpm×speed) × 1000（已乘方向，正 = 晚按）
// 方向与逐砖速度不在回放文件里（HitSnapshot 只存角度），取自当前加载的谱面，
// 因此分析要求关卡已加载——与回放启动的前置条件一致。
// 除误差值外，逐击同步计算该击上下文的原版 Perfect/Pure 窗口宽度（毫秒），
// 供散点图做"按判定窗口归一化"的动态着色。
internal static class ReplayAnalyzer
{
    internal sealed class Report
    {
        internal int TotalHits;      // 回放文件里的命中总数
        internal int AnalyzedHits;   // 纳入统计的命中数
        internal int EarlyCount;
        internal int LateCount;
        internal float MedianMs;
        internal float MeanMs;
        internal float SigmaMs;
        internal float MinMs;
        internal float MaxMs;
        internal List<float> SamplesMs;    // 按命中顺序
        internal List<float> PerfectWinMs; // 与 SamplesMs 平行：该击上下文的 Perfect 窗口宽
        internal List<float> PureWinMs;    // 同上：Pure 窗口宽
    }

    /// <summary>返回 null 时 reason 为本地化键后缀（need_level / no_data）</summary>
    internal static Report Analyze(ReplayData data, out string reason)
    {
        reason = null;
        var floors = ADOBase.lm != null ? ADOBase.lm.listFloors : null;
        scrConductor conductor = ADOBase.conductor;
        if (data == null || data.HitSnapshots.Count == 0)
        {
            reason = "no_data";
            return null;
        }
        if (floors == null || floors.Count == 0 || conductor == null)
        {
            reason = "need_level";
            return null;
        }
        double bpm = conductor.bpm;
        float pitch = conductor.song != null ? conductor.song.pitch : 1f;

        Difficulty savedDifficulty = GCS.difficulty;
        float savedSpeedTrial = GCS.currentSpeedTrial;
        bool contextChanged = false;
        if (data.JudgeMode is string judgeMode
            && Enum.TryParse(judgeMode, out Difficulty recorded)
            && Enum.IsDefined(typeof(Difficulty), recorded))
        {
            GCS.difficulty = recorded;
            contextChanged = true;
        }
        // 与上面 JudgeMode 的 Enum.TryParse 对称：持久化值同样需要校验。
        // 0/负数/NaN/Infinity 的速度会让 CustomJudge.BoundaryRaw 的判定窗口
        // 撑成无穷大（每次命中都算最高档）。存量回放里可能已含这类值——它只在
        // bpm=0 的谱面下才可能被录进去，而那种谱面本身已经不可玩。
        // 跳过恢复（保留当前速度）是最保守的退化：合法回放完全不受影响。
        if (data.SpeedTrailMode is bool stm && stm
            && data.SpeedTrail is double stv
            && stv > 0 && !double.IsNaN(stv) && !double.IsInfinity(stv))
        {
            GCS.currentSpeedTrial = (float)stv;
            contextChanged = true;
        }

        var samples = new List<float>(data.HitSnapshots.Count);
        var perfectWins = new List<float>(data.HitSnapshots.Count);
        var pureWins = new List<float>(data.HitSnapshots.Count);
        try
        {
            foreach (var hit in data.HitSnapshots)
            {
                // 自动命中与 midspin 无限判定没有时间含义，与实时统计口径一致
                if (hit.IsAuto || hit.MidspinInfiniteMargin)
                    continue;
                if (hit.CurrentFloorID < 0 || hit.CurrentFloorID >= floors.Count)
                    continue;
                scrFloor floor = floors[hit.CurrentFloorID];
                // CurrentFloorID 记录被离开的地板：逼近目标出角期间的角速度由它的 speed 决定
                double bpmTimesSpeed = bpm * floor.speed;
                if (Math.Abs(bpmTimesSpeed) < 1e-6)
                    continue;
                double dir = floor.isCCW ? -1.0 : 1.0;
                double diffRad = (hit.CachedAngle - hit.TargetExitAngle) * dir;
                double ms = diffRad * 60.0 / (Math.PI * pitch * bpmTimesSpeed) * 1000.0;
                if (double.IsNaN(ms) || double.IsInfinity(ms))
                    continue;
                samples.Add((float)ms);
                double degToMs = (Math.PI / 180.0) * 60.0
                    / (Math.PI * pitch * bpmTimesSpeed) * 1000.0;
                // 形状差异（r150 起返回结构体、之前按判定档返回单个 double）由兼容层吸收。
                // 解析不出来时填 0：HitScatterMath.Windows 见非正值会自动退回 25/60ms 兜底，
                // 所以这里不需要让整个分析失败——误差值本身仍然是对的。
                if (GameCompat.AngleBoundary(GCS.difficulty, bpmTimesSpeed, pitch, 1.0,
                        out double perfectDeg, out double pureDeg))
                {
                    perfectWins.Add((float)(perfectDeg * degToMs));
                    pureWins.Add((float)(pureDeg * degToMs));
                }
                else
                {
                    perfectWins.Add(0f);
                    pureWins.Add(0f);
                }
            }
        }
        finally
        {
            if (contextChanged)
            {
                GCS.difficulty = savedDifficulty;
                GCS.currentSpeedTrial = savedSpeedTrial;
            }
        }
        if (samples.Count == 0)
        {
            reason = "no_data";
            return null;
        }

        var sorted = samples.OrderBy(x => x).ToList();
        float mean = samples.Average();
        return new Report
        {
            TotalHits = data.HitSnapshots.Count,
            AnalyzedHits = samples.Count,
            EarlyCount = samples.Count(x => x < 0),
            LateCount = samples.Count(x => x >= 0),
            MedianMs = sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) * 0.5f,
            MeanMs = mean,
            SigmaMs = Mathf.Sqrt(samples.Average(x => (x - mean) * (x - mean))),
            MinMs = sorted[0],
            MaxMs = sorted[sorted.Count - 1],
            SamplesMs = samples,
            PerfectWinMs = perfectWins,
            PureWinMs = pureWins,
        };
    }
}
