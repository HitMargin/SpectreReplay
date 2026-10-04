using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SpectreReplay.Features.Playback
{
    /// <summary>
    /// Win32 键盘输入模拟（照 Spectre 的 Features/Replay/KeyboardSimulation.cs）。
    ///
    /// **这是回放能不能工作的分水岭。**
    ///
    /// 我原先把回放事件塞进 <c>AsyncInputManager.keyQueue</c>，那是错的：
    /// scrController.cs:2885 有一道门——
    /// <code>
    /// if (!RDInput.asyncKeyboard.isActive &amp;&amp; !...Left... &amp;&amp; !...Right...)
    ///     return;
    /// </code>
    /// 而 <c>asyncKeyboard.isActive</c> 只有**玩家自己在游戏设置里开了异步输入**才为真
    /// （AsyncInputManager.Update:147）。玩家用默认输入时它永远是 false，
    /// UpdateInput 第 2885 行直接 return——我塞进队列的按键从头到尾没人读。
    /// 现象就是"点了回放，完全没反应"。
    ///
    /// 改成往 Windows 注入真实按键，游戏侧的 SkyHook 驱动会像真人输入一样
    /// 收到事件，走完全相同的判定链路。**不再依赖玩家是否开了异步输入。**
    /// </summary>
    internal static class KeyboardSimulation
    {
        private const uint InputKeyboard = 1;
        private const uint KeyeventfExtendedkey = 0x0001;
        private const uint KeyeventfKeyup = 0x0002;

        /// <summary>
        /// 需要 KEYEVENTF_EXTENDEDKEY 标记的键。
        /// 照 Spectre（SpectreState.cs:205）——这些键（方向键、Insert 等）
        /// 的扫描码大于 0xFF，不带标记会被当成主键区的键。
        /// </summary>
        private static readonly ushort[] ExtendedKeys = { 165, 163, 38, 40, 37, 39 };

        /// <summary>
        /// 不参与回放的键：Shift / Ctrl / Alt / Esc。
        /// 照 Spectre（SpectreState.cs:207）——回放时按住修饰键会与玩家
        /// 自己的按键叠加，而且 Esc 会被游戏当成暂停。
        /// </summary>
        private static readonly ushort[] BlockedKeys = { 16, 17, 18, 27 };

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        private static readonly List<INPUT> Buffer = new List<INPUT>(32);

        /// <summary>上一帧结束时仍处于按下状态的键，用于停止时补齐松开。</summary>
        private static readonly bool[] Held = new bool[256];

        internal static bool IsExtendedKey(ushort keyCode)
            => Array.IndexOf(ExtendedKeys, keyCode) >= 0;

        internal static bool IsBlockedKey(ushort keyCode)
            => Array.IndexOf(BlockedKeys, keyCode) >= 0;

        /// <summary>
        /// Unity 的 KeyCode → Win32 虚拟键码。
        ///
        /// Unity 的 KeyCode 枚举本身就是按 Win32 虚拟键码排的（LetterA=97…KeypadEnter=13），
        /// 直接当 VK 用即可——Spectre 的 key_code_convert 默认也是恒等映射
        /// （Settings.cs:357）。越界或 0 的跳过。
        /// </summary>
        private static bool ToVirtual(ushort unityKey, out byte vk)
        {
            vk = 0;
            if (unityKey == 0 || unityKey > 255)
            {
                return false;
            }
            if (IsBlockedKey(unityKey))
            {
                return false;
            }
            vk = (byte)unityKey;
            return true;
        }

        /// <summary>把一批按键注入系统。</summary>
        internal static void Send(List<INPUT> inputs)
        {
            if (inputs == null || inputs.Count == 0)
            {
                return;
            }
            INPUT[] arr = inputs.ToArray();
            uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT)));
            if (sent != arr.Length)
            {
                // 必须带出 Win32 错误码，否则"部分失败"对排查毫无帮助。
                // 最常见的成因是 UIPI 权限隔离（游戏提权运行、注入方没提权，或反过来），
                // 此时 GetLastError 返回 5 = ERROR_ACCESS_DENIED，一眼可辨。
                int err = Marshal.GetLastWin32Error();
                Debug.LogWarning("[SpectreReplay] SendInput 部分失败：已处理 "
                    + sent + "/" + arr.Length + " 个按键（Win32 错误码 " + err
                    + (err == 5 ? " = ERROR_ACCESS_DENIED：游戏与模组权限级别不一致，"
                                  + "请用相同权限运行游戏" : "")
                    + "）");
            }
        }

        /// <summary>
        /// 注入一条按键并更新持有状态。
        /// 不传 INPUT：只更新 Held，由调用方批量 Send。
        /// </summary>
        internal static void Stage(ushort unityKey, bool pressed, List<INPUT> buffer)
        {
            if (!ToVirtual(unityKey, out byte vk))
            {
                return;
            }
            uint flags = pressed ? 0u : KeyeventfKeyup;
            if (IsExtendedKey(unityKey))
            {
                flags |= KeyeventfExtendedkey;
            }
            buffer.Add(new INPUT
            {
                type = InputKeyboard,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    }
                }
            });
            Held[vk] = pressed;
        }

        /// <summary>
        /// 补发所有"仍按着"的松开事件。
        ///
        /// 回放中途停止/切场景时，录制里配对的松开事件可能还没轮到。
        /// 不补的话那个键会**一直卡在按下状态**——玩家的下一个物理按键
        /// 只会被当成一次松开，判定彻底错乱。这是回放最阴险的后遗症。
        /// </summary>
        internal static void ReleaseAll()
        {
            Buffer.Clear();
            for (int i = 0; i < Held.Length; i++)
            {
                if (!Held[i])
                {
                    continue;
                }
                ushort key = (ushort)i;
                Buffer.Add(new INPUT
                {
                    type = InputKeyboard,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = (ushort)i,
                            wScan = 0,
                            dwFlags = KeyeventfKeyup
                                | (IsExtendedKey(key) ? KeyeventfExtendedkey : 0u),
                            time = 0,
                            dwExtraInfo = IntPtr.Zero,
                        }
                    }
                });
                Held[i] = false;
            }
            Send(Buffer);
            Buffer.Clear();
        }

        /// <summary>清掉状态但不注入（卸载模组时用）。</summary>
        internal static void Forget()
        {
            for (int i = 0; i < Held.Length; i++)
            {
                Held[i] = false;
            }
        }
    }
}