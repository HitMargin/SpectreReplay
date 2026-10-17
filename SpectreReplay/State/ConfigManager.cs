using System;
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
        /// <summary>转换目标容器（与渲染格式解耦，见 Settings.ConvertTarget）。</summary>
        public int convert_target { get; set; }
        public int render_capture_mode { get; set; }
        public float render_tail { get; set; }
        public bool render_on { get; set; }
        // 实验性：确定性渲染时钟。缺键时跟随字段默认值（false = 关），
        // 保持升级前行为——此前这条补丁是"常开无开关"，但**旧配置里没有这个键**，
        // 若缺键当 true 会让所有存量玩家在不知情下启用实验性功能。
        public bool render_clock_on { get; set; }
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
                ManualSave = Settings.ManualSave,
                CancelSave = Settings.CancelSave,
                LegacyEngine = Settings.LegacyEngine,
                KeybdSoundRecordActive = Settings.KeybdSoundRecordActive,
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
                convert_target = Settings.ConvertTarget,
                render_capture_mode = Settings.RenderCaptureMode,
                render_tail = Settings.RenderTailSeconds,
                render_on = Settings.RenderOn,
                render_clock_on = Settings.RenderClockOn,
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
            Debug.Log("[SpectreReplay] 配置序列化失败（本次设置改动未落盘）: " + ex.GetType().Name + " " + ex.Message);
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
            Debug.Log("[SpectreReplay] 配置保存失败（本次设置改动未落盘，重启后会丢失）: "
                + ex.GetType().Name + " " + ex.Message);
            return false;
        }
        return true;
    }

    /// <summary>
    /// 把自研引擎时代的扁平 Configs.json 升级成现在的分区结构。
    ///
    /// **为什么需要它。** 旧结构是顶层 PascalCase（RecordingOn / Surface / Width /
    /// Fps / Encoder / DuplicateWhenLate…），新结构是 Sets/Ints/Strings 分区。
    /// 部署脚本从不覆盖 Configs.json，所以升级用户磁盘上一直躺着旧文件。
    /// 读取侧全是 `?? Settings.X`，缺键就落默认值——不崩溃，但**玩家原来的
    /// 选择被静默改回默认**（旧文件 Surface=1 没人读，实际变成 RenderCaptureMode=0）。
    ///
    /// 判据：有旧键且没有新分区。两个结构互斥，不会误判新版文件。
    /// 迁移后立刻落盘，否则这次会话改的东西下次启动又按旧文件读一遍。
    /// </summary>
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
            // ── 旧版配置迁移（早期扁平格式 → 现在的分区格式）───────────────
            //
            // 旧 Configs.json 是**扁平**结构（顶层 PascalCase：RecordingOn /
            // Surface / Width / Fps / Encoder / DuplicateWhenLate…），新结构是
            // Sets/Ints/Strings 分区。部署脚本从不覆盖 Configs.json，
            // 所以升级用户的磁盘上躺着的一直是旧文件。
            //
            // 不迁移的后果不是崩溃（读取侧全是 `?? Settings.X`，读到缺键就落默认值），
            // 而是**玩家原来的选择被静默改回默认**：比如旧文件里 Surface=1（整屏取帧）
            // 没人读，实际变成 RenderCaptureMode=0（纯净相机），玩家只觉得
            // "我设过的东西怎么自己变了"，而日志里一行都没有。
            //
            // 判据用"有旧键且没有新分区"——两个结构互斥，不会误判。
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
                Settings.ManualSave = sets.Value<bool?>("ManualSave") ?? Settings.ManualSave;
                Settings.LegacyEngine = sets.Value<bool?>("LegacyEngine") ?? Settings.LegacyEngine;
                Settings.KeybdSoundRecordActive = sets.Value<bool?>("KeybdSoundRecordActive") ?? Settings.KeybdSoundRecordActive;
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
                // 旧配置没有这个键：缺省时**跟随渲染格式**，保持升级前的行为
                // （升级前转换用的就是 RenderOutputFormat）。
                Settings.ConvertTarget = Mathf.Clamp(ints.Value<int?>("convert_target") ?? Settings.RenderOutputFormat, 0, 3);
                Settings.RenderCaptureMode = Mathf.Clamp(ints.Value<int?>("render_capture_mode") ?? Settings.RenderCaptureMode, 0, 1);
                Settings.RenderTailSeconds = Mathf.Clamp(ints.Value<float?>("render_tail") ?? Settings.RenderTailSeconds, 0f, 15f);
                Settings.RenderOn = ints.Value<bool?>("render_on") ?? Settings.RenderOn;
                Settings.RenderClockOn = ints.Value<bool?>("render_clock_on") ?? Settings.RenderClockOn;
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
                var hiddenMargins = strLists["judge_hidden_margins"]?.ToObject<HashSet<string>>();
                if (hiddenMargins != null)
                    Settings.JudgeHiddenMargins = hiddenMargins;
            }
            }
            catch (Exception ex)
            {
                Debug.Log("[SpectreReplay] 配置分区 \"StringLists\" 载入失败（其余分区不受影响）: " + ex.Message);
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
                Debug.Log("[SpectreReplay] 配置分区 \"Colors\" 载入失败（其余分区不受影响）: " + ex.Message);
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

    private static string ToHex(Color c) => "#" + ColorUtility.ToHtmlStringRGBA(c);    private static string[] ToHexArray(Color[] colors)
    {
        // null 兜底：调用方传 null 时返回 null，而不是在这里抛 NRE。
        // 传 null 曾让整份设置写不下去（PlanetColorBase 那次）；这类"助手函数被
        // null 打穿"的崩溃一律就地堵死。返回 null 对 JSON 是合法值，读回来仍是
        // null，而各调用点本来就有 (x != null) 判断。
        if (colors == null) return null;
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
}
