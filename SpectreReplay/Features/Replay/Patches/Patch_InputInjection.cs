using System;
using System.Collections.Generic;
using UnityEngine;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Features.Replay;

internal static class Patch_RDInput
{
    // ── 注入时机（**顺序是这套东西的另一半**）──────────────────
    // 游戏在 Simulated_PlayerControl_Update 里读输入（scrPlayer.cs:417/430/433/434），
    // 而那个方法由 PlayerControl_Update 内部调用（scrController.cs:552，同步链）
    // 或由 UpdateInput → ProcessKeyInputs 调用（异步链）。
    // 所以在两条链的**入口 Prefix** 上记账，才能保证"先写后读"。
    // 记在 Postfix 上等于每帧先读后写，事件永远晚一帧。
    [SpectrePatch(typeof(scrController), "PlayerControl_Update", PatchType.Prefix, Feature = FeatureKeys.Replay)]
    internal static void PlayerControl_Prefix()
    {
        try { ReplayPlayer.PumpInput(); }
        catch (Exception ex) { Debug.LogException(ex); }
    }

    // UpdateInput 在 AsyncInputManager.isActive 时由 scrConductor.Update 每帧调用
    // （scrConductor.cs:797-800）。它的早退在方法体内，Prefix 仍在其之前。
    [SpectrePatch(typeof(scrController), "UpdateInput", PatchType.Prefix, Feature = FeatureKeys.Replay)]
    internal static void UpdateInput_Prefix()
    {
        try { ReplayPlayer.PumpInput(); }
        catch (Exception ex) { Debug.LogException(ex); }
    }

    // ── RDInput.GetMain(ButtonState) ──────────────────────────
    // mainPressCount / mainHeldCount 的来源。回放期间整个覆盖。
    [SpectrePatch(typeof(RDInput), "GetMain", PatchType.Postfix, Feature = FeatureKeys.Replay)]
    internal static void GetMain_Postfix(ref int __result, ButtonState state)
    {
        // NativeTakeover 时放行：事件已进 AsyncInputManager.keyQueue，游戏自己按 tick
        // 分组算 mainPressCount。这里再覆盖会把每个 tick 组都算成「本帧刚按下」。
        if (!ManagedInput.Active || ManagedInput.NativeTakeover) return;
        __result = ManagedInput.Count(state);
    }

    // ── RDInput.GetState(InputAction, ButtonState) ────────────
    // action1Press / action2Press / confirmPress / 方向盘等走这里。
    [SpectrePatch(typeof(RDInput), "GetState", PatchType.Postfix, Feature = FeatureKeys.Replay)]
    internal static void GetState_Postfix(ref bool __result, InputAction inputAction, ButtonState state)
    {
        if (!ManagedInput.Active || ManagedInput.NativeTakeover || !IsGameplayAction(inputAction)) return;
        __result = ManagedInput.Matches(inputAction, state);
    }

    // ── RDInput.GetStateKeys(List<AnyKeyCode>, ButtonState) ───
    // GetMainPressKeys / GetMainHeldKeys 的实现，也是 holdKeys 的唯一来源。
    // 必须在这一层覆盖：GetStateKeys 读的是各 RDInputType 内部的 pressCount.keys /
    // heldCount.keys（在 Main 里填充），只改 GetMain 的返回值不会让这些列表有内容。
    [SpectrePatch(typeof(RDInput), "GetStateKeys", PatchType.Postfix, Feature = FeatureKeys.Replay)]
    internal static void GetStateKeys_Postfix(List<AnyKeyCode> keys, ButtonState state)
    {
        // NativeTakeover 时放行：pressCount/heldCount 已由游戏按真实事件累积，
        // 覆盖成「此刻按住的全集」会丢掉 pressCount（WentDown）的语义。
        if (!ManagedInput.Active || ManagedInput.NativeTakeover || keys == null) return;
        keys.Clear();
        ManagedInput.Append(keys, state);
    }

    /// <summary>
    /// 会推动玩法的动作键。只有这些在回放期间被覆盖——
    /// Cancel/Back/Quit/Restart 属于界面控制，必须保留真实键盘，
    /// 否则回放期间按 Esc 暂停不了、按 R 重开不动。
    /// </summary>
    private static bool IsGameplayAction(InputAction action)
    {
        switch (action)
        {
            case InputAction.Action1:
            case InputAction.Action2:
            case InputAction.Confirm:
            case InputAction.Left:
            case InputAction.Right:
            case InputAction.Up:
            case InputAction.Down:
            case InputAction.LeftAlt:
            case InputAction.RightAlt:
            case InputAction.FaceUp:
            case InputAction.FaceLeft:
                return true;
            default:
                return false;
        }
    }
}

internal static class Patch_HoldStateInjection
{
    [SpectrePatch(typeof(scrPlayer), "ValidInputWasReleased", PatchType.Postfix, Feature = FeatureKeys.Replay)]
    internal static void Postfix(scrPlayer __instance)
    {
        try
        {
            if (!ManagedInput.Active || __instance == null) return;

            List<AnyKeyCode> keys = __instance.holdKeys;
            if (keys == null) return;
            keys.Clear();
            // **必须与 ValidInputWasReleased 内部比较的那份数据同源**（scrPlayer.cs:880）。
            // 原生异步链接管时，它取的是 keyMask 里的 AsyncKeyCode(key, label)：
            //   · 粒度：那条链是**事件 tick** 粒度，ManagedInput 是**帧**粒度；
            //   · 形态：RDInputType_AsyncKeyboard.Main 还会按 label 过滤（:225），
            //     合成的 Label=Unknown 值进不了游戏的 heldCount。
            // 两套状态从源头分叉的后果是：holding 短暂为真、下一帧又被判松手。
            if (ManagedInput.NativeTakeover)
                RDInput.GetMainHeldKeys(keys);
            else
                ManagedInput.Append(keys, ButtonState.IsDown);
        }
        catch
        {
            // 注入是锦上添花：任何异常都不能影响原版判定流程。
        }
    }
}