// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using static SpectreReplay.SpectreState;
using static SpectreReplay.Features.Replay.ReplayKeys;

namespace SpectreReplay.Features.Replay;

// ——— scrController.Won_Update ———————————————————————————————
internal class Patch_Won_Update
{
    [SpectrePatch(typeof(scrController), "Won_Update", PatchType.Postfix, Feature = FeatureKeys.Replay)]
    internal static void Postfix(scrController __instance)
    {
        if (__instance == null) return;
        if (is_recording && Application.isFocused) Record_kbdstate(forceSync: true);
        if (is_playing) Play_keybd_event(flag: true);
    }
}

internal static class ReplayAngleGuard
{
    internal static bool IsSane(float angle)
        => !float.IsNaN(angle) && !float.IsInfinity(angle);
}

// ——— scrPlayer.UpdateHoldKeys ———————————————————————————————
// 游戏此处有个潜伏 bug：命中 midspin 时 Hit() 尾部会向 keyTimes 自补一个待处理
// 按键，函数末段 midspin 分支的第二次 RemoveAt(0) 假定这个补充一定还在。若中途
// 那次 Hit() 提前返回 false（无响应窗口、忽略输入特效等），既不推进地板也不补
// 条目，第二次 RemoveAt 就在空表上抛越界。正常游玩几乎凑不齐触发条件；回放引擎
// 按量化时间注入按键，偶尔恰好凑齐（midspin 链上 keyTimes 恰剩 1 项 + hold 守卫
// 放行 + Hit 空转），渲染期子帧回拨挪动守卫边界，概率进一步上升。
// 抛出只中断当帧的输入模拟——回放状态每帧由录制数据覆盖，吞掉不影响回放/成片。
internal class Patch_HoldKeysCrashGuard
{
    [SpectrePatch(typeof(scrPlayer), "UpdateHoldKeys", PatchType.Finalizer, Feature = FeatureKeys.Replay)]
    internal static Exception Finalizer(Exception __exception)
    {
        if (__exception == null || !is_playing || !(__exception is ArgumentOutOfRangeException))
            return __exception;
        Debug.Log("[SpectreReplay] replay hit the game's UpdateHoldKeys index bug (midspin keyTimes), swallowed");
        return null;
    }
}

// ——— scrController.PlayerControl_Update / Countdown_Update / Checkpoint_Update ——
// 多目标：同一方法挂三个 [SpectrePatch]
internal class Patch_PlayerControlUpdate
{
    [SpectrePatch(typeof(scrController), "PlayerControl_Update", PatchType.Postfix, Feature = FeatureKeys.Replay)]
    [SpectrePatch(typeof(scrController), "Countdown_Update", PatchType.Postfix, Feature = FeatureKeys.Replay)]
    [SpectrePatch(typeof(scrController), "Checkpoint_Update", PatchType.Postfix, Feature = FeatureKeys.Replay)]
    internal static void Postfix(scrController __instance)
    {
        if (__instance == (UnityEngine.Object)null) return;

        if (is_recording && Settings.KeybdSoundRecordActive
            && !AudioRecorder.Instance.IsRecording
            && !data.KeybdSoundStartTick.HasValue)
        {
            string filePath = Path.Combine(Settings.SavePath, "tmp.wav");
            // 清掉遗留的 tmp.wav：若上次录音因崩溃或强退而异常结束，tmp.wav 会留在
            // 磁盘上（SaveFile 才会把它改名挂到回放上）。不清理的话，这次录到的声音
            // 会被残留文件覆盖，回放挂错键盘音且极难察觉。
            try
            {
                if (File.Exists(filePath)) File.Delete(filePath);
            }
            catch (Exception ex)
            {
                // 清不掉不阻断录音：旧文件顶多是音频不对，录了总比不录好
                Debug.LogWarning("[SpectreReplay] 清理陈旧 tmp.wav 失败（本次录音可能挂上旧音频）: " + ex.Message);
            }
            AudioRecorder.Instance.StartRecording(filePath, Settings.MicrophoneDeviceName);
            // 起录帧拿不到导体是常态（倒计时/检查点阶段切场景）。**不能在这里 return**：
            // 那会同时跳过本帧的 Record_kbdstate，更糟的是 tick 永远缺着——回放侧
            // Patch_Playback:187 判到没有 KeybdSoundStartTick 就静默 return，
            // 键盘声整段消失且没有任何提示（HasKeybdSound 仍为 true，没有别处会报）。
            // 记 0 占位：它让下面"缺 tick 就补"的分支接管，导体就绪后立刻补真值。
            data.KeybdSoundStartTick = 0.0;
        }

        // 补起始时间戳：起录帧没有导体时留下的是 0，这里每帧试着换成真值。
        // 只补一次（非 0 即视为已记），不覆盖已记下的真值。
        if (is_recording && Settings.KeybdSoundRecordActive
            && AudioRecorder.Instance.IsRecording
            && data.KeybdSoundStartTick is double pendingTick
            && pendingTick == 0.0)
        {
            scrConductor tickConductor = ADOBase.conductor;
            if (tickConductor != null)
                data.KeybdSoundStartTick = tickConductor.songposition_minusi;
        }

        if (is_recording && Application.isFocused) Record_kbdstate();
        if (!is_playing) return;

        if (PlayIndex < 0) { KeyEventIndex = 0; PlayIndex = 0; }

        var hitSnapshots = data.HitSnapshots;
        if (hitSnapshots.Count == 0)
        {
            PlayActions.StopPlaying();
            TriggerMessage(LocalizationManager.GetLocalizedText("note.data_empty"));
            return;
        }

        if (PlayIndex < hitSnapshots.Count)
        {
            var hc = hitSnapshots[PlayIndex];
            if (__instance.currFloor.seqID != hc.CurrentFloorID
                || __instance.curFreeRoamSection > hc.curFreeRoamSection)
            {
                while (PlayIndex < hitSnapshots.Count
                    && (hitSnapshots[PlayIndex].CurrentFloorID < __instance.currFloor.seqID
                        || hitSnapshots[PlayIndex].curFreeRoamSection < __instance.curFreeRoamSection))
                    PlayIndex++;
                if (PlayIndex >= hitSnapshots.Count)
                {
                    PlayActions.StopPlaying();
                    TriggerMessage(LocalizationManager.GetLocalizedText("note.data_empty"));
                    return;
                }
                hc = hitSnapshots[PlayIndex];
            }

            // **必须传 __instance**，不能用无参 GetAngle()：后者读静态 scrController.instance，
            // 与这里判定砖块的 __instance 在编辑器/场景切换中间态可能不是同一个对象。
            float angle = GetAngle(__instance);
            // ── 单帧命中上限 + 角度健全性检查 ─────────────────────────
            // 这个 while 是**不可逆**的：每轮 PlayIndex++，一旦某一帧角度算坏
            // （Infinity 会让 `angle >= CurrAngle` 恒真），整份命中快照会在**一帧内**
            // 被吃完——玩家看到的就是「播放时一瞬间模拟完」，而且回不来。
            //
            // 角度算坏的入口不止一个：conductor.Rewind() 会把 crotchetAtStart 置 0
            // （scrConductor.cs:355），下一次 Update 才由 60/bpm 补回；这中间的
            // `(songpos - lastHit) / crotchetAtStart` 就是 ±Infinity。
            //
            // 所以两道闸都要有：
            //   ① 非有限角度 → 本帧一次都不许吃（NaN 虽然不会被 >= 命中，但别依赖这点）；
            //   ② 单帧上限 → 即使角度合法地很大（极小 bpm / 极端倍速），也只推进
            //      有限步，宁可慢一帧也不要把整份数据吞掉。
            const int MaxHitsPerFrame = 256;
            int drainedThisFrame = 0;
            while (__instance.currFloor.seqID == hc.CurrentFloorID
                && (double)angle >= hc.CurrAngle
                && __instance.curFreeRoamSection == hc.curFreeRoamSection
                && ReplayAngleGuard.IsSane(angle)
                && drainedThisFrame < MaxHitsPerFrame)
            {
                // 用补丁入参 __instance 而不是 scrController.instance：入参由 Harmony 传入、
                // 必然是正在执行该方法的实例，而静态 instance 在场景卸载/重载的中间态
                // 可能为 null 或已指向另一个实例
                ReplayHit(__instance.playerOne, hc);
                // 渲染期子帧回拨：放在 ReplayHit 之后——命中内部状态全部来自录制数据、
                // 与 pin 无关；回拨只影响本帧后续的行星刷新/相机/渲染，让画面呈现恰好
                // 命中而非越过后（越过量 0~1/输出帧率，密集段每帧多命中会叠加）。
                // pin 每帧由渲染循环重算，回拨只在当帧生效，不累积
                if (Features.VideoRender.VideoRender.Active && angle > hc.CurrAngle)
                {
                    // 渲染期回拨同样要防 conductor 为空：缺失时只是这次不做回拨，
                    // 而抛异常会打断整帧的命中推进
                    scrConductor cond = ADOBase.conductor;
                    if (cond == null) break;
                    double overRad = angle - hc.CurrAngle;
                    double speed = __instance.playerOne.planetarySystem.speed;
                    if (Math.Abs(speed) > 1e-6)
                    {
                        double dt = overRad * cond.crotchetAtStart / (Math.PI * Math.Abs(speed));
                        Features.VideoRender.VideoRender.PinnedSongPosition =
                            cond.songposition_minusi - dt;
                    }
                }
                PlayIndex++;
                drainedThisFrame++;
                if (PlayIndex >= hitSnapshots.Count) break;
                angle = GetAngle(__instance);
                hc = hitSnapshots[PlayIndex];
                if (__instance.currFloor.seqID != hc.CurrentFloorID
                    || __instance.curFreeRoamSection > hc.curFreeRoamSection)
                {
                    break;
                }
            }
        }

        Play_keybd_event(flag: false);

        if (PlayIndex >= hitSnapshots.Count
            && KeyEventIndex >= data.KeyEvents.Count)
        {
            KeyEventIndex = 0;
            PlayIndex = 0;
            PlayActions.StopPlaying();
            TriggerMessage(LocalizationManager.GetLocalizedText("note.stop_playing"));
            return;
        }

        // Keyboard sound playback sync
        if (!HasKeybdSound)
            return;

        // HasKeybdSound 只保证载入那一刻 clip 非空（ReplayManager:275）。AudioClip /
        // AudioSource 都是 Unity 对象，被销毁后是假 null：标志仍为 true，但读
        // .length / .time 会抛 MissingReferenceException——本补丁挂在
        // PlayerControl_Update 这类每帧调用的 Postfix 上，抛一次就打断一整帧的
        // 输入更新。判空用 Unity 语义（==，能识别已销毁）并顺手清标志。
        if (WavLoader.loaded_clip == null || WavLoader.musicSource == null)
        {
            HasKeybdSound = false;
            return;
        }

        if (data.KeybdSoundStartTick is not double keybdStartTick)
            return;
        // tick 为 0 = 起录帧没拿到导体、之后也一直没补上（极端情况：整个倒计时
        // 阶段导体都未就绪）。此时拿 0 当基准会把 wav 从头对齐到歌曲中段，
        // 键盘声整体前移一大段且听起来"像在乱响"——不如整段不播。
        // HasKeybdSound 一并清掉，避免每帧白算。
        if (keybdStartTick <= 0.0)
        {
            HasKeybdSound = false;
            return;
        }

        // conductor / song 为空时直接返回：本补丁挂在 PlayerControl_Update 这类每帧
        // 调用的 Postfix 上，缺这一句就是每帧一次异常（同上面对 loaded_clip 的处理）。
        // 判空用 Unity 语义（==，能识别已销毁对象）。
        scrConductor conductor = ADOBase.conductor;
        if (conductor == null || conductor.song == null)
        {
            HasKeybdSound = false;
            return;
        }

        // 键盘声是实时录音，wav 时间轴 = 真实时间；songposition 是歌曲时间，n 倍速谱面
        // 下推进速度是墙钟的 n 倍。song.pitch 即歌曲实际播放速率（倍速/调速/移调都在这），
        // 歌曲时间差先除回速率换算成真实时间，AudioSource 保持 1× 播放——否则 wav 被
        // 时间压缩 n 倍（倍速谱面键盘声提前跑完、音调变尖），两个 offset 也只在真实时间域有效
        float rate = Mathf.Max(0.05f, Mathf.Abs(conductor.song.pitch));
        float num = ((float)(conductor.songposition_minusi
            - keybdStartTick) / rate
            + (float)scrConductor.currentPreset.inputOffset / 1000f
            + (float)Settings.MicrophoneOffset / 1000f);

        if (num >= 0f && (double)num < WavLoader.loaded_clip.length - 0.1)
        {
            if (Math.Abs(WavLoader.musicSource.time - num) > 0.02f)
                WavLoader.musicSource.time = num;
            WavLoader.musicSource.pitch = 1f;
            if (!WavLoader.musicSource.isPlaying)
                WavLoader.Play_keybdsound(WavLoader.loaded_clip, (float)Settings.KeybdSoundVolume / 10f);
        }
        else if (WavLoader.musicSource.isPlaying)
            WavLoader.Stop_keybdsound();
    }
}
