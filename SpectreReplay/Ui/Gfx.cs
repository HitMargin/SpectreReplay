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
        internal static GameObject Toggle(Transform row, float left, float top,
            bool value, Action<bool> onChanged)
        {
            var bg = Rect(row, "Toggle", left, top, Theme.CtlToggleW, Theme.CtlToggleH,
                value ? Theme.Accent : Theme.Control, true);
            // 轨道用一个小圆角矩形即可；Unity 内置 UI 不带圆角 Sprite，
            // 用方形 + 一个白色方块旋钮，靠缩进关系读出"开/关"。
            var knob = Rect(bg, "Knob", 16f, 4f, 24f, 24f, Color.white, false);

            bool current = value;
            MakeButton(bg.gameObject, Color.white, Color.white, new Color(0.8f, 0.8f, 0.8f, 1f),
                () =>
                {
                    current = !current;
                    bg.GetComponent<Image>().color = current ? Theme.Accent : Theme.Control;
                    knob.anchoredPosition = new Vector2(current ? 32f : 16f, -4f);
                    onChanged?.Invoke(current);
                });
            return bg.gameObject;
        }

        // ── 按钮 ────────────────────────────────────────────────
        internal static Button Button(Transform row, float left, float top, float width,
            string label, bool primary, Action onClick)
        {
            var h = Theme.CtlButtonH;
            var bg = Rect(row, "Button", left, top + (Theme.RowH - h) * 0.5f, width, h,
                primary ? Theme.Accent : Theme.Control, true);
            TextFill(bg, "Text", label, Theme.FontSmall,
                primary ? Theme.TextOnAccent : Theme.Text, TextAlignmentOptions.Center);
            var btn = MakeButton(bg.gameObject, Color.white,
                new Color(1.25f, 1.25f, 1.25f, 1f), new Color(0.75f, 0.75f, 0.75f, 1f), onClick);
            return btn;
        }

        // ── 滑条 ────────────────────────────────────────────────
        internal static UnityEngine.UI.Slider Slider(Transform row, float left, float top, float value,
            float min, float max, bool integer, Action<float> onChanged,
            Func<float, string> format = null)
        {
            const float w = Theme.CtlSliderW;
            float h = Theme.CtlSliderH;
            var root = Rect(row, "Slider", left, top + (Theme.RowH - h) * 0.5f, w, h,
                new Color(0f, 0f, 0f, 0f), true);

            const float trackH = 6f;
            Rect(root, "Track", 0f, (h - trackH) * 0.5f, w, trackH, Theme.Track, false);
            var fill = Rect(root, "Fill", 0f, (h - trackH) * 0.5f, 0f, trackH, Theme.Accent, false);
            var handle = Rect(root, "Handle", 0f, -3f, 16f, h + 6f, Color.white, false);

            var slider = root.gameObject.AddComponent<UnityEngine.UI.Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = integer;
            slider.SetValueWithoutNotify(Mathf.Clamp(value, min, max));
            // fillAmount 必须在设完 value 后再刷一次，否则初始填充是 0
            slider.fillRect.sizeDelta = new Vector2(w * Normalized(slider.value, min, max), trackH);

            Func<float, string> fmt = format ?? (v => integer
                ? ((int)Mathf.Round(v)).ToString()
                : v.ToString("0.##"));
            Text(root, "Value", 0f, h + 6f, w, 24f, fmt(slider.value), Theme.FontTiny,
                Theme.TextMuted, TextAlignmentOptions.MidlineRight);

            slider.onValueChanged.AddListener(v =>
            {
                float snapped = integer ? Mathf.Round(v) : v;
                slider.fillRect.sizeDelta = new Vector2(
                    w * Normalized(snapped, min, max), trackH);
                var lbl = root.Find("Value")?.GetComponent<TMP_Text>();
                if (lbl != null)
                {
                    lbl.text = fmt(snapped);
                }
                onChanged?.Invoke(snapped);
            });
            return slider;
        }

        private static float Normalized(float v, float min, float max)
            => max - min < 0.0001f ? 0f : Mathf.Clamp01((v - min) / (max - min));

        // ── 下拉 ────────────────────────────────────────────────
        internal static TMP_Dropdown DropDown(Transform row, float left, float top,
            string[] options, int selected, Action<int> onChanged)
        {
            const float w = Theme.CtlDropW, h = Theme.CtlDropH;
            var root = Rect(row, "DropDown", left, top + (Theme.RowH - h) * 0.5f, w, h,
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

            // 展开层：挂在 root 外面（root 有 Image 挡着，且布局受行约束），
            // 直接作为 root 的兄弟，锚在 root 正下方。
            var template = Rect(root.parent, "DropList", left, top + (Theme.RowH - h) * 0.5f + h,
                w, 0f, new Color(0.16f, 0.16f, 0.20f, 1f), true);
            template.gameObject.SetActive(false);
            // ignoreLayout 已无意义（没有布局系统），但保留布局元素不影响
            var fitter = template.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            template.sizeDelta = new Vector2(w, 0f);
            template.pivot = new Vector2(0f, 1f);

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
            dropdown.SetValueWithoutNotify(Mathf.Clamp(selected, 0,
                Mathf.Max(0, options.Length - 1)));
            dropdown.onValueChanged.AddListener(i => onChanged?.Invoke(i));
            return dropdown;
        }

        // ── 文本输入 ────────────────────────────────────────────
        internal static TMP_InputField Input(Transform row, float left, float top,
            string value, string placeholder, Action<string> onChanged)
        {
            const float w = Theme.CtlInputW, h = Theme.CtlInputH;
            var root = Rect(row, "Input", left, top + (Theme.RowH - h) * 0.5f, w, h,
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
