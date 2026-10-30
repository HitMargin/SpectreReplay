using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpectreReplay.Features.Cloud;
using UnityEngine;

namespace SpectreReplay;

public static class ConfigManager
{
    internal static string configPath = "";

    // 显式 DTO，避免匿名类型反射序列化（脆弱且慢），并保持 JSON 键名不变以兼容旧配置
    private class ConfigData
    {
        public SetsData Sets { get; set; } = new SetsData();
        public IntsData Ints { get; set; } = new IntsData();
        public StringsData Strings { get; set; } = new StringsData();
        public StringListsData StringLists { get; set; } = new StringListsData();
    }


    private class SetsData
    {
        public bool ReplayOn { get; set; }
        public bool AutoSave { get; set; }
        public bool CompleteSave { get; set; }
        public bool DontSaveWhenAuto { get; set; }
        public bool DontSaveWhenMiss { get; set; }
        public bool LateSave { get; set; }
        public bool FailSave { get; set; }
        public bool Backup { get; set; }
        public bool CloudSyncOn { get; set; }
        public bool CloudRestoreOnLoad { get; set; }
        public bool ManualSave { get; set; }
        public bool CancelSave { get; set; }
        public bool KeybdSoundRecordActive { get; set; }
        public bool KeybdSoundCompact { get; set; }
        public bool KeyConvertEnabled { get; set; }
        public bool LimitKey { get; set; }
        public bool GetDataFromAsyncInput { get; set; }
        public bool MirrorInputToOS { get; set; }

        public bool EffectRemoverOn { get; set; }
        public bool EffectRemoverEnableSave { get; set; }
        public bool EffectRemoverFilters { get; set; }
        public bool EffectRemoverAdvancedFilters { get; set; }
        public bool EffectRemoverParticles { get; set; }
        public bool EffectRemoverDecorations { get; set; }
        public bool EffectRemoverBackgrounds { get; set; }
        public bool EffectRemoverCameras { get; set; }
        public bool EffectRemoverRepeatEvents { get; set; }
        public bool EffectRemoverFrameRate { get; set; }
        public bool EffectRemoverHitSounds { get; set; }
        public bool EffectRemoverCheckpoints { get; set; }
        public bool EffectRemoverInputEvents { get; set; }
        public bool EffectRemoverPlanetOrbit { get; set; }
        public bool EffectRemoverPlanetScale { get; set; }
        public bool EffectRemoverPlanetRadius { get; set; }
        public bool EffectRemoverTrackAnimations { get; set; }
        public bool EffectRemoverTrackPositions { get; set; }
        public bool EffectRemoverTrackMoves { get; set; }
        public bool EffectRemoverTrackColors { get; set; }
        public bool EffectRemoverHoldSounds { get; set; }
        public bool EffectRemoverHideIcons { get; set; }
        public bool EffectRemoverRemoveAllDecorations { get; set; }
        public bool EffectRemoverResetTrackOpacity { get; set; }
        public bool EffectRemoverResetTrackAnimation { get; set; }
        public bool EffectRemoverResetTrackColor { get; set; }
        public bool EffectRemoverSetCameraZoom { get; set; }

        public bool AlwaysShowMaxUsedKeys { get; set; }
        public bool LegacyPauseCalculation { get; set; }
        public bool DecorationPreviewOn { get; set; }
        public bool MemoryOptimizerOn { get; set; }
        public bool EditorBugfixOn { get; set; }
        public bool BpmBeatCalculatorOn { get; set; }
        public bool SpeedPauseConverterOn { get; set; }
        public bool BetterUndoRedoOn { get; set; }
        public bool BGAModeOn { get; set; }
        public bool EditorUIOn { get; set; }
        public bool EditorUIFloorPanelOn { get; set; }
        public bool EditorUIHorizontalOn { get; set; }
        public bool TimelineOn { get; set; }
        public bool FixChartLoadOn { get; set; }
        public bool CustomEventsOn { get; set; }
        public bool EnableProEvents { get; set; }
    }

    private class IntsData
    {
        public int selected_tab { get; set; }
        public int save_button { get; set; }
        public int cancel_save_button { get; set; }
        public int KeybdSoundVolume { get; set; }
        public int MicrophoneOffset { get; set; }
        public float EffectRemoverCameraZoomScale { get; set; }
        public int render_width { get; set; }
        public int render_height { get; set; }
        public int render_fps { get; set; }
        public int render_encoder { get; set; }
        public int render_quality { get; set; }
        public int render_output_format { get; set; }
        /// <summary>转换目标容器（与渲染格式解耦，见 Settings.ConvertTarget）。</summary>
        public int convert_target { get; set; }
        public int render_capture_mode { get; set; }
        public float render_tail { get; set; }
        public bool render_on { get; set; }
        public bool render_clock_on { get; set; }
        public bool render_cursor_on { get; set; }
        public bool render_stop_on { get; set; }
        public int render_stop_key { get; set; }
        /// <summary>提示条停留秒数（见 Settings.NotificationSeconds）。</summary>
        public float notification_seconds { get; set; }
    }

    private class StringsData
    {
        public string savepath { get; set; }
        public string inputedKey { get; set; }
        public string currlanguage { get; set; }
        public string microphone_device_name { get; set; }
        public string key_code_convert { get; set; }
        public string render_output_dir { get; set; }
    }

    private class StringListsData
    {
        public HashSet<string> allowed_keyboard_special_keys { get; set; }
        public HashSet<string> allowed_async_special_keys { get; set; }
    }

    /// <summary>
    /// 把全部设置落盘。<paramref name="filePath"/> 为空/无目录名时视为无事可做，返回 true。
    /// <para>
    /// 返回值供 SpectreAPI.SaveSettings 这类公开 API 告知外部 mod 结果；内部调用方
    /// （UM4 回调、退出钩子）忽略即可——它们无法向用户呈现失败。
    /// </para>
    /// </summary>
    internal static bool SaveConfigs(string filePath)
    {
        // 路径为 null 时后续 Path.GetDirectoryName(null) 会抛 ArgumentNullException，
        // 而那一行在 try 之外 → 异常穿透到 UM4 回调。内部调用方传的都是 Path.Combine
        // 的结果（不会为 null），但 SaveSettings 是对外 API，这里显式兜住。
        if (string.IsNullOrEmpty(filePath))
        {
            Debug.LogWarning("[SpectreReplay] 配置保存被跳过：未指定配置路径");
            return false;
        }
        configPath = filePath;
        var data = new ConfigData
        {
            Sets = new SetsData
            {
                ReplayOn = Settings.ReplayOn,
                AutoSave = Settings.AutoSave,
                CompleteSave = Settings.CompleteSave,
                DontSaveWhenAuto = Settings.DontSaveWhenAuto,
                DontSaveWhenMiss = Settings.DontSaveWhenMiss,
                LateSave = Settings.LateSave,
                FailSave = Settings.FailSave,
                Backup = Settings.Backup,
                CloudSyncOn = Settings.CloudSyncOn,
                CloudRestoreOnLoad = Settings.CloudRestoreOnLoad,
                ManualSave = Settings.ManualSave,
                CancelSave = Settings.CancelSave,
                KeybdSoundRecordActive = Settings.KeybdSoundRecordActive,
                KeybdSoundCompact = Settings.KeybdSoundCompact,
                KeyConvertEnabled = Settings.KeyConvertEnabled,
                LimitKey = Settings.LimitKey,
                GetDataFromAsyncInput = Settings.GetDataFromAsyncInput,
                MirrorInputToOS = Settings.MirrorInputToOS,

                EffectRemoverOn = Settings.EffectRemoverOn,
                EffectRemoverEnableSave = Settings.EffectRemoverEnableSave,
                EffectRemoverFilters = Settings.EffectRemoverFilters,
                EffectRemoverAdvancedFilters = Settings.EffectRemoverAdvancedFilters,
                EffectRemoverParticles = Settings.EffectRemoverParticles,
                EffectRemoverDecorations = Settings.EffectRemoverDecorations,
                EffectRemoverBackgrounds = Settings.EffectRemoverBackgrounds,
                EffectRemoverCameras = Settings.EffectRemoverCameras,
                EffectRemoverRepeatEvents = Settings.EffectRemoverRepeatEvents,
                EffectRemoverFrameRate = Settings.EffectRemoverFrameRate,
                EffectRemoverHitSounds = Settings.EffectRemoverHitSounds,
                EffectRemoverCheckpoints = Settings.EffectRemoverCheckpoints,
                EffectRemoverInputEvents = Settings.EffectRemoverInputEvents,
                EffectRemoverPlanetOrbit = Settings.EffectRemoverPlanetOrbit,
                EffectRemoverPlanetScale = Settings.EffectRemoverPlanetScale,
                EffectRemoverPlanetRadius = Settings.EffectRemoverPlanetRadius,
                EffectRemoverTrackAnimations = Settings.EffectRemoverTrackAnimations,
                EffectRemoverTrackPositions = Settings.EffectRemoverTrackPositions,
                EffectRemoverTrackMoves = Settings.EffectRemoverTrackMoves,
                EffectRemoverTrackColors = Settings.EffectRemoverTrackColors,
                EffectRemoverHoldSounds = Settings.EffectRemoverHoldSounds,
                EffectRemoverHideIcons = Settings.EffectRemoverHideIcons,
                EffectRemoverRemoveAllDecorations = Settings.EffectRemoverRemoveAllDecorations,
                EffectRemoverResetTrackOpacity = Settings.EffectRemoverResetTrackOpacity,
                EffectRemoverResetTrackAnimation = Settings.EffectRemoverResetTrackAnimation,
                EffectRemoverResetTrackColor = Settings.EffectRemoverResetTrackColor,
                EffectRemoverSetCameraZoom = Settings.EffectRemoverSetCameraZoom,

                AlwaysShowMaxUsedKeys = Settings.AlwaysShowMaxUsedKeys,
                LegacyPauseCalculation = Settings.LegacyPauseCalculation,
                DecorationPreviewOn = Settings.DecorationPreviewOn,
                MemoryOptimizerOn = Settings.MemoryOptimizerOn,
                EditorBugfixOn = Settings.EditorBugfixOn,
                BpmBeatCalculatorOn = Settings.BpmBeatCalculatorOn,
                SpeedPauseConverterOn = Settings.SpeedPauseConverterOn,
                BetterUndoRedoOn = Settings.BetterUndoRedoOn,
                BGAModeOn = Settings.BGAModeOn,
                EditorUIOn = Settings.EditorUIOn,
                EditorUIFloorPanelOn = Settings.EditorUIFloorPanelOn,
                EditorUIHorizontalOn = Settings.EditorUIHorizontalOn,
                TimelineOn = Settings.TimelineOn,
                FixChartLoadOn = Settings.FixChartLoadOn,
                CustomEventsOn = Settings.CustomEventsOn,
                EnableProEvents = Settings.EnableProEvents
            },
            Ints = new IntsData
            {
                selected_tab = 0,
                save_button = Settings.SaveButton,
                cancel_save_button = Settings.CancelSaveButton,
                KeybdSoundVolume = Settings.KeybdSoundVolume,
                MicrophoneOffset = Settings.MicrophoneOffset,
                EffectRemoverCameraZoomScale = Settings.EffectRemoverCameraZoomScale,
                render_width = Settings.RenderWidth,
                render_height = Settings.RenderHeight,
                render_fps = Settings.RenderFps,
                render_encoder = Settings.RenderEncoderMode,
                render_quality = Settings.RenderQualityMode,
                render_output_format = Settings.RenderOutputFormat,
                convert_target = Settings.ConvertTarget,
                render_capture_mode = Settings.RenderCaptureMode,
                render_tail = Settings.RenderTailSeconds,
                render_on = Settings.RenderOn,
                render_clock_on = Settings.RenderClockOn,
                render_cursor_on = Settings.RenderCursorOn,
                render_stop_on = Settings.RenderStopOn,
                render_stop_key = Settings.RenderStopKey,
                notification_seconds = Settings.NotificationSeconds,
            },
            Strings = new StringsData
            {
                savepath = Settings.SavePath,
                inputedKey = Settings.InputedKey,
                currlanguage = Settings.CurrLanguage,
                microphone_device_name = Settings.MicrophoneDeviceName,
                key_code_convert = Settings.KeyCodeConvert,
                render_output_dir = Settings.RenderOutputDir
            },
            StringLists = new StringListsData
            {
                allowed_keyboard_special_keys = Settings.AllowedKeyboardSpecialKeys,
                allowed_async_special_keys = Settings.AllowedAsyncSpecialKeys
            }
        };
        // 从这里往下的全部工作（序列化、建目录、写盘）都在
        // try 保护内。SaveConfigs 的 4 个调用方都**没有**自己的 try —— UM4 的
        // OnHideGUI 回调、退出钩子、设置页保存按钮，而 UM4 不包异常。异常穿透的后果
        // 不只是"没存上"，还会**中断调用方后续的清理**：AudioRecorder.StopAllMicrophones()
        // 排在保存之后，一旦抛出，麦克风会一直开着。
        string contents;
        try
        {
            contents = JsonConvert.SerializeObject(data, Formatting.Indented);
        }
        catch (Exception ex)
        {
            Debug.Log("[SpectreReplay] 配置序列化失败（本次设置改动未落盘）: " + ex.GetType().Name + " " + ex.Message);
            return false;
        }
        string directoryName = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directoryName))
            return true;
        // 落盘失败必须就地兜住而不是往上抛：SaveConfigs 的 4 个调用方都**没有**自己的
        // try —— UM4 的 OnHideGUI 回调、退出钩子、设置页保存按钮。异常穿透的后果不只是
        // "没存上"，还会**中断调用方后续的清理**：AudioRecorder.StopAllMicrophones()
        // 排在保存之后，一旦抛出，麦克风会一直开着。
        try
        {
            if (!Directory.Exists(directoryName))
                Directory.CreateDirectory(directoryName);
            File.WriteAllText(filePath, contents);
        }
        catch (Exception ex)
        {
            // 只记日志：抛出去对用户没有帮助（UI 已经关掉了），反而会打断清理链
            Debug.Log("[SpectreReplay] 配置保存失败（本次设置改动未落盘，重启后会丢失）: "
                + ex.GetType().Name + " " + ex.Message);
            return false;
        }

        // 落盘成功之后才做「配置上云」——顺序不能反：先云后本地的话，云端那份
        // 可能比本地新，而本地写盘失败时反倒显得「已经同步好了」。
        // 节流、去重、Steam 未就绪、非主线程转派全部在 CloudReplay 内部兜住，
        // 这里再包一层只是双保险：**云同步失败绝不能改成「本地保存失败」**，
        // 本地文件此刻已经确实写好了，那是唯一会让玩家丢数据的方向。
        try
        {
            CloudReplay.OnConfigSaved(filePath);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] 配置云同步异常（不影响本地保存）: " + ex.Message);
        }
        return true;
    }

    private static void MigrateLegacyConfig(JObject json)
    {
        // 新版分区任一存在即视为新文件，不做任何事
        if (json["Sets"] != null || json["Ints"] != null || json["Strings"] != null)
            return;
        // 旧版识别键：Surface / Fps / Encoder 三者是自研引擎独有的命名
        bool legacy = json["Surface"] != null || json["Fps"] != null
                || json["Encoder"] != null || json["RecordingOn"] != null;
        if (!legacy)
            return;

        Debug.Log("[SpectreReplay] 检测到旧版（自研引擎）配置文件，正在迁移到新结构");

        var sets = new JObject();
        var ints = new JObject();
        var strs = new JObject();

        // ── 布尔开关 ──
        // ReplayOn ← RecordingOn（同一个总开关，旧名叫 RecordingOn）
        MigrateBool(json, sets, "RecordingOn", "ReplayOn", Settings.ReplayOn);
        MigrateBool(json, sets, "RenderOn", "RenderOn", Settings.RenderOn);
        MigrateBool(json, sets, "AsyncInput", "GetDataFromAsyncInput", Settings.GetDataFromAsyncInput);
        // AutoSave 新旧同名，只是从顶层搬进 Sets 分区
        MigrateBool(json, sets, "AutoSave", "AutoSave", Settings.AutoSave);
        // DuplicateWhenLate = 晚了也照录（自研引擎的容错），Spectre 侧无对应字段，
        // 旧语义"允许重复按键"在本版恒开，不需要映射——丢弃即可。

        // ── 整数 / 枚举 ──
        MigrateInt(json, ints, "SaveKey", "save_button", Settings.SaveButton);
        MigrateInt(json, ints, "CancelSaveKey", "cancel_save_button", Settings.CancelSaveButton);
        MigrateInt(json, ints, "Surface", "render_capture_mode", Settings.RenderCaptureMode);
        MigrateInt(json, ints, "Width", "render_width", Settings.RenderWidth);
        MigrateInt(json, ints, "Height", "render_height", Settings.RenderHeight);
        MigrateInt(json, ints, "Fps", "render_fps", Settings.RenderFps);
        MigrateInt(json, ints, "Encoder", "render_encoder", Settings.RenderEncoderMode);
        MigrateInt(json, ints, "Quality", "render_quality", Settings.RenderQualityMode);
        MigrateInt(json, ints, "Container", "render_output_format", Settings.RenderOutputFormat);
        // Mode 是自研引擎的"实时/离线"模式选择；新版只有离线一条路，无需映射。

        // ── 浮点 ──
        MigrateFloat(json, ints, "TailSeconds", "render_tail", Settings.RenderTailSeconds);

        // ── 字符串 ──
        // OutputDir：旧键名 OutputDir，新键名 render_output_dir（新旧不同名，是唯一
        // 一个字符串项）。旧文件里它是顶层字符串，不是分区内的值。
        var outDir = json["OutputDir"]?.Type == JTokenType.String
            ? json["OutputDir"].Value<string>() : null;
        if (!string.IsNullOrEmpty(outDir))
            strs["render_output_dir"] = outDir;

        json["Sets"] = sets;
        json["Ints"] = ints;
        json["Strings"] = strs;
        // 旧键留着不删：它在新结构里不被读取，删掉反而让"回退到旧版"不可能。
        // 新结构的 ?? 语义保证旧键永远不影响新字段。

        try
        {
            File.WriteAllText(configPath, json.ToString(Formatting.Indented));
            Debug.Log("[SpectreReplay] 旧配置已迁移并落盘");
        }
        catch (Exception ex)
        {
            // 迁移失败不阻断启动：内存里已改好，本次会话照新值跑；
            // 下次启动会再迁移一次（幂等）。
            Debug.LogWarning("[SpectreReplay] 旧配置迁移后落盘失败（本次会话仍按新值运行）: " + ex.Message);
        }
    }

    private static void MigrateBool(JObject json, JObject target, string oldKey, string newKey, bool current)
    {
        var t = json[oldKey];
        if (t != null && t.Type == JTokenType.Boolean)
            target[newKey] = t.Value<bool>();
    }

    private static void MigrateInt(JObject json, JObject target, string oldKey, string newKey, int current)
    {
        var t = json[oldKey];
        if (t == null) return;
        try
        {
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
                target[newKey] = t.Value<int>();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] 旧配置 " + oldKey + " 值无法转换，已跳过: " + ex.Message);
        }
    }

    private static void MigrateFloat(JObject json, JObject target, string oldKey, string newKey, float current)
    {
        var t = json[oldKey];
        if (t == null) return;
        try
        {
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
                target[newKey] = t.Value<float>();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] 旧配置 " + oldKey + " 值无法转换，已跳过: " + ex.Message);
        }
    }

    public static void LoadConfigs(string filePath)
    {
        // 与 SaveConfigs 对称：路径为空时直接返回，别把 configPath 污染成 null
        // （之后 SaveSettings 会读到它）。File.Exists(null) 本身不抛，但状态不该被写坏。
        if (string.IsNullOrEmpty(filePath))
        {
            Debug.LogWarning("[SpectreReplay] 配置载入被跳过：未指定配置路径");
            return;
        }
        configPath = filePath;
        if (!File.Exists(filePath))
        {
            Debug.Log("[SpectreReplay] 配置文件不存在（用默认设置启动）: " + filePath);
            return;
        }
        try
        {
            var json = JObject.Parse(File.ReadAllText(filePath));
            MigrateLegacyConfig(json);
            // 分区隔离：Configs.json 是用户会手工编辑的文件，某个值写成别的类型
            // （把 true 写成 "是"、把数字写成带引号的字符串）会让该分区的 Value<T> 抛
            // JsonReaderException。若整个 LoadConfigs 共用一个 try，这一个坏值会连带
            // 丢弃**后面全部**分区——用户看到的是"设置被莫名重置"，原因只有一行日志。
            // 每个分区自带 try 后，坏值只影响它自己所在的分区。
            try
            {
                var sets = json["Sets"];
                if (sets != null)
                {
                    Settings.ReplayOn = sets.Value<bool?>("ReplayOn") ?? Settings.ReplayOn;
                    Settings.AutoSave = sets.Value<bool?>("AutoSave") ?? Settings.AutoSave;
                    Settings.CompleteSave = sets.Value<bool?>("CompleteSave") ?? Settings.CompleteSave;
                    Settings.DontSaveWhenAuto = sets.Value<bool?>("DontSaveWhenAuto") ?? Settings.DontSaveWhenAuto;
                    Settings.DontSaveWhenMiss = sets.Value<bool?>("DontSaveWhenMiss") ?? Settings.DontSaveWhenMiss;
                    Settings.LateSave = sets.Value<bool?>("LateSave") ?? Settings.LateSave;
                    Settings.FailSave = sets.Value<bool?>("FailSave") ?? Settings.FailSave;
                    Settings.Backup = sets.Value<bool?>("Backup") ?? Settings.Backup;
                    Settings.CloudSyncOn = sets.Value<bool?>("CloudSyncOn") ?? Settings.CloudSyncOn;
                    Settings.CloudRestoreOnLoad = sets.Value<bool?>("CloudRestoreOnLoad") ?? Settings.CloudRestoreOnLoad;
                    Settings.ManualSave = sets.Value<bool?>("ManualSave") ?? Settings.ManualSave;
                    Settings.CancelSave = sets.Value<bool?>("CancelSave") ?? Settings.CancelSave;
                    Settings.KeybdSoundRecordActive = sets.Value<bool?>("KeybdSoundRecordActive") ?? Settings.KeybdSoundRecordActive;
                    Settings.KeybdSoundCompact = sets.Value<bool?>("KeybdSoundCompact") ?? Settings.KeybdSoundCompact;
                    Settings.KeyConvertEnabled = sets.Value<bool?>("KeyConvertEnabled") ?? Settings.KeyConvertEnabled;
                    Settings.LimitKey = sets.Value<bool?>("LimitKey") ?? Settings.LimitKey;
                    Settings.GetDataFromAsyncInput = sets.Value<bool?>("GetDataFromAsyncInput") ?? Settings.GetDataFromAsyncInput;
                    Settings.MirrorInputToOS = sets.Value<bool?>("MirrorInputToOS") ?? Settings.MirrorInputToOS;

                    Settings.EffectRemoverOn = sets.Value<bool?>("EffectRemoverOn") ?? Settings.EffectRemoverOn;
                    Settings.EffectRemoverEnableSave = sets.Value<bool?>("EffectRemoverEnableSave") ?? Settings.EffectRemoverEnableSave;
                    Settings.EffectRemoverFilters = sets.Value<bool?>("EffectRemoverFilters") ?? Settings.EffectRemoverFilters;
                    Settings.EffectRemoverAdvancedFilters = sets.Value<bool?>("EffectRemoverAdvancedFilters") ?? Settings.EffectRemoverAdvancedFilters;
                    Settings.EffectRemoverParticles = sets.Value<bool?>("EffectRemoverParticles") ?? Settings.EffectRemoverParticles;
                    Settings.EffectRemoverDecorations = sets.Value<bool?>("EffectRemoverDecorations") ?? Settings.EffectRemoverDecorations;
                    Settings.EffectRemoverBackgrounds = sets.Value<bool?>("EffectRemoverBackgrounds") ?? Settings.EffectRemoverBackgrounds;
                    Settings.EffectRemoverCameras = sets.Value<bool?>("EffectRemoverCameras") ?? Settings.EffectRemoverCameras;
                    Settings.EffectRemoverRepeatEvents = sets.Value<bool?>("EffectRemoverRepeatEvents") ?? Settings.EffectRemoverRepeatEvents;
                    Settings.EffectRemoverFrameRate = sets.Value<bool?>("EffectRemoverFrameRate") ?? Settings.EffectRemoverFrameRate;
                    Settings.EffectRemoverHitSounds = sets.Value<bool?>("EffectRemoverHitSounds") ?? Settings.EffectRemoverHitSounds;
                    Settings.EffectRemoverCheckpoints = sets.Value<bool?>("EffectRemoverCheckpoints") ?? Settings.EffectRemoverCheckpoints;
                    Settings.EffectRemoverInputEvents = sets.Value<bool?>("EffectRemoverInputEvents") ?? Settings.EffectRemoverInputEvents;
                    Settings.EffectRemoverPlanetOrbit = sets.Value<bool?>("EffectRemoverPlanetOrbit") ?? Settings.EffectRemoverPlanetOrbit;
                    Settings.EffectRemoverPlanetScale = sets.Value<bool?>("EffectRemoverPlanetScale") ?? Settings.EffectRemoverPlanetScale;
                    Settings.EffectRemoverPlanetRadius = sets.Value<bool?>("EffectRemoverPlanetRadius") ?? Settings.EffectRemoverPlanetRadius;
                    Settings.EffectRemoverTrackAnimations = sets.Value<bool?>("EffectRemoverTrackAnimations") ?? Settings.EffectRemoverTrackAnimations;
                    Settings.EffectRemoverTrackPositions = sets.Value<bool?>("EffectRemoverTrackPositions") ?? Settings.EffectRemoverTrackPositions;
                    Settings.EffectRemoverTrackMoves = sets.Value<bool?>("EffectRemoverTrackMoves") ?? Settings.EffectRemoverTrackMoves;
                    Settings.EffectRemoverTrackColors = sets.Value<bool?>("EffectRemoverTrackColors") ?? Settings.EffectRemoverTrackColors;
                    Settings.EffectRemoverHoldSounds = sets.Value<bool?>("EffectRemoverHoldSounds") ?? Settings.EffectRemoverHoldSounds;
                    Settings.EffectRemoverHideIcons = sets.Value<bool?>("EffectRemoverHideIcons") ?? Settings.EffectRemoverHideIcons;
                    Settings.EffectRemoverRemoveAllDecorations = sets.Value<bool?>("EffectRemoverRemoveAllDecorations") ?? Settings.EffectRemoverRemoveAllDecorations;
                    Settings.EffectRemoverResetTrackOpacity = sets.Value<bool?>("EffectRemoverResetTrackOpacity") ?? Settings.EffectRemoverResetTrackOpacity;
                    Settings.EffectRemoverResetTrackAnimation = sets.Value<bool?>("EffectRemoverResetTrackAnimation") ?? Settings.EffectRemoverResetTrackAnimation;
                    Settings.EffectRemoverResetTrackColor = sets.Value<bool?>("EffectRemoverResetTrackColor") ?? Settings.EffectRemoverResetTrackColor;
                    Settings.EffectRemoverSetCameraZoom = sets.Value<bool?>("EffectRemoverSetCameraZoom") ?? Settings.EffectRemoverSetCameraZoom;
                    Settings.AlwaysShowMaxUsedKeys = sets.Value<bool?>("AlwaysShowMaxUsedKeys") ?? Settings.AlwaysShowMaxUsedKeys;
                    Settings.LegacyPauseCalculation = sets.Value<bool?>("LegacyPauseCalculation") ?? Settings.LegacyPauseCalculation;
                    Settings.DecorationPreviewOn = sets.Value<bool?>("DecorationPreviewOn") ?? Settings.DecorationPreviewOn;
                    Settings.MemoryOptimizerOn = sets.Value<bool?>("MemoryOptimizerOn") ?? Settings.MemoryOptimizerOn;
                    Settings.EditorBugfixOn = sets.Value<bool?>("EditorBugfixOn") ?? Settings.EditorBugfixOn;
                    Settings.BpmBeatCalculatorOn = sets.Value<bool?>("BpmBeatCalculatorOn") ?? Settings.BpmBeatCalculatorOn;
                    Settings.SpeedPauseConverterOn = sets.Value<bool?>("SpeedPauseConverterOn") ?? Settings.SpeedPauseConverterOn;
                    Settings.BetterUndoRedoOn = sets.Value<bool?>("BetterUndoRedoOn") ?? Settings.BetterUndoRedoOn;
                    Settings.BGAModeOn = sets.Value<bool?>("BGAModeOn") ?? Settings.BGAModeOn;
                    Settings.EditorUIOn = sets.Value<bool?>("EditorUIOn") ?? Settings.EditorUIOn;
                    Settings.EditorUIFloorPanelOn = sets.Value<bool?>("EditorUIFloorPanelOn") ?? Settings.EditorUIFloorPanelOn;
                    Settings.EditorUIHorizontalOn = sets.Value<bool?>("EditorUIHorizontalOn") ?? Settings.EditorUIHorizontalOn;
                    Settings.TimelineOn = sets.Value<bool?>("TimelineOn") ?? Settings.TimelineOn;
                    Settings.FixChartLoadOn = sets.Value<bool?>("FixChartLoadOn") ?? Settings.FixChartLoadOn;
                    Settings.CustomEventsOn = sets.Value<bool?>("CustomEventsOn") ?? Settings.CustomEventsOn;
                    Settings.EnableProEvents = sets.Value<bool?>("EnableProEvents") ?? Settings.EnableProEvents;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[SpectreReplay] 配置分区 \"Sets\" 载入失败（其余分区不受影响）: " + ex.Message);
            }
            try
            {
                var ints = json["Ints"];
                if (ints != null)
                {
                    // 记住上次停留的页签（越界时回首页，防页签数量变化后指错）
                    /* selected_tab 由新 UI 维护，此处不再持久化 */
                    Settings.SaveButton = ints.Value<int?>("save_button") ?? Settings.SaveButton;
                    Settings.CancelSaveButton = ints.Value<int?>("cancel_save_button") ?? Settings.CancelSaveButton;
                    Settings.KeybdSoundVolume = ints.Value<int?>("KeybdSoundVolume") ?? Settings.KeybdSoundVolume;
                    Settings.MicrophoneOffset = ints.Value<int?>("MicrophoneOffset") ?? Settings.MicrophoneOffset;
                    Settings.EffectRemoverCameraZoomScale = ints.Value<float?>("EffectRemoverCameraZoomScale") ?? Settings.EffectRemoverCameraZoomScale;
                    Settings.RenderWidth = Mathf.Clamp(ints.Value<int?>("render_width") ?? Settings.RenderWidth, 320, 7680);
                    Settings.RenderHeight = Mathf.Clamp(ints.Value<int?>("render_height") ?? Settings.RenderHeight, 240, 4320);
                    Settings.RenderFps = Mathf.Clamp(ints.Value<int?>("render_fps") ?? Settings.RenderFps, 15, 480);
                    Settings.RenderEncoderMode = Mathf.Clamp(ints.Value<int?>("render_encoder") ?? Settings.RenderEncoderMode, 0, 3);
                    Settings.RenderQualityMode = Mathf.Clamp(ints.Value<int?>("render_quality") ?? Settings.RenderQualityMode, 0, 3);
                    Settings.RenderOutputFormat = Mathf.Clamp(ints.Value<int?>("render_output_format") ?? Settings.RenderOutputFormat, 0, 3);
                    // 旧配置没有这个键：缺省时**跟随渲染格式**，保持升级前的行为
                    // （升级前转换用的就是 RenderOutputFormat）。
                    Settings.ConvertTarget = Mathf.Clamp(ints.Value<int?>("convert_target") ?? Settings.RenderOutputFormat, 0, 3);
                    Settings.RenderCaptureMode = Mathf.Clamp(ints.Value<int?>("render_capture_mode") ?? Settings.RenderCaptureMode, 0, 1);
                    Settings.RenderTailSeconds = Mathf.Clamp(ints.Value<float?>("render_tail") ?? Settings.RenderTailSeconds, 0f, 15f);
                    Settings.RenderOn = ints.Value<bool?>("render_on") ?? Settings.RenderOn;
                    Settings.RenderClockOn = ints.Value<bool?>("render_clock_on") ?? Settings.RenderClockOn;
                    Settings.RenderCursorOn = ints.Value<bool?>("render_cursor_on") ?? Settings.RenderCursorOn;
                    Settings.RenderStopOn = ints.Value<bool?>("render_stop_on") ?? Settings.RenderStopOn;
                    // 键值不 Clamp：它是 KeyCode 数值域，合法集合由 Enum.IsDefined
                    // 在轮询处把关（见 VideoRenderJob.StopRequestedByHotkey）。
                    // Clamp 到某个区间反而会把手改配置里的怪值悄悄改成另一个**看似正常**的键。
                    Settings.RenderStopKey = ints.Value<int?>("render_stop_key") ?? Settings.RenderStopKey;
                    Settings.NotificationSeconds = Mathf.Clamp(ints.Value<float?>("notification_seconds") ?? Settings.NotificationSeconds, 1f, 10f);
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[SpectreReplay] 配置分区 \"Ints\" 载入失败（其余分区不受影响）: " + ex.Message);
            }
            try
            {
                var strs = json["Strings"];
                if (strs != null)
                {
                    Settings.SavePath = strs.Value<string>("savepath") ?? Settings.SavePath;
                    Settings.InputedKey = strs.Value<string>("inputedKey") ?? Settings.InputedKey;
                    Settings.CurrLanguage = strs.Value<string>("currlanguage") ?? Settings.CurrLanguage;
                    Settings.MicrophoneDeviceName = strs.Value<string>("microphone_device_name") ?? Settings.MicrophoneDeviceName;
                    Settings.KeyCodeConvert = strs.Value<string>("key_code_convert") ?? Settings.KeyCodeConvert;
                    Settings.RenderOutputDir = strs.Value<string>("render_output_dir") ?? Settings.RenderOutputDir;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[SpectreReplay] 配置分区 \"Strings\" 载入失败（其余分区不受影响）: " + ex.Message);
            }
            try
            {
                var strLists = json["StringLists"];
                if (strLists != null)
                {
                    var kbKeys = strLists["allowed_keyboard_special_keys"]?.ToObject<HashSet<string>>();
                    if (kbKeys != null)
                        Settings.AllowedKeyboardSpecialKeys = kbKeys;
                    var asyncKeys = strLists["allowed_async_special_keys"]?.ToObject<HashSet<string>>();
                    if (asyncKeys != null)
                        Settings.AllowedAsyncSpecialKeys = asyncKeys;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[SpectreReplay] 配置分区 \"StringLists\" 载入失败（其余分区不受影响）: " + ex.Message);
            }
            Debug.Log("[SpectreReplay] 配置载入成功");
        }
        catch (JsonException ex)
        {
            Debug.Log("[SpectreReplay] 配置文件 JSON 解析失败（已回退默认设置）: " + ex.Message);
        }
        catch (IOException ex)
        {
            // 文件被占用/读取失败不该炸穿 mod 加载，落默认值即可
            Debug.Log("[SpectreReplay] 配置文件读取失败（已回退默认设置）: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            // 注意：UnauthorizedAccessException **不是** IOException 的子类
            // （权限/ACL 受限的配置文件、从别的机器拷来的只读文件都会走这里）。
            // Main.Load 没有全局 try/catch，异常穿透就等于整个 mod 加载失败 ——
            // 比"用默认设置启动"严重得多，故必须单独兜住。
            Debug.Log("[SpectreReplay] 配置文件访问被拒（权限不足）: " + ex.Message);
        }
        catch (System.Exception ex)
        {
            // 兜底：路径非法（ArgumentException）、反序列化器内部的其他异常等。
            // 配置文件损坏/不可读时降级为默认设置，绝不让 mod 起不来。
            Debug.Log("[SpectreReplay] 配置载入失败（已回退默认设置）: " + ex);
        }
    }

}
