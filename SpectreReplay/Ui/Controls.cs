using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 控件层。每个控件都是"一行内的右侧槽"，签名统一为
    ///     Build(Transform row, ...)
    /// 页面代码只描述"这一行长什么样"，不接触 GameObject 细节。
    ///
    /// 与 QuartzUi 的区别：QuartzUi 的 Toggle/Slider/DropDown 各自再包一层
    /// Collapsible/DictRows 的联动逻辑；这里保持扁平——控件只管自己那一行，
    /// 跨行联动交给调用方用回调表达。
    /// </summary>
    internal static class Controls
    {
        // ── 开关 ────────────────────────────────────────────────
        /// <summary>开关。返回控件本体（可再挂 enabled 之类）。</summary>
        internal static GameObject Toggle(Transform row, string label, bool value,
            Action<bool> onChanged, bool mutedLabel = false)
        {
            var slot = Row.Slot(row, 54f, 32f);
            var go = new GameObject("Toggle", typeof(RectTransform));
            go.transform.SetParent(slot, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(54f, 32f);

            var bg = go.AddComponent<Image>();
            bg.color = value ? UiSkin.Accent : UiSkin.TrackBg;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;

            var knobGo = new GameObject("Knob", typeof(RectTransform));
            knobGo.transform.SetParent(rt, false);
            var knob = (RectTransform)knobGo.transform;
            knob.sizeDelta = new Vector2(26f, 26f);
            var knobImg = knobGo.AddComponent<Image>();
            knobImg.color = Color.white;
            knob.anchorMin = knob.anchorMax = new Vector2(0.5f, 0.5f);
            knob.anchoredPosition = new Vector2(value ? 13f : -13f, 0f);

            bool current = value;
            btn.onClick.AddListener(() =>
            {
                current = !current;
                bg.color = current ? UiSkin.Accent : UiSkin.TrackBg;
                knob.anchoredPosition = new Vector2(current ? 13f : -13f, 0f);
                onChanged?.Invoke(current);
            });

            Row.Label(row, label, 0.62f, mutedLabel);
            return go;
        }

        // ── 按钮 ────────────────────────────────────────────────
        internal static Button Button(Transform row, string label, Action onClick,
            bool primary = false, float width = 150f)
        {
            var slot = Row.Slot(row, width, 38f);
            var go = new GameObject("Button", typeof(RectTransform));
            go.transform.SetParent(slot, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(width, 38f);
            var bg = go.AddComponent<Image>();
            bg.color = primary ? UiSkin.Accent : UiSkin.TrackBg;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            btn.onClick.AddListener(() => onClick?.Invoke());

            var t = Row.NewText(go.transform, "Text", UiSkin.FontBody, Color.white);
            StretchText(t.rectTransform);
            t.text = label;
            t.alignment = TextAlignmentOptions.Center;
            return btn;
        }

        // ── 滑条 ────────────────────────────────────────────────
        /// <summary>滑条。onChanged 收到的是已按 min/max/步长规整后的值。</summary>
        internal static Slider Slider(Transform row, string label, float value,
            float min, float max, bool integer, Action<float> onChanged,
            Func<float, string> format = null)
        {
            const float width = 320f;
            var slot = Row.Slot(row, width, 34f);
            var go = new GameObject("Slider", typeof(RectTransform));
            go.transform.SetParent(slot, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(width, 34f);

            var slider = go.AddComponent<UnityEngine.UI.Slider>();

            var bg = new GameObject("Track", typeof(RectTransform));
            bg.transform.SetParent(rt, false);
            var bgRt = (RectTransform)bg.transform;
            bgRt.anchorMin = new Vector2(0f, 0.5f);
            bgRt.anchorMax = new Vector2(1f, 0.5f);
            bgRt.offsetMin = new Vector2(0f, -3f);
            bgRt.offsetMax = new Vector2(0f, 3f);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = UiSkin.TrackBg;

            var fillArea = new GameObject("FillArea", typeof(RectTransform));
            fillArea.transform.SetParent(rt, false);
            var faRt = (RectTransform)fillArea.transform;
            Stretch(faRt);
            var fill = new GameObject("Fill", typeof(RectTransform));
            fill.transform.SetParent(faRt, false);
            var fillRt = (RectTransform)fill.transform;
            fillRt.sizeDelta = Vector2.zero;
            var fillImg = fill.AddComponent<Image>();
            fillImg.color = UiSkin.FillBg;

            var handleArea = new GameObject("HandleArea", typeof(RectTransform));
            handleArea.transform.SetParent(rt, false);
            var haRt = (RectTransform)handleArea.transform;
            Stretch(haRt);
            var handle = new GameObject("Handle", typeof(RectTransform));
            handle.transform.SetParent(haRt, false);
            var hRt = (RectTransform)handle.transform;
            hRt.sizeDelta = new Vector2(18f, 28f);
            var hImg = handle.AddComponent<Image>();
            hImg.color = Color.white;

            slider.fillRect = fillRt;
            slider.handleRect = hRt;
            slider.targetGraphic = hImg;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = Mathf.Clamp(value, min, max);
            slider.wholeNumbers = integer;

            var valueText = Row.NewText(row, "Value", UiSkin.FontSmall, UiSkin.TextMuted);
            var vt = valueText.rectTransform;
            vt.anchorMin = new Vector2(0.62f, 0f);
            vt.anchorMax = new Vector2(0.74f, 1f);
            vt.offsetMin = new Vector2(0f, 2f);
            vt.offsetMax = new Vector2(0f, -2f);
            valueText.alignment = TextAlignmentOptions.MidlineRight;

            Func<float, string> fmt = format ?? (v => integer
                ? ((int)Mathf.Round(v)).ToString()
                : v.ToString("0.##"));
            valueText.text = fmt(slider.value);
            Row.Label(row, label, 0.62f);

            slider.onValueChanged.AddListener(v =>
            {
                float snapped = integer ? Mathf.Round(v) : v;
                valueText.text = fmt(snapped);
                onChanged?.Invoke(snapped);
            });
            return slider;
        }

        // ── 下拉 ────────────────────────────────────────────────
        /// <summary>下拉选择。options 为显示文本，onChanged 传下标。</summary>
        internal static TMP_Dropdown DropDown(Transform row, string label, string[] options,
            int selected, Action<int> onChanged, float width = 260f)
        {
            var slot = Row.Slot(row, width, 38f);
            var go = new GameObject("DropDown", typeof(RectTransform));
            go.transform.SetParent(slot, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(width, 38f);

            var bg = go.AddComponent<Image>();
            bg.color = UiSkin.TrackBg;
            var dropdown = go.AddComponent<TMP_Dropdown>();
            dropdown.targetGraphic = bg;

            var labelT = Row.NewText(go.transform, "Label", UiSkin.FontSmall, UiSkin.TextNormal);
            var lt = labelT.rectTransform;
            lt.anchorMin = Vector2.zero;
            lt.anchorMax = Vector2.one;
            lt.offsetMin = new Vector2(12f, 0f);
            lt.offsetMax = new Vector2(-26f, 0f);
            labelT.alignment = TextAlignmentOptions.MidlineLeft;

            var template = new GameObject("Template", typeof(RectTransform));
            template.transform.SetParent(rt, false);
            var trt = (RectTransform)template.transform;
            trt.anchorMin = new Vector2(0f, 0f);
            trt.anchorMax = new Vector2(1f, 0f);
            trt.pivot = new Vector2(0.5f, 1f);
            trt.anchoredPosition = Vector2.zero;
            trt.sizeDelta = new Vector2(0f, 190f);
            template.SetActive(false);
            var tImg = template.AddComponent<Image>();
            tImg.color = new Color(0.16f, 0.16f, 0.20f, 1f);
            var scrollGo = new GameObject("Scroll", typeof(RectTransform));
            scrollGo.transform.SetParent(trt, false);
            var sRt = (RectTransform)scrollGo.transform;
            sRt.anchorMin = Vector2.zero;
            sRt.anchorMax = Vector2.one;
            sRt.offsetMin = Vector2.zero;
            sRt.offsetMax = Vector2.zero;
            var scroll = scrollGo.AddComponent<ScrollRect>();
            var viewport = new GameObject("Viewport", typeof(RectTransform));
            viewport.transform.SetParent(sRt, false);
            var vpRt = (RectTransform)viewport.transform;
            vpRt.anchorMin = Vector2.zero;
            vpRt.anchorMax = Vector2.one;
            vpRt.offsetMin = new Vector2(0f, 0f);
            vpRt.offsetMax = new Vector2(0f, 0f);
            viewport.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            var itemRoot = new GameObject("Items", typeof(RectTransform));
            itemRoot.transform.SetParent(vpRt, false);
            var irt = (RectTransform)itemRoot.transform;
            irt.anchorMin = new Vector2(0f, 1f);
            irt.anchorMax = new Vector2(1f, 1f);
            irt.pivot = new Vector2(0.5f, 1f);
            irt.sizeDelta = new Vector2(0f, 40f);
            var vLayout = itemRoot.AddComponent<VerticalLayoutGroup>();
            vLayout.spacing = 0f;
            vLayout.childControlWidth = true;
            vLayout.childControlHeight = true;
            vLayout.childForceExpandWidth = true;
            vLayout.childForceExpandHeight = false;
            itemRoot.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = irt;
            scroll.viewport = vpRt;
            scroll.horizontal = false;
            scroll.vertical = true;

            dropdown.template = trt;
            dropdown.itemText = labelT;

            var list = new List<TMP_Text>();
            for (int i = 0; i < options.Length; i++)
            {
                var itemGo = new GameObject("Item" + i, typeof(RectTransform));
                itemGo.transform.SetParent(irt, false);
                var itemRt = (RectTransform)itemGo.transform;
                var le = itemGo.AddComponent<LayoutElement>();
                le.preferredHeight = 40f;
                var itemImg = itemGo.AddComponent<Image>();
                itemImg.color = new Color(0f, 0f, 0f, 0f);
                var itemToggle = itemGo.AddComponent<Button>();
                itemToggle.targetGraphic = itemImg;
                var it = Row.NewText(itemGo.transform, "Text", UiSkin.FontSmall, UiSkin.TextNormal);
                var itr = it.rectTransform;
                itr.anchorMin = Vector2.zero;
                itr.anchorMax = Vector2.one;
                itr.offsetMin = new Vector2(12f, 0f);
                itr.offsetMax = new Vector2(-8f, 0f);
                it.text = options[i];
                it.alignment = TextAlignmentOptions.MidlineLeft;
                list.Add(it);
            }
            // TMP_Dropdown 没有 itemImages 字段（那是 Unity UI.Dropdown 才有的），
            // 选中高亮靠每个 item 的 ColorBlock：它会切 targetGraphic 的颜色，
            // 而 targetGraphic 就是这一行自己的底色 Image。
            for (int i = 0; i < options.Length; i++)
            {
                var itemRt = (RectTransform)list[i].rectTransform.parent;
                var itemImg = itemRt.GetComponent<Image>();
                var tog = itemRt.gameObject.AddComponent<Toggle>();
                tog.targetGraphic = itemImg;
                var cb = tog.colors;
                cb.normalColor = new Color(0f, 0f, 0f, 0f);
                cb.highlightedColor = new Color(1f, 1f, 1f, 0.10f);
                cb.selectedColor = UiSkin.Accent;
                cb.disabledColor = new Color(0f, 0f, 0f, 0f);
                tog.colors = cb;
            }

            int cur = Mathf.Clamp(selected, 0, Mathf.Max(0, options.Length - 1));
            dropdown.SetValueWithoutNotify(cur);
            Row.Label(row, label, 0.62f);
            dropdown.onValueChanged.AddListener(i => onChanged?.Invoke(i));
            return dropdown;
        }

        // ── 文本输入 ────────────────────────────────────────────
        internal static TMP_InputField Input(Transform row, string label, string value,
            Action<string> onChanged, string placeholder = "", float width = 380f)
        {
            var slot = Row.Slot(row, width, 38f);
            var go = new GameObject("Input", typeof(RectTransform));
            go.transform.SetParent(slot, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(width, 38f);
            var bg = go.AddComponent<Image>();
            bg.color = UiSkin.TrackBg;

            var field = go.AddComponent<TMP_InputField>();

            var textArea = new GameObject("Text", typeof(RectTransform));
            textArea.transform.SetParent(rt, false);
            var taRt = (RectTransform)textArea.transform;
            taRt.anchorMin = Vector2.zero;
            taRt.anchorMax = Vector2.one;
            taRt.offsetMin = new Vector2(12f, 0f);
            taRt.offsetMax = new Vector2(-12f, 0f);
            var text = taRt.gameObject.AddComponent<TextMeshProUGUI>();
            text.fontSize = UiSkin.FontSmall * UiSkin.TextScale;
            text.color = UiSkin.TextNormal;
            text.raycastTarget = false;

            var phArea = new GameObject("Placeholder", typeof(RectTransform));
            phArea.transform.SetParent(rt, false);
            var phRt = (RectTransform)phArea.transform;
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(12f, 0f);
            phRt.offsetMax = new Vector2(-12f, 0f);
            var ph = phRt.gameObject.AddComponent<TextMeshProUGUI>();
            ph.fontSize = UiSkin.FontSmall * UiSkin.TextScale;
            ph.color = UiSkin.TextDisabled;
            ph.raycastTarget = false;
            ph.text = placeholder;

            field.textViewport = taRt;
            field.textComponent = text;
            field.placeholder = ph;
            field.targetGraphic = bg;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.SetTextWithoutNotify(value ?? string.Empty);

            Row.Label(row, label, 0.62f);
            field.onValueChanged.AddListener(v => onChanged?.Invoke(v));
            return field;
        }

        // ── 纯说明文字（一整行，不是控件）────────────────────────
        internal static TMP_Text Note(Transform parent, string text, bool muted = true)
        {
            var rt = Row.Create(parent, 0f);
            var t = Row.NewText(rt, "Note", muted ? UiSkin.FontSmall : UiSkin.FontBody,
                muted ? UiSkin.TextMuted : UiSkin.TextNormal);
            t.rectTransform.anchorMin = Vector2.zero;
            t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(UiSkin.PadX, 4f);
            t.rectTransform.offsetMax = new Vector2(-UiSkin.PadX, -4f);
            t.text = text;
            t.alignment = TextAlignmentOptions.TopLeft;
            var fitter = rt.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return t;
        }

        internal static TMP_Text SectionTitle(Transform parent, string text)
        {
            var rt = Row.Create(parent, 40f);
            rt.gameObject.AddComponent<Image>().color = UiSkin.HeaderBg;
            var t = Row.NewText(rt, "Title", UiSkin.FontBody, UiSkin.Accent);
            t.rectTransform.anchorMin = Vector2.zero;
            t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(UiSkin.PadX, 0f);
            t.rectTransform.offsetMax = new Vector2(-UiSkin.PadX, 0f);
            t.text = text;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            return t;
        }

        private static void StretchText(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
