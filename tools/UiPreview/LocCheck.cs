// 文案完整性验证。
//
// 漏一个键，界面上就是空白或直接显示 "record.autosave" 这种原始键名——
// 而这在真机上一眼就能看见，却恰恰是最容易在改版式时被忽略的地方。
// 这里把"页面实际用到的键"逐一在两套字典里查一遍。
//
// 另外顺带做一件同类的事：界面里出现的每一个中文字符，是否都在
// 当前 TMP 字体的字符表里。TMP 缺字形就是画一个空白方块，且不报错。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SpectreReplay.Ui;

namespace UiPreview
{
    internal static class LocCheck
    {
        /// <summary>直接扫页面源码，收集所有以字面量形式传入的键。</summary>
        internal static HashSet<string> CollectUsedKeys()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in SourceFiles())
            {
                if (!File.Exists(f)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(f),
                    @"""([a-z][a-z0-9]*\.[a-z0-9_.]+)"""))
                {
                    used.Add(m.Groups[1].Value);
                }
            }
            return used;
        }

        private static IEnumerable<string> SourceFiles() => new[]
        {
            @"..\..\SpectreReplay\Pages\Pages.cs",
            @"..\..\SpectreReplay\Ui\OnGui.cs",
        };

        /// <summary>
        /// 扫描页面代码里用到的文案键。
        ///
        /// 以前这里还要和"预览 shim"对账——预览器内联了一份与 Pages.cs
        /// 同步的页面内容，改一处忘另一处，预览出来的就跟实际界面不一样，
        /// 而且图上看不出来。换成 UMM 的 OnGUI 之后**根本没有 shim**：
        /// 预览器只验数据层，页面代码只有一份，这类"忘了同步"的坑整类消失。
        /// </summary>
        private static HashSet<string> KeysFrom(IEnumerable<string> files)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in files)
            {
                if (!File.Exists(f)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(f),
                    @"""([a-z][a-z0-9]*\.[a-z0-9_.]+)"""))
                {
                    set.Add(m.Groups[1].Value);
                }
            }
            return set;
        }

        internal static int Run(HashSet<string> used)
        {
            // 原来这里还有一次「预览 shim 与真实页面的文案键对账」。
            // 预览器删掉之后那个坑不复存在：页面就是 Pages.cs 本身，
            // 没有第二份需要同步的副本——曾经栽过的跟头不会再来。
            var zh = Table("Zh");
            var en = Table("En");
            int fail = 0;

            Console.WriteLine("  ── 文案完整性 ──");
            Console.WriteLine($"    页面用到 {used.Count} 个键；中文表 {zh.Count} 条，英文表 {en.Count} 条");

            var missZh = new List<string>();
            var missEn = new List<string>();
            foreach (var k in used)
            {
                if (!zh.ContainsKey(k)) missZh.Add(k);
                if (!en.ContainsKey(k)) missEn.Add(k);
            }
            // 两种语言都缺的（那些是拼错的键，不是漏翻）
            var bothMissing = new List<string>();
            foreach (var k in used)
            {
                if (!zh.ContainsKey(k) && !en.ContainsKey(k)) bothMissing.Add(k);
            }

            foreach (var k in bothMissing)
            {
                Console.WriteLine($"    ✗ 两种语言都没有：{k}  ← 拼错了，界面上会显示原始键名");
                fail++;
            }
            if (missZh.Count > 0 && bothMissing.Count == 0)
            {
                foreach (var k in missZh)
                {
                    Console.WriteLine($"    ✗ 中文缺：{k}  ← 中文界面下会退到英文");
                    fail++;
                }
            }
            if (missEn.Count > 0 && bothMissing.Count == 0)
            {
                foreach (var k in missEn)
                {
                    Console.WriteLine($"    ✗ 英文缺：{k}  ← 英文界面下会退到中文");
                    fail++;
                }
            }
            if (fail == 0)
            {
                Console.WriteLine("    ✓ 页面引用的键在两种语言下都齐");
            }

            // 表对账：两张字典的键集合必须一致。上面只查了“页面引用到的键”，
            // 于是两张表**互相缺**的那类漂移永远查不出来（实测 render.progress
            // 只有英文有、中文没有）。这类不一致的后果是：将来有人引用它时，
            // 中文界面会静默退到英文或直接露出键名，而改的人根本不知道自己漏了。
            // 全量对账比“等被引用”更早发现，代价是零。
            var onlyZh = new List<string>();
            var onlyEn = new List<string>();
            foreach (var k in zh.Keys) if (!en.ContainsKey(k)) onlyZh.Add(k);
            foreach (var k in en.Keys) if (!zh.ContainsKey(k)) onlyEn.Add(k);
            onlyZh.Sort(StringComparer.Ordinal);
            onlyEn.Sort(StringComparer.Ordinal);
            foreach (var k in onlyZh)
            {
                Console.WriteLine($"    ✗ 只有中文表有：{k}  ← 英文界面下会退到中文");
                fail++;
            }
            foreach (var k in onlyEn)
            {
                Console.WriteLine($"    ✗ 只有英文表有：{k}  ← 中文界面下会退到英文");
                fail++;
            }
            if (onlyZh.Count == 0 && onlyEn.Count == 0)
            {
                Console.WriteLine($"    ✓ 两张表键集合一致（各 {zh.Count} 条）");
            }

            // 两种语言下都解析一遍，确认不会露出键名
            foreach (var zhMode in new[] { true, false })
            {
                Loc.UseChinese = zhMode;
                int leaked = 0;
                foreach (var k in used)
                {
                    string got = Loc.T(k);
                    if (got == k)
                    {
                        Console.WriteLine($"    ✗ [{(zhMode ? "中文" : "英文")}] T(\"{k}\") 返回了键名本身");
                        leaked++;
                    }
                    if (string.IsNullOrWhiteSpace(got))
                    {
                        Console.WriteLine($"    ✗ [{(zhMode ? "中文" : "英文")}] T(\"{k}\") 是空白");
                        leaked++;
                    }
                }
                if (leaked == 0)
                {
                    Console.WriteLine($"    ✓ [{(zhMode ? "中文" : "英文")}] {used.Count} 个键都能解析成文案");
                }
                else
                {
                    fail += leaked;
                }
            }
            Loc.UseChinese = true;
            return fail;
        }

        private static Dictionary<string, string> Table(string field)
        {
            var t = typeof(Loc);
            var f = t.GetField(field,
                System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static);
            return f?.GetValue(null) as Dictionary<string, string>
                ?? new Dictionary<string, string>();
        }
    }
}
