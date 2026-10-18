// 日志前缀必须是 [SpectreReplay]，不能是 [Spectre]。
//
// 为什么这条是承重的：本项目的**加载诊断完全依赖前缀区分**——铁律⑤要人在
// Player.log 里找 `[SpectreReplay] Load() 进入` 才能确认模组真的跑了。
// 而真实环境里 **Spectre 模组确实装着并会跑**（真 Player.log 第 127 行
// `[Spectre] Version '0.4.3'. Loading.`）。两边都打 `[Spectre]` 时：
//   ① 玩家/开发者读日志无法判断哪行来自哪个 mod
//   ② 铁律⑤的判据失效——看到 `[Spectre] ...` 会误以为本模组已加载
//   ③ 用户报告问题时日志没法归类
//
// 历史：这 63 处是从 Spectre 整体移植时**逐字节复制**过来的（连日志字符串一起
// 搬了），所以"忠实移植"在这里正是缺陷成因。2026-10-06 全量改正。
//
// 判据：源码里不得出现 `"[Spectre] ` 这种日志字符串形状。
// 注意必须锚定**引号**以避免误伤：`namespace SpectreReplay`、注释里
// "Spectre 的回放文件" 这些合法的 "Spectre" 字样不受影响。
using System;
using System.IO;
using System.Linq;

namespace UiPreview
{
    internal static class LogPrefixCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 日志前缀（必须与 Spectre 区分） ──");
            int fail = 0;
            var bad = new System.Collections.Generic.List<string>();

            foreach (var f in Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                if (f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                string text = File.ReadAllText(f);
                // 只认日志字符串形状：引号 + [Spectre] + 空格。
                // 剥不剥注释都要查——注释里写 `[Spectre]` 会误导读者，
                // 但那是文档问题不是日志问题，所以这里用原始文本查字符串、
                // 用剥注释文本查会不会其实是代码。简化：直接查原始文本的字符串形状。
                var hits = System.Text.RegularExpressions.Regex.Matches(
                    text, "\"\\[Spectre\\] ");
                foreach (System.Text.RegularExpressions.Match m in hits)
                {
                    bad.Add(Path.GetFileName(f));
                    break;
                }
            }

            if (bad.Count > 0)
            {
                fail = bad.Count;
                Console.WriteLine($"    ✗ {bad.Count} 个文件仍在打 [Spectre] 前缀（应为 [SpectreReplay]）：");
                foreach (var b in bad.Take(10)) Console.WriteLine("        " + b);
                if (bad.Count > 10) Console.WriteLine($"        …另 {bad.Count - 10} 个");
                return fail;
            }
            Console.WriteLine("    ✓ 全部日志用 [SpectreReplay] 前缀（与真实 Spectre 模组可区分）");
            return fail;
        }
    }
}
