// 补丁框架的异常兜底（偏差 10）。
//
// 要验的是三件事，且都是"能不能在真机上无声失败"这一类的：
//
// ① Guard 真的被挂上去了，且**四种 kind 都不靠 else 兜底**
// ② Guard 只吞**我们自己补丁体**抛的异常（靠栈帧比对），
//    认不出来时原样上抛——绝不能把游戏自己的异常吞掉
// ③ DisableAfterExceptions 默认是 0（从不已用）
//
// 判据落在**源码的真实控制流**上，不是"有没有出现过 Guard 这个词"。
// 第一版就是这么写的，结果把 Guard 注释里出现的词也算进去了。
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class PatchGuardCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 补丁异常兜底 ──");
            string file = Path.Combine(projectRoot, "Patch", "PatchLoader.cs");
            if (!File.Exists(file))
            {
                Console.WriteLine("    ✗ ① 找不到 Patch/PatchLoader.cs");
                return 1;
            }
            string src = StripComments(File.ReadAllText(file));
            int fail = 0;
            fail += GuardIsWired(src);
            fail += OnlyOurPatchesAreSwallowed(src);
            fail += AutoDisableIsOffByDefault(src);
            return fail;
        }

        /// <summary>
        /// Guard 必须真的挂到 Prefix/Postfix 上，且四种 kind 显式分派。
        ///
        /// 判据是 **Patch(target, …) 的调用形状**，不是"Guard 出现在文件里"——
        /// 只查名字的话，Guard 的方法定义本身就会让检查通过。
        ///
        /// 四种 kind 都要**显式**判：`else` 兜底会让新增的 kind 静默
        /// 落到 Finalizer 那一支，而补丁照样"挂上了"、日志无异常。
        /// </summary>
        private static int GuardIsWired(string src)
        {
            if (!Regex.IsMatch(src, @"prefix:\s*hm,\s*finalizer:\s*guard"))
            {
                Console.WriteLine("    ✗ ① Prefix 挂载时没有把 Guard 接上"
                    + "（补丁体抛异常会进游戏栈，Harmony 连续失败后还会永久跳过它）");
                return 1;
            }
            if (!Regex.IsMatch(src, @"postfix:\s*hm,\s*finalizer:\s*guard"))
            {
                Console.WriteLine("    ✗ ① Postfix 挂载时没有把 Guard 接上");
                return 1;
            }
            if (!src.Contains("transpiler: hm"))
            {
                Console.WriteLine("    ✗ ① Transpiler 没有显式分派"
                    + "（会被静默挂成 Finalizer 或 Postfix）");
                return 1;
            }
            // Transpiler 与 Finalizer 都不该挂 Guard，各有理由
            if (!Regex.IsMatch(src,
                @"Kind\s*!=\s*PatchKind\.Transpiler[\s\S]{0,80}Kind\s*!=\s*PatchKind\.Finalizer"))
            {
                Console.WriteLine("    ✗ ① Guard 的挂载条件没有同时排除 Transpiler 与 Finalizer："
                    + "Transpiler 的异常发生在应用期，Finalizer 自己是兜底");
                return 1;
            }
            Console.WriteLine("    ✓ ① Prefix/Postfix 挂上共享 Guard；"
                + "四种 kind 显式分派，Transpiler 与 Finalizer 不挂");
            return 0;
        }

        /// <summary>
        /// Guard **只**吞我们补丁体抛的异常。
        ///
        /// 这是最关键的一条：吞错了就是把游戏的 bug 变成静默——
        /// 玩法异常（行星不前进、判定乱）不再报错，玩家只会觉得"这模组有问题"。
        ///
        /// 判据是那三道分支都必须还在：
        ///   · <c>__exception == null</c> → 直接放行（Harmony 无异常时也调 finalizer）
        ///   · 栈帧认不出来 → return __exception
        ///   · 栈帧不在 GuardedMethods 里 → return __exception
        ///
        /// 后两条**都不能省**。省掉任何一条都会让 Guard 变成
        /// "游戏方法体里任何异常都消失"——那比不加 Guard 坏得多。
        /// </summary>
        private static int OnlyOurPatchesAreSwallowed(string src)
        {
            int guard = src.IndexOf("private static Exception Guard(");
            if (guard < 0)
            {
                Console.WriteLine("    ✗ ② 找不到 Guard 实现");
                return 1;
            }
            int end = src.IndexOf("private static Spec FindOwningSpec(");
            if (end < 0 || end < guard)
            {
                Console.WriteLine("    ✗ ② 找不到 Guard 的方法体边界");
                return 1;
            }
            string body = src.Substring(guard, end - guard);

            // 兜底异常时也必须吞掉，否则 Guard 自己会抛
            if (!Regex.IsMatch(body, @"catch[\s\S]*?return\s+null"))
            {
                Console.WriteLine("    ✗ ② Guard 自己的 catch 没有吞掉异常："
                    + "兜底层一旦抛出去就变成放大器");
                return 1;
            }
            bool nullIsPassthrough = Regex.IsMatch(body,
                @"if\s*\(\s*__exception\s*==\s*null\s*\)\s*\{[^}]*return\s+null");
            bool unknownRethrow = Regex.IsMatch(body,
                @"if\s*\(\s*frameMethod\s*==\s*null\s*\)\s*\{[^}]*return\s+__exception");
            bool notOursRethrow = Regex.IsMatch(body,
                @"if\s*\(\s*!GuardedMethods\.Contains[\s\S]{0,140}return\s+__exception");
            if (!nullIsPassthrough || !unknownRethrow || !notOursRethrow)
            {
                Console.WriteLine("    ✗ ② Guard 的分支不全："
                    + "__exception 为 null 直接放行(" + (nullIsPassthrough ? "有" : "缺")
                    + ") / 认不出栈帧就上抛(" + (unknownRethrow ? "有" : "缺")
                    + ") / 不在 GuardedMethods 就上抛(" + (notOursRethrow ? "有" : "缺")
                    + ")；缺任何一个都会把游戏自己的异常静默吞掉");
                return 1;
            }
            Console.WriteLine("    ✓ ② Guard 只吞自己补丁体的异常："
                + "认不出 / 不是我们的，一律原样上抛");
            return 0;
        }

        /// <summary>
        /// <c>DisableAfterExceptions</c> 默认 0（从不已用）。
        ///
        /// 给它一个非 0 默认值听起来更"安全"，实际很糟：**一次偶发异常就会
        /// 永久关掉某项功能**，而用户界面上看不出任何区别——
        /// 开关还亮着，功能没了，重新开模组才恢复。
        /// "宁缺毋滥"只该由具体补丁显式选择，不是全局默认。
        /// </summary>
        private static int AutoDisableIsOffByDefault(string src)
        {
            // 判据先认"字段存在"（两种形态都认），再单独判"有没有默认值"。
            //
            // 第一版只认 `DisableAfterExceptions;`，于是把它改成 `= 5;` 之后
            // 报的是「找不到字段」——字段明明在。**报错理由比报错本身更费时间**：
            // 照着"字段不存在"去查声明，怎么也查不出来。
            var m = Regex.Match(src,
                @"internal\s+int\s+DisableAfterExceptions\s*(=\s*[^;]+)?;");
            if (!m.Success)
            {
                Console.WriteLine("    ✗ ③ 特性上找不到 DisableAfterExceptions 字段"
                    + "（应为 internal int DisableAfterExceptions;，且不带初始化）");
                return 1;
            }
            if (m.Groups[1].Success)
            {
                Console.WriteLine("    ✗ ③ DisableAfterExceptions 默认 "
                    + m.Groups[1].Value.TrimStart('=', ' ')
                    + "：一次偶发异常就会永久关掉该补丁，"
                    + "而界面上看不出区别（开关还亮着，功能没了）");
                return 1;
            }
            // 判据本身必须兜 0：默认 0 若被当成"阈值 0"会对每个补丁都触发
            if (!src.Contains("DisableAfterExceptions ?? 0) > 0"))
            {
                Console.WriteLine("    ✗ ③ 异常自禁用的判据没有兜 0："
                    + "默认 0 会被当成「阈值 0」而对每个补丁都触发");
                return 1;
            }
            Console.WriteLine("    ✓ ③ DisableAfterExceptions 默认 0（从不已用），"
                + "只有显式声明的补丁才会自我了断");
            return 0;
        }

        private static string StripComments(string src)
        {
            var sb = new StringBuilder(src.Length);
            bool inBlock = false, inStr = false, inChar = false, esc = false;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (inBlock)
                {
                    if (c == '*' && i + 1 < src.Length && src[i + 1] == '/')
                    { inBlock = false; i++; }
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
                { inBlock = true; i++; continue; }
                if (esc) { esc = false; sb.Append(c); continue; }
                if (c == '\\') { esc = true; sb.Append(c); continue; }
                if (c == '"') inStr = !inStr;
                else if (c == '\'') inChar = !inChar;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}