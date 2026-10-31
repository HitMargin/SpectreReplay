using System;
using System.Collections.Generic;
using SpectreReplay.Features.Compat;
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
    public const string ApiVersion = "0.5";

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

    /// <summary>结算口径准度（原版四段权重，与文件名所用口径一致）。</summary>
    public static double GetXacc() => SpectreState.ComputePercentXacc();

    /// <summary>强制原版口径的准度（文件名所用口径，跨判定方案可比）。</summary>
    public static double GetVanillaXacc() => SpectreState.ComputePercentXaccVanilla();


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
        Enum.GetNames(typeof(Features.VideoRender.VideoRender.Phase));

    // 枚举值超出上表时回落 "Idle" 并留一条日志（比让查询接口抛异常友好）；
    // 负数同样走这里。只在真的失配时打日志，正常路径零噪声
    private static string PhaseOf(Features.VideoRender.VideoRender.Phase phase)
    {
        int index = (int)phase;
        if (index >= 0 && index < PhaseNames.Length)
            return PhaseNames[index];
        Debug.LogWarning("[SpectreReplay] GetRenderStatus: unknown render phase " + index
            + " (" + phase + "), PhaseNames has " + PhaseNames.Length + " entries");
        // 回落固定"Idle"而不是 PhaseNames[0]——后者现在语义是"序号最小的枚举名"，
        // 若将来给 Phase 加了个负值成员就不再是 Idle 了
        return "Idle";
    }

    /// <summary>
    /// 当前是否有渲染作业在跑。
    /// </summary>
    public static bool IsRendering => Features.VideoRender.VideoRender.Active;

    // ── 渲染叠加（把你的画布合成进成片） ────────────────────────

    /// <summary>
    /// 把你的画布合成进渲染视频（仅「纯净相机」来源；「游戏画面」来源拍的是
    /// 后备缓冲，你的画布本来就在）。主线程调用。
    ///
    /// 契约：渲染会话内画布会被临时切到 ScreenSpaceCamera 挂上渲染相机，
    /// 会话结束恢复原样；登记是 standing intent，跨渲染会话保留，画布销毁
    /// 自动除名，重复登记无副作用。你的动画若由游戏时间驱动
    /// （Time.time / Animator / 组件动画）在片里速度正确；由墙钟驱动的动画
    /// （DateTime/Stopwatch）在慢放视频里会偏快——JRP 的雨滴由本模组内置
    /// 换轨，无需你处理。
    /// </summary>
    public static void RegisterRenderOverlay(Canvas canvas)
        => Features.VideoRender.RenderOverlayCapture.RegisterExternal(canvas);

    /// <summary>撤销 <see cref="RegisterRenderOverlay"/> 的登记（已切开的画布立即恢复）。</summary>
    public static void UnregisterRenderOverlay(Canvas canvas)
        => Features.VideoRender.RenderOverlayCapture.UnregisterExternal(canvas);

    /// <summary>
    /// 渲染进度快照（随时可读；建议主线程）。Active=false 时仍可读到上次失败的 LastError。
    /// </summary>
    public static RenderStatus GetRenderStatus()
    {
        var s = Features.VideoRender.VideoRender.Current;
        return new RenderStatus
        {
            Active = Features.VideoRender.VideoRender.Active,
            // 越界保护：Phase 枚举增长时这张表若忘了同步，裸索引会在这里抛
            // IndexOutOfRange，把一次进度查询变成调用方（可能是外部 mod）的崩溃点。
            Phase = PhaseOf(Features.VideoRender.VideoRender.CurrentPhase),
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
    /// feature 取 FeatureKeys 常量字符串，如 "Replay"、"EffectRemover"。
    /// （未注册的 feature 名一律返回 false——本模组只注册了 Main.cs 里那几条。）
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
            d.SongName ?? "",
            d.ArtistName ?? "",
            d.JudgeMode ?? "",
            d.InternalLevelName ?? "",
            d.ModVersion ?? "",
            d.StartTile ?? -1,
            d.EndTile ?? -1,
            d.Bpm ?? 0,
            d.PlaybackSpeed ?? 1,
            d.HitSnapshots?.Count ?? 0,
            d.KeyEvents?.Count ?? 0);
    }
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
