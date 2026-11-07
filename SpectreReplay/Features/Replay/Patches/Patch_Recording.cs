// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using System;
using System.Linq;
using HarmonyLib;
using SpectreReplay.Features.Compat;
using UnityEngine;
using UnityEngine.SceneManagement;
using SpectreReplay.API;
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
    // ── 录制诊断（一次性）─────────────────────────────────────────
    // 2026-10-31 实测出一类坏回放：KeyEvents 记满整首歌、HitSnapshots 却是
    // 0 条，播放必报「回放数据为空」。静态推演能走到 Add 的前提全部成立
    // （玩法正常 = 吞键的门没拦；KeyEvents 在记 = is_recording 全程为真），
    // 唯一无法离线验证的是「前缀到底有没有被调用」与三道 **静默跳过** 门
    // （return true，玩法照常、唯独不记）。四个一次性日志把嫌疑人钉死：
    // 录一局后看日志——一行都没有 = 前缀没上链（Harmony 挂载问题）；
    // 有「命中被跳过」行 = 门的问题（带现场值）；有「首个快照已记」= 链路通。
    private static bool _diagPrefixSeen;
    private static bool _diagSceneNull;
    private static bool _diagSceneMismatch;
    private static bool _diagPlayerMismatch;
    private static bool _diagFirstSnapshot;

    internal static void ResetDiagnostics()
    {
        _diagPrefixSeen = false;
        _diagSceneNull = false;
        _diagSceneMismatch = false;
        _diagPlayerMismatch = false;
        _diagFirstSnapshot = false;
    }

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

            if (!_diagPrefixSeen)
            {
                _diagPrefixSeen = true;
                Debug.Log("[SpectreReplay] 录制诊断：Hit 前缀收到首个录制命中（Harmony 链路通）");
            }

            if (data == null) data = new ReplayData();
            if (data.SceneName == null)
            {
                if (!_diagSceneNull)
                {
                    _diagSceneNull = true;
                    Debug.Log("[SpectreReplay] 录制诊断：命中被跳过——data.SceneName 为 null（StartRecording 未完成？）");
                }
                return true;
            }
            if (SceneManager.GetActiveScene().name != data.SceneName)
            {
                if (!_diagSceneMismatch)
                {
                    _diagSceneMismatch = true;
                    Debug.Log("[SpectreReplay] 录制诊断：命中被跳过——活动场景 \""
                        + SceneManager.GetActiveScene().name + "\" ≠ data.SceneName \"" + data.SceneName + "\"");
                }
                return true;
            }
            if (__instance != controller.playerOne)
            {
                if (!_diagPlayerMismatch)
                {
                    _diagPlayerMismatch = true;
                    Debug.Log("[SpectreReplay] 录制诊断：命中被跳过——命中玩家 #"
                        + __instance.GetInstanceID() + " ≠ playerOne #" + controller.playerOne.GetInstanceID()
                        + "（r150 玩家生命周期变更？）");
                }
                return true;
            }

            var player = controller.playerOne;
            // 显式传 controller（第 58 行已保证它与 __instance 同源）而不是无参重载：
            // 无参版本读静态 scrController.instance，读代码时无法一眼确认两者是同一个。
            float angle = GetAngle(controller);
            var nextfloor = controller.chosenPlanet.currfloor.nextfloor;
            var item = new HitSnapshot
            {
                CurrentFloorID = controller.currFloor.seqID,
                CurrAngle = angle,
                OverloadCounter = player.failBar.overloadCounter,
                NoFailHit = controller.noFailInfiniteMargin,
                IsAuto = isAuto,
                NextFloorAuto = nextfloor != null && nextfloor.auto,
                CachedAngle = controller.chosenPlanet.angle,
                TargetExitAngle = controller.chosenPlanet.targetExitAngle,
                MidspinInfiniteMargin = player.midspinInfiniteMargin,
                RDC_auto = RDC.auto,
                curFreeRoamSection = controller.curFreeRoamSection,
                HitTick = hitTick
            };

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
            else if (data.HitSnapshots.Count > 0)
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
            if (!_diagFirstSnapshot)
            {
                _diagFirstSnapshot = true;
                Debug.Log("[SpectreReplay] 录制诊断：首个命中快照已记（floor=" + item.CurrentFloorID + "）——录制链路完整");
            }
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
        // 全自动判定读「过关瞬间」的 RDC.auto：这是模式级开关（编辑器自动演示、
        // Otto、调试键），自动砖每击的临时翻转由 scrPlayer 当击恢复，污染不到
        // 这里。原先按「每击快照全带 auto」判，纯手打的自动砖测试关会被整局
        // 误杀（2026-11-07 实测）。编辑器试玩是作者自己的测试，不受此设置约束。
        bool editorPlaytest = data.SceneName == "scnEditor";
        if (!editorPlaytest && Settings.DontSaveWhenAuto && RDC.auto)
        {
            NotifySaveSkipped(LocalizationManager.GetLocalizedText("note.save_skip_auto"),
                "auto run (DontSaveWhenAuto)");
            return;
        }
        if (Settings.CompleteSave && data.StartTile != 0)
        {
            NotifySaveSkipped(LocalizationManager.GetLocalizedText("note.save_skip_checkpoint"),
                "checkpoint start (CompleteSave)");
            return;
        }

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
