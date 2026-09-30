using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 页面构建器：把内容从上往下堆，**y 坐标由自己累加**。
    ///
    /// 不挂 VerticalLayoutGroup、不用 LayoutElement、不用 ContentSizeFitter。
    /// 每一行的高度是常量，"下一行的 top = 上一行的 top + 行高 + 间距"，
    /// 这个式子能手算，也就永远不会出"控件铺满整行"那种事。
    /// 滚动靠 ScrollRect，内容高度由 Add 累加成 ContentHeight 报给外层。
    /// </summary>
    internal sealed class PageBuilder
    {
        private readonly RectTransform _parent;
        private float _y;
        private int _index;

        internal float ContentHeight => _y;
        internal RectTransform Parent => _parent;

        internal PageBuilder(RectTransform parent)
        {
            _parent = parent;
        }

        private float Next(float h, float gap)
        {
            float top = _y;
            _y += h + gap;
            return top;
        }

        /// <summary>一个标准行：左侧标签 + 右侧控件。控件在回调里建。</summary>
        internal RectTransform Row(string labelKey, float controlWidth)
        {
            float top = Next(Theme.RowH, Theme.RowGap);
            var row = Gfx.Rect(_parent, "Row", 0f, top, _parent.sizeDelta.x, Theme.RowH,
                Theme.Zebra(_index++), false);
            if (!string.IsNullOrEmpty(labelKey))
            {
                Gfx.TextFill(row, "Label", Loc.T(labelKey), Theme.FontBody, Theme.Text);
            }
            // 标签宽度 = 行宽 - 左边距 - 控件宽 - 间隙
            var lbl = row.Find("Label")?.GetComponent<RectTransform>();
            if (lbl != null)
            {
                Gfx.Stretch(lbl, Theme.PadX, 0f,
                    controlWidth + Theme.PadX + Theme.LabelGap, 0f);
            }
            return row;
        }

        /// <summary>空行（没有标签，控件从左边距开始）。</summary>
        internal RectTransform BlankRow(float controlWidth)
        {
            float top = Next(Theme.RowH, Theme.RowGap);
            return Gfx.Rect(_parent, "Row", 0f, top, _parent.sizeDelta.x, Theme.RowH,
                Theme.Zebra(_index++), false);
        }

        internal void Toggle(string labelKey, bool value, Action<bool> set)
        {
            var row = Row(labelKey, Theme.CtlToggleW);
            Gfx.Toggle(row, row.sizeDelta.x - Theme.PadX - Theme.CtlToggleW,
                (Theme.RowH - Theme.CtlToggleH) * 0.5f, value, set);
        }

        internal void Button(string labelKey, bool primary, Action onClick)
        {
            var row = Row(labelKey, 180f);
            Gfx.Button(row, row.sizeDelta.x - Theme.PadX - 180f,
                (Theme.RowH - Theme.CtlButtonH) * 0.5f, 180f, Loc.T(labelKey), primary, onClick);
        }

        internal void Slider(string labelKey, float value, float min, float max, bool integer,
            Action<float> set, Func<float, string> format = null)
        {
            var row = Row(labelKey, Theme.CtlSliderW);
            Gfx.Slider(row, row.sizeDelta.x - Theme.PadX - Theme.CtlSliderW,
                (Theme.RowH - Theme.CtlSliderH) * 0.5f, value, min, max, integer, set, format);
        }

        internal void Drop(string labelKey, string[] options, int selected, Action<int> set)
        {
            var row = Row(labelKey, Theme.CtlDropW);
            Gfx.DropDown(row, row.sizeDelta.x - Theme.PadX - Theme.CtlDropW,
                (Theme.RowH - Theme.CtlDropH) * 0.5f, options, selected, set);
        }

        internal void Input(string labelKey, string value, string placeholder, Action<string> set)
        {
            var row = Row(labelKey, Theme.CtlInputW);
            Gfx.Input(row, row.sizeDelta.x - Theme.PadX - Theme.CtlInputW,
                (Theme.RowH - Theme.CtlInputH) * 0.5f, value, placeholder, set);
        }

        internal void Note(string textKey)
        {
            float top = Next(Theme.NoteH, Theme.RowGap);
            var r = Gfx.Rect(_parent, "Note", Theme.PadX, top,
                _parent.sizeDelta.x - Theme.PadX * 2f, Theme.NoteH,
                new Color(0f, 0f, 0f, 0f), false);
            Gfx.Text(r, "Text", 0f, 0f, r.sizeDelta.x, Theme.NoteH, Loc.T(textKey),
                Theme.FontSmall, Theme.TextMuted);
        }

        internal void NoteText(string text)
        {
            float top = Next(Theme.NoteH, Theme.RowGap);
            var r = Gfx.Rect(_parent, "Note", Theme.PadX, top,
                _parent.sizeDelta.x - Theme.PadX * 2f, Theme.NoteH,
                new Color(0f, 0f, 0f, 0f), false);
            Gfx.Text(r, "Text", 0f, 0f, r.sizeDelta.x, Theme.NoteH, text,
                Theme.FontSmall, Theme.TextMuted);
        }

        /// <summary>分组标题：一条带底色的横条。</summary>
        internal void Group(string titleKey)
        {
            if (_y > 0f)
            {
                _y += 8f;
            }
            float top = Next(Theme.GroupHeadH, Theme.RowGap);
            var r = Gfx.Rect(_parent, "Group", 0f, top, _parent.sizeDelta.x,
                Theme.GroupHeadH, Theme.GroupHead, false);
            Gfx.Text(r, "Text", Theme.PadX, 0f, r.sizeDelta.x - Theme.PadX * 2f,
                Theme.GroupHeadH, Loc.T(titleKey), Theme.FontBody, Theme.Accent);
        }
    }
}
