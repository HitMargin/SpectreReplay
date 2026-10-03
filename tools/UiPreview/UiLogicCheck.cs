// UI 逻辑修复的回归断言（用户报「你这个UI逻辑不合理」那一轮的 11 条）。
//
// ── 这一组守的是什么 ─────────────────────────────────────────
// 用户看到的毛病按"严重"排是这样的：
//   1. **勾了没反应**：设置页上有些开关点下去什么都不会发生。
//   2. **状态说不清**：录制进行中、渲染进行中、回放已载入——
//      界面都不说，玩家只能去翻日志（而日志里什么也没有）。
//   3. **按钮是摆设**：有一个「开始渲染」按钮，无论在哪种模式下
//      点下去都不会开始任何东西。
//
// 这三类有一个共同点：**代码是"在"的，但没接到界面上**。
// 编译通过、闸门全绿、界面也能画出来，唯独语义是空的。
// 上一轮就是这个状态：116 条断言全绿，11 个界面缺陷一条都没抓到。
//
// ── 为什么这些断言是这个形状 ─────────────────────────────────
// 对"有个开关"这件事，断言很容易写成一句废话：
//   · `grep AsyncInput` → 现在是 0 次（删干净了），但明天有人加回来
//     没人知道；更糟的是**加回来并接上一次**就又是个假开关。
//   · `grep StartManual` → 一直在，永远绿。
// 所以这里的判据一律落在**控制流形状**上，而不是"某字符串出现过"：
//   · 死开关：既不能有定义，也不能有界面行，也不能有配置读写——
//     三处都要没有，否则就是"删了一半"的半吊子状态。
//   · 恒定无效的按钮：必须落在 `if (Mode == Live)` 的**另一支**里。
//     只查"按钮还在"永远绿；只查"有 if"也够——可以把 Live 和 Offline
//     两个分支都放按钮，那还是恒定无效。必须断言 Live 那支里**没有**它。
//   · 状态行：必须**每一个分支都有赋值**，否则 `text` 用到最后是
//     未定义值——那种情况编译器会报，但如果写成 `text ??= "…"`
//     就蒙混过关了。判据取 if/else 链的结构。
//
// ── 这一组管不到什么 ─────────────────────────────────────────
// GUILayout 是**声明式**的：控件的位置与命中由引擎当场算出来，
// 离线复刻它等于重写一遍引擎。所以这里**一条断言都不涉及界面几何**——
// "这几个按钮摆在一起好不好看""点得到点不到"只能真机开一次窗口。
// 这里只钉**代码层面的语义**：哪个控件在哪种条件下才出现、写了什么值。
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class UiLogicCheck
    {
        private const string PagesRel = "Pages/Pages.cs";
        private const string OnGuiRel = "Ui/OnGui.cs";
        private const string SettingsRel = "Config/Settings.cs";
        private const string LocRel = "Ui/Loc.cs";
        private const string RenderServiceRel = "Features/VideoRender/RenderService.cs";
        private const string RenderStatusRel = "Features/VideoRender/RenderStatus.cs";
        private const string OfflineRel = "Features/VideoRender/OfflineSession.cs";
        private const string LiveRel = "Features/VideoRender/LiveSession.cs";

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── UI 逻辑 ──");
            int fail = 0;
            fail += NoDeadSwitches(projectRoot);
            fail += StatusLineCoversEveryCase(projectRoot);
            fail += RenderPageButtonsAreNotDecorative(projectRoot);
            fail += PlaybackPageNeverGoesBlank(projectRoot);
            fail += TextInputDoesNotCommitEveryKeystroke(projectRoot);
            fail += RenderProgressHasARealTotal(projectRoot);
            return fail;
        }

        // ── ① 死开关：定义、界面、配置三处都没有 ──────────────────
        //
        // AsyncInput（"使用高精度输入（SkyHook）"）曾经是一行真实存在的
        // 开关，但它**从来没有被任何代码读过一次**：录制路径走 Win32
        // GetKeyboardState 轮询（KeyRecorder.cs 的文件头写明了为什么），
        // 勾它只是让人以为输入精度被调过了。
        //
        // 为什么要钉"三处都没有"而不是"没定义"：
        //   · 只留配置读写 = 老配置文件里那个字段还在，界面删了但
        //     ConfigStore 还在读它，下一个人会以为它有用；
        //   · 只留界面行 = 界面还能勾，勾了没反应，正是这次要修的毛病。
        private static int NoDeadSwitches(string projectRoot)
        {
            string settings = SrcUtil.Source(projectRoot, SettingsRel);
            string pages = SrcUtil.Source(projectRoot, PagesRel);
            string loc = SrcUtil.Source(projectRoot, LocRel);
            int fail = 0;

            if (settings.Length == 0 || pages.Length == 0 || loc.Length == 0)
            {
                Console.WriteLine("    ✗ ① 读不到 Settings/Pages/Loc 源码");
                return 1;
            }

            // (a) 字段定义：形如 `internal static bool AsyncInput`
            if (Regex.IsMatch(settings,
                @"(internal|private|public)\s+static\s+bool\s+AsyncInput\b"))
            {
                Console.WriteLine("    ✗ ①a Settings 里还有 AsyncInput 字段"
                    + "\n      它没有写入点：勾它什么都不会发生");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ①a AsyncInput 字段已删除");
            }

            // (b)(c) 配置读写：字段删了之后这两处也必须跟着删。
            //     半删最糟——界面没了、字段没了，读写还在，读它只会
            //     静默拿默认值，下一个人会以为它"还在兼容旧配置"。
            int cfgFail = 0;
            if (Regex.IsMatch(settings, @"GetBool\s*\(\s*d\s*,\s*""AsyncInput"""))
            {
                Console.WriteLine("    ✗ ①b ConfigStore 还在读 AsyncInput"
                    + "\n      字段已删，读它会静默拿默认值");
                cfgFail++;
            }
            if (Regex.IsMatch(settings, @"\""AsyncInput\"""))
            {
                Console.WriteLine("    ✗ ①c ConfigStore 还在写 AsyncInput");
                cfgFail++;
            }
            fail += cfgFail;
            if (cfgFail == 0)
            {
                Console.WriteLine("    ✓ ①b①c 配置读写里的 AsyncInput 也清干净了");
            }

            // (d)(e) 界面行与文案键：留着就是"能勾但不生效"或孤儿键。
            int uiFail = 0;
            if (pages.Contains("record.asyncinput"))
            {
                Console.WriteLine("    ✗ ①d 录制页还在画 AsyncInput 那一行"
                    + "\n      开关没了但按钮还在 = 玩家还能勾一个不存在的东西");
                uiFail++;
            }
            if (loc.Contains("record.asyncinput"))
            {
                Console.WriteLine("    ✗ ①e Loc 表里还有 record.asyncinput（无人引用）");
                uiFail++;
            }
            fail += uiFail;
            if (uiFail == 0)
            {
                Console.WriteLine("    ✓ ①d①e 界面与文案里都没有它了");
            }
            return fail;
        }

        // ── ② 顶栏状态行：每个分支都有话可说 ─────────────────────
        //
        // 原来的 DrawStatus 是这样写的：
        //
        //     bool rendering = state != Idle && state != Done && state != Failed;
        //     if (!rendering) { return; }              // ← 只剩渲染时才有字
        //     GUILayout.Label(...);
        //
        // 于是**开着录制打一整局，顶部一片空白**——"我明明勾了录制"。
        // 而 status.idle / status.recording / status.playing 这几条文案
        // 早就写好了，只是从来没被引用过。
        //
        // 判据取"if/else 链每个分支都赋值 text"：写成
        // `text ??= Loc.T("status.idle")` 那种兜底也能让玩家看到字，
        // 但那样"渲染中"和"什么都不干"就显示同一句话了——不算修好。
        private static int StatusLineCoversEveryCase(string projectRoot)
        {
            string src = SrcUtil.Source(projectRoot, OnGuiRel);
            if (src.Length == 0)
            {
                Console.WriteLine("    ✗ ② 读不到 Ui/OnGui.cs");
                return 1;
            }
            int start = src.IndexOf("DrawStatus", StringComparison.Ordinal);
            if (start < 0)
            {
                Console.WriteLine("    ✗ ② 找不到 DrawStatus");
                return 1;
            }
            string body = src.Substring(start, Math.Min(3000, src.Length - start));

            // (a) 不许再有"只在渲染时才画"的早退
            if (Regex.IsMatch(body,
                @"if\s*\(\s*!\s*rendering\s*\)\s*\{\s*return;"))
            {
                Console.WriteLine("    ✗ ②a DrawStatus 仍然是 `if (!rendering) return;`"
                    + "\n      不渲染时顶部什么都不画：录制中/回放中/空闲全都没有提示");
                return 1;
            }
            Console.WriteLine("    ✓ ②a 不再是「只在渲染时才画」");

            // (b) 四个来源都要被问一句：渲染中、录制中、回放中、两个总开关全关。
            //     少一个就有一类情况回到"顶部空白"。
            string[] branches =
            {
                "rendering",                                     // 渲染中
                "KeyRecorder.IsRecording",                       // 录制中
                "PlaybackEngine.IsPlaying",                      // 回放中
                "status.all_off",                                // 两个开关都关
            };
            int fail = 0;
            foreach (var b in branches)
            {
                if (!body.Contains(b))
                {
                    Console.WriteLine("    ✗ ②b DrawStatus 没处理「" + b + "」这一种情况");
                    fail++;
                }
            }
            if (fail == 0)
            {
                Console.WriteLine("    ✓ ②b 渲染中 / 录制中 / 回放中 / 全关，四种都有话说");
            }

            // (c) 每条文案都要真的用上，否则加了也是白加（LocCheck 的
            //     "未用键"清单里它们挂了很久）。
            foreach (var key in new[]
            {
                "status.recording", "status.playing",
                "status.pending_short", "status.idle",
            })
            {
                if (!body.Contains(key))
                {
                    Console.WriteLine("    ✗ ②c DrawStatus 没引用 " + key);
                    fail++;
                }
            }
            if (fail == 0)
            {
                Console.WriteLine("    ✓ ②c 四条状态文案都被引用了（不再是孤儿键）");
            }
            return fail;
        }

        // ── ③ 渲染页的按钮不是摆设 ───────────────────────────────
        //
        // 原版有两个按钮，无条件地画在页面顶部：
        //     Button("render.start",  → RenderService.StartManual())
        //     Button("render.cancel", → RenderService.Cancel())
        //
        // 「开始渲染」在**两种模式下都是恒定无效的**：
        //   · Live 模式：进关那一刻 RenderService.OnLevelStart() 已经
        //     Begin() 了，而 StartManual 里 `if (!LiveSession.IsRunning)
        //     Begin()` 于是直接返回 —— 点它什么也不会发生；
        //   · Offline 模式：它**静默**取 mtime 最新的那一份 .srp。
        //     玩家无从知道自己点下去渲染的是哪一局，等出来才发现不对，
        //     几分钟已经没了。
        // 而「取消」常年摆着，在不渲染的时候按下去走的是
        // `OfflineSession.Cancel()`（空转）。
        //
        // 现在：Live 支只留说明文字；Offline 支先写出"将渲染这份回放：X"
        // 再给按钮；取消只在真的在跑时画。
        private static int RenderPageButtonsAreNotDecorative(string projectRoot)
        {
            string pages = SrcUtil.Source(projectRoot, PagesRel);
            string service = SrcUtil.Source(projectRoot, RenderServiceRel);
            if (pages.Length == 0 || service.Length == 0)
            {
                Console.WriteLine("    ✗ ③ 读不到 Pages.cs 或 RenderService.cs");
                return 1;
            }
            int start = pages.IndexOf("DrawRender", StringComparison.Ordinal);
            if (start < 0)
            {
                Console.WriteLine("    ✗ ③ 找不到 DrawRender");
                return 1;
            }
            string body = pages.Substring(start, Math.Min(4000, pages.Length - start));
            int fail = 0;

            // (a) 「开始渲染」必须落在一个分支里。判据：分支里先判了模式。
            var liveBranch = Regex.Match(body,
                @"if\s*\(\s*live\s*\)\s*\{(?<then>[^{}]*(?:\{[^{}]*\}[^{}]*)*)\}\s*else\s*\{");
            if (!liveBranch.Success)
            {
                Console.WriteLine("    ✗ ③a 渲染页没有按渲染模式分支"
                    + "\n      「开始渲染」必须只出现在离线那一支：实时模式进关即自动开录，"
                    + "手动按钮点了永远不会发生任何事");
                fail++;
            }
            else
            {
                string then = liveBranch.Groups["then"].Value;
                if (then.Contains("render.start") || then.Contains("StartManual"))
                {
                    Console.WriteLine("    ✗ ③a 实时分支里仍有「开始渲染」"
                        + "\n      OnLevelStart() 已在进关时 Begin()，这个按钮恒定无效："
                        + "玩家点一下没反应，只会当成渲染坏了");
                    fail++;
                }
                else
                {
                    Console.WriteLine("    ✓ ③a 实时模式不画「开始渲染」（进关即自动开录）");
                }
            }

            // (b) 离线分支必须先把目标文件说出来。没有这一步，玩家就是在
            //     对一个隐式的"最新一份"按下按钮。
            int elseStart = liveBranch.Success ? liveBranch.Index : -1;
            string offline = elseStart > 0 ? body.Substring(elseStart) : "";
            if (!offline.Contains("render.offline_target"))
            {
                Console.WriteLine("    ✗ ③b 离线分支没有写出将要渲染哪一份回放"
                    + "\n      LatestReplay() 按 mtime 静默取最新，玩家无从选择、也无从核对");
                fail++;
            }
            else if (!offline.Contains("render.start"))
            {
                Console.WriteLine("    ✗ ③b 离线分支写了目标却没有按钮");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ③b 离线模式先写明目标文件，再给按钮");
            }

            // (c) 没有可渲染的文件时，只说明、不给一个必然失败的按钮。
            if (!offline.Contains("render.offline_noreplay"))
            {
                Console.WriteLine("    ✗ ③c 没有回放文件时没有专门提示"
                    + "\n      那时按钮点了只会跳出一句 Failed + 找不到回放");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ③c 没有可渲染的回放时只说明、不给空按钮");
            }

            // (d) 取消只在真的在跑时画。判据看 IsRunning 有没有被问。
            if (!Regex.IsMatch(body,
                @"if\s*\(\s*running\s*\)\s*\{[^}]*render\.cancel"))
            {
                Console.WriteLine("    ✗ ③d 「取消」不是按会话状态给的"
                    + "\n      不渲染时它照样摆在那儿，按下去走的是空转的 Cancel()");
                fail++;
            }
            else if (!body.Contains("OfflineSession.IsRunning")
                || !body.Contains("LiveSession.IsRunning"))
            {
                Console.WriteLine("    ✗ ③d running 没同时问实时与离线两个会话"
                    + "\n      漏一个就有一边渲染时看不到取消");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ③d 「取消」只在真的在渲染时出现");
            }

            // (e) 服务层：StartManual 必须**如实报告**有没有启动什么。
            //     返回 void 的旧版做不到这件事——UI 只能假装成功。
            if (!Regex.IsMatch(service,
                @"internal\s+static\s+bool\s+StartManual\s*\(\s*\)"))
            {
                Console.WriteLine("    ✗ ③e RenderService.StartManual 还是 void"
                    + "\n      调用方无法知道这次点击到底有没有开始渲染");
                fail++;
            }
            else if (!Regex.IsMatch(service,
                @"if\s*\(\s*Settings\.Mode\s*==\s*Settings\.RenderMode\.Live\s*\)\s*\{\s*return\s+false;"))
            {
                Console.WriteLine("    ✗ ③e StartManual 的实时分支没有如实返回 false"
                    + "\n      实时模式下它没有任何手动语义，必须明说「什么都没干」");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ③e StartManual 返回 bool，实时分支如实报告未启动");
            }
            return fail;
        }

        // ── ④ 回放页不会一片空白 ────────────────────────────────
        //
        // DrawPlaybackState 开头是 `if (!IsPlaying) return;`，于是
        // 「当前」这一组在没载入回放时**整块不画**。玩家看到的界面是
        // "这页上面什么都没有"，分不清是没载入、载入了没生效、还是
        // 校验把它拒了——而这三件事的处理方式完全不同。
        // Load 失败时错误只写进 Player.log，界面上一个字都没有。
        private static int PlaybackPageNeverGoesBlank(string projectRoot)
        {
            string pages = SrcUtil.Source(projectRoot, PagesRel);
            if (pages.Length == 0)
            {
                Console.WriteLine("    ✗ ④ 读不到 Pages.cs");
                return 1;
            }
            // ⚠️ 定位要用**定义**那一处，不能用 IndexOf("DrawPlaybackState")：
            // DrawPlayback 里有一句 DrawPlaybackState(); 在定义之前，
            // 从那儿截出来的 2000 字符根本不含函数体，判据会一直红。
            int start = pages.IndexOf(
                "private static void DrawPlaybackState", StringComparison.Ordinal);
            if (start < 0)
            {
                Console.WriteLine("    ✗ ④ 找不到 DrawPlaybackState 的定义");
                return 1;
            }
            string body = pages.Substring(start, Math.Min(2000, pages.Length - start));
            int fail = 0;

            // (a) 分组标题必须画在早退**之前**，否则空状态整组消失。
            int groupAt = body.IndexOf("playback.current", StringComparison.Ordinal);
            int retAt = body.IndexOf("return;", StringComparison.Ordinal);
            if (groupAt < 0)
            {
                Console.WriteLine("    ✗ ④a 「当前」分组不见了");
                fail++;
            }
            else if (retAt >= 0 && retAt < groupAt)
            {
                Console.WriteLine("    ✗ ④a 「当前」分组在早退之后才画"
                    + "\n      没载入回放时这一组整块消失，玩家只看到空白");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ④a 「当前」分组无条件绘制");
            }

            // (b) 空状态与失败状态各自有自己的话。
            //
            // **两半都要钉，而且要钉"取用"而不是"出现"。**
            // 只查 `playback.idle_err` 在不在会放过一种典型半成品：
            // 把空闲文案改成一个拼错的键（`playback.idle_x`），
            // 界面于是显示原始键名。那样 LocCheck 会红——可它红的是
            // "漏翻文案"，不是"空状态没话说"，看起来像另一回事，
            // 修的时候容易只去补 Loc 表，把真正的逻辑问题留着。
            // 所以这里额外要求：空状态那条分支**取用的就是**
            // `playback.idle`，而不是别的键。
            if (!body.Contains("playback.idle_err"))
            {
                Console.WriteLine("    ✗ ④b 没载入/载入失败时没有对应提示"
                    + "\n      LastError 之前只进日志，界面上看不到");
                fail++;
            }
            else if (!Regex.IsMatch(body,
                @"LastError[\s\S]{0,200}playback\.idle_err|playback\.idle_err[\s\S]{0,200}LastError"))
            {
                Console.WriteLine("    ✗ ④b 载入失败没有把 LastError 摆出来");
                fail++;
            }
            else if (!Regex.IsMatch(body,
                @"\?\s*Loc\.T\(\s*""playback\.idle""\s*\)"
                + @"|\?\s*Loc\.F\(\s*""playback\.idle_err"""))
            {
                Console.WriteLine("    ✗ ④b 空状态与失败状态没有各自取用自己的文案"
                    + "\n      三元两边必须分别是 idle 与 idle_err；"
                    + "\n      用别的键（哪怕是拼错后去补 Loc 表）仍然是错的");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ④b 空状态说「没载入」、失败状态摆出 LastError");
            }

            // (c) 载入失败必须真的能被看见 ⇒ LastError 不能在 Stop 里被清掉。
            //     这里只钉"界面读了它"，读取语义由 ④b 覆盖。
            return fail;
        }

        // ── ⑤ 文本框不按每一个键提交一次 ─────────────────────────
        //
        // 旧版 RowInput 每敲一个字符就调一次 onChange → Pages.Commit →
        // Settings.Commit → ConfigStore.Save() + PatchLoader.Refresh()。
        // 后果不是"多个循环"那么简单：
        //   · 玩家想整个删掉路径（Ctrl+A 再退格）时，中途每一个中间态
        //     都会被存下来，其中某一个还可能被立刻用掉（VideoDir 立刻
        //     变成一个不存在的目录）；
        //   · 每次存都写一次 _dirty，磁盘写入被按键频率放大；
        //   · 按什么键改什么键与文本框共存时，中间态会漏进
        //     KeyboardSimulation 的按键判定。
        //
        // 判据分两头：回调签名不再接 Action（草稿和已提交值分开），
        // 且提交只在回车时发生。
        private static int TextInputDoesNotCommitEveryKeystroke(string projectRoot)
        {
            string ongui = SrcUtil.Source(projectRoot, OnGuiRel);
            if (ongui.Length == 0)
            {
                Console.WriteLine("    ✗ ⑤ 读不到 Ui/OnGui.cs");
                return 1;
            }
            int start = ongui.IndexOf("RowInput", StringComparison.Ordinal);
            if (start < 0)
            {
                Console.WriteLine("    ✗ ⑤ 找不到 RowInput");
                return 1;
            }
            string body = ongui.Substring(start, Math.Min(1600, ongui.Length - start));
            int fail = 0;

            // (a) 每键回调必须消失。写成 `if (now != _textDraft) onChange(now)`
            //     就是旧版那个坑。
            if (Regex.IsMatch(body,
                @"Action<string>\s+on(Change|Commit)"))
            {
                Console.WriteLine("    ✗ ⑤a RowInput 还接每键回调"
                    + "\n      每敲一个字符提交一次：中间态会被存下来并立刻用掉");
                fail++;
            }
            else if (!Regex.IsMatch(body, @"_textDraft\s*="))
            {
                Console.WriteLine("    ✗ ⑤a RowInput 不写草稿字段，改动无处可放");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ⑤a 改动只落草稿，没有每键回调");
            }

            // (b) 回车判据必须认控件名**常量**，不能写死字面量。
            //     RowInput 里用的是 const RowInputControlName；只查
            //     GetNameOfFocusedControl 会放过"两边名字对不上"那种
            //     死代码——回车永远不成立，而且编译照过、界面照画。
            if (!body.Contains("CommitText"))
            {
                Console.WriteLine("    ✗ ⑤b 没有提交路径（回车/关窗口都没接上）");
                fail++;
            }
            else if (!body.Contains("EventType.KeyDown")
                || !body.Contains("KeyCode.Return"))
            {
                Console.WriteLine("    ✗ ⑤b 没有回车提交的判据"
                    + "\n      没有提交入口的话玩家打一半关窗口就丢了");
                fail++;
            }
            else if (!Regex.IsMatch(body,
                @"GetNameOfFocusedControl\s*\(\s*\)\s*==\s*RowInputControlName"))
            {
                Console.WriteLine("    ✗ ⑤b 回车判据没有拿焦点控件名去比 RowInputControlName"
                    + "\n      不比就可能把回车算到别的控件上；写死字面量则两边"
                    + "改名时必有一边对不上，回车永远不成立");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ⑤b 回车（或失焦）才提交，且认了焦点");
            }

            // (c) 控件名不得长得像文案键。
            //     LocCheck.CollectUsedKeys() 的正则
            //     `"([a-z][a-z0-9]*\.[a-z0-9_.]+)"` 会把
            //     SetNextControlName("模块.名字") 里的点号名当成漏翻的文案键。
            //     这不是洁癖：闸门真的会红，而红的原因极难一眼看懂
            //     （报的是"两种语言都没有 spectrereplay.outdir"）。
            //     注意这里读的是**剥掉注释之后**的源码：LocCheck 读的是
            //     原文，所以在注释里举例同一个名字照样会把它误伤。
            var ctrlName = Regex.Match(ongui,
                @"private\s+const\s+string\s+RowInputControlName\s*=\s*""(?<n>[^""]*)""");
            if (!ctrlName.Success)
            {
                Console.WriteLine("    ✗ ⑤c 找不到 RowInputControlName"
                    + "（回车认焦点没有依据，两边改名会各改一处）");
                fail++;
            }
            else
            {
                string n = ctrlName.Groups["n"].Value;
                if (Regex.IsMatch(n, @"^[a-z][a-z0-9]*\."))
                {
                    Console.WriteLine("    ✗ ⑤c 控件名 \"" + n + "\" 含点号"
                        + "\n      LocCheck 会把它当成漏翻的文案键，"
                        + "闸门报「两种语言都没有」——同一个坑已经踩过一次");
                    fail++;
                }
                else if (!ongui.Contains("SetNextControlName(RowInputControlName)"))
                {
                    Console.WriteLine("    ✗ ⑤c 声明了控件名却没有把它交给 SetNextControlName"
                        + "\n      不命名的话 GetNameOfFocusedControl 永远比不中，回车恒不生效");
                    fail++;
                }
                else
                {
                    Console.WriteLine("    ✓ ⑤c 控件名不含点号，也不会与文案键混淆");
                }
            }
            return fail;
        }

        // ── ⑥ 进度条有它依据的总帧数 ─────────────────────────────
        //
        // 原先 RenderStatus 里有个 BarFill 属性负责
        // "有总帧数用帧数比、没有就用 Progress"，但它**零调用点**——
        // 于是离线渲染的进度条从来没被画出来过。
        // 更糟的是那个二选一在实时模式下给出**恒为满格**的结果：
        // 实时的 Progress 是 `帧数 / (已过秒数 × fps)`，即"出帧跟上了
        // 目标帧率没有"，正常跟随时恒等于 1。拿它填进度条会得到一条
        // 永远是满的横杠，比没有更误导。
        //
        // 所以判据是两条：帧数比的那条要有**可判的总帧数**，
        // 而且实时模式的两处写入点必须把总帧数**清零**——不清零的话
        // 上一次离线渲染留下的数字还在，进度条一开录就满格。
        private static int RenderProgressHasARealTotal(string projectRoot)
        {
            string status = SrcUtil.Source(projectRoot, RenderStatusRel);
            string offline = SrcUtil.Source(projectRoot, OfflineRel);
            string live = SrcUtil.Source(projectRoot, LiveRel);
            string pages = SrcUtil.Source(projectRoot, PagesRel);
            if (status.Length == 0 || offline.Length == 0 || live.Length == 0
                || pages.Length == 0)
            {
                Console.WriteLine("    ✗ ⑥ 读不到渲染相关源码");
                return 1;
            }
            int fail = 0;

            // (a) 总帧数必须真有写入点：离线那处 + 实时那处的清零。
            if (!Regex.IsMatch(offline, @"FramesTotal\s*="))
            {
                Console.WriteLine("    ✗ ⑥a 离线渲染不写 FramesTotal"
                    + "\n      没有总数就没有进度条可言");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ⑥a 离线渲染写入 FramesTotal");
            }

            int zeroes = Regex.Matches(live,
                @"RenderStatus\.FramesTotal\s*=\s*0\s*;").Count;
            if (zeroes < 2)
            {
                Console.WriteLine("    ✗ ⑥b 实时录制没有把 FramesTotal 清零（找到 " + zeroes
                    + " 处，需要开录与收尾各一处）"
                    + "\n      不清零的话上一趟离线渲染的总帧数会留着，"
                    + "开始录实时之后进度条立刻满格");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ⑥b 实时录制在开录与收尾各清零一次 FramesTotal");
            }

            // (c) 界面这一侧：判空必须**先看总帧数**，不能靠算出来的比例。
            //     FramesTotal 是 int，除法会把它提升成 float，0/0 得 NaN；
            //     而 Mathf.Clamp01 用 `<` / `>` 比较，NaN 两边都假，
            //     于是 NaN 原样返回，后面 `fill <= 0f` 也为假——
            //     拿 NaN 宽度去 GUI.DrawTexture 会抛 ArgumentException
            //     打断整个 OnGUI。只查 BarFill 不见了抓不到这个。
            if (Regex.IsMatch(pages, @"RenderStatus\.BarFill"))
            {
                Console.WriteLine("    ✗ ⑥c 界面还在用 BarFill"
                    + "\n      那个属性会拿实时的 Progress 当进度，正常跟随时恒为满格");
                fail++;
            }
            else if (!Regex.IsMatch(pages,
                @"RenderStatus\.FramesTotal\s*(\(\))?\s*;\s*if\s*\(\s*total\s*<=\s*0\s*\)"))
            {
                Console.WriteLine("    ✗ ⑥c 进度条没有先判 FramesTotal <= 0"
                    + "\n      int 除法会把 0/0 变成 NaN，Clamp01 的 < 与 > 对 NaN"
                    + "全为假，于是 NaN 一路走到 DrawTexture 抛异常，"
                    + "OnGUI 整帧被打断——恰好在还没开始渲染就翻到这页时发生");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ⑥c 进度条先判 FramesTotal <= 0，只在离线渲染时画");
            }

            // (d) ETA 必须真有**真实**写入点。原先它全仓无人写，于是
            //     Describe() 里那句 `EtaSeconds >= 0 ? …` 永远不成立。
            //
            //     判据不接受"写了个 -1"这种样子：`RenderStatus.EtaSeconds = -1;`
            // 满足 `EtaSeconds\s*=` 却把字段钉死成"没有 ETA"，那正是修复
            //     **之前**的等价物。所以要的是一次带条件的真赋值——
            //     等号右边必须跟着一个判断（`elapsed > …` 之类）。
            if (!Regex.IsMatch(offline,
                @"RenderStatus\.EtaSeconds\s*=\s*(?!\-1\s*;)[^;]*[<>]"))
            {
                Console.WriteLine("    ✗ ⑥d 离线渲染没有真写 EtaSeconds"
                    + "\n      只有一次带条件的赋值才算数；写成 `= -1;` 等于把"
                    + "\n      字段钉死成「没有 ETA」，Describe() 的 ETA 分支仍不会成立");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ⑥d 离线渲染写入 EtaSeconds（ETA 分支终于会成立）");
            }
            return fail;
        }
    }
}