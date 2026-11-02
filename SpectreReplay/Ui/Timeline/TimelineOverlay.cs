using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SpectreReplay.Features.Replay;

namespace SpectreReplay.Ui.Timeline;

/// <summary>
/// 屏幕底部的 UGUI 回放时间轴。圆角面板 + 时间刻度尺 + 进度填充 +
/// 可拖动播放头 + 选区手柄。只在关卡回放模式下显示。
/// </summary>
internal sealed class TimelineOverlay : MonoBehaviour
{
    // ── 布局 ──────────────────────────────────────────────────
    private const float PanelHeight = 148f;
    private const float TrackHeight = 24f;
    private const float HandleSize = 12f;
    private const float PlayheadWidth = 5f;
    private const float PanelRadius = 8f;
    private const float TrackRadius = 4f;
    private const float HandleRadius = 5f;
    private const float Padding = 14f;
    private const float TitleRowH = 22f;
    private const float InfoRowH = 22f;
    private const float RulerRowH = 14f;
    private const float RowGap = 2f;

    // ── 配色 ──────────────────────────────────────────────────
    private static readonly Color PanelColor      = new(0.10f, 0.11f, 0.13f, 0.94f);
    private static readonly Color PanelBorderColor = new(1f, 1f, 1f, 0.08f);
    private static readonly Color TrackColor       = new(1f, 1f, 1f, 0.06f);
    private static readonly Color TrackHoverColor  = new(1f, 1f, 1f, 0.11f);
    private static readonly Color ProgressColor    = new(0.35f, 0.72f, 1f, 0.30f);
    private static readonly Color SelectionColor   = new(0.30f, 0.65f, 1f, 0.14f);
    private static readonly Color SelBorderColor   = new(0.35f, 0.72f, 1f, 0.50f);
    private static readonly Color HandleColor      = new(0.40f, 0.78f, 1f, 0.90f);
    private static readonly Color HandleHoverColor = new(0.60f, 0.88f, 1f, 1f);
    private static readonly Color PlayheadColor    = new(1f, 0.85f, 0.28f, 0.95f);
    private static readonly Color MarkerColor      = new(0.55f, 0.80f, 1f, 0.55f);
    private static readonly Color TickColor        = new(1f, 1f, 1f, 0.12f);
    private static readonly Color TextPrimary      = new(0.92f, 0.94f, 0.96f, 0.95f);
    private static readonly Color TextSecondary    = new(0.55f, 0.60f, 0.68f, 0.80f);
    private static readonly Color TextAccent       = new(0.45f, 0.80f, 1f, 0.95f);

    private static TimelineOverlay _instance;

    private Canvas _canvas;
    private RectTransform _root;
    private RectTransform _track;
    private RectTransform _ruler;
    private RectTransform _titleBar;
    private Image _panelBg;
    private Image _panelBorder;
    private Image _trackBg;
    private Image _progress;
    private Image _selection;
    private Image _startHandle;
    private Image _endHandle;
    private Image _playhead;
    private TimelineMarkersGraphic _markers;
    private Text _titleText;
    private Text _timeText;
    private Text _rangeText;
    private InputField _floorSeekField;
    private InputField _selStartField;
    private InputField _selEndField;
    private bool _floorFieldFocused;
    private Text _floorTotalLabel;
    private Font _font;
    private readonly List<Image> _tickLines = new();
    private readonly List<Text> _tickLabels = new();
    private readonly List<Rect> _markerRects = new();

    private int _dragMode; // 0=none 1=seek 2=move-start 3=move-end 4=pan
    private Vector2 _lastMouse;
    private float _downTime;
    private bool _inputCaptured;
    private bool _overTrack;
    private bool _overTitleBar;
    private bool _dragPanel;
            private RectTransform _resizeHandle;
    private bool _dragResize;
    private float _panelWidth = 960f;
    private const float MinPanelWidth = 560f;
    private const float MaxPanelWidth = 1800f;

    internal static bool InputCaptured => _instance != null && _instance._inputCaptured;

    internal static void Ensure()
    {
        if (_instance != null) return;
        var go = new GameObject("SpectreReplay.Timeline");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<TimelineOverlay>();
    }

    internal static void Destroy()
    {
        if (_instance == null) return;
        TimelineController.SetInputCaptured(false);
        Destroy(_instance.gameObject);
        _instance = null;
    }

    private void Awake() => Build();

    private void OnDestroy()
    {
        TimelineController.SetInputCaptured(false);
        if (_instance == this) _instance = null;
    }

    private void Build()
    {
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Font.CreateDynamicFontFromOSFont("Arial", 14);

        _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 25000;
        _canvas.pixelPerfect = true;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        gameObject.AddComponent<GraphicRaycaster>();

        _root = CreateRect("Root", gameObject.transform);
        _root.anchorMin = new Vector2(0.5f, 0.5f);
        _root.anchorMax = new Vector2(0.5f, 0.5f);
        _root.pivot = new Vector2(0.5f, 0.5f);
        _root.sizeDelta = new Vector2(960f, PanelHeight);
        LoadPanelPosition();

        // ── 面板（圆角 + 描边）────────────────────────────────
        _panelBg = _root.gameObject.AddComponent<Image>();
        RoundedSprite.ApplyFill(_panelBg, PanelRadius);
        _panelBg.color = PanelColor;
        _panelBg.raycastTarget = false;

        _panelBorder = CreateImage("PanelBorder", _root);
        RoundedSprite.ApplyOutline(_panelBorder, PanelRadius);
        _panelBorder.color = PanelBorderColor;
        _panelBorder.raycastTarget = false;
        Stretch(_panelBorder.rectTransform);

        // ── 标题行 ──────────────────────────────────────────
        float titleY = -6f;
        float infoY = -32f;
        _titleText = CreateText("Title", _root, _font, 12, TextAnchor.MiddleLeft);
        _titleText.color = TextSecondary;
        SetAnchors(_titleText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(Padding, titleY), new Vector2(180f, TitleRowH));
        _titleText.text = "⠿ " + Loc.T("playback.current");

        // 标题栏拖拽区：覆盖整行，透明
        _resizeHandle = CreateRect("ResizeHandle", _root);
        var resizeImg = _resizeHandle.gameObject.AddComponent<Image>();
        RoundedSprite.ApplyFill(resizeImg, 2f);
        resizeImg.color = new Color(1f, 1f, 1f, 0.15f);
        resizeImg.raycastTarget = false;
        SetAnchors(_resizeHandle, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-6f, 6f), new Vector2(14f, 14f));

        _titleBar = CreateRect("TitleBarDrag", _root);
        var titleBarImg = _titleBar.gameObject.AddComponent<Image>();
        titleBarImg.color = new Color(0f, 0f, 0f, 0f);
        SetAnchors(_titleBar, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -2f), new Vector2(0f, 28f));

        _timeText = CreateText("Time", _root, _font, 14, TextAnchor.MiddleRight);
        _timeText.color = TextAccent;
        SetAnchors(_timeText.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-Padding, titleY), new Vector2(280f, TitleRowH));

        _rangeText = CreateText("Range", _root, _font, 10, TextAnchor.MiddleRight);
        _rangeText.color = TextSecondary;
        SetAnchors(_rangeText.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-Padding, -58f), new Vector2(280f, 14f));

        // 地板 seek 输入框 + 总数标签，与标题同一行
        var floorRow = CreateRect("FloorRow", _root);
        SetAnchors(floorRow, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(Padding, infoY), new Vector2(180f, InfoRowH));

        _floorSeekField = CreateInputField("FloorSeek", floorRow, _font, Loc.T("timeline.floor.hint"), 40f);
        // 输入框自身定位由 RectTransform 锚点控制，不另设 position

        var floorTotal = CreateText("FloorTotal", floorRow, _font, 11, TextAnchor.MiddleLeft);
        floorTotal.color = TextSecondary;
        SetAnchors(floorTotal.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            new Vector2(44f, 0f), new Vector2(150f, InfoRowH));
        _floorTotalLabel = floorTotal;

        _floorSeekField.onEndEdit.AddListener(_ => OnFloorSeekSubmit());

        // 选区输入框与地板输入框同一行
        var selRow = CreateRect("SelFloorRow", _root);
        SetAnchors(selRow, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(Padding + 200f, infoY), new Vector2(180f, InfoRowH));

        var selLabel = CreateText("SelLabel", selRow, _font, 9, TextAnchor.MiddleLeft);
        selLabel.color = TextSecondary;
        SetAnchors(selLabel.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            new Vector2(0f, 0f), new Vector2(56f, 16f));
        selLabel.text = Loc.T("timeline.selection");

        _selStartField = CreateInputField("SelStart", selRow, _font, "起", 34f);
        SetAnchors((RectTransform)_selStartField.transform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            new Vector2(62f, 0f), new Vector2(34f, 16f));

        var arrow = CreateText("Arrow", selRow, _font, 9, TextAnchor.MiddleLeft);
        arrow.color = TextSecondary;
        SetAnchors(arrow.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(118f, 0f), new Vector2(14f, 12f));
        arrow.text = "→";

        _selEndField = CreateInputField("SelEnd", selRow, _font, "止", 34f);
        SetAnchors((RectTransform)_selEndField.transform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            new Vector2(134f, 0f), new Vector2(34f, 16f));

        // Enter 提交回调
        _selStartField.onEndEdit.AddListener(_ => OnSelStartSubmit());
        _selEndField.onEndEdit.AddListener(_ => OnSelEndSubmit());

        // ── 轨道 ────────────────────────────────────────────
        float trackY = (PanelHeight - TrackHeight) / 2f;
        _track = CreateRect("Track", _root);
        SetAnchors(_track, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, trackY - RulerRowH / 2f + 2f), new Vector2(-Padding * 2, TrackHeight));

        _trackBg = _track.gameObject.AddComponent<Image>();
        RoundedSprite.ApplyFill(_trackBg, TrackRadius);
        _trackBg.color = TrackColor;
        _trackBg.raycastTarget = false;

        var markersObject = CreateRect("Markers", _track);
        Stretch(markersObject);
        _markers = markersObject.gameObject.AddComponent<TimelineMarkersGraphic>();

        _progress = CreateImage("Progress", _track);
        _progress.color = ProgressColor;
        _progress.raycastTarget = false;

        _selection = CreateImage("Selection", _track);
        _selection.color = SelectionColor;
        _selection.raycastTarget = false;

        // 选区左右边框
        CreateSelBorder("SelBorderL", 1f);
        CreateSelBorder("SelBorderR", 1f);

        _startHandle = CreateImage("StartHandle", _track);
        RoundedSprite.ApplyFill(_startHandle, HandleRadius);
        _startHandle.color = HandleColor;
        _startHandle.raycastTarget = false;

        _endHandle = CreateImage("EndHandle", _track);
        RoundedSprite.ApplyFill(_endHandle, HandleRadius);
        _endHandle.color = HandleColor;
        _endHandle.raycastTarget = false;

        // 手柄初始锚点：不放这里，UpdateVisuals 每帧设置
        _startHandle.rectTransform.anchorMin = Vector2.zero;
        _startHandle.rectTransform.anchorMax = Vector2.zero;
        _endHandle.rectTransform.anchorMin = Vector2.zero;
        _endHandle.rectTransform.anchorMax = Vector2.zero;

        _playhead = CreateImage("Playhead", _track);
        RoundedSprite.ApplyFill(_playhead, 1.5f);
        _playhead.color = PlayheadColor;
        _playhead.raycastTarget = false;

        // ── 时间刻度尺 ──────────────────────────────────────
        _ruler = CreateRect("Ruler", _root);
        SetAnchors(_ruler, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, trackY - TrackHeight / 2f - RulerRowH / 2f - 8f), new Vector2(-Padding * 2, RulerRowH));
        // 刻度线 + 标签在 Update 时动态创建/更新

        _canvas.enabled = false;
    }

    private Image _selBorderL;
    private Image _selBorderR;

    private void CreateSelBorder(string name, float width)
    {
        if (name == "SelBorderL") { _selBorderL = CreateImage(name, _track); _selBorderL.color = SelBorderColor; _selBorderL.raycastTarget = false; }
        else { _selBorderR = CreateImage(name, _track); _selBorderR.color = SelBorderColor; _selBorderR.raycastTarget = false; }
    }

    // ─── 可见性 ─────────────────────────────────────────────

    private static bool InLevelScene
    {
        get
        {
            string name = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            return name == "scnEditor" || name == "scnGame";
        }
    }

    private void Update()
    {
        var state = TimelineController.State;
        bool shouldShow = Settings.TimelineOn
            && !SpectreReplay.Features.VideoRender.VideoRender.Active
            && state.Source != null
            && state.Duration > 0.0
            && SpectreState.PlayMode
            && InLevelScene;

        if (!shouldShow)
        {
            if (_canvas.enabled) _canvas.enabled = false;
            _inputCaptured = false;
            TimelineController.SetInputCaptured(false);
            return;
        }

        if (!_canvas.enabled) _canvas.enabled = true;

        if (ReplayPlayer.TryGetSongPosition(out double songPos))
            TimelineController.Tick(songPos);

        // 输入框聚焦时阻断游戏键盘输入（防止数字被游戏当作按键）
        bool fieldFocused = _floorSeekField != null && _floorSeekField.isFocused
            || _selStartField != null && _selStartField.isFocused
            || _selEndField != null && _selEndField.isFocused;
        _floorFieldFocused = fieldFocused;

        HandleInput(state);
        UpdateVisuals(state);
    }

    // ─── 输入 ───────────────────────────────────────────────

    private void HandleInput(ReplayTimelineState state)
    {
        Vector2 mouse = Input.mousePosition;
        _overTitleBar = ContainsPoint(_titleBar, mouse);
        _overTrack = ContainsPoint(_track, mouse);

        // ── 面板大小调整（右下角手柄）─────────────────────────
        bool overResize = ContainsPoint(_resizeHandle, mouse, 4f);
        if (_dragResize)
        {
            _inputCaptured = true;
            TimelineController.SetInputCaptured(true);
            Vector2 rdelta = mouse - _lastMouse;
            _panelWidth = Mathf.Clamp(_panelWidth + rdelta.x * 2f, MinPanelWidth, MaxPanelWidth);
            _root.sizeDelta = new Vector2(_panelWidth, PanelHeight);
            _lastMouse = mouse;
            if (Input.GetMouseButtonUp(0))
            {
                _dragResize = false;
                Settings.TimelineWidth = _panelWidth;
            }
            return;
        }
        if (Input.GetMouseButtonDown(0) && overResize)
        {
            _dragResize = true;
            _lastMouse = mouse;
            return;
        }

        // ── 面板拖拽（标题栏）────────────────────────────────
        if (_dragPanel)
        {
            _inputCaptured = true;
            TimelineController.SetInputCaptured(true);
            Vector2 delta = mouse - _lastMouse;
            _root.anchoredPosition += delta;
            // 钳制在屏幕内
            var canvasRect = (_canvas.transform as RectTransform).rect;
            float halfW = _root.rect.width / 2f;
            float halfH = _root.rect.height / 2f;
            _root.anchoredPosition = new Vector2(
                Mathf.Clamp(_root.anchoredPosition.x, -canvasRect.width / 2f + halfW, canvasRect.width / 2f - halfW),
                Mathf.Clamp(_root.anchoredPosition.y, -canvasRect.height / 2f + halfH, canvasRect.height / 2f - halfH));
            _lastMouse = mouse;
            if (Input.GetMouseButtonUp(0))
            {
                _dragPanel = false;
                SavePanelPosition();
            }
            return;
        }
        if (Input.GetMouseButtonDown(0) && _overTitleBar)
        {
            _dragPanel = true;
            _lastMouse = mouse;
            return;
        }
        bool overStart = ContainsPoint(_startHandle.rectTransform, mouse, 6f);
        bool overEnd = ContainsPoint(_endHandle.rectTransform, mouse, 6f);
        bool overPlayhead = ContainsPoint(_playhead.rectTransform, mouse, 10f);

        if (Input.GetMouseButtonDown(0))
        {
            if (overStart) { _dragMode = 2; _lastMouse = mouse; }
            else if (overEnd) { _dragMode = 3; _lastMouse = mouse; }
            else if (overPlayhead)
            {
                _dragMode = 1;
                _lastMouse = mouse;
            }
            else if (_overTrack)
            {
                _dragMode = 1;
                _downTime = Time.unscaledTime;
                _lastMouse = mouse;
                TimelineController.PreviewSeek(TimeAtPointer(state, mouse));
            }
        }
        else if (Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2))
        {
            if (_overTrack) { _dragMode = 4; _lastMouse = mouse; }
        }

        if (_dragMode != 0)
        {
            _inputCaptured = true;
            TimelineController.SetInputCaptured(true);
            Vector2 delta = mouse - _lastMouse;
            if (_dragMode == 1) TimelineController.PreviewSeek(TimeAtPointer(state, mouse));
            else if (_dragMode == 2) state.MoveSelectionStart(TimeAtPointer(state, mouse));
            else if (_dragMode == 3) state.MoveSelectionEnd(TimeAtPointer(state, mouse));
            else if (_dragMode == 4) state.Pan(-delta.x * state.ViewSpan / Mathf.Max(1f, _track.rect.width));
            _lastMouse = mouse;
        }
        else
        {
            _inputCaptured = false;
            TimelineController.SetInputCaptured(false);
        }

        if (Input.GetMouseButtonUp(0))
        {
            if (_dragMode == 1 && _overTrack)
                TimelineController.Seek(TimeAtPointer(state, mouse));
            else if (_dragMode == 1 && !_overTrack && Time.unscaledTime - _downTime < 0.3f)
                state.Fit();
            _dragMode = 0;
        }
        if (Input.GetMouseButtonUp(1) || Input.GetMouseButtonUp(2))
            _dragMode = 0;

        float wheel = Input.mouseScrollDelta.y;
        if (Mathf.Abs(wheel) > 0.01f && _overTrack)
            state.Zoom(wheel > 0f ? 0.85f : 1.18f, TimeAtPointer(state, mouse));
    }


    private double TimeAtPointer(ReplayTimelineState state, Vector2 mouse)
    {
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_track, mouse, null, out Vector2 local))
            return state.Cursor;
        float w = Mathf.Max(1f, _track.rect.width);
        return state.RatioToTime((local.x / w) + 0.5f);
    }

    // ─── 视觉 ───────────────────────────────────────────────

    private void UpdateVisuals(ReplayTimelineState state)
    {
        float w = Mathf.Max(1f, _track.rect.width);
        float h = _track.rect.height;

        // 悬停
        _trackBg.color = _overTrack ? TrackHoverColor : TrackColor;

        // 进度填充（0 → cursor）
        float cursorX = Mathf.Clamp01((float)state.TimeToRatio(state.Cursor));
        SetAnchors(_progress.rectTransform, new Vector2(0f, 0f), new Vector2(cursorX, 1f),
            new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

        // 选区
        float selStart = Mathf.Clamp01((float)state.TimeToRatio(state.Selection.Start));
        float selEnd = Mathf.Clamp01((float)state.TimeToRatio(state.Selection.End));
        SetAnchors(_selection.rectTransform, new Vector2(selStart, 0f), new Vector2(selEnd, 1f),
            new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        SetAnchors(_selBorderL.rectTransform, new Vector2(selStart, 0f), new Vector2(selStart, 1f),
            new Vector2(0.5f, 0.5f), new Vector2(-0.5f, 0f), new Vector2(1f, 0f));
        SetAnchors(_selBorderR.rectTransform, new Vector2(selEnd, 0f), new Vector2(selEnd, 1f),
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f), new Vector2(1f, 0f));

        // 手柄
        bool hStart = _dragMode == 2 || ContainsPoint(_startHandle.rectTransform, Input.mousePosition, 6f);
        bool hEnd = _dragMode == 3 || ContainsPoint(_endHandle.rectTransform, Input.mousePosition, 6f);
        _startHandle.color = hStart ? HandleHoverColor : HandleColor;
        _endHandle.color = hEnd ? HandleHoverColor : HandleColor;
        SetAnchors(_startHandle.rectTransform, new Vector2(selStart, 0.5f), new Vector2(selStart, 0.5f),
            new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(HandleSize, HandleSize));
        SetAnchors(_endHandle.rectTransform, new Vector2(selEnd, 0.5f), new Vector2(selEnd, 0.5f),
            new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(HandleSize, HandleSize));

        // 播放头
        SetAnchors(_playhead.rectTransform, new Vector2(cursorX, 0f), new Vector2(cursorX, 1f),
            new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(PlayheadWidth, 0f));

        // 文本
        double dur = state.Duration;
        _timeText.text = FormatTime(state.Cursor) + " / " + FormatTime(dur);
        // ── 地板信息更新 ────────────────────────────────
        int total = TimelineController.TotalFloors;
        if (total > 0)
        {
            int curFloor = TimelineController.CurrentFloor;
            if (!_floorSeekField.isFocused)
                _floorSeekField.text = (curFloor + 1).ToString();
            _floorTotalLabel.text = "/ " + total;

            int selStartF = TimelineController.SelectionStartFloor;
            int selEndF = TimelineController.SelectionEndFloor;
            if (!_selStartField.isFocused)
                _selStartField.text = (selStartF + 1).ToString();
            if (!_selEndField.isFocused)
                _selEndField.text = (selEndF + 1).ToString();
        }

        _rangeText.text = Loc.T("timeline.selection") + " " + FormatTime(state.Selection.Start) + " → " + FormatTime(state.Selection.End)
            + "  (" + FormatTime(state.Selection.Duration) + ")";

        // 刻度尺
        UpdateRuler(state, w);

        // 标记
        _markerRects.Clear();
        var keys = state.Index.KeyTimes;
        int maxM = Mathf.Max(32, Mathf.FloorToInt(w));
        if (keys.Count <= maxM)
        {
            for (int i = 0; i < keys.Count; i++)
                AddMarker(state, keys[i], w, 0.20f, 0.80f);
        }
        else
        {
            int buckets = maxM;
            var last = new int[buckets];
            for (int i = 0; i < last.Length; i++) last[i] = -1;
            for (int i = 0; i < keys.Count; i++)
            {
                int b = Mathf.Clamp(Mathf.FloorToInt((float)state.TimeToRatio(keys[i]) * buckets), 0, buckets - 1);
                if (last[b] < 0) { last[b] = i; AddMarker(state, keys[i], w, 0.15f, 0.68f); }
            }
        }
        _markers.SetMarkers(_markerRects, MarkerColor);
    }

    private void UpdateRuler(ReplayTimelineState state, float width)
    {
        // 选合适的刻度间隔：目标 6~12 个刻度
        double viewSpan = state.ViewSpan;
        double rawInterval = viewSpan / 8.0;
        double interval = PickInterval(rawInterval);
        double startTick = Math.Floor(state.ViewStart / interval) * interval;

        int needed = (int)Math.Ceiling(viewSpan / interval) + 2;
        if (needed > 20) needed = 20;
        EnsureTicks(needed);

        int count = 0;
        for (double t = startTick; t <= state.ViewStart + viewSpan + 0.001 && count < needed; t += interval)
        {
            if (t < 0) continue;
            float ratio = Mathf.Clamp01((float)state.TimeToRatio(t));
            if (ratio <= 0f || ratio >= 1f) continue;

            var line = _tickLines[count];
            SetAnchors(line.rectTransform, new Vector2(ratio, 0f), new Vector2(ratio, 1f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1f, 0f));
            line.gameObject.SetActive(true);

            var label = _tickLabels[count];
            label.text = FormatTick(t, interval);
            SetAnchors(label.rectTransform, new Vector2(ratio, 0.5f), new Vector2(ratio, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(60f, RulerRowH));
            label.gameObject.SetActive(true);
            count++;
        }
        // 隐藏多余的
        for (int i = count; i < _tickLines.Count; i++)
        {
            _tickLines[i].gameObject.SetActive(false);
            _tickLabels[i].gameObject.SetActive(false);
        }
    }

    private static double PickInterval(double raw)
    {
        double[] candidates = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
        foreach (double c in candidates)
        {
            if (raw <= c) return c;
        }
        return 600;
    }

    private void EnsureTicks(int count)
    {
        while (_tickLines.Count < count)
        {
            var line = CreateImage("Tick" + _tickLines.Count, _ruler);
            line.color = TickColor;
            line.raycastTarget = false;
            line.gameObject.SetActive(false);
            _tickLines.Add(line);

            var label = CreateText("TickLabel" + _tickLabels.Count, _ruler, _font, 9, TextAnchor.UpperCenter);
            label.color = TextSecondary;
            label.raycastTarget = false;
            label.gameObject.SetActive(false);
            _tickLabels.Add(label);
        }
    }

    private static string FormatTick(double time, double interval)
    {
        if (interval >= 60.0)
        {
            int min = (int)(time / 60.0);
            return min + ":00";
        }
        if (interval >= 1.0)
        {
            int min = (int)(time / 60.0);
            int sec = (int)(time % 60.0);
            return $"{min}:{sec:00}";
        }
        // 亚秒间隔
        TimeSpan ts = TimeSpan.FromSeconds(time);
        return $"{(int)ts.TotalSeconds}.{ts.Milliseconds / 100:0}s";
    }

    private void AddMarker(ReplayTimelineState state, double time, float width, float yMin, float yMax)
    {
        float x = Mathf.Clamp01((float)state.TimeToRatio(time));
        float h = _track.rect.height;
        _markerRects.Add(new Rect(((x - 0.5f) * width) - 0.5f, h * yMin, 1.5f, h * (yMax - yMin)));
    }

    // ─── 工具 ───────────────────────────────────────────────

    private static bool ContainsPoint(RectTransform rect, Vector2 screenPoint, float padding = 0f)
    {
        if (rect == null) return false;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPoint, null, out Vector2 local))
            return false;
        Rect r = rect.rect;
        return local.x >= r.xMin - padding && local.x <= r.xMax + padding
            && local.y >= r.yMin - padding && local.y <= r.yMax + padding;
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static Image CreateImage(string name, Transform parent)
    {
        var rect = CreateRect(name, parent);
        return rect.gameObject.AddComponent<Image>();
    }

    private static Text CreateText(string name, Transform parent, Font font, int size, TextAnchor anchor)
    {
        var rect = CreateRect(name, parent);
        var text = rect.gameObject.AddComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.alignment = anchor;
        text.color = Color.white;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private static InputField CreateInputField(string name, Transform parent, Font font, string placeholder, float width)
    {
        var rect = CreateRect(name, parent);
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.sizeDelta = new Vector2(width, 16f);
        rect.anchoredPosition = Vector2.zero;

        var bg = rect.gameObject.AddComponent<Image>();
        RoundedSprite.ApplyFill(bg, 3f);
        bg.color = new Color(1f, 1f, 1f, 0.08f);

        var field = rect.gameObject.AddComponent<InputField>();
        field.textComponent = CreateText("Value", rect, font, 11, TextAnchor.MiddleLeft);
        field.textComponent.color = TextAccent;
        Stretch(field.textComponent.rectTransform);
        field.textComponent.rectTransform.offsetMin = new Vector2(4f, 0f);
        field.textComponent.rectTransform.offsetMax = new Vector2(-4f, 0f);

        var phObj = CreateText("Placeholder", rect, font, 10, TextAnchor.MiddleLeft);
        phObj.color = new Color(1f, 1f, 1f, 0.25f);
        Stretch(phObj.rectTransform);
        phObj.rectTransform.offsetMin = new Vector2(4f, 0f);
        phObj.rectTransform.offsetMax = new Vector2(-4f, 0f);
        field.placeholder = phObj;
        phObj.text = placeholder;

        field.contentType = InputField.ContentType.IntegerNumber;
        return field;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void SetAnchors(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPosition, Vector2 sizeDelta)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;
    }

    private void LoadPanelPosition()
    {
        float x = Settings.TimelinePosX;
        float y = Settings.TimelinePosY;
        _panelWidth = Settings.TimelineWidth;
        _root.sizeDelta = new Vector2(_panelWidth, PanelHeight);
        _root.anchoredPosition = new Vector2(x, y);
    }

    private void SavePanelPosition()
    {
        Settings.TimelinePosX = _root.anchoredPosition.x;
        Settings.TimelinePosY = _root.anchoredPosition.y;
    }

    private void OnFloorSeekSubmit()
    {
        if (int.TryParse(_floorSeekField.text, out int floorNum) && floorNum >= 1)
            SeekToFloor(floorNum - 1);
    }

    private void OnSelStartSubmit()
    {
        if (int.TryParse(_selStartField.text, out int floorNum) && floorNum >= 1)
        {
            var floors = ADOBase.lm?.listFloors;
            if (floors != null && floorNum - 1 < floors.Count)
                TimelineController.State.MoveSelectionStart(floors[floorNum - 1].entryTime);
        }
    }

    private void OnSelEndSubmit()
    {
        if (int.TryParse(_selEndField.text, out int floorNum) && floorNum >= 1)
        {
            var floors = ADOBase.lm?.listFloors;
            if (floors != null && floorNum - 1 < floors.Count)
                TimelineController.State.MoveSelectionEnd(floors[floorNum - 1].entryTime);
        }
    }

    private static void SeekToFloor(int floorIndex)
    {
        var floors = ADOBase.lm?.listFloors;
        if (floors == null || floorIndex < 0 || floorIndex >= floors.Count) return;
        TimelineController.Seek(floors[floorIndex].entryTime);
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0) seconds = 0.0;
        TimeSpan v = TimeSpan.FromSeconds(seconds);
        return $"{(int)v.TotalMinutes:00}:{v.Seconds:00}.{v.Milliseconds:000}";
    }
}
