using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SpectreReplay.Ui
{
    /// <summary>配色与尺寸常量。</summary>
    internal static class UiSkin
    {
        internal static readonly Color PanelBg = new Color(0.10f, 0.10f, 0.12f, 1f);
        internal static readonly Color SidebarBg = new Color(0.13f, 0.13f, 0.16f, 1f);
        internal static readonly Color HeaderBg = new Color(0.17f, 0.17f, 0.21f, 1f);
        internal static readonly Color RowBg = new Color(1f, 1f, 1f, 0.025f);
        internal static readonly Color RowAltBg = new Color(1f, 1f, 1f, 0.055f);
        internal static readonly Color NavIdleBg = new Color(1f, 1f, 1f, 0.07f);
        internal static readonly Color CatBg = new Color(1f, 1f, 1f, 0.035f);
        internal static readonly Color TrackBg = new Color(0.26f, 0.26f, 0.31f, 1f);
        internal static readonly Color Accent = new Color(0.29f, 0.60f, 0.88f, 1f);
        internal static readonly Color TextNormal = new Color(0.93f, 0.93f, 0.95f, 1f);
        internal static readonly Color TextMuted = new Color(0.60f, 0.60f, 0.67f, 1f);
        internal static readonly Color TextDisabled = new Color(0.42f, 0.42f, 0.47f, 1f);

        internal const float RowHeight = 56f;
        internal const float NoteHeight = 44f;
        internal const float SectionHeight = 48f;
        internal const float RowSpacing = 4f;
        internal const float SidebarWidth = 240f;
        internal const float HeaderHeight = 64f;
        internal const float PadX = 22f;
        internal const float FontTitle = 30f;
        internal const float FontBody = 24f;
        internal const float FontSmall = 20f;

        internal static float TextScale = 1f;
    }

    /// <summary>
    /// 行上下文。行高是**常量**，子件位置由常量算出来——完全不走 LayoutGroup 谈判。
    ///
    /// 为什么彻底放弃行内的布局系统：试过 VerticalLayoutGroup + 行内
    /// HorizontalLayoutGroup + LayoutElement，实测结果是子件被算成 0 高、
    /// 行被内容撑开、控件铺满整行（三行开关叠成三条横贯的面板）。
    /// 原因在于 LayoutElement 的 preferred/min/flexible 三个值互相同步时
    /// 由布局系统在布局过程中回写，嵌套两层后尺寸协商的结果不可预期。
    ///
    /// 现在的规则简单到可以手算：
    ///   行高 = 常量；标签横向拉伸、右边距 = 已登记控件的最大宽度；
    ///   控件贴右、垂直居中、尺寸由常量给定。行宽未知也不影响——横向全用 anchor。
    /// </summary>
    internal sealed class RowRef
    {
        internal RectTransform Rect;

        /// <summary>右侧被控件占用的宽度（含与标签的间隙）。</summary>
        internal float Inset = UiSkin.PadX;

        internal readonly List<RectTransform> Labels = new List<RectTransform>(1);

        internal void ApplyInsets()
        {
            for (int i = 0; i < Labels.Count; i++)
            {
                RectTransform l = Labels[i];
                l.anchorMin = new Vector2(0f, 0f);
                l.anchorMax = new Vector2(1f, 1f);
                l.offsetMin = new Vector2(UiSkin.PadX, 0f);
                l.offsetMax = new Vector2(-Inset, 0f);
            }
        }
    }

    /// <summary>行工厂。调用序：Label 可先可后，控件登记后会回头刷新标签右距。</summary>
    internal static class Row
    {
        internal static RowRef Create(Transform parent, float height = UiSkin.RowHeight,
            Color? background = null)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;

            // 行高由 LayoutElement 固定给布局系统，且**不再挂任何 LayoutGroup**——
            // 行的内部尺寸与布局系统无关，行与行之间只靠这个值排布。
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            le.flexibleHeight = 0f;

            var img = go.AddComponent<Image>();
            img.color = background ?? UiSkin.RowBg;
            img.raycastTarget = false;

            return new RowRef { Rect = rt };
        }

        /// <summary>左侧标签，横向拉伸到控件左边。</summary>
        internal static TMP_Text Label(RowRef row, string text, bool muted = false,
            float fontSize = UiSkin.FontBody)
        {
            // 文字组件必须**直接挂在这个对象上**，不能像 NewText 那样再嵌一层。
            // 嵌一层之后，登记给 ApplyInsets 的是内层矩形的尺寸，而内层又是相对
            // 外层（新建物体默认 100×100、居中）定位的——算出来宽度为负，
            // 于是一个字都不显示。上一版整页标签全空就是踩了这个。
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(row.Rect, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            UiFonts.Apply(t);
            t.fontSize = fontSize * UiSkin.TextScale;
            t.color = muted ? UiSkin.TextMuted : UiSkin.TextNormal;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.text = text;

            var rt = (RectTransform)go.transform;
            row.Labels.Add(rt);
            row.ApplyInsets();
            return t;
        }

        /// <summary>右侧控件槽：登记占用宽度并把标签让开。</summary>
        internal static RectTransform Slot(RowRef row, float width, float height)
        {
            var go = new GameObject("Slot", typeof(RectTransform));
            go.transform.SetParent(row.Rect, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(1f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(-UiSkin.PadX, 0f);

            row.Inset = Mathf.Max(row.Inset, width + UiSkin.PadX + 12f);
            row.ApplyInsets();
            return rt;
        }

        internal static TMP_Text NewText(Transform parent, string name, float fontSize, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            // TMP 组件没有显式 font 就一个 glyph 都不画，必须补。
            UiFonts.Apply(t);
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

        internal static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }

    /// <summary>竖直滚动容器：只做上下滚动，行高由各行的 LayoutElement 固定。</summary>
    internal sealed class ScrollArea : MonoBehaviour
    {
        private RectTransform _content;

        internal RectTransform Content => _content;

        internal static ScrollArea Create(Transform parent, out RectTransform viewport)
        {
            var root = new GameObject("Scroll", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var rt = (RectTransform)root.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var vp = new GameObject("Viewport", typeof(RectTransform));
            vp.transform.SetParent(rt, false);
            var vpRt = (RectTransform)vp.transform;
            Row.Stretch(vpRt);
            var vpImg = vp.AddComponent<Image>();
            vpImg.color = new Color(0f, 0f, 0f, 0f);
            vpImg.raycastTarget = true;

            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(vpRt, false);
            var cRt = (RectTransform)content.transform;
            cRt.anchorMin = new Vector2(0f, 1f);
            cRt.anchorMax = new Vector2(1f, 1f);
            cRt.pivot = new Vector2(0.5f, 1f);
            cRt.offsetMin = new Vector2(0f, 0f);
            cRt.offsetMax = new Vector2(0f, 0f);
            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(12, 12, 12, 18);
            vlg.spacing = UiSkin.RowSpacing;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            var fit = content.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scrollGo = new GameObject("ScrollRect", typeof(RectTransform));
            scrollGo.transform.SetParent(rt, false);
            var sRt = (RectTransform)scrollGo.transform;
            Row.Stretch(sRt);
            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.content = cRt;
            scroll.viewport = vpRt;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 48f;

            viewport = vpRt;
            return root.AddComponent<ScrollArea>().Init(cRt);
        }

        private ScrollArea Init(RectTransform content)
        {
            _content = content;
            return this;
        }

        internal void Refresh() => LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
    }
}
