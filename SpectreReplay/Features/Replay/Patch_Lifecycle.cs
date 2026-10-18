// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using SpectreReplay.Features.Compat;
using UnityEngine;
using UnityEngine.SceneManagement;
using SpectreReplay.API;
using static SpectreReplay.SpectreState;
using static SpectreReplay.Features.Replay.ReplayKeys;

namespace SpectreReplay.Features.Replay;

// ——— scrUIController.WipeToBlack ——————————————————————————
internal class Patch_WipeToBlack
{
    [SpectrePatch(typeof(scrUIController), "WipeToBlack", PatchType.Prefix)]
    internal static void Prefix() => StopAll();
}

// ——— scnEditor.SwitchToEditMode ———————————————----———————
internal class Patch_SwitchToEditMode
{
    [SpectrePatch(typeof(scnEditor), "SwitchToEditMode", PatchType.Prefix)]
    internal static void Prefix(scnEditor __instance) => StopAll();
}

// ——— scrController.Restart ——————————————————————————————————
internal class Patch_Restart
{
    [SpectrePatch(typeof(scrController), "Restart", PatchType.Prefix)]
    internal static void Prefix(scrController __instance) => StopAll();
}

// ——— scrController.QuitToMainMenu ——————————————————————————
internal class Patch_QuitToMainMenu
{
    [SpectrePatch(typeof(scrController), "QuitToMainMenu", PatchType.Prefix)]
    internal static void Prefix(scrController __instance) => StopAll();
}

// ——— scrController.Start_Rewind ————————————————————————————
internal class Patch_StartRewind
{
    [SpectrePatch(typeof(scrController), "Start_Rewind", PatchType.Prefix, Feature = FeatureKeys.Replay)]
    internal static void Prefix()
    {
        if (Settings.KeybdSoundRecordActive && RecordMode)
        {
            // 这里原本还有两行遗留调试：Debug.Log(Settings.MicrophoneDeviceName) 和
            // Debug.Log(ValidateDevice(...))。后者为了让日志有内容又调了一遍
            // ValidateDevice，紧接着下面的条件又调一次——而 ValidateDevice 每次都要
            // 读 Microphone.devices 枚举所有输入设备，于是每次重开都多枚举一轮，
            // 外加两条无来源前缀的日志。Start_Rewind 是重开/换关/进关都会走的热路径。
            if (AudioRecorder.Instance.ValidateDevice(Settings.MicrophoneDeviceName))
                AudioRecorder.Instance.startMicrophone(Settings.MicrophoneDeviceName);
        }
    }

    [SpectrePatch(typeof(scrController), "Start_Rewind", PatchType.Postfix, Feature = FeatureKeys.Replay)]
    internal static void Postfix()
    {
        PlayActions.StopPlaying();
        PlayActions.StopRecording();
        HitLocked = false;
        Settings.checklang();

        Scene scene = SceneManager.GetActiveScene();
        Debug.Log(scene.name);

        switch (scene.name)
        {
            case "scnLevelSelect":
            case "scnLoading":
            case "scnTaroMenu0":
                if (!LateSaveMode) break;
                PlayActions.StopRecording();
                if (Settings.DontSaveWhenMiss)
                {
                    if (HitMarginCompat.FailCount(HitMarginCounts) == 0) SaveFile();
                }
                else SaveFile();
                LateSaveMode = false;
                break;

            default:
                if (scene.name == "scnCLS") break;

                if (PlayMode)
                {
                    TryStartPlayback();
                    break;
                }

                if (RecordMode) TryStartRecording();
                break;
        }
    }

    private static void TryStartPlayback()
    {
        try
        {
            if (!DebugMode || !debug_skip_Verification)
            {
                if (DataIntegrity.Check())
                {
                    TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error7"), 3f, NotifType.Warning);
                    return;
                }
                var blocks = SpectreAPI.SnapshotBlocks();
                if (blocks.Count != 0)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var kv in blocks)
                        sb.Append(kv.Key).Append(':').Append(kv.Value).Append('\n');
                    TriggerMessage(sb.ToString(), 3f, NotifType.Warning);
                    return;
                }
                if (scrPlayerManager.instance.players.Count() != 1)
                {
                TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error8"), 3f, NotifType.Warning);
                    return;
                }
                if (data.bools[IsOfficialLevel] != ADOBase.isOfficialLevel)
                {
                    // TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error1") + data.strings[SongName], 3f, NotifType.Warning);
                    // return;
                }
                if (data.bools[IsOfficialLevel])
                {
                    // if (data.strings[SceneName] != SceneManager.GetActiveScene().name)
                    // {
                    //     TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error1") + data.strings[SongName], 3f, NotifType.Warning);
                    //     return;
                    // }
                    if (!string.IsNullOrEmpty(data.strings[InternalLevelName])
                         && data.strings[InternalLevelName] != GCS.internalLevelName)
                    {
                        TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error1") + data.strings[SongName], 3f, NotifType.Warning);
                        return;
                    }
                }
                int floorHash = ADOBase.isOfficialLevel
                    ? 0
                    : (scnGame.instance.levelData.isOldLevel
                        ? scnGame.instance.levelData.pathData.GetHashCode()
                        : ComputeAngleHash(scnGame.instance.levelData.angleData));
                if (floorHash != data.ints[FloorHash])
                {
                    TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error2"), 3f, NotifType.Warning);
                    return;
                }
                if (!ADOBase.isOfficialLevel
                    && (data.ints[Pitch] != scnGame.instance.levelData.pitch
                        || data.doubles[Bpm] != (double)scnGame.instance.levelData.bpm))
                {
                    TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error2"), 3f, NotifType.Warning);
                    return;
                }
                if (GCS.difficulty.ToString() != data.strings[JudgeMode])
                {
                    TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error3") + data.strings[JudgeMode], 3f, NotifType.Warning);
                    return;
                }
                if (!IsFullRun && data.ints.TryGetValue(EndTile, out var endTileVal))
                {
                    if (GCS.checkpointNum < data.ints[StartTile] || GCS.checkpointNum > endTileVal)
                    {
                        TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error4") + data.ints[StartTile], 3f, NotifType.Warning);
                        return;
                    }
                }
                else if (!IsFullRun && GCS.checkpointNum != data.ints[StartTile])
                {
                    TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error4") + data.ints[StartTile], 3f, NotifType.Warning);
                    return;
                }
                if (data.bools[SpeedTrailMode] || data.bools[QuickPitched])
                {
                    // if (data.strings[SceneName] != SceneManager.GetActiveScene().name)
                    // {
                    //     TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error6"), 3f, NotifType.Warning);
                    //     return;
                    // }
                }
                double expectedSpeed = 1.0;
                double actualSpeed = 1.0;
                if (SceneManager.GetActiveScene().name == "scnEditor")
                {
                    actualSpeed = GCS.editorQuickPitchedPlaying ? scnEditor.instance.playbackSpeed : 1f;
                    expectedSpeed = data.bools[QuickPitched] ? data.doubles[PlaybackSpeed] : 1.0;
                }
                else
                {
                    actualSpeed = (GCS.speedTrialMode || GCS.practiceMode) ? GCS.currentSpeedTrial : 1f;
                    expectedSpeed = data.bools[SpeedTrailMode] ? data.doubles[SpeedTrail] : 1.0;
                }
                if (actualSpeed != expectedSpeed)
                {
                    TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error5") + expectedSpeed, 3f, NotifType.Warning);
                    return;
                }
            }
            else Debug.Log("Debug模式跳过校验");

            // 默认值 (HitMarginLimit)0 = None，是"最宽松"的保守侧。
            // **必须 Enum.IsDefined 拒掉数字串**：`Enum.TryParse("99")` 会成功并
            // 产出 (HitMarginLimit)99，而这个值会被**直接赋给 GCS.hitMarginLimit**——
            // scrFloor.LightUp 与判定路径都会读它。越界值不会抛异常，只会让
            // 判定窗口按一个不存在的档位走，且回放照常开始。回放文件是玩家可编辑的
            // 文本，这一项比 ReplayAnalyzer 里那处更敏感（那里只影响显示宽度）。
            StoredHitMarginLimit = (HitMarginLimit)0;
            if (data.strings.ContainsKey(HitMarginLimitKey)
                && Enum.TryParse<HitMarginLimit>(data.strings[HitMarginLimitKey], out var result)
                && Enum.IsDefined(typeof(HitMarginLimit), result))
                StoredHitMarginLimit = result;
            GCS.hitMarginLimit = StoredHitMarginLimit;
            CachedNoFail = scrController.instance.noFail;
            scrController.instance.noFail = true;
            SyncEditorNoFailButton();

            IsFullRun = data.ints.ContainsKey(StartTile) && data.ints.ContainsKey(EndTile)
                && data.ints[StartTile] == 0
                && data.ints[EndTile] == ADOBase.lm.listFloors.Count - 1;

            if (GCS.checkpointNum != data.ints[StartTile])
                ReplayPlayer.FastForward(GCS.checkpointNum);

            PlayActions.StartPlaying();
            TriggerMessage(LocalizationManager.GetLocalizedText("note.start_playing"));
        }
        catch (Exception ex)
        {
            Debug.Log("load error: " + ex);
            TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error7"), 3f, NotifType.Warning);
        }
    }

    private static void TryStartRecording()
    {
        try
        {
            if (data == null) data = new LegacyReplayData();
            if (LateSaveMode)
            {
                if (Settings.DontSaveWhenMiss)
                {
                    if (HitMarginCompat.FailCount(HitMarginCounts) == 0) SaveFile();
                }
                else SaveFile();
                LateSaveMode = false;
            }
            if (scrPlayerManager.instance.players.Count() != 1)
            {
                TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error8"), 3f, NotifType.Warning);
                return;
            }
            data.reset();
            data.ints[StartTile] = GCS.checkpointNum;
            data.strings[SceneName] = SceneManager.GetActiveScene().name;
            data.strings[JudgeMode] = GCS.difficulty.ToString();
            data.doubles[SpeedTrail] = GCS.currentSpeedTrial;
            data.bools[SpeedTrailMode] = GCS.speedTrialMode || GCS.practiceMode;
            data.strings[InternalLevelName] = GCS.internalLevelName ?? "";
            data.bools[IsOfficialLevel] = ADOBase.isOfficialLevel;
            data.bools[QuickPitched] = GCS.editorQuickPitchedPlaying;
            data.bools[IfNoFail] = ADOBase.controller?.noFail ?? GCS.useNoFail;
            data.ints[FloorHash] = ADOBase.isOfficialLevel
                ? 0
                : (scnGame.instance.levelData.isOldLevel
                    ? scnGame.instance.levelData.pathData.GetHashCode()
                    : ComputeAngleHash(scnGame.instance.levelData.angleData));
            var speeds = new System.Collections.Generic.List<double>();
            var times = new System.Collections.Generic.List<double>();
            for (int i = 0; i < ADOBase.lm.listFloors.Count; i++)
            {
                if (i > 0) times.Add(ADOBase.lm.listFloors[i].entryTime - ADOBase.lm.listFloors[i - 1].entryTime);
                speeds.Add(ADOBase.lm.listFloors[i].speed * (ADOBase.lm.listFloors[i].isCCW ? -1f : 1f));
            }
            data.ints[SpeedHash] = ComputeDoubleListHash(speeds);
            data.ints[TimeHash] = ComputeTimeListHash(times);
            data.strings[LevelPath] = "";
            if (ADOBase.isOfficialLevel)
            {
                data.strings[ArtistName] = "OfficialLevel";
                string songName = GCS.internalLevelName ?? "";
                if (data.strings[SceneName] != "scnGame" && data.strings[SceneName] != "scnEditor")
                    songName = data.strings[SceneName];
                data.strings[SongName] = songName;
                data.strings[LevelPath] = "OfficialLevel " + songName;
                data.ints[Pitch] = 0;
                // **BPM 不能硬写 0。** 这里原先两行都是 0，理由是"官方关卡
                // 不比 Pitch/BPM"——但那是不比，不是不知道。写 0 的直接后果
                // 是详情面板把它显示成「?」（2026-10-18 用户截图报的就是它），
                // 而玩家展开一份蓝盘回放，最想看的信息之一恰恰是这关多快。
                //
                // 填真值不改变任何校验：TryStartPlayback 里那一句
                // `!ADOBase.isOfficialLevel && (Pitch/Bpm 比对)` 对官方关卡
                // 整段跳过，存什么都不会被比。所以这里只是把已知信息记下来。
                //
                // **取值走 conductor 而不是 scnGame.instance.levelData。**
                // 两者是同一个值（scrConductor.SetupConductorWithLevelData 里
                // `bpm = levelData.bpm`），但 scnGame.instance 在编辑器、
                // 回放重开等时点可能为 null，取它会把一次空引用变成录制失败；
                // conductor 的 instance getter 自带 FindAnyObjectByType 兜底。
                // Pitch 仍留 0：面板不显示它，而数值一旦变成真值就会让"官方
                // 关卡 Pitch 恒为 0"这个既有约定悄悄失效。
                data.doubles[Bpm] = CurrentBpm();
            }
            else
            {
                data.strings[ArtistName] = (scnGame.instance.levelData.artist ?? "").Trim();
                string song = (scnGame.instance.levelData.song ?? "").Trim();
                if (string.IsNullOrEmpty(song))
                {
                    song = scnGame.instance.levelData.songFilename;
                    int dot = song.IndexOf('.');
                    if (dot != -1) song = song.Substring(0, dot);
                }
                data.strings[SongName] = song;
                data.strings[LevelPath] = scnGame.instance.levelPath ?? "";
                data.ints[Pitch] = scnGame.instance.levelData.pitch;
                data.doubles[Bpm] = scnGame.instance.levelData.bpm;
            }
            data.doubles[PlaybackSpeed] = SceneManager.GetActiveScene().name == "scnEditor"
                ? scnEditor.instance.playbackSpeed : 1.0;
            data.ints[AudioBufferSize] = Persistence.audioBufferSize;
            data.strings[HoldBehaviorKey] = Persistence.holdBehavior.ToString();
            data.strings[HitMarginLimitKey] = Persistence.hitMarginLimit.ToString();
            data.strings[LoadedMods] = string.Join("\n", Main.GetLoadedMods().Select(m => Main.mod2string(m)));
            data.strings[DeviceID] = SystemInfo.deviceUniqueIdentifier;
            data.strings[ModVersion] = Main.mod.Info.Version;
            PlayActions.StartRecording();
            TriggerMessage(LocalizationManager.GetLocalizedText("note.start_recording"));
        }
        catch (Exception ex)
        {
            Debug.Log("start recording error: " + ex);
        }
    }

    /// <summary>
    /// 当前这关的 BPM（官方关卡也要能用）。
    ///
    /// **为什么不直接用 <c>scnGame.instance.levelData.bpm</c>。** 那是最短的一条路，
    /// 但 <c>scnGame.instance</c> 是 `public static scnGame instance;`（scnGame.cs:28），
    /// 只在 <c>scnGame.Awake</c> 里赋值——编辑器、从回放重开、练习模式这些时点
    /// 取它可能拿到 null，而这里在 <c>TryStartRecording</c> 的 try 里，一旦
    /// 抛异常整次录制就失败（日志里只有一句 "start recording error"）。
    ///
    /// **为什么 conductor 是安全的。** <c>scrConductor.instance</c> 的 getter 自带
    /// <c>FindAnyObjectByType</c> 兜底（scrConductor.cs:270-278），且它的 bpm 就是
    /// `SetupConductorWithLevelData` 从同一份 levelData 抄过来的（:380），两者
    /// 语义完全等价。取不到就返回 0（= 面板显示「?」，与旧行为一致），不抛。
    /// </summary>
    private static double CurrentBpm()
    {
        try
        {
            var c = ADOBase.conductor;
            return c != null ? c.bpm : 0.0;
        }
        catch
        {
            return 0.0;
        }
    }

    // 复用缓冲，避免每元素分配；最终用运行时的 string.GetHashCode()，
    // 与旧版 string.Join("", parts).GetHashCode() 完全等价（同一运行时算法），保证旧 replay 文件兼容。
    // 数值统一 InvariantCulture 格式化：默认 G 格式无分组分隔符、负号同为 "-"，
    // 点号小数区输出与旧行为逐字节一致（存量回放仍兼容）；
    // 逗号小数区不再因区域差异在他机重算出不同哈希而误报 play_error2。
    private static readonly System.Text.StringBuilder _hashBuilder = new System.Text.StringBuilder(4096);

    private static int ComputeAngleHash(IEnumerable<float> angles)
    {
        _hashBuilder.Clear();
        foreach (var x in angles)
        {
            float norm = x >= 0f ? x % 360f : 360f - -x % 360f;
            _hashBuilder.Append(norm.ToString(CultureInfo.InvariantCulture));
        }
        return _hashBuilder.ToString().GetHashCode();
    }

    private static int ComputeDoubleListHash(IEnumerable<double> values)
    {
        _hashBuilder.Clear();
        foreach (var v in values)
            _hashBuilder.Append(v.ToString(CultureInfo.InvariantCulture));
        return _hashBuilder.ToString().GetHashCode();
    }

    private static int ComputeTimeListHash(IEnumerable<double> values)
    {
        _hashBuilder.Clear();
        foreach (var d in values)
            _hashBuilder.Append(d.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        return _hashBuilder.ToString().GetHashCode();
    }
}
