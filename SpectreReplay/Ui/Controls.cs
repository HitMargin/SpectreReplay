using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 控件层。每个控件占行右侧的一个定宽槽，槽的尺寸由布局系统给，
    /// 控件自身只做内部排布 —— 与上一版最大的区别就在这里。
    /// </summary>
    internal static class Controls
    {
        // ── 开关 ────────────────────────────────────────────────
        internal static GameObject Toggle(RowRef row, string label, bool value,
            Action<bool> onChanged, bool mutedLabel = false)
        {
            const float W = 56f, H = 32f;
            var slot = Row.Slot(row, W + 12f, H);
            var go = new GameObject("Toggle", typeof(RectTransform));
            go.transform.SetParent(slot, false);
            var rt = (RectTransform)go.transform;
            Row.Stretch(rt);

            var bg = go.AddComponent<Image>();
            bg.color = value ? UiSkin.Accent : UiSkin.TrackBg;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;

            var knobGo = new GameObject("Knob", typeof(RectTransform));
            knobGo.transform.SetParent(rt, false);
            var knob = (RectTransform)knobGo.transform;
            var knobImg = knobGo.AddComponent<Image>();
            knobImg.color = Color.white;
            // 靠 anchor 左右切，不靠 anchoredPosition 数值——锚点法对
            // 父级尺寸变化是自适应的，尺寸被布局系统改也不会跑偏
            knob.sizeDelta = new Vector2(24f, 24f);
            knob.anchorMin = new Vector2(0f, 0.5f);
            knob.anchorMax = new Vector2(0f, 0.5f);
            knob.pivot = new Vector2(0.5f, 0.5f);
            knob.anchoredPosition = new Vector2(15f, 0f);

            bool current = value;
            btn.onClick.AddListener(() =>
            {
                current = !current;
                bg.color = current ? UiSkin.Accent : UiSkin.TrackBg;
                knob.anchoredPosition = new Vector2(current ? 15f : -15f, 0f);
                onChanged?.Invoke(current);
            });

            Row.Label(row, label, mutedLabel);
            return go;
        }

        // ── 按钮 ────────────────────────────────────────────────
        internal static Button Button(RowRef row, string label, Action onClick,
            bool primary = false, float width = 160f)
        {
            var slot = Row.Slot(row, width, 38f);
            var go = new GameObject("Button", typeof(RectTransform));
            go.transform.SetParent(slot, false);
            var rt = (RectTransform)go.transform;
            Row.Stretch(rt);

            var bg = go.AddComponent<Image>();
            bg.color = primary ? UiSkin.Accent : UiSkin.TrackBg;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            var cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            cb.fadeDuration = 0.08f;
            btn.colors = cb;
            btn.onClick.AddListener(() => onClick?.Invoke());

            var t = Row.NewText(rt, "Text", UiSkin.FontSmall, Color.white);
            Row.Stretch(t.rectTransform);
            t.text = label;
            t.alignment = TextAlignmentOptions.Center;
            return btn;
        }

        // ── 滑条 ────────────────────────────────────────────────
        internal static Slider Slider(RowRef row, string label, float value,
            float min, float max, bool integer, Action<float> onChanged,
            Func<float, string> format = null)
        {
            var slot = Row.Slot(row, 330f, 30f);
            var go = new GameObject("Slider", typeof(RectTransform));
            go.transform.SetParent(slot, false);
            var rt = (RectTransform)go.transform;
            Row.Stretch(rt);

            var slider = go.AddComponent<UnityEngine.UI.Slider>();

            var track = new GameObject("Track", typeof(RectTransform));
            track.transform.SetParent(rt, false);
            var tRt = (RectTransform)track.transform;
            tRt.anchorMin = new Vector2(0f, 0.5f);
            tRt.anchorMax = new Vector2(1f, 0.5f);
            tRt.pivot = new Vector2(0.5f, 0.5f);
            tRt.sizeDelta = new Vector2(0f, 6f);
            tRt.anchoredPosition = Vector2.zero;
            tRt.gameObject.AddComponent<Image>().color = UiSkin.TrackBg;

            var fillArea = new GameObject("FillArea", typeof(RectTransform));
            fillArea.transform.SetParent(rt, false);
            var faRt = (RectTransform)fillArea.transform;
            Row.Stretch(faRt);
            var fill = new GameObject("Fill", typeof(RectTransform));
            fill.transform.SetParent(faRt, false);
            var fRt = (RectTransform)fill.transform;
            fRt.sizeDelta = new Vector2(0f, 6f);
            var fImg = fill.AddComponent<Image>();
            fImg.color = UiSkin.Accent;

            var handleArea = new GameObject("HandleArea", typeof(RectTransform));
            handleArea.transform.SetParent(rt, false);
            var haRt = (RectTransform)handleArea.transform;
            Row.Stretch(haRt);
            var handle = new GameObject("Handle", typeof(RectTransform));
            handle.transform.SetParent(haRt, false);
            var hRt = (RectTransform)handle.transform;
            hRt.sizeDelta = new Vector2(16f, 26f);
            var hImg = handle.AddComponent<Image>();
            hImg.color = Color.white;

            slider.fillRect = fRt;
            slider.handleRect = hRt;
            slider.targetGraphic = hImg;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = integer;
            slider.SetValueWithoutNotify(Mathf.Clamp(value, min, max));

            Func<float, string> fmt = format ?? (v => integer
                ? ((int)Mathf.Round(v)).ToString()
                : v.ToString("0.##"));

            // 数值文本挂在滑条槽**内部下沿**右端：它跟着控件走，
            // 不占行内另一个格（行内只有"标签 + 控件"两段）。
            var valueText = Row.NewText(slot, "Value", UiSkin.FontSmall, UiSkin.TextMuted);
            var vRt = valueText.rectTransform;
            vRt.anchorMin = new Vector2(1f, 0f);
            vRt.anchorMax = new Vector2(1f, 0f);
            vRt.pivot = new Vector2(1f, 0f);
            vRt.sizeDelta = new Vector2(120f, 24f);
            vRt.anchoredPosition = new Vector2(0f, -2f);
            valueText.alignment = TextAlignmentOptions.MidlineRight;
            valueText.text = fmt(slider.value);

            Row.Label(row, label);

            slider.onValueChanged.AddListener(v =>
            {
                float snapped = integer ? Mathf.Round(v) : v;
                valueText.text = fmt(snapped);
                onChanged?.Invoke(snapped);
            });
            return slider;
        }

        // ── 下拉 ────────────────────────────────────────────────
        internal static TMP_Dropdown DropDown(RowRef row, string label, string[] options,
            int selected, Action<int> onChanged, float width = 280f)
        {
            var slot = Row.Slot(row, width, 40f);
            var go = new GameObject("DropDown", typeof(RectTransform));
            go.transform.SetParent(slot, false);
            var rt = (RectTransform)go.transform;
            Row.Stretch(rt);

            var bg = go.AddComponent<Image>();
            bg.color = UiSkin.TrackBg;
            var dropdown = go.AddComponent<TMP_Dropdown>();
            dropdown.targetGraphic = bg;

            var labelT = Row.NewText(rt, "Label", UiSkin.FontSmall, UiSkin.TextNormal);
            var lt = labelT.rectTransform;
            lt.anchorMin = Vector2.zero;
            lt.anchorMax = Vector2.one;
            lt.offsetMin = new Vector2(14f, 0f);
            lt.offsetMax = new Vector2(-14f, 0f);
            labelT.alignment = TextAlignmentOptions.MidlineLeft;

            var arrow = Row.NewText(rt, "Arrow", UiSkin.FontSmall, UiSkin.TextMuted);
            var ar = arrow.rectTransform;
            ar.anchorMin = new Vector2(1f, 0f);
            ar.anchorMax = new Vector2(1f, 1f);
            ar.pivot = new Vector2(1f, 0.5f);
            ar.sizeDelta = new Vector2(30f, 0f);
            ar.anchoredPosition = Vector2.zero;
            arrow.alignment = TextAlignmentOptions.Center;
            arrow.text = "▾";

            // 模板：展开的列表
            var template = new GameObject("Template", typeof(RectTransform));
            template.transform.SetParent(rt, false);
            var trt = (RectTransform)template.transform;
            trt.anchorMin = new Vector2(0f, 0f);
            trt.anchorMax = new Vector2(1f, 0f);
            trt.pivot = new Vector2(0.5f, 1f);
            trt.anchoredPosition = new Vector2(0f, -4f);
            trt.sizeDelta = new Vector2(0f, 0f);
            template.SetActive(false);
            template.AddComponent<Image>().color = new Color(0.18f, 0.18f, 0.22f, 1f);
            var tLayout = template.AddComponent<LayoutElement>();
            tLayout.ignoreLayout = true;

            var tContent = new GameObject("Items", typeof(RectTransform));
            tContent.transform.SetParent(trt, false);
            var tcRt = (RectTransform)tContent.transform;
            tcRt.anchorMin = new Vector2(0f, 0f);
            tcRt.anchorMax = new Vector2(1f, 1f);
            tcRt.offsetMin = new Vector2(0f, 0f);
            tcRt.offsetMax = new Vector2(0f, 0f);
            tcRt.pivot = new Vector2(0.5f, 1f);
            tcRt.anchoredPosition = Vector2.zero;
            var vlg = tContent.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 0f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            var fit = tContent.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var tFit = trt.gameObject.AddComponent<ContentSizeFitter>();
            tFit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scrollGo = new GameObject("Scroll", typeof(RectTransform));
            scrollGo.transform.SetParent(trt, false);
            var sRt = (RectTransform)scrollGo.transform;
            Row.Stretch(sRt);
            var scroll = scrollGo.AddComponent<ScrollRect>();

            var viewport = new GameObject("Viewport", typeof(RectTransform));
            viewport.transform.SetParent(sRt, false);
            var vpRt = (RectTransform)viewport.transform;
            Row.Stretch(vpRt);
            var vpImg = viewport.AddComponent<Image>();
            vpImg.color = new Color(0f, 0f, 0f, 0.01f);
            var mask = viewport.AddComponent<RectMask2D>();

            var itemRoot = new GameObject("Items", typeof(RectTransform));
            itemRoot.transform.SetParent(vpRt, false);
            var irt = (RectTransform)itemRoot.transform;
            irt.anchorMin = new Vector2(0f, 1f);
            irt.anchorMax = new Vector2(1f, 1f);
            irt.pivot = new Vector2(0.5f, 1f);
            irt.sizeDelta = new Vector2(0f, 0f);
            var iVlg = itemRoot.AddComponent<VerticalLayoutGroup>();
            iVlg.childControlWidth = true;
            iVlg.childControlHeight = true;
            iVlg.childForceExpandWidth = true;
            iVlg.childForceExpandHeight = false;
            var iFit = itemRoot.AddComponent<ContentSizeFitter>();
            iFit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.content = irt;
            scroll.viewport = vpRt;
            scroll.horizontal = false;
            scroll.vertical = true;

            dropdown.template = trt;
            dropdown.itemText = labelT;

            for (int i = 0; i < options.Length; i++)
            {
                var itemGo = new GameObject("Item" + i, typeof(RectTransform));
                itemGo.transform.SetParent(irt, false);
                var itemRt = (RectTransform)itemGo.transform;
                var le = itemGo.AddComponent<LayoutElement>();
                le.preferredHeight = 42f;
                le.minHeight = 42f;
                var itemImg = itemGo.AddComponent<Image>();
                itemImg.color = new Color(0f, 0f, 0f, 0f);
                var tog = itemGo.AddComponent<Toggle>();
                tog.targetGraphic = itemImg;
                var cb = tog.colors;
                cb.normalColor = new Color(0f, 0f, 0f, 0f);
                cb.highlightedColor = new Color(1f, 1f, 1f, 0.12f);
                cb.selectedColor = UiSkin.Accent;
                cb.disabledColor = new Color(0f, 0f, 0f, 0f);
                cb.fadeDuration = 0.06f;
                tog.colors = cb;

                var it = Row.NewText(itemRt, "Text", UiSkin.FontSmall, UiSkin.TextNormal);
                Row.Stretch(it.rectTransform);
                it.rectTransform.offsetMin = new Vector2(14f, 0f);
                it.rectTransform.offsetMax = new Vector2(-14f, 0f);
                it.text = options[i];
                it.alignment = TextAlignmentOptions.MidlineLeft;
            }

            int cur = Mathf.Clamp(selected, 0, Mathf.Max(0, options.Length - 1));
            dropdown.SetValueWithoutNotify(cur);
            Row.Label(row, label);
            dropdown.onValueChanged.AddListener(i => onChanged?.Invoke(i));
            return dropdown;
        }

        // ── 文本输入 ────────────────────────────────────────────
        internal static TMP_InputField Input(RowRef row, string value,
            Action<string> onChanged, string placeholder = "", float width = 400f)
        {
            var slot = Row.Slot(row, width, 40f);
            var go = new GameObject("Input", typeof(RectTransform));
            go.transform.SetParent(slot, false);
            var rt = (RectTransform)go.transform;
            Row.Stretch(rt);

            var bg = go.AddComponent<Image>();
            bg.color = UiSkin.TrackBg;
            var field = go.AddComponent<TMP_InputField>();
            field.targetGraphic = bg;

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(rt, false);
            var taRt = (RectTransform)textGo.transform;
            Row.Stretch(taRt);
            taRt.offsetMin = new Vector2(14f, 0f);
            taRt.offsetMax = new Vector2(-14f, 0f);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            UiFonts.Apply(text);
            text.fontSize = UiSkin.FontSmall * UiSkin.TextScale;
            text.color = UiSkin.TextNormal;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.Normal;

            var phGo = new GameObject("Placeholder", typeof(RectTransform));
            phGo.transform.SetParent(rt, false);
            var phRt = (RectTransform)phGo.transform;
            Row.Stretch(phRt);
            phRt.offsetMin = new Vector2(14f, 0f);
            phRt.offsetMax = new Vector2(-14f, 0f);
            var ph = phGo.AddComponent<TextMeshProUGUI>();
            UiFonts.Apply(ph);
            ph.fontSize = UiSkin.FontSmall * UiSkin.TextScale;
            ph.color = UiSkin.TextDisabled;
            ph.raycastTarget = false;
            ph.text = placeholder;

            field.textViewport = taRt;
            field.textComponent = text;
            field.placeholder = ph;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.SetTextWithoutNotify(value ?? string.Empty);


            field.onValueChanged.AddListener(v => onChanged?.Invoke(v));
            return field;
        }

        // ── 说明文字（占整行）───────────────────────────────────
        internal static TMP_Text Note(Transform parent, string text)
        {
            // 固定高度，不再靠 ContentSizeFitter 撑——让布局系统去问文本要高度
            // 是上一版行高失控的来源之一。说明文案控制在两行内。
            var row = Row.Create(parent, UiSkin.NoteHeight, new Color(0f, 0f, 0f, 0f));
            var t = Row.NewText(row.Rect, "Note", UiSkin.FontSmall, UiSkin.TextMuted);
            var rt = t.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(UiSkin.PadX, 4f);
            rt.offsetMax = new Vector2(-UiSkin.PadX, -4f);
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.text = text;
            return t;
        }

        internal static TMP_Text SectionTitle(Transform parent, string text)
        {
            var row = Row.Create(parent, UiSkin.SectionHeight, UiSkin.HeaderBg);
            var t = Row.NewText(row.Rect, "Title", UiSkin.FontBody, UiSkin.Accent);
            var rt = t.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(UiSkin.PadX, 0f);
            rt.offsetMax = new Vector2(-UiSkin.PadX, 0f);
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.text = text;
            return t;
        }
    }
}
