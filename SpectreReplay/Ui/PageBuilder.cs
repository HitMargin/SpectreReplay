using System;
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
    ///
    /// 同样**不读父级的 sizeDelta 来算宽度**。内容容器是横向拉伸的矩形，
    /// 它的 sizeDelta.x 恒为 0——早先版本正是拿它当行宽，结果每一行都是
    /// 0 宽、标签框宽成负数、控件被摆到行外面去。行与控件一律用锚点拉伸，
    /// 宽度由父级决定，调用方不需要（也不可能）预先知道。
    ///
    /// 滚动靠 ScrollRect，内容高度由 Next 累加成 ContentHeight 报给外层。
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

        /// <summary>
        /// 一个标准行：左侧标签 + 右侧控件。标签右边缘按
        /// <paramref name="controlWidth"/> 让开，控件自己贴右边。
        /// </summary>
        internal RectTransform Row(string labelKey, float controlWidth)
        {
            float top = Next(Theme.RowH, Theme.RowGap);
            var row = Gfx.RectFillX(_parent, "Row", top, Theme.RowH,
                Theme.Zebra(_index++), false);
            if (!string.IsNullOrEmpty(labelKey))
            {
                Gfx.LabelIn(row, Loc.T(labelKey), Theme.FontBody, Theme.Text,
                    controlWidth + Theme.PadX + Theme.LabelGap);
            }
            return row;
        }

        /// <summary>空行（没有标签，控件从右边贴过去）。</summary>
        internal RectTransform BlankRow(float controlWidth) => Row(null, controlWidth);

        internal void Toggle(string labelKey, bool value, Action<bool> set)
        {
            var row = Row(labelKey, Theme.CtlToggleW);
            Gfx.Toggle(row, (Theme.RowH - Theme.CtlToggleH) * 0.5f, value, set);
        }

        internal void Button(string labelKey, bool primary, Action onClick)
        {
            const float w = 180f;
            var row = Row(labelKey, w);
            Gfx.Button(row, (Theme.RowH - Theme.CtlButtonH) * 0.5f, w,
                Loc.T(labelKey), primary, onClick);
        }

        internal void Slider(string labelKey, float value, float min, float max, bool integer,
            Action<float> set, Func<float, string> format = null)
        {
            var row = Row(labelKey, Theme.CtlSliderW + 88f + 14f);
            Gfx.Slider(row, (Theme.RowH - Theme.CtlSliderH) * 0.5f,
                value, min, max, integer, set, format);
        }

        internal void Drop(string labelKey, string[] options, int selected, Action<int> set)
        {
            var row = Row(labelKey, Theme.CtlDropW);
            Gfx.DropDown(row, (Theme.RowH - Theme.CtlDropH) * 0.5f, options, selected, set);
        }

        internal void Input(string labelKey, string value, string placeholder,
            Action<string> set)
        {
            var row = Row(labelKey, Theme.CtlInputW);
            Gfx.Input(row, (Theme.RowH - Theme.CtlInputH) * 0.5f, value, placeholder, set);
        }

        internal void Note(string textKey) => NoteText(Loc.T(textKey));

        internal void NoteText(string text)
        {
            float top = Next(Theme.NoteH, Theme.RowGap);
            var r = Gfx.RectFillX(_parent, "Note", top, Theme.NoteH,
                new Color(0f, 0f, 0f, 0f), false);
            // 说明文案要折行：单行会被 Ellipsis 截掉半句（最长的一条要 1092px）
            Gfx.TextWrap(r, "Text", text, Theme.FontSmall, Theme.TextMuted, Theme.PadX);
        }

        /// <summary>分组标题：一条带底色的横条。</summary>
        internal void Group(string titleKey)
        {
            if (_y > 0f)
            {
                _y += 8f;
            }
            float top = Next(Theme.GroupHeadH, Theme.RowGap);
            var r = Gfx.RectFillX(_parent, "Group", top, Theme.GroupHeadH,
                Theme.GroupHead, false);
            var t = Gfx.TextFill(r, "Text", Loc.T(titleKey), Theme.FontBody, Theme.Accent);
            Gfx.Stretch((RectTransform)t.transform, Theme.PadX, 0f, Theme.PadX, 0f);
        }
    }
}