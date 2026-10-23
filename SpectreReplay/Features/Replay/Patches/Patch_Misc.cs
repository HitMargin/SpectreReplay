// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
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
        int state = (int)(States)scrController.instance.stateMachine.GetState();
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
        int state = (int)(States)scrController.instance.stateMachine.GetState();
        // 与 Patch_ConductorUpdate 同一组状态：Fail/Fail2/Won（数字含义见上）
        if (state != Patch_ConductorUpdate.StateFail
            && state != Patch_ConductorUpdate.StateFail2
            && state != Patch_ConductorUpdate.StateWon) return;
        if (!PendingSave)
        {
            // 原来这里直接 return：用户按下取消键却毫无反应，会以为按键没绑上或
            // 功能坏了。提示放在这里而不是 CancelSave() 里——只有这一个调用方，
            // 而它在调用前就判了 !PendingSave，CancelSave 里加提示永远走不到。
            // 与"保存已取消"区分开：什么都没取消时不能说已取消。
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
internal class Patch_LightUp
{
    [SpectrePatch(typeof(scrFloor), "LightUp", PatchType.Prefix, Feature = FeatureKeys.Replay)]
    internal static void Prefix()
    {
        if (is_playing) GCS.hitMarginLimit = Persistence.hitMarginLimit;
    }

    [SpectrePatch(typeof(scrFloor), "LightUp", PatchType.Postfix, Feature = FeatureKeys.Replay)]
    internal static void Postfix()
    {
        if (is_playing) GCS.hitMarginLimit = StoredHitMarginLimit;
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
