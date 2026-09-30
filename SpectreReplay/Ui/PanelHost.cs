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
        private static ScrollRect _scroll;
        private static readonly List<PageDef> Pages = new List<PageDef>();
        private static int _current = -1;
        private static readonly List<LiveText> LiveTexts = new List<LiveText>();

        internal static bool IsOpen => _root != null;
        internal static int Hotkey = 290;

        /// <summary>
        /// 顶部提示里的热键文字。由 <see cref="SetHotkeyHint"/> 从设置注入，
        /// 面板本身不读配置——这样离线预览不必连上 ConfigStore。
        /// </summary>
        internal static string HotkeyHint = "F9 开关界面 · ESC 关闭";

        internal static void SetHotkeyHint(string text) => HotkeyHint = text;

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
            // 每次打开都重取：玩家可能在设置里换过语言
            Loc.RefreshLanguage();
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

            BuildInto(root);
        }

        /// <summary>
        /// 把面板 / 侧栏 / 页面区建到给定根上。
        ///
        /// 刻意与 <see cref="Build"/> 分开：不依赖 Canvas 与屏幕尺寸，
        /// 这样 tools\UiPreview 能离线调用**同一份代码**渲染出真实布局。
        /// 预览另写一套搭建逻辑的话，几何一旦不同步就验了个假的。
        /// </summary>
        internal static RectTransform BuildInto(RectTransform root)
        {
            var panel = BuildPanel(root);
            // 侧栏与页面区挂在**面板**下，不是画布根下。
            // 挂在根下时它们是对着屏幕中心定位的，只有"面板恰好居中且尺寸不变"
            // 才对得上——改一下面板尺寸就整块错位，而且从画面上看不出原因。
            BuildSidebar(panel);
            BuildPageArea(panel);
            return _content;
        }

        private static RectTransform BuildPanel(RectTransform root)
        {
            var panel = Gfx.Rect(root, "Panel", 0f, 0f, Theme.PanelW, Theme.PanelH,
                Theme.Panel, true);
            panel.anchorMin = new Vector2(0.5f, 0.5f);
            panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = new Vector2(Theme.PanelW, Theme.PanelH);
            panel.anchoredPosition = Vector2.zero;
            return panel;
        }

        private static void BuildSidebar(RectTransform panel)
        {
            // 父级是面板，用面板左上角当原点即可，不需要任何居中偏移
            var side = Gfx.Rect(panel, "Sidebar", 0f, 0f, Theme.SidebarW, Theme.PanelH,
                Theme.Sidebar, true);

            // 字号 30 时 "Spectre Replay" 要 230px，侧栏只有 220px 可用，
            // 离线预览量出来的。降到 28。
            Gfx.Text(side, "Brand", 14f, 20f, Theme.SidebarW - 24f, 36f,
                Loc.Brand, Theme.FontTitle - 2f, Theme.Accent);

            float y = 70f;
            string lastGroup = null;
            for (int i = 0; i < Pages.Count; i++)
            {
                PageDef p = Pages[i];
                // 只有一页的分组不画标题。现在每个分组恰好一页，画出来就是
                // "录制 / 录制" 这种紧挨着的重复——纯噪音，还占掉一行高度。
                if (p.Group != lastGroup)
                {
                    lastGroup = p.Group;
                    if (CountGroup(p.Group) > 1)
                    {
                        var gh = Gfx.Rect(side, "GroupHead", 12f, y,
                            Theme.SidebarW - 24f, 30f, Theme.GroupHead, false);
                        Gfx.TextFill(gh, "Text", Loc.Cat(p.Group).ToUpperInvariant(),
                            Theme.FontTiny, Theme.TextMuted);
                        y += 30f + 6f;
                    }
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

        /// <summary>某分组下有几页。只有一页时不画分组标题（见 BuildSidebar）。</summary>
        private static int CountGroup(string group)
        {
            int n = 0;
            for (int i = 0; i < Pages.Count; i++)
            {
                if (Pages[i].Group == group)
                {
                    n++;
                }
            }
            return n;
        }

        private static void BuildPageArea(RectTransform panel)
        {
            // 父级是面板，原点就是面板左上角：页面区从侧栏右边缘开始，
            // 即 left = SidebarW。不需要任何居中偏移。
            float pageW = Theme.PanelW - Theme.SidebarW;
            var area = Gfx.Rect(panel, "PageArea", Theme.SidebarW, 0f, pageW,
                Theme.PanelH, new Color(0f, 0f, 0f, 0f), true);

            var header = Gfx.Rect(area, "Header", 0f, 0f, pageW, Theme.HeaderH,
                Theme.Header, false);
            // 标题与提示别挨着：标题 30px 字的字形下沿会探出 32px 的框，
            // 框贴框排看着就是两行字撞在一起。留 8px 间隙。
            _pageTitle = Gfx.Text(header, "Title", Theme.PadX, 8f, 520f, 34f, "",
                Theme.FontTitle, Theme.Text);
            Gfx.Text(header, "Hint", Theme.PadX, 46f, 500f, 20f,
                HotkeyHint, Theme.FontTiny, Theme.TextFaint);

            // 滚动：视口固定，内容高度由 PageBuilder 累加后写回 sizeDelta
            float vh = Theme.PanelH - Theme.HeaderH;
            var viewport = Gfx.Rect(area, "Viewport", 0f, Theme.HeaderH,
                pageW, vh, new Color(0f, 0f, 0f, 0f), true);
            // 必须裁剪：内容比视口高（渲染页 1054 vs 654），不裁的话多出来的行
            // 会直接画到面板外面、盖在游戏画面上——离线预览采样那一带的像素
            // 拿到的正是背景色。
            // 用 RectMask2D 而不是 Mask：后者需要带 alpha 的 Sprite 才能按
            // 图形裁剪，这里只有实心矩形，RectMask2D 够用且不吃图集。
            viewport.gameObject.AddComponent<RectMask2D>();

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
            _scroll = scroll;
            BuildScrollbar(area, scroll, vh);
        }

        /// <summary>
        /// 右侧滚动条。没有它的话，内容比视口高时（渲染页 1054 vs 654）
        /// 用户根本看不出下面还有东西——只有转了滚轮才知道。
        /// </summary>
        private static void BuildScrollbar(RectTransform area, ScrollRect scroll, float viewH)
        {
            const float barW = 8f, pad = 6f;
            var bar = Gfx.Rect(area, "Scrollbar",
                area.sizeDelta.x - barW - pad, Theme.HeaderH, barW, viewH,
                new Color(0f, 0f, 0f, 0f), true);
            // 条体本身不吃射线：挡住内容区的点击就麻烦了
            bar.GetComponent<Image>().raycastTarget = false;

            var handle = Gfx.Rect(bar, "Handle", 0f, 0f, barW, 80f,
                new Color(1f, 1f, 1f, 0.22f), false);
            var sb = bar.gameObject.AddComponent<UnityEngine.UI.Scrollbar>();
            sb.handleRect = handle;
            sb.targetGraphic = handle.GetComponent<Image>();
            sb.direction = UnityEngine.UI.Scrollbar.Direction.BottomToTop;
            scroll.verticalScrollbar = sb;
            scroll.verticalScrollbarVisibility =
                ScrollRect.ScrollbarVisibility.AutoHide;
            scroll.verticalScrollbarSpacing = 0f;
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
