using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 自绘的屏幕提示卡。
    ///
    /// <para>双模式：简洁（胶囊 + 圆点 + 一行字）/ 详细（卡底 + 左色条 +
    /// 标题/详情 + 底部剩余时间条）。六个位置：右上（默认）/ 顶部中央 /
    /// 底部中央 / 左上 / 右下 / 左下，从各自方向滑入。同因 1.5s 内合并 ×N，
    /// 同屏最多 3 张。</para>
    ///
    /// <para>动画：ein = EaseOut(age/0.28)，out = (Life-age)/0.5，
    /// ga（全局 alpha）在 Repaint 趟乘 ein*outT；卡片间 y 用 Approach 指数
    /// 逼近而不是 MoveTowards 线性步进。**推进只在每帧首趟做**
    /// （Time.frameCount 去重），否则 IMGUI 的多趟事件把动画吃成瞬移。</para>
    /// </summary>
    internal static class Toast
    {
        private const float Life = 4f;
        private const float TW = 272f, TH = 58f, TWShort = 236f, THShort = 32f;
        private const float Gap = 8f, GapShort = 6f;
        private const int MaxCards = 3;

        /// <summary>
        /// 显示位置：0=右上 1=顶部中央 2=底部中央 3=左上 4=右下 5=左下。
        /// 索引追加而不是重排，保证旧 Configs.json 的选择不会错位。
        /// </summary>
        internal static int Pos = 0;

        internal sealed class Rec
        {
            internal string Title, Detail, Short;
            internal bool Warning;
            internal float Born, Life;
            internal int Count = 1;
        }

        private static readonly List<Rec> Cards = new List<Rec>();
        private static readonly Dictionary<Rec, float> Y = new Dictionary<Rec, float>();
        private static Host _host;
        private static GUIStyle _title, _detail, _short;
        private static Texture2D _white;
        private static int _stepFrame = -1;
        private static float _ga = 1f;

        internal static void Show(string text, float seconds, bool warning)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!MainThread.IsMainThread)
            {
                MainThread.Run(() => Show(text, seconds, warning));
                return;
            }
            string trimmed = text.TrimEnd('\n');
            int nl = trimmed.IndexOf('\n');
            string title = nl < 0 ? trimmed : trimmed.Substring(0, nl);
            string detail = nl < 0 ? "" : trimmed.Substring(nl + 1).TrimStart('\n');
            float life = Mathf.Max(2f, seconds > 0f ? seconds : Life);
            float now = Time.unscaledTime;
            for (int i = 0; i < Cards.Count; i++)
            {
                var c = Cards[i];
                if (c.Title == title && c.Warning == warning && now - c.Born < 1.5f)
                {
                    c.Count++;
                    c.Born = now;
                    c.Life = life;
                    if (!string.IsNullOrEmpty(detail)) c.Detail = detail;
                    c.Short = ShortOf(c);
                    return;
                }
            }
            var rec = new Rec
            {
                Title = title, Detail = detail, Warning = warning,
                Born = now, Life = life,
                Short = nl < 0 ? title : title + " — " + detail,
            };
            Cards.Insert(0, rec);
            while (Cards.Count > MaxCards)
            {
                Y.Remove(Cards[Cards.Count - 1]);
                Cards.RemoveAt(Cards.Count - 1);
            }
            EnsureHost();
        }

        private static string ShortOf(Rec c)
            => string.IsNullOrEmpty(c.Detail) ? c.Title : c.Title + " — " + c.Detail;

        private static void EnsureHost()
        {
            if (_host != null) return;
            var go = new GameObject("SpectreToastHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _host = go.AddComponent<Host>();
        }

        private sealed class Host : MonoBehaviour
        {
            private void OnGUI()
            {
                if (Cards.Count == 0) return;
                float dt = Time.unscaledDeltaTime;
                float now = Time.unscaledTime;
                bool step = Time.frameCount != _stepFrame;
                if (step) _stepFrame = Time.frameCount;

                // **到期必须真的从列表里删掉。** 少了这段，卡片会一直留在
                // Cards 里：alpha 归零后每帧还在画（不可见但仍占位、还会挡住
                // 后续卡的堆叠位置），用户看到的就是"框永远不消失"。
                if (step)
                {
                    for (int i = Cards.Count - 1; i >= 0; i--)
                    {
                        if (now - Cards[i].Born > Cards[i].Life + 0.5f)
                        {
                            Y.Remove(Cards[i]);
                            Cards.RemoveAt(i);
                        }
                    }
                }
                if (Cards.Count == 0) return;

                EnsureStyles();
                float scale = Mathf.Clamp(Screen.height / 1080f * 1.1f, 0.78f, 1.15f);
                float sw = Screen.width / scale, sh = Screen.height / scale;
                bool detailed = true;
                // 卡片高度不再写死 58：标题/详情行高按字体实测算，中文、
                // 日文、韩文的行高都比拉丁文高一截，写死就会把下半截裁掉
                // （用户截图"回放已装载"只剩半行就是这个）。每张卡按自己
                // 的内容算高度，堆叠时用各自高度往下排。
                float tw = detailed ? TW : TWShort, th = detailed ? TH : THShort;
                float gap = detailed ? Gap : GapShort;

                float x, ty, dir = 1f;
                Vector2 enter;
                switch (Pos)
                {
                    case 1: x = sw / 2f - tw / 2f; ty = 18f; enter = new Vector2(0f, -18f); break;
                    case 2: x = sw / 2f - tw / 2f; ty = sh - 18f - th; dir = -1f; enter = new Vector2(0f, 18f); break;
                    case 3: x = 20f; ty = 18f; enter = new Vector2(-24f, 0f); break;
                    case 4: x = sw - tw - 20f; ty = sh - 18f - th; dir = -1f; enter = new Vector2(24f, 0f); break;
                    case 5: x = 20f; ty = sh - 18f - th; dir = -1f; enter = new Vector2(-24f, 0f); break;
                    default: x = sw - tw - 20f; ty = 18f; enter = new Vector2(24f, 0f); break;
                }

                Matrix4x4 oldM = GUI.matrix;
                GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
                try
                {
                    for (int i = 0; i < Cards.Count; i++)
                    {
                        var t = Cards[i];
                        float y;
                        if (!Y.TryGetValue(t, out y)) y = ty;
                        if (step) { y = Approach(y, ty, 14f); Y[t] = y; }

                        float age = now - t.Born;
                        float ein = EaseOut(Mathf.Clamp01(age / 0.28f));
                        float outT = Mathf.Clamp01((t.Life - age) / 0.5f);
                        string titleText = t.Count > 1 ? t.Title + "  ×" + t.Count : t.Title;
                        float titleH = Mathf.Max(18f,
                            _title.CalcSize(new GUIContent(titleText)).y);
                        float detailH = string.IsNullOrEmpty(t.Detail) ? 0f
                            : Mathf.Max(16f,
                                _detail.CalcSize(new GUIContent(t.Detail)).y);
                        float cardH = detailed
                            ? 10f + titleH + (detailH > 0f ? 4f + detailH : 0f) + 12f
                            : th;
                        // 进场沿 enter 方向滑入；离场**沿同一方向反向收回**
                        // （从哪来回哪去）。
                        float slide = (1f - ein) + (1f - outT);
                        var r = new Rect(x + enter.x * slide,
                            y + enter.y * slide, tw, cardH);

                        float old = _ga;
                        _ga = old * ein * outT;
                        Panel(r, detailed ? 12f : th / 2f);
                        if (detailed)
                        {
                            Fill(new Rect(r.x + 10f, r.y + 12f, 3f, r.height - 24f),
                                t.Warning ? WarnCol : OkCol, 1.5f);
                            // 行高用实测值，并且留 1px 余量：CalcSize 有时
                            // 正好等于字形下沿，浮点舍入会把最后一像素裁掉。
                            Label(new Rect(r.x + 22f, r.y + 9f,
                                r.width - 32f, titleH + 1f), titleText, _title);
                            if (detailH > 0f)
                            {
                                Label(new Rect(r.x + 22f, r.y + 9f + titleH + 4f,
                                    r.width - 32f, detailH + 1f), t.Detail, _detail);
                            }
                            float left = Mathf.Clamp01(1f - age / t.Life);
                            Fill(new Rect(r.x + 22f, r.yMax - 5f, (r.width - 44f) * left, 2f),
                                new Color((t.Warning ? WarnCol : OkCol).r,
                                    (t.Warning ? WarnCol : OkCol).g,
                                    (t.Warning ? WarnCol : OkCol).b, 0.5f), 1f);
                        }
                        else
                        {
                            Fill(new Rect(r.x + 13f, r.center.y - 3f, 6f, 6f),
                                t.Warning ? WarnCol : OkCol, 3f);
                            Label(new Rect(r.x + 26f, r.y, r.width - 34f, r.height),
                                t.Count > 1 ? t.Short + " ×" + t.Count : t.Short, _short);
                        }
                        _ga = old;
                        ty += dir * (cardH + gap);
                    }
                }
                finally
                {
                    GUI.matrix = oldM;
                    _ga = 1f;
                }
            }
        }

        private static readonly Color OkCol = new Color(0.20f, 0.72f, 0.39f, 1f);
        private static readonly Color WarnCol = new Color(1f, 0.28f, 0.28f, 1f);
        private static readonly Color BaseCol = new Color(0.06f, 0.065f, 0.08f, 0.96f);

        private static void Panel(Rect r, float radius)
        {
            Fill(r, BaseCol, radius);
            Border(r, new Color(1f, 1f, 1f, 0.09f), radius);
        }

        private static void Fill(Rect r, Color c, float radius)
        {
            c.a *= _ga;
            // **必须走 GUIStyle.border 九宫格，不能用 DrawTexture 的 border。**
            // DrawTexture 在这里把 32px 圆角贴图整张拉成卡片大小：四角被拉成
            // 椭圆、中段被挖空，正是用户截图里那个"空心大黑椭圆"。
            // GUI.Box + GUIStyle(border) 是本项目按钮/卡片的既定路径
            // （见 Gui.MakeButton / _pad）：四角原像素、只拉伸中段。
            GUI.color = c;
            GUI.Box(Gui.Snap(r), GUIContent.none, PanelStyle(Mathf.RoundToInt(radius)));
            GUI.color = Color.white;
        }

        private static readonly Dictionary<int, GUIStyle> PanelStyles =
            new Dictionary<int, GUIStyle>();

        /// <summary>按半径缓存的九宫格卡底样式（白色底图靠 GUI.color 染色）。</summary>
        private static GUIStyle PanelStyle(int radius)
        {
            radius = Mathf.Clamp(radius, 2, 12);
            GUIStyle st;
            if (PanelStyles.TryGetValue(radius, out st) && st != null) return st;
            st = new GUIStyle
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(radius + 1, radius + 1,
                    radius + 1, radius + 1),
            };
            // 48x48、半径 <= 12 = 真正的圆角矩形。之前用 32x32 + 半径 12，
            // 圆角区域占了整个贴图，形状接近圆——拉成宽卡就成了"椭圆"。
            var tex = RoundedTex.Get(48, 48, radius, Color.white);
            // **八个状态都要设。** GUI.Box 取的是 onNormal，只设 normal 的话
            // 底图根本不会画——卡"中间是空的"就是这么来的。
            st.normal.background = tex;
            st.hover.background = tex;
            st.active.background = tex;
            st.focused.background = tex;
            st.onNormal.background = tex;
            st.onHover.background = tex;
            st.onActive.background = tex;
            st.onFocused.background = tex;
            PanelStyles[radius] = st;
            return st;
        }

        private static void Border(Rect r, Color c, float radius)
        {
            c.a *= _ga;
            const int b = 1;
            // 描边走 GUI.color + 白贴图。**不要用 DrawTexture 的 color 参数**：
            // 那个参数会覆盖 GUI.color，与上面的 alpha 计算打架。
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, b), _white);
            GUI.DrawTexture(new Rect(r.x, r.yMax - b, r.width, b), _white);
            GUI.DrawTexture(new Rect(r.x, r.y, b, r.height), _white);
            GUI.DrawTexture(new Rect(r.xMax - b, r.y, b, r.height), _white);
            GUI.color = old;
        }

        private static void Label(Rect r, string s, GUIStyle st)
        {
            var c = st.normal.textColor;
            c.a *= _ga;
            // GUI.Label 的最终色 = style.normal.textColor × GUI.color，
            // 所以这里用 GUI.color 叠 alpha 是对的（Label 没有 color 参数）。
            Color old = GUI.color;
            GUI.color = new Color(c.r, c.g, c.b, c.a);
            GUI.Label(r, s, st);
            GUI.color = old;
        }

        private static void EnsureStyles()
        {
            if (_title != null) return;
            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft, clipping = TextClipping.Clip,
            };
            _title.normal.textColor = new Color(0.95f, 0.96f, 0.98f, 1f);
            _detail = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11, alignment = TextAnchor.UpperLeft,
                clipping = TextClipping.Clip, wordWrap = true,
            };
            _detail.normal.textColor = new Color(0.72f, 0.75f, 0.80f, 1f);
            _short = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip,
            };
            _short.normal.textColor = new Color(0.95f, 0.96f, 0.98f, 1f);
            _white = Texture2D.whiteTexture;
        }

        private static float EaseOut(float t)
        {
            t = 1f - Mathf.Clamp01(t);
            return 1f - t * t * t;
        }

        private static float Approach(float cur, float target, float speed)
            => cur + (target - cur) * (1f - Mathf.Exp(-speed * Time.unscaledDeltaTime));
    }
}
