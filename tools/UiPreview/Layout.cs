// 把桩树解析成绝对矩形，再用 GDI+ 画成 PNG。
// 坐标系：左上角原点、y 向下——与 Gfx.Rect 的 left/top 语义一致。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using UnityEngine;
using UColor = UnityEngine.Color;
using UImage = UnityEngine.UI.Image;
using Theme = SpectreReplay.Ui.Theme;
using TMPro;
using UnityEngine.UI;

namespace UiPreview
{
    internal sealed class Node
    {
        internal GameObject Go;
        internal Node Parent;
        internal readonly List<Node> Kids = new List<Node>();
        internal float X, Y, W, H;          // 绝对矩形（y 向下，供绘制）
        internal float Px, Py;              // 同一矩形的 Unity 坐标（左下原点），供子节点推算
        internal float ParentRectTop;
        internal UColor Color = UColor.clear;
        internal bool Raycast;
        internal string Text;
        internal float FontSize = 14f;
        internal TextAlignmentOptions Align;
        internal bool IsImage, IsButton;
        /// <summary>挂在这个节点上的 Button 数量。命中它才算点得动。</summary>        internal int Buttons;
        /// <summary>RectMask2D：子树要按本节点的矩形裁剪。预览必须实现，
        /// 否则超出视口的内容会被画到面板外，半透明白底压着背景色混成一片
        /// 怪色，看着像配色坏了，其实是没有裁剪。</summary>
        internal bool Clips;
        internal bool Wrap;    // 折行文字：宽度超框是正常的，要看高度
        internal bool Cjk;

        private static bool HasCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char ch in s)
            {
                if (ch >= 0x4E00 && ch <= 0x9FFF) return true;
            }
            return false;
        }

        /// <summary>
        /// 命中测试：模拟 GraphicRaycaster，找出点 (x, y) 落在哪个 raycastTarget 上。
        ///
        /// 用户报过"设置菜单点不动"。光看 raycastTarget 标志不够——那只能说明
        /// 元素**愿意**吃射线，还要确认**确实吃到了**：有没有别的元素盖在上面、
        /// 或者命中的是个什么都不做的容器。Unity 的规则是"画家顺序里最后
        /// 绘制（最靠上）且 rect 含该点的那一个"，这里照此实现。
        /// </summary>
        internal static Node HitTest(Node root, float x, float y)
        {
            Node best = null;
            int bestDepth = -1;
            Collect(root, x, y, 0, ref best, ref bestDepth);
            return best;
        }

        private static void Collect(Node n, float x, float y, int depth,
            ref Node best, ref int bestDepth)
        {
            bool contains = x >= n.X && x <= n.X + n.W
                         && y >= n.Y && y <= n.Y + n.H;
            if (n.Raycast && contains && depth >= bestDepth)
            {
                best = n;
                bestDepth = depth;
            }
            // 不含该点的子树里不会有命中（子矩形必然在父矩形内或相交）
            if (!contains && !n.Clips)
            {
                // 仍然要往下走：子件可能被 stretch 到父外（面板居中那类）
            }
            foreach (var k in n.Kids)
            {
                Collect(k, x, y, depth + 1, ref best, ref bestDepth);
            }
        }

        /// <summary>节点路径，如 "Panel/Sidebar/Nav0"。</summary>
        internal static string Path(Node n)
        {
            var parts = new List<string>();
            for (var c = n; c != null && c.Go != null; c = c.Parent)
            {
                parts.Insert(0, c.Go.name);
            }
            return string.Join("/", parts);
        }

        internal static Node Walk(Transform t, Node parent)
        {
            var n = new Node { Parent = parent, Go = t.gameObject };
            if (t.gameObject.GetComponent<UImage>() != null)
            {
                var img = t.gameObject.GetComponent<UImage>();
                n.IsImage = true;
                n.Color = img.color;
                n.Raycast = img.raycastTarget;
            }
            if (t.gameObject.GetComponent<Button>() != null)
            {
                n.IsButton = true;
            }
            n.Buttons = 1;
            n.Clips = t.gameObject.GetComponent<UnityEngine.UI.RectMask2D>() != null;
            var tmp = t.gameObject.GetComponent<TMP_Text>();
            if (tmp != null)
            {
                // 文字节点不是 Image，颜色得从 TMP 组件自己取。
                // 漏了这一步，Color 就停在默认的全透明——DrawString 被调用
                // 十六次，图上一个字都没有，还以为又是布局坏了。
                n.Color = tmp.color;
            }
            if (tmp != null)
            {
                n.Text = tmp.text;
                n.FontSize = tmp.fontSize;
                n.Align = tmp.alignment;
                // 从真实属性判断，别猜：Overflow + Normal 才是折行
                n.Wrap = tmp.textWrappingMode == TextWrappingModes.Normal
                    && tmp.overflowMode == TextOverflowModes.Overflow;
                n.Cjk = HasCjk(tmp.text);
            }
            parent.Kids.Add(n);

            // ── 解析矩形 ──────────────────────────────────────
            // 全程用 Unity 的坐标（y 向上、原点在父矩形左下）算，最后一次性翻成
            // y 向下。之前在"y 向下的空间"里直接套锚点公式，把 anchor.y=1
            // 当成"距顶 1×高"，整棵树错位——预览自己算错就会验出假结果。
            var rt = t as RectTransform;
            float px = parent.Px, py = parent.Py, pw = parent.W, ph = parent.H;
            bool stretchX = rt.anchorMin.x != rt.anchorMax.x;
            bool stretchY = rt.anchorMin.y != rt.anchorMax.y;
            float x0, y0, x1, y1;
            if (stretchX)
            {
                x0 = px + rt.anchorMin.x * pw + rt.offsetMin.x;
                x1 = px + rt.anchorMax.x * pw + rt.offsetMax.x;
            }
            else
            {
                float cx = px + rt.anchorMin.x * pw + rt.anchoredPosition.x;
                x0 = cx - rt.pivot.x * rt.sizeDelta.x;
                x1 = x0 + rt.sizeDelta.x;
            }
            if (stretchY)
            {
                y0 = py + rt.anchorMin.y * ph + rt.offsetMin.y;
                y1 = py + rt.anchorMax.y * ph + rt.offsetMax.y;
            }
            else
            {
                float cy = py + rt.anchorMin.y * ph + rt.anchoredPosition.y;
                y0 = cy - rt.pivot.y * rt.sizeDelta.y;
                y1 = y0 + rt.sizeDelta.y;
            }

            n.Px = x0;
            n.Py = y0;
            n.W = x1 - x0;
            n.H = y1 - y0;
            // 翻成 y 向下：顶边 = 父顶 - (rect.maxY - 父底)
            n.X = parent.X + (x0 - px);
            n.Y = parent.Y + (ph - (y1 - py));
            n.ParentRectTop = parent.Y;

            foreach (var c in t.children)
            {
                Walk(c, n);
            }
            return n;
        }
    }

    internal sealed class Renderer
    {
        private readonly Bitmap _bmp;
        private readonly Graphics _g;
        private readonly StringFormat _fmt = new StringFormat(StringFormat.GenericTypographic);
        private readonly Dictionary<float, Font> _fonts = new Dictionary<float, Font>();
        private readonly string _family = PickFamily();

        /// <summary>溢出告警：文字实际宽度超出它所在的矩形。</summary>
        internal readonly List<string> Overflows = new List<string>();
        internal int DrawTextCalls;

        internal Renderer(int w, int h)
        {
            _bmp = new Bitmap(w, h);
            _g = Graphics.FromImage(_bmp);
            // 背景用明显的蓝绿色，模拟游戏画面——面板是深灰，必须能区分开
            _g.Clear(System.Drawing.Color.FromArgb(20, 60, 55));
            _g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            _fmt.Alignment = StringAlignment.Near;
            _fmt.LineAlignment = StringAlignment.Center;
            _fmt.Trimming = StringTrimming.EllipsisCharacter;
            _fmt.FormatFlags |= StringFormatFlags.NoWrap;
        }

        private Font Font(float px)
        {
            float key = MathF.Round(px * 2) / 2f;
            if (_fonts.TryGetValue(key, out var f)) return f;
            f = new Font(_family, MathF.Max(5f, key), FontStyle.Regular, GraphicsUnit.Pixel);
            _fonts[key] = f;
            return f;
        }

        /// <summary>
        /// 挑一个**确实装在这台机器上**的字体。原来硬写 "Microsoft YaHei"，
        /// 构造出的 Font 对象不可用，DrawString 静默什么都不画——图上就一个字
        /// 都没有，差点被当成布局又坏了。宁可在这里验字形，也不要在游戏里验。
        /// </summary>
        private static string PickFamily()
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var fam in FontFamily.Families)
                {
                    installed.Add(fam.Name);
                }
            }
            catch
            {
                // 枚举失败就只用兜底名
            }
            string[] preferred =
            {
                "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑",
                "SimHei", "SimSun", "Noto Sans CJK SC", "Source Han Sans SC",
                "DengXian", "NSimSun", "Malgun Gothic", "MS Gothic",
                "Segoe UI", "Tahoma", "Arial", "Helvetica",
            };
            foreach (var name in preferred)
            {
                if (installed.Contains(name))
                {
                    return name;
                }
            }
            return FontFamily.GenericSansSerif.Name;
        }

        private static System.Drawing.Color Conv(UColor c) => System.Drawing.Color.FromArgb(
            (int)(Mathf.Clamp01(c.a) * 255), (int)(Mathf.Clamp01(c.r) * 255),
            (int)(Mathf.Clamp01(c.g) * 255), (int)(Mathf.Clamp01(c.b) * 255));

        internal void Draw(Node n, float offsetX = 0, float offsetY = 0)
        {
            // 画家顺序：先画自己，再画子件。先画子件的话，父件会盖住它们——
            // 之前就是这么写的，结果整棵子树被 Panel 涂成一片，什么都看不见，
            // 差点被当成"布局又坏了"。
            DrawSelf(n, offsetX, offsetY);
            if (n.Clips)
            {
                var clip = new RectangleF(n.X + offsetX, n.Y + offsetY, n.W, n.H);
                var saved = _g;
                var state = saved.Save();
                saved.SetClip(clip);
                foreach (var k in n.Kids)
                {
                    Draw(k, offsetX, offsetY);
                }
                saved.Restore(state);
                return;
            }
            foreach (var k in n.Kids)
            {
                Draw(k, offsetX, offsetY);
            }
        }

        private void DrawSelf(Node n, float offsetX, float offsetY)
        {
            if (!n.IsImage && n.Text == null)
            {
                return;
            }
            float x = n.X + offsetX, y = n.Y + offsetY;
            if (n.IsImage && n.Color.a > 0.001f)
            {
                using (var b = new SolidBrush(Conv(n.Color)))
                {
                    _g.FillRectangle(b, x, y, n.W, n.H);
                }
            }
            if (n.IsButton && n.IsImage)
            {
                using (var p = new Pen(System.Drawing.Color.FromArgb(60, 255, 255, 255)))
                {
                    _g.DrawRectangle(p, x, y, n.W, n.H);
                }
            }
            if (!string.IsNullOrEmpty(n.Text))
            {
                DrawText(n, x, y);
            }
        }

        private void DrawText(Node n, float x, float y)
        {
            DrawTextCalls++;
            var font = Font(n.FontSize);
            var sb = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Near,
                Trimming = StringTrimming.None,
            };
            var brush = new SolidBrush(Conv(n.Color));

            // 折行文字：按框宽逐行排版、逐行画。点定位一次只能画一行，
            // 所以不能靠 DrawString 自己的折行。
            if (n.Wrap)
            {
                float lineH = font.GetHeight(_g);
                var lines = WrapLines(n.Text, font, n.W);
                for (int i = 0; i < lines.Length; i++)
                {
                    float w = _g.MeasureString(lines[i], font).Width;
                    if (w > n.W + 1f)
                    {
                        Overflows.Add($"  「{Trim(n.Text)}」 第{i + 1}行「{Trim(lines[i])}」"
                            + $" 需 {w:F0}px，框宽 {n.W:F0}px");
                    }
                    _g.DrawString(lines[i], font, brush,
                        new PointF(x, y + i * lineH), sb);
                }
                float used = lines.Length * lineH;
                if (used > n.H + 1f)
                {
                    Overflows.Add($"  「{Trim(n.Text)}」 {lines.Length} 行占 {used:F0}px，"
                        + $"框高 {n.H:F0}px  ← 行高不够");
                }
                brush.Dispose();
                return;
            }

            SizeF sz = _g.MeasureString(n.Text, font, int.MaxValue, sb);
            if (sz.Width > n.W + 1f)
            {
                Overflows.Add($"  「{Trim(n.Text)}」 单行需 {sz.Width:F0}px，框宽 {n.W:F0}px"
                    + (n.Cjk ? "  (含中文)" : ""));
            }
            float dx = x;
            if (n.Align == TextAlignmentOptions.Center)
            {
                dx = x + (n.W - sz.Width) * 0.5f;
            }
            else if (n.Align == TextAlignmentOptions.MidlineRight)
            {
                dx = x + (n.W - sz.Width);
            }
            _g.DrawString(n.Text, font, brush,
                new PointF(dx, y + (n.H - sz.Height) * 0.5f), sb);
            brush.Dispose();
        }

        /// <summary>按可用宽度硬折行。中文逐字断，西文按空格断。</summary>
        private string[] WrapLines(string text, Font font, float width)
        {
            var result = new List<string>();
            var line = new System.Text.StringBuilder();
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    result.Add(line.ToString());
                    line.Clear();
                    continue;
                }
                line.Append(ch);
                if (_g.MeasureString(line.ToString(), font).Width > width && line.Length > 1)
                {
                    line.Length -= 1;
                    result.Add(line.ToString());
                    line.Clear();
                    line.Append(ch);
                }
            }
            if (line.Length > 0)
            {
                result.Add(line.ToString());
            }
            return result.Count > 0 ? result.ToArray() : new[] { string.Empty };
        }

        private static string Trim(string s)
            => s.Length <= 28 ? s : s.Substring(0, 28) + "…";

        internal void Save(string path)
        {
            _bmp.Save(path, ImageFormat.Png);
            _g.Dispose();
        }

        /// <summary>
        /// 自检：在角落画几段字，确认这台机器的 GDI+ 真能出字形。
        /// 画不出字却一声不吭，是最难查的那类问题——图上什么都没有，
        /// 很容易误判成"布局又坏了"，实际只是字体没画出来。
        /// </summary>
        internal string SelfTest()
        {
            var f = Font(28f);
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Near,
                Trimming = StringTrimming.None,
            };
            // **必须画在临时图上。** 原来直接往 _g（输出图）画，于是每张
            // 预览图的左上角都印着 "SELF TEST"——图就废了，没法拿来判断
            // 界面好不好看。顺带说一句：既然要用眼睛看预览图，任何画到
            // 输出上的调试痕迹都是致命的。
            using (var bmp = new System.Drawing.Bitmap(400, 120))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Black);
                using (var b = new SolidBrush(System.Drawing.Color.White))
                {
                    // 点定位能画、矩形定位画不出——那就用点定位画，省得再被
                    // GDI+ 那个静默失败的组合坑一次。
                    g.DrawString("SELF TEST 中文 Test 123", f, b, new PointF(8f, 8f), sf);
                    g.DrawString("SELF TEST 点定位 OK", f, b, new PointF(8f, 46f), sf);
                }
                // 真的画出来了吗？数一下非黑像素——GDI+ 出问题时不报错，
                // 只是什么都不画，光看"没抛异常"是不够的。
                int lit = 0;
                for (int y = 0; y < bmp.Height && lit == 0; y += 2)
                {
                    for (int x = 0; x < bmp.Width; x += 2)
                    {
                        if (bmp.GetPixel(x, y).R > 40) { lit++; break; }
                    }
                }
                var sz = g.MeasureString("SELF TEST", f, int.MaxValue, sf);
                return $"字体族={f.Name} 测量={sz.Width:F0}x{sz.Height:F0}"
                    + (lit > 0 ? " 出字 ✓" : " **没画出任何字形**");
            }
        }
    }
}