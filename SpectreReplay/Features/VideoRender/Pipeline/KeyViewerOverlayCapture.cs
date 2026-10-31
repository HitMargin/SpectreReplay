// 渲染期把 Overlay 画布合成进成片：内置登记键显画布（Jipper KeyViewer，
// 含 JRP 雨滴），并通过 SpectreAPI.RegisterRenderOverlay 向其它 mod 开放。
//
// 键显画布是 ScreenSpaceOverlay（反编译 JKV BuildOverlay：renderMode 0 /
// sortingOrder 2），由 Unity 直接合成到后备缓冲——我们的成片从相机
// RenderTexture 逐帧取像，Overlay 永远拍不到。0.1.6 用户观察：
// 渲染时雨滴在屏幕上照下，成片里没有。
//
// 修法两层，缺一不可：
//   · 画布进片：渲染会话内把画布临时切到 ScreenSpaceCamera、worldCamera
//     指向主渲染相机（camMain），排序拉到场景之上；结束恢复 Overlay。
//     副作用是良性的：预览相机把 RT 贴回屏幕，屏幕上的画面与成片一致。
//   · 时钟换轨：雨滴/KPS 全部吃 JRP 的 KeyViewer.CurrentTicks——Stopwatch
//     墙钟（用户观察：渲染时雨速不变）。渲染期游戏时间被钉到虚拟时钟后，
//     墙钟动画在慢放视频里全是快放废片。给 CurrentTicks 挂手工前缀
//     （PatchManager.RegisterManualPrefix；目标是外部 mod 的属性 getter，
//     JRP 未安装就整段跳过），渲染期返回「锚点 + 虚拟时钟增量」
//     （UnscaledDeltaPin——与画面同一个权威时钟）。
//
// **开放 API**：其它 mod 经 SpectreAPI.RegisterRenderOverlay / Unregister-
// RenderOverlay 登记自己的画布（standing intent，跨渲染会话保留）。
// 契约写在 SpectreAPI 的方法注释里：游戏时间驱动的动画在片里速度正确，
// 墙钟驱动的动画需自行换轨——JRP 的雨滴由本类内置处理。
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

internal static class KeyViewerOverlayCapture
{
    private const string CanvasName = "Jipper KeyViewer";
    // 与 Status 提示条同一档（order 100 → 30000）：压过场景与其它 UI。
    private const int OverlayOrder = 30000;

    private sealed class Entry
    {
        public Canvas Canvas;
        public RenderMode OldMode;
        public Camera OldWorldCamera;
        public int OldSortingOrder;
        public bool Switched;      // 已切到 ScreenSpaceCamera
        public bool External;      // SpectreAPI 登记（跨会话保留）；false = JKV 自动登记（跟随设置）
    }

    private static readonly List<Entry> entries = new();
    private static Camera captureCamera;

    private static System.Reflection.MethodInfo clockTarget;
    private static readonly System.Reflection.MethodInfo clockPrefix
        = AccessTools.Method(typeof(KeyViewerOverlayCapture), nameof(CurrentTicksPrefix));
    private static long anchorStopwatchTicks;
    private static double anchorVirtualSeconds;
    private static bool anchorTaken;

    /// <summary>渲染会话开始时调一次。mainCamera = 承载成片的主渲染相机。</summary>
    internal static void Install(Camera mainCamera)
    {
        captureCamera = mainCamera;
        // JKV 自动登记跟随设置开关；外部登记是别的 mod 的 standing intent，
        // 不受本模组设置影响。
        if (Settings.RenderKeyViewerOverlay) EnsureAutoRegistered();
        else RemoveAuto();
        InstallClockPatch();
        ReapplyIfNeeded();
    }

    /// <summary>每帧调：登记的画布被重建（mod 改设置/切场景）时重新查找套用。</summary>
    internal static void ReapplyIfNeeded()
    {
        if (captureCamera == null) return;
        EnsureAutoRegistered();
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            Entry e = entries[i];
            if (e.Canvas == null)
            {
                // 画布随场景/mod 卸载销毁：外部登记一并除名（持有已无意义）
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

    /// <summary>渲染会话结束时调一次（幂等）。已切换的画布恢复原样；外部登记保留。</summary>
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
        entries.Add(new Entry { Canvas = canvas, External = true });
        ReapplyIfNeeded();
    }

    internal static void UnregisterExternal(Canvas canvas)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            Entry e = entries[i];
            if (!e.External || e.Canvas != canvas) continue;
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

    // JKV 自动登记：按设置开关增删。画布此刻可能还没建（JKV 晚建/重建），
    // 找不到就留空，Reapply 每帧重试。
    private static void EnsureAutoRegistered()
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].External) continue;
            if (entries[i].Canvas == null) entries.RemoveAt(i);   // 销毁的自动登记除名
        }
        if (entries.Any(e => !e.External)) return;
        GameObject go = GameObject.Find(CanvasName);
        if (go == null) return;
        Canvas found = go.GetComponent<Canvas>();
        if (found == null) return;
        entries.Add(new Entry { Canvas = found, External = false });
    }

    private static void RemoveAuto()
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            Entry e = entries[i];
            if (e.External) continue;
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
                Debug.LogWarning("[SpectreReplay] 键显时钟换轨未生效：JRP 的 CurrentTicks 属性形状变了"
                    + "（雨滴在成片里将跟随墙钟，速度不对）");
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
