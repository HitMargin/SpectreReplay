using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 基元层：所有矩形都由调用方显式给出，函数只负责建物体与挂组件。
    ///
    /// 两条硬规则（都是踩出来的）：
    ///   1. **文字组件必须直接挂在刚建的物体上**，不能建完父级再塞子级。
    ///      嵌一层之后，偏移量是相对那个"从没定过尺寸的父级"算的，
    ///      宽度会算成负数，字一个都不显示。
    ///   2. **raycastTarget 一律显式指定**。Button 的 targetGraphic 若
    ///      raycastTarget=false，按钮永远收不到点击——上一版整页点不动就是
    ///      这个原因，而它当时是"继承"来的默认值。
    /// </summary>
    internal static class Gfx
    {
        // ── 基础 ────────────────────────────────────────────────

        /// <summary>建一个纯色矩形。left/top 相对父级左上角，向右下为正。</summary>
        internal static RectTransform Rect(Transform parent, string name, float left, float top,
            float width, float height, Color color, bool raycast)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            // 锚在左上角、轴心也在左上：left/top 就是"距左上角的偏移"，
            // 不用心算 pivot 的补偿。
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(left, -top);
            rt.sizeDelta = new Vector2(width, height);
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = raycast;
            return rt;
        }

        /// <summary>
        /// 横向铺满父级、高度给定的矩形。顶边在距父顶 <paramref name="top"/> 处。
        ///
        /// 不用 <c>Rect</c> 的原因：<c>Rect</c> 要求调用方给出宽度，而内容容器
        /// 是横向拉伸的矩形，其 <c>sizeDelta.x</c> **恒为 0**——拿它当行宽，
        /// 每一行都会是 0 宽，标签框宽算成负数、控件被摆到行外面去。
        /// 改成锚点拉伸后，宽度由父级决定，不必（也不可能）预先知道。
        /// </summary>
        internal static RectTransform RectFillX(Transform parent, string name, float top,
            float height, Color color, bool raycast)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);   // ← 横向拉伸
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(0f, height);
            rt.anchoredPosition = new Vector2(0f, -top);
            rt.offsetMin = new Vector2(0f, 0f);
            rt.offsetMax = new Vector2(0f, 0f);
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = raycast;
            return rt;
        }

        /// <summary>
        /// 贴父级右边缘的矩形（右边留 <c>PadX</c>）。行内控件一律用它，
        /// 于是控件位置不再依赖"父级有多宽"。
        /// </summary>
        internal static RectTransform RectRight(Transform parent, string name, float top,
            float width, float height, Color color, bool raycast)
        {
            var rt = Rect(parent, name, 0f, top, width, height, color, raycast);
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-Theme.PadX, -top);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return rt;
        }

        /// <summary>
        /// 可折行的文字，撑满父级。用于说明文案——那种句子本来就长，
        /// 强行单行会被 Ellipsis 截掉半句（离屏预览实测最长的一条要 1092px，
        /// 单行框只有 782px）。
        /// </summary>
        internal static TMP_Text TextWrap(Transform parent, string name, string text,
            float size, Color color, float insetX)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            Stretch(rt, insetX, 0f, insetX, 0f);
            var t = go.AddComponent<TextMeshProUGUI>();
            UiFonts.Apply(t);
            t.fontSize = size * Theme.TextScale;
            t.color = color;
            t.raycastTarget = false;
            t.text = text;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Overflow;   // 折行，不截断
            return t;
        }

        /// <summary>
        /// 行内标签：横向拉伸到父级右边距 <paramref name="rightInset"/> 处。
        /// 宽度同样由父级决定。
        /// </summary>
        internal static TMP_Text LabelIn(Transform row, string text, float size, Color color,
            float rightInset)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(row, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(Theme.PadX, 0f);
            rt.offsetMax = new Vector2(-rightInset, 0f);
            var t = go.AddComponent<TextMeshProUGUI>();
            UiFonts.Apply(t);
            t.fontSize = size * Theme.TextScale;
            t.color = color;
            t.raycastTarget = false;
            t.text = text;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        /// <summary>建一段文字。字体由 <see cref="UiFonts"/> 统一注入。</summary>
        internal static TMP_Text Text(Transform parent, string name, float left, float top,
            float width, float height, string content, float size, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(left, -top);
            rt.sizeDelta = new Vector2(width, height);

            var t = go.AddComponent<TextMeshProUGUI>();
            UiFonts.Apply(t);
            t.fontSize = size * Theme.TextScale;
            t.color = color;
            t.raycastTarget = false;
            t.text = content;
            t.alignment = align;
            t.textWrappingMode = TextWrappingModes.Normal;
            // Ellipsis + 单行：文字再长也只占一行，不会把行撑高。
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        /// <summary>文字撑满父级（用于行内标签这类宽度要跟着行走的场合）。</summary>
        internal static TMP_Text TextFill(Transform parent, string name, string content,
            float size, Color color, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            Stretch(rt, 0f, 0f, 0f, 0f);

            var t = go.AddComponent<TextMeshProUGUI>();
            UiFonts.Apply(t);
            t.fontSize = size * Theme.TextScale;
            t.color = color;
            t.raycastTarget = false;
            t.text = content;
            t.alignment = align;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        internal static void Stretch(RectTransform rt, float left, float bottom,
            float right, float top)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>在已有 Graphic 上挂一个可点击的 Button，并配好颜色反馈。</summary>
        internal static Button MakeButton(GameObject target, Color normal, Color hover,
            Color pressed, Action onClick)
        {
            var btn = target.AddComponent<Button>();
            btn.targetGraphic = target.GetComponent<Graphic>();
            btn.transition = Selectable.Transition.ColorTint;
            var cb = btn.colors;
            cb.normalColor = normal;
            cb.highlightedColor = hover;
            cb.pressedColor = pressed;
            cb.selectedColor = normal;
            cb.disabledColor = new Color(1f, 1f, 1f, 0.3f);
            cb.fadeDuration = 0.07f;
            btn.colors = cb;
            if (onClick != null)
            {
                btn.onClick.AddListener(() => onClick());
            }
            return btn;
        }

        // ── 开关 ────────────────────────────────────────────────
        /// <summary>
        /// 开关。背景 rect 的 raycastTarget 必须为 true，否则点不动。
        /// </summary>
        internal static GameObject Toggle(Transform row, float top,
            bool value, Action<bool> onChanged)
        {
            const float knobW = 24f;
            // 轨道：56 宽，旋钮 24，两侧各留 16。开着旋钮靠右、关着靠左。
            var bg = RectRight(row, "Toggle", top, Theme.CtlToggleW, Theme.CtlToggleH,
                value ? Theme.Accent : Theme.Control, true);
            // 旋钮的初始位置必须跟着 value 走。之前不管开��都摆在左边，
            // 于是"开"的开关看起来也是关的——图上一眼假。
            float knobX = value ? 32f : 16f;
            var knob = Rect(bg, "Knob", knobX, (Theme.CtlToggleH - knobW) * 0.5f,
                knobW, knobW, Color.white, false);

            bool current = value;
            MakeButton(bg.gameObject, Color.white, Color.white, new Color(0.8f, 0.8f, 0.8f, 1f),
                () =>
                {
                    current = !current;
                    bg.GetComponent<Image>().color = current ? Theme.Accent : Theme.Control;
                    knob.anchoredPosition = new Vector2(
                        current ? 32f : 16f, -(Theme.CtlToggleH - knobW) * 0.5f);
                    onChanged?.Invoke(current);
                });
            return bg.gameObject;
        }

        // ── 按钮 ────────────────────────────────────────────────
        /// <summary>
        /// 行内按钮，贴父级右边缘。<paramref name="offsetFromRight"/> 让它
        /// 往左挪——一行里放第二枚按钮时用（第一枚是 offset=0 的那个）。
        /// </summary>
        internal static Button Button(Transform row, float top, float width,
            string label, bool primary, Action onClick, float offsetFromRight = 0f)
        {
            var h = Theme.CtlButtonH;
            var bg = RectRight(row, "Button", (Theme.RowH - h) * 0.5f, width, h,
                primary ? Theme.Accent : Theme.Control, true);
            if (offsetFromRight != 0f)
            {
                bg.anchoredPosition = new Vector2(
                    -Theme.PadX - offsetFromRight, -(Theme.RowH - h) * 0.5f);
            }
            TextFill(bg, "Text", label, Theme.FontSmall,
                primary ? Theme.TextOnAccent : Theme.Text, TextAlignmentOptions.Center);
            var btn = MakeButton(bg.gameObject, Color.white,
                new Color(1.25f, 1.25f, 1.25f, 1f), new Color(0.75f, 0.75f, 0.75f, 1f), onClick);
            return btn;
        }

        // ── 滑条 ────────────────────────────────────────────────
        internal static UnityEngine.UI.Slider Slider(Transform row, float top, float value,
            float min, float max, bool integer, Action<float> onChanged,
            Func<float, string> format = null)
        {
            const float trackW = Theme.CtlSliderW;
            const float valueW = 88f;
            float h = Theme.CtlSliderH;
            // 槽宽 = 数值 + 间隙 + 轨道。数值**放左边**而不是轨道下方：
            // 行高只有 56，放下面会掉出这一行、压到下一行的标签上。
            float slotW = valueW + 14f + trackW;
            var root = RectRight(row, "Slider", (Theme.RowH - h) * 0.5f, slotW, h,
                new Color(0f, 0f, 0f, 0f), true);

            Func<float, string> fmt = format ?? (v => integer
                ? ((int)Mathf.Round(v)).ToString()
                : v.ToString("0.##"));

            // 数值文本
            var valueText = Text(root, "Value", 0f, 0f, valueW, h, fmt(value),
                Theme.FontTiny, Theme.TextMuted, TextAlignmentOptions.MidlineRight);

            const float trackH = 6f;
            float tx = valueW + 14f;
            Rect(root, "Track", tx, (h - trackH) * 0.5f, trackW, trackH, Theme.Track, false);
            var fill = Rect(root, "Fill", tx, (h - trackH) * 0.5f, 0f, trackH, Theme.Accent, false);
            var handle = Rect(root, "Handle", tx, (h - 34f) * 0.5f, 8f, 34f, Color.white, false);

            var slider = root.gameObject.AddComponent<UnityEngine.UI.Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = integer;
            slider.SetValueWithoutNotify(Mathf.Clamp(value, min, max));
            // Unity 的 Slider 会把 handleRect 挪到 value 对应的位置，但那是
            // 它在下一帧布局里做的。为了让**建完的瞬间**手柄就位（以及离线
            // 预览能画对），这里显式摆一次。
            {
                float t = Normalized(slider.value, min, max);
                float trackX = valueW + 14f;
                handle.anchoredPosition = new Vector2(trackX + t * trackW - 4f,
                    handle.anchoredPosition.y);
                slider.fillRect.sizeDelta = new Vector2(trackW * t, trackH);
            }

            slider.onValueChanged.AddListener(v =>
            {
                float snapped = integer ? Mathf.Round(v) : v;
                slider.fillRect.sizeDelta = new Vector2(
                    trackW * Normalized(snapped, min, max), trackH);
                valueText.text = fmt(snapped);
                onChanged?.Invoke(snapped);
            });
            return slider;
        }

        private static float Normalized(float v, float min, float max)
            => max - min < 0.0001f ? 0f : Mathf.Clamp01((v - min) / (max - min));

        // ── 下拉 ────────────────────────────────────────────────
        internal static TMP_Dropdown DropDown(Transform row, float top,
            string[] options, int selected, Action<int> onChanged)
        {
            const float w = Theme.CtlDropW, h = Theme.CtlDropH;
            var root = RectRight(row, "DropDown", (Theme.RowH - h) * 0.5f, w, h,
                Theme.Control, true);

            var label = TextFill(root, "Label", "", Theme.FontSmall, Theme.Text);
            var lrt = (RectTransform)label.transform;
            Stretch(lrt, 14f, 0f, 32f, 0f);

            var arrow = TextFill(root, "Arrow", "▾", Theme.FontSmall, Theme.TextMuted,
                TextAlignmentOptions.Center);
            var art = (RectTransform)arrow.transform;
            art.anchorMin = new Vector2(1f, 0f);
            art.anchorMax = new Vector2(1f, 1f);
            art.pivot = new Vector2(1f, 0.5f);
            art.sizeDelta = new Vector2(28f, 0f);
            art.anchoredPosition = Vector2.zero;

            var dropdown = root.gameObject.AddComponent<TMP_Dropdown>();
            dropdown.targetGraphic = root.GetComponent<Image>();
            dropdown.itemText = label;

            // 展开层挂在行的下面（root 本身有 Image 挡着，放它里面会被压住）。
            // 用 RectRight 与下拉本体对齐——同样不依赖行宽。
            var template = RectRight(root.parent, "DropList", (Theme.RowH - h) * 0.5f + h,
                w, 0f, new Color(0.16f, 0.16f, 0.20f, 1f), true);
            template.gameObject.SetActive(false);
            template.sizeDelta = new Vector2(w, 0f);
            template.pivot = new Vector2(1f, 1f);

            var items = new List<TMP_Text>(options.Length);
            float y = 0f;
            for (int i = 0; i < options.Length; i++)
            {
                var itemRt = Rect(template, "Item" + i, 0f, y, w, 44f,
                    new Color(0f, 0f, 0f, 0f), true);
                var t = TextFill(itemRt, "Text", options[i], Theme.FontSmall, Theme.Text);
                var trt = (RectTransform)t.transform;
                Stretch(trt, 14f, 0f, 12f, 0f);
                var tog = itemRt.gameObject.AddComponent<Toggle>();
                tog.targetGraphic = itemRt.GetComponent<Image>();
                var cb = tog.colors;
                cb.normalColor = new Color(0f, 0f, 0f, 0f);
                cb.highlightedColor = new Color(1f, 1f, 1f, 0.13f);
                cb.selectedColor = Theme.Accent;
                cb.fadeDuration = 0.05f;
                tog.colors = cb;
                items.Add(t);
                y += 44f;
            }
            template.sizeDelta = new Vector2(w, y);

            dropdown.template = template;
            int cur = Mathf.Clamp(selected, 0, Mathf.Max(0, options.Length - 1));
            dropdown.SetValueWithoutNotify(cur);
            // **必须在 SetValueWithoutNotify 之后**赋值：它会按内部选项表回填
            // itemText，先写的话会被它清空（真实 Unity 遍历模板项，桩件直接
            // 查表——两边都覆盖先前赋的值）。顺序反了下拉框就永远是空的。
            label.text = options[cur];
            dropdown.onValueChanged.AddListener(i =>
            {
                label.text = options[Mathf.Clamp(i, 0, options.Length - 1)];
                onChanged?.Invoke(i);
            });
            return dropdown;
        }

        // ── 文本输入 ────────────────────────────────────────────
        internal static TMP_InputField Input(Transform row, float top,
            string value, string placeholder, Action<string> onChanged)
        {
            const float w = Theme.CtlInputW, h = Theme.CtlInputH;
            var root = RectRight(row, "Input", (Theme.RowH - h) * 0.5f, w, h,
                Theme.Control, true);

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(root, false);
            var taRt = (RectTransform)textGo.transform;
            Stretch(taRt, 14f, 0f, 14f, 0f);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            UiFonts.Apply(text);
            text.fontSize = Theme.FontSmall * Theme.TextScale;
            text.color = Theme.Text;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;

            var phGo = new GameObject("Placeholder", typeof(RectTransform));
            phGo.transform.SetParent(root, false);
            var phRt = (RectTransform)phGo.transform;
            Stretch(phRt, 14f, 0f, 14f, 0f);
            var ph = phGo.AddComponent<TextMeshProUGUI>();
            UiFonts.Apply(ph);
            ph.fontSize = Theme.FontSmall * Theme.TextScale;
            ph.color = Theme.TextFaint;
            ph.raycastTarget = false;
            ph.text = placeholder;

            var field = root.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = root.GetComponent<Image>();
            field.textViewport = taRt;
            field.textComponent = text;
            field.placeholder = ph;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.SetTextWithoutNotify(value ?? string.Empty);
            field.onValueChanged.AddListener(v => onChanged?.Invoke(v));
            return field;
        }
    }
}
