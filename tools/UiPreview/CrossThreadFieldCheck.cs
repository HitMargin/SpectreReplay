// 跨线程字段可见性：后台线程写、主线程读的字段必须是 volatile。
//
// 为什么值得一条断言：这类缺陷**编译通过、跑起来也基本正常**——JIT 在 x86
// 的弱内存模型下常常"碰巧"让你看到新值。它只在真机 CoreCount 高 / Release
// JIT 重排时表现为"偶发卡住 / 状态不更新"，极难复现，属于典型的"看着在工作"。
//
// 实测抓到一处（2026-10-06）：VideoEncoder.pumpFailure 是普通字段，泵线程写、
// 主线程在 Submit 里读。见 ReflectionTargetCheck 之外本检查盯字段声明本身。
//
// 判据（源码形状 + 可验证）：
//   ① 已知的跨线程字段必须带 volatile（列名 + 所在文件）
//   ② 反向：不许出现"形似跨线程但漏了 volatile"的新增字段——这个不自动化，
//      靠上面那份清单随代码演进维护；清单本身在源码里，改起来显式。
using System;
using System.IO;
using System.Linq;

namespace UiPreview
{
    internal static class CrossThreadFieldCheck
    {
        // 已知跨线程字段：文件 → 必须带 volatile 的字段名
        private static readonly (string file, string field)[] MustBeVolatile =
        {
            (@"State\AudioRecorder.cs",        "isRecording"),
            (@"State\AudioRecorder.cs",        "recordingClip"),
            (@"Features\VideoRender\Pipeline\VideoConverter.cs", "Running"),
            (@"Features\VideoRender\Pipeline\VideoConverter.cs", "Total"),
            (@"Features\VideoRender\Pipeline\VideoConverter.cs", "Done"),
            (@"Features\VideoRender\Pipeline\VideoConverter.cs", "Converted"),
            (@"Features\VideoRender\Pipeline\VideoConverter.cs", "Skipped"),
            (@"Features\VideoRender\Pipeline\VideoConverter.cs", "Failed"),
            (@"Features\VideoRender\Pipeline\VideoEncoder.cs",    "pumpFailure"),
        };

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 跨线程字段可见性 ──");
            int fail = 0;
            foreach (var (rel, field) in MustBeVolatile)
            {
                string ok = Path.Combine(projectRoot, rel);
                if (!File.Exists(ok))
                {
                    Console.WriteLine("    ✗ 找不到 " + rel);
                    fail++;
                    continue;
                }
                // 剥注释后找声明行。**正则不能用 `^\s*(?:private|internal|...)` 这种
                // 带 alternation 的行首形式**——实测在这种形状下 [regex]::Match
                // 对明显该命中的串返回 false（PowerShell 正则解析的坑，原因未深究）。
                // 改成"访问修饰符 + 捕获组 + 字段名"的直接形式，实测稳定。
                string code = SrcUtil.Clean(File.ReadAllText(ok));
                var decl = System.Text.RegularExpressions.Regex.Matches(code,
                    @"(?:private|internal|public|protected)\s+(?<mod>[\w\.\[\]]+)"
                    + @"\s+[\w\.\[\]<>]+\s+" + System.Text.RegularExpressions.Regex.Escape(field)
                    + @"\s*[;=]");
                if (decl.Count == 0)
                {
                    // **两种可能，报错必须都说**：字段真被改名/删了，或者只是漏了
                    // volatile 导致这行匹配不上。实测漏 volatile 时正则是匹配不上的
                    // （少一个类型 token），若只报"字段不见了"，排查的人会去翻改名
                    // 记录，完全指不到真正的原因——这就是"报错方向错了比报错本身
                    // 更费时间"。（MUT-X1/X2 实测走到的正是这条分支。）
                    Console.WriteLine("    ✗ " + rel + " 的 " + field
                        + " 声明匹配不上：可能被改名/删掉，也可能漏了 volatile"
                        + "（本检查要求 `private volatile <类型> " + field + "` 这一形状）");
                    fail++;
                    continue;
                }
                bool isVolatile = false;
                foreach (System.Text.RegularExpressions.Match m in decl)
                {
                    if (m.Groups["mod"].Value == "volatile") isVolatile = true;
                }
                if (!isVolatile)
                {
                    Console.WriteLine("    ✗ " + rel + " 的 " + field
                        + " 不是 volatile —— 后台线程写、主线程读，JIT 重排会让主线程读到旧值");
                    fail++;
                }
            }
            if (fail == 0)
                Console.WriteLine("    ✓ " + MustBeVolatile.Length + " 个跨线程字段都带 volatile");
            return fail;
        }
    }
}
