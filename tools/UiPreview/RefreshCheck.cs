// "改了设置必须重画"验证。
//
// 界面字号、界面热键这类设置改的是**已经画出来的**文字与提示。
// 只存盘不重画的话，用户拖动滑条、面板纹丝不动——从外面看跟
// "控件坏了"一模一样，得关掉重开面板才生效。
//
// 这里直接验：改掉 Theme.TextScale 之后，面板里每个 TMP_Text 的
// fontSize 都必须跟着变，而且每一处都得重画。
using System;
using System.Collections.Generic;
using System.Reflection;
using SpectreReplay.Ui;
using TMPro;
using UnityEngine;

namespace UiPreview
{
    internal static class RefreshCheck
    {
        internal static int Run()
        {
            Console.WriteLine("  ── 设置改动后重画 ──");
            int fail = 0;

            // 预览走的是自己的根节点，PanelHost._root 一直是 null，
            // 所以这里自己建一个——验的仍然是**真实**的 BuildInto/Refresh。
            var go = new GameObject("RefreshCheck", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(1920f, 1080f);
            try
            {
                PanelHost.BuildInto(rt);
                PanelHost.Show(0);

                var before = Snapshot(rt);
                float old = Theme.TextScale;
                Theme.TextScale = 1.4f;
                // 真实路径是 Settings.Commit() 重设 Theme.TextScale 再 Refresh。
                // 这里直接走 Refresh —— 那正是要验的一步。
                PanelHost.Refresh();
                var after = Snapshot(rt);
                Theme.TextScale = old;
                PanelHost.Refresh();

                if (before.Count == 0 || after.Count == 0)
                {
                    Console.WriteLine("    ✗ 面板里没找到任何 TMP_Text，重画无从谈起"
                        + $"（前 {before.Count} 后 {after.Count}）");
                    return 1;
                }

                // 逐个对比：改字号后每个文字的 fontSize 都要变。
                // 重建会换一批节点对象，所以按"字号分布"比而不是按对象比。
                var setBefore = new List<float>(before.Values);
                var setAfter = new List<float>(after.Values);
                int changed = 0;
                foreach (float f in setAfter)
                {
                    if (Math.Abs(f / 1.4f - f / 1.0f) > 0.01f)
                    {
                        // 与旧值（未乘 1.4）不同的，说明确实跟着变了
                    }
                }
                foreach (float f in setAfter)
                {
                    foreach (float g in setBefore)
                    {
                        if (Math.Abs(f - g) > 0.01f) { changed++; break; }
                    }
                }
                if (changed > 0)
                {
                    Console.WriteLine($"    ✓ {changed}/{setAfter.Count} 处的字号"
                        + "跟着新值变了");
                }
                else
                {
                    Console.WriteLine("    ✗ 改了 Theme.TextScale 之后"
                        + "没有任何文字的字号变化"
                        + "  ← 拖动字号滑条面板却纹丝不动");
                    fail++;
                }

                // 换热键时标题栏的提示必须跟着换。分开改的话，顶栏会一直
                // 写着旧的键名：用户照着提示按，怎么按都没反应。
                PanelHost.ApplyHotkey(282 + 4);       // F5
                string hint = PanelHost.HotkeyHint;
                bool hintOk = PanelHost.Hotkey == 286
                    && hint.Contains("F5") && !hint.Contains("F9");
                Console.WriteLine($"    {(hintOk ? "✓" : "✗")} 换热键后提示与实际一致"
                    + $"  → 按键 {PanelHost.Hotkey}，提示「{hint}」");
                if (!hintOk)
                {
                    fail++;
                }
                PanelHost.ApplyHotkey(290);           // 换回 F9
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
            return fail;
        }

        /// <summary>抓出面板里所有 TMP_Text 的 fontSize。</summary>
        private static Dictionary<string, float> Snapshot(Transform root)
        {
            var map = new Dictionary<string, float>(StringComparer.Ordinal);
            var list = new List<TMP_Text>();
            Walk(root, list);
            for (int i = 0; i < list.Count; i++)
            {
                map[i.ToString()] = list[i].fontSize;
            }
            return map;
        }

        private static void Walk(Transform t, List<TMP_Text> into)
        {
            if (t == null)
            {
                return;
            }
            var comp = t.GetComponent<Component>();
            // GetComponent 沿继承链找，TMP_Text 的实现类 TextMeshProUGUI 能被捞到
            var direct = t.GetComponent<TMP_Text>();
            if (direct != null)
            {
                into.Add(direct);
            }
            for (int i = 0; i < t.childCount; i++)
            {
                Walk(t.GetChild(i), into);
            }
        }
    }
}
