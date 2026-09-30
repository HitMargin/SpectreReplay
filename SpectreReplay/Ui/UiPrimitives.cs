using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 界面基元层：把 Unity uGUI 的原语（RectTransform / Image / TextMeshPro）
    /// 包成"一行 = 一个矩形 + 若干子件"的排版模型。
    ///
    /// 设计取向（与 QuartzUi 刻意不同的地方）：
    ///   · 布局只有一个垂直方向。QuartzUi 有 Row / Collapsible / DictRows 三套行模型，
    ///     这里统一成 Row —— 每一行都是父容器的一条 LayoutElement，
    ///     子件按 anchor 落在行的左/中/右。少一层抽象意味着少一类"行高对不上"的 bug。
    ///   · 颜色、字号、间距是一组常量，不是散落各处的字面量。
    ///   · 每次重建（Rebuild）都整棵销毁再重建，不做增量 diff。设置面板是
    ///     "改完就关"的低频界面，增量更新的复杂度换不回可感知的收益。
    /// </summary>
    internal static class UiSkin
    {
        // 取自游戏 UI 的深色调，避免与游戏画面打架
        internal static readonly Color PanelBg = new Color(0.09f, 0.09f, 0.11f, 0.96f);
        internal static readonly Color SidebarBg = new Color(0.12f, 0.12f, 0.15f, 1f);
        internal static readonly Color HeaderBg = new Color(0.16f, 0.16f, 0.20f, 1f);
        internal static readonly Color RowAltBg = new Color(1f, 1f, 1f, 0.03f);
        internal static readonly Color TrackBg = new Color(0.22f, 0.22f, 0.26f, 1f);
        internal static readonly Color FillBg = new Color(0.35f, 0.62f, 0.85f, 1f);
        internal static readonly Color Accent = new Color(0.35f, 0.62f, 0.85f, 1f);
        internal static readonly Color TextNormal = new Color(0.90f, 0.90f, 0.92f, 1f);
        internal static readonly Color TextMuted = new Color(0.62f, 0.62f, 0.68f, 1f);
        internal static readonly Color TextDisabled = new Color(0.40f, 0.40f, 0.44f, 1f);

        internal const float RowHeight = 46f;
        internal const float RowHeightTall = 64f;   // 带副标题的行
        internal const float RowSpacing = 2f;
        internal const float SidebarWidth = 210f;
        internal const float HeaderHeight = 54f;
        internal const float PadX = 18f;
        internal const float FontTitle = 30f;
        internal const float FontBody = 23f;
        internal const float FontSmall = 19f;

        /// <summary>全局字号缩放（设置项可调），供所有文字创建时乘用。</summary>
        internal static float TextScale = 1f;
    }

    /// <summary>
    /// 竖直滚动容器。所有页面内容都挂在它下面，行高由 LayoutElement 驱动，
    /// 滚动交给 ScrollRect —— 不自己算可视区/位移，避免和 LayoutGroup 打架。
    /// </summary>
    internal sealed class ScrollArea : MonoBehaviour
    {
        private RectTransform _content;
        private ScrollRect _scroll;

        internal RectTransform Content => _content;

        /// <summary>把一个带竖直 LayoutGroup 的内容根挂进滚动区。</summary>
        internal static ScrollArea Create(Transform parent, out RectTransform viewport)
        {
            var root = new GameObject("Scroll", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var rt = (RectTransform)root.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var maskGo = new GameObject("Mask", typeof(RectTransform));
            maskGo.transform.SetParent(rt, false);
            var maskRt = (RectTransform)maskGo.transform;
            Stretch(maskRt);
            // 不挂 Mask 组件：RectMask2D 会多一次绘制裁剪，而我们的背景是纯色块，
            // 内容不会被画到面板外（内容宽度恒等于视口），遮罩是多余的。
            var maskImg = maskGo.AddComponent<Image>();
            maskImg.color = new Color(0, 0, 0, 0.001f);   // 挡住 Raycast，不可见
            maskImg.raycastTarget = true;

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(maskRt, false);
            var contentRt = (RectTransform)contentGo.transform;
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.offsetMin = new Vector2(0f, 0f);
            contentRt.offsetMax = new Vector2(0f, 0f);
            var layout = contentGo.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(
                (int)UiSkin.PadX, (int)UiSkin.PadX, 10, 14);
            layout.spacing = UiSkin.RowSpacing;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scrollGo = new GameObject("ScrollRect", typeof(RectTransform));
            scrollGo.transform.SetParent(rt, false);
            var scrollRt = (RectTransform)scrollGo.transform;
            Stretch(scrollRt);
            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.content = contentRt;
            scroll.viewport = maskRt;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 42f;

            viewport = maskRt;
            return root.AddComponent<ScrollArea>().Init(contentRt, scroll);
        }

        private ScrollArea Init(RectTransform content, ScrollRect scroll)
        {
            _content = content;
            _scroll = scroll;
            return this;
        }

        /// <summary>内容高度变化后强制重算一次（打开页面的第一帧需要）。</summary>
        internal void Refresh() => LayoutRebuilder.ForceRebuildLayoutImmediate(_content);

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }

    /// <summary>行工厂：所有控件都基于它，保证间距与左边距一致。</summary>
    internal static class Row
    {
        /// <summary>创建一条行。height 传 0 表示"由内容撑开"（用于多行说明文字）。</summary>
        internal static RectTransform Create(Transform parent, float height = UiSkin.RowHeight,
            bool alternate = false)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            var le = go.AddComponent<LayoutElement>();
            if (height > 0f)
            {
                le.preferredHeight = height;
                le.flexibleHeight = 0f;
                // LayoutGroup 只在 childControlWidth/Height 为 true 时采用子件的尺寸。
                // 行本身作为"容器"必须允许子件铺满，否则里面的控件宽度会塌成 0。
                le.minHeight = height;
            }
            else
            {
                le.preferredHeight = -1f;
                le.flexibleHeight = 0f;
            }
            if (alternate)
            {
                var bg = go.AddComponent<Image>();
                bg.color = UiSkin.RowAltBg;
            }
            return rt;
        }

        /// <summary>行内左侧文字区（宽度 62%，垂直居中）。</summary>
        internal static TMP_Text Label(Transform row, string text, float widthRatio = 0.62f,
            bool muted = false, float fontSize = UiSkin.FontBody)
        {
            var t = NewText(row, "Label", fontSize,
                muted ? UiSkin.TextMuted : UiSkin.TextNormal);
            var rt = t.rectTransform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(widthRatio, 1f);
            rt.offsetMin = new Vector2(UiSkin.PadX, 2f);
            rt.offsetMax = new Vector2(0f, -2f);
            t.text = text;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            return t;
        }

        /// <summary>行内右侧控件槽：贴右、固定宽高。</summary>
        internal static RectTransform Slot(Transform row, float width, float height = 34f,
            float rightMargin = UiSkin.PadX)
        {
            var go = new GameObject("Slot", typeof(RectTransform));
            go.transform.SetParent(row, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(1f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(-rightMargin, 0f);
            return rt;
        }

        internal static TMP_Text NewText(Transform parent, string name, float fontSize, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.fontSize = fontSize * UiSkin.TextScale;
            t.color = color;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Overflow;
            return t;
        }

        internal static Image Panel(Transform parent, string name, Color color, bool raycast = true)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }
    }
}
