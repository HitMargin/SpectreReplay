using System;
using System.IO;
using UnityEngine;
using UnityModManagerNet;
using ModEntry = UnityModManagerNet.UnityModManager.ModEntry;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// UMM 自带设置界面（IMGUI / GUILayout）。外观由 <see cref="Gui"/> 提供，
    /// 这里只负责把页面摆出来、把输入派发下去。
    ///
    /// **四个必须在位的结构约束，每一个都对应一类真实故障：**
    ///
    /// 1. OnGUI / OnShow / OnHide / **OnSave** 是四个不同的入口。
    ///    UMM 窗口底部的 Save 走 SaveSettingsAndParams()，它**只**调 OnSaveGUI，
    ///    既不关窗口、也不隐藏我们的设置页
    ///    （UnityModManager.decompiled.cs:3706-3709 / :6326-6343）。
    ///    原先这里把 OnSaveGUI 直接接到 OnHide，而 OnHide 第一句是
    ///    `_visible = false`——于是玩家一点 Save，设置内容当场变成一整片空白，
    ///    这就是"点保存之后设置不见了"。保存与隐藏必须分开（见 OnSave）。
    ///
    /// 2. Save 必须**当场写盘**，不能只置脏等下一帧。落盘若只挂在宿主对象的
    ///    每帧回调上，宿主缺失/异常时 _dirty 永远不写，而 UMM 会把 OnSaveGUI
    ///    的异常吞进日志——界面上看不到任何失败，现象就是"保存了但没保存上"。
    ///
    /// 3. 内容外面必须自己包一层 ScrollView。UMM 只给自己的 tab 包滚动
    ///    （:3738 / :3950 / :3988），**模组 OnGUI 的调用点没有包**
    ///    （:3886-3899）。不自己包，内容一旦超过窗口高度，下面的控件就是
    ///    "画在窗口外"：看得见一半、点不到另一半。
    ///
    /// 4. 每帧开头把下拉序号归零。IMGUI 一帧跑多趟（Layout / Repaint），
    ///    序号跨趟自增会让存下来的"第几个下拉"在第二趟对不上号，
    ///    现象是下拉点开的瞬间就收起，等于点不动。
    /// </summary>
    internal static class OnGui
    {
        /// <summary>UMM 选中本模组并显示设置时为真。画界面前置条件。</summary>
        private static bool _visible;

        /// <summary>当前页（0 起始）。必须存静态字段：见类注释第 4 条。</summary>
        private static int _tab;

        /// <summary>内容滚动位置。见类注释第 3 条。</summary>
        private static Vector2 _scroll;

        /// <summary>玩家正在改的文本（可能还没提交）。</summary>
        private static string _textDraft;

        /// <summary>当前展开的下拉是第几个行（-1 = 没有）。只能存"第几个"。
        /// 存内容或索引都不行：同一帧里可能有几个下拉，闭合一个不能把另一个也关了。</summary>
        private static int _openRow = -1;

        /// <summary>本趟画到第几个下拉。必须在每趟开头归零（类注释第 4 条）。</summary>
        private static int _openSeq;

        /// <summary>正在等按键的那一行（0 = 没有）。</summary>
        private static int _capturing;

        /// <summary>
        /// 挂失败的补丁数。由 Main 注入。
        ///
        /// UI 层不直接引 PatchLoader：离线预览器不链接那套（要 Harmony 与游戏
        /// 程序集），引了那边编不过。挂不上的补丁 = 对应功能静默消失，
        /// 而设置页看起来一切正常——必须在顶部明说。
        /// </summary>
        internal static int PatchFailures;

        private static readonly string[] TabKeys =
        {
            "page.record", "page.playback", "page.render", "page.about",
        };

        /// <summary>标签页固定宽度：四个页名长短不一，按内容伸缩会让整排跳动。</summary>
        private const float TabW = 132f;

        // ── UMM 回调 ────────────────────────────────────────────

        internal static void OnShow(ModEntry modEntry)
        {
            _visible = true;
            // 每次打开都重取一次：目录里的 .sprp 可能刚被录出来，
            // 而文本框的草稿上一轮可能还留着玩家没提交完的编辑。
            _textDraft = Settings.RenderOutputDir;
            Loc.RefreshLanguage();
        }

        internal static void OnHide(ModEntry modEntry)
        {
            _visible = false;
            // 关窗口时提交文本框里还没回车的内容——窗口一关，玩家看不到任何
            // "未保存"的提示，不提交就等于白打。顺手落一次盘，与 Save 同语义。
            CommitText();
            // 拖动滑条时落盘被推迟了（见 Pages.Commit）。FlushConfig 是统一收口，
            // 它会强制补上——否则"松手前就关窗"= 这次拖动白改。
            FlushConfig();
        }

        /// <summary>
        /// UMM 底部「Save」按钮的入口。**必须与 OnHide 分开**（类注释第 1 条）。
        ///
        /// Save 不关窗口：它只要求"把当前设置落盘"。所以这里**不碰 _visible**，
        /// 只做两件事：提交文本框草稿、立刻写盘（类注释第 2 条）。
        /// </summary>
        internal static void OnSave(ModEntry modEntry)
        {
            CommitText();
            FlushConfig();
        }

        /// <summary>把配置立刻写盘。包一层 try：UMM 会吞掉 OnSaveGUI 的异常，
        /// 抛出去只会变成日志里一行，玩家什么也看不到。
        ///
        /// 这里是**所有落盘路径的统一收口**（关窗 / 底部 Save / 关模组 / 退出）。
        /// 有待办时先跑 Pages 的完整提交：它除了写盘还会 Settings.check 重新解析
        /// 键表并把热键绑回 SpectreState——只写盘的话，改完热键立刻退游戏，
        /// 本次会话里那个键就是没绑上（下次启动才生效）。</summary>
        internal static void FlushConfig()
        {
            try
            {
                if (Pages.Pages.FlushPending(true))
                {
                    return;   // 完整提交已经写过盘了，不重复写
                }
                if (Main.mod != null && !string.IsNullOrEmpty(Main.mod.Path))
                    ConfigManager.SaveConfigs(Path.Combine(Main.mod.Path, "Configs.json"));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 保存设置失败：" + ex.Message);
            }
        }

        /// <summary>UMM 每帧调用。mod.Active 为假时根本不画。</summary>
        internal static void OnGUI(ModEntry modEntry)
        {
            if (!_visible)
            {
                return;
            }
            Loc.RefreshLanguage();
            if (_textDraft == null)
            {
                _textDraft = Settings.RenderOutputDir;
            }

            _openSeq = 0;
            // ScrollView 包住**整页**（含头部与标签）：只包正文的话，
            // 头部滚动条位置会与正文错位。异常用 finally 收尾，
            // 避免留下未配对的 Begin——UMM 遇到未配对布局会整窗崩掉。
            _scroll = GUILayout.BeginScrollView(_scroll);
            try
            {
                DrawHeader();
                DrawPatchWarning();
                DrawTabs();
                GUILayout.Space(4f);
                DrawPage();
                GUILayout.Space(12f);
            }
            finally
            {
                GUILayout.EndScrollView();
            }
            // 拖动滑条时落盘被推迟（见 Pages.Commit）。放在**画完**之后：
            // 这时 Input.GetMouseButton(0) 反映的是本帧真实状态，松手那一帧
            // 会走到这里把攒下的改动一次写清。
            Pages.Pages.FlushPending();
        }

        // ── 头部 ────────────────────────────────────────────────

        /// <summary>
        /// 品牌头：名称 + 提示 + 右侧状态徽章。
        ///
        /// 状态从原来的"一行裸文字"收进徽章里。关键是**四种情况都有话可说**：
        /// 原先不在渲染时直接 return，于是开着录制打一整局顶栏一片空白，
        /// 玩家只会觉得"我明明勾了录制，怎么什么都没发生"。
        /// </summary>
        private static void DrawHeader()
        {
            string status;
            Color color;
            if (Features.VideoRender.VideoRender.Active)
            {
                var snap = Features.VideoRender.VideoRender.Current;
                string stage = string.IsNullOrEmpty(snap.Stage) ? "" : snap.Stage;
                string pct = snap.FramesTotal > 0
                    ? "  " + (snap.Progress * 100f).ToString("F0") + "%"
                    : "";
                status = stage + pct;
                color = Gui.Accent;
            }
            else if (SpectreReplay.SpectreState.is_recording)
            {
                status = Loc.T("status.recording");
                color = Gui.BadCol;
            }
            else if (SpectreReplay.SpectreState.is_playing)
            {
                status = Loc.T("status.playing");
                color = Gui.OkCol;
            }
            else if (Settings.ReplayOn)
            {
                status = Loc.T("status.idle");
                color = Gui.DimCol;
            }
            else
            {
                // 总开关关着：什么都不录也不渲，功能层却还挂着，
                // 属于"看起来正常、其实整条链是空的"，必须说明。
                status = Loc.T("status.all_off");
                color = Gui.WarnCol;
            }
            Gui.Header(Loc.Brand, Loc.Hint, status, color);
        }

        /// <summary>
        /// 补丁挂不上时在顶部说一次。
        ///
        /// 挂不上的补丁 = 对应功能静默消失：玩家看到的是"某个开关点了没反应"，
        /// 猜不到是补丁没挂上，而原因只在日志里。这一段不能省。
        /// </summary>
        private static void DrawPatchWarning()
        {
            if (PatchFailures <= 0)
            {
                return;
            }
            GUILayout.Space(4f);
            Rect r = GUILayoutUtility.GetRect(1f, 24f, GUILayout.ExpandWidth(true));
            Gui.Fill(r, new Color(Gui.BadCol.r, Gui.BadCol.g, Gui.BadCol.b, 0.18f));
            Gui.Frame(r, new Color(Gui.BadCol.r, Gui.BadCol.g, Gui.BadCol.b, 0.6f));
            Gui.Text(new Rect(r.x + 10f, r.y, r.width - 16f, r.height),
                Loc.T("ui.patchfailed") + "  (" + PatchFailures + ")", Gui.BadCol, Gui.BoldStyle);
            GUILayout.Space(4f);
        }

        // ── 分页 ────────────────────────────────────────────────

        private static void DrawTabs()
        {
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < TabKeys.Length; i++)
            {
                bool on = i == _tab;
                Color oldBg = GUI.backgroundColor;
                // 选中页用强调色实底、未选中压暗——比只靠按钮自带的 hover
                // 更容易分辨"我在哪一页"。用完立刻还原，否则会把 UMM 自己的
                // 表头与 Save 按钮一起染色（它在调用我们之前刚设成 white）。
                GUI.backgroundColor = on ? Gui.Accent : new Color(1f, 1f, 1f, 0.72f);
                if (GUILayout.Button(Loc.T(TabKeys[i]), Gui.ButtonStyle,
                        GUILayout.Width(TabW), GUILayout.Height(28f)))
                {
                    _tab = i;
                    // 换页时收起展开的下拉：否则旧页的下拉会挂在新页上，
                    // 看起来像"点开了一个不属于这里的列表"。
                    _openRow = -1;
                }
                GUI.backgroundColor = oldBg;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private static void DrawPage()
        {
            GUILayout.BeginVertical();
            try
            {
                switch (_tab)
                {
                    case 0: Pages.Pages.DrawRecord(); break;
                    case 1: Pages.Pages.DrawPlayback(); break;
                    case 2: Pages.Pages.DrawRender(); break;
                    default: Pages.Pages.DrawAbout(); break;
                }
            }
            finally
            {
                GUILayout.EndVertical();
            }
        }

        // ── 控件 ────────────────────────────────────────────────
        // 都排成"标签在左、控件在右"的一行。用固定标签宽度而不是自动排布：
        // 不同语言下标签长度差很多，自动排布会让控件列左右跳动。

        internal const float LabelW = Gui.LabelW;

        /// <summary>一行开关。视觉走 Gui.Switch（自绘 pill）。</summary>
        internal static void Row(string labelKey, bool value, Action<bool> onChange)
        {
            Gui.Switch(labelKey, value, onChange);
        }

        /// <summary>一行滑条 + 右侧当前值。</summary>
        internal static void RowSlider(string labelKey, float value, float min, float max,
            Func<float, string> fmt, Action<float> onChange)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), Gui.LabelStyle, GUILayout.Width(LabelW));
            float now = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(Gui.CtrlW));
            GUILayout.Label(fmt(now), Gui.LabelStyle, GUILayout.Width(90f));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(now, value))
            {
                onChange(now);
            }
        }

        /// <summary>
        /// 一行下拉：点按钮展开列表，选中即收起，点开的那行再点一次也收起。
        ///
        /// 上一版用 SelectionGrid 当下拉——那是把选项**竖排全部画出来**，
        /// 一行占掉半页，根本不是下拉。
        /// </summary>
        internal static void RowDropdown(string labelKey, string[] options, int current,
            Action<int> onChange)
        {
            int seq = _openSeq++;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), Gui.LabelStyle, GUILayout.Width(LabelW));
            string shown = current >= 0 && current < options.Length ? options[current] : "—";
            if (GUILayout.Button(shown, Gui.ButtonStyle,
                    GUILayout.Width(Gui.CtrlW), GUILayout.Height(24f)))
            {
                _openRow = _openRow == seq ? -1 : seq;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (_openRow != seq)
            {
                return;
            }
            // 展开的选项缩进到标签列右边，视觉上属于这一行
            GUILayout.BeginHorizontal();
            GUILayout.Space(LabelW);
            GUILayout.BeginVertical(GUILayout.Width(Gui.CtrlW));
            for (int i = 0; i < options.Length; i++)
            {
                // 当前项标出来：选项多的时候光靠位置认不出来
                string mark = i == current ? "● " : "    ";
                if (GUILayout.Button(mark + options[i], Gui.ButtonStyle, GUILayout.Height(22f)))
                {
                    _openRow = -1;
                    if (i != current)
                    {
                        onChange(i);
                    }
                }
            }
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 「按任意键改键」的行。三段状态：已绑定 / 等待按键 / 未绑定。
        /// 点一次进入等待，再点退出；等待期间 Esc 取消**并独占 Esc**——
        /// 否则玩家想绑 Esc，窗口却先被 UMM 关掉，一来一回永远绑不上。
        /// </summary>
        internal static void RowHotkey(string labelKey, Func<int> get, Action<int> set)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), Gui.LabelStyle, GUILayout.Width(LabelW));
            int cur = get();
            bool waiting = _capturing == GetCaptureId(labelKey);
            string text = waiting
                ? Loc.T("ui.presskey")
                : (cur > 0 ? KeyName((KeyCode)cur) : Loc.T("ui.nokey"));
            // 等待按键时把按钮染成强调色：这是"现在轮到你按键"的唯一提示
            Color oldBg = GUI.backgroundColor;
            if (waiting)
            {
                GUI.backgroundColor = Gui.Accent;
            }
            if (GUILayout.Button(text, Gui.ButtonStyle,
                    GUILayout.Width(Gui.CtrlW), GUILayout.Height(24f)))
            {
                int id = GetCaptureId(labelKey);
                _capturing = _capturing == id ? 0 : id;
            }
            GUI.backgroundColor = oldBg;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (Event.current == null || _capturing != GetCaptureId(labelKey))
            {
                return;
            }
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                _capturing = 0;
                Event.current.Use();
                return;
            }
            // 只在 Repaint 扫，避免 Layout 趟也吃输入——那样一个键会被算两次
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            foreach (KeyCode k in (KeyCode[])Enum.GetValues(typeof(KeyCode)))
            {
                // 鼠标键不适合当保存键：分不清左中右，而且点哪都触发
                if (k == KeyCode.Mouse0 || k == KeyCode.Mouse1 || k == KeyCode.Mouse2)
                {
                    continue;
                }
                if (!Input.GetKeyDown(k))
                {
                    continue;
                }
                _capturing = 0;
                set((int)k);
                Settings.CommitSafe();
                return;
            }
        }

        private static int GetCaptureId(string labelKey) => labelKey.GetHashCode() & 0x7fffffff;

        /// <summary>键码 → 可读名字。F1..F12 是连续的，其余交给枚举名。</summary>
        internal static string KeyName(KeyCode k)
        {
            int n = (int)k;
            if (n >= 282 && n <= 293)
            {
                return "F" + (n - 281);
            }
            return k.ToString();
        }

        /// <summary>
        /// 一段说明文字。**换行**（Gui.NoteStyle 设了 wordWrap）：UMM 把
        /// GUI.skin.label.clipping 改成了 Overflow，照抄默认样式的话长句会一路
        /// 画出窗口外而不是折行，玩家看到半句话被硬生生截断。
        /// </summary>
        internal static void Note(string labelKey)
        {
            NoteText(Loc.T(labelKey));
        }

        /// <summary>一段不带标签键的说明文字（内容已经本地化过了）。</summary>
        internal static void NoteText(string text)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(LabelW);
            GUILayout.Label(text, Gui.NoteStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        /// <summary>一整行按钮。wide = 主要动作（开始渲染），用强调色底 + 加粗。</summary>
        internal static void Button(string labelKey, Action onClick, bool wide)
        {
            GUILayout.BeginHorizontal();
            Color oldBg = GUI.backgroundColor;
            if (wide)
            {
                GUI.backgroundColor = Gui.Accent;
            }
            bool hit = GUILayout.Button(Loc.T(labelKey),
                wide ? Gui.PrimaryButtonStyle : Gui.ButtonStyle,
                GUILayout.Height(28f), GUILayout.Width(wide ? 220f : 140f));
            GUI.backgroundColor = oldBg;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (hit)
            {
                onClick();
            }
        }

        /// <summary>
        /// 文本框的控件名。回车提交靠它认焦点。
        ///
        /// **刻意不含点号。** LocCheck.CollectUsedKeys() 用正则
        /// `"([a-z][a-z0-9]*\.[a-z0-9_.]+)"` 扫页面源码收集文案键，点号接小写单词
        /// 长得和真正的文案键一模一样：写成"模块名.名字"就会被闸门当成漏翻的键，
        /// 报"两种语言都没有"。控件名不需要点号，不点就两不误伤。
        /// </summary>
        private const string RowInputControlName = "OutDirBox";

        /// <summary>
        /// 文本输入行。**改动先落到草稿，回车才提交。**
        ///
        /// 原先每次按键都直接提交：一个字符一次落盘。后果不只是"多个循环"
        /// ——玩家想整个删掉路径（Ctrl+A 再退格）时，中途每个中间态都会被
        /// 存下来并被立刻用掉（输出目录会瞬间变成不存在的目录）。
        /// </summary>
        internal static void RowInput(string labelKey)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), Gui.LabelStyle, GUILayout.Width(LabelW));
            // SetNextControlName + GetNameOfFocusedControl 是 Unity 自己留下的成对
            // 接口：前者给**即将画的下一个控件**起名，后者问焦点在谁身上。不用它
            // 就得自己维护 control id，而 id 取决于前后控件，容易在 Repaint 那趟
            // 失焦——症状是"偶尔按回车没反应"。
            GUI.SetNextControlName(RowInputControlName);
            // **宽度必须走 Gui.CtrlW，不能写死 360。** 页面上每一行控件列
            // 都是 LabelW + CtrlW，写死 360 会让这一行的控件比上面几行宽一截——
            // 视觉上"这一行歪了"，而且整页包在 ScrollView 里，窗口窄一点时
            // LabelW+360 先溢出（下拉宽 70px 才会露不出来）。
            _textDraft = GUILayout.TextField(_textDraft ?? "", GUILayout.Width(Gui.CtrlW));
            bool enter = Event.current != null
                && Event.current.type == EventType.KeyDown
                && Event.current.keyCode == KeyCode.Return
                && GUI.GetNameOfFocusedControl() == RowInputControlName;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (enter)
            {
                CommitText();
                GUI.FocusControl(null);
            }
        }

        /// <summary>提交文本框内容。关窗口、点 Save 时都会调。</summary>
        internal static void CommitText()
        {
            if (_textDraft != null && _textDraft != Settings.RenderOutputDir)
            {
                Settings.RenderOutputDir = _textDraft;
                Settings.CommitSafe();
            }
        }
    }
}
