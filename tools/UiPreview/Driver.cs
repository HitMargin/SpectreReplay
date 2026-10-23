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
            int wavFail = WavRoundTripCheck.Run();
            int runtimeFail = LocRuntimeCheck.Run();
            int portedFail = PortedCompletenessCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int shapeFail = UiLogicShapeCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int missingKeyFail = MissingRuntimeKeyCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int keybdFail = KeybdSoundTickCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int cfgFail = ConfigMigrationCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int patchFail = PatchTargetCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int reflectFail = ReflectionTargetCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            // LevelProbeCheck 记录三次错判据的历史 + 钉住"移植后仍用场景名"。
            // 它曾是一份从未注册的孤儿检查（存在但一次都不跑）——
            // 所以任何 *.cs 加进 tools\UiPreview 后，必须在这里登记，
            // 否则它既不跑也不会有人发现。
            int levelFail = LevelProbeCheck.Run();
            int xthreadFail = CrossThreadFieldCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int cryptoFail = ReplayCryptoCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int enumFail = EnumParseGuardCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int loadMarkerFail = LoadMarkerCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int locFallbackFail = LocFallbackCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int locLangFail = LocLanguageCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int pinStateFail = PinStateCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int injectFail = InstanceInjectionCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int logPrefixFail = LogPrefixCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int scatterFail = HitScatterMathCheck.Run();
            // 鼠标指针合成：没有异常的那类缺陷——指针不出现、画反、上下颠倒，
            // 只有翻成片才发现，所以拿真源码往 byte[] 上真画一遍再验像素。
            int cursorFail = CursorOverlayCheck.Run();
            // 指针轨迹：尾部可选段的兼容矩阵（无段/未知尾部/版本上界/count 防护）
            // 与按歌曲时间取样（插值/夹取/倒计时负区间）——兼容路径断了在游戏里
            // 毫无症状，只有成片里指针消失/跳回才看得到，必须离线钉住。
            int cursorTrackFail = CursorTrackCheck.Run();
            // 跨平台回放输入：非 Windows / 未聚焦时 SendInput 完全没有用，
            // 而这条链最弱的环（holdKeys 里是不是真实键、去重条件对不对）
            // 只会在实机上暴露。离线钉住它的接线形状。
            int crossInputFail = CrossPlatformInputCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int orphanFail = PortedCompletenessCheck.NoOrphanChecks(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));
            int total = locFail + hardFail + replayFail + wavFail + runtimeFail + portedFail + shapeFail + missingKeyFail + keybdFail + cfgFail + patchFail + reflectFail + levelFail + xthreadFail + cryptoFail + enumFail + loadMarkerFail + locFallbackFail + locLangFail + pinStateFail + injectFail + logPrefixFail + scatterFail + cursorFail + cursorTrackFail + crossInputFail + orphanFail;
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
