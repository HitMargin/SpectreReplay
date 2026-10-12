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

        // 已被别的 mod 的同类 transpiler 替换过？（Harmony 的 Calls 是
        // `object.Equals(operand, method)`——精确比 MethodInfo。所以目标被换成
        // 别的程序集里同名的 RenderClockSource.DspTime 时，我们的 Calls(dspGetter)
        // 一个都不命中，而指令里会留着**那个**程序集的调用。）
        // 判据取"类型同名"而不是比对我们自己的 MethodInfo：对方那份的类型
        // 来自另一个程序集，拿到这里也只会是 null。
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

/// <summary>
/// 异步输入的 GetSongPosition：直接从 RenderClock 算（与 conductor 同一时间轴），
/// 替代旧方案的 AdjustAngle 跳过（Patch_VideoLockAsyncAngle 可退役）。
///
/// **2026-10-06 修掉一个继承自 Spectre 的潜伏缺陷：形参名不能写 `__instance`。**
/// `AsyncInputUtils.GetSongPosition` 是**静态方法**（真实签名
/// `GetSongPosition(scrConductor conductor, long nowTick)`），而 Harmony 的
/// `__instance` 只在"原方法非静态"时注入——它内部是个**合成形参**
/// （`MethodBodyReader+ThisParameter`，派生自 ParameterInfo），不是按名字匹配的。
/// 静态方法上声明 `__instance` 的后果是它拿到不到导体，恒为 null。
///
/// 具体到这段代码的危害：`__instance == null` 分支会 `__result = 0.0; return false`
/// ⇒ **GetSongPosition 恒返回 0**。它唯一的调用者是 `AsyncInputUtils.GetAngle`
/// （AsyncInputUtils.cs:23），而 `GetAngle` 的唯一调用者是
/// `scrPlanet.AsyncRefreshAngles()`（scrPlanet.cs:555），后者又只被
/// `AsyncInputUtils.AdjustAngle`（:31）调用——**而 AdjustAngle 正是我们渲染期
/// 禁用的那个**（Patch_VideoLockAsyncAngle）。所以这条链目前被另一条补丁挡着，
/// 缺陷暂时不发作；但它是**潜伏**的：哪天有人重开 AdjustAngle、或别处调
/// GetSongPosition，就会静默拿到 0 而不是报错。
///
/// 修法：改用**原方法的真实形参名 `conductor`**（Harmony 按名注入普通参数）。
/// 这不是风格问题——名字写错时补丁照样挂得上、照样跑，只是永远拿到 null。
/// </summary>
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
