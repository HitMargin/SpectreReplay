using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static SpectreReplay.Features.Replay.ReplayKeys;

namespace SpectreReplay.Features.Replay;

// 回放离线误差分析：不进游戏、不回放，直接从已加载回放的 HitContext 还原每次命中的
// 毫秒误差，换算公式：
//   ms = 角差(rad) × 60 / (π × pitch × bpm×speed) × 1000（已乘方向，正 = 晚按）
// 方向与逐砖速度不在回放文件里（HitContext 只存角度），取自当前加载的谱面，
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
    internal static Report Analyze(LegacyReplayData data, out string reason)
    {
        reason = null;
        var floors = ADOBase.lm != null ? ADOBase.lm.listFloors : null;
        scrConductor conductor = ADOBase.conductor;
        if (data == null || data.HitContext_list.Count == 0)
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

        // 窗口公式读取全局难度与飙速态（BoundaryRaw 内部消费）：
        // 临时对齐录制时的上下文再计算，算完恢复，避免拿"现在"的设置误判"当时"
        Difficulty savedDifficulty = GCS.difficulty;
        float savedSpeedTrial = GCS.currentSpeedTrial;
        bool contextChanged = false;
        // **必须拒掉数字串。** `Enum.TryParse("99", out var v)` 会成功并给出
        // `(Difficulty)99` 这种越界值——BoundaryRaw 内部是
        // `== Lenient ? … : == Strict ? … : 默认`，越界值不会抛，只会悄无声息
        // 落进"Normal"那一支，于是窗口宽度按错误的难度算出来。回放文件是玩家
        // 可编辑的文本，"JudgeMode": "99" 完全可能出现。
        // 判据：先 Enum.IsDefined 确认落点合法，再赋值。不合法就跳过（保留"现在"
        // 的难度）——与下面 SpeedTrail 的保守退化同一思路。
        if (data.strings.TryGetValue(JudgeMode, out string judgeMode)
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
        if (data.bools.TryGetValue(SpeedTrailMode, out bool stm) && stm
            && data.doubles.TryGetValue(SpeedTrail, out double stv)
            && stv > 0 && !double.IsNaN(stv) && !double.IsInfinity(stv))
        {
            GCS.currentSpeedTrial = (float)stv;
            contextChanged = true;
        }

        var samples = new List<float>(data.HitContext_list.Count);
        var perfectWins = new List<float>(data.HitContext_list.Count);
        var pureWins = new List<float>(data.HitContext_list.Count);
        try
        {
            foreach (var hit in data.HitContext_list)
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
                // 窗口角度走 MarginBoundary.Raw 的原版语义（不经过任何判定改写 Postfix）。
                // 注意它返回的是【度】，须先转回弧度再用与误差相同的逆变换，
                // 否则窗口会被放大 180/π ≈ 57 倍，散点图全部误绿
                double degToMs = (Math.PI / 180.0) * 60.0
                    / (Math.PI * pitch * bpmTimesSpeed) * 1000.0;
                perfectWins.Add((float)(Features.Compat.MarginBoundary.Raw(
                    HitMarginGeneral.Perfect, bpmTimesSpeed, pitch, 1.0) * degToMs));
                pureWins.Add((float)(Features.Compat.MarginBoundary.Raw(
                    HitMarginGeneral.Pure, bpmTimesSpeed, pitch, 1.0) * degToMs));
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
            TotalHits = data.HitContext_list.Count,
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
