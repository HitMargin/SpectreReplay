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

    // ── Steam 云同步（回放自动备份到 Steam 云端）─────────────
    // 默认开：这正是玩家要的"不担心本地文件丢失"。关掉它只是少一步云写入，
    // 本地保存路径完全不受影响。
    public static bool CloudSyncOn = true;
    // 启动时把云端有、本地没有的回放写回本地（换机器/重装后自动找回）。
    // 只回填缺失文件，从不覆盖本地已有文件。
    public static bool CloudRestoreOnLoad = true;

    public static bool ManualSave = true;
    public static bool CancelSave = true;
    public static bool KeybdSoundRecordActive = true;

    public static bool KeyConvertEnabled = false;
    /// <summary>
    /// **只模拟映射列表中的按键。**
    ///
    /// 开启（且 <see cref="KeyConvertEnabled"/> 也开）时，映射表里没有的键
    /// 一律不产生输入——只用手柄/宏键盘的几个键打谱时，屏蔽掉其余按键。
    /// 关掉时表外的键照常按原样使用。
    ///
    /// 默认 <c>false</c>：它会让大量按键**静默失效**，必须是玩家自己选的。
    /// 与 <see cref="KeyConvertEnabled"/> 一样，改动后要调
    /// <see cref="BuildKeyCodeConvertTable"/> 重建表，否则当场不生效。
    /// </summary>
    public static bool LimitKey = false;
    public static bool GetDataFromAsyncInput = false;

    /// <summary>
    /// 游戏底部提示条（Notification 弹窗）的**基础停留秒数**。
    ///
    /// <para>本模组所有提示都从这里派生：普通提示 = 该值，需要多留一会儿的提示
    /// = 该值 + 调用点给的差量（见 <see cref="NotificationDuration"/>）。默认 3 秒——
    /// 正是改造前硬编码的那个基数，所以调这个值不会让既有提示的观感突变，
    /// 只是把"多久收起"交给玩家。</para>
    ///
    /// <para>改它的入口在「关于」页（那里是纯界面偏好的归处，不掺录制/回放行为）。</para>
    /// </summary>
    public static float NotificationSeconds = 3f;

    /// <summary>
    /// 提示条时长解析：基础值 + <paramref name="extra"/> 秒。
    ///
    /// 用"基础 + 差量"而不是给每个调用点留一个绝对秒数：玩家把基础调成 6 秒时，
    /// 那句要读两行的错误提示应当跟着变成 8 秒，而不是原地停在 5 秒——
    /// 否则"我明明调大了，怎么这条还是很快就没了"会变成一个查不出来的坑。
    /// </summary>
    internal static float NotificationDuration(float extra = 0f)
        => Mathf.Clamp(NotificationSeconds + extra, 0.5f, 30f);

    public static int SaveButton = 290;
    public static int CancelSaveButton = 291;
    public static int KeybdSoundVolume = 10;
    public static int MicrophoneOffset = 0;

    /// <summary>
    /// 键盘音嵌入回放时是否用「极限」档（8 kHz / 约 4 KB/s）。
    ///
    /// <para>开：负载体积减半，音质略降（实测 SNR 18.4 dB，扫频段 19.7 dB）；
    /// 关：默认「均衡」档（16 kHz / 约 8 KB/s，SNR 19.7 dB）。</para>
    ///
    /// <para><b>默认 false。</b>体积差在真实录音上只有约 13%（键盘音大部分是静音，
    /// 静音块本来就只占 4 字节），而音质的损失是全局的——默认给均衡档更合适，
    /// 特别在意回放体积的玩家自己切。</para>
    /// </summary>
    public static bool KeybdSoundCompact = false;

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
    // **默认关**（上游是 true，这里有意偏离）。它是唯一会改到**关卡内容本身**
    // 的功能：打开后进关看到的谱面已经不是原谱，而玩家是来录制/回放的，
    // 不是来改谱的。默认开会让「我只是想录一局」的人先撞上一次莫名其妙的
    // 谱面变形，且他不会想到是新装的模组干的。要用的人自己在「去特效」页开。
    public static bool EffectRemoverOn = false;
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

    public static bool TimelineOn = true;

    public static bool FixChartLoadOn = false;

    // ── CustomEvents（自定义事件：变量系统等，PACL2 谱面兼容） ──
    public static bool CustomEventsOn = false;

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
    public static int ConvertTarget = 0;       // 转换目标容器：0 mp4 1 mkv 2 webm 3 mov
    public static float RenderTailSeconds = 3f;
    public static bool RenderCursorOn = false;
    // 是否允许在渲染途中按一个快捷键**提前结束**，把「从开始渲染到按键那一刻」
    // 的画面导出——也就是渲染不再受回放文件长度限制。
    //
    // **默认关**（理由与 RenderCursorOn 同一类）：它会改变成片的长度。开着时
    // 一按那个键，视频就在那里结束；默认语义仍应是「整份回放录完」，要提前
    // 结束得玩家自己打开并指定按键——否则一句「这视频怎么短了」无从解释。
    public static bool RenderStopOn = false;
    // 提前结束键（KeyCode 数值，与 SaveButton / CancelSaveButton 同一域）。
    // 290 / 291 已被录制页的「保存 / 取消保存」占用，这里取 292（F11）：避开
    // 它们，也避开游戏常用的字母区与 Esc（Esc 是各处的取消键）。
    public static int RenderStopKey = 292;
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

    // —— 启动归一化 / 键表构建 ————————————————————————

    /// <summary>
    /// 把 <see cref="key_code_convert_list"/> 这张「码位 → 目标键」的对照表展开成
    /// 256 项查找表 <see cref="key_code_convert"/>，供录制/回放每帧按码位索引。
    ///
    /// 三种末态：
    ///   · 功能关闭 → 恒等表（num → num），等于不改写任何键；
    ///   · 功能开启 → 表内条目取映射值，表外按 <see cref="LimitKey"/> 决定
    ///     「不模拟(0)」还是恒等；
    ///   · 中间态 0xFFFF 只是「尚未赋值」的哨兵，返回前保证被消干净（见方法末尾）。
    ///
    /// 名字用 PascalCase 并直述行为。原名 <c>BuildKeyCodeConvertTable</c> 是上游
    /// Spectre 的 snake_case 写法，改名属纯标识符重构（wire 与行为未动）；
    /// port-diff 的 $renameFold 已登记折回，移植比对不受影响。
    /// </summary>
    internal static void BuildKeyCodeConvertTable()
    {
        key_code_convert = new ushort[256];

        if (!KeyConvertEnabled)
        {
            for (ushort num = 0; num < 256; num++)
                key_code_convert[num] = num;
            return;
        }

        // 先全部标成「尚未赋值」。注意这里**不能**直接填 0：0 是「不模拟」。
        for (int i = 0; i < 256; i++)
            key_code_convert[i] = 0xFFFF;

        foreach (KeyValuePair<ushort, ushort> item in key_code_convert_list)
        {
            // 两端都必须落在键码域内：配置 JSON 手写可注入越界值，
            // 回放侧会拿映射结果去索引（CurrKeyState / 键表），越界直接抛。
            // 不合法条目保持 0xFFFF，交给下面的兜底决定。
            if (item.Key < 256 && item.Value < 256)
                key_code_convert[item.Key] = item.Value;
        }

        for (ushort num = 0; num < 256; num++)
        {
            if (key_code_convert[num] != 0xFFFF) continue;
            // 表外：限制模式下一律不模拟，否则恒等。
            // 0 与 27(Esc) 在回放侧本来就跳过，所以「不模拟」用 0 表达最省事，
            // 且不会额外吞掉一颗本来就不该被模拟的键。
            key_code_convert[num] = LimitKey ? (ushort)0 : num;
        }
    }

    internal static void checklang()
    {
        LocalizationManager.GetLanguages();
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
        // 提示条停留时长：手改配置文件可能写进 0 或 999。夹到有意义区间——
        // 0 秒等于「弹出来看不见」，999 秒则永不消失，两种都像模组坏了。
        NotificationSeconds = Mathf.Clamp(NotificationSeconds, 1f, 10f);
        checklang();
        BuildKeyCodeConvertTable();
        // 这里不能有 `== 0xFFFF → num` 的兜底：BuildKeyCodeConvertTable 已保证末态
        // 没有 0xFFFF（限制模式填 0、否则填恒等），多一道会把「0 = 不模拟」
        // 整个盖成恒等映射。
        SpectreState.save_button = (KeyCode)SaveButton;
        SpectreState.cancel_save_button = (KeyCode)CancelSaveButton;
    }

    /// <summary>把当前设置立即落盘并刷新补丁开关（通用提交入口）。</summary>
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
