// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpectreReplay.Features.VideoRender;

// 渲染期间对 conductor 与异步输入的三个干预点：
// 1/2. songposition_minusi 读写都替换为钉扎值（写入换值 + 读取覆盖，双保险）
// 3.   异步输入角度修正跳过 —— 该修正按真实时间戳换算时间，与钉扎时钟脱钩，
//      会以真实速率覆盖星球角度导致旋转错乱
// 注意：输入校准（calibration_i）在渲染期间必须保持真实值，不能归零——
// 钉扎时间轴锚在 songposition_minusi 上（已含 −i·pitch），游戏的行星视觉公式
// 靠 +i·pitch / −i 的回加项与之对消；归零会让星球恒定偏移一个校准量。
// 已迁移 [SpectrePatch] 属性路径（Accessor 指属性访问器，见 SpectrePatchHub）
internal static class Patch_VideoPinSet
{
    // 渲染是瞬态过程：补丁常驻、体内早退，避免会话中途刷新补丁。
    // 录像地基同录制：pin 到已验证 build
    [SpectrePatch(typeof(scrConductor), nameof(scrConductor.songposition_minusi), PatchType.Prefix,
        PropertyAccessor.Set)]
    internal static void Prefix(ref double value)
    {
        if (VideoRender.TryReadPinnedPosition(out double pinned))
            value = pinned;
    }
}

internal static class Patch_VideoPinGet
{
    [SpectrePatch(typeof(scrConductor), nameof(scrConductor.songposition_minusi), PatchType.Postfix,
        PropertyAccessor.Get)]
    internal static void Postfix(ref double __result)
    {
        if (VideoRender.TryReadPinnedPosition(out double pinned))
            __result = pinned;
    }
}

internal static class Patch_VideoLockAsyncAngle
{
    [SpectrePatch(typeof(AsyncInputUtils), nameof(AsyncInputUtils.AdjustAngle), PatchType.Prefix)]
    internal static bool Prefix()
        => !VideoRender.Active;
}

// ——— scnEditor.RemakePath ————————————————————————————————
// 渲染期把倍速钉回回放记录值。
//
// **时机是这条补丁的全部意义。** scnEditor.Play() 先无条件写
// `playbackSpeed = holdingControl ? … : 1f`（源码 scnEditor.cs:7204-7205），
// 紧接着调 RemakePath() → scnGame.RemakePath → SetupConductorWithLevelData，
// 而后者用 editor.playbackSpeed 推导 song.pitch（scrConductor.cs:378-395）。
// 只在 Play() 之前设没有用（会被覆盖），只在 Play() 之后设也没有用
// （pitch 已经算错、时间轴已经按 1x 建好）。必须在两者之间，也就是
// RemakePath 的 Prefix——这正是 Play() 内部唯一经过的钩子。
internal static class Patch_VideoSpeedOverride
{
    [SpectrePatch(typeof(scnEditor), nameof(scnEditor.RemakePath), PatchType.Prefix,
        Feature = FeatureKeys.Replay)]
    internal static void Prefix(scnEditor __instance)
        => VideoRender.ApplySpeedOverrideTo(__instance);
}

// 渲染期把 unscaled 时间族整体钉到虚拟时钟：captureFramerate 只钉缩放时间，unscaled 系
// 按设计走真实耗时——离线渲染每帧真实耗时近似恒定（瓶颈在回读/编码），用它驱动的
// 动画在成片里会按 帧率×真实帧耗时 变速（120fps≈2 倍）。
//
// 覆盖五个 getter：unscaledDeltaTime（增量）+ unscaledTime / realtimeSinceStartup 及
// 其 AsDouble 变体（绝对值）。绝对值必须一并钉：DOTween 独立 tween（SetUpdate(true)，
// 游戏 Notification 弹窗等）取绝对时钟差驱动，只钉增量时它们仍按真实速率跑。
// 绝对值实现为"安装时读原始基值 + 每帧推进 1/captureFramerate"的虚拟时钟，
// 由渲染循环每帧调用 TickFrame 推进。
//
// Time.* 均为引擎 icall，Harmony 打不了（生成托管包装必得非法 IL），用其底层
// MonoMod.Core 的原生 detour，只在渲染会话内安装、结束即卸载：正常游玩期
// icall 原样零风险，钉扎体无需分支（不渲染时钩子不存在），不需要 trampoline
internal static class UnscaledDeltaPin
{
    // 0Harmony 把 MonoMod.Core（DetourFactory/ICoreDetour）整体内化为 internal，
    // 只能反射调用；detour 经其 IDisposable 接口持有与撤销
    private static readonly List<IDisposable> detours = new();
    private static System.Reflection.MethodInfo createDetour;
    private static object factory;

    private static double baseUnscaled;    // Time.unscaledTime 安装时基值
    private static double baseRealtime;    // Time.realtimeSinceStartup 安装时基值
    private static double virtualElapsed;  // 安装后累计的虚拟时间

    // 钉扎期使用的帧率快照。**不能直接读 Time.captureFramerate 做除数。**
    // 收尾时 RestoreGlobalState 会把它还原成安装前的值（通常是 0），而
    // PinnedDelta 正是 `1f / Time.captureFramerate`——0 做除数得 Infinity，
    // 灌进星球位置后整个画面变 NaN。快照一旦定下来就不再跟随后续还原。
    private static int armedFps = 60;

    internal static void Install()
    {
        // **不能直接 return。** 上一轮 Uninstall 若有 detour 撤不掉（见 Uninstall
        // 的说明），列表里会留下那个仍然挂着的 detour。此时若 return，本轮
        // 「什么都不装」，而 Time.unscaledDeltaTime 仍被残留 detour 接管——
        // 它读的是 captureFramerate，那个值此刻已恢复成 0 ⇒ 1/0 = Infinity
        // ⇒ 星球位置 NaN。表现就是用户报的「取消一次渲染后，下一次时钟全炸」。
        // 所以有残留时先尽力清掉，清掉了再正常安装。
        if (detours.Count > 0)
        {
            Uninstall();
            if (detours.Count > 0)
            {
                // 确实撤不掉。**不再往上叠新 detour**：叠上去就是两层钉扎，
                // 而旧那层读的是本类的静态字段、新那层也是，两者互相踩，
                // 比单纯残留更难定位。明确告警并放弃本轮钉扎——游戏拿到的是
                // 真实时钟，成片可能不够"确定性"，但不会静默产出 NaN。
                Debug.LogWarning("[SpectreReplay] unscaled time pin: "
                    + detours.Count + " 个残留 detour 撤销失败，本次不钉扎（走真实时钟）");
                return;
            }
        }
        try
        {
            if (createDetour == null)
            {
                Type factoryType = AccessTools.TypeByName("MonoMod.Core.DetourFactory");
                if (factoryType != null)
                    factory = PatchManager.CreateStaticPropertyGetter<object>(factoryType, "Current")();
                if (factoryType != null && factory != null)
                    createDetour = PatchManager.GetMethodInfo(factoryType, "CreateDetour");
                // 签名校验：UMM 换捆绑 MonoMod 版本后若出现同名但形参语义不同的重载，
                // Invoke"成功"却建立错误 detour = 原生崩溃无法兜——宁可判无效降级
                if (createDetour != null && !DetourSignatureLooksRight(createDetour))
                {
                    Debug.LogWarning("[SpectreReplay] unscaled time pin: CreateDetour signature unexpected, skipping");
                    createDetour = null;
                }
            }
            if (createDetour == null || factory == null)
            {
                Debug.LogWarning("[SpectreReplay] unscaled time pin unavailable: MonoMod.Core detour API not found");
                return;
            }

            // 安装前读原始基值（此时 getter 尚未被替换）
            baseUnscaled = Time.unscaledTime;
            baseRealtime = Time.realtimeSinceStartup;
            virtualElapsed = 0.0;
            // 先把帧率快照定死再装 detour。装完之后 PinnedDelta 只认这个快照，
            // 不再读 Time.captureFramerate——它会在收尾时被还原成 0。
            armedFps = Time.captureFramerate > 0 ? Time.captureFramerate : 60;

            // 增量是关键项（雨滴等逐帧动画），绝对值为尽力项（个别 Unity 版本缺 AsDouble）
            TryMake(nameof(Time.unscaledDeltaTime), nameof(PinnedDelta), critical: true);
            TryMake(nameof(Time.unscaledTime), nameof(PinnedUnscaled), critical: true);
            TryMake(nameof(Time.unscaledTimeAsDouble), nameof(PinnedUnscaledD), critical: false);
            TryMake(nameof(Time.realtimeSinceStartup), nameof(PinnedRealtime), critical: true);
            TryMake(nameof(Time.realtimeSinceStartupAsDouble), nameof(PinnedRealtimeD), critical: false);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] unscaled time pin unavailable: " + ex.Message);
            Uninstall();
        }
    }

    // 渲染循环每帧调用一次，推进虚拟时钟（与增量钉扎值严格一致）
    internal static void TickFrame()
        => virtualElapsed += 1.0 / armedFps;

    // 供 RenderClock 时间源 Transpiler 复用：unscaled 绝对时间只有一个权威实现。
    // 否则"Transpiler 用 dsp 锚点"与"原生 detour 用 unscaled 基值"会形成两套虚拟时钟，
    // 同一帧内游戏读到的 unscaled 时间取决于调用点，产生难以定位的分叉。
    internal static bool Installed => detours.Count > 0;
    internal static double CurrentUnscaledD => baseUnscaled + virtualElapsed;
    internal static float CurrentUnscaledF => (float)(baseUnscaled + virtualElapsed);
    internal static float CurrentDelta => 1f / armedFps;

    internal static void Uninstall()
    {
        // 撤不掉的 detour **必须留在列表里**，不能连同 Clear 一起丢掉。
        //
        // 后果链是这样的：取消渲染发生在场景切换途中（渲染第一步就是 SwitchToPlayMode
        // 重开关卡），此时目标 getter 所在的对象可能已被卸载，MonoMod 的 Dispose
        // 会抛。原实现把异常吞掉后照样 detours.Clear()，于是那个 detour
        // **仍然挂在 Time.unscaledDeltaTime 上，但我们再也不知道它的存在**。
        // 下一轮 Install 看到 Count == 0 就正常安装，旧 detour 成了永久接管者，
        // 读的却是新一轮的静态字段 —— 时钟就此错乱。
        var failed = new List<IDisposable>();
        foreach (IDisposable d in detours)
        {
            bool undone = false;
            try { d.Dispose(); undone = true; }
            catch (Exception ex)
            {
                Debug.Log("[SpectreReplay] unscaled time pin undo failed: " + ex.Message
                    + "（重试一次）");
                // 重试一次：首次失败常常是目标对象正在销毁、下一帧就正常了
                try { d.Dispose(); undone = true; }
                catch (Exception ex2)
                {
                    Debug.LogError("[SpectreReplay] unscaled time pin undo keeps failing: "
                        + ex2.Message + "（保留引用以便下次再试）");
                }
            }
            if (!undone)
                failed.Add(d);
        }
        detours.Clear();
        foreach (IDisposable d in failed)
            detours.Add(d);          // 留着，下次 Install/Uninstall 再试
    }

    private static void TryMake(string propertyName, string pinMethodName, bool critical)
    {
        try
        {
            System.Reflection.MethodBase orig = PatchManager.GetPropertyGetter(typeof(Time), propertyName);
            System.Reflection.MethodBase pin = typeof(UnscaledDeltaPin).GetMethod(pinMethodName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            detours.Add((IDisposable)createDetour.Invoke(null, new object[] { factory, orig, pin, true }));
        }
        catch (Exception ex) when (!critical)
        {
            Debug.Log("[SpectreReplay] unscaled time pin skipped " + propertyName + ": " + ex.Message);
        }
    }

    private static bool DetourSignatureLooksRight(System.Reflection.MethodInfo m)
    {
        var ps = m.GetParameters();
        return ps.Length == 4
            && typeof(System.Reflection.MethodBase).IsAssignableFrom(ps[1].ParameterType)
            && typeof(System.Reflection.MethodBase).IsAssignableFrom(ps[2].ParameterType)
            && ps[3].ParameterType == typeof(bool);
    }

    private static float PinnedDelta() => 1f / armedFps;
    private static float PinnedUnscaled() => (float)(baseUnscaled + virtualElapsed);
    private static double PinnedUnscaledD() => baseUnscaled + virtualElapsed;
    private static float PinnedRealtime() => (float)(baseRealtime + virtualElapsed);
    private static double PinnedRealtimeD() => baseRealtime + virtualElapsed;
}
