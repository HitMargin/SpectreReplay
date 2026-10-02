// 补丁挂载点检查。
//
// 存在的理由：回放与录制曾经**完全不工作**，根因是补丁挂在
// scrController.UpdateInput 上，而那个方法的第一行是
//
//     if (!RDInput.asyncKeyboard.isActive && !...Left... && !...Right...)
//         return;                                    // scrController.cs:2885
//
// asyncKeyboard.isActive 只在玩家自己去设置里勾了异步输入才为真
// （AsyncInputManager.Update:147）。默认输入下它恒为 false，整个方法体
// 都不执行——**挂在它上面的补丁一帧都不会被调用**，而日志里毫无异常。
//
// 这类 bug 靠看代码看不出来（那行 if 在别人写的文件里，而且"方法被调用
// 了但立刻 return"没有任何症状）。能离线抓住它的办法只有一条：把
// "不能挂 UpdateInput" 写成断言，让它以后每次构建都复检一次。
//
// 与"跑一遍游戏看现象"不同，这条检查是确定性的、秒级的、每次都跑的。
// 它守的不是回放逻辑本身，而是那个会**静默废掉整条链路**的挂载点。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class PatchTargetCheck
    {
        /// <summary>
        /// 已知会被前置 return 挡掉的方法：挂上去等于没挂。
        /// 理由都写在这里，免得下次有人觉得"换个地方挂也一样"又挂一遍。
        /// </summary>
        private static readonly Dictionary<string, string> DeadMethods =
            new Dictionary<string, string>
        {
            { "UpdateInput",
                "scrController.cs:2885 起先判 asyncKeyboard.isActive；"
                + "该值仅在玩家勾选异步输入时为真，默认设置下为 false，"
                + "方法体整段不执行。改挂 PlayerControl_Update。" },
        };

        /// <summary>
        /// 每帧都跑、且不按异步输入开关分叉的泵点。
        /// 录制与回放都应该挂在这些上面。
        /// </summary>
        private static readonly string[] SafePumps =
        {
            "PlayerControl_Update", "Countdown_Update", "Checkpoint_Update",
        };

        /// <summary>
        /// 这个位置是不是落在注释里。
        ///
        /// 只做一次线性扫描：记录到目前为止有没有未闭合的块注释、
        /// 当前行有没有出现过 //。字符串字面量里的 // 不算注释起始，
        /// 但特性参数里不会出现那种写法，可以接受这点简化——
        /// 它只需要"别把注释当成代码"，不需要当词法分析器用。
        /// </summary>
        private static bool IsInComment(string src, int index)
        {
            bool inBlock = false;
            bool inStr = false;
            bool esc = false;
            bool lineComment = false;
            for (int i = 0; i < index && i < src.Length; i++)
            {
                char c = src[i];
                if (c == '\n')
                {
                    inBlock = false;
                    lineComment = false;
                    continue;
                }
                if (inBlock)
                {
                    if (c == '*' && i + 1 < src.Length && src[i + 1] == '/')
                    {
                        inBlock = false;
                        i++;
                    }
                    continue;
                }
                if (lineComment) continue;
                if (!inStr && c == '/' && i + 1 < src.Length && src[i + 1] == '/')
                {
                    lineComment = true;
                    i++;
                    continue;
                }
                if (!inStr && c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    inBlock = true;
                    i++;
                    continue;
                }
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') inStr = !inStr;
            }
            return inBlock || lineComment;
        }

        /// <summary>数前 index 个字符里的换行，用来把偏移换算成行号。</summary>
        private static int CountNewlines(string s, int index)
        {
            int n = 0;
            for (int i = 0; i < index && i < s.Length; i++)
            {
                if (s[i] == '\n') n++;
            }
            return n;
        }

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 补丁挂载点 ──");
            int fail = 0;
            var hits = new List<string>();
            var safeSeen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in Directory.GetFiles(projectRoot, "*.cs",
                SearchOption.AllDirectories))
            {
                if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
                if (file.Contains("tools")) continue;      // 预览器自身不在检查范围
                string src = File.ReadAllText(file);
                string name = Path.GetFileNameWithoutExtension(file);

                // 只取特性上的目标方法名：[GamePatch(typeof(X), "Method", ...)]
                //
                // 必须显式跳过 typeof(...) 那个括号：原先写的是
                //   GamePatch\s*\([^)]*?"(\w+)"
                // 而 [^)] 跨不过 typeof(scrController) 里闭掉的那个 ')'，
                // 结果一条都匹配不上——检查永远是绿的，等于没有。
                // 现在按真实形状匹配：typeof(类型) 之后的第一个字符串字面量。
                foreach (Match m in Regex.Matches(src,
                    @"typeof\s*\(\s*[\w.]+\s*\)\s*,\s*""(\w+)"""))
                {
                    string method = m.Groups[1].Value;
                    // 跳过注释里的字样：文件头的用法示例就写着
                    // [GamePatch(typeof(scrController), "UpdateInput", ...)]，
                    // 而那正好是本检查要禁掉的写法——照字面扫会天天误报。
                    if (IsInComment(src, m.Index)) continue;
                    foreach (var dead in DeadMethods)
                    {
                        if (method != dead.Key) continue;
                        int line = 1 + CountNewlines(src, m.Index);
                        hits.Add($"{name}:{line} 挂在了 {method} 上");
                    }
                    foreach (var s in SafePumps)
                    {
                        if (method == s) { safeSeen.Add(s); break; }
                    }
                }
            }

            foreach (var h in hits)
            {
                Console.WriteLine($"    ✗ {h}  ← 前置 return 会让它一帧都不执行");
                foreach (var dead in DeadMethods)
                {
                    Console.WriteLine($"      {dead.Key}：{dead.Value}");
                }
                fail++;
            }

            // 反向检查：录制/回放一个泵点都没挂，说明整体搬走了或改错了。
            // 这种情况下正向检查会"全绿"，却什么都没守住——必须显式报。
            if (fail == 0 && safeSeen.Count == 0)
            {
                Console.WriteLine(
                    "    ✗ 一个安全泵点都没挂上（应至少挂 PlayerControl_Update）"
                    + "——回放与录制都会没有触发点");
                fail++;
            }

            if (fail == 0)
            {
                Console.WriteLine($"    ✓ 没有补丁挂在会被前置 return 挡掉的方法上"
                    + $"（已确认使用：{string.Join(" / ", SafePumps)})");
            }
            return fail;
        }
    }
}