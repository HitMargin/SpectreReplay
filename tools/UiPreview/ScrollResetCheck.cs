// 复现"从渲染页滚到底再切到关于页"这个场景。
//
// 用户实际撞到的：在渲染页把列表滚到底，切到"关于"，整页内容不见了，
// 屏幕上只剩最后一条说明的下半截，下面全是空白。
//
// 原因：ScrollRect 的滚动偏移是跟着对象走的。渲染页内容比视口高三四百
// 像素，滚到底时内容被抬了那么高；切到内容很短的"关于"页之后，那个
// 偏移还在——内容就被整个推出视口了。而每次切页面板高度还会变（按当前
// 页量出来的），偏移与视口不再匹配，MovementType.Clamped 也救不回来。
using System;
using System.Reflection;
using SpectreReplay.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace UiPreview
{
    internal static class ScrollResetCheck
    {
        internal static int Run()
        {
            Console.WriteLine("  ── 切页后滚动位置 ──");
            int fail = 0;

            var go = new GameObject("ScrollReset", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(1920f, 1080f);
            try
            {
                PanelHost.BuildInto(rt);
                PanelHost.Show(2);                 // 渲染页：内容最长

                var scroll = Scroll();
                if (scroll == null)
                {
                    Console.WriteLine("    ✗ 找不到 ScrollRect");
                    return 1;
                }

                // 模拟"在渲染页滚到底"
                scroll.verticalNormalizedPosition = 0f;
                float scrolled = scroll.verticalNormalizedPosition;

                // 切到"关于"页——内容短得多
                PanelHost.Show(3);
                float after = scroll.verticalNormalizedPosition;

                bool reset = after > 0.99f;
                Console.WriteLine($"    {(reset ? "✓" : "✗")} 切页后回到顶部"
                    + $"  → 滚到底时 {scrolled:F2}，切页后 {after:F2}");
                if (!reset)
                {
                    Console.WriteLine("    ✗ 偏移没重置：内容被整个推出视口，"
                        + "屏幕上只剩最后一条说明的下半截");
                    fail++;
                }

                // 再验一次内容高度确实变短了（否则上面那条可能没意义）
                var content = Content();
                float h = content != null ? content.sizeDelta.y : 0f;
                Console.WriteLine($"    ·  关于页内容高 {h:F0}px");

                fail += ClampHolds();                fail += BarSize();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
            return fail;
        }

        /// <summary>
        /// 每帧的不变量兜底：内容被推出去之后，ClampContent 必须把它拉回来。
        ///
        /// 这条**是真能在离屏验的**——ClampContent 只依赖视口高与内容高，
        /// 两个都是我们自己显式写进去的 sizeDelta，不牵扯 ScrollRect 的
        /// 内部时序。所以不像"切页后回到顶部"那条（桩件里那个 setter 什么
        /// 都不做，验的是空）。
        ///
        /// 直接把偏移设成一个离谱的值模拟故障，再看它有没有被夹回范围。
        /// </summary>
        private static int ClampHolds()
        {
            int bad = 0;
            var content = Content();
            var viewport = Viewport();
            if (content == null || viewport == null)
            {
                Console.WriteLine("    ✗ 找不到内容或视口");
                return 1;
            }

            // ① 内容比视口高：能滚，但不能被推出两端之外
            PanelHost.Show(2);                     // 渲染页，内容最长
            content.anchoredPosition = new Vector2(0f, -99999f);
            PanelHost.ClampContent();
            float viewH = viewport.rect.height;
            float contentH = content.rect.height;
            float minY = -MathF.Max(0f, contentH - viewH);
            float y = content.anchoredPosition.y;
            bool okA = y >= minY - 0.5f && y <= 0.5f;
            Console.WriteLine($"    {(okA ? "✓" : "✗")} 内容被推出后夹回范围"
                + $"  → {y:F0} ∈ [{minY:F0}, 0]（视口 {viewH:F0} 内容 {contentH:F0}）");
            if (!okA) bad++;

            // ② 往下也推：内容顶边不能被抬到视口顶边之上
            content.anchoredPosition = new Vector2(0f, 99999f);
            PanelHost.ClampContent();
            y = content.anchoredPosition.y;
            bool okB = y <= 0.5f;
            Console.WriteLine($"    {(okB ? "✓" : "✗")} 内容顶边不被抬到视口之上  → {y:F0}");
            if (!okB) bad++;

            // ③ 内容比视口短：必须钉在顶部
            PanelHost.Show(3);                     // 关于页，内容最短
            content.anchoredPosition = new Vector2(0f, -500f);
            PanelHost.ClampContent();
            y = content.anchoredPosition.y;
            bool okC = MathF.Abs(y) < 0.5f;
            Console.WriteLine($"    {(okC ? "✓" : "✗")} 内容比视口短时钉在顶部  → {y:F0}");
            if (!okC) bad++;

            // ④ **偏移本来就合法时，不该报警、更不该改写。**
            //    原来范围判断写反了（y <= minY && y >= maxY），一个完全正常
            //    的偏移 0 也被判成越界。真机日志里"内容跑出视口（偏移 0），
            //    已夹回 0"就是这么来的——一眼看着像废话，细想才知道是个 bug。
            PanelHost.Show(2);
            content.anchoredPosition = new Vector2(0f, 0f);     // 合法
            var warned = new System.Collections.Generic.List<string>();
            var real = UnityEngine.Debug.LogWarning;
            UnityEngine.Debug.LogWarning = o => { warned.Add(o?.ToString() ?? ""); };
            try
            {
                PanelHost.ClampContent();
            }
            finally
            {
                UnityEngine.Debug.LogWarning = real;
            }
            bool okD = warned.Count == 0;
            Console.WriteLine($"    {(okD ? "✓" : "✗")} 偏移合法时不报警"
                + $"  → {warned.Count} 条");
            if (!okD)
            {
                foreach (var w in warned) Console.WriteLine("        " + w);
                bad++;
            }

            PanelHost.Show(0);
            return bad;
        }

        /// <summary>
        /// 滑块长度必须反映内容与视口的比例。
        ///
        /// **ScrollRect 只驱动 value（滑块位置），从不设置 size（滑块长度）**，
        /// 而 size 默认是 1 —— 不自己算的话滑块永远是满格，看上去"能拖到底"，
        /// 其实底下没什么可看的。这条在离屏验得了：size 就是个普通字段。
        /// </summary>
        private static int BarSize()
        {
            int bad = 0;
            var content = Content();
            var viewport = Viewport();
            var bar = Bar();
            if (bar == null)
            {
                Console.WriteLine("    ✗ 找不到滚动条");
                return 1;
            }

            // 内容比视口高 → 滑块必须短于满格
            PanelHost.Show(2);
            float want = viewport.rect.height / content.rect.height;
            bool ok = bar.size > 0.05f && bar.size < 0.99f
                && MathF.Abs(bar.size - want) < 0.02f;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} 内容比视口高时滑块变短"
                + $"  → size={bar.size:F2}（应为 {want:F2}）");
            if (!ok) bad++;

            // 内容装得下 → 满格
            PanelHost.Show(3);
            bool ok2 = MathF.Abs(bar.size - 1f) < 0.01f;
            Console.WriteLine($"    {(ok2 ? "✓" : "✗")} 内容装得下时滑块满格  → size={bar.size:F2}");
            if (!ok2) bad++;

            PanelHost.Show(0);
            return bad;
        }

        private static UnityEngine.UI.Scrollbar Bar()
        {
            var f = typeof(PanelHost).GetField("_bar",
                BindingFlags.NonPublic | BindingFlags.Static);
            return f?.GetValue(null) as UnityEngine.UI.Scrollbar;
        }

        private static ScrollRect Scroll()        {
            var f = typeof(PanelHost).GetField("_scroll",
                BindingFlags.NonPublic | BindingFlags.Static);
            return f?.GetValue(null) as ScrollRect;
        }

        private static RectTransform Viewport()
        {
            var f = typeof(PanelHost).GetField("_viewport",
                BindingFlags.NonPublic | BindingFlags.Static);
            return f?.GetValue(null) as RectTransform;
        }

        private static RectTransform Content()
        {
            var f = typeof(PanelHost).GetField("_content",
                BindingFlags.NonPublic | BindingFlags.Static);
            return f?.GetValue(null) as RectTransform;
        }
    }
}
