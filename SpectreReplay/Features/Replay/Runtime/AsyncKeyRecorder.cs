using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using SkyHook;
using UnityEngine;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Features.Replay;

// 异步输入录制：在 SkyHook 事件线程上捕获原始按键，主线程每帧统一换算成歌曲时间。
// 换算必须推迟到主线程：dspTimeSong / addoffset 等导体状态只在主线程更新，
// 事件线程直接读会拿到撕裂的基准。KeyEvent 结构保持不变，旧回放文件照常播放。
internal static class AsyncKeyRecorder
{
    private static readonly ConcurrentQueue<SkyHookEvent> Pending = new();

    // 帧内复用缓冲 + 缓存的比较器：事件本身按到达序排队（SkyHook 单线程回调），
    // 排序只是防时钟乱序的保险，且避免每帧分配
    private static readonly List<SkyHookEvent> FrameBuffer = new();
    private static readonly Comparison<SkyHookEvent> ByEventTick = (a, b)
        => a.GetTimeInTicks().CompareTo(b.GetTimeInTicks());

    // SkyHookManager.KeyUpdated 回调（事件线程）
    internal static void OnKeyUpdated(SkyHookEvent keyEvent)
    {
        // 鼠标不参与键盘回放
        if (keyEvent.Label == KeyLabel.MouseLeft || keyEvent.Label == KeyLabel.MouseMiddle
            || keyEvent.Label == KeyLabel.MouseRight || keyEvent.Label == KeyLabel.MouseX1
            || keyEvent.Label == KeyLabel.MouseX2)
            return;
        Pending.Enqueue(keyEvent);
    }

    // 不录制期间持续丢弃，避免积压的旧事件在下一次录制开始时混入
    internal static void DiscardPending()
    {
        while (Pending.TryDequeue(out _)) { }
    }

    // UpdateInput 的 Prefix 每帧调用（主线程）
    internal static void FlushFrame()
    {
        if (!is_recording || !Settings.GetDataFromAsyncInput
            || !Application.isFocused || !AsyncInputManager.isActive)
        {
            DiscardPending();
            return;
        }
        if (!Pending.TryPeek(out _))
            return;

        // **取基准之前必须确认导体可用。** 换算式的每一项都挂在导体上，而
        // scrController.UpdateInput 在结算转场/场景卸载期间仍会跑，此时
        // ADOBase.conductor 可能已经销毁、或 song 尚未就绪。
        //
        // 这个判断的位置很关键：必须放在**出队之前**。原实现先 while 把 Pending
        // 全部搬进 FrameBuffer、再在循环体里解引用，异常被外层补丁的 catch 吞掉
        // ——事件已经从队列里拿走了，于是那一批按键彻底丢失，而玩家只看到
        // "我明明按了却没录上"。这里提前返回则事件留在队列里，下一帧导体就绪后
        // 照常换算（SkyHook 时间戳是绝对 tick，晚一帧换算不影响精度）。
        scrConductor conductor = ADOBase.conductor;
        if (conductor == null || conductor.song == null)
            return;

        // SkyHook 时间戳是绝对 tick，与基准何时取样无关；一帧只取一对基准，
        // dspTime 与系统时钟取得越近换算越准
        long nowTicks = DateTime.Now.Ticks;
        long dspNowTicks = (long)(AudioSettings.dspTime * 10000000.0);

        FrameBuffer.Clear();
        while (Pending.TryDequeue(out SkyHookEvent ev))
            FrameBuffer.Add(ev);
        FrameBuffer.Sort(ByEventTick);

        foreach (SkyHookEvent ev in FrameBuffer)
        {
            // 本模组热键不录（原因见 ManagedInput.IsModHotkey）：它们会在回放时
            // 被当成玩法输入，导致非编辑器关卡在结算画面自动退出。
            if (ManagedInput.IsModHotkey(ev.Key))
                continue;
            double dsp = ((long)ev.GetTimeInTicks() + dspNowTicks - nowTicks) / 10000000.0;
            double songPos = conductor.song.pitch * (dsp - conductor.dspTimeSong - scrConductor.calibration_i)
                            - conductor.addoffset;
            data.KeyEvents.Add(new KeyEvent
            {
                KeyCode = ev.Key,
                IsPressed = ev.Type == SkyHook.EventType.KeyPressed,
                SongPosition = songPos
            });
        }
    }
}
