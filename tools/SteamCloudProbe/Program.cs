// Steam Cloud 探针 —— 只做一件事：在 ADOFAI 的 appid 上下文里走一遍
// Remote Storage API 的最短闭环（写 → 查 → 读 → 配额 → 列表），
// 验证“mod 借用游戏云端”的可行性。成功输出全部 PASS。

using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    // ---- steam_api64.dll flat API（全部为 C 导出函数，x64）----
    // 版本依据：游戏插件目录 steam_api64.dll 内导出
    // SteamAPI_SteamRemoteStorage_v016 / STEAMREMOTESTORAGE_INTERFACE_VERSION016。

    // 新版 SDK 已删除 SteamAPI_Init，改用 SteamAPI_InitSafe / SteamAPI_InitFlat
    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_InitSafe();

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SteamAPI_Shutdown();

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_SteamRemoteStorage_v016();

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamRemoteStorage_FileWrite(
        IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string pchFile, byte[] pvData, int cubData);

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamRemoteStorage_FileExists(
        IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string pchFile);

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SteamAPI_ISteamRemoteStorage_GetFileSize(
        IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string pchFile);

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SteamAPI_ISteamRemoteStorage_FileRead(
        IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string pchFile,
        byte[] pvData, int cubDataToRead);

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamRemoteStorage_FileDelete(
        IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string pchFile);

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamRemoteStorage_GetQuota(
        IntPtr self, out ulong pnTotalBytes, out ulong puMaxBytes);

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SteamAPI_ISteamRemoteStorage_GetFileCount(IntPtr self);

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_ISteamRemoteStorage_GetFileNameAndSize(
        IntPtr self, int index, out int pnFileSizeInBytes);

    // 云端开关状态查询：定位 "Sync Disabled" 到底关在哪一层
    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamRemoteStorage_IsCloudEnabledForAccount(IntPtr self);

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamRemoteStorage_IsCloudEnabledForApp(IntPtr self);

    private const string ProbePath = "SpectreReplayProbe/probe.bin";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 1 && args[0] == "delete")
            return Cleanup();
        if (args.Length == 1 && args[0] == "check")
            return CheckCloudState();
        if (args.Length == 1 && args[0] == "names")
            return NameEncodingTest();
        if (args.Length == 1 && args[0] == "clean")
            return CleanupAll();
        if (args.Length == 2 && args[0] == "seed")
            return Seed(args[1], true);
        if (args.Length == 2 && args[0] == "unseed")
            return Seed(args[1], false);
        if (args.Length == 1 && args[0] == "enable")
            return SetCloudState(true);
        if (args.Length == 1 && args[0] == "disable")
            return SetCloudState(false);

        Console.WriteLine($"[probe] cwd = {Environment.CurrentDirectory}");

        // 1) 初始化：steam_appid.txt(977950) 必须在进程工作目录里
        if (!SteamAPI_InitSafe())
        {
            Console.WriteLine("[probe] FAIL SteamAPI_Init —— Steam 客户端未运行或 appid 上下文未生效");
            return 1;
        }
        Console.WriteLine("[probe] PASS SteamAPI_Init (appid 977950 via steam_appid.txt)");

        IntPtr rs = SteamAPI_SteamRemoteStorage_v016();
        if (rs == IntPtr.Zero)
        {
            Console.WriteLine("[probe] FAIL RemoteStorage 接口指针为空");
            SteamAPI_Shutdown();
            return 1;
        }
        Console.WriteLine("[probe] PASS RemoteStorage interface ptr = 0x" + rs.ToString("X"));

        int fail = 0;

        // 2) 写探针文件
        byte[] payload = Encoding.UTF8.GetBytes("SpectreReplay Steam Cloud probe @ " + DateTime.Now.ToString("o"));
        bool w = SteamAPI_ISteamRemoteStorage_FileWrite(rs, ProbePath, payload, payload.Length);
        Console.WriteLine($"[probe] {(w ? "PASS" : "FAIL")} FileWrite(\"{ProbePath}\", {payload.Length} bytes)");
        if (!w) fail++;

        // 3) 查存在与大小
        bool e = SteamAPI_ISteamRemoteStorage_FileExists(rs, ProbePath);
        int sz = SteamAPI_ISteamRemoteStorage_GetFileSize(rs, ProbePath);
        Console.WriteLine($"[probe] {(e && sz == payload.Length ? "PASS" : "FAIL")} FileExists={e} GetFileSize={sz}");
        if (!e || sz != payload.Length) fail++;

        // 4) 读回验证内容
        var buf = new byte[sz];
        int rd = SteamAPI_ISteamRemoteStorage_FileRead(rs, ProbePath, buf, buf.Length);
        string echoed = Encoding.UTF8.GetString(buf, 0, rd);
        bool roundTrip = rd == payload.Length && echoed == Encoding.UTF8.GetString(payload);
        Console.WriteLine($"[probe] {(roundTrip ? "PASS" : "FAIL")} FileRead 往返一致 (read {rd} bytes)");
        if (!roundTrip) fail++;

        // 5) 配额
        if (SteamAPI_ISteamRemoteStorage_GetQuota(rs, out ulong total, out ulong max))
            Console.WriteLine($"[probe] Quota: used={total:N0} / max={max:N0}");
        else
            Console.WriteLine("[probe] Quota 查询失败（部分应用未开云时会如此）");

        // 6) 列已有云端文件（确认游戏自己的 User/*.sav 是否也在 API 视图里）
        int count = SteamAPI_ISteamRemoteStorage_GetFileCount(rs);
        Console.WriteLine($"[probe] 云端文件数(GetFileCount) = {count}");
        for (int i = 0; i < Math.Min(count, 20); i++)
        {
            string name = Marshal.PtrToStringAnsi(SteamAPI_ISteamRemoteStorage_GetFileNameAndSize(rs, i, out int fs));
            Console.WriteLine($"[probe]   [{i}] {name} ({fs} bytes)");
        }

        SteamAPI_Shutdown();
        Console.WriteLine("[probe] SteamAPI_Shutdown 完成 —— 退出后 Steam 客户端会择机上传");
        Console.WriteLine($"[probe] RESULT = {(fail == 0 ? "ALL PASS" : fail + " FAIL")}");
        return fail == 0 ? 0 : 1;
    }

    // 打开/关闭本 app 的云同步开关（等价于 Steam 库 → 属性 → 通用 → Steam 云）
    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SteamAPI_ISteamRemoteStorage_SetCloudEnabledForApp(
        IntPtr self, [MarshalAs(UnmanagedType.I1)] bool bEnabled);

    private static int SetCloudState(bool enable)
    {
        if (!SteamAPI_InitSafe())
        {
            Console.WriteLine("[probe] FAIL SteamAPI_Init");
            return 1;
        }
        IntPtr rs = SteamAPI_SteamRemoteStorage_v016();
        if (rs == IntPtr.Zero)
        {
            Console.WriteLine("[probe] FAIL 接口指针为空");
            SteamAPI_Shutdown();
            return 1;
        }

        Console.WriteLine($"[probe] before: IsCloudEnabledForApp = {SteamAPI_ISteamRemoteStorage_IsCloudEnabledForApp(rs)}");
        SteamAPI_ISteamRemoteStorage_SetCloudEnabledForApp(rs, enable);
        Console.WriteLine($"[probe] SetCloudEnabledForApp({enable}) 已调用");
        Console.WriteLine($"[probe] after : IsCloudEnabledForApp = {SteamAPI_ISteamRemoteStorage_IsCloudEnabledForApp(rs)}");

        SteamAPI_Shutdown();
        return 0;
    }

    /// <summary>
    /// 文件名编码验证：回放文件名里会有中文谱名，而 DllImport 默认 LPStr=ANSI 编码。
    /// 若 Steam 按 UTF-8 解释，中文名会乱码/写不进去。
    /// 同时验证子目录、空格、以及"纯 ASCII 对照"。
    /// </summary>
    private static int NameEncodingTest()
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!SteamAPI_InitSafe())
        {
            Console.WriteLine("[names] FAIL SteamAPI_InitSafe");
            return 1;
        }
        IntPtr rs = SteamAPI_SteamRemoteStorage_v016();
        var names = new[]
        {
            "SpectreReplayProbe/ascii_name.sprp",
            "SpectreReplayProbe/中文谱名_测试.sprp",
            "SpectreReplayProbe/空格 与 括号 (1).sprp",
        };
        byte[] payload = Encoding.UTF8.GetBytes("encoding-probe");
        foreach (var n in names)
        {
            bool ok = SteamAPI_ISteamRemoteStorage_FileWrite(rs, n, payload, payload.Length);
            Console.WriteLine($"[names] write {(ok ? "OK  " : "FAIL")} <- {n}");
        }

        int count = SteamAPI_ISteamRemoteStorage_GetFileCount(rs);
        Console.WriteLine($"[names] GetFileCount = {count}");
        for (int i = 0; i < count; i++)
        {
            IntPtr ptr = SteamAPI_ISteamRemoteStorage_GetFileNameAndSize(rs, i, out int fs);
            string? got = Marshal.PtrToStringUni(ptr);   // 先按 UTF-16 看，再按 ANSI
            string? ansi = Marshal.PtrToStringUTF8(ptr);   // Steam 的返回串是 UTF-8
            string? utf8 = null;
            try
            {
                var bytes = new List<byte>();
                for (int k = 0; ; k++)
                {
                    byte b = Marshal.ReadByte(ptr, k);
                    if (b == 0) break;
                    bytes.Add(b);
                }
                utf8 = Encoding.UTF8.GetString(bytes.ToArray());
            }
            catch { }
            Console.WriteLine($"[names]   [{i}] ansi={ansi} | utf8={utf8} | uni={got} ({fs} bytes)");
        }

        // 用原始 UTF-8 名字回查，验证 FileExists 是否认得
        foreach (var n in names)
        {
            bool ex = SteamAPI_ISteamRemoteStorage_FileExists(rs, n);
            Console.WriteLine($"[names] exists={ex} <- {n}");
        }

        SteamAPI_Shutdown();
        return 0;
    }

    private static int CheckCloudState()
    {
        Console.WriteLine($"[probe] cwd = {Environment.CurrentDirectory}");
        if (!SteamAPI_InitSafe())
        {
            Console.WriteLine("[probe] FAIL SteamAPI_Init");
            return 1;
        }
        IntPtr rs = SteamAPI_SteamRemoteStorage_v016();
        if (rs == IntPtr.Zero)
        {
            Console.WriteLine("[probe] FAIL 接口指针为空");
            SteamAPI_Shutdown();
            return 1;
        }

        Console.WriteLine($"[probe] IsCloudEnabledForAccount = {SteamAPI_ISteamRemoteStorage_IsCloudEnabledForAccount(rs)}");
        Console.WriteLine($"[probe] IsCloudEnabledForApp     = {SteamAPI_ISteamRemoteStorage_IsCloudEnabledForApp(rs)}");
        if (SteamAPI_ISteamRemoteStorage_GetQuota(rs, out ulong total, out ulong max))
            Console.WriteLine($"[probe] Quota used={total:N0} / max={max:N0}");

        int count = SteamAPI_ISteamRemoteStorage_GetFileCount(rs);
        Console.WriteLine($"[probe] 云端文件数 = {count}");
        for (int i = 0; i < Math.Min(count, 50); i++)
        {
            string name = Marshal.PtrToStringAnsi(SteamAPI_ISteamRemoteStorage_GetFileNameAndSize(rs, i, out int fs)) ?? "?";
            Console.WriteLine($"[probe]   [{i}] {name} ({fs} bytes)");
        }

        SteamAPI_Shutdown();
        return 0;
    }

    /// <summary>
    /// 在云端放/删一个探针文件（用来验证 mod 的启动自动恢复）。
    /// </summary>
    private static int Seed(string remoteName, bool write)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!SteamAPI_InitSafe())
        {
            Console.WriteLine("[seed] FAIL SteamAPI_InitSafe");
            return 1;
        }
        IntPtr rs = SteamAPI_SteamRemoteStorage_v016();
        if (rs == IntPtr.Zero)
        {
            Console.WriteLine("[seed] FAIL 接口指针为空");
            SteamAPI_Shutdown();
            return 1;
        }

        if (write)
        {
            byte[] payload = Encoding.UTF8.GetBytes(
                "CLOUDTEST marker written by SteamCloudProbe @ " + DateTime.Now.ToString("o"));
            bool ok = SteamAPI_ISteamRemoteStorage_FileWrite(rs, remoteName, payload, payload.Length);
            Console.WriteLine($"[seed] {(ok ? "PASS" : "FAIL")} write {remoteName} ({payload.Length} bytes)");
            Console.WriteLine("[seed] 文件已进入本地云缓存，退出后由 Steam 上传（或随游戏会话一起同步）");
            SteamAPI_Shutdown();
            return ok ? 0 : 1;
        }

        bool del = SteamAPI_ISteamRemoteStorage_FileDelete(rs, remoteName);
        Console.WriteLine($"[seed] {(del ? "PASS" : "FAIL")} delete {remoteName}");
        SteamAPI_Shutdown();
        return del ? 0 : 1;
    }

    /// <summary>删除探针命名空间下的全部文件，避免污染用户的云端。</summary>
    private static int CleanupAll()
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!SteamAPI_InitSafe())
        {
            Console.WriteLine("[clean] FAIL SteamAPI_InitSafe");
            return 1;
        }
        IntPtr rs = SteamAPI_SteamRemoteStorage_v016();
        if (rs == IntPtr.Zero)
        {
            Console.WriteLine("[clean] FAIL 接口指针为空");
            SteamAPI_Shutdown();
            return 1;
        }

        // 先快照名字再删：边遍历边删会让下标错位
        var targets = new List<string>();
        int count = SteamAPI_ISteamRemoteStorage_GetFileCount(rs);
        for (int i = 0; i < count; i++)
        {
            string? n = Marshal.PtrToStringUTF8(SteamAPI_ISteamRemoteStorage_GetFileNameAndSize(rs, i, out _));
            if (n != null && n.StartsWith("SpectreReplayProbe/", StringComparison.Ordinal))
                targets.Add(n);
        }
        foreach (var n in targets)
        {
            bool ok = SteamAPI_ISteamRemoteStorage_FileDelete(rs, n);
            Console.WriteLine($"[clean] {(ok ? "deleted" : "FAILED ")} {n}");
        }
        Console.WriteLine($"[clean] 共处理 {targets.Count} 个探针文件，剩余 GetFileCount={SteamAPI_ISteamRemoteStorage_GetFileCount(rs)}");

        SteamAPI_Shutdown();
        return 0;
    }

    private static int Cleanup()
    {
        if (!SteamAPI_InitSafe())
        {
            Console.WriteLine("[probe] FAIL SteamAPI_Init");
            return 1;
        }
        IntPtr rs = SteamAPI_SteamRemoteStorage_v016();
        bool d = rs != IntPtr.Zero && SteamAPI_ISteamRemoteStorage_FileDelete(rs, ProbePath);
        Console.WriteLine($"[probe] {(d ? "PASS" : "FAIL")} FileDelete(\"{ProbePath}\")");
        SteamAPI_Shutdown();
        return d ? 0 : 1;
    }
}
