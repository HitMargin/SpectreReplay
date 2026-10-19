using System;
using System.Collections.Generic;
using System.Linq;
using SpectreReplay.Features.Compat;
using UnityEngine;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Features.Replay;

internal static class ReplayPlayer
{
    // 复用列表，避免 PlayKeyboardEvent 每帧分配 List<INPUT>
    private static readonly List<KeyboardSimulation.INPUT> _inputBuffer = new List<KeyboardSimulation.INPUT>(256);
    internal static void Start()
    {
        if (!is_playing && !ApiBlockPlaying)
        {
            Stop();
            CurrKeyState = new byte[256];
            PlayIndex = 0;
            KeyEventIndex = 0;
            is_playing = true;
            is_recording = false;
            HitLocked = true;
            LateSaveMode = false;
            IgnoreMarkFail = true;
        }
    }

    internal static void Stop()
    {
        if (!is_playing)
        {
            return;
        }
        GCS.hitMarginLimit = Persistence.hitMarginLimit;
        is_playing = false;
        if (PlayMode && Settings.LegacyEngine)
        {
            var releases = new List<KeyboardSimulation.INPUT>();
            for (int i = 1; i < 256; i++)
            {
                if ((CurrKeyState[i] & 0x80) != 0)
                {
                    releases.Add(KeyboardSimulation.MakeInput((byte)i, false, IsExtendedKey((ushort)i)));
                }
            }
            KeyboardSimulation.SendInputs(releases);
        }
        // 场景已卸时 instance 为 null：noFail 无从复原，但后续清理（键盘音停播、
        // CachedNoFail/IgnoreMarkFail 复位）必须照常跑到尾
        var controller = scrController.instance;
        if (controller != null)
            controller.noFail = CachedNoFail ?? false;
        CachedNoFail = null;
        IgnoreMarkFail = false;
        SyncEditorNoFailButton();
        try
        {
            WavLoader.Stop_keybdsound();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }

    internal static void FastForward(int targetFloor)
    {
        if (data == null) return;

        PlayIndex = 0;
        for (int i = 0; i < data.HitContext_list.Count; i++)
        {
            if (data.HitContext_list[i].CurrentFloorID >= targetFloor)
            {
                PlayIndex = i;
                break;
            }
        }

        // 边界检查要含**下界**：只判上界时负的 targetFloor 会通过检查，
        // 紧接着 listFloors[targetFloor] 抛 IndexOutOfRangeException
        if (ADOBase.lm != null && targetFloor >= 0 && targetFloor < ADOBase.lm.listFloors.Count)
        {
            double targetTime = ADOBase.lm.listFloors[targetFloor].entryTime;
            KeyEventIndex = 0;
            for (int i = 0; i < data.KeyEvent_list.Count; i++)
            {
                if (data.KeyEvent_list[i].SongPosition >= targetTime)
                {
                    KeyEventIndex = i;
                    break;
                }
            }
        }

        CurrKeyState = new byte[256];
        AllAutoHits = false;
    }

    internal static void PlayKeyboardEvent(bool flag)
    {
        // data 为空时（回放数据尚未装载/已被清空）直接返回：本方法每帧被调用，
        // 缺这一句就是每帧一次 NullReferenceException
        if (data == null || data.KeyEvent_list == null)
        {
            return;
        }
        if (KeyEventIndex < 0 || KeyEventIndex >= data.KeyEvent_list.Count)
        {
            return;
        }
        // 本方法由 scrController.Won_Update 的 Postfix **每帧**调用（结算阶段亦如此）。
        // conductor 为空/已销毁时（场景卸载中、关卡尚未就绪）直接返回：否则每帧抛一次
        // 异常，而 Unity 已销毁的对象再访问其成员会抛 MissingReferenceException。
        scrConductor conductor = ADOBase.conductor;
        if (conductor == null)
        {
            return;
        }
        double songPosition = conductor.songposition_minusi;
        // 无待办事件时提前返回，避免空 List 分配
        if (songPosition < data.KeyEvent_list[KeyEventIndex].SongPosition)
            return;

        var inputs = _inputBuffer;
        inputs.Clear();
        while (KeyEventIndex < data.KeyEvent_list.Count
            && songPosition >= data.KeyEvent_list[KeyEventIndex].SongPosition)
        {
            var ev = data.KeyEvent_list[KeyEventIndex];
            if (Settings.LegacyEngine && Application.isFocused)
            {
                // 回放文件里的键码是 ushort，损坏/伪造文件可能越界；映射表恒为 256 长度，
                // 直接索引会在每帧回放里抛 IndexOutOfRange。越界键按被屏蔽键（0）处理跳过
                ushort num = ev.KeyCode < Settings.key_code_convert.Length
                    ? Settings.key_code_convert[ev.KeyCode]
                    : (ushort)0;
                if (num != 0 && num != 27 && !IsBlockedKey(num))
                {
                    inputs.Add(KeyboardSimulation.MakeInput((byte)num, ev.IsPressed, IsExtendedKey(num)));
                    CurrKeyState[num] = (byte)(ev.IsPressed ? 128u : 0u);
                }
            }
            KeyEventIndex++;
        }
        KeyboardSimulation.SendInputs(inputs);
        if (KeyEventIndex >= data.KeyEvent_list.Count)
        {
            if (flag)
            {
                KeyEventIndex = 0;
                PlayIndex = 0;
                Stop();
                TriggerMessage(LocalizationManager.GetLocalizedText("note.stop_playing"));
            }
        }
    }

    internal static bool ReplayHitOld(scrPlayer player, HitContext hitdata)
    {
        player.consecMultipressCounter = 0;
        // 命中路径上 controller 一定存在（游戏正是在派发命中），但场景卸载的中间态里
        // 静态 instance 可能已为 null。**只跳过依赖 controller 的两项赋值**并继续往下走：
        // 中途 return 会让后续的行星角度 / auto 标志 / SwitchChosen 都不执行，留下
        // "半个命中"的状态（行星没转、auto 没设），比缺两项 controller 级设置更糟。
        scrController controller = scrController.instance;
        if (controller != null)
        {
            controller.multipressPenalty = false;
            controller.noFailInfiniteMargin = hitdata.NoFailHit;
        }
        player.failBar.overloadCounter = 0f;
        bool isAuto = hitdata.IsAuto;
        player.planetarySystem.chosenPlanet.SetTargetExitAngle(hitdata.TargetExitAngle);
        player.midspinInfiniteMargin = hitdata.MidspinInfiniteMargin;
        player.failBar.overloadCounter = hitdata.OverloadCounter;
        player.planetarySystem.chosenPlanet.angle = hitdata.CachedAngle;
        RDC.auto = hitdata.IsAuto;
        player.responsive = true;
        IgnoreMarkFail = false;
        if (hitdata.NoFailHit)
        {
            scrMissIndicator val = player.planetarySystem.chosenPlanet.MarkFail();
            if (val != null)
            {
                val.BlinkForSeconds(3f);
            }
        }
        scrMisc.Vibrate(50L);
        if (!player.responsive || player.isReunifying)
        {
            return false;
        }
        if (ADOBase.isLevelEditor && ADOBase.controller.paused)
        {
            return false;
        }
        bool flag = player.planetarySystem.chosenPlanet.currfloor.nextfloor != null && player.planetarySystem.chosenPlanet.currfloor.nextfloor.auto;
        player.planetarySystem.chosenPlanet.cachedAngle = player.planetarySystem.chosenPlanet.angle;
        scrFloor currFloor = player.planetarySystem.chosenPlanet.player.currFloor;
        player.planetarySystem.chosenPlanet.next.planetRenderer.ChangeFace(true, false);
        scrPlanet chosenPlanet = player.planetarySystem.chosenPlanet;
        // SwitchChosen 在 r150 起多了一个 long? hitTick 形参：老版 0 参、新版 1 参，
        // 直接调用会按编译期签名绑定，旧游戏上抛 MissingMethodException，必须走兼容层择路
        player.planetarySystem.chosenPlanet = GameCompat.SwitchChosen(player.planetarySystem.chosenPlanet);
        bool result = chosenPlanet != player.planetarySystem.chosenPlanet;
        // r150 起准度条喂数搬进了 scrController.UpdateHitErrorMeter（由 scrPlayer.Hit 调用，
        // 内部还会自取 HitMargin 决定 tick 颜色）。此处若再按老签名补一次就会双记。
        // 有 UpdateHitErrorMeter 的版本一律交给游戏自己喂
        if (!GameCompat.HasUpdateHitErrorMeter
            && ADOBase.controller.errorMeter != null && ADOBase.controller.gameworld
            && (int)Persistence.hitErrorMeterSize > 0)
        {
            float num = (float)(chosenPlanet.cachedAngle - chosenPlanet.targetExitAngle);
            if (currFloor.isCCW)
            {
                num *= -1f;
            }
            if (!player.midspinInfiniteMargin)
            {
                // 走到这里必然是旧版（r150 起由 UpdateHitErrorMeter 自喂，上面已 return 掉）：
                // 旧 AddHit 没有 HitMargin 形参，margin 传参会被兼容层丢弃，仅供新签名分支占位
                if ((player.auto || flag) && !RDC.useOldAuto)
                {
                    GameCompat.ErrorMeterAddHit(ADOBase.controller.errorMeter, 0f,
                        HitMarginCompat.Absent, 1f, player.planetarySystem.chosenPlanet, currFloor);
                }
                else
                {
                    GameCompat.ErrorMeterAddHit(ADOBase.controller.errorMeter, num,
                        HitMarginCompat.Absent, (float)player.currFloor.marginScale,
                        player.planetarySystem.chosenPlanet, currFloor);
                }
            }
        }
        if (ADOBase.playerIsOnIntroScene)
        {
            return result;
        }
        bool flag2 = player.planetarySystem.chosenPlanet.currfloor.holdLength == -1 || (player.planetarySystem.chosenPlanet.currfloor.holdLength > -1 && ADOBase.controller.lastCamPulseFloor < player.planetarySystem.chosenPlanet.currfloor.seqID);
        ADOBase.controller.lastCamPulseFloor = player.planetarySystem.chosenPlanet.currfloor.seqID;
        scrCamera camy = ADOBase.controller.camy;
        if (flag2)
        {
            camy.UpdateFollowCam(false);
        }
        if (camy.isPulsingOnHit && flag2)
        {
            camy.Pulse();
        }
        bool flag3 = true;
        if (ADOBase.lm != null && ADOBase.controller.gameworld)
        {
            if (player.currFloor.midSpin || (player.currFloor.seqID > 0 && ADOBase.lm.listFloors[player.currFloor.seqID - 1].holdLength > -1))
            {
                flag3 = false;
            }
            if (player.currFloor.seqID > 1 && ADOBase.lm.listFloors[player.currFloor.seqID - 1].midSpin && ADOBase.lm.listFloors[player.currFloor.seqID - 2].holdLength > -1)
            {
                flag3 = false;
            }
        }
        if (flag3)
        {
            if (scnEditor.instance != null)
            {
                scnEditor.instance.OttoBlink();
            }
            if (VirtualAvatarCanvas.instance != null)
            {
                VirtualAvatarCanvas.instance.Hit(player.playerID);
            }
        }
        if (player.currFloor.midSpin)
        {
            player.midspinInfiniteMargin = true;
            player.keyTimes.Add(Time.unscaledTimeAsDouble);
        }
        else
        {
            player.midspinInfiniteMargin = false;
        }
        player.planetarySystem.chosenPlanet.Update_RefreshAngles();
        if (!hitdata.NextFloorAuto && hitdata.RDC_auto)
        {
            RDC.auto = true;
        }
        else
        {
            RDC.auto = false;
        }
        // 收尾：controller 缺失时跳过（不抛）
        scrController tailController = scrController.instance;
        if (tailController != null)
        {
            tailController.noFailInfiniteMargin = false;
        }
        IgnoreMarkFail = true;
        return result;
    }

    internal static bool ReplayHit(scrPlayer player, HitContext hitdata)
    {
        if (debug_use_old_hit)
        {
            return ReplayHitOld(player, hitdata);
        }
        player.consecMultipressCounter = 0;
        // 同 ReplayHitOld：只跳过依赖 controller 的两项，其余照常
        scrController controller = scrController.instance;
        if (controller != null)
        {
            controller.multipressPenalty = false;
            controller.noFailInfiniteMargin = hitdata.NoFailHit;
        }
        player.failBar.overloadCounter = 0f;
        bool isAuto = hitdata.IsAuto;
        player.planetarySystem.chosenPlanet.SetTargetExitAngle(hitdata.TargetExitAngle);
        player.midspinInfiniteMargin = hitdata.MidspinInfiniteMargin;
        player.failBar.overloadCounter = hitdata.OverloadCounter;
        player.planetarySystem.chosenPlanet.angle = hitdata.CachedAngle;
        RDC.auto = hitdata.IsAuto;
        player.responsive = true;
        IgnoreMarkFail = false;
        if (hitdata.NoFailHit)
        {
            scrMissIndicator val = player.planetarySystem.chosenPlanet.MarkFail();
            if (val != null)
            {
                val.BlinkForSeconds(3f);
            }
        }
        HitLocked = false;
        // 【绝不透传录制的 HitTick】它是 SkyHook 的原始 tick，换算歌曲时间要减
        // AsyncInputManager.offsetTick——那是【本次运行】启动时才校准的锚点，每局都不同。
        // 拿录制局的原始 tick 在回放局换算会得到完全错位的时间差 → GetHitMarginInSec
        // 判出不计数 → 回放强制 noFail 吞掉失败 → 行星原地打转、永不前进（"卡输入"）。
        // 回放已精确恢复了 angle / targetExitAngle：传 null 让 SwitchChosen 走
        // GetHitMarginInDeg 的角度分支（录制前缀在 AdjustAngle 之后取角，角度里已含
        // tick 的精确度），判定与录制瞬间一致。HitTick 仅作录制元数据保留在文件里。
        bool result = GameCompat.PlayerHit(player, isAuto, null);
        HitLocked = true;
        if (!hitdata.NextFloorAuto && hitdata.RDC_auto)
        {
            RDC.auto = true;
        }
        else
        {
            RDC.auto = false;
        }
        // 收尾：controller 缺失时跳过（不抛）
        scrController tailController = scrController.instance;
        if (tailController != null)
        {
            tailController.noFailInfiniteMargin = false;
        }
        IgnoreMarkFail = true;
        return result;
    }
}
