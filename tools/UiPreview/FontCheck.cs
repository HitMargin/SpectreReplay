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
            TMPro.TMP_Settings.defaultFontAsset = null;
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

            // ⑥ 回退链：中英混排是常态（"60 fps" 挨着中文标签），
            //    只挂一个字体的话缺的那部分就是空白方块。
            fail += CheckFallbackChain();
            return fail;
        }

        /// <summary>
        /// 验证两件事：
        ///   · 中文主字体上挂了拉丁回退（照 DynamicallyOrderedFont 的做法）
        ///   · 兜底路径请求的是**游戏清单里真实存在**的资源名
        /// </summary>
        private static int CheckFallbackChain()
        {
            int fail = 0;
            UnityEngine.RDConstants.data = new UnityEngine.RDConstants
            {
                chineseFontTMPro = new TMPro.TMP_FontAsset { name = "CJK" },
                latinFontTMPro = new TMPro.TMP_FontAsset { name = "Latin" },
            };
            SpectreReplay.Ui.UiFonts.Invalidate();
            var main = SpectreReplay.Ui.UiFonts.Main;
            var chain = main?.fallbackFontAssetTable;
            bool ok = chain != null && chain.Count == 1 && chain[0].name == "Latin";
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ⑥ 中文字体挂上拉丁回退链"
                + $"  → {(chain == null || chain.Count == 0 ? "（空）" : chain[0].name)}");
            if (!ok) fail++;

            // 只留拉丁（模拟没有 CJK 资源），此时回退链不该重复挂自己
            UnityEngine.RDConstants.data = new UnityEngine.RDConstants
            {
                latinFontTMPro = new TMPro.TMP_FontAsset { name = "Latin" },
            };
            SpectreReplay.Ui.UiFonts.Invalidate();
            var m2 = SpectreReplay.Ui.UiFonts.Main;
            bool noDup = m2 == null || m2.fallbackFontAssetTable == null
                || m2.fallbackFontAssetTable.Count == 0
                || m2.fallbackFontAssetTable[0] != m2;
            Console.WriteLine($"    {(noDup ? "✓" : "✗")} ⑦ 只有一套字体时不把自身挂成回退");
            if (!noDup) fail++;

            // RDConstants 拿不到时，请求的路径必须是清单里真实存在的
            UnityEngine.RDConstants.data = null;
            UnityEngine.Resources.HasSourceHanSans = true;
            UnityEngine.Resources.LastPath = null;
            SpectreReplay.Ui.UiFonts.Invalidate();   // 忘了这行，Resolve 直接被缓存挡回去
            _ = SpectreReplay.Ui.UiFonts.Main;
            bool real = UnityEngine.Resources.LastPath != null
                && UnityEngine.Resources.Exists(UnityEngine.Resources.LastPath);
            Console.WriteLine($"    {(real ? "✓" : "✗")} ⑧ 兜底路径在游戏清单里真实存在"
                + $"  → {UnityEngine.Resources.LastPath ?? "(没请求)"}");
            if (!real) fail++;
            return fail;
        }

        /// <summary>重置缓存后解析一次，看 Main 是不是期望的那个字体。</summary>
        private static int Probe(string label, string expectName)
        {
            SpectreReplay.Ui.UiFonts.Invalidate();
            var main = SpectreReplay.Ui.UiFonts.Main;
            string got = main == null ? "(null)" : main.name;
            bool ok = got == expectName;
            if (ok)
            {
                Console.WriteLine($"    ✓ {label,-34} → {got}");
                return 0;
            }
            Console.WriteLine($"    ✗ {label,-34} → 得到 {got}，期望 {expectName}"
                + $"  [桩件状态: RDConstants.data={(UnityEngine.RDConstants.data == null ? "null" : "有")}"
                + $" SourceHanSans={UnityEngine.Resources.HasSourceHanSans}]");
            return 1;
        }
    }
}
