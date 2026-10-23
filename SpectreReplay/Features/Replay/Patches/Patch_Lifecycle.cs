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
    internal static void Prefix(scnEditor __instance)
    {
        Features.VideoRender.VideoRender.CancelForSceneExit();
        StopAll();
    }
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
    internal static string LastReject;

    internal static void ResetRejectLog() => LastReject = null;

    [SpectrePatch(typeof(scrController), "Start_Rewind", PatchType.Prefix, Feature = FeatureKeys.Replay)]
    internal static void Prefix()
    {
        // 捕获期间再来一次 Start_Rewind = 关卡被重置，时间轴锚点立即失效。
        // 不中止的话渲染会一直钉在旧锚点上，表现为「回放不动、进度条也不动」。
        // 内部自带 CaptureInProgress 守卫：启动阶段的多次 Start_Rewind 是正常的，
        // 不会被误判（那时捕获循环还没开始）。
        VideoRender.VideoRender.NotifyPlaybackRestarted();
        // 渲染期把倍速钉回回放记录值。scnEditor.Play() 在程序化调用时无条件
        // 写成 1x，而 Start_Rewind 是重开/换关的统一点——这里每轮补一次，
        // TryStartPlayback 的倍速校验才可能通过（详见 VideoRender 的说明）。
        if (VideoRender.VideoRender.Active)
            VideoRender.VideoRender.ApplySpeedOverrideTo(ADOBase.editor);
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
        Debug.Log(scene.name + " [模式] PlayMode=" + PlayMode + " RecordMode=" + RecordMode
            + " is_playing=" + is_playing + " is_recording=" + is_recording);

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

    private static void Reject(string userMessage, string detail = null)
    {
        // 同一份原因既进日志，也留给渲染的等待阶段快速失败用。只留最后一条。
        LastReject = userMessage + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")");
        Debug.Log("[SpectreReplay] TryStartPlayback 拒绝: " + userMessage
            + (string.IsNullOrEmpty(detail) ? "" : " | " + detail));
        TriggerMessage(userMessage, 3f, NotifType.Warning);
    }

    private static void TryStartPlayback()
    {
        try
        {
            Debug.Log("[SpectreReplay] TryStartPlayback 进入（PlayMode=" + PlayMode
                + " RecordMode=" + RecordMode + " is_playing=" + is_playing + "）");
            if (!DebugMode || !debug_skip_Verification)
            {
                if (DataIntegrity.Check())
                {
                    Reject(LocalizationManager.GetLocalizedText("note.play_error7"), "DataIntegrity");
                    return;
                }
                var blocks = SpectreAPI.SnapshotBlocks();
                if (blocks.Count != 0)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var kv in blocks)
                        sb.Append(kv.Key).Append(':').Append(kv.Value).Append('\n');
                    Reject(LocalizationManager.GetLocalizedText("note.play_error8"), "BlockPlaying: " + sb);
                    return;
                }
                if (scrPlayerManager.instance.players.Count() != 1)
                {
                    Reject(LocalizationManager.GetLocalizedText("note.play_error8"),
                        "players=" + scrPlayerManager.instance.players.Count());
                    return;
                }
                if (data.IsOfficialLevel != ADOBase.isOfficialLevel)
                {
                    // TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error1") + data.SongName, 3f, NotifType.Warning);
                    // return;
                }
                if (data.IsOfficialLevel == true)
                {
                    // if (data.SceneName != SceneManager.GetActiveScene().name)
                    // {
                    //     TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error1") + data.SongName, 3f, NotifType.Warning);
                    //     return;
                    // }
                    if (!string.IsNullOrEmpty(data.InternalLevelName)
                        && data.InternalLevelName != GCS.internalLevelName)
                    {
                        Reject(LocalizationManager.GetLocalizedText("note.play_error1") + data.SongName);
                        Debug.Log("[SpectreReplay]   细节 internal: 录="
                            + data.InternalLevelName + " 现=" + GCS.internalLevelName);
                        return;
                    }
                }
                int floorHash = ADOBase.isOfficialLevel
                    ? 0
                    : (scnGame.instance.levelData.isOldLevel
                        ? scnGame.instance.levelData.pathData.GetHashCode()
                        : ComputeAngleHash(scnGame.instance.levelData.angleData));
                if (floorHash != data.FloorHash)
                {
                    Reject(LocalizationManager.GetLocalizedText("note.play_error2"));
                    Debug.Log("[SpectreReplay]   细节 floorHash: 录="
                        + data.FloorHash + " 现=" + floorHash);
                    return;
                }
                if (!ADOBase.isOfficialLevel
                    && (data.Pitch != scnGame.instance.levelData.pitch
                        || data.Bpm != (double)scnGame.instance.levelData.bpm))
                {
                    Reject(LocalizationManager.GetLocalizedText("note.play_error2"));
                    Debug.Log("[SpectreReplay]   细节 pitch/bpm: 录="
                        + data.Pitch + "/" + data.Bpm
                        + " 现=" + scnGame.instance.levelData.pitch + "/" + scnGame.instance.levelData.bpm);
                    return;
                }
                if (!IsFullRun && data.EndTile is int endTileVal)
                {
                    if (GCS.checkpointNum < data.StartTile || GCS.checkpointNum > endTileVal)
                    {
                        Reject(LocalizationManager.GetLocalizedText("note.play_error4") + data.StartTile);
                        Debug.Log("[SpectreReplay]   细节 checkpointNum=" + GCS.checkpointNum
                            + " 区间=" + data.StartTile + ".." + endTileVal);
                        return;
                    }
                }
                // 不要求与录像起点相等：回放按 GCS.checkpointNum 调
                // ReplayPlayer.FastForward 对齐按键游标，能从区间内任意砖块起跑。
                // 只需拒绝**早于录像起点**：那之前没有按键数据，音画对不上。
                else if (!IsFullRun && GCS.checkpointNum < data.StartTile)
                {
                    // detail 只进日志（见 Reject 注释），所以用 ASCII 与其余几处
                    // （"DataIntegrity" / "BlockPlaying: "）保持一致，别把中文漏进界面文案扫描。
                    Reject(LocalizationManager.GetLocalizedText("note.play_error4") + data.StartTile,
                        "checkpointNum=" + GCS.checkpointNum + " < StartTile=" + data.StartTile);
                    return;
                }
                if (data.SpeedTrailMode == true || data.QuickPitched == true)
                {
                    // if (data.SceneName != SceneManager.GetActiveScene().name)
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
                    expectedSpeed = data.QuickPitched == true ? (data.PlaybackSpeed ?? 1.0) : 1.0;
                }
                else
                {
                    actualSpeed = (GCS.speedTrialMode || GCS.practiceMode) ? GCS.currentSpeedTrial : 1f;
                    expectedSpeed = data.SpeedTrailMode == true ? (data.SpeedTrail ?? 1.0) : 1.0;
                }
                if (actualSpeed != expectedSpeed)
                {
                    Reject(LocalizationManager.GetLocalizedText("note.play_error5") + expectedSpeed,
                        "actual=" + actualSpeed);
                    return;
                }

                // **判定模式不一致时自动套用录像的设置，而不是拒绝播放。**
                // 录制时是什么难度（严格/标准/宽松），回放就按什么难度判——
                // 否则同一份按键数据在「标准」下是 Perfect、在「严格」下就变成
                // Early/Late，判定与录制时对不上。
                //
                // **必须放在全部校验通过之后。** 曾经排在校验流程中间（起点砖/
                // 倍速校验之前）：那三道校验被拒时，GCS.difficulty 已经被改成
                // 回放值而 CachedDifficulty 里锁着玩家原值——回放没开始，
                // ReplayPlayer.Stop 的还原因 is_playing=false 直接跳过，玩家的
                // 难度就被泄漏成回放的难度；下次回放另一难度的录像时这里会把
                // 泄漏值当成「玩家原设置」重新存进 CachedDifficulty，真正的
                // 偏好从此永久丢失。挪到末尾后，走到这里 = 必然开播，还原链
                // 必然有机会执行。
                //
                // 玩家自己的默认难度存在 Persistence 里**不动**：这里只改本次
                // 会话的 GCS.difficulty，回放结束（ReplayPlayer.Stop）时还原。
                //
                // 用 Enum.TryParse + IsDefined 而不是直接赋值：回放文件是玩家
                // 可编辑的文本，写个 "99" 进去会让判定窗口按不存在的档位走，
                // 且不抛异常、回放照常开始。解析失败就保持当前难度（退回旧行为）。
                if (GCS.difficulty.ToString() != data.JudgeMode)
                {
                    if (Enum.TryParse(data.JudgeMode, out Difficulty storedDifficulty)
                        && Enum.IsDefined(typeof(Difficulty), storedDifficulty))
                    {
                        CachedDifficulty = GCS.difficulty;
                        GCS.difficulty = storedDifficulty;
                        Debug.Log("[SpectreReplay] 判定模式自动对齐: " + CachedDifficulty
                            + " → " + storedDifficulty + "（回放结束后还原）");
                        SyncDifficultyLabel();
                    }
                    else
                    {
                        // 解析不出合法难度：保持当前难度继续播放。判定可能与该录像
                        // 录制时不一致，但拒绝播放会让玩家什么都做不了。
                        Debug.Log("[SpectreReplay] 判定模式无法解析: \"" + data.JudgeMode
                            + "\"，保持当前难度 " + GCS.difficulty);
                    }
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
            if (data.HitMarginLimit != null
                && Enum.TryParse<HitMarginLimit>(data.HitMarginLimit, out var result)
                && Enum.IsDefined(typeof(HitMarginLimit), result))
                StoredHitMarginLimit = result;
            GCS.hitMarginLimit = StoredHitMarginLimit;
            CachedNoFail = scrController.instance.noFail;
            scrController.instance.noFail = true;
            SyncEditorNoFailButton();

            // 全量回放 = 第 0 块录到本关最后一块。**这里必须打日志**：这个标志
            // 依赖关卡数据，而它在「先装弹、后进关」与「已在关卡里再装弹」两条
            // 路径上取到的值可能不同——不打出来就只能靠猜。
            int fullRunTotal = ADOBase.lm != null && ADOBase.lm.listFloors != null
                ? ADOBase.lm.listFloors.Count : -1;
            IsFullRun = data.StartTile.HasValue && data.EndTile.HasValue
                && data.StartTile == 0
                && fullRunTotal > 0
                && data.EndTile == fullRunTotal - 1;
            Debug.Log("[SpectreReplay] fullrun 判定: StartTile=" + data.StartTile
                + " EndTile=" + data.EndTile + " listFloors=" + fullRunTotal
                + " => IsFullRun=" + IsFullRun + " scene=" + SceneManager.GetActiveScene().name);

            // **必须先 StartPlaying 再 FastForward。**
            // ReplayPlayer.Start() 会把 PlayIndex / KeyEventIndex 清零；反过来调的话，
            // FastForward 算好的游标会被紧接着清零，等于每次跳转都从文件开头重灌
            // 整局事件——大关卡里追平过程长达数百帧，追平瞬间还会被判定为"已到尾部"
            // 直接停止回放。
            PlayActions.StartPlaying();
            // **静默未启动同样是泄漏态。** 校验时（SnapshotBlocks）无阻断、到这里
            // 之间被外部 mod 调 BlockPlaying 置了 ApiBlockPlaying 的话，
            // ReplayPlayer.Start 会整个跳过（is_playing 保持 false）且不抛异常——
            // 上面接管的全局设置（难度 / noFail / hitMarginLimit）就没人还原了。
            // 在此显式判一次：没启动就还原并按被阻断上报。
            if (!is_playing)
            {
                if (CachedDifficulty.HasValue)
                {
                    GCS.difficulty = CachedDifficulty.Value;
                    CachedDifficulty = null;
                    SyncDifficultyLabel();
                }
                if (CachedNoFail.HasValue)
                {
                    var failController = scrController.instance;
                    if (failController != null)
                        failController.noFail = CachedNoFail.Value;
                    CachedNoFail = null;
                }
                Reject(LocalizationManager.GetLocalizedText("note.play_error8"),
                    "ApiBlockPlaying set between validation and StartPlaying");
                return;
            }
            if (is_playing && GCS.checkpointNum != data.StartTile)
                ReplayPlayer.FastForward(GCS.checkpointNum);
            if (is_playing)
                ResetRejectLog();
            TriggerMessage(LocalizationManager.GetLocalizedText("note.start_playing"));
        }
        catch (Exception ex)
        {
            Debug.Log("load error: " + ex);
            // **失败路径必须把接管的全局设置还回去。** 难度对齐与 noFail 接管
            // 都在 try 内（对齐点之后还有 hitMarginLimit / noFail / FastForward
            // 一整段），中途抛异常时回放没开播，ReplayPlayer.Stop 的还原链
            // （is_playing=false 直接 return）永远不会执行——难度与 noFail 就此
            // 泄漏到玩家的正常游玩里。is_playing 为真说明回放已实际启动，还原
            // 交给 Stop；为假才是泄漏态，就地还原。
            if (!is_playing)
            {
                if (CachedDifficulty.HasValue)
                {
                    GCS.difficulty = CachedDifficulty.Value;
                    CachedDifficulty = null;
                    SyncDifficultyLabel();
                }
                if (CachedNoFail.HasValue)
                {
                    var failController = scrController.instance;
                    if (failController != null)
                        failController.noFail = CachedNoFail.Value;
                    CachedNoFail = null;
                }
            }
            TriggerMessage(LocalizationManager.GetLocalizedText("note.play_error7"), 3f, NotifType.Warning);
        }
    }

    private static void TryStartRecording()
    {
        try
        {
            if (data == null) data = new ReplayData();
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
            data.Reset();
            data.StartTile = GCS.checkpointNum;
            data.SceneName = SceneManager.GetActiveScene().name;
            data.JudgeMode = GCS.difficulty.ToString();
            data.SpeedTrail = GCS.currentSpeedTrial;
            data.SpeedTrailMode = GCS.speedTrialMode || GCS.practiceMode;
            data.InternalLevelName = GCS.internalLevelName ?? "";
            data.IsOfficialLevel = ADOBase.isOfficialLevel;
            data.QuickPitched = GCS.editorQuickPitchedPlaying;
            data.IfNoFail = ADOBase.controller?.noFail ?? GCS.useNoFail;
            data.FloorHash = ADOBase.isOfficialLevel
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
            data.SpeedHash = ComputeDoubleListHash(speeds);
            data.TimeHash = ComputeTimeListHash(times);
            data.LevelPath = "";
            if (ADOBase.isOfficialLevel)
            {
                data.ArtistName = "OfficialLevel";
                string songName = GCS.internalLevelName ?? "";
                if (data.SceneName != "scnGame" && data.SceneName != "scnEditor")
                    songName = data.SceneName;
                data.SongName = songName;
                data.LevelPath = "OfficialLevel " + songName;
                data.Pitch = 0;
                data.Bpm = CurrentBpm();
            }
            else
            {
                data.ArtistName = (scnGame.instance.levelData.artist ?? "").Trim();
                string song = (scnGame.instance.levelData.song ?? "").Trim();
                if (string.IsNullOrEmpty(song))
                {
                    song = scnGame.instance.levelData.songFilename;
                    int dot = song.IndexOf('.');
                    if (dot != -1) song = song.Substring(0, dot);
                }
                data.SongName = song;
                data.LevelPath = scnGame.instance.levelPath ?? "";
                data.Pitch = scnGame.instance.levelData.pitch;
                data.Bpm = scnGame.instance.levelData.bpm;
            }
            data.PlaybackSpeed = SceneManager.GetActiveScene().name == "scnEditor"
                ? scnEditor.instance.playbackSpeed : 1.0;
            data.AudioBufferSize = Persistence.audioBufferSize;
            data.HoldBehavior = Persistence.holdBehavior.ToString();
            data.HitMarginLimit = Persistence.hitMarginLimit.ToString();
            data.LoadedMods = string.Join("\n", Main.GetLoadedMods().Select(m => Main.mod2string(m)));
            data.DeviceId = SystemInfo.deviceUniqueIdentifier;
            data.ModVersion = Main.mod.Info.Version;
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
