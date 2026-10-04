using System;
using System.Collections.Generic;
using UnityEngine;
using UnityModManagerNet;
using ModEntry = UnityModManagerNet.UnityModManager.ModEntry;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// UMM 自带设置界面（IMGUI / GUILayout）。
    ///
    /// **这一整套是推倒重写的。** 之前用自建 Canvas + RectTransform 画了
    /// 100KB 的 UI（Gfx / PageBuilder / Theme / UiFonts / PanelHost），
    /// 踩的坑一个接一个：EventSystem 与游戏的撞车、ScrollRect 归位时机、
    /// raycast 标志、RectMask2D 裁剪、TMP 字体回退……每一个都要单独调，
    /// 而每一个的失败方式都长得像"界面坏了"。
    ///
    /// 换成 UMM 的 OnGUI 之后，这些**整类问题都不存在**了：
    ///   · 画布、EventSystem、GraphicRaycaster 全是 UMM 管的
    ///   · 点击与滚轮由 IMGUI 自己处理，不吃射线
    ///   · 字体用 UMM 的皮肤，中文由 UMM 自己解决（不再需要 TMP 资源）
    ///   · 布局用 GUILayout，不存在锚点算错、sizeDelta 恒为 0 这类坑
    ///
    /// 代价是外观朴素（UMM 的默认皮肤），但**功能与可维护性上的收益
    /// 远大于外观上的损失**——设置页能稳定点得动，比好看重要。
    /// </summary>
    internal static class OnGui
    {
        /// <summary>UMM 选中本模组并显示设置时为真。画界面前置条件。</summary>
        private static bool _visible;

        /// <summary>
        /// 上一次画的是哪一页。0 起始。
        ///
        /// 必须存在静态字段里：OnGUI 每帧被调用好几遍（IMGUI 的
        /// Layout/Repaint 两趟），任何"当帧新建"的局部状态都会在
        /// Layout 与 Repaint 之间丢掉，控件就会跳位置。
        /// </summary>
        private static int _tab;

        /// <summary>玩家正在改的文本（可能还没提交）。</summary>
        private static string _textDraft;

        /// <summary>
        /// 当前展开的下拉是第几个行（-1 = 没有）。
        ///
        /// 只能存"第几个下拉"，不能存内容或索引：同一帧里可能有几个下拉，
        /// 闭合一个不能把另一个也关了。
        /// </summary>
        private static int _openRow = -1;

        /// <summary>
        /// 本趟画到第几个下拉了。**必须在每趟开头归零**。
        ///
        /// IMGUI 一帧跑好几趟（Layout / Repaint 各一次）。如果让计数器
        /// 跨趟自增，Repaint 那趟的编号就跟 Layout 对不上，存下来的
        /// _openRow 永远找不到自己——下拉点开的瞬间就合上，等于点不动。
        /// </summary>
        private static int _openSeq;

        /// <summary>
        /// 挂失败的补丁数。由 Main 注入。
        ///
        /// UI 层不直接引 PatchLoader：编译隔离，也让 OnGui.cs 不依赖
        /// Harmony。挂不上的补丁 = 对应功能静默消失，而设置页看起来
        /// 一切正常——必须在设置页顶部明说，否则玩家只会觉得
        /// "这个开关点了没反应"，去翻日志的不到。
        /// </summary>
        internal static int PatchFailures;

        private static readonly string[] TabKeys =
        {
            "page.record", "page.playback", "page.render", "page.about",
        };

        /// <summary>
        /// 选中标签页的底色。原本在 Theme.cs 里——那一整套跟着自建
        /// Canvas 一起删了，只剩这一个颜色还有用，就近定义。
        /// </summary>
        private static readonly Color Accent = new Color(0.24f, 0.62f, 0.92f);

        internal static void OnShow(ModEntry modEntry)
        {
            _visible = true;
            // 每次打开都重取一次：目录里的 .srp 可能刚被录出来，
            // 而文本框的草稿上一轮可能还留着玩家没提交完的编辑。
            _textDraft = Settings.RenderOutputDir;
        }

        internal static void OnHide(ModEntry modEntry)
        {
            _visible = false;
            // 关窗口时提交文本框里还没按回车的内容——UMM 的窗口一关，
            // 玩家看不到任何"未保存"的提示，不提交就等于白打。
            CommitText();
        }

        /// <summary>UMM 每帧调用。mod.Active 为假时根本不画。</summary>
        internal static void OnGUI(ModEntry modEntry)
        {
            if (!_visible)
            {
                return;
            }
            Loc.RefreshLanguage();
            // 面板刚打开时先把文本框填上，之后由玩家编辑
            if (_textDraft == null)
            {
                _textDraft = Settings.RenderOutputDir;
            }

            // IMGUI 一帧跑两趟（Layout/Repaint），_openSeq 若跨趟自增，
            // 下拉存的行号在第二趟就对不上了——点开的瞬间被判定成"别的行"
            // 直接收起。每趟开头归零，两趟编号才一致。
            _openSeq = 0;
            GUILayout.Space(4f);
            DrawStatus();
            DrawPatchWarning();
            DrawTabs();
            GUILayout.Space(8f);
            DrawPage();
        }

        /// <summary>
        /// 顶部那行"现在在干什么"。
        ///
        /// **这一行以前只会画渲染进度，别的什么都不画。** 后果是：开着录制
        /// 打了一整局，设置页顶部**一片空白**——玩家看到的是"我明明勾了
        /// 录制，怎么什么都没发生"。而 `status.idle` / `status.recording` /
        /// `status.playing` 这几条文案早就写好了，却从来没人引用。
        ///
        /// 现在改成一条真正的总状态行：渲染中显示百分比，其余情况显示
        /// 当前在做的事（录制中 / 回放中 / 回放已装好 / 空闲）。
        ///
        /// 事件提示仍然走游戏自带的 Notification（浮在游戏画面上，那个才是
        /// 玩家真正会看到的地方）；这里只留**持续状态**，不刷通知。
        /// </summary>
        private static void DrawStatus()
        {
            // 状态行：渲染中显示进度，其余显示录制/回放/空闲（移植 Spectre 状态）
            if (Features.VideoRender.VideoRender.Active)
            {
                var snap = Features.VideoRender.VideoRender.Current;
                string stage = !string.IsNullOrEmpty(snap.Stage) ? snap.Stage : "";
                string pct = snap.FramesTotal > 0
                    ? "  " + (snap.Progress * 100f).ToString("F0") + "%"
                    : "";
                GUILayout.Label(stage + pct, GUILayout.Height(20f));
                return;
            }
            if (SpectreReplay.SpectreState.is_recording)
            {
                GUILayout.Label(Loc.T("status.recording"), GUILayout.Height(20f));
                return;
            }
            if (SpectreReplay.SpectreState.is_playing)
            {
                GUILayout.Label(Loc.T("status.playing"), GUILayout.Height(20f));
                return;
            }
            if (Settings.ReplayOn)
            {
                GUILayout.Label(Loc.T("status.idle"), GUILayout.Height(20f));
                return;
            }
            GUILayout.Label(Loc.T("status.all_off"), GUILayout.Height(20f));
        }

        /// <summary>
        /// 补丁挂不上时，在设置页顶部说一次。
        ///
        /// **这一步不能省。** 挂不上的补丁 = 对应功能静默消失：玩家看到的是
        /// "某个开关点了没反应"，根本猜不到是补丁没挂上，而原因只在日志里。
        /// Spectre 专门为这件事做了 ShowApplyFailureSummary，注释写得很直白。
        ///
        /// 早先那版是弹一条横幅，跟着自建 Canvas 界面一起删掉了；这里改成
        /// 常驻在设置页顶部——UMM 的 OnGUI 没有独立的提示层，而设置页是玩家
        /// 唯一会主动打开的地方，在这里提示比藏在日志里强得多。
        /// </summary>
        private static void DrawPatchWarning()
        {
            if (PatchFailures <= 0)
            {
                return;
            }
            // **不能用 GUI.backgroundColor 上色**——那只影响 Button/Box 之类
            // 用背景贴图的控件，Label 是直接画文字的，改了 backgroundColor
            // 一点变化都没有。之前那版红底警示就这么"生效"着，其实全是
            // 默认样式，看着跟普通文字没区别。底色走 skin.box，文字走
            // GUI.color，两个都要设才对。
            GUIStyle style = WarnStyle();
            Rect r = GUILayoutUtility.GetRect(
                LabelW + 420f, 26f, GUILayout.ExpandWidth(false));
            // 顺序要紧：先设 backgroundColor **再**画 Box。反过来写的话
            // Box 用的是默认色，红底等于没画——而 Label 那时也已经是默认样式，
            // 从外面看就是"什么都没变"，完全察觉不到设错了。
            Color oldColor = GUI.color;
            Color oldBox = GUI.backgroundColor;
            GUI.backgroundColor = WarnBoxColor;
            GUI.Box(r, GUIContent.none, GUI.skin.box);
            GUI.color = new Color(1f, 0.94f, 0.94f);
            GUI.Label(new Rect(r.x + 8f, r.y + 3f, r.width - 16f, 20f),
                Loc.T("ui.patchfailed") + "  (" + PatchFailures + ")", style);
            GUI.color = oldColor;
            GUI.backgroundColor = oldBox;
        }

        private static readonly Color WarnBoxColor = new Color(0.55f, 0.16f, 0.16f, 1f);

        /// <summary>警示文字样式：加粗，保证在 skin.box 上也读得清。</summary>
        private static GUIStyle _warnStyle;
        private static GUIStyle WarnStyle()
        {
            if (_warnStyle == null)
            {
                _warnStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                };
            }
            return _warnStyle;
        }

        /// <summary>
        /// 分组标题样式。加粗 + 留白，与下面的控件行拉开层次——
        /// 照 Spectre 的 FeaturesBase（它用 fontStyle=Bold 的 GUIStyle）的做法。
        /// 纯 Label 的话所有文字一样重，十几行连成一片看不出哪里是块。
        /// </summary>
        private static GUIStyle _boldLabel;
        private static GUIStyle BoldLabel()
        {
            if (_boldLabel == null)
            {
                _boldLabel = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                };
            }
            return _boldLabel;
        }

        // ── 分页 ────────────────────────────────────────────────

        private static void DrawTabs()
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < TabKeys.Length; i++)
            {
                Color old = GUI.backgroundColor;
                // 选中的页用强调色，不选中的压暗——比只靠按钮高亮更容易分辨
                GUI.backgroundColor = i == _tab
                    ? new Color(0.24f, 0.62f, 0.86f)
                    : new Color(1f, 1f, 1f, 0.72f);
                if (GUILayout.Button(Loc.T(TabKeys[i]),
                        GUILayout.Height(26f), GUILayout.ExpandWidth(true)))
                {
                    _tab = i;
                }
                GUI.backgroundColor = old;
            }
            GUILayout.EndHorizontal();
        }

        private static void DrawPage()
        {
            GUILayout.BeginVertical();
            switch (_tab)
            {
                case 0:
                    Pages.Pages.DrawRecord();
                    break;
                case 1:
                    Pages.Pages.DrawPlayback();
                    break;
                case 2:
                    Pages.Pages.DrawRender();
                    break;
                default:
                    Pages.Pages.DrawAbout();
                    break;
            }
            GUILayout.EndVertical();
        }

        // ── 控件 ────────────────────────────────────────────────
        // 都排成"标签在左、控件在右"的一行。用固定标签宽度而不是
        // GUILayout.Label + 自动排布：不同语言下标签长度差很多，
        // 自动排布会让控件列左右跳动，拖滑条时尤其明显。

        internal const float LabelW = 190f;

        /// <summary>一行开关。</summary>
        internal static void Row(string labelKey, bool value, Action<bool> onChange)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), GUILayout.Width(LabelW));
            bool now = GUILayout.Toggle(value, GUIContent.none, GUILayout.Width(20f));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (now != value)
            {
                onChange(now);
            }
        }

        /// <summary>一行整数滑条 + 右侧当前值。</summary>
        internal static void RowSlider(string labelKey, float value, float min, float max,
            Func<float, string> fmt, Action<float> onChange)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), GUILayout.Width(LabelW));
            float now = GUILayout.HorizontalSlider(value, min, max,
                GUILayout.Width(200f));
            GUILayout.Label(fmt(now), GUILayout.Width(90f));
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(now, value))
            {
                onChange(now);
            }
        }

        /// <summary>
        /// 一行下拉。选项是已本地化的文字。
        ///
        /// 上一版用 SelectionGrid 当下拉——那是把 4 个选项**竖排全部画出来**，
        /// 一行占 96px，根本不是下拉。现在是常见做法：点按钮展开列表，
        /// 选中即收起，点开的那行再点一次也收起。
        /// </summary>
        internal static void RowDropdown(string labelKey, string[] options, int current,
            Action<int> onChange)
        {
            int seq = _openSeq++;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), GUILayout.Width(LabelW));
            string shown = current >= 0 && current < options.Length
                ? options[current] : "—";
            if (GUILayout.Button(shown, GUILayout.Width(200f), GUILayout.Height(24f)))
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
            GUILayout.BeginVertical(GUILayout.Width(200f));
            for (int i = 0; i < options.Length; i++)
            {
                // 当前项标出来：选项多的时候光靠位置认不出来
                string mark = i == current ? "● " : "    ";
                if (GUILayout.Button(mark + options[i], GUILayout.Height(22f)))
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
        /// 「按任意键改键」的行。
        ///
        /// 照 Spectre 的 Features/QuartzUi/SpectreUiPages.cs（它用
        /// KeyCaptureWatch 组件每帧扫 Input.GetKeyDown）。IMGUI 版不需要那个
        /// 组件——OnGUI 每帧被调用，在这里扫一遍等价，而且少一个常驻组件。
        ///
        /// 三段状态：已绑定（显示键名）/ 等待按键（显示 "…"）/ 未绑定。
        /// 点一次进入等待，再点退出；**等待期间要能按 Esc 取消**，否则手滑
        /// 按了 Esc 就再也退不出来。
        /// </summary>
        internal static void RowHotkey(string labelKey, Func<int> get, Action<int> set)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), GUILayout.Width(LabelW));
            int cur = get();
            string text = _capturing == GetCaptureId(labelKey)
                ? Loc.T("ui.presskey")
                : (cur > 0 ? KeyName((KeyCode)cur) : Loc.T("ui.nokey"));
            if (GUILayout.Button(text, GUILayout.Width(200f), GUILayout.Height(24f)))
            {
                int id = GetCaptureId(labelKey);
                _capturing = _capturing == id ? 0 : id;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (_capturing != GetCaptureId(labelKey))
            {
                return;
            }
            // 等按键时**不能让 UMM 关窗口**：ESC 既是"取消捕获"，
            // 又可能是玩家想绑的键，两者会同时发生——玩家按 ESC 想绑，
            // 窗口却先关了，一来一回永远绑不上 ESC。
            // 办法：捕获期间独占 ESC（吃掉它），窗口就关不掉；
            // 想退出捕获就点那个按钮。这是有意的取舍。
            if (Event.current.type == EventType.KeyDown
                && Event.current.keyCode == KeyCode.Escape)
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

        /// <summary>正在等按键的那一行（0 = 没有）。</summary>
        private static int _capturing;

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

        /// <summary>一段说明文字（缩进到标签列右边，与上面的控件对齐）。
        ///
        /// 不给定死宽度：之前写的是 Width(LabelW+300f)，UMM 窗口宽度随
        /// 玩家窗口变，一旦窄于那个值，右边的话就被**截断**而不是换行——
        /// 玩家看到半句话就断了。ExpandWidth 让它跟着窗口宽度走。
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
            GUILayout.Label(text, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 分组标题。加粗 + 上下留白，跟下面的控件行拉开层次。
        ///
        /// 不加粗的话十几行文字一样重，看不出哪里换了一块——这是 IMGUI
        /// 页面最基本的分组手段。Spectre 也用 fontStyle=Bold 的 GUIStyle
        /// （见它的 FeaturesBase.OnGUI）。
        /// </summary>
        internal static void Group(string labelKey)
        {
            GUILayout.Space(8f);
            GUILayout.Label(Loc.T(labelKey), BoldLabel());
            GUILayout.Space(2f);
        }

        /// <summary>一整行按钮。</summary>
        internal static void Button(string labelKey, Action onClick, bool wide)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T(labelKey),
                    GUILayout.Height(28f), GUILayout.Width(wide ? 260f : 140f)))
            {
                onClick();
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 文本框的控件名。回车提交靠它认焦点。
        ///
        /// **刻意不含点号。** LocCheck.CollectUsedKeys() 用正则
        /// `"([a-z][a-z0-9]*\.[a-z0-9_.]+)"` 扫页面源码收集文案键，
        /// 点号接小写单词长得和真正的文案键一模一样：这里要是写成
        /// 模块名加句点，闸门就会把它当成一个漏翻的键报"两种语言都没有"。
        /// 而且它读的是**整个文件、连注释一起读**，所以连在注释里举例
        /// 都不行。控件名不需要点号，不点就两不误伤。
        /// </summary>
        private const string RowInputControlName = "OutDirBox";

        /// <summary>
        /// 文本输入行。**改动先落到草稿，回车才提交。**
        ///
        /// 原先每次按键都直接提交，一个字符一次 `ConfigStore.Save()` +
        /// `PatchLoader.Refresh()`。看着只是"多个循环"，实际后果有三个：
        ///   · 玩家想删掉整个路径（Ctrl+A 然后退格）时，会先存进一堆
        ///     中间态，其中某一个还可能被立刻用掉（比如提交后
        ///     `VideoDir` 立刻变成一个不存在的目录）；
        ///   · 每存一次都写一次 `_dirty`，磁盘写入被按键频率放大；
        ///   · 「按什么键改什么键」和文本框共存时，中间态会漏进
        ///     `KeyboardSimulation` 的按键判定。
        ///
        /// 现在草稿和已提交值分开：只有**值真的变了**才提交，
        /// 而 IMGUI 每帧跑两趟，所以 `_textDraft != now` 这个条件本身
        /// 已经保证只在玩家敲键盘时成立一次。
        /// </summary>
        internal static void RowInput(string labelKey)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), GUILayout.Width(LabelW));
            // SetNextControlName + GetNameOfFocusedControl 是 Unity 自己
            // 留下的成对接口：前者给**即将画的下一个控件**起名，后者问
            // 焦点在谁身上。不用它就得自己维护 control id，而 IMGUI 一帧
            // 跑两趟、id 又取决于前后控件，自行推断很容易在 Repaint 那趟
            // 失焦——那正好是玩家看不见的一趟，症状是"偶尔按回车没反应"。
            GUI.SetNextControlName(RowInputControlName);
            _textDraft = GUILayout.TextField(_textDraft ?? "",
                GUILayout.Width(380f));
            bool enter = Event.current != null
                && Event.current.type == EventType.KeyDown
                && Event.current.keyCode == KeyCode.Return
                && GUI.GetNameOfFocusedControl() == RowInputControlName;
            GUILayout.EndHorizontal();

            if (enter)
            {
                CommitText();
                GUI.FocusControl(null);
            }
        }

        /// <summary>提交文本框内容。关窗口时也会调。</summary>
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