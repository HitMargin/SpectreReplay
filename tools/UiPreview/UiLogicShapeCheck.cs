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
            if (fail == 0)
                Console.WriteLine("    ✓ 十一条界面形状判据都成立");
            return fail;
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
