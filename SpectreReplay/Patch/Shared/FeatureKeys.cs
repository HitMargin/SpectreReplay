namespace SpectreReplay;

// 功能键常量：attribute 的 Feature 与 Main 的 SetFeatureToggle 共用同一来源，
// 消除两处字符串各自手写导致的错位（错位 = 补丁静默不应用）。
// 不能命名为 Features——会与命名空间 Spectre.Features 在子命名空间内撞名
internal static class FeatureKeys
{
    public const string ResultsPlus = "ResultsPlus";
    public const string CalibAdvice = "CalibAdvice";
    public const string JudgeText = "JudgeText";
    public const string JudgeFx = "JudgeFx";
    public const string JudgeFilter = "JudgeFilter";
    public const string JudgeColor = "JudgeColor";
    public const string JudgeBar = "JudgeBar";
    public const string CustomJudge = "CustomJudge";
    public const string EffectRemover = "EffectRemover";
    public const string PlanetColor = "PlanetColor";
    public const string LegacyPauseCalc = "LegacyPauseCalc";
    public const string AlwaysShowMaxUsedKeys = "AlwaysShowMaxUsedKeys";
    public const string DecorationPreview = "DecorationPreview";
    public const string EnableProEvents = "EnableProEvents";
    // PACL2 对等功能的 Spectre 原生实现
    public const string MemoryOptimizer = "MemoryOptimizer";
    public const string EditorBugfix = "EditorBugfix";
    public const string BpmBeatCalculator = "BpmBeatCalculator";
    public const string SpeedPauseConverter = "SpeedPauseConverter";
    public const string BetterUndoRedo = "BetterUndoRedo";
    public const string BGAMode = "BGAMode";
    public const string EditorUI = "EditorUI";
    public const string Timeline = "Timeline";
    public const string CustomEvents = "CustomEvents";
    // 回放系统总开关：录制/回放/保存/录像共用的地基补丁全部挂此键；
    // 生命周期清理补丁（StopAll 系列）不挂，保证关闭后切场景仍能清残留状态
    public const string Replay = "Replay";
    // 主开关下的细分 key：谓词 = 主开关 && 子开关（Main 注册处体现层级），
    // 拨子开关同拍挂/卸补丁；UI 侧必须用 FeatureTog（裸 Tog 不触发 Refresh）
    public const string ReplayManualSave = "ReplayManualSave";
    public const string ReplayCancelSave = "ReplayCancelSave";
    public const string ReplayAsyncInput = "ReplayAsyncInput";
    public const string CustomRealJudge = "CustomRealJudge";
    public const string CustomOnly = "CustomOnly";
}
