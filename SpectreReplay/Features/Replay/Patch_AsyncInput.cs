// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using System;
using HarmonyLib;
using UnityEngine;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Features.Replay;

// ——— scrController.UpdateInput ————————————————————————
// 每帧把 AsyncKeyRecorder 攒下的原始按键事件换算成歌曲时间并落账
internal class Patch_UpdateInputConsumeAsync
{
    [SpectrePatch(typeof(scrController), "UpdateInput", PatchType.Prefix, Feature = FeatureKeys.ReplayAsyncInput)]
    internal static void Prefix()
    {
        try
        {
            AsyncKeyRecorder.FlushFrame();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }
}
