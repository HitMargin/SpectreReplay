// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using System;
using System.Linq;
using HarmonyLib;
using SpectreReplay.Features.Compat;
using UnityEngine;
using UnityEngine.SceneManagement;
using SpectreReplay.API;
using Object = UnityEngine.Object;
using static SpectreReplay.SpectreState;
using static SpectreReplay.Features.Replay.ReplayKeys;

namespace SpectreReplay.Features.Replay;

// ——— scrPlayer.Hit ————————————————————————————————————————
// 已迁移 [SpectrePatch] 属性路径：候选链 + 异常兜底见 SpectrePatchHub
//
// 版本分叉：r150 把签名从 Hit(bool isAuto) 改成 Hit(long? hitTick, bool isAuto)。
// SpectrePatch 的按名消歧要求补丁方法的每个普通参数名都出现在目标上
//（DisambiguateByPatchParams：matched < ordinary.Length 直接淘汰），
// 所以单个带 hitTick 的补丁在旧版会绑定失败，必须拆成两条（模式③）。
// 两条都进同一个 Record，录制逻辑不分叉。
internal static class Patch_Hit
{
    // r141–r149：Hit(bool isAuto)
    [SpectrePatch(typeof(scrPlayer), "Hit", PatchType.Prefix, Feature = FeatureKeys.Replay, MaxVersion = 149)]
    internal static bool PrefixOld(scrPlayer __instance, bool isAuto = false)
        => Record(__instance, null, isAuto);

    // r150+：Hit(long? hitTick, bool isAuto) —— 异步输入长按命中靠 tick 定位准时
    [SpectrePatch(typeof(scrPlayer), "Hit", PatchType.Prefix, Feature = FeatureKeys.Replay, MinVersion = 150)]
    internal static bool PrefixNew(scrPlayer __instance, long? hitTick = null, bool isAuto = false)
        => Record(__instance, hitTick, isAuto);

    private static bool Record(scrPlayer __instance, long? hitTick, bool isAuto)
    {
        try
        {
            var controller = scrController.instance;
            if (HitLocked) return false;
            if (!__instance.responsive) return false;
            if (ADOBase.isLevelEditor && ADOBase.controller.paused) return false;
            // r150（异步输入重构）前：游戏的 Hit 不自带输入事件触发，由本前缀代发，
            // 并按 ignoreInput 中止原方法。
            // r150 起 Hit 内部自调 HitInputEvent（scrPlayer.cs:754，handleAll:false）并自行中止——
            // 此时再代发会【双触发】：前缀先把 Any 目标的 lastHitFrame 帧位占掉（handled=true），
            // 游戏那次 Any 被帧守卫拦下、Action1 反而因 handled=false 放行，
            // 一次按键 Any+Action1 双发（原版只发首个），且触发时点早于 cachedAngle 回写。
            // 兼容层按"Hit 首参为 long?"探测重构形状，两版本各走各的原版路径。
            if (!GameCompat.HitFiresInputEvents
                && !GameCompat.HitInputEvent(controller.playerOne, isAuto, InputEventState.Down)) return false;
            if (!is_recording) return true;

            if (data == null) data = new ReplayData();
            if (data.SceneName == null) return true;
            if (SceneManager.GetActiveScene().name != data.SceneName) return true;
            if (__instance != controller.playerOne) return true;

            var player = controller.playerOne;
            float angle = GetAngle();
            var nextfloor = controller.chosenPlanet.currfloor.nextfloor;
            var item = new HitSnapshot
            {
                CurrentFloorID = controller.currFloor.seqID,
                CurrAngle = angle,
                OverloadCounter = player.failBar.overloadCounter,
                NoFailHit = controller.noFailInfiniteMargin,
                IsAuto = isAuto,
                NextFloorAuto = nextfloor != (Object)null && nextfloor.auto,
                CachedAngle = controller.chosenPlanet.angle,
                TargetExitAngle = controller.chosenPlanet.targetExitAngle,
                MidspinInfiniteMargin = player.midspinInfiniteMargin,
                RDC_auto = RDC.auto,
                curFreeRoamSection = controller.curFreeRoamSection,
                HitTick = hitTick
            };

            if (!RDC.auto) AllAutoHits = false;

            var marginCounts = player.marginTracker.hitMarginsCount;
            // 两个数组都按 Max(HitMargin)+1 分配，正常等长；但这里用**较短者**收敛：
            // 一旦长度不一致（版本差异/兼容层枚举解析偏差），越界异常会被本方法外层的
            // catch 捕获，而那个 catch 会直接 StopRecording —— 代价是用户正在录的回放
            // 全部作废。用 min() 最多丢失末尾几个 margin 桶的统计，录制仍能继续。
            int count = Math.Min(HitMarginCounts.Length, marginCounts.Length);
            if (data.HitSnapshots.Count == 0)
            {
                CurrentHitFloor = controller.chosenPlanet.currfloor;
                for (int i = 0; i < count; i++)
                    HitMarginCounts[i] = marginCounts[i];
            }
            else
            {
                bool changed = false;
                for (int j = 0; j < count; j++)
                {
                    if (HitMarginCounts[j] != marginCounts[j])
                    { changed = true; break; }
                }
                if (CurrentHitFloor != controller.chosenPlanet.currfloor) changed = true;
                if (!changed)
                {
#if DEBUG
                    Debug.Log("hit deleted");
#endif
                    data.HitSnapshots.RemoveAt(data.HitSnapshots.Count - 1);
                }
            }

            CurrentHitFloor = controller.chosenPlanet.currfloor;
            for (int k = 0; k < count; k++)
                HitMarginCounts[k] = marginCounts[k];
            data.HitSnapshots.Add(item);
            return true;
        }
        catch (Exception ex)
        {
            PlayActions.StopPlaying();
            PlayActions.StopRecording();
            Debug.LogException(ex);
        }
        return false;
    }
}

// ——— scrController.OnLandOnPortal ——————————————————————————
internal class Patch_OnLandOnPortal
{
    [SpectrePatch(typeof(scrController), "OnLandOnPortal", PatchType.Postfix, Feature = FeatureKeys.Replay)]
    internal static void Postfix(scrController __instance, Portal portalDestination, string portalArguments)
    {
        if ((int)portalDestination != 1 || data.SceneName == null) return;
        if (SceneManager.GetActiveScene().name != data.SceneName) return;
        if (!is_recording || !Settings.AutoSave) return;
        if (Settings.DontSaveWhenAuto && AllAutoHits) return;
        if (Settings.CompleteSave && data.StartTile != 0) return;

        if (Settings.LateSave) { LateSaveMode = true; return; }

        PlayActions.StopRecording();
        if (Settings.DontSaveWhenMiss)
        {
            // FailMiss/FailOverload 桶按名取（r150 枚举重排后旧下标 8/9 已指向别的判定）
            if (HitMarginCompat.FailCount(HitMarginCounts) == 0 && SaveFile())
                PendingSave = false;
        }
        else if (SaveFile())
            PendingSave = false;
    }
}

// ——— scrController.Fail2Action —————————————————————————————
internal class Patch_Fail2Action
{
    [SpectrePatch(typeof(scrController), "Fail2Action", PatchType.Postfix, Feature = FeatureKeys.Replay)]
    internal static void Postfix(scrController __instance)
    {
        if (is_playing) TriggerMessage(LocalizationManager.GetLocalizedText("note.stop_playing"));
        if (is_recording) TriggerMessage(LocalizationManager.GetLocalizedText("note.stop_recording"));
        PlayActions.StopPlaying();
        PlayActions.StopRecording();
        if (Settings.FailSave && PendingSave && SaveFile())
            PendingSave = false;
    }
}
