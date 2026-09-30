// 配置往返检查：页面写的每个设置，必须同时出现在保存清单和读取清单里。
//
// 与 ConfigFlusher 那个 bug 是同一族问题——都属于"界面点得动、值也变了，
// 但下次不认"。少一个字段的表现是：某个开关重开游戏后又弹回去了，
// 而界面上没有任何提示。
//
// 三份清单必须对齐：
//   ① 页面里被赋值的 Settings 字段
//   ② ConfigStore.Flush 写进 JSON 的键
//   ③ ConfigStore.Apply 从 JSON 读回的字段
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class ConfigCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 配置往返 ──");
            string pages = Read(projectRoot, @"Pages\Pages.cs");
            string settings = Read(projectRoot, @"Config\Settings.cs");
            if (pages.Length == 0 || settings.Length == 0)
            {
                Console.WriteLine("    ? 读不到源码，检查路径");
                return 0;
            }

            var written = Unique(Regex.Matches(pages,
                @"Settings\.(\w+)\s*="), 1);
            // 保存清单里有的带强制转换（`{ "Mode", (int)Settings.Mode }`），
            // 正则漏掉它会把已保存的字段误报成"没保存"——断言自己写错，
            // 比代码错更容易误导人。
            var flushed = Unique(Regex.Matches(Slice(settings, "Flush"),
                @"{ ""(\w+)"",\s*(?:\([\w.]+\))?\s*Settings\."), 1);
            var applied = Unique(Regex.Matches(Slice(settings, "private static void Apply"),
                @"Settings\.(\w+)\s*="), 1);

            int fail = 0;
            fail += Diff("页面写的但没保存", written, flushed, ref written);
            fail += Diff("保存了但没读回", flushed, applied, ref flushed);
            fail += Diff("页面写的但没读回", written, applied, ref written);
            if (fail == 0)
            {
                Console.WriteLine($"    ✓ 三份清单一致（各 {written.Count} 个设置）");
            }
            return fail;
        }

        private static int Diff(string what, HashSet<string> a, HashSet<string> b,
            ref HashSet<string> keep)
        {
            var miss = new List<string>();
            foreach (var k in a)
            {
                if (!b.Contains(k)) miss.Add(k);
            }
            foreach (var k in miss)
            {
                Console.WriteLine($"    ✗ {what}：{k}  ← 改动不会持久化");
            }
            return miss.Count;
        }

        private static HashSet<string> Unique(MatchCollection ms, int group)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in ms)
            {
                set.Add(m.Groups[group].Value);
            }
            return set;
        }

        private static string Read(string root, string rel)
        {
            string p = Path.Combine(root, rel);
            return File.Exists(p) ? File.ReadAllText(p) : string.Empty;
        }

        /// <summary>取从某个标记开始到下一个方法定义之间的片段。</summary>
        private static string Slice(string src, string marker)
        {
            int i = src.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return string.Empty;
            int len = Math.Min(4000, src.Length - i);
            return src.Substring(i, len);
        }
    }
}
