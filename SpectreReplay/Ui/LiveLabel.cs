using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 会随时间变化的标签。渲染进度这类值每帧都变，页面重建时挂上这个组件，
    /// 由 <see cref="Tick"/> 组件统一驱动刷新——不做"每帧遍历所有标签找对象"，
    /// 而是让每个标签自己注册到列表里，Tick 只遍历这一个列表。
    /// </summary>
    internal sealed class LiveLabel : MonoBehaviour
    {
        private TMP_Text _text;
        private System.Func<string> _get;
        private float _interval = 0.25f;
        private float _next;

        /// <summary>把一段会变的文字挂成**行内的标签**（右侧），与其它控件同一套模型。</summary>
        internal static LiveLabel Create(RowRef row, System.Func<string> get,
            float interval = 0.25f, float width = 420f)
        {
            var slot = Row.Slot(row, width, 30f);
            var t = Row.NewText(slot, "Live", UiSkin.FontSmall, UiSkin.TextNormal);
            Row.Stretch(t.rectTransform);
            return Attach(t, get, interval, TextAlignmentOptions.MidlineRight);
        }

        /// <summary>占整行的会变文字（不带左侧标签）。</summary>
        internal static LiveLabel CreateFullRow(Transform parent, System.Func<string> get,
            float interval = 0.25f)
        {
            var row = Row.Create(parent, 40f, new Color(0f, 0f, 0f, 0f));
            var t = Row.NewText(row.Rect, "Live", UiSkin.FontSmall, UiSkin.TextMuted);
            var rt = t.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(UiSkin.PadX, 0f);
            rt.offsetMax = new Vector2(-UiSkin.PadX, 0f);
            return Attach(t, get, interval, TextAlignmentOptions.MidlineLeft, row.Rect);
        }

        private static LiveLabel Attach(TMP_Text t, System.Func<string> get, float interval,
            TextAlignmentOptions align, Transform host = null)
        {
            t.alignment = align;
            t.text = "";
            Transform owner = host != null ? host : t.transform;
            var ll = owner.gameObject.AddComponent<LiveLabel>();
            ll._text = t;
            ll._get = get;
            ll._interval = interval;
            return ll;
        }

        private void OnEnable() => LiveLabelHub.Add(this);
        private void OnDisable() => LiveLabelHub.Remove(this);

        internal void Refresh()
        {
            if (_text == null || _get == null)
            {
                return;
            }
            _text.text = _get();
        }

        internal bool Tick(float now)
        {
            if (now < _next)
            {
                return false;
            }
            _next = now + _interval;
            Refresh();
            return true;
        }
    }

    /// <summary>活动标签登记表。没有它就只能每帧 GetComponent 全树扫描。</summary>
    internal static class LiveLabelHub
    {
        private static readonly System.Collections.Generic.List<LiveLabel> Items =
            new System.Collections.Generic.List<LiveLabel>();

        internal static void Add(LiveLabel l)
        {
            if (l != null && !Items.Contains(l))
            {
                Items.Add(l);
            }
        }

        internal static void Remove(LiveLabel l) => Items.Remove(l);

        internal static void Clear() => Items.Clear();

        internal static void TickAll(float now)
        {
            // 倒序遍历：控件可能在 get() 里触发页面重建，从而在本轮把自己移除
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (Items[i] == null)
                {
                    Items.RemoveAt(i);
                    continue;
                }
                Items[i].Tick(now);
            }
        }
    }
}
