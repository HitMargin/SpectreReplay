using System;
using SpectreReplay.Patching;
using SpectreReplay.Features.Recording;
using UnityEngine;

namespace SpectreReplay.Features.Playback
{
    /// <summary>
    /// 离开关卡的拆除钩子（偏差 23）。
    ///
    /// ── 为什么必须有 ────────────────────────────────────────────
    /// <c>PlaybackEngine.Stop()</c> 原本只有三个调用点：界面上的"停止"按钮、
    /// 泵自身的异常兜底、离线渲染收尾。也就是说：**只要玩家不点那个按钮，
    /// 回放就不会停**——而关卡已经重开、换关或退回主菜单了。
    ///
    /// 具体后果（都实测过，不是推演）：
    ///   · <c>RestoreGlobalState()</c> 没跑 → <c>noFail</c> 仍然是 <c>true</c>、
    ///     <c>GCS.hitMarginLimit</c> 仍然是录制时的档位，之后**正常游玩**也跟着生效。
    ///   · <c>KeyboardSimulation.ReleaseAll()</c> 没跑 → 回放中途按下的键一直卡着，
    ///     玩家的下一个物理按键只会被当成一次松开。
    ///   · 录制的记账补丁（<c>scrMarginTracker.AddHit</c> 的 Postfix）属于
    ///     "recording" 组，照常挂在游戏上，而新关卡里的判定会经过它——
    ///     **回放产生的判定被记进了新一局的回放**。
    ///   · <c>HitStateReplayer</c> 还留着上一局的数据，跟着新关卡的
    ///     <c>UpdateFreeroam</c> 继续按上一局的角度推进行星。
    ///
    /// ── 为什么全用 Prefix ───────────────────────────────────────
    /// 拆除必须在场景切换**之前**做完。<c>scrLoader.LoadSceneWithTransition</c>
    /// （scrLoader.cs:107）起一个 0.3 秒的 DOTween 擦黑，擦完才
    /// <c>SceneManager.LoadScene</c>；等到 Postfix 时旧场景的
    /// <c>scrController</c> 还活着、按键还按着——那已经晚了。
    ///
    /// ── 为什么不设 Feature 名 ──────────────────────────────────
    /// <c>FeatureGate.IsEnabled</c> 对**没声明的名字**返回 <c>false</c>
    /// （PatchLoader.cs:158），而带 Feature 的补丁在开关为假时根本不会挂上。
    /// 这些补丁的特征是"装上了模组就在"，不设 Feature（写空串）才对：
    /// 写 <c>"playback"</c> 的话，回放**没载入**时它们压根不挂，
    /// 于是"没载入回放 → 玩家退出去 → 上一局的录制状态残留"照样发生。
    ///
    /// 代价是计数：日志里"补丁 3/4"这类数字会更难看。
    /// 换来的是拆除路径不依赖任何开关。
    /// </summary>
    internal static class Patch_Lifecycle
    {
        /// <summary>
        /// 玩家从关卡里出去的唯一拆除入口。
        ///
        /// 四处挂点（UI 擦黑 / 换关重开 / 退主菜单 / 编辑器切回编辑态）
        /// 都调它，这样"退出时到底做了什么"只有一份可以核对。
        ///
        /// 录制与渲染的收尾**不在这里做**：那是
        /// <c>LevelWatcher.FinishCurrentLevel</c> 与 <c>RewindPostfix</c> 的活，
        /// 判据不同、落盘时机也不同（那边要取 <c>currentSeqID</c> 算终点地板）。
        /// 这里只管回放自己那份状态。
        /// </summary>
        internal static void Teardown()
        {
            try
            {
                // Stop() 内部对每一项都做了空守卫与 try/catch，多调一次无害，
                // 但必须调：**清数据**（_data/_cursor/_position）与
                // **还原全局状态**（noFail / hitMarginLimit）都只在这一条路径上。
                PlaybackEngine.Stop();
            }
            catch (Exception ex)
            {
                // 这四个 Prefix 挂在游戏的场景切换入口上。抛出去会让整个
                // 场景切换失败（玩家点了退主菜单却没退），所以必须兜住。
                Debug.LogError("[SpectreReplay] 退出关卡时拆除回放失败：" + ex);
            }
        }

        // ── ① 关卡选择/ TARO 菜单的 UI 级擦黑 ───────────────────
        //
        // scnLevelSelect.cs:822/:1020、scnTaroMenu2.cs:295、
        // scnTaroMenu3.cs:163、scnLevelSelectTaro.cs:149、scrController.cs:769
        // 都走这里。Spectre 也挂这一个（Patch_Lifecycle.cs:16-21）。
        [GamePatch(typeof(scrUIController), "WipeToBlack", PatchKind.Prefix)]
        private static void WipeToBlackPrefix()
        {
            Teardown();
        }

        // ── ② 重开当前关卡 ─────────────────────────────────────
        //
        // scrController.cs:2620 → :2626 LoadSceneWithTransition。
        // 注意 PauseMenu.cs:1434/:1463 走的是 StartLoadingScene 而不是 Restart，
        // 所以光挂 Restart 盖不住暂停菜单换关——那由 ① 覆盖。
        [GamePatch(typeof(scrController), "Restart", PatchKind.Prefix)]
        private static void RestartPrefix()
        {
            Teardown();
        }

        // ── ③ 退回主菜单 ───────────────────────────────────────
        //
        // scrController.cs:2437 → :2447 LoadSceneWithTransition。
        [GamePatch(typeof(scrController), "QuitToMainMenu", PatchKind.Prefix)]
        private static void QuitToMainMenuPrefix()
        {
            Teardown();
        }

        // ── ④ 编辑器里从游玩态切回编辑态 ───────────────────────
        //
        // scnEditor.cs:3156。它**不换场景**，只把游戏切回编辑态——
        // 于是 LevelWatcher 的场景名判据完全看不见这次离开，回放会一路
        // 挂到编辑器里继续跑。Spectre 专门挂了它（Patch_Lifecycle.cs:24-28）。
        [GamePatch(typeof(scnEditor), "SwitchToEditMode", PatchKind.Prefix)]
        private static void SwitchToEditModePrefix()
        {
            Teardown();
        }
    }
}