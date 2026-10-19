// 硬编码文案检查：用户能在界面上看到的中文，必须走 Loc 表。
//
// 为什么只查"会被看到的"：代码里中文最多的是注释，它们当然不该进文案表。
// 但只要有一处**用户可见**的中文被写死，换成英文语言的玩家就会在英文
// 界面里看到夹进来的中文——而且这种问题在中文环境下永远看不出来，
// 等于是个不会自己暴露的缺陷。
//
// 做法：先剥掉行注释与块注释，再找剩下的字符串字面量里的中日韩字符。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class HardTextCheck
    {
        /// <summary>不进文案表也不该被查的文件。</summary>
        private static readonly string[] Skip =
        {
            "Loc.cs",                       // 选择逻辑（数据在 LocData.cs）
            "LocData.cs",                   // 14 语言 × 325 键的数据表，由 _gen_locdata.py 生成
            "LocalizationManager.cs",       // 移植 Spectre 的 14 语言文案表，同 Loc.cs 性质
        };

        /// <summary>
        /// 只进诊断日志、玩家在界面上看不到的字符串。
        ///
        /// 这些没法靠语法判定：它们跟界面文案在代码里长得一模一样，只是最后
        /// 流进了 Player.log。与其让检查去猜数据流（猜错就是误报，误报多了
        /// 整条检查就没人信了），不如显式列出来并写清理由——多一条要说明白。
        /// </summary>
        private static readonly Dictionary<string, string> DiagnosticOnly =
            new Dictionary<string, string>
        {
            { "AudioRecorder.cs",
                "[Header(…)] 是 Unity Inspector 属性面板标注，玩家在游戏内看不到" },
            { "PatchLoader.cs",
                "Failures 列表只流向 Debug.LogWarning，玩家在界面上看不到" },
            { "UiFonts.cs",
                "字体来源/告警只打 Player.log，界面上的提示另有文案" },
            // Main.cs 里的中文只出现在 Load 的失败分支与"已加载"摘要里，
            // 两者都只走 Debug.Log / mod.Logger。入口抛异常时 UMM 只记一行
            // 警告就继续，不把这句打出来的话，模组在现象上就是"什么都没发生"。
            { "Main.cs",
                "入口失败诊断与加载摘要只写 Player.log / UMM 窗口，不在界面上" },
            // 兼容层的降级告警与版本摘要同样只进日志。刻意按**类型**放行而不是
            // 按文件名：GameCompat 里有 5 个通道，每个都只在失败时 WarnOnce
            // 打一行，写成 5 处 Loc 键纯属噪音（这些字符串玩家永远看不到，
            // 只有排查者看得到，而排查者看的就是日志）。
            { "GameCompat.cs",
                "兼容层的 WarnOnce 告警与版本摘要只写 Player.log / UMM 窗口" },
            // PlaybackClamps 的挡命中框告警走的是 Player.log：这条在正常游玩
            // 里**不该**出现，出现即说明有一关真的用到了激光/尖刺，值得查录制。
            // 玩家在界面上看不到，也不需要看到——它不是他能处理的。
            { "SpectrePatchHub.cs",
                "补丁链日志（AsyncLog）只写 Player.log / UMM 窗口，玩家在界面上看不到" },
            { "PlaybackClamps.cs",
                "回放期挡下激光/尖刺等 hitbox 死亡的告警只写 Player.log" },
            // RenderClock transpiler 的失败/让位说明走 Debug.Log/Player.log：
            // 它们只在"补丁挂载失败"时出现，而挂载失败本身已经由设置页顶部的
            // 「补丁未挂载」徽章 + SpectrePatchHub 的汇总行告知玩家（那些走 Loc）。
            // 这里的细节是给排查者看的，玩家看不到也不需要看到。
            { "VideoRenderClockPatches.cs",
                "transpiler 的失败/让位诊断只写 Player.log，界面提示另有 Loc 文案" },
        };

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 硬编码文案 ──");
            int fail = 0;
            int scanned = 0;
            var shownReason = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
                string name = Path.GetFileName(file);
                bool skip = false;
                foreach (var s in Skip) if (name == s) skip = true;
                if (skip) continue;
                // 预览器自己的桩件不在被检查的目录里，这里也只管主项目
                if (file.Contains("tools")) continue;

                var stripped = StripComments(File.ReadAllText(file));
                scanned++;
                // 跨行跟踪：Debug.Log( 的参数可能换行写，字符串会落在续行上。
                // 只看单行会把这些全误报成"界面文案"。
                int line = 1, depth = 0, logDepth = -1;
                for (int i = 0; i < stripped.Length; i++)
                {
                    char c = stripped[i];
                    if (c == '\n') { line++; continue; }
                    if (c == '(')
                    {
                        depth++;
                        // 刚跨过 Debug.Log 的话记住当时的括号层
                        if (logDepth < 0 && EndsWithLog(stripped, i))
                        {
                            logDepth = depth;
                        }
                        continue;
                    }
                    if (c == ')')
                    {
                        if (logDepth >= 0 && depth <= logDepth) logDepth = -1;
                        depth--;
                        continue;
                    }
                    if (c != '"') continue;
                    if (logDepth >= 0) { i = SkipString(stripped, i); continue; }
                    int end = SkipString(stripped, i);
                    string lit = stripped.Substring(i + 1, end - i - 1);
                    i = end;
                    if (lit.Length < 2) continue;
                    if (!Regex.IsMatch(lit, @"[\u4e00-\u9fff]")) continue;
                    if (DiagnosticOnly.ContainsKey(name))
                    {
                        // 每轮最多提示一次理由，别刷屏
                        if (!shownReason.Contains(name))
                        {
                            shownReason.Add(name);
                            Console.WriteLine($"    · {name} 整文件放行："
                                + DiagnosticOnly[name]);
                        }
                        continue;
                    }
                    Console.WriteLine(
                        $"    ✗ {name}:{line}  «{Trim(lit)}»"
                        + "  ← 玩家会在界面上看到，应该走 Loc.T");
                    fail++;
                }
            }
            if (fail == 0)
            {
                Console.WriteLine($"    ✓ 扫了 {scanned} 个文件，没有被写死的用户可见中文");
            }
            fail += LeakedLiterals(projectRoot);
            return fail;
        }

        /// <summary>
        /// 这一个左括号是不是 Debug.Log( 的那个——即前面紧邻着 Debug.Log。
        /// 靠"往前扫回去"判断，而不是正则整行匹配：正则跨行也会把
        /// 前面第 N 行的调用算到这一行上来。
        /// </summary>
        private static bool EndsWithLog(string s, int parenIndex)
        {
            int i = parenIndex - 1;
            while (i >= 0 && char.IsWhiteSpace(s[i])) i--;
            int end = i + 1;
            while (i >= 0 && (char.IsLetterOrDigit(s[i]) || s[i] == '.')) i--;
            string word = s.Substring(i + 1, end - i - 1);
            // 代码里常写全限定名 UnityEngine.Debug.LogWarning(，取的是整段，
            // 所以要比后缀而不是全等。
            return word.EndsWith("Debug.Log") || word.EndsWith("Debug.LogWarning")
                || word.EndsWith("Debug.LogError")
                // UM 的 mod.Logger.Log 也只写日志文件
                || word.EndsWith("Logger.Log") || word.EndsWith("Logger.Warning")
                || word.EndsWith("Logger.Error");
        }

        /// <summary>返回字符串字面量的结束引号位置。</summary>
        private static int SkipString(string s, int openQuote)
        {
            for (int i = openQuote + 1; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '"') return i;
                if (s[i] == '\n') return i;   // 没闭合，放弃
            }
            return s.Length - 1;
        }

        private static string Trim(string s)
            => s.Length <= 34 ? s : s.Substring(0, 32) + "…";

        /// <summary>
        /// 文案表文件（<see cref="Skip"/> 里那两个）在主检查里是**整文件豁免**的——
        /// 它们的正文本来就是一整片中文字符串。豁免的代价是：这两个文件里**任何**
        /// 中文都被放过，包括压根不在文案表里、会被界面直接显示的那种。
        ///
        /// 真实案例：Loc.cs 里的 `internal const string Hint = "F9 开关界面 · ESC 关闭"`
        /// 是头部副标题，英文界面下直接显示中文；而整文件豁免让它永远不会变红，
        /// 连“玩家在英文界面看到中文”这个主检查存在的唯一理由都绕过去了。
        ///
        /// 判据：中文串必须落在文案表条目的位置上——
        ///     { "键", "值" }   或   ["键"] = "值"
        /// 其余位置（字段初值、方法实参、拼接等）一律算泄漏。
        /// </summary>
        private static int LeakedLiterals(string projectRoot)
        {
            Console.WriteLine("  ── 文案表文件里的漏网字符串 ──");
            int fail = 0, scanned = 0, entries = 0;
            foreach (string name in Skip)
            {
                string found = null;
                foreach (string f in Directory.GetFiles(projectRoot, name, SearchOption.AllDirectories))
                {
                    if (f.Contains(@"\obj\") || f.Contains(@"\bin\") || f.Contains("tools")) continue;
                    found = f; break;
                }
                if (found == null)
                {
                    Console.WriteLine("    ✗ 找不到文案表文件 " + name);
                    fail++;
                    continue;
                }
                scanned++;
                string[] lines = StripComments(File.ReadAllText(found)).Split('\n');
                for (int li = 0; li < lines.Length; li++)
                {
                    string line = lines[li];
                    int idx = 0;
                    while (true)
                    {
                        int q = line.IndexOf('"', idx);
                        if (q < 0) break;
                        int end = SkipString(line, q);
                        string lit = line.Substring(q + 1, Math.Max(0, end - q - 1));
                        idx = end + 1;
                        if (lit.Length < 2 || !Regex.IsMatch(lit, @"[\u4e00-\u9fff]")) continue;
                        // 往回看这个字面量是不是表项的值：
                        //   { "键", "值"    或    ["键"] = "值"
                        string prefix = line.Substring(0, q);
                        if (Regex.IsMatch(prefix, @"(\{\s*""[^""]*""\s*,\s*|\[""[^""]*""\]\s*=\s*)$"))
                        {
                            entries++;
                            continue;
                        }
                        Console.WriteLine($"    ✗ {Path.GetFileName(found)}:{li + 1}  «{Trim(lit)}»"
                            + "  ← 含中文却不在文案表里，会直接显示给玩家");
                        fail++;
                    }
                }
            }
            if (fail == 0)
            {
                Console.WriteLine($"    ✓ {scanned} 个文案表文件、{entries} 条表项，没有漏网的中文字符串");
            }
            return fail;
        }
        /// <summary>去掉行注释与块注释，保留行数不变便于报行号。</summary>
        private static string StripComments(string src)
        {
            var sb = new System.Text.StringBuilder(src.Length);
            bool inBlock = false;
            bool inStr = false, inChar = false, esc = false;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (inBlock)
                {
                    if (c == '*' && i + 1 < src.Length && src[i + 1] == '/')
                    {
                        inBlock = false; i++;
                    }
                    else if (c == '\n') sb.Append(c);
                    continue;
                }
                if (!inStr && !inChar && c == '/' && i + 1 < src.Length
                    && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != '\n') i++;
                    sb.Append('\n');
                    continue;
                }
                if (!inStr && !inChar && c == '/' && i + 1 < src.Length
                    && src[i + 1] == '*')
                {
                    inBlock = true; i++;
                    continue;
                }
                if (esc) { esc = false; sb.Append(c); continue; }
                if (c == '\\') { esc = true; sb.Append(c); continue; }
                // **字符串内的单引号不是字符字面量的定界符。**
                // 原实现把 inStr 与 inChar 当成两个互不相关的开关，于是一行
                //   Debug.Log("[Spectre] replay hit the game's UpdateHoldKeys ...")
                // 里的撇号就把 inChar 置真——此后整个文件的 // 与 /* 都因
                // `!inStr && !inChar` 守卫而被跳过，**注释不再被剥掉**，
                // 注释里带 ASCII 引号的中文短语全被当成界面文案误报
                // （实测 Patch_Playback.cs:59 的「还没开始录」就是这么来的）。
                // 撇号在英文日志文案里极其常见，所以这不是边缘情况：
                // 它让硬编码中文检查对含英文日志的文件整段失效。
                if (c == '"') { inStr = !inStr; sb.Append(c); continue; }
                if (inStr) { sb.Append(c); continue; }
                if (c == '\'') inChar = !inChar;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
