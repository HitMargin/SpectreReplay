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

// 渲染期把 unscaled 时间族与 AudioSettings.dspTime 整体钉到虚拟时钟：
// captureFramerate 只钉缩放时间，unscaled 系按设计走真实耗时——离线渲染每帧真实
// 耗时近似恒定（瓶颈在回读/编码），用它驱动的动画在成片里会按 帧率×真实帧耗时
// 变速（120fps≈2 倍）。
//
// 覆盖六个 getter：unscaledDeltaTime（增量）+ unscaledTime / realtimeSinceStartup 及
// 其 AsDouble 变体（绝对值）+ AudioSettings.dspTime（游戏歌曲时钟）。绝对值必须一并钉：
// DOTween 独立 tween（SetUpdate(true)，游戏 Notification 弹窗等）取绝对时钟差驱动，
// 只钉增量时它们仍按真实速率跑。dspTime 是 scrConductor 计算歌曲位置的权威时钟
// （任何游戏版本都读它），getter 钉死后游戏时钟 = 基值 + 已捕获帧数/fps，与成片帧号
// 严格锁定——这是 ADOFAIRenderer 同款思路，r149/r150 无差别，不再依赖 Transpiler。
// 虚拟时钟统一为"安装时读原始基值 + 每帧推进 1/armedFps"，由渲染循环每帧调用
// TickFrame 推进；真实 dspTime 经 trampoline 探针保留给诊断（见 RealDsp）。
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
    private static double baseDsp;         // AudioSettings.dspTime 安装时基值

    // dspTime 的 detour 独立于 unscaled 列表管理：它在捕获就绪时才装（等待回放/
    // 倒计时阶段游戏必须继续走真实时钟，提前钉住会把倒计时冻死），失败姿态是
    // 放行 Unity 原生行为（captureFramerate 下原生 dspTime 也按帧推进，仍可用）。
    private static IDisposable dspDetour;
    // 真实 dspTime 探针（原 getter 的 trampoline）：钉扎期混音静默诊断要区分
    // "dsp 没推进"与"有 dsp 没混音"，读被钉的 getter 只会读到自己的回声。
    private static Func<double> realDspProbe;

    // 钉扎期使用的帧率快照。**不能直接读 Time.captureFramerate 做除数。**
    // 收尾时 RestoreGlobalState 会把它还原成安装前的值（通常是 0），而
    // PinnedDelta 正是 `1f / Time.captureFramerate`——0 做除数得 Infinity，
    // 灌进星球位置后整个画面变 NaN。快照一旦定下来就不再跟随后续还原。
    private static int armedFps = 60;

    internal static void Install(int fps)
    {
        if (detours.Count > 0)
        {
            Uninstall();
            if (detours.Count > 0)
            {
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
            // 帧率快照由调用方显式传入：安装点（SaveGlobalState）先于
            // captureFramerate 设置，读 Time.captureFramerate 只会拿到上一局的
            // 残值（通常 0 → 60），自定义帧率下 unscaled 虚拟时钟就会走错步长。
            armedFps = Math.Max(1, fps);

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

    // 捕获就绪时调用一次：把 AudioSettings.dspTime getter 钉到虚拟时钟。
    // 必须晚于"等待回放"阶段（倒计时走真实时钟），早于任何捕获帧的游戏更新。
    internal static void InstallDsp()
    {
        if (dspDetour != null) return; // 已装（理论不可达：会话内只调一次）
        try
        {
            if (createDetour == null || factory == null)
            {
                Debug.LogWarning("[SpectreReplay] dsp time pin unavailable: MonoMod.Core detour API not found"
                    + "（游戏时钟将走 Unity 原生 captureFramerate 行为）");
                return;
            }
            // 安装前读原始基值：虚拟 dspTime 从此刻的真实值起连续推进，游戏侧
            // 无时间跳变。之后所有托管读取（任意游戏版本的 scrConductor、音频
            // 调度、诊断）都拿到 base + 已捕获帧数/fps。
            baseDsp = AudioSettings.dspTime;
            System.Reflection.MethodBase orig = PatchManager.GetPropertyGetter(typeof(AudioSettings), "dspTime");
            System.Reflection.MethodBase pin = typeof(UnscaledDeltaPin).GetMethod(nameof(PinnedDsp),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            object detour = createDetour.Invoke(null, new object[] { factory, orig, pin, true });
            realDspProbe = MakeRealDspProbe(detour);
            dspDetour = (IDisposable)detour;
            Debug.Log("[SpectreReplay] dsp time pinned to render clock: base=" + baseDsp.ToString("0.000")
                + " fps=" + armedFps + (realDspProbe != null ? " (real-dsp probe ready)" : ""));
        }
        catch (Exception ex)
        {
            dspDetour = null;
            realDspProbe = null;
            Debug.LogWarning("[SpectreReplay] dsp time pin failed: " + ex.Message
                + "（游戏时钟将走 Unity 原生 captureFramerate 行为）");
        }
    }

    // 从 detour 对象上取原 getter 的 trampoline（MonoMod.Core.IDetour.GenerateTrampoline<T>）。
    // 探针只服务诊断，取不到就退化为 NaN（调用方按"无法探真实 dsp"降级）。
    private static Func<double> MakeRealDspProbe(object detour)
    {
        try
        {
            const System.Reflection.BindingFlags All
                = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance;
            foreach (System.Reflection.MethodInfo m in detour.GetType().GetMethods(All))
            {
                if (!m.IsGenericMethod || m.Name != "GenerateTrampoline") continue;
                return (Func<double>)m.MakeGenericMethod(typeof(Func<double>)).Invoke(detour, null);
            }
        }
        catch
        {
            // trampoline 生成失败不影响钉扎本身
        }
        return null;
    }

    // 钉扎期的真实 dspTime（诊断专用）。探针自毁：调用失败一次就置空，
    // 不让坏 trampoline 在静默诊断路径里反复抛异常。
    internal static double RealDsp()
    {
        Func<double> probe = realDspProbe;
        if (probe == null) return double.NaN;
        try
        {
            return probe();
        }
        catch
        {
            realDspProbe = null;
            return double.NaN;
        }
    }

    internal static void Uninstall()
    {
        // dsp detour 撤销失败时保留引用：PinnedDsp 在会话外会走 trampoline 回
        // 原值（探针在则游戏时钟不受影响），下一次 InstallDsp 会先重试撤销。
        if (dspDetour != null)
        {
            bool dspUndone = false;
            try { dspDetour.Dispose(); dspUndone = true; }
            catch (Exception ex)
            {
                Debug.Log("[SpectreReplay] dsp time pin undo failed: " + ex.Message + "（重试一次）");
                try { dspDetour.Dispose(); dspUndone = true; }
                catch (Exception ex2)
                {
                    Debug.LogError("[SpectreReplay] dsp time pin undo keeps failing: " + ex2.Message
                        + "（保留引用以便下次再试）");
                }
            }
            if (dspUndone)
            {
                dspDetour = null;
                realDspProbe = null;
            }
        }
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

    // dspTime 的钉扎体。会话外（detour 泄漏未撤成）且探针可用时回原值，
    // 保证残留 detour 不再把正常游玩的时钟冻死；会话内一律虚拟时钟。
    private static double PinnedDsp()
        => dspDetour != null && realDspProbe != null && !VideoRender.Active
            ? realDspProbe()
            : baseDsp + virtualElapsed;
}
