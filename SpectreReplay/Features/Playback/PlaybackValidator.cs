using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using ADOFAI;
using SpectreReplay.Ui;
using SpectreReplay.Features.Recording;

namespace SpectreReplay.Features.Playback
{
    /// <summary>
    /// 回放前置校验。
    ///
    /// ── 为什么必须有 ──────────────────────────────────────────
    /// 回放按**地板序号**与**歌曲时间**推进。谱面一改，同一个序号就对应
    /// 完全不同的砖，同一个角度对应的时间也变了。这时回放会：
    ///   · 在错误的位置触发判定
    ///   · 行星卡住或判定档位全错
    /// 而**日志里没有任何异常**——因为代码没出错，只是数据不再匹配。
    ///
    /// 这类问题用户没法自查，也没法靠翻日志定位。必须在播放前拦住并说清原因。
    ///
    /// ── 与 Spectre 的对照 ────────────────────────────────────
    /// 照 Spectre 的 <c>TryStartPlayback</c>（Patch_Lifecycle.cs:112-243）。
    /// 但有两处故意不同：
    ///
    /// 1. **取不到的字段一律跳过检查，不当成不匹配。**
    ///    Spectre 用弱类型字典 + DataIntegrity.Check 强制 14 个键必须存在，
    ///    缺了就报错。本模组取不到时留空（如编辑器内没有 scnGame.levelData），
    ///    硬拦只会让编辑器内的回放永远播不了。宁可少一道校验，也不能拦死正常用法。
    ///
    /// 2. **FloorHash 用 int.MinValue 作"取不到"的哨兵。**
    ///    0 是官方关卡的合法哈希，把"取不到"写成 0 会让校验静默失效。
    ///
    /// ── 判定顺序 ──────────────────────────────────────────────
    /// 从"最不可能是玩家操作失误"排到"最可能是"：官方谱关卡名 → 谱面指纹
    /// → 移调/BPM → 难度 → 起点区间 → 倍速。玩家最先看到的应该是
    /// "你打开的不是那一关"，而不是"倍率不对"。
    /// </summary>
    internal static class PlaybackValidator
    {
        /// <summary>
        /// 校验。返回 true 表示可以播放。
        /// 失败时已经把原因通过提示告知玩家，并写进日志。
        /// </summary>
        internal static bool Validate(ReplayData d)
        {
            if (d == null)
            {
                Warn("playback.err.no_data", "");
                return false;
            }
            try
            {
                return Check(d);
            }
            catch (Exception ex)
            {
                // 校验本身抛异常时**放行**：校验是保护措施，
                // 不能因为它自己坏了就把回放也一起禁掉。
                Debug.LogWarning("[SpectreReplay] 校验异常，放行回放：" + ex.Message);
                return true;
            }
        }

        private static bool Check(ReplayData d)
        {
            // ── 1. 官方谱的内置关卡名 ──────────────────────────
            // 官方关卡才有内置名；自定义谱这个字段为空，跳过。
            if (d.IsOfficialLevel && !string.IsNullOrEmpty(d.InternalLevelName))
            {
                string now = GCS.internalLevelName ?? "";
                if (d.InternalLevelName != now)
                {
                    return Fail("playback.err.wrong_level", d.SongName);
                }
            }

            // ── 2. 谱面指纹 ────────────────────────────────────
            // 最关键的一道：谱面改一个角，按键就在错误的位置触发判定。
            if (d.FloorHash != int.MinValue)
            {
                int now = CurrentFloorHash();
                if (now != int.MinValue && now != d.FloorHash)
                {
                    return Fail("playback.err.floor_changed", d.SongName);
                }
            }

            // ── 3. 移调与 BPM ───────────────────────────────────
            // 官方谱录制时 Pitch/Bpm 不写（恒 0），跳过。
            if (!d.IsOfficialLevel && d.Bpm > 0.0)
            {
                try
                {
                    var ld = scnGame.instance?.levelData;
                    if (ld != null && (ld.pitch != d.Pitch || (double)ld.bpm != d.Bpm))
                    {
                        return Fail("playback.err.chart_mismatch", d.SongName);
                    }
                }
                catch { }
            }

            // ── 4. 判定难度 ─────────────────────────────────────
            // 难度直接改变判定档位的宽度，难度错了判定全错。
            if (!string.IsNullOrEmpty(d.JudgeMode))
            {
                if (GCS.difficulty.ToString() != d.JudgeMode)
                {
                    return Fail("playback.err.wrong_difficulty", d.JudgeMode);
                }
            }

            // ── 5. 起点区间 ─────────────────────────────────────
            // 完整局（0 → 末块）可以从任意检查点起；非完整局必须落在区间内。
            int total = TotalFloors();
            bool fullRun = total > 1 && d.StartTile == 0 && d.EndTile == total - 1;
            if (!fullRun && total > 0)
            {
                int cp = GCS.checkpointNum;
                bool ok = d.EndTile >= 0
                    ? (cp >= d.StartTile && cp <= d.EndTile)
                    : (cp == d.StartTile);
                if (!ok)
                {
                    return Fail("playback.err.wrong_tile", d.StartTile.ToString());
                }
            }

            // ── 6. 倍速 ─────────────────────────────────────────
            // 倍率直接缩放时间轴，不符时按键整体错位。
            //
            // SpeedTrail <= 0 表示「录制时倍率没记到」（见
            // KeyRecorder 的取值处，编辑器内可能拿不到）。
            // **这种情况必须放行**：拿默认值 1.0 去和当前的 2.0 比，
            // 会把每一份这类回放都拦下——而它们其实完全能播。
            if (d.SpeedTrailMode && d.SpeedTrail > 0.0)
            {
                double actual = (GCS.speedTrialMode || GCS.practiceMode)
                    ? (double)GCS.currentSpeedTrial : 1.0;
                if (Math.Abs(actual - d.SpeedTrail) > 0.001)
                {
                    return Fail("playback.err.wrong_speed",
                        d.SpeedTrail.ToString("0.##"));
                }
            }

            return true;
        }

        /// <summary>
        /// 当前谱面的指纹。取不到返回 int.MinValue（= 跳过校验）。
        ///
        /// 与 KeyRecorder.ComputeFloorHash 同一套算法——**两边必须一致**，
        /// 否则录的时候算一个值、放的时候算另一个，每份回放都会被拦下。
        /// </summary>
        private static int CurrentFloorHash()
        {
            try
            {
                if (ADOBase.isOfficialLevel)
                {
                    return 0;
                }
                var ld = scnGame.instance?.levelData;
                if (ld == null)
                {
                    return int.MinValue;
                }
                var angles = ld.angleData;
                if (angles == null || angles.Count == 0)
                {
                    return int.MinValue;
                }
                var sb = new System.Text.StringBuilder(angles.Count * 8);
                for (int i = 0; i < angles.Count; i++)
                {
                    float a = angles[i];
                    float norm = a >= 0f ? a % 360f : 360f - (-a % 360f);
                    sb.Append(norm.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                return sb.ToString().GetHashCode();
            }
            catch
            {
                return int.MinValue;
            }
        }

        private static int TotalFloors()
        {
            try
            {
                return ADOBase.lm?.listFloors?.Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool Fail(string key, string detail)
        {
            Warn(key, detail);
            return false;
        }

        private static void Warn(string key, string detail)
        {
            string msg = string.IsNullOrEmpty(detail)
                ? Loc.T(key) : Loc.F(key, detail);
            Debug.LogWarning("[SpectreReplay] 回放校验未通过：" + msg);
            Ui.Status.Warn(key, detail);
        }
    }
}