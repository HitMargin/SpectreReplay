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
        // ResizeTo 要用：面板高度变了，这几个跟着一起变        private static RectTransform _panel;        private static RectTransform _sidebar;        private static RectTransform _viewport;        private static RectTransform _scrollbar;
        private static readonly List<PageDef> Pages = new List<PageDef>();
        private static int _current = -1;
        private static readonly List<LiveText> LiveTexts = new List<LiveText>();

        internal static bool IsOpen => _root != null;
        internal static int Hotkey = 290;

        /// <summary>
        /// 顶部提示里的热键文字。由 <see cref="SetHotkeyHint"/> 从设置注入，
        /// 面板本身不读配置——这样离线预览不必连上 ConfigStore。
        /// </summary>
        internal static string HotkeyHint = Loc.T("ui.hint").Replace("{0}", "F9");

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
            // 换过场景之后，游戏的 EventSystem 可能这时候才到位
            EnsureEventSystem();
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
            // 面板不开的时候不需要输入派发，把自己建的那个 EventSystem 拆掉。
            // 留着它的话，下一个场景的游戏 EventSystem 一到位就会撞车
            // （Unity 报 "There can be only one active Event System"）。
            if (_ownEventSystem != null)
            {
                UnityEngine.Object.Destroy(_ownEventSystem);
                _ownEventSystem = null;
            }
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

            BuildInto(root);
        }

        /// <summary>
        /// 把遮罩 / 面板 / 侧栏 / 页面区建到给定根上。
        ///
        /// 刻意与 <see cref="Build"/> 分开：不依赖 Canvas 与屏幕尺寸，
        /// 这样 tools\UiPreview 能离线调用**同一份代码**渲染出真实布局。
        /// 预览另写一套搭建逻辑的话，几何一旦不同步就验了个假的。
        ///
        /// 遮罩也放在这里而不是 Build 里：它是个 UI 元素，不是画布管线的一部分。
        /// 放在 Build 里的话，离线预览就看不到它——于是"点空白处关闭"这条
        /// 永远验不了：遮罩没铺满、或者铺满了却没接回调，都是几何上
        /// 完全看不出来的毛病。
        /// </summary>
        internal static RectTransform BuildInto(RectTransform root)
        {
            // 先离屏量一遍，决定面板该多高，再开始搭
            PanelHeight = MeasurePanelHeight();

            // 遮罩：铺满整个根，点了关闭面板。锚点拉满而不是按屏幕尺寸给，
            // 这样窗口尺寸变了不用重算——按尺寸给的那个版本在改分辨率后
            // 边角会点不到。
            var overlay = Gfx.Rect(root, "Overlay", 0f, 0f, root.sizeDelta.x,
                root.sizeDelta.y, Theme.Overlay, true);
            overlay.anchorMin = Vector2.zero;
            overlay.anchorMax = Vector2.one;
            overlay.pivot = new Vector2(0f, 1f);
            overlay.offsetMin = Vector2.zero;
            overlay.offsetMax = Vector2.zero;
            Gfx.MakeButton(overlay.gameObject, Color.white, Color.white, Color.white,
                Close);

            var panel = BuildPanel(root);
            // 侧栏与页面区挂在**面板**下，不是画布根下。
            // 挂在根下时它们是对着屏幕中心定位的，只有"面板恰好居中且尺寸不变"
            // 才对得上——改一下面板尺寸就整块错位，而且从画面上看不出原因。
            BuildSidebar(panel);
            BuildPageArea(panel);
            return _content;
        }

        /// <summary>本次打开时定下的面板高度。侧栏与页面区都按它摆。</summary>
        private static float PanelHeight = Theme.PanelH;

        /// <summary>
        /// 量出所有页面里最高的一页，据此决定面板高度。
        ///
        /// 取最高页而不是当前页：面板尺寸每切一次就变一次的话，比空一大块
        /// 更晃。下限 PanelH 免得页面太少时面板缩成一个奇怪的窄条，
        /// 上限 MaxPanelH 免得长页面把面板顶出屏幕。
        /// </summary>
        private static float MeasurePanelHeight()
        {
            float max = 0f;
            for (int i = 0; i < Pages.Count; i++)
            {
                PageDef p = Pages[i];
                try
                {
                    float h = PageBuilder.Measure(p.Build, Loc.T(p.TitleKey));
                    if (h > max) max = h;
                }
                catch
                {
                    // 量不出来就退回默认高度，不该因为测量失败就不开界面
                    max = Theme.PanelH;
                    break;
                }
            }
            return Mathf.Clamp(max + Theme.HeaderH + Theme.ContentPad,
                Theme.PanelH, Mathf.Min(Theme.MaxPanelH, ScreenCap()));
        }

        /// <summary>
        /// 量**当前页**要占多高，据此决定面板高度。
        ///
        /// 一开始想的是"取所有页里最高的，这样切页时尺寸不跳"——错了：
        /// 最高的那页正是需要滚动的那页，按它定高等于面板还是那么高，
        /// 而短页面照样空一大片；更糟的是它比屏幕还高，标题直接被切掉。
        /// 改成按当前页算，高度夹在屏幕内。
        /// </summary>
        private static float HeightFor(int index)
        {
            float need;
            try
            {
                PageDef p = Pages[index];
                need = PageBuilder.Measure(p.Build, Loc.T(p.TitleKey))
                    + Theme.HeaderH + Theme.ContentTopPad + Theme.ContentPad;
            }
            catch
            {
                need = Theme.PanelH;
            }
            return Mathf.Clamp(need, Theme.PanelMinH,
                Mathf.Min(Theme.MaxPanelH, ScreenCap()));
        }

        /// <summary>面板能占的上限：屏幕高的 88%，留点边。</summary>
        private static float ScreenCap()
            => Screen.height > 0 ? Screen.height * 0.88f : Theme.PanelH;

        private static void ResizeTo(int index)
        {
            float h = HeightFor(index);
            if (Mathf.Abs(h - PanelHeight) < 1f)
            {
                return;
            }
            PanelHeight = h;
            if (_panel != null)
            {
                _panel.sizeDelta = new Vector2(Theme.PanelW, h);
            }
            if (_sidebar != null)
            {
                ((RectTransform)_sidebar.transform).sizeDelta =
                    new Vector2(Theme.SidebarW, h);
            }
            if (_viewport != null)
            {
                var vrt = (RectTransform)_viewport.transform;
                vrt.anchoredPosition = new Vector2(0f,
                    -(Theme.HeaderH + Theme.ContentTopPad));
                vrt.sizeDelta = new Vector2(Theme.PageAreaW - Theme.PadX * 2f,
                    h - Theme.HeaderH - Theme.ContentTopPad);
            }
            if (_scrollbar != null)
            {
                var brt = (RectTransform)_scrollbar.transform;
                brt.anchoredPosition = new Vector2(brt.anchoredPosition.x,
                    -(Theme.HeaderH + Theme.ContentTopPad));
                brt.sizeDelta = new Vector2(8f,
                    h - Theme.HeaderH - Theme.ContentTopPad);
            }
        }

        private static RectTransform BuildPanel(RectTransform root)
        {
            var panel = Gfx.Rect(root, "Panel", 0f, 0f, Theme.PanelW, PanelHeight,
                Theme.Panel, true);
            panel.anchorMin = new Vector2(0.5f, 0.5f);
            panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = new Vector2(Theme.PanelW, PanelHeight);
            panel.anchoredPosition = Vector2.zero;
            _panel = panel;
            return panel;
        }

        private static void BuildSidebar(RectTransform panel)
        {
            // 父级是面板，用面板左上角当原点即可，不需要任何居中偏移
            var side = Gfx.Rect(panel, "Sidebar", 0f, 0f, Theme.SidebarW, PanelHeight,
                Theme.Sidebar, true);
            _sidebar = side;

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
                PanelHeight, new Color(0f, 0f, 0f, 0f), true);

            var header = Gfx.Rect(area, "Header", 0f, 0f, pageW, Theme.HeaderH,
                Theme.Header, false);
            // 标题与提示别挨着：标题 30px 字的字形下沿会探出 32px 的框，
            // 框贴框排看着就是两行字撞在一起。留 8px 间隙。
            _pageTitle = Gfx.Text(header, "Title", Theme.PadX, 8f, 520f, 34f, "",
                Theme.FontTitle, Theme.Text);
            Gfx.Text(header, "Hint", Theme.PadX, 46f, 500f, 20f,
                HotkeyHint, Theme.FontTiny, Theme.TextFaint);

            // 滚动：视口固定，内容高度由 PageBuilder 累加后写回 sizeDelta
            // 视口从顶栏下方再留一点开始：**第一行紧贴顶栏底边看着像浮在
            // 标题上**。回放页第一行是透明背景的说明文字，没这点间距尤其明显。
            float vTop = Theme.HeaderH + Theme.ContentTopPad;
            float vh = PanelHeight - vTop;
            var viewport = Gfx.Rect(area, "Viewport", 0f, vTop,
                pageW, vh, new Color(0f, 0f, 0f, 0f), true);
            // 必须裁剪：内容比视口高（渲染页 1054 vs 654），不裁的话多出来的行
            // 会直接画到面板外面、盖在游戏画面上——离线预览采样那一带的像素
            // 拿到的正是背景色。
            // 用 RectMask2D 而不是 Mask：后者需要带 alpha 的 Sprite 才能按
            // 图形裁剪，这里只有实心矩形，RectMask2D 够用且不吃图集。
            _viewport = viewport;            viewport.gameObject.AddComponent<RectMask2D>();

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
            BuildScrollbar(area, scroll, vh, vTop);
        }

        /// <summary>
        /// 右侧滚动条。没有它的话，内容比视口高时（渲染页 1054 vs 654）
        /// 用户根本看不出下面还有东西——只有转了滚轮才知道。
        /// </summary>
        private static void BuildScrollbar(RectTransform area, ScrollRect scroll,            float viewH, float top)
        {
            const float barW = 8f, pad = 6f;
            _scrollbar = Gfx.Rect(area, "Scrollbar",
                area.sizeDelta.x - barW - pad, Theme.HeaderH, barW, viewH,
                new Color(0f, 0f, 0f, 0f), true);
            // 条体本身不吃射线：挡住内容区的点击就麻烦了
            _scrollbar.GetComponent<Image>().raycastTarget = false;

            var handle = Gfx.Rect(_scrollbar, "Handle", 0f, 0f, barW, 80f,
                new Color(1f, 1f, 1f, 0.22f), false);
            var sb = _scrollbar.gameObject
                .AddComponent<UnityEngine.UI.Scrollbar>();
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
            // 面板高度跟着当前页走：短页面不该在下面空一大片。
            // 放在最前面，ResizeTo 只改尺寸、不动内容。
            ResizeTo(index);
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

            var pb = new PageBuilder(_content)
            {
                // 让页面知道自己的标题，好跳过正文里重复的那个分组标题
                PageTitle = _pageTitle.text,
            };
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

        /// <summary>
        /// 保证场景里有且只有一个可用的 EventSystem。
        ///
        /// 游戏的 EventSystem 是**场景/预制体里的对象**（ADOBase 里是序列化字段，
        /// 没有代码创建它），而本面板是 DontDestroyOnLoad 的常驻对象。两者的
        /// 生命周期对不上，于是有两个坑：
        ///
        ///   · 面板先开、游戏的 EventSystem 后到 → 场景里出现**两个**
        ///     EventSystem，Unity 会报 "There can be only one active Event System"，
        ///     之后谁处理输入就不确定了，表现为"有时候点得动有时候点不动"。
        ///   · 游戏那个被禁用或换场景销毁 → 面板还活着但没有派发者，全部点不动。
        ///
        /// 所以这里以 <c>EventSystem.current</c>（引擎自己维护的当前实例）为
        /// 权威，而不是 FindAnyObjectByType（那个只找场景里的激活对象），
        /// 并且每次开面板都重新判定：游戏有就用游戏的、把自己建的那个拆掉。
        /// </summary>
        private static void EnsureEventSystem()
        {
            var current = UnityEngine.EventSystems.EventSystem.current;
            if (current != null && !IsOurs(current))
            {
                // 游戏的事件系统到位了：拆掉我们多余的，避免两个并存
                if (_ownEventSystem != null)
                {
                    UnityEngine.Object.Destroy(_ownEventSystem);
                    _ownEventSystem = null;
                }
                return;
            }
            if (_ownEventSystem != null)
            {
                return;         // 我们建的那个还在用
            }
            var go = new GameObject("SpectreReplayEventSystem",
                typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.EventSystems.StandaloneInputModule));
            UnityEngine.Object.DontDestroyOnLoad(go);
            _ownEventSystem = go;
        }

        private static GameObject _ownEventSystem;

        private static bool IsOurs(UnityEngine.EventSystems.EventSystem es)
            => es != null && es.gameObject == _ownEventSystem;
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
