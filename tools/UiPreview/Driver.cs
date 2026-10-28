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
        // 主项目目录：从**程序集位置**向上找，取同时含 SpectreReplay/ 与 tools/ 的目录。
        // 老实现按 `Path.GetFullPath(@"..\..\SpectreReplay")` 从**工作目录**推导，只在
        // cwd 恰好是 tools/UiPreview 时成立——在仓库根 `dotnet run --project tools/UiPreview`
        // 会把根算成 <盘>:\SpectreReplay，第一个按文件扫描的检查直接
        // DirectoryNotFoundException 炸掉整套。向上找则与 cwd、输出路径深度
        // （bin\Debug|Release\net10.0-windows\）都无关。
        internal static readonly string ProjectDir = FindProjectDir();

        private static string FindProjectDir()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "SpectreReplay"))
                    && Directory.Exists(Path.Combine(dir.FullName, "tools")))
                    return Path.Combine(dir.FullName, "SpectreReplay");
            }
            throw new InvalidOperationException(
                "找不到项目根：从 \"" + AppContext.BaseDirectory
                + "\" 向上没有同时含 SpectreReplay/ 与 tools/ 的目录");
        }

        internal static int Run(string outDir, string[] args)
        {
            int locFail = LocCheck.Run(LocCheck.CollectUsedKeys());
            int hardFail = HardTextCheck.Run(
                ProjectDir);
            int replayFail = ReplayCheck.Run();
            int wavFail = WavRoundTripCheck.Run();
            int runtimeFail = LocRuntimeCheck.Run();
            int portedFail = PortedCompletenessCheck.Run(
                ProjectDir);
            int shapeFail = UiLogicShapeCheck.Run(
                ProjectDir);
            int missingKeyFail = MissingRuntimeKeyCheck.Run(
                ProjectDir);
            int keybdFail = KeybdSoundTickCheck.Run(
                ProjectDir);
            int cfgFail = ConfigMigrationCheck.Run(
                ProjectDir);
            int patchFail = PatchTargetCheck.Run(
                ProjectDir);
            int reflectFail = ReflectionTargetCheck.Run(
                ProjectDir);
            // LevelProbeCheck 记录三次错判据的历史 + 钉住"移植后仍用场景名"。
            // 它曾是一份从未注册的孤儿检查（存在但一次都不跑）——
            // 所以任何 *.cs 加进 tools\UiPreview 后，必须在这里登记，
            // 否则它既不跑也不会有人发现。
            int levelFail = LevelProbeCheck.Run();
            int xthreadFail = CrossThreadFieldCheck.Run(
                ProjectDir);
            int cryptoFail = ReplayCryptoCheck.Run(
                ProjectDir);
            int enumFail = EnumParseGuardCheck.Run(
                ProjectDir);
            int loadMarkerFail = LoadMarkerCheck.Run(
                ProjectDir);
            int locFallbackFail = LocFallbackCheck.Run(
                ProjectDir);
            int locLangFail = LocLanguageCheck.Run(
                ProjectDir);
            int pinStateFail = PinStateCheck.Run(
                ProjectDir);
            int injectFail = InstanceInjectionCheck.Run(
                ProjectDir);
            int logPrefixFail = LogPrefixCheck.Run(
                ProjectDir);
            int scatterFail = HitScatterMathCheck.Run();
            // 鼠标指针合成：没有异常的那类缺陷——指针不出现、画反、上下颠倒，
            // 只有翻成片才发现，所以拿真源码往 byte[] 上真画一遍再验像素。
            int cursorFail = CursorOverlayCheck.Run();
            // 指针轨迹：尾部可选段的兼容矩阵（无段/未知尾部/版本上界/count 防护）
            // 与按歌曲时间取样（插值/夹取/倒计时负区间）——兼容路径断了在游戏里
            // 毫无症状，只有成片里指针消失/跳回才看得到，必须离线钉住。
            int cursorTrackFail = CursorTrackCheck.Run();
            // 嵌入键盘音：容器段（与轨迹共存的回退位置、段版本/长度上界）
            // 与压缩编解码（压缩率、时长守恒、导出 wav 头自洽、畸形输入不 OOM）。
            // 这一段的缺陷在游戏里全都没有异常，只表现为「没声音」，必须离线钉住。
            int keybdAudioFail = KeybdAudioCheck.Run();
            // 逐份回放的键盘音覆盖值（音量/偏移/静音）：尾部可选段的「不写段」「与音频
            // 共存」「无音频仍可读」三种布局 + 版本上界 + 垃圾尾部。这些缺陷在游戏里
            // 一律不抛异常，只表现为音量/静音突然不对，必须离线钉住。
            int keybdPrefsFail = KeybdPrefsCheck.Run();
            // 跨平台回放输入：非 Windows / 未聚焦时 SendInput 完全没有用，
            // 而这条链最弱的环（holdKeys 里是不是真实键、去重条件对不对）
            // 只会在实机上暴露。离线钉住它的接线形状。
            int crossInputFail = CrossPlatformInputCheck.Run(
                ProjectDir);
            // Steam 云生命周期：退出链 OnQuitting → FlushConfig 会在 Steam 上下文
            // 已被游戏 Shutdown 之后上传（_RE 工程编辑器退出实测崩进程，原生栈）。
            // 三处接线（退出先失能再落盘 / 装配重置探测 / 失能是持久标志）断在
            // 游戏里毫无托管异常——直接原生访问违例，必须离线钉住形状。
            int cloudFail = CloudLifecycleCheck.Run(
                ProjectDir);
            int orphanFail = PortedCompletenessCheck.NoOrphanChecks(
                ProjectDir);
            int total = locFail + hardFail + replayFail + wavFail + runtimeFail + portedFail + shapeFail + missingKeyFail + keybdFail + cfgFail + patchFail + reflectFail + levelFail + xthreadFail + cryptoFail + enumFail + loadMarkerFail + locFallbackFail + locLangFail + pinStateFail + injectFail + logPrefixFail + scatterFail + cursorFail + cursorTrackFail + keybdAudioFail + keybdPrefsFail + crossInputFail + cloudFail + orphanFail;
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
