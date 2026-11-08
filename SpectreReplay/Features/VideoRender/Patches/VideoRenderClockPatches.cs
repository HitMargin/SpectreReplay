// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace SpectreReplay.Features.VideoRender;

// ── 时间源权威 ─────────────────────────────────────────────────────────
//
// 确定性时钟的唯一权威是 UnscaledDeltaPin.InstallDsp：AudioSettings.dspTime
// 的 getter 被原生 detour 钉到虚拟时钟（基值 + 已捕获帧数/fps），任何版本、
// 任何方法体的托管读取都拿到一致的值——游戏自己的节拍推进/相机事件/倒计时/
// hitsound 调度链全部自动确定性化，不再需要逐方法体的 Transpiler。
// （2026-11-08 重构：原 scrConductor.Update Transpiler 与其实验性开关已删——
// 它只钉得住 r150 一个方法体，"兼容路径"整类问题都源于此。）

/// <summary>渲染期的活动时钟；AsyncInputUtils.GetSongPosition 前缀据此服务</summary>
internal static class RenderClockSource
{
    // VideoRenderJob 在渲染开始时设置；null = 非渲染期，走原始值
    internal static RenderClock ActiveClock;

    internal static bool ControlsTime => ActiveClock != null;
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
