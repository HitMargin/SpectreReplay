using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SpectreReplay.Ui
{
    internal sealed class PageDef
    {
        internal string Key;
        internal string Group;
        internal string TitleKey;
        internal Action<PageBuilder> Build;
        internal Image Background;
        internal TMP_Text Label;
    }

    /// <summary>
    /// 面板宿主。Canvas / 遮罩 / 侧栏 / 页面区，全部用显式坐标搭，
    /// 关闭时整棵 Destroy——这是低频界面，整棵重建能杜绝"上次的控件还挂在场景里"。
    /// </summary>
    internal static class PanelHost
    {
        private static GameObject _root;
        private static RectTransform _content;
        private static TMP_Text _pageTitle;
        private static readonly List<PageDef> Pages = new List<PageDef>();
        private static int _current = -1;
        private static readonly List<LiveText> LiveTexts = new List<LiveText>();

        internal static bool IsOpen => _root != null;
        internal static int Hotkey = 290;

        // ── 注册 ────────────────────────────────────────────────
        internal static void Page(string key, string group, string titleKey,
            Action<PageBuilder> build)
        {
            Pages.RemoveAll(p => p.Key == key);
            Pages.Add(new PageDef
            {
                Key = key,
                Group = group,
                TitleKey = titleKey,
                Build = build,
            });
        }

        // ── 开关 ────────────────────────────────────────────────
        internal static void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        internal static void Open()
        {
            if (IsOpen)
            {
                return;
            }
            UiFonts.Invalidate();
            LiveTexts.Clear();
            Build();
            Show(0);
        }

        internal static void Close()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
            _root = null;
            _content = null;
            _pageTitle = null;
            _current = -1;
            LiveTexts.Clear();
        }

        // ── 构建 ────────────────────────────────────────────────
        private static void Build()
        {
            _root = new GameObject("SpectreReplayPanel", typeof(RectTransform));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var root = (RectTransform)_root.transform;
            Gfx.Stretch(root, 0f, 0f, 0f, 0f);

            // Canvas 不加就什么都不渲染——这是上一版"界面不出现"的根因。
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32760;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();

            var overlay = Gfx.Rect(root, "Overlay", 0f, 0f,
                Screen.width, Screen.height, Theme.Overlay, true);
            // 遮罩尺寸按屏幕给，并随窗口变化重算
            overlay.anchorMin = Vector2.zero;
            overlay.anchorMax = Vector2.one;
            overlay.pivot = new Vector2(0f, 1f);
            overlay.offsetMin = Vector2.zero;
            overlay.offsetMax = Vector2.zero;
            Gfx.MakeButton(overlay.gameObject, Color.white, Color.white, Color.white, Close);

            BuildPanel(root);
            BuildSidebar(root);
            BuildPageArea(root);
        }

        private static void BuildPanel(RectTransform root)
        {
            var panel = Gfx.Rect(root, "Panel", 0f, 0f, Theme.PanelW, Theme.PanelH,
                Theme.Panel, true);
            panel.anchorMin = new Vector2(0.5f, 0.5f);
            panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = new Vector2(Theme.PanelW, Theme.PanelH);
            panel.anchoredPosition = Vector2.zero;
        }

        private static void BuildSidebar(RectTransform root)
        {
            var side = Gfx.Rect(root, "Sidebar", 0f, 0f, Theme.SidebarW, Theme.PanelH,
                Theme.Sidebar, true);
            side.anchorMin = new Vector2(0.5f, 0.5f);
            side.anchorMax = new Vector2(0.5f, 0.5f);
            side.pivot = new Vector2(0.5f, 0.5f);
            side.anchoredPosition = new Vector2(
                -(Theme.PanelW - Theme.SidebarW) * 0.5f, 0f);
            side.sizeDelta = new Vector2(Theme.SidebarW, Theme.PanelH);

            Gfx.Text(side, "Brand", 20f, 18f, Theme.SidebarW - 40f, 40f,
                Loc.Brand, Theme.FontTitle, Theme.Accent);

            float y = 70f;
            string lastGroup = null;
            for (int i = 0; i < Pages.Count; i++)
            {
                PageDef p = Pages[i];
                if (p.Group != lastGroup)
                {
                    lastGroup = p.Group;
                    var gh = Gfx.Rect(side, "GroupHead", 12f, y,
                        Theme.SidebarW - 24f, 30f, Theme.GroupHead, false);
                    Gfx.TextFill(gh, "Text", Loc.Cat(p.Group).ToUpperInvariant(),
                        Theme.FontTiny, Theme.TextMuted);
                    y += 30f + 6f;
                }
                var bg = Gfx.Rect(side, "Nav" + i, 10f, y, Theme.SidebarW - 20f, Theme.NavH,
                    Theme.NavIdle, true);
                var lbl = Gfx.TextFill(bg, "Text", Loc.T(p.TitleKey), Theme.FontBody, Theme.Text);
                int captured = i;
                Gfx.MakeButton(bg.gameObject, Theme.NavIdle, Theme.NavHover,
                    Theme.NavActive, () => Show(captured));
                p.Background = bg.GetComponent<Image>();
                p.Label = lbl;
                y += Theme.NavH + 4f;
            }
        }

        private static void BuildPageArea(RectTransform root)
        {
            // 面板居中，坐标原点在面板中心。侧栏占左边 SidebarW，
            // 页面区必须从「侧栏右边缘」开始：-PanelW/2 + SidebarW。
            // 之前误写成 +(PanelW - SidebarW)/2 = +415，整块跑到面板外面去了
            // （pivot 是 (0,0.5)，anchoredPosition.x 就是左边缘）。
            float pageLeft = -(Theme.PanelW * 0.5f) + Theme.SidebarW;
            float pageW = Theme.PanelW - Theme.SidebarW;
            var area = Gfx.Rect(root, "PageArea", pageLeft, 0f, pageW,
                Theme.PanelH, new Color(0f, 0f, 0f, 0f), true);
            area.anchorMin = new Vector2(0.5f, 0.5f);
            area.anchorMax = new Vector2(0.5f, 0.5f);
            area.pivot = new Vector2(0f, 0.5f);
            area.sizeDelta = new Vector2(pageW, Theme.PanelH);
            area.anchoredPosition = new Vector2(pageLeft, 0f);

            var header = Gfx.Rect(area, "Header", 0f, 0f, pageW, Theme.HeaderH,
                Theme.Header, false);
            _pageTitle = Gfx.Text(header, "Title", Theme.PadX, 12f, 520f, 32f, "",
                Theme.FontTitle, Theme.Text);
            Gfx.Text(header, "Hint", Theme.PadX, 44f, 500f, 20f,
                "F" + (Settings.Hotkey - 281) + " 开关界面 · ESC 关闭",
                Theme.FontTiny, Theme.TextFaint);

            // 滚动：视口固定，内容高度由 PageBuilder 累加后写回 sizeDelta
            float top = Theme.HeaderH;
            float vh = Theme.PanelH - Theme.HeaderH;
            var viewport = Gfx.Rect(area, "Viewport", 0f, top,
                Theme.PanelW - Theme.SidebarW, vh, new Color(0f, 0f, 0f, 0f), true);

            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(viewport, false);
            var cRt = (RectTransform)content.transform;
            cRt.anchorMin = new Vector2(0f, 1f);
            cRt.anchorMax = new Vector2(1f, 1f);
            cRt.pivot = new Vector2(0.5f, 1f);
            cRt.anchoredPosition = Vector2.zero;
            cRt.sizeDelta = new Vector2(0f, 0f);
            _content = cRt;

            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = cRt;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 48f;
        }

        // ── 切页 ────────────────────────────────────────────────
        internal static void Show(int index)
        {
            if (index < 0 || index >= Pages.Count)
            {
                return;
            }
            if (_content == null)
            {
                return;
            }
            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                var c = _content.GetChild(i);
                if (c != null)
                {
                    UnityEngine.Object.Destroy(c.gameObject);
                }
            }
            LiveTexts.Clear();
            _current = index;
            PageDef p = Pages[index];
            _pageTitle.text = Loc.T(p.TitleKey);

            for (int i = 0; i < Pages.Count; i++)
            {
                var img = Pages[i].Background;
                bool on = i == index;
                if (img != null)
                {
                    img.color = on ? Theme.NavActive : Theme.NavIdle;
                }
                if (Pages[i].Label != null)
                {
                    Pages[i].Label.color = on ? Theme.TextOnAccent : Theme.Text;
                }
            }

            var pb = new PageBuilder(_content);
            p.Build?.Invoke(pb);
            _content.sizeDelta = new Vector2(0f, pb.ContentHeight + 24f);
        }

        internal static void Refresh() => Show(_current);

        // ── 会变文字 ────────────────────────────────────────────
        /// <summary>登记一段会变的文字，由宿主统一驱动刷新。</summary>
        internal static void RegisterLive(TMP_Text t, Func<string> get)
            => LiveTexts.Add(new LiveText { Text = t, Get = get });

        private sealed class LiveText
        {
            internal TMP_Text Text;
            internal Func<string> Get;
        }

        internal static void TickLive()
        {
            for (int i = 0; i < LiveTexts.Count; i++)
            {
                var l = LiveTexts[i];
                if (l.Text == null || l.Get == null)
                {
                    continue;
                }
                l.Text.text = l.Get();
            }
        }

        private static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() != null)
            {
                return;
            }
            var go = new GameObject("SpectreReplayEventSystem",
                typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.EventSystems.StandaloneInputModule));
            UnityEngine.Object.DontDestroyOnLoad(go);
        }
    }

    /// <summary>常驻输入轮询：热键开关面板、ESC 关闭、驱动会变文字。</summary>
    internal sealed class PanelHostBehaviour : MonoBehaviour
    {
        private float _nextTick;

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown((KeyCode)PanelHost.Hotkey))
            {
                UnityEngine.Debug.Log("[SpectreReplay] 收到界面热键 " + PanelHost.Hotkey);
                PanelHost.Toggle();
            }
            if (PanelHost.IsOpen && UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                PanelHost.Close();
            }
            if (PanelHost.IsOpen && Time.unscaledTime >= _nextTick)
            {
                _nextTick = Time.unscaledTime + 0.2f;
                PanelHost.TickLive();
            }
        }
    }
}
