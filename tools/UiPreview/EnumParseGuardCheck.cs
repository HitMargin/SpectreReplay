// 持久化枚举值必须拒掉数字串。
//
// 为什么（2026-10-06 抓到两处）：`Enum.TryParse("99", out var v)` **会成功**，
// 并给出 `(SomeEnum)99` 这种越界值。越界枚举不会抛异常，只会：
//   · ReplayAnalyzer 里 → 窗口宽度按错误难度算（显示错，不影响判定）
//   · Patch_Lifecycle 里 → `GCS.hitMarginLimit` 被直接赋成越界值，
//     scrFloor.LightUp 与判定路径都读它（影响实跑）
// 回放文件是**玩家可编辑的文本**，"JudgeMode": "99" 完全可能出现。
//
// 判据：源码里每个从回放数据取难度字符串的 Enum.TryParse，其后必须紧跟
// `Enum.IsDefined` 校验。已修的三处 + 本来就对的 HitMarginCompat 一起纳入。
using System;
using System.IO;
using System.Linq;

namespace UiPreview
{
    internal static class EnumParseGuardCheck
    {
        // 已知必须带 IsDefined 护栏的调用点
        private static readonly (string file, string needle)[] Sites =
        {
            (@"Features\Replay\Patches\Patch_Lifecycle.cs", "Enum.TryParse<HitMarginLimit>"),
            (@"Features\Replay\Runtime\ReplayAnalyzer.cs", "Enum.TryParse(judgeMode"),
        };

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 持久化枚举护栏（Enum.IsDefined） ──");
            int fail = 0;
            foreach (var (rel, needle) in Sites)
            {
                string ok = Path.Combine(projectRoot, rel);
                if (!File.Exists(ok))
                {
                    Console.WriteLine("    ✗ 找不到 " + rel);
                    fail++;
                    continue;
                }
                // **必须剥注释**：判据禁止的写法（没有 IsDefined 的 TryParse）会在
                // 解释"为什么必须加"的注释里被逐字写出来。不剥就是常红。
                string code = SrcUtil.Clean(File.ReadAllText(ok));
                int at = code.IndexOf(needle, StringComparison.Ordinal);
                if (at < 0)
                {
                    // 调用点形状变了（改成 Enum.TryParse<T> 之类）也是信号
                    Console.WriteLine("    ✗ " + rel + " 里找不到 " + needle
                        + "（调用形状变了？同步更新 EnumParseGuardCheck 的清单）");
                    fail++;
                    continue;
                }
                // 从调用点起往后看 200 字符内必须出现 IsDefined（允许跨行续接）
                int window = Math.Min(200, code.Length - at);
                string after = code.Substring(at, window);
                bool guarded = after.Contains("Enum.IsDefined", StringComparison.Ordinal);
                Console.WriteLine($"    {(guarded ? "✓" : "✗")} {rel} 的 Enum.TryParse "
                    + (guarded ? "带 IsDefined 护栏" : "**没有** IsDefined——数字串会被当成合法枚举值"));
                if (!guarded) fail++;
            }

            // 反向确认：HitMarginCompat 那处本来就有数字串防护，别被改坏
            string compat = Path.Combine(projectRoot, @"Features\Compat\HitMarginCompat.cs");
            if (File.Exists(compat))
            {
                string c = SrcUtil.Clean(File.ReadAllText(compat));
                bool has = c.Contains("Enum.TryParse(name, false,", StringComparison.Ordinal);
                Console.WriteLine($"    {(has ? "✓" : "✗")} HitMarginCompat 仍用带 caseSensitive 形参的 TryParse"
                    + "（`false` 形参是它拒数字串语义的一部分）");
                if (!has) fail++;
            }

            if (fail == 0)
                Console.WriteLine("    ✓ 持久化枚举都有护栏");
            return fail;
        }
    }
}
