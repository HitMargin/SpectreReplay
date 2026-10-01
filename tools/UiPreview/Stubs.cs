// Unity 桩件。目的：把真实的 Gfx.cs / PageBuilder.cs / Theme.cs / Loc.cs
// 拿到一个控制台程序里跑，从而在不开游戏的情况下得到真实布局。
//
// 只实现"真实代码用到的那部分 API"，多一个都没有——桩件一旦比真实 Unity
// 更宽松，验证就失去意义了。
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0, 0);
        public static Vector2 one => new Vector2(1, 1);
        public override string ToString() => $"({x}, {y})";
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; this.a = 1f; }
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1, 1, 1, 1);
        public static Color black => new Color(0, 0, 0, 1);
        public static Color clear => new Color(0, 0, 0, 0);
        public override string ToString()
            => $"#{Mathf.Round(r * 255):X2}{Mathf.Round(g * 255):X2}{Mathf.Round(b * 255):X2}"
             + (a >= 0.999f ? "" : $"/{Mathf.Round(a * 255):X2}");
    }

    public static class Mathf
    {
        public static float Clamp(float v, float a, float b) => v < a ? a : (v > b ? b : v);
        public static float Clamp01(float v) => Clamp(v, 0f, 1f);
        public static float Round(float v) => (float)Math.Round(v, MidpointRounding.AwayFromZero);
        public static int RoundToInt(float v) => (int)Round(v);
        public static int CeilToInt(float v) => (int)Math.Ceiling(v);
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static float Abs(float a) => a < 0 ? -a : a;
        // 真实 Unity 有 int 重载；Gfx.cs 的 Clamp(selected, 0, Max(0, len-1)) 会用到
        public static int Max(int a, int b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static int Clamp(int v, int a, int b) => v < a ? a : (v > b ? b : v);
        public static int Abs(int a) => a < 0 ? -a : a;
    }

    /// <summary>
    /// 游戏的常量资源（ScriptableObject）。UiFonts 从这里取中日韩字体。
    /// 桩件做成可赋值，才能把字体回退链的每条分支都跑一遍。
    /// </summary>
    public class RDConstants
    {
        public static RDConstants data;

        public TMPro.TMP_FontAsset latinFontTMPro;
        public TMPro.TMP_FontAsset chineseFontTMPro;
        public TMPro.TMP_FontAsset chineseFontTMProDuplicate;
        public TMPro.TMP_FontAsset koreanFontTMPro;
        public TMPro.TMP_FontAsset japaneseFontTMPro;
    }

    public enum SystemLanguage
    {
        English, Chinese, ChineseSimplified, ChineseTraditional,
        Japanese, Korean,
    }

    /// <summary>
    /// 游戏的语言设置。与真实 RDString.language 同名同类型，
    /// Loc.RefreshLanguage 直接读它。
    /// </summary>
    public static class RDString
    {
        public static SystemLanguage language = SystemLanguage.English;
    }

    /// <summary>
    /// Resources 加载的桩。资源路径按**游戏真实清单**判定——清单在
    /// StreamingAssets\aa\catalog.json 里，只有 SourceHanSans/{Normal,Light,
    /// Medium,Bold,Heavy,ExtraLight} 与 SourceHanSans/{Latin,ChineseSimplified,
    /// ChineseTraditional,Japanese,Korean}/*。不存在的路径必须返回 null，
    /// 否则预览会把"路径写错了"这件事藏起来。
    /// </summary>
    public static class Resources
    {
        /// <summary>SourceHanSans 资源是否存在。</summary>
        public static bool HasSourceHanSans = true;

        /// <summary>最近一次被请求的路径，便于断言"没去请求不存在的资源"。</summary>
        public static string LastPath;

        private static readonly string[] Real =
        {
            "SourceHanSans/Normal", "SourceHanSans/Light", "SourceHanSans/Medium",
            "SourceHanSans/Bold", "SourceHanSans/Heavy", "SourceHanSans/ExtraLight",
            "SourceHanSans/Latin/Latin-Normal",
            "SourceHanSans/ChineseSimplified/SourceHanSansSC-Normal",
            "SourceHanSans/ChineseSimplified/ChineseSimplified-Normal",
            "SourceHanSans/ChineseTraditional/SourceHanSansTC-Normal",
            "SourceHanSans/Japanese/Japanese-Normal",
            "SourceHanSans/Korean/Korean-Normal",
        };

        internal static bool Exists(string path)
        {
            foreach (var p in Real)
            {
                if (p == path) return true;
            }
            return false;
        }

        public static T Load<T>(string path) where T : Object
        {
            if (typeof(T).Name != "TMP_FontAsset")
            {
                return null;
            }
            LastPath = path;
            if (!HasSourceHanSans || !Exists(path))
            {
                return null;
            }
            return (T)(Object)new TMPro.TMP_FontAsset { name = path };
        }
    }

    /// <summary>对应 UnityEngine.MissingReferenceException：访问已销毁对象。</summary>
    public class MissingReferenceException : Exception
    {
        public MissingReferenceException(string m) : base(m) { }
    }

    public class Object
    {
        public string name;
        public bool activeSelf = true;

        /// <summary>
        /// 已被销毁。真实 Unity 里访问已销毁对象会抛 MissingReferenceException，
        /// 而原先的桩件照常返回——于是"销毁后又去取它的字段"这种在真机上
        /// 必崩的写法，在预览里一路绿灯。把这层补上。
        /// </summary>
        public bool destroyed;

        internal void ThrowIfDestroyed()
        {
            if (destroyed)
            {
                throw new MissingReferenceException(
                    "对象已被销毁（真机上访问它会抛 MissingReferenceException）");
            }
        }

        /// <summary>
        /// 预览是单次构建，**立即**摘除即可。
        ///
        /// 真实 Unity 是推迟到帧末销毁的，于是"销毁旧子节点 + 立刻建新的"
        /// 那一瞬间，父节点下新旧并存。原桩件不模拟这一点——好处是不会
        /// 平白多出一堆重复节点，代价是"新旧并存"这类问题验不到。
        /// 目前逐个查过：Show() 销毁旧子节点后只调 LiveTexts.Clear()，
        /// 不会再去读 childCount，帧末才销毁也不会碰到。
        /// </summary>
        public static void Destroy(Object o)
        {
            if (o == null) return;
            // 真实 Unity 销毁一个 GameObject 会连整棵子树一起销毁。
            // 只标记顶层的话，子节点的 GetComponent 仍能照常返回——
            // 于是"销毁整棵面板"在桩件里只毁了一半。
            if (o is GameObject go)
            {
                MarkTree(go.transform);
                if (go.transform.parent != null)
                {
                    go.transform.parent.children.Remove(go.transform);
                }
            }
            o.destroyed = true;
        }

        private static void MarkTree(Transform t)
        {
            if (t == null) return;
            t.gameObject.destroyed = true;
            foreach (var c in new List<Transform>(t.children))
            {
                MarkTree(c);
            }
        }

        public static void DestroyImmediate(Object o) => Destroy(o);

        public static void DontDestroyOnLoad(Object o) { }

        public static T FindAnyObjectByType<T>() where T : class => null;
        public void SetActive(bool v) => activeSelf = v;
    }

    /// <summary>面板的输入轮询挂在它上面；预览不跑帧，只占位。</summary>
    public class MonoBehaviour : Component
    {
    }

    // Canvas 一族：预览不用它们（BuildInto 不建 Canvas），但同一文件里的
    // Build() 引用了，所以必须存在——否则桩件比真实 Unity 窄，编译不过就
    // 没法复用同一份 PanelHost 源码。
    public enum RenderMode { ScreenSpaceOverlay, ScreenSpaceCamera, WorldSpace }

    public class Canvas : Component
    {
        public RenderMode renderMode;
        public int sortingOrder;
    }

    public class CanvasScaler : Component
    {
        public enum ScaleMode { ConstantPixelSize, ScaleWithScreenSize, ConstantPhysicalSize }
        public enum ScreenMatchMode { MatchWidthOrHeight, Expand, Shrink }
        public ScaleMode uiScaleMode;
        public Vector2 referenceResolution;
        public ScreenMatchMode screenMatchMode;
        public float matchWidthOrHeight;
    }

    public class GraphicRaycaster : Component { }

    public static class Screen
    {
        public static int width = 1920;
        public static int height = 1080;
    }

    public static class Debug
    {
        public static void Log(object o) => Console.WriteLine(o);
        public static void LogWarning(object o) => Console.WriteLine(o);
    }

    public static class Time
    {
        public static float unscaledTime;
    }

    public static class Input
    {
        public static bool GetKeyDown(KeyCode k) => false;
    }

    public enum KeyCode
    {
        Escape = 27,
        F1 = 282, F2 = 283, F3 = 284, F4 = 285, F5 = 286, F6 = 287,
        F7 = 288, F8 = 289, F9 = 290, F10 = 291, F11 = 292, F12 = 293,
    }

    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject?.transform;
        public RectTransform rectTransform => gameObject?.transform as RectTransform;
        public T GetComponent<T>() where T : class => gameObject?.GetComponent<T>();
    }

    public class Transform : Component
    {
        public Transform parent;

        /// <summary>
        /// 原先只 `p?.children.Add(this)`，**从不设置 this.parent**——
        /// 于是 parent 恒为 null，Destroy 里那句"从父节点摘除"从来没执行过，
        /// 被销毁的节点一直留在树上。预览照样能出图（节点照样被遍历到，
        /// 只是内容重复），所以看不出问题；直到给 GetComponent 加上
        /// 销毁检查，一访问就抛。
        /// </summary>
        public readonly List<Transform> children = new List<Transform>();

        public void SetParent(Transform p, bool worldPositionStays)
        {
            parent?.children.Remove(this);
            parent = p;
            p?.children.Add(this);
        }
        public int childCount { get { ThrowIfDestroyed(); return children.Count; } }
        public Transform GetChild(int i) => children[i];
        public Transform Find(string n) => null;   // 真实代码只在运行时用
        public Transform FindDeep(string n)
        {
            foreach (var c in children)
            {
                if (c.gameObject.name == n) return c;
                var r = c.FindDeep(n);
                if (r != null) return r;
            }
            return null;
        }
    }

    public class RectTransform : Transform
    {
        public Vector2 anchorMin, anchorMax, pivot, anchoredPosition, sizeDelta, offsetMin, offsetMax;
        /// <summary>预览用：解析出的绝对矩形（左上角原点、y 向下）。</summary>
        public float X, Y, W, H;
    }

    public class GameObject : Object
    {
        public readonly Transform transform;
        private readonly Dictionary<Type, Component> comps = new Dictionary<Type, Component>();

        public GameObject() : this("GO") { }

        public GameObject(string n, params Type[] types)
        {
            name = n;
            transform = Make(this, new RectTransform());
            foreach (var t in types) AddComponent(t);
        }

        private static T Make<T>(GameObject go, T c) where T : Component
        {
            c.gameObject = go;
            c.name = go.name;
            return c;
        }

        public T AddComponent<T>() where T : Component, new()
        {
            var c = Make(this, new T());
            comps[typeof(T)] = c;
            RegisterSpecial(c);
            return c;
        }

        /// <summary>
        /// 真实 Unity 里 EventSystem 一被挂上就会成为 <c>EventSystem.current</c>。
        /// 桩件不模拟这一点的话，"场景里到底有没有事件系统"这条永远验不了。
        /// </summary>
        private static void RegisterSpecial(Component c)
        {
            if (c is EventSystems.EventSystem es)
            {
                EventSystems.EventSystem.current = es;
            }
        }

        public Component AddComponent(Type t)
        {
            var c = (Component)Activator.CreateInstance(t);
            c.gameObject = this;
            comps[t] = c;
            RegisterSpecial(c);
            return c;
        }

        /// <summary>
        /// 必须像真实 Unity 那样沿**继承链**查找：TMP_Text 是 TextMeshProUGUI
        /// 的基类，按精确类型查表会让 GetComponent&lt;TMP_Text&gt;() 永远返回 null，
        /// 于是预览里一个字都读不到——这种桩件比真实 Unity 窄，验证就成了摆设。
        /// </summary>
        public T GetComponent<T>() where T : class
        {
            // 真实 Unity：GameObject 被销毁后再取组件会抛 MissingReferenceException
            ThrowIfDestroyed();
            foreach (var c in comps.Values)
            {
                if (c is T hit) return hit;
            }
            return null;
        }
    }
}

namespace UnityEngine.Events
{
    public class UnityEvent<T>
    {
        private readonly List<Action<T>> _list = new List<Action<T>>();
        public void AddListener(Action<T> cb) { if (cb != null) _list.Add(cb); }
        public void Invoke(T v) { foreach (var c in _list) c(v); }
    }

    /// <summary>Button.onClick 是无参事件，与 UnityEvent&lt;T&gt; 不是一回事。</summary>
    public class ButtonEvent
    {
        private readonly List<Action> _list = new List<Action>();
        public void AddListener(Action cb) { if (cb != null) _list.Add(cb); }
        public void Invoke() { foreach (var c in _list) c(); }
    }
}

namespace UnityEngine.UI
{
    using UnityEngine;
    using UnityEngine.Events;

    public class Graphic : Component
    {
        public Color color = Color.white;
        public bool raycastTarget = true;
    }

    public class Image : Graphic { }

    public class MaskableGraphic : Graphic { }

    public struct ColorBlock
    {
        public Color normalColor, highlightedColor, pressedColor, selectedColor, disabledColor;
        public float fadeDuration;
    }

    public class Selectable : Component
    {
        public enum Transition { None, ColorTint }
        public Graphic targetGraphic;
        public Transition transition = Transition.ColorTint;
        public ColorBlock colors;
    }

    public class Button : Selectable
    {
        public UnityEngine.Events.ButtonEvent onClick = new UnityEngine.Events.ButtonEvent();
    }

    public class Toggle : Selectable
    {

    }

    public class Slider : Selectable
    {
        public enum Direction { LeftToRight, RightToLeft }
        public RectTransform fillRect, handleRect;
        public Direction direction;
        public float minValue, maxValue, value;
        public bool wholeNumbers;
        public UnityEvent<float> onValueChanged = new UnityEvent<float>();

        /// <summary>轨道宽度。真实 Unity 不需要它，是给桩件定位手柄用的。</summary>
        internal float trackW;

        /// <summary>
        /// 真实 Unity 的 Slider 会把 handleRect 挪到 value 对应的位置。
        /// 桩件不实现的话，预览图里的手柄永远在轨道最左端，看着像控件坏了。
        /// </summary>
        public void SetValueWithoutNotify(float v)
        {
            value = v;
            if (handleRect == null || direction != Direction.LeftToRight)
            {
                return;
            }
            float t = maxValue - minValue < 0.0001f
                ? 0f
                : Mathf.Clamp01((v - minValue) / (maxValue - minValue));
            handleRect.anchoredPosition = new Vector2(
                handleRect.anchoredPosition.x + t * trackW,
                handleRect.anchoredPosition.y);
        }
    }

    public class Scrollbar : Selectable
    {        public enum Direction { LeftToRight, RightToLeft, BottomToTop, TopToBottom }        public RectTransform handleRect;        public Direction direction;        public float size, value;    }
    public class RectMask2D : Component
    {
    }

    public class ScrollRect : Component
    {
        public RectTransform content, viewport;
        public bool horizontal, vertical;
        public Scrollbar horizontalScrollbar, verticalScrollbar;        public enum ScrollbarVisibility { Permanent, AutoHide, AutoHideAndExpandViewport }        public ScrollbarVisibility horizontalScrollbarVisibility, verticalScrollbarVisibility;        public float horizontalScrollbarSpacing, verticalScrollbarSpacing;
        public MovementType movementType;
        public enum MovementType { Unrestricted, Elastic, Clamped }
        public float scrollSensitivity;

        /// <summary>
        /// 1 = 顶部，0 = 底部。真实 ScrollRect 用它把内容挪到对应位置。
        ///
        /// 存着不用它 = 验不出"切页没重置滚动位置"这类问题——那正是
        /// 用户实际撞上的那个：从渲染页滚到底再切到"关于"，整页内容
        /// 被推出视口，屏幕上只剩最后一条说明的下半截。
        /// </summary>
        public float verticalNormalizedPosition = 1f;

        public float horizontalNormalizedPosition = 1f;

        /// <summary>真实 ScrollRect 有这个方法：立刻停止惯性滑动。</summary>
        public void StopMovement() { }
    }

    public class ContentSizeFitter : Component
    {
        public enum FitMode { Unconstrained, MinSize, PreferredSize }
        public FitMode horizontalFit, verticalFit;
    }
}

namespace TMPro
{
    using UnityEngine;
    using UnityEngine.UI;

    public enum TextAlignmentOptions
    {
        TopLeft, Top, TopRight, MidlineLeft, Center, MidlineRight,
        BottomLeft, Bottom, BottomRight
    }

    public enum TextWrappingModes { NoWrap, Normal }
    public enum TextOverflowModes { Overflow, Ellipsis, Masking, ScrollRect, Page, Linked }

    public class TMP_Text : UnityEngine.UI.MaskableGraphic
    {
        public float fontSize = 14f;
        public Color color = Color.white;
        public bool raycastTarget;
        public string text = "";
        public TextAlignmentOptions alignment;
        public TextWrappingModes textWrappingMode;
        public TextOverflowModes overflowMode;
        public TMP_FontAsset font;
    }

    public class TMP_FontAsset : Object
    {
        /// <summary>回退链。真实 TMP 用它给缺字形的字符找替代字体。</summary>
        public List<TMP_FontAsset> fallbackFontAssetTable;
    }

    public static class TMP_Settings
    {
        public static TMP_FontAsset defaultFontAsset;
    }

    public class TextMeshProUGUI : TMP_Text { }

    public class TMP_Dropdown : Component
    {
        public Graphic targetGraphic;
        public TMP_Text itemText;
        public RectTransform template;
        public UnityEngine.Events.UnityEvent<int> onValueChanged
            = new UnityEngine.Events.UnityEvent<int>();

        /// <summary>选项文本。真实 Unity 的下拉靠它显示当前选中项。</summary>
        internal string[] labels;

        public void SetValueWithoutNotify(int v)
        {
            if (itemText == null)
            {
                return;
            }
            itemText.text = labels != null && v >= 0 && v < labels.Length
                ? labels[v]
                : string.Empty;
        }
    }

    public class TMP_InputField : Component
    {
        public enum LineType { SingleLine, MultiLine }
        public Graphic targetGraphic;
        public RectTransform textViewport;
        public TMP_Text textComponent, placeholder;
        public LineType lineType;
        public UnityEngine.Events.UnityEvent<string> onValueChanged
            = new UnityEngine.Events.UnityEvent<string>();
        public void SetTextWithoutNotify(string v) { }
    }
}
namespace UnityEngine.EventSystems
{
    public class EventSystem : MonoBehaviour
    {
        /// <summary>引擎维护的"当前事件系统"。真实 Unity 同一时刻只允许一个。</summary>
        public static EventSystem current;
    }

    public class StandaloneInputModule : MonoBehaviour { }
    public class BaseInputModule : MonoBehaviour { }
}
