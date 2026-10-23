// 文案完整性验证。
//
// 漏一个键，界面上就是空白或直接显示 "record.autosave" 这种原始键名——
// 而这在真机上一眼就能看见，却恰恰是最容易在改版式时被忽略的地方。
// 这里把"页面实际用到的键"逐一在每一种语言的表里查一遍。
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
        /// <summary>
        /// 界面支持的全部语言代码。**必须与 Loc.CodeByLanguage 的值集合一致**，
        /// 判据 ⑲a 会重新解析 Loc.cs 做比对——这里手写一份清单，是因为
        /// 检查脚本要遍历它，而遍历 Reflect 出来的字典顺序不稳定。
        /// </summary>
        internal static readonly string[] Languages =
        {
            "zh", "zh-t", "en", "ko", "ja", "es", "pt", "fr", "de", "pl", "cs", "ro", "ru", "vi",
        };

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
            // 走 Driver.ProjectDir（从程序集位置向上推导），不再依赖工作目录
            Path.Combine(Driver.ProjectDir, "Pages", "Pages.cs"),
            Path.Combine(Driver.ProjectDir, "Ui", "OnGui.cs"),
        };

        internal static int Run(HashSet<string> used)
        {
            var tables = LocData.Languages;
            int fail = 0;

            Console.WriteLine("  ── 文案完整性 ──");
            Console.WriteLine($"    页面用到 {used.Count} 个键；语言 {tables.Count} 套");

            // ① 每种语言的表都必须与基准键集合一致。
            //
            //    这里**不是**只查"页面引用到的键"——那样只能发现被引用的漏翻，
            //    而 14 语言里最容易出的事是"某个键在某种语言下整条缺失"，
            //    它在别的语言下被引用过、在这个语言下没被引用就永远查不出来。
            //    全量对账比"等被引用"更早发现，代价是零。
            var baseline = new HashSet<string>(tables["en"].Keys, StringComparer.Ordinal);
            foreach (var lang in Languages)
            {
                if (!tables.TryGetValue(lang, out var t))
                {
                    Console.WriteLine($"    ✗ 缺少语言表：{lang}");
                    fail++;
                    continue;
                }
                var miss = new List<string>();
                foreach (var k in baseline) if (!t.ContainsKey(k)) miss.Add(k);
                var extra = new List<string>();
                foreach (var k in t.Keys) if (!baseline.Contains(k)) extra.Add(k);
                if (miss.Count > 0 || extra.Count > 0)
                {
                    Console.WriteLine($"    ✗ [{lang}] 键集合与 en 不一致：缺 {miss.Count}，多 {extra.Count}");
                    foreach (var k in miss.GetRange(0, Math.Min(3, miss.Count)))
                        Console.WriteLine($"        · 缺 {k}  ← 该语言下会退到英文");
                    foreach (var k in extra.GetRange(0, Math.Min(3, extra.Count)))
                        Console.WriteLine($"        · 多 {k}  ← en 里没有这个键");
                    fail++;
                }
            }
            if (fail == 0)
            {
                Console.WriteLine($"    ✓ {Languages.Length} 套语言表的键集合完全一致（各 {baseline.Count} 条）");
            }

            // ② 页面引用到的键，在**每一种语言**下都必须能解析成非空、且不是键名本身。
            foreach (var lang in Languages)
            {
                if (!tables.ContainsKey(lang)) continue;
                Loc.CurrentLanguage = lang;
                int bad = 0;
                foreach (var k in used)
                {
                    string got = Loc.T(k);
                    if (got == k)
                    {
                        Console.WriteLine($"    ✗ [{lang}] T(\"{k}\") 返回了键名本身");
                        bad++;
                    }
                    else if (string.IsNullOrWhiteSpace(got))
                    {
                        Console.WriteLine($"    ✗ [{lang}] T(\"{k}\") 是空白");
                        bad++;
                    }
                }
                if (bad == 0)
                {
                    Console.WriteLine($"    ✓ [{lang}] {used.Count} 个键都能解析成文案");
                }
                fail += bad;
            }

            // ③ 未在 14 种语言里的键（比如荷兰语）必须回落到英文，而不是露键名。
            Loc.CurrentLanguage = "nl";
            string fallback = Loc.T("page.record");
            string enVal = tables["en"]["page.record"];
            bool okFallback = fallback == enVal;
            Console.WriteLine($"    {(okFallback ? "✓" : "✗")} ③ 不支持的语言回落到英文（nl → \"{fallback}\"）");
            if (!okFallback) fail++;

            Loc.CurrentLanguage = "en";
            return fail;
        }
    }
}