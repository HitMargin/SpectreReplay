namespace SpectreReplay;

// 功能键常量：attribute 的 Feature 与 Main 的 SetFeatureToggle 共用同一来源，
// 消除两处字符串各自手写导致的错位（错位 = 补丁静默不应用）。
// 不能命名为 Features——会与命名空间 Spectre.Features 在子命名空间内撞名
internal static class FeatureKeys
{
    public const string JudgeText = "JudgeText";
    public const string JudgeFx = "JudgeFx";
    public const string JudgeFilter = "JudgeFilter";
    public const string JudgeColor = "JudgeColor";
    public const string JudgeBar = "JudgeBar";
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
    // 渲染时钟补丁（scrConductor.Update 的时间源 Transpiler）。**实验性，默认关**：
    // 它把 AudioSettings.dspTime / Time.unscaled* 四个调用整体换掉，收益是
    // "游戏自己的节拍推进/相机/倒计时/hitsound 全部自动确定性化"，代价是
    // ①改了游戏时间源本身，出问题的面比"只拦输出"大得多；
    // ②与别的 mod 的同款 transpiler 竞争（先到先得，见 RenderClockSource 让位逻辑）。
    // 做成开关后：关闭 = 补丁根本不挂载，渲染走 audio-cursor 兜底时钟。
    public const string RenderClock = "RenderClock";
    // 特殊按键过滤（F1~F12 / Tab / Alt / Win / Print 等不再被游戏剔除）。
    // **没有对应的设置开关**：界面上的"勾选"直接就是这组设置本身，
    // 谓词 = 回放总开关 && 至少勾了一个键（见 Main 的注册行）。
    // 为什么不在"一个键都没勾"时也挂着：这条路径在**每帧输入处理**上，
    // 没勾键的玩家不该为一个用不到的功能付每帧一次委托调用的代价。
    public const string SpecialKeys = "SpecialKeys";
}
