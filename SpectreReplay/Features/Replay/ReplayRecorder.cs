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
        data.ints[EndTile] = scrController.instance.currentSeqID;
        data.strings[JudgmentList] = "[" + string.Join(",", HitMarginCounts) + "]";
        data.doubles[PercentXacc] = ComputePercentXacc();
        data.ints.TryGetValue(MaximumUsedKeys, out int peak);
        int cur = ADOBase.controller?.maximumUsedKeys ?? 0;
        data.ints[MaximumUsedKeys] = Math.Max(peak, cur);
        data.strings[FileName] = CreateFileName();
        if (Settings.KeybdSoundRecordActive)
        {
            data.strings[KeybdSoundFileName] = data.strings[FileName] + ".wav";
        }
        if (AudioRecorder.Instance.IsRecording)
        {
            data.strings[KeybdSoundHash] = AudioRecorder.Instance.StopRecording();
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
            data.ints.TryGetValue(MaximumUsedKeys, out int prev);
            int cur = ADOBase.controller.maximumUsedKeys;
            if (cur > prev)
                data.ints[MaximumUsedKeys] = cur;
        }

        // 异步输入开启时按键由 AsyncKeyRecorder 记录，轮询路径只兜 forceSync（结算画面）
        if (!forceSync && UseAsyncInput)
            return;
        KeyboardSimulation.GetKeyboardState(CurrKeyState);
        var conductor = ADOBase.conductor;
        var events = data.KeyEvent_list;
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
