using System;
using UnityEngine;

namespace SpectreReplay.Features.Compat;

// 原版判定窗口边界的本地复刻（scrMisc.GetAdjustedAngleBoundaryInDeg 同式）。
//
// **来历**：这段数学原本住在 `Features\CustomJudge\CustomJudge.BoundaryRaw`。
// 2026-10-17 用户指出「CustomJudge 并不属于 replay 和视频渲染的部分」——判定改写
// 那整套（档位、状态、档案、改判定的补丁）都不是本项目的事，已整体移除。
// 但**这段窗口数学是 replay 真正依赖的**：回放散点图要按原版 Perfect/Pure 窗口
// 宽度归一化着色（ReplayAnalyzer），找不到游戏函数时 GameCompat 也要用它兜底。
// 所以把它单独摘出来放在这里，只保留数学、不留任何判定改写能力。
//
// **为什么自己算而不直接调游戏函数**：上游 Spectre 的真实判定改写会 Postfix 挂在
// 游戏那个函数上；这里要的永远是**原版语义**（散点图的"完美窗口"必须是游戏本来
// 的窗口，不能是被别的 mod 改过之后的）。
internal static class MarginBoundary
{
    internal const double DegPerRad = 57.29577951308232;

    /// <summary>原版判定窗口边界，单位：度。</summary>
    internal static double Raw(HitMarginGeneral marginType,
        double bpmTimesSpeed, double conductorPitch, double marginMult)
    {
        float num = GCS.difficulty == Difficulty.Lenient ? 0.091f
            : GCS.difficulty == Difficulty.Strict ? 0.04f : 0.065f;
        bool isMobile = ADOBase.isMobile;
        // 除数钳到小正数：谱面 bpm=0（手改谱面或外部工具导入）时 x/0 = Infinity，
        // 而下面三行 Mathf.Max(x, 0.025f) 拦不住 Infinity（实测 Max(∞, 0.025) = ∞），
        // 于是判定窗口变成无穷大 —— 回放里每次命中都会被算成落在完美窗口内，
        // 散点图整片误绿。钳除数而非钳结果：合法速度下结果逐位不变（除以 1 还是原值），
        // 且三处除法一次保护。游戏本体 scrLoader 也有同样的无保护除法，
        // 但那只会影响它的提示时长，这里影响的是散点图的着色口径。
        float speedTrial = Mathf.Max(GCS.currentSpeedTrial, 0.01f);
        num = isMobile ? 0.09f : num / speedTrial;
        float a = isMobile ? 0.07f : 0.03f / speedTrial;
        float a2 = isMobile ? 0.05f : 0.02f / speedTrial;
        num = Mathf.Max(num, 0.025f);
        a = Mathf.Max(a, 0.025f);
        a2 = Mathf.Max(a2, 0.025f);
        // 惰性求值：原来无条件算三个 TimeToAngleInRad，而下面的 switch 每个分支
        // 只用到其中一个——每次调用白算两个。TimeToAngleInRad 是纯函数（只依赖入参、
        // 无副作用），按分支只算需要的那一个，结果逐位相同。
        switch (marginType)
        {
            case HitMarginGeneral.Perfect:
                return Math.Max(45.0 * marginMult,
                    scrMisc.TimeToAngleInRad(a, bpmTimesSpeed, conductorPitch) * DegPerRad);
            case HitMarginGeneral.Pure:
                return Math.Max(30.0 * marginMult,
                    scrMisc.TimeToAngleInRad(a2, bpmTimesSpeed, conductorPitch) * DegPerRad);
            default:    // Counted 及其余一律落到 Counted 语义
                return Math.Max((double)GCS.HITMARGIN_COUNTED * marginMult,
                    scrMisc.TimeToAngleInRad(num, bpmTimesSpeed, conductorPitch) * DegPerRad);
        }
    }
}