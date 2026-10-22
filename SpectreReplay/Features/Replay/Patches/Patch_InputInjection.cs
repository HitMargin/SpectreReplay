using System;
using System.Collections.Generic;
using UnityEngine;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Features.Replay;

// ——— 回放输入注入（跨平台）————————————————————————————————
//
// 游戏读键盘只有一个入口：RDInput（RDInput.cs:282-371）。它遍历 inputs 里每个
// RDInputType，而每个实现最终都落在两条物理输入上：
//   · RDInputType_Keyboard      → Input.GetKey*（Unity 旧输入，需要 OS 级按键事件）
//   · RDInputType_AsyncKeyboard → AsyncInput.GetKey* → AsyncInputManager.keyMask
//                                 （只由 SkyHook 的真实键盘回调写入）
// 没有任何一条路能被"回放数据"写入。于是非 Windows / 未聚焦时：
//   · mainPressCount 恒 0 → ValidInputWasTriggered 恒假 → keyTimes 永不灌入
//     → UpdateHoldKeys 里一次 Hit 都不会发生；
//   · GetMainHeldKeys 恒空 → scrPlayer.holdKeys 恒空 → holding 恒假
//     → CheckPreHoldFail 把长按判成松手并 Die()。
//
// ── 去重：用「覆盖」而不是「叠加」 ──────────────────────────
// 第一版把托管状态**加到**游戏读数上，那就必须保证 SendInput 没在同时发键，
// 于是引入「SendInput 是否可用」这个前提。那个前提是灾难性的：
//   · 平台探针判错（Application.platform 在某些环境下报的是别的值）
//   · 一次 UIPI 失败就永久置 Broken
// 任一发生，SendInput 与托管注入**两条路一起关掉**，回放整场不动。
//
// 现在回放期间**直接覆盖**游戏读数：返回值就是回放状态本身。
//   · 不需要知道 SendInput 在不在跑 → 不可能因此关掉任何一条路
//   · 不可能重复计数（一次按键永远只算一次）
//   · SendInput 保持原样，不受任何新判据影响
// 覆盖只针对**游戏操作类**查询；Cancel/Back/Quit 这类界面控制仍然放行真实键盘，
// 否则回放期间玩家按 Esc 暂停不了。
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

// ——— scrPlayer.ValidInputWasReleased ————————————————————————
//
// 光让 RDInput 报出按住键还不够：scrPlayer.holdKeys 只在 UpdateHoldKeys 里、
// 且**只有 Hit 返回 true 之后**才会被 IterateValidKeysHeld 重新填充
// （scrPlayer.cs:718-730）。回放期间真实 Hit 被 Patch_Hit 的前缀拦下
// （HitLocked = true，判定改由角度驱动的 ReplayHit），所以那条填充路径不会发生，
// holdKeys 恒空、holding 恒假。
//
// 这里在游戏自己的清理逻辑**之后**把 holdKeys 同步成回放此刻的按住集合：
//   · 时机：ValidInputWasReleased() 在 scrPlayer.cs:417 执行，CheckPreHoldFail
//     在 :433；后者是唯一读 holding 的死亡判据，所以必须用 Postfix。
//   · 内容：真实键码，不是 AnyKeyCode(null) 占位。null 项会被
//     ValidInputWasReleased 静默 RemoveAt（scrPlayer.cs:884-887），那种写法
//     只是骗过死亡检查，游戏其它读 holdKeys 的地方都拿不到键。
//   · 来源：**必须与 ValidInputWasReleased 内部用来比对的那份数据同源**
//     （scrPlayer.cs:880）。原生异步链接管时那是 RDInput.GetMainHeldKeys，
//     所以这里也要问游戏同一个问题，而不是灌自己的帧粒度状态。
//   · 触摸端跳过：触摸分支只在 Input.touchCount == 0 && !Input.anyKey 时才清
//     holdKeys，强行注入会一直留着。桌面端 touchEnabled 恒 false。
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