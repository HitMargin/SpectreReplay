using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        public ColorsData Colors { get; set; } = new ColorsData();
        public CustomJudgeData CustomJudge { get; set; } = new CustomJudgeData();
    }

    // 自定义判定档案（独立嵌套节点，字段与 Settings.CustomTier* 一一对应）
    private class CustomJudgeData
    {
        public bool on { get; set; }
        public bool textOn { get; set; } = false; // 默认关：飘字改写需主动开启
        public bool[] tier_on { get; set; }
        public float[] tier_inner { get; set; }
        public float[] tier_outer { get; set; }
        public float[] tier_deg { get; set; }  // 旧版累计边界，兼容读取
        public float[] tier_ms { get; set; }
        public string[] tier_name { get; set; }
        public string[] tier_prefix { get; set; }
        public string[] tier_color { get; set; }
        public float[] tier_weight { get; set; }
        public bool[] tier_split { get; set; }
        public bool[] tier_hide { get; set; }
        public string[] tier_outline_color { get; set; }
        public string[] tier_tick_outline_color { get; set; }
        public bool realjudge { get; set; }
        public int real_pure { get; set; } = -1;
        public int real_perfect { get; set; } = -1;
        public int real_counted { get; set; } = -1;
        public bool xacc { get; set; }
        public bool only { get; set; }
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
        public bool ManualSave { get; set; }
        public bool CancelSave { get; set; }
        public bool LegacyEngine { get; set; }
        public bool KeybdSoundRecordActive { get; set; }
        public bool FollowGameLanguage { get; set; }
        public bool KeyConvertEnabled { get; set; }
        public bool LimitKey { get; set; }
        public bool GetDataFromAsyncInput { get; set; }

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
        public bool PlanetColorOn { get; set; }
        public bool JudgeTextOn { get; set; }
        public bool JudgeFxOn { get; set; }
        public bool JudgeFilterOn { get; set; }
        public bool JudgeColorOn { get; set; }
        public bool JudgeGlowOn { get; set; }
        public bool JudgeTextOutlineOn { get; set; }
        public bool JudgeTextOutlineGradientOn { get; set; }
        public bool JudgeTextParticleOn { get; set; }
        public bool JudgeBarOn { get; set; }
        public bool JudgeBarDynamicOn { get; set; }
        public bool JudgeBarShowTiersOn { get; set; }
        public bool JudgeBarUnlockStrict { get; set; }
        public bool JudgeBarEdgeColorOn { get; set; }
        public bool JudgeBarZonePureOn { get; set; }
        public bool JudgeBarZonePerfectOn { get; set; }
        public bool JudgeBarZoneCountedOn { get; set; }
        public bool JudgeBarShowFailureJudgement { get; set; }
        public bool JudgeBarFailureBandAlwaysOn { get; set; }
        public bool JudgeBarTickOutlineOn { get; set; }
        public bool OverloadBarOn { get; set; }
        public bool ResultsPlusOn { get; set; }
        public bool CalibAdviceOn { get; set; }
        public bool ResultsKeysOn { get; set; }
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
        public float judge_size_mult { get; set; }
        public float judgetext_outline_width { get; set; }
        public float judge_fade_mult { get; set; }
        public int render_width { get; set; }
        public int render_height { get; set; }
        public int render_fps { get; set; }
        public int render_encoder { get; set; }
        public int render_quality { get; set; }
        public int render_output_format { get; set; }
        public int render_capture_mode { get; set; }
        public float render_tail { get; set; }
        public float judgebar_anim_duration { get; set; }
        public float judgebar_sensitivity { get; set; }
        public float judgebar_life { get; set; }
        public float overloadbar_x { get; set; }
        public float overloadbar_y { get; set; }
        public float judgebar_edge_span { get; set; }
        public float judgebar_bg_opacity { get; set; }
        public float judgebar_pos_x { get; set; }
        public float judgebar_pos_y { get; set; }
        public int calib_games { get; set; }
    }

    private class StringsData
    {
        public string savepath { get; set; }
        public string inputedKey { get; set; }
        public string currlanguage { get; set; }
        public string microphone_device_name { get; set; }
        public string key_code_convert { get; set; }
        public List<string> judgetext_texts { get; set; }
        public string render_output_dir { get; set; }
        public string calib_samples { get; set; }
    }

    private class StringListsData
    {
        public HashSet<string> allowed_keyboard_special_keys { get; set; }
        public HashSet<string> allowed_async_special_keys { get; set; }
        public HashSet<string> judge_hidden_margins { get; set; }
    }

    // 星球颜色：hex 字符串 "#RRGGBBAA"，索引与 PlanetarySystem.allPlanets 一致，部件顺序见 PlanetColorApplier
    private class ColorsData
    {
        public bool[] planetcolor_player_on { get; set; }
        public string[] planetcolor_base { get; set; }
        public string[][] planetcolor_parts { get; set; }
        public bool[][] planetcolor_part_on { get; set; }
        public string[] judge_margin_colors { get; set; }
        public bool[] judge_margin_colors_on { get; set; }
        public string judge_glow_color { get; set; }
        public string judgebar_edge_color { get; set; }
        public string judgetext_outline_color { get; set; }
        public string judgebar_tick_outline_color { get; set; }
        public string judgebar_zone_pure { get; set; }
        public string judgebar_zone_perfect { get; set; }
        public string judgebar_zone_counted { get; set; }
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
            Debug.LogWarning("[Spectre] 配置保存被跳过：未指定配置路径");
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
                ManualSave = Settings.ManualSave,
                CancelSave = Settings.CancelSave,
                LegacyEngine = Settings.LegacyEngine,
                KeybdSoundRecordActive = Settings.KeybdSoundRecordActive,
                FollowGameLanguage = Settings.FollowGameLanguage,
                KeyConvertEnabled = Settings.KeyConvertEnabled,
                LimitKey = Settings.LimitKey,
                GetDataFromAsyncInput = Settings.GetDataFromAsyncInput,

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
                PlanetColorOn = Settings.PlanetColorOn,
                JudgeTextOn = Settings.JudgeTextOn,
                JudgeFxOn = Settings.JudgeFxOn,
                JudgeFilterOn = Settings.JudgeFilterOn,
                JudgeColorOn = Settings.JudgeColorOn,
                JudgeGlowOn = Settings.JudgeGlowOn,
                JudgeTextOutlineOn = Settings.JudgeTextOutlineOn,
                JudgeTextOutlineGradientOn = Settings.JudgeTextOutlineGradientOn,
                JudgeTextParticleOn = Settings.JudgeTextParticleOn,
                JudgeBarOn = Settings.JudgeBarOn,
                JudgeBarDynamicOn = Settings.JudgeBarDynamicOn,
                JudgeBarShowTiersOn = Settings.JudgeBarShowTiersOn,
                JudgeBarUnlockStrict = Settings.JudgeBarUnlockStrict,
                JudgeBarEdgeColorOn = Settings.JudgeBarEdgeColorOn,
                JudgeBarZonePureOn = Settings.JudgeBarZonePureOn,
                JudgeBarZonePerfectOn = Settings.JudgeBarZonePerfectOn,
                JudgeBarZoneCountedOn = Settings.JudgeBarZoneCountedOn,
                JudgeBarShowFailureJudgement = Settings.JudgeBarShowFailureJudgement,
                JudgeBarFailureBandAlwaysOn = Settings.JudgeBarFailureBandAlwaysOn,
                JudgeBarTickOutlineOn = Settings.JudgeBarTickOutlineOn,
                OverloadBarOn = Settings.OverloadBarOn,
                ResultsPlusOn = Settings.ResultsPlusOn,
                CalibAdviceOn = Settings.CalibAdviceOn,
                ResultsKeysOn = Settings.ResultsKeysOn,
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
                judge_size_mult = Settings.JudgeTextSizeMult,
                judgetext_outline_width = Settings.JudgeTextOutlineWidth,
                judge_fade_mult = Settings.JudgeTextFadeMult,
                render_width = Settings.RenderWidth,
                render_height = Settings.RenderHeight,
                render_fps = Settings.RenderFps,
                render_encoder = Settings.RenderEncoderMode,
                render_quality = Settings.RenderQualityMode,
                render_output_format = Settings.RenderOutputFormat,
                render_capture_mode = Settings.RenderCaptureMode,
                render_tail = Settings.RenderTailSeconds,
                judgebar_anim_duration = Settings.JudgeBarAnimationDuration,
                judgebar_sensitivity = Settings.JudgeBarSensitivity,
                judgebar_life = Settings.JudgeBarTickLife,
                overloadbar_x = Settings.OverloadBarX,
                overloadbar_y = Settings.OverloadBarY,
                judgebar_edge_span = Settings.JudgeBarEdgeSpan,
                judgebar_bg_opacity = Settings.JudgeBarBackgroundOpacity,
                judgebar_pos_x = Settings.JudgeBarPositionX,
                judgebar_pos_y = Settings.JudgeBarPositionY,
                calib_games = Settings.CalibGames
            },
            Strings = new StringsData
            {
                savepath = Settings.SavePath,
                inputedKey = Settings.InputedKey,
                currlanguage = Settings.CurrLanguage,
                microphone_device_name = Settings.MicrophoneDeviceName,
                key_code_convert = Settings.KeyCodeConvert,
                judgetext_texts = new List<string>(Settings.JudgeTexts),
                render_output_dir = Settings.RenderOutputDir,
                calib_samples = Settings.CalibSamples
            },
            StringLists = new StringListsData
            {
                allowed_keyboard_special_keys = Settings.AllowedKeyboardSpecialKeys,
                allowed_async_special_keys = Settings.AllowedAsyncSpecialKeys,
                judge_hidden_margins = Settings.JudgeHiddenMargins
            },
            Colors = new ColorsData
            {
                planetcolor_player_on = Settings.PlanetColorPlayerOn,
                planetcolor_base = ToHexArray(Settings.PlanetColorBase),
                planetcolor_parts = ToHexArrays(Settings.PlanetColorParts),
                planetcolor_part_on = Settings.PlanetColorPartsOn,
                judge_margin_colors = ToHexArray(Settings.JudgeMarginColors),
                judge_margin_colors_on = Settings.JudgeMarginColorsOn,
                judge_glow_color = ToHex(Settings.JudgeGlowColor),
                judgetext_outline_color = ToHex(Settings.JudgeTextOutlineColor),
                judgebar_edge_color = ToHex(Settings.JudgeBarEdgeColor),
                judgebar_zone_pure = ToHex(Settings.JudgeBarPureColor),
                judgebar_zone_perfect = ToHex(Settings.JudgeBarPerfectColor),
                judgebar_zone_counted = ToHex(Settings.JudgeBarCountedColor),
                judgebar_tick_outline_color = ToHex(Settings.JudgeBarTickOutlineColor)
            }
        };
        // SaveCustomJudge() 原本在上面的构造里求值（在 try 之外）——它遍历档位表的
        // 十个平行 List 取色，任何一处长度不同步都会抛 IndexOutOfRangeException 并穿透
        // 到调用方。挪到这里与序列化/写盘同处一个 try。
        CustomJudgeData customJudge;
        try
        {
            customJudge = SaveCustomJudge();
        }
        catch (Exception ex)
        {
            Debug.Log("[Spectre] 档位表取值失败，本次配置将不带 CustomJudge 段: "
                + ex.GetType().Name + " " + ex.Message);
            customJudge = null;
        }
        if (customJudge != null) data.CustomJudge = customJudge;
        // 从这里往下的全部工作（序列化、建目录、写盘）都在
        // try 保护内。SaveConfigs 的 4 个调用方都**没有**自己的 try —— UM4 的
        // OnHideGUI 回调、退出钩子、设置页保存按钮，而 UM4 不包异常。异常穿透的后果
        // 不只是"没存上"，还会**中断调用方后续的清理**：/*SettingsUI*/ 里
        // AudioRecorder.StopAllMicrophones() 排在保存之后，一旦抛出，麦克风会一直开着；
        // Glue.SpectreConfMgr.OnSave 里 UiConfigManager.Save() 同理会连带落空。
        string contents;
        try
        {
            contents = JsonConvert.SerializeObject(data, Formatting.Indented);
        }
        catch (Exception ex)
        {
            Debug.Log("[Spectre] 配置序列化失败（本次设置改动未落盘）: " + ex.GetType().Name + " " + ex.Message);
            return false;
        }
        string directoryName = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directoryName))
            return true;
        // 落盘失败必须就地兜住而不是往上抛：SaveConfigs 的 4 个调用方都**没有**自己的
        // try —— UM4 的 OnHideGUI 回调、退出钩子、设置页保存按钮。异常穿透的后果不只是
        // "没存上"，还会**中断调用方后续的清理**：/*SettingsUI*/ 里
        // AudioRecorder.StopAllMicrophones() 排在保存之后，一旦抛出，麦克风会一直开着；
        // UiConfigManager.Save() 同理会连带落空。
        try
        {
            if (!Directory.Exists(directoryName))
                Directory.CreateDirectory(directoryName);
            File.WriteAllText(filePath, contents);
        }
        catch (Exception ex)
        {
            // 只记日志：抛出去对用户没有帮助（UI 已经关掉了），反而会打断清理链
            Debug.Log("[Spectre] 配置保存失败（本次设置改动未落盘，重启后会丢失）: "
                + ex.GetType().Name + " " + ex.Message);
            return false;
        }
        return true;
    }

    public static void LoadConfigs(string filePath)
    {
        // 与 SaveConfigs 对称：路径为空时直接返回，别把 configPath 污染成 null
        // （之后 SaveSettings 会读到它）。File.Exists(null) 本身不抛，但状态不该被写坏。
        if (string.IsNullOrEmpty(filePath))
        {
            Debug.LogWarning("[Spectre] 配置载入被跳过：未指定配置路径");
            return;
        }
        configPath = filePath;
        if (!File.Exists(filePath))
        {
            Debug.Log("[Spectre] 配置文件不存在（用默认设置启动）: " + filePath);
            return;
        }
        try
        {
            var json = JObject.Parse(File.ReadAllText(filePath));
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
                Settings.ManualSave = sets.Value<bool?>("ManualSave") ?? Settings.ManualSave;
                Settings.LegacyEngine = sets.Value<bool?>("LegacyEngine") ?? Settings.LegacyEngine;
                Settings.KeybdSoundRecordActive = sets.Value<bool?>("KeybdSoundRecordActive") ?? Settings.KeybdSoundRecordActive;
                Settings.FollowGameLanguage = sets.Value<bool?>("FollowGameLanguage") ?? Settings.FollowGameLanguage;
                Settings.KeyConvertEnabled = sets.Value<bool?>("KeyConvertEnabled") ?? Settings.KeyConvertEnabled;
                Settings.LimitKey = sets.Value<bool?>("LimitKey") ?? Settings.LimitKey;
                Settings.GetDataFromAsyncInput = sets.Value<bool?>("GetDataFromAsyncInput") ?? Settings.GetDataFromAsyncInput;

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
                Settings.PlanetColorOn = sets.Value<bool?>("PlanetColorOn") ?? Settings.PlanetColorOn;
                Settings.JudgeTextOn = sets.Value<bool?>("JudgeTextOn") ?? Settings.JudgeTextOn;
                Settings.JudgeFxOn = sets.Value<bool?>("JudgeFxOn") ?? Settings.JudgeFxOn;
                Settings.JudgeFilterOn = sets.Value<bool?>("JudgeFilterOn") ?? Settings.JudgeFilterOn;
                Settings.JudgeColorOn = sets.Value<bool?>("JudgeColorOn") ?? Settings.JudgeColorOn;
                Settings.JudgeGlowOn = sets.Value<bool?>("JudgeGlowOn") ?? Settings.JudgeGlowOn;
                Settings.JudgeTextOutlineOn = sets.Value<bool?>("JudgeTextOutlineOn") ?? Settings.JudgeTextOutlineOn;
                Settings.JudgeTextOutlineGradientOn = sets.Value<bool?>("JudgeTextOutlineGradientOn") ?? Settings.JudgeTextOutlineGradientOn;
                Settings.JudgeTextParticleOn = sets.Value<bool?>("JudgeTextParticleOn") ?? Settings.JudgeTextParticleOn;
                Settings.JudgeBarOn = sets.Value<bool?>("JudgeBarOn") ?? Settings.JudgeBarOn;
                Settings.JudgeBarDynamicOn = sets.Value<bool?>("JudgeBarDynamicOn") ?? Settings.JudgeBarDynamicOn;
                Settings.JudgeBarShowTiersOn = sets.Value<bool?>("JudgeBarShowTiersOn") ?? Settings.JudgeBarShowTiersOn;
                Settings.JudgeBarUnlockStrict = sets.Value<bool?>("JudgeBarUnlockStrict") ?? Settings.JudgeBarUnlockStrict;
                Settings.JudgeBarEdgeColorOn = sets.Value<bool?>("JudgeBarEdgeColorOn") ?? Settings.JudgeBarEdgeColorOn;
                Settings.JudgeBarZonePureOn = sets.Value<bool?>("JudgeBarZonePureOn") ?? Settings.JudgeBarZonePureOn;
                Settings.JudgeBarZonePerfectOn = sets.Value<bool?>("JudgeBarZonePerfectOn") ?? Settings.JudgeBarZonePerfectOn;
                Settings.JudgeBarZoneCountedOn = sets.Value<bool?>("JudgeBarZoneCountedOn") ?? Settings.JudgeBarZoneCountedOn;
                Settings.JudgeBarTickOutlineOn = sets.Value<bool?>("JudgeBarTickOutlineOn") ?? Settings.JudgeBarTickOutlineOn;
                Settings.JudgeBarShowFailureJudgement = sets.Value<bool?>("JudgeBarShowFailureJudgement") ?? Settings.JudgeBarShowFailureJudgement;
                Settings.JudgeBarFailureBandAlwaysOn = sets.Value<bool?>("JudgeBarFailureBandAlwaysOn") ?? Settings.JudgeBarFailureBandAlwaysOn;
                Settings.OverloadBarOn = sets.Value<bool?>("OverloadBarOn") ?? Settings.OverloadBarOn;
                Settings.ResultsPlusOn = sets.Value<bool?>("ResultsPlusOn") ?? Settings.ResultsPlusOn;
                Settings.CalibAdviceOn = sets.Value<bool?>("CalibAdviceOn") ?? Settings.CalibAdviceOn;
                Settings.ResultsKeysOn = sets.Value<bool?>("ResultsKeysOn") ?? Settings.ResultsKeysOn;
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
                // 旧配置迁移：P1/P2 开关写入对应星球
                bool? p1On = sets.Value<bool?>("PlanetColorP1On");
                if (p1On.HasValue)
                    Settings.PlanetColorPlayerOn[0] = p1On.Value;
                bool? p2On = sets.Value<bool?>("PlanetColorP2On");
                if (p2On.HasValue)
                    Settings.PlanetColorPlayerOn[1] = p2On.Value;
            }
            }
            catch (Exception ex)
            {
                Debug.Log("[Spectre] 配置分区 \"Sets\" 载入失败（其余分区不受影响）: " + ex.Message);
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
                // 范围与 UI 滑条一致（judgetext.size / judgetext.fadespeed）。这两个值每次飘字
                // 都直接乘到游戏的 scale / fadeSpeed 上（Patch_HitTextShowFx），手改配置写入
                // 极大值会让文本缩放爆掉，写 1e400 会被解析成 Infinity 灌进 Unity 变换。
                // 同区块的 outline_width / render_* 都已 Clamp，此前只有这两个漏了。
                Settings.JudgeTextSizeMult = Mathf.Clamp(ints.Value<float?>("judge_size_mult") ?? Settings.JudgeTextSizeMult, 0.5f, 3f);
                Settings.JudgeTextOutlineWidth = Mathf.Clamp(ints.Value<float?>("judgetext_outline_width") ?? Settings.JudgeTextOutlineWidth, 0f, 1f);
                Settings.JudgeTextFadeMult = Mathf.Clamp(ints.Value<float?>("judge_fade_mult") ?? Settings.JudgeTextFadeMult, 0.25f, 5f);
                Settings.RenderWidth = Mathf.Clamp(ints.Value<int?>("render_width") ?? Settings.RenderWidth, 320, 7680);
                Settings.RenderHeight = Mathf.Clamp(ints.Value<int?>("render_height") ?? Settings.RenderHeight, 240, 4320);
                Settings.RenderFps = Mathf.Clamp(ints.Value<int?>("render_fps") ?? Settings.RenderFps, 15, 480);
                Settings.RenderEncoderMode = Mathf.Clamp(ints.Value<int?>("render_encoder") ?? Settings.RenderEncoderMode, 0, 3);
                Settings.RenderQualityMode = Mathf.Clamp(ints.Value<int?>("render_quality") ?? Settings.RenderQualityMode, 0, 3);
                Settings.RenderOutputFormat = Mathf.Clamp(ints.Value<int?>("render_output_format") ?? Settings.RenderOutputFormat, 0, 3);
                Settings.RenderCaptureMode = Mathf.Clamp(ints.Value<int?>("render_capture_mode") ?? Settings.RenderCaptureMode, 0, 1);
                Settings.RenderTailSeconds = Mathf.Clamp(ints.Value<float?>("render_tail") ?? Settings.RenderTailSeconds, 0f, 15f);
                Settings.JudgeBarAnimationDuration = Mathf.Clamp(ints.Value<float?>("judgebar_anim_duration") ?? Settings.JudgeBarAnimationDuration, 0f, 1f);
                Settings.JudgeBarSensitivity = Mathf.Clamp(ints.Value<float?>("judgebar_sensitivity") ?? Settings.JudgeBarSensitivity, 0f, 1f);
                Settings.JudgeBarTickLife = Mathf.Clamp(ints.Value<float?>("judgebar_life") ?? Settings.JudgeBarTickLife, 0f, 10f);
                Settings.OverloadBarX = Mathf.Clamp(ints.Value<float?>("overloadbar_x") ?? Settings.OverloadBarX, -960f, 960f);
                Settings.OverloadBarY = Mathf.Clamp(ints.Value<float?>("overloadbar_y") ?? Settings.OverloadBarY, -960f, 960f);
                Settings.JudgeBarEdgeSpan = Mathf.Clamp(ints.Value<float?>("judgebar_edge_span") ?? Settings.JudgeBarEdgeSpan, 60f, 70f);
                Settings.JudgeBarBackgroundOpacity = Mathf.Clamp(ints.Value<float?>("judgebar_bg_opacity") ?? Settings.JudgeBarBackgroundOpacity, 0f, 1f);
                Settings.JudgeBarPositionX = Mathf.Clamp(ints.Value<float?>("judgebar_pos_x") ?? Settings.JudgeBarPositionX, -960f, 960f);
                Settings.JudgeBarPositionY = Mathf.Clamp(ints.Value<float?>("judgebar_pos_y") ?? Settings.JudgeBarPositionY, -960f, 960f);
                Settings.CalibGames = ints.Value<int?>("calib_games") ?? Settings.CalibGames;
            }
            }
            catch (Exception ex)
            {
                Debug.Log("[Spectre] 配置分区 \"Ints\" 载入失败（其余分区不受影响）: " + ex.Message);
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
                var judgeTexts = strs["judgetext_texts"]?.ToObject<List<string>>();
                if (judgeTexts != null)
                    for (int i = 0; i < Settings.JudgeTexts.Length && i < judgeTexts.Count; i++)
                        Settings.JudgeTexts[i] = judgeTexts[i] ?? "";
                Settings.RenderOutputDir = strs.Value<string>("render_output_dir") ?? Settings.RenderOutputDir;
                Settings.CalibSamples = strs.Value<string>("calib_samples") ?? Settings.CalibSamples;
                SpectreReplay.Features.ResultsPlus.CalibrationAdvisor.LoadState();
            }
            }
            catch (Exception ex)
            {
                Debug.Log("[Spectre] 配置分区 \"Strings\" 载入失败（其余分区不受影响）: " + ex.Message);
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
                var hiddenMargins = strLists["judge_hidden_margins"]?.ToObject<HashSet<string>>();
                if (hiddenMargins != null)
                    Settings.JudgeHiddenMargins = hiddenMargins;
            }
            }
            catch (Exception ex)
            {
                Debug.Log("[Spectre] 配置分区 \"StringLists\" 载入失败（其余分区不受影响）: " + ex.Message);
            }
            try
            {
            var colors = json["Colors"];
            if (colors != null)
            {
                var playerOns = colors["planetcolor_player_on"]?.ToObject<bool[]>();
                if (playerOns != null)
                    for (int i = 0; i < Settings.PlanetColorPlayerOn.Length && i < playerOns.Length; i++)
                        Settings.PlanetColorPlayerOn[i] = playerOns[i];

                var bases = colors["planetcolor_base"]?.ToObject<string[]>();
                if (bases != null)
                    for (int i = 0; i < Settings.PlanetColorBase.Length && i < bases.Length; i++)
                        if (TryParseHex(bases[i], out Color c))
                            Settings.PlanetColorBase[i] = c;

                var parts = colors["planetcolor_parts"]?.ToObject<string[][]>();
                if (parts != null)
                    for (int i = 0; i < Settings.PlanetColorParts.Length && i < parts.Length; i++)
                    {
                        if (parts[i] == null) continue;
                        for (int j = 0; j < Settings.PlanetColorParts[i].Length && j < parts[i].Length; j++)
                            if (TryParseHex(parts[i][j], out Color c))
                                Settings.PlanetColorParts[i][j] = c;
                    }

                var partOns = colors["planetcolor_part_on"]?.ToObject<bool[][]>();
                if (partOns != null)
                    for (int i = 0; i < Settings.PlanetColorPartsOn.Length && i < partOns.Length; i++)
                    {
                        if (partOns[i] == null) continue;
                        for (int j = 0; j < Settings.PlanetColorPartsOn[i].Length && j < partOns[i].Length; j++)
                            Settings.PlanetColorPartsOn[i][j] = partOns[i][j];
                    }

                var judgeColors = colors["judge_margin_colors"]?.ToObject<string[]>();
                if (judgeColors != null)
                    for (int i = 0; i < Settings.JudgeMarginColors.Length && i < judgeColors.Length; i++)
                        if (TryParseHex(judgeColors[i], out Color jc))
                            Settings.JudgeMarginColors[i] = jc;

                var judgeColorOns = colors["judge_margin_colors_on"]?.ToObject<bool[]>();
                if (judgeColorOns != null)
                    for (int i = 0; i < Settings.JudgeMarginColorsOn.Length && i < judgeColorOns.Length; i++)
                        Settings.JudgeMarginColorsOn[i] = judgeColorOns[i];

                string judgeGlow = colors.Value<string>("judge_glow_color");
                if (TryParseHex(judgeGlow, out Color glowColor))
                    Settings.JudgeGlowColor = glowColor;

                string textOutline = colors.Value<string>("judgetext_outline_color");
                if (TryParseHex(textOutline, out Color textOutlineColor))
                    Settings.JudgeTextOutlineColor = textOutlineColor;

                string edgeColor = colors.Value<string>("judgebar_edge_color");
                if (TryParseHex(edgeColor, out Color barEdgeColor))
                    Settings.JudgeBarEdgeColor = barEdgeColor;

                string zonePure = colors.Value<string>("judgebar_zone_pure");
                if (TryParseHex(zonePure, out Color zonePureColor))
                    Settings.JudgeBarPureColor = zonePureColor;
                string zonePerfect = colors.Value<string>("judgebar_zone_perfect");
                if (TryParseHex(zonePerfect, out Color zonePerfectColor))
                    Settings.JudgeBarPerfectColor = zonePerfectColor;
                string zoneCounted = colors.Value<string>("judgebar_zone_counted");
                if (TryParseHex(zoneCounted, out Color zoneCountedColor))
                    Settings.JudgeBarCountedColor = zoneCountedColor;
                string tickOutline = colors.Value<string>("judgebar_tick_outline_color");
                if (TryParseHex(tickOutline, out Color tickOutlineColor))
                    Settings.JudgeBarTickOutlineColor = tickOutlineColor;
            }
            }
            catch (Exception ex)
            {
                Debug.Log("[Spectre] 配置分区 \"Colors\" 载入失败（其余分区不受影响）: " + ex.Message);
            }
            // CustomJudge 分区自身已带 try（内部结构不同），无需再包一层
            var cj = json["CustomJudge"];
            if (cj != null)
            {
                try
                {
                    var data = cj.ToObject<CustomJudgeData>();
                    if (data != null)
                        LoadCustomJudge(data);
                }
                catch (System.Exception ex)
                {
                    Debug.Log("[Spectre] 自定义判定配置解析失败（档位表回退默认值）: " + ex.Message);
                }
            }
            Debug.Log("[Spectre] 配置载入成功");
        }
        catch (JsonException ex)
        {
            Debug.Log("[Spectre] 配置文件 JSON 解析失败（已回退默认设置）: " + ex.Message);
        }
        catch (IOException ex)
        {
            // 文件被占用/读取失败不该炸穿 mod 加载，落默认值即可
            Debug.Log("[Spectre] 配置文件读取失败（已回退默认设置）: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            // 注意：UnauthorizedAccessException **不是** IOException 的子类
            // （权限/ACL 受限的配置文件、从别的机器拷来的只读文件都会走这里）。
            // Main.Load 没有全局 try/catch，异常穿透就等于整个 mod 加载失败 ——
            // 比"用默认设置启动"严重得多，故必须单独兜住。
            Debug.Log("[Spectre] 配置文件访问被拒（权限不足）: " + ex.Message);
        }
        catch (System.Exception ex)
        {
            // 兜底：路径非法（ArgumentException）、反序列化器内部的其他异常等。
            // 配置文件损坏/不可读时降级为默认设置，绝不让 mod 起不来。
            Debug.Log("[Spectre] 配置载入失败（已回退默认设置）: " + ex);
        }
    }

    private static string ToHex(Color c) => "#" + ColorUtility.ToHtmlStringRGBA(c);    private static string[] ToHexArray(Color[] colors)
    {
        var arr = new string[colors.Length];
        for (int i = 0; i < colors.Length; i++)
            arr[i] = "#" + ColorUtility.ToHtmlStringRGBA(colors[i]);
        return arr;
    }

    private static string[][] ToHexArrays(Color[][] colorArrays)
    {
        var arr = new string[colorArrays.Length][];
        for (int i = 0; i < colorArrays.Length; i++)
            arr[i] = ToHexArray(colorArrays[i]);
        return arr;
    }

    private static bool TryParseHex(string s, out Color color)
    {
        color = Color.clear;
        if (string.IsNullOrEmpty(s)) return false;
        return ColorUtility.TryParseHtmlString(s.StartsWith("#") ? s : "#" + s, out color);
    }

    // 自定义判定档案持久化：九个平行 List 按索引对齐整体重建（档数 = 列表长度）
    private static CustomJudgeData SaveCustomJudge()
    {
        int n = Settings.CustomTierOn.Count;
        var color = new string[n];
        var outlineColor = new string[n];
        var tickOutlineColor = new string[n];
        for (int i = 0; i < n; i++)
        {
            color[i] = "#" + ColorUtility.ToHtmlStringRGBA(Settings.CustomTierColor[i]);
            outlineColor[i] = "#" + ColorUtility.ToHtmlStringRGBA(
                i < Settings.CustomTierOutlineColor.Count ? Settings.CustomTierOutlineColor[i] : default);
            tickOutlineColor[i] = "#" + ColorUtility.ToHtmlStringRGBA(
                i < Settings.CustomTierTickOutlineColor.Count ? Settings.CustomTierTickOutlineColor[i] : default);
        }
        return new CustomJudgeData
        {
            on = Settings.CustomJudgeOn,
            textOn = Settings.CustomJudgeTextOn,
            tier_on = Settings.CustomTierOn.ToArray(),
            tier_inner = Settings.CustomTierInner.ToArray(),
            tier_outer = Settings.CustomTierOuter.ToArray(),
            tier_ms = Settings.CustomTierMs.ToArray(),
            tier_name = Settings.CustomTierName.ToArray(),
            tier_prefix = Settings.CustomTierPrefix.ToArray(),
            tier_color = color,
            tier_weight = Settings.CustomTierWeight.ToArray(),
            tier_split = Settings.CustomTierSplit.ToArray(),
            tier_hide = Settings.CustomTierHide.ToArray(),
            tier_outline_color = outlineColor,
            tier_tick_outline_color = tickOutlineColor,
            realjudge = Settings.CustomRealJudgeOn,
            real_pure = Settings.CustomRealPure,
            real_perfect = Settings.CustomRealPerfect,
            real_counted = Settings.CustomRealCounted,
            xacc = Settings.CustomXaccOn,
            only = Settings.CustomOnlyOn
        };
    }

    private static void LoadCustomJudge(CustomJudgeData d)
    {
        Settings.CustomJudgeOn = d.on;
        Settings.CustomJudgeTextOn = d.textOn; // 默认 false（旧配置无此键 = 不改飘字）
        // 档数以 tier_on 为准；outer 兼容旧键 tier_deg，inner 缺失时按旧累计模型推导
        //（inner[0]=0、inner[i]=outer[i-1]，语义等价迁移）
        int n = d.tier_on?.Length ?? 0;
        Settings.CustomTierOn = new List<bool>(n);
        Settings.CustomTierInner = new List<float>(n);
        Settings.CustomTierOuter = new List<float>(n);
        Settings.CustomTierMs = new List<float>(n);
        Settings.CustomTierName = new List<string>(n);
        Settings.CustomTierPrefix = new List<string>(n);
        Settings.CustomTierColor = new List<Color>(n);
        Settings.CustomTierWeight = new List<float>(n);
        Settings.CustomTierSplit = new List<bool>(n);
        Settings.CustomTierHide = new List<bool>(n);
        Settings.CustomTierOutlineColor = new List<Color>(n);
        float[] outerSrc = d.tier_outer ?? d.tier_deg;
        for (int i = 0; i < n; i++)
        {
            Settings.CustomTierOn.Add(d.tier_on[i]);
            Settings.CustomTierOuter.Add(At(outerSrc, i));
            if (d.tier_inner != null && i < d.tier_inner.Length)
                Settings.CustomTierInner.Add(d.tier_inner[i]);
            else
                Settings.CustomTierInner.Add(i > 0 ? At(outerSrc, i - 1) : 0f);
            Settings.CustomTierMs.Add(At(d.tier_ms, i));
            Settings.CustomTierName.Add(At(d.tier_name, i) ?? "");
            Settings.CustomTierPrefix.Add(At(d.tier_prefix, i) ?? "");
            Settings.CustomTierColor.Add(Color.white);
            Settings.CustomTierWeight.Add(At(d.tier_weight, i, 1f));
            Settings.CustomTierSplit.Add(At(d.tier_split, i));
            Settings.CustomTierHide.Add(At(d.tier_hide, i));
            Settings.CustomTierOutlineColor.Add(default); // 透明 = 不描边（旧配置兼容）
            Settings.CustomTierTickOutlineColor.Add(default);
        }
        if (d.tier_color != null)
            for (int i = 0; i < n && i < d.tier_color.Length; i++)
                if (TryParseHex(d.tier_color[i], out Color c))
                    Settings.CustomTierColor[i] = c;
        if (d.tier_outline_color != null)
            for (int i = 0; i < n && i < d.tier_outline_color.Length; i++)
                if (TryParseHex(d.tier_outline_color[i], out Color oc))
                    Settings.CustomTierOutlineColor[i] = oc;
        if (d.tier_tick_outline_color != null)
            for (int i = 0; i < n && i < d.tier_tick_outline_color.Length; i++)
                if (TryParseHex(d.tier_tick_outline_color[i], out Color toc))
                    Settings.CustomTierTickOutlineColor[i] = toc;
        Settings.CustomRealJudgeOn = d.realjudge;
        int last = Mathf.Max(n - 1, 0);
        if (d.real_pure >= 0) Settings.CustomRealPure = Mathf.Clamp(d.real_pure, 0, last);
        if (d.real_perfect >= 0) Settings.CustomRealPerfect = Mathf.Clamp(d.real_perfect, 0, last);
        if (d.real_counted >= 0) Settings.CustomRealCounted = Mathf.Clamp(d.real_counted, 0, last);
        Settings.CustomXaccOn = d.xacc;
        Settings.CustomOnlyOn = d.only;
        // 档位表已按存档重建：账本立刻对齐尺寸（否则首次 Commit 前结算页读计数会越界）
        Features.CustomJudge.TierJudgeState.Reset();
    }

    private static float At(float[] src, int i, float fill = 0f)
        => src != null && i < src.Length ? src[i] : fill;

    private static bool At(bool[] src, int i)
        => src != null && i < src.Length && src[i];

    private static string At(string[] src, int i)
        => src != null && i < src.Length ? src[i] : null;
}
