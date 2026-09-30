// 回放文件往返测试：写出来再读回来，必须一模一样。
//
// 数据格式坏掉的话，录制好的回放读不回来、离线渲染也跟着废——而这种
// 缺陷**只在真机上读到文件时才暴露**，离屏预览完全看不见。
// ReplayData.cs 只依赖 System，不碰引擎，所以能直接链接真实源码来跑。
//
// 这一轮就是靠它挖出来的：Write() 写 MarginTimes 时**不写自己的计数**，
// 而 Read() 按头部的 marginKinds 循环。两个集合项数一般不同，
// 读回来必然错位。
using System;
using System.Collections.Generic;
using System.IO;
using SpectreReplay.Features.Recording;

namespace UiPreview
{
    internal static class ReplayCheck
    {
        internal static int Run()
        {
            Console.WriteLine("  ── 回放文件往返 ──");
            int fail = 0;
            string dir = Path.Combine(Path.GetTempPath(), "srp_check");
            Directory.CreateDirectory(dir);

            fail += RoundTrip("① 典型的两局", dir, "typical", Sample(3, 2));
            fail += RoundTrip("② Margins 与 MarginTimes 项数不同", dir, "mismatch",
                Sample(4, 1));
            fail += RoundTrip("③ 空回放", dir, "empty", Sample(0, 0));
            fail += RoundTrip("④ 单个判定点", dir, "one", Sample(1, 1));
            fail += RejectsGarbage(dir);
            return fail;
        }

        private static ReplayData Sample(int marginKinds, int timeKinds)
        {
            var d = new ReplayData
            {
                SongId = "1a2b3c4d",
                SongName = "测试曲目",
                DifficultyId = "hard",
                DifficultyName = "困难",
                Mapper = "someone",
                Length = 187.5,
            };
            for (int i = 0; i < marginKinds; i++)
            {
                d.Margins["Lane" + i] = 10 + i;
            }
            for (int i = 0; i < timeKinds; i++)
            {
                var list = new List<double>();
                for (int j = 0; j <= i; j++)
                {
                    list.Add(j * 1.25);
                }
                d.MarginTimes["Lane" + i] = list;
            }
            for (int i = 0; i < 5; i++)
            {
                d.Keys.Add(new ReplayKeyEvent
                {
                    Key = (ushort)(100 + i),
                    Label = 200 + i,
                    IsPressed = i % 2 == 0,
                    SongPosition = i * 0.375,
                });
            }
            return d;
        }

        private static int RoundTrip(string label, string dir, string name,
            ReplayData src)
        {
            string path = Path.Combine(dir, name + ".srp");
            try
            {
                ReplayFile.Write(path, src);
                var back = ReplayFile.Read(path);
                var problems = Compare(src, back);
                if (problems.Count == 0)
                {
                    Console.WriteLine($"    ✓ {label,-34} 往返一致"
                        + $"（{src.Keys.Count} 键 / {src.Margins.Count} 判定 /"
                        + $" {src.MarginTimes.Count} 判定时刻）");
                    return 0;
                }
                foreach (var p in problems)
                {
                    Console.WriteLine($"    ✗ {label}  {p}");
                }
                return problems.Count;
            }
            catch (Exception e)
            {
                Console.WriteLine($"    ✗ {label,-34} 抛异常："
                    + $"{e.GetType().Name}: {e.Message}");
                return 1;
            }
        }

        private static List<string> Compare(ReplayData a, ReplayData b)
        {
            var bad = new List<string>();
            if (a.SongId != b.SongId) bad.Add("SongId 不符");
            if (a.SongName != b.SongName) bad.Add("SongName 不符");
            if (a.DifficultyId != b.DifficultyId) bad.Add("DifficultyId 不符");
            if (a.DifficultyName != b.DifficultyName) bad.Add("DifficultyName 不符");
            if (a.Mapper != b.Mapper) bad.Add("Mapper 不符");
            if (a.Length != b.Length) bad.Add($"Length 不符（{a.Length} vs {b.Length}）");

            if (a.Margins.Count != b.Margins.Count)
            {
                bad.Add($"Margins 项数不符（{a.Margins.Count} vs {b.Margins.Count}）");
            }
            else
            {
                foreach (var kv in a.Margins)
                {
                    if (!b.Margins.TryGetValue(kv.Key, out int v))
                    {
                        bad.Add($"Margins 缺 {kv.Key}");
                    }
                    else if (v != kv.Value)
                    {
                        bad.Add($"Margins[{kv.Key}] 不符（{kv.Value} vs {v}）");
                    }
                }
            }

            if (a.MarginTimes.Count != b.MarginTimes.Count)
            {
                bad.Add($"MarginTimes 项数不符（{a.MarginTimes.Count} vs {b.MarginTimes.Count}）");
            }
            else
            {
                foreach (var kv in a.MarginTimes)
                {
                    if (!b.MarginTimes.TryGetValue(kv.Key, out List<double> v))
                    {
                        bad.Add($"MarginTimes 缺 {kv.Key}");
                        continue;
                    }
                    if (v.Count != kv.Value.Count)
                    {
                        bad.Add($"MarginTimes[{kv.Key}] 长度不符"
                            + $"（{kv.Value.Count} vs {v.Count}）");
                        continue;
                    }
                    for (int i = 0; i < v.Count; i++)
                    {
                        if (v[i] != kv.Value[i])
                        {
                            bad.Add($"MarginTimes[{kv.Key}][{i}] 不符");
                            break;
                        }
                    }
                }
            }

            if (a.Keys.Count != b.Keys.Count)
            {
                bad.Add($"Keys 项数不符（{a.Keys.Count} vs {b.Keys.Count}）");
            }
            else
            {
                for (int i = 0; i < a.Keys.Count; i++)
                {
                    var x = a.Keys[i];
                    var y = b.Keys[i];
                    if (x.Key != y.Key || x.Label != y.Label
                        || x.IsPressed != y.IsPressed
                        || x.SongPosition != y.SongPosition)
                    {
                        bad.Add($"Keys[{i}] 不符");
                        break;
                    }
                }
            }
            return bad;
        }

        /// <summary>不是回放文件、或被截断时，必须干净地拒绝而不是读出垃圾。</summary>
        private static int RejectsGarbage(string dir)
        {
            string path = Path.Combine(dir, "garbage.srp");
            File.WriteAllText(path, "not a replay at all");
            try
            {
                ReplayFile.Read(path);
                Console.WriteLine("    ✗ ⑤ 垃圾文件被当成回放读进去了");
                return 1;
            }
            catch (InvalidDataException)
            {
                Console.WriteLine("    ✓ ⑤ 垃圾文件被干净地拒绝");
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine($"    ✗ ⑤ 垃圾文件抛出的是 {e.GetType().Name}"
                    + "（应当是 InvalidDataException）");
                return 1;
            }
        }
    }
}
