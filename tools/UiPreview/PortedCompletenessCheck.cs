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
            // ⑦ 转换的目标容器必须来自 **Settings.ConvertTarget**。
            //
            //    **2026-10-12 用户拍板方案 B，本条判据被推翻并改写（记录在此，
            //    免得下一个会话以为它是自己冒出来的）。**
            //    原先的判据是「转换目标必须来自 render.format，不许第二处来源」，
            //    理由是怕"选了 webm 却转出 mp4"。但那个复用带来一个更实际的问题：
            //    渲染格式是"这局录成什么"，转换格式是"把已有的一堆转成什么"——
            //    复用同一个值会让"渲染 mp4、顺手把旧视频转 mkv"做不到，只能来回改。
            //    上游 Spectre 也是分开的（`_videoConvertTarget`）。
            //    "转错格式"的风险改由**界面那一行自己显示当前目标**来消除。
            //
            //    判据仍限定在 VideoConverter.Start 调用那一行：整文件搜
            //    "ConvertTarget" 是假断言——DrawConvertSection 里的下拉也用这个名字，
            //    把转换处写死成 Mp4 后它照样在（原 MUT-V2 实测过这个形态）。
            fail += CheckPredicate(projectRoot, "⑦ 转换目标容器必须来自 Settings.ConvertTarget",
                "Pages\\Pages.cs", ConverterContainerFromSetting);
            // ⑦b 转换目标必须是**可编辑**的，不能退回只读展示。
            //
            //    2026-10-12 用户截图报「格式转换不能选择格式」——那一行当时是
            //    `Gui.KeyValue("render.format", …)`：界面上有一行、看着像能选，
            //    实际只显示不可改。这是「数据在、链路通、界面缺」那一族的
            //    **第 4 例**，且是最隐蔽的子形态（前 3 例是"完全没有界面行"，
            //    这一族是"有行但只读"）。
            //
            //    判据：转换区块里必须有一个真的绑 setter 的控件。
            //    只查 "convert.target" 出现过是假判据——只读的 KeyValue
            //    用的也是同一个文案键（这正是 MUT-V3 要复现的形态）。
            fail += CheckPredicate(projectRoot, "⑦b 转换目标必须可编辑（不许退回只读展示）",
                "Pages\\Pages.cs", ConvertTargetIsEditable);
            // ⑦c 转换目标必须**双向**落盘（既写出去、也读回来）。
            //
            //    **不能写成"文件里出现过 convert_target"**：那会漏掉只写不读
            //    （存了但读不回来）或只读不写（能记住但存不下去）两种形态——
            //    而 MUT-V4 去掉的恰恰是"读"那一半，只查存在的话它照样绿，
            //    是条假判据（本项目反复踩过这个形态）。
            //    判据：DTO 里要有一个字段声明，且赋值与读取两处都在。
            fail += CheckPredicate(projectRoot, "⑦c 转换目标必须双向持久化（写了也要读回来）",
                "State\\ConfigManager.cs", ConvertTargetIsPersisted);
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
            // ⑪ 保存目录（Settings.SavePath）必须有界面入口。**与 ⑨/⑩ 同族，
            //   而且是最容易被忽略的一个**：它只有一个只读的 Gui.KeyValue 把
            //   路径显示出来，看着"界面上有这一项"，但没有任何控件能改它。
            //   数据在（Settings.cs:57）、链路通（ConfigManager 落盘、
            //   SpectreState.cs:243/266/277 真的按它写回放与键盘音），
            //   玩家却只能去手改 Configs.json，且从界面上看不出有这一项。
            //   这与上一轮「输出目录」是同一张脸：一个黑框、一个只读文本，
            //   都长着"有界面"的样子。
            fail += Check(projectRoot, "⑪ 保存目录有定义、有真消费方",
                new[] { "State\\Settings.cs", "State\\SpectreState.cs" },
                new[] { "SavePath" });
            fail += Check(projectRoot, "⑪b 录制页要有「更改保存目录」的控件（不能只是只读展示）",
                new[] { "Pages\\Pages.cs" },
                new[] { "record.changedir" });
            // ⑫ 回放页要有「打开这一关」——从已载入回放直达它录自的那一关。
            //    2026-10-12 用户报「为什么缺少打开定位关卡的按钮」。
            //    **为什么它必需**：回放只在"重开/换关/进关"时才启动
            //    （scrController.Start_Rewind），所以玩家载入回放后必须自己再进
            //    那一关。练习谱/自制谱找回去很麻烦——上游有这颗按钮
            //    （SpectreUiPages.More.cs:98-121），我们移植时丢了。
            //    拆两条：⑫ 管"跳转逻辑存在且有正确判据"，⑫b 管"界面真的给了入口"。
            fail += Check(projectRoot, "⑫ 打开关卡要读 level_path 并区分官方/编辑器",
                new[] { "Pages\\Pages.cs", "State\\SpectreState.cs" },
                new[] { "LevelPath", "OpenReplayLevel" });
            // ⑫b 回放页要有「打开这一关」的**按钮**。
            //     **不能只查 "playback.openlevel" 这个子串**：它同时是
            //     playback.openlevel.official / .missing 的前缀，而那两条只是
            //     提示文字（Label）。只查子串的话，把按钮整个删掉、
            //     只留两条提示，判据照样绿——是条假断言（本项目反复踩过）。
            //     判据：必须存在一个 **Button 调用**，其文案含 playback.openlevel。
            fail += CheckPredicate(projectRoot, "⑫b 回放页要有「打开这一关」的按钮（不能只剩提示文字）",
                "Pages\\Pages.cs", HasOpenLevelButton);
            // ⑫c 官方关卡也必须能直达（2026-10-17 用户报「官方关卡不能直接打开」）。
            //
            //    原先这里只显示一句"官方关卡没有可打开的谱面文件"，看着有道理，
            //    其实是**把"没有文件"错当成了"没有关卡"**——官方关卡的数据烤在
            //    游戏里，靠 `GCS.internalLevelName`（形如 "0-0"/"AR-X"）选。
            //    回放录制时已把这个名字写进 `data.strings[InternalLevelName]`，
            //    所以完全能直达：清 customLevelPaths → 写 internalLevelName →
            //    重载 scnGame，`scnGame.Update` 第三帧就会 LoadAndPlayLevel。
            //
            //    判据三件齐（缺任一件都会静默跳到别的关卡）：
            //      ① 真的写了 `GCS.internalLevelName = `；
            //      ② **真的清掉了 `GCS.customLevelPaths`**——它是 scnGame.Update
            //         里 `internalLevelName ?? customLevelPaths[index]` 的另一边，
            //         留着上一关的自制谱路径会让关卡选错；
            //      ③ 真的重载了 scnGame（只改字段不重载场景，什么都不会发生）。
            fail += CheckPredicate(projectRoot, "⑫c 官方关卡必须能直达（写 internalLevelName + 清 customLevelPaths + 重载 scnGame）",
                "State\\SpectreState.cs", OfficialLevelJumpIsWired);
            // ⑫d 官方关卡那颗按钮必须真的存在（不能退回只显示提示文字）。
            //    与 ⑫b 同族：只查 "playback.openlevel" 子串会被提示文案骗过。
            fail += CheckPredicate(projectRoot, "⑫d 官方关卡要有可点的按钮（不能只剩提示文字）",
                "Pages\\Pages.cs", HasOfficialOpenButton);
            // ⑬ 编辑器跳转必须等场景就绪再定位起始砖。
            //    **这条最容易漏**：编辑器试玩从"当前选中砖"起跳，不定位就会从
            //    第 0 块开始，整份回放的按键位置全错——而界面看不出任何异常，
            //    只是"回放放出来完全不对"。判据取三件必须同时成立的事：
            //    等 isLoading 熄灭、用 SelectFloor、cameraJump 为 true
            //    （渲染作业里是 false，那是离线专用，不能照搬）。
            fail += CheckPredicate(projectRoot, "⑬ 编辑器跳转必须等就绪后定位起始砖（cameraJump=true）",
                "State\\SpectreState.cs", EditorLocateWaitsForReady);
            // ⑭ 命中分布图必须有缓存。它每帧都要画，而贴图不受 GC 管理——
            //    每帧 new 会持续泄漏显存（与 RoundedTex 同一条理由）。
            //
            //    **判据不能只查 "_tex != null" 出现过**：那个子串在 Reset()
            //    里也有一份，把 Get() 里的复用分支删掉后判据照样绿
            //    ——第一版就是这么假绿的（MUT-V8 实测）。必须**限定在 Get()
            //    方法体内**，要求"复用分支既判了 _tex 非空、又真的把它返回"。
            fail += CheckPredicate(projectRoot, "⑭ 命中分布图必须复用缓存贴图（不许每帧重建）",
                "Ui\\HitScatter.cs", ScatterReusesCache);
            // ⑮ 渲染时钟开关必须**四件齐**（2026-10-17 用户要求把它做成实验性开关）。
            //
            //    这条防的是一类特别隐蔽的半成品：把 Settings 字段和界面控件都加了，
            //    但**忘了给补丁挂 Feature 或忘了 SetFeatureToggle**。后果是
            //    开关看着能拨、也落盘了，而补丁**始终常开**（或始终不挂）——
            //    界面上完全看不出异常，玩家以为自己在控制它。
            //
            //    四件必须同时成立（缺任一件都是假开关）：
            //      ① 补丁的 attribute 真的挂了这个 Feature（否则拨开关不影响挂载）；
            //      ② Main 里真的注册了谓词（未注册 = 按关闭处理 + 日志告警，
            //         表现为"开关拨了没反应"）；
            //      ③ ConfigManager 双向持久化（只写不读/只读不写都会让开关不生效）；
            //      ④ 界面有控件（否则只能手改 Configs.json）。
            //    每条独立判、各自能单独变红，不用一条大判据蒙混。
            fail += Check(projectRoot, "⑮a 渲染时钟补丁必须挂在 FeatureKeys.RenderClock 上（否则开关是假的）",
                new[] { "Features\\VideoRender\\VideoRenderClockPatches.cs" },
                new[] { "Feature = FeatureKeys.RenderClock" });
            fail += Check(projectRoot, "⑮b Main 必须注册该 Feature 的谓词（未注册 = 按关闭处理）",
                new[] { "Main.cs" },
                new[] { "FeatureKeys.RenderClock," });
            fail += Check(projectRoot, "⑮c 该开关必须双向持久化（字段/写入/读取三处齐）",
                new[] { "State\\Settings.cs", "State\\ConfigManager.cs" },
                new[] { "RenderClockOn" });
            fail += CheckPredicate(projectRoot, "⑮d 该开关必须双向持久化到 render_clock_on",
                "State\\ConfigManager.cs", RenderClockPersisted);
            fail += Check(projectRoot, "⑮e 渲染页要有该开关的控件",
                new[] { "Pages\\Pages.cs" },
                new[] { "render.clock.experimental" });
            // ⑮f 渲染作业的就绪判断必须**同时**看开关，不能只看 TranspilerReady。
            //
            //    这是 2026-10-17 加开关时自己引进来的真 bug：`TranspilerReady`
            //    全仓只有置 true、没有置回 false 的地方（补丁卸载不会清它）。
            //    开关拨关后补丁已卸载、scrConductor.Update 恢复读真实 dspTime，
            //    而 TranspilerReady 仍是旧 true ⇒ 作业继续走确定性轨：
            //    一边用帧号算 PinnedSongPosition，一边游戏内部用真实 dspTime，
            //    **两条时间轴混用**，比纯净兜底更糟。
            //    坏序是「开→渲染→关→再渲染」，只看单次渲染永远发现不了。
            //
            //    判据：那一行必须写成 `RenderClockSource.TranspilerReady && Settings.RenderClockOn`。
            //    只查 "TranspilerReady" 出现过是没用的（它本来就该出现）；
            //    必须钉住"它后面还 AND 了开关"这个形状。
            fail += Check(projectRoot, "⑮f 渲染作业的就绪判断必须与开关一起判（否则关掉后仍走确定性轨）",
                new[] { "Features\\VideoRender\\VideoRenderJob.cs" },
                new[] { "RenderClockSource.TranspilerReady && Settings.RenderClockOn" });
            if (fail == 0)
                Console.WriteLine("    ✓ 随移植搬来的模块都已被接上");
            return fail;
        }

        /// <summary>
        /// 渲染时钟开关是否**真的**双向持久化。
        ///
        /// 只查 "RenderClockOn" 或 "render_clock_on" 出现在 ConfigManager 里是不够的
        /// ——第一版就是这么写的，被 MUT-U18 当场证伪：变异体把读取行改成
        /// `Settings.RenderClockOn = Settings.RenderClockOn; if (false) Settings.RenderClockOn = ints.Value<bool?>("render_clock_on") ...`，
        /// **原来那段子串仍然在里面**，子串判据照样绿，而开关已经读不回来了。
        ///
        /// 所以判据必须是**语义的**：把注释剥掉后逐行看，
        ///   ① DTO 里有 `bool render_clock_on` 字段；
        ///   ② 存在**赋值给它**的行（`render_clock_on = Settings.RenderClockOn`）；
        ///   ③ 存在**从它读出来写回设置**的行——而且那一行**不能**是先自赋值
        ///      再用 `if (false)` 之类挡住的形态。
        ///
        /// ③ 的判法：读回行必须以 `Settings.RenderClockOn = ints.Value<bool?>` 开头
        /// （左值就是 Settings，不是先给自己赋一次），且不含 `if (false)`。
        /// </summary>
        private static bool RenderClockPersisted(string code)
        {
            if (!code.Contains("bool render_clock_on { get; set; }", StringComparison.Ordinal))
                return false;
            if (!code.Contains("render_clock_on = Settings.RenderClockOn", StringComparison.Ordinal))
                return false;

            // 逐行找"真正的读回"：左值必须是 Settings.RenderClockOn，
            // 且整行不是被条件挡住的死代码。
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (!line.StartsWith("Settings.RenderClockOn = ints.Value<bool?>", StringComparison.Ordinal))
                    continue;
                // 自赋值 + 死条件 的伪装形态一律不算（MUT-U18 复现的就是它）
                if (line.Contains("Settings.RenderClockOn = Settings.RenderClockOn", StringComparison.Ordinal))
                    continue;
                if (line.Contains("if (false)", StringComparison.Ordinal))
                    continue;
                if (!line.Contains("\"render_clock_on\"", StringComparison.Ordinal))
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 命中分布图是否真的**复用**缓存贴图。
        ///
        /// 做法：定位 <c>Get(</c> 的声明，在它的方法体范围内（往后 40 行）
        /// 要求两件事同时成立：①有一个"判 _tex 非空"的分支 ②该分支返回 _tex。
        ///
        /// **为什么必须限定方法体**：`_tex != null` 这个子串在 <c>Reset()</c>
        /// 里也有一份——整文件 Contains 的话，把 Get() 的复用分支删掉，
        /// 判据仍然绿（第一版实测如此，MUT-V8 证明了它是假判据）。
        /// </summary>
        private static bool ScatterReusesCache(string code)
        {
            int at = code.IndexOf("internal static Texture2D Get(", StringComparison.Ordinal);
            if (at < 0) return false;
            string[] lines = code.Substring(at).Replace("\r\n", "\n").Split('\n');
            bool tested = false, returned = false;
            for (int i = 0; i < lines.Length && i < 40; i++)
            {
                string line = lines[i];
                // 复用分支的判据：既提到 _tex 非空，也提到缓存命中
                if (line.Contains("_tex != null", StringComparison.Ordinal)
                    && line.Contains("ReferenceEquals", StringComparison.Ordinal)) tested = true;
                if (line.Contains("return _tex;", StringComparison.Ordinal)) returned = true;
            }
            return tested && returned;
        }

        /// <summary>
        /// 是否存在一个**按钮**，其文案是「打开这一关」那一族。
        ///
        /// 判据必须同时看到 <c>GUILayout.Button</c> 与 <c>playback.openlevel</c>
        /// 在**同一行**：那一族的另外两条（.official / .missing）走的是
        /// <c>GUILayout.Label</c>，是纯提示，不能算"给了入口"。
        /// </summary>
        /// <summary>
        /// 官方关卡直达是否**真的接好了**：三件必须同时成立。
        ///
        /// 单看任一件都会假绿——
        ///   · 只写 `internalLevelName` 不重载场景 ⇒ 什么都没发生；
        ///   · 只重载场景不写名字 ⇒ scnGame.Awake 会落回默认值（`AR-X`），跳到别的关；
        ///   · 不清 `customLevelPaths` ⇒ `scnGame.Update` 的
        ///     `internalLevelName ?? customLevelPaths[index]` 里那半边仍有值，
        ///     上一关的自制谱路径会把关卡抢走。
        ///
        /// 判据落在**代码行**上（剥注释后），且限定在 `GoOfficial` 方法体内：
        /// 同文件里 `Go()` 合法地写着 `GCS.customLevelPaths = new[] { levelPath }`
        /// （那是自制谱分支），整文件搜 `customLevelPaths` 会假绿。
        /// </summary>
        private static bool OfficialLevelJumpIsWired(string code)
        {
            string body = MethodBody(code, "GoOfficial");
            if (body == null) return false;
            return body.Contains("GCS.internalLevelName = internalName", StringComparison.Ordinal)
                && body.Contains("GCS.customLevelPaths = null", StringComparison.Ordinal)
                && body.Contains("\"scnGame\"", StringComparison.Ordinal)
                && body.Contains("LoadSceneWithTransition", StringComparison.Ordinal);
        }

        /// <summary>
        /// 官方关卡那颗按钮是否真的存在。
        ///
        /// **与 `HasOpenLevelButton` 分开的原因**：那条只认"含 playback.openlevel
        /// 的 Button 行"，而官方分支曾经连按钮都没有、只有提示文案。现在两条分支
        /// 各有自己的按钮，各自判——否则修好其中一条，另一条退回纯提示也不会被发现。
        /// 这里认的是 `GoOfficial()` 这个**独有的调用**出现在文件里，
        /// 比匹配文案更稳（文案可以复用在别处）。
        /// </summary>
        private static bool HasOfficialOpenButton(string code)
        {
            foreach (string raw in code.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("*", StringComparison.Ordinal)) continue;
                if (line.Contains("OpenReplayLevel.GoOfficial()", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 取某方法的体（从签名行到下一个成员声明为止），剥掉注释行。
        /// 本项目的成员都直接挂在 static class 下（没有局部函数），
        /// 所以"最近的成员声明"就是真正的宿主方法。返回 null = 没找到。
        /// </summary>
        private static string MethodBody(string code, string method)
        {
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            int start = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(lines[i],
                        @"^\s*(?:private|internal|public|protected)\s+[\w<>\[\],\.\s]+\s+" +
                        System.Text.RegularExpressions.Regex.Escape(method) + @"\s*\("))
                {
                    continue;
                }
                start = i;
                break;
            }
            if (start < 0) return null;

            var sb = new System.Text.StringBuilder();
            for (int i = start; i < lines.Length; i++)
            {
                if (i > start && System.Text.RegularExpressions.Regex.IsMatch(lines[i],
                        @"^\s*(?:private|internal|public|protected)\s+[\w<>\[\],\.\s]+\s+\w+\s*\("))
                {
                    break;
                }
                string t = lines[i].TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal)
                    || t.StartsWith("*", StringComparison.Ordinal)
                    || t.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;   // 注释里提到这些名字不算接线
                }
                sb.AppendLine(lines[i]);
            }
            return sb.ToString();
        }

        private static bool HasOpenLevelButton(string code)
        {
            foreach (string raw in code.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("*", StringComparison.Ordinal)) continue;
                if (!line.Contains("GUILayout.Button", StringComparison.Ordinal)) continue;

                // **必须按"键名边界"判，不能只 Contains("playback.openlevel")。**
                // 2026-10-17 实测踩到：官方关卡那颗按钮用了 `playback.openlevel.official.go`，
                // 它包含 `playback.openlevel` 这个子串 ⇒ 把自制谱那颗改坏（MUT-V5）后
                // 判据仍被官方这颗命中，断言当场退化成假的。
                // 判法：出现 `playback.openlevel` 之后紧接的字符必须是引号或行尾，
                // 即它是一个**完整键名**而不是更长的键的前缀。
                int at = line.IndexOf("playback.openlevel", StringComparison.Ordinal);
                while (at >= 0)
                {
                    int after = at + "playback.openlevel".Length;
                    // 前面也必须是引号，避免匹配到 `xxx.playback.openlevel` 这种
                    char before = at > 0 ? line[at - 1] : '"';
                    if (after >= line.Length || line[after] == '"')
                    {
                        if (before == '"' || before == '.' || before == ' ')
                        {
                            return true;
                        }
                    }
                    at = line.IndexOf("playback.openlevel", at + 1, StringComparison.Ordinal);
                }
            }
            return false;
        }

        /// <summary>
        /// 编辑器跳转是否**等场景就绪再定位起始砖**。
        ///
        /// 三件必须同时成立（缺任何一件，回放位置都会错而界面无异常）：
        ///   ① 等到 <c>!editor.isLoading</c>（谱面还没加载完就 SelectFloor 会抛/无效）
        ///   ② 真的调了 <c>SelectFloor</c>
        ///   ③ <c>cameraJump: true</c>（交互式定位要跟镜头；渲染作业用 false 是离线专用）
        ///
        /// 判据按"整段协程体"扫，不逐行——这三句顺序与写法都可能变。
        /// </summary>
        private static bool EditorLocateWaitsForReady(string code)
        {
            // **必须锚在"声明"而不是"第一次出现"**：本文件里这个符号先出现在
            // 调用处（MainThread.StartCoroutine(...)），从那里往后看 40 行
            // 根本够不到方法体——第一版就是这么假红的。
            int at = code.IndexOf("System.Collections.IEnumerator SelectEditorFloorWhenReady",
                StringComparison.Ordinal);
            if (at < 0)
            {
                at = code.IndexOf("IEnumerator SelectEditorFloorWhenReady", StringComparison.Ordinal);
            }
            if (at < 0) return false;
            string[] lines = code.Substring(at).Replace("\r\n", "\n").Split('\n');
            bool waited = false, selected = false, cameraJump = false;
            for (int i = 0; i < lines.Length && i < 40; i++)
            {
                string line = lines[i];
                if (line.Contains("isLoading", StringComparison.Ordinal)
                    && line.Contains("!", StringComparison.Ordinal)) waited = true;
                if (line.Contains("SelectFloor(", StringComparison.Ordinal)) selected = true;
                if (line.Contains("cameraJump: true", StringComparison.Ordinal)) cameraJump = true;
            }
            return waited && selected && cameraJump;
        }

        /// <summary>
        /// 转换目标是否是**可编辑**控件（而不是只读展示）。
        ///
        /// 判据：<c>convert.target</c> 那一行的调用里出现了写回
        /// （<c>ConvertTarget = i</c>），且该行是控件调用（RowDropdown/Row 等）
        /// 而不是 <c>Gui.KeyValue</c>。
        ///
        /// **为什么不能只查文案键出现过**：只读的 `Gui.KeyValue("convert.target", …)`
        /// 用的是同一个键——那正是 2026-10-12 用户报的形态（MUT-V3 复现它）。
        /// </summary>
        private static bool ConvertTargetIsEditable(string code)
        {
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("convert.target", StringComparison.Ordinal)) continue;
                // 往后看几行：控件的回调参数常写在下一行
                for (int j = i; j < lines.Length && j < i + 4; j++)
                {
                    // 只读展示 ⇒ 不是可编辑控件
                    if (lines[j].Contains("Gui.KeyValue", StringComparison.Ordinal)) return false;
                    if (lines[j].Contains("ConvertTarget = ", StringComparison.Ordinal)) return true;
                    if (j > i && lines[j].Contains(";", StringComparison.Ordinal)) break;
                }
            }
            return false;
        }

        /// <summary>
        /// 转换目标是否**双向**持久化：DTO 有字段、赋值一处、读取一处。
        ///
        /// 三种缺一不可，因为它们是三种不同的故障：
        ///   缺字段   ⇒ 序列化根本装不下它
        ///   缺赋值   ⇒ 存的时候没写进去（改了但存不下来）
        ///   缺读取   ⇒ 读的时候没取回来（存了但记不住）
        /// 只查"出现过 convert_target"会把后两种放过（MUT-V4 去掉的就是"读取"）。
        /// </summary>
        private static bool ConvertTargetIsPersisted(string code)
        {
            bool field = false, write = false, read = false;
            foreach (string raw in code.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("//") || line.StartsWith("*")) continue;
                if (line.Contains("convert_target { get; set; }", StringComparison.Ordinal)) field = true;
                // 写：convert_target = Settings.ConvertTarget,
                if (line.Contains("convert_target = Settings.ConvertTarget", StringComparison.Ordinal)) write = true;
                // 读：Settings.ConvertTarget = Mathf.Clamp(ints.Value<int?>("convert_target") …
                if (line.Contains("Settings.ConvertTarget =", StringComparison.Ordinal)
                    && line.Contains("\"convert_target\"", StringComparison.Ordinal)) read = true;
            }
            return field && write && read;
        }

        /// <summary>
        /// VideoConverter.Start 的调用行必须用 <c>Settings.ConvertTarget</c> 推导容器。
        /// 只在调用行上判：整文件判会被 DrawConvertSection 里的目标下拉蒙混过去。
        ///
        /// **2026-10-12 判据对象从 RenderOutputFormat 改为 ConvertTarget**
        /// （用户拍板方案 B，两者解耦）。改的是"必须来自哪个设置"，不是"要不要判"。
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
                //               (VideoEncoder.OutContainer)Mathf.Clamp(ConvertTarget, 0, 3), ff);
                // 所以从调用行起往后看几行，直到这一句结束（分号）。
                for (int j = i; j < lines.Length && j < i + 4; j++)
                {
                    if (lines[j].Contains("ConvertTarget", StringComparison.Ordinal))
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
