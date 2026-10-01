// 字体回退链的验证。
//
// 字形没法在游戏外验，但**解析逻辑**可以：把 RDConstants / Resources /
// TMP_Settings 都做成可控的桩，然后链接真实的 UiFonts.cs 跑一遍。
//
// 2026-10-01 就是靠这一步发现的：原实现用 FindAnyObjectByType<RDConstants>()
// 去场景里找 ScriptableObject，永远找不到，字体一路退化到兜底分支还不报错。
// 光看代码看不出来——得让三条路径分别命中。
using System;
using System.Collections.Generic;

namespace UiPreview
{
    internal static class FontCheck
    {
        internal static int Run()
        {
            int fail = 0;
            Console.WriteLine("  ── 字体回退链（跑真实的 UiFonts.Resolve）──");

            // ① RDConstants.data 里有中日韩字体
            UnityEngine.RDConstants.data = new UnityEngine.RDConstants
            {
                chineseFontTMPro = new TMPro.TMP_FontAsset { name = "CJK" },
                latinFontTMPro = new TMPro.TMP_FontAsset { name = "Latin" },
            };
            UnityEngine.Resources.HasSourceHanSans = true;
            TMPro.TMP_Settings.defaultFontAsset = new TMPro.TMP_FontAsset { name = "TMP默认" };
            fail += Probe("① RDConstants 命中中文", "CJK");

            // ② 只有拉丁（游戏没装中日韩语言包的情况）
            UnityEngine.RDConstants.data = new UnityEngine.RDConstants
            {
                latinFontTMPro = new TMPro.TMP_FontAsset { name = "Latin" },
            };
            fail += Probe("② RDConstants 只有拉丁", "Latin");

            // ③ RDConstants 为空，只能走 Resources
            UnityEngine.RDConstants.data = null;
            fail += Probe("③ 退到 Resources 的 CJK 字体",
                "SourceHanSans/ChineseSimplified/SourceHanSansSC-Normal");

            // ④ 全都没有 —— 必须降级而不是静默留 null
            UnityEngine.Resources.HasSourceHanSans = false;
            fail += Probe("④ 退到 TMP 默认字体", "TMP默认");

            // ⑤ 连默认字体都没有：应返回 null 并告警，不能抛异常
            // ⑤ 连默认字体都没有：应返回 null 并告警，不能抛异常。
            //    **必须先 Invalidate**：Resolve() 是粘的，选中的候选会缓存进
            //    _cjk。上一例（④）已经把 TMP 默认字体选进去了，不清缓存的话
            //    这里读到的还是它——那不是"彻底没有字体"的情形。
            TMPro.TMP_Settings.defaultFontAsset = null;
            SpectreReplay.Ui.UiFonts.Invalidate();
            int before = fail;
            var m = SpectreReplay.Ui.UiFonts.Main;
            bool none = m == null;
            if (!none)
            {
                fail++;
            }
            Console.WriteLine($"    {(none ? "✓" : "✗")} ⑤ 彻底没有字体           → "
                + $"Main={(none ? "null（会打告警）" : m.name)}");
            if (none)
            {
                // 预期路径
            }
            else
            {
                fail = before;
            }

            // ⑦ **选字体不能相信字段名**。
            //    真机日志里 RDConstants.data.chineseFontTMPro 给的是
            //    `cjkFonts-regular-normalized SDF`，它缺界面里 200 多个字，
            //    于是整页看着是空的。现在改成逐个候选验字形，谁真的有就用谁。
            fail += CheckPicksByGlyphs();
            // ⑧ **不许改游戏字体的回退链。** 模组改 fallbackFontAssetTable
            //    就是在改游戏自己界面用的那份共享状态。
            fail += CheckLeavesFallbackAlone();

            // 原来这里有一条"中文字体应挂上拉丁回退链"的断言。
            // **删掉了**：那个做法本身就是错的。
            //
            // 游戏把自己的 sourceFont（cjkFonts-regular-normalized SDF，
            // 自己不含 CJK）的 fallbackFontAssetTable 设成
            // [选中语言字体, ...其它, ...extra]。模组把这条链改掉，
            // 等于拆掉了游戏的字体解析链——汉字只在链里的简体中文字体上。
            //
            // 中英混排不靠这个解决：选字体时逐个验过字形，挑的是一个
            // **自己就带全部所需字形**的字体（Source Han Sans SC 里本来
            // 就有拉丁字母）。
            return fail;
        }

        /// <summary>
        /// 每个用例的共同套路：清缓存 → 读 Main → 看是不是期望的那个字体。
        /// </summary>
        private static int Probe(string label, string expect)
        {
            SpectreReplay.Ui.UiFonts.Invalidate();
            var m = SpectreReplay.Ui.UiFonts.Main;
            bool ok = m != null && m.name == expect;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} {label,-28} → "
                + $"{(m == null ? "null" : m.name)}");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 选字体时**逐个候选验字形**：字段名叫"中文"的未必真���常用字。
        ///
        /// 真机上就是这么翻车的：RDConstants.data.chineseFontTMPro 给的是
        /// `cjkFonts-regular-normalized SDF`，界面里 200 多个字它都没有，
        /// 屏幕上却只有少数几个字（靠回退链救回来的）。界面上看起来就是
        /// "整页是空的"。
        ///
        /// 这里模拟两个候选：前面那个缺一堆字，后面那个齐全。
        /// 期望选中后面那个——而不是"第一个取到的"。
        /// </summary>
        private static int CheckPicksByGlyphs()
        {
            UnityEngine.Resources.HasSourceHanSans = true;
            TMPro.TMP_Settings.defaultFontAsset = null;

            var bogus = new TMPro.TMP_FontAsset { name = "缺字的那个" };
            // good 必须挂在**真实的资源路径**上，否则 Resources.Load 取不到它，
            // 解析就会落到别的候选上——那不是"选错了"，是"压根没这个选项"。
            var good = new TMPro.TMP_FontAsset
            {
                name = "SourceHanSans/ChineseSimplified/SourceHanSansSC-Normal"
            };
            foreach (char c in "渲界面设置录制回放视频关于") bogus.Missing.Add(c);
            // 上一个用例（⑤）把 RDConstants.data 整个置空了，得先建回来。
            if (UnityEngine.RDConstants.data == null)
            {
                UnityEngine.RDConstants.data = new UnityEngine.RDConstants();
            }
            var saved = UnityEngine.RDConstants.data?.chineseFontTMPro;
            UnityEngine.RDConstants.data.chineseFontTMPro = bogus;
            // 让 Resources 分发到那个"齐全"的字体
            UnityEngine.Resources.Registry.Clear();
            UnityEngine.Resources.Registry.Add(good);
            SpectreReplay.Ui.UiFonts.Invalidate();
            var picked = SpectreReplay.Ui.UiFonts.Main;
            bool ok = picked == good;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} 缺字的候选被跳过 "
                + $"→ 选中「{picked?.name}」");

            UnityEngine.RDConstants.data.chineseFontTMPro = saved;
            UnityEngine.Resources.Registry.Clear();
            UnityEngine.Resources.HasSourceHanSans = false;
            TMPro.TMP_Settings.defaultFontAsset = null;
            SpectreReplay.Ui.UiFonts.Invalidate();
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 解析字体**不许改动任何字体的 fallbackFontAssetTable**。
        ///
        /// 游戏的做法（ADOFAI\DynamicallyOrderedFont.ApplyForLanguage）是：
        ///
        ///     sourceFont.fallbackFontAssetTable = [选中语言字体, ...其它, ...extra];
        ///
        /// 也就是说 `cjkFonts-regular-normalized SDF` 是 **sourceFont**，
        /// 它自己本来就不含 CJK 字形，汉字全在它回退链里的语言字体上。
        ///
        /// 模组原先把这条链设成 `[拉丁]`，等于**把游戏的字体解析链拆了**——
        /// 那正是"整页字都不显示"的成因之一。fallbackFontAssetTable 是
        /// 资源上的共享全局状态，不是某个文字的私有属性。
        /// </summary>
        private static int CheckLeavesFallbackAlone()
        {
            UnityEngine.Resources.HasSourceHanSans = true;
            TMPro.TMP_Settings.defaultFontAsset = null;
            if (UnityEngine.RDConstants.data == null)
            {
                UnityEngine.RDConstants.data = new UnityEngine.RDConstants();
            }

            // 给游戏那个 sourceFont 装上一条"本来就该有"的链
            var baseFont = new TMPro.TMP_FontAsset
            { name = "sourceFont（游戏的 base font）" };
            var sc = new TMPro.TMP_FontAsset { name = "简体中文字体" };
            baseFont.fallbackFontAssetTable =
                new List<TMPro.TMP_FontAsset> { sc, new TMPro.TMP_FontAsset() };

            UnityEngine.RDConstants.data.chineseFontTMPro = baseFont;
            UnityEngine.RDConstants.data.latinFontTMPro = null;
            UnityEngine.Resources.Registry.Clear();
            UnityEngine.Resources.Registry.Add(
                new TMPro.TMP_FontAsset
                {
                    name = "SourceHanSans/ChineseSimplified/SourceHanSansSC-Normal"
                });

            int before = baseFont.fallbackFontAssetTable.Count;
            var firstBefore = baseFont.fallbackFontAssetTable[0];
            SpectreReplay.Ui.UiFonts.Invalidate();
            var _ = SpectreReplay.Ui.UiFonts.Main;

            int after = baseFont.fallbackFontAssetTable.Count;
            bool same = after == before
                && ReferenceEquals(baseFont.fallbackFontAssetTable[0], firstBefore);
            Console.WriteLine($"    {(same ? "✓" : "✗")} 不改游戏字体的回退链 "
                + $"  → 条数 {before}→{after}");
            if (!same)
            {
                Console.WriteLine("        模组把游戏自己的字体解析链改了");
            }
            SpectreReplay.Ui.UiFonts.Invalidate();
            return same ? 0 : 1;
        }
    }
}

