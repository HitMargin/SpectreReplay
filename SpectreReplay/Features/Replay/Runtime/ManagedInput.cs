using System;
using System.Collections.Generic;
using UnityEngine;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 回放期的「托管输入状态」：由回放数据（KeyEvents）驱动的按住 / 按下 / 抬起。
///
/// 它**是**回放期间游戏输入的唯一来源（见 Patch_RDInput 的覆盖语义），
/// 不是对真实键盘的补充。
///
/// 为什么必须这样：游戏真正消费键盘的是托管输入抽象 <see cref="RDInput"/>——
/// 它的每个 <see cref="RDInputType"/> 最终都落到 <c>Input.GetKey*</c>（需要 OS 级
/// 按键事件）或 <c>AsyncInput.GetKey*</c>（读 SkyHook 静态 mask，只由真实键盘回调写入）。
/// 回放数据一条也写不进去，于是：
///   · <c>mainPressCount</c> 恒 0 ⇒ <c>ValidInputWasTriggered</c> 恒假 ⇒
///     <c>keyTimes</c> 永不灌入；
///   · <c>GetMainHeldKeys</c> 恒空 ⇒ <c>scrPlayer.holdKeys</c> 恒空 ⇒
///     <c>holding</c> 恒假 ⇒ <c>CheckPreHoldFail</c> 把长按判成松手。
///
/// **时序是这套东西的一半。** 游戏在 <c>Simulated_PlayerControl_Update</c> 里读输入
/// （scrPlayer.cs:417/430/433/434），而那个方法由 <c>scrController.PlayerControl_Update</c>
/// 内部调用（:552）。所以记账必须发生在**这两个方法进入之前**（见 Patch_RDInput 的
/// Prefix），否则每帧都是「先读后写」，事件永远晚一帧。
///
/// **「本帧按下」用帧戳而不是清空集合。** 靠清集合的写法在「无待办事件」时
/// 走不到清空（PlayKeyboardEvent 开头就 return），按下状态会一直挂着重复触发。
///
/// 刻意不复用 <see cref="SpectreState.CurrKeyState"/>：那是与录像端共用的采样缓冲区
/// （ReplayRecorder 用 SampleKeyState 直接覆写它），回放侧写它会把录像端状态搅乱。
/// </summary>
internal static class ManagedInput
{
    private static readonly bool[] Held = new bool[256];
    // 帧戳：值为「事件发生在哪一帧」。int.MaxValue = 从未发生。
    private static readonly int[] PressedFrame = new int[256];
    private static readonly int[] ReleasedFrame = new int[256];

    private static int _heldCount;
    private static bool _loggedFirstInjection;

    /// <summary>
    /// 托管输入是否接管游戏读数。**只看是否在回放。**
    ///
    /// 这里绝不能再挂任何「SendInput 是否可用」「平台是不是 Windows」
    /// 「某个开关开没开」的前提：那些判据一旦判错，就会把回放输入整个关掉，
    /// 而玩家看到的只是「回放不动」，没有任何线索指向判断条件。
    /// 去重也不靠它——Patch_RDInput 用覆盖语义，回放期间只认这一份状态。
    ///
    /// 曾经这里接过「模拟按键」开关，那个开关现已删除：它关掉后并非
    /// 「只播不注入」，而是让长条判定失效、键位显示变暗，属于「关掉就坏」的
    /// 假开关。真要停止注入，用 ReplayOn 总开关（它连补丁一起卸）。
    /// </summary>
    internal static bool Active => is_playing;

    /// <summary>
    /// **禁用 SendInput（OS 级按键镜像）。当前为 <c>true</c>。**
    ///
    /// 置为 true 时 <see cref="Mirror"/> 恒假，两条 SendInput 调用
    /// （PumpInput 的按下/抬起、Stop 的释放残留键）都不会执行。
    /// 托管输入覆盖与异步事件注入**不受影响**——它们才是判定来源。
    ///
    /// 关闭它的代价：只读 <c>UnityEngine.Input</c> 的第三方模组看不到回放按键。
    /// 现在按键显示走两条不碰 OS 的路：
    ///   · <see cref="KeyViewerBridge"/>：KeyViewer 系模组（读 KeyInput.GetKey）；
    ///   · <see cref="AsyncInputInjector"/> 的 KeyUpdated 广播：直接订阅 SkyHook 的模组
    ///     （如 JipperResourcePack）。
    ///
    /// 收益：不依赖窗口聚焦、不受 UIPI 拦截、不会把按键泄漏到其它窗口。
    /// 若要恢复镜像，改回 false 即可（判据见 <see cref="Mirror"/>）。
    /// </summary>
    internal const bool DisableSendInput = true;

    /// <summary>
    /// 是否额外把按键镜像到操作系统（SendInput）。
    ///
    /// 这**只影响其它模组的按键显示**，与游戏输入完全无关：游戏读数已经被
    /// 托管状态覆盖，镜像成功或失败都不改变任何判定。
    /// 所以这里可以放心用「窗口聚焦」这种软条件，失败也没有后果。
    /// </summary>
    internal static bool Mirror
        => !DisableSendInput && is_playing && Application.isFocused;

    /// <summary>回放数据里此刻是否还有键按住。</summary>
    internal static bool AnyHeld => _heldCount > 0;

    /// <summary>指定虚拟键码此刻是否处于按住状态。</summary>
    /// <remarks>
    /// 供 <see cref="KeyViewerBridge"/> 把回放按键暴露给按键显示类模组。
    /// 只读、不改任何状态——显示层查询必须是纯查询，否则会和判定链抢同一个状态。
    /// </remarks>
    internal static bool IsHeldVk(ushort vk) => vk < Held.Length && Held[vk];

    private static int _takeoverFrame = -1;
    private static bool _takeover;

    /// <summary>
    /// **原生异步链是否接管读取层。** 为真时 <see cref="Patch_RDInput"/> 的三个
    /// Postfix 必须**放行**，让 <see cref="RDInput"/> 走游戏自己的读数。
    ///
    /// 为什么必须让位：异步链的粒度是**事件 tick**，不是帧。
    /// <c>scrController.UpdateInput</c> 按 tick 分组：每遇到新 tick 先
    /// <c>ProcessKeyInputs(上一组)</c> 再清 <c>keyDownMask</c>/<c>keyUpMask</c>
    /// （scrController.cs:2929-2938），末尾再把最后一组补一次（:2985）。
    /// 也就是说一帧内 <c>Simulated_PlayerControl_Update</c> 可能被调用**多次**，
    /// 每次看到的是**本 tick 组**的掩码。
    ///
    /// 而 <see cref="ManagedInput"/> 的按下判据是 <c>PressedFrame[vk] == frame</c>
    /// ——**帧**粒度。若在原生模式下仍覆盖，同一颗键在这一帧的每个 tick 组里
    /// 都判「本帧刚按下」，一次按键被重复消费（多灌 keyTimes、重复触发命中）。
    ///
    /// 反过来，原生链不可用时（失焦、SkyHook 未开、反射失败）必须由托管状态
    /// 接管，否则游戏读不到任何回放键。
    ///
    /// 缓存到帧：这个判据每帧会被三个 Postfix 各问一次。
    /// </summary>
    internal static bool NativeTakeover
    {
        get
        {
            int frame = Time.frameCount;
            if (_takeoverFrame != frame)
            {
                _takeoverFrame = frame;
                // **注意：这里用的判据与 ReplayPlayer 的 asyncInject 不是同一个。**
                //
                // 两者的分工：
                //   · asyncInject 用 Injectable —— "事件能不能广播出去"，
                //     只要 SkyHook 钩子活着就成立，与游戏状态无关。
                //     广播的消费者不只是游戏（JipperResourcePack 直接订阅 KeyUpdated）。
                //   · 这里用 Usable —— "游戏会不会来出队"，要求 asyncKeyboard.isActive，
                //     即只在 PlayerControl 为真。托管覆盖只在游戏真会读原生链时才让位。
                //
                // 混用会出事：
                //   · 若这里改用 Injectable，非 PlayerControl 下（游戏不出队）覆盖层却让位，
                //     判定改读永远空的 keyMask，回放当场失效；
                //   · 若 asyncInject 改用 Usable，非 PlayerControl 下事件根本不广播，
                //     订阅 KeyUpdated 的键位显示器整段失明。
                //
                // Healthy 不能省：Usable 只是"看起来能用"。若 Inject 实际在失败
                // 而这里仍放行，游戏那条链读到的就是永远空的 keyMask，
                // 而托管覆盖已经让位——两条路一起失效。
                _takeover = is_playing
                    && AsyncInputInjector.Usable
                    && AsyncInputInjector.Healthy;
            }
            return _takeover;
        }
    }

    /// <summary>
    /// 记录一个按键事件。重复的按下 / 重复的抬起都按幂等处理（录音文件里偶发
    /// 连续的 down 或 up 不应让计数漂移，也不应重复刷新本帧帧戳）。
    /// </summary>
    /// <param name="vk">已转换的虚拟键码（<see cref="Settings.key_code_convert"/> 之后）。</param>
    /// <param name="pressed">true 为按下。</param>
    internal static void Note(ushort vk, bool pressed)
    {
        if (vk >= Held.Length) return;
        if (Held[vk] == pressed) return;
        Held[vk] = pressed;
        _heldCount += pressed ? 1 : -1;
        // 兜底：理论上不会为负，但一旦为负，AnyHeld 会永远为假且再也修不回来
        if (_heldCount < 0) _heldCount = 0;

        int frame = Time.frameCount;
        if (pressed) PressedFrame[vk] = frame;
        else ReleasedFrame[vk] = frame;

        // 一次性确认日志：区分「补丁没挂上 / 没走到注入」与「注入了但游戏没反应」。
        // 玩家报「回放不动」时，这一行是唯一能一眼定性的线索。
        if (!_loggedFirstInjection)
        {
            _loggedFirstInjection = true;
            Debug.Log($"[SpectreReplay] 托管输入已接管回放按键（首个键 vk={vk} "
                + (pressed ? "按下" : "抬起") + $"，帧 {frame}）");
        }
    }

    /// <summary>清空状态。开始 / 停止 / 快进（跳转）时调用，避免上一段的按住状态残留。</summary>
    internal static void Clear()
    {
        _takeoverFrame = -1;
        _takeover = false;
        Array.Clear(Held, 0, Held.Length);
        for (int i = 0; i < PressedFrame.Length; i++)
        {
            PressedFrame[i] = int.MaxValue;
            ReleasedFrame[i] = int.MaxValue;
        }
        _heldCount = 0;
        _loggedFirstInjection = false;
    }

    /// <summary>把本帧满足 <paramref name="state"/> 的回放键追加进 <paramref name="keys"/>，返回追加数。</summary>
    internal static int Append(List<AnyKeyCode> keys, ButtonState state)
    {
        if (keys == null) return 0;
        int frame = Time.frameCount;
        int added = 0;
        for (int vk = 0; vk < Held.Length; vk++)
        {
            if (!Matches(state, vk, frame)) continue;
            // **必须是 AsyncKeyCode，不能是 KeyCode。**
            // scrPlayer.ValidInputWasReleased 清理 holdKeys 时按类型分岔
            // （scrPlayer.cs:888-905）：非 AsyncKeyCode 项走
            // <c>value.IndexOf(item)</c>，而 AnyKeyCode 的默认判等是
            // <c>o1 == o2</c>（装箱引用比较，见 AnyKeyCode.cs:80-83）——
            // 对 boxed KeyCode 永远返回 -1，于是**每帧都被判定为松手**：
            // RemoveAt + result = true，长按地板当场 Die。
            // AsyncKeyCode 分支用的是它自己的 <c>operator ==</c>（值比较），
            // 只要 holdKeys 与 GetMainHeldKeys 产出同一种值就能存活。
            keys.Add(new AnyKeyCode(new AsyncKeyCode((ushort)vk)));
            added++;
        }
        return added;
    }

    /// <summary>本帧满足 <paramref name="state"/> 的回放键数量。</summary>
    internal static int Count(ButtonState state)
    {
        int frame = Time.frameCount;
        int count = 0;
        for (int vk = 0; vk < Held.Length; vk++)
            if (Matches(state, vk, frame)) count++;
        return count;
    }

    private static bool Matches(ButtonState state, int vk, int frame)
    {
        switch (state)
        {
            case ButtonState.WentDown: return PressedFrame[vk] == frame;
            case ButtonState.IsDown: return Held[vk];
            case ButtonState.WentUp: return ReleasedFrame[vk] == frame;
            // IsUp 在游戏里的实现是 `!Input.GetKey(key)`，对全部键求反没有意义
            // （会返回几百个"没按的键"），回放不提供。
            default: return false;
        }
    }

    /// <summary>
    /// <paramref name="action"/> 在本帧是否成立。键表取自 <see cref="RDInputType_Keyboard"/>
    /// 的公开静态表——与游戏自己判定 Action1/Action2/Confirm 用的是同一份定义。
    /// </summary>
    internal static bool Matches(InputAction action, ButtonState state)
    {
        int frame = Time.frameCount;
        switch (action)
        {
            case InputAction.Restart: return Any(state, frame, KeyCode.R);
            case InputAction.Cancel:
            case InputAction.Back: return Any(state, frame, KeyCode.Escape);
            case InputAction.Quit: return Any(state, frame, KeyCode.Q);
            case InputAction.Left: return Any(state, frame, KeyCode.LeftArrow);
            case InputAction.Right: return Any(state, frame, KeyCode.RightArrow);
            case InputAction.Up: return Any(state, frame, KeyCode.UpArrow);
            case InputAction.Down: return Any(state, frame, KeyCode.DownArrow);
            case InputAction.LeftAlt: return Any(state, frame, KeyCode.LeftShift);
            case InputAction.RightAlt: return Any(state, frame, KeyCode.RightShift);
            case InputAction.FaceUp: return Any(state, frame, KeyCode.S);
            case InputAction.FaceLeft: return Any(state, frame, KeyCode.A);
            case InputAction.Action1: return Any(state, frame, RDInputType_Keyboard.LeftKeys);
            case InputAction.Action2: return Any(state, frame, RDInputType_Keyboard.RightKeys);
            case InputAction.Confirm: return Any(state, frame, RDInputType_Keyboard.ConfirmKeys);
            // UpAlt / DownAlt：游戏自身恒返回 false（RDInputType_Keyboard.cs:366-374）。
            default: return false;
        }
    }

    /// <summary>
    /// Unity <see cref="KeyCode"/> → 原生虚拟键码。
    ///
    /// **两套编码只在少数键上恰好同值。** <c>KeyCode.Space</c>=32=VK_SPACE、
    /// <c>KeyCode.Escape</c>=27=VK_ESCAPE、<c>KeyCode.Return</c>=13=VK_RETURN 相同，
    /// 但字母与方向键全部错位：<c>KeyCode.Q</c>=113 而 VK_Q=81、
    /// <c>KeyCode.LeftArrow</c>=276 而 VK_LEFT=37、
    /// <c>KeyCode.LeftShift</c>=304 而 VK_LSHIFT=160。
    ///
    /// <see cref="Held"/> 按下标存的是**原生 VK**（ReplayPlayer 写入的是映射后的 VK），
    /// 所以凡是从 <see cref="KeyCode"/> 进来的查询都必须先换算。
    /// 换算失败时退回原值——调用方会因越界返回 false，不会误判成「按住了」。
    /// </summary>
    private static int ToNativeVk(KeyCode k)
    {
        try
        {
            SkyHook.KeyLabel label = SkyHook.SkyHookKeyMapper.UnityKeyToSkyHookKey(k);
            if (label == SkyHook.KeyLabel.Unknown) return (int)k;
            return SkyHook.SkyHookKeyMapper.KeyLabelToNativeKeyCode(label);
        }
        catch
        {
            return (int)k;
        }
    }

    private static bool Any(ButtonState state, int frame, KeyCode vk)
    {
        // **必须换算成原生 VK 再查。** 直接用 (int)vk 会让所有字母键/方向键/
        // 修饰键查错位置：Q 查 113 而事件存在 81，于是 Action1 永远不亮；
        // LeftArrow 查 276 直接越界。只有 Space/Esc/Enter 因为两套编码同值才
        // 一直「碰巧正常」——这正是这类错位最难发现的原因。
        int index = ToNativeVk(vk);
        return index >= 0 && index < Held.Length && Matches(state, index, frame);
    }

    private static bool Any(ButtonState state, int frame, KeyCode[] set)
    {
        // 遍历键表而不是遍历 256 个 VK：键表最多 20 项，且天然跳过表外键
        // （例如 KeypadEnter = 271 超出 256 的数组长度，在 Held 上直接越界）
        for (int i = 0; i < set.Length; i++)
            if (Any(state, frame, set[i])) return true;
        return false;
    }
}