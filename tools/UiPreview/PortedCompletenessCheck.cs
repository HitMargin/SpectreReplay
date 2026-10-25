// 移植完整性：Spectre 有、我们却缺的那部分，是否真的"缺得有理由"。
//
// 这一轮为什么要有它：SpectreReplay 是 Spectre 的**部分**移植。移植的方式是
// 逐文件搬，搬完之后 ReplayAnalyzer 这类**数据类**都在（纯逻辑、不碰引擎），
// 而**喂数据的补丁**是另一批文件。于是出现过一个真缺口：
//
//   · ReplayAnalyzer.Analyze / VideoEncoder.DescribeEncoder 零调用点
//
// 共同形态：**"代码在、但没有任何一条路径会执行到它"**。这对任何
// "检查声明在/不在"的闸门都是隐形的——上一轮 UI 逻辑 11 条缺陷已经栽过一次。
//
// 判据因此落在**调用点**上，不在声明上：
//   ③ ReplayAnalyzer.Analyze 在 UI 层被调用（分析结果要显示给玩家）
//   ④ VideoEncoder.DescribeEncoder 在 UI 层被调用（NVENC 静默降级要说出来）
// 每条都是"至少要有一个调用点"这种弱判据——换个措辞写个死调用骗不过 ⑤ 的反向验证：
// ⑤ 上面那些补丁类都必须真的带 [SpectrePatch]（否则调用点是死代码）
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
            // ⑧ 回放列表必须走**集中在一处**的扩展名清单，不许在界面里写死一份。
            //    历史：列表原先只 glob `.sprp`，而当时解码端三种格式全能读，
            //    于是 `.crpl2/.crpl` 的文件"磁盘上有、界面上没有"。
            //    2026-10-18 用户裁定删掉旧回放格式适配后，可读格式只剩新格式两种，
            //    但"扩展名清单只有一份"这条仍然要守——写死的第二份会在格式再变时
            //    静默失效（文件还在、列表里没有）。
            fail += Check(projectRoot, "⑧ 回放列表要列全部可读格式（不许只 glob 默认扩展名）",
                new[] { "Pages\\Pages.cs", "Features\\Replay\\Runtime\\ReplayManager.cs" },
                new[] { "ReadableExtensions" });
            // ⑧b 清单内容：两种新格式都要在，且**旧格式的四个扩展名一个都不许回来**。
            //    前半段防"少列一种"（`.psprp` 漏掉 → 不解密录的文件点不开）；
            //    后半段防回退——旧格式读写都删了，列表要是还列 `.crpl2`，
            //    玩家点它只会得到"文件头不匹配"，比不显示更像坏了。
            //
            //    **必须只看数组块内部（2026-10-19 修）。** 原先用 Check 在全文件里
            //    找 `ReplayConstants.ExtensionNoEncrypt,`，而 ReadReplayData 里
            //    `filePath.EndsWith(ReplayConstants.ExtensionNoEncrypt, StringComparison…)`
            //    同样带一个尾逗号 —— 于是把清单里的那一行删掉（MUT-U46）断言照样绿，
            //    它命中的是**用法**不是**清单**。这是"判据看错了地方"那一族。
            fail += CheckPredicate(projectRoot, "⑧b 可读格式清单只含新格式两种",
                "Features\\Replay\\Runtime\\ReplayManager.cs", ReadableListHasBothFormats);
            fail += CheckAbsent(projectRoot, "⑧c 可读格式清单不得再列旧格式",
                "Features\\Replay\\Runtime\\ReplayManager.cs",
                new[] { "\".crpl2\"", "\".pcrpl2\"", "\".crpl\"", "\".pcrpl\"" });
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
                new[] { "State\\Settings.cs", "Features\\Replay\\Patches\\Patch_Playback.cs",
                        "State\\ConfigManager.cs" },
                new[] { "KeybdSoundRecordActive", "KeybdSoundVolume", "MicrophoneOffset" });
            fail += Check(projectRoot, "⑨b 键盘音三项要有界面入口（不能再数据在、链路通、界面缺）",
                new[] { "Pages\\Pages.cs" },
                new[] { "record.keybd.record", "record.keybd.volume", "record.keybd.offset" });
            // ⑩ 「模拟按键」开关（原 Settings.LegacyEngine）**已删除**，这里转为防退回。
            //
            //   上游 Spectre 把它摆在回放设置首页，叫「模拟按键 / Simulate Key Press」。
            //   但它关掉后并非「只播不注入」：ManagedInput.Active 变假 → 判定覆盖停、
            //   HoldStateInjection 直接 return（长条被判松手）、KeyViewer 桥接变暗。
            //   也就是一个「关掉就坏」的假开关——玩家查不出来，因为画面看起来正常。
            //
            //   真要停止注入用 ReplayOn 总开关（连补丁一起卸，语义干净）。
            //   这条判据防的是「哪天有人又把它加回来」。
            fail += CheckAbsent(projectRoot,
                "⑩ 回放页不许再出现「模拟按键」开关控件（关掉会让长条判定失效）",
                "Pages\\Pages.cs", new[] { "playback.simulate" });
            fail += CheckAbsent(projectRoot,
                "⑩ Settings 里不许再出现 LegacyEngine（该字段随开关一并删除）",
                "State\\Settings.cs", new[] { "LegacyEngine" });
            fail += CheckAbsent(projectRoot,
                "⑩ 托管输入不许再按 LegacyEngine 分叉",
                "Features\\Replay\\Runtime\\ManagedInput.cs", new[] { "LegacyEngine" });
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
            // ⑪c 键位重映射必须有界面入口。**与 ⑨/⑪ 同族，且是最严重的一例**：
            //   Settings 有 KeyConvertEnabled / KeyCodeConvert 两个字段、ConfigManager
            //   落盘、BuildKeyCodeConvertTable 真的在建映射表、回放与录制两侧都按它
            //   换算 VK——链路完全通，而设置页里**一行都没有**。
            //   settings-ui-audit.ps1 把这两项判为「无界面但被消费」。
            //
            //   玩家想把手柄/宏键盘/改键工具发出的自定义码位对回标准键时，只能手改
            //   Configs.json 里的 JSON 数组，而从界面上完全看不出有这个功能。
            //   上游有完整面板（SpectreUiPages.More.cs:301-361 BuildKeyRemapList）。
            //
            //   拆两条：⑪c 管"数据与运行时建表存在"，⑪d 管"界面真的给了入口"。
            //   合成一条会把界面键往 Settings.cs 上套，永远红。
            fail += Check(projectRoot, "⑪c 键位重映射有定义、有运行时建表",
                new[] { "State\\Settings.cs" },
                new[] { "KeyConvertEnabled", "KeyCodeConvert", "BuildKeyCodeConvertTable" });
            // ⑪d 界面必须真的画了这张表，而不是只放一个总开关。
            //
            // **不能用 Check(...) 查子串**：`DrawKeyRemapList` 既是方法名也是
            // 调用名，方法定义本身就让子串成立——把调用行整个删掉、只留定义，
            // 判据照样绿（MUT-KM1 实测过）。必须断言**调用形状** `名字();`。
            // 同理 total 开关行要查实际控件调用，不是文案键出现。
            fail += CheckPredicate(projectRoot, "⑪d 键位重映射要有总开关 + 对照表调用 + 写回调用",
                "Pages\\Pages.cs", RemapUiWired);
            // ⑪e **写回必须是三步齐全**：序列化 JSON、重建运行时表、落盘。
            //   只序列化不建表 ⇒ 界面显示变了、实际映射还是旧的；
            //   只建表不序列化 ⇒ 当场生效、下次启动全部丢失。
            //   这条钉的是 ApplyKeyRemap 的方法体形状（不是 Pages 里有没有这个名字）。
            fail += CheckPredicate(projectRoot, "⑪e ApplyKeyRemap 必须序列化 + 重建映射表 + 落盘",
                "Pages\\Pages.cs", RemapAppliesAllThree);
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
            //    回放录制时已把这个名字写进 `data.InternalLevelName`，
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
            // ⑫e 必须能**卸载**当前回放（2026-10-17 用户报「不能去取消当前的回放文件」）。
            //
            //    `ReplayManager.UnLoadReplay` 一直在库里，但**零调用点**——界面只有
            //    「回放」没有「卸载」，载入后回不到未装弹状态，想换成"直接打一局录制"
            //    必须重启游戏。又是「数据在、链路通、界面缺」那一族。
            //
            //    判据三件齐：
            //      ① 卸载动作存在且**顺序正确**（先停播放/录制，再换出 data，
            //         最后翻回录制态）——顺序反了会读到已清空的 data；
            //      ② 真的调了 UnLoadReplay（只清 PlayMode 不换 data，等于没卸）；
            //      ③ 界面**两个状态分支**都有入口（回放中 / 已装弹未开播）。
            //         只补一处的话，另一个状态仍然卸不掉。
            fail += CheckPredicate(projectRoot, "⑫e 卸载回放必须按序收口（停→换出data→回录制态）",
                "State\\SpectreState.cs", ReplayUnloadIsOrdered);
            fail += Check(projectRoot, "⑫f 卸载必须真的调用 UnLoadReplay（不能只翻标志）",
                new[] { "State\\SpectreState.cs" },
                new[] { "ReplayManager.UnLoadReplay(out data)" });
            fail += CheckPredicate(projectRoot, "⑫g 界面必须给出卸载入口（回放中与已装弹两处都要）",
                "Pages\\Pages.cs", HasUnloadEntryPoints);
            // ⑫h 「从异步输入取按键」必须**先检测游戏侧有没有开**（2026-10-17 用户要求）。
            //
            //    我们这侧的实现是完整的（Settings 字段 → Main 挂 Patch_UpdateInputConsumeAsync
            //    → AsyncKeyRecorder.FlushFrame 里判它），但它依赖**游戏先打开异步输入**：
            //    FlushFrame 的第三个条件就是 `AsyncInputManager.isActive`，为假时整帧
            //    直接 DiscardPending。也就是说游戏侧没开时，勾上这个开关
            //    **一点效果都没有、也不报错**。
            //
            //    这是「有界面但改不了」的变体——「有开关但没效果」，比没开关更糟：
            //    玩家会以为自己配错了别的东西（键位？输入法？），永远查不到原因。
            //
            //    判据：该行必须读游戏侧状态 `RDC.useAsyncInput`
            //    （= AsyncInputManager.isActive，反编译 RDC.cs:210-215 核实），
            //    且未开时给出明确提示文案。
            fail += CheckPredicate(projectRoot, "⑫h 异步输入开关必须先检测游戏侧是否已开启",
                "Pages\\Pages.cs", AsyncInputChecksGameSide);
            // ⑫i 「界面语言跟随游戏」开关必须保持删除（2026-10-17 用户要求）。
            //
            //    删它的理由不是"多余"而是**误导**：面板语言由 Ui.Loc.RefreshLanguage
            //    始终跟随游戏（每次开面板重算），而 Settings.FollowGameLanguage 只
            //    影响 14 语言大表里的通知文案。两个东西并存会让玩家以为
            //    "界面语言可以单独设"，而实际上调了没反应。
            //    这条判据防它被"顺手加回来"。
            fail += CheckPredicate(projectRoot, "⑫i 「界面语言跟随游戏」开关不得回归（界面语言始终跟随游戏）",
                "Pages\\Pages.cs", NoFollowLangToggle);
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
                new[] { "Features\\VideoRender\\Patches\\VideoRenderClockPatches.cs" },
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
                new[] { "Features\\VideoRender\\Pipeline\\VideoRenderJob.cs" },
                new[] { "RenderClockSource.TranspilerReady && Settings.RenderClockOn" });
            // ⑯ 去特效总开关必须**默认关**（2026-10-17 用户裁决）。
            //
            //    上游 `Settings.EffectRemoverOn = true`，我们有意偏离。理由不是
            //    "保守"，而是**爆炸半径不同**：本项目其它开关都只影响自己的输出
            //    （录什么、渲染成什么），唯独它改的是**关卡内容本身**——打开后
            //    进关看到的谱面已经不是原谱。玩家装这个模组是来录制/回放的，
            //    不是来改谱的；默认开会让"我只想录一局"的人先撞上一次莫名其妙
            //    的谱面变形，而他不会想到是新装的模组干的。
            //
            //    判据必须判**赋值结果**：`= true` 与 `= false` 是同一行代码的
            //    两种状态，子串 `EffectRemoverOn` 永远命中，判"出现过"是假判据。
            fail += CheckPredicate(projectRoot, "⑯ 去特效总开关必须默认关（它改的是关卡内容本身）",
                "State\\Settings.cs", EffectRemoverDefaultsOff);
            // ⑯b 指针叠加必须**默认关**（2026-10-20 用户裁决）。
            //
            //    这一项不是"录得更全"，而是**往画面里加东西**：指针本来就不在游戏画面里
            //    （Cursor.visible 只藏/显硬件光标），打开后是模组自己画一个上去。成片的默认
            //    语义是"这关本来的样子"，要多出一样东西得玩家自己说要——和 ⑯ 同一条理由。
            //
            //    判法同样必须读**赋值结果**：`= true` 与 `= false` 是同一行代码的两种状态，
            //    判"出现过 RenderCursorOn" 是假判据（注释里就写着这个名字）。
            fail += CheckPredicate(projectRoot, "⑯b 鼠标指针叠加必须默认关（它是往画面里加东西，不是录得更全）",
                "State\\Settings.cs", CursorOverlayDefaultsOff);
            // ⑰ 中文文案里不许把「关卡」称作「存档」（2026-10-17 用户指出：
            //    「存档翻译存在问题 应为 关卡」）。
            //
            //    这不是用词口味问题：本模组语境里的「存档」在玩家那边指**游戏进度**，
            //    而这一项覆盖的是**关卡文件**（.adofai 谱面）。写成「允许覆盖存档」
            //    会让玩家以为它会动自己的过关记录，于是不敢开；或者反过来理解成
            //    "开了更安全"。同一处误导在 README 里也出现过。
            fail += CheckPredicate(projectRoot, "⑰ 中文文案不得把关卡称作存档（会让玩家以为动的是进度存档）",
                "Ui\\LocData.cs", ChineseLocSaysLevelNotSave);
            // ⑰b 「只模拟映射列表中的按键」必须真的生效（2026-10-23 反转）。
            //
            //    **这一段此前是「必须保持删除」**（2026-10-19）。删它的理由是它当时
            //    确实是假开关，而且假得很彻底：
            //      · 文案声称"限制单局记录的按键数量"，而全仓没有收按键盘数的代码；
            //      · Settings.LimitKey 唯一的消费点是 BuildKeyCodeConvertTable 的
            //        `!LimitKey ||` 前置条件，而该方法末尾还有一条
            //        `== 0xFFFF → num` 兜底循环把表整个重填一遍——开关真假只改
            //        中间态，末态**逐字节相同**。
            //
            //    但它的**文案与上游语义**其实是另一件事：「只模拟映射列表中的按键」
            //    （Only simulate the keys in the remapping list）。这是个真需求：
            //    只用手柄/宏键盘少数几个键打谱时，要屏蔽其余所有按键。上游没做成，
            //    因为它把「未映射」与「恒等」抹成了同一件事。
            //
            //    现在用 **0 = 不模拟** 表达（与回放侧 `num != 0` 的跳过条件对齐），
            //    并删掉那两道会把它盖掉的兜底循环。于是判据也要反过来：
            //    不再查"有没有这个字段"，而是查**它是否真的改变末态**。
            //
            //    判据必须查末态：`BuildKeyCodeConvertTable` 里必须出现
            //    `LimitKey ? ... : num` 这个三元（而不是只在前置条件里出现）。
            fail += CheckPredicate(projectRoot, "⑰b 「只模拟列表中的键」必须真的改变映射表末态（不能只改中间态）",
                "State\\Settings.cs", LimitKeyChangesFinalState);
            //    字段与链路必须在。四条各查一个文件——只查一处的话「半删」照样绿：
            //    界面删了、ConfigManager 还在写，玩家手改配置文件仍能改到它。
            fail += Check(projectRoot, "⑰c 设置字段里要有 LimitKey",
                new[] { "State\\Settings.cs" }, new[] { "LimitKey" });
            fail += Check(projectRoot, "⑰d 配置落盘要读写 LimitKey",
                new[] { "State\\ConfigManager.cs" }, new[] { "LimitKey" });
            fail += Check(projectRoot, "⑰e 界面要有 record.keymap.limit 行",
                new[] { "Pages\\Pages.cs" }, new[] { "record.keymap.limit" });
            // ⑰f 兜底循环不得回归。**这条是整段的根**：LimitKey 曾经失效，唯一原因
            //    就是那两道 `== 0xFFFF → num`。只要有人把它加回去，开关立刻变假，
            //    而界面上完全看不出来（拨了没反应）。
            fail += CheckPredicate(projectRoot, "⑰f 映射表构建后不得再有 `0xFFFF → num` 兜底（会把 LimitKey 重新废掉）",
                "State\\Settings.cs", NoIdentityFallbackAfterBuild);
            // ⑰g 显示层不得把正向映射表当反向表用（2026-10-23 用户报「映射 R→Space，回放却亮 R」）。
            //
            //    `ManagedInput.Held[]` 的下标是**已经过映射**的键码
            //    （ReplayPlayer 里 `num = key_code_convert[ev.KeyCode]` 之后才 Note）。
            //    显示层问的是「这颗物理键此刻亮不亮」，只需要 KeyCode→原生 VK。
            //    再查一次同一张表就是反向查表：
            //      映射 R→Space 时，问 R 得 map[82]=32 ⇒ `Held[32]` 为真 ⇒ **R 亮**；
            //      问 Space 得 map[32]=32 ⇒ 也亮（未限制时），或 0 ⇒ 不亮（开 LimitKey 时）。
            //    打开 LimitKey 后表外全填 0，只剩被错误命中的那颗还亮——
            //    现象就是「映射成空格，回放却按 R」，看起来像映射没生效。
            fail += CheckPredicate(projectRoot, "⑰g 键位显示不得再过一次 key_code_convert（Held 已是映射后的域）",
                "Features\\Replay\\Runtime\\KeyViewerBridge.cs", KeyViewerDoesNotRemapAgain);
            // ⑰h 判定层从 KeyCode 翻键时也必须先换算成原生 VK。
            //
            //    这是 ⑰g 的同族错误，藏在兜底路径里（异步链不可用时才走到）：
            //    `ManagedInput.Matches(InputAction)` 里 `(int)KeyCode.Q` = 113，
            //    而事件存在 VK 81；LeftArrow 276 直接越界。只有 Space/Esc/Enter
            //    因为两套编码同值才一直「碰巧正常」。
            fail += CheckPredicate(projectRoot, "⑰h 从 KeyCode 翻键必须先换算成原生 VK（字母/方向键全错位）",
                "Features\\Replay\\Runtime\\ManagedInput.cs", KeyCodeQueriesConvertToVk);
            // ⑱ 系列（**总开关关掉时子设置必须压暗**）不在这里判：那四条要读
            // `Pages\\Pages.cs`，而它**没有链进本离屏套件**（见 UiPreview.csproj
            // 的链接清单——只链了 Loc.cs 与 Features\\Replay 那批）。放在这里的话
            // CheckPredicate 会因为"找不到文件"直接判红，而那个红是**假红**，
            // 与被测代码无关。
            // ⇒ 它们落在 UiLogicShapeCheck：那个套件本来就在读 Pages.cs（⑫ 用的
            //   就是它），且已在 Driver 里登记。
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

        /// <summary>
        /// 卸载回放的**顺序**是否正确。
        ///
        /// 顺序不是风格问题：回放/录制协程还在跑时清 data，它们下一帧会去读
        /// 已清空的对象（轻则报错重则卡死）；而先翻回录制态再 Stop，Stop 里
        /// 判断"现在是不是在播"的依据已经被改掉，停不下来。
        ///
        /// 判据要求三件事**按位置递增**出现：
        ///   ① StopPlaying / StopRecording  → ② UnLoadReplay → ③ SwitchToRecordMode
        /// </summary>
        private static bool ReplayUnloadIsOrdered(string code)
        {
            string body = MethodBody(code, "UnloadReplay");
            if (body == null) return false;
            int stop = body.IndexOf("StopPlaying", StringComparison.Ordinal);
            int unload = body.IndexOf("UnLoadReplay", StringComparison.Ordinal);
            int back = body.IndexOf("SwitchToRecordMode", StringComparison.Ordinal);
            return stop >= 0 && unload > stop && back > unload;
        }

        /// <summary>
        /// 界面是否在**两个状态分支**都给了卸载入口。
        ///
        /// `DrawPlaybackState` 有两个互斥分支：is_playing（回放中）与空闲态
        /// （含"已装弹未开播"）。只在一处画按钮的话，另一个状态仍然卸不掉——
        /// 而"刚载入就又卸不掉"恰恰是最容易被感觉到的那个。
        ///
        /// **判法不能是"数 DrawUnloadButton 出现几次"**：第一版就是这么写的，
        /// 造变异时把空闲态那处的 `if (PlayMode && data != null)` 改成 `false`
        /// （调用行还在、只是永不执行），次数仍是 2，判据照样绿——**假绿**
        /// （2026-10-17 MUT-U23 实测）。"调用写在死代码里"和"没写"对玩家等价。
        ///
        /// 正确判法：要求两处**都**处在活路径上——
        ///   ① 回放中分支：`is_playing` 块里出现 DrawUnloadButton；
        ///   ② 空闲分支：必须存在 `if (PlayMode && data != null)` 这个**真条件**
        ///      （不是 `false` / `!true` 这类恒假写法），且其后出现 DrawUnloadButton。
        /// </summary>
        private static bool HasUnloadEntryPoints(string code)
        {
            string body = MethodBody(code, "DrawPlaybackState");
            if (body == null) return false;

            int n = 0, at = 0;
            while ((at = body.IndexOf("DrawUnloadButton", at, StringComparison.Ordinal)) >= 0)
            {
                n++;
                at += "DrawUnloadButton".Length;
            }
            if (n < 2) return false;

            // 空闲分支的门必须是"真条件"：不能是恒假写法。
            // 找 `if (PlayMode && data != null)` 这一行，且它**不是**
            // `if (false)` / `if (!true)` 这类被改死的样子。
            foreach (string raw in body.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("if (", StringComparison.Ordinal)) continue;
                if (!line.Contains("PlayMode", StringComparison.Ordinal)) continue;
                if (!line.Contains("data != null", StringComparison.Ordinal)) continue;
                // 排掉恒假伪装
                if (line.Contains("false", StringComparison.Ordinal)) continue;
                if (line.Contains("!true", StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 异步输入开关是否**真的先检测了游戏侧状态**。
        ///
        /// 三件必须同时成立：
        ///   ① 存在 `RDC.useAsyncInput` 的读取（游戏侧权威判据）；
        ///   ② 该读取处在 try/catch 里——游戏未初始化时读它会抛，
        ///      不能因为一个提示行把整个设置页带走；
        ///   ③ 未开时真的画出提示（`record.asyncinput.off`），
        ///      而不是只读一下就算了。
        ///
        /// 只查 ① 不够：读了不提示 = 玩家依然不知道为什么不生效。
        /// </summary>
        private static bool AsyncInputChecksGameSide(string code)
        {
            string body = MethodBody(code, "DrawAsyncInputRow");
            if (body == null) return false;
            return body.Contains("RDC.useAsyncInput", StringComparison.Ordinal)
                && body.Contains("catch", StringComparison.Ordinal)
                && body.Contains("record.asyncinput.off", StringComparison.Ordinal);
        }

        /// <summary>
        /// 「界面语言跟随游戏」开关不得回归。
        ///
        /// 判法：`record.followlang` 这个文案键不得再被用作**控件**。
        /// 只查"键名出现过"会把注释里的说明文字也算进去（Pages.cs 与 Loc.cs
        /// 都留着"为什么删掉它"的解释），所以这里要求的是
        /// 「`OnGui.Row("record.followlang"` 这种控件调用」为零。
        /// </summary>
        private static bool NoFollowLangToggle(string code)
        {
            foreach (string raw in code.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("*", StringComparison.Ordinal)) continue;
                if (line.Contains("Row(\"record.followlang\"", StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 去特效总开关是否**默认关**。
        ///
        /// 判法必须是「找到声明并读它等号右边」，不能判子串：
        /// `public static bool EffectRemoverOn` 这一截在 `= true` 与 `= false`
        /// 两种状态下完全一样，判"出现过"的判据在默认值被改回 true 时照样绿
        /// （这正是本项目反复栽的「判出现过 ≠ 判生效着」）。
        /// 同时排除注释行——解释"为什么默认关"的注释里必然写着 true。
        /// </summary>
        private static bool EffectRemoverDefaultsOff(string code)
        {
            foreach (string raw in code.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("*", StringComparison.Ordinal)) continue;
                // 声明行：public static bool EffectRemoverOn = ...
                if (!line.Contains("bool EffectRemoverOn", StringComparison.Ordinal))
                    continue;
                // 只认**赋值过**的那一行（字段声明本身带初值）；不带 = 的是分号结尾
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string rhs = line.Substring(eq + 1).Trim();
                // 去掉行尾注释残留与分号
                if (rhs.StartsWith("true", StringComparison.Ordinal)) return false;
                if (rhs.StartsWith("false", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 鼠标指针叠加是否**默认关**（2026-10-20 用户裁决）。
        ///
        /// 理由与 ⑯ 同源：这一项不是"录得更全"，而是往画面里加东西。指针本来就不在
        /// 游戏画面里，打开后是模组自己画一个上去；成片的默认语义是"这关本来的样子"。
        ///
        /// 判法与 <see cref="EffectRemoverDefaultsOff"/> 一致：找声明行读等号右边，
        /// 不判子串、跳过注释行——否则把默认值改回 true 时判据照样绿。
        /// </summary>
        private static bool CursorOverlayDefaultsOff(string code)
        {
            foreach (string raw in code.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("*", StringComparison.Ordinal)) continue;
                if (!line.Contains("bool RenderCursorOn", StringComparison.Ordinal))
                    continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string rhs = line.Substring(eq + 1).Trim();
                if (rhs.StartsWith("true", StringComparison.Ordinal)) return false;
                if (rhs.StartsWith("false", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 中文文案里是否把关卡错叫成「存档」。
        ///
        /// 判法：中文字符串字面量里不得出现「存档」。**只查中文表**——英文表
        /// 说的是 saves / overwriting，没有这个歧义。
        ///
        /// 为什么值得一条判据：这不是文风问题。「存档」在玩家语境里指游戏
        /// 进度（打到第几关），而这一项覆盖的是关卡文件本身。写成
        /// 「允许覆盖存档」会让人以为它会动自己的进度记录。
        /// </summary>
        private static bool ChineseLocSaysLevelNotSave(string code)
        {
            // 剥注释已在 CheckPredicate 里做过；这里按行找中文字面量。
            foreach (string raw in code.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("*", StringComparison.Ordinal)) continue;
                // 只判含中文字符的行（英文表整行 ASCII）
                bool hasCjk = false;
                foreach (char c in line)
                {
                    if (c >= 0x4E00 && c <= 0x9FFF) { hasCjk = true; break; }
                }
                if (!hasCjk) continue;
                if (line.Contains("存档", StringComparison.Ordinal)) return false;
            }
            return true;
        }


        /// <summary>
        /// <c>ApplyKeyRemap</c> 必须把三件事都做完。
        ///
        /// 这条钉的是**方法体形状**，不是"Pages.cs 里出现过这个函数名"：
        ///   · 只序列化不建表 ⇒ 界面显示变了、实际映射还是旧的（当场不生效）；
        ///   · 只建表不序列化 ⇒ 当场生效、下次启动全部丢失（玩家会以为设置没保存）；
        ///   · 不落盘 ⇒ 同上。
        ///
        /// 三者的顺序不重要（先建表后写 JSON 也能工作），但**一个都不能少**。
        /// </summary>
        /// <summary>
        /// 键位重映射的界面是否**真的接线**（不是只有方法定义 / 只有文案键）。
        ///
        /// 三样缺一都会退化成「能拨但改不了对照」：
        ///   · 总开关：必须是 <c>OnGui.Row("record.keymap.on"</c> 这个控件调用；
        ///   · 对照表：必须是 <c>DrawKeyRemapList();</c> 这个**调用**
        ///     （方法定义本身会让裸名字匹配成立，MUT-KM1 就是这么被抓出来的）；
        ///   · 写回：必须是 <c>ApplyKeyRemap();</c> 调用。
        /// </summary>
        private static bool RemapUiWired(string code)
        {
            bool hasToggle = code.Contains("OnGui.Row(\"record.keymap.on\"", StringComparison.Ordinal);
            bool hasList = HasCall(code, "DrawKeyRemapList");
            bool hasApply = HasCall(code, "ApplyKeyRemap");
            return hasToggle && hasList && hasApply;
        }

        /// <summary>
        /// 源码里是否有 `<paramref name="method"/>();` 这种**调用**（不含方法定义）。
        /// 定义形如 `... DrawKeyRemapList()`，它后面不是分号而是 `{` 或换行，所以
        /// 只认「名字 + () + 分号」即可把定义排除掉。
        /// </summary>
        private static bool HasCall(string code, string method)
        {
            // 定义行形如 `private static void DrawKeyRemapList()`——后面不是分号，
            // 因此「名字 + () + ;」只可能命中真正的调用点。
            return code.Contains(method + "();", StringComparison.Ordinal);
        }

        private static bool RemapAppliesAllThree(string code)
        {
            string body = MethodBody(code, "ApplyKeyRemap");
            if (body == null) return false;
            return body.Contains("KeyCodeConvert = ", StringComparison.Ordinal)
                && body.Contains("BuildKeyCodeConvertTable", StringComparison.Ordinal)
                && body.Contains("Commit(", StringComparison.Ordinal);
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
        /// 可读扩展名清单（<c>ReadableExtensions</c>）的数组块里，两种新格式是否都在。
        ///
        /// **只看数组块内部**：这两个常量在文件里另有用法
        /// （<c>EndsWith(ReplayConstants.ExtensionNoEncrypt, StringComparison…)</c>），
        /// 全文搜索会把"用了它"当成"清单里有它"——MUT-U46 就是这么漏过的。
        /// 做法：定位 <c>ReadableExtensions</c> 之后的第一个 <c>{</c>，截到与之配对的
        /// <c>}</c>，只在这个片段里找两个常量。
        /// </summary>
        private static bool ReadableListHasBothFormats(string code)
        {
            int decl = code.IndexOf("ReadableExtensions", StringComparison.Ordinal);
            if (decl < 0) return false;
            int open = code.IndexOf('{', decl);
            if (open < 0) return false;
            int close = code.IndexOf('}', open + 1);
            if (close < 0) return false;
            string block = code.Substring(open + 1, close - open - 1);
            return block.Contains("ReplayConstants.Extension", StringComparison.Ordinal)
                && block.Contains("ReplayConstants.ExtensionNoEncrypt", StringComparison.Ordinal);
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

        /// <summary>
        /// 反向判：这些符号**一个都不许**出现在该文件的代码行里。
        ///
        /// 与 <see cref="Check"/> 成对使用——只判"必须在"防不住"旧的又加回来"：
        /// 本项目删掉的每一处都补了一条反向判据，否则下一个人看到"格式少了两个"
        /// 又会顺手补回去（语言开关那次就是这么复发的）。
        /// </summary>
        private static int CheckAbsent(string projectRoot, string name, string rel, string[] symbols)
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
                if (HasCodeUse(text, s))
                {
                    Console.WriteLine("    ✗ " + name + "：" + rel + " 里仍有 " + s);
                    return 1;
                }
            }
            return 0;
        }

        /// <summary>
        /// <c>LimitKey</c> 是否**真的改变映射表末态**。
        ///
        /// 这条钉的是 2026-10-19 那次误判的根因：当时的实现只在**前置条件**里用到它
        /// （`!LimitKey ||` 决定先填恒等还是留哨兵），而函数末尾的 `== 0xFFFF → num`
        /// 兜底循环把两种中间态重写成同一个末态，开关因此完全无效。
        /// 只查「LimitKey 出现过」会替那个错误设计背书——**必须查它参与末态赋值**。
        ///
        /// 判据形态：方法体里同时出现
        ///   · `LimitKey ?`（三元参与赋值，而不是只在 if 条件里）
        ///   · `(ushort)0`（「不模拟」的表示）
        /// </summary>
        /// <summary>
        /// 键位显示层不得再查一次重映射表。
        ///
        /// `ManagedInput.Held[]` 的下标是**映射后**的键码；显示层问的是物理键亮不亮，
        /// 只需 KeyCode→原生 VK。再查 `Settings.key_code_convert` 等于把正向表当反查，
        /// 映射 R→Space 时会亮成 R（见 ⑰g 的推导）。
        /// </summary>
        private static bool KeyViewerDoesNotRemapAgain(string code)
        {
            string conv = MethodBody(code, "ToNativeVk");
            if (conv == null) return false;
            // 换算函数本身不该碰映射表
            if (conv.Contains("key_code_convert", StringComparison.Ordinal)) return false;

            string post = MethodBody(code, "GetKeyPostfix");
            if (post == null) return false;
            // 取下标的那处也不能碰映射表（历史上就是这里 `return Settings.key_code_convert[native];`）
            return !post.Contains("key_code_convert", StringComparison.Ordinal)
                && post.Contains("ToNativeVk(", StringComparison.Ordinal);
        }

        /// <summary>
        /// `ManagedInput` 里凡是从 <see cref="KeyCode"/> 取下标的地方，都必须先经
        /// `ToNativeVk` 换算。钉的是 `Any(ButtonState, int, KeyCode)` 这一个入口——
        /// 所有 InputAction 查询都从这里走。
        /// </summary>
        private static bool KeyCodeQueriesConvertToVk(string code)
        {
            string body = MethodBody(code, "Any");
            if (body == null) return false;
            // 必须调 ToNativeVk，且不能直接把 KeyCode 强转当下标
            return body.Contains("ToNativeVk(", StringComparison.Ordinal)
                && !body.Contains("int index = (int)vk;", StringComparison.Ordinal);
        }

        private static bool LimitKeyChangesFinalState(string code)
        {
            string body = MethodBody(code, "BuildKeyCodeConvertTable");
            if (body == null) return false;
            return body.Contains("LimitKey ?", StringComparison.Ordinal)
                && body.Contains("(ushort)0", StringComparison.Ordinal);
        }

        /// <summary>
        /// 映射表构建之后不得再有把「未赋值」重写成「恒等」的兜底循环。
        ///
        /// 那两道循环（`BuildKeyCodeConvertTable` 末尾与 `check()` 里各一条）
        /// 是 <c>LimitKey</c> 曾经彻底失效的**唯一原因**：限制模式把表外的键写成 0
        /// （不模拟），兜底随即把它们逐项改回 num（恒等），末态与不限制时逐字节相同。
        ///
        /// 现在 `BuildKeyCodeConvertTable` 自己保证末态没有 0xFFFF，
        /// 因此这个写法在任何地方都不该再出现。
        /// </summary>
        private static bool NoIdentityFallbackAfterBuild(string code)
        {
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string cur = lines[i].TrimStart();
                if (cur.StartsWith("//", StringComparison.Ordinal)
                    || cur.StartsWith("*", StringComparison.Ordinal)) continue;
                if (!cur.Contains("0xFFFF", StringComparison.Ordinal)) continue;
                // 同行就是恒等赋值
                if (cur.Contains("key_code_convert[", StringComparison.Ordinal)
                    && cur.Contains("] = num", StringComparison.Ordinal)) return false;
                // 拆成「if (...)」+ 紧随的赋值行
                for (int j = i + 1; j < Math.Min(i + 4, lines.Length); j++)
                {
                    string t = lines[j].TrimStart();
                    if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (t.Contains("key_code_convert[num] = num", StringComparison.Ordinal)) return false;
                    if (t.Contains("key_code_convert[i] = i", StringComparison.Ordinal)) return false;
                }
            }
            return true;
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
