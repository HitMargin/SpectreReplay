using System;
using System.Collections.Generic;
using SpectreReplay.Features.Compat;
using SpectreReplay.Features.CustomJudge;
using SpectreReplay.Features.Replay;
using UnityEngine;

namespace SpectreReplay.API;

// 对外 API 门面：其他 mod 直接引用 Spectre.dll 调用，或按稳定命名反射软依赖。
// 契约：
//   - 事件一律主线程触发、逐订阅者异常隔离——第三方回调炸了不伤 Spectre 管道；
//   - 数据只出只读快照，绝不外借内部可变结构；
//   - 标注「任意线程」的成员可从后台线程调用，其余请在 Unity 主线程调。
public static class SpectreAPI
{
    /// <summary>API 版本；软依赖 mod 反射探测能力用。新增成员 = 次版本号 +1。</summary>
    public const string ApiVersion = "0.4";

    // ── 生命周期事件 ─────────────────────────────────────────────

    /// <summary>开始录制（仅状态真正翻转时触发）。</summary>
    public static event Action RecordingStarted;
    /// <summary>录制结束。</summary>
    public static event Action RecordingStopped;
    /// <summary>开始回放（仅状态真正翻转时触发）。</summary>
    public static event Action PlaybackStarted;
    /// <summary>回放结束（播完/手动停止/被 BlockPlaying 打断均会触发）。</summary>
    public static event Action PlaybackStopped;
    /// <summary>回放文件成功落盘；参数为最终完整路径（保存失败自动改名重试后以重试路径为准）。</summary>
    public static event Action<string> ReplaySaved;

    internal static void RaiseRecordingStarted() => Raise(RecordingStarted);
    internal static void RaiseRecordingStopped() => Raise(RecordingStopped);
    internal static void RaisePlaybackStarted() => Raise(PlaybackStarted);
    internal static void RaisePlaybackStopped() => Raise(PlaybackStopped);
    internal static void RaiseReplaySaved(string path) => Raise(ReplaySaved, path);

    private static void Raise(Action evt)
    {
        if (evt == null) return;
        RunOnMain(() =>
        {
            foreach (Action d in evt.GetInvocationList())
            {
                try { d(); }
                catch (Exception ex) { LogSubscriberError(nameof(Raise), d, ex); }
            }
        });
    }

    private static void Raise<T>(Action<T> evt, T arg)
    {
        if (evt == null) return;
        RunOnMain(() =>
        {
            foreach (Action<T> d in evt.GetInvocationList())
            {
                try { d(arg); }
                catch (Exception ex) { LogSubscriberError(nameof(Raise), d, ex); }
            }
        });
    }

    /// <summary>
    /// 报告订阅者异常。**必须带出订阅者的身份**（声明类型 + 方法名）：本项目的订阅者
    /// 多为外部 mod，只说"事件订阅者异常"对作者毫无用处——他既不知道是哪个事件、
    /// 也不知道是自己的哪个回调，只能回去逐个断点。
    /// </summary>
    private static void LogSubscriberError(string raiseSite, Delegate d, Exception ex)
    {
        string who = "?";
        try
        {
            System.Reflection.MethodBase m = d?.Method;
            if (m != null)
            {
                who = (m.DeclaringType != null ? m.DeclaringType.FullName : "?") + "." + m.Name;
            }
        }
        catch (System.Exception)
        {
            // 取方法信息本身失败（罕见的动态代理）也不能让报告路径再抛
        }
        Debug.LogWarning("[SpectreAPI] 事件订阅者异常（已隔离，其余订阅者照常收到）"
            + "  订阅者: " + who + "  经由: " + raiseSite
            + "  → " + ex.GetType().Name + ": " + ex.Message);
    }

    // 主线程封送：泵未装（mod 未开启早期）则就地执行
    private static void RunOnMain(Action body)
    {
        if (MainThread.IsMainThread) InvokeSafe(body);
        else MainThread.Run(() => InvokeSafe(body));
    }

    private static void InvokeSafe(Action body)
    {
        try { body(); }
        catch (Exception ex) { Debug.LogException(ex); }
    }

    // ── 玩法阻断协商 ─────────────────────────────────────────────
    // 引用计数式：多 mod 各自登记，全部解除才恢复游玩。

    private static readonly object _blockLock = new object();
    private static readonly Dictionary<string, string> _blockSources = new Dictionary<string, string>();

    /// <summary>当前是否存在任何玩法阻断（任意线程可读）。</summary>
    public static bool IsPlayingBlocked
    {
        get { lock (_blockLock) return _blockSources.Count != 0; }
    }

    /// <summary>
    /// 阻断开始游玩：立即生效并停掉进行中的回放。<br/>
    /// 同一 source 重复调用覆盖消息；用对应的 AllowPlaying(source) 解除。任意线程可调。
    /// </summary>
    public static void BlockPlaying(string source, string msg)
    {
        lock (_blockLock)
        {
            _blockSources[source ?? ""] = msg ?? "";
            SpectreState.ApiBlockPlaying = true;
        }
        // 停回放要动游戏状态机，转回主线程；未开启回放时为无害空操作
        MainThread.Run(SpectreState.PlayActions.StopPlaying);
    }

    /// <summary>阻断开始游玩（匿名来源）。</summary>
    public static void BlockPlaying(string msg) => BlockPlaying("", msg);

    /// <summary>解除指定来源的阻断；全部解除后自动恢复可游玩。任意线程可调。</summary>
    public static void AllowPlaying(string source)
    {
        lock (_blockLock)
        {
            _blockSources.Remove(source ?? "");
            if (_blockSources.Count == 0)
                SpectreState.ApiBlockPlaying = false;
        }
    }

    /// <summary>解除匿名来源的阻断。</summary>
    public static void AllowPlaying() => AllowPlaying("");

    /// <summary>当前阻断快照（source → 说明），供内部展示与诊断。</summary>
    internal static List<KeyValuePair<string, string>> SnapshotBlocks()
    {
        lock (_blockLock)
            return new List<KeyValuePair<string, string>>(_blockSources);
    }

    // ── 自定义判定读取 ───────────────────────────────────────────

    /// <summary>命中符号：早 / 自动 / 晚 / 中旋。</summary>
    public enum HitSign { Early = -1, Auto = 0, Late = 1, Midspin = 2 }

    /// <summary>单个判定档的计数快照。</summary>
    public readonly struct TierCount
    {
        public readonly int Early;
        public readonly int Late;
        public readonly int Auto;
        public readonly int Midspin;

        public TierCount(int early, int late, int auto, int midspin)
        {
            Early = early; Late = late; Auto = auto; Midspin = midspin;
        }

        /// <summary>四类合计（加权 xacc 的分子口径与此一致）。</summary>
        public int Total => Early + Late + Auto + Midspin;
    }

    /// <summary>自定义判定功能是否开启（关闭时下列计数接口返回空数据）。</summary>
    public static bool IsCustomJudgeActive => Settings.CustomJudgeOn;

    /// <summary>当前档位数（随档案动态变化）。</summary>
    public static int GetTierCount() => Settings.CustomTierOn.Count;

    /// <summary>
    /// 单档计数快照。tier 越界或功能关闭返回全零。<br/>
    /// 注意：检查点回滚、重开都会使数值回退，属正常口径。
    /// </summary>
    public static TierCount GetTierCounts(int tier)
    {
        if (!Settings.CustomJudgeOn || tier < 0 || tier >= Settings.CustomTierOn.Count)
            return default;
        return new TierCount(TierJudgeState.EarlyCount(tier), TierJudgeState.LateCount(tier),
            TierJudgeState.AutoCount(tier), TierJudgeState.MidspinCount(tier));
    }

    /// <summary>全部档位的计数快照（下标即档位）。</summary>
    public static TierCount[] GetAllTierCounts()
    {
        int n = GetTierCount();
        var arr = new TierCount[n];
        for (int i = 0; i < n; i++) arr[i] = GetTierCounts(i);
        return arr;
    }

    /// <summary>最近一次落账命中的档位索引；-1 = 尚无命中或越档命中。</summary>
    public static int GetLastHitTier() => TierJudgeState.CommittedTier;

    /// <summary>最近一次落账命中的符号；配合 GetLastHitTier 使用。</summary>
    public static HitSign GetLastHitSign()
        => TierJudgeState.CommittedKind switch
        {
            -1 => HitSign.Early,
            1 => HitSign.Late,
            2 => HitSign.Midspin,
            _ => HitSign.Auto,
        };

    /// <summary>结算口径准度：自定义权重开启时走自定义公式，否则原版四段权重。</summary>
    public static double GetXacc() => SpectreState.ComputePercentXacc();

    /// <summary>强制原版口径的准度（文件名所用口径，跨判定方案可比）。</summary>
    public static double GetVanillaXacc() => SpectreState.ComputePercentXaccVanilla();

    /// <summary>
    /// 判定档位定义快照——Configs.json 里 CustomJudge 节的内存镜像（加载后以 Settings 为准），
    /// 与设置页所见一致。数组下标即档位索引。
    /// </summary>
    public static TierDefinition[] GetTierDefinitions()
    {
        int n = Settings.CustomTierOn.Count;
        var arr = new TierDefinition[n];
        for (int i = 0; i < n; i++)
        {
            arr[i] = new TierDefinition(
                i,
                Settings.CustomTierOn[i],
                At(Settings.CustomTierInner, i, 0f),
                At(Settings.CustomTierOuter, i, 0f),
                At(Settings.CustomTierMs, i, 0f),
                AtStr(Settings.CustomTierName, i),
                AtStr(Settings.CustomTierPrefix, i),
                AtCol(Settings.CustomTierColor, i),
                At(Settings.CustomTierWeight, i, 1f),
                AtBool(Settings.CustomTierSplit, i),
                AtBool(Settings.CustomTierHide, i));
        }
        return arr;
    }

    private static T At<T>(List<T> list, int i, T def)
        => list != null && (uint)i < list.Count ? list[i] : def;

    private static string AtStr(List<string> list, int i)
        => At(list, i, "") ?? "";

    private static Color AtCol(List<Color> list, int i)
        => At(list, i, Color.black);

    private static bool AtBool(List<bool> list, int i) => At(list, i, false);

    /// <summary>
    /// 档位在判定条上的当前实时区间（条坐标 0..60°，已含级联铺排与毫秒换算）。<br/>
    /// 随 BPM×速度、音调、难度窗口动态变化；零宽退化档 Outer==Inner。<br/>
    /// 不在关卡内 / 自定义判定关闭 / 游戏对象缺失时返回 null。
    /// </summary>
    public static TierZone[] GetLiveTierLayout()
    {
        if (!Settings.CustomJudgeOn) return null;
        scrController controller = scrController.instance;
        scrConductor conductor = scrConductor.instance;
        if (controller == null || conductor == null) return null;
        try
        {
            // 与准度条 tick 配色同源的上下文推导：速度取前一地板，mm 取目标块
            var floor = controller.playerOne?.currFloor;
            double bpmTimesSpeed = (double)conductor.bpm * (floor?.prevfloor?.speed ?? 1f);
            double pitch = conductor.song != null ? conductor.song.pitch : 1.0;
            float marginScale = (float)(floor?.nextfloor != null ? floor.nextfloor.marginScale
                              : floor?.marginScale ?? 1.0);
            // 逐版本取窗口：旧版直调 scrMisc（HitMarginGeneral 参数），r150 起签名
            // 变成 Difficulty→结构体返回，由 GameCompat 按反射自动择路
            double countedRaw = GameCompat.Boundary(
                HitMarginGeneral.Counted, bpmTimesSpeed, pitch, marginScale);
            int n = CustomJudge.Count;
            var arr = new TierZone[n];
            for (int i = 0; i < n; i++)
            {
                var (inner, outer) = CustomJudge.TierRangeBar(i, bpmTimesSpeed, pitch, marginScale, countedRaw);
                arr[i] = new TierZone(inner, outer);
            }
            return arr;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreAPI] 解析档位实时布局失败: " + ex);
            return null;
        }
    }

    // ── 回放视频渲染 ─────────────────────────────────────────────
    // 渲染使用「回放视频」页签的当前设置（分辨率/帧率/编码器等）；
    // 需要先加载回放并处于关卡或编辑器内，前置校验失败时 TryStartRender 返回 false。

    /// <summary>渲染结束状态。</summary>
    public enum RenderEndStatus { Completed, Failed, Canceled }

    /// <summary>渲染作业的进度快照。</summary>
    public sealed class RenderStatus
    {
        public bool Active { get; internal set; }
        /// <summary>当前阶段：Idle/Restarting/WaitingPlayback/Capturing/Finalizing。</summary>
        public string Phase { get; internal set; }
        /// <summary>本地化的阶段名（可直接显示）。</summary>
        public string Stage { get; internal set; }
        public string Detail { get; internal set; }
        /// <summary>目标输出路径（Finalizing 前即已知；成品以 RenderFinished 事件为准）。</summary>
        public string OutputPath { get; internal set; }
        public int FramesDone { get; internal set; }
        public int FramesTotal { get; internal set; }
        /// <summary>0..1；捕获未开始时为 0。</summary>
        public float Progress01 { get; internal set; }
        /// <summary>捕获阶段预计剩余秒数；暂不可估为负。</summary>
        public double EtaSeconds { get; internal set; }
        /// <summary>最近一次失败的错误信息（历史值，渲染中为空）。</summary>
        public string LastError { get; internal set; }
    }

    /// <summary>渲染作业的终态报告（RenderFinished 事件参数）。</summary>
    public sealed class RenderResult
    {
        public RenderEndStatus Status { get; internal set; }
        /// <summary>Status == Completed 时为成品完整路径，否则 null。</summary>
        public string OutputPath { get; internal set; }
        /// <summary>Status == Failed 时为错误描述，否则 null。取消时也可能带说明。</summary>
        public string Error { get; internal set; }

        internal RenderResult(RenderEndStatus status, string outputPath, string error)
        {
            Status = status; OutputPath = outputPath; Error = error;
        }
    }

    /// <summary>渲染作业启动（前置校验通过后）。主线程触发。</summary>
    public static event Action RenderStarted;
    /// <summary>渲染作业到达终态：完成 / 失败 / 取消，每个作业恰好一次。主线程触发。</summary>
    public static event Action<RenderResult> RenderFinished;

    internal static void RaiseRenderStarted() => Raise(RenderStarted);

    internal static void RaiseRenderFinished(RenderEndStatus status, string outputPath, string error)
        => Raise(RenderFinished, new RenderResult(status, outputPath, error));

    private static readonly string[] PhaseNames =
        { "Idle", "Restarting", "WaitingPlayback", "Capturing", "Finalizing" };

    /// <summary>
    /// 当前是否有渲染作业在跑。
    /// </summary>
    public static bool IsRendering => Features.VideoRender.VideoRender.Active;

    /// <summary>
    /// 渲染进度快照（随时可读；建议主线程）。Active=false 时仍可读到上次失败的 LastError。
    /// </summary>
    public static RenderStatus GetRenderStatus()
    {
        var s = Features.VideoRender.VideoRender.Current;
        return new RenderStatus
        {
            Active = Features.VideoRender.VideoRender.Active,
            Phase = PhaseNames[(int)Features.VideoRender.VideoRender.CurrentPhase],
            Stage = s.Stage ?? "",
            Detail = s.Detail ?? "",
            OutputPath = s.Output ?? "",
            FramesDone = s.FramesDone,
            FramesTotal = s.FramesTotal,
            Progress01 = s.Progress,
            EtaSeconds = s.EtaSeconds,
            LastError = s.Error ?? "",
        };
    }

    /// <summary>
    /// 尝试开始渲染当前加载的回放（使用「回放视频」页签的当前设置）。<br/>
    /// 需要已加载回放且处于关卡或编辑器内、ffmpeg 可用；不满足时弹游戏内警告并返回 false。<br/>
    /// 已有作业在跑时返回 false。仅主线程。
    /// </summary>
    public static bool TryStartRender()
    {
        Features.VideoRender.VideoRender.StartRender();
        return Features.VideoRender.VideoRender.Active;
    }

    /// <summary>请求取消当前渲染（异步生效，终态经 RenderFinished 报告）。仅主线程。</summary>
    public static void CancelRender() => Features.VideoRender.VideoRender.CancelRender();

    // ── 配置与其他 ───────────────────────────────────────────────

    /// <summary>
    /// 立即把全部设置落盘（等价于关一次 mod 设置窗口）。
    /// <para>
    /// 返回是否真的写入了磁盘。改动前的签名返回 void，外部 mod 直接忽略返回值仍可编译；
    /// 但"调用了保存"和"保存成功"是两回事（文件被占用/权限不足/磁盘满都会失败），
    /// 需要确认结果的调用方现在可以直接用返回值。
    /// </para>
    /// </summary>
    public static bool SaveSettings() => ConfigManager.SaveConfigs(ConfigManager.configPath);

    /// <summary>
    /// 功能补丁是否全部解析就绪（目标方法存在、版本门控通过）。<br/>
    /// feature 取 FeatureKeys 常量字符串，如 "CustomJudge"、"Replay"、"PlanetColor"。
    /// </summary>
    public static bool IsFeatureAvailable(string feature) => SpectrePatchHub.IsAvailable(feature);

    /// <summary>当前是否加载了回放数据。</summary>
    public static bool HasLoadedReplay => SpectreState.data != null;

    /// <summary>已加载回放的只读信息快照；无加载数据时返回 null。</summary>
    public static ReplayInfo GetLoadedReplayInfo()
    {
        var d = SpectreState.data;
        if (d == null) return null;
        return new ReplayInfo(
            GetStr(d, ReplayKeys.SongName),
            GetStr(d, ReplayKeys.ArtistName),
            GetStr(d, ReplayKeys.JudgeMode),
            GetStr(d, ReplayKeys.InternalLevelName),
            GetStr(d, ReplayKeys.ModVersion),
            d.ints.TryGetValue(ReplayKeys.StartTile, out int st) ? st : -1,
            d.ints.TryGetValue(ReplayKeys.EndTile, out int et) ? et : -1,
            d.doubles.TryGetValue(ReplayKeys.Bpm, out double bpm) ? bpm : 0,
            d.doubles.TryGetValue(ReplayKeys.PlaybackSpeed, out double spd) ? spd : 1,
            d.HitContext_list?.Count ?? 0,
            d.KeyEvent_list?.Count ?? 0);
    }

    private static string GetStr(LegacyReplayData d, string key)
        => d.strings.TryGetValue(key, out string v) ? v : "";
}

/// <summary>已加载回放的只读信息快照（SpectreAPI.GetLoadedReplayInfo 返回值）。</summary>
public sealed class ReplayInfo
{
    public string SongName { get; }
    public string ArtistName { get; }
    public string JudgeMode { get; }
    public string InternalLevelName { get; }
    /// <summary>录制时的 Spectre 版本。</summary>
    public string ModVersion { get; }
    public int StartTile { get; }
    public int EndTile { get; }
    public double Bpm { get; }
    public double PlaybackSpeed { get; }
    /// <summary>命中上下文条数。</summary>
    public int HitCount { get; }
    /// <summary>键盘事件条数。</summary>
    public int KeyEventCount { get; }

    internal ReplayInfo(string songName, string artistName, string judgeMode, string internalLevelName,
        string modVersion, int startTile, int endTile, double bpm, double playbackSpeed,
        int hitCount, int keyEventCount)
    {
        SongName = songName; ArtistName = artistName; JudgeMode = judgeMode;
        InternalLevelName = internalLevelName; ModVersion = modVersion;
        StartTile = startTile; EndTile = endTile;
        Bpm = bpm; PlaybackSpeed = playbackSpeed;
        HitCount = hitCount; KeyEventCount = keyEventCount;
    }
}

/// <summary>判定档位定义（Configs.json CustomJudge 节的只读镜像，SpectreAPI.GetTierDefinitions 返回值）。</summary>
public sealed class TierDefinition
{
    /// <summary>档位索引。</summary>
    public int Index { get; }
    /// <summary>档位是否启用。</summary>
    public bool Enabled { get; }
    /// <summary>
    /// 写定内沿角（判定条坐标 0..60°）。-1 = 接上一档结束角。
    /// </summary>
    public float InnerDeg { get; }
    /// <summary>
    /// 写定外沿角。-1/-2/-3 = 跟随游戏 Pure/Perfect/Counted 窗口实时边界；
    /// ≥60 = 触边钉死；配合 <see cref="MsFloor"/> 取二者较大值。
    /// </summary>
    public float OuterDeg { get; }
    /// <summary>毫秒下限（真实时间换算为窗口角，随 BPM×速度移动）；0 = 不设。</summary>
    public float MsFloor { get; }
    /// <summary>显示名（空串 = 回退原版飘字文本）。</summary>
    public string Name { get; }
    /// <summary>飘字前缀（如 "+"、"X"）。</summary>
    public string Prefix { get; }
    /// <summary>档位颜色（飘字/tick/结算）。</summary>
    public Color Color { get; }
    /// <summary>加权 xacc 的权重。</summary>
    public float Weight { get; }
    /// <summary>结算屏是否按早/迟拆分显示。</summary>
    public bool SplitDisplay { get; }
    /// <summary>是否隐藏该档飘字。</summary>
    public bool Hidden { get; }

    internal TierDefinition(int index, bool enabled, float innerDeg, float outerDeg, float msFloor,
        string name, string prefix, Color color, float weight, bool splitDisplay, bool hidden)
    {
        Index = index; Enabled = enabled;
        InnerDeg = innerDeg; OuterDeg = outerDeg; MsFloor = msFloor;
        Name = name ?? ""; Prefix = prefix ?? ""; Color = color;
        Weight = weight; SplitDisplay = splitDisplay; Hidden = hidden;
    }
}

/// <summary>档位在判定条坐标上的实时区间（SpectreAPI.GetLiveTierLayout 返回值）。单位：度（0..60）。</summary>
public readonly struct TierZone
{
    public readonly float InnerDeg;
    public readonly float OuterDeg;

    internal TierZone(float innerDeg, float outerDeg)
    {
        InnerDeg = innerDeg;
        OuterDeg = outerDeg;
    }
}
