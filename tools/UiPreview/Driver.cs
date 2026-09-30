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
using System.Collections.Generic;
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
            // 字体回退链先验：它决定界面上有没有字，而字形本身离屏验不了
            int fontFail = FontCheck.Run();
            // 文案完整性：漏一个键，界面上就是空白或原始键名
            int locFail = LocCheck.Run(LocCheck.CollectUsedKeys());
            // 事件系统唯一性：两个并存就是"有时候点得动"
            int evFail = EventCheck.Run();            // 孤儿组件：定义了却从没被 AddComponent 的，永远是 bug            int orphanFail = OrphanCheck.Run(                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));            int cfgFail = ConfigCheck.Run(                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));            PanelHost.Close();

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
            // 中英文都要渲染：**中文字宽和英文不一样**，只验一种等于没验。
            // 玩家把游戏设成中文时看到的就是中文界面。
            foreach (var zh in new[] { true, false })
            {
                Loc.UseChinese = zh;
                string suffix = zh ? ".zh" : ".en";
                Console.WriteLine(zh ? "  ── 中文 ──" : "  ── 英文 ──");
                for (int i = 0; i < pages.Length; i++)
                {
                var key = pages[i].Key;
                // 回放页有两种状态（有回放/无回放），两种都画。
                // 空列表那条分支的行数最少，恰恰是最容易在某次改动里塌掉的。
                Pages.ReplayCount = key == "playback"
                    ? (zh ? 2 : 0)
                    : 2;

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
                if (i == 0 && zh)
                {
                    Console.WriteLine("  自检: " + r.SelfTest());
                }
                r.Draw(screen);
                string path = Path.Combine(outDir, key + suffix + ".png");
                r.Save(path);
                Console.WriteLine($"    {key,-9} → {Path.GetFileName(path)}");
                // 先记住根节点：HitTest 要从根开始走
                _screen = screen;
                if (args.Length > 2 && args[1] == "--dump"
                    && i == int.Parse(args[2]))
                {
                    Dump(screen, 0);
                }
                // 点击测试与语言无关（几何相同），只跑一遍就够
                if (args.Length > 1 && args[1] == "--click" && zh)
                {
                    RunClickTests(key, screen);
                }
                foreach (var o in r.Overflows)
                {
                    Console.WriteLine("      文字溢出 " + o);
                }
                totalOverflow += r.Overflows.Count;
                }
            }
            Loc.UseChinese = true;

            Console.WriteLine();
            Console.WriteLine(totalOverflow == 0
                ? "  ✓ 无文字溢出"
                : $"  ✗ {totalOverflow} 处文字溢出");
            if (ClickFailures > 0)
            {
                Console.WriteLine($"  ✗ {ClickFailures} 处点击点不中"
                    + "（点得动的控件被别的东西盖住了）");
            }
            if (fontFail > 0)
            {
                Console.WriteLine($"  ✗ 字体回退链有 {fontFail} 处不对");
            }
            if (cfgFail > 0)
            {
                Console.WriteLine($"  ✗ 配置往返有 {cfgFail} 处对不上");
            }
            if (orphanFail > 0)
            {
                Console.WriteLine($"  ✗ {orphanFail} 个组件从没被挂上去");
            }
            if (evFail > 0)
            {
                Console.WriteLine($"  ✗ EventSystem 有 {evFail} 处不对");
            }
            if (locFail > 0)
            {
                Console.WriteLine($"  ✗ 文案有 {locFail} 处缺漏");
            }
            return totalOverflow == 0 && ClickFailures == 0
                && fontFail == 0 && locFail == 0 ? 0 : 1;
        }

        private static int ClickFailures;

        /// <summary>
        /// 代表性点击：每个都点在**应该能点的控件**上，检查命中的是不是它自己。
        /// 用户报过"设置菜单点不动"——光看 raycastTarget 标志证明不了这点。
        /// </summary>
        private static void RunClickTests(string page, Node screen)
        {
            Console.WriteLine($"    ── {page} 点击命中 ──");
            // 名称 → 期望命中路径的末段
            var expects = new (string Target, string MustEndWith)[]
            {
                ("Nav0", "Nav0"),      // 侧栏第一项
                ("Nav1", "Nav1"),
                ("Nav3", "Nav3"),
            };
            foreach (var (target, suffix) in expects)
            {
                var n = Find(screen, target);
                if (n == null)
                {
                    Console.WriteLine($"      ?  找不到 {target}");
                    continue;
                }
                Report(page, target, n);
            }
            // 所有开关、按钮、下拉、滑条本体
            foreach (var kind in new[] { "Toggle", "Button", "DropDown", "Slider", "Input" })
            {
                foreach (var n in FindAll(screen, kind))
                {
                    Report(page, kind, n);
                }
            }
            NegativeControls(page, screen);
        }

        /// <summary>
        /// 反向对照。只做正向断言是不够的：万一命中测试无论点什么都返回同一个
        /// 节点，全绿也是假的。所以这里点**不该命中任何控件**的地方，
        /// 确认命中的确实是容器而不是按钮。
        /// </summary>
        private static void NegativeControls(string page, Node screen)
        {
            var viewport = Find(screen, "Viewport");
            var panel = Find(screen, "Panel");
            if (viewport == null || panel == null)
            {
                return;
            }
            // 视口底部那片空白：内容不满一屏时那里什么都没有
            float x = viewport.X + viewport.W * 0.5f;
            float y = viewport.Y + viewport.H - 30f;
            Check(page, "空白处", x, y, "Viewport");

            // 侧栏底部空白：应命中侧栏本身
            var side = Find(screen, "Sidebar");
            if (side != null)
            {
                Check(page, "侧栏空白", side.X + side.W * 0.5f, side.Y + side.H - 30f,
                    "Sidebar");
            }

            // 页面标题栏：Header 故意 raycastTarget=false，所以命中会落到 PageArea。
            // 该保证的不是"命中 Header"，而是"**不会激活任何控件**"。
            var header = Find(screen, "Header");
            if (header != null)
            {
                CheckNoControl(page, "标题栏",
                    header.X + header.W * 0.5f, header.Y + header.H * 0.5f);
            }
        }

        private static readonly HashSet<string> ControlNames = new HashSet<string>
        {
            "Nav0", "Nav1", "Nav2", "Nav3", "Toggle", "Button", "DropDown",
            "Slider", "Input", "DropList", "Overlay",
        };

        /// <summary>点这��不该激活任何控件。</summary>
        private static void CheckNoControl(string page, string what, float x, float y)
        {
            var hit = Node.HitTest(_screen, x, y);
            string got = hit == null ? "(无)" : Node.Path(hit);
            bool bad = hit != null && ControlNames.Contains(hit.Go.name);
            if (bad)
            {
                Console.WriteLine($"      ✗ {page} {what,-8} ({x:F0},{y:F0}) 命中 {got}"
                    + "  ← 非交互区域不该激活控件");
                ClickFailures++;
            }
            else
            {
                Console.WriteLine($"      ✓ {page} {what,-8} → {got}（无控件被激活）");
            }
        }

        private static void Check(string page, string what, float x, float y, string want)
        {
            var hit = Node.HitTest(_screen, x, y);
            string got = hit == null ? "(无)" : Node.Path(hit);
            bool ok = got.EndsWith("/" + want) || got == want;
            if (ok)
            {
                Console.WriteLine($"      ✓ {page} {what,-8} → {got}");
            }
            else
            {
                Console.WriteLine($"      ✗ {page} {what,-8} ({x:F0},{y:F0}) 命中 {got}，"
                    + $"期望 {want}  ← 空白处也有控件在吃点击");
                ClickFailures++;
            }
        }

        private static void Report(string page, string what, Node n)
        {
            float cx = n.X + n.W * 0.5f, cy = n.Y + n.H * 0.5f;
            var hit = Node.HitTest(screenOf(n), cx, cy);
            if (hit == null)
            {
                Console.WriteLine($"      ✗ {page} {what,-8} 中心({cx:F0},{cy:F0}) 没命中任何东西"
                    + "  ← 控件被 raycastTarget=false 的容器盖住了？");
                ClickFailures++;
                return;
            }
            bool ok = ReferenceEquals(hit, n) || Node.Path(hit).EndsWith("/" + n.Go.name)
                || Node.Path(hit) == n.Go.name;
            if (!ok)
            {
                Console.WriteLine($"      ✗ {page} {what,-8} 中心({cx:F0},{cy:F0}) 命中的是 "
                    + $"{Node.Path(hit)}  ← 有别的东西盖在上面");
                ClickFailures++;
            }
            else
            {
                Console.WriteLine($"      ✓ {page} {what,-8} → {Node.Path(hit)}");
            }
        }

        private static Node _screen;

        private static Node screenOf(Node n)
        {
            for (var c = n; c != null; c = c.Parent)
            {
                if (c.Parent == null) return c;
            }
            return _screen;
        }

        private static Node Find(Node n, string name)
        {
            if (n.Go != null && n.Go.name == name) return n;
            foreach (var k in n.Kids)
            {
                var r = Find(k, name);
                if (r != null) return r;
            }
            return null;
        }

        private static List<Node> FindAll(Node n, string name)
        {
            var list = new List<Node>();
            void Walk(Node x)
            {
                if (x.Go != null && x.Go.name == name) list.Add(x);
                foreach (var k in x.Kids) Walk(k);
            }
            Walk(n);
            return list;
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