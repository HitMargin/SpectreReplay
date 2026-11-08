using System;
using System.Collections.Generic;
using System.IO;
using UnityFileDialog;
using UnityEngine;
using UnityModManagerNet;
using ModEntry = UnityModManagerNet.UnityModManager.ModEntry;

namespace SpectreReplay.Ui
{
    internal static class OnGui
    {
        /// <summary>UMM 选中本模组并显示设置时为真。画界面前置条件。</summary>
        private static bool _visible;

        /// <summary>当前页（0 起始）。必须存静态字段：见类注释第 4 条。</summary>
        private static int _tab;

        // （原 _scroll 字段已删：不再自建 ScrollView，位置由 UMM 维护）

        private static readonly TextField _outDir = new TextField();

        /// <summary>输入框是否已从设置载入过。首次打开面板前 OnShow 不一定跑过。</summary>
        private static bool _outDirLoaded;

        /// <summary>点击选目录后置位，等下一拍再弹选择框（同 Pages.DrawSaveDirRow）。</summary>
        private static bool _pickOutDirPending;

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
            "page.record", "page.playback", "page.render", "page.effects", "page.about",
        };

        /// <summary>
        /// 一级页签内部的长页分类。键全部复用现有文案，不再造第二套标题；
        /// 独立窗口只显示选中的组，业务页面仍然只画一遍。
        /// </summary>
        private static readonly string[][] SubTabKeys =
        {
            new[] { "subtab.record.basics", "record.hotkeys", "record.keybd", "record.specialkeys", "record.keymap" },
            new[] { "playback.current", "playback.files", "playback.cloud" },
            new[] { "subtab.render.start", "subtab.render.output", "render.codec", "convert.title" },
            // effects.presets 必须登记在这里：现代窗按「Section key == 页签 key」
            // 过滤分组（IsModernSectionVisible），漏登记 = 分组整块不可见。
            // 预设紧挨主开关——它配置的是整个去特效功能，不是某一类特效。
            new[] { "effects.master", "effects.presets", "effects.visual", "effects.decor", "effects.track",
                "effects.planet", "effects.audio", "effects.gameplay" },
            new[] { "subtab.about.info" },
        };

        private static readonly int[] _subSel = new int[5];

        /// <summary>标签页固定宽度：页名长短不一，按内容伸缩会让整排跳动。</summary>
        private const float TabW = 132f;

        /// <summary>重映射行中间箭头「→」的列宽：单个字形，固定即可，但宽度
        /// 纪律要求不写数字字面量，提出成常量。</summary>
        private const float ArrowW = 20f;

        // ── UMM 回调 ────────────────────────────────────────────

        internal static void OnShow(ModEntry modEntry)
        {
            _visible = true;
            _outDir.Text = Settings.RenderOutputDir ?? "";
            _outDir.Editing = false;
            _outDirLoaded = true;
            Loc.RefreshLanguage();
        }

        /// <summary>
        /// 独立浮层第一次打开时的初始化。与 <see cref="OnShow"/> 共用同一份草稿，
        /// 但不设置 UMM 的 `_visible`：独立窗口不要求 UMM 面板同时打开。
        /// </summary>
        internal static void RefreshForModernWindow()
        {
            _outDir.Text = Settings.RenderOutputDir ?? "";
            _outDir.Editing = false;
            _outDirLoaded = true;
            Loc.RefreshLanguage();
        }

        internal static void OnHide(ModEntry modEntry)
        {
            _visible = false;
            // 关窗口时提交文本框里还没回车的内容——窗口一关，玩家看不到任何
            // "未保存"的提示，不提交就等于白打。顺手落一次盘，与 Save 同语义。
            CommitText();
            // 拖动滑条时落盘被推迟了（见 Pages.Commit）。FlushConfig 统一收口，
            // 它会强制补上，否则松手前关窗会丢掉这次修改。
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
            DrawPanel(true);
        }

        /// <summary>
        /// UMM 自身打开设置页时的入口。
        ///
        /// 用户要的是独立 IMGUI 窗口。UMM 的老窗口仍可能被管理器打开；
        /// 这里把它改成一个明确入口，避免又显示旧布局，同时保留 UMM 的
        /// OnHide/OnSave 生命周期。入口只在从关闭变为打开时触发一次。
        /// </summary>
        internal static void OnUmmOpen(ModEntry modEntry)
        {
            if (!_visible)
            {
                return;
            }
            ModernSettingsWindow.OpenFromUmm();
        }

        /// <summary>
        /// 独立 IMGUI 浮层调用的同一套页面绘制入口。
        ///
        /// **不要再复制一份 DrawPage。** UMM 外框与游戏内独立浮层只是两个宿主：
        /// 页签、设置字段、热键捕获、延迟提交都必须走同一条路径，否则一处改了
        /// 另一处仍显示旧状态。调用方负责窗口底色、裁切和输入隔离。
        /// </summary>
        /// <summary>
        /// <paramref name="fixedSubTabs"/>：二级页签条由外层（独立窗口）画过时，
        /// 这里不再画——不然窗口层的滑动动画会把页签条一起平移+淡入，
        /// 玩家看到"切个二级页整个页签条都在动"。
        /// </summary>
        internal static void DrawPanel(bool includeTabs = true, bool fixedSubTabs = false)
        {
            Loc.RefreshLanguage();
            if (!_outDirLoaded)
            {
                _outDir.Text = Settings.RenderOutputDir ?? "";
                _outDirLoaded = true;
            }

            _openSeq = 0;
            // 悬浮说明同理：控件在画的过程中只"登记"，鼠标移开必须当帧就清掉，
            // 否则说明框会挂在上一次悬停的那一行上（看着像卡住了）。
            Gui.ResetTips();
            if (includeTabs)
            {
                DrawHeader();
                DrawPatchWarning();
                DrawTabs();
                GUILayout.Space(4f);
            }
            else
            {
                if (!fixedSubTabs)
                {
                    DrawPatchWarning();
                    int sub = Gui.SegmentTabs(
                        SubTabLabels(CurrentTab), CurrentSubTab, "subtab");
                    SetSubTab(sub);
                    GUILayout.Space(2f);
                }
            }
            try
            {
                Gui.ModernMode = !includeTabs;
                DrawPage();
                // 说明框必须在**整页画完之后**画：它要盖在别的控件之上，
                // 而且不能参与布局（否则会把下面的行往下顶，鼠标一动整页就跳）。
                Gui.DrawTooltip();
                GUILayout.Space(12f);
                // 拖动滑条时落盘被推迟（见 Pages.Commit）。放在**画完**之后：
                // 这时 Input.GetMouseButton(0) 反映的是本帧真实状态，松手那一帧
                // 会走到这里把攒下的改动一次写清。
                Pages.Pages.FlushPending();
            }
            finally
            {
                Gui.ModernMode = false;
            }
        }

        private static string[] SubTabLabels(int page)
        {
            string[] keys = SubTabKeys[Mathf.Clamp(page, 0, TabKeys.Length - 1)];
            var labels = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++) labels[i] = Loc.T(keys[i]);
            return labels;
        }

        // ── 头部 ────────────────────────────────────────────────

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

        /// <summary>补丁挂载失败数（独立窗口的页签条也要画同一份警告）。</summary>
        internal static int PatchFailuresPublic => PatchFailures;

        /// <summary>给外层窗口用的二级页签键表（按一级页索引取）。</summary>
        internal static string[] SubTabLabelsFor(int page)
            => SubTabLabels(page);

        // ── 分页 ────────────────────────────────────────────────

        internal static int TabCount => TabKeys.Length;

        internal static string TabLabel(int index)
            => index >= 0 && index < TabKeys.Length ? Loc.T(TabKeys[index]) : "";

        internal static int CurrentSubTab
            => Mathf.Clamp(_subSel[Mathf.Clamp(_tab, 0, TabKeys.Length - 1)], 0,
                SubTabCount - 1);

        internal static int SubTabCount
            => SubTabKeys[Mathf.Clamp(_tab, 0, TabKeys.Length - 1)].Length;

        internal static void SetSubTab(int index)
        {
            int page = Mathf.Clamp(_tab, 0, TabKeys.Length - 1);
            _subSel[page] = Mathf.Clamp(index, 0, SubTabKeys[page].Length - 1);
            _openRow = -1;
        }

        internal static bool IsModernSectionVisible(string key)
        {
            int page = Mathf.Clamp(_tab, 0, TabKeys.Length - 1);
            int sub = _subSel[page];
            if (page == 0)
            {
                if (sub == 0) return key == "page.record";
                if (sub == 1) return key == "record.hotkeys";
                if (sub == 2) return key == "record.keybd";
                if (sub == 3) return key == "record.specialkeys";
                if (sub == 4) return key == "record.keymap";
                return key == "page.record";
            }
            if (page == 1 || page == 3)
                return key == SubTabKeys[page][Mathf.Clamp(sub, 0, SubTabKeys[page].Length - 1)];
            if (page == 2)
            {
                if (sub == 0)
                    return key == "render.enable" || key == "render.start" || key == "render.cancel";
                if (sub == 1)
                    return key == "render.options";
                return key == SubTabKeys[page][Mathf.Clamp(sub, 0, SubTabKeys[page].Length - 1)];
            }
            return true;
        }

        internal static int CurrentTab
        {
            get => _tab;
            set
            {
                _tab = Mathf.Clamp(value, 0, TabKeys.Length - 1);
                _openRow = -1;
            }
        }

        internal static int CapturingTarget => _capturing;

        internal static string CurrentHotkey(int value)
            => value > 0 ? KeyName((KeyCode)value) : Loc.T("ui.nokey");

        private static void DrawTabs()
        {
            GUILayout.Space(8f);
            GUILayout.BeginHorizontal(Gui.QuietButtonStyle);
            for (int i = 0; i < TabKeys.Length; i++)
            {
                bool on = i == _tab;
                if (GUILayout.Button(Loc.T(TabKeys[i]),
                        on ? Gui.AccentButtonStyle : Gui.QuietButtonStyle,
                        GUILayout.Width(TabW), GUILayout.Height(26f)))
                {
                    _tab = i;
                    // 换页时收起展开的下拉：否则旧页的下拉会挂在新页上，
                    // 看起来像"点开了一个不属于这里的列表"。
                    _openRow = -1;
                }
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
                    case 3: Pages.Pages.DrawEffects(); break;
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
        /// <summary>
        /// 独立设置窗口控件列右对齐时，标签列实际可用宽度。
        /// 旧 UMM 面板保留 190px；独立窗口没有右边按钮列，可缩短，
        /// 但所有行仍走同一常量，语言一变也不会左右跳。
        /// </summary>
        private const float InlineLabelW = 156f;

        private static void RowGap()
        {
            GUILayout.Space(4f);
        }

        /// <summary>
        /// 一行开关。视觉走 Gui.Switch（自绘 pill）。
        ///
        /// <paramref name="enabled"/> 为 false 时整行压暗且不响应——用于
        /// 「总开关关掉后它下面的子设置」那种情形。**默认 true**，所以既有
        /// 调用点一行都不用改。
        /// </summary>
        internal static void Row(string labelKey, bool value, Action<bool> onChange,
            bool enabled = true)
        {
            if (Gui.ModernMode)
            {
                Gui.Dim(!enabled, () =>
                {
                    Gui.BeginModernRow();
                    GUILayout.BeginHorizontal();
                    GUILayout.BeginVertical();
                    DrawModernTextStack(labelKey);
                    GUILayout.EndVertical();
                    GUILayout.Space(12f);
                    Rect hitArea = GUILayoutUtility.GetRect(Gui.ModernSwitchW + 16f,
                        Gui.ModernSwitchH + 12f, GUILayout.Width(Gui.ModernSwitchW + 16f),
                        GUILayout.Height(Gui.ModernSwitchH + 12f));
                    bool hit = !Gui.IsDim && GUI.Button(hitArea, GUIContent.none, GUIStyle.none);
                    Rect track = new Rect(hitArea.x + 8f, hitArea.y + 6f,
                        Gui.ModernSwitchW, Gui.ModernSwitchH);
                    Gui.SwitchAt(labelKey, track, value, enabled);
                    if (hit) onChange?.Invoke(!value);
                    GUILayout.EndHorizontal();
                    Gui.EndModernRow();
                });
                return;
            }
            Gui.Dim(!enabled, () => Gui.Switch(labelKey, value, onChange));
            RowGap();
        }

        /// <summary>一行滑条 + 右侧**可编辑**当前值。</summary>
        internal static void RowSlider(string labelKey, float value, float min, float max,
            Func<float, string> fmt, Action<float> onChange, bool enabled = true)
        {
            if (Gui.ModernMode)
            {
                ModernSlider(labelKey, value, min, max, fmt, onChange, enabled);
                return;
            }
            Gui.Dim(!enabled, () =>
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T(labelKey), Gui.LabelStyle, GUILayout.Width(InlineLabelW));
                // 自绘滑条：原生 HorizontalSlider 的贴图来自 GUI.skin.horizontalSlider，
                // 而 UMM 只改它的 fixedHeight/border/padding/margin、**从不换贴图**，
                // 所以它永远是 Unity 默认的圆头灰条（见 Gui.Slider 的说明）。
                Rect track = GUILayoutUtility.GetRect(10f, 28f,
                    GUILayout.ExpandWidth(true), GUILayout.Height(28f));
                // 悬停说明覆盖「标签 + 滑条 + 数值框 + 单位」整行。玩家想被解释的是
                // 这一项设置，不是那条 6px 高的槽；只认槽的话等于要求他先猜准位置。
                // 右侧留 46+4 与下面单位区的宽度一致，改那边时别忘了这里。
                Gui.Tip(new Rect(track.x - InlineLabelW, track.y,
                    InlineLabelW + track.width + FieldW + 50f, track.height), labelKey);
                float now = Gui.Slider(track, value, min, max);
                float shown = Mathf.Approximately(now, value) ? value : now;
                Rect field = GUILayoutUtility.GetRect(FieldW, 28f,
                    GUILayout.Width(FieldW), GUILayout.Height(28f));
                string full = fmt(shown);
                string suffix = UnitOf(full);
                if (suffix.Length > 0)
                {
                    // 单位不占 GUILayout 宽度，直接画在框和 FlexibleSpace 之间的空地上
                    Gui.Text(new Rect(field.xMax + 4f, field.y, 46f, field.height),
                        suffix, Gui.DimCol, Gui.SmallStyle);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                // 与 RowInput 同理：自绘控件不参与 GUILayout 布局，画在组外。
                var st = GetField(labelKey);
                SyncField(st, NumberOf(full));
                bool canceled;
                bool commit = st.Draw(field, Gui.LabelStyle, "", out canceled);
                if (canceled)
                {
                    // Esc = 放弃本次编辑，还原成滑条当前值
                    st.Text = NumberOf(full);
                }
                else if (commit)
                {
                    if (TryParse(st.Text, out float typed))
                    {
                        // 夹进滑条自身的区间：输入框能写 99999，而滑条给不出那个值——
                        // 不夹的话下一帧滑条按越界值算 k，滑块会黏在端点上而数值显示
                        // 一个永远拖不回来的数。
                        float clamped = Mathf.Clamp(typed, min, max);
                        if (!Mathf.Approximately(clamped, value))
                        {
                            onChange(clamped);
                            return;   // 本帧以输入框为准，别再让滑条覆盖一次
                        }
                    }
                    st.Text = NumberOf(full);   // 解析失败：复位成当前值（自愈）
                }
                if (!Mathf.Approximately(now, value))
                {
                    onChange(now);
                }
            });
            RowGap();
        }

        private static void ModernSlider(string labelKey, float value, float min, float max,
            Func<float, string> fmt, Action<float> onChange, bool enabled)
        {
            Gui.Dim(!enabled, () =>
            {
                Gui.BeginModernRow();
                DrawModernTextStack(labelKey);
                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T(labelKey), Gui.ModernDescStyle,
                    GUILayout.Width(Gui.ModernLabelW), GUILayout.Height(28f));
                Rect track = GUILayoutUtility.GetRect(10f, 28f,
                    GUILayout.ExpandWidth(true), GUILayout.Height(28f));
                float now = Gui.Slider(track, value, min, max);
                Rect field = GUILayoutUtility.GetRect(Gui.ModernValueW, 28f,
                    GUILayout.Width(Gui.ModernValueW), GUILayout.Height(28f));
                string shown = fmt(Mathf.Approximately(now, value) ? value : now);
                TextField st = GetField(labelKey);
                SyncField(st, NumberOf(shown));
                bool canceled;
                bool commit = st.Draw(field, Gui.LabelStyle, "", out canceled);
                if (canceled) st.Text = NumberOf(shown);
                else if (commit && TryParse(st.Text, out float typed))
                {
                    onChange?.Invoke(Mathf.Clamp(typed, min, max));
                    return;
                }
                if (!Mathf.Approximately(now, value)) onChange?.Invoke(now);
                GUILayout.EndHorizontal();
                Gui.EndModernRow();
            });
        }

        private static void DrawModernTextStack(string labelKey)
        {
            string desc = Loc.T(labelKey + ".tip");
            if (string.IsNullOrEmpty(desc) || desc == labelKey + ".tip")
            {
                string note = Loc.T(NoteKeyFor(labelKey));
                desc = note == NoteKeyFor(labelKey) ? "" : note;
            }
            GUILayout.Label(Loc.T(labelKey), Gui.ModernTitleStyle,
                GUILayout.ExpandWidth(true));
            if (!string.IsNullOrEmpty(desc))
                GUILayout.Label(desc, Gui.ModernDescStyle, GUILayout.ExpandWidth(true));
        }

        private static string NoteKeyFor(string labelKey)
        {
            switch (labelKey)
            {
                case "page.record": return "record.howto";
                case "effects.on": return "effects.on.note";
                case "effects.enablesave": return "effects.enablesave.note";
                case "effects.removealldecor": return "effects.decor.note";
                case "effects.repeatevents": return "effects.gameplay.note";
                case "convert.target": return "convert.hint";
                case "about.notif.seconds": return "about.note";
                default: return "";
            }
        }

        /// <summary>数值框宽度。与原先只读 Label 的 90f 一致，列宽不变。</summary>
        private const float FieldW = 90f;

        /// <summary>
        /// 从 "1920 px" 里取出数字部分 "1920"。**按第一个空格切**而不是正则：
        /// 本项目所有数值格式都是 `数字 + 空格 + 单位` 一种形状（见各调用点），
        /// 没有负数带前导空格的情况；用正则反而多一层要维护的语法。
        /// </summary>
        private static string NumberOf(string formatted)
        {
            int i = formatted.IndexOf(' ');
            return i < 0 ? formatted : formatted.Substring(0, i);
        }

        /// <summary>从 "1920 px" 里取出单位部分 "px"；没有单位则返回空串。</summary>
        private static string UnitOf(string formatted)
        {
            int i = formatted.IndexOf(' ');
            return i < 0 ? "" : formatted.Substring(i + 1);
        }

        /// <summary>
        /// 数值输入框的解析。**必须宽进严出**：玩家会打 "60"、"60.5"、"-12"，
        /// 也可能打成乱码或留空；解析失败就**保持原值不动**（不回退到 0，
        /// 也不提交）——把一次手滑变成"帧率变成 0"是最糟的处理。
        /// 顺带收全角负号/小数点：中文输入法下这两个键很容易打成全角。
        /// </summary>
        private static bool TryParse(string s, out float v)
        {
            v = 0f;
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }
            s = s.Trim().Replace("－", "-").Replace("．", ".");
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v)
                && !float.IsNaN(v) && !float.IsInfinity(v);
        }

        /// <summary>
        /// 每行滑条一个持久输入框（按 labelKey 缓存）。
        ///
        /// **为什么不能用一个共享草稿**（RowInput 的 _outDir 是单实例）：
        /// 一页上有上十条滑条，共享草稿会在画到第二行时把第一行的内容冲掉。
        /// 键用 labelKey 而不是序号：序号会随页面增删控件整体漂移，
        /// 漂移之后"帧率"框里会显示"画质"上次留下的数。
        /// </summary>
        private static readonly Dictionary<string, TextField> _fields =
            new Dictionary<string, TextField>();

        private static TextField GetField(string key)
        {
            if (!_fields.TryGetValue(key, out TextField f))
            {
                f = new TextField();
                _fields[key] = f;
            }
            return f;
        }

        /// <summary>不在编辑时，把框里的文字跟当前值对齐（拖动滑条/载入设置后要跟上）。</summary>
        private static void SyncField(TextField f, string text)
        {
            if (!f.Editing && f.Text != text)
            {
                f.Text = text;
            }
        }

        internal static void RowDropdown(string labelKey, string[] options, int current,
            Action<int> onChange)
        {
            if (Gui.ModernMode)
            {
                Gui.BeginModernRow();
                DrawModernTextStack(labelKey);
                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                string modernShown = current >= 0 && current < options.Length ? options[current] : "—";
                if (GUILayout.Button(modernShown, Gui.ButtonStyle,
                        GUILayout.Width(Gui.ModernDropdownW), GUILayout.Height(28f)))
                    _openRow = _openRow == _openSeq ? -1 : _openSeq;
                int modernSeq = _openSeq++;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                if (_openRow == modernSeq)
                {
                    GUILayout.Space(4f);
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(6f);
                    GUILayout.BeginVertical(GUILayout.Width(Gui.ModernDropdownListW));
                    for (int i = 0; i < options.Length; i++)
                    {
                        if (i > 0) GUILayout.Space(4f);
                        if (GUILayout.Button(options[i], i == current
                                ? Gui.AccentButtonStyle : Gui.ButtonStyle,
                                GUILayout.Height(26f)))
                        {
                            _openRow = -1;
                            if (i != current) onChange(i);
                        }
                    }
                    GUILayout.EndVertical();
                    GUILayout.EndHorizontal();
                }
                Gui.EndModernRow();
                return;
            }
            int seq = _openSeq++;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), Gui.LabelStyle, GUILayout.Width(LabelW));
            string shown = current >= 0 && current < options.Length ? options[current] : "—";
            Rect dd = GUILayoutUtility.GetRect(Gui.CtrlW, 24f,
                GUILayout.Width(Gui.CtrlW), GUILayout.Height(24f));
            // 说明覆盖整行(标签 + 下拉框)。**不登记在展开的选项列表上**:
            // 玩家点开下拉时是在挑值，这时候弹说明正好把选项挡住。
            Gui.Tip(new Rect(dd.x - LabelW, dd.y, LabelW + dd.width, dd.height), labelKey);
            if (GUI.Button(dd, shown, Gui.ButtonStyle))
            {
                _openRow = _openRow == seq ? -1 : seq;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            // 展开时这一段同时把「触发按钮」与「选项列表」分开，否则它们
            // 也咬成一根，看不出哪个是当前值、哪几个是可选项。
            RowGap();

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
                if (i > 0)
                {
                    GUILayout.Space(4f);
                }
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
        ///
        /// <paramref name="enabled"/> 为 false 时整行压暗、不再接受点击，
        /// 也不再进入按键扫描——用于「上一项没开，这个快捷键就没有意义」那种
        /// 情形。**默认 true**，既有调用点一行都不用改。
        /// </summary>
        internal static void RowHotkey(string labelKey, Func<int> get, Action<int> set,
            bool enabled = true)
        {
            // 捕获 id 只取决于 labelKey，循环内每次重算是一样的值；
            // 提到闭包外面是为了让「画」和「扫键」两段读同一个 id。
            int id = GetCaptureId(labelKey);
            if (Gui.ModernMode)
            {
                Gui.Dim(!enabled, () =>
                {
                    Gui.BeginModernRow();
                    DrawModernTextStack(labelKey);
                    GUILayout.Space(6f);
                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    int cur = get();
                    bool waiting = _capturing == id;
                    string text = waiting ? Loc.T("ui.presskey")
                        : cur > 0 ? KeyName((KeyCode)cur) : Loc.T("ui.nokey");
                    Rect hk = GUILayoutUtility.GetRect(160f, 28f,
                        GUILayout.Width(Gui.ModernHotkeyW), GUILayout.Height(28f));
                    bool hit = GUI.Button(hk, text, waiting
                        ? Gui.AccentButtonStyle : Gui.ButtonStyle);
                    if (!enabled || Gui.IsDim) hit = false;
                    if (hit) _capturing = _capturing == id ? 0 : id;
                    GUILayout.EndHorizontal();
                    Gui.EndModernRow();
                });
                CaptureKey(id, enabled, set);
                return;
            }
            Gui.Dim(!enabled, () =>
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T(labelKey), Gui.LabelStyle, GUILayout.Width(LabelW));
                int cur = get();
                bool waiting = _capturing == id;
                string text = waiting
                    ? Loc.T("ui.presskey")
                    : (cur > 0 ? KeyName((KeyCode)cur) : Loc.T("ui.nokey"));
                // 等待按键时换成强调色按钮：这是"现在轮到你按键"的唯一提示。
                // 同页签那处：用专门样式而不是 GUI.backgroundColor（乘算会双重变暗）。
                Rect hk = GUILayoutUtility.GetRect(Gui.CtrlW, 24f,
                    GUILayout.Width(Gui.CtrlW), GUILayout.Height(24f));
                Gui.Tip(new Rect(hk.x - LabelW, hk.y, LabelW + hk.width, hk.height), labelKey);
                // **始终调 GUI.Button，只丢弃结果**，与 OnGui.Button 同一套理由：
                // 短路跳过会让置灰行整块消失，玩家看到的是空洞而不是灰按钮。
                bool hit = GUI.Button(hk, text,
                    waiting ? Gui.AccentButtonStyle : Gui.ButtonStyle);
                if (!enabled || Gui.IsDim)
                {
                    hit = false;
                }
                if (hit)
                {
                    _capturing = _capturing == id ? 0 : id;
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                RowGap();
            });

            CaptureKey(id, enabled, set);
        }

        private static void CaptureKey(int id, bool enabled, Action<int> set)
        {
            if (!enabled)
            {
                // 行在等待按键期间被关掉（总开关拨回去）：顺手退出等待态，
                // 否则它会继续占着 _capturing，下一次点别处才被顶掉。
                if (_capturing == id)
                {
                    _capturing = 0;
                }
                return;
            }
            if (Event.current == null || _capturing != id)
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
        /// 原生虚拟键码（VK）→ 可读名字。KeyCode 的枚举名在字母区是错的
        /// （<c>KeyCode.A</c> = 97 而 VK_A = 65），所以**不能**直接把 VK 强转成
        /// <see cref="KeyCode"/> 再取名字——那正是键位显示变成「按 A 亮 R」的成因。
        /// </summary>
        internal static string VkName(ushort vk)
        {
            if (vk == 0) return Loc.T("ui.keymap.none");
            switch (vk)
            {
                case 8: return "Backspace";
                case 9: return "Tab";
                case 13: return "Enter";
                case 16: return "Shift";
                case 17: return "Ctrl";
                case 18: return "Alt";
                case 19: return "Pause";
                case 20: return "CapsLock";
                case 27: return "Esc";
                case 32: return "Space";
                case 33: return "PageUp";
                case 34: return "PageDown";
                case 35: return "End";
                case 36: return "Home";
                case 37: return "←";
                case 38: return "↑";
                case 39: return "→";
                case 40: return "↓";
                case 44: return "PrintScreen";
                case 45: return "Insert";
                case 46: return "Delete";
                case 91: return "Win";
                case 92: return "Win";
                case 93: return "Menu";
                case 144: return "NumLock";
                case 145: return "ScrollLock";
                case 160: return "LShift";
                case 161: return "RShift";
                case 162: return "LCtrl";
                case 163: return "RCtrl";
                case 164: return "LAlt";
                case 165: return "RAlt";
                case 186: return ";";
                case 187: return "=";
                case 188: return ",";
                case 189: return "-";
                case 190: return ".";
                case 191: return "/";
                case 192: return "`";
                case 219: return "[";
                case 220: return "\\";
                case 221: return "]";
                case 222: return "'";
            }
            // 主键区字母/数字：VK 0x30-0x39 = '0'-'9'，0x41-0x5A = 'A'-'Z'
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            // 小键盘 0x60-0x69
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);
            // F1-F24：0x70-0x87
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            return "VK" + vk;
        }

        internal static void RowKeyRemap(ushort from, ushort to, bool capturing, bool capturingFrom,
            float slotW, Action<bool> onStartCapture, Action<ushort> onCapture, Action onClear, Action onDelete)
        {
            GUILayout.BeginHorizontal();
            bool waitingFrom = capturing && capturingFrom;
            bool waitingTo = capturing && !capturingFrom;
            // 槽位宽度由调用方按**全部可能出现的文案**实测取最大后传入
            // （对照表是两列格子，逐行各量各的会左右错位）。写死 74 时长键名
            // （VkName 会出「PrintScreen」）与等待按键的提示词都会超宽折行，
            // 两行文字塞进 24px 高的按钮就被拦腰裁掉。
            Rect f = GUILayoutUtility.GetRect(slotW, 24f, GUILayout.Width(slotW), GUILayout.Height(24f));
            if (GUI.Button(f, waitingFrom ? Loc.T("ui.presskey") : VkName(from),
                    waitingFrom ? Gui.AccentButtonStyle : Gui.ButtonStyle))
            {
                onStartCapture(true);
            }
            GUILayout.Label("→", Gui.LabelStyle, GUILayout.Width(ArrowW));
            Rect t = GUILayoutUtility.GetRect(slotW, 24f, GUILayout.Width(slotW), GUILayout.Height(24f));
            if (GUI.Button(t, waitingTo ? Loc.T("ui.presskey") : VkName(to),
                    waitingTo ? Gui.AccentButtonStyle : Gui.ButtonStyle))
            {
                onStartCapture(false);
            }
            // 「Delete」比「Clear」宽：写死 56px 时英文/西语下这个词超出内容区，
            // 按钮样式把词折成两行，22px 高的按钮装不下两行，字被拦腰裁断
            // （2026-10-30 用户截图）。宽度按文案实测，下限沿用原设计——
            // 与 Gui.ButtonW 注释里记的是同一课。
            string clearLabel = Loc.T("ui.keymap.clear");
            if (GUILayout.Button(clearLabel, Gui.QuietButtonStyle,
                    GUILayout.Width(Gui.ButtonW(clearLabel, Gui.QuietButtonStyle, 56f)), GUILayout.Height(22f)))
            {
                onClear();
            }
            string deleteLabel = Loc.T("ui.keymap.delete");
            if (GUILayout.Button(deleteLabel, Gui.QuietButtonStyle,
                    GUILayout.Width(Gui.ButtonW(deleteLabel, Gui.QuietButtonStyle, 56f)), GUILayout.Height(22f)))
            {
                onDelete();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            RowGap();

            if (!capturing || Event.current == null) return;
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                Event.current.Use();
                onCapture(0);   // Esc 取消：写回「未绑定」由调用方忽略
                return;
            }
            if (Event.current.type != EventType.Repaint) return;

            // **采样前必须再次确认还在等按键。** 上面那串按钮在 Layout 趟与
            // Repaint 趟都会跑，而 `capturing` 是调用方在按钮回调里改的：
            // 本趟已取消、状态还没落到下一次绘制时，这里会继续吃按键。
            // 用与录制侧完全相同的采样入口扫原生 VK。
            SpectreState.GetKeyboardState(_remapCurr);
            for (ushort k = 1; k < 256; k++)
            {
                if ((_remapPrev[k] & 0x80) == 0 && (_remapCurr[k] & 0x80) != 0)
                {
                    onCapture(k);
                    break;
                }
            }
            Array.Copy(_remapCurr, _remapPrev, 256);
        }

        private static readonly byte[] _remapPrev = new byte[256];
        private static readonly byte[] _remapCurr = new byte[256];

        /// <summary>开始等按键前先拍一张当前键盘状态，否则「已经按住的键」会被当成新按下。</summary>
        internal static void PrimeKeyRemapCapture()
        {
            SpectreState.GetKeyboardState(_remapPrev);
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

        /// <summary>带颜色的说明文字。警告类提示（如录键音的质量告诫）用
        /// <see cref="Gui.WarnCol"/>，与普通灰说明区分开。</summary>
        internal static void Note(string labelKey, Color color)
        {
            NoteText(Loc.T(labelKey), color);
        }

        /// <summary>一段不带标签键的说明文字（内容已经本地化过了）。</summary>
        internal static void NoteText(string text)
        {
            if (Gui.ModernMode)
            {
                GUILayout.Label(text ?? "", Gui.ModernDescStyle,
                    GUILayout.ExpandWidth(true));
                return;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Space(LabelW);
            GUILayout.Label(text, Gui.NoteStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        /// <summary>带颜色的说明文字。染色沿用 Gui.Text 的乘算约定
        /// （style.normal.textColor × GUI.color），不另建 GUIStyle。</summary>
        internal static void NoteText(string text, Color color)
        {
            if (Gui.ModernMode)
            {
                Color old = GUI.color;
                GUI.color = color * Gui.Tint;
                GUILayout.Label(text ?? "", Gui.ModernDescStyle,
                    GUILayout.ExpandWidth(true));
                GUI.color = old;
                return;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Space(LabelW);
            Color oldClassic = GUI.color;
            GUI.color = color * Gui.Tint;
            GUILayout.Label(text, Gui.NoteStyle, GUILayout.ExpandWidth(true));
            GUI.color = oldClassic;
            GUILayout.EndHorizontal();
        }

        /// <summary>一整行按钮。wide = 主要动作（开始渲染），用强调色底 + 加粗。</summary>
        /// <param name="enabled">
        /// false 时只画不响应（与 <see cref="Row(string,bool,Action{bool},bool)"/> 的语义对齐）。
        /// **必须显式拦，不能只靠外层 <see cref="Gui.Dim"/>**：
        /// Dim 只设 GUI.color 并抬高 depth，而 <c>GUI.Button</c> 是内建控件，
        /// 不看 depth（Gui 自己的自绘控件才有 <c>!IsDim &amp;&amp;</c> 那一下）。
        /// 于是外层压暗 + 这里不拦 = 按钮看着灰、点着照常生效。
        /// </param>
        internal static void Button(string labelKey, Action onClick, bool wide, bool enabled = true)
        {
            if (Gui.ModernMode)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(Gui.ModernRowPadX);
                string modernCaption = Loc.T(labelKey);
                float modernButtonW = Gui.ButtonW(modernCaption,
                    wide ? Gui.PrimaryButtonStyle : Gui.ButtonStyle, wide ? 220f : 140f);
                if (GUILayout.Button(modernCaption,
                        wide ? Gui.PrimaryButtonStyle : Gui.ButtonStyle,
                        GUILayout.Width(modernButtonW),
                        GUILayout.Height(wide ? 36f : 30f)) && enabled)
                    onClick?.Invoke();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(6f);
                return;
            }
            GUILayout.BeginHorizontal();
            // wide 用专门的强调样式：GUI.backgroundColor 是乘算，会把贴图里
            // 烘好的底色再压一遍。
            string caption = Loc.T(labelKey);
            GUIStyle st = wide ? Gui.PrimaryButtonStyle : Gui.ButtonStyle;
            // 宽度按文案实测，下限沿用原设计（wide 220 / 普通 140）：14 张语言表
            // 里总有比中文长的一门，写死就是下一颗被裁字的按钮。
            float bw = Gui.ButtonW(caption, st, wide ? 220f : 140f);
            Rect bt = GUILayoutUtility.GetRect(bw, 28f,
                GUILayout.Width(bw), GUILayout.Height(28f));
            Gui.Tip(bt, labelKey);
            // **始终调 GUI.Button，只丢弃结果**，不要短路跳过这次调用：
            // 那样禁用时按钮整块不画，玩家看到的是位置上的一个空洞，
            // 而不是「灰着的按钮」。绘制走 Repaint 趟，点中判定走 MouseUp 趟，
            // 两者由同一次调用内部分流，丢弃返回值正好只砍掉点击、保住外观。
            bool hit = GUI.Button(bt, caption, st);
            if (!enabled || Gui.IsDim)
            {
                hit = false;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            RowGap();
            if (hit)
            {
                onClick();
            }
        }

        internal static void RowInput(string labelKey)
        {
            if (Gui.ModernMode)
            {
                Gui.BeginModernRow();
                DrawModernTextStack(labelKey);
                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                Rect modernBox = GUILayoutUtility.GetRect(210f, 28f,
                    GUILayout.Width(Gui.ModernInputW), GUILayout.Height(28f));
                string modernChangeLabel = Loc.T("record.changedir");
                GUIStyle changeStyle = Gui.ButtonStyle;
                if (GUILayout.Button(modernChangeLabel, Gui.ButtonStyle,
                        GUILayout.Width(Gui.ButtonW(modernChangeLabel, changeStyle, 90f)),
                        GUILayout.Height(28f)))
                    _pickOutDirPending = true;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                bool canceledInput;
                bool commitInput = _outDir.Draw(modernBox, Gui.LabelStyle,
                    Loc.T("render.outdir.empty"), out canceledInput);
                if (canceledInput) _outDir.Text = Settings.RenderOutputDir ?? "";
                if (commitInput) CommitText();
                if (_pickOutDirPending)
                {
                    _pickOutDirPending = false;
                    try { MainThread.Defer(PickOutDirDeferred); }
                    catch (Exception ex) { Debug.LogWarning("[SpectreReplay] 无法启动输出目录选择框：" + ex.Message); }
                }
                Gui.EndModernRow();
                return;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), Gui.LabelStyle, GUILayout.Width(LabelW));
            // **宽度必须走 Gui.CtrlW，不能写死 360。** 页面上每一行控件列
            // 都是 LabelW + CtrlW，写死 360 会让这一行的控件比上面几行宽一截——
            // 视觉上"这一行歪了"，而且整页包在 ScrollView 里，窗口窄一点时
            // LabelW+360 先溢出（下拉宽 70px 才会露不出来）。
            Rect box = GUILayoutUtility.GetRect(Gui.CtrlW, 24f,
                GUILayout.Width(Gui.CtrlW), GUILayout.Height(24f));
            // 悬停说明只认输入框那一块，不并上右边的「更改…」按钮：
            // 那颗按钮有自己的说明，两个控件的解释挤在一起就分不清在说谁。
            Gui.Tip(box, labelKey);
            // 选目录按钮。**放在方框右边而不是另起一行**：截图里方框右侧本来
            // 就空着约 120px（后面跟的是 FlexibleSpace），按钮塞进那块空地，
            // 既不用加宽控件列、也不会让这一行比上下行宽一截。
            // 手打绝对路径对玩家是很糟的体验：路径要精确到盘符与每层目录名，
            // 打错一个字符就是「渲染失败」而界面不说是路径的问题。
            // 宽度按文案实测（下限 90）：写死会在长文案语言下裁字。
            string changeLabel = Loc.T("record.changedir");
            if (GUILayout.Button(changeLabel, Gui.ButtonStyle,
                    GUILayout.Width(Gui.ButtonW(changeLabel, Gui.ButtonStyle, 90f)), GUILayout.Height(24f)))
            {
                _pickOutDirPending = true;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            RowGap();

            // 画在 EndHorizontal 之后：本控件不参与 GUILayout 布局（矩形已经取好），
            // 放在水平组里画反而会和 UMM 自己的滚动区互相干扰。
            bool canceled;
            bool commit = _outDir.Draw(box, Gui.LabelStyle, Loc.T("render.outdir.empty"), out canceled);
            if (canceled)
            {
                // Esc = 放弃本次编辑，还原成设置里的值（不回写设置）
                _outDir.Text = Settings.RenderOutputDir ?? "";
            }
            if (commit)
            {
                CommitText();
            }

            if (!_pickOutDirPending)
            {
                return;
            }
            _pickOutDirPending = false;
            try
            {
                // 与保存目录同理：FileBrowser.PickFolder 是**阻塞式原生模态框**，
                // 在 IMGUI 的 Layout/Repaint 趟里直接弹会让事件队列配对错位。
                MainThread.Defer(PickOutDirDeferred);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 无法启动输出目录选择框：" + ex.Message);
            }
        }

        /// <summary>
        /// 在 OnGUI 之外跑原生选择框，选完写进 RenderOutputDir。
        ///
        /// **不留「取消就清空」的语义**：返回空串既可能表示玩家取消，也可能是
        /// 真的选不出目录；而 RenderOutputDir 留空本身是**有意义的默认值**
        /// （= SavePath\RenderVideos，见 VideoRenderJob.BuildOutputPath）。
        /// 分不清就会把「取消」当成「恢复默认」，把玩家已填的路径悄悄清掉。
        /// 所以：拿不到非空结果就什么都不做。
        /// </summary>
        private static void PickOutDirDeferred()
        {
            try
            {
                string start = string.IsNullOrEmpty(Settings.RenderOutputDir)
                    ? Settings.SavePath : Settings.RenderOutputDir;
                string dir = FileBrowser.PickFolder(start, null, null, Loc.T("render.outdir.pick"));
                if (string.IsNullOrEmpty(dir))
                {
                    return;   // 玩家取消：保持原样
                }
                Settings.RenderOutputDir = dir;
                Settings.CommitSafe();
                _outDir.Text = dir;
                _outDir.Editing = false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 选择输出目录失败：" + ex.Message);
            }
        }

        /// <summary>提交输入框内容。关窗口、点 Save、点别处、回车时都会调。</summary>
        internal static void CommitText()
        {
            string draft = _outDir.Text ?? "";
            if (draft != Settings.RenderOutputDir)
            {
                Settings.RenderOutputDir = draft;
                Settings.CommitSafe();
            }
        }
    }
}
