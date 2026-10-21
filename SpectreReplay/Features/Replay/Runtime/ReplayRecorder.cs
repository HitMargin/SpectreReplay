using System;
using System.Collections.Generic;
using SpectreReplay.Features.Compat;
using UnityEngine;
using static SpectreReplay.SpectreState;
using static SpectreReplay.Features.Replay.ReplayKeys;

namespace SpectreReplay.Features.Replay;

internal static class ReplayRecorder
{
    internal static void Start()
    {
        if (!is_recording)
        {
            Stop();
            PlayIndex = 0;
            KeyEventIndex = 0;
            is_recording = true;
            is_playing = false;
            PendingSave = true;
            HitLocked = false;
            AllAutoHits = true;
            LateSaveMode = false;
            IgnoreMarkFail = false;
            PrevKeyState = new byte[256];
        }
    }

    internal static void Stop()
    {
        if (!is_recording)
        {
            return;
        }
        is_recording = false;
        IgnoreMarkFail = false;
        // 与 Patch_Recording 同理：两个数组按 Max(HitMargin)+1 分配，正常等长。
        // 这里更不能抛——本方法负责**保存回放**，越界异常会连带丢掉整段录制。
        // 用较短者收敛，最多少统计末尾几个桶，回放仍能存下来。
        var marginCounts = scrController.instance.playerOne.marginTracker.hitMarginsCount;
        int count = Math.Min(HitMarginCounts.Length, marginCounts.Length);
        for (int i = 0; i < count; i++)
        {
            HitMarginCounts[i] = marginCounts[i];
        }
        data.EndTile = scrController.instance.currentSeqID;
        data.JudgmentList = "[" + string.Join(",", HitMarginCounts) + "]";
        data.PercentXacc = ComputePercentXacc();
        int peak = data.MaximumUsedKeys ?? 0;
        int cur = ADOBase.controller?.maximumUsedKeys ?? 0;
        data.MaximumUsedKeys = Math.Max(peak, cur);
        data.FileName = CreateFileName();
        if (Settings.KeybdSoundRecordActive)
        {
            data.KeybdSoundFileName = data.FileName + ".wav";
        }
        if (AudioRecorder.Instance.IsRecording)
        {
            data.KeybdSoundHash = AudioRecorder.Instance.StopRecording();
        }
        if (!LateSaveMode)
        {
            return;
        }
        if (Settings.DontSaveWhenMiss)
        {
            if (HitMarginCompat.FailCount(HitMarginCounts) == 0)
            {
                SaveFile();
            }
        }
        else
        {
            SaveFile();
        }
        LateSaveMode = false;
    }

    internal static void RecordKeyboardState(bool forceSync = false)
    {
        if (data == null) return;
        if (ADOBase.controller != null)
        {
            int prev = data.MaximumUsedKeys ?? 0;
            int cur = ADOBase.controller.maximumUsedKeys;
            if (cur > prev)
                data.MaximumUsedKeys = cur;
        }

        // 异步输入开启时按键由 AsyncKeyRecorder 记录，轮询路径只兜 forceSync（结算画面）
        if (!forceSync && UseAsyncInput)
            return;
        KeyboardSimulation.GetKeyboardState(CurrKeyState);
        // **必须自己确认导体可用。** 本方法由 Won_Update / UpdateInput 的补丁
        // 直接调用，而那两个补丁**没有 try**（Patch_Won_Update.Postfix 只对
        // __instance 判了空）：conductor 在结算转场/场景卸载期间会被销毁，
        // 那时解引用抛的是 NRE，会直接灌进游戏自己的 Won_Update，而不是被本模组
        // 吞掉——表现为转场瞬间游戏报错、这一帧的按键与结算处理一起中断。
        // 回放侧（ReplayPlayer）早就为同一理由加了同样的判断，录制侧漏了。
        //
        // 拿不到导体时直接返回：CurrKeyState 本帧已采样过，下一帧会重新采样，
        // 不会留下错位的 PrevKeyState。
        var conductor = ADOBase.conductor;
        if (conductor == null)
            return;
        var events = data.KeyEvents;
        for (int i = 1; i < 256; i++)
        {
            if ((PrevKeyState[i] & 0x80) == 0 && (CurrKeyState[i] & 0x80) != 0)
            {
                events.Add(new KeyEvent
                {
                    KeyCode = (ushort)i,
                    IsPressed = true,
                    SongPosition = conductor.songposition_minusi
                });
            }
            else if ((PrevKeyState[i] & 0x80) != 0 && (CurrKeyState[i] & 0x80) == 0)
            {
                events.Add(new KeyEvent
                {
                    KeyCode = (ushort)i,
                    IsPressed = false,
                    SongPosition = conductor.songposition_minusi
                });
            }
            PrevKeyState[i] = CurrKeyState[i];
        }
    }
}
