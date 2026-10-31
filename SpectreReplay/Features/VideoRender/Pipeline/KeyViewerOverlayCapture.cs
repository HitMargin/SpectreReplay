// 渲染期把键位显示画布（Jipper KeyViewer，含 JRP 雨滴）合成进成片。
//
// 键显画布是 ScreenSpaceOverlay（反编译 JKV BuildOverlay：renderMode 0 /
// sortingOrder 2），由 Unity 直接合成到后备缓冲——我们的成片从相机
// RenderTexture 逐帧取像，Overlay 永远拍不到。0.1.6 用户观察：
// 渲染时雨滴在屏幕上照下，成片里没有。
//
// 修法两层，缺一不可：
//   · 画布进片：渲染会话内把画布临时切到 ScreenSpaceCamera、worldCamera
//     指向主渲染相机（camMain），排序拉到场景之上；结束恢复 Overlay。
//     副作用是良性的：预览相机把 RT 贴回屏幕，屏幕上的键显从此与成片一致。
//   · 雨滴跟拍虚拟时钟：雨滴/KPS 全部吃 JRP 的 KeyViewer.CurrentTicks——
//     Stopwatch 墙钟（用户观察：渲染时雨速不变）。渲染期游戏时间被钉到
//     虚拟时钟后，墙钟雨滴在慢放视频里全是快放废片。给 CurrentTicks 挂
//     手工前缀（PatchManager.RegisterManualPrefix；目标是外部 mod 的属性
//     getter，JRP 未安装就整段跳过），渲染期返回「锚点 + 虚拟时钟增量」
//     （UnscaledDeltaPin——与画面同一个权威时钟），雨滴/KPS 跟拍游戏时间。
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
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace SpectreReplay.Features.VideoRender;

internal static class KeyViewerOverlayCapture
{
    private const string CanvasName = "Jipper KeyViewer";
    // 与 Status 提示条同一档（order 100 → 30000）：压过场景与其它 UI。
    private const int OverlayOrder = 30000;

    private static Canvas canvas;
    private static RenderMode oldMode;
    private static Camera oldWorldCamera;
    private static int oldSortingOrder;
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
        ReapplyIfNeeded();
        InstallClockPatch();
    }

    /// <summary>每帧调：画布被重建（JKV 改设置/切场景）时重新套用切模式。</summary>
    internal static void ReapplyIfNeeded()
    {
        if (!Settings.RenderKeyViewerOverlay || captureCamera == null) return;
        if (canvas != null) return;
        // Find 只匹配**活动**的根对象；键显画布恰是根（"Jipper KeyViewer"）
        GameObject go = GameObject.Find(CanvasName);
        if (go == null) return;
        Canvas found = go.GetComponent<Canvas>();
        if (found == null) return;
        canvas = found;
        oldMode = canvas.renderMode;
        oldWorldCamera = canvas.worldCamera;
        oldSortingOrder = canvas.sortingOrder;
        // 先指相机再切模式：切到 ScreenSpaceCamera 的瞬间若 worldCamera 为空，
        // Unity 会抓 Camera.main 兜底——渲染期那不是我们的取像相机。
        canvas.worldCamera = captureCamera;
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.sortingOrder = OverlayOrder;
    }

    /// <summary>渲染会话结束时调一次（幂等）。画布恢复 Overlay、时钟补丁卸载。</summary>
    internal static void Restore()
    {
        UninstallClockPatch();
        if (canvas != null)
        {
            try
            {
                canvas.sortingOrder = oldSortingOrder;
                canvas.renderMode = oldMode;
                canvas.worldCamera = oldWorldCamera;
            }
            catch (Exception ex)
            {
                // 画布可能随场景销毁：恢复失败不影响相机还原，记日志即可
                Debug.LogException(ex);
            }
        }
        canvas = null;
        captureCamera = null;
        anchorTaken = false;
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
