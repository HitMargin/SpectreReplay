using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 设置界面的视觉工具箱（IMGUI 自绘）。
    ///
    /// **为什么要有这一层。** UMM 的默认皮肤是"白底灰框 + 系统字号"，
    /// 摆出来的效果就是用户说的那种毛坯房：所有文字一样重、所有控件一样平、
    /// 分组靠空行、状态靠一行裸文字、没有任何视觉层级。要让它"能看"，
    /// 缺的不是某个控件的样式，而是**一层统一的分组与配色**；
    /// 把颜色常量散在四个页面里手写，改一次配色就要改四个文件。
    ///
    /// **为什么全部自绘。** UnityModManager.UI 里 window/h1/h2/bold/button
    /// 是 public，可以直接用；但 Textures（图标贴图）与 settings/status/
    /// www/updates/tooltipBox 全是 private——引用它们等于把自己钉在
    /// 某个 UMM 版本的内部结构上。这里只用 UnityEngine 自带的
    /// Texture2D.whiteTexture 染色，配 Unicode 符号做图标（UMM 用的是
    /// OS 动态字体，这些符号与中文同源，不会缺字形）。
    ///
    /// **三条硬约束，全是踩过的坑：**
    ///   1. Begin/End 必须配对。UMM 对模组 OnGUI 的异常处理是
    ///      LogException + ShowModSettings=-1 + GUIUtility.ExitGUI()，
    ///      等于**整个设置面板当场消失**（UnityModManager.decompiled.cs:3886-3899）。
    ///      所以分组一律走 Section(Action) 这种带 try/finally 的形式，
    ///      不对外暴露裸的 Begin/End——异常顶多让这一页难看，不会让窗口没了。
    ///   2. GUI.color / GUI.backgroundColor 用完必须还原。它们影响的是
    ///      "之后画的每一个控件"，不还原会把 UMM 自己的表头与 Save 按钮
    ///      一起染色（而 UMM 在调用我们之前刚把两者设成 Color.white，
    ///      见 :3604-3607）。
    ///   3. 手画装饰只在 EventType.Repaint 那一趟做。IMGUI 一帧跑好几趟
    ///      （Layout/Repaint），在 Layout 趟画图等于白画，而 GetLastRect
    ///      在非 Repaint 趟返回的是无意义矩形。
    /// </summary>
    internal static class Gui
    {
        // ── 调色板（一处定义，四个页面共用）─────────────────────
        internal static readonly Color CardBg  = new Color(0.13f, 0.15f, 0.19f, 1f);
        internal static readonly Color HeadBg  = new Color(0.09f, 0.11f, 0.14f, 1f);
        internal static readonly Color LineCol = new Color(1f, 1f, 1f, 0.09f);
        internal static readonly Color Accent  = new Color(0.24f, 0.62f, 0.92f, 1f);
        internal static readonly Color TextCol = new Color(0.90f, 0.93f, 0.96f, 1f);
        internal static readonly Color DimCol  = new Color(0.60f, 0.66f, 0.74f, 1f);
        internal static readonly Color OkCol   = new Color(0.30f, 0.82f, 0.46f, 1f);
        internal static readonly Color WarnCol = new Color(0.96f, 0.72f, 0.22f, 1f);
        internal static readonly Color BadCol  = new Color(0.93f, 0.34f, 0.34f, 1f);
        internal static readonly Color PillOff = new Color(1f, 1f, 1f, 0.16f);
        internal static readonly Color BtnOff  = new Color(1f, 1f, 1f, 0.14f);
        // 按钮面。**必须是"比卡片亮"的实色**（同 FieldBg 那条教训）：
        // 圆角贴图自带颜色，不再靠 GUI.backgroundColor 染色，所以这里要直接给对。
        // 注意：GUI.backgroundColor 是**乘算**的，谁要是再对圆角按钮设它，
        // 颜色会被再乘一遍变暗——所以原来那几处染色点已改成用专门的样式。
        internal static readonly Color BtnFace  = new Color(0.20f, 0.23f, 0.29f, 1f);
        internal static readonly Color BtnHover = new Color(0.28f, 0.32f, 0.39f, 1f);
        // 分段/页签的"未选中"底色（比按钮面再暗一点，让选中态更跳）。
        // 用**不透明**色而不是半透明白：贴图把颜色烘进去了，半透明会在卡片上
        // 二次混色，同一组页签在深/浅卡片上看起来不一样。
        internal static readonly Color BtnQuiet = new Color(0.16f, 0.18f, 0.23f, 1f);
        // 输入框自绘用的四色。**为什么必须自绘**：UMM 的 PrepareGUI / ScaleGUI
        // 只重设 label / button / horizontalSlider / horizontalSliderThumb / toggle
        // （UnityModManager.decompiled.cs:3467-3472 与 :3508-3519），
        // **从不碰 GUI.skin.textField**——内建输入框会沿用 ADOFAI 自己的深色皮肤，
        // 在深色卡片上就是一条黑框。
        //
        // **底色必须比卡片更亮（2026-10-12 踩过一次）。** 第一版把 FieldBg 定成
        // (0.07,0.08,0.11)，比 CardBg (0.13,0.15,0.19) **还暗**——玩家看到的仍是
        // 一个黑洞，与修复前的原生黑框毫无区别，于是"黑框还在"被反馈回来。
        // 判据不是"颜色是多少"，而是**与卡片的明度差**：凹槽要比承载它的平面亮，
        // 才读得出"这里可以输入"。描边同理，0.18 太淡，视觉上等于没有边。
        internal static readonly Color FieldBg     = new Color(0.21f, 0.24f, 0.30f, 1f);
        internal static readonly Color FieldBorder = new Color(1f, 1f, 1f, 0.30f);
        internal static readonly Color FieldFocus  = new Color(0.35f, 0.68f, 0.95f, 1f);
        internal static readonly Color FieldSel    = new Color(0.24f, 0.62f, 0.92f, 0.45f);
        // 占位文案单独一色：DimCol (0.60,0.66,0.74) 在 (0.21,0.24,0.30) 上偏亮，
        // 会被误当成"已填好的真路径"。压暗到能读但不抢眼。
        internal static readonly Color FieldHint   = new Color(0.55f, 0.60f, 0.68f, 1f);
        // 自绘滑条：轨道凹槽 + 滑块。滑块要比轨道亮得多才抓得住眼睛。
        internal static readonly Color SliderTrack = new Color(0.14f, 0.16f, 0.21f, 1f);
        internal static readonly Color SliderThumb = new Color(0.88f, 0.91f, 0.95f, 1f);

        // ── hover 色板（滑条 / 开关 / 输入框）─────────────────────────
        // **为什么这三类控件也要 hover。** 按钮的 hover 是烘在贴图里的
        // （normal 与 hover 两张图），而滑条/开关/输入框是**纯自绘**的，
        // 没有任何样式帮它们做这件事——不主动判鼠标位置，鼠标移上去就毫无
        // 反应，整页只有按钮"活"着，读起来像坏了一半。
        //
        // 提亮幅度刻意与 BtnFace→BtnHover（约 +0.06~0.08 亮度）对齐：
        // 幅度不一致的话，同一页里会出现"按钮亮得很明显、开关几乎看不出来"，
        // 反而更像 bug。判据 ⑪ 钉的是"取用了 hover 判据与 hover 色"，
        // 不是某几个具体数字——数字可以调，形态不能丢。
        internal static readonly Color SliderTrackHover = new Color(0.20f, 0.23f, 0.29f, 1f);
        internal static readonly Color SliderFillHover  = new Color(0.36f, 0.72f, 1f, 1f);
        internal static readonly Color SliderThumbHover = new Color(1f, 1f, 1f, 1f);
        internal static readonly Color PillOffHover     = new Color(1f, 1f, 1f, 0.26f);
        // 输入框 hover 必须**另起名字**、不能改 FieldBg：判据 ⑤ 拿 FieldBg 与
        // CardBg 比 Rec.709 亮度（凹槽要比承载面亮），FieldBg 是非悬停的基准色，
        // 改它就等于把基准改掉了。
        internal static readonly Color FieldBgHover     = new Color(0.26f, 0.30f, 0.37f, 1f);
        internal static readonly Color FieldBorderHover = new Color(1f, 1f, 1f, 0.45f);

        // ── 悬浮说明（tooltip）调色板 ────────────────────────────
        // **底色必须接近不透明。** 说明框会盖在卡片与文字之上，半透明底会把
        // 下面的行文字透上来，两层字叠在一起比没有说明更难读。
        internal static readonly Color TipBg     = new Color(0.05f, 0.06f, 0.08f, 0.98f);
        internal static readonly Color TipBorder = new Color(0.35f, 0.68f, 0.95f, 0.95f);

        // ── 禁用态（总开关关掉时，它下面的子设置压暗）──────────────
        // **为什么不用 GUI.enabled = false。** 本页四个主要控件（开关/滑条/
        // 输入框/下拉）全是自绘的：它们自己 Fill 矩形、自己画文字，**根本不
        // 经过 IMGUI 的禁用态渲染**。GUI.enabled = false 的结果是"颜色照旧、
        // 但点不动"——比不置灰更迷惑，玩家会以为界面卡了。所以禁用态必须自己
        // 做一遍：压暗走颜色，拦截走各控件自己的守卫（它们是唯一知道"这一行
        // 该不该响应鼠标"的地方）。
        //
        // 这也是 Pages.cs 里那句「不禁用——本项目没有禁用态样式」的答案：
        // 以前确实没有，所以当时的取舍是干脆不禁用。现在有了。
        private static int _dimDepth;
        private static readonly Color DimTint = new Color(0.62f, 0.62f, 0.62f, 1f);

        /// <summary>当前是否处在禁用态里（控件据此决定要不要响应鼠标）。</summary>
        internal static bool IsDim { get { return _dimDepth > 0; } }

        /// <summary>
        /// 压暗系数。自绘控件的颜色都要先乘过它——它们各自 `GUI.color = c`
        /// 是**覆盖**而不是相乘，不主动乘的话整个禁用态对它们完全无效。
        /// </summary>
        internal static Color Tint { get { return _dimDepth > 0 ? DimTint : Color.white; } }

        /// <summary>
        /// 在禁用态里画一段内容：视觉压暗 + 内部控件不再响应。
        ///
        /// 压暗用 <c>GUI.color</c> 而不是逐控件改色：它会被之后画的**每一个**
        /// 控件继承（含 GUILayout.Button 那类内建控件，它们的底色烘在贴图里，
        /// 没有别的地方可以调），而自绘控件因为会覆盖 GUI.color，另由
        /// <see cref="Tint"/> 显式补偿一次——两边各管一半，才不会一半灰一半亮。
        ///
        /// **嵌套不再叠加**（depth 从 0 变 1 时才设色）：两处嵌套各乘一次
        /// 0.62，第二层会黑到看不清。
        /// </summary>
        internal static void Dim(bool when, Action body)
        {
            if (!when)
            {
                body?.Invoke();
                return;
            }
            Color old = GUI.color;
            if (_dimDepth == 0)
            {
                GUI.color = DimTint;
            }
            _dimDepth++;
            try
            {
                body?.Invoke();
            }
            finally
            {
                _dimDepth--;
                GUI.color = old;
            }
        }

        /// <summary>标签列的固定宽度。不同语言下标签长短差很多，
        /// 自动排布会让控件列左右跳动，拖滑条时尤其明显。</summary>
        internal const float LabelW = 190f;
        /// <summary>控件列宽度。下拉/滑条/按键框统一用它，列才对得齐。</summary>
        internal const float CtrlW = 240f;

        // ── 样式缓存 ──────────────────────────────────────────
        // 必须缓存：GUIStyle 的构造涉及字体尺寸计算，每帧每个控件新建一个
        // 会明显掉帧（这一页有几十个控件）。缓存失效的判据是"皮肤或字体
        // 换了"——UMM 的 ScaleGUI 会换掉 GUI.skin.font（玩家在 UMM 设置里
        // 改字号/字体），不重建就会一直用旧字号。
        private static GUISkin _skin;
        private static Font _font;
        private static GUIStyle _label, _labelC, _bold, _boldC, _h1, _h2, _small, _smallC, _note, _pad, _btn, _btnPrimary, _btnQuiet, _btnAccent, _tipStyle;

        private static void Ensure()
        {
            if (_label != null && _skin == GUI.skin && _font == GUI.skin.font)
            {
                return;
            }
            _skin = GUI.skin;
            _font = GUI.skin.font;
            _label    = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft };
            _labelC   = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            _bold     = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
            _boldC    = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _h1       = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, fontSize = 19 };
            _h2       = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, fontSize = 13 };
            _small    = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft,  fontSize = 11 };
            _smallC   = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 11, fontStyle = FontStyle.Bold };
            // 说明文字要**换行**：UMM 把 GUI.skin.label.clipping 改成了 Overflow
            // （decompiled :3509），照抄 Skin.label 的话长句会一路画出窗口外
            // 而不是折行——玩家看到半句话被硬生生截断。
            _note     = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = true, clipping = TextClipping.Clip };
            _pad      = new GUIStyle { padding = new RectOffset(12, 12, 8, 8), margin = new RectOffset(0, 0, 0, 0) };
            // 卡片底色**必须由贴图自带**，绝不能用 GUI.backgroundColor 去染一张白底：
            // 它是**乘算**的，会乘到之后画的每个控件背景上——卡片里那些圆角按钮的
            // 底色是烘在贴图里的，再乘一遍就把 hover 与 normal 的色差从 20/23/26
            // 压成 3/3/5（≈1%，肉眼不可辨）。2026-10-16 用户报「打开编辑器按钮
            // hover 没颜色变化」正是它。判据 ⑨ / MUT-V12 守这里。
            _pad.normal.background = RoundedTex.Get(2, 2, 0, CardBg);
            _btn      = MakeButton(GUI.skin.button, BtnFace, BtnHover, TextCol, false);
            _btnPrimary = MakeButton(GUI.skin.button, Accent, Lighten(Accent, 0.15f), Color.white, true);
            _btnQuiet = MakeButton(GUI.skin.button, BtnQuiet, Lighten(BtnQuiet, 0.10f), DimCol, false);
            _btnAccent = MakeButton(GUI.skin.button, Accent, Lighten(Accent, 0.15f), Color.white, true);
            // 说明框正文。**必须 wordWrap**（长句不折行会一路画出框外），
            // 且字号与正文一致——说明文字比正文小一档时，在 1080p 下
            // 中文字形会糊成一团。
            _tipStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                clipping = TextClipping.Clip,
                fontSize = GUI.skin.label.fontSize,
            };
        }

        /// <summary>
        /// 圆角按钮样式。**这是"看起来还是原生控件"的正解**：
        /// UMM 的 ScaleGUI 只改 button 的 padding / fixedHeight，**从不换贴图**，
        /// 所以原生按钮永远是 Unity 那套方角灰底。圆角只能由贴图本身带
        /// （见 <see cref="RoundedTex"/>）。
        ///
        /// **9-slice 是必须的**：贴图只有 64×64，而按钮宽度可达 220px。
        /// 不设 <c>border</c> 的话 Unity 会整体拉伸，圆角被横向拉成椭圆——
        /// 看着像两头尖的胶囊，比不改还难看。设了 border 之后只拉伸中间段。
        /// border 取 radius+1，让"圆角那几像素"完整落在边角区内。
        /// </summary>
        private static GUIStyle MakeButton(GUIStyle basis, Color face, Color hover, Color text, bool bold)
        {
            const int Size = 64, Radius = 8;
            var st = new GUIStyle(basis)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                // 让文字与圆角之间留出呼吸，否则贴边看起来像被裁掉
                padding = new RectOffset(10, 10, 4, 4),
            };
            st.border = new RectOffset(Radius + 1, Radius + 1, Radius + 1, Radius + 1);
            // **不要在这里设 margin 去补行距。** 圆角按钮竖向紧贴确实会咬边
            // （2026-10-17 用户截图报「按钮的布局似乎融合到了一起」），但
            // margin 是**布局**尺寸：UMM 的 ScaleGUI 会给按钮定 fixedHeight，
            // 两者叠加时按钮实际高度会变，页签与行高一起漂。行距统一由
            // OnGui.RowGap 在行助手那一层补。

            st.normal.background = RoundedTex.Get(Size, Size, Radius, face);
            st.normal.textColor = text;
            st.hover.background = RoundedTex.Get(Size, Size, Radius, hover);
            st.hover.textColor = text;
            st.active.background = RoundedTex.Get(Size, Size, Radius, Darken(face, 0.12f));
            st.active.textColor = text;
            st.focused.background = st.hover.background;
            st.focused.textColor = text;
            // onNormal 是"选中态"（SelectionGrid / Toggle 用）。这里用不到，
            // 但按钮若被当成开关用会读它——统一指到 hover，免得漏出原生灰底。
            st.onNormal.background = st.hover.background;
            st.onNormal.textColor = text;
            return st;
        }

        private static Color Lighten(Color c, float k)
            => new Color(Mathf.Min(1f, c.r + k), Mathf.Min(1f, c.g + k), Mathf.Min(1f, c.b + k), c.a);

        private static Color Darken(Color c, float k)
            => new Color(Mathf.Max(0f, c.r - k), Mathf.Max(0f, c.g - k), Mathf.Max(0f, c.b - k), c.a);

        // 样式对外只读出口。OnGui / Pages 拿它画自己的行，而不是各自 new GUIStyle
        // ——重复构造既费时，也会绕过 Ensure 的缓存判据（UMM 的 ScaleGUI 换字体后
        // 那些自建样式不会跟着更新字号）。
        internal static GUIStyle LabelStyle { get { Ensure(); return _label; } }
        internal static GUIStyle BoldStyle { get { Ensure(); return _bold; } }
        internal static GUIStyle H2Style { get { Ensure(); return _h2; } }
        internal static GUIStyle NoteStyle { get { Ensure(); return _note; } }
        internal static GUIStyle SmallStyle { get { Ensure(); return _small; } }
        internal static GUIStyle ButtonStyle { get { Ensure(); return _btn; } }
        internal static GUIStyle PrimaryButtonStyle { get { Ensure(); return _btnPrimary; } }
        /// <summary>压暗的按钮（页签未选中 / 下拉列表项）。</summary>
        internal static GUIStyle QuietButtonStyle { get { Ensure(); return _btnQuiet; } }
        /// <summary>强调色按钮（页签选中 / 正在等按键）。</summary>
        internal static GUIStyle AccentButtonStyle { get { Ensure(); return _btnAccent; } }

        // ── 原子绘制 ──────────────────────────────────────────

        /// <summary>
        /// 鼠标是否悬停在 <paramref name="r"/> 上。
        ///
        /// **这是滑条 / 开关 / 输入框能做到 hover 变化的唯一来源。** 按钮的
        /// hover 来自样式里烘好的两张贴图（normal / hover），那三个控件是纯自绘的，
        /// 没有任何机制替它们判定鼠标位置。判据 ⑪ 认的就是"用没用这个函数"。
        ///
        /// **不要在这里碰 <c>GUIUtility.GetControlID</c>**：它按调用顺序分配
        /// 控件 id，而这个函数会被多个控件在 Layout/Repaint 两趟各调一次，
        /// 插进来会把别人的 id 全带偏（下拉错位那类毛病的温床）。
        /// 需要"拖动中也算悬停"的控件（滑条）自己比较 hotControl。
        /// </summary>
        internal static bool Hover(Rect r)
            => Event.current != null && r.Contains(Event.current.mousePosition);

        /// <summary>纯色填充。只在 Repaint 趟真的画。</summary>
        internal static void Fill(Rect r, Color c)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }
            Color old = GUI.color;
            // **必须显式乘 Tint**：下面这行是"覆盖"GUI.color，Dim() 设进去的
            // 压暗色会被整个盖掉，于是禁用态里自绘控件颜色照旧、只有内建控件
            // 变灰——一半灰一半亮比不置灰更像坏了。
            GUI.color = c * Tint;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>四边描框（厚度 1px）。</summary>
        internal static void Frame(Rect r, Color c)
        {
            Fill(new Rect(r.x, r.y, r.width, 1f), c);
            Fill(new Rect(r.x, r.y + r.height - 1f, r.width, 1f), c);
            Fill(new Rect(r.x, r.y, 1f, r.height), c);
            Fill(new Rect(r.x + r.width - 1f, r.y, 1f, r.height), c);
        }

        /// <summary>画文字并临时染色。用 GUI.color 而不是改样式本身：
        /// Label 的最终颜色是 style.normal.textColor 与 GUI.color 的乘积，
        /// 所以同一个样式可以画不同颜色，不必为每种颜色建一个 GUIStyle。</summary>
        internal static void Text(Rect r, string s, Color c, GUIStyle st)
        {
            Color old = GUI.color;
            GUI.color = c * Tint;
            GUI.Label(r, s, st);
            GUI.color = old;
        }

        /// <summary>一条 1px 分隔线，占一整行的布局高度。</summary>
        internal static void Divider()
        {
            Rect r = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            Fill(r, LineCol);
        }

        // ── 分组卡片 ──────────────────────────────────────────

        /// <summary>
        /// 一个分组：标题条 + 卡片体。**必用这个形式，不要手写 Begin/End。**
        ///
        /// 卡片底色由 <c>_pad</c> 的**卡色贴图**自带（见 Ensure 里那条注释），
        /// **不再用 <c>GUI.backgroundColor = CardBg</c>**：那是乘算，会把卡片里
        /// 圆角按钮烘好的底色再乘一遍，直接抹平 hover 与 normal 的色差
        /// （2026-10-16「打开编辑器按钮 hover 没颜色变化」的根因）。
        ///
        /// 历史上这里确实靠 backgroundColor 染色，且注释记过"要在 EndVertical
        /// 之后才还原"以避免白卡片——换成贴图后这个时序问题**整类消失**：
        /// 底色是样式自带的，与取色发生在哪一趟无关。
        /// </summary>
        internal static void Section(string titleKey, Color accent, Action body, bool enabled = true)
        {
            Ensure();
            GUILayout.Space(8f);
            Rect head = GUILayoutUtility.GetRect(1f, 24f, GUILayout.ExpandWidth(true));
            Fill(head, HeadBg);
            Fill(new Rect(head.x, head.y + head.height - 1f, head.width, 1f), LineCol);
            Fill(new Rect(head.x, head.y, 3f, head.height), accent);
            Text(new Rect(head.x + 11f, head.y, head.width - 18f, head.height), Loc.T(titleKey), TextCol, _bold);

            // 卡片体的边框/左侧色条。**量法上的两个坑**（这条最难查：不报错，
            // 只是描边画错位置，而"偏一点"很难联想到是 IMGUI 语义）：
            //
            //  坑 1：GetLastRect() 返回的是"最后一个**控件**的布局矩形"，
            //        不是外层组的矩形，也不含组的 padding。原来的写法在
            //        EndVertical() 之后调它，拿到的是 body 里最后一个子控件——
            //        于是卡片边框画在那个子控件的位置上，卡片矮一截。
            //  坑 2：紧跟在 Begin* 之后调 GetLastRect() 会直接抛
            //        "You cannot call GetLast immediately after beginning a group"。
            //
            // 这里改成：进组前取一次全宽标尺记 y，body 跑完后再取一次。
            // 两次都在**已经被布局过**的位置上调用，语义确定。
            // 只取 y 不做额外 Space：GetRect 本身不吃布局高度（height=1 的标尺
            // 是原有代码就在用的手法）。
            float top = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true)).y;
            float bottom = float.NaN;

            GUILayout.BeginVertical(_pad);
            try
            {
                // **标题条不压暗、内容压暗。** 标题是分组的名字，玩家要能读清
                // "这一组叫什么"才能理解自己为什么调不了；把标题也灰掉就变成
                // 一整块死掉的区域，看不出层级。
                Dim(!enabled, body);

                // 这一行必须在 EndVertical 之前：此时整个组的高度已经定下来了。
                // 放在 End 之后读到的会是最后一个子控件（坑 1）。
                bottom = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true)).yMax;

                if (Event.current != null && Event.current.type == EventType.Repaint
                    && !float.IsNaN(bottom) && bottom - top > 4f)
                {
                    Rect card = new Rect(head.x, top, head.width, bottom - top);
                    Frame(card, LineCol);
                    Fill(new Rect(card.x, card.y, 3f, card.height), accent);
                }
            }
            finally
            {
                GUILayout.EndVertical();
            }
        }

        /// <summary>一行"标签 + 说明"，用于把关键信息摆成表格状。</summary>
        internal static void KeyValue(string labelKey, string value, Color valueColor)
        {
            Ensure();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), _label, GUILayout.Width(LabelW));
            Text(GUILayoutUtility.GetRect(1f, 20f, GUILayout.ExpandWidth(true)), value, valueColor, _label);
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 一行可以点的文字（回放列表里的文件名）。
        ///
        /// <para><b>为什么不用 <c>GUILayout.Button</c>。</b>这颗按钮的语义是
        /// 「展开详情」，给它一块按钮底色会让它和同一行的「回放」「删除」抢
        /// 视觉权重——那两颗才是行动。所以外观保持纯文字，只补两件事：
        /// ① 悬停时变强调色（全页一致性：凡可点必须有 hover 反馈）；
        /// ② 悬停说明（这一页每颗控件都要能自我解释）。</para>
        ///
        /// <para><b>为什么用 <c>GUI.Button</c> 而不是自己判 <c>Event.current</c>。</b>
        /// 点击的判定要处理 Layout/Repaint 两趟、焦点、以及 UMM 那层 ScrollView
        /// 的事件拦截。自己判等于重写一遍 IMGUI 的点击语义，且必然在某一趟里
        /// 重复触发（一个点击删两份回放那种）。这里让 GUI.Button 负责命中，
        /// 只是把它的外观换成文字。</para>
        /// </summary>
        internal static void ClickableLabel(string text, bool active, string tipKey, Action onClick)
        {
            Ensure();
            Rect r = GUILayoutUtility.GetRect(1f, 20f, GUILayout.ExpandWidth(true));
            bool hot = r.Contains(Event.current != null ? Event.current.mousePosition : new Vector2(-1f, -1f));
            Tip(r, tipKey);
            Color old = GUI.color;
            if (hot) GUI.color = Accent;
            // active = 这一条正在展开：用粗体标出，否则展开后看不出是哪一行。
            GUIStyle st = active ? _bold : _label;
            if (GUI.Button(r, text, st))
            {
                GUI.color = old;
                if (onClick != null) onClick();
                return;
            }
            GUI.color = old;
        }

        /// <summary>
        /// 窄标签版「标签 + 值」，给**两列并排**的信息面板用（回放文件详情）。
        ///
        /// <para>为什么需要两个版本：<see cref="LabelW"/>=190 是按设置页那一列
        /// 长标签（「允许覆盖关卡」这类）定的；信息面板里标签都很短（「命中」
        /// 「时长」），用 190 会让一列吃掉三分之一宽度，两列就放不下。</para>
        /// </summary>
        internal const float LabelWNarrow = 104f;
        internal static void KeyValueNarrow(string labelKey, string value, Color valueColor)
        {
            Ensure();
            GUILayout.Label(Loc.T(labelKey), _small, GUILayout.Width(LabelWNarrow));
            Text(GUILayoutUtility.GetRect(1f, 18f, GUILayout.ExpandWidth(true)),
                value ?? "", valueColor, _label);
        }

        /// <summary>一条进度条。total&lt;=0 时直接返回，避免 0/0 得到 NaN 宽度
        /// ——NaN 传给 GUI.DrawTexture 会抛 ArgumentException 打断整个 OnGUI，
        /// 而 UMM 对 OnGUI 异常的处理是"把整个设置面板收掉"。
        /// </summary>
        internal static void ProgressBar(float fill, string caption)
        {
            Ensure();
            Rect r = GUILayoutUtility.GetRect(1f, 18f, GUILayout.ExpandWidth(true));
            Fill(r, new Color(1f, 1f, 1f, 0.10f));
            float w = r.width * Mathf.Clamp01(fill);
            if (w > 0f)
            {
                Fill(new Rect(r.x, r.y, w, r.height), Accent);
            }
            Frame(r, LineCol);
            if (!string.IsNullOrEmpty(caption))
            {
                Text(r, caption, TextCol, _smallC);
            }
        }

        /// <summary>
        /// 自绘滑条。返回新值（与 <c>GUILayout.HorizontalSlider</c> 同语义）。
        ///
        /// **为什么不用原生 HorizontalSlider。** 它的轨道/滑块贴图来自
        /// <c>GUI.skin.horizontalSlider</c>，而 UMM 的 ScaleGUI 只改那个样式的
        /// <c>fixedHeight / border / padding / margin</c>，**从不换贴图**
        /// （UnityModManager.decompiled.cs:3511-3518）——所以它永远是 Unity 默认
        /// 那套圆头灰条，跟本页自绘的卡片/开关/输入框格格不入。这就是
        /// 「看起来还是原生控件」里最扎眼的一处。
        ///
        /// **交互必须自己处理**，不能只画不响应：原生控件那套 hotControl 语义
        /// （按下→抢占、拖动→跟随、松开→释放）要照搬，否则会出现"能看不能拖"
        /// 或"拖到一半跳出别的控件也跟着动"。
        ///
        /// 与原生版的一个行为差异：**点击轨道会直接跳到该位置**（原生也这样），
        /// 而不是只能拖滑块——轨道的命中区比滑块大得多，玩家直觉就是点哪到哪。
        /// </summary>
        internal static float Slider(Rect track, float value, float min, float max)
        {
            Ensure();
            if (Event.current == null || max <= min)
            {
                return value;
            }

            const float Thumb = 14f;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;
            // 轨道压成细条，滑块才是视觉主角；命中区仍用整块 track
            Rect bar = new Rect(track.x, track.y + (track.height - 6f) * 0.5f, track.width, 6f);
            float span = Mathf.Max(1f, bar.width - Thumb);
            float k = Mathf.Clamp01((value - min) / (max - min));

            // **禁用态：一个鼠标事件都不吃。**
            // 不能只靠"压暗"——画面上灰了但照着能拖动，玩家会以为"灰只是个
            // 错误提示"。这里连 e.Use() 都不做，让事件落空，滑块纹丝不动。
            // GetControlID 仍在上面取了：本来就没进 switch，不会抢占 hotControl。
            if (!IsDim)
            {
                switch (e.GetTypeForControl(id))
                {
                case EventType.MouseDown:
                    if (e.button == 0 && track.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        k = Mathf.Clamp01((e.mousePosition.x - bar.x - Thumb * 0.5f) / span);
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        k = Mathf.Clamp01((e.mousePosition.x - bar.x - Thumb * 0.5f) / span);
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
                }
            }

            if (e.type == EventType.Repaint)
            {
                // hover：鼠标在轨道上、或正拖着本控件，就整体提一档。
                // **拖动中也要保持**（hotControl == id）：拖的时候鼠标很容易
                // 偏离 6px 高的细轨道，只认 Contains 会让高亮途中熄灭，
                // 看起来像"拖丢了"。
                // **禁用态不许提亮**：灰着却还跟着鼠标发亮，等于告诉玩家"它能
                // 用"，而实际拖不动。
                bool hot = !IsDim && (Hover(track) || GUIUtility.hotControl == id);

                // 先还原一次：调用方（如 RowSlider）可能刚设过 GUI.color，
                // 留着它会把这里所有贴图整体染色。
                Color old = GUI.color;
                GUI.color = Tint;

                // 轨道与填充用**纯色 Fill**，不用圆角贴图。
                // 原因是拉伸：贴图只有 16px 宽，而填充条要撑到上百像素，
                // GUI.DrawTexture 会把左边那两个圆角横向拉成椭圆（"两头尖"）。
                // 而这条槽只有 6px 高，圆角本来就看不出来——不值得为它做 9-slice。
                // 真正需要圆角的是滑块：它是 1:1 绘制（32→14 等比缩小），不会被拉歪。
                Fill(bar, hot ? SliderTrackHover : SliderTrack);
                float fillW = span * k;
                if (fillW > 1f)
                {
                    Fill(new Rect(bar.x, bar.y, fillW, bar.height), hot ? SliderFillHover : Accent);
                }
                float tx = bar.x + span * k;
                GUI.DrawTexture(new Rect(tx, track.y + (track.height - Thumb) * 0.5f, Thumb, Thumb),
                    RoundedTex.Get(32, 32, (int)(Thumb * 0.5f), hot ? SliderThumbHover : SliderThumb));
                GUI.color = old;
            }

            return min + k * (max - min);
        }

        // ── 按键标签组（多选） ─────────────────────────────────

        /// <summary>
        /// 一排可切换的按键标签，用于「特殊按键过滤」这类"从固定集合里多选"的场景。
        ///
        /// **为什么不用 <see cref="Switch"/> 逐行摆**：那组候选有 22 + 40 个，
        /// 逐行摆会占满好几屏，而这只是个很少调的开关集合——玩家要的是
        /// "一眼看完、点谁谁亮"。
        ///
        /// **为什么每行自己 GetRect 而不算格子**：IMGUI 的布局是流式的，
        /// 手动算 x/y 会在窗口缩放时错位。这里每行取一个满宽矩形，再把它
        /// 均分成 n 份，行高固定——缩放时自动跟着走。
        ///
        /// **禁用态**（父分组灰掉时）：整块压暗且点不动。与 Switch 同理，
        /// 自绘 + GUIStyle.none 必须自己拦 <c>GUI.Button</c>。
        /// </summary>
        /// <param name="names">候选键名（同时是显示文本与存储值）。</param>
        /// <param name="chosen">已选集合；本方法就地增删。</param>
        /// <param name="columns">每行几个。</param>
        /// <param name="onChanged">任一标签状态变化后回调（用于落盘）。</param>
        /// <param name="tipKey">可选：悬停说明的**标签键**（该键 + ".tip" 是文案）。
        /// 传空 = 不登记说明——只在"这组标签自解释"时才允许，特殊按键那种
        /// "Print/SysReq 到底是哪个键"的场景必须传。</param>
        /// <param name="enabled">父级是否可用（同 <see cref="Section"/> 的末参：
        /// 为假时整组压暗且点不动）。**必须传**——录制总开关关掉时补丁根本没挂载，
        /// 这时还能勾格子就是"能点但不生效"。</param>
        internal static void KeyChips(string[] names, HashSet<string> chosen, int columns,
            Action onChanged, bool enabled, string tipKey = null)
        {
            Ensure();
            if (names == null || names.Length == 0 || chosen == null) return;
            if (columns < 1) columns = 1;

            const float rowH = 26f;
            const float gap = 4f;

            // 走 Section 同一套压暗机制（Dim）。**不能只用局部 enabled 变量**：
            // 本方法是纯自绘的，Fill/Text 各自把颜色乘 Tint 才有压暗效果；
            // 少了 Dim 就变成"颜色照旧、只是点不动"，玩家会以为界面卡了。
            Dim(!enabled, () =>
            {
                for (int i = 0; i < names.Length; i += columns)
                {
                    int cols = Math.Min(columns, names.Length - i);
                    Rect row = GUILayoutUtility.GetRect(1f, rowH, GUILayout.ExpandWidth(true));
                    float cellW = (row.width - gap * (cols - 1)) / cols;

                    for (int j = 0; j < cols; j++)
                    {
                        string key = names[i + j];
                        Rect cell = new Rect(row.x + j * (cellW + gap), row.y, cellW, rowH);
                        bool on = chosen.Contains(key);

                        // 先画底与框，再叠文字——顺序反了文字会被盖掉。
                        bool hot = !IsDim && Hover(cell);
                        // **选中态用 Accent 的浅一档，不是 Lighten(Accent)**：整组只画
                        // 一排小格子时，"选中"必须与"悬停"能被一眼区分。两者若都只是
                        // 提亮 Accent，鼠标扫过去的那一格会看起来像已选中。
                        Fill(cell, on ? (hot ? Lighten(Accent, 0.12f) : Accent)
                                    : (hot ? BtnHover : BtnFace));
                        Frame(cell, on ? new Color(0.55f, 0.82f, 1f, 0.9f)
                                    : new Color(1f, 1f, 1f, hot ? 0.34f : 0.22f));
                        Text(cell, key, on ? Color.white : TextCol, _smallC);

                        // 说明按**格**登记（不是整组一条）：玩家悬停的是具体哪个键，
                        // 一条整组说明回答不了"Print 和 SysReq 有什么区别"。
                        if (!string.IsNullOrEmpty(tipKey))
                            Tip(cell, tipKey);

                        // 透明热区压在最上层接管点击；禁用态直接不建热区。
                        if (!IsDim && GUI.Button(cell, GUIContent.none, GUIStyle.none))
                        {
                            if (!chosen.Remove(key)) chosen.Add(key);
                            onChanged?.Invoke();
                        }
                    }
                    GUILayout.Space(gap);
                }
            });
        }

        // ── 徽章 / 标签 ───────────────────────────────────────

        /// <summary>胶囊徽章：半透明底 + 同色描边 + 同色文字。</summary>
        internal static void Badge(Rect r, string s, Color c)
        {
            Fill(r, new Color(c.r, c.g, c.b, 0.16f));
            Frame(r, new Color(c.r, c.g, c.b, 0.55f));
            Text(r, s, c, _smallC);
        }

        /// <summary>品牌头：大标题 + 副标题 + 右侧徽章。</summary>
        internal static void Header(string title, string subtitle, string badge, Color badgeColor)
        {
            Ensure();
            Rect r = GUILayoutUtility.GetRect(1f, 46f, GUILayout.ExpandWidth(true));
            Fill(r, HeadBg);
            Fill(new Rect(r.x, r.y + r.height - 2f, r.width, 2f), Accent);
            Text(new Rect(r.x + 12f, r.y + 6f, r.width - 200f, 24f), title, TextCol, _h1);
            Text(new Rect(r.x + 13f, r.y + 28f, r.width - 200f, 16f), subtitle, DimCol, _small);
            if (!string.IsNullOrEmpty(badge))
            {
                Rect b = new Rect(r.x + r.width - 132f, r.y + 13f, 122f, 20f);
                if (badgeColor == Color.clear)
                {
                    badgeColor = OkCol;
                }
                Badge(b, badge, badgeColor);
            }
        }

        // ── 控件 ──────────────────────────────────────────────

        /// <summary>
        /// 一行开关。自绘 pill 而不是 GUILayout.Toggle：
        /// 默认 Toggle 是一个小方框 + 一段标签，在深色卡片上既小又不显眼，
        /// 而且它自带标签会让"标签列对齐"失效（标签要固定 190px）。
        /// </summary>
        internal static void Switch(string labelKey, bool value, Action<bool> onChange)
        {
            Ensure();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), _label, GUILayout.Width(LabelW));
            Rect track = GUILayoutUtility.GetRect(44f, 20f, GUILayout.Width(44f), GUILayout.Height(20f));
            // **禁用态：按钮点不动。** GUI.Button 自己会检查 GUI.enabled，但这里
            // 是自绘 + GUIStyle.none，**必须自己拦**——否则灰色的 pill 照样能点开。
            bool clicked = !IsDim && GUI.Button(track, GUIContent.none, GUIStyle.none);
            // 悬停区 = 标签列 + 开关本体。标签列宽固定（LabelW），所以不用
            // GetLastRect 就能反推出整行的矩形——而 GetLastRect 在本文件里是
            // 被判据 ③ 明令禁止的（它返回的是最后一个子控件，不是组的矩形）。
            // 禁用态不登记说明：说明在说"这个开关干什么"，而此刻它开着也点不动。
            if (!IsDim)
            {
                Tip(RowHit(track, LabelW), labelKey);
            }
            // hover：与按钮同一套语汇（悬停即提亮）。关闭态的 pill 底色是
            // 半透明白，在深色卡片上本来就淡，不给反馈会让人以为点不动。
            // 禁用态同样不提亮（理由见 Slider）。
            bool hot = !IsDim && Hover(track);
            Fill(track, value ? (hot ? Lighten(Accent, 0.10f) : Accent)
                            : (hot ? PillOffHover : PillOff));
            Frame(track, value ? new Color(0.55f, 0.82f, 1f, 0.9f)
                            : new Color(1f, 1f, 1f, hot ? 0.34f : 0.22f));
            float k = track.height - 6f;
            Rect knob = new Rect(
                value ? track.x + track.width - k - 3f : track.x + 3f,
                track.y + 3f, k, k);
            Fill(knob, value ? Color.white : new Color(0.75f, 0.78f, 0.82f, 1f));
            Text(track, Loc.T(value ? "ui.on" : "ui.off"), value ? Accent : DimCol, _smallC);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            // clicked 是本次点击事件，不是新值：早先写成 `clicked != value`
            // 时，value 已经是 true 就永远不成立——开关只能打开、关不掉。
            if (clicked)
            {
                onChange?.Invoke(!value);
            }
        }

        // ── 悬浮说明（tooltip） ─────────────────────────────────
        //
        // **为什么必须集中到一处画。** IMGUI 只能按调用顺序画，而说明框要盖在
        // 别的控件之上、又不能把布局撑开——所以控件只"登记"自己这一行在哪，
        // 真正的绘制留到整页画完（<see cref="DrawTooltip"/>）再一次性做。
        //
        // **为什么不复用 UMM 的 tooltipBox。** 那是 UnityModManager 的内部字段，
        // 私有且没有公开的绘制入口（本文件开头那条注释记的就是这件事）。
        private static string _tipKey;
        private static Rect _tipAnchor;

        /// <summary>每帧开头清一次：鼠标移开后说明必须消失，不能挂在上一帧的控件上。</summary>
        internal static void ResetTips()
        {
            _tipKey = null;
        }

        /// <summary>
        /// 把控件矩形向左扩出标签列，得到「整行」的命中区。
        ///
        /// **为什么要扩到标签上。** 玩家说「鼠标移到功能上」，而一行里真正写名字
        /// 的是左边那段标签——只认右边那颗小小的开关/滑条，等于要求玩家先猜
        /// 「哪块算这个功能」。扩出去之后指针落在名字上也会弹说明。
        ///
        /// **为什么不用 <c>GetLastRect</c> 去拿标签自己的矩形。** 判据 ③ 明令
        /// 禁止它（返回的是最后一个**控件**的矩形，紧跟 Begin* 还会抛异常），
        /// 而标签宽度是固定的 <see cref="LabelW"/>，向左推即可，不必回头去取。
        /// </summary>
        private static Rect RowHit(Rect ctrl, float labelW)
            => new Rect(ctrl.x - labelW, ctrl.y, labelW + ctrl.width, ctrl.height);

        /// <summary>
        /// 登记「鼠标悬停在 <paramref name="r"/> 上时显示 <paramref name="labelKey"/> 的说明」。
        ///
        /// 说明正文的键是 <c>labelKey + ".tip"</c>，另有专门的闸门判据保证每个
        /// 控件都写了两种语言——漏写时这里**什么都不弹**，而不是把
        /// "render.fps.tip" 这种原始键名糊到玩家脸上。
        ///
        /// 同一帧内后登记的赢：它画在更上层，视觉上就该由它解释。
        /// </summary>
        internal static void Tip(Rect r, string labelKey)
        {
            if (string.IsNullOrEmpty(labelKey) || Event.current == null)
            {
                return;
            }
            if (!r.Contains(Event.current.mousePosition))
            {
                return;
            }
            _tipKey = labelKey;
            _tipAnchor = r;
        }

        /// <summary>
        /// 当前真正可画的矩形（我们所在窗口/滚动视口，而不是整块屏幕）。
        ///
        /// **为什么不能用 <c>Screen.width/height</c>。** UMM 是用 <c>GUI.Window</c>
        /// 把模组 OnGUI 回调起来的（<c>UnityModManager.UI::OnGUI</c> 里
        /// <c>GUILayout.Window(0, mWindowRect, WindowFunction, …)</c>），控件的
        /// 矩形、<c>Event.current.mousePosition</c> 与它交给我们的坐标系**全是
        /// 窗口内的局部坐标**；而 UMM 又把整个模组页包在自己的一层 ScrollView
        /// 里（见 OnGui.cs 类注释第 3 条）。拿屏幕尺寸当边界 ⇒
        /// 「窗口 700 宽、屏幕 1920 宽」时 `x + W > 1920 - 8` 恒不成立，
        /// 越界分支等于不存在，说明框直接画出面板右缘被裁掉。
        ///
        /// <c>GUIClip.visibleRect</c> 是 Unity 自己维护的**当前裁剪矩形**，
        /// 正好就是"这一时刻允许画到哪里"——它随 Window 与 ScrollView 一起变，
        /// 不需要我们去读任何私有字段、也不用假定窗口宽度。UMM 内部画 tooltip
        /// 时比的是 <c>mWindowRect.width</c>（私有字段），我们能拿到的等价信息
        /// 就是它。
        ///
        /// **取不到就退回 Screen。** 反射失败只会让边界重新变成"屏幕"，
        /// 也就是退回本缺陷修复前的行为——难看，但不会把说明框画到窗口外面去，
        /// 更不会让整个面板消失（OnGUI 抛异常 = UMM 收掉面板）。
        /// </summary>
        private static Rect Viewport()
        {
            if (_guiClipProp == null && !_guiClipProbed)
            {
                _guiClipProbed = true;
                try
                {
                    // GUIClip 是 **internal** 类型（UnityEngine.dll 里 IsPublic=False），
                    // 所以只能按名字找类型 + 取非公开静态属性。
                    Type t = Type.GetType("UnityEngine.GUIClip, UnityEngine.IMGUIModule")
                            ?? Type.GetType("UnityEngine.GUIClip, UnityEngine");
                    if (t != null)
                    {
                        _guiClipProp = t.GetProperty("visibleRect",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                }
                catch
                {
                    _guiClipProp = null;
                }
            }
            if (_guiClipProp != null)
            {
                try
                {
                    object v = _guiClipProp.GetValue(null, null);
                    if (v is Rect r && r.width > 0f && r.height > 0f)
                    {
                        return r;
                    }
                }
                catch
                {
                    // 运行时取不到（理论上不会有）：下面的兜底照样能画。
                }
            }
            return new Rect(0f, 0f, Screen.width, Screen.height);
        }

        private static PropertyInfo _guiClipProp;
        private static bool _guiClipProbed;

        /// <summary>
        /// 画当前登记的说明框。**必须在整页画完之后调用**（见 <see cref="Tip"/>）。
        ///
        /// 三处容易踩的点：
        /// ① 只在 Repaint 趟画——Layout 趟画的东西会被丢掉，文字测量还会算两遍；
        /// ② 宽度写死、高度按内容算（<c>CalcHeight</c>）。反过来（高度写死）会让
        ///    长句被裁掉半句，而它恰好是本项目最容易被忽略的缺陷形态；
        /// ③ 贴边时翻到控件上方。悬停最后一行时说明框往下画会落到窗口外，
        ///    玩家看到的不是"说明没了"，而是"这一行没有说明"——两种现象分不开。
        /// </summary>
        internal static void DrawTooltip()
        {
            if (_tipKey == null || Event.current == null)
            {
                return;
            }
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            string key = _tipKey + ".tip";
            string text = Loc.T(key);
            // Loc.T 查不到时返回键名本身：那是"没写说明"，不是说明内容
            if (string.IsNullOrEmpty(text) || text == key)
            {
                return;
            }

            Ensure();
            // **边界必须是"我们所在的那个窗口"，不是 Screen。**
            // 这一段原先前后两处都拿 Screen.width / Screen.height 判越界，
            // 而我们的 OnGUI 是 UMM 用 GUI.Window 回调起来的：控件矩形与
            // Event.current.mousePosition 全都是**窗口内的局部坐标**。
            // 窗口宽约 700、屏幕宽 1920 ⇒ `x + W > Screen.width - 8` 永远
            // 不成立，"贴边翻折"整个分支等于不存在，框子直接画出面板右缘
            // 被窗口裁掉（2026-10-18 用户截图报的就是它）。
            // UMM 自己画 tooltip 时比的是 mWindowRect.width，也是这个道理。
            Rect view = Viewport();
            // **宽度仍由常量给上限**（跟着内容走会让长说明撑满整行），
            // 只是再夹到可视区内：窗口窄于 320+边距时，写死的宽度会顶出
            // 右缘——与"位置越界"是同一个坏结果，只是原因在宽度。
            const float W = 320f;
            float w = Mathf.Min(W, view.width - 16f);
            if (w < 80f)
            {
                return;   // 窄到放不下一句说明：宁可不弹，也不画一条挤烂的字
            }
            const float PadX = 10f;
            const float PadY = 8f;
            float inner = w - PadX * 2f;
            var content = new GUIContent(text);
            float h = _tipStyle.CalcHeight(content, inner) + PadY * 2f;

            float right = view.width - 8f;
            float x = _tipAnchor.x;
            if (x + w > right)
            {
                x = right - w;
            }
            if (x < 8f)
            {
                x = 8f;
            }

            float bottom = view.height - 8f;
            float below = _tipAnchor.yMax + 6f;
            float above = _tipAnchor.y - h - 6f;
            float y = below;
            if (y + h > bottom)
            {
                y = above;          // 先翻到控件上方
            }
            if (y + h > bottom)
            {
                y = bottom - h;     // 上方也放不下：贴底，整块仍在窗口内
            }
            if (y < 8f)
            {
                y = 8f;             // 最后夹到顶部：宁可盖住控件，也不画出窗口
            }

            Rect box = new Rect(x, y, w, h);
            // 底色近乎不透明：说明框盖在别的行文字上，半透明会让两层字叠起来
            Fill(box, TipBg);
            Frame(box, TipBorder);
            Text(new Rect(box.x + PadX, box.y + PadY, inner, h - PadY * 2f),
                text, TextCol, _tipStyle);
        }
    }
}
