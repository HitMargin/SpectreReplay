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
            fail += HitsRoundTrip(dir);
            fail += ReadsV1(dir);
            fail += ReadsV3(dir);
            fail += RejectsAbsurdHitCount(dir);
            fail += RejectsGarbage(dir);
            return fail;
        }

        /// <summary>
        /// 状态轨必须完整往返。
        ///
        /// 11 个字段里有 8 个是 bool 或 float，**恰好是那种"写漏一个、
        /// 读回来仍然不是垃圾"的类型**：错位之后读到的是相邻字段的字节，
        /// 表现为"回放里 midspin 判定和 overload 计数莫名其妙"，而不是
        /// 读失败。所以必须逐字段比，不能只比条数。
        /// </summary>
        private static int HitsRoundTrip(string dir)
        {
            string path = Path.Combine(dir, "hits.srp");
            try
            {
                var src = new ReplayData();
                // 三个命中：三块砖 + 一段自由漫游。值全部取**互不相同**的
                // 边界值，float/double 用 NaN 之外的极端值，避免
                // "两个字段恰好相等"把错位掩盖过去。
                src.Hits.Add(new ReplayHitContext
                {
                    Angle = -12345.5f,
                    CachedAngle = 0.0,
                    TargetExitAngle = 6.2831854820251465,
                    NoFailHit = true,
                    IsAuto = false,
                    NextFloorAuto = true,
                    RdcAuto = false,
                    MidspinInfiniteMargin = true,
                    OverloadCounter = 0.875f,
                    CurrentFloorId = 0,
                    FreeRoamSection = 0,
                });
                src.Hits.Add(new ReplayHitContext
                {
                    Angle = 180.25f,
                    CachedAngle = -98765.125,
                    TargetExitAngle = -1.5,
                    NoFailHit = false,
                    IsAuto = true,
                    NextFloorAuto = false,
                    RdcAuto = true,
                    MidspinInfiniteMargin = false,
                    OverloadCounter = -0.0625f,
                    CurrentFloorId = 4211,
                    FreeRoamSection = 0,
                });
                src.Hits.Add(new ReplayHitContext
                {
                    Angle = 0.0f,
                    CachedAngle = 1e-8,
                    TargetExitAngle = 1e8,
                    NoFailHit = true,
                    IsAuto = true,
                    NextFloorAuto = true,
                    RdcAuto = true,
                    MidspinInfiniteMargin = true,
                    OverloadCounter = 100f,
                    CurrentFloorId = 4212,
                    FreeRoamSection = 3,
                });
                ReplayFile.Write(path, src);
                var back = ReplayFile.Read(path);
                var bad = new List<string>();
                if (back.Hits.Count != 3)
                {
                    bad.Add($"Hits 条数错（{back.Hits.Count} vs 3）");
                }
                else
                {
                    for (int i = 0; i < 3; i++)
                    {
                        foreach (var p in CompareHit(src.Hits[i], back.Hits[i], i))
                        {
                            bad.Add(p);
                        }
                    }
                }
                if (bad.Count == 0)
                {
                    Console.WriteLine("    ✓ ⑤ 状态轨 11 字段逐项往返一致"
                        + "（3 条，含自由漫游区段与极端角度）");
                    return 0;
                }
                foreach (var p in bad)
                {
                    Console.WriteLine("    ✗ 状态轨往返 " + p);
                }
                return bad.Count;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ 状态轨往返 抛异常 "
                    + $"{ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        private static IEnumerable<string> CompareHit(ReplayHitContext a,
            ReplayHitContext b, int i)
        {
            // 逐字段。名字写清楚是哪一项，便于直接定位错位。
            if (a.Angle != b.Angle) yield return $"Hits[{i}].Angle（{a.Angle} vs {b.Angle}）";
            if (a.CachedAngle != b.CachedAngle)
                yield return $"Hits[{i}].CachedAngle（{a.CachedAngle} vs {b.CachedAngle}）";
            if (a.TargetExitAngle != b.TargetExitAngle)
                yield return $"Hits[{i}].TargetExitAngle（{a.TargetExitAngle} vs {b.TargetExitAngle}）";
            if (a.NoFailHit != b.NoFailHit) yield return $"Hits[{i}].NoFailHit";
            if (a.IsAuto != b.IsAuto) yield return $"Hits[{i}].IsAuto";
            if (a.NextFloorAuto != b.NextFloorAuto) yield return $"Hits[{i}].NextFloorAuto";
            if (a.RdcAuto != b.RdcAuto) yield return $"Hits[{i}].RdcAuto";
            if (a.MidspinInfiniteMargin != b.MidspinInfiniteMargin)
                yield return $"Hits[{i}].MidspinInfiniteMargin";
            if (a.OverloadCounter != b.OverloadCounter)
                yield return $"Hits[{i}].OverloadCounter（{a.OverloadCounter} vs {b.OverloadCounter}）";
            if (a.CurrentFloorId != b.CurrentFloorId)
                yield return $"Hits[{i}].CurrentFloorId（{a.CurrentFloorId} vs {b.CurrentFloorId}）";
            if (a.FreeRoamSection != b.FreeRoamSection)
                yield return $"Hits[{i}].FreeRoamSection（{a.FreeRoamSection} vs {b.FreeRoamSection}）";
        }

        /// <summary>
        /// v3 文件必须仍能读出按键与判定，且状态轨为空。
        ///
        /// v4 在定长头里加了一个 int32。v3 文件的读取必须**完全跳过**它：
        /// 多读一个 int32 就会把 Margins 段整体错位——而 Keys 依然可能
        /// "看起来正常"（数量对、值乱），玩家的体感是"旧回放播出来是乱的"。
        ///
        /// 这正是 v2 加字段时踩过的同一个坑（见 Read 里的注释），
        /// 所以每次加字段都要有对应的一条断言。
        /// </summary>
        private static int ReadsV3(string dir)
        {
            string path = Path.Combine(dir, "legacy_v3.srp");
            try
            {
                var d = new ReplayData
                {
                    SongName = "v3 曲目",
                    DifficultyName = "Insane",
                    Length = 12.5,
                    SceneName = "scnGame",
                    HitMarginLimit = 3,
                };
                d.Keys.Add(new ReplayKeyEvent
                {
                    Key = 65, Label = 0, IsPressed = true, SongPosition = 1.5,
                });
                d.Margins["Perfect"] = 1;
                d.MarginTimes["Perfect"] = new List<double> { 1.5 };
                // 降级成 v3：把版本号改回去并**删掉那一个 int32**。
                // 不能直接调 Write（它总写当前版本），所以照 Read 的
                // 形状手写一份 v3 布局。
                using (var fs = new FileStream(path, FileMode.Create))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(0x53505250u);
                    w.Write(3);                 // Version = 3（没有 v4 的 hitCount）
                    w.Write(1);                 // Keys.Count
                    w.Write(1);                 // Margins.Count
                    WriteStr(w, "sid");
                    WriteStr(w, "v3 曲目");
                    WriteStr(w, "diff");
                    WriteStr(w, "Insane");
                    WriteStr(w, "mapper");
                    w.Write(12.5);
                    WriteStr(w, "scnGame");
                    WriteStr(w, "Hard");
                    WriteStr(w, "Official_1");
                    w.Write(true);
                    w.Write(0);
                    w.Write(99);
                    w.Write(0);
                    w.Write(0);
                    w.Write(120.0);
                    w.Write(true);
                    w.Write(0.0);
                    w.Write(false);
                    w.Write(3);                 // HitMarginLimit（v3 末尾）
                    // ↑ v3 到这里为止 —— 接下来直接是 Margins 段
                    WriteStr(w, "Perfect");
                    w.Write(1);
                    w.Write(1);
                    WriteStr(w, "Perfect");
                    w.Write(1);
                    w.Write(1.5);
                    w.Write((ushort)65);
                    w.Write(0);
                    w.Write(true);
                    w.Write(1.5);
                }
                var back = ReplayFile.Read(path);
                var bad = new List<string>();
                if (back.SongName != "v3 曲目") bad.Add("SongName 读错（段整体错位）");
                if (back.DifficultyName != "Insane") bad.Add("DifficultyName 读错");
                if (back.HitMarginLimit != 3) bad.Add($"HitMarginLimit 读错（{back.HitMarginLimit}）");
                if (back.Keys.Count != 1) bad.Add($"Keys 数错（{back.Keys.Count}）");
                if (back.Keys.Count == 1 && back.Keys[0].SongPosition != 1.5)
                {
                    bad.Add("Keys[0].SongPosition 错");
                }
                if (!back.Margins.TryGetValue("Perfect", out int m) || m != 1)
                {
                    bad.Add("Margins[Perfect] 错");
                }
                // v3 没有状态轨：必须是空，不是"读到了别处的字节"
                if (back.Hits.Count != 0)
                {
                    bad.Add($"Hits 应为空，实为 {back.Hits.Count} 条");
                }
                if (bad.Count == 0)
                {
                    Console.WriteLine("    ✓ ⑥ v3 文件仍能完整读出，状态轨为空"
                        + "（v4 的 hitCount 被正确跳过）");
                    return 0;
                }
                foreach (var p in bad)
                {
                    Console.WriteLine("    ✗ v3 兼容 " + p);
                }
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ v3 兼容：抛异常 "
                    + $"{ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        /// <summary>
        /// 自报的命中条数离谱时必须被拒绝。
        ///
        /// 这一条守的是**读取路径本身**：段头里的条数直接决定
        /// <c>Hits.Capacity = hitCount</c>，而整个读取发生在玩家的
        /// 点击事件里（同步）。一个损坏或手工改过的文件能让它变成
        /// int.MaxValue，结果是当场申请 2G 内存、界面直接卡死，
        /// 甚至把游戏带崩。
        ///
        /// 没有上限的话这条检查自己就是风险的演示。
        /// </summary>
        private static int RejectsAbsurdHitCount(string dir)
        {
            string path = Path.Combine(dir, "absurd_hits.srp");
            try
            {
                using (var fs = new FileStream(path, FileMode.Create))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(0x53505250u);
                    w.Write(4);                 // Version = 4
                    w.Write(0);                 // Keys.Count
                    w.Write(0);                 // Margins.Count
                    WriteStr(w, "");
                    WriteStr(w, "");
                    WriteStr(w, "");
                    WriteStr(w, "");
                    WriteStr(w, "");
                    w.Write(0.0);
                    WriteStr(w, "");
                    WriteStr(w, "");
                    WriteStr(w, "");
                    w.Write(false);
                    w.Write(0);
                    w.Write(-1);
                    w.Write(int.MinValue);
                    w.Write(0);
                    w.Write(120.0);
                    w.Write(false);
                    w.Write(0.0);
                    w.Write(false);
                    w.Write(0);                 // HitMarginLimit
                    w.Write(int.MaxValue);     // ← 状态轨条数：荒谬
                }
                ReplayFile.Read(path);
                Console.WriteLine("    ✗ 状态轨条数 int.MaxValue 被照单全收"
                    + "（会在点击事件里当场申请 2G 内存）");
                return 1;
            }
            catch (InvalidDataException)
            {
                Console.WriteLine("    ✓ ⑦ 荒谬的状态轨条数被干净地拒绝");
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine($"    ✗ 荒谬条数抛出的是 {e.GetType().Name}"
                    + "（应当是 InvalidDataException）");
                return 1;
            }
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
                // v1 也没有状态轨：必须为空。
                // 若读端在这里多读了 v4 的 hitCount，这里就会读出垃圾。
                if (back.Hits.Count != 0)
                {
                    bad.Add($"Hits 应为空，实为 {back.Hits.Count} 条");
                }

                if (bad.Count == 0)
                {
                    Console.WriteLine("    ✓ ⑤ v1 旧文件仍能完整读出"
                        + "（2 键 / 1 判定 / 2 判定时刻，校验字段与状态轨为空）");
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
