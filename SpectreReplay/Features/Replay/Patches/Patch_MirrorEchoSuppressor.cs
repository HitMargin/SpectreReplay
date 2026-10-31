// 回放输入隔离：回放期间用户的物理键盘**整体退出判定**。
//
// 背景（2026-10-31 用户确认的根因链）：
//   · 镜像（MirrorInputToOS）把回放按键用 SendInput 发成系统级真实按键——
//     给「只读 Unity Input.GetKey 的键位显示模组」看的。SkyHook 的全局钩子
//     不区分真手与注入，回声会被它再捕一次，经 HookCallback → KeyUpdated →
//     keyQueue，与合成事件（AsyncInputInjector）汇合 → 同一颗键判两遍。
//   · JKV 的按键模拟同理：合成事件经 KeyUpdated 广播喂 kv 显示，JKV 把
//     它们当真键再注入一次 OS 层，同样直达判定（第二判「大慢/太快」随
//     模拟时机抖动）。用户实测：按下 JRP/JKV 配置的按键就会触发游戏判定。
//
// 修法（2026-10-31 第二版，取代「按发送记录认领回声」的第一版）：
//   **回放期间钩子事件一律吞掉，Esc（27）除外**。判定正主是合成事件，
//   不经过 HookCallback（Inject 直调 KeyUpdated），吞掉的永远不会是正主；
//   JRP 的 kv 显示走 KeyUpdated 合成事件广播，也不受影响。第一版按
//   「发送记录认领回声」有先天竞态：回声在钩子线程送达时，主线程可能
//   还没走到 RecordSentKeys——认领必然概率性失灵（用户实测「有概率」）。
//   全吞没有竞态可言。Esc 放行 = 暂停必须能用；模组自身热键走
//   GetKeyboardState 采样，不经钩子，不受影响。
//
// 回调跑在钩子原生线程：前缀只做一次布尔判断，不碰任何 Unity API。
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
    private static bool _isolatedLogged;
    // ADOFAI 的暂停键：Esc。回放期间唯一放行的用户按键。
    private const int PauseKey = 27;

    [SpectrePatch(typeof(SkyHook.SkyHookManager), "HookCallback", PatchType.Prefix,
        Feature = FeatureKeys.Replay)]
    internal static bool Prefix(SkyHook.SkyHookEvent ev)
    {
        // 非回放期（含渲染收尾后）一切照旧；Esc 永远放行（暂停必须能用）。
        if (!SpectreReplay.SpectreState.is_playing || ev.Key == PauseKey)
        {
            return true;
        }
        if (!_isolatedLogged)
        {
            _isolatedLogged = true;
            Debug.Log("[SpectreReplay] 回放期间已隔离物理键盘（Esc 除外）——镜像回声与"
                + "键位显示模组的再注入都拦在钩子入口，判定只认合成事件");
        }
        return false;
    }
}
