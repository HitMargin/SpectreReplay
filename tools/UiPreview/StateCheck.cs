// 状态判据对齐（偏差「状态码 3/5/6」）。
//
// 出发点是一个魔数：保存/取消快捷键里写着 `state != 3 && state != 5 && state != 6`，
// 注释说那是"游玩中/暂停/结算"。真程序集里的 States 是：
//
//     0=None 1=Start 2=Countdown 3=Checkpoint
//     4=PlayerControl 5=Fail 6=Fail2 7=Won
//
// 也就是说那三个数是**存档点 / 失败 / 失败二**，和注释里说的完全是两回事，
// 而**真正的结算状态 7 被漏掉了**。玩家在游玩中按保存键没反应，
// 而这恰恰是它存在的理由（练到一半的短局存不下来）。
//
// 判据直接从**真程序集**读枚举值，所以游戏改枚举就会变红——
// 源码级断言只能确认"写了枚举名"，确认不了那个名字指的是哪几个状态。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class StateCheck
    {
        /// <summary>
        /// 快捷键只在**结算**状态响应：Fail / Fail2 / Won。
        ///
        /// 照 Spectre 的 Patch_Misc.cs:41 与 :57（保存与取消两处都用
        /// `state != 6 && state != 5 && state != 7`）。
        ///
        /// 为什么不包含 PlayerControl：回放数据必须是**跑完的一整局**，
        /// 存到一半的数据重放出来是断的。这个取舍是 Spectre 定的。
        /// </summary>
        private static readonly string[] Required =
        {
            "States.Fail", "States.Fail2", "States.Won",
        };

        /// <summary>绝不能出现的：这几个是游玩中/存档点/主菜单之类。</summary>
        private static readonly string[] Forbidden =
        {
            "States.PlayerControl", "States.Checkpoint",
            "States.Start", "States.Countdown", "States.None",
        };

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 结算状态判据 ──");
            int fail = 0;
            fail += HotkeyUsesEnumNames(projectRoot);
            fail += EnumValuesFromRealAssembly();
            return fail;
        }

        private static int HotkeyUsesEnumNames(string projectRoot)
        {
            string file = Path.Combine(projectRoot, "Features", "Recording",
                "Patch_Recording.cs");
            if (!File.Exists(file))
            {
                Console.WriteLine("    ✗ ① 找不到 Patch_Recording.cs");
                return 1;
            }
            string src = StripComments(File.ReadAllText(file));
            int at = src.IndexOf("HotkeyPostfix");
            if (at < 0)
            {
                Console.WriteLine("    ✗ ① 找不到 HotkeyPostfix");
                return 1;
            }
            string body = src.Substring(at, Math.Min(1400, src.Length - at));

            foreach (string name in Required)
            {
                if (!body.Contains(name))
                {
                    Console.WriteLine("    ✗ ① 快捷键没判 " + name
                        + "（结算状态之一）——玩家按保存键会没反应");
                    return 1;
                }
            }
            foreach (string name in Forbidden)
            {
                if (body.Contains(name))
                {
                    Console.WriteLine("    ✗ ① 快捷键不该判 " + name
                        + "：回放数据必须是跑完的一整局，"
                        + "存到一半的数据重放出来是断的");
                    return 1;
                }
            }
            // 魔数一律不许出现在这一段里
            var magic = Regex.Match(body, @"state\s*[!=]=\s*\d");
            if (magic.Success)
            {
                Console.WriteLine("    ✗ ① 快捷键里仍有状态魔数：" + magic.Value
                    + "——枚举改版时会静默指向别的状态，"
                    + "而注释还写着旧的名字，排查时两边都对不上");
                return 1;
            }
            // 必须是枚举比较，不是先转 int 再比
            if (!Regex.IsMatch(body,
                @"var\s+state\s*=\s*\(States\)[^;]*GetState\(\)"))
            {
                Console.WriteLine("    ✗ ① state 不是按 States 取的："
                    + "先转 int 再比，等于把枚举名又变回魔数");
                return 1;
            }
            Console.WriteLine("    ✓ ① 快捷键按 States 枚举名判 Fail/Fail2/Won，"
                + "且这一段里没有任何状态魔数");
            return 0;
        }

        /// <summary>
        /// 枚举值来自**真程序集**，且必须与本断言写死的一致。
        ///
        /// 为什么还要钉住具体数字：上面 ① 验的是"代码写了 States.Fail"，
        /// 验不了 `States.Fail` 此刻是不是 5。游戏改枚举的话，
        /// 这里会红而 ① 仍然绿——那时就需要重新确认哪个状态是结算。
        ///
        /// 这也是本项目唯一能"离线核对游戏枚举"的地方：
        /// Enum.GetName 只需要元数据，不需要加载 UnityEngine。
        /// </summary>
        private static int EnumValuesFromRealAssembly()
        {
            string dll = FindGameAssembly();
            if (dll == null)
            {
                Console.WriteLine("    ✗ ② 找不到 Assembly-CSharp.dll，无法核对枚举值");
                return 1;
            }
            Assembly game = Assembly.LoadFrom(dll);
            Type states = game.GetType("States", false);
            if (states == null)
            {
                Console.WriteLine("    ✗ ② 游戏程序集里没有 States 枚举");
                return 1;
            }
            var want = new (string, int)[]
            {
                ("None", 0), ("Start", 1), ("Countdown", 2), ("Checkpoint", 3),
                ("PlayerControl", 4), ("Fail", 5), ("Fail2", 6), ("Won", 7),
            };
            var sb = new System.Text.StringBuilder();
            int bad = 0;
            foreach (var (name, value) in want)
            {
                string real = Enum.GetName(states, value);
                bool ok = real == name;
                sb.Append(real ?? "?").Append('=').Append(value)
                  .Append(ok ? "" : "(期望 " + name + ")").Append(' ');
                if (!ok) bad++;
            }
            Console.WriteLine($"    {(bad == 0 ? "✓" : "✗")} ② 真程序集的 States 取值"
                + $"  → {sb}".TrimEnd());
            if (bad != 0)
            {
                Console.WriteLine("      ← 枚举改版了：结算状态不再等于 7，"
                    + "① 里那几个名字还指的是旧的状态，得重新确认");
                return 1;
            }
            return 0;
        }

        private static string FindGameAssembly()
        {
            var candidates = new List<string>
            {
                Path.GetFullPath(@"..\..\..\adofai-libs\Assembly-CSharp.dll"),
                Path.GetFullPath(@"..\..\adofai-libs\Assembly-CSharp.dll"),
            };
            var dir = new DirectoryInfo(Path.GetFullPath(".."));
            for (int i = 0; i < 5 && dir != null; i++)
            {
                candidates.Add(
                    Path.Combine(dir.FullName, "adofai-libs", "Assembly-CSharp.dll"));
                dir = dir.Parent;
            }
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
        }

        private static string StripComments(string src)
        {
            var sb = new System.Text.StringBuilder(src.Length);
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