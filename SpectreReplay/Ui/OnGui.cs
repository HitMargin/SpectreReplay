using System;
using System.Collections.Generic;
using System.IO;
using UnityFileDialog;
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
    /// 3. **绝对不要自己再包一层 ScrollView。** UMM 已经把整个 Mods 页（含模组
    ///    OnGUI 的调用点）包在它自己的滚动区里：BeginScrollView 在 :3738，我们的
    ///    OnGUI 在 :3891 被调用，EndScrollView 直到 :3904 才关。再嵌一层会让内层
    ///    视口高度塌成表头那么高，正文被整片裁掉，而且**不抛任何异常**——
    ///    UMM 的 Log.txt 里一条 Exception 都没有，现象只是「下面什么都没有」。
    ///    （这条注释原先写的是反的，说"模组 OnGUI 的调用点没有包"，据此加的那层
    ///    嵌套滚动就是 2026-10-11 那个"Options 正文不渲染"的直接原因。）
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

        // （原 _scroll 字段已删：不再自建 ScrollView，位置由 UMM 维护）

        /// <summary>输出目录输入框。**自绘**（见 <see cref="TextField"/>）——
        /// 内建的那个输入框是本页唯一没被 UMM 重设皮肤的控件，在深色卡片上
        /// 会显示成一条黑框（2026-10-12 用户截图报的就是这一行）。</summary>
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

        /// <summary>标签页固定宽度：页名长短不一，按内容伸缩会让整排跳动。</summary>
        private const float TabW = 132f;

        // ── UMM 回调 ────────────────────────────────────────────

        internal static void OnShow(ModEntry modEntry)
        {
            _visible = true;
            // 每次打开都重取一次：上一轮可能还留着玩家没提交完的编辑，
            // 而设置本身也可能被别处改过（载入配置、外部编辑 Configs.json）。
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
            if (!_outDirLoaded)
            {
                _outDir.Text = Settings.RenderOutputDir ?? "";
                _outDirLoaded = true;
            }

            _openSeq = 0;
            // 悬浮说明同理：控件在画的过程中只"登记"，鼠标移开必须当帧就清掉，
            // 否则说明框会挂在上一次悬停的那一行上（看着像卡住了）。
            Gui.ResetTips();
            // **不要再自己包一层 ScrollView。** UMM 已经把整个 Mods 页（含模组
            // OnGUI 的调用点）包在它自己的滚动区里：
            //   :3738  BeginScrollView(..., ExpandHeight(false))
            //   :3891  modEntries2[num3].OnGUI(...)   ← 我们在这里面
            //   :3904  EndScrollView()
            // 再嵌一层会让**内层视口高度塌成表头那么高**，正文被整片裁掉——
            // 现象是「Options 只剩标题和标签、下面什么都没有」，而且不报任何异常
            // （UMM 的 Log.txt 里连一条 Exception 都没有，这正是它难查的原因）。
            //
            // 旧注释曾断言「模组 OnGUI 的调用点没有包」——那是错的：当时只看到
            // :3738/:3950/:3988 三处 BeginScrollView，没往上核到 :3904 的 End
            // 在模组调用点**之后**。布局的 Begin/End 配对已由 UMM 自己负责，
            // 分组配对我们另有 Gui.Section / DrawPage 的 try/finally。
            DrawHeader();
            DrawPatchWarning();
            DrawTabs();
            GUILayout.Space(4f);
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
                // 选中页用强调色实底、未选中压暗——比只靠按钮自带的 hover
                // 更容易分辨"我在哪一页"。
                //
                // **不能再用 GUI.backgroundColor 染色。** 它是**乘算**的：
                // 圆角按钮的底色已经烘进贴图里，再乘一次会把颜色压暗成另一回事
                // （原先那两处染色就是这么写的，配上自绘贴图会双重变暗）。
                // 改成两个专门样式，颜色在贴图里定死。
                if (GUILayout.Button(Loc.T(TabKeys[i]),
                        on ? Gui.AccentButtonStyle : Gui.QuietButtonStyle,
                        GUILayout.Width(TabW), GUILayout.Height(28f)))
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
        /// 行与行之间的竖向间距。**每个行助手收尾时都要调它。**
        ///
        /// 为什么必须补：圆角按钮的贴图半径是 8，而按钮高只有 22~28。
        /// 两行紧贴时上一颗的下圆角与下一颗的上圆角正好咬在一起，中间
        /// 没有卡片底色透出来——一列按钮连成一根粗色条，看着像"一个被
        /// 弄坏的控件"而不是三个独立项（2026-10-17 用户截图报
        /// 「按钮的布局似乎融合到了一起」，指的就是它）。
        ///
        /// **为什么不设在 ButtonStyle.margin 里**（那是最省事的写法）：
        /// margin 是布局尺寸，而 UMM 的 ScaleGUI 会给按钮另外设
        /// fixedHeight；两者叠加时按钮实高不再等于各调用点写的 22/28，
        /// 页签与行高会一起漂。行距归布局、贴图归样式，两边互不干扰。
        ///
        /// 用「行尾补」而不是「行首补」：第一个行助手前面不会多出一条
        /// 空白，卡片顶部与标题条仍然贴着。最后一行多出这一档间距无害。
        /// </summary>
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
            Gui.Dim(!enabled, () => Gui.Switch(labelKey, value, onChange));
            RowGap();
        }

        /// <summary>一行滑条 + 右侧**可编辑**当前值。</summary>
        internal static void RowSlider(string labelKey, float value, float min, float max,
            Func<float, string> fmt, Action<float> onChange, bool enabled = true)
        {
            // **不拆成"薄壳 + Core"两个方法。** ⑬d/⑬f 两条判据都是按
            // 「行助手自己体内有没有 Tip( / RowGap(」判的——拆出 Core 之后
            // 那两样都跑进 Core 里，薄壳看着就是"没登记说明、没留行距"，
            // 判据当场变红。判据没写错，它守的正是"每行自己收尾"这个形状。
            // 所以门控走 Gui.Dim 包住**整段**，让 Tip/RowGap 仍在体内。
            Gui.Dim(!enabled, () =>
            {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), Gui.LabelStyle, GUILayout.Width(LabelW));
            // 自绘滑条：原生 HorizontalSlider 的贴图来自 GUI.skin.horizontalSlider，
            // 而 UMM 只改它的 fixedHeight/border/padding/margin、**从不换贴图**，
            // 所以它永远是 Unity 默认的圆头灰条（见 Gui.Slider 的说明）。
            Rect track = GUILayoutUtility.GetRect(Gui.CtrlW, 20f,
                GUILayout.Width(Gui.CtrlW), GUILayout.Height(20f));
            // 悬停说明覆盖「标签 + 滑条 + 数值框 + 单位」整行。玩家想被解释的是
            // 这一项设置，不是那条 6px 高的槽；只认槽的话等于要求他先猜准位置。
            // 右侧留 46+4 与下面单位区的宽度一致，改那边时别忘了这里。
            Gui.Tip(new Rect(track.x - LabelW, track.y,
                LabelW + track.width + FieldW + 50f, track.height), labelKey);
            float now = Gui.Slider(track, value, min, max);
            float shown = Mathf.Approximately(now, value) ? value : now;
            // 右侧数值框。**从只读 Label 改成可编辑输入框**（用户 2026-10-17 要求）：
            // 滑条拖到 1920 与 1921 在像素上分不出来，而帧率/偏移这类值往往
            // 玩家心里已有确数——只能拖是"看得见却打不进去"。
            //
            // **框里只放数字，单位画在框外**：fmt 产出的是 "1920 px" 这种带单位
            // 的串，直接当可编辑内容的话，玩家一进去就得先删单位才打得动，
            // 而且解析必然失败。拆开之后显示与编辑各归各的。
            Rect field = GUILayoutUtility.GetRect(FieldW, 20f,
                GUILayout.Width(FieldW), GUILayout.Height(20f));
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
                // 选项之间同样要留缝：贴在一起时三张圆角贴图的边角互相
                // 吃掉，整列融成一根粗色条——2026-10-17 截图里那两块
                // 粘连的，正是这里和相邻下拉的触发按钮。
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
            // 等待按键时换成强调色按钮：这是"现在轮到你按键"的唯一提示。
            // 同页签那处：用专门样式而不是 GUI.backgroundColor（乘算会双重变暗）。
            Rect hk = GUILayoutUtility.GetRect(Gui.CtrlW, 24f,
                GUILayout.Width(Gui.CtrlW), GUILayout.Height(24f));
            Gui.Tip(new Rect(hk.x - LabelW, hk.y, LabelW + hk.width, hk.height), labelKey);
            if (GUI.Button(hk, text,
                    waiting ? Gui.AccentButtonStyle : Gui.ButtonStyle))
            {
                int id = GetCaptureId(labelKey);
                _capturing = _capturing == id ? 0 : id;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            RowGap();

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
            // 同上：wide 用专门的强调样式，不再靠 GUI.backgroundColor 染色
            // （乘算会把贴图里烘好的底色再压一遍）。
            Rect bt = GUILayoutUtility.GetRect(wide ? 220f : 140f, 28f,
                GUILayout.Width(wide ? 220f : 140f), GUILayout.Height(28f));
            Gui.Tip(bt, labelKey);
            bool hit = GUI.Button(bt, Loc.T(labelKey),
                wide ? Gui.PrimaryButtonStyle : Gui.ButtonStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            RowGap();
            if (hit)
            {
                onClick();
            }
        }

        /// <summary>
        /// 文本输入行。**改动先落到草稿，回车（或点别处）才提交。**
        ///
        /// 原先每次按键都直接提交：一个字符一次落盘。后果不只是"多个循环"
        /// ——玩家想整个删掉路径（Ctrl+A 再退格）时，中途每个中间态都会被
        /// 存下来并被立刻用掉（输出目录会瞬间变成不存在的目录）。
        ///
        /// **控件是自绘的**（<see cref="TextField"/>），不再用
        /// <c>GUILayout.TextField</c>：后者是本页唯一没被 UMM 重设皮肤的控件，
        /// 会沿用 ADOFAI 自己的深色皮肤，在深色卡片上显示成一条黑框
        /// （2026-10-12 用户截图报的正是这一行）。自绘之后焦点也由
        /// <see cref="TextField"/> 自己管，不再需要
        /// SetNextControlName / GetNameOfFocusedControl 那套 IMGUI 焦点机制
        /// ——它依赖前后控件决定 control id，在 Repaint 趟容易失焦。
        /// </summary>
        internal static void RowInput(string labelKey)
        {
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
            if (GUILayout.Button(Loc.T("record.changedir"), Gui.ButtonStyle,
                    GUILayout.Width(90f), GUILayout.Height(24f)))
            {
                _pickOutDirPending = true;
            }            GUILayout.FlexibleSpace();
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
