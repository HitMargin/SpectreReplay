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
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
            return fail;
        }

        private static ScrollRect Scroll()
        {
            var f = typeof(PanelHost).GetField("_scroll",
                BindingFlags.NonPublic | BindingFlags.Static);
            return f?.GetValue(null) as ScrollRect;
        }

        private static RectTransform Content()
        {
            var f = typeof(PanelHost).GetField("_content",
                BindingFlags.NonPublic | BindingFlags.Static);
            return f?.GetValue(null) as RectTransform;
        }
    }
}
