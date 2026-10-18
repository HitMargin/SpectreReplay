// 运行时文案键必须真的存在。
//
// 为什么单独一条：GetLocalizedText 查不到时 `return key`（LocalizationManager.cs:5922），
// 也就是**玩家会在屏幕上看到 "note.play_error2" 这样的原始键名**。
// 而这种键只在**异常路径**上被引用（关卡不匹配、音频启动失败、ffmpeg 起不来），
// 正常玩一整局一次都不会走到——漏了它在中文/英文环境下都发现不了。
//
// 与 LocCheck 的分工：LocCheck 扫的是 Pages.cs / OnGui.cs 里的**页面键**（Ui\Loc.cs 两表），
// 本检查扫的是 GetLocalizedText("...") 的**运行时键**（LocalizationManager.cs 的 14 语言表）。
// 两条扫的文件与表都不同，互相不可替代。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UiPreview
{
    internal static class MissingRuntimeKeyCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 运行时文案键存在性 ──");
            var files = Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "tools" + Path.DirectorySeparatorChar))
                .ToList();

            // ① 被引用的运行时键（剥注释，注释里举例提到的键不算引用）
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                string code = SrcUtil.Clean(File.ReadAllText(f));
                var hits = System.Text.RegularExpressions.Regex.Matches(
                    code, "GetLocalizedText\\(\"([a-z][a-z0-9_]*(?:\\.[a-z0-9_]+)+)\"\\)");
                foreach (System.Text.RegularExpressions.Match m in hits)
                {
                    used.Add(m.Groups[1].Value);
                }
            }

            // ② LocalizationManager.cs 里 14 张语言表定义的所有键。
            //    正则不锚定行首——这些行是缩进的，且 `[` 前有空格（锚定会一条都匹配不到）。
            string lm = File.ReadAllText(Path.Combine(projectRoot, "State", "LocalizationManager.cs"));
            var definedRuntime = new HashSet<string>(StringComparer.Ordinal);
            var lmHits = System.Text.RegularExpressions.Regex.Matches(
                lm, "\\[\"([a-z][a-z0-9_]*(?:\\.[a-z0-9_]+)+)\"\\]");
            foreach (System.Text.RegularExpressions.Match m in lmHits)
            {
                definedRuntime.Add(m.Groups[1].Value);
            }

            // ③ Ui\Loc.cs 的页面键也一并算"有定义"（两个表服务不同调用方，互不取代）
            string ui = File.ReadAllText(Path.Combine(projectRoot, "Ui", "Loc.cs"));
            var definedUi = new HashSet<string>(StringComparer.Ordinal);
            var uiHits = System.Text.RegularExpressions.Regex.Matches(
                ui, "\\{\\s*\"([a-z][a-z0-9_]*(?:\\.[a-z0-9_]+)+)\"\\s*,");
            foreach (System.Text.RegularExpressions.Match m in uiHits)
            {
                definedUi.Add(m.Groups[1].Value);
            }

            int fail = 0;
            foreach (string k in used.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!definedRuntime.Contains(k) && !definedUi.Contains(k))
                {
                    // 玩家会看到键名本身——这就是这条断言存在的全部理由
                    Console.WriteLine("    ✗ 运行时键没有定义：" + k
                        + "  ← 界面上会直接显示这个键名");
                    fail++;
                }
            }
            if (fail == 0)
                Console.WriteLine("    ✓ " + used.Count + " 个运行时键都有定义");
            return fail;
        }
    }
}
