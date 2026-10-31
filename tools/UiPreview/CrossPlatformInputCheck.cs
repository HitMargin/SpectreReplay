// 跨平台回放输入。
//
// 缺陷史（用户三次实测反馈，逐层剥出来的）：
//   ① 首报「跨平台的按键模拟就是屁用没有」——
//      上游只有 SendInput 一条注入路（Windows user32 + 要求窗口聚焦）。游戏真正
//      消费键盘的是托管抽象 RDInput，它遍历每个 RDInputType，最终只落到两条物理输入：
//        · RDInputType_Keyboard      → Input.GetKey*
//        · RDInputType_AsyncKeyboard → AsyncInput.GetKey* → AsyncInputManager.keyMask
//      两条都只有 OS 级真实按键事件才能写入。非 Windows / 未聚焦时回放驱动不了。
//   ② 第二次报「关掉模拟按键后不管异步开不开都没法模拟」——
//      第一版把 Settings.LegacyEngine（「用不用 SendInput」）误当成托管注入的前提，
//      关掉它 = 两条路全灭。那个开关的本意恰恰是躲开 SendInput 与真实键盘冲突。
//   ③ 第三次报「原有模拟按键彻底失效 + 跨平台完全没用」——
//      第二版引入「SendInput 是否可用」这个前提去判断要不要叠加托管状态：
//      平台探针判错（Application.platform）或一次 UIPI 失败置 Broken，
//      SendInput 与托管注入**一起**被关掉。
//
// 教训：**去重不能靠「另一条路的状态」。** 那等于让 A 的可用性决定 B 生死，
// 任何一个判错就同时失效。现在托管状态是回放期间**唯一**输入来源（覆盖），
// SendInput 降级为纯镜像——只影响别的模组的按键显示，成败不改变任何判定。
//
// 判据落在每一层最弱的地方——只验「名字出现过」会被删调用点、留方法骗过。
using System;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class CrossPlatformInputCheck
    {
        private static bool Has(string code, string pattern)
            => Regex.IsMatch(code, pattern);

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 跨平台回放输入 ──");
            string input = SrcUtil.Source(projectRoot, "Features/Replay/Runtime/ManagedInput.cs");
            string patch = SrcUtil.Source(projectRoot, "Features/Replay/Patches/Patch_InputInjection.cs");
            string player = SrcUtil.Source(projectRoot, "Features/Replay/Runtime/ReplayPlayer.cs");
            string kbsim = SrcUtil.Source(projectRoot, "Features/Replay/Runtime/KeyboardSimulation.cs");
            string recorder = SrcUtil.Source(projectRoot, "Features/Replay/Runtime/ReplayRecorder.cs");

            int fail = 0;
            void Expect(bool ok, string msg)
            {
                if (ok) return;
                Console.WriteLine("    ✗ " + msg);
                fail++;
            }

            // ① 三个 RDInput 入口都必须被补，且挂不到 RDInputType 层。
            //    异步输入时 AsyncInputManager.Update 把 keyboardInput.isActive 置 false
            //    （AsyncInputManager.cs:147-163），下沉到那一层 = 异步模式整条不动。
            Expect(Has(patch, @"typeof\(RDInput\)\s*,\s*""GetMain"""),
                "① 没补 RDInput.GetMain —— mainPressCount 恒 0，keyTimes 永不灌入");
            Expect(Has(patch, @"typeof\(RDInput\)\s*,\s*""GetState"""),
                "① 没补 RDInput.GetState —— action1Press/confirmPress 等对回放不可见");
            Expect(Has(patch, @"typeof\(RDInput\)\s*,\s*""GetStateKeys"""),
                "① 没补 RDInput.GetStateKeys —— holdKeys 恒空，holding 恒假，长按会被判死");
            Expect(!patch.Contains("typeof(RDInputType)"),
                "① 补丁挂到了 RDInputType 层 —— 异步输入时该层整条不工作");

            // ② 补丁体必须真改写返回值。
            Expect(Has(patch, @"__result\s*=\s*ManagedInput\.Count\("),
                "② GetMain 的后置没有把托管按键数写进返回值");
            Expect(Has(patch, @"__result\s*=\s*ManagedInput\.Matches\("),
                "② GetState 的后置没有把托管按键状态写进返回值");
            Expect(Has(patch, @"keys\.Clear\(\);\s*ManagedInput\.Append\("),
                "② GetStateKeys 没有先清空再追加 —— 真实键盘的空条目会混进 holdKeys");

            // ③ 覆盖而不是叠加。回到 += 就必须重新判断「另一条路在不在跑」，
            //    那个前提判错会让两条路一起死（前两轮就是这么坏的）。
            Expect(!Has(patch, @"__result\s*\+="),
                "③ 仍有 += 叠加 —— 叠加必须依赖「另一条路在不在跑」，那个前提判错会同时关掉两条路");
            Expect(!patch.Contains("ManagedInput.Enabled"),
                "③ 仍在用 ManagedInput.Enabled 分叉 —— 去重不该靠「另一条路的状态」");

            // ④ Active 只看 is_playing。
            //
            //    任何附加前提（平台、聚焦、SendInput 是否可用）都可能判错而把
            //    回放输入整个关掉，而玩家没有任何线索 —— 那是前几轮踩过的坑。
            //
            //    曾经这里接过「模拟按键」开关（Settings.LegacyEngine）。那个开关
            //    已删除：它关掉后并非「只播不注入」，而是让长条判定失效、键位显示
            //    变暗，属于「关掉就坏」的假开关。这条判据防的是「又把它接回来」。
            Expect(input.Contains("internal static bool Active => is_playing;"),
                "④ ManagedInput.Active 不是「只看 is_playing」—— 又被绑上了可能判错的前提");
            Expect(!Regex.IsMatch(input, @"Active[^;]*KeyboardSimulation"),
                "④ Active 里出现 KeyboardSimulation —— 平台/失败标记被用来决定注不注入");
            Expect(!Regex.IsMatch(input, @"Active[^;]*Application\.isFocused"),
                "④ Active 里出现 Application.isFocused —— 窗口失焦时回放输入会被整个关掉");

            // ⑤ 原有路径不得被削弱：ReplayPlayer 的发送条件不能出现平台探针或失败标记。
            Expect(!player.Contains("KeyboardSimulation.Available") && !player.Contains("KeyboardSimulation.Broken"),
                "⑤ ReplayPlayer 的发送条件被平台/失败标记污染 —— 原有按键模拟会被新判据削弱");
            Expect(player.Contains("ManagedInput.Mirror"),
                "⑤ ReplayPlayer 的镜像条件不是 ManagedInput.Mirror");

            // ⑤b SendInput 的 dll 缺失守卫必须是**异常闩锁**，不是平台判断。
            //     user32 在非 Windows 上不存在，那里抛 DllNotFoundException；调用点
            //     在回放每帧的路径上，一次未捕获的异常就打断整帧的输入与命中推进。
            //     而用 Application.platform 提前返回是另一种错法（见下条）：判断写错
            //     会让 SendInput 在 Windows 上也整条静默失效，而它是镜像路径唯一的载体。
            Expect(kbsim.Contains("if (_dllMissing) return;"),
                "⑤b SendInputs 没有 _dllMissing 守卫 —— 非 Windows 上每帧抛 DllNotFoundException，整帧输入与命中推进被打断");
            Expect(kbsim.Contains("catch (DllNotFoundException)") && kbsim.Contains("_dllMissing = true;"),
                "⑤b SendInputs 没有把 DllNotFoundException 闩锁成 _dllMissing —— 每次调用都重新抛一次");
            Expect(!Regex.IsMatch(kbsim, @"Application\.platform|RuntimePlatform"),
                "⑤b SendInputs 用平台判断提前返回 —— 判断一旦写错，SendInput 会整条静默失效");

            // ⑤c 录制侧必须走 SampleKeyState（带守卫 + Input.GetKey 回退），
            //     不能直接调 user32 的 GetKeyboardState。前者在非 Windows 上退回
            //     Unity 的 Input.GetKey 逐键轮询（与游戏 RDInputType_Keyboard 同源），
            //     后者在非 Windows 上直接 DllNotFoundException——录制一开局每帧抛，
            //     回放文件里一个按键事件都没有。
            Expect(recorder.Contains("KeyboardSimulation.SampleKeyState("),
                "⑤c 录制侧没走 SampleKeyState —— 非 Windows 上每帧抛 DllNotFoundException，录出的回放没有任何按键");
            Expect(!recorder.Contains("KeyboardSimulation.GetKeyboardState("),
                "⑤c 录制侧直接调了 GetKeyboardState —— 绕过了平台守卫与 Input.GetKey 回退");
            Expect(kbsim.Contains("Input.GetKey("),
                "⑤c SampleKeyState 没有 Input.GetKey 回退 —— 非 Windows 上录制拿不到任何按键状态");

            // ⑥ holdKeys 必须是真实键码，不能是 AnyKeyCode(null) 占位。
            Expect(!patch.Contains("new AnyKeyCode(null)"),
                "⑥ 仍在用 AnyKeyCode(null) 占位 —— 它只骗过 CheckPreHoldFail，不是真实按键");
            // holdKeys 必须是 AsyncKeyCode：ValidInputWasReleased 清理时按类型分岔
            // （scrPlayer.cs:888/896）——非 AsyncKeyCode 项走 value.IndexOf(item)，
            // 而 AnyKeyCode 默认判等是装箱引用比较（AnyKeyCode.cs:80-83）→ 恒 -1
            // → 每帧 RemoveAt 并 result = true，长按当场被判松手 Die。
            Expect(input.Contains("new AnyKeyCode(new AsyncKeyCode((ushort)vk))"),
                "⑥ 托管状态没有产出 AsyncKeyCode —— holdKeys 每帧被剥光，长按被判松手");
            Expect(!input.Contains("new AnyKeyCode((KeyCode)vk)"),
                "⑥ 仍在用 KeyCode 填 holdKeys —— 引用判等使它永远匹配不上，等于没按住");

            // ⑥b 原生接管时 holdKeys 必须与游戏比较的那份数据同源。
            //     ValidInputWasReleased（scrPlayer.cs:880）在原生链下用
            //     RDInput.GetMainHeldKeys 取 keyMask 来比对 holdKeys。若这边仍灌
            //     帧粒度的 ManagedInput 状态，两套状态从源头分叉：holding 短暂为真、
            //     下一帧又被判松手 —— 长条被判 Die。
            //     r150 改了 GetMainHeldKeys 的形状（返回值 → 传参填充），调用点因此经
            //     GameCompat 兼容层转一手。断言随之放宽到两条路都认，但补上后半段：
            //     兼容层内部必须真的调 RDInput.GetMainHeldKeys，否则「同源」就断了。
            Expect(Has(patch, @"if\s*\(\s*ManagedInput\.NativeTakeover\s*\)\s*(RDInput\.GetMainHeldKeys|GameCompat\.AppendMainHeldKeys)\(keys\)"),
                "⑥b 原生接管时 holdKeys 没有取自 RDInput.GetMainHeldKeys —— 与 keyMask 不同源，长条被判松手");
            Expect(Has(SrcUtil.Source(projectRoot, "Features/Compat/GameCompat.cs"), @"RDInput\.GetMainHeldKeys"),
                "⑥b 兼容层里没有调 RDInput.GetMainHeldKeys —— 跨版本读取持键的源头断了");

            // ⑦ 时序：记账必须在游戏读输入之前。
            //    游戏读输入的链是 PlayerControl_Update → Simulated_PlayerControl_Update
            //    （scrController.cs:552），后者按 :417/:430/:433/:434 读
            //    ValidInputWasReleased / ValidInputWasTriggered / holding / keyTimes。
            Expect(Has(patch, @"typeof\(scrController\)\s*,\s*""PlayerControl_Update""\s*,\s*PatchType\.Prefix"),
                "⑦ 没有在 PlayerControl_Update 的 Prefix 上记账 —— 同步链每帧先读后写，按键晚一帧才被看到");
            Expect(Has(patch, @"typeof\(scrController\)\s*,\s*""UpdateInput""\s*,\s*PatchType\.Prefix"),
                "⑦ 没有在 UpdateInput 的 Prefix 上记账 —— 异步链由 UpdateInput → ProcessKeyInputs 驱动");
            Expect(patch.Contains("ReplayPlayer.PumpInput()"),
                "⑦ 两个 Prefix 没有真正调用 PumpInput —— 事件根本没人灌");

            // ⑧ 按下/抬起按帧戳判定。
            //    靠清集合的写法在「无待办事件」时走不到清空，按下会一直挂着。
            Expect(input.Contains("PressedFrame[vk] = frame") && input.Contains("ReleasedFrame[vk] = frame"),
                "⑧ 按下/抬起没有记帧戳 —— mainPressCount 会一直为真");
            Expect(input.Contains("PressedFrame[vk] == frame"),
                "⑧ WentDown 判据不是帧戳相等 —— 历史按键会被当成本帧按下");
            Expect(!input.Contains("BeginFrame"),
                "⑧ 仍在用 BeginFrame 清集合 —— 无待办事件时走不到清空");

            // ⑨ 生命周期清理。
            Expect(player.Split(new[] { "ManagedInput.Clear()" }, StringSplitOptions.None).Length - 1 >= 3,
                "⑨ ManagedInput.Clear 少于三处 —— 开始/停止/跳转里漏了清理");

            // ⑩ 界面控制键放行真实键盘，否则回放期间按 Esc 暂停不了。
            Expect(patch.Contains("IsGameplayAction("),
                "⑩ GetState 没有按动作类型分流 —— Esc/Quit/Restart 也会被覆盖，玩家暂停不了");

            // ⑪ 原生链与托管覆盖必须**联动**：同一判据，绝不能各说各话。
            //
            //    这两处的历史很曲折，值得写死，因为错法极具欺骗性：
            //
            //    · 若 asyncInject 用动态判据、而 NativeTakeover 恒 false：
            //      异步链在往 keyQueue 写，托管覆盖层同时也在改 RDInput 返回值，
            //      同一颗键被算两次 → 多灌 keyTimes、重复触发命中。
            //    · 若 asyncInject 恒 false、而 NativeTakeover 仍按 Usable 推导：
            //      Inject 从不被调用 → Healthy 恒真 → 只要游戏开着异步输入，
            //      Usable 就为真 → 恒让位。判定改读永远空的 keyMask，
            //      托管覆盖形同虚设。回放看似能用，实则全靠 SendInput 打进 OS
            //      再绕回 Unity Input；失焦 / UIPI 拦截时整条链当场失效。
            //
            //    正确形态：两边都用 Usable && Healthy，且 ReplayPlayer 里的
            //    SendInput 仅在**不经**异步链时才开（否则同一颗键出现两次）。
            Expect(input.Contains("internal static bool NativeTakeover"),
                "⑪ 没有 NativeTakeover 属性 —— Patch_RDInput 依赖它决定放行/覆盖");
            Expect(Has(input, @"_takeover\s*=\s*is_playing"),
                "⑪ NativeTakeover 没有按 is_playing 判 —— 未在回放时也可能放行");
            Expect(input.Contains("AsyncInputInjector.Usable") && input.Contains("AsyncInputInjector.Healthy"),
                "⑪ NativeTakeover 没同时看 Usable 与 Healthy —— 前者是\"看起来能用\"，后者才反映实际入队");
            Expect(patch.Split(new[] { "ManagedInput.NativeTakeover" }, StringSplitOptions.None).Length - 1 >= 3,
                "⑪ 三个 RDInput 覆盖点没有全部联动 —— 漏掉的那个会在原生链下重复计数");

            // ⑪b 回放侧必须自己也在用同一条判据。
            //    注入判据必须是 **Injectable**，不能是 Usable。
            //    Usable 额外要求 asyncKeyboard.isActive（只在 PlayerControl 为真），
            //    而 KeyUpdated 的消费者不只是游戏——JipperResourcePack 直接订阅
            //    SkyHookManager.KeyUpdated（KeyViewer.cs:2216），不读 RDInput / keyMask /
            //    UnityEngine.Input。用 Usable 当判据，非 PlayerControl 状态下
            //    Inject 不被调用、Invoke 不发生，这类模组整段看不到按键。
            Expect(player.Contains("AsyncInputInjector.Injectable") && player.Contains("AsyncInputInjector.Healthy"),
                "⑪b ReplayPlayer 的 asyncInject 没有看 Injectable && Healthy —— 非 PlayerControl 状态下键位显示模组会整段失明");
            Expect(!Has(player, @"asyncInject\s*=\s*AsyncInputInjector\.Usable"),
                "⑪b asyncInject 用了 Usable —— 它要求 asyncKeyboard.isActive，只在 PlayerControl 为真，会把广播一起掐掉");

            // ⑪c 镜像与异步广播**并存**（2026-10-30 用户反馈：异步开着时镜像
            //     完全没效果，界面又毫无提示）。旧实现把两者互斥——SendInput 的
            //     回声会被 SkyHook 全局钩子再捕一次，与合成事件一起进 keyQueue，
            //     同一颗键判两遍（实测 double press）。现在的分层：SendInput 照发
            //     （只读 Unity Input 的显示模组靠它），回声由 Patch_MirrorEchoSuppressor
            //     在 HookCallback 入口掐掉——那是 keyQueue 与 KeyUpdated 的共同
            //     入口，掐在别处必漏一半——判定只认合成事件一份。
            //     正确性押在两件事上，缺一就是「回放判两遍」：
            //     ① 镜像门必须带「抑制补丁已挂载」的退路，且查询必须走
            //        **SpectrePatchHub**：属性补丁的登记册在 Hub 的 spec.Applied，
            //        PatchManager 的挂载集只记 RegisterPatch 的手工补丁类，
            //        查那边恒 false，镜像永远退回互斥——2026-10-30 实测的错账
            //        （日志 156 行「已挂载」 vs 1250 行「未挂载」的矛盾就是它）；
            //     ② SendInputs 必须登记发出的键——那是抑制补丁在钩子线程认领
            //        回声的唯一凭据（SkyHookEvent 没有 extra-info 字段）。
            Expect(Has(player, @"bool suppressorArmed\s*=\s*PatchShared\.SpectrePatchHub\.IsApplied\(typeof\(Patch_MirrorEchoSuppressor\)\)"),
                "⑪c 镜像门的挂载查询没走 SpectrePatchHub（查 PatchManager 恒 false）—— 异步开着时镜像整个没效果");
            Expect(Has(player, @"sendInput\s*=\s*ManagedInput\.Mirror\s*&&\s*\(!asyncInject\s*\|\|\s*suppressorArmed\)"),
                "⑪c 镜像门没有按 suppressorArmed 退路 —— 抑制补丁失效还发 SendInput，同一颗键判两遍");
            Expect(!Has(player, @"sendInput\s*=\s*ManagedInput\.Mirror\s*&&\s*!asyncInject"),
                "⑪c 镜像退回了无条件互斥 —— 异步开着时镜像整个没效果（2026-10-30 用户反馈的原缺陷）");
            string suppressor = SrcUtil.Source(projectRoot, "Features/Replay/Patches/Patch_MirrorEchoSuppressor.cs");
            Expect(Has(suppressor, @"typeof\(SkyHook\.SkyHookManager\)\s*,\s*""HookCallback""\s*,\s*PatchType\.Prefix"),
                "⑪c 回声抑制没打在 HookCallback 上 —— 那是 keyQueue 与 KeyUpdated 的共同入口，掐在别处必漏");
            Expect(suppressor.Contains("IsRecentlySentByUs(") && suppressor.Contains("return false"),
                "⑪c 抑制前缀没有按回声登记认领并跳过原方法 —— 等于没抑制");
            // 判据钉**调用点实参**（inputs, result），不是方法名：删调用点、留定义
            // 的半修形态会让「方法名出现过」的宽判据照样全绿（U52 首轮实测）。
            Expect(kbsim.Contains("RecordSentKeys(inputs, result)"),
                "⑪c SendInputs 没有登记发出的键 —— 抑制补丁在钩子线程认领不到回声，double press 回归");


            // ⑫ 「播放一瞬间全部模拟完」。三次实测里最严重的一类：不是不动，是
            //     整局在一两帧里跑完。两个独立成因，都必须钉住：
            //
            //     ⑫a 注入侧时间基准。dspTimeSong 只在歌曲真正排定播放后才赋值
            //         （scrConductor.cs:668/938/956）。倒计时/检查点阶段它仍是 0，
            //         dsp 退化成常量、evTicks 落在遥远的过去；而 UpdateInput 按 tick
            //         逐个调 ProcessKeyInputs（scrController.cs:2929-2935），于是这批
            //         陈旧事件在同一帧把整段历史重放一遍。
            //         同理还要一个窗口兜底：任何基准错乱都会把 evTicks 推离 nowTicks
            //         几十秒，偏离超过窗口就宁可丢弃、交给托管层。
            //
            //     ⑫b 回放侧游标。Start() 无条件 KeyEventIndex = 0，从中途开局时
            //         所有早于此刻的按键都算待办，PumpInput 的 while 第一帧就灌爆。
            //         另需单帧上限：积压时闷头灌完 = 同一帧重放整段。
            string injector = SrcUtil.Source(projectRoot, "Features/Replay/Runtime/AsyncInputInjector.cs");
            Expect(injector.Contains("conductor.dspTimeSong == 0.0"),
                "⑫a Inject 的 dspTimeSong 判据必须是 == 0 —— 歌曲未排定时是 0，但检查点起跑时是大负数（正常）");
            Expect(!injector.Contains("conductor.dspTimeSong <= 0.0"),
                "⑫a Inject 出现了 `dspTimeSong <= 0.0` —— 检查点起跑时该值为负，异步注入会被整段拒绝");
            Expect(Has(injector, @"Math\.Abs\(evTicks - nowTicks\)"),
                "⑫a Inject 没有 evTicks 相对 nowTicks 的窗口兜底 —— 任何基准错乱都会灌进陈旧 tick");
            Expect(player.Contains("MaxEventsPerFrame"),
                "⑫b PumpInput 没有单帧事件上限 —— 游标积压时整段会在同一帧内重放");
            // 上限只许限速，不许丢弃：曾经在触顶后 SeekKeyEvents(songPosition)，
            // 而那个 songPosition 在时钟未就绪时是几万秒的巨大值，游标被直接推到
            // 文件末尾 —— 整局按键静默消失（用户报「按键模拟全失效」）。
            Expect(!player.Contains("SeekKeyEvents"),
                "⑫b 仍在用 SeekKeyEvents 跳游标 —— 时钟未就绪时会跳到文件末尾，整局按键消失");
            // 歌曲时钟未就绪时必须什么都不做。dspTimeSong 为 0 时 songposition_minusi
            // 退化成 (dspTime - 0) * pitch，是几万秒；用它比较会把整局事件一次灌完。
            // **不能读 conductor.songposition_minusi。**
            // 它在 scrConductor.Update 的末尾（:852）才重算，而 scrController.UpdateInput
            // 在同一方法的 :799 就被调用——Prefix 里读到的永远是上一帧的值，开局第一帧
            // 甚至是上一局遗留的。实测：读到 15.262（上一局曲中位置），而按当时
            // dspTimeSong/dspTime 现场算是 -1.526（倒计时，正确）。那个假值让游标一次性
            // 推进 64 个事件，前 8 秒按键在同一帧被全部按下。
            Expect(player.Contains("CurrentSongPosition(conductor"),
                "⑫b 没有按游戏公式现场算歌曲位置 —— 读 songposition_minusi 会拿到上一帧/上一局的陈旧值");
            Expect(!Has(player, @"PumpInput\(conductor\.songposition_minusi"),
                "⑫b 仍在把 conductor.songposition_minusi 喂给 PumpInput —— 该值是上一帧的");
            // 现场算还不够：dspTimeSong 未排定时恰好是 0，公式退化成
            // (dspTime - 0) * pitch，是几万秒量级的伪值，会在一帧内灌完整局。
            //
            // **判据只能是 `== 0`，不能是 `<= 0`。** 从检查点起跑时游戏会执行
            // ScrubMusicToTime，把 dspTimeSong 设成 dspTime - entryTime，即一个大负数
            // （实测 29.315 - 214.412 = -184.915）。那是「歌曲从第 214 秒开始」的正常
            // 表达；用 `<= 0` 会把它当成未就绪，导致检查点起的回放整段无按键。
            Expect(player.Contains("if (conductor.dspTimeSong == 0.0) return 0.0;"),
                "⑫b CurrentSongPosition 的 dspTimeSong 判据必须是 == 0 —— `<= 0` 会把检查点起跑的大负数误判为未就绪，整段无按键");
            Expect(!player.Contains("conductor.dspTimeSong <= 0.0"),
                "⑫b 出现了 `dspTimeSong <= 0.0` —— 检查点起跑时该值为负，会把整段回放的按键全部挡掉");
            Expect(player.Contains("if (!ready) return;"),
                "⑫b 调用方没有检查 ready —— 时钟未就绪时仍会拿伪值去灌事件");
            Expect(!Has(player, @"internal static void Start\(\)[\s\S]*?SeekKeyEvents"),
                "⑫b Start 仍在按歌曲时间对齐游标 —— 那一刻 songposition_minusi 是上一局的陈旧值");
            // 日志占位符：`$"...，帧 {frame}）"` 里 frame 是常量段，永远不会被替换，
            // 玩家报障时这行恰好是最关键的线索，却是死的。
            // 判据：含 `帧 {frame}` 的那个字符串字面量前面必须有 `$`。
            // 之前写成「整段源码里搜一遍」，被修好之后的插值写法反过来命中，
            // 属于判据本身写错——这里按「字面量的引导字符」来判。
            Expect(!Has(input, @"(?<![$@])""[^""]*帧 \{frame\}"),
                "⑫c 首键日志的 {frame} 没进插值 —— 玩家报障时最关键的帧号恒为字面量");

            // ⑫d 跳转（FastForward）的游标必须真的生效。两处缺陷叠在一起，症状都是
            //     「跳到最后之后按键模拟消失」：
            //       1. Patch_Lifecycle 先 FastForward 再 StartPlaying，而 Start() 无条件把
            //          PlayIndex / KeyEventIndex 清零，FastForward 的成果当场作废；
            //       2. FastForward 的两个查找循环「找不到就保留初值 0」，跳到最后一砖时
            //          目标时间之后往往没有事件，游标停在 0。
            //     大关卡上万条事件要几百帧才追平（每帧上限 64），而追平瞬间
            //     Patch_Playback 判定两份数据都到尾部，直接 StopPlaying。
            string lifecycle = SrcUtil.Source(projectRoot, "Features/Replay/Patches/Patch_Lifecycle.cs");
            int startAt = lifecycle.IndexOf("PlayActions.StartPlaying()", StringComparison.Ordinal);
            int fastAt = lifecycle.IndexOf("ReplayPlayer.FastForward(", StringComparison.Ordinal);
            Expect(startAt >= 0 && fastAt >= 0 && startAt < fastAt,
                "⑫d 先 FastForward 再 StartPlaying —— Start() 会把游标清零，跳转等于没做");
            // PlayIndex 兜底必须是 0 而不是 Count：Count 会让 Patch_Playback 的完成判定
            // `PlayIndex >= Count && KeyEventIndex >= Count` 在开始那一帧成立，回放
            // 立即自我 StopPlaying——玩家看到的就是「跳转后完全没有回放」。
            Expect(player.Contains("PlayIndex = 0;"),
                "⑫d FastForward 的 PlayIndex 兜底必须是 0 —— 落到 Count 会让回放在开始那一帧就自我终止");
            Expect(!Has(player, @"PlayIndex = data\.HitSnapshots\.Count;"),
                "⑫d FastForward 把 PlayIndex 兜底到了 Count —— 会触发完成判定，回放立刻结束");
            Expect(Has(player, @"KeyEventIndex = data\.KeyEvents\.Count;"),
                "⑫d FastForward 没把 KeyEventIndex 兜底到末尾 —— 跳到最后一砖时游标停在 0");
            Expect(player.Contains("ManagedInput.Restore("),
                "⑫d 跳转后没有重建仍按住的键 —— 长条中间跳转会漏判");

            // ⑬ 「瞬间全部模拟完」的**命中推进**成因（与 ⑫ 的按键成因是两回事）。
            //
            //     Patch_PlayerControlUpdate 里的 while 是单向不可逆的：每轮 PlayIndex++，
            //     判定 `angle >= CurrAngle`。一旦 angle 算坏成 ±Infinity（crotchetAtStart
            //     为 0 时的除零），比较恒真，整份 HitSnapshots 在一帧内被吃完——
            //     玩家看到的就是整局瞬间播完，且无法恢复。
            //
            //     还有一处更隐蔽的错源：循环用 __instance 判砖块，却用无参 GetAngle()
            //     取角度。后者读静态 scrController.instance；场景切换/编辑器里两者
            //     可以不是同一个对象，角度就来自另一个星球。
            string playback = SrcUtil.Source(projectRoot, "Features/Replay/Patches/Patch_Playback.cs");
            Expect(!Has(playback, @"float angle = GetAngle\(\);"),
                "⑬ 命中推进用无参 GetAngle() 取角度 —— 与判砖块的 __instance 可能不同源，角度会跑飞");
            Expect(playback.Contains("GetAngle(__instance)"),
                "⑬ 命中推进没有显式传入 __instance 取角度");
            Expect(playback.Contains("MaxHitsPerFrame"),
                "⑬ 命中推进没有单帧上限 —— 角度一旦异常，整份 HitSnapshots 会在一帧内吃完");
            Expect(playback.Contains("ReplayAngleGuard.IsSane("),
                "⑬ 命中推进没有挡 ±Infinity —— Infinity 让 `angle >= CurrAngle` 恒真");
            // ⑫a 的就绪判据现在在 CurrentSongPosition 里（PumpInput 已不再自行算时钟）。

            if (fail == 0)
                Console.WriteLine("    ✓ 跨平台输入的 15 组判据都成立");
            return fail;
        }
    }
}