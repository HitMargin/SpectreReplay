// Load() 的启动行必须存在、且在 try 之前。
//
// 为什么这是一条承重断言：项目铁律⑤依赖这一行判断"模组到底有没有被加载"。
// 历史上整整 24 轮偏差核对全白做，真实原因就是 `Player.log` 里只有
// `[com.spectre.replay] To skip (disabled).`、没有我们的启动行。
// 少了这一行 → "Load 抛异常"与"Load 根本没跑"在日志上长得一模一样，
// 而这两件事的处理方式完全相反。
//
// 判据（源码形状，两条都要）：
//   ① 第一句可执行语句必须是 Debug.Log（含 "Load()" 字样）
//   ② 它必须在 try 之前——放在 try 里的话，异常路径下它就没了，
//      而那正是最需要它的时刻
using System;
using System.IO;

namespace UiPreview
{
    internal static class LoadMarkerCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── Load() 启动行 ──");
            string ok = Path.Combine(projectRoot, "Main.cs");
            if (!File.Exists(ok))
            {
                Console.WriteLine("    ✗ 找不到 Main.cs");
                return 1;
            }
            // **必须剥注释**：判据要求"第一句可执行语句是 Debug.Log"，而说明
            // 为什么这么写的注释里必然逐字出现 Debug.Log 与 try。不剥就是常红。
            string code = SrcUtil.Clean(File.ReadAllText(ok));
            int fail = 0;

            int loadAt = code.IndexOf("void Load(", StringComparison.Ordinal);
            if (loadAt < 0)
            {
                Console.WriteLine("    ✗ 找不到 Load( 方法（入口签名变了？info.json 的 EntryMethod 也要跟着改）");
                return 1;
            }
            int brace = code.IndexOf('{', loadAt);
            int tryAt = code.IndexOf("try", brace, StringComparison.Ordinal);
            if (brace < 0 || tryAt < 0)
            {
                Console.WriteLine("    ✗ Load 方法体里找不到 try（形状变了？）");
                return 1;
            }

            // 取 { 与 try 之间的代码，剥空行后应只剩一句 Debug.Log
            string pre = code.Substring(brace + 1, tryAt - brace - 1).Trim();
            bool isLog = pre.Contains("Debug.Log", StringComparison.Ordinal)
                        && pre.Contains("Load()", StringComparison.Ordinal);
            Console.WriteLine($"    {(isLog ? "✓" : "✗")} ① try 之前有 Debug.Log 启动行"
                + (isLog ? "" : $"（实际是: {Short(pre)}）"));
            if (!isLog) fail++;

            // 它必须真的是"第一句"——中间不该夹着别的语句。
            // 注意判据：`Debug.Log` 前面带限定前缀（UnityEngine.Debug.Log）是正常的，
            // 所以要看的是"Debug.Log 之前有没有**别的语句结束符**"，而不是
            // "有没有字符"。第一版判 `before.Length == 0` 把 `UnityEngine.`
            // 也算成"前面有东西"，自己误报。
            int logAt = pre.IndexOf("Debug.Log", StringComparison.Ordinal);
            string before = logAt <= 0 ? "" : pre.Substring(0, logAt).Trim().TrimEnd('{', ' ', '\t');
            bool first = !before.Contains(';');
            Console.WriteLine($"    {(first ? "✓" : "✗")} ② 启动行是 try 前的第一句"
                + (first ? "" : $"（它前面还有语句: {Short(before)}）"));
            if (!first) fail++;

            if (fail == 0)
                Console.WriteLine("    ✓ Load() 一进方法体就留痕，异常路径也带得到");
            return fail;
        }

        private static string Short(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
        }
    }
}
