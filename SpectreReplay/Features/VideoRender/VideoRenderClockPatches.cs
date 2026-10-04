// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace SpectreReplay.Features.VideoRender;

// ── 时间源替换（OrbitRender ConductorPatch 同款思路）─────────────────────
//
// 不再拦截 songposition_minusi 的读写（输出端），而是把 scrConductor.Update /
// scrCountdown.Update 方法体里的时间源调用整体替换：
//   AudioSettings.dspTime       → RenderDspTime()    ← 确定性 DSP 时钟
//   Time.unscaledTimeAsDouble   → RenderTimeD()      ← 确定性帧时间
//   Time.unscaledDeltaTime      → RenderDelta()      ← 固定 1/fps 步长
//   Time.unscaledTime           → RenderTime()       ← float 版
//
// 效果：游戏自己的节拍推进/相机事件/倒计时/hitsound 调度链全部自动确定性化。
// 补丁常驻但体内早退（渲染时钟不存在时原样返回真实值），不渲染时零开销。
//
// Transpiler 匹配失败时（游戏改了 Update 方法体）SpectrePatch 探针报失败，
// VideoRenderJob 降级回旧 Pin 方案——双轨保留不断功能。

/// <summary>Transpiler 替换的时间源入口（全部静态方法，被 IL 直接 call）</summary>
internal static class RenderClockSource
{
    // VideoRenderJob 在渲染开始时设置；null = 非渲染期，走原始值
    internal static RenderClock ActiveClock;

    internal static bool ControlsTime => ActiveClock != null;

    // 启动时 Transpiler 匹配成功 → true；失败（游戏改了 Update）→ false → 渲染走旧 Pin
    internal static bool TranspilerReady;

    internal static double DspTime()
        => ControlsTime ? ActiveClock.DspTime : AudioSettings.dspTime;

    // unscaled 时间族统一取 UnscaledDeltaPin 的虚拟时钟（渲染期它是权威实现，
    // 非渲染期 detour 未安装则回落真实值）。绝不在这里用 DspOrigin 拼 unscaled
    // 时间——dsp 锚点与 unscaled 基值是两个不同原点，同一帧内混用会让
    // scrConductor 内外读到的时间轴不一致。
    internal static double TimeD()
        => UnscaledDeltaPin.Installed ? UnscaledDeltaPin.CurrentUnscaledD : Time.unscaledTimeAsDouble;

    internal static float TimeF()
        => UnscaledDeltaPin.Installed ? UnscaledDeltaPin.CurrentUnscaledF : Time.unscaledTime;

    internal static float Delta()
        => UnscaledDeltaPin.Installed ? UnscaledDeltaPin.CurrentDelta : Time.unscaledDeltaTime;
}

/// <summary>
/// scrConductor.Update 的时间源 Transpiler。
/// 用 SpectrePatch 的 Transpiler 模式挂载；找不到目标调用模式时抛异常
/// → 探针报"解析失败" → VideoRenderJob 感知并降级。
///
/// 注意：scrCountdown.Update 的 Transpiler 已移除——倒计时用 dspTime 做调度
/// 计算，替换后与音频系统的调度不一致导致倒计时异常（实测）。倒计时走
/// Time.captureFramerate（缩放时间已正确）+ UnscaledDeltaPin（unscaled 时间
/// 已钉住），无需额外干预。
/// </summary>
internal static class Patch_RenderClockConductor
{
    [SpectrePatch(typeof(scrConductor), "Update", PatchType.Transpiler)]
    internal static IEnumerable<CodeInstruction> ConductorTranspiler(IEnumerable<CodeInstruction> instructions)
        => ReplaceTimeSource(instructions, "scrConductor.Update");

    private static IEnumerable<CodeInstruction> ReplaceTimeSource(IEnumerable<CodeInstruction> instructions,
        string methodName)
    {
        MethodInfo dspGetter = AccessTools.PropertyGetter(typeof(AudioSettings), nameof(AudioSettings.dspTime));
        MethodInfo timeDGetter = AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTimeAsDouble));
        MethodInfo timeFGetter = AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTime));
        MethodInfo deltaGetter = AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledDeltaTime));

        // 替换目标方法是 internal static：普通 GetMethod 只搜 Public 会返回 null
        // → operand = null → "call NULL" 非法 IL。AccessTools.Method 全可见性搜索
        MethodInfo dspPin = AccessTools.Method(typeof(RenderClockSource), nameof(RenderClockSource.DspTime));
        MethodInfo timeDPin = AccessTools.Method(typeof(RenderClockSource), nameof(RenderClockSource.TimeD));
        MethodInfo timeFPin = AccessTools.Method(typeof(RenderClockSource), nameof(RenderClockSource.TimeF));
        MethodInfo deltaPin = AccessTools.Method(typeof(RenderClockSource), nameof(RenderClockSource.Delta));

        if (dspPin == null || timeDPin == null || timeFPin == null || deltaPin == null)
            throw new InvalidOperationException(
                "[Spectre] RenderClock transpiler: replacement method not found (AccessTools.Method returned null)");

        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(dspGetter))
            {
                instruction.operand = dspPin;
                replaced++;
            }
            else if (instruction.Calls(timeDGetter))
            {
                instruction.operand = timeDPin;
                replaced++;
            }
            else if (instruction.Calls(timeFGetter))
            {
                instruction.operand = timeFPin;
                replaced++;
            }
            else if (instruction.Calls(deltaGetter))
            {
                instruction.operand = deltaPin;
                replaced++;
            }
            yield return instruction;
        }

        if (replaced == 0)
            throw new InvalidOperationException(
                $"[Spectre] RenderClock transpiler: no time-source calls found in {methodName} — game Update likely changed");
        RenderClockSource.TranspilerReady = true;
    }
}

/// <summary>
/// 异步输入的 GetSongPosition：直接从 RenderClock 算（与 conductor 同一时间轴），
/// 替代旧方案的 AdjustAngle 跳过（Patch_VideoLockAsyncAngle 可退役）。
/// </summary>
internal static class Patch_RenderClockAsyncPosition
{
    [SpectrePatch(typeof(AsyncInputUtils), "GetSongPosition", PatchType.Prefix)]
    internal static bool Prefix(scrConductor __instance, ref double __result)
    {
        if (!RenderClockSource.ControlsTime)
            return true; // 非渲染期走原版
        if (__instance == null || __instance.song == null)
        {
            __result = 0.0;
            return false;
        }
        __result = RenderClockSource.ActiveClock.SongPosition(
            __instance.dspTimeSong,
            __instance.song.pitch,
            __instance.addoffset,
            0.0);
        return false;
    }
}
