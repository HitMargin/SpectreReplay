// 回放前置校验规则的离线验证。
//
// 存在的理由：没有校验时，谱面一改回放就会「在错误的位置触发判定」
// 而日志里没有任何异常。这类问题玩家没法自查、没法靠翻日志定位。
//
// 下面把六道校验各自建模，断言两件事：
//   ① 每道校验在「数据真的对不上」时必须拦下
//   ② 每道校验在「数据取不到」时必须**放行**（而不是拿哨兵值误判）
//
// 第 ② 条同样是硬要求：编辑器内没有 scnGame.levelData、自定义谱没有
// internalLevelName，取不到是常态。硬拦会让正常用法永远播不了，
// 那比不校验更糟。
using System;

namespace UiPreview
{
    internal static class ValidatorCheck
    {
        /// <summary>int.MinValue = 取不到的哨兵（与真机一致）。</summary>
        private const int Unknown = int.MinValue;

        private sealed class Case
        {
            internal bool IsOfficialLevel;
            internal string InternalLevelName = "";
            internal string NowInternalLevelName = "";
            internal int FloorHash;          // 录制时
            internal int NowFloorHash;       // 当前
            internal double Bpm;             // 录制时
            internal int Pitch;
            internal double NowBpm;
            internal int NowPitch;
            internal bool HasChart;
            internal string JudgeMode = "";
            internal string NowDifficulty = "";
            internal int StartTile;
            internal int EndTile = -1;
            internal int TotalFloors;
            internal int NowCheckpoint;
            internal bool SpeedTrailMode;
            internal double SpeedTrail = 1.0;
            internal double NowSpeed = 1.0;
        }

        /// <summary>
        /// 与 PlaybackValidator.Check 同一套判定。
        /// 刻意不复用真代码：真代码依赖 scnGame/GCS/ADOBase，
        /// 链进来就要拖进整个 Unity。
        /// </summary>
        private static string Check(Case c)
        {
            if (c.IsOfficialLevel && c.InternalLevelName.Length > 0
                && c.InternalLevelName != c.NowInternalLevelName)
            {
                return "wrong_level";
            }
            if (c.FloorHash != Unknown && c.NowFloorHash != Unknown
                && c.NowFloorHash != c.FloorHash)
            {
                return "floor_changed";
            }
            if (!c.IsOfficialLevel && c.Bpm > 0.0 && c.HasChart
                && (c.NowPitch != c.Pitch || c.NowBpm != c.Bpm))
            {
                return "chart_mismatch";
            }
            if (c.JudgeMode.Length > 0 && c.NowDifficulty != c.JudgeMode)
            {
                return "wrong_difficulty";
            }
            bool fullRun = c.TotalFloors > 1 && c.StartTile == 0
                && c.EndTile == c.TotalFloors - 1;
            if (!fullRun && c.TotalFloors > 0)
            {
                bool ok = c.EndTile >= 0
                    ? (c.NowCheckpoint >= c.StartTile && c.NowCheckpoint <= c.EndTile)
                    : (c.NowCheckpoint == c.StartTile);
                if (!ok) return "wrong_tile";
            }
            if (c.SpeedTrailMode && c.SpeedTrail > 0.0
                && Math.Abs(c.NowSpeed - c.SpeedTrail) > 0.001)
            {
                return "wrong_speed";
            }
            return null;
        }

        /// <summary>一份全部匹配的基准用例。派生用例只改一个字段。</summary>
        private static Case Good()
        {
            return new Case
            {
                IsOfficialLevel = false,
                FloorHash = 12345,
                NowFloorHash = 12345,
                Bpm = 140.0,
                NowBpm = 140.0,
                Pitch = 0,
                NowPitch = 0,
                HasChart = true,
                JudgeMode = "Normal",
                NowDifficulty = "Normal",
                StartTile = 0,
                EndTile = 99,
                TotalFloors = 100,
                NowCheckpoint = 0,
            };
        }

        internal static int Run()
        {
            Console.WriteLine("  ── 回放校验规则 ──");
            int fail = 0;
            fail += Passes(Good(), null, "全部匹配");
            fail += Rejects(Case0("wrong_level"), "wrong_level");
            fail += Rejects(ChartChanged(), "floor_changed");
            fail += Rejects(PitchChanged(), "chart_mismatch");
            fail += Rejects(DifficultyChanged(), "wrong_difficulty");
            fail += Rejects(TileOutOfRange(), "wrong_tile");
            fail += Rejects(SpeedChanged(), "wrong_speed");
            fail += UnknownFieldsPass();
            fail += FullRunAnyTile();
            return fail;
        }

        private static Case Case0(string why)
        {
            var c = Good();
            c.IsOfficialLevel = true;
            c.InternalLevelName = "Official_1";
            c.NowInternalLevelName = "Official_7";
            c.Bpm = 0;          // 官方谱不校验 bpm，避免抢先命中别的规则
            c.HasChart = false;
            return c;
        }

        private static Case ChartChanged()
        {
            var c = Good();
            c.NowFloorHash = 99999;     // 谱面被改了一个角
            return c;
        }

        private static Case PitchChanged()
        {
            var c = Good();
            c.NowPitch = 3;             // 移调变了
            return c;
        }

        private static Case DifficultyChanged()
        {
            var c = Good();
            c.NowDifficulty = "Hard";
            return c;
        }

        private static Case TileOutOfRange()
        {
            var c = Good();
            c.StartTile = 10;
            c.EndTile = 20;
            c.NowCheckpoint = 50;       // 在区间之外
            return c;
        }

        private static Case SpeedChanged()
        {
            var c = Good();
            c.SpeedTrailMode = true;
            c.SpeedTrail = 1.5;
            c.NowSpeed = 1.0;
            return c;
        }

        private static int Rejects(Case c, string expect)
        {
            string got = Check(c);
            bool ok = got == expect;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} 拦下 {expect}"
                + $"  → 实得 {(got ?? "放行")}");
            if (!ok)
            {
                Console.WriteLine("      ← 校验漏了：回放会在错误的位置触发判定且无任何报错");
            }
            return ok ? 0 : 1;
        }

        private static int Passes(Case c, string expect, string label)
        {
            string got = Check(c);
            bool ok = got == expect;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} {label}  → {(got ?? "放行")}");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 字段取不到时必须放行。
        ///
        /// 这是硬要求：编辑器内没有 scnGame.levelData、自定义谱没有
        /// internalLevelName，取不到是常态。拿哨兵值硬拦会把正常用法
        /// 拦死——那比不校验更糟。
        /// </summary>
        private static int UnknownFieldsPass()
        {
            var c = Good();
            c.FloorHash = Unknown;        // 录制时没取到
            c.NowFloorHash = 55555;       // 现在取到了另一个值
            c.Bpm = 0.0;                  // 没取到 bpm
            c.HasChart = true;            // 但当前谱面是可读的
            c.NowBpm = 999.0;
            c.JudgeMode = "";             // 没取到难度
            c.NowDifficulty = "Hard";
            c.SpeedTrailMode = true;      // 倍速模式但没记倍率（<= 0 = 没记到）
            c.SpeedTrail = 0.0;
            c.NowSpeed = 2.0;
            c.TotalFloors = 0;            // 取不到地板总数

            string got = Check(c);
            bool ok = got == null;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} 取不到的字段一律放行"
                + $"  → {(got ?? "放行")}");
            if (!ok)
            {
                Console.WriteLine("      ← 拿哨兵值硬拦，编辑器内的回放会永远播不了");
            }
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 完整局（0 → 末块）允许从任意检查点起播——数据覆盖全程，
        /// 从中间起播时前面的判定会被跳过，但那是有意的取舍。
        /// </summary>
        private static int FullRunAnyTile()
        {
            var c = Good();
            c.StartTile = 0;
            c.EndTile = 99;          // = TotalFloors - 1
            c.TotalFloors = 100;
            c.NowCheckpoint = 73;    // 从中间起
            string got = Check(c);
            bool ok = got == null;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} 完整局允许任意检查点起播"
                + $"  → {(got ?? "放行")}");
            return ok ? 0 : 1;
        }
    }
}