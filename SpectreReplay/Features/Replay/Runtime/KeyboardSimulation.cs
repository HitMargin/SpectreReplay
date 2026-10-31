using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SpectreReplay.Features.Replay;

internal static class KeyboardSimulation
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static volatile bool _dllMissing;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetKeyboardState(byte[] pbKeyState);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    internal static INPUT MakeInput(byte keyCode, bool press, bool isExtended = false)
    {
        uint flags = press ? 0u : KEYEVENTF_KEYUP;
        if (isExtended) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = keyCode,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    /// <summary>
    /// 把当前键盘状态采样进 <paramref name="pbKeyState"/>
    /// （高位 = 按下，与 Win32 GetKeyboardState 同布局）。
    ///
    /// 先直接调 user32；只有它**确实不存在**（DllNotFoundException）才退回
    /// Unity 的 <c>Input.GetKey</c> 逐键查询——那正是游戏自己 RDInputType_Keyboard
    /// 用的同一份数据源，语义一致。
    /// </summary>
    internal static void SampleKeyState(byte[] pbKeyState)
    {
        if (pbKeyState == null) return;
        if (!_dllMissing)
        {
            try
            {
                GetKeyboardState(pbKeyState);
                return;
            }
            catch (DllNotFoundException)
            {
                _dllMissing = true;
            }
            catch (EntryPointNotFoundException)
            {
                _dllMissing = true;
            }
        }
        for (int i = 0; i < pbKeyState.Length; i++)
        {
            bool down;
            try { down = Input.GetKey((KeyCode)i); }
            catch { down = false; }
            pbKeyState[i] = (byte)(down ? 0x80 : 0x00);
        }
    }

    internal static void SendInputs(List<INPUT> inputs)
        => SendInputs(inputs.ToArray());

    internal static void SendInputs(INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        // 非 Windows 上 user32 不存在。**不要用平台判断提前返回**：
        // 那个判断一旦出错，SendInput 会整条静默失效，而这是玩家唯一在用的路径。
        // 只认真实异常，且 latch 起来避免每次按键都抛。
        if (_dllMissing) return;
        uint result;
        try
        {
            result = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }
        catch (DllNotFoundException)
        {
            _dllMissing = true;
            return;
        }
        catch (EntryPointNotFoundException)
        {
            _dllMissing = true;
            return;
        }
        if (result != inputs.Length)
        {
            // 必须带出 Win32 错误码：P/Invoke 已声明 SetLastError = true，但只说
            // "部分失败"对排查毫无帮助。回放按键没打出去最常见的原因是 UIPI 权限
            // 隔离（游戏以管理员身份运行、注入方不是，反之亦然）——此时
            // GetLastError 返回 5 (ERROR_ACCESS_DENIED)，一眼可辨。
            //
            // 注意：UIPI 失败**不**关闭 SendInput。它可能只是这一批被拒；
            // 关掉它等于让"原本能用的"也失效，损失远大于收益。
            int err = Marshal.GetLastWin32Error();
            UnityEngine.Debug.LogWarning($"[SpectreReplay] SendInput 部分失败：已处理 {result}/{inputs.Length} 个按键"
                + $"（Win32 错误码 {err}"
                + (err == 5 ? " = ERROR_ACCESS_DENIED：游戏与注入进程的权限级别不一致，"
                            + "请用相同权限（通常都提权或都不提权）运行游戏" : "")
                + "）");
        }
        // **登记本批实际注入的按键**（部分失败时只记前 result 个——没打出去的
        // 键不会有回声）。这是 Patch_MirrorEchoSuppressor 在钩子回调里认领
        // 「我们自己发的系统按键」的唯一凭据：SendInput 与合成事件注入并存时
        // （MirrorInputToOS 开 + 异步广播在走），OS 级按键会被 SkyHook 的全局
        // 钩子再捕一次，不掐掉就与合成事件一起进 keyQueue——同一颗键判两遍。
        RecordSentKeys(inputs, result);
    }

    // ── 回声登记（写：主线程 SendInputs；读：钩子原生线程的补丁前缀） ──
    //
    // 两个线程之间就这一份交接，每秒至多几十个事件，一把锁足够。
    // SkyHookEvent 没有 extra-info 字段（只有 Time/Type/Label/Key），
    // 回声只能按「键码 + 方向 + 时间窗」认领。

    private struct SentKey
    {
        internal ushort Vk;
        internal bool Pressed;
        internal long Stamp;   // Stopwatch.GetTimestamp()，单调钟
    }

    private static readonly object _sentGate = new();
    private static readonly SentKey[] _sentKeys = new SentKey[512];
    private static int _sentNext;
    private static int _diagSends;

    // 钩子回调通常几毫秒内到达；放宽到 150ms 是给 LL 钩子超时
    // （LowLevelHooksTimeout）留余量。放宽的代价只是「真实按键与镜像键
    // 同码同向且落窗内」被一并吞掉——回放期间玩家的实键不参与判定，可接受。
    private static readonly long SentWindowTicks = (long)(Stopwatch.Frequency * 0.15);

    private static void RecordSentKeys(INPUT[] inputs, uint count)
    {
        if (count == 0) return;
        bool diag = _diagSends < 3;
        if (diag) _diagSends++;
        long now = Stopwatch.GetTimestamp();
        lock (_sentGate)
        {
            for (int i = 0; i < count && i < inputs.Length; i++)
            {
                _sentKeys[_sentNext] = new SentKey
                {
                    Vk = inputs[i].u.ki.wVk,
                    Pressed = (inputs[i].u.ki.dwFlags & KEYEVENTF_KEYUP) == 0,
                    Stamp = now,
                };
                _sentNext = (_sentNext + 1) % _sentKeys.Length;
            }
        }
        if (diag)
        {
            string keys = "";
            for (int i = 0; i < count && i < inputs.Length; i++)
                keys += inputs[i].u.ki.wVk
                    + (((inputs[i].u.ki.dwFlags & KEYEVENTF_KEYUP) == 0) ? "(↓) " : "(↑) ");
            Debug.Log("[SpectreReplay][诊断] SendInput 已发 " + count + " 个: " + keys
                + "（回声认领窗口 150ms）");
        }
    }

    /// <summary>钩子线程查询：这条事件是不是我们刚用 SendInput 发的。
    /// 顺带清理窗口外的旧记录，稳态下数组几乎恒空。</summary>
    internal static bool IsRecentlySentByUs(ushort vk, bool pressed)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_sentGate)
        {
            for (int i = 0; i < _sentKeys.Length; i++)
            {
                ref SentKey s = ref _sentKeys[i];
                if (s.Stamp == 0 || s.Vk != vk || s.Pressed != pressed) continue;
                if (now - s.Stamp > SentWindowTicks)
                {
                    s.Stamp = 0;
                    continue;
                }
                return true;
            }
        }
        return false;
    }

    // ── 诊断（有界）：回声认领失败时的现场还原 ─────────────────
    // 只在镜像回放期间由抑制前缀调用，前 12 次输出钩子事件与环形缓冲
    // 的对照——「回声没拦住」时一次测试就能看清是没到达、还是没认上。

    // 返回值刻意全 ASCII（HardTextCheck 对「方法返回的字符串」按可能上界面
    // 处理）；中文措辞由调用方在 Debug.Log 字面量里拼。
    internal static string DescribeRecent(ushort vk, bool pressed)
    {
        lock (_sentGate)
        {
            long now = Stopwatch.GetTimestamp();
            int inWindow = 0;
            string nearestSameVk = null;
            double nearestAge = double.MaxValue;
            for (int i = 0; i < _sentKeys.Length; i++)
            {
                ref SentKey s = ref _sentKeys[i];
                if (s.Stamp == 0) continue;
                double ms = (now - s.Stamp) * 1000.0 / Stopwatch.Frequency;
                if (ms > 150) continue;
                inWindow++;
                if (s.Vk == vk && ms < nearestAge)
                {
                    nearestAge = ms;
                    nearestSameVk = $"same-vk vk={s.Vk} pressed={s.Pressed} age={ms:F0}ms";
                }
            }
            return nearestSameVk ?? $"window={inWindow} no-vk={vk}";
        }
    }
}