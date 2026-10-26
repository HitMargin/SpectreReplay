using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace SpectreReplay.Features.Cloud
{
    /// <summary>
    /// Steam 云（ISteamRemoteStorage）直连封装。
    ///
    /// **为什么直接 P/Invoke 游戏自带的 steam_api64.dll**：mod 跑在游戏进程里，
    /// 游戏已经用 Facepunch.Steamworks 初始化过 SteamAPI（appid 977950）。
    /// 再调一次 SteamAPI_Init 是错的（不可能重入），但 flat API 的接口访问器
    /// <c>SteamAPI_SteamRemoteStorage_v016()</c> 读的是**同一个已初始化的上下文**，
    /// 拿到指针直接调即可。实测（2026-10-26，tools/SteamCloudProbe）：
    /// FileWrite / FileRead / FileExists / GetFileSize / FileDelete / GetFileCount
    /// 全部可用；写入立刻落到本地云缓存（userdata/&lt;账号&gt;/977950/remote/），
    /// **真正上传由 Steam 客户端在游戏退出时批量完成**。
    ///
    /// **字符串一律手工按 UTF-8 编组**：DllImport 默认的 LPStr 在中文 Windows 上
    /// 是 ANSI(CP936)，实测带中文的谱名直接 FileWrite 返回 false（一个字都写不进去）。
    /// 所以这里不声明 string 参数，全部 IntPtr + Encoding.UTF8。同理，读回来的
    /// 文件名也必须按 UTF-8 解，不能用 PtrToStringAnsi。
    ///
    /// **安全前提**：拿不到接口指针（Steam 没运行 / 非 Steam 版 / API 未初始化）
    /// 时一律返回不可用，绝不回调一个空指针过去——那会直接崩掉游戏进程。
    /// </summary>
    internal static class SteamCloud
    {
        /// <summary>云端命名空间（与游戏自身的 User/*.sav Auto-Cloud 完全分开）。</summary>
        internal const string RemoteRoot = "SpectreReplay";

        /// <summary>回放文件（.sprp / .psprp 及其配套 .wav）所在的云端目录。</summary>
        internal const string RemoteReplayDir = RemoteRoot + "/Replays";

        /// <summary>Steam 云单文件上限（k_unMaxCloudFileChunkSize = 100 MiB）。</summary>
        internal const int MaxFileBytes = 100 * 1024 * 1024;

        private const string Dll = "steam_api64";

        /// <summary>云端目录里的一项。</summary>
        internal struct Entry
        {
            internal string Name;      // 完整云端路径，如 SpectreReplay/Replays/xxx.sprp
            internal int Size;         // 字节
            internal uint Timestamp;   // Steam 记录的 RTime32
        }

        // ── steam_api64 flat API（v016，导出名由游戏 DLL 实测确认）──────────

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SteamAPI_SteamRemoteStorage_v016();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_ISteamRemoteStorage_FileWrite(
            IntPtr self, IntPtr pchFile, byte[] pvData, int cubData);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SteamAPI_ISteamRemoteStorage_FileRead(
            IntPtr self, IntPtr pchFile, byte[] pvData, int cubDataToRead);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_ISteamRemoteStorage_FileExists(
            IntPtr self, IntPtr pchFile);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SteamAPI_ISteamRemoteStorage_GetFileSize(
            IntPtr self, IntPtr pchFile);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_ISteamRemoteStorage_FileDelete(
            IntPtr self, IntPtr pchFile);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SteamAPI_ISteamRemoteStorage_GetFileCount(IntPtr self);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SteamAPI_ISteamRemoteStorage_GetFileNameAndSize(
            IntPtr self, int index, out int pnFileSizeInBytes);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint SteamAPI_ISteamRemoteStorage_GetFileTimestamp(
            IntPtr self, IntPtr pchFile);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_ISteamRemoteStorage_IsCloudEnabledForApp(IntPtr self);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_ISteamRemoteStorage_IsCloudEnabledForAccount(IntPtr self);

        // ── 可用性探测（只做一次，失败原因留给 UI 显示）───────────────

        private static IntPtr _iface;
        private static string _reason = "";
        /// <summary>下一次允许重试探测的时刻（TickCount）。见 <see cref="Ensure"/>。</summary>
        private static int _nextProbeTick = int.MinValue;
        private static bool _readyLogged;

        internal static bool Ready
        {
            get { Ensure(); return _iface != IntPtr.Zero; }
        }

        /// <summary>不可用时的原因键（可直接给 UI 用，不含玩家可见文案）。</summary>
        internal static string Reason
        {
            get { Ensure(); return _reason; }
        }

        /// <summary>该 app 的云同步在客户端里是否开启（关闭时写入会成功但永不上传）。</summary>
        internal static bool AppCloudEnabled
        {
            get
            {
                Ensure();
                if (_iface == IntPtr.Zero) return false;
                try { return SteamAPI_ISteamRemoteStorage_IsCloudEnabledForApp(_iface); }
                catch (Exception) { return false; }
            }
        }

        /// <summary>
        /// 探测接口指针。
        ///
        /// **"未初始化"必须可重试，不能一锤定音。** 模组的 OnToggle（本类第一次
        /// 被调用）发生在游戏 "Startup: Initialize Steam SDK" **之前**——那时
        /// SteamAPI 还没初始化，访问器返回空指针。把这次失败当成终态缓存起来，
        /// 自动回填就永远不会发生，而且日志里一个字都没有（实测踩到过）。
        /// 所以只有"确定不会变"的失败（DLL 不存在 / 接口版本不对 / 位数不符）
        /// 才是终态；"还没初始化"每隔 1 秒重试一次。
        /// </summary>
        private static void Ensure()
        {
            if (_iface != IntPtr.Zero) return;
            if (_reason == "no_dll" || _reason == "old_sdk" || _reason == "bad_arch") return;
            if (_nextProbeTick != int.MinValue
                && (uint)(Environment.TickCount - _nextProbeTick) < 1000u)
            {
                return;   // 节流：OnGUI 每帧都会问一次
            }
            _nextProbeTick = Environment.TickCount;

            try
            {
                IntPtr p = SteamAPI_SteamRemoteStorage_v016();
                if (p == IntPtr.Zero)
                {
                    _reason = "not_initialized";
                    return;
                }
                _iface = p;
                _reason = "";
                if (!_readyLogged)
                {
                    _readyLogged = true;
                    bool app = false;
                    try { app = SteamAPI_ISteamRemoteStorage_IsCloudEnabledForApp(_iface); }
                    catch (Exception) { }
                    Debug.Log("[SpectreReplay] Steam 云接口已就绪（本游戏云同步："
                        + (app ? "开启" : "关闭") + "）");
                }
            }
            catch (DllNotFoundException)
            {
                // 非 Steam 版 / 插件目录里没有 steam_api64.dll
                _reason = "no_dll";
                Debug.Log("[SpectreReplay] 找不到 steam_api64.dll（非 Steam 版？），云同步不可用");
            }
            catch (EntryPointNotFoundException)
            {
                // 极旧的 SDK：没有 v016 访问器
                _reason = "old_sdk";
                Debug.LogWarning("[SpectreReplay] steam_api64.dll 没有 RemoteStorage v016 接口，云同步不可用");
            }
            catch (BadImageFormatException)
            {
                _reason = "bad_arch";
                Debug.LogWarning("[SpectreReplay] steam_api64.dll 位数不匹配，云同步不可用");
            }
            catch (Exception ex)
            {
                _reason = "error";
                Debug.LogWarning("[SpectreReplay] Steam 云探测失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ── UTF-8 编组 ──────────────────────────────────────────

        private static IntPtr ToUtf8(string s)
        {
            if (s == null) return IntPtr.Zero;
            byte[] b = Encoding.UTF8.GetBytes(s);
            IntPtr p = Marshal.AllocHGlobal(b.Length + 1);
            if (b.Length > 0) Marshal.Copy(b, 0, p, b.Length);
            Marshal.WriteByte(p, b.Length, 0);
            return p;
        }

        private static string FromUtf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return "";
            int len = 0;
            // 云端文件名最长不过几百字节，这里不加长度上限也不会走太远；
            // 真出现没有终止符的坏指针，先挡住 4096 再放弃，避免越界读崩进程。
            while (len < 4096 && Marshal.ReadByte(p, len) != 0) len++;
            if (len == 0) return "";
            byte[] b = new byte[len];
            Marshal.Copy(p, b, 0, len);
            return Encoding.UTF8.GetString(b);
        }

        // ── 文件操作 ────────────────────────────────────────────

        /// <summary>写一个云端文件。返回 false 时 <paramref name="error"/> 说明原因。</summary>
        internal static bool Upload(string remoteName, byte[] data, out string error)
        {
            error = "";
            Ensure();
            if (_iface == IntPtr.Zero) { error = "not_available"; return false; }
            if (string.IsNullOrEmpty(remoteName)) { error = "empty_name"; return false; }
            if (data == null) data = new byte[0];
            if (data.Length > MaxFileBytes) { error = "too_large"; return false; }

            IntPtr name = IntPtr.Zero;
            try
            {
                name = ToUtf8(remoteName);
                // FileWrite 的 cubData 是 int；空文件也要给一个合法指针
                byte[] payload = data.Length == 0 ? new byte[1] : data;
                if (!SteamAPI_ISteamRemoteStorage_FileWrite(_iface, name, payload, data.Length))
                {
                    error = "write_failed";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
                return false;
            }
            finally
            {
                if (name != IntPtr.Zero) Marshal.FreeHGlobal(name);
            }
        }

        /// <summary>从本地文件上传。</summary>
        internal static bool UploadFile(string remoteName, string localPath, out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrEmpty(localPath) || !System.IO.File.Exists(localPath))
                {
                    error = "missing_local";
                    return false;
                }
                var info = new System.IO.FileInfo(localPath);
                if (info.Length > MaxFileBytes) { error = "too_large"; return false; }
                byte[] bytes = System.IO.File.ReadAllBytes(localPath);
                return Upload(remoteName, bytes, out error);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
                return false;
            }
        }

        /// <summary>读一个云端文件。成功时 <paramref name="data"/> 为完整内容。</summary>
        internal static bool Download(string remoteName, out byte[] data, out string error)
        {
            data = null;
            error = "";
            Ensure();
            if (_iface == IntPtr.Zero) { error = "not_available"; return false; }

            IntPtr name = IntPtr.Zero;
            try
            {
                name = ToUtf8(remoteName);
                int size = SteamAPI_ISteamRemoteStorage_GetFileSize(_iface, name);
                if (size < 0) { error = "not_found"; return false; }
                if (size > MaxFileBytes) { error = "too_large"; return false; }
                if (size == 0) { data = new byte[0]; return true; }

                var buf = new byte[size];
                int read = SteamAPI_ISteamRemoteStorage_FileRead(_iface, name, buf, size);
                if (read != size)
                {
                    error = "short_read";
                    return false;
                }
                data = buf;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
                return false;
            }
            finally
            {
                if (name != IntPtr.Zero) Marshal.FreeHGlobal(name);
            }
        }

        internal static bool Exists(string remoteName)
        {
            Ensure();
            if (_iface == IntPtr.Zero) return false;
            IntPtr name = IntPtr.Zero;
            try
            {
                name = ToUtf8(remoteName);
                return SteamAPI_ISteamRemoteStorage_FileExists(_iface, name);
            }
            catch (Exception) { return false; }
            finally
            {
                if (name != IntPtr.Zero) Marshal.FreeHGlobal(name);
            }
        }

        internal static bool Delete(string remoteName, out string error)
        {
            error = "";
            Ensure();
            if (_iface == IntPtr.Zero) { error = "not_available"; return false; }
            IntPtr name = IntPtr.Zero;
            try
            {
                name = ToUtf8(remoteName);
                if (!SteamAPI_ISteamRemoteStorage_FileDelete(_iface, name))
                {
                    error = "delete_failed";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
                return false;
            }
            finally
            {
                if (name != IntPtr.Zero) Marshal.FreeHGlobal(name);
            }
        }

        /// <summary>
        /// 列出云端文件（可按前缀过滤，传 null/空 = 全部）。
        /// 注意：Auto-Cloud 跟随的 User/*.sav 不在这个 API 视图里，只能看到
        /// 通过本 API 写进去的文件——这正是我们要的隔离。
        /// </summary>
        internal static List<Entry> List(string prefix)
        {
            var result = new List<Entry>();
            Ensure();
            if (_iface == IntPtr.Zero) return result;

            try
            {
                int count = SteamAPI_ISteamRemoteStorage_GetFileCount(_iface);
                for (int i = 0; i < count; i++)
                {
                    int size;
                    IntPtr p = SteamAPI_ISteamRemoteStorage_GetFileNameAndSize(_iface, i, out size);
                    string full = FromUtf8(p);
                    if (string.IsNullOrEmpty(full)) continue;
                    if (!string.IsNullOrEmpty(prefix)
                        && !full.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var e = new Entry { Name = full, Size = size, Timestamp = 0 };
                    try
                    {
                        IntPtr np = ToUtf8(full);
                        try { e.Timestamp = SteamAPI_ISteamRemoteStorage_GetFileTimestamp(_iface, np); }
                        finally { if (np != IntPtr.Zero) Marshal.FreeHGlobal(np); }
                    }
                    catch (Exception) { }
                    result.Add(e);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 枚举 Steam 云端文件失败：" + ex.Message);
            }
            return result;
        }
    }
}
