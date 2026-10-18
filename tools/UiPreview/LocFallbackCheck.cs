// 文案回退链是否被真验到。
//
// 背景：Loc.T 是**三级回退**——
//   ① UseChinese 命中 Zh
//   ② 命中 En
//   ③ 命中 Zh（另一种语言漏翻时兜底，别露键名）
//   ④ 露键名（两种都漏 = 真漏）
//
// LocCheck 只验了"最终不露键名"，也就是把 ①②③ 全算通过。于是
// **英文表缺一个键时**：UseChinese=false → ①不中 → ②不中 → ③中中文 ⇒
// 玩家看到中文，而闸门全绿（它只看"有没有露键名"）。
//
// 这类"静默降级到另一种语言"正是中文环境下永远发现不了的问题。
// 本检查直接对比两张表的键集合，任何单边缺失都报出来。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace UiPreview
{
    internal static class LocFallbackCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 文案回退链（含静默降级） ──");
            int fail = 0;

            var zh = Table("Zh");
            var en = Table("En");
            if (zh.Count == 0 || en.Count == 0)
            {
                Console.WriteLine("    ✗ 取不到 Zh/En 表（Loc.cs 形状变了？）");
                return 1;
            }

            // ① 单边缺失 = 会在另一种语言里静默降级
            var onlyZh = zh.Keys.Except(en.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var onlyEn = en.Keys.Except(zh.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();
            foreach (var k in onlyZh)
            {
                Console.WriteLine("    ✗ 只有中文有 " + k + " —— 英文界面会静默显示中文（不露键名，闸门原本抓不到）");
                fail++;
            }
            foreach (var k in onlyEn)
            {
                Console.WriteLine("    ✗ 只有英文有 " + k + " —— 中文界面会静默显示英文");
                fail++;
            }
            if (onlyZh.Count == 0 && onlyEn.Count == 0)
                Console.WriteLine($"    ✓ 两张表键集合完全一致（各 {zh.Count} 条），无静默降级");

            // ② 行为验证：故意造成"英文缺键"，确认 T 真的会退回中文（而不是露键名）
            //    这证明③那条兜底链是活的——否则"英文缺键"会直接露键名，
            //    虽然也会被发现，但诊断方向完全不同。
            string probe = en.Keys.First();
            string saved = en[probe];
            bool removed = en.Remove(probe);
            string got = SpectreReplay.Ui.Loc.T(probe);   // UseChinese 此时应为 false
            en[probe] = saved;
            bool fellBack = removed && got == zh[probe];
            Console.WriteLine($"    {(fellBack ? "✓" : "✗")} ② 英文字典缺键时真的退回中文（探针键 {probe}）");
            if (!fellBack) fail++;

            // ③ 两张都缺才露键名——这是"真漏"的唯一信号
            string ghost = "__no_such_key__";
            string leaked = SpectreReplay.Ui.Loc.T(ghost);
            bool ok = leaked == ghost;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ③ 两种语言都缺才露键名");
            if (!ok) fail++;

            if (fail == 0)
                Console.WriteLine("    ✓ 回退链每一级都按设计工作，且没有静默降级");
            return fail;
        }

        private static Dictionary<string, string> Table(string field)
        {
            var f = typeof(SpectreReplay.Ui.Loc).GetField(field,
                BindingFlags.NonPublic | BindingFlags.Static);
            return f?.GetValue(null) as Dictionary<string, string>
                ?? new Dictionary<string, string>();
        }
    }
}
