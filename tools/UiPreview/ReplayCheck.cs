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
using System.Text;
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
            fail += ReadsV1(dir);
            fail += RejectsGarbage(dir);
            return fail;
        }

        /// <summary>
        /// v1 文件必须仍能读出完整的按键与判定。
        ///
        /// 格式升级时最容易漏的就是这条：v2 在 Length 之后插了一段元数据，
        /// 读端若不按版本号跳过，v1 文件会从 Margins 段开始整体错位——
        /// 而 Keys 依然可能"看起来正常"（数量对、值乱）。
        /// 玩家的体感是"旧回放播出来是乱的"，且极难归因。
        ///
        /// 这里手工拼一个 v1 文件（不经过 Write，因为 Write 只产 v2）。
        /// </summary>
        private static int ReadsV1(string dir)
        {
            string path = Path.Combine(dir, "legacy_v1.srp");
            try
            {
                using (var fs = new FileStream(path, FileMode.Create))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(0x53505250u);      // Magic "SRPP"
                    w.Write(1);                 // Version = 1（没有 v2 那一段）
                    w.Write(2);                 // Keys.Count
                    w.Write(1);                 // Margins.Count
                    WriteStr(w, "sid");
                    WriteStr(w, "旧曲目");
                    WriteStr(w, "");
                    WriteStr(w, "Normal");
                    WriteStr(w, "");
                    w.Write(99.5);             // Length
                    // ↑ v1 到这里为止 —— 接下来直接是 Margins 段

                    WriteStr(w, "Perfect");
                    w.Write(7);
                    w.Write(1);                 // MarginTimes.Count
                    WriteStr(w, "Perfect");
                    w.Write(2);
                    w.Write(1.5);
                    w.Write(2.5);
                    // Keys：2 条
                    for (int i = 0; i < 2; i++)
                    {
                        w.Write((ushort)(65 + i));
                        w.Write(0);
                        w.Write(i == 0);
                        w.Write(i * 3.25);
                    }
                }

                var back = ReplayFile.Read(path);
                var bad = new List<string>();
                if (back.SongName != "旧曲目") bad.Add("SongName 读错");
                if (back.DifficultyName != "Normal") bad.Add("DifficultyName 读错");
                if (back.Length != 99.5) bad.Add($"Length 读错（{back.Length}）");
                if (back.Keys.Count != 2) bad.Add($"Keys 数错（{back.Keys.Count}）");
                if (back.Keys.Count == 2 && back.Keys[1].SongPosition != 3.25)
                {
                    bad.Add($"Keys[1].SongPosition 错（{back.Keys[1].SongPosition}）");
                }
                if (!back.Margins.TryGetValue("Perfect", out int m) || m != 7)
                {
                    bad.Add("Margins[Perfect] 错");
                }
                if (!back.MarginTimes.TryGetValue("Perfect", out var t) || t.Count != 2
                    || t[0] != 1.5 || t[1] != 2.5)
                {
                    bad.Add("MarginTimes[Perfect] 错");
                }
                // v1 没有的字段必须是"没记到"的哨兵，校验器据此跳过
                if (back.FloorHash != int.MinValue)
                {
                    bad.Add($"FloorHash 应为没记到的哨兵，实为 {back.FloorHash}");
                }
                if (back.SpeedTrail > 0.0)
                {
                    bad.Add($"SpeedTrail 应为没记到（<=0），实为 {back.SpeedTrail}");
                }

                if (bad.Count == 0)
                {
                    Console.WriteLine("    ✓ ⑤ v1 旧文件仍能完整读出"
                        + "（2 键 / 1 判定 / 2 判定时刻，校验字段为哨兵）");
                    return 0;
                }
                foreach (var p in bad)
                {
                    Console.WriteLine("    ✗ v1 兼容 " + p);
                }
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ v1 兼容：抛异常 "
                    + $"{ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        private static void WriteStr(BinaryWriter w, string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            w.Write(bytes.Length);
            w.Write(bytes);
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
                // v2 校验元数据：全部填成**非默认值**，默认值漏写时也能被发现
                SceneName = "scnGame",
                JudgeMode = "Hard",
                InternalLevelName = "Official_117",
                IsOfficialLevel = false,
                StartTile = 3,
                EndTile = 4211,
                FloorHash = -1887432,
                Pitch = 2,
                Bpm = 174.25,
                IfNoFail = true,
                SpeedTrail = 1.75,
                SpeedTrailMode = true,
                HitMarginLimit = 2,
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

            // v2 新增的校验元数据。**必须逐项比**——格式错位（比如
            // 少写一个字段）会让这些值读到别处的字节上，而 Keys/Margins
            // 看起来仍然"正常"，往返检查就漏过去了。
            if (a.SceneName != b.SceneName) bad.Add("SceneName 不符");
            if (a.JudgeMode != b.JudgeMode) bad.Add("JudgeMode 不符");
            if (a.InternalLevelName != b.InternalLevelName) bad.Add("InternalLevelName 不符");
            if (a.IsOfficialLevel != b.IsOfficialLevel) bad.Add("IsOfficialLevel 不符");
            if (a.StartTile != b.StartTile) bad.Add($"StartTile 不符（{a.StartTile} vs {b.StartTile}）");
            if (a.EndTile != b.EndTile) bad.Add($"EndTile 不符（{a.EndTile} vs {b.EndTile}）");
            if (a.FloorHash != b.FloorHash) bad.Add($"FloorHash 不符（{a.FloorHash} vs {b.FloorHash}）");
            if (a.Pitch != b.Pitch) bad.Add($"Pitch 不符（{a.Pitch} vs {b.Pitch}）");
            if (a.Bpm != b.Bpm) bad.Add($"Bpm 不符（{a.Bpm} vs {b.Bpm}）");
            if (a.IfNoFail != b.IfNoFail) bad.Add("IfNoFail 不符");
            if (a.SpeedTrail != b.SpeedTrail) bad.Add($"SpeedTrail 不符（{a.SpeedTrail} vs {b.SpeedTrail}）");
            if (a.SpeedTrailMode != b.SpeedTrailMode) bad.Add("SpeedTrailMode 不符");
            if (a.HitMarginLimit != b.HitMarginLimit) bad.Add($"HitMarginLimit 不符（{a.HitMarginLimit} vs {b.HitMarginLimit}）");

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
