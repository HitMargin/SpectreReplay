using UnityEngine;

namespace SpectreReplay.Ui
{
    internal sealed class TextField
    {
        /// <summary>当前文本。调用方直接读写它（载入设置 / 撤销时覆盖）。</summary>
        internal string Text = "";

        /// <summary>是否处于编辑态（有光标在闪）。</summary>
        internal bool Editing;

        // ── 编辑状态 ──────────────────────────────────────────
        /// <summary>插入点，0..Text.Length。</summary>
        private int _caret;
        /// <summary>选区的另一头。与 _caret 相等时无选区。</summary>
        private int _anchor;
        /// <summary>水平滚动量（像素）。文本比框长时用它把插入点带进视野。</summary>
        private float _scroll;
        private bool _dragging;
        private float _blinkAt;

        // ── 前缀宽度缓存 ──────────────────────────────────────
        private float[] _bounds;      // 长度 = Text.Length + 1，_bounds[i] = 前 i 个字符的宽度
        private string _boundsFor;
        private GUIStyle _boundsStyle;

        private const float PadX = 8f;
        private const float BlinkPeriod = 0.55f;

        /// <summary>
        /// 输入框专用单行样式。
        ///
        /// <para><b>必须显式关掉 wordWrap。</b>绘制时传进来的通常是
        /// <see cref="Gui.LabelStyle"/>，而 GUI.skin.label 默认 wordWrap=true。
        /// 输入框只有 28px 高，一换行第二行就落在裁剪区外——玩家看到的是
        /// "文字只显示半截/下面一行被切掉"。这里按传入样式的字体度量派生一份
        /// 单行样式：水平 Overflow（超长靠内部 _scroll 处理，不靠换行）、
        /// 垂直 Middle（在框内居中）、clipping 用 Clip 收在裁剪区内。</para>
        /// </summary>
        private static GUIStyle _singleLine;
        private static GUIStyle _singleLineFor;

        private static GUIStyle SingleLine(GUIStyle basis)
        {
            if (_singleLine != null && _singleLineFor == basis) return _singleLine;
            _singleLine = new GUIStyle(basis)
            {
                wordWrap = false,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                richText = false,
            };
            // 高度按字体行高走：行高比控件高时，用 Middle 居中仍会被 Clip
            // 切掉上下沿，所以把固定高度交给布局（不设 fixedHeight）。
            _singleLine.fixedHeight = 0f;
            _singleLine.stretchHeight = false;
            _singleLineFor = basis;
            return _singleLine;
        }

        /// <summary>
        /// 画在 <paramref name="box"/> 里并处理输入。
        /// 返回 true = 本次回车（或点别处）提交，调用方据此落盘。
        /// <paramref name="canceled"/> 为 true = 玩家按了 Esc，调用方据此把
        /// 文本还原成设置里的值。
        ///
        /// box 由调用方用 <c>GUILayoutUtility.GetRect</c> 取好：本控件不参与
        /// GUILayout 布局，免得和 UMM 自己的滚动区互相干扰。
        /// </summary>
        internal bool Draw(Rect box, GUIStyle style, string placeholder, out bool canceled)
        {
            canceled = false;
            Text = Text ?? "";
            if (style == null)
            {
                style = GUI.skin.label;
            }
            // 统一换成单行样式：度量、命中、绘制三步必须用同一套字体，
            // 否则光标位置和实际字形会对不上。
            style = SingleLine(style);

            Rect inner = new Rect(box.x + PadX, box.y + 1f,
                Mathf.Max(1f, box.width - PadX * 2f), Mathf.Max(1f, box.height - 2f));

            EnsureBounds(style);
            Event e = Event.current;
            bool committed = false;

            // **禁用态：不进编辑、不处理任何按键/鼠标。**
            // 只压暗不拦是没有意义的——灰框仍然能点进去打字、提交后值就变了，
            // 玩家会认为"灰色只是表示默认值"。这里连 Editing 都强制清掉：
            // 若玩家正在编辑时总开关被关，框必须失焦并放弃这次编辑，
            // 否则会出现"框已灰、却还在接收输入"的怪状态。
            if (Gui.IsDim)
            {
                Editing = false;
                _dragging = false;
                Paint(box, inner, style, placeholder);
                return false;
            }

            if (e != null)
            {
                switch (e.type)
                {
                    case EventType.MouseDown:
                        if (e.button == 0)
                        {
                            if (box.Contains(e.mousePosition))
                            {
                                if (!Editing)
                                {
                                    Editing = true;
                                    SelectAll();   // 点入即全选：整体替换路径是这里的常态操作
                                }
                                else if (e.clickCount >= 2)
                                {
                                    SelectAll();
                                }
                                else
                                {
                                    int at = IndexAt(e.mousePosition.x - inner.x);
                                    _caret = at;
                                    if (!e.shift)
                                    {
                                        _anchor = at;
                                    }
                                }
                                _dragging = true;
                                Touch();
                                e.Use();
                            }
                            else if (Editing)
                            {
                                // 点别处 = 失焦 + 提交。**不 Use**（坑 3）。
                                Editing = false;
                                _dragging = false;
                                committed = true;
                            }
                        }
                        break;

                    case EventType.MouseDrag:
                        if (_dragging && Editing)
                        {
                            _caret = IndexAt(e.mousePosition.x - inner.x);
                            Touch();
                            e.Use();
                        }
                        break;

                    case EventType.MouseUp:
                        _dragging = false;
                        break;

                    case EventType.KeyDown:
                        if (Editing)
                        {
                            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                            {
                                Editing = false;
                                _dragging = false;
                                committed = true;
                                e.Use();
                            }
                            else if (e.keyCode == KeyCode.Escape)
                            {
                                // Esc = 放弃本次编辑。**这里只能管到 IMGUI 这一层**：
                                // UMM 关面板走的是它自己的轮询——Param.EscapeHotkey.Up()
                                // 里是 Input.GetKeyUp(KeyCode.Escape)
                                // （UnityModManager.decompiled.cs:3384 与 :7864-7879），
                                // 直接读硬件状态，Event.Use() 拦不住。
                                // 所以按 Esc 的真实后果是「丢弃编辑 + 面板跟着关」，
                                // 而这是自洽的：本行把 Text 还原成设置里的值，
                                // 随后 OnHide → CommitText 会看到"没有改动"，
                                // 不会把半截路径存进去。
                                // Use() 仍然要做，但它拦的是**其它 IMGUI 控件**
                                // 看到同一个 Esc（例如同一帧里展开的下拉）。
                                Editing = false;
                                _dragging = false;
                                canceled = true;
                                e.Use();
                            }
                            else if (HandleKey(e))
                            {
                                Touch();
                                e.Use();
                            }
                        }
                        break;
                }
            }

            EnsureBounds(style);          // HandleKey 可能改过 Text，这里重算一次
            EnsureCaretVisible(inner.width);
            Paint(box, inner, style, placeholder);
            return committed;
        }

        // ── 绘制 ──────────────────────────────────────────────

        private void Paint(Rect box, Rect inner, GUIStyle style, string placeholder)
        {
            // hover：鼠标落在框上时凹槽与描边各提一档。与按钮/滑条/开关用
            // 同一套语汇（悬停即提亮），否则整页只有按钮"活"着。
            // 禁用态不提亮：灰着还跟鼠标发亮会让人以为能点。
            bool hot = !Gui.IsDim && Gui.Hover(box);
            // **输入框就是一块平的圆角面，没有凹槽、没有描边环。**
            // 之前叠 Well3D（内阴影）+ 1px 圆角环，在 24px 高的细条里圆角半径
            // 接近半高，整条被看成"胶囊"，外圈那道光就是环被 9-slice 拉伸后
            // 的直边——用户看到的就是这个。
            // 现在只画一层圆角填充：正常 = FieldBg，hover = FieldBgHover，
            // 编辑中 = FieldBgHover（更亮一档，作为焦点反馈）。
            Rect snapped = Gui.Snap(box);
            Color face = Editing ? Gui.FieldBgHover
                : (hot ? Gui.FieldBgHover : Gui.FieldBg);
            // 半径 6：贴图只做 9-slice，圆角在任何宽高下都保持 6px，
            // 不会随高度变成胶囊。
            GUI.DrawTexture(snapped, RoundedTex.Get(48, 48, 6, face),
                ScaleMode.StretchToFill, true, 0f, Color.white, 7f, 7f);

            // 占位文案与正文都要在裁剪里。Gui.LabelStyle 克隆自 GUI.skin.label，
            // 而 UMM 把 skin.label.clipping 改成了 Overflow（decompiled :3509），
            // 不剪的话长文案会画出框外，盖住右边那一列。占位文案是整句话，
            // 最容易超宽。
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }
            GUI.BeginClip(inner);
            try
            {
                if (Text.Length == 0 && !Editing && !string.IsNullOrEmpty(placeholder))
                {
                    float phLine = style.CalcHeight(new GUIContent(placeholder), inner.width);
                    float phY = Mathf.Max(0f, (inner.height - phLine) * 0.5f);
                    Gui.Text(new Rect(0f, phY, inner.width, phLine),
                        placeholder, Gui.FieldHint, style);
                    return;
                }

                int a = Mathf.Min(_anchor, _caret);
                int b = Mathf.Max(_anchor, _caret);
                float textW = _bounds[Text.Length];
                float h = inner.height;
                // **单行文字必须在框内垂直居中。** 之前一律从 y=0 开始画，
                // 而 clipping=Clip 只给框那么高：字形的下沿直接被裁掉，看起来
                // 像"只显示半截"。这里按样式行高算出居中偏移，并保证不低于 0。
                float lineH = style.CalcHeight(new GUIContent(Text), inner.width);
                float textY = Mathf.Max(0f, (h - lineH) * 0.5f);

                // BeginClip 把坐标原点挪到裁剪矩形左上角，所以里面的坐标是
                // 相对值，滚动量直接减在 x 上。
                if (a != b)
                {
                    float x1 = _bounds[a] - _scroll;
                    float x2 = _bounds[b] - _scroll;
                    Gui.Fill(new Rect(x1, textY, Mathf.Max(1f, x2 - x1),
                        Mathf.Min(h - textY, lineH)), Gui.FieldSel);
                }
                Gui.Text(new Rect(-_scroll, textY, Mathf.Max(textW, inner.width), lineH),
                    Text, Gui.TextCol, style);

                bool blink = ((Time.realtimeSinceStartup - _blinkAt) % (BlinkPeriod * 2f)) < BlinkPeriod;
                if (Editing && blink)
                {
                    Gui.Fill(new Rect(_bounds[_caret] - _scroll, textY + 1f, 1f,
                        Mathf.Max(1f, Mathf.Min(h - textY - 2f, lineH - 2f))), Gui.TextCol);
                }
            }
            finally
            {
                GUI.EndClip();
            }
        }

        // ── 几何 ──────────────────────────────────────────────

        /// <summary>按需重算前缀宽度。判据：文本或样式换了。</summary>
        private void EnsureBounds(GUIStyle style)
        {
            if (_bounds != null && _boundsFor == Text && ReferenceEquals(_boundsStyle, style))
            {
                return;
            }
            _boundsFor = Text;
            _boundsStyle = style;

            int n = Text.Length;
            if (_bounds == null || _bounds.Length != n + 1)
            {
                _bounds = new float[n + 1];
            }
            _bounds[0] = 0f;
            for (int i = 1; i <= n; i++)
            {
                _bounds[i] = style.CalcSize(new GUIContent(Text.Substring(0, i))).x;
            }
            _caret = Mathf.Clamp(_caret, 0, n);
            _anchor = Mathf.Clamp(_anchor, 0, n);
        }

        /// <summary>把插入点带进视野（文本比框长时才会真的动）。</summary>
        private void EnsureCaretVisible(float viewW)
        {
            float caret = _bounds[_caret];
            float textW = _bounds[Text.Length];
            if (caret - _scroll > viewW - 2f)
            {
                _scroll = caret - (viewW - 2f);
            }
            if (caret - _scroll < 2f)
            {
                _scroll = caret - 2f;
            }
            _scroll = Mathf.Clamp(_scroll, 0f, Mathf.Max(0f, textW - viewW + 4f));
        }

        /// <summary>框内局部 x（相对文字起点）→ 最近的字符边界。</summary>
        private int IndexAt(float localX)
        {
            float x = localX + _scroll;
            for (int i = 0; i < Text.Length; i++)
            {
                if (x < (_bounds[i] + _bounds[i + 1]) * 0.5f)
                {
                    return i;
                }
            }
            return Text.Length;
        }

        private void Touch()
        {
            _blinkAt = Time.realtimeSinceStartup;
        }

        // ── 编辑动作 ──────────────────────────────────────────

        private void SelectAll()
        {
            _anchor = 0;
            _caret = Text.Length;
        }

        private bool DeleteSelection()
        {
            int a = Mathf.Min(_anchor, _caret);
            int b = Mathf.Max(_anchor, _caret);
            if (a == b)
            {
                return false;
            }
            Text = Text.Remove(a, b - a);
            _caret = a;
            _anchor = a;
            return true;
        }

        private void Move(int delta, bool shift)
        {
            if (!shift && _anchor != _caret)
            {
                // 有选区时按方向键：先塌缩到选区靠边的那一端
                _caret = delta < 0 ? Mathf.Min(_anchor, _caret) : Mathf.Max(_anchor, _caret);
                _anchor = _caret;
                return;
            }
            _caret = Mathf.Clamp(_caret + delta, 0, Text.Length);
            if (!shift)
            {
                _anchor = _caret;
            }
        }

        private void Copy()
        {
            int a = Mathf.Min(_anchor, _caret);
            int b = Mathf.Max(_anchor, _caret);
            if (a == b)
            {
                return;
            }
            try
            {
                GUIUtility.systemCopyBuffer = Text.Substring(a, b - a);
            }
            catch
            {
                // 个别平台/环境没有系统剪贴板：复制失败不该让整个设置页抛异常
            }
        }

        private void Paste()
        {
            string clip;
            try
            {
                clip = GUIUtility.systemCopyBuffer;
            }
            catch
            {
                return;
            }
            if (string.IsNullOrEmpty(clip))
            {
                return;
            }
            // 单行框：换行 / 制表一律当分隔符丢掉（坑 1）
            clip = clip.Replace("\r", "").Replace("\n", "").Replace("\t", "");
            if (clip.Length == 0)
            {
                return;
            }
            DeleteSelection();
            Text = Text.Insert(_caret, clip);
            _caret += clip.Length;
            _anchor = _caret;
        }

        /// <summary>返回是否消费了这个按键。返回 false 就交给 UMM（如 Tab）。</summary>
        private bool HandleKey(Event e)
        {
            bool ctrl = e.control || e.command;

            switch (e.keyCode)
            {
                case KeyCode.LeftArrow: Move(-1, e.shift); return true;
                case KeyCode.RightArrow: Move(1, e.shift); return true;
                case KeyCode.Home: _caret = 0; if (!e.shift) _anchor = _caret; return true;
                case KeyCode.End: _caret = Text.Length; if (!e.shift) _anchor = _caret; return true;
                case KeyCode.Backspace:
                    if (!DeleteSelection() && _caret > 0)
                    {
                        Text = Text.Remove(_caret - 1, 1);
                        _caret--;
                        _anchor = _caret;
                    }
                    return true;
                case KeyCode.Delete:
                    if (!DeleteSelection() && _caret < Text.Length)
                    {
                        Text = Text.Remove(_caret, 1);
                        _anchor = _caret;
                    }
                    return true;
                case KeyCode.A: if (ctrl) { SelectAll(); return true; } break;
                case KeyCode.C: if (ctrl) { Copy(); return true; } break;
                case KeyCode.X: if (ctrl) { Copy(); DeleteSelection(); return true; } break;
                case KeyCode.V: if (ctrl) { Paste(); return true; } break;
            }

            // 可打印字符。必须排除 Ctrl 组合：Ctrl+A 的 character 是 '\x01'，
            // 只有 >= ' ' 不成立才能挡住。
            char c = e.character;
            if (!ctrl && c >= ' ' && c != (char)127)
            {
                DeleteSelection();
                Text = Text.Insert(_caret, c.ToString());
                _caret++;
                _anchor = _caret;
                return true;
            }
            return false;
        }
    }
}
