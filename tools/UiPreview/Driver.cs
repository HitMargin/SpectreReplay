// 离线验证（移植 Spectre 后）：只验不碰引擎的纯逻辑层。
//   · LocCheck      文案表完整性
//   · HardTextCheck 用户可见中文有没有写死
//   · ReplayCheck   回放文件格式往返（Encoder/Decoder 真实源码）
// 界面的数据层/回放语义随 Spectre 移植走 SpectrePatchHub 补丁链，
// 离线闸门不再逐条复刻引擎内部（那是 Spectre 已验过的）。
using System;
using System.IO;

namespace UiPreview
{
    internal static class Driver
    {
        internal static int Run(string outDir, string[] args)
        {
            int locFail = LocCheck.Run(LocCheck.CollectUsedKeys());
            int hardFail = HardTextCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int replayFail = ReplayCheck.Run();
            int total = locFail + hardFail + replayFail;
            Console.WriteLine();
            Console.WriteLine(total == 0
                ? "  全部通过"
                : $"  {total} 项不通过");
            return total;
        }

        internal static int Main(string[] args)
        {
            return Run(args.Length > 0 ? args[0] : "preview", args);
        }
    }
}
