// 离线预览：不启动游戏也能看到 UI 长什么样、并检查文字溢出。
//
// 它调用的是**真实的** PanelHost.BuildInto / Show / PageBuilder / Gfx，
// 只把 UnityEngine / TMPro / UnityEngine.UI 换成 Stubs.cs 里的桩件。
// 桩件只实现真实代码用到的那部分 API —— 桩件一旦比真实 Unity 更宽松，
// 这个验证就没有意义了（TMP_Text 的 transform、Mathf 的 int 重载
// 都是这样被补齐的）。
//
// 为什么值得存在：这套布局错过的每一次都是离屏就能查出来的毛病
// （子件被算成 0 高、控件铺满整行、标签框宽为负、页面区跑到面板外）。
// 自己看图比让人反复开游戏按 F9 靠谱得多。
using System;
using System.Drawing;
using System.IO;
using SpectreReplay.Ui;
using UnityEngine;

namespace UiPreview
{
    internal static class Driver
    {
        // 预览用 1920x1080 画布 —— 与 CanvasScaler 的参考分辨率一致
        private const float CanvasW = 1920f, CanvasH = 1080f;

        internal static int Run(string outDir, string[] args)
        {
            Directory.CreateDirectory(outDir);

            var pages = new (string Key, Action<PageBuilder> Build)[]
            {
                ("record", Pages.BuildRecord),
                ("playback", Pages.BuildPlayback),
                ("render", Pages.BuildRender),
                ("about", Pages.BuildAbout),
            };
            foreach (var p in pages)
            {
                PanelHost.Page(p.Key, p.Key, "page." + p.Key, p.Build);
            }

            int totalOverflow = 0;
            for (int i = 0; i < pages.Length; i++)
            {
                var key = pages[i].Key;

                // 虚拟屏幕：锚点全零、轴心全零、偏移为零 —— 就是坐标原点
                var screenGo = new GameObject("Screen", typeof(RectTransform));
                var sRt = (RectTransform)screenGo.transform;
                sRt.anchorMin = Vector2.zero;
                sRt.anchorMax = Vector2.zero;
                sRt.pivot = Vector2.zero;
                sRt.anchoredPosition = Vector2.zero;
                sRt.sizeDelta = new Vector2(CanvasW, CanvasH);

                PanelHost.BuildInto(sRt);
                PanelHost.Show(i);          // ← 真实代码：建页面内容

                // 根节点的 Unity 坐标就是 (0,0)-(CanvasW,CanvasH)
                var screen = Node.Walk(screenGo.transform,
                    new Node
                    {
                        X = 0f, Y = 0f, W = CanvasW, H = CanvasH,
                        Px = 0f, Py = 0f,
                    });

                var r = new Renderer((int)CanvasW, (int)CanvasH);
                if (i == 0)
                {
                    Console.WriteLine("  自检: " + r.SelfTest());
                }
                r.Draw(screen);
                Console.WriteLine($"    DrawText 调用 {r.DrawTextCalls} 次");
                string path = Path.Combine(outDir, key + ".png");
                r.Save(path);
                Console.WriteLine($"  {key,-9} → {path}");
                if (args.Length > 2 && args[1] == "--dump"
                    && i == int.Parse(args[2]))
                {
                    Dump(screen, 0);
                }
                foreach (var o in r.Overflows)
                {
                    Console.WriteLine("      文字溢出 " + o);
                }
                totalOverflow += r.Overflows.Count;
            }

            Console.WriteLine();
            Console.WriteLine(totalOverflow == 0
                ? "  ✓ 无文字溢出"
                : $"  ✗ {totalOverflow} 处文字溢出");
            return totalOverflow == 0 ? 0 : 1;
        }

        /// <summary>打印整棵树的实际矩形。眼睛猜布局会骗人，数字不会。</summary>
        private static void Dump(Node n, int depth)
        {
            string txt = n.Text == null ? "" : $"  «{n.Text}»";
            Console.WriteLine("    " + new string(' ', depth * 2)
                + $"{n.Go.name,-12} x={n.X,7:F0} y={n.Y,7:F0} w={n.W,6:F0} h={n.H,5:F0}"
                + (n.Raycast ? " [ray]" : "") + txt);
            foreach (var k in n.Kids)
            {
                Dump(k, depth + 1);
            }
        }

        internal static void Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0]
                : Path.Combine(Path.GetTempPath(), "uipreview");
            Environment.ExitCode = Run(outDir, args);
        }
    }
}