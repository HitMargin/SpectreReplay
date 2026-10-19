// 文案回退链是否被真验到。
//
// 背景：Loc.T 是**四级回退**——
//   ① 命中当前语言
//   ② 命中英文
//   ③ 命中中文（作者母语，通常最全，用它兜住漏翻）
//   ④ 露键名（全都漏 = 真漏）
//
// LocCheck 只验了"最终不露键名"，也就是把 ①②③ 全算通过。于是
// **某种语言的表缺一个键时**：①不中 → ②中英文 ⇒ 玩家在他的语言里
// 看到英文，而闸门全绿（它只看"有没有露键名"）。
//
// 这类"静默降级到另一种语言"正是自己机器上永远发现不了的问题。
// 本检查直接对比各表键集合，并逐级证明兜底链是活的。
using System;
using System.Collections.Generic;
using System.Linq;
using SpectreReplay.Ui;

namespace UiPreview
{
    internal static class LocFallbackCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 文案回退链（含静默降级） ──");
            int fail = 0;

            var tables = LocData.Languages;
            if (tables.Count == 0)
            {
                Console.WriteLine("    ✗ 取不到 LocData.Languages（LocData.cs 形状变了？）");
                return 1;
            }

            var en = tables["en"];
            var zh = tables["zh"];

            // ① 每一种语言的键集合都必须与英文一致。
            //    单边缺失 = 该语言下会静默降级成英文（不露键名，只查"露没露键名"抓不到）。
            foreach (var lang in LocCheck.Languages)
            {
                if (!tables.TryGetValue(lang, out var t))
                {
                    Console.WriteLine("    ✗ 缺少语言表 " + lang);
                    fail++;
                    continue;
                }
                var lackKeys = en.Keys.Where(k => !t.ContainsKey(k))
                    .OrderBy(x => x, StringComparer.Ordinal).ToList();
                var extraKeys = t.Keys.Where(k => !en.ContainsKey(k))
                    .OrderBy(x => x, StringComparer.Ordinal).ToList();
                foreach (var k in lackKeys.Take(3))
                {
                    Console.WriteLine("    ✗ " + lang + " 缺 " + k + " —— 该语言界面会静默显示英文（不露键名，闸门原本抓不到）");
                    fail++;
                }
                if (lackKeys.Count > 3) fail += lackKeys.Count - 3;
                foreach (var k in extraKeys.Take(3))
                {
                    Console.WriteLine("    ✗ " + lang + " 多 " + k + " —— 英文基准里没有这个键");
                    fail++;
                }
                if (extraKeys.Count > 3) fail += extraKeys.Count - 3;
            }
            if (fail == 0)
            {
                Console.WriteLine("    ✓ 各语言键集合完全一致（各 " + en.Count + " 条），无静默降级");
            }

            // ② 行为验证：让某语言缺键，确认 T 真的退到英文（而不是露键名）。
            //
            //    探针键必须在**所有**语言里都存在，否则 Remove 的命中与否本身就
            //    不确定。page.record 是四个页签之一，必然有。
            string probe = "page.record";
            string langSaved = Loc.CurrentLanguage;
            try
            {
                Loc.CurrentLanguage = "ja";
                var ja = tables["ja"];
                string jaSaved = ja[probe];
                ja.Remove(probe);
                string got = Loc.T(probe);
                ja[probe] = jaSaved;
                bool fellBack = got == en[probe];
                Console.WriteLine("    " + (fellBack ? "✓" : "✗")
                    + " ② 当前语言缺键时退回英文（探针 " + probe + "）");
                if (!fellBack) fail++;
            }
            finally
            {
                Loc.CurrentLanguage = langSaved;
            }

            // ③ 连英文也缺时才退到中文——证明中间那一级不是摆设。
            string enSaved = en[probe];
            try
            {
                Loc.CurrentLanguage = "ja";
                var ja = tables["ja"];
                string jaSaved = ja[probe];
                ja.Remove(probe);
                en.Remove(probe);
                string got = Loc.T(probe);
                ja[probe] = jaSaved;
                bool fellBack = got == zh[probe];
                Console.WriteLine("    " + (fellBack ? "✓" : "✗")
                    + " ③ 英文也缺时退回中文（证明中文兜底不是摆设）");
                if (!fellBack) fail++;
            }
            finally
            {
                en[probe] = enSaved;      // 无论上面成没成，字典都要复原
                Loc.CurrentLanguage = "en";
            }

            // ④ 全都缺才露键名——这是"真漏"的唯一信号
            string ghost = "__no_such_key__";
            string leaked = Loc.T(ghost);
            bool ok = leaked == ghost;
            Console.WriteLine("    " + (ok ? "✓" : "✗") + " ④ 全都缺才露键名");
            if (!ok) fail++;

            Loc.CurrentLanguage = "en";
            if (fail == 0)
            {
                Console.WriteLine("    ✓ 回退链每一级都按设计工作，且没有静默降级");
            }
            return fail;
        }
    }
}