using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 把回放按键作为**合成事件**送进游戏的异步输入管线。
///
/// 为什么是这个位置：开启异步输入时，游戏读键盘的路径是
///   scrConductor.Update → scrController.UpdateInput
///     → 从 <c>AsyncInputManager.keyQueue</c>（ConcurrentQueue&lt;SkyHookEvent&gt;）出队
///     → 逐个写 keyMask / keyDownMask / keyUpMask
///     → ProcessKeyInputs(eventTick) → scrPlayer.Simulated_PlayerControl_Update(tick)
/// 也就是说，**只要事件进了这个队列，游戏就把它当成真实按键**：判定、命中、
/// keyTimes、holdKeys 全部由游戏自己的代码算。这比在 RDInput 上覆盖返回值更接近
/// "真的按了一下"——后者只是让查询返回真，跳过了整条时间戳换算链。
///
/// 时间戳必须换算成 SkyHook 的绝对 tick（DateTime.Ticks 域），换算公式是
/// AsyncKeyRecorder（录制侧）的严格逆运算：
///   录制：songPos = pitch * (dsp - dspTimeSong - calibration_i) - addoffset
///         dsp     = (evTicks + dspNowTicks - nowTicks) / 1e7
///   注入：dsp     = (songPos + addoffset) / pitch + dspTimeSong + calibration_i
///         evTicks = dsp * 1e7 - dspNowTicks + nowTicks
/// 两边同一套基准，回放出来才是"同一次按键"，而不是差半个音频缓冲的近似。
///
/// SkyHookEvent 的字段是 public readonly、且没有公开构造器（XInput 那套 native
/// 结构只能由 DLL 填）。这里用反射在**装箱副本**上写字段——值类型装箱后写入是
/// 允许的，取回时是完整的结构。事件频率只有每秒几个，反射成本可以忽略。
/// </summary>
internal static class AsyncInputInjector
{
    // SkyHookEvent.EpochTicks = new DateTime(1970,1,1).Ticks，源码里是 private static readonly
    private static readonly long EpochTicks = new DateTime(1970, 1, 1).Ticks;

    private static bool _resolved;
    private static FieldInfo _fTimeSec;
    private static FieldInfo _fTimeSubsecNano;
    private static FieldInfo _fType;
    private static FieldInfo _fLabel;
    private static FieldInfo _fKey;
    private static MethodInfo _nativeToLabel;

    // 最近一次**实际入队失败**的帧号。-1 表示从没失败过。
    private static int _lastFailFrame = -1;

    internal static bool Healthy
    {
        get
        {
            if (_lastFailFrame < 0) return true;
            return Time.frameCount - _lastFailFrame > 1;
        }
    }

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        try
        {
            Type t = typeof(SkyHook.SkyHookEvent);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _fTimeSec = t.GetField("TimeSec", flags);
            _fTimeSubsecNano = t.GetField("TimeSubsecNano", flags);
            _fType = t.GetField("Type", flags);
            _fLabel = t.GetField("Label", flags);
            _fKey = t.GetField("Key", flags);
            _nativeToLabel = typeof(SkyHook.SkyHookKeyMapper)
                .GetMethod("NativeKeyCodeToKeyLabel", BindingFlags.Public | BindingFlags.Static);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] 异步事件注入初始化失败（将退回同步路径）: " + ex.Message);
        }
    }

    internal static bool Injectable
    {
        get
        {
            Resolve();
            if (_fTimeSec == null || _fTimeSubsecNano == null || _fType == null
                || _fLabel == null || _fKey == null)
                return false;
            if (!Application.isFocused) return false;

            try { return SkyHook.SkyHookManager.Instance.isHookActive; }
            catch { return false; }
        }
    }

    /// <summary>
    /// 游戏**确实会来出队**：asyncKeyboard.isActive 为真。
    ///
    /// 只用于「是否让位给原生链」的判断（<see cref="ManagedInput.NativeTakeover"/>）。
    /// 广播能力见 <see cref="Injectable"/>——两者不同，混用会让键位显示器在
    /// 非 PlayerControl 状态整段失明。
    /// </summary>
    internal static bool Usable
    {
        get
        {
            Resolve();
            if (_fTimeSec == null || _fTimeSubsecNano == null || _fType == null
                || _fLabel == null || _fKey == null)
                return false;
            if (!Application.isFocused) return false;

            // 三项或：与 UpdateInput 的早退条件严格对齐，任一为真游戏就会来出队
            try
            {
                RDInputType_AsyncKeyboard main = RDInput.asyncKeyboard;
                if (main != null && main.isActive) return true;
                RDInputType_AsyncKeyboard left = RDInput.asyncKeyboardLeft;
                if (left != null && left.isActive) return true;
                RDInputType_AsyncKeyboard right = RDInput.asyncKeyboardRight;
                if (right != null && right.isActive) return true;
            }
            catch { }
            return false;
        }
    }

    /// <summary>
    /// 把一个回放按键作为合成事件入队。
    /// </summary>
    /// <param name="vk">已转换的虚拟键码，同时也是 SkyHook 记录的 native key。</param>
    /// <param name="pressed">true = 按下。</param>
    /// <param name="songPosition">这个事件应当落在的歌曲时间（秒）。</param>
    internal static bool Inject(ushort vk, bool pressed, double songPosition)
    {
        Resolve();
        if (_fTimeSec == null || _fType == null || _fLabel == null || _fKey == null) return Fail();

        scrConductor conductor = ADOBase.conductor;
        if (conductor == null || conductor.song == null) return Fail();
        if (SkyHook.SkyHookManager.KeyUpdated == null) return Fail();

        try
        {
            double pitch = conductor.song.pitch;
            if (Math.Abs(pitch) < 1e-6) return Fail();

            // dspTimeSong 只在歌曲真正排定播放后才被赋值（scrConductor.cs:668/938/956）。
            // 倒计时 / 检查点 / 转场阶段它仍是 0：此时 dsp 退化成一个近似"音频系统
            // 启动时刻"的常量，evTicks 落在遥远的过去。而 scrController.UpdateInput
            // 会按 tick 逐个 ProcessKeyInputs（scrController.cs:2929-2935），于是这批
            // 陈旧事件在同一帧里把整段历史重新模拟一遍——就是"播放一瞬间全部模拟完"。
            if (conductor.dspTimeSong <= 0.0) return Fail();

            // 与录制侧严格互逆（见类注释）
            double dsp = (songPosition + conductor.addoffset) / pitch
                         + conductor.dspTimeSong + scrConductor.calibration_i;
            long dspNowTicks = (long)(AudioSettings.dspTime * 10000000.0);
            long nowTicks = DateTime.Now.Ticks;
            long evTicks = (long)(dsp * 10000000.0) - dspNowTicks + nowTicks;

            // 兜底窗口：事件本来就该"此刻"发生（调用方只喂 SongPosition <= 当前歌曲
            // 时间的按键），所以 dsp 与 AudioSettings.dspTime 的差应当接近 0。任何基准
            // 错乱（dspTimeSong 陈旧、pitch 反号、addoffset 还没就绪）都会把它推开几十
            // 秒。偏离超过 2 秒就说明换算不可信——宁可不入队交给托管层兜底，也不能把
            // 陈旧 tick 灌进 UpdateInput 触发整段重放。
            if (Math.Abs(evTicks - nowTicks) > 20000000L) return Fail();

            long rel = evTicks - EpochTicks;
            if (rel < 0) rel = 0;

            object box = Activator.CreateInstance(typeof(SkyHook.SkyHookEvent));
            _fTimeSec.SetValue(box, rel / 10000000L);
            _fTimeSubsecNano.SetValue(box, (uint)((rel % 10000000L) * 100L));
            _fType.SetValue(box, pressed
                ? SkyHook.EventType.KeyPressed
                : SkyHook.EventType.KeyReleased);
            _fKey.SetValue(box, vk);

            // label 只参与 "这个键算不算主键" 的筛选（RDInputType_AsyncKeyboard.Main
            // 里 Array.IndexOf(mainKeys, item.label)），KeyboardFull 收全部 label；
            // 映射失败时用 Unknown 也不影响 keyMask 本身。
            SkyHook.KeyLabel label = SkyHook.KeyLabel.Unknown;
            if (_nativeToLabel != null)
            {
                try
                {
                    object r = _nativeToLabel.Invoke(null, new object[] { vk });
                    if (r is SkyHook.KeyLabel l) label = l;
                }
                catch { }
            }
            _fLabel.SetValue(box, label);

            // **走真实钩子的同一入口 KeyUpdated.Invoke，不能直接 keyQueue.Enqueue。**
            //
            // 那个队列是 AsyncInputManager.Setup() 在 KeyUpdated 的监听器里写入的
            // （AsyncInputManager.cs:121-127）。直接塞队列等于绕过广播：
            // 游戏自己的消费者仍能收到，但**所有订阅 KeyUpdated 的模组**
            // （键位显示器、统计、录像等）永远看不到这些合成按键。
            //
            // ADOFAIMacro 的 VirtualAsyncInput 用的就是这一行（:137），注释写得
            // 很清楚：「走真实钩子的同一入口……订阅 KeyUpdated 的其他模组也能看到虚拟按键」。
            SkyHook.SkyHookManager.KeyUpdated.Invoke((SkyHook.SkyHookEvent)box);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] 异步事件入队失败: " + ex.Message);
            return Fail();
        }
    }

    /// <summary>
    /// 记一次入队失败并返回 false。任何一条「本来该成功却没能入队」的分支都要走这里，
    /// 否则 <see cref="Healthy"/> 会把失败的这一帧当成正常，托管覆盖层继续放行，
    /// 游戏那条链却一颗键都看不到。
    /// </summary>
    private static bool Fail()
    {
        _lastFailFrame = Time.frameCount;
        return false;
    }
}