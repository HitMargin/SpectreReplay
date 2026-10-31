// 镜像回声抑制：MirrorInputToOS 与异步广播**并存**时的判定正确性闸。
//
// 镜像把回放按键用 SendInput 发成系统级真实按键——那是给「只读 Unity
// Input.GetKey 的键位显示模组」看的（它们既收不到托管状态，也收不到
// KeyUpdated 广播）。但 SkyHook 的钩子是全局 LL 钩子，不区分真手与注入：
// SendInput 的回声会被它再捕一次，经 HookCallback → KeyUpdated →
// AsyncInputManager 的 keyQueue，与合成事件（AsyncInputInjector）汇合——
// **同一颗键游戏判两遍**（实测 double press，回放判定被污染）。
// 2026-10-30 之前代码直接把两者互斥，代价是异步开着时镜像整个没效果、
// 且界面无任何提示（用户反馈的原话：「勾了没效果」）。
//
// 现在的分层：SendInput 照发（显示模组看得到），回声在钩子入口掐掉
// （队列与 KeyUpdated 都不再见到），游戏判定只认合成事件一份。
//
// 识别凭据：SkyHookEvent 没有 extra-info 字段（只有 Time/Type/Label/Key），
// 只能按「键码 + 方向 + 时间窗」认领（KeyboardSimulation.IsRecentlySentByUs，
// 窗口 150ms）。真实按键与镜像键同码同向且落窗内会被一并吞掉——回放期间
// 玩家的实键不参与判定，可接受。
//
// 回调跑在钩子原生线程：前缀只做一次环形缓冲查询，不碰任何 Unity API。
//
// **与其它 mod 的兼容性（0Harmony IL 级验证，MethodPatcher.AddPrefixes）**：
// 多个返回 bool 的前缀按优先级排序执行，生成代码是
//   `if (runOriginal == false) 跳过本前缀; 本前缀(); runOriginal = 返回值;`
// 即「首个 false 短路一切，后续前缀不再执行」。本补丁用默认优先级
// （None = -1，垫底最后跑），同方法的 ChatterBlocker / KeyboardChatterBlocker
// （400，先跑）返回 false 丢弃杂键时**我们根本不会执行**——不可能复活它们
// 的过滤；它们放行后我们的 false 也无人能推翻（在后的前缀已被短路）。
// 两个方向都互不破坏。
//
// **本补丁没挂上时（游戏改形状/挂载失败），ReplayPlayer 的镜像门退回
// 互斥**（不发 SendInput）——宁可显示模组失明，不能判定双算。挂载状态
// 经 SpectrePatchHub.IsApplied 查询：属性补丁的登记册在 Hub，PatchManager
// 的挂载集只记手工补丁类（2026-10-30 实测查错账本的教训），
// 退路断线由 CrossPlatformInputCheck ⑪c 钉住。

global using SpectreReplay.PatchShared;
using UnityEngine;

namespace SpectreReplay.Features.Replay;

internal static class Patch_MirrorEchoSuppressor
{
    private static bool _logged;
    private static bool _dropLogged;

    // 诊断（有界）：镜像回放期间的前 12 个钩子事件全量落日志——
    // 「回声没拦住」时对照发送记录即可分辨「没到达」还是「没认上」。
    private static int _diagLogged;

    [SpectrePatch(typeof(SkyHook.SkyHookManager), "HookCallback", PatchType.Prefix,
        Feature = FeatureKeys.Replay)]
    internal static bool Prefix(SkyHook.SkyHookEvent ev)
    {
        bool pressed = ev.Type == SkyHook.EventType.KeyPressed;
        bool sent = KeyboardSimulation.IsRecentlySentByUs(ev.Key, pressed);
        // **回放键一律吞**（判定正主是合成事件，不经 HookCallback，见下）：
        // 回放期间钩子里出现的回放键，来源是镜像回声，或 JKV 键位模拟对
        // 合成事件的再广播——合成事件经 KeyUpdated 广播喂 kv 显示，JKV 的
        // 按键模拟把它们当真键再注入一次 OS 层，钩子收到 → KeyUpdated →
        // 游戏判第二次（2026-10-31 用户确认的多判定，第二判「大慢/太快」
        // 随模拟时机抖动）。
        bool replayKey = SpectreReplay.SpectreState.is_playing
            && ReplayPlayer.ReplayKeySet.Contains(ev.Key);
        if (ManagedInput.Mirror && _diagLogged < 12)
        {
            _diagLogged++;
            Debug.Log($"[SpectreReplay][诊断] 钩子事件 Key={ev.Key} Pressed={pressed} "
                + $"认领={sent} 回放键={replayKey} | " + KeyboardSimulation.DescribeRecent(ev.Key, pressed));
        }
        if (replayKey)
        {
            if (!_dropLogged)
            {
                _dropLogged = true;
                Debug.Log("[SpectreReplay] 已在钩子入口吞掉回放键事件（镜像回声与 JKV 键位模拟"
                    + "都拦在这里；判定只认合成事件，kv 显示走 KeyUpdated 广播不受影响）");
            }
            return false;
        }
        // EventType 只有 KeyPressed / KeyReleased 两值，与 SendInput 的
        // KEYEVENTF_KEYUP 一一同型；Key 与我们发出去的 wVk 同一码空间
        // （AsyncInputInjector.Inject 的注释：vk 就是 SkyHook 记录的 native key）。
        if (!sent)
        {
            return true;
        }
        if (!_logged)
        {
            _logged = true;
            Debug.Log("[SpectreReplay] 已在钩子入口拦截 SendInput 回声"
                + "（镜像与异步广播并存：判定只认合成事件，显示走系统按键）");
        }
        return false;
    }
}
