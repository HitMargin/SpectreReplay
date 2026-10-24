// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Features.Replay;

// ——— scrController.TogglePauseGame ————————————————————----——
internal class Patch_TogglePauseGame
{
    [SpectrePatch(typeof(scrController), "TogglePauseGame", PatchType.Prefix, Feature = FeatureKeys.Replay)]
    internal static bool Prefix()
    {
        // 场景名是 scnEditor 不代表 scnEditor.instance 非空：场景卸载/重载的中间态里
        // 两者会短暂不一致，此时访问其成员抛 MissingReferenceException
        scnEditor editor = scnEditor.instance;
        if (RDC.auto
            && SceneManager.GetActiveScene().name == "scnEditor"
            && editor != null
            && editor.playMode
            && is_playing)
        {
            editor.pausedInPlayMode = false;
            editor.buttonAuto.interactable = true;
            return false;
        }
        return true;
    }
}

// ——— scrConductor.Update ————————————————————————————————————
internal class Patch_ConductorUpdate
{
    internal const int StateFail = 5;
    internal const int StateFail2 = 6;
    internal const int StateWon = 7;

    [SpectrePatch(typeof(scrConductor), "Update", PatchType.Postfix, Feature = FeatureKeys.ReplayManualSave)]
    internal static void Postfix()
    {
        if (!Input.GetKeyDown(save_button)) return;
        // scrConductor.Update 每帧都跑、本补丁拦在它后面：转场/拆场景窗口里
        // instance 可能为 null，此时谈不上"在可保存的状态"，直接早退——比让
        // Guard 吞 NRE（功能退化 + 日志刷屏）干净。
        var controller = scrController.instance;
        if (controller == null) return;
        int state = (int)(States)controller.stateMachine.GetState();
        if (state != StateFail && state != StateFail2 && state != StateWon) return;
        if (!PendingSave) return;

        PlayActions.StopRecording();
        if (SaveFile()) PendingSave = false;
    }
}

// ——— scrConductor.Update (cancel save hotkey) ———————————————————
internal class Patch_CancelSaveKey
{
    [SpectrePatch(typeof(scrConductor), "Update", PatchType.Postfix, Feature = FeatureKeys.ReplayCancelSave)]
    internal static void Postfix()
    {
        if (!Input.GetKeyDown(cancel_save_button)) return;
        // 同 Patch_ConductorUpdate：转场窗口里 instance 可能为 null，早退而非 NRE
        var controller = scrController.instance;
        if (controller == null) return;
        int state = (int)(States)controller.stateMachine.GetState();
        // 与 Patch_ConductorUpdate 同一组状态：Fail/Fail2/Won（数字含义见上）
        if (state != Patch_ConductorUpdate.StateFail
            && state != Patch_ConductorUpdate.StateFail2
            && state != Patch_ConductorUpdate.StateWon) return;
        if (!PendingSave)
        {
            // 提示放在这里而不是 CancelSave() 里：那个方法只在 PendingSave 为真时
            // 才被调用，什么都没取消时不能说"已取消"。
            TriggerMessage(LocalizationManager.GetLocalizedText("note.cancel_save_none"), 3f, NotifType.Warning);
            return;
        }

        CancelSave();
    }
}

// ——— scrPlanet.MarkFail —————————————————————————————————————
internal static class Patch_MarkFail
{
    [SpectrePatch(typeof(scrPlanet), "MarkFail", PatchType.Prefix, Feature = FeatureKeys.Replay)]
    internal static bool Prefix(ref scrMissIndicator __result)
    {
        if (IgnoreMarkFail) { __result = null; return false; }
        return true;
    }
}

// ——— scrFloor.LightUp ———————————————————————————————————————
// **为什么在 LightUp 前后夹一次 GCS.hitMarginLimit。** scrFloor.LightUp
// （Assembly-CSharp/scrFloor.cs:1201）第 1212 行：
//     if (GCS.hitMarginLimit != HitMarginLimit.PerfectsOnly) SetToRandomColor();
// 这个设置除了判定严格度，还决定地板点亮时**是否走随机闪色**。回放期间
// GCS.hitMarginLimit 被占成录像录制时的档位（StoredHitMarginLimit）；但闪色
// 属于「此刻观看的人」的观感，与录像录在哪个档位无关，所以这里临时换回玩家
// 自己在设置菜单里的选择（Persistence.hitMarginLimit），出 LightUp 立即还原，
// 保证判定路径读到的仍是录像档位（IsCounted / ffxCheckpoint 都不受影响）。
//
// **已知副作用（是取舍，不是漏改）。** SetToRandomColor（scrFloor.cs:1451）只在
// vfx.tileFlashStyle == TileFlashStyle.Rando 时用 UnityEngine.Random.Range 取色；
// 于是玩家档位不是 PerfectsOnly 时，同一份回放两次渲染的砖块闪色不同——成片
// 不确定。旧自研引擎的做法相反：_legacy_srp/Playback/PlaybackClamps.cs 把这里
// **夹成 PerfectsOnly**，直接跳过随机；该文件已随 4f6d561 整体删除，本类是移植
// Spectre 时带来的实现（f96aa86），未保留该做法。要「渲染确定」就得放弃「闪色
// 与玩家当前设置一致」，属产品取向，见 docs/SPECTRE_COMPARISON.md 的对应条目。
//
// **还原为什么用 Finalizer 而不是 Postfix。** LightUp 抛异常（或别的 mod 的
// Prefix 返回 false 把原方法短路掉）时 Postfix 不执行，GCS.hitMarginLimit 就
// 停在玩家档位上；而判定读点 scrPlanet.SwitchChosen:661 在同一个 Hit() 里早于
// 点亮，于是紧接着的**下一拍**会按错的严格度统计一次。Finalizer 无论正常返回
// 还是异常逃逸都会跑：先还回录像档位，再把异常原样放行。这里不能像
// Patch_HoldKeysCrashGuard 那样吞异常——LightUp 抛出代表画面状态没走完，
// 悄悄咽下会改变游戏的失败/中断语义，只做「还值」这件事。
internal class Patch_LightUp
{
    [SpectrePatch(typeof(scrFloor), "LightUp", PatchType.Prefix, Feature = FeatureKeys.Replay)]
    internal static void Prefix()
    {
        if (is_playing) GCS.hitMarginLimit = Persistence.hitMarginLimit;
    }

    [SpectrePatch(typeof(scrFloor), "LightUp", PatchType.Finalizer, Feature = FeatureKeys.Replay)]
    internal static Exception Finalizer(Exception __exception)
    {
        if (is_playing) GCS.hitMarginLimit = StoredHitMarginLimit;
        return __exception;
    }
}

// ——— scrController.UpdateFreeroam ——————————————————————————
internal static class Patch_UpdateFreeroam
{
    [SpectrePatch(typeof(scrController), "UpdateFreeroam", PatchType.Prefix, Feature = FeatureKeys.Replay)]
    internal static bool Prefix(scrController __instance)
    {
        if (__instance == null) return true;
        if (is_playing && PlayIndex < data.HitSnapshots.Count
            && data.HitSnapshots[PlayIndex].curFreeRoamSection == __instance.curFreeRoamSection)
        {
            // 无论 ADOBase.lm / starts 状态如何，当前都统一禁止原 UpdateFreeroam 逻辑，
            // 避免回放期间自由漫游区段被干扰。所有分支都返回 false。
            return false;
        }
        return true;
    }
}
