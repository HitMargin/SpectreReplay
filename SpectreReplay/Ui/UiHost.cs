using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 界面宿主：全屏遮罩 + 左侧导航 + 右侧页面。
    ///
    /// 打开/关闭走"整棵重建"：面板关闭时 Destroy 掉全部 GameObject，下次打开重建。
    /// 理由——这是低频界面（设置一次就不再碰），而"重建"能彻底消灭
    /// "上一轮残留的控件还挂在场景里"这类问题。Unity 里忘记 Destroy 的
    /// Canvas 会在切场景后继续存在，是很难查的泄漏。
    ///
    /// 与 QuartzUi 的差别：QuartzUi 的面板由宿主系统统一管理、并支持多模组共用
    /// 一个 Canvas；这里完全自持，Canvas 由本模组自己建、自己拆。
    /// </summary>
    internal static class UiHost
    {
        private static GameObject _root;
        private static GameObject _panel;
        private static RectTransform _pageHost;
        private static RectTransform _sidebarList;
        private static TMP_Text _pageTitle;
        private static TMP_Text _hintLine;
        private static ScrollArea _scroll;

        private static readonly List<NavEntry> Nav = new List<NavEntry>();
        private static int _current = -1;

        private sealed class NavEntry
        {
            internal string Key;
            internal string Category;
            internal string TitleKey;
            internal Action<Transform> Build;
            internal Button Button;
        }

        internal static bool IsOpen => _root != null;
        internal static int Hotkey = 290;   // F9 —— 必须与 Spectre 的 F8(289) 错开

        internal static void Install()
        {
            var go = new GameObject("SpectreReplayUiHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<UiHostBehaviour>();
        }

        // ── 导航注册 ────────────────────────────────────────────
        internal static void Page(string key, string category, string titleKey,
            Action<Transform> build)
        {
            Nav.RemoveAll(n => n.Key == key);
            Nav.Add(new NavEntry
            {
                Key = key,
                Category = category,
                TitleKey = titleKey,
                Build = build,
            });
        }

        // ── 开关 ────────────────────────────────────────────────
        internal static void Toggle()
        {
            if (IsOpen) Close(); else Open();
        }

        internal static void Open()
        {
            if (IsOpen)
            {
                return;
            }
            Build();
        }

        internal static void Close()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
            _root = null;
            _panel = null;
            _pageHost = null;
            _sidebarList = null;
            _scroll = null;
            _current = -1;
        }

        private static void Build()
        {
            // 场景可能刚换过（主菜单 ↔ 关卡是不同场景，RDConstants 也不同）。
            // 模组加载那一刻场景里未必有 RDConstants，只解析一次就会永远拿不到字体。
            UiFonts.Invalidate();

            _root = new GameObject("SpectreReplayUi", typeof(RectTransform));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var rootRt = (RectTransform)_root.transform;
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = Vector2.zero;
            rootRt.offsetMax = Vector2.zero;

            // Canvas 挂在根上——缺了它，整棵 RectTransform 层级根本不会被渲染。
            // 这不是可选组件：Unity 只渲染挂在 Canvas 下的 Graphic。
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // 排序值取高值，确保盖在游戏自己的 UI 之上
            canvas.sortingOrder = 32760;
            canvas.pixelPerfect = false;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();

            EnsureEventSystem();

            // 全屏遮罩：压暗背景、点击即关。alpha 0.55 太浅，游戏画面
            // 透过来把整个面板衬成灰绿色，看着浑浊。压到 0.78。
            var mask = Row.Panel(rootRt, "Mask", new Color(0f, 0f, 0f, 0.78f));
            mask.rectTransform.anchorMin = Vector2.zero;
            mask.rectTransform.anchorMax = Vector2.one;
            mask.rectTransform.offsetMin = Vector2.zero;
            mask.rectTransform.offsetMax = Vector2.zero;
            var closeBtn = mask.gameObject.AddComponent<Button>();
            closeBtn.transition = Selectable.Transition.None;
            closeBtn.onClick.AddListener(Close);

            _panel = Row.Panel(rootRt, "Panel", UiSkin.PanelBg).gameObject;
            var prt = (RectTransform)_panel.transform;
            prt.anchorMin = new Vector2(0.5f, 0.5f);
            prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(1000f, 680f);

            BuildSidebar(prt);
            BuildPageArea(prt);

            // 默认停在第一页
            if (Nav.Count > 0)
            {
                Show(0);
            }
            UnityEngine.Debug.Log("[SpectreReplay] 界面已构建（页面 " + Nav.Count + " 个）");
        }

        /// <summary>
        /// UI 要能点，必须有 EventSystem 在场景里。游戏自己通常有一个，
        /// 但关卡切换/编辑器里未必；没有就自己建一个。
        /// </summary>
        private static void EnsureEventSystem()
        {
            // 不用 FindObjectOfType：它在这版 Unity 已废弃，且全场景扫描有成本。
            // 只在 Build 的那一刻调一次，用 FindAny 语义足够。
            if (UnityEngine.Object.FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() != null)
            {
                return;
            }
            var go = new GameObject("SpectreReplayEventSystem",
                typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.EventSystems.StandaloneInputModule));
            UnityEngine.Object.DontDestroyOnLoad(go);
        }

        private static void BuildSidebar(RectTransform parent)
        {
            var side = Row.Panel(parent, "Sidebar", UiSkin.SidebarBg);
            var srt = side.rectTransform;
            srt.anchorMin = new Vector2(0f, 0f);
            srt.anchorMax = new Vector2(0f, 1f);
            srt.pivot = new Vector2(0f, 0.5f);
            srt.offsetMin = Vector2.zero;
            srt.offsetMax = new Vector2(UiSkin.SidebarWidth, 0f);

            var title = Row.NewText(side.rectTransform, "Brand", UiSkin.FontTitle, UiSkin.Accent);
            title.rectTransform.anchorMin = new Vector2(0f, 1f);
            title.rectTransform.anchorMax = new Vector2(1f, 1f);
            title.rectTransform.pivot = new Vector2(0.5f, 1f);
            title.rectTransform.offsetMin = new Vector2(UiSkin.PadX, -UiSkin.HeaderHeight);
            title.rectTransform.offsetMax = new Vector2(-UiSkin.PadX, 0f);
            title.text = Loc.Brand;
            title.alignment = TextAlignmentOptions.MidlineLeft;

            var listGo = new GameObject("NavList", typeof(RectTransform));
            listGo.transform.SetParent(side.rectTransform, false);
            var lrt = (RectTransform)listGo.transform;
            lrt.anchorMin = new Vector2(0f, 0f);
            lrt.anchorMax = new Vector2(1f, 1f);
            lrt.offsetMin = new Vector2(0f, 8f);
            lrt.offsetMax = new Vector2(0f, -UiSkin.HeaderHeight - 8f);
            var vlg = listGo.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 2f;
            vlg.padding = new RectOffset(8, 8, 0, 0);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            _sidebarList = lrt;

            // 按 Category 分组渲染
            string lastCat = null;
            for (int i = 0; i < Nav.Count; i++)
            {
                var entry = Nav[i];
                if (entry.Category != lastCat)
                {
                    lastCat = entry.Category;
                    var catRow = Row.Create(lrt, 40f, UiSkin.CatBg);
                    var ct = Row.NewText(catRow.Rect, "Cat", UiSkin.FontSmall, UiSkin.TextMuted);
                    var crt = ct.rectTransform;
                    crt.anchorMin = Vector2.zero;
                    crt.anchorMax = Vector2.one;
                    crt.offsetMin = new Vector2(12f, 0f);
                    crt.offsetMax = new Vector2(-8f, 0f);
                    ct.text = Loc.Cat(entry.Category).ToUpperInvariant();
                    ct.alignment = TextAlignmentOptions.MidlineLeft;
                }
                int index = i;
                // 按钮要有实际底色——上一版用全透明 Image，未选中时侧栏
                // 整个是空的，看上去像没有界面。
                var btn = Row.Create(lrt, 50f, UiSkin.NavIdleBg);
                var b = btn.Rect.gameObject.AddComponent<Button>();
                b.targetGraphic = btn.Rect.GetComponent<Image>();
                var cb = b.colors;
                cb.normalColor = Color.white;
                cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
                cb.selectedColor = Color.white;
                cb.fadeDuration = 0.06f;
                b.colors = cb;
                int captured = index;
                b.onClick.AddListener(() => Show(captured));

                var t = Row.NewText(btn.Rect, "Text", UiSkin.FontBody, UiSkin.TextNormal);
                Row.Stretch(t.rectTransform);
                t.rectTransform.offsetMin = new Vector2(6f, 0f);
                t.rectTransform.offsetMax = new Vector2(-6f, 0f);
                t.text = Loc.T(entry.TitleKey);
                t.alignment = TextAlignmentOptions.MidlineLeft;
                entry.Button = b;
            }
        }

        private static void BuildPageArea(RectTransform parent)
        {
            var area = Row.Panel(parent, "Area", new Color(0f, 0f, 0f, 0f), false);
            var art = area.rectTransform;
            art.anchorMin = new Vector2(0f, 0f);
            art.anchorMax = Vector2.one;
            art.offsetMin = new Vector2(UiSkin.SidebarWidth, 0f);
            art.offsetMax = Vector2.zero;

            var header = Row.Panel(art, "Header", UiSkin.HeaderBg, false);
            var hrt = header.rectTransform;
            hrt.anchorMin = new Vector2(0f, 1f);
            hrt.anchorMax = new Vector2(1f, 1f);
            hrt.pivot = new Vector2(0.5f, 1f);
            hrt.offsetMin = Vector2.zero;
            hrt.offsetMax = new Vector2(0f, UiSkin.HeaderHeight);

            _pageTitle = Row.NewText(hrt, "Title", UiSkin.FontTitle, UiSkin.TextNormal);
            _pageTitle.rectTransform.anchorMin = Vector2.zero;
            _pageTitle.rectTransform.anchorMax = new Vector2(1f, 1f);
            _pageTitle.rectTransform.offsetMin = new Vector2(UiSkin.PadX, 0f);
            _pageTitle.rectTransform.offsetMax = new Vector2(-UiSkin.PadX, 0f);
            _pageTitle.alignment = TextAlignmentOptions.MidlineLeft;

            _hintLine = Row.NewText(hrt, "Hint", UiSkin.FontSmall, UiSkin.TextMuted);
            _hintLine.rectTransform.anchorMin = new Vector2(0f, 0f);
            _hintLine.rectTransform.anchorMax = new Vector2(1f, 0f);
            _hintLine.rectTransform.pivot = new Vector2(0.5f, 0f);
            _hintLine.rectTransform.anchoredPosition = new Vector2(0f, 6f);
            _hintLine.rectTransform.sizeDelta = new Vector2(0f, 20f);
            _hintLine.alignment = TextAlignmentOptions.MidlineLeft;
            _hintLine.text = Loc.Hint;

            var hostGo = new GameObject("Pages", typeof(RectTransform));
            hostGo.transform.SetParent(art, false);
            var prt = (RectTransform)hostGo.transform;
            prt.anchorMin = Vector2.zero;
            prt.anchorMax = Vector2.one;
            prt.offsetMin = new Vector2(0f, 0f);
            prt.offsetMax = new Vector2(0f, -UiSkin.HeaderHeight);
            _scroll = ScrollArea.Create(prt, out _);
            _pageHost = _scroll.Content;
        }

        private static void Show(int index)
        {
            if (index < 0 || index >= Nav.Count)
            {
                return;
            }
            // 重建页面：销毁旧的子节点
            for (int i = _pageHost.childCount - 1; i >= 0; i--)
            {
                var child = _pageHost.GetChild(i);
                if (child != null)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }
            _current = index;
            var entry = Nav[index];
            _pageTitle.text = Loc.T(entry.TitleKey);
            for (int i = 0; i < Nav.Count; i++)
            {
                var img = Nav[i].Button?.targetGraphic as Image;
                if (img != null)
                {
                    img.color = i == index ? UiSkin.Accent : new Color(0f, 0f, 0f, 0f);
                }
            }
            entry.Build?.Invoke(_pageHost);
            _scroll.Refresh();
        }

        /// <summary>页面内容变了以后调用，重新算一次布局高度。</summary>
        internal static void Refresh() => _scroll?.Refresh();
    }

    /// <summary>常驻的输入轮询组件：热键开关面板、ESC 关闭、驱动实时标签。</summary>
    internal sealed class UiHostBehaviour : MonoBehaviour
    {
        private void Update()
        {
            bool hotkeyDown = UnityEngine.Input.GetKeyDown((KeyCode)UiHost.Hotkey);
            if (hotkeyDown)
            {
                // 明确记录：否则"按了没反应"分不清是热键没读到还是界面没画出来。
                // 这两种情况的修法完全不同。
                UnityEngine.Debug.Log("[SpectreReplay] 收到界面热键 "
                    + UiHost.Hotkey + "（" + SpectreReplay.Pages.SettingsPages.KeyName(
                        (KeyCode)UiHost.Hotkey) + "）");
                UiHost.Toggle();
            }
            if (UiHost.IsOpen && UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                UiHost.Close();
            }
        }

        private void LateUpdate()
        {
            if (UiHost.IsOpen)
            {
                LiveLabelHub.TickAll(Time.unscaledTime);
            }
        }
    }
}
