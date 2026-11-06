using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 游戏画面上的独立 IMGUI 设置窗口。
    ///
    /// 自己建 `GameObject + MonoBehaviour + OnGUI`，不寄宿在 UMM 的窗口里。
    /// UMM 面板仍保留作为兼容入口，但独立窗口负责现代外观、开合动画、页签栏
    /// 与输入隔离。页面本体仍调用 <see cref="OnGui.DrawPanel"/>，避免复制
    /// 设置字段和业务状态机。
    /// </summary>
    internal sealed class ModernSettingsWindow : MonoBehaviour
    {
        private static ModernSettingsWindow _instance;
        private static bool _open;
        private static bool _closing;

        private const float WindowW = 960f;
        private const float WindowH = 590f;
        private const float HeaderH = 62f;
        private const float FooterH = 42f;
        private const float TabH = 36f;
        private const float MinScale = 0.78f;
        private const float MaxScale = 1.15f;
        // 正文区比外层玻璃更沉，但保留较多底层画面；0.52 不再压成死黑。
        private const float BodyBgAlpha = 0.52f;

        private float _show;
        private float _pageT = 1f;
        private Vector2 _bodyScroll;
        private bool _dragging;
        private Vector2 _dragOffset;
        private int _lastTab;
        private int _lastSub;
        private float _subPageT = 1f;
        private Rect _windowRect;
        private float _tabRailX = -1f;
        private float _tabRailW = -1f;
        /// <summary>整屏模糊后的源纹理（开窗时抓一次，之后复用）。</summary>
        private RenderTexture _glassFull;
        /// <summary>按当前窗口矩形从 <see cref="_glassFull"/> 裁出的玻璃贴图。</summary>
        private RenderTexture _glass;
        /// <summary><see cref="_glass"/> 对应的窗口像素区域（变了才重裁）。</summary>
        private Rect _glassCrop;
        private int _glassState;

        internal static bool IsOpen => _open;
        internal static bool IsClosing => _closing;

        internal static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("SpectreReplay.ModernSettings");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ModernSettingsWindow>();
        }

        internal static void Toggle()
        {
            Ensure();
            if (_instance == null) return;
            _instance.SetOpen(!_open || _closing);
        }

        /// <summary>UMM 面板打开时，把可见界面交给独立窗口。</summary>
        internal static void OpenFromUmm()
        {
            Ensure();
            _instance?.SetOpen(true);
        }

        internal static void Close()
        {
            _instance?.SetOpen(false);
        }

        internal static void Destroy()
        {
            if (_instance == null) return;
            Destroy(_instance.gameObject);
            _instance = null;
            _open = false;
            _closing = false;
            Gui.ViewportOverride = null;
        }

        private void Awake()
        {
            _lastTab = OnGui.CurrentTab;
            _lastSub = OnGui.CurrentSubTab;
        }

        private void Update()
        {
            if (_instance != this) return;
            if (Input.GetKeyDown((KeyCode)Settings.WindowButton))
            {
                SetOpen(!_open || _closing);
            }
            if (!_open && !_closing)
            {
                // 完全关闭后每帧确认挡板撤除；挡板残留 = 鼠标点击全被吃掉。
                UiInputBlock.Clear();
                return;
            }
            if (_closing)
            {
                // 关闭请求已发出后，挡板必须持续撤除，不能等动画结束。
                UiInputBlock.Clear();
            }
            if (Input.GetKeyDown(KeyCode.Escape) && OnGui.CapturingTarget == 0)
            {
                SetOpen(false);
            }

            // 开合动画 + FinishClose 必须在 Update 里推进（每帧恰好一次）。
            // 之前放在 OnGUI：一帧跑多趟（Layout/Repaint/事件趟），热键关窗时
            // 可能根本走不到 FinishClose，透明输入挡板就不会被撤掉——用户按
            // 热键关窗后鼠标失效就是这个。挪出来后无论 OnGUI 是否被调用，
            // 关闭流程都会在固定帧数内走完。
            float dt = Time.unscaledDeltaTime;
            if (_closing)
                _show = Mathf.MoveTowards(_show, 0f, dt / 0.14f);
            else
                _show = Mathf.MoveTowards(_show, 1f, dt / 0.16f);
            if (_show <= 0f && _closing)
            {
                FinishClose();
                return;
            }
            // 关闭过程中挡板仍要跟着窗口（缩小的窗口只挡自己），
            // 由 DrawWindow 里的 Place 更新；彻底关掉后由上面 Clear 兜底。
        }

        /// <summary>
        /// 帧末把窗口对应的模糊区域从整屏纹理裁到玻璃贴图上。
        ///
        /// <para><b>为什么在 LateUpdate 而不是 OnGUI。</b>上一版在 OnGUI 的
        /// Repaint 里直接 Blit，会把当前帧部分 UI 画进临时 RT（闪）；再上一版
        /// 又改成 OnGUI 里直接用 UV 采样，但 IMGUI 的 uv.y 方向跟抓帧贴图的
        /// v 方向组合起来极易出错（镜像）。这里把"算 UV + Blit"整个挪出
        /// IMGUI：LateUpdate 里按**渲染像素**方向（同抓帧贴图，v=1 是屏幕
        /// 顶部）裁一块，OnGUI 里用普通 <c>GUI.DrawTexture</c> 整张画——
        /// 不再有任何 UV 换算，方向天然正确。</para>
        ///
        /// <para>只在窗口矩形变化时裁一次（GPU 拷贝），静止时零成本。
        /// LateUpdate 在渲染前执行，本帧内不会有"半画的 UI"被写进临时 RT，
        /// 因此也不会闪。</para>
        /// </summary>
        private void LateUpdate()
        {
            if (_instance != this || _glassFull == null
                || (!_open && !_closing) || _glassState != 3)
            {
                return;
            }
            float scale = Mathf.Clamp(Screen.height / 1080f * 1.1f, MinScale, MaxScale);
            var px = new Rect(
                _windowRect.x * scale, _windowRect.y * scale,
                _windowRect.width * scale, _windowRect.height * scale);
            float w = Mathf.Clamp(px.width, 8f, Screen.width);
            float h = Mathf.Clamp(px.height, 8f, Screen.height);
            float x = Mathf.Clamp(px.x, 0f, Screen.width - w);
            float y = Mathf.Clamp(px.y, 0f, Screen.height - h);
            var crop = new Rect(x, y, w, h);
            if (_glass != null && _glassCrop == crop) return;

            // 与整屏抓帧同向的像素坐标。v 从贴图底部起算：
            // 窗口顶边（y 小）对应更大的 v；uvRect.y 取较小 v，即 1-(y+h)/H。
            bool flip = SystemInfo.graphicsUVStartsAtTop;
            var uvScale = new Vector2(
                w / Screen.width,
                h / Screen.height * (flip ? -1f : 1f));
            var uvOff = new Vector2(
                x / Screen.width,
                flip ? 1f - y / Screen.height
                     : 1f - (y + h) / Screen.height);

            var next = RenderTexture.GetTemporary(
                Mathf.Max(8, (int)(w / 8f)), Mathf.Max(8, (int)(h / 8f)),
                0, RenderTextureFormat.ARGB32);
            next.filterMode = FilterMode.Bilinear;
            Graphics.Blit(_glassFull, next, uvScale, uvOff);
            ReleaseRenderTexture(ref _glass);
            _glass = next;
            _glassCrop = crop;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
                _open = false;
                _closing = false;
            }
            Gui.ViewportOverride = null;
            ReleaseGlass();
            UiInputBlock.Destroy();
        }

        private void SetOpen(bool open)
        {
            if (open)
            {
                if (_open && !_closing) return;
                _open = true;
                _closing = false;
                _show = 0f;
                _pageT = 1f;
                _subPageT = 1f;
                _lastSub = OnGui.CurrentSubTab;
                _glassState = 1;
                OnGui.RefreshForModernWindow();
            }
            else if (_open)
            {
                // _open 表示“当前允许交互/铺输入挡板”，不是“动画是否结束”。
                // 热键关窗必须在这一拍就翻假并撤挡板；否则关闭动画期间
                // DrawWindow 仍会重新 Place，状态和实际鼠标行为会短暂分叉。
                _open = false;
                _closing = true;
                UiInputBlock.Clear();
            }
        }

        private void FinishClose()
        {
            _open = false;
            _closing = false;
            _show = 0f;
            _pageT = 1f;
            _subPageT = 1f;
            ReleaseGlass();
            _glassState = 0;
            // 关窗必须撤掉输入挡板，否则透明板还浮在屏幕上继续吃点击。
            UiInputBlock.Clear();
            Gui.ViewportOverride = null;
            OnGui.CommitText();
            OnGui.FlushConfig();
        }

        private void OnGUI()
        {
            if (!_open && !_closing) return;

            float dt = Time.unscaledDeltaTime;
            if (_closing)
                // 关 0.14s、开 0.16s，都是纯线性 MoveTowards——窗口整体 alpha
                // 由 EaseOut(_show) 出曲线，这里再叠变速反而"生硬"（先慢后快
                // 再停）。之前的 (0.4+0.6*show) 变速就是"关闭很奇怪"的来源。
                _show = Mathf.MoveTowards(_show, 0f, dt / 0.14f);
            else
                _show = Mathf.MoveTowards(_show, 1f, dt / 0.16f);
            if (_show <= 0f && _closing)
            {
                FinishClose();
                return;
            }

            if (_glassState == 1)
            {
                if (Event.current.type == EventType.Repaint)
                {
                    _glassState = 2;
                    StartCoroutine(CaptureGlass(true));
                }
                return;
            }
            if (_glassState == 2) return;

            float pageTarget = _lastTab == OnGui.CurrentTab ? 1f : 0f;
            _pageT = Mathf.MoveTowards(_pageT, pageTarget, dt / 0.18f);
            if (_pageT <= 0f && _lastTab != OnGui.CurrentTab)
            {
                _lastTab = OnGui.CurrentTab;
                _pageT = 0f;
            }

            // 二级页切换：与一级页同一套滑入语言（先收起 → 换内容 → 滑入）。
            // 之前只调 SetSubTab 换内容，切换时新页"啪"一下直接替换旧页，
            // 与一级页的 0.18s 滑入并排一比就生硬。
            float subTarget = _lastSub == OnGui.CurrentSubTab ? 1f : 0f;
            _subPageT = Mathf.MoveTowards(_subPageT, subTarget, dt / 0.16f);
            if (_subPageT <= 0f && _lastSub != OnGui.CurrentSubTab)
            {
                _lastSub = OnGui.CurrentSubTab;
                _subPageT = 0f;
                // 换二级页时把滚动位回顶：旧页的滚动深度对新页没意义，
                // 留着会出现"新页从中间开始看"的怪状态。
                _bodyScroll = Vector2.zero;
            }

            float scale = Mathf.Clamp(Screen.height / 1080f * 1.1f, MinScale, MaxScale);
            float sw = Screen.width / scale;
            float sh = Screen.height / scale;
            // 自定义尺寸（Settings.WindowWOverride/HOverride，>0 = 用户拖过
            // 右下角把手）。仍受屏幕边界约束；回到默认 = 把把手拖到最小值以下
            // 会夹到 MinW，或临时删 Configs.json 里的 window_w/h 两行。
            float w = Settings.WindowWOverride > 0f ? Settings.WindowWOverride : WindowW;
            float h = Settings.WindowHOverride > 0f ? Settings.WindowHOverride : WindowH;
            w = Mathf.Clamp(Mathf.Min(w, sw - 46f), MinW, sw - 46f);
            h = Mathf.Clamp(Mathf.Min(h, sh - 24f), MinH, sh - 24f);
            float ease = EaseOut(_show);
            float targetX = Settings.WindowX < 0f
                ? (sw - w) * 0.5f : Mathf.Clamp(Settings.WindowX, 4f, sw - w - 4f);
            float targetY = Settings.WindowY < 0f
                ? (sh - h) * 0.5f : Mathf.Clamp(Settings.WindowY, 4f, sh - h - 4f);
            // Windows 11 式开合：从下方升起、途中放大；关闭反向下沉缩小。
            // 之前 0.94→1.0 只差 6%，看起来几乎没动。这里 0.72→1.0 放大
            // 28%，同时从窗口高度的 22% 下方升起，方向和幅度都能看出来。
            float animScale = 0.72f + 0.28f * ease;
            float aw = Mathf.Max(560f, w * animScale);
            float ah = Mathf.Max(420f, h * animScale);
            _windowRect = new Rect(
                targetX + (w - aw) * 0.5f,
                // 起点在目标位置下方 22% 窗口高度处，升起时同时放大：
                // 视觉上像从底部"长"出来，而不是原地闪一下。
                targetY + (h - ah) * 0.5f + (1f - ease) * h * 0.22f,
                aw, ah);

            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColor = GUI.color;
            try
            {
                GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
                GUI.color = new Color(1f, 1f, 1f, ease);

                // 面板展开时才压暗背景。这里不吞整屏点击，窗口外仍然可操作。
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.color = new Color(0f, 0f, 0f, 0.22f * ease);
                    GUI.DrawTexture(new Rect(0f, 0f, sw, sh), Texture2D.whiteTexture);
                    GUI.color = new Color(1f, 1f, 1f, ease);
                    // 窗口外圈柔和投影：左右各 34、上 22、下 48。没有这一层，
                    // 窗口只有 1px 描边，远看像贴在屏幕上的一张贴纸。
                    GUI.color = new Color(1f, 1f, 1f, 1f);
                    GUI.DrawTexture(new Rect(_windowRect.x - 34f, _windowRect.y - 22f,
                        _windowRect.width + 68f, _windowRect.height + 70f),
                        RoundedTex.Shadow(), ScaleMode.StretchToFill, true, 0f,
                        new Color(1f, 1f, 1f, ease), 34f, 34f);

                    // **玻璃层在窗口背景下面，不是上面。**
                    // 正确顺序是：投影 → 裁剪模糊的游戏画面（100% 不透明）→
                    // 半透明深色玻璃卡盖在上面。游戏画面完整透出来，再压一层
                    // 薄雾。反过来（不透明底 + 24% 玻璃叠顶）只剩一块死黑，
                    // 既没玻璃也没层次。
                    DrawGlass();
                }

                GUI.Window(0x53524D, _windowRect, DrawWindow, GUIContent.none, ModernWindowStyle());
            }
            finally
            {
                Gui.ViewportOverride = null;
                GUI.color = oldColor;
                GUI.matrix = oldMatrix;
            }
        }

        private GUIStyle _style;
        private GUIStyle _bodyBgStyle;

        private GUIStyle ModernWindowStyle()
        {
            if (_style != null) return _style;
            _style = new GUIStyle(GUI.skin.window)
            {
                padding = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(12, 12, 12, 12),
                margin = new RectOffset(0, 0, 0, 0),
            };
            // 玻璃卡盖层：alpha **0.56** 的半透明深色面，靠下面的模糊游戏
            // 画面透出层次。之前这里用 0.86，几乎不透，游戏画面被彻底挡住
            // ——玻璃感全无。
            var bg = RoundedTex.Panel3D(
                new Color(0.078f, 0.082f, 0.098f, 0.56f),   // 0x14151A, 0.56
                new Color(0.28f, 0.29f, 0.32f, 0.45f),
                new Color(0.03f, 0.032f, 0.04f, 0.55f),
                new Color(1f, 1f, 1f, 0.14f),
                14, 0, 0f, 0);
            // GUI.Window 被点击后会切到 focused / onFocused；这两个状态不覆盖，
            // 就会露出 Unity 原皮肤那套亮底。on* 一并处理，避免选中态残影。
            _style.normal.background = bg;
            _style.hover.background = bg;
            _style.active.background = bg;
            _style.focused.background = bg;
            _style.onNormal.background = bg;
            _style.onHover.background = bg;
            _style.onActive.background = bg;
            _style.onFocused.background = bg;
            return _style;
        }

        private GUIStyle BodyBgStyle()
        {
            if (_bodyBgStyle != null) return _bodyBgStyle;
            _bodyBgStyle = new GUIStyle
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                // 8px 边界让 32px 圆角贴图按 9-slice 拉伸，正文变宽时圆角不被拉扁。
                border = new RectOffset(8, 8, 8, 8),
            };
            var tex = RoundedTex.Get(32, 32, 8,
                new Color(0.035f, 0.038f, 0.048f, 1f));
            _bodyBgStyle.normal.background = tex;
            _bodyBgStyle.hover.background = tex;
            _bodyBgStyle.active.background = tex;
            _bodyBgStyle.focused.background = tex;
            _bodyBgStyle.onNormal.background = tex;
            _bodyBgStyle.onHover.background = tex;
            _bodyBgStyle.onActive.background = tex;
            _bodyBgStyle.onFocused.background = tex;
            return _bodyBgStyle;
        }

        private void DrawWindow(int id)
        {
            Rect area = new Rect(0f, 0f, _windowRect.width, _windowRect.height);
            Gui.ViewportOverride = area;

            // **内部不再画任何底色。** 玻璃画面 + 半透明玻璃卡已经在 OnGUI
            // 里、窗口之前按正确顺序铺好了（投影 → 模糊游戏画面 → 玻璃卡）。
            // 这里再垫一层不透明底会把玻璃挡死，再叠 24% 玻璃则变成
            // "黑底 + 一层薄雾"，两头都不像。
            // 抓屏未就绪时（_glassState 1/2）根本不进这个函数，所以不需要兜底。
            if (Event.current.type == EventType.Repaint)
            {
                // 顶边内侧 1px 白色高光——玻璃边缘的"厚度"。
                Gui.Fill(new Rect(14f, 0f, area.width - 28f, 1f),
                    new Color(1f, 1f, 1f, 0.12f));
            }

            HandleDrag(area);
            HandleResize(area);
            // 把窗口矩形交给 uGUI 透明挡板：窗口内的点击被 EventSystem 吃掉，
            // 不再穿透到游戏 UI / 判定输入（IMGUI 自己完全不参与射线检测）。
            bool blockWindowInput = !_closing;
            if (blockWindowInput)
            {
                UiInputBlock.Place(new Rect(
                    _windowRect.x * Mathf.Clamp(Screen.height / 1080f * 1.1f, MinScale, MaxScale),
                    _windowRect.y * Mathf.Clamp(Screen.height / 1080f * 1.1f, MinScale, MaxScale),
                    _windowRect.width * Mathf.Clamp(Screen.height / 1080f * 1.1f, MinScale, MaxScale),
                    _windowRect.height * Mathf.Clamp(Screen.height / 1080f * 1.1f, MinScale, MaxScale)));
            }
            else
            {
                UiInputBlock.Clear();
            }
            DrawHeader(area);
            DrawPageTabs(area);
            DrawBody(area);
            DrawFooter(area);

            if (Event.current.type == EventType.MouseDown
                && !area.Contains(Event.current.mousePosition))
            {
                SetOpen(false);
            }
        }

        /// <summary>画裁好的玻璃底。裁剪在 <see cref="LateUpdate"/> 按像素方向
        /// 完成，这里不再做任何 UV 换算。</summary>
        private void DrawGlass()
        {
            if (_glass == null) return;
            GUI.DrawTexture(_windowRect, _glass, ScaleMode.StretchToFill,
                false, 0f, new Color(1f, 1f, 1f, EaseOut(_show)), 0f, 12f);
        }

        private void HandleDrag(Rect area)
        {
            Event e = Event.current;
            if (e == null) return;
            Rect grip = new Rect(0f, 0f, area.width - 54f, HeaderH);
            if (e.type == EventType.MouseDown && e.button == 0
                && grip.Contains(e.mousePosition))
            {
                _dragging = true;
                _dragOffset = e.mousePosition;
                GUIUtility.hotControl = GUIUtility.GetControlID(FocusType.Passive);
                e.Use();
            }
            else if (_dragging && e.type == EventType.MouseDrag && e.button == 0)
            {
                // **必须在这里夹住，不能只靠 OnGUI 里的 Clamp。** WindowX < 0
                // 是"未设置=居中"的哨兵，拖出左/上边界时若把负值写进去，
                // 下一帧 targetX 就变成居中——窗口瞬间跳回屏幕中间。
                // 拖动时按当前布局公式同款边界夹取，负值永远进不了配置。
                float scale = Mathf.Clamp(Screen.height / 1080f * 1.1f, MinScale, MaxScale);
                float sw = Screen.width / scale, sh = Screen.height / scale;
                float w = Mathf.Min(WindowW, sw - 46f), h = Mathf.Min(WindowH, sh - 24f);
                Settings.WindowX = Mathf.Clamp(
                    _windowRect.x + (e.mousePosition.x - _dragOffset.x), 4f, sw - w - 4f);
                Settings.WindowY = Mathf.Clamp(
                    _windowRect.y + (e.mousePosition.y - _dragOffset.y), 4f, sh - h - 4f);
                e.Use();
            }
            else if (_dragging && e.type == EventType.MouseUp && e.button == 0)
            {
                _dragging = false;
                GUIUtility.hotControl = 0;
                Settings.CommitSafe();
                e.Use();
            }
        }

        private bool _resizing;
        private float _resizeStartW;
        private float _resizeStartH;
        private Vector2 _resizeStartMouse;
        private const float MinW = 640f;
        private const float MinH = 400f;

        /// <summary>右下角调整把手：拖动改 Settings.WindowW/HOverride 并落盘。</summary>
        private void HandleResize(Rect area)
        {
            Event e = Event.current;
            if (e == null) return;
            var grip = new Rect(area.xMax - 22f, area.yMax - 22f, 20f, 20f);
            bool hot = grip.Contains(e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && hot)
            {
                _resizing = true;
                // 记下拖拽起点。**尺寸必须由"起点 + 鼠标位移"算，不能拿
                // _windowRect.xMax 和局部坐标相加**：area 是窗口内坐标、
                // _windowRect 是屏幕坐标，两者混算会让窗口每帧跳到鼠标
                // 所在的屏幕绝对位置——就是"调整时很诡异"的根因。
                _resizeStartW = area.width;
                _resizeStartH = area.height;
                _resizeStartMouse = e.mousePosition;
                GUIUtility.hotControl = GUIUtility.GetControlID(FocusType.Passive);
                e.Use();
            }
            else if (_resizing && e.type == EventType.MouseDrag && e.button == 0)
            {
                float scale = Mathf.Clamp(Screen.height / 1080f * 1.1f, MinScale, MaxScale);
                float sw = Screen.width / scale, sh = Screen.height / scale;
                Settings.WindowWOverride = Mathf.Clamp(
                    _resizeStartW + (e.mousePosition.x - _resizeStartMouse.x),
                    MinW, sw - 46f);
                Settings.WindowHOverride = Mathf.Clamp(
                    _resizeStartH + (e.mousePosition.y - _resizeStartMouse.y),
                    MinH, sh - 24f);
                e.Use();
            }
            else if (_resizing && e.type == EventType.MouseUp && e.button == 0)
            {
                _resizing = false;
                GUIUtility.hotControl = 0;
                Settings.CommitSafe();
                e.Use();
            }
            if (e.type == EventType.Repaint && (hot || _resizing))
            {
                // 提示把手存在：hover / 拖动时右下角三条斜线提亮。
                Color c = new Color(1f, 1f, 1f, hot || _resizing ? 0.55f : 0.25f);
                for (int i = 0; i < 3; i++)
                {
                    Gui.Fill(new Rect(
                        area.xMax - 8f - i * 5f, area.yMax - 4f - i * 5f, 5f, 1.5f), c);
                }
            }
        }

        private void DrawHeader(Rect area)
        {
            Rect r = new Rect(0f, 0f, area.width, HeaderH);
            Rect mark = new Rect(r.x + 16f, r.y + 20f, 19f, 19f);
            GUI.Box(Gui.Snap(mark), GUIContent.none, BrandMarkStyle());
            if (Event.current.type == EventType.Repaint)
            {
                Color oldMarkColor = GUI.color;
                GUI.color = new Color(0.95f, 0.96f, 0.98f, 1f);
                GUI.Label(new Rect(mark.x, mark.y - 1f, mark.width, mark.height),
                    "▶", ModernMarkStyle());
                GUI.color = oldMarkColor;
            }
            GUI.Label(new Rect(r.x + 50f, r.y + 17f, 160f, 25f),
                "SpectreReplay", ModernTitleStyle());
            GUI.Label(new Rect(r.x + 50f, r.y + 39f, 260f, 18f),
                Loc.T(OnGui.TabLabel(OnGui.CurrentTab)), ModernSubtitleStyle());

            if (GUI.Button(new Rect(r.xMax - 46f, r.y + 16f, 30f, 30f), "×",
                    ModernCloseStyle()))
            {
                SetOpen(false);
            }

            Gui.Fill(new Rect(r.x, r.yMax - 1f, r.width, 1f),
                new Color(1f, 1f, 1f, 0.08f));
        }

        /// <summary>
        /// 顶栏页签：文字 + 选中下划线，不画整块按钮底。
        /// 这与右侧栏不是两份导航——独立窗口只保留这一份，避免重复。
        /// </summary>
        private void DrawPageTabs(Rect area)
        {
            Rect strip = new Rect(0f, HeaderH, area.width, TabH);
            // **这里不再铺整块底色。** 之前用 0.045,0.048,0.058,0.96 填满
            // 整个页签区，等于在一整条近乎不透明的黑带上放文字——用户看到
            // 的"底边是黑色的"就是这一条。页签应当直接浮在玻璃背景上，
            // 只用一条 1px 分隔线收边（见函数末尾）。
            float x = 22f;
            Event e = Event.current;
            float selX = 0f, selW = 0f;
            for (int i = 0; i < OnGui.TabCount; i++)
            {
                string label = OnGui.TabLabel(i);
                GUIStyle st = i == OnGui.CurrentTab ? ModernTabOnStyle() : ModernTabStyle();
                float w = st.CalcSize(new GUIContent(label)).x + 22f;
                Rect tab = new Rect(x, strip.y, w, strip.height);
                bool hov = e != null && tab.Contains(e.mousePosition);
                if (e != null && e.type == EventType.MouseDown && e.button == 0 && hov)
                {
                    OnGui.CurrentTab = i;
                    _pageT = 0f;
                    // 一级页换了，二级页内容也跟着换——把二级滑入也拉到起点，
                    // 让它跟一级一起从 0 开始（两级同时动，不是先一级再二级）。
                    _subPageT = 0f;
                    _lastSub = OnGui.CurrentSubTab;
                    _bodyScroll = Vector2.zero;
                    e.Use();
                }
                GUI.Label(tab, label, i == OnGui.CurrentTab ? ModernTabOnStyle()
                    : hov ? ModernTabHoverStyle() : ModernTabStyle());
                if (i == OnGui.CurrentTab)
                {
                    selX = tab.x + 8f;
                    selW = tab.width - 16f;
                }
                x += w;
            }
            // 一级页签和二级页签共用同一套“下划线滑动”语言：
            // 首次打开直接落位；切换后用指数缓动追到新选中项。
            if (e != null && e.type == EventType.Repaint && selW > 0f)
            {
                if (_tabRailW <= 0f || _tabRailX < 0f)
                {
                    _tabRailX = selX;
                    _tabRailW = selW;
                }
                else
                {
                    float k = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
                    _tabRailX = Mathf.Lerp(_tabRailX, selX, k);
                    _tabRailW = Mathf.Lerp(_tabRailW, selW, k);
                }
                Gui.Fill(new Rect(_tabRailX, strip.yMax - 2f, _tabRailW, 2f),
                    Gui.Accent);
            }
            Gui.Fill(new Rect(strip.x, strip.yMax - 1f, strip.width, 1f),
                new Color(1f, 1f, 1f, 0.07f));
        }

        private void DrawBody(Rect area)
        {
            // 二级页签条画在窗口层、不参与任何滑动/淡入动画（切子页时它
            // 固定不动，只有下面的内容滑入）。条本身仍占一行高度，内容区
            // 从它下面开始。
            float subTop = HeaderH + TabH + 4f;
            DrawSubTabs(new Rect(18f, subTop, area.width - 36f, Gui.SegmentTabsH));

            float top = subTop + Gui.SegmentTabsH + 8f;
            float bottom = area.height - FooterH;
            float pageEase = EaseOut(_pageT);
            float subEase = EaseOut(_subPageT);
            // 二级页滑入幅度比一级小一档（10px vs 6px）：内容变化更小，
            // 幅度太大反而显得"整页都重画了一遍"。
            Rect body = new Rect(
                18f + (1f - pageEase) * 10f + (1f - subEase) * 6f, top,
                area.width - 36f, bottom - top - 8f);
            // 正文页自己铺一层圆角深色底，避免内容卡片以外的区域直接透出
            // 外层红色模糊画面；alpha 随开合/切页动画淡入淡出。
            if (Event.current.type == EventType.Repaint)
            {
                Color oldBodyColor = GUI.color;
                GUI.color = new Color(1f, 1f, 1f,
                    BodyBgAlpha * EaseOut(_show) * pageEase * subEase) * Gui.Tint;
                GUI.Box(Gui.Snap(body), GUIContent.none, BodyBgStyle());
                GUI.color = oldBodyColor;
            }
            GUI.BeginGroup(body);
            try
            {
                float oldA = GUI.color.a;
                GUI.color = new Color(1f, 1f, 1f, oldA * pageEase * subEase);
                GUILayout.BeginArea(new Rect(0f, 0f, body.width, body.height));
                GUILayout.BeginHorizontal();
                GUILayout.Space(8f);
                GUILayout.BeginVertical(GUILayout.Width(body.width - 34f));
                GUILayout.Space(10f);
                _bodyScroll = Gui.ModernScrollView(_bodyScroll,
                    () => OnGui.DrawPanel(false, fixedSubTabs: true));
                GUILayout.Space(12f);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
                GUI.color = new Color(1f, 1f, 1f, oldA);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 独立设置页绘制失败：" + ex.Message);
            }
            finally
            {
                GUI.EndGroup();
            }
        }

        /// <summary>二级页签条：窗口层画，固定不动。交互复用 Gui.SegmentTabs。</summary>
        private void DrawSubTabs(Rect area)
        {
            GUILayout.BeginArea(area);
            try
            {
                DrawPatchWarningIfAny();
                int sub = Gui.SegmentTabs(
                    OnGui.SubTabLabelsFor(OnGui.CurrentTab), OnGui.CurrentSubTab, "subtab");
                // 点击帧：SegmentTabs 返回的 sub 与当前不同时，把二级动画拉回
                // 0 并换页。**只在变化时调**——之前每帧都 SetSubTab(sub)，
                // 与 OnGUI 里 _subPageT 的推进互相踩：点击帧内容开始收起，
                // 但下一帧 SegmentTabs 的 current 又是旧高亮，视觉上
                // "页签跳、内容抖"。
                if (sub != OnGui.CurrentSubTab)
                {
                    OnGui.SetSubTab(sub);
                    _subPageT = 0f;
                    _lastSub = sub;
                    _bodyScroll = Vector2.zero;
                }
            }
            finally
            {
                GUILayout.EndArea();
            }
        }

        private void DrawPatchWarningIfAny()
        {
            // PatchWarning 是稀有的（补丁挂载失败才出现），为了不把 OnGui 的
            // 私有方法一个个翻成 internal，这里用同样式的本地副本。
            // 正常情况下（PatchFailures=0）它什么都不画，不占布局。
            if (OnGui.PatchFailuresPublic <= 0) return;
            GUILayout.Space(4f);
            Rect r = GUILayoutUtility.GetRect(1f, 24f, GUILayout.ExpandWidth(true));
            Gui.Fill(r, new Color(Gui.BadCol.r, Gui.BadCol.g, Gui.BadCol.b, 0.18f));
            Gui.Frame(r, new Color(Gui.BadCol.r, Gui.BadCol.g, Gui.BadCol.b, 0.6f));
            Gui.Text(new Rect(r.x + 10f, r.y, r.width - 16f, r.height),
                Loc.T("ui.patchfailed") + "  (" + OnGui.PatchFailuresPublic + ")",
                Gui.BadCol, Gui.BoldStyle);
            GUILayout.Space(4f);
        }

        private void DrawFooter(Rect area)
        {
            Rect r = new Rect(0f, area.height - FooterH, area.width, FooterH);
            Gui.Fill(new Rect(r.x, r.y, r.width, 1f),
                new Color(1f, 1f, 1f, 0.08f));
            GUI.Label(new Rect(r.x + 18f, r.y + 10f, 280f, 22f),
                "SpectreReplay " + (Main.mod != null ? Main.mod.Info.Version.ToString() : "?"),
                ModernFooterStyle());
            // 热键提示做成可点：点它直接跳到"录制 → 快捷键"二级页（改绑处）。
            // 之前只是文案，玩家找不到"在哪改热键"，现在一条路径直达。
            string hint = Loc.F("ui.modern.hint", OnGui.CurrentHotkey(Settings.WindowButton));
            Rect hintRect = new Rect(r.xMax - 420f, r.y + 10f, 400f, 22f);
            GUI.Label(hintRect, hint, ModernFooterStyle());
            if (GUI.Button(hintRect, GUIContent.none, GUIStyle.none)
                && Event.current.type == EventType.Used)
            {
                OnGui.CurrentTab = 0;
                OnGui.SetSubTab(1);
                _pageT = 0f;
                _subPageT = 0f;
                _lastTab = 0;
                _lastSub = OnGui.CurrentSubTab;
                _bodyScroll = Vector2.zero;
            }
        }

        private GUIStyle _titleStyle;
        private GUIStyle _subtitleStyle;
        private GUIStyle _tabStyle;
        private GUIStyle _tabOnStyle;
        private GUIStyle _tabHoverStyle;
        private GUIStyle _closeStyle;
        private GUIStyle _footerStyle;
        private GUIStyle _markStyle;
        private GUIStyle _markTextStyle;

        private GUIStyle ModernTitleStyle()
            => _titleStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 18,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.95f, 0.96f, 0.98f, 1f) },
            };

        private GUIStyle ModernSubtitleStyle()
            => _subtitleStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.62f, 0.65f, 0.72f, 1f) },
            };

        private GUIStyle BrandMarkStyle()
        {
            if (_markStyle != null) return _markStyle;
            _markStyle = new GUIStyle
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(6, 6, 6, 6),
            };
            var tex = RoundedTex.Get(24, 24, 6,
                new Color(0.16f, 0.18f, 0.22f, 0.96f));
            _markStyle.normal.background = tex;
            _markStyle.hover.background = tex;
            _markStyle.active.background = tex;
            _markStyle.focused.background = tex;
            _markStyle.onNormal.background = tex;
            _markStyle.onHover.background = tex;
            _markStyle.onActive.background = tex;
            _markStyle.onFocused.background = tex;
            return _markStyle;
        }

        private GUIStyle ModernMarkStyle()
            => _markTextStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip,
                richText = false,
                normal = { textColor = new Color(0.95f, 0.96f, 0.98f, 1f) },
            };

        private GUIStyle ModernTabStyle()
            => _tabStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                richText = false,
                normal = { textColor = new Color(0.62f, 0.65f, 0.72f, 1f) },
                hover = { textColor = new Color(0.62f, 0.65f, 0.72f, 1f) },
            };

        private GUIStyle ModernTabOnStyle()
            => _tabOnStyle ??= new GUIStyle(ModernTabStyle())
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.95f, 0.96f, 0.98f, 1f) },
                hover = { textColor = new Color(0.95f, 0.96f, 0.98f, 1f) },
            };

        private GUIStyle ModernTabHoverStyle()
            => _tabHoverStyle ??= new GUIStyle(ModernTabStyle())
            {
                // 与二级页签（Gui.SegmentTabs 的 hover=_modernTabOn）同一套
                // 反馈：悬停即加粗提亮，而不是只变色。只变色的反馈在深色
                // 底上太弱，玩家分不清"能点"和"只是经过"。
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.92f, 0.94f, 0.97f, 1f) },
                hover = { textColor = new Color(0.92f, 0.94f, 0.97f, 1f) },
            };

        private GUIStyle ModernCloseStyle()
            => _closeStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 21,
                alignment = TextAnchor.MiddleCenter,
                richText = false,
                normal = { textColor = new Color(0.62f, 0.65f, 0.72f, 1f) },
                hover = { textColor = new Color(0.62f, 0.65f, 0.72f, 1f) },
            };

        private GUIStyle ModernFooterStyle()
            => _footerStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.55f, 0.58f, 0.64f, 1f) },
            };

        private static float EaseOut(float t)
        {
            t = 1f - Mathf.Clamp01(t);
            return 1f - t * t * t;
        }

        /// <summary>
        /// 打开时抓一帧屏幕，连续降采样后模糊，裁剪成窗口大小的玻璃底。
        /// 不是每帧画正弦带，也不是用假噪声冒充 shader。
        /// </summary>
        private System.Collections.IEnumerator CaptureGlass(bool initial)
        {
            yield return new WaitForEndOfFrame();
            RenderTexture full = null;
            try
            {
                // **必须抓"已经合成完的当前屏幕"，不能把游戏相机重新渲染一遍。**
                // 重渲染只画出相机的原始输出：后处理、UI 覆盖、以及
                // backgroundFlash 这类"按当前状态叠上去"的效果全都丢失，
                // 玻璃后面就成了画面里根本不存在的死背景（用户说的
                // "用的是游戏的 background，不是当前实时颜色"）。
                // 只用这一条：CaptureScreenshotIntoRenderTexture。
                full = RenderTexture.GetTemporary(Screen.width, Screen.height, 0,
                    RenderTextureFormat.ARGB32);
                ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
                // **这里只做"整屏模糊"，不做裁剪。**
                // 裁剪留到绘制时按当前窗口位置做 GPU 采样：窗口移动/缩放只需要
                // 换一组 UV，不必重抓屏幕。之前每次拖动都重抓，而且为了不让
                // 窗口被拍进去还得跳过一帧绘制——玩家看到的就是"背景闪一下"。
                // 连续减半降采样 4 次：足够模糊，又不会把内容糊成纯色块。
                var cur = full;
                int cw = Screen.width, ch = Screen.height;
                for (int i = 0; i < 4; i++)
                {
                    cw = Mathf.Max(2, cw / 2); ch = Mathf.Max(2, ch / 2);
                    var nx = RenderTexture.GetTemporary(cw, ch, 0, RenderTextureFormat.ARGB32);
                    nx.filterMode = FilterMode.Bilinear;
                    Graphics.Blit(cur, nx);
                    if (cur != full) RenderTexture.ReleaseTemporary(cur);
                    cur = nx;
                }
                // 直接把模糊后的整屏留作玻璃源（temporary 转正）。
                var keep = new RenderTexture(cw, ch, 0, RenderTextureFormat.ARGB32)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                Graphics.Blit(cur, keep);
                if (cur != full) RenderTexture.ReleaseTemporary(cur);

                var oldFull = _glassFull;
                _glassFull = keep;
                _glassState = 3;
                ReleaseRenderTexture(ref oldFull);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 独立设置玻璃背景失败：" + ex.Message);
                _glassState = initial ? 0 : 3;
            }
            finally
            {
                if (full != null) RenderTexture.ReleaseTemporary(full);
            }
        }

        private static void ReleaseRenderTexture(ref RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            UnityEngine.Object.Destroy(texture);
            texture = null;
        }

        private void ReleaseGlass()
        {
            ReleaseRenderTexture(ref _glassFull);
            ReleaseRenderTexture(ref _glass);
            _glassCrop = default;
        }
    }
}
