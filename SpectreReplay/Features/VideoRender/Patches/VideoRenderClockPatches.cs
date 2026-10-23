// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace SpectreReplay.Features.VideoRender;

// ── 时间源替换 ─────────────────────────────────────────────────────────
//
// 不拦截 songposition_minusi 的读写（输出端），而是把 scrConductor.Update /
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

internal static class Patch_RenderClockConductor
{
    // 挂在 FeatureKeys.RenderClock 上（**实验性开关，默认关**）。
    // Feature 契约：谓词为假时补丁根本不挂载、拨开关同拍卸载——所以这里
    // **不需要**在补丁体内再查 Settings.RenderClockOn，体内检查只留给
    // Feature 之下的子选项。开关一关，scrConductor.Update 立刻恢复原样，
    // 渲染自动落到 VideoRenderJob 的 audio-cursor 兜底路径。
    [SpectrePatch(typeof(scrConductor), "Update", PatchType.Transpiler, Feature = FeatureKeys.RenderClock)]
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
                "[SpectreReplay] RenderClock transpiler: replacement method not found (AccessTools.Method returned null)");

        // **必须先物化成 List 再判断"已被别的 mod 接管"。**
        // 下面那个 foreach 带 yield return，是**惰性**的——在它跑完之前
        // instructions 一个元素都还没被枚举过。若把探测写在 foreach 之后，
        // 拿到的 replaced 仍是 0，判定永远不成立（且会真的把流耗尽）。
        // 这是本项目"foreach + yield 里做后置判断"的典型陷阱。
        var body = new List<CodeInstruction>();
        if (instructions != null)
        {
            body.AddRange(instructions);
        }

        foreach (CodeInstruction ci in body)
        {
            if (ci.operand is MethodInfo mi
                && (mi.DeclaringType?.Name == "RenderClockSource"))
            {
                // 静默让位：不置 TranspilerReady（渲染走 audio-cursor 兜底），
                // 也不抛异常。**只能逐条 yield**——本方法是迭代器，
                // `return body;` 会报 CS1622（无法从迭代器返回值）。
                Debug.Log("[SpectreReplay] RenderClock transpiler: " + methodName
                    + " 的时间源已被另一个模组接管（" + mi.DeclaringType?.Assembly.GetName().Name
                    + "），本次让位；离线渲染走音频游标兜底时钟。");
                foreach (CodeInstruction passthrough in body)
                {
                    yield return passthrough;
                }
                yield break;
            }
        }

        int replaced = 0;
        foreach (CodeInstruction instruction in body)
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
                $"[SpectreReplay] RenderClock transpiler: no time-source calls found in {methodName} — "
                + "目标方法里既没有游戏的时间源调用、也没有别的模组留下的替换，游戏侧形状应该变了");
        RenderClockSource.TranspilerReady = true;
    }
}

internal static class Patch_RenderClockAsyncPosition
{
    [SpectrePatch(typeof(AsyncInputUtils), "GetSongPosition", PatchType.Prefix)]
    internal static bool Prefix(scrConductor conductor, ref double __result)
    {
        if (!RenderClockSource.ControlsTime)
            return true; // 非渲染期走原版
        if (conductor == null || conductor.song == null)
        {
            __result = 0.0;
            return false;
        }
        __result = RenderClockSource.ActiveClock.SongPosition(
            conductor.dspTimeSong,
            conductor.song.pitch,
            conductor.addoffset,
            0.0);
        return false;
    }
}
