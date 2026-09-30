// 孤儿 MonoBehaviour 检查。
//
// 定义了却从没被 AddComponent 挂上去的组件，等于一行死代码——但它往往
// 承担着关键职责。真实踩过的坑：ConfigFlusher 负责把设置写盘，
// Save() 只标脏、真正写文件靠它的 LateUpdate，可它从来没被挂上去，
// 于是**用户在界面里改的每个设置重开游戏就丢了**，表现是
// "点得动、界面也变、就是不生效"。
//
// 静态扫一遍：所有 MonoBehaviour 派生类，必须在源码里被 AddComponent 过。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class OrphanCheck
    {
        internal static int Run(string sourceRoot)
        {
            Console.WriteLine("  ── 孤儿组件 ──");
            var sources = new List<string>();
            foreach (var f in Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains(@"\obj\") || f.Contains(@"\bin\")) continue;
                sources.Add(f);
            }
            var all = new List<string>();
            foreach (var f in sources)
            {
                all.Add(File.ReadAllText(f));
            }
            string blob = string.Join("\n", all);

            var defined = new List<string>();
            foreach (Match m in Regex.Matches(blob,
                @"internal\s+(?:sealed\s+)?class\s+(\w+)\s*:\s*MonoBehaviour"))
            {
                defined.Add(m.Groups[1].Value);
            }
            // 公开的也要（可能没写 internal）
            foreach (Match m in Regex.Matches(blob,
                @"(?:^|\n)\s*(?:public\s+)?(?:sealed\s+)?class\s+(\w+)\s*:\s*MonoBehaviour"))
            {
                if (!defined.Contains(m.Groups[1].Value))
                {
                    defined.Add(m.Groups[1].Value);
                }
            }

            int fail = 0;
            if (defined.Count == 0)
            {
                Console.WriteLine("    ? 没扫到任何 MonoBehaviour，检查一下路径");
                return 0;
            }
            foreach (var name in defined)
            {
                bool mounted = Regex.IsMatch(blob,
                    @"AddComponent<\s*(?:[A-Za-z0-9_.]+\.)?" + Regex.Escape(name) + @"\s*>")
                    || Regex.IsMatch(blob, @"new\s+" + Regex.Escape(name) + @"\s*\(");
                if (mounted)
                {
                    Console.WriteLine($"    ✓ {name}");
                }
                else
                {
                    Console.WriteLine($"    ✗ {name} 定义了但从没被挂上去"
                        + "  ← 它承担的活儿根本没在跑");
                    fail++;
                }
            }
            return fail;
        }
    }
}
