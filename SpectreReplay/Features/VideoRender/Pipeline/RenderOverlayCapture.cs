// 渲染期 overlay 时钟换轨 + 第三方画布 opt-in 进片。
//
// 背景（0.1.6 用户观察）：「游戏画面」来源拍的是后备缓冲，键显/雨滴本来
// 就在片里——但 JRP 雨滴动画吃 KeyViewer.CurrentTicks（Stopwatch 墙钟），
// 渲染期游戏时间被钉到虚拟时钟后，片里的雨滴全是快放废片（用户观察：
// 渲染时雨速不变）。修法：渲染会话内给 CurrentTicks 挂手工前缀
// （PatchManager.RegisterManualPrefix；目标是外部 mod 的属性 getter，
// JRP 未安装就整段跳过），返回「锚点 + UnscaledDeltaPin 虚拟时钟增量」
// ——与画面同一个权威时钟，雨速与歌曲同步。**与画面来源无关**：
// 纯净相机 / 游戏画面两种来源都在会话开始时装配。
//
// 第三方画布 opt-in（SpectreAPI.RegisterRenderOverlay）：「纯净相机」来源
// 默认干净（键显/第三方 UI 一律不进片），别的 mod 显式登记自己的 Canvas
// 才会在纯净相机渲染时临时切 ScreenSpaceCamera 挂上渲染相机（order 30000
// 压过场景），会话结束恢复；「游戏画面」来源拍的就是后备缓冲，登记与否
// 画布本来就在，切模式反而是双重绘制，故不适用。
//
// 时钟域：JRP 全程用 Stopwatch 域做差（ToStopwatchTicks 把事件时间换算进
// 同一域），锚点取 Stopwatch.GetTimestamp()，增量乘 Stopwatch.Frequency
// ——与 JRP 的换算同域，不依赖 Stopwatch.Frequency 恰好等于 1e7。
//
// **前缀绝不抛异常**：JRP 的显示线程与 RainManager 都在读它，任何异常都会
// 打断显示模组；失败姿态是放行（return true → JRP 墙钟），宁可雨滴速度
// 不对，不能带崩别人的 mod。虚拟秒读 CurrentUnscaledF（float 读原子，
// 双精度在 32 位进程会被撕裂）；五分钟渲染的浮点误差 ~16μs，装饰动画无感。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SpectreReplay.Features.VideoRender;

internal static class RenderOverlayCapture
{
    // 第三方画布在纯净相机里的排序：与 Status 提示条同一档
    // （order 100 → 30000），压过场景与其它 UI。
    private const int OverlayOrder = 30000;

    private sealed class Entry
    {
        public Canvas Canvas;
        public RenderMode OldMode;
        public Camera OldWorldCamera;
        public int OldSortingOrder;
        public bool Switched;      // 已切到 ScreenSpaceCamera
    }

    private static readonly List<Entry> entries = new();
    private static Camera captureCamera;

    private static System.Reflection.MethodInfo clockTarget;
    private static readonly System.Reflection.MethodInfo clockPrefix
        = AccessTools.Method(typeof(RenderOverlayCapture), nameof(CurrentTicksPrefix));
    private static long anchorStopwatchTicks;
    private static double anchorVirtualSeconds;
    private static bool anchorTaken;

    /// <summary>
    /// 渲染会话开始时调一次（**与画面来源无关**，两种来源都必须）——
    /// JRP 雨滴吃墙钟，不换轨的话「游戏画面」成片里雨速全是错的。
    /// </summary>
    internal static void InstallClockOverride()
    {
        InstallClockPatch();
    }

    /// <summary>
    /// 仅「纯净相机」来源调：启用第三方画布的 opt-in 合成（切模式在此生效）。
    /// </summary>
    internal static void InstallCameraCapture(Camera mainCamera)
    {
        captureCamera = mainCamera;
        ReapplyIfNeeded();
    }

    /// <summary>每帧调（仅纯净相机生效）：登记的画布被重建时重新查找套用。</summary>
    internal static void ReapplyIfNeeded()
    {
        if (captureCamera == null) return;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            Entry e = entries[i];
            if (e.Canvas == null)
            {
                // 画布随场景/mod 卸载销毁：登记一并除名（持有已无意义）
                entries.RemoveAt(i);
                continue;
            }
            if (e.Switched) continue;
            e.OldMode = e.Canvas.renderMode;
            e.OldWorldCamera = e.Canvas.worldCamera;
            e.OldSortingOrder = e.Canvas.sortingOrder;
            // 先指相机再切模式：切到 ScreenSpaceCamera 的瞬间若 worldCamera 为空，
            // Unity 会抓 Camera.main 兜底——渲染期那不是我们的取像相机。
            e.Canvas.worldCamera = captureCamera;
            e.Canvas.renderMode = RenderMode.ScreenSpaceCamera;
            e.Canvas.sortingOrder = OverlayOrder;
            e.Switched = true;
        }
    }

    /// <summary>渲染会话结束时调一次（幂等，两种来源都调）。时钟补丁卸载、已切换的画布恢复。</summary>
    internal static void Restore()
    {
        UninstallClockPatch();
        foreach (Entry e in entries)
        {
            if (!e.Switched) continue;
            try
            {
                e.Canvas.sortingOrder = e.OldSortingOrder;
                e.Canvas.renderMode = e.OldMode;
                e.Canvas.worldCamera = e.OldWorldCamera;
            }
            catch (Exception ex)
            {
                // 画布可能随场景销毁：恢复失败不影响相机还原，记日志即可
                Debug.LogException(ex);
            }
            e.Switched = false;
        }
        captureCamera = null;
        anchorTaken = false;
    }

    /// <summary>外部 mod 经 SpectreAPI 登记。重复登记无副作用；画布销毁自动除名。</summary>
    internal static void RegisterExternal(Canvas canvas)
    {
        if (canvas == null || entries.Any(e => e.Canvas == canvas)) return;
        entries.Add(new Entry { Canvas = canvas });
        ReapplyIfNeeded();
    }

    internal static void UnregisterExternal(Canvas canvas)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            Entry e = entries[i];
            if (e.Canvas != canvas) continue;
            if (e.Switched)
            {
                try
                {
                    e.Canvas.sortingOrder = e.OldSortingOrder;
                    e.Canvas.renderMode = e.OldMode;
                    e.Canvas.worldCamera = e.OldWorldCamera;
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
            entries.RemoveAt(i);
        }
    }

    private static void InstallClockPatch()
    {
        if (clockTarget != null) return;
        try
        {
            Type kv = AccessTools.TypeByName("JipperResourcePack.KeyViewerContents.KeyViewer");
            if (kv == null) return;   // JRP 未安装：没有雨滴，无需换轨
            clockTarget = AccessTools.PropertyGetter(kv, "CurrentTicks");
            if (clockTarget == null)
            {
                Debug.LogWarning("[SpectreReplay] 雨滴时钟换轨未生效：JRP 的 CurrentTicks 属性形状变了"
                    + "（游戏画面来源的成片里雨滴将跟随墙钟，速度不对）");
                return;
            }
            PatchManager.RegisterManualPrefix(clockTarget, clockPrefix);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            clockTarget = null;
        }
    }

    private static void UninstallClockPatch()
    {
        if (clockTarget == null) return;
        try
        {
            PatchManager.UnpatchManualPrefix(clockTarget);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
        clockTarget = null;
        anchorTaken = false;
    }

    // 渲染期 KeyViewer.CurrentTicks 的替身：锚点 + 虚拟时钟增量。
    // 锚点惰性取自首次调用（此时 UnscaledDeltaPin 必已安装——帧源构造晚于
    // 渲染时钟装配），未安装则放行走 JRP 墙钟。
    private static bool CurrentTicksPrefix(ref long __result)
    {
        if (!VideoRender.Active || !UnscaledDeltaPin.Installed) return true;
        if (!anchorTaken)
        {
            anchorStopwatchTicks = Stopwatch.GetTimestamp();
            anchorVirtualSeconds = UnscaledDeltaPin.CurrentUnscaledD;
            anchorTaken = true;
        }
        // CurrentUnscaledF 每帧推进 1/captureFramerate（钉扎语义），雨滴由此跟拍游戏时间
        __result = anchorStopwatchTicks
            + (long)((UnscaledDeltaPin.CurrentUnscaledF - (float)anchorVirtualSeconds) * Stopwatch.Frequency);
        return false;
    }
}
