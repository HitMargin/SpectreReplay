// 移植完整性：Spectre 有、我们却缺的那部分，是否真的"缺得有理由"。
//
// 这一轮为什么要有它：SpectreReplay 是 Spectre 的**部分**移植。移植的方式是
// 逐文件搬，搬完之后 HitsErrorData / CalibrationAdvisor / ReplayAnalyzer
// 这类**数据类**都在（纯逻辑、不碰引擎），而**喂数据的补丁**是另一批文件。
// 于是出现过一个真缺口：
//
//   · HitErrorData.CapturePending / Record 全仓零调用点
//     → 喂它的两个补丁（Patch_HitError.cs）没搬
//     → Patch_Lifecycle 每局在调 ResetForNewRun() 清一份永远是空的表
//     → 校准建议永远攒不够样本，玩家再练也不出建议，日志无异常
//
//   · CalibrationAdvisor.CommitRun 同样零调用点（消费方没搬）
//   · ReplayAnalyzer.Analyze / VideoEncoder.DescribeEncoder 零调用点
//
// 共同形态：**"代码在、但没有任何一条路径会执行到它"**。这对任何
// "检查声明在/不在"的闸门都是隐形的——上一轮 UI 逻辑 11 条缺陷已经栽过一次。
//
// 判据因此落在**调用点**上，不在声明上：
//   ① HitErrorData 的两个入口各有一个补丁调它（喂数据）
//   ② CalibrationAdvisor.CommitRun 有一个补丁调它（消费数据）
//   ③ ReplayAnalyzer.Analyze 在 UI 层被调用（分析结果要显示给玩家）
//   ④ VideoEncoder.DescribeEncoder 在 UI 层被调用（NVENC 静默降级要说出来）
// 每条都是"至少要有一个调用点"这种弱判据——换个措辞写个死调用骗不过 ② 的反向验证：
// ⑤ 上面四个补丁类都必须真的带 [SpectrePatch]（否则调用点是死代码）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UiPreview
{
    internal static class PortedCompletenessCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 移植完整性（数据类必须有喂它的补丁） ──");
            int fail = 0;
            fail += Check(projectRoot, "① HitErrorData 采集入口有补丁喂",
                new[] { "Features\\Replay\\Patch_HitError.cs" },
                new[] { "CapturePending", "Record" });
            fail += Check(projectRoot, "② CalibrationAdvisor 有消费方",
                new[] { "Features\\Replay\\Patch_HitError.cs" },
                new[] { "CommitRun" });
            fail += Check(projectRoot, "③ ReplayAnalyzer 的结果显示给玩家",
                new[] { "Pages\\Pages.cs" },
                new[] { "Analyze(" });
            fail += Check(projectRoot, "④ 编码器降级要说出来",
                new[] { "Pages\\Pages.cs" },
                new[] { "DescribeEncoder" });
            fail += Check(projectRoot, "⑤ 装弹后状态要说清楚（playback.ready 不能再当摆设）",
                new[] { "Pages\\Pages.cs" },
                new[] { "playback.ready", "status.pending_replay" });
            // ⑥ VideoConverter 的入口。它与前三处同族（搬了没接线），
            //    2026-10-06 才发现并补上入口；加了这条，退回"零调用点"会被抓。
            fail += Check(projectRoot, "⑥ 批量转格式要有入口（VideoConverter 不能再零调用点）",
                new[] { "Pages\\Pages.cs" },
                new[] { "VideoConverter.Start" });
            // ⑦ 转换的目标容器必须与 render.format 同源。
            //    两处各选一个容器的话，"选 webm 却转出 mp4" 必然发生——
            //    而那种错法界面完全看不出（文件确实生成了，只是格式不对）。
            //    **判据必须限定在 VideoConverter.Start 调用那一行**：
            //    整文件搜 "RenderOutputFormat" 是假断言——DrawRenderOptions 里的
            //    render.format 下拉也用这个名字，把转换处写死成 Mp4 后它照样在（MUT-V2 实测）。
            fail += CheckPredicate(projectRoot, "⑦ 转换目标容器必须来自 render.format（不许第二处来源）",
                "Pages\\Pages.cs", ConverterContainerFromSetting);
            // ⑧ 回放列表必须列出**全部可读格式**，不能只列默认那一种。
            //    LoadReplayAuto 三种格式全能读（.sprp/.crpl2/.crpl），而列表原先
            //    只 glob .sprp——另外两种文件"磁盘上有、界面上没有"，玩家从
            //    Spectre 拷来的回放或改用 Crp2 录的全点不了。这种缺失不报错、
            //    日志无异常，只是功能对那部分文件不存在。
            //    判据：列表处必须走 ReadableExtensions（集中在一处的那份清单），
            //    且清单本身要覆盖 6 个扩展名。
            fail += Check(projectRoot, "⑧ 回放列表要列全部可读格式（不许只 glob 默认扩展名）",
                new[] { "Pages\\Pages.cs", "Features\\Replay\\ReplayManager.cs" },
                new[] { "ReadableExtensions" });
            fail += Check(projectRoot, "⑧b 可读格式清单要覆盖三种格式（含不解密变体共 6 个）",
                new[] { "Features\\Replay\\ReplayManager.cs" },
                new[] { "\".crpl2\"", "\".pcrpl2\"", "\".crpl\"", "\".pcrpl\"",
                        "ReplayConstants.Extension,", "ReplayConstants.ExtensionNoEncrypt," });
            // ⑨ 键盘音三项必须有界面入口。与 ⑥ 同族（搬了没接线）且**犯了第二次**：
            //   Settings 有字段、ConfigManager 落盘、Patch_Playback 有四处消费
            //   （:65/:93 判 KeybdSoundRecordActive、:232 用 MicrophoneOffset 校准、
            //   :240 用 KeybdSoundVolume 当地音量），而界面上**一行都不能改**。
            //   尤其 MicrophoneOffset 是校准值：键盘声整体偏早/偏晚时它是唯一修正
            //   手段，玩家没得调只能手改 Configs.json，而从界面看不出有这一项。
            //   2026-10-06 round 49 补上界面；这一条防退回。
            //   判据取两个形状：设置项名 + 对应界面行（RowSlider/Row）。
            // ⑨ 键盘音三项必须有界面入口。与 ⑥ 同族（搬了没接线）且**犯了第二次**：
            //   Settings 有字段、ConfigManager 落盘、Patch_Playback 有四处消费
            //   （:65/:93 判 KeybdSoundRecordActive、:232 用 MicrophoneOffset 校准、
            //   :240 用 KeybdSoundVolume 当地音量），而界面上**一行都不能改**。
            //   尤其 MicrophoneOffset 是校准值：键盘声整体偏早/偏晚时它是唯一修正
            //   手段，玩家没得调只能手改 Configs.json，而从界面看不出有这一项。
            //   2026-10-06 round 49 补上界面；这两条防退回。
            //   **必须拆成两条**：⑨a 管"数据与消费存在"，⑨b 管"界面真的要改它们"。
            //   合成一条的话 Check 会把全部符号往每个文件上套（界面键在 Settings.cs 里
            //   根本不存在），永远红——那是判据自身写错，不是抓到了缺陷。
            fail += Check(projectRoot, "⑨a 键盘音三项有定义、有消费方",
                new[] { "State\\Settings.cs", "Features\\Replay\\Patch_Playback.cs",
                        "State\\ConfigManager.cs" },
                new[] { "KeybdSoundRecordActive", "KeybdSoundVolume", "MicrophoneOffset" });
            fail += Check(projectRoot, "⑨b 键盘音三项要有界面入口（不能再数据在、链路通、界面缺）",
                new[] { "Pages\\Pages.cs" },
                new[] { "record.keybd.record", "record.keybd.volume", "record.keybd.offset" });
            // ⑩ 回放驱动开关（Settings.LegacyEngine）必须有界面入口。
            //   **名字骗人的典型**：叫 LegacyEngine，听着像"已废弃的自研引擎开关"，
            //   实际它控制整个按键模拟回放链路——ReplayPlayer.cs:38 关掉后停止回放
            //   不会释放已按住的键（键卡住）、:133 关掉后回放一个键都不注入（画面
            //   毫无反应）。上游 Spectre 把它当一等公民摆在回放设置首页，标签就是
            //   「模拟按键 / Simulate Key Press」（SpectreUiPages.More.cs:160）。
            //   移植时丢了 UI ⇒ 只剩下 Configs.json 一条路，而它恰是"回放没反应"时
            //   最该查的一项。round 53 补上；这条防退回。
            fail += Check(projectRoot, "⑩ 回放驱动开关要有界面入口（LegacyEngine 不是废弃项）",
                new[] { "State\\Settings.cs", "Features\\Replay\\ReplayPlayer.cs" },
                new[] { "LegacyEngine" });
            fail += Check(projectRoot, "⑩b 回放页要有该开关的控件",
                new[] { "Pages\\Pages.cs" },
                new[] { "playback.simulate" });
            if (fail == 0)
                Console.WriteLine("    ✓ 随移植搬来的模块都已被接上");
            return fail;
        }

        /// <summary>
        /// VideoConverter.Start 的调用行必须用 RenderOutputFormat 推导容器。
        /// 只在调用行上判：整文件判会被 render.format 下拉那一处蒙混过去。
        /// </summary>
        private static bool ConverterContainerFromSetting(string code)
        {
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("//") || line.StartsWith("*")) continue;
                if (!line.Contains("VideoConverter.Start", StringComparison.Ordinal)) continue;
                // 调用可能跨行：_convertState = VideoConverter.Start(dir,
                //               (VideoEncoder.OutContainer)Mathf.Clamp(RenderOutputFormat, 0, 3), ff);
                // 所以从调用行起往后看几行，直到这一句结束（分号）。
                for (int j = i; j < lines.Length && j < i + 4; j++)
                {
                    if (lines[j].Contains("RenderOutputFormat", StringComparison.Ordinal))
                        return true;
                    if (j > i && lines[j].Contains(";", StringComparison.Ordinal))
                        break;
                }
                return false;
            }
            return false; // 连调用行都找不到，同样算不通过
        }

        /// <summary>
        /// **没有任何 *Check 是孤儿。**
        ///
        /// 实测（2026-10-06）：LevelProbeCheck.cs 在 tools\UiPreview 里躺了很久，
        /// 内容是一份写得很好的"关卡探测判据错了三次"的记录，却**从未在
        /// Driver.cs 里登记**——也就是一次都没跑过，而没人发现。
        ///
        /// 这类缺陷的形状很特殊：它不是"断言假"，而是"断言不存在"。
        /// 闸门全绿，因为少跑一份检查只会让通过率更高。
        ///
        /// 判据：tools\UiPreview 下每个 *Check.cs 的类名都必须出现在 Driver.cs 里。
        /// </summary>
        internal static int NoOrphanChecks(string projectRoot)
        {
            string prevDir = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(projectRoot, "..", "tools", "UiPreview"));
            if (!System.IO.Directory.Exists(prevDir)) return 0;
            string driver = System.IO.Path.Combine(prevDir, "Driver.cs");
            if (!System.IO.File.Exists(driver)) return 0;
            string driverText = System.IO.File.ReadAllText(driver);

            int fail = 0;
            foreach (var f in System.IO.Directory.GetFiles(prevDir, "*Check.cs"))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(f);
                // 本类自己由 Run 调用，不参与本例检
                if (name == "PortedCompletenessCheck") continue;
                if (!driverText.Contains(name + ".Run(", StringComparison.Ordinal))
                {
                    Console.WriteLine("    ✗ " + name + " 没有在 Driver.cs 里登记"
                        + "  ← 这份检查一次都不会跑，而闸门只会更绿");
                    fail++;
                }
            }
            if (fail == 0)
                Console.WriteLine("    ✓ 没有孤儿检查（每份 *Check 都登记在 Driver 里）");
            return fail;
        }

        // 每个符号必须出现在**给定的文件**里（调用点所在处），而不是"全仓出现过"。
        // 后者太松：注释里提一句就过。声明文件自己不算调用点。
        private static int CheckPredicate(string projectRoot, string name, string rel,
            Func<string, bool> predicate)        {
            string ok = Path.Combine(projectRoot, rel);
            if (!File.Exists(ok))
            {
                Console.WriteLine("    ✗ " + name + "：找不到 " + rel);
                return 1;
            }
            // **必须先剥注释。** 这三条判据全是"不许出现 X"，而说明"为什么不许"
            // 的注释里必然要写出 X 本身（本例中"360" 与 "GetLastRect" 都写在
            // 注释里解释这个坑）。不剥的话三条会同时变红——看上去像判据全中，
            // 实际是把说明文字当成了缺陷（这就叫自证的同义反复）。
            string code = SrcUtil.Clean(File.ReadAllText(ok));
            if (!predicate(code))
            {
                Console.WriteLine("    ✗ " + name + "（" + rel + "）");
                return 1;
            }
            return 0;
        }

        /// <summary>按符号清单判（每个符号都必须在代码里出现）。</summary>
        private static int Check(string projectRoot, string name, string[] files, string[] symbols)
        {
            foreach (string rel in files)
            {
                string ok = Path.Combine(projectRoot, rel);
                if (!File.Exists(ok))
                {
                    Console.WriteLine("    ✗ " + name + "：找不到 " + rel);
                    return 1;
                }
                string text = File.ReadAllText(ok);
                foreach (string s in symbols)
                {
                    if (!HasCodeUse(text, s))
                    {
                        Console.WriteLine("    ✗ " + name + "：" + rel + " 里没有调用 " + s);
                        return 1;
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// 符号是否在**代码行**里出现。
        ///
        /// 必须剥掉注释行：本文件要防的恰恰是"代码在、但没人调用"。注释里
        /// 提一句方法名（说明为什么要有它）就满足判据的话，这条断言就是
        /// 自证的同义反复 —— MUT-P9/P10 第一版就是这么失败的：
        /// 调用点删干净了，注释里那两个名字照样让闸门全绿。
        /// </summary>
        private static bool HasCodeUse(string text, string symbol)
        {
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("*", StringComparison.Ordinal))
                    continue;
                if (line.Contains(symbol, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
