// 运行时文案键完整性（LocalizationManager 那一套，全部语言块）。
//
// 为什么必须单独查。LocCheck 主要那段只覆盖 Loc.cs 的键，而通知/弹窗走的是
// LocalizationManager.GetLocalizedText 那一套（34 个键），离线闸门从没验过。
// 这些同样是玩家可见文字，漏一个的表现也一样：通知里直接显示原始键名
// （例如 rendervideo.wait_timeout）。
//
// 更要紧的是这套表有十几个语言块：漏在哪个语种，只有把游戏切成那个语种的玩家才
// 看得见，中文/英文环境下永远发现不了 —— 正是最该交给闸门的那类缺陷。
//
// 实现刻意不用正则，也刻意不在 C# 里写任何带转义引号的字面量：这套工具链
// 在多层转义下很容易把 "" 写坏，改用 Q 这个 char 常量拼字符串。
using System;
using System.Collections.Generic;
using System.IO;

namespace UiPreview
{
    internal static class LocRuntimeCheck
    {
        // 双引号字符。用它拼标记串，源码里就不需要 \" 这种转义。
        private const char Q = (char)34;
        // 换行/回车：判断捞到的键是否跨行（跨行说明匹配错了）
        private const char LF = (char)10;
        private const char CR = (char)13;
        private const char RB = (char)41;   // )
        private const char SEMI = (char)59; // ;
        private const char LB = (char)123;  // {
        private const char SP = (char)32;   // 空格
        private const char EQ = (char)61;   // =

        internal static int Run()
        {
            // 语言数不写死：写完 13 那次就漏了 zh-t（繁体），实际是 14。
            // 数出来再打印，标签永远不会跟事实不符。
            Console.WriteLine("  ── 运行时文案键（多语言）──");
            string root = Driver.ProjectDir;   // 从程序集位置推导，不依赖工作目录
            string lm = Path.Combine(root, Path.Combine("State", "LocalizationManager.cs"));
            if (!File.Exists(lm))
            {
                Console.WriteLine("    ✗ 找不到 LocalizationManager.cs，检查无效");
                return 1;
            }

            var perLang = ParseLanguageTables(File.ReadAllText(lm));
            if (perLang.Count == 0)
            {
                Console.WriteLine("    ✗ 一个语言块都没解析出来（文件结构变了？）");
                return 1;
            }

            var used = new Dictionary<string, string>(StringComparer.Ordinal);
            string sep = Path.DirectorySeparatorChar.ToString();
            foreach (string f in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains(sep + "obj" + sep) || f.Contains(sep + "bin" + sep)) continue;
                if (f.Contains(sep + "tools" + sep)) continue;
                if (Path.GetFileName(f) == "LocalizationManager.cs") continue;
                string text = File.ReadAllText(f);
                string name = Path.GetFileName(f);
                CollectPrefixed(text, "GetLocalizedText(" + Q, name, used);
                CollectSetPhase(text, name, used);
            }

            if (used.Count == 0)
            {
                Console.WriteLine("    ✗ 一个运行时键都没收集到（扫描失效）");
                return 1;
            }

            var langs = new List<string>(perLang.Keys);
            langs.Sort(StringComparer.Ordinal);
            int fail = 0;
            foreach (var kv in used)
            {
                var miss = new List<string>();
                foreach (string lang in langs)
                    if (!perLang[lang].Contains(kv.Key)) miss.Add(lang);
                if (miss.Count == 0) continue;
                Console.WriteLine("    ✗ " + kv.Key + "  (" + kv.Value + ")  缺 "
                    + miss.Count + " 种语言: " + string.Join(",", miss));
                fail++;
            }
            if (fail == 0)
                Console.WriteLine("    ✓ " + used.Count + " 个运行时键在全部 "
                    + langs.Count + " 种语言下都齐");

            // 反向：表里定义了的键，必须真有人取词。
            // 只在"每个键都齐"上判是不够的——这套表历史上从 Spectre 整块搬来，
            // 搬完大功能（CustomJudge / ResultsPlus / 编辑器增强 / 21 个去特效分组）
            // 被裁掉后，键没人删，于是 315 个键里真正在用的只有 33 个。
            // 死键的害处不是体积（几十 KB 而已），而是下一次读这段表的人
            // 会照着它去理解界面——他看到的是一份早就不是现状的清单。
            // 这条判据钉住"表里不许有没人取的键"。
            var defined = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lang in langs)
                foreach (string k in perLang[lang]) defined.Add(k);

            var deadKeys = new List<string>();
            foreach (string k in defined)
                if (!used.ContainsKey(k)) deadKeys.Add(k);
            deadKeys.Sort(StringComparer.Ordinal);
            if (deadKeys.Count == 0)
                Console.WriteLine("    ✓ 表里 " + defined.Count + " 个键全部有人取，没有死键");
            else
            {
                Console.WriteLine("    ✗ 有 " + deadKeys.Count + " 个键定义了却没人取（应从表里删掉）:");
                foreach (string k in deadKeys) Console.WriteLine("        " + k);
                fail++;
            }
            return fail;
        }

        // GetLocalizedText 后面紧跟开引号，前缀里已经带了 Q
        private static void CollectPrefixed(string text, string prefix, string file,
            Dictionary<string, string> used)
        {
            int i = 0;
            while (true)
            {
                int at = text.IndexOf(prefix, i, StringComparison.Ordinal);
                if (at < 0) break;
                int start = at + prefix.Length;
                int end = text.IndexOf(Q, start);
                if (end < 0) break;
                i = end + 1;
                AddKey(text, start, end, file, used);
            }
        }

        // SetPhase(阶段, 键, 细节)：键必须是第二个实参
        private static void CollectSetPhase(string text, string file,
            Dictionary<string, string> used)
        {
            const string marker = "SetPhase(";
            int i = 0;
            while (true)
            {
                int at = text.IndexOf(marker, i, StringComparison.Ordinal);
                if (at < 0) break;
                int q = text.IndexOf(Q, at + marker.Length);
                if (q < 0) break;
                // 标记与引号之间只允许出现逗号/空格/字母数字点：否则多半是
                // SetPhase 的方法定义（第二个形参是 string stageKey，没有引号），
                // 往后捞会把别处的字符串误当键。
                string between = text.Substring(at + marker.Length, q - at - marker.Length);
                if (between.IndexOf(RB) >= 0 || between.IndexOf(SEMI) >= 0
                    || between.IndexOf(LB) >= 0 || between.IndexOf(LF) >= 0)
                {
                    // 跳过这一段再继续找。**这里必须推进 i**：原地 continue 会让
                    // 循环永远停在这个位置（方法定义本身永远不满足条件），
                    // 实测直接把预览器挂死。
                    i = at + marker.Length;
                    continue;
                }
                int end = text.IndexOf(Q, q + 1);
                if (end < 0) break;
                i = end + 1;
                AddKey(text, q + 1, end, file, used);
            }
        }

        private static void AddKey(string text, int start, int end, string file,
            Dictionary<string, string> used)
        {
            if (end <= start) return;
            string key = text.Substring(start, end - start);
            // 跨行/带空格的多半不是文案键
            if (key.IndexOf(LF) >= 0 || key.IndexOf(CR) >= 0 || key.IndexOf(SP) >= 0) return;
            used[key] = file;
        }

        // 按花括号深度切出每个语言块，形如  ["zh"] = new() { ... }
        private static Dictionary<string, HashSet<string>> ParseLanguageTables(string src)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            int root = src.IndexOf("languageData", StringComparison.Ordinal);
            if (root < 0) return result;
            int i = root;
            while (true)
            {
                int at = src.IndexOf("[" + Q, i, StringComparison.Ordinal);
                if (at < 0) break;
                int close = src.IndexOf(Q + "]", at + 2, StringComparison.Ordinal);
                if (close < 0) break;
                string lang = src.Substring(at + 2, close - at - 2);
                int after = close + 2;
                i = after;
                int eq = src.IndexOf(EQ, after);
                if (eq < 0) break;
                // 等号与键之间必须是空的，且紧跟 new：否则不是语言块
                if (src.Substring(after, eq - after).Trim().Length != 0) continue;
                int newAt = src.IndexOf("new", eq, StringComparison.Ordinal);
                if (newAt < 0) continue;
                int open = src.IndexOf(LB, newAt);
                if (open < 0) continue;
                int depth = 0, j = open;
                for (; j < src.Length; j++)
                {
                    if (src[j] == LB) depth++;
                    else if (src[j] == '}')
                    {
                        depth--;
                        if (depth == 0) { j++; break; }
                    }
                }
                result[lang] = KeySet(src.Substring(open, j - open));
                i = j;
            }
            return result;
        }

        // 取一个语言块里定义的所有键，形如  ["KEY"] = 值
        private static HashSet<string> KeySet(string body)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            int i = 0;
            while (true)
            {
                int at = body.IndexOf("[" + Q, i, StringComparison.Ordinal);
                if (at < 0) break;
                int close = body.IndexOf(Q + "]", at + 2, StringComparison.Ordinal);
                if (close < 0) break;
                string key = body.Substring(at + 2, close - at - 2);
                int eq = body.IndexOf(EQ, close + 2);
                if (eq >= 0) keys.Add(key);
                i = close + 2;
            }
            return keys;
        }
    }
}
