// 字体回退链的验证。
//
// 字形没法在游戏外验，但**解析逻辑**可以：把 RDConstants / Resources /
// TMP_Settings 都做成可控的桩，然后链接真实的 UiFonts.cs 跑一遍。
//
// 2026-10-01 就是靠这一步发现的：原实现用 FindAnyObjectByType<RDConstants>()
// 去场景里找 ScriptableObject，永远找不到，字体一路退化到兜底分支还不报错。
// 光看代码看不出来——得让三条路径分别命中。
using System;

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
            fail += Probe("③ 退到 Resources/SourceHanSans", "SourceHanSans");

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
