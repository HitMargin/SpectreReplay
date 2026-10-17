// 界面层的控制流/接线断言。
//
// 为什么单独一组：上一轮（偏差 25）11 条界面缺陷在 116 条断言全绿的情况下
// 全部漏网，原因是闸门没有界面层。补上的 UiLogicCheck 那 6 组盯着"控制流形状"，
// 但那一轮之后又长出新形态，本组盯三件新事：
//
//   ① 按钮文案必须与它触发的动作相符。曾把 record.cancelsavekey「取消保存」
//      当"停止录制"按钮用——两个是不同的动作（后者是 CancelSave：丢掉本局
//      待存数据）。玩家点它想停录，本局回放却永远不出现。
//   ② 控件列宽必须走 Gui.CtrlW。写死 360 的那一行比别的行宽一截，
//      而且 LabelW+360 在窄窗口下先溢出（页面整体包在 ScrollView 里）。
//   ③ 分组边框不许用 GetLastRect()。它返回的是"最后一个控件的矩形"而非组的
//      矩形，描边会画在最后一个子控件的位置上（卡片矮一截），而紧跟 Begin*
//      调用还会直接抛异常。
//
// 三条都落在"源码形状"上而不是"画面看起来对"——前者能离线跑，后者不能。
using System;
using System.IO;

namespace UiPreview
{
    internal static class UiLogicShapeCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 界面控制流/接线 ──");
            int fail = 0;
            fail += Check(projectRoot,
                "① 停止录制按钮不许借用「取消保存」文案",
                "Pages\\Pages.cs",
                // 判据必须精确到**按钮那行**。第一版写的是"文件里不许出现
                // record.cancelsavekey"——那是假断言：Pages.cs:65 的热键绑定行
                // 合法使用同一个键（它就是取消保存热键），于是判据与代码无关地
                // 常红。只有 GUILayout.Button(...) 里的 caption 才要求是 record.stop。
                s => RegexButtonCaption(s, "record.cancelsavekey") == 0
                     && RegexButtonCaption(s, "record.stop") == 1);
            // ② 这条盯的是**皮肤来源**，不只是一个数字。2026-10-12 用户截图报
            //    「输出目录那一行是黑的」：内建 GUILayout.TextField 是本页唯一
            //    没被 UMM 重设皮肤的控件——UMM 的 PrepareGUI/ScaleGUI 只重设
            //    label/button/horizontalSlider/horizontalSliderThumb/toggle
            //    （UnityModManager.decompiled.cs:3467-3472 与 :3508-3519），
            //    **从不碰 GUI.skin.textField**，于是它沿用 ADOFAI 自己的深色皮肤，
            //    在深色卡片上就是一条黑框。其余控件要么被 UMM 重设过，要么由
            //    Gui 克隆 label 后自绘。
            //    判据因此是两条同时成立：不许出现内建输入框 + 宽度必须走 Gui.CtrlW。
            //    宽度那条保留（写死 360 会让这一行的控件比上下行宽一截，
            //    窄窗口下 LabelW+360 先溢出），只是不再单独作为判据。
            fail += Check(projectRoot,
                "② 文本框必须自绘（不许用内建输入框）且宽度走 Gui.CtrlW",
                "Ui\\OnGui.cs",
                s => !s.Contains("360", StringComparison.Ordinal)
                     && s.Contains("GUILayout.Width(Gui.CtrlW)", StringComparison.Ordinal)
                     // 注释已被 SrcUtil.Clean 剥掉，所以这里命中的一定是真代码。
                     // 只查 OnGui.cs：自绘控件实现在 Ui\TextField.cs，那边合法地
                     // 提到这两个名字（在注释里说明为什么不用它们）。
                     && !s.Contains("GUILayout.TextField", StringComparison.Ordinal)
                     && !s.Contains("GUI.TextField", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "③ 分组边框不许用 GetLastRect（返回的是子控件而非组的矩形）",
                "Ui\\Gui.cs",
                s => !s.Contains("GetLastRect", StringComparison.Ordinal)
                     && s.Contains("BeginVertical", StringComparison.Ordinal));
            // ④ 阻塞式原生目录选择框必须在 OnGUI 之外调用。
            //    `FileBrowser.PickFolder` 走的是原生模态对话框
            //    （UnityFileDialog.decompiled.cs:340 → file_dialog_pick_folder），
            //    **调用即阻塞当前线程直到玩家选完**。若把它写在画控件的方法体里
            //    （这里是 DrawSaveDirRow），就是在 IMGUI 的 Layout/Repaint 那一趟
            //    中途钻进模态原生窗口——事件队列的配对会错位。
            //    正确形状：点击只置待办，真正调用交给 MainThread.Defer 在下一拍
            //    Update（OnGUI 之外）执行。
            //    这条判据是"接线"而不是"存在"：光检查 PickFolder 出现过没有用——
            //    它写在画控件的方法里同样"出现过"，而那正是要防的形态。
            fail += Check(projectRoot,
                "④ 阻塞式目录选择框不许画在控件方法体里（必须 Defer 到 OnGUI 之外）",
                "Pages\\Pages.cs",
                s => s.Contains("MainThread.Defer(PickSaveDirDeferred)", StringComparison.Ordinal)
                     && PickFolderNotInDrawingMethod(s, "DrawSaveDirRow"));
            // ④b 同一形态的第二个调用点（渲染输出目录的选择框在 OnGui 里）。
            //    一条判据只查一个文件就会漏掉后加的那处——所以每加一个
            //    PickFolder 调用点，这里就要跟着加一条，或者改成本条那种
            //    全文件扫的办法（这里用后者：直接扫两个文件的**全部**调用）。
            fail += Check(projectRoot,
                "④b 输出目录的选择框同样必须 Defer（不许出现在 RowInput 里）",
                "Ui\\OnGui.cs",
                s => s.Contains("MainThread.Defer(PickOutDirDeferred)", StringComparison.Ordinal)
                     && PickFolderNotInDrawingMethod(s, "RowInput"));
            // ⑤ 自绘输入框的底色必须**比承载它的卡片更亮**。
            //    2026-10-12 真事：我把 FieldBg 定成 (0.07,0.08,0.11)，而 CardBg 是
            //    (0.13,0.15,0.19)——输入框比卡片还暗，玩家看到的仍是一个黑洞，
            //    于是"黑框还在"被反馈回来。**控件确实是自绘的**（占位文案都画出来了），
            //    错的是颜色：凹槽不比平面亮，就读不出"这里可以输入"。
            //
            //    这一条能成立，是因为该缺陷**在算术上可判**——不比 GetLastRect
            //    那类要靠语义理解的坑。判据取 Rec.709 亮度，留一点余量（不要求
            //    严格大于 0，避免把"略亮"判死，但也不许相等）。
            //    解析失败一律判**不通过**：判据的默认姿态是不信。
            fail += Check(projectRoot,
                "⑤ 输入框底色必须比卡片更亮（暗于卡片 = 看着仍是个黑洞）",
                "Ui\\Gui.cs",
                s => FieldIsLighterThanCard(s));
            // ⑥ 按钮必须**传自定义样式**（不许吃内建皮肤的贴图）。
            //
            //    2026-10-12 用户第二次反馈「UI 还是用了 IMGUI 的原生控件，我要的是
            //    ADOFAIMacro 那种自定义控件」。根因与黑框同源但**范围大得多**：
            //    UMM 的 ScaleGUI 只改内建样式的 padding / fixedHeight / border /
            //    margin，**从不换贴图**（UnityModManager.decompiled.cs:3511-3518），
            //    所以内建 button / horizontalSlider 永远是 Unity 那套方角灰底圆头。
            //
            //    **判据形状订正（第一版写错了）。** 第一版写成"不许出现
            //    GUILayout.Button"——那是**假判据**：查参考实现（ADOFAIMacro
            //    UIUtils.cs:326/674）发现**它也在用 GUILayout.Button**，只是传了
            //    带圆角贴图的 GUIStyle。所以"用不用内建控件"根本不是缺陷所在，
            //    **"传不传自定义样式"才是**。内建 Button 只是个命中测试器，
            //    外观完全由传入的样式决定。这条第一版会把正确的代码判红
            //    （实测：Pages.cs 四处全都传了 Gui.ButtonStyle，照样报 ✗）。
            //
            //    判据：每个 GUILayout.Button( 调用的实参里必须出现 Gui.*Style。
            //    调用常跨行（样式在三元表达式里），所以往后看几行再判。
            fail += Check(projectRoot,
                "⑥ 按钮必须传自定义样式（吃内建皮肤 = 原生外观）",
                "Ui\\OnGui.cs",
                s => ButtonsUseCustomStyle(s));
            fail += Check(projectRoot,
                "⑥c 渲染/回放页的按钮同样必须传自定义样式",
                "Pages\\Pages.cs",
                s => ButtonsUseCustomStyle(s));
            // ⑥b 滑条必须完全自绘。**这条与按钮不同**：滑条的轨道/滑块贴图在
            //    内建样式里是分开的槽位，靠一个 GUIStyle 换不干净（参考实现
            //    ADOFAIMacro 的 M3HorizontalSlider 就是整个手绘的，UIUtils.cs:401）。
            fail += Check(projectRoot,
                "⑥b 滑条必须自绘（不许用内建 HorizontalSlider）",
                "Ui\\OnGui.cs",
                s => !s.Contains("GUILayout.HorizontalSlider", StringComparison.Ordinal)
                     && s.Contains("Gui.Slider(", StringComparison.Ordinal));
            // ⑥d 圆角贴图必须走缓存。每帧 new Texture2D 会持续泄漏显存
            //     （Unity 贴图不受 GC 管理），而控件每帧都重画。
            fail += Check(projectRoot,
                "⑥d 圆角贴图必须有缓存（不许每帧 new Texture2D）",
                "Ui\\RoundedTex.cs",
                s => s.Contains("Dictionary<Key, Texture2D>", StringComparison.Ordinal)
                     && s.Contains("Cache.TryGetValue", StringComparison.Ordinal)
                     && s.Contains("HideFlags.HideAndDontSave", StringComparison.Ordinal));
            // ⑦ RenderClock transpiler 必须区分"已被别的 mod 接管"与"目标形状变了"。
            //
            //    2026-10-12 真事：Spectre 与 SpectreReplay 同时启用时，Spectre 的
            //    同类 transpiler 先装，把 scrConductor.Update 的四个时间源调用
            //    换成了它自己程序集的 RenderClockSource。轮到我们时，Harmony 的
            //    `Calls` 是 `object.Equals(operand, method)`——精确比 MethodInfo，
            //    于是一个都不命中 ⇒ replaced == 0 ⇒ 抛
            //    「no time-source calls found — **game Update likely changed**」。
            //    **那句诊断是错的**（游戏没变，是被别人抢先了），它把排查方向
            //    整个带偏到"去比对游戏 IL"。
            //
            //    判据三件齐：①探测必须写在 yield 迭代**之前**（否则惰性求值，
            //    拿到的 replaced 恒为 0，判定永不成立）；②探测看的是
            //    "指令里有别的 RenderClockSource 调用"；③原诊断文案不许再断言
            //    "game Update likely changed"。
            fail += Check(projectRoot,
                "⑦ RenderClock transpiler 必须识别「已被别的 mod 接管」（不许误报 game changed）",
                "Features\\VideoRender\\VideoRenderClockPatches.cs",
                s => TranspilerDistinguishesTakeover(s));
            // ⑧ 喂给 AudioRenderer.Render 的原生容器必须**精确等于本帧样本数**。
            //
            //    2026-10-16 真事：上游 c36e7b3（v0.4.1 第 150 轮审计）为消掉每帧
            //    重分配，把原生容器也改成 4096 对齐的分桶（长度 ≥ floats），
            //    注释写着"Unity 侧按容量足够判定"。**那个假设是错的**：
            //    Unity 按传入数组的长度推进采集环形缓冲的读指针，长度大于实际样本
            //    会把读指针推过写指针——此后 GetSampleCountForCaptureFrame() 恒为 0、
            //    AudioSettings.dspTime 冻结。于是走"静默→重启"，每次 Start() 只换来
            //    一个缓冲又立刻归零，三次预算用尽即判死。日志形态：
            //      mixdown started → silent frame=2 → restarted 1 → silent frame=123 …
            //    而"退回精确尺寸"的兜底分支**从不触发**，因为 Render 返回的是 true
            //    （证据：DeathReason 是 mixdown silent，不是 Render returned false）。
            //
            //    **判据的输入是外部事实**：上游成片在 c36e7b3 之前全部带音轨
            //    （09-19 及更早共 16 个，ffmpeg 逐个验过），之后再无一次成功过。
            //    不是环境漂移、也不是游戏更新。
            //
            //    正确形状：Render 的目标来自 RenderBuffer(floats)——长度严格等于
            //    本帧样本数、变化时重分配；分桶只允许给**托管数组**（managed/bytes
            //    只承接拷贝与落盘，长度不参与 Render 语义）。
            //    判据因此必须**限定在 RenderBuffer 方法体内**：SizeBuffers 里合法地
            //    写着 Math.Max(floats, 桶大小)，整文件搜 Math.Max 是假判据。
            fail += Check(projectRoot,
                "⑧ 混音轨 Render 容器必须精确（分桶会让捕获静默停住）",
                "Features\\VideoRender\\VideoAudioTrack.cs",
                s => MixdownRenderBufferIsExact(s));
            // ⑨ 分组卡片**不许用 GUI.backgroundColor 染色**（它乘算，会抹平按钮 hover）。
            //
            //    2026-10-16 真事：用户报「打开编辑器按钮鼠标放上去没有颜色变化」。
            //    按钮样式本身没错——hover.background 一直在设（Gui.cs MakeButton）。
            //    问题是它落在 Gui.Section 的卡片体里，而 Section 给整段设了
            //    GUI.backgroundColor = CardBg。**backgroundColor 是乘算的**：
            //    按钮底色烘在圆角贴图里，再乘一遍卡片色 ——
            //      BtnFace  (51,59,74) × CardBg(33,38,48) = 7,9,14
            //      BtnHover (71,82,99) × CardBg          = 9,12,19
            //    hover 与 normal 的色差从 20/23/26 被压到 3/3/5（≈1%），肉眼不可辨。
            //    ⇒ 现象是"没有 hover"，实际是"hover 被乘没了"。
            //
            //    同项目里这个坑**已经踩过一次**：页签当初正因"双重变暗"改成了
            //    独立样式，但 Section 内那些按钮没跟着改。所以判据必须钉在
            //    **Gui.cs 里不许出现 backgroundColor 赋值**，而不只是修那一颗按钮
            //    ——治形态，不治位置。
            //
            //    修法：卡片底色由 _pad 自带的**卡色贴图**提供（见 Ensure），
            //    颜色不再经过乘算。判据三件齐：
            //      ① 赋值语句为零（只查 `GUI.backgroundColor =`，注释已被剥掉）；
            //      ② _pad 的底图必须是卡色贴图而不是 texture2D.whiteTexture；
            //      ③ 还原语句也应当没有了（oldBg 这套随之消失）。
            fail += Check(projectRoot,
                "⑨ 卡片不许用 GUI.backgroundColor 染色（乘算会抹平按钮 hover）",
                "Ui\\Gui.cs",
                s => !s.Contains("GUI.backgroundColor =", StringComparison.Ordinal)
                     && !s.Contains("GUI.backgroundColor=", StringComparison.Ordinal)
                     && s.Contains("_pad.normal.background = RoundedTex.Get(2, 2, 0, CardBg)", StringComparison.Ordinal)
                     // 只钉 _pad 那一处：Fill() 里合法地用 whiteTexture 画纯色块
                     // （它随后自己还原 GUI.color），整文件禁 whiteTexture 会假红。
                     && !s.Contains("_pad.normal.background = Texture2D.whiteTexture", StringComparison.Ordinal));
            // ⑩ 滑条右侧的数值必须是**可编辑输入框**，不能退回只读显示。
            //
            //    2026-10-17 用户要求：「把滑动条右边的数字显示改为输入框」。
            //    理由与「只读展示行」那一族同源但方向相反：数值**显示出来了**、
            //    滑条**也能拖**，看起来什么都不缺——所以它不是"界面缺失"那类
            //    缺陷，而是"精度不够用"。
            //
            //    **为什么必须用自绘输入框而不是 GUILayout.TextField**：
            //    同页那个内建输入框会沿用 ADOFAI 深色皮肤（UMM 从不重设
            //    GUI.skin.textField），在深色卡片上就是一条黑框——判据 ② 已经
            //    为 OnGui.cs 钉死了这一点，这里的滑条数值框必须走同一条路。
            //
            //    判据三件齐：
            //      ① RowSlider 方法体里出现了自绘控件的 Draw（而不是 GUILayout.Label）；
            //      ② 一行滑条的数值框调了 GetField/字段缓存（否则每帧新建控件、
            //         编辑状态每帧丢失，打一个字就掉一个）；
            //      ③ 解析失败时必须**复位**（st.Text = NumberOf(full)），
            //         否则输入框会留着一段永远提交不上去的乱码。
            fail += Check(projectRoot,
                "⑩ 滑条右侧数值必须是可编辑输入框（不许退回只读显示）",
                "Ui\\OnGui.cs",
                s => SliderValueIsEditable(s));
            // ⑪ 滑条 / 输入框 / 开关都必须有 hover 反馈。
            //
            //    2026-10-17 用户要求：「滑条 输入框 开关 等控件都像按钮那样
            //    做成鼠标移到上面时变化」。**按钮的 hover 来自样式里烘好的
            //    normal/hover 两张贴图**（Gui.MakeButton），而这三个是纯自绘的，
            //    没有任何机制替它们判鼠标位置——不主动做，鼠标移上去毫无反应，
            //    整页只有按钮"活"着，读起来像坏了一半。
            //
            //    判据钉的是**形态**（用没用 Gui.Hover + hover 色），不是具体色值：
            //    数字可以调，机制不能丢。分别判三个文件/方法，避免"改一处算全篇"。
            fail += Check(projectRoot,
                "⑪ 滑条必须有 hover 反馈（自绘控件不会自动获得 hover）",
                "Ui\\Gui.cs",
                s => MethodUsesHover(s, "Slider", "SliderTrackHover"));
            fail += Check(projectRoot,
                "⑪b 开关必须有 hover 反馈",
                "Ui\\Gui.cs",
                s => MethodUsesHover(s, "Switch", "PillOffHover"));
            fail += Check(projectRoot,
                "⑪c 输入框必须有 hover 反馈",
                "Ui\\TextField.cs",
                s => s.Contains("Gui.Hover(box)", StringComparison.Ordinal)
                     && s.Contains("FieldBgHover", StringComparison.Ordinal));
            // ⑫ 每个控件标签都必须有悬停说明(.<key>.tip 中英两套齐)。
            //
            //    2026-10-17 用户要求：「鼠标移到功能上 自动弹出功能的介绍」。
            //    这条判据守的不是"画得出框"(那是 Gui.DrawTooltip 的事)，而是
            //    **每一项功能都真的写了说明**--没写的那个控件悬停上去毫无反应，
            //    而玩家分不清"这行没有说明"和"说明功能坏了"，只会重复悬停。
            //
            //    **为什么按标签键判而不是按控件个数判**：说明是挂在 labelKey 上的
            //    (见 Gui.Tip)，同一个键在两处用到只需要写一条说明;反过来按
            //    调用行数判，会把"同一键两处用"误报成漏写。
            fail += Check(projectRoot,
                "⑫ 每个控件标签都必须有悬停说明(中英两套齐)",
                "Pages\\\\Pages.cs",
                s => EveryControlHasTip(s, projectRoot));
            // ⑬ 悬停说明必须**真的接上**(⑫ 只管"说明写了没有")。
            //
            //    ⑫ 与 ⑬ 的分工正好是"内容"与"接线"：说明文案写得再齐，
            //    只要 DrawTooltip 没被调用、或者 ResetTips 没在帧首清空，
            //    悬停上去照样毫无反应 -- 而那正是本项目反复栽的
            //    「有数据、链路断、界面没效果」那一族。
            fail += Check(projectRoot,
                "⑬a 每帧必须清空悬停登记(否则说明框挂在上一次悬停的行上)",
                "Ui\\\\OnGui.cs",
                s => s.Contains("Gui.ResetTips();", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "⑬b 说明框必须在整页画完之后画(先于 DrawPage 会被后面的控件盖住)",
                "Ui\\\\OnGui.cs",
                s => TooltipDrawnAfterPage(s));
            fail += Check(projectRoot,
                "⑬c 开关必须登记悬停说明",
                "Ui\\\\Gui.cs",
                s => { var body = MethodBody(s, "Switch"); return body != null && body.Contains("Tip(", StringComparison.Ordinal); });
            fail += Check(projectRoot,
                "⑬d 五种行控件都必须登记悬停说明(slider/dropdown/hotkey/input/button)",
                "Ui\\\\OnGui.cs",
                s => AllRowControlsTip(s));
            fail += Check(projectRoot,
                "⑬e 说明框必须只在 Repaint 画且高度按内容算",
                "Ui\\\\Gui.cs",
                s => TooltipBodyIsSane(s));
            // ⑬f 行与行之间必须留竖向间距。
            //
            //    圆角贴图半径 8、按钮高 22~28：两行紧贴时上一颗的下圆角与
            //    下一颗的上圆角正好咬合，中间没有卡片底色透出来，一列按钮
            //    连成一根粗色条（2026-10-17 用户截图报「按钮的布局似乎
            //    融合到了一起」）。**逐方法判**：只查文件里出现过 RowGap(
            //    的话，写一处就全绿，其余五处漏掉照样看不出来。
            fail += Check(projectRoot,
                "⑬f 五种行控件与整行按钮都必须留出行距(否则圆角咬合成一根)",
                "Ui\\\\OnGui.cs",
                s => AllRowsHaveGap(s));
            if (fail == 0)
                Console.WriteLine("    ✓ 二十三条界面形状判据都成立");
            return fail;
        }

        /// <summary>
        /// 混音轨是否用**精确尺寸**的原生容器调 <c>AudioRenderer.Render</c>。
        ///
        /// 判据四件齐（缺一件就能让 4096 分桶悄悄回来）：
        ///   ① Render 的目标来自 <c>RenderBuffer(floats)</c>——按本帧样本数取容器；
        ///   ② <c>RenderBuffer</c> 内部只按 <c>floats</c> 分配——精确值；
        ///   ③ 该分配行不得出现分桶值（<c>need</c> / <c>Math.Max</c>）——
        ///      这正是 c36e7b3 的形态；只查①会漏掉"RenderBuffer 里又分桶"。
        ///   ④ 不得再有 <c>AudioRenderer.Render(native)</c> 这种直传字段的旧形态。
        ///
        /// **判据限定在 RenderBuffer 方法体内**，不做整文件 Contains：
        /// SizeBuffers 里合法地写着 <c>Math.Max(floats, 桶大小)</c>，整文件搜会假红。
        ///
        /// 找不到 RenderBuffer 一律判**不通过**：判据的默认姿态是不信——
        /// "一处都没有"更可能是判据自己失效（有人改了方法名/形状），
        /// 而不是"全都合规"。
        /// </summary>
        private static bool MixdownRenderBufferIsExact(string code)
        {
            if (!code.Contains("RenderBuffer(floats)", StringComparison.Ordinal))
                return false;
            if (code.Contains("AudioRenderer.Render(native)", StringComparison.Ordinal))
                return false;

            // 定位 RenderBuffer 方法体（从方法签名到下一个成员声明为止）
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            int start = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("NativeArray<float> RenderBuffer(int floats)", StringComparison.Ordinal))
                {
                    start = i;
                    break;
                }
            }
            if (start < 0) return false;

            var body = new System.Text.StringBuilder();
            for (int i = start + 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();
                // 方法体在本类里结束于下一个成员声明（注释已被 SrcUtil.Clean 剥掉）
                if (line.StartsWith("private ", StringComparison.Ordinal)
                    || line.StartsWith("internal ", StringComparison.Ordinal)
                    || line.StartsWith("public ", StringComparison.Ordinal))
                {
                    break;
                }
                body.AppendLine(lines[i]);
            }
            string b = body.ToString();
            // 必须按 floats 精确分配
            if (!b.Contains("new NativeArray<float>(floats,", StringComparison.Ordinal))
                return false;
            // 不许出现分桶值或取大运算
            if (b.Contains("need", StringComparison.Ordinal)
                || b.Contains("Math.Max", StringComparison.Ordinal)
                || b.Contains("bucket", StringComparison.Ordinal))
                return false;
            return true;
        }

        /// <summary>
        /// RenderClock transpiler 是否区分了"已被别的 mod 接管"与"目标形状变了"。
        ///
        /// 三件必须同时成立（缺任何一件都会回到原来的误报）：
        ///   ① 物化与探测都排在第一个 `yield return` **之前**
        ///   ② 探测判据是"operand 的声明类型名为 RenderClockSource"——
        ///      **且该行末尾就是判断的收尾**（`))` 或 `)`），
        ///      防止有人再加一个恒假合取项让探测失效而结构看着原样
        ///   ③ 真正的失败文案里不再出现 "game Update likely changed"
        ///
        /// ② 的严格形状是 MUT-V10 逼出来的：那个变异给探测条件接了一个
        /// `&& mi.Name == "NeverMatches"`，三处结构标记全在、行为却已失效，
        /// 而只查"出现过 DeclaringType?.Name"的判据照样绿。所以必须
        /// **连判断的收尾一起钉**。
        /// </summary>
        private static bool TranspilerDistinguishesTakeover(string code)
        {
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            int materializeAt = -1, firstYieldAt = -1, probeAt = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (materializeAt < 0
                    && (line.Contains("new List<CodeInstruction>", StringComparison.Ordinal)
                        || line.Contains("body.AddRange", StringComparison.Ordinal)))
                {
                    materializeAt = i;
                }
                if (probeAt < 0
                    && line.Contains("DeclaringType?.Name == \"RenderClockSource\"", StringComparison.Ordinal))
                {
                    probeAt = i;
                }
                if (firstYieldAt < 0 && line.Contains("yield return", StringComparison.Ordinal))
                {
                    firstYieldAt = i;
                }
            }
            if (materializeAt < 0 || probeAt < 0 || firstYieldAt < 0) return false;
            bool beforeYield = materializeAt < firstYieldAt && probeAt < firstYieldAt;
            // ② 的严格形状：那一行必须以判断收尾结束（Trim 后以 ) 结尾），
            // 即 `&& (mi.DeclaringType?.Name == "RenderClockSource"))` 这一形态。
            // 且 RenderClockSource 之后**不再有** && / || 合取——否则就是
            // 有人接了恒假项让探测失效（MUT-V10 那个形态）。
            string probeLine = lines[probeAt].TrimEnd();
            int at = probeLine.IndexOf("RenderClockSource", StringComparison.Ordinal);
            bool probeIsWhole = probeLine.EndsWith(")", StringComparison.Ordinal)
                && probeLine.IndexOf("&&", at, StringComparison.Ordinal) < 0
                && probeLine.IndexOf("||", at, StringComparison.Ordinal) < 0;
            // ③ 误报文案已移除。注释里可以讨论它（说明为什么不那么写），
            //    而 Check 统一剥注释，所以这里命中的一定是**真字符串**。
            bool staleMessage = code.Contains("game Update likely changed", StringComparison.Ordinal);
            return beforeYield && probeIsWhole && !staleMessage;
        }

        /// <summary>
        /// 每个 <c>GUILayout.Button(</c> 的实参里是否都出现了自定义样式
        /// （<c>Gui.…Style</c>）。
        ///
        /// 调用常跨行，样式落在三元表达式里（如
        /// <c>on ? Gui.AccentButtonStyle : Gui.QuietButtonStyle</c>），
        /// 所以从调用行起往后扫几行。
        ///
        /// 找不到任何 Button 调用时返回 false——判据的默认姿态是不信：
        /// "一处都没有"更像是判据自己失效（比如有人把调用改了名），
        /// 而不是"全都合规"。
        /// </summary>
        private static bool ButtonsUseCustomStyle(string code)
        {
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            int found = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("GUILayout.Button(", StringComparison.Ordinal))
                {
                    continue;
                }
                found++;
                bool custom = false;
                // 往后看 3 行：够覆盖跨行的三元样式；遇到分号就停，别把下一个控件算进来
                for (int j = i; j < lines.Length && j < i + 4; j++)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(lines[j], @"\bGui\.\w*Style\b"))
                    {
                        custom = true;
                        break;
                    }
                    if (j > i && lines[j].Contains(";", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
                if (!custom)
                {
                    return false;
                }
            }
            return found > 0;
        }

        /// <summary>
        /// 取某个调色板常量的 Rec.709 亮度；解析不到返回 NaN。
        /// 只认 <c>Name = new Color(r, g, b</c> 这种写法（本项目调色板全是这一种）。
        /// </summary>
        private static double ColorBrightness(string code, string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(code,
                @"\b" + name + @"\s*=\s*new\s+Color\(\s*([0-9.]+)f\s*,\s*([0-9.]+)f\s*,\s*([0-9.]+)f");
            if (!m.Success)
            {
                return double.NaN;
            }
            double r = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            double g = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            double b = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        /// <summary>FieldBg 是否真的比 CardBg 亮（两者都得解析得到）。</summary>
        private static bool FieldIsLighterThanCard(string code)
        {
            double card = ColorBrightness(code, "CardBg");
            double field = ColorBrightness(code, "FieldBg");
            if (double.IsNaN(card) || double.IsNaN(field))
            {
                return false;
            }
            return field > card;
        }

        /// <summary>
        /// <c>FileBrowser.PickFolder(</c> 是否**不在**画控件的方法体里。
        ///
        /// 做法：找到调用那一行，往回找最近的**成员声明行**（private/internal/
        /// public/protected 开头），看它的方法名是不是 <paramref name="drawingMethod"/>。
        /// 本项目的成员都直接挂在 static class 下（没有局部函数），所以"最近
        /// 的成员声明"就是它真正的宿主方法，不需要括号配对。
        ///
        /// **调用方必须点名它要防的那个方法**（不能写死一个名字）：这个形态有
        /// 两个调用点（Pages.DrawSaveDirRow 与 OnGui.RowInput），把宿主名写死在
        /// 辅助函数里的话，第二个调用点根本不可能被拦住。
        ///
        /// 找不到调用、或找不到宿主方法，一律判**不通过**——判据的默认姿态是
        /// 不信（与"不许出现 X"那几条相反，这里是"必须出现在 Y 里"，找不到
        /// 就是接线断了，而不是"没问题"）。
        /// </summary>
        private static bool PickFolderNotInDrawingMethod(string code, string drawingMethod)
        {
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("FileBrowser.PickFolder(", StringComparison.Ordinal))
                    continue;
                for (int j = i; j >= 0; j--)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(lines[j],
                        @"^\s*(?:private|internal|public|protected)\s+[\w<>\[\],\.\s]+\s+(\w+)\s*\(");
                    if (!m.Success) continue;
                    // 宿主是画控件那个方法 ⇒ 在 IMGUI 趟里弹模态窗 ⇒ 不通过
                    return m.Groups[1].Value != drawingMethod;
                }
                return false;
            }
            return false;
        }

        /// <summary>
        /// 统计 <c>GUILayout.Button(...)</c> 的 caption 里出现了某个 Loc 键几次。
        ///
        /// 只看 caption 位：热键行（RowHotkey）合法复用"取消保存"这个键，
        /// 只有按钮上的文字才必须与它触发的动作相符。
        /// </summary>
        private static int RegexButtonCaption(string code, string key)
        {
            // 正则必须用逐字字符串 @"..."：普通字符串里写 \" 是 CS1009
            // （无法识别的转义序列），而 [TF]\( 这类反斜杠会让转义更难读。
            const string Pattern = @"GUILayout\.Button\(\s*Loc\.[TF]\(""([^""]+)""";
            int n = 0;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(code, Pattern))
            {
                if (m.Groups[1].Value == key)
                    n++;
            }
            return n;
        }

        /// <summary>
        /// 滑条右侧的数值是否真的是**可编辑自绘输入框**。
        ///
        /// 判据落在 <c>RowSlider</c> 方法体内，不整文件搜：OnGui.cs 别处
        /// 合法地出现 <c>GUILayout.Label</c>（标签列就是），整文件判会假绿。
        ///
        /// 三件必须同时成立：
        ///   ① 调了自绘输入的 <c>.Draw(</c>——数值框是自绘的；
        ///   ② 用了按 key 缓存的字段（<c>GetField(</c> / <c>_fields</c>）——
        ///      不缓存的话每帧新建 TextField，编辑状态每帧归零，
        ///      **打一个字掉一个**，比只读还难用；
        ///   ③ 解析失败有复位路径（<c>NumberOf(</c>）——否则框里会留一段
        ///      永远提交不上去的乱码。
        ///
        /// 找不到 RowSlider 一律判**不通过**：判据默认姿态是不信，
        /// "一处都没有"更可能是判据自己失效（有人改了方法名）。
        /// </summary>
        private static bool SliderValueIsEditable(string code)
        {
            string body = MethodBody(code, "RowSlider");
            if (body == null) return false;
            return body.Contains(".Draw(", StringComparison.Ordinal)
                && body.Contains("GetField(", StringComparison.Ordinal)
                && body.Contains("NumberOf(", StringComparison.Ordinal)
                // 不许退回内建输入框（它会沿用 ADOFAI 深色皮肤，同判据 ②）
                && !body.Contains("GUILayout.TextField", StringComparison.Ordinal);
        }

        /// <summary>
        /// 某个方法体内是否**同时**出现了 hover 判定与 hover 色。
        ///
        /// 两个都要：只写了 hover 色不判鼠标位置 = 恒不生效（等于没做）；
        /// 只判鼠标位置不换色 = 判了也没用。这正是"变异形状要对准判据"那条——
        /// 只查其一的话，把另一半删掉判据照样绿。
        /// </summary>
        private static bool MethodUsesHover(string code, string method, string hoverColor)
        {
            string body = MethodBody(code, method);
            return body != null
                && body.Contains("Hover(", StringComparison.Ordinal)
                && body.Contains(hoverColor, StringComparison.Ordinal);
        }

        /// <summary>
        /// 取某方法的体（从签名行到下一个成员声明为止）。
        /// 本项目的成员都直接挂在 static class 下（没有局部函数），
        /// 所以"最近的成员声明"就是真正的宿主方法，不需要括号配对。
        /// 返回 null = 没找到该方法。
        /// </summary>
        private static string MethodBody(string code, string method)
        {
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            int start = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                // 匹配 `... method(` 且该行是成员声明（不是调用）
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
                sb.AppendLine(lines[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 页面里每一个控件标签键，是否都有对应的 `<key>.tip` 说明(中英两套齐)。
        ///
        /// **判据的输入是外部事实**(Loc.cs 的两张表与页面源码)，不是"我自己以为
        /// 写了"。提取方式按调用形状匹配 `OnGui.Row*("<key>"` 与
        /// `OnGui.Button("<key>"`--键都在**第一个实参**，与后续参数无关。
        ///
        /// 三种失败都要能抓：
        ///   ① 某个控件**根本没有** .tip(悬停无反应，玩家分不清是没写还是坏了);
        ///   ② 只有中文有、英文没有(英文界面下弹不出说明);
        ///   ③ 只有英文有、中文没有(中文界面下弹不出说明)。
        ///
        /// 找不到任何控件调用时返回 false--判据的默认姿态是不信："一处都没
        /// 匹配到"更可能是判据自己失效(有人改了调用形状)，而不是"全都合规"。
        /// </summary>
        private static bool EveryControlHasTip(string pagesCode, string projectRoot)
        {
            var keys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            // Row / RowSlider / RowDropdown / RowHotkey / RowInput / Button
            // 共用同一个形状：OnGui.<Method>("<key>", ...
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(pagesCode,
                         @"OnGui\.(?:Row|RowSlider|RowDropdown|RowHotkey|RowInput|Button)\(\s*""([a-z][a-z0-9_.]+)"""))
            {
                keys.Add(m.Groups[1].Value);
            }
            if (keys.Count == 0) return false;

            string locPath = Path.Combine(projectRoot, "Ui", "Loc.cs");
            if (!File.Exists(locPath)) return false;
            string loc = SrcUtil.Clean(File.ReadAllText(locPath));

            // 中英两表分开取：只查"整文件出现过 <key>.tip"会让"只写了一种语言"
            // 蒙混过关，而那正是英文玩家看不到说明的形态。
            int zhStart = loc.IndexOf("Dictionary<string, string> Zh", StringComparison.Ordinal);
            int enStart = loc.IndexOf("Dictionary<string, string> En", StringComparison.Ordinal);
            if (zhStart < 0 || enStart < 0 || enStart < zhStart) return false;
            string zhTable = loc.Substring(zhStart, enStart - zhStart);
            string enTable = loc.Substring(enStart);

            foreach (string k in keys)
            {
                string tip = k + ".tip";
                // 比较的是**带引号的键名**（"<key>.tip"），不是裸键名：
                // 裸串会让 "render.fps.tip" 命中 "render.fps.tip.extra" 这类前缀重叠，
                // 而本项目已经栽过一次前缀假判据（playback.openlevel）。
                string want = "\"" + tip + "\"";
                if (!zhTable.Contains(want, StringComparison.Ordinal)) return false;
                if (!enTable.Contains(want, StringComparison.Ordinal)) return false;
            }
            return true;
        }
        /// <summary>
        /// 说明框是否画在整页**之后**(DrawPage 之后、Space 之前)。
        ///
        /// 顺序是这条判据的全部内容：IMGUI 按调用顺序画，先画的会被后画的盖住。
        /// 把 DrawTooltip 挪到 DrawPage 前面，说明框就会压在页签与卡片底下--
        /// 而"完全看不见"与"没写说明"在玩家眼里是同一件事。
        ///
        /// 同时要求 ResetTips 出现在 DrawPage 之前：清空必须早于登记，
        /// 否则本帧登记完又被自己清掉，永远弹不出来。
        /// </summary>
        private static bool TooltipDrawnAfterPage(string code)
        {
            int page = code.IndexOf("DrawPage();", StringComparison.Ordinal);
            int tip = code.IndexOf("Gui.DrawTooltip();", StringComparison.Ordinal);
            int reset = code.IndexOf("Gui.ResetTips();", StringComparison.Ordinal);
            return page >= 0 && tip >= 0 && reset >= 0 && page < tip && reset < page;
        }

        /// <summary>
        /// 五种行控件(RowSlider / RowDropdown / RowHotkey / RowInput / 整行 Button)
        /// 是否都在自己的方法体里登记了悬停说明。
        ///
        /// **逐个方法判而不是整文件搜 <c>Tip(</c>**：整文件搜的话，只要有一处
        /// 调了 Tip，其余四处漏写也会绿--正是"改一处算全篇"那种假判据。
        /// </summary>
        private static bool AllRowControlsTip(string code)
        {
            foreach (string m in new[] { "RowSlider", "RowDropdown", "RowHotkey", "RowInput", "Button" })
            {
                string body = MethodBody(code, m);
                if (body == null || !body.Contains("Tip(", StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>
        /// 是否每个行助手在收尾时都补了行距。
        ///
        /// **必须逐个方法判。** 整文件搜 <c>RowGap();</c> 的话，只要有一处
        /// 调了它就全绿——而"其余行照旧咬在一起"正是这条判据要拦的形态，
        /// 也正是 ⑬d（悬停说明）必须逐方法判的同一个理由。
        ///
        /// <c>Row</c> 也在这里：它只是一行开关，没有任何按钮，但它同样
        /// 参与"行与行要不要分开"——开关 pill 与下面那颗按钮贴在一起时，
        /// 视觉上会被读成同一组的延续。
        /// </summary>
        private static bool AllRowsHaveGap(string code)
        {
            foreach (string m in new[]
                     { "Row", "RowSlider", "RowDropdown", "RowHotkey", "RowInput", "Button" })
            {
                string body = MethodBody(code, m);
                if (body == null || !body.Contains("RowGap();", StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>
        /// 说明框的实现是否三件齐：只在 Repaint 画、高度按内容算、宽度不随内容膨胀。
        ///
        /// ① **只在 Repaint**：Layout 趟画的东西会被丢掉，文字测量还会多算一遍;
        /// ② **高度按内容**：写死高度会让长句被裁掉半句，而"说明只显示半句话"
        ///    玩家不会当成缺陷报回来，只会以为说明就这么点内容;
        /// ③ **宽度写死**：跟着内容走会让长说明撑满整行甚至溢出窗口。
        /// </summary>
        private static bool TooltipBodyIsSane(string code)
        {
            string body = MethodBody(code, "DrawTooltip");
            if (body == null) return false;
            return body.Contains("EventType.Repaint", StringComparison.Ordinal)
                && body.Contains("CalcHeight(", StringComparison.Ordinal)
                && body.Contains("const float W =", StringComparison.Ordinal);
        }
        private static int Check(string projectRoot, string name, string rel,
            Func<string, bool> predicate)
        {
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
    }
}
