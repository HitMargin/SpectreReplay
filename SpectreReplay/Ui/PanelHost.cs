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
        private static UnityEngine.UI.Scrollbar _bar;
        private static RectTransform _banner;
        private static TMP_Text _bannerText;
        private static float _bannerHideAt;
        private static readonly List<PageDef> Pages = new List<PageDef>();
        private static int _current = -1;
        private static readonly List<LiveText> LiveTexts = new List<LiveText>();

        internal static bool IsOpen => _root != null;
        internal static int Hotkey = 290;

        /// <summary>
        /// 挂失败的补丁数。由 <c>Main</c> 注入。
        ///
        /// UI 层**不直接引 PatchLoader**：离线预览器不链接它（那要 Harmony
        /// 和游戏程序集），引了那边预览就编不过。钩子式注入是 UI 与功能层
        /// 解耦的老办法，不是为了绕过编译而临时糊的。
        /// </summary>
        internal static int PatchFailures;

        /// <summary>
        /// 顶部提示里的热键文字。由 <see cref="SetHotkeyHint"/> 从设置注入，
        /// 面板本身不读配置——这样离线预览不必连上 ConfigStore。
        /// </summary>
        /// <summary>
        /// 顶部提示里的热键文字。
        ///
        /// **按需生成，不缓存。** 原来是静态字段，在类初始化时就把
        /// <c>Loc.T("ui.hint")</c> 求值了——那一刻游戏语言还没确定
        /// （Main.Load 里 ApplyHotkey 跑在 RefreshLanguage 之前），
        /// 于是英文语言的用户看到的是一句中文提示。缓存一个"依赖当时
        /// 环境"的字符串，就把它冻在了那个时刻。
        ///
        /// 面板本身不读配置，热键由 <see cref="ApplyHotkey"/> 注入——
        /// 这样离线预览不必连上 ConfigStore。
        /// </summary>
        internal static string HotkeyHint => string.Format(Loc.T("ui.hint"),
            _hotkeyName);
        private static string _hotkeyName = "F9";

        internal static void SetHotkeyHint(string text) => _hotkeyName = text;

        /// <summary>
        /// 换热键：把生效的键与标题栏的提示**一起**改。
        ///
        /// 之前这两件事是分开放的——设置页改了 Hotkey，顶栏却还写着
        /// "F9 开关界面 · ESC 关闭"。用户看提示去按 F9，怎么按都没反应，
        /// 而真正生效的键只有滑条上那个数字告诉他。两处一起改才不会出现
        /// "提示说的和实际做的不是一个键"。
        /// </summary>
        internal static void ApplyHotkey(int keyCode)
        {
            Hotkey = keyCode;
            _hotkeyName = KeyName(keyCode);
        }

        /// <summary>F1..F12 → "F1".."F12"；别的键退回键名本身。</summary>
        private static string KeyName(int keyCode)
        {
            // KeyCode: F1=282 … F12=293
            if (keyCode >= 282 && keyCode <= 293)
            {
                return "F" + (keyCode - 281);
            }
            return ((KeyCode)keyCode).ToString();
        }

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
            // 字体解析完成之后，把界面要用的字全查一遍缺不缺。
            // 放在 Show(0) 之前：那时字体已经解析好了。
            // 标签用英文：它只出现在日志里，不是玩家看到的界面文案。
            UiFonts.ReportMissing("UI text", Loc.AllText());
            Show(0);
            // 挂不上的补丁 = 对应功能静默消失，而原因只在日志里。
            // 用户看到的现象是"某个开关点了没反应"，根本猜不到是补丁没挂上。
            // 所以除了日志，还要在界面上说一次——Spectre 也是这么做的
            // （SpectrePatchHub.ShowApplyFailureSummary）。
            // 用 UI 层自己的钩子，不引 PatchLoader：离线预览没有链接补丁
            // 加载器（它要 Harmony 与游戏程序集），引了那边那边就编译不过。
            if (PatchFailures > 0)
            {
                ShowBanner(Loc.T("ui.patchFailed"), 8f);
            }
        }

        /// <summary>
        /// 在面板顶部弹一条自动消失的横幅。
        ///
        /// 挂在 Overlay 底下而不是面板里：面板有 RectMask2D，会把它裁掉。
        /// </summary>
        internal static void ShowBanner(string text, float seconds)
        {
            if (_banner == null)
            {
                return;
            }
            _banner.gameObject.SetActive(true);
            _bannerText.text = text;
            _bannerHideAt = Time.unscaledTime + seconds;
        }

        /// <summary>
        /// 到点收起横幅。由 PanelHostBehaviour 每帧调一次。
        ///
        /// 用 unscaledTime：暂停时 Time.time 不走，横幅会永远收不起来。
        /// </summary>
        internal static void TickBanner()
        {
            if (_banner == null || !_banner.gameObject.activeSelf)
            {
                return;
            }
            if (Time.unscaledTime >= _bannerHideAt)
            {
                _banner.gameObject.SetActive(false);
            }
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

            // 失败横幅贴在遮罩上、面板之下：点得穿（不吃射线），
            // 但看得见。见 Banner()。
            _banner = Gfx.Rect(overlay, "Banner", 0f, 0f, 0f, 0f,
                new Color(0f, 0f, 0f, 0f), false);
            _banner.anchorMin = new Vector2(0.5f, 0f);
            _banner.anchorMax = new Vector2(0.5f, 0f);
            _banner.pivot = new Vector2(0.5f, 0f);
            _banner.sizeDelta = new Vector2(760f, 40f);
            _banner.anchoredPosition = new Vector2(0f, 40f);
            _banner.gameObject.SetActive(false);

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
            // 框高必须容得下一整行：TMP 的 Ellipsis 在放不下时是**整段不显示**，
            // 不是截断成省略号。30pt 的行高约 35px（Source Han Sans 的
            // lineHeight/unitsPerEm ≈ 1.16），原先只给 34px——差这一像素，
            // 标题在真机上一个字都不显示，而预览器只看宽度、压根没查高度，
            // 于是"全绿"地放过去了。侧栏品牌是 28pt 塞进 36px，所以它正常，
            // 一眼就能看出"品牌名有、标题没有"。
            _pageTitle = Gfx.Text(header, "Title", Theme.PadX, 8f, 520f, 40f, "",
                Theme.FontTitle, Theme.Text);
            // 18pt 的行高约 21px，给 24px。位置跟着标题下沿走，两行之间
            // 留 6px 间隙，框贴框排会看着像两行字撞在一起。
            Gfx.Text(header, "Hint", Theme.PadX, 50f, 500f, 24f,
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
            // 这里读 area.sizeDelta.x 是安全的：PageArea 用显式尺寸建的
            // （锚点固定在左上），**不是横向拉伸的**。一旦哪天把它改成拉伸的，
            // sizeDelta.x 会恒为 0，滚动条就会跑到屏幕左边去——画面上只
            // 表现为"滚动条不见了"，不会有任何报错。
            //
            // top 传的是 vTop（含内容区上边距），不是 Theme.HeaderH。
            // 用错的话滚动条会比视口高出一截、往上错开一截。
            _scrollbar = Gfx.Rect(area, "Scrollbar",
                area.sizeDelta.x - barW - pad, top, barW, viewH,
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
            _bar = sb;
        }

        /// <summary>
        /// 按内容与视口的比例设置滑块长度。
        ///
        /// **ScrollRect 不会替你做这件事。** Unity 的 ScrollRect 只驱动
        /// 滚动条的 `value`（滑块在轨道上的位置），**从不设置 `size`**
        /// （滑块占轨道的比例）——那个默认是 1，于是不管下面有多少内容，
        /// 滑块永远是满格，看上去"能拖到底"，其实底下没什么可看的。
        /// 真正的做法是自己按 视口高/内容高 算一遍。
        /// </summary>
        private static void UpdateScrollbarSize()
        {
            if (_bar == null || _viewport == null || _content == null)
            {
                return;
            }
            float viewH = _viewport.rect.height;
            float contentH = _content.rect.height;
            if (viewH <= 0f)
            {
                return;
            }
            _bar.size = contentH > viewH
                ? Mathf.Clamp01(viewH / contentH)
                : 1f;
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
            // 底部余量必须与 HeightFor 算面板高度时用的**同一个常量**。
            // 之前这里是写死的 24，而那边用的是 Theme.ContentPad（16）——
            // 差这 8 像素，于是每张内容刚好装得下的短页面都会比视口高 8px，
            // 永远多出一截没必要的滚动条和滚动范围。高度和内容两个地方
            // 算同一件事，就得用同一个数。
            _content.sizeDelta = new Vector2(0f,
                pb.ContentHeight + Theme.ContentPad);
            UpdateScrollbarSize();
            indexForLog = index;
            _clampWarned = false;
            ResetScroll();
        }

        /// <summary>
        /// 切页后把滚动位置拨回顶部。
        ///
        /// **不重置的话，从渲染页滚到底再切到"关于"，看到的是一整页空白。**
        /// 原因是 ScrollRect 的偏移是跟着对象走的：渲染页内容 1250 / 视口
        /// 950，滚到底时内容被抬了约 300px；切到内容只有 344 的"关于"页，
        /// 那个 300px 的偏移还在，于是整页内容被推到视口外面去了——
        /// 屏幕上只剩最后一条说明的下半截，再下面全是空的。
        ///
        /// 而且每次切页**面板高度也会变**（按当前页量出来的），偏移与
        /// 视口高度不再匹配，Clamped 也救不回来。
        ///
        /// 用 normalizedPosition 而不是直接写 anchoredPosition：前者是
        /// ScrollRect 自己的语义，后者在布局还没定下来时写会被覆盖。
        /// </summary>
        private static void ResetScroll()
        {
            if (_scroll == null)
            {
                return;
            }
            _scroll.StopMovement();

            // **关键：得先让布局算完。**
            //
            // verticalNormalizedPosition 的 setter 内部会调 UpdateBounds()，
            // 读的是**当前这一刻**的内容与视口矩形。而我们是刚把新的
            // sizeDelta 写上去就立刻设它，Unity 还没重跑布局——它读到的是
            // 上一页留下来的陈旧矩形，于是算出一个错的偏移。
            // 紧接着 Clamped 拿着同一套陈旧边界去纠正，自然纠正不到位。
            //
            // 真机上的症状就是这么来的：切到渲染页，内容被抬到视口上方
            // 一千多像素，屏幕上只剩最后一条说明的一线边。
            //
            // 设两遍：第一遍强制布局生效，第二遍用已经算好的边界再校正一次。
            _content.anchoredPosition = new Vector2(0f, 0f);
            Canvas.ForceUpdateCanvases();
            _scroll.verticalNormalizedPosition = 1f;
            Canvas.ForceUpdateCanvases();
            _scroll.verticalNormalizedPosition = 1f;
            _content.anchoredPosition = new Vector2(0f, 0f);

            // 把实际算出来的尺寸记进日志：这类问题光看画面猜不出来，
            // 有这几个数就能直接判断是面板过高、内容过高还是偏移没归零。
            UnityEngine.Debug.Log(
                $"[SpectreReplay] 第{indexForLog}页 布局：面板 {_panel.rect.height:F0}"
                + $" 视口 {_viewport.rect.height:F0} 内容 {_content.rect.height:F0}"
                + $" 偏移 {_content.anchoredPosition.y:F0}");
        }

        private static int indexForLog;

        /// <summary>
        /// 每帧把内容夹回视口内——一条**不变量**，不管是谁把它推出去的。
        ///
        /// 内容顶边不许高过视口顶边（否则上面的行全被 RectMask2D 裁掉，
        /// 界面上大片空白），底边也不许低过视口底边。内容比视口短时直接
        /// 钉在顶部。
        ///
        /// 为什么非得每帧兜底：ScrollRect 归位靠它自己的边界计算，而那依赖
        /// "布局已经重算"。切页、改面板高度、字体解析完成导致行高变化——
        /// 每一次都会有一两帧读到的还是旧边界，偏移就停在错的地方不回来。
        /// 盯着归位不如直接守住范围：这几个数（视口高、内容高）都是我们自己
        /// 写进去的，不依赖任何 Unity 内部时序。
        ///
        /// 只在越界时才写回，静止时一帧都不碰。
        /// </summary>
        internal static void ClampContent()
        {
            if (_content == null || _viewport == null)
            {
                return;
            }
            float viewH = _viewport.rect.height;
            float contentH = _content.rect.height;
            if (viewH <= 0f || contentH <= 0f)
            {
                return;
            }
            // 锚点在顶部、pivot 在顶部：偏移 0 就是"顶边贴视口顶边"。
            float maxY = 0f;
            float minY = -Mathf.Max(0f, contentH - viewH);
            float y = _content.anchoredPosition.y;
            // minY <= maxY 恒成立，所以"在范围内"是 y >= minY && y <= maxY。
            // 原来写成 y <= minY && y >= maxY —— 那只在 minY==maxY==0 时为真，
            // 于是一个完全正常的偏移 0 也被判成越界，每次切页都白打一条
            // "内容跑出视口"的警告。真机日志里那行就是这么来的。
            if (y >= minY && y <= maxY)
            {
                return;
            }
            float fixedY = Mathf.Clamp(y, minY, maxY);
            _content.anchoredPosition = new Vector2(_content.anchoredPosition.x, fixedY);
            if (!_clampWarned)
            {
                _clampWarned = true;
                UnityEngine.Debug.LogWarning(
                    $"[SpectreReplay] 内容跑出视口（偏移 {y:F0}），已夹回 "
                    + $"{fixedY:F0}。视口 {viewH:F0} 内容 {contentH:F0}");
            }
        }

        private static bool _clampWarned;

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
            if (PanelHost.IsOpen)
            {
                // 横幅自己算消失时刻，不另开一个协程：低频界面，
                // 为了一个 5 秒的淡出多挂一个 MonoBehaviour 不划算。
                PanelHost.TickBanner();
            }
            if (PanelHost.IsOpen && Time.unscaledTime >= _nextTick)
            {
                _nextTick = Time.unscaledTime + 0.2f;
                PanelHost.TickLive();
            }
            if (PanelHost.IsOpen)
            {
                PanelHost.ClampContent();
            }
        }
    }
}
