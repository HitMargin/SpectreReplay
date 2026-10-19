using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SpectreReplay.Features.Replay;

internal static class KeyboardSimulation
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

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

    internal static void SendInputs(List<INPUT> inputs)
        => SendInputs(inputs.ToArray());

    internal static void SendInputs(INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        uint result = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (result != inputs.Length)
        {
            // 必须带出 Win32 错误码：P/Invoke 已声明 SetLastError = true，但只说
            // "部分失败"对排查毫无帮助。回放按键没打出去最常见的原因是 UIPI 权限
            // 隔离（游戏以管理员身份运行、注入方不是，反之亦然）——此时
            // GetLastError 返回 5 (ERROR_ACCESS_DENIED)，一眼可辨。
            int err = Marshal.GetLastWin32Error();
            Debug.LogWarning($"[SpectreReplay] SendInput 部分失败：已处理 {result}/{inputs.Length} 个按键"
                + $"（Win32 错误码 {err}"
                + (err == 5 ? " = ERROR_ACCESS_DENIED：游戏与注入进程的权限级别不一致，"
                            + "请用相同权限（通常都提权或都不提权）运行游戏" : "")
                + "）");
        }
    }
}
