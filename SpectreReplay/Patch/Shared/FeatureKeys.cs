namespace SpectreReplay;

// 功能键常量：attribute 的 Feature 与 Main 的 SetFeatureToggle 共用同一来源，
// 消除两处字符串各自手写导致的错位（错位 = 补丁静默不应用）。
// 不能命名为 Features——会与命名空间 Spectre.Features 在子命名空间内撞名
internal static class FeatureKeys
{
    public const string EffectRemover = "EffectRemover";
    // 回放系统总开关：录制/回放/保存/录像共用的地基补丁全部挂此键；
    // 生命周期清理补丁（StopAll 系列）不挂，保证关闭后切场景仍能清残留状态
    public const string Replay = "Replay";
    // 主开关下的细分 key：谓词 = 主开关 && 子开关（Main 注册处体现层级），
    // 拨子开关同拍挂/卸补丁；UI 侧必须用 FeatureTog（裸 Tog 不触发 Refresh）
    public const string ReplayManualSave = "ReplayManualSave";
    public const string ReplayCancelSave = "ReplayCancelSave";
    public const string ReplayAsyncInput = "ReplayAsyncInput";
    // 特殊按键过滤（F1~F12 / Tab / Alt / Win / Print 等不再被游戏剔除）。
    // **没有对应的设置开关**：界面上的"勾选"直接就是这组设置本身，
    // 谓词 = 回放总开关 && 至少勾了一个键（见 Main 的注册行）。
    // 为什么不在"一个键都没勾"时也挂着：这条路径在**每帧输入处理**上，
    // 没勾键的玩家不该为一个用不到的功能付每帧一次委托调用的代价。
    public const string SpecialKeys = "SpecialKeys";
    // 结算详情的「最大同时按键数」默认只在 >10 时显示；打开后无条件显示。
    // 与回放/录制相互独立，所以谓词只看它自己的设置。
    public const string AlwaysShowMaxUsedKeys = "AlwaysShowMaxUsedKeys";
}
