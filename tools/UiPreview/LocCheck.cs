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
            @"..\..\SpectreReplay\Ui\PanelHost.cs",
        };

        /// <summary>预览用的是 PagesShim，它必须与真实 Pages.cs 同步。</summary>
        private static IEnumerable<string> ShimFiles() => new[]
        {
            @"..\..\SpectreReplay\Pages\Pages.cs",
            "PagesShim.cs",
        };

        /// <summary>
        /// 真实页面与预览 shim 的文案键必须一致。
        ///
        /// 这次就栽在这儿：把下拉选项从写死的 "auto"/"low" 改成走文案表之后，
        /// 忘了同步 shim，预览里渲染出来的还是旧文案——图上看不出来，
        /// 因为两条路径都能正常显示字符串。靠人记着同步是不靠谱的。
        /// </summary>
        internal static int CheckShimInSync()
        {
            var real = KeysFrom(ShimFiles());
            var shim = KeysFrom(new[] { "PagesShim.cs" });
            var onlyReal = new List<string>();
            var onlyShim = new List<string>();
            foreach (var k in real) if (!shim.Contains(k)) onlyReal.Add(k);
            foreach (var k in shim) if (!real.Contains(k)) onlyShim.Add(k);
            if (onlyReal.Count == 0 && onlyShim.Count == 0)
            {
                Console.WriteLine("    ✓ 预览 shim 与真实页面的文案键一致");
                return 0;
            }
            foreach (var k in onlyReal)
            {
                Console.WriteLine($"    ✗ 真实页面用了 {k}，预览 shim 没有"
                    + "  ← 预览渲染的不是实际界面");
            }
            foreach (var k in onlyShim)
            {
                Console.WriteLine($"    ✗ 预览 shim 多了 {k}，真实页面没有");
            }
            return onlyReal.Count + onlyShim.Count;
        }

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
            int syncFail = CheckShimInSync();
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
                Console.WriteLine("    ✓ 所有键在两种语言下都齐");
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
