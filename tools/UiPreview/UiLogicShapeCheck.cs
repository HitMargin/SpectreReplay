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
                "Features\\VideoRender\\Patches\\VideoRenderClockPatches.cs",
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
                "Features\\VideoRender\\Pipeline\\VideoAudioTrack.cs",
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
                "Pages\\Pages.cs",
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
            // ⑬g 说明框的边界必须是「我们所在的那个窗口」，不能是 Screen。
            //
            //    2026-10-18 用户截图报「tip显示超过UMM的界面了」。根因不是配色
            //    也不是宽度，而是坐标系搞错了：我们的 OnGUI 是 UMM 用 GUI.Window
            //    回调起来的，控件矩形与鼠标位置**全是窗口内局部坐标**，而原实现
            //    拿 Screen.width/height 判越界。窗口宽约 700、屏幕 1920 ⇒
            //    `x + W > 1920 - 8` 恒不成立，贴边翻折那段**形同不存在**。
            //
            //    这条特别值得固化成判据：它不报错、不抛异常、不改变任何布局，
            //    只在窗口窄于屏幕时把框画到窗口外——而绝大多数玩家的屏幕都宽于
            //    UMM 窗口，所以它稳定复现却完全不像代码问题。
            //    判据钉两件事：不许再用 Screen 判边界；必须去取可视矩形。
            fail += Check(projectRoot,
                "⑬g 说明框边界必须按窗口可视区算(用 Screen 判越界会画出面板被裁)",
                "Ui\\Gui.cs",
                s => TooltipBoundsUseViewport(s));
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
            // ⑱ 总开关关掉时，它下面的子设置必须压暗（2026-10-17 用户要求：
            //    「如果 且不能调整一个子功能的总开关被关闭了 那么 子设置应为灰色」）。
            //
            //    **这条守的不是"有没有灰色常量"**，而是"子设置到底接没接上总开关"。
            //    三种形态各自都会出问题，而且都"看着像做了"：
            //      ① 整块分组没传总开关 ⇒ 总开关关了，子项照样能点（等于没做）；
            //      ② 传了但传的是 `true` 常量 ⇒ 看着写了、永远不生效；
            //      ③ 传了别的字段 ⇒ 灰的时机全错，比不灰更糟。
            //    所以判据要**逐个分组/逐行**钉住那个实参，不是数"出现过 Dim("。
            //
            //    **为什么放在这个文件而不是 PortedCompletenessCheck**：那一个要读
            //    Pages.cs，而 Pages.cs 没链进它的源码集，CheckPredicate 会因为
            //    "找不到文件"判红——那个红与代码无关，是假红。本套件已在读
            //    Pages.cs（⑫ 就是），放在这里才真的测到。
            fail += Check(projectRoot,
                "⑱b 去特效六个子分组必须挂总开关(关掉时整块压暗)",
                "Pages\\Pages.cs",
                s => EffectsSectionsDimWithMaster(s));
            fail += Check(projectRoot,
                "⑱c 录制页保存策略必须挂录制总开关",
                "Pages\\Pages.cs",
                s => RecordRowsDimWithReplay(s));
            fail += Check(projectRoot,
                "⑱d 渲染参数整块必须挂渲染总开关",
                "Pages\\Pages.cs",
                s => s.Contains("Gui.Dim(!RenderOn, DrawRenderOptions)", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "⑱e 键盘音两项必须挂录音开关",
                "Pages\\Pages.cs",
                s => KeybdRowsDimWithRecord(s));
            fail += Check(projectRoot,
                "⑱f 去特效「全选本组」只许长在 visual/track/planet 三组且都挂总开关",
                "Pages\\Pages.cs",
                s => ToggleAllPerSection(s));
            fail += Check(projectRoot,
                "⑱f2 去特效「全选本组」必须是双向切换(否则批量打开后无法批量撤销)",
                "Pages\\Pages.cs",
                s => ToggleAllBidirectional(s));
            fail += Check(projectRoot,
                "⑱g 镜头缩放：开关挂「镜头移动」，滑条再挂该开关(否则拖了没用)",
                "Pages\\Pages.cs",
                s => CameraZoomGatedByParent(s));
            fail += Check(projectRoot,
                "⑱i「移除全部装饰」挂两个父项(它是清除口径,父项关着时拨了没用)",
                "Pages\\Pages.cs",
                s => RemoveAllDecorGatedByParent(s));
            // ⑲ 更新检查：三种态必须分开说，且版本比较方向不能反。
            //
            //    2026-10-17 用户要求「关于列表里加一个检测更新的功能 并且用上UMM
            //    自带的 Repository HomePage」。这条功能真正的风险不是「没做」，
            //    而是**做了但在撒谎**：
            //      ① NewestVersion 为 null 有两种含义——「已是最新」与「UMM 根本没查」
            //         （玩家关了 CheckUpdates / 没网）。把后者说成前者，玩家会以为
            //         自己已经是最新，永远等不到更新，而且没有任何迹象。
            //      ② 版本比较写成 `newest < current` 或 `newest == current` 时，
            //         「有新版」永远不显示——功能看着在、永远不响。
            //      ③ 把 Info.Version（string）拿去比 Version：编译就会挡，但只测
            //         到「能编译」不够，要钉住它读的是 ModEntry.Version。
            //
            //    判据必须**判分支结构**而不是「出现过 NewestVersion」——后者在把
            //    整个 else 分支删掉时照样绿。
            fail += Check(projectRoot,
                "⑲a 更新状态：有新版/已最新/未检查三态必须分开（已最新必须依据「查过没有」，不是 newest!=null）",
                "Pages\\Pages.cs",
                s => UpdateStatesAreDistinct(s));
            fail += Check(projectRoot,
                "⑲a2 必须真去读 UMM 的 Params.LastUpdateCheck(区分查过与没查过的唯一旁证)",
                "Pages\\Pages.cs",
                s => UpdateReadsLastUpdateCheck(s));
            fail += Check(projectRoot,
                "⑲b 版本比较方向必须是 newest > current",
                "Pages\\Pages.cs",
                s => UpdateVersionCompareDirection(s));
            fail += Check(projectRoot,
                "⑲c 当前版本必须读 ModEntry.Version(Version 类型),不是 Info.Version(string)",
                "Pages\\Pages.cs",
                s => UpdateUsesModEntryVersion(s));
            fail += Check(projectRoot,
                "⑲d info.json 必须填 Repository 与 HomePage(UMM 靠这两个字段检查更新)",
                "info.json",
                s => InfoJsonHasUpdateFields(s));
            // ⑳ 拨开关必须**当场**刷新编辑器保存按钮（2026-10-18 用户报的缺陷：
            //    「为什么去特效的允许覆盖关卡开关后不会实时生效？」）。
            //
            //    形态：`Commit()` 只做「落盘 + 重挂补丁」，`SpectrePatchHub.Refresh()`
            //    只重挂补丁——**两者都不碰任何 UI 状态**。而全仓唯一写
            //    `buttonSave.interactable` 的是 `EffectRemover.SetEditorSaveButtons`，
            //    它只被 `scnEditor.LoadGameScene` 的 Postfix 调一次。于是按钮态是
            //    **进编辑器那一刻定死的**，之后拨开关纹丝不动。
            //
            //    最坏的后果不是「按钮没变」，而是**两边脱节**：`EffectRemoverEnableSave`
            //    已改成允许、`SaveLevelEditorAction.Execute` 的 Prefix 也已放行，
            //    但按钮仍是禁用态 ⇒ 玩家根本点不动；反过来按钮亮着、Prefix 在拦，
            //    点了没反应且不报错。
            //
            //    ⑳a 与 ⑳b 分开判：单看「文件里出现过 RefreshEditorSaveButtons」
            //    是假判据——总开关那一处的 RestoreEditorSaveButtons 也在同一段附近，
            //    漏掉「允许覆盖关卡」那一处照样绿。
            fail += Check(projectRoot,
                "⑳a 「允许覆盖关卡」拨动后必须当场刷新保存按钮(Commit 只落盘、不碰 UI)",
                "Pages\\Pages.cs",
                s => RowOnChangeCalls(s, "effects.enablesave", "RefreshEditorSaveButtons"));
            fail += Check(projectRoot,
                "⑳b 去特效总开关 onChange 两半都要刷新按钮(只接关掉那一半会留下脱节)",
                "Pages\\Pages.cs",
                s => EffectMasterToggleRefreshesButtons(s));
            // ⑳c 2026-10-24 全项目审计 D-F2：「谱面已被就地改写」比总开关活得久。
            //   玩家开去特效进关（Decode 已改完内存里的谱面）之后把开关关掉——
            //   保存拦截若随 Feature 一起卸载，Ctrl+S 就会把剥过的数据写回原文件，
            //   原谱直接没了。所以这两条补丁必须常开，由 EditorSaveEnabled 自己判。
            fail += Check(projectRoot,
                "⑳c 去特效的保存拦截/按钮刷新不许挂总开关(改写残留态要求在功能关闭后仍被拦住)",
                "Features\\EffectRemover\\Patch_EffectRemover.cs",
                s => SaveGuardNotFeatureGated(s));
            // ㉑ 特殊按键过滤必须**真的会生效**（2026-10-18 接线）。
            //
            //    这功能的失败方式是本项目最典型的一种：补丁、界面、配置三段全在，
            //    只差 `FeatureKeys` 与 `SetFeatureToggle` —— PatchHub 对未注册的
            //    功能按「关闭」处理、只打一行告警，于是玩家勾了一堆键、回放里照样
            //    没有，而这个现象与「游戏本来就不录这些键」一模一样。
            //
            //    ㉑a 两条输入路径**各自**都要挂 Feature —— 只挂一条时，玩家切到
            //    另一种输入模式就静默失效，现象与「根本没实现」无法区分。
            //    ㉑b Main 必须注册谓词（否则 ㉑a 挂的 Feature 永远不会被求值成真）。
            //    ㉑c Escape 不能被放开 —— 它来自同一个返回列表里的 `Cancel()`，
            //    放开会让回放真的按下取消键、打断进关流程。
            fail += Check(projectRoot,
                "㉑a 特殊按键两条输入路径都要挂 Feature(轮询 + 异步，缺一是静默半残)",
                "Features\\Replay\\Patches\\Patch_SpecialKeys.cs",
                s => BothInputPathsHaveFeature(s));
            fail += Check(projectRoot,
                "㉑b Main 必须注册 SpecialKeys 的谓词(未注册 = 按关闭处理)",
                "Main.cs",
                s => s.Contains("FeatureKeys.SpecialKeys,", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "㉑c Escape 不得进入允许集(它是取消键，放开会打断进关流程)",
                "Features\\Replay\\Patches\\Patch_SpecialKeys.cs",
                s => s.Contains("KeyCode.Escape", StringComparison.Ordinal)
                    && s.Contains("\"Escape\"", StringComparison.Ordinal));
            // ㉒ 「删除回放文件」必须是**两段式**（2026-10-18 用户要求新增该按钮）。
            //
            //    这条按钮的失败方式与别的不同：它不是「没反应」，而是**删错**。
            //    回放文件是玩家唯一产出，不走回收站、不可撤销，而它旁边就是
            //    「回放」主按钮——单点即删时手滑一次那局就没了。所以判据不看
            //    「有没有删除调用」，只看「删除是不是发生在第二次点击之后」。
            //
            //    ㉒a 必须真的删文件（否则按钮是个摆设）；
            //    ㉒b 第一次点击**不许**出现 File.Delete——这是两段式的全部意义；
            //    ㉒c 删的若是当前载入那份，必须连带清 _loadedPath（否则界面
            //        一直写着「已载入」一个不存在的文件，看起来像删除失败）；
            //    ㉒d 删完必须刷列表缓存（1 秒缓存会把已删的行再挂 1 秒，
            //        玩家再点一下就是删一个不存在的路径）。
            fail += Check(projectRoot,
                "㉒a 每行要有删除按钮且真的调 File.Delete",
                "Pages\\Pages.cs",
                s => s.Contains("DrawDeleteFileButton(captured)", StringComparison.Ordinal)
                    && MethodBody(s, "DrawDeleteFileButton") is string db
                    && db.Contains("File.Delete(path)", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "㉒b 删除必须两段式：第一次点击不许删文件",
                "Pages\\Pages.cs",
                s => DeleteIsTwoStep(MethodBody(s, "DrawDeleteFileButton")));
            fail += Check(projectRoot,
                "㉒c 删掉当前载入的回放要连带卸载(否则界面还显示已载入一个不存在的文件)",
                "Pages\\Pages.cs",
                s => MethodBody(s, "AfterReplayDeleted") is string ab
                    && ab.Contains("_loadedPath = \"\"", StringComparison.Ordinal)
                    && ab.Contains("UnloadReplay", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "㉒d 删完要刷列表缓存(1 秒缓存会让已删的行还在，再点就是删不存在的路径)",
                "Pages\\Pages.cs",
                s => MethodBody(s, "AfterReplayDeleted") is string ab2
                    && ab2.Contains("_replayCache = null", StringComparison.Ordinal));
            // ㉓ 回放详情面板（2026-10-18 用户要求「点击回放文件可以看信息」）。
            //
            //    这块最容易出的错不是「没画出来」，而是**为了拿数据把文件装载了**：
            //    载入路径里那句 TryLoadKeybdSound 会无条件改写全局键盘音，
            //    于是「看一眼信息」就把当前正放的回放的键盘音顶掉——没有报错、
            //    现象是「播放到一半突然没键盘声了」，几乎追不到是点了详情。
            fail += Check(projectRoot,
                "㉓a 点文件名要能展开详情(ClickableLabel + Toggle + Draw)",
                "Pages\\Pages.cs",
                s => s.Contains("Gui.ClickableLabel(", StringComparison.Ordinal)
                    && s.Contains("ReplayInfoPanel.Toggle(captured)", StringComparison.Ordinal)
                    && s.Contains("ReplayInfoPanel.Draw(captured)", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "㉓b 读详情必须无副作用(走 ReadReplayData，不许 LoadReplay)",
                "Features\\Replay\\Data\\ReplayFileInfo.cs",
                s => s.Contains("ReplayManager.ReadReplayData(", StringComparison.Ordinal)
                    && !s.Contains("ReplayManager.LoadReplay(", StringComparison.Ordinal)
                    && !s.Contains("LoadReplayAuto(", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "㉓c 删掉文件后要收起展开位(否则下一帧去读不存在的路径)",
                "Pages\\Pages.cs",
                s => MethodBody(s, "AfterReplayDeleted") is string ab
                    && ab.Contains("ReplayInfoPanel.Forget(", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "㉓d 判定分布文案键必须是字面量表(动态拼键会被判成死键)",
                "Features\\Replay\\Data\\ReplayFileInfo.cs",
                s => CountOccurrences(s, "\"playback.judge.") >= 16
                    && s.Contains("JudgmentLocaleKeys", StringComparison.Ordinal));
            // ㉔ 官方关卡回放必须记录**真实 BPM**（2026-10-18 用户截图报「官方关卡
            //    的 BPM 怎么是个问号」）。
            //
            //    根因不在面板：录制时 `if (ADOBase.isOfficialLevel)` 那一段把
            //    `data.Bpm` 硬写成 0.0，面板再把 `<= 0` 渲染成「?」。
            //    面板的显示规则本身是对的（0 确实不该显示成 0 BPM）——所以
            //    修的地方在录制侧，判据也必须钉在录制侧，否则改面板只会把
            //    「?」变成「0」，仍然是错的。
            //
            //    **为什么填真值不会破坏校验**：TryStartPlayback 里的 Pitch/Bpm
            //    比对整段包在 `!ADOBase.isOfficialLevel &&` 后面，官方关卡从不比
            //    这两个值。判据因此要同时钉住两件事：写入的是活的取值函数，
            //    且该函数走 conductor（scnGame.instance 可能为 null）。
            fail += Check(projectRoot,
                "㉔ 官方关卡回放必须记录真实 BPM(硬写 0 会让详情面板显示「?」)",
                "Features\\Replay\\Patches\\Patch_Lifecycle.cs",
                s => OfficialLevelBpmIsReal(s));
            // ㉕ 详情面板必须同时给 Accuracy 与 X-Accuracy（2026-10-18 用户报
            //    「有X-ACC少了ACC」）。
            //
            //    **为什么这不算"多加一行好看"**：两者不是同一指标的两种精度。
            //    xacc 按档位加权（TooEarly 也有 0.2 分），Accuracy 只数完美占比
            //    再加内档奖励——同一局可以 99.83% 与 96.5% 并存。只显示 xacc
            //    会让玩家以为"我几乎全完美，怎么才 97%"，且与游戏结算面板对不上。
            //
            //    判据钉两件事：① 面板真的两行都画；② Accuracy 不是另存字段，
            //    而是由判定分布按官方公式还原（老回放没有这个字段，另存必然
            //    对旧文件显示占位符）——所以必须走 HitMarginCompat.Accuracy。
            fail += Check(projectRoot,
                "㉕ 详情面板必须同时显示 Accuracy 与 X-Accuracy",
                "Pages\\ReplayInfoPanel.cs",
                s => MethodBody(s, "DrawStats") is string ds
                    && ds.Contains("playback.info.acc", StringComparison.Ordinal)
                    && ds.Contains("playback.info.xacc", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "㉕b Accuracy 必须由判定分布还原(老回放没存这个字段，另存会显示占位符)",
                "Features\\Replay\\Data\\ReplayFileInfo.cs",
                s => s.Contains("HitMarginCompat.Accuracy(info.JudgmentCounts)", StringComparison.Ordinal));
            // ㉖ 渲染必须**自己**把回放记录的倍速装回编辑器（2026-10-18 用户报
            //    「等待回放开始超时」且画面没变慢）。
            //
            //    根因在游戏源码里：`scnEditor.Play()` 对程序化调用无条件写
            //    `playbackSpeed = holdingControl ? shortcut/100f : 1f`
            //    （scnEditor.cs:7204-7205，holdingControl 只有真按住 Ctrl 才为真），
            //    紧接着 `RemakePath → SetupConductorWithLevelData` 用它推导
            //    `song.pitch`（scrConductor.cs:378-395）。而 TryStartPlayback 的
            //    倍速校验拿它与录像比对，于是**每次渲染都判 1x ≠ 1.5x 并拒绝**，
            //    StartPlaying 从未执行——关卡自己跑起来，10 秒后报超时。
            //
            //    修法不能是"让用户先按住 Ctrl 再点渲染"：渲染是自己重建关卡的，
            //    条件必须由渲染准备。判据钉三件事，缺一条都会退回原样：
            //      ① 注入点必须在 `RemakePath` 的 Prefix（Play() 内部唯一经过的钩子，
            //         早于 Play() 会被覆盖、晚于 Play() 时 pitch 已算错）；
            //      ② 写回的是回放数据里的 `PlaybackSpeed`，且受 `QuickPitched` 门控
            //         （非飙速录像写 1.5 会让校验按 1.0 通过、时间轴却已拉高）；
            //      ③ 作业收尾必须撤销武装（否则倍速泄漏到之后的手动重开关卡）。
            fail += Check(projectRoot,
                "㉖a 渲染必须自己装回录像倍速(Play() 会把程序化调用的倍速压成 1x)",
                "Features\\VideoRender\\Patches\\VideoRenderPatches.cs",
                // **必须锚到带 scnEditor 形参的那个 Prefix。** 本文件里还有
                // Patch_VideoPinSet.Prefix 等同名方法，MethodBody 取到的是第一个
                // （实测：第一版写成 MethodBody(s, "Prefix") 直接假红）。
                s => s.Contains("internal static void Prefix(scnEditor __instance)", StringComparison.Ordinal)
                    && s.Contains("ApplySpeedOverrideTo", StringComparison.Ordinal)
                    && s.Contains("nameof(scnEditor.RemakePath)", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "㉖b 倍速来源必须是录像的 PlaybackSpeed 且受 QuickPitched 门控",
                "Features\\VideoRender\\Pipeline\\VideoRenderJob.cs",
                s => SpeedOverrideComesFromReplay(s));
            fail += Check(projectRoot,
                "㉖c 渲染收尾必须撤销倍速钉扎(否则泄漏到之后的手动重开关卡)",
                "Features\\VideoRender\\Pipeline\\VideoRenderJob.cs",
                // 2026-10-19 收尾后台化后，这行随其余的"交还游戏"动作一起搬进了
                // ReleaseGameControl（原先直接写在 TearDown 体里）。
                // **判据跟着改，但原意不变**：撤销动作必须真实存在于收尾路径上——
                // 所以既要求 ReleaseGameControl 体里有它，又要求 TearDown 真的调用它。
                // 只查"文件里出现过 SpeedOverrideArmed = false"会把一处死代码也算通过。
                s => MethodBody(s, "ReleaseGameControl") is string rb
                    && rb.Contains("SpeedOverrideArmed = false", StringComparison.Ordinal)
                    && MethodBody(s, "TearDown") is string tb
                    && tb.Contains("ReleaseGameControl", StringComparison.Ordinal));
            // ㉖d 收尾后台化**之前**必须先交还游戏控制。
            //
            //    **这条守的是 2026-10-19 真实踩到的回归**：渲染期有三个虚拟时钟
            //    （PinnedSongPosition / UnscaledDeltaPin / RenderClockSource），
            //    它们的推进全在捕获循环里。原先收尾同步阻塞主线程，游戏没机会跑；
            //    把收尾搬到后台后，主线程空出来带着**冻结的时钟**继续跑游戏循环，
            //    现象就是「渲染结束后星球直接停住」——进度条已走完，星球不动。
            //
            //    判据钉的是**顺序**：Phases 里 ReleaseGameControl 必须出现在
            //    StartFinalizeOnWorker 之前。只查"两个名字都出现过"是假判据——
            //    顺序反了照样命中，而顺序反了正是这个 bug 的形态。
            fail += Check(projectRoot,
                "㉖d 后台收尾启动前必须已交还游戏控制(否则冻结时钟让星球停住)",
                "Features\\VideoRender\\Pipeline\\VideoRenderJob.cs",
                s => MethodBody(s, "Phases") is string pb
                    && pb.IndexOf("ReleaseGameControl", StringComparison.Ordinal) >= 0
                    && pb.IndexOf("StartFinalizeOnWorker", StringComparison.Ordinal) >= 0
                    && pb.IndexOf("ReleaseGameControl", StringComparison.Ordinal)
                       < pb.IndexOf("StartFinalizeOnWorker", StringComparison.Ordinal));
            // ㉗ 「等待回放开始超时」必须**带出真正原因**（同一次用户报障）。
            //
            //    原先失败路径只有一句 wait_timeout；而日志里其实早有
            //    `拒绝: ... actual=1`——只是没人把它和超时联系起来。
            //    更糟的是 `!is_playing` 那条 continue 把后面两个接受条件全屏蔽，
            //    于是哪怕关卡已经在跑（songLive / PlayerControl），也只能空转到
            //    10 秒。判据：拒绝原因要接线到失败通知，且早退必须同时要求
            //    「关卡已在跑」——只看拒绝记录会误杀连发 Start_Rewind 中后一发成功的轮次。
            fail += Check(projectRoot,
                "㉗a 等待失败时必须报出真实拒绝原因(不许只说超时)",
                "Features\\VideoRender\\Pipeline\\VideoRenderJob.cs",
                s => s.Contains("playbackRejectReason", StringComparison.Ordinal)
                    && s.Contains("Patch_StartRewind.LastReject", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "㉗b 早退必须同时要求「关卡已在跑」+「有拒绝记录」(只看其一会误杀)",
                "Features\\VideoRender\\Pipeline\\VideoRenderJob.cs",
                s => AwaitReplayAliveRequiresBoth(s));
            // ㉘ 等待回放开始的那段预算必须是**真实秒数**，不能是帧数。
            //
            //    2026-10-20 现场：scnGame 视频渲染报「等待回放开始超时」，日志里
            //    is_playing=True / songLive=True / state=Countdown。倒计时本身没坏，
            //    是预算算错了：SaveGlobalState 在等待之前就设好了
            //    Time.captureFramerate=fps / vSyncCount=0 / targetFrameRate≥1000，
            //    等待循环每帧只花亚毫秒级，旧版 600 帧不到一秒就跑完；而进
            //    PlayerControl 的判决（beatNumber 追上 adjustedCountdownTicks）走的是
            //    真实 AudioSettings.dspTime：buffer 0.5s + 4 拍 ≈ 2.1s，必然误报。
            //
            //    两条一起判，缺一条都能被「看起来改了」骗过：
            //    ① 方法体里不许再有按帧的循环预算（旧形态是 fps*10）；
            //    ② 必须真的用 System.Diagnostics.Stopwatch 计时。
            //    也不许改用 Time.realtimeSinceStartup / Time.unscaledTime——它们已被
            //    UnscaledDeltaPin 换成虚拟时钟，推进它的 TickFrame() 只在捕获循环里，
            //    等待期读到的值是冻住的，那等于把「太快误报」改成「永不超时」。
            fail += Check(projectRoot,
                "㉘ 等待回放的预算必须按真实秒数记(按帧算会在倒计时没走完时误报超时)",
                "Features\\VideoRender\\Pipeline\\VideoRenderJob.cs",
                s => MethodBody(s, "AwaitReplayAlive") is string ab
                    && !ab.Contains("fps * 10", StringComparison.Ordinal)
                    && ab.Contains("System.Diagnostics.Stopwatch", StringComparison.Ordinal));
            // ㉙ 等待放行必须承认「倒计时也算回放已接管」。
            //
            //    2026-10-20 成片 Render_20261020_023143_再生.mp4 整段没有倒计时：
            //    旧闸要求 state==PlayerControl，而 scrController.Countdown_Update
            //    是「beatNumber 追上 adjustedCountdownTicks」才切 PlayerControl，
            //    beatNumber 又靠 songposition 逐拍 +1（每帧最多一拍），切状态时
            //    songposition 已经 ≈1.24 s ⇒ 捕获起点永远在倒计时之后。
            //
            //    判据拆成两半，缺一条都能被「看起来改了」骗过：
            //    ① 计数阶段的门必须真的是 States.Countdown（改成别的状态就红，
            //       这正是 MUT-D23 的手法）；
            //    ② 原来那两道 PlayerControl + 连续两帧正位置的闸必须保留——
            //       检查点开局 / forceNoCountdown 的谱面没有倒计时，全靠它挡
            //       「seek 还没把位置写回」的瞬间。
            fail += Check(projectRoot,
                "㉙ 等待放行必须允许倒计时阶段(否则倒计时整段录不到)且保留 PlayerControl 那道闸",
                "Features\\VideoRender\\Pipeline\\VideoRenderJob.cs",
                s => CountdownReleaseKeepsPlayGate(s));
            // ㉚ 渲染选项里的「鼠标指针」开关必须真的通到像素上。
            //
            //    这一项最容易做成假功能：界面上加一行、Settings 加个字段，两边都
            //    编译过，但**没人把开关读到画帧的地方**——这正是本项目反复栽的
            //    「有开关、有说明、没实现」。三段分开判，红了能直接看出断在哪：
            //    ① 界面上有 `render.cursor` 这一行且回调真的写 RenderCursorOn；
            //    ② 渲染作业把设置交给画面源（frames.DrawCursor）；
            //    ③ 画面源在像素交付路径上真的调 CursorOverlay.Draw，且指针位置
            //       是 Grab 那一刻采样的（交付时现读会甩尾两三帧）。
            fail += Check(projectRoot,
                "㉚ 渲染选项里的鼠标指针开关必须接到 Settings.RenderCursorOn",
                "Pages\\Pages.cs",
                s => s.Contains("OnGui.Row(\"render.cursor\", RenderCursorOn", StringComparison.Ordinal)
                    && s.Contains("RenderCursorOn = v;", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "㉛ 渲染作业必须把「录不录指针」交给画面源(否则开关只在界面上存在)",
                "Features\\VideoRender\\Pipeline\\VideoRenderJob.cs",
                s => s.Contains("frames.DrawCursor = Settings.RenderCursorOn;", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "㉜ 指针要在像素交付路径上画且位置随 Grab 采样(交付时现读会甩尾)",
                "Features\\VideoRender\\Pipeline\\VideoFrameSource.cs",
                s => CursorInkedIntoFrame(s));
            // ㉝ 渲染起点必须取**玩家点渲染时选中的砖块**，不许回退录像的 data.StartTile。
            //
            //    2026-10-21 现场：在第 N 块点「渲染」，成片却从第 0 块开始——必须在
            //    选中的地板上先手动播一次、退出，再渲染才正确。成因是起点取的是
            //    SpectreState.data.StartTile（录像里记的**录制时**起点），而玩家这次
            //    想从哪开始跟录像无关。
            //
            //    判据拆成四段，红了能直接看出断在哪一段：
            //    ① 起点由 ResolveRequestedStartFloor() 解析，且**抓取发生在重启之前**
            //       ——SwitchToPlayMode/Play() 会重开关卡，之后选区已经不是玩家原来那个；
            //    ② 解析只认 editor.selectedFloors[0].seqID（与 scnEditor.Play() 的
            //       SelectionIsSingle 分支同式），**不许读 GCS.checkpointNum**——那是
            //       Play() 写入的「上次播过哪里」，首次打开关卡时是 0，正是本 bug 的形态；
            //    ③ Phases 里不许再出现 StartTile：出现即回退；
            //    ④ 起点要真的送进 Play()：Clamp → SelectFloor → checkpointNum → Play 的顺序。
            fail += Check(projectRoot,
                "㉝ 渲染起点必须取玩家当前选区(不许回退录像的 data.StartTile)",
                "Features\\VideoRender\\Pipeline\\VideoRenderJob.cs",
                s => RenderStartFloorComesFromSelection(s));
            // ㉞ 检查点开局（= 从中间砖块渲染）必须在 States.Checkpoint 放行。
            //
            //    2026-10-21 现场：从第 N 块点渲染，成片开头没有倒计时。根因不在倒计时
            //    长度，而在**放行条件**：从中间开局走的是 scrController.OnMusicScheduled
            //    的检查点分支（:1611 置 hasSongStarted=true → Scrub 到该砖块 entryTime
            //    − 4 拍 → :1613 ChangeState(States.Checkpoint)），**整段倒计时都在
            //    States.Checkpoint 里放完**，状态机从不经过 States.Countdown；而旧闸
            //    只认 Countdown / PlayerControl，于是一路等到 PlayerControl 才放行——
            //    那正好是倒计时结束的瞬间，开头必然丢失。
            //
            //    四段缺一不可，每段对应一种「看起来改了」的假修：
            //    ① checkpointing 这个活变量真的从 controller.state 取 States.Checkpoint；
            //    ② levelRunning 把 checkpointing 算作「关卡在跑」，否则等待期会被
            //       LastReject 早退判死，还没放行就先报失败；
            //    ③ hasSongStarted 那条放行路要**同时**认 checkpointing——只做 ①② 不做
            //       这里，状态认得出来却仍然不放行（这正是本 bug 的半修形态）；
            //    ④ 那条路保留 freshStartPosition 新鲜度闸：hasSongStarted 一旦为真就
            //       永远为真，不做新鲜度判断的话，上一轮播完位置停在结尾，下一轮等待
            //       会立刻误放行（录到一段空转画面）。
            fail += Check(projectRoot,
                "㉞ 检查点开局(从中间渲染)必须在 Checkpoint 放行(否则倒计时整段录不到)",
                "Features\\VideoRender\\Pipeline\\VideoRenderJob.cs",
                s => CheckpointStartCanRelease(s));
            // ㉟ 控件宽度必须按**实测文案**算，不许写死。
            //
            //    2026-10-27 用户截图：云配置备份那两颗按钮的英文文案
            //    「Upload Config to Cloud」被截成「Jpload Config to Clouc」——
            //    按钮矩形写死 150f，而文案比 150 宽。同类写法当时还有 10 处
            //    （回放/删除/卸载/打开关卡/换目录…），全部在长文案语言下裁字。
            //
            //    为什么判据是「禁止写死」而不是「把 150 调成 190」：调数字只对
            //    当下这一种语言有效，14 张语言表里还有更长的（西语
            //    「Confirmar eliminación」、俄语「Подтвердить удаление」）。唯一
            //    不随语言失效的做法是量出来。
            //
            //    ① Pages.cs 里不许再出现 GUILayout.Width(数字：按钮、徽章、
            //       尺寸标签一律走 Gui.ButtonW / Gui.BadgeRect；
            //    ② 按钮文案不许内联 Loc.*：不先取到变量就无从测宽，这一条正好
            //       卡住「看着改了、其实还是老写法」的半修形态；
            //    ③ 测宽工具必须真的走 GUIStyle.CalcSize，而不是又一张常量表。
            fail += Check(projectRoot,
                "㉟a 布局宽度不许写死(GUILayout.Width(数字) 一律禁止：长文案语言会裁字)",
                "Pages\\Pages.cs",
                s => NoNumericLayoutWidth(s));
            fail += Check(projectRoot,
                "㉟b 按钮文案不许内联 Loc.*(必须先取变量才谈得上按文案测宽)",
                "Pages\\Pages.cs",
                s => ButtonsHoistCaption(s));
            fail += Check(projectRoot,
                "㉟c 测宽工具必须走 GUIStyle.CalcSize(ButtonW/BadgeRect)",
                "Ui\\Gui.cs",
                s => WidthToolsMeasureText(s));
            // ㊱ label 派生样式必须**逐个显式** clipping=Clip。
            //
            //    2026-10-29 用户截图：回放列表里长文件名的尾部「钻进」右侧
            //    「回放」按钮底下，字面消失。根因是 UMM 把 skin.label 的 clipping
            //    改成了 Overflow（Gui.cs Ensure 的注释里有来历），克隆自它的样式
            //    全部继承：超出矩形的文字不是裁掉，而是**继续画出去**；同一行
            //    右侧的按钮是后画的，底色正好盖在溢出的字上。同一根因此前已经
            //    各修过一轮局部：输入框用 BeginClip 自剪、说明文字显式设了 Clip
            //    ——都是哪疼修哪，其余八个 label 派生样式一直漏着。
            //
            //    判据逐个初始化器检查：每个 new GUIStyle(GUI.skin.label) 的
            //    大括号里必须写明 clipping = TextClipping.Clip。只写 clipping
            //    不写值、或写成 Overflow，都算失败——这条防的是「新加一个样式
            //    忘了设」和「顺手把显式设置删掉当冗余」两种回归。
            fail += Check(projectRoot,
                "㊱ label 派生样式必须逐个显式 Clip(继承 Overflow 会把字画进后画控件底下)",
                "Ui\\Gui.cs",
                s => LabelStylesExplicitlyClip(s));
            // ㊲ 顶栏状态徽章必须按文案实测宽度（右缘对齐、向左生长）。
            //
            //    ㉟ 那轮把列表徽章/按钮全改成了实测宽（写死 46f/150f 在长文案
            //    语言下裁字），但 Header 里的状态徽章漏网：写死 122×20，而状态
            //    是一整句话（status.all_off 最长 22 个字 ≈ 240px），文字会从
            //    胶囊两边溢出。判据钉在 Header 方法体内：宽度必须来自
            //    _smallC.CalcSize，且写死的 -132f 定位不许回来。只查整个文件
            //    是假判据——BadgeRect 里合法地有 CalcSize，会把没修的 Header
            //    陪绿。
            fail += Check(projectRoot,
                "㊲ 顶栏徽章必须按文案实测宽(写死 122 时长状态句从胶囊两边溢出)",
                "Ui\\Gui.cs",
                s => HeaderBadgeMeasuresText(s));
            // ㊳ OnGui 的行助手同样不许写死布局宽度。㉟a 只扫 Pages.cs，而
            //    行助手里有一批写死宽度的按钮：2026-10-30 用户截图，键位重映射
            //    行的「Delete」在英文下比写死的 56px 内容区宽，按钮样式把词折成
            //    两行，22px 高的按钮装不下两行，字被拦腰裁断——「Clear」短所以
            //    没事，看上去就像随机坏。键位槽写死 74 同理：长键名
            //    （PrintScreen）与等待提示（按下任意键…）都会折行。宽度纪律与
            //    Pages.cs 同一套：要么 Gui.ButtonW 实测，要么走具名常量。
            fail += Check(projectRoot,
                "㊳ OnGui 行助手布局宽度不许写死(重映射行 Delete 折行被拦腰裁断)",
                "Ui\\OnGui.cs",
                s => NoNumericLayoutWidth(s));
            // ㊴ 键位显示合成（渲染选项 → 帧源 → 虚拟时钟换轨）三段接线。
            //
            //    render.cursor（㉝）的同族形态：界面上加一行、Settings 加个字段，
            //    两边编译过但中间没人接线——「有开关、有说明、没实现」。
            //    背景是 0.1.6 用户观察：键显/雨滴在 ScreenSpaceOverlay 画布上，
            //    相机 RT 取像永远拍不到；且雨滴动画吃 JRP 的墙钟时钟，直接合成
            //    进慢放视频全是快放废片。两条断言分别钉住：
            //    ① 界面行回调真的写 RenderKeyViewerOverlay；
            //    ② 帧源真的调 KeyViewerOverlayCapture.Install/Restore（画布切
            //       ScreenSpaceCamera 挂渲染相机，Overlay 不切模式拍不到）；
            //    ③ 捕获类必须把 JRP 的 CurrentTicks 换轨到 UnscaledDeltaPin 虚拟
            //       时钟（RegisterManualPrefix）——少了它，画布进片了雨滴也是
            //       墙钟快放废片。
            fail += Check(projectRoot,
                "㊴a 渲染选项里的键位显示行必须接到 Settings.RenderKeyViewerOverlay",
                "Pages\\Pages.cs",
                s => s.Contains("OnGui.Row(\"render.keyviewer\", RenderKeyViewerOverlay", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "㊴b 帧源必须调 KeyViewerOverlayCapture.Install/Restore（Overlay 不切模式拍不到）",
                "Features\\VideoRender\\Pipeline\\VideoFrameSource.cs",
                s => s.Contains("KeyViewerOverlayCapture.Install(", StringComparison.Ordinal)
                    && s.Contains("KeyViewerOverlayCapture.Restore(", StringComparison.Ordinal));
            fail += Check(projectRoot,
                "㊴c 键显时钟必须换轨到虚拟时钟（墙钟雨滴在慢放视频里是快放废片）",
                "Features\\VideoRender\\Pipeline\\KeyViewerOverlayCapture.cs",
                s => s.Contains("PatchManager.RegisterManualPrefix", StringComparison.Ordinal)
                    && s.Contains("UnscaledDeltaPin.CurrentUnscaledF", StringComparison.Ordinal));

            if (fail == 0)
                Console.WriteLine("    ✓ " + _checksRun + " 条界面形状判据都成立");
            return fail;
        }

        /// <summary>
        /// 页面里不许出现 <c>GUILayout.Width(数字</c>。
        ///
        /// <para>Clean() 已经剥掉注释，所以注释里提到宽度不会假红；这个判据盯的是
        /// **布局代码**：控件宽度要么按文案实测（<see cref="Gui"/> 的 ButtonW /
        /// BadgeRect），要么用 Gui.LabelW / Gui.CtrlW 这类统一的列宽常量。</para>
        /// </summary>
        private static bool NoNumericLayoutWidth(string s)
        {
            const string Key = "GUILayout.Width(";
            int at = 0;
            while ((at = s.IndexOf(Key, at, StringComparison.Ordinal)) >= 0)
            {
                int i = at + Key.Length;
                if (i < s.Length && s[i] >= '0' && s[i] <= '9') return false;
                at = i;
            }
            return true;
        }

        /// <summary>
        /// 按钮文案不许内联 <c>Loc.T(...)</c>／<c>Loc.F(...)</c>：必须先取到局部
        /// 变量，才能把同一份文案喂给测宽工具。内联等于「宽度只能写死」。
        ///
        /// <para>两种按法不同：<c>GUI.Button(rect, caption, style)</c> 的文案是
        /// 第 2 个实参，<c>GUILayout.Button(caption, style)</c> 的是第 1 个。</para>
        ///
        /// <para>切实参必须**按括号深度**：<c>Loc.F("a", b)</c> 自己带逗号，
        /// 按逗号二分会把一条文案切成两半、于是漏判。</para>
        /// </summary>
        private static bool ButtonsHoistCaption(string s)
        {
            return CaptionArg(s, "GUI.Button(", 1) == null
                && CaptionArg(s, "GUILayout.Button(", 0) == null;
        }

        /// <summary>
        /// 扫一遍 <paramref name="head"/> 的每次出现，返回**第一个内联了 Loc. 的文案实参**；
        /// 全都先取变量则返回 <c>null</c>。
        /// </summary>
        private static string CaptionArg(string s, string head, int captionIndex)
        {
            int at = 0;
            while ((at = s.IndexOf(head, at, StringComparison.Ordinal)) >= 0)
            {
                int i = at + head.Length;
                int depth = 0;
                int arg = 0;
                int start = i;
                int end = -1;
                for (; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '(')
                    {
                        depth++;
                    }
                    else if (c == ')')
                    {
                        // depth == 0 表示这个右括号收的是按钮调用本身
                        if (depth == 0) { end = i; break; }
                        depth--;
                    }
                    else if (c == ',' && depth == 0)
                    {
                        if (arg == captionIndex) { end = i; break; }
                        arg++;
                        start = i + 1;
                    }
                }
                if (end < 0) { at += head.Length; continue; }
                string caption = s.Substring(start, end - start);
                if (caption.Contains("Loc.")) return caption.Trim();
                at = end;
            }
            return null;
        }

        /// <summary>测宽工具必须真的量文字（GUIStyle.CalcSize），不能又是一张常量表。</summary>
        private static bool WidthToolsMeasureText(string s)
            => s.Contains("internal static float ButtonW(", StringComparison.Ordinal)
               && s.Contains("internal static Rect BadgeRect(", StringComparison.Ordinal)
               && s.Contains("CalcSize(new GUIContent(label))", StringComparison.Ordinal)
               && s.Contains("CalcSize(new GUIContent(s))", StringComparison.Ordinal);

        /// <summary>
        /// 「删除」是不是**两段式**：第一下只进入待确认态、第二下才删。
        ///
        /// 判法：在方法体里找到 <c>File.Delete</c> 的位置，它之前必须既有
        /// 「记录待确认路径」又有「未处于待确认就 return」的早退。只判
        /// 「有 _pendingDelete」不够——把它赋值放在 File.Delete 之后也能过，
        /// 而那等价于单点即删。
        /// </summary>
        /// <summary>
        /// 官方关卡分支是否记录了**真实**的 BPM。
        ///
        /// 三条一起判，缺一条都能被"看起来改了"骗过：
        /// ① `data.Bpm = 0.0;` 这一行**必须消失**——只判"有没有赋值"
        ///    的话，硬写 0 那行照样命中；
        /// ② 必须改成调用 <c>CurrentBpm()</c>；
        /// ③ 那个助手必须走 <c>ADOBase.conductor</c>，不许碰
        ///    <c>scnGame.instance</c>（它在编辑器/重开时点可能为 null，
        ///    而这段代码在 try 里，一抛就是整次录制失败）。
        /// </summary>
        private static bool OfficialLevelBpmIsReal(string code)
        {
            if (!code.Contains("data.Bpm = CurrentBpm();", StringComparison.Ordinal))
                return false;
            string body = MethodBody(code, "CurrentBpm");
            if (body == null) return false;
            return body.Contains("ADOBase.conductor", StringComparison.Ordinal)
                && !body.Contains("scnGame.instance", StringComparison.Ordinal);
        }

        /// <summary>
        /// 渲染的倍速来源必须是录像的 <c>PlaybackSpeed</c>，且受 <c>QuickPitched</c> 门控。
        ///
        /// <c>QuickPitched</c> 这道门是**正确性**要求而不是整洁：校验式的期望值是
        /// `QuickPitched ? PlaybackSpeed : 1.0`。非飙速录像若也写 1.5，校验仍按
        /// 1.0 通过，但 <c>song.pitch</c> 已被拉高——成片会整体音画不同步，
        /// 而这种坏只在成片里看得出来。
        /// </summary>
        private static bool SpeedOverrideComesFromReplay(string code)
        {
            string body = MethodBody(code, "Phases");
            if (body == null) return false;
            return body.Contains("SpeedOverride", StringComparison.Ordinal)
                && body.Contains("PlaybackSpeed", StringComparison.Ordinal)
                && body.Contains("QuickPitched", StringComparison.Ordinal);
        }

        /// <summary>
        /// 等待阶段的早退条件是否**同时**包含两条判据。
        ///
        /// 只看到拒绝记录就退出会误杀：<c>Start_Rewind</c> 会连发多次，
        /// 前一发被拒、后一发成功是常态（这正是超时现场里
        /// `is_playing` 会闪的来源）。只看到关卡在跑就退出也同样错——
        /// 非渲染的普通游玩本来就会让关卡跑起来。
        /// </summary>
        private static bool AwaitReplayAliveRequiresBoth(string code)
        {
            string body = MethodBody(code, "AwaitReplayAlive");
            if (body == null) return false;
            int at = body.IndexOf("levelRunning", StringComparison.Ordinal);
            if (at < 0) return false;
            // 早退那一段必须把两个条件写在同一个判断里
            int guard = body.IndexOf("if (levelRunning", at, StringComparison.Ordinal);
            if (guard < 0) return false;
            int close = body.IndexOf(')', guard);
            if (close < 0) return false;
            string cond = body.Substring(guard, close - guard);
            return cond.Contains("LastReject", StringComparison.Ordinal);
        }

        /// <summary>
        /// 等待放行里必须有一条「倒计时就放行」的路，且不许顺手砍掉
        /// PlayerControl + 连续两帧正位置那道闸。
        ///
        /// 判法用**具体 token**，不判「有没有 States.Countdown 这个词」——
        /// 方法体里注释也会出现它，而注释在判据跑之前已被 SrcUtil.Clean 剥掉，
        /// 所以这里比对的是活代码：`controller.state == States.Countdown`
        /// 只出现在计数阶段的定义里，把它换成别的状态（MUT-D23）就红。
        ///
        /// ③ 倒计时长度必须按谱面派生：方法体里要出现 conductor.countdownTicks /
        ///    countdownSpeedMultiplier / crotchetAtStart 三个读数。把拍数写死成
        ///    常数（MUT-D24）就红——那正是「倒计时不一定只有两秒」的反面。
        ///
        /// ④ 放行条件必须只有 `if (conductor != null && counting)`，且方法体里
        ///    不许再出现 `CountdownWindowFrom` / `CountdownWindowTo` 这类绝对
        ///    秒数窗口：加回 `-2.0` 下界（MUT-D25）就红。
        /// </summary>
        private static bool CountdownReleaseKeepsPlayGate(string code)
        {
            string body = MethodBody(code, "AwaitReplayAlive");
            if (body == null) return false;
            bool hasCountdown = body.Contains("controller.state == States.Countdown", StringComparison.Ordinal);
            // 窗口长度必须按谱面派生（读 countdownTicks / crotchetAtStart /
            // countdownSpeedMultiplier），不许写死秒数——2026-10-20 用户指出
            // 「倒计时不一定只有两秒」，写死会在慢歌长倒计时上切掉开头。
            bool hasDerived = body.Contains("conductor.countdownTicks", StringComparison.Ordinal)
                && body.Contains("conductor.countdownSpeedMultiplier", StringComparison.Ordinal)
                && body.Contains("conductor.crotchetAtStart", StringComparison.Ordinal);
            bool hasRelease = body.Contains("render starts during countdown", StringComparison.Ordinal);
            // 放行条件必须**只有**「在倒计时态」这一条；再加回任何写死的秒数
            // 下界/上界（MUT-D25 的手法）都会让这里的整串比对失败。
            bool releaseUnconditional = body.Contains("if (conductor != null && counting)",
                StringComparison.Ordinal);
            bool noFixedWindow = !body.Contains("CountdownWindowFrom", StringComparison.Ordinal)
                && !body.Contains("CountdownWindowTo", StringComparison.Ordinal);
            bool hasPlayGate = body.Contains("controller.state == States.PlayerControl", StringComparison.Ordinal)
                && body.Contains("sawPositivePosition", StringComparison.Ordinal);
            return hasCountdown && hasDerived && hasRelease && hasPlayGate
                && releaseUnconditional && noFixedWindow;
        }

        /// <summary>
        /// 检查点开局（= 从中间砖块渲染）能不能在 <c>States.Checkpoint</c> 放行。
        ///
        /// 判法全在 <c>AwaitReplayAlive</c> 的**活代码**里取具体 token：注释在判据
        /// 跑之前已被 SrcUtil.Clean 剥掉，所以这里比对的是真代码而不是说明文字。
        ///
        /// 四段缺一不可，每段对应一种「看起来改了」的假修：
        /// ① <c>controller.state == States.Checkpoint</c> 真的被读进一个布尔量；
        /// ② <c>levelRunning</c> 的定义句里带上 checkpointing——否则等待期会被
        ///    「关卡在跑 + 有 LastReject」早退判死，还没放行就先报失败；
        /// ③ <c>hasSongStarted</c> 那条放行路的**条件里**同时有 checkpointing——
        ///    只做 ①② 不做这里，状态认得出来却仍然不放行（本 bug 的半修形态）；
        /// ④ 同一条件里保留 freshStartPosition，且它的定义读的是 songNow 的区间
        ///    而不是常数：<c>hasSongStarted</c> 一旦为真就永远为真，去掉新鲜度
        ///    判断后，上一轮停在结尾的位置会让下一轮等待立刻误放行。同时不许
        ///    把旧的「必须 songNow &gt; 0」加回条件——检查点前导位置是负的，
        ///    加回它等于把这条路关掉。
        /// </summary>
        private static bool CheckpointStartCanRelease(string code)
        {
            string body = MethodBody(code, "AwaitReplayAlive");
            if (body == null) return false;

            // ① 检查点状态真的从 controller.state 取
            if (!body.Contains("controller.state == States.Checkpoint", StringComparison.Ordinal))
                return false;

            // ② levelRunning 把检查点算作「关卡在跑」
            int running = body.IndexOf("bool levelRunning =", StringComparison.Ordinal);
            if (running < 0) return false;
            int runningEnd = body.IndexOf(';', running);
            if (runningEnd < 0) return false;
            if (!body.Substring(running, runningEnd - running)
                     .Contains("checkpointing", StringComparison.Ordinal))
                return false;

            // ③ hasSongStarted 那条放行路。锚点带 if 前缀：levelRunning 里也有一处
            //    conductor.hasSongStarted，但它只是「关卡在跑」的判断，不是放行点。
            const string release = "if (conductor != null && conductor.hasSongStarted";
            int releaseAt = body.IndexOf(release, StringComparison.Ordinal);
            if (releaseAt < 0) return false;
            // 按括号配平取**完整条件文本**，而不是「到下一个 yield break 为止」：
            // 后面那段 Debug.Log 的三元式里也写着 checkpointing，混进来会把
            // 「条件里删掉 checkpointing、日志里留着」这种假修判绿。
            int open = body.IndexOf('(', releaseAt);
            if (open < 0) return false;
            int depth = 0, close = -1;
            for (int i = open; i < body.Length; i++)
            {
                if (body[i] == '(') depth++;
                else if (body[i] == ')')
                {
                    depth--;
                    if (depth == 0) { close = i; break; }
                }
            }
            if (close < 0) return false;
            string cond = body.Substring(releaseAt, close - releaseAt + 1);
            if (!cond.Contains("checkpointing", StringComparison.Ordinal)) return false;

            // ④ 新鲜度闸还在，且读的是 songNow 的区间
            if (!cond.Contains("freshStartPosition", StringComparison.Ordinal)) return false;
            int fresh = body.IndexOf("freshStartPosition =", StringComparison.Ordinal);
            if (fresh < 0) return false;
            int freshEnd = body.IndexOf(';', fresh);
            if (freshEnd < 0) return false;
            string freshDef = body.Substring(fresh, freshEnd - fresh);
            if (!freshDef.Contains("songNow", StringComparison.Ordinal)
                || !freshDef.Contains(">=", StringComparison.Ordinal)
                || !freshDef.Contains("<", StringComparison.Ordinal))
                return false;

            // 不许把「必须 songNow > 0」加回放行条件
            return !cond.Contains("positiveNow", StringComparison.Ordinal);
        }

        /// <summary>
        /// 指针是不是**在像素交付路径上**画进去的，且位置采在正确的时刻。
        ///
        /// 三条缺一不可：
        /// ① <c>Grab</c> 里读 <c>Input.mousePosition</c>——回读是异步的，位置必须在
        ///    画面被截下的那一帧采样；交付时才读会把两三帧之后的位置画进这一帧，
        ///    快速移动时指针明显甩在画面后面。
        /// ② <c>Grab</c> 里用 <c>Input.mousePresent</c> 把「没有鼠标」挡掉，否则
        ///    无鼠标设备时会往 (0,0) 画一个指针。
        /// ③ <c>TryTake</c>（像素真正交付的地方）调 <c>CursorOverlay.Draw</c>。
        ///    只在 Grab 里读位置、却不在这里画，等于开关没有任何效果。
        /// </summary>
        private static bool CursorInkedIntoFrame(string code)
        {
            string grab = MethodBody(code, "Grab");
            string take = MethodBody(code, "TryTake");
            if (grab == null || take == null) return false;
            return grab.Contains("Input.mousePosition", StringComparison.Ordinal)
                && grab.Contains("Input.mousePresent", StringComparison.Ordinal)
                && grab.Contains("DrawCursor", StringComparison.Ordinal)
                && take.Contains("CursorOverlay.Draw", StringComparison.Ordinal);
        }

        /// <summary>
        /// 渲染起点是不是**玩家当前的选择**，而不是录像里记的起点。
        ///
        /// 四段缺一不可，任何一段塌掉都会退回「从第 0 块开始」这个现场：
        /// ① 抓取时机：<c>requestedStartFloor = ResolveRequestedStartFloor()</c> 必须
        ///    在 <c>SwitchToPlayMode</c> **之前**——重启会换掉关卡与选区，晚抓等于白抓；
        /// ② 来源：解析式走 <c>SelectionIsSingle()</c> + <c>selectedFloors[0].seqID</c>，
        ///    且体里不许出现 <c>checkpointNum</c>（那是 Play() 写的残留值，首次打开
        ///    关卡时恒 0，正是本 bug 的形态）；
        /// ③ 不留回退：Phases 里出现 <c>StartTile</c> 即判红；
        /// ④ 送达：<c>Clamp(requestedStartFloor</c> / <c>SelectFloor(</c> /
        ///    <c>GCS.checkpointNum = floor</c> / <c>ADOBase.editor.Play()</c> 齐备，
        ///    且选中必须**先于** Play()——Play() 是用当前选区推导起点的。
        /// </summary>
        private static bool RenderStartFloorComesFromSelection(string code)
        {
            string phases = MethodBody(code, "Phases");
            string resolve = MethodBody(code, "ResolveRequestedStartFloor");
            if (phases == null || resolve == null) return false;

            // ① 抓取点在重启之前
            int capture = phases.IndexOf("requestedStartFloor = ResolveRequestedStartFloor()", StringComparison.Ordinal);
            int restart = phases.IndexOf("SwitchToPlayMode", StringComparison.Ordinal);
            if (capture < 0 || restart < 0 || capture > restart) return false;

            // ③ 不许回退录像起点
            if (phases.Contains("StartTile", StringComparison.Ordinal)) return false;

            // ② 来源必须是当前选区，且不读残留的 checkpointNum
            if (!resolve.Contains("SelectionIsSingle()", StringComparison.Ordinal)) return false;
            if (!resolve.Contains("selectedFloors[0].seqID", StringComparison.Ordinal)) return false;
            if (resolve.Contains("checkpointNum", StringComparison.Ordinal)) return false;

            // ④ 真的送进 Play()，且选中先于 Play()
            int clamp = phases.IndexOf("Mathf.Clamp(requestedStartFloor", StringComparison.Ordinal);
            int select = phases.IndexOf("SelectFloor(", StringComparison.Ordinal);
            int setCp = phases.IndexOf("GCS.checkpointNum = floor", StringComparison.Ordinal);
            int play = phases.IndexOf("ADOBase.editor.Play()", StringComparison.Ordinal);
            return clamp >= 0 && select >= 0 && setCp >= 0 && play >= 0 && select < play;
        }

        private static bool DeleteIsTwoStep(string body)
        {
            if (body == null) return false;
            int del = body.IndexOf("File.Delete(path)", StringComparison.Ordinal);
            if (del < 0) return false;
            int mark = body.IndexOf("_pendingDelete = path;", StringComparison.Ordinal);
            if (mark < 0 || mark > del) return false;   // 记录待确认必须发生在删除之前
            // 早退：`if (!pending)` 里 return，且该 return 在 File.Delete 之前
            int guard = body.IndexOf("if (!pending)", StringComparison.Ordinal);
            if (guard < 0 || guard > del) return false;
            int ret = body.IndexOf("return;", guard, StringComparison.Ordinal);
            return ret > guard && ret < del;
        }

        /// <summary>
        /// 某个 <c>OnGui.Row(&quot;key&quot;, ...)</c> 的 **onChange 回调体**里是否调用了
        /// <paramref name="call"/>。
        ///
        /// **必须限定在这条调用的范围内**，不能整段 Contains：那一行收尾还有
        /// <c>, EffectRemoverOn</c> 这种门控实参，整段搜会把「挂了开关」当成
        /// 「刷新了按钮」。做法：从键名那行扫到收尾 <c>);</c>，在拼起来的整段里找。
        ///
        /// 键名要带引号一起匹配：<c>("effects.on"</c> 不加尾引号的话会命中
        /// <c>OnGui.Note("effects.on.note")</c>——前缀重叠，锚点就飘了。
        /// </summary>
        private static bool RowOnChangeCalls(string code, string key, string call)
        {
            string body = CallBody(code, "(\"" + key + "\"");
            return body != null && body.Contains(call, StringComparison.Ordinal);
        }

        /// <summary>
        /// 取出以 <paramref name="open"/> 开头的那一条调用的**完整实参文本**。
        ///
        /// **不能靠「某一行以 <c>);</c> 结尾」找收尾**：本文件第一版就是那么写的，
        /// 而 onChange 体里第一句 <c>Commit();</c> 恰好以 <c>);</c> 结尾，循环当场
        /// 停在它上面，后面的 <c>RefreshEditorSaveButtons();</c> 根本没被扫到
        /// ⇒ 判据恒红，而红的样子看起来像「代码真没接刷新」（实测）。
        /// 真正的收尾只能按**括号深度**判。
        ///
        /// 返回 null 表示找不到锚点。判据的默认姿态是不信：找不到更可能是
        /// 锚点失效（有人改了调用形状），而不是「全都合规」。
        /// </summary>
        private static string CallBody(string code, string open)
            => CallBody(code, open, 0);

        /// <summary>
        /// 同上，但从 <paramref name="from"/> 起找锚点。遍历**多个**同名调用点
        /// （比如三颗「全选本组」）时必须用这个重载：不带偏移的版本每次都从文件头
        /// 找起，循环里三次拿到的是同一颗按钮的体——第三颗单向 `= true` 也照样绿。
        /// 这不是假想：<see cref="ToggleAllBidirectional"/> 第一版就是这么写的（实测）。
        /// </summary>
        private static string CallBody(string code, string open, int from)
        {
            int at = from <= 0
                ? code.IndexOf(open, StringComparison.Ordinal)
                : code.IndexOf(open, from, StringComparison.Ordinal);
            if (at < 0) return null;
            // open 的**首字符就是那个 '('**。这里第一版写成从 at + open.Length - 1
            // 往后找 '('，结果落到了收尾引号之后、命中括号体里的 `Commit(`，
            // 深度从那里起算 ⇒ 判据恒红而看起来像代码没接刷新（实测）。
            int start = at;
            int depth = 0, i = start;
            for (; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) { i++; break; }
                }
            }
            return code.Substring(at, i - at);
        }

        /// <summary>
        /// 去特效总开关的 onChange 是否**两半都**刷新了保存按钮。
        ///
        /// 只判「出现过 RestoreEditorSaveButtons」是假判据——原先就只接了那一半，
        /// 而它恰好就是这个缺陷的形态。必须同时看到「关掉→恢复」与「打开→按设置重算」。
        /// </summary>
        private static bool EffectMasterToggleRefreshesButtons(string code)
        {
            string body = CallBody(code, "(\"effects.on\"");
            if (body == null) return false;
            return body.Contains("RestoreEditorSaveButtons", StringComparison.Ordinal)
                && body.Contains("RefreshEditorSaveButtons", StringComparison.Ordinal);
        }

        private static int CountOccurrences(string s, string needle)
        {
            int n = 0, i = 0;
            while (true)
            {
                int at = s.IndexOf(needle, i, StringComparison.Ordinal);
                if (at < 0) return n;
                n++;
                i = at + needle.Length;
            }
        }

        /// <summary>
        /// 数「**完整闭合**的实参」出现次数：<paramref name="needle"/> 之后必须紧跟
        /// <c>)</c> 或 <c>,</c>（允许中间有空白）。
        ///
        /// **为什么不能直接用 <c>CountOccurrences</c>**：那是子串计数，把
        /// <c>Feature = FeatureKeys.SpecialKeys</c> 改成
        /// <c>...SpecialKeys_DISABLED</c> 之后，前者仍是后者的**前缀**，
        /// 计数照样是 2 ⇒ 变异改了代码而判据仍绿（MUT-U42 实测踩到，
        /// 与本项目早前「playback.openlevel 前缀假判据」同一形态）。
        /// </summary>
        /// <summary>
        /// ㉑a 的判据：**按两个补丁类切段**，每段都必须出现 Feature 绑定。
        ///
        /// **不能用「全文出现 N 次」。** 异步侧因 r150 把 <c>GetSpecialInput</c> 从
        /// <c>List&lt;AsyncKeyCode&gt; GetSpecialInput()</c> 改成
        /// <c>void GetSpecialInput(List&lt;AsyncKeyCode&gt; keys)</c>，拆成
        /// <c>PostfixOld</c>/<c>PostfixNew</c> 两条版本分叉补丁 ⇒ 同一个接入点上
        /// 有 2 次绑定，全文共 3 次。写死 2 会把正确的分叉判红（实测就这条挂）；
        /// 放宽成「&gt;= 2」又会让「键盘侧挂了、异步侧整段没挂」照样绿——而那正是
        /// 这条判据要抓的静默半残。分段计数对两种情况都对：一段里分叉成几条都行，
        /// 但**每段至少要有一条**。
        /// </summary>
        private static bool BothInputPathsHaveFeature(string s)
        {
            const string needle = "Feature = FeatureKeys.SpecialKeys";
            int kb = s.IndexOf("class Patch_KeyboardSpecialKeys", StringComparison.Ordinal);
            int ac = s.IndexOf("class Patch_AsyncSpecialKeys", StringComparison.Ordinal);
            if (kb < 0 || ac < 0 || ac < kb)
                return false;   // 两个补丁类缺一 / 顺序变了 ⇒ 判红，不静默通过
            return CountClosedArgs(s.Substring(kb, ac - kb), needle) >= 1
                && CountClosedArgs(s.Substring(ac), needle) >= 1;
        }

        private static int CountClosedArgs(string s, string needle)
        {
            int n = 0, i = 0;
            while (true)
            {
                int at = s.IndexOf(needle, i, StringComparison.Ordinal);
                if (at < 0) return n;
                int k = at + needle.Length;
                while (k < s.Length && (s[k] == ' ' || s[k] == '\t' || s[k] == '\r' || s[k] == '\n')) k++;
                if (k >= s.Length || s[k] == ')' || s[k] == ',') n++;
                i = at + needle.Length;
            }
        }

        // ── ⑱ 系列：总开关关掉时子设置必须压暗 ─────────────────────
        //
        // 这几条判的**都是同一个形状**：某个分组/某几行的末参必须是那个总开关
        // 字段本身。共同的三条注意：
        //   ① 必须剥注释再判——解释"为什么挂这个开关"的注释里当然写着字段名，
        //      不剥的话把实参删掉、注释留着，判据照样绿（本项目栽过的老形态）；
        //   ② 必须判**实参位置**，不能只判"这一行包含 X"：字段名会出现在
        //      onValue lambda 里（`v => X = v`），那不是门控。d
        //   ③ 不能数"出现了几次"：漏掉一个分组的同一句话在其��分组里还有五份。

        /// <summary>去特效六个子分组是否都挂 <c>EffectRemoverOn</c>。</summary>
        private static bool EffectsSectionsDimWithMaster(string code)
        {
            string[] titles =
            {
                "effects.visual", "effects.decor", "effects.track",
                "effects.planet", "effects.audio", "effects.gameplay",
            };
            foreach (string title in titles)
            {
                if (!SectionEndsWith(code, title, "EffectRemoverOn")) return false;
            }
            return true;
        }

        /// <summary>录制页的保存策略行是否都挂了 <c>ReplayOn</c>。</summary>
        private static bool RecordRowsDimWithReplay(string code)
        {
            string[] keys =
            {
                "record.autosave", "record.completesave", "record.dontsavewhenauto",
                "record.dontsavewhenmiss", "record.latesave", "record.failsave",
                "record.backup", "record.manualsave",
            };
            foreach (string k in keys)
            {
                if (!RowCallEndsWith(code, k, "ReplayOn")) return false;
            }
            return true;
        }

        /// <summary>键盘音的音量/偏移是否挂了 <c>KeybdSoundRecordActive</c>。</summary>
        private static bool KeybdRowsDimWithRecord(string code)
        {
            foreach (string k in new[] { "record.keybd.volume", "record.keybd.offset" })
            {
                if (!RowCallEndsWith(code, k, "KeybdSoundRecordActive")) return false;
            }
            return true;
        }

        /// <summary>
        /// 「全选本组」按钮的**分区口径**与门控。
        ///
        /// ① 该有的三组（visual / track / planet）各有一个，不该有的四组
        ///    （master / decor / audio / gameplay）一个都没有；
        /// ② 每个都挂总开关 <c>EffectRemoverOn</c>，且调用点数量正好是 3。
        ///
        /// 为什么不能用 <c>RowCallEndsWith</c>：它只找**第一处**出现——轨道组挂对了、
        /// 行星组漏了照样绿。这里逐个调用点判末参，并要求数量。
        ///
        /// 为什么判「哪个分区里有」而不是只判数量：数量对上但长错了分区
        /// （比如给 decor 加一个、给 planet 删一个）是同一句话的另一种错法，
        /// 只判数量的话它照样绿。分区判断用 <see cref="CallBody"/> 取整段
        /// `Gui.Section(...)` 实参（含 lambda 体），看里面有没有那颗按钮。
        /// </summary>
        private static bool ToggleAllPerSection(string code)
        {
            foreach (string title in new[] { "effects.visual", "effects.track", "effects.planet" })
            {
                string body = CallBody(code, "(\"" + title + "\"");
                if (body == null || !body.Contains("effects.toggleall", StringComparison.Ordinal))
                    return false;
            }
            foreach (string title in new[]
            {
                "effects.master", "effects.decor", "effects.audio", "effects.gameplay",
            })
            {
                string body = CallBody(code, "(\"" + title + "\"");
                if (body == null) return false;
                if (body.Contains("effects.toggleall", StringComparison.Ordinal)) return false;
            }
            return AllCallsEndWith(code, "effects.toggleall", "EffectRemoverOn", 3);
        }

        /// <summary>
        /// 每颗「全选本组」都必须是**双向**的：本组已有一项开着时按下去是全部关掉。
        ///
        /// 单向 `= true` 的版本能通过前一条判据（门控、分区都对），但玩家批量
        /// 打开后没有任何办法批量撤销——只能逐项去点，而按钮名叫「全选」。
        /// 判法：每个调用点体内必须有 `bool on = !(` 这个形状，且有 `= on;` 赋值。
        /// 只判「出现过 !」不够（`!EffectRemoverOn` 在外层 Dim 里到处都是）。
        /// </summary>
        private static bool ToggleAllBidirectional(string code)
        {
            string open = "(\"effects.toggleall\"";
            int seen = 0;
            int from = 0;
            while (true)
            {
                int at = code.IndexOf(open, from, StringComparison.Ordinal);
                if (at < 0) break;
                string body = CallBody(code, open, at);
                if (body == null) return false;
                if (!body.Contains("bool on = !(", StringComparison.Ordinal)) return false;
                if (!body.Contains("= on;", StringComparison.Ordinal)) return false;
                seen++;
                from = at + open.Length;
            }
            return seen == 3;
        }

        /// <summary>
        /// 「移除全部装饰」是否挂在两个父项上：<c>EffectRemoverOn &amp;&amp; EffectRemoverDecorations</c>。
        ///
        /// 它是「移除装饰物」的清除口径，不是独立动作：全仓只有
        /// <c>EffectRemover.RemoveDecorations</c> 读它，而那个方法只在
        /// <c>if (Settings.EffectRemoverDecorations)</c> 里被调。父项关着时它
        /// 拨到哪都不改变任何字节，不压暗就是「拨得动、没反应」。
        /// </summary>
        private static bool RemoveAllDecorGatedByParent(string code)
            => AllCallsEndWith(code, "effects.removealldecor",
                   "EffectRemoverOn && EffectRemoverDecorations", 1);

        /// <summary>
        /// 「移除镜头移动」的配套控件是否按父子关系门控：
        ///   ① 缩放开关受「总开关 ∧ 移除镜头移动」管；
        ///   ② 缩放滑条再受「缩放开关」管——<c>EffectRemover.RemoveCameras</c>
        ///      一进来就判这个字段，关着直接 return，滑条拖到哪都不会写进谱面。
        ///
        /// 只判"源码里出现过 EffectRemoverSetCameraZoom"不够：字段名也在开关那行
        /// 的 onValue lambda 里（<c>v =&gt; EffectRemoverSetCameraZoom = v</c>），把滑条
        /// 的门控删掉照样绿。所以判**滑条这一行真正的末参表达式**。
        /// </summary>
        private static bool CameraZoomGatedByParent(string code)
            => AllCallsEndWith(code, "effects.setcamerazoom",
                   "EffectRemoverOn && EffectRemoverCameras", 1)
               && AllCallsEndWith(code, "effects.camerazoom",
                   "EffectRemoverOn && EffectRemoverCameras && EffectRemoverSetCameraZoom", 1);

        /// <summary>
        /// 每一个 <c>("key"</c> 调用点的末参是否都等于 <paramref name="arg"/>，
        /// 且调用点数量正好是 <paramref name="expected"/>。
        ///
        /// 数量也要判：只判"每个都挂对"的话，按钮被整个删掉会得到零个调用点，
        /// 循环一次都不进、直接返回 true——判据绿着而控件没了。
        /// </summary>
        private static bool AllCallsEndWith(string code, string key, string arg, int expected)
        {
            string open = "(\"" + key + "\"";
            int seen = 0;
            int at = code.IndexOf(open, StringComparison.Ordinal);
            while (at >= 0)
            {
                seen++;
                if (!string.Equals(LastArgOfCall(code, at), arg, StringComparison.Ordinal))
                    return false;
                at = code.IndexOf(open, at + open.Length, StringComparison.Ordinal);
            }
            return seen == expected;
        }

        /// <summary>
        /// 从某个左括号起，取这个调用的**最后一个实参**原文（两端去空白）。
        ///
        /// 按深度走：`(`/`[`/`{` 加一，`)`/`]`/`}` 减一，回到 0 即收尾。
        /// 只记深度 1 处的逗号——嵌套 lambda 里的逗号在深度 2，记进来会把真正的
        /// 末参分隔符覆盖掉（`}, false, EffectRemoverOn` 就会被读成 `false`）。
        /// 字符串内的括号要跳过，否则 `"("` 这类字面量会把深度带偏。
        /// </summary>
        private static string LastArgOfCall(string code, int openParen)
        {
            int depth = 0;
            int lastComma = openParen;
            bool inStr = false, esc = false;
            for (int i = openParen; i < code.Length; i++)
            {
                char c = code[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '(' || c == '[' || c == '{') { depth++; continue; }
                if (c == ')' || c == ']' || c == '}')
                {
                    depth--;
                    if (depth == 0) return code.Substring(lastComma + 1, i - lastComma - 1).Trim();
                    continue;
                }
                if (c == ',' && depth == 1) lastComma = i;
            }
            return "";
        }

        /// <summary>
        /// 找到 <c>Gui.Section(&quot;title&quot;</c> 那一行，往后找**本组**的收尾
        /// <c>}, X);</c>，判断 X 是否等于 <paramref name="arg"/>。
        ///
        /// **只在这一组的范围内找**：六个分组的收尾长得一模一样，从全文件找
        /// `}, EffectRemoverOn);` 的话，六个里挂了一个就全绿。
        ///
        /// **收尾必须按花括号深度认，不能认「第一个以 `},` 开头的行」。**
        /// 分组体里可以有嵌套的 lambda（比如「全选本组」按钮
        /// `OnGui.Button("effects.toggleall", () => {...}, false, EffectRemoverOn);`），
        /// 它的收尾行 `}, false, EffectRemoverOn));` 同样以 `},` 开头，却比整组的
        /// 收尾早出现----按行首前缀找就会把按钮的实参当成分组的实参，判据恒红，
        /// 而红的样子像是「分组没挂总开关」（2026-10-18 实测就是这么误报的）。
        /// 正确地找：从 `Gui.Section(` 的左括号起数深度，回到 0 的那一对就是本组；
        /// 紧跟其后的 `, X)` 才是总开关实参。
        /// </summary>
        private static bool SectionEndsWith(string code, string title, string arg)
        {
            string open = "Gui.Section(\"" + title + "\"";
            int at = code.IndexOf(open, StringComparison.Ordinal);
            if (at < 0) return false;
            // 定位到 `Gui.Section(` 的那个左括号本身。
            int paren = code.IndexOf('(', at);
            if (paren < 0) return false;

            // 从括号起数：`(`/`[`/`{` 加一，`)`/`]`/`}` 减一。
            // 必须跳过字符串内的括号，否则 `"("` 这类字面量会把深度带偏。
            //
            // `Gui.Section("t", Gui.Accent, () => { ... }, X)` 的形状里，**只记
            // 深度 1 处的逗号**：嵌套 lambda 里的逗号在深度 2，记进来就会把
            // `EffectRemoverOn` 前面那个逗号覆盖掉。深度回到 0 时，最后一个
            // 深度 1 逗号与收尾右括号之间就是最后一个实参 X。
            int depth = 0, lastComma = -1;
            bool inStr = false, esc = false;
            for (int i = paren; i < code.Length; i++)
            {
                char c = code[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '(' || c == '[' || c == '{') { depth++; continue; }
                if (c == ')' || c == ']' || c == '}') { depth--; if (depth != 0) continue; }
                else if (c == ',' && depth == 1) { lastComma = i; continue; }
                else continue;

                // 深度回到 0：本调用的收尾右括号。
                if (lastComma < 0) return false;
                string last = code.Substring(lastComma + 1, i - lastComma - 1).Trim();
                return string.Equals(last, arg, StringComparison.Ordinal);
            }
            return false;
        }

        /// <summary>
        /// 找到 <c>OnGui.Row(&quot;key&quot;</c> 起的一段，看它的最后一个实参是不是
        /// <paramref name="arg"/>。这一段可能跨多行（RowSlider 的 fmt / onChange
        /// 各占一行），所以判到**语句结束**为止：以 `);` 结尾的那行就是收尾。
        /// </summary>
        private static bool RowCallEndsWith(string code, string key, string arg)
        {
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            // **锚点只认键名，不认助手名。** 原先写成 `OnGui.Row("key"`，
            // 而键盘音那两行实际是 `OnGui.RowSlider("key"` —— `Row(` 不是
            // `RowSlider(` 的前缀，于是永远找不到起点、判据恒红，而红的样子
            // 像是"代码没挂开关"（实测就是这么误判的）。
            // 改成只认 `("key"`：键名本身在项目里唯一，助手是哪个不影响。
            string open = "(\"" + key + "\"";
            int start = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(open, StringComparison.Ordinal)) { start = i; break; }
            }
            if (start < 0) return false;
            var sb = new System.Text.StringBuilder();
            for (int i = start; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                sb.Append(t).Append(' ');
                if (t.EndsWith(");", StringComparison.Ordinal)) break;
            }
            string call = sb.ToString();
            int close = call.LastIndexOf(");", StringComparison.Ordinal);
            if (close < 0) return false;
            string head = call.Substring(0, close).TrimEnd();
            return head.EndsWith(", " + arg, StringComparison.Ordinal);
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
            // 2026-10-27 起按钮文案改成「先取变量、再按实测宽度」：
            // string label = Loc.T("k"); ... GUILayout.Button(label, ...)。
            // 只认内联形态会让这两条判据在改造后与代码无关地常红——
            // 假红和假绿一样是缺陷，都得修判据。
            const string Assign = @"string\s+(\w+)\s*=\s*Loc\.[TF]\(""([^""]+)""";
            int n = 0;
            var hits = System.Text.RegularExpressions.Regex.Matches(code, Pattern);
            foreach (System.Text.RegularExpressions.Match m in hits)
            {
                if (m.Groups[1].Value == key)
                    n++;
            }

            // ② 先取变量再交给按钮的形态。变量名回代进正则，避免把
            //    "某个叫 label 的变量" 误当成"这个键的按钮"。
            foreach (System.Text.RegularExpressions.Match m
                in System.Text.RegularExpressions.Regex.Matches(code, Assign))
            {
                if (m.Groups[2].Value != key) continue;
                string v = System.Text.RegularExpressions.Regex.Escape(m.Groups[1].Value);
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        code, @"GUILayout\.Button\(\s*" + v + @"\b"))
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
            var hits = System.Text.RegularExpressions.Regex.Matches(
                pagesCode,
                @"OnGui\.(?:Row|RowSlider|RowDropdown|RowHotkey|RowInput|Button)\(\s*""([a-z][a-z0-9_.]+)""");
            foreach (System.Text.RegularExpressions.Match m in hits)
            {
                keys.Add(m.Groups[1].Value);
            }
            if (keys.Count == 0) return false;

            // 文案表在 LocData.cs（Loc.cs 只剩选择逻辑）。
            //
            // **2026-10-19 这里差点变成假绿**：文案从 Loc.cs 搬走之后，本函数仍读
            // 旧文件，`Index.Of("Dictionary<string, string> Zh") < 0` 直接 return false
            // ——那样会"碰巧"还是红的，看起来判据没事。但反过来的情形才是真危险：
            // 若判据写成"找不到表就跳过"，搬家当天它就会静默全绿，而 13 种语言的
            // 说明一条都没验。所以这里找不到文件必须**判红**，不能放行。
            string locPath = Path.Combine(projectRoot, "Ui", "LocData.cs");
            if (!File.Exists(locPath)) return false;
            string loc = SrcUtil.Clean(File.ReadAllText(locPath));

            // 逐语言切块：`["<lang>"] = new System.Collections.Generic.Dictionary...`
            // 只在**整文件**搜 "<key>.tip" 会让"只写了一种语言"蒙混过关，
            // 而玩家在另外 13 种语言下看不到说明。
            var langBlocks = new System.Collections.Generic.Dictionary<string, string>(
                StringComparer.Ordinal);
            var marks = new System.Collections.Generic.List<(string Lang, int Start)>();
            var langHits = System.Text.RegularExpressions.Regex.Matches(
                loc, @"\[""([a-z\-]+)""\]\s*=\s*new\s+System\.Collections\.Generic\.Dictionary");
            foreach (System.Text.RegularExpressions.Match m in langHits)
            {
                marks.Add((m.Groups[1].Value, m.Index));
            }
            if (marks.Count == 0) return false;
            for (int i = 0; i < marks.Count; i++)
            {
                int end = i + 1 < marks.Count ? marks[i + 1].Start : loc.Length;
                langBlocks[marks[i].Lang] = loc.Substring(marks[i].Start, end - marks[i].Start);
            }

            // 14 种语言**每一种**都要有这条说明。
            // 为什么要全查而不是抽查中英：漏翻最常发生在小语种上，
            // 而那正是"作者永远看不见"的那一侧。
            string[] langs = LocCheck.Languages;
            foreach (string k in keys)
            {
                string tip = k + ".tip";
                // 比较的是**带引号的键名**（"<key>.tip"），不是裸键名：
                // 裸串会让 "render.fps.tip" 命中 "render.fps.tip.extra" 这类前缀重叠，
                // 而本项目已经栽过一次前缀假判据（playback.openlevel）。
                string want = "\"" + tip + "\"";
                foreach (string lang in langs)
                {
                    if (!langBlocks.TryGetValue(lang, out string table)) return false;
                    if (!table.Contains(want, StringComparison.Ordinal)) return false;
                }
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
            string[] rows =
            {
                "Row", "RowSlider", "RowDropdown", "RowHotkey", "RowInput", "Button",
            };
            foreach (string m in rows)
            {
                string body = MethodBody(code, m);
                if (body == null || !body.Contains("RowGap();", StringComparison.Ordinal)) return false;
            }
            return true;
        }

        // ── ⑲ 系列：更新检查不许「看着在、其实在撒谎」 ─────────────
        //
        // 这一族的核心风险不是没做，而是**说错话**：
        //   · NewestVersion 为 null 同时代表「已最新」与「UMM 没查」；
        //     把后者说成前者，玩家永远等不到更新且毫无迹象；
        //   · 版本比较方向写反，则「有新版」永不显示；
        //   · 拿 Info.Version（string）当 Version 比，编译就会挡，
        //     但只测「能编译」不够，要钉住它读的是 ModEntry.Version。

        /// <summary>
        /// 三种态必须各自独立成支，且「已最新」「未检查」的分界是
        /// <c>checkedOnce</c>（UMM 是否真的查过），**不是** <c>newest != null</c>。
        ///
        /// 这条判据在实现过程中改过一次，原因值得记下来：反编译 IL 证明
        /// <c>ParseRepository</c> 只在 `current &lt; release` 时才 `stfld NewestVersion`
        /// ——**版本相等时它保持 null**。所以第一版判据（「已最新」挂在
        /// `newest != null` 上）虽然能通过，但它守的语义是错的：「已最新」
        /// 永远亮不起来，界面会一直停在「未检查」。
        /// 判据如果只验「三态都出现过」，就会替这个错误设计背书。
        ///
        /// 因此这里钉的是**分界条件本身**：
        ///   ① 三个文案键都在；
        ///   ② `else if` 的条件是 `checkedOnce`，不能是 `newest != null`；
        ///   ③ 未检查是最后那支（else）。
        /// </summary>
        private static bool UpdateStatesAreDistinct(string code)
        {
            string body = MethodBody(code, "DrawUpdateRow");
            if (body == null) return false;
            bool hasAvailable = body.Contains("about.update.available", StringComparison.Ordinal);
            bool hasLatest = body.Contains("about.update.latest", StringComparison.Ordinal);
            bool hasUnknown = body.Contains("about.update.unknown", StringComparison.Ordinal);
            if (!hasAvailable || !hasLatest || !hasUnknown) return false;

            // ② 「已最新」必须挂在 checkedOnce 上。挂回 newest != null 就是退回
            //    那个「永远不亮」的错误设计。
            if (!body.Contains("else if (checkedOnce)", StringComparison.Ordinal)) return false;
            if (body.Contains("else if (newest != null)", StringComparison.Ordinal)) return false;

            // ③ 未检查在最后。
            int unknownIdx = body.IndexOf("about.update.unknown", StringComparison.Ordinal);
            int latestIdx = body.IndexOf("about.update.latest", StringComparison.Ordinal);
            if (unknownIdx < latestIdx) return false;
            return true;
        }

        /// <summary>
        /// 必须真的去读 UMM 的 <c>Params.LastUpdateCheck</c>——那是区分「查过」与
        /// 「没查过」的唯一旁证。没有它，「已最新」这一态就无从判断，只能退回
        /// 「未检查」。
        ///
        /// 两点必须同时成立：① 按名字取 <c>Params</c>；② 读 <c>LastUpdateCheck</c>。
        /// 只判前者会放过「取了 Params 但没用它」的空实现。
        /// </summary>
        private static bool UpdateReadsLastUpdateCheck(string code)
        {
            string body = MethodBody(code, "LastUpdateCheckSeen");
            if (body == null) return false;
            if (!body.Contains("LastUpdateCheck", StringComparison.Ordinal)) return false;
            if (!body.Contains("\"Params\"", StringComparison.Ordinal)) return false;
            // 必须包在 try 里：反射失败时不能把整个设置页带走。
            string outer = MethodBody(code, "LastUpdateCheckSeen");
            return outer != null && outer.Contains("catch", StringComparison.Ordinal);
        }

        /// <summary>
        /// 比较方向必须是 <c>newest &gt; current</c>。
        ///
        /// 写成 <c>newest &lt; current</c>、<c>newest == current</c> 或干脆省掉比较
        /// （只判 newest != null）时，「有新版」这条永远不亮——功能看着接好了，
        /// 却从不响，而玩家只会以为自己一直是最新。
        /// </summary>
        private static bool UpdateVersionCompareDirection(string code)
        {
            string body = MethodBody(code, "DrawUpdateRow");
            if (body == null) return false;
            // 允许 newest > current 与 current < newest 两种等价写法，但**不许**反向。
            bool forward = body.Contains("newest > current", StringComparison.Ordinal)
                        || body.Contains("current < newest", StringComparison.Ordinal);
            if (!forward) return false;
            // 排除「反差方向也写了一遍」这种破坏对称性的写法。
            if (body.Contains("newest < current", StringComparison.Ordinal)) return false;
            if (body.Contains("current > newest", StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>
        /// 当前版本必须取 <c>Main.mod.Version</c>（System.Version），
        /// 不许取 <c>Main.mod.Info.Version</c>（string）。
        ///
        /// 这两者名字像、类型不同：拿 Info.Version 去跟 Version 比必然编译不过，
        /// 但若有人为了让它编译通过而改成 `new Version(Info.Version)`，
        /// 那就在「版本字符串不合规」时抛异常——而 OnGUI 抛异常的后果是
        /// 整个设置面板被 UMM 收掉。
        /// </summary>
        private static bool UpdateUsesModEntryVersion(string code)
        {
            string body = MethodBody(code, "CurrentVersionOrNull");
            if (body == null) return false;
            if (!body.Contains("Main.mod.Version", StringComparison.Ordinal)) return false;
            if (body.Contains("Info.Version", StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>
        /// <c>info.json</c> 必须同时有 <c>Repository</c> 与 <c>HomePage</c>，
        /// 且都是非空字符串。
        ///
        /// 这两个字段是 UMM 检查更新的**唯一入口**：
        ///   · 没有 Repository → UMM 根本不去下载版本清单，NewestVersion 永远 null；
        ///   · 没有 HomePage → UMM 列表里不显示那颗主页按钮，关于页的「打开主页」
        ///     也会因为拿不到 URL 而不画。
        /// 判「字段存在且非空」：只判存在会让 `"Repository": ""` 蒙混过关，
        /// 而那与没填等价（UMM 那边 IsNullOrEmpty 直接跳过）。
        /// </summary>
        private static bool InfoJsonHasUpdateFields(string code)
        {
            return HasNonEmptyJsonField(code, "Repository")
                && HasNonEmptyJsonField(code, "HomePage");
        }

        /// <summary>JSON 里是否有 <c>"name": "非空"</c>。宽容空格与顺序。</summary>
        private static bool HasNonEmptyJsonField(string code, string name)
        {
            int at = code.IndexOf("\"" + name + "\"", StringComparison.Ordinal);
            if (at < 0) return false;
            int colon = code.IndexOf(':', at);
            if (colon < 0) return false;
            int q1 = code.IndexOf('"', colon + 1);
            if (q1 < 0) return false;
            int q2 = code.IndexOf('"', q1 + 1);
            if (q2 < 0) return false;
            return q2 > q1 + 1;
        }

        /// <summary>
        /// 说明框的实现是否三件齐：只在 Repaint 画、高度按内容算、宽度不随内容膨胀。
        ///
        /// ① **只在 Repaint**：Layout 趟画的东西会被丢掉，文字测量还会多算一遍;
        /// ② **高度按内容**：写死高度会让长句被裁掉半句，而"说明只显示半句话"
        ///    玩家不会当成缺陷报回来，只会以为说明就这么点内容;
        /// ③ **宽度写死**：跟着内容走会让长说明撑满整行甚至溢出窗口。
        /// </summary>
        /// <summary>
        /// 说明框的越界判定是否用了**窗口可视区**而不是屏幕尺寸，且锚点是否鼠标。
        ///
        /// 判法有三条，缺一不可：
        /// ① <c>DrawTooltip</c> 体内**不许**再出现 <c>Screen.width</c>/<c>Screen.height</c>
        ///    ——它们判的是整块屏幕，而我们在窗口里，比较必然不成立；
        /// ② 必须真的去取可视矩形（<c>Viewport()</c>），且它内部要落到
        ///    <c>GUIClip.visibleRect</c> 上。只判 ① 会放过"删掉了越界判断、
        ///    什么夹取都不做"的写法——那同样会把框画到窗口外。
        /// ③ 锚点必须是 <c>Event.current.mousePosition</c>，不许再用静态的
        ///    控件矩形（<c>_tipAnchor</c>）。
        ///
        /// **③ 是 2026-10-18 用户第二次报同一处的根因。** 第一次只修了边界
        /// 用的坐标系（① ②），但锚点仍是控件矩形：鼠标在一行里移动框不动，
        /// 而 UMM 的 ScrollView 让控件矩形与可视区不同系，滚动后框被夹到
        /// 视口顶部——现象是"说明永远出现在模组名称那一行"。修法照 UMM 自己：
        /// 反编译 :3714-3722 用的就是 mousePosition。
        /// </summary>
        private static bool TooltipBoundsUseViewport(string code)
        {
            string body = MethodBody(code, "DrawTooltip");
            if (body == null) return false;
            // ① 不许拿屏幕尺寸当窗口边界。
            if (body.Contains("Screen.width", StringComparison.Ordinal)) return false;
            if (body.Contains("Screen.height", StringComparison.Ordinal)) return false;
            // ② 必须取可视区，且那个助手真的去问 GUIClip。
            if (!body.Contains("Viewport()", StringComparison.Ordinal)) return false;
            string vp = MethodBody(code, "Viewport");
            if (vp == null || !vp.Contains("visibleRect", StringComparison.Ordinal)) return false;
            // ③ 锚点必须是鼠标；静态控件矩形会让框不跟手。
            if (!body.Contains("Event.current.mousePosition", StringComparison.Ordinal)) return false;
            return !body.Contains("_tipAnchor", StringComparison.Ordinal);
        }

        private static bool TooltipBodyIsSane(string code)
        {
            string body = MethodBody(code, "DrawTooltip");
            if (body == null) return false;
            return body.Contains("EventType.Repaint", StringComparison.Ordinal)
                && body.Contains("CalcHeight(", StringComparison.Ordinal)
                && body.Contains("const float W =", StringComparison.Ordinal);
        }
        /// <summary>本文件已执行的判据条数。汇总行按它印，不写字面量——
        /// 原先硬编码的「四十八条」在加判据后就是一句谎话，而且没人会记得改。</summary>
        private static int _checksRun;

        /// <summary>
        /// 去特效的「保存拦截」与「按钮刷新」两条补丁**不许挂 Feature**（⑳c，2026-10-24 审计 D-F2）。
        ///
        /// <para>挂上就会随总开关一起卸载，而「谱面已被就地改写」这件事比开关活得久：
        /// 玩家开去特效进关（<c>LevelData.Decode</c> 已把内存里那份改完）→ 关掉开关 →
        /// 补丁随之下线 → Ctrl+S 把剥过的数据写回原文件，原谱就没了。</para>
        ///
        /// <para><c>LoadGameScene</c> 那条同理：关掉开关后重新进关，若没人再刷新，
        /// 保存按钮会停在上一局留下的禁用态。两条都必须常开，由
        /// <c>EffectRemover.EditorSaveEnabled</c> 自己判断。</para>
        ///
        /// <para>判据形状：找到这两条 <c>[SpectrePatch(...)]</c>，断言其参数里没有
        /// <c>Feature</c>，且两条都要真的存在（删掉整条也不会白绿）。</para>
        /// </summary>
        private static bool SaveGuardNotFeatureGated(string code)
        {
            bool sawSave = false, sawLoad = false;
            foreach (string raw in code.Split('\n'))
            {
                string line = raw.Trim();
                if (!line.Contains("[SpectrePatch(", StringComparison.Ordinal)) continue;
                if (line.Contains("SaveLevelEditorAction", StringComparison.Ordinal))
                {
                    sawSave = true;
                    if (line.Contains("Feature", StringComparison.Ordinal)) return false;
                }
                if (line.Contains("LoadGameScene", StringComparison.Ordinal))
                {
                    sawLoad = true;
                    if (line.Contains("Feature", StringComparison.Ordinal)) return false;
                }
            }
            return sawSave && sawLoad;
        }

        /// <summary>
        /// ㊱ 的判定：每个克隆自 skin.label 的样式，初始化器里必须显式写
        /// <c>clipping = TextClipping.Clip</c>。对象初始化器可能跨行
        /// （_tipStyle 就是），用 <c>[^}]</c> 配 Singleline 一次取到大括号内容。
        /// 一个匹配都找不到同样判失败：判据的默认姿态是不信。
        /// </summary>
        private static bool LabelStylesExplicitlyClip(string s)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(
                s, @"new GUIStyle\(GUI\.skin\.label\)\s*\{(?<init>[^}]*)\}",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (matches.Count == 0)
            {
                return false;
            }
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (!m.Groups["init"].Value.Contains(
                    "clipping = TextClipping.Clip", StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// ㊲ 的判定：只看 Header 方法体（从签名到第一个 8 空格缩进的收括号），
        /// 徽章宽度必须来自 _smallC.CalcSize，写死的 -132f 定位不许回来。
        /// </summary>
        private static bool HeaderBadgeMeasuresText(string s)
        {
            int at = s.IndexOf("internal static void Header", StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }
            int end = s.IndexOf("\n        }", at, StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }
            string body = s.Substring(at, end - at);
            return body.Contains("_smallC.CalcSize", StringComparison.Ordinal)
                && !body.Contains("- 132f", StringComparison.Ordinal);
        }

        private static int Check(string projectRoot, string name, string rel,
            Func<string, bool> predicate)
        {
            _checksRun++;
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
