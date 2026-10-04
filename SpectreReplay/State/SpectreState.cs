using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SpectreReplay.API;
using SpectreReplay.Features.Compat;
using SpectreReplay.Features.Replay;
using static SpectreReplay.Features.Replay.ReplayKeys;
using HarmonyLib;
using MonsterLove.StateMachine;
using SkyHook;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityModManagerNet;
using Object = UnityEngine.Object;

namespace SpectreReplay;

public static class SpectreState
{
    internal static class DataIntegrity
    {
        public static List<string> strings_ = new List<string> { SongName, SceneName, InternalLevelName, JudgeMode };

        public static List<string> bools_ = new List<string> { IsOfficialLevel, SpeedTrailMode, QuickPitched };

        public static List<string> ints_ = new List<string> { FloorHash, TimeHash, SpeedHash, Pitch, StartTile };

        public static List<string> doubles_ = new List<string> { Bpm, PlaybackSpeed, SpeedTrail };

        internal static bool Check()
        {
            return !HasAllKeys(data.strings, strings_, "String")
                || !HasAllKeys(data.bools, bools_, "Bool")
                || !HasAllKeys(data.ints, ints_, "Int")
                || !HasAllKeys(data.doubles, doubles_, "Double");
        }

        private static bool HasAllKeys<T>(Dictionary<string, T> dict, List<string> keys, string kind)
        {
            foreach (string key in keys)
            {
                if (!dict.ContainsKey(key))
                {
                    Debug.Log("Missing " + kind + ":" + key);
                    return false;
                }
            }
            return true;
        }
    }

    internal class PlayActions
    {
        internal static void SwitchToPlayMode()
        {
            PlayMode = true;
            RecordMode = false;
            is_playing = false;
            is_recording = false;
            HitLocked = false;
            PendingSave = false;
            LateSaveMode = false;
            PlayIndex = 0;
            KeyEventIndex = 0;
            AllAutoHits = true;
            IgnoreMarkFail = false;
        }

        internal static void SwitchToRecordMode()
        {
            HitLocked = false;
            PlayMode = false;
            RecordMode = true;
            is_playing = false;
            is_recording = false;
            PendingSave = false;
            LateSaveMode = false;
            PlayIndex = 0;
            KeyEventIndex = 0;
            AllAutoHits = true;
            IgnoreMarkFail = false;
            IsFullRun = false;
        }

        // 启停事件只在状态真正翻转时触发（Start/Stop 内部各有守卫，重复调用无害）；
        // 全部启停路径都经 PlayActions 收口，这里是对外事件的唯一挂点。
        // Start* 会把互斥的另一侧标志直接翻转（不经过对应 Stop），此处一并补发 Stopped
        internal static void StopPlaying()
        {
            bool was = is_playing;
            // Stop 内部对场景已卸做了空守卫，此处的 catch 是最后防线：
            // 即使再有意外异常，is_playing 标志已翻转，事件照常触发
            try { ReplayPlayer.Stop(); }
            catch (Exception ex) { Debug.LogException(ex); }
            if (was && !is_playing) SpectreAPI.RaisePlaybackStopped();
        }

        internal static void StartPlaying()
        {
            bool wasPlay = is_playing;
            bool wasRec = is_recording;
            ReplayPlayer.Start();
            if (!wasPlay && is_playing)
            {
                if (wasRec) SpectreAPI.RaiseRecordingStopped();
                SpectreAPI.RaisePlaybackStarted();
            }
        }

        internal static void StartRecording()
        {
            bool wasRec = is_recording;
            bool wasPlay = is_playing;
            ReplayRecorder.Start();
            if (!wasRec && is_recording)
            {
                if (wasPlay) SpectreAPI.RaisePlaybackStopped();
                SpectreAPI.RaiseRecordingStarted();
            }
        }

        internal static void StopRecording()
        {
            bool was = is_recording;
            ReplayRecorder.Stop();
            if (was && !is_recording) SpectreAPI.RaiseRecordingStopped();
        }
    }

    internal static bool ApiBlockPlaying = false;

    internal static bool is_playing = false;

    internal static bool is_recording = false;

    internal static bool? CachedNoFail;

    internal static void SyncEditorNoFailButton()
    {
        if (scnEditor.instance == null) return;
        var btn = scnEditor.instance.buttonNoFail;
        if (btn == null) return;
        if (data == null || !data.bools.ContainsKey(IfNoFail)) return;
        var img = btn.GetComponent<UnityEngine.UI.Image>();
        img.color = data.bools[IfNoFail] ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);
    }

    internal static bool RecordMode = true;

    internal static bool PlayMode = false;

    internal static bool HitLocked = false;

    internal static bool IgnoreMarkFail = false;

    internal static bool UseAsyncInput = false;

    internal static bool PendingSave = false;

    internal static bool LateSaveMode = false;

    internal static int[] HitMarginCounts = new int[Enum.GetValues(typeof(HitMargin)).Cast<int>().Max() + 1];

    // 仅作引用比较的哨兵（Patch_Recording 里判断是否同一块），不实例化 MonoBehaviour
    internal static scrFloor CurrentHitFloor;

    internal static LegacyReplayData data;

    internal static int PlayIndex = 0;

    internal static int KeyEventIndex = 0;

    internal static bool AllAutoHits = true;

    internal static bool IsFullRun = false;
    internal static bool HasKeybdSound = false;

    internal static HitMarginLimit StoredHitMarginLimit = (HitMarginLimit)0;

    internal static bool DebugMode = false;

    internal static bool debug_skip_Verification = false;

    internal static bool debug_no_encryption = false;

    internal static ReplayManager.ReplaySaveFormat debug_save_format = ReplayManager.ReplaySaveFormat.New;

    internal static bool debug_hide_text = false;

    internal static bool debug_use_old_hit = false;

    internal static KeyCode save_button = (KeyCode)290;

    internal static KeyCode cancel_save_button = (KeyCode)291;

    internal static byte[] PrevKeyState = new byte[256];

    internal static byte[] CurrKeyState = new byte[256];

    private static readonly ushort[] ExtendedKeys = { 165, 163, 38, 40, 37, 39 };

    private static readonly ushort[] BlockedKeys = { 16, 17, 18, 27 };

    internal static int GetKeyboardState(byte[] pbKeyState)
        => KeyboardSimulation.GetKeyboardState(pbKeyState);

    internal static bool IsExtendedKey(ushort keyCode)
        => Array.IndexOf(ExtendedKeys, keyCode) >= 0;

    internal static bool IsBlockedKey(ushort keyCode)
        => Array.IndexOf(BlockedKeys, keyCode) >= 0;

    internal static float GetAngle()
    {
        float num = (float)(scrController.instance.chosenPlanet.angle - scrController.instance.chosenPlanet.targetExitAngle);
        if (!scrController.instance.playerOne.planetarySystem.isCW)
        {
            num *= -1f;
        }
        return num;
    }

    internal static bool SaveFile(bool without_sound = false, bool retry = false)
    {
        if (data == null)
        {
            return true;
        }
        string message = "";
        bool success = true;
        string savedPath = null;
        if (retry)
        {
            data.strings[FileName] = CreateFileName(safe_mode: true);
            data.strings[KeybdSoundFileName] = data.strings[FileName] + ".wav";
        }
        string fileName = !data.strings.ContainsKey(FileName) ? CreateFileName() : data.strings[FileName];
        string savePath = Path.Combine(Settings.SavePath, fileName);
        if (ReplayManager.SaveReplay(data, savePath, debug_save_format, debug_no_encryption))
        {
            message += LocalizationManager.GetLocalizedText("note.save_success") + "\n";
            savedPath = savePath;
        }
        else
        {
            message += LocalizationManager.GetLocalizedText("note.save_fail") + savePath + "\n";
            success = false;
        }
        if (Settings.Backup)
        {
            // 备份结果原本被完全忽略：主回放已保存成功时，备份可能因权限受限/磁盘满/
            // 目录不可写而失败，而用户毫无察觉 —— 直到真出事那天才发现"备份早就没了"。
            // 备份失败不影响主回放的成功状态（success），但必须告知用户。
            if (!ReplayManager.SaveReplay(data, Path.Combine(Main.mod.Path, "backup"), debug_save_format, debug_no_encryption))
            {
                message += LocalizationManager.GetLocalizedText("note.save_fail") + "backup\n";
            }
        }
        if (!without_sound && data.strings.ContainsKey(KeybdSoundFileName))
        {
            string tmpPath = Path.Combine(Settings.SavePath, "tmp.wav");
            string keybdName = data.strings[KeybdSoundFileName];
            // 键存在但值为空串时（解码自外部/损坏的回放文件），Path.Combine 会退化成
            // "目录\tmp.wav" 这样的路径：要么 File.Move 抛异常，要么把 tmp.wav 移到目录上。
            // ReplayManager.TryLoadKeybdSound 已对同源问题做了这道判断，这里对齐。
            if (string.IsNullOrEmpty(keybdName))
            {
                message += LocalizationManager.GetLocalizedText("note.keybdsoundfile_save_fail") + " (empty name)" + "\n";
            }
            else
            {
                string wavPath = Path.Combine(Settings.SavePath, keybdName);
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        if (File.Exists(wavPath))
                        {
                            File.Delete(wavPath);
                        }
                        File.Move(tmpPath, wavPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log(ex);
                }
                if (!File.Exists(wavPath))
                {
                    message += LocalizationManager.GetLocalizedText("note.keybdsoundfile_save_fail") + wavPath + "\n";
                }
            }
        }
        // 事件放在伴生 wav 改名/备份之后：订阅者此刻能拿到完整的成套文件
        if (savedPath != null)
            SpectreAPI.RaiseReplaySaved(savedPath);
        TriggerMessage(message);
        if (!success && !retry)
        {
            return SaveFile(without_sound, retry: true);
        }
        return success;
    }

    internal enum NotifType { Info, Warning }

    internal static void TriggerMessage(string message, float dur = 3f, NotifType type = NotifType.Info)
    {
        if (debug_hide_text) return;
        // Notification 内部 StartCoroutine + UI 操作，后台线程直调会炸——转回主线程
        if (!MainThread.IsMainThread)
        {
            MainThread.Run(() => TriggerMessage(message, dur, type));
            return;
        }
        try
        {
            var n = Notification.instance;
            n.text.text = message.TrimEnd('\n');
            n.icon.enabled = true;
            n.button.onClick.RemoveAllListeners();
            switch (type)
            {
                case NotifType.Warning:
                    n.icon.sprite = n.warningIcon;
                    n.icon.color = new Color(1f, 0.2f, 0.2f);
                    break;
                default:
                    n.icon.sprite = n.completeIcon;
                    n.icon.color = new Color(0.2f, 0.7215686f, 0.3921569f);
                    break;
            }
            var graphics = n.bar.GetComponentsInChildren<Graphic>(true);
            var states = graphics.Select(g => g.raycastTarget).ToArray();
            foreach (var g in graphics) g.raycastTarget = false;
            n.StartCoroutine(RestoreGraphics(graphics, states, dur));
            var m = PatchManager.GetMethodInfo(typeof(Notification), "SetupNotification",
                [typeof(float), typeof(float), typeof(bool)]);
            m.Invoke(n, [dur, 1f, true]);
        }
        catch (Exception ex)
        {
            Debug.Log(ex);
        }
    }

    private static System.Collections.IEnumerator RestoreGraphics(Graphic[] graphics, bool[] states, float dur)
    {
        yield return new WaitForSecondsRealtime(0.5f + 0.5f + dur + 0.3f);
        for (int i = 0; i < graphics.Length; i++)
            if (graphics[i] != null) graphics[i].raycastTarget = states[i];
    }

    public static bool ReplayHit(scrPlayer player, HitContext hitdata)
        => ReplayPlayer.ReplayHit(player, hitdata);

    internal static void Play_keybd_event(bool flag)
        => ReplayPlayer.PlayKeyboardEvent(flag);

    internal static void Record_kbdstate(bool forceSync = false)
        => ReplayRecorder.RecordKeyboardState(forceSync);

    private static UnityEngine.Events.UnityAction<SkyHook.SkyHookEvent> _asyncKeyListener;

    public static void SetGetKeyAsyncEnabled(bool enabled)
    {
        if (enabled == UseAsyncInput)
            return;
        UseAsyncInput = enabled;
        if (enabled)
        {
            if (_asyncKeyListener == null)
                _asyncKeyListener = AsyncKeyRecorder.OnKeyUpdated;
            SkyHook.SkyHookManager.KeyUpdated.AddListener(_asyncKeyListener);
        }
        else if (_asyncKeyListener != null)
        {
            SkyHook.SkyHookManager.KeyUpdated.RemoveListener(_asyncKeyListener);
        }
    }

    internal static void StopAll()
    {
        if (is_playing)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("note.stop_playing"));
        }
        if (is_recording)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("note.stop_recording"));
        }
        PlayActions.StopPlaying();
        PlayActions.StopRecording();
    }

    internal static void CancelSave()
    {
        if (!PendingSave) return;
        PendingSave = false;
        LateSaveMode = false;
        TriggerMessage(LocalizationManager.GetLocalizedText("note.cancel_save"));
    }

    public static double ComputePercentXacc()
    {
        var tracker = scrController.instance?.playerOne?.marginTracker;
        if (tracker == null || tracker.hitMargins == null || tracker.hitMargins.Count == 0)
            return 0.0;
        // 自定义权重模式：分子 = Σ(档命中 × 档权重)，越档命中不计；分母仍为全部命中数
        if (Settings.CustomJudgeOn && Settings.CustomXaccOn)
            return Features.CustomJudge.TierJudgeState.WeightedSum() / (double)tracker.hitMargins.Count;
        return ComputePercentXaccVanilla();
    }

    // 原版口径 xacc：官方四段权重（1 / 0.75 / 0.4 / 0.2）对 margin 桶计数加权。
    // 文件名固定用这个口径——文件名要跨玩家、跨判定方案通用可比，
    // 自定义档准度只上结算屏，不进文件名。
    // 注：RealJudge 改写判定边界时，桶本身已按自定义规则落账，
    // 此时该值是“原版公式对本次实际判定的取值”，而非无 mod 重打的假想值
    public static double ComputePercentXaccVanilla()
    {
        var tracker = scrController.instance?.playerOne?.marginTracker;
        if (tracker == null || tracker.hitMargins == null || tracker.hitMargins.Count == 0)
            return 0.0;
        // 权重表按名解析、逐版本复刻（旧版下标与 r150 重排后的下标由兼容层各自持有）：
        //   旧版：1.0×{Perfect,Auto} + 0.75×{Early,LatePerfect} + 0.4×{VeryEarly,VeryLate} + 0.2×{TooEarly,TooLate}
        //   r150：1.0×{XPerfect,PerfectMinus,PerfectPlus} + … + 0.2×{TooEarly,TooLate,FailedFloor}，Auto 不计入
        double numerator = HitMarginCompat.XAccNumerator(HitMarginCounts);
        return numerator / (double)tracker.hitMargins.Count;
    }

    public static double ComputePercentComplete(int curr_floor)
    {
        // lm 已加载但 listFloors 为空的窗口期（转场中/关卡构造中）会让除法得到
        // Infinity/NaN，直接透出到 UI 会显示成 "Infinity%"。分母为 0 时按 0 处理。
        if (scrController.instance != null && ADOBase.lm != null
            && ADOBase.lm.listFloors != null && ADOBase.lm.listFloors.Count > 0)
        {
            return (float)(curr_floor + 1) / (float)ADOBase.lm.listFloors.Count;
        }
        return 0.0;
    }

    internal static string CreateFileName(bool safe_mode = false)
    {
        if (safe_mode)
        {
            return DateTime.Now.ToString("yyyyMMddHHmmss") + "_";
        }
        string fileName = "";
        if (data.strings.ContainsKey(ArtistName) && data.strings.ContainsKey(SongName))
        {
            fileName = data.strings[ArtistName] + "_" + data.strings[SongName];
        }
        fileName = Regex.Replace(fileName, "<[^>]*>", string.Empty);
        fileName = DateTime.Now.ToString("yyyyMMddHHmmss") + "_" + fileName;
        try
        {
            bool isFullRun = data.ints[EndTile] == ADOBase.lm.listFloors.Count - 1 && data.ints[StartTile] == 0;
            if (isFullRun)
            {
                fileName += "_" + (100.0 * ComputePercentXaccVanilla()).ToString("F2");
            }
            else
            {
                fileName += "_" + (100.0 * ComputePercentComplete(data.ints[StartTile])).ToString("F0")
                    + "-" + (100.0 * ComputePercentComplete(data.ints[EndTile])).ToString("F0");
            }
        }
        catch
        {
        }
        return ReplayManager.RemoveInvalidPathChars(fileName);
    }
}
