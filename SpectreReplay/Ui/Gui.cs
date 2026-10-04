using System;
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
        private static GUIStyle _label, _labelC, _bold, _boldC, _h1, _h2, _small, _smallC, _note, _pad, _btn, _btnPrimary;

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
            _pad.normal.background = Texture2D.whiteTexture;
            _btn      = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleCenter };
            _btnPrimary = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        }

        // ── 原子绘制 ──────────────────────────────────────────

        /// <summary>纯色填充。只在 Repaint 趟真的画。</summary>
        internal static void Fill(Rect r, Color c)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }
            Color old = GUI.color;
            GUI.color = c;
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
            GUI.color = c;
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
        /// 卡片底色靠 GUI.backgroundColor + 只带 padding 的样式实现：
        /// backgroundColor 在 EndVertical **之后**才还原，这样无论 Unity
        /// 是在 Begin 那一趟还是 End 那一趟取色，读到的都是卡片色——
        /// 只设一处的话会退成白色卡片，而现象是"只有内容没有底色"，
        /// 看起来像是没画，很难联想到是这个顺序的问题。
        /// </summary>
        internal static void Section(string titleKey, Color accent, Action body)
        {
            Ensure();
            GUILayout.Space(8f);
            Rect head = GUILayoutUtility.GetRect(1f, 24f, GUILayout.ExpandWidth(true));
            Fill(head, HeadBg);
            Fill(new Rect(head.x, head.y + head.height - 1f, head.width, 1f), LineCol);
            Fill(new Rect(head.x, head.y, 3f, head.height), accent);
            Text(new Rect(head.x + 11f, head.y, head.width - 18f, head.height), Loc.T(titleKey), TextCol, _bold);

            Color oldBg = GUI.backgroundColor;
            GUI.backgroundColor = CardBg;
            GUILayout.BeginVertical(_pad);
            try
            {
                body?.Invoke();
            }
            finally
            {
                GUILayout.EndVertical();
                GUI.backgroundColor = oldBg;
                // 描边画在卡片体边缘：card 有 12px 内边距，不会盖住内容。
                if (Event.current != null && Event.current.type == EventType.Repaint)
                {
                    Rect body2 = GUILayoutUtility.GetLastRect();
                    if (body2.width > 4f && body2.height > 4f)
                    {
                        Frame(body2, LineCol);
                        Fill(new Rect(body2.x, body2.y, 3f, body2.height), accent);
                    }
                }
            }
        }

        // ── 徽章 / 标签 ───────────────────────────────────────

        /// <summary>胶囊徽章：半透明底 + 同色描边 + 同色文字。</summary>
        internal static void Badge(Rect r, string s, Color c)
        {
            Fill(r, new Color(c.r, c.g, c.b, 0.16f));
            Frame(r, new Color(c.r, c.g, c.b, 0.55f));
            Text(r, s, c, _smallC);
        }

        /// <summary>状态点：一个 8x8 的实心方块（IMGUI 画不出圆，方块反而更清楚）。</summary>
        internal static void Dot(Rect r, Color c)
        {
            float s = Mathf.Min(8f, Mathf.Min(r.width, r.height));
            Fill(new Rect(r.x + (r.width - s) * 0.5f, r.y + (r.height - s) * 0.5f, s, s), c);
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
            bool clicked = GUI.Button(track, GUIContent.none, GUIStyle.none);
            Fill(track, value ? Accent : PillOff);
            Frame(track, value ? new Color(0.55f, 0.82f, 1f, 0.9f) : new Color(1f, 1f, 1f, 0.22f));
            float k = track.height - 6f;
            Rect knob = new Rect(
                value ? track.x + track.width - k - 3f : track.x + 3f,
                track.y + 3f, k, k);
            Fill(knob, value ? Color.white : new Color(0.75f, 0.78f, 0.82f, 1f));
            Text(track, value ? "开" : "关", value ? Accent : DimCol, _smallC);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (clicked && clicked != value)
            {
                onChange?.Invoke(clicked);
            }
        }
    }
}
