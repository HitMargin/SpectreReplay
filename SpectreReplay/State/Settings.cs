using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace SpectreReplay;

// 模组设置（纯数据）：全部持久化字段的唯一事实源 + 启动归一化（check）+ 键位重映射表。
// 界面绘制在 SettingsUI（经 using static 引用本类字段）；落盘由 ConfigManager 负责。
public static class Settings
{
    // 回放系统总开关：false = 卸载录制/回放/保存/录像的全部补丁（生命周期清理补丁除外）
    public static bool ReplayOn = true;

    public static bool AutoSave = true;
    public static bool CompleteSave = true;
    public static bool DontSaveWhenAuto = true;
    public static bool DontSaveWhenMiss = true;
    public static bool LateSave = true;
    public static bool FailSave = false;
    public static bool Backup = true;
    public static bool ManualSave = true;
    public static bool CancelSave = true;
    public static bool LegacyEngine = true;
    public static bool KeybdSoundRecordActive = true;
    public static bool FollowGameLanguage = true;
    public static bool KeyConvertEnabled = false;
    public static bool LimitKey = false;
    public static bool GetDataFromAsyncInput = false;

    // UI 电源钮（Quartz 壳左下角总开关）：false = 暂时禁用全部功能补丁（总闸）
    public static bool UiPowerOn = true;

    // 设置面板几何持久化（Quartz Conf 的 Spectre 落盘通道；位置为 Spectre 增量）
    public static float UiPanelWidthFrac = 0f;
    public static float UiPanelHeightFrac = 0f;
    public static float UiPanelOpacity = 1f;
    public static float UiPanelPosX = 0f;
    public static float UiPanelPosY = 0f;

    // 界面主题色（Quartz accent；默认 = Quartz 原版橙红）
    public static float UiAccentR = 1.0f;
    public static float UiAccentG = 0.5995077f;
    public static float UiAccentB = 0.5995077f;

    // 面板飞入/飞出动画时长（秒；0 = 瞬时）
    public static float UiFlyDuration = 0.25f;

    public static int TextSize = 20;
    public static int SaveButton = 290;
    public static int CancelSaveButton = 291;
    public static int KeybdSoundVolume = 10;
    public static int MicrophoneOffset = 0;

    // uGUI 设置界面呼出热键（KeyCode；默认 F8=289，避开手动保存 F9/取消保存 F10）
    public static int UiHotkey = 289;

    public static string SavePath = "";
    public static string InputedKey = "";
    public static string CurrLanguage = "";
    public static string MicrophoneDeviceName = "";
    public static string KeyCodeConvert = "";

    public static bool AlwaysShowMaxUsedKeys = false;

    public static bool LegacyPauseCalculation = false;

    // ── Special keys filter ─────────────────────────────
    public static HashSet<string> AllowedKeyboardSpecialKeys = new();
    public static HashSet<string> AllowedAsyncSpecialKeys = new();

    // ── EffectRemover settings ──────────────────────────
    public static bool EffectRemoverOn = true;
    public static bool EffectRemoverEnableSave = false;

    // ── Enable Pro Events ────────────────────────────────
    public static bool EnableProEvents = false;
    public static bool EffectRemoverFilters = true;
    public static bool EffectRemoverAdvancedFilters = true;
    public static bool EffectRemoverParticles = true;
    public static bool EffectRemoverDecorations = true;
    public static bool EffectRemoverBackgrounds = true;
    public static bool EffectRemoverCameras = false;
    public static bool EffectRemoverRepeatEvents = false;
    public static bool EffectRemoverFrameRate = true;
    public static bool EffectRemoverHitSounds = true;
    public static bool EffectRemoverCheckpoints = false;
    public static bool EffectRemoverInputEvents = false;
    public static bool EffectRemoverPlanetOrbit = false;
    public static bool EffectRemoverPlanetScale = false;
    public static bool EffectRemoverPlanetRadius = false;
    public static bool EffectRemoverTrackAnimations = false;
    public static bool EffectRemoverTrackPositions = false;
    public static bool EffectRemoverTrackMoves = true;
    public static bool EffectRemoverTrackColors = true;
    public static bool EffectRemoverHoldSounds = false;
    public static bool EffectRemoverHideIcons = false;
    public static bool EffectRemoverRemoveAllDecorations = true;
    public static bool EffectRemoverResetTrackOpacity = false;
    public static bool EffectRemoverResetTrackAnimation = false;
    public static bool EffectRemoverResetTrackColor = true;
    public static bool EffectRemoverSetCameraZoom = false;
    public static float EffectRemoverCameraZoomScale = 250f;

    // ── Decoration Preview ──────────────────────────────
    public static bool DecorationPreviewOn = false;

    // ── Memory Optimizer（纹理 DXT 压缩，有损） ─────────
    public static bool MemoryOptimizerOn = false;

    // ── Editor Bugfix（编辑器独立 bug 修复） ────────────
    public static bool EditorBugfixOn = true;

    // ── BpmBeatCalculator（realBPM / durationAngle 联动字段） ──
    public static bool BpmBeatCalculatorOn = false;

    // ── SpeedPauseConverter（变速 ↔ 暂停 互转按钮） ──
    public static bool SpeedPauseConverterOn = false;

    // ── BetterUndoRedo（无操作状态过滤 + 重做队列修正） ──
    public static bool BetterUndoRedoOn = true;

    // ── BGAMode（BGA 录制：隐藏前景元素录纯背景） ──
    public static bool BGAModeOn = false;

    // ── EditorUI（编辑器 UI 增强：floor 属性注入 + 横向属性行） ──
    public static bool EditorUIOn = false;
    public static bool EditorUIFloorPanelOn = true;
    public static bool EditorUIHorizontalOn = false;

    // ── Timeline（编辑器时间轴条） ──
    // 此前这个字段只被写、被存盘，没有任何逻辑读它——UI 上拖了像生效了，实际毫无反应。
    // 现由 PacLibHost.Activate / SyncPACL2Features 真正驱动 TimelineFeature.Active。
    // 默认 true：接开关之前 Timeline 是无条件启用的，改默认值会让全新安装丢掉时间轴 UI。
    // 遗留问题：从旧版本升级的用户，其 Configs.json 里已存在一个从未生效的
    // "TimelineOn": false，读取后仍会得到"关"——那不是用户的选择，只是当初的默认值。
    // 判别需要一次性标记位，收益不足以引入，这里如实记录。
    public static bool TimelineOn = true;

    // ── FixChartLoad（谱面加载修复：编辑器增删砖块/事件、Scope 撤销粒度） ──
    // 同样此前没有开关，只能常开。
    //
    // 默认改为 false：这套补丁是**深度改写编辑器行为**的一组 transpiler + Scope，
    // 不是"修个 bug"级别的增强——用户反馈它在游戏内编辑器里造成难以归因的副作用
    // （典型：删除事件会连带删掉这块地上同类型的全部事件）。副作用型功能不该
    // 默认替用户做决定，要用的人自己开。
    //
    // 关闭后原版逻辑直接生效：编辑器增删砖块/事件回到游戏原生行为，代价是失去
    // Scope 的细粒度撤销追踪等增强。开关仍在 UI 上，随时可开。
    public static bool FixChartLoadOn = false;

    // ── CustomEvents（自定义事件：变量系统等，PACL2 谱面兼容） ──
    public static bool CustomEventsOn = false;

    // ── JudgeText settings ──────────────────────────────
    public static bool JudgeTextOn = false;
    public static readonly string[] JudgeTextKeys =
    {
        "TooEarly", "VeryEarly", "EarlyPerfect", "Perfect",
        "LatePerfect", "VeryLate", "TooLate", "Multipress",
        "FailMiss", "FailOverload", "Auto", "OverPress",
        // r150 起完美拆三档：XPerfect 在“显示 XPerfect”预设下单独飘字，
        // 追加独立自定义槽（追加在尾部，旧配置按索引加载不受影响；
        // PerfectMinus/PerfectPlus 无需槽——游戏默认回落到 Perfect 键）
        "XPerfect"
    };
    public static string[] JudgeTexts = new string[JudgeTextKeys.Length];

    // 显示效果：大小 / 淡出速度倍率
    public static bool JudgeFxOn = false;
    public static float JudgeTextSizeMult = 1f;
    public static float JudgeTextFadeMult = 1f;

    // 判定过滤：隐藏指定判定
    public static bool JudgeFilterOn = false;
    public static HashSet<string> JudgeHiddenMargins = new();

    // 判定颜色（前 9 = 游戏配色方案，第 10 = Auto）与发光
    public static bool JudgeColorOn = false;
    public static readonly string[] JudgeColorKeys =
    {
        "tooearly", "veryearly", "earlyperfect", "perfect",
        "lateperfect", "verylate", "toolate", "multipress",
        "failmiss", "auto"
    };
    // 必须在 JudgeMarginColorsOn 之前声明。C# 静态字段初始化器按**文本顺序**执行，
    // 放到文件末尾会让下面那行读到一个 null 数组，抛 NullReferenceException 并被包成
    // TypeInitializationException —— 整个 Settings 类都不可用（2026-10-11 首次实跑
    // Load() 就崩在这里，闸门结构上抓不到：Settings.cs 依赖 UnityEngine，没链进离屏套件）。
    // 原先这里引用的是另一个类 JudgeTextDefaults.DefaultColors（跨类无顺序约束），
    // 9feee37 删掉那个文件、改成同类字段后踩中。
    internal static readonly Color[] VanillaMarginColors = NewColorArray();
    public static Color[] JudgeMarginColors = NewColorArray();
    public static bool[] JudgeMarginColorsOn = new bool[VanillaMarginColors.Length];
    public static bool JudgeGlowOn = false;
    public static Color JudgeGlowColor = Color.black;

    // 飘字描边 / XPerfect 粒子（机制研究自游戏 xPerfectBorder / xPerfectParticle：
    // 原版 TMP Distance Field Overlay 着色器 + _OutlineTex 渐变纹理按字形高度取色）
    public static bool JudgeTextOutlineOn = false;
    public static float JudgeTextOutlineWidth = 0.405f;     // 游戏同款默认宽度
    public static Color JudgeTextOutlineColor = Color.white;
    public static bool JudgeTextOutlineGradientOn = false;  // 游戏式粉→蓝渐变替代纯色
    public static bool JudgeTextParticleOn = true;          // false = 强制关 XPerfect 星星粒子

    // ── JudgeBar settings ──────────────────────────────
    public static bool JudgeBarOn = false;
    // 动态窗口缩放：开 = 分区实时按难度/BPM/音调缩放（精确判定条行为）；关 = 固定原版 30/45/60 布局
    public static bool JudgeBarDynamicOn = true;
    public static bool JudgeBarUnlockStrict = false;
    public static float JudgeBarAnimationDuration = 0.4f;
    public static float JudgeBarSensitivity = 0.2f;
    public static float JudgeBarTickLife = 3f;
    // 判定条外沿装饰边（原版 ±60~63.2° 红边）：开关 + 颜色 + 外沿角，默认=原版
    public static bool JudgeBarEdgeColorOn = false;
    public static Color JudgeBarEdgeColor = new(1f, 0f, 0f, 1f);
    public static float JudgeBarEdgeSpan = 63.2f;

    // 判定 tick 描边（r150 ErrorMeterTick.tickOutline）：游戏只给 XPerfect + 特定预设
    // 配白色描边，这里解绑——开关开 = 所有 tick 带自定义色描边
    public static bool JudgeBarTickOutlineOn = false;
    public static Color JudgeBarTickOutlineColor = Color.white;
    // 底层游戏窗口三段配色（Pure/Perfect/Counted 区；档位未覆盖处露出的就是它们）
    public static bool JudgeBarZonePureOn = false;
    public static Color JudgeBarPureColor = new(0.37255f, 1f, 0.30588f, 1f);
    public static bool JudgeBarZonePerfectOn = false;
    public static Color JudgeBarPerfectColor = new(0.98824f, 1f, 0.30196f, 1f);
    public static bool JudgeBarZoneCountedOn = false;
    public static Color JudgeBarCountedColor = new(1f, 0.43529f, 0.30196f, 1f);

    // 原版默认配色（开关关闭与"恢复默认"共用的唯一事实源）
    internal static readonly Color JudgeBarVanillaEdge = new(1f, 0f, 0f, 1f);
    internal static readonly Color JudgeBarVanillaPure = new(0.37255f, 1f, 0.30588f, 1f);
    internal static readonly Color JudgeBarVanillaPerfect = new(0.98824f, 1f, 0.30196f, 1f);
    internal static readonly Color JudgeBarVanillaCounted = new(1f, 0.43529f, 0.30196f, 1f);
    public static bool OverloadBarOn = false;
    public static float OverloadBarX = 0f;
    public static float OverloadBarY = -500f;

    // 判定条整体位置偏移（AJB BarPositionX/Y 同款）：叠加到 wrapperRectTransform 的
    // anchoredPosition 上（正 = 右/上，0,0 = 原位）；游戏重排后由 Patch_MeterLayout 重锚基准
    public static float JudgeBarPositionX = 0f;
    public static float JudgeBarPositionY = 0f;

    // 失败判定带（AJB ShowFailureJudgement 同款，默认开）：noFail 局内出现失败命中后，
    // 判定条外沿长出紫色环 + 背景扩展 + 失败 tick 钉在环上；重开/回检查点消除
    public static bool JudgeBarShowFailureJudgement = true;

    // 紫色失败环常驻显示：不等你真的失误，环始终满幅显示（装饰/提醒用途）
    public static bool JudgeBarFailureBandAlwaysOn = false;

    // 判定条黑色背板透明度（AJB BackgroundOpacity 对应项；默认 0.4 ≈ 原版背板的观感，
    // AJB 的 0.6 偏深，想要 AJB 观感手动调高即可）
    public static float JudgeBarBackgroundOpacity = 0.4f;


    // ── ResultsPlus settings ────────────────────────────
    public static bool ResultsPlusOn = true;
    public static bool CalibAdviceOn = true;
    public static bool ResultsKeysOn = true;
    // 以下两个是 CalibrationAdvisor 的持久化载体（非用户设置），ConfigManager 落盘
    public static string CalibSamples = "";
    public static int CalibGames = 0;

    // ── PlanetColors settings ───────────────────────────
    public static bool PlanetColorOn = false;
    public static bool[] PlanetColorPlayerOn = InitPlanetOn();
    // **不能是 null。** ConfigManager.SaveConfigs 构造 ConfigData 时会把它交给
    // ToHexArray，而那段构造不在 try 内 —— 抛出的 NRE 会让整份设置都写不下去
    // （2026-10-11 实测：改任何设置都不落盘，日志只有一行"保存设置失败"）。
    // 上游此处是 (Color[])PlanetColorApplier.DefaultColors.Clone()；我们没移植
    // PlanetColors 特性（该目录不存在），故给一个定长 8（= 上游 PlanetCount）的
    // 中性数组即可 —— PlanetColorOn 默认 false 且无消费方，这组字段只是持久化载体。
    public static Color[] PlanetColorBase = new Color[8];
    public static Color[][] PlanetColorParts = InitPlanetParts();
    public static bool[][] PlanetColorPartsOn = InitPlanetPartsOn();

    // ── ReplayVideo settings ───────────────────────────
    /// <summary>
    /// 视频渲染总开关。
    ///
    /// **原先没有这个字段，设置页那行「启用视频渲染」绑的是 ReplayOn。**
    /// 后果有两层：一是关掉"视频渲染"会把**录制/回放**一起关掉（同一个字段
    /// 在录制页绑的是同一样东西），二是这一页根本没有能单独关渲染的开关——
    /// 玩家以为关的是渲染，实际关掉的是整条回放链。
    /// </summary>
    public static bool RenderOn = true;
    // ── 实验性：确定性渲染时钟 ────────────────────────────────────────
    // 打开 = 挂 scrConductor.Update 的时间源 Transpiler（Patch_RenderClockConductor），
    // 把 AudioSettings.dspTime / Time.unscaled* 四个调用换成帧号驱动的 RenderClock，
    // 于是游戏自己的节拍推进/相机事件/倒计时/hitsound 调度链**全部自动确定性化**。
    // 关（默认）= 补丁根本不挂载（Feature 契约：谓词为假就不应用），
    // 渲染走 audio-cursor 兜底时钟（±2.7ms 抖动，音画同步精度较低）。
    //
    // **为什么默认关**：它动的是游戏的时间源本身，影响面比"只拦输出"大得多——
    // 时间源一歪，节拍/相机/倒计时全都跟着歪。而且它与别的 mod 的同款
    // transpiler 天然竞争（Harmony 按注册顺序先到先得）。实验性功能默认关，
    // 由玩家自己打开并承担风险，是这类改动应有的默认。
    public static bool RenderClockOn = false;
    public static int RenderCaptureMode = 1;  // 0 纯净相机 1 游戏画面(含UI)
    public static int RenderWidth = 1920;
    public static int RenderHeight = 1080;
    public static int RenderFps = 60;
    public static int RenderEncoderMode = 0;   // 0 自动 1 最快 2 质量 3 纯 CPU
    public static int RenderQualityMode = 1;   // 0 低 1 标准 2 高 3 极致
    public static int RenderOutputFormat = 0;  // 输出容器：0 mp4 1 mkv 2 webm 3 mov（见 VideoEncoder.OutContainer）
    // 转换目标容器。**与 RenderOutputFormat 刻意解耦**（用户 2026-10-12 拍板方案 B）：
    // 两件事的目的不同——渲染格式是"我这局要录成什么"，转换格式是"把已有的一堆
    // 视频批量转成什么"。复用同一个值会导致"想渲染 mp4、顺手把旧视频转 mkv"
    // 做不到，只能来回改。
    //
    // 上游 Spectre 用的是**不持久化**的 `private static int _videoConvertTarget`
    // （SpectreUiPages.Features.cs:643）。我们改为持久化：本项目的既有约定是
    // "凡界面上能选的都落盘"（缺陷①「设置存不下来」就是这条约定被破坏），
    // 且玩家很可能分几次转同一批文件。
    public static int ConvertTarget = 0;       // 转换目标容器：0 mp4 1 mkv 2 webm 3 mov
    public static float RenderTailSeconds = 3f;
    public static string RenderOutputDir = ""; // 留空 = SavePath/RenderVideos

    // 键位重映射：list 为持久化 JSON 源（KeyCodeConvert），array 为运行时映射表
    internal static List<KeyValuePair<ushort, ushort>> key_code_convert_list = new List<KeyValuePair<ushort, ushort>>();
    internal static ushort[] key_code_convert = new ushort[256];

    // —— 数据初始化助手 ————————————————————————————

    internal static Color[] FillColor(Color c)
    {
        var arr = new Color[6];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = c;
        return arr;
    }

    private static bool[] InitPlanetOn()
    {
        var arr = new bool[2];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = true;
        return arr;
    }

    private static Color[][] InitPlanetParts() { return new Color[2][] { new Color[6], new Color[6] }; }

    private static bool[][] InitPlanetPartsOn() { return new bool[2][] { new bool[6], new bool[6] }; }

    // —— 启动归一化 / 键表构建 ————————————————————————

    internal static void key_convert_list2array()
    {
        key_code_convert = new ushort[256];
        for (int i = 0; i < 256; i++)
            key_code_convert[i] = 0xFFFF;
        if (!LimitKey || !KeyConvertEnabled)
        {
            for (ushort num = 0; num < 256; num++)
                key_code_convert[num] = num;
        }
        if (!KeyConvertEnabled)
            return;
        foreach (KeyValuePair<ushort, ushort> item in key_code_convert_list)
        {
            // 目标值同样必须落在键码域内：配置 JSON 手写可注入越界值，
            // 回放侧 CurrKeyState[映射值] 会直接越界。不合法条目保持 0xFFFF，
            // 由下方兜底循环回落为恒等映射（等价未重映射）
            if (item.Key < 256 && item.Value < 256)
                key_code_convert[item.Key] = item.Value;
        }
        for (ushort num = 0; num < 256; num++)
        {
            if (key_code_convert[num] == 0xFFFF)
                key_code_convert[num] = num;
        }
    }

    internal static void checklang()
    {
        if (FollowGameLanguage)
            LocalizationManager.GetLanguages();
        else if (LocalizationManager.currentLanguage != CurrLanguage)
            LocalizationManager.SwitchLanguage(CurrLanguage);
    }

    // 启动时兜底：保存路径/语言/麦克风/键表缺省值补齐与解析
    internal static void check(string path = "")
    {
        SavePath = string.IsNullOrEmpty(SavePath) ? path : SavePath;
        CurrLanguage = string.IsNullOrEmpty(CurrLanguage) ? LocalizationManager.GetLangCode(RDString.language) : CurrLanguage;
        if (string.IsNullOrEmpty(MicrophoneDeviceName))
            MicrophoneDeviceName = Microphone.devices.Length != 0 ? Microphone.devices[0] : "None";
        if (Microphone.devices.Length == 0)
            KeybdSoundRecordActive = false;
        if (string.IsNullOrEmpty(KeyCodeConvert))
        {
            key_code_convert_list = new List<KeyValuePair<ushort, ushort>>();
            KeyCodeConvert = JsonConvert.SerializeObject(key_code_convert_list, Formatting.Indented);
        }
        else
        {
            try
            {
                key_code_convert_list = JsonConvert.DeserializeObject<List<KeyValuePair<ushort, ushort>>>(KeyCodeConvert);
            }
            catch
            {
                key_code_convert_list = new List<KeyValuePair<ushort, ushort>>();
                KeyCodeConvert = JsonConvert.SerializeObject(key_code_convert_list, Formatting.Indented);
            }
            key_code_convert_list ??= new List<KeyValuePair<ushort, ushort>>();
        }
        checklang();
        key_convert_list2array();
        for (ushort num = 0; num < 256; num++)
        {
            if (key_code_convert[num] == 0xFFFF)
                key_code_convert[num] = num;
        }
        SpectreState.save_button = (KeyCode)SaveButton;
        SpectreState.cancel_save_button = (KeyCode)CancelSaveButton;
    }

    internal static Color[] NewColorArray()
    {
        // 原版默认判定颜色（红橙黄绿青蓝紫白粉）
        return new Color[10]
        {
            new Color(1f, 0.2f, 0.2f), new Color(1f, 0.6f, 0.2f), new Color(1f, 0.9f, 0.2f),
            new Color(0.3f, 1f, 0.3f), new Color(0.2f, 0.8f, 1f), new Color(0.3f, 0.4f, 1f),
            new Color(0.8f, 0.3f, 1f), new Color(1f, 0.4f, 0.8f), new Color(1f, 1f, 1f),
            new Color(0.9f, 0.9f, 0.9f)
        };
    }

    /// <summary>把当前设置立即落盘并刷新补丁开关（移植后的通用提交入口）。</summary>
    internal static void CommitSafe()
    {
        try
        {
            ConfigManager.SaveConfigs(System.IO.Path.Combine(Main.mod.Path, "Configs.json"));
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogWarning("[SpectreReplay] 设置保存失败：" + ex.Message);
        }
    }

}
