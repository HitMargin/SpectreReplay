using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using K = SpectreReplay.Features.Replay.ReplayKeys;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 一份回放的「只读摘要」，给回放列表点开时展示用。
///
/// <para><b>为什么单独一个类型，而不是把 <see cref="ReplayData"/> 直接交给界面。</b>
/// 界面要的是**已经算好、可以直接摆的文字**，不是一堆可空字段。让界面去
/// 直接读 <c>SongName</c>／<c>Bpm</c> 这类属性，会把「字段缺失怎么办」「BPM 是 0 怎么办」
/// 这类判断散到 UI 层——而 UI 层每加一行都在 OnGUI 里，抛异常就等于整个设置
/// 面板被 UMM 收掉（见 <see cref="Pages.Pages"/> 的规矩）。所以这里把
/// 取值、缺省、格式化一次做完，界面只负责排版。</para>
///
/// <para><b>能显示什么由「录制时到底写了什么」决定，不是由数据结构有什么决定。</b>
/// 例如 <c>StartTime</c> 在 <see cref="Replay"/> 里有字段、编码器也写了，
/// 但录制侧从来不给它赋值 ⇒ 读出来恒为空。把这种字段摆到界面上，
/// 玩家看到的是一条**永远空着的「录制时间」**，比不显示更糟。
/// 所以这里只暴露确有数据的东西，时间一律用文件时间。</para>
/// </summary>
internal sealed class ReplayFileInfo
{
    internal string FilePath;
    internal string FileName;
    internal bool Encrypted;        // false = .psprp（不加密变体）
    internal long FileBytes;
    internal DateTime FileTime;     // 最后写入时间（本地时区）

    internal string SongName;
    internal string ArtistName;
    internal string JudgeMode;
    internal string LevelPath;
    internal bool IsOfficialLevel;
    internal string InternalLevelName;

    internal int StartTileIndex;
    internal int EndTileIndex;
    internal double BpmValue;
    internal int PitchValue;
    internal double PlaybackSpeed;
    internal bool QuickPitched;
    internal string HitMarginLimit;
    internal string ModVersionText;
    internal double PercentXacc;
    /// <summary>普通 Accuracy（由判定分布按官方公式还原；无判定数据时为 -1）。</summary>
    internal double Accuracy;

    internal int KeyCount;
    internal int HitCount;
    /// <summary>回放时长（秒，按键事件的最后时刻）。取不到就是 0。</summary>
    internal double DurationSeconds;

    /// <summary>判定分布，按 <see cref="HitMargin"/> 枚举顺序；解析失败就是 null。</summary>
    internal int[] JudgmentCounts;

    /// <summary>读不出来时的原因（已本地化的短句）。成功为 null。</summary>
    internal string Error;

    internal bool Ok => Error == null;

    /// <summary>
    /// 从磁盘读一份文件并算出摘要。**不装载、不产生任何全局副作用**
    /// （走 <see cref="ReplayManager.ReadReplayData"/>，不碰键盘音）。
    ///
    /// <para>异常一律在内部吃掉并转成 <see cref="Error"/>：这是给界面点的按钮用的，
    /// 抛出去会让整个设置面板消失。</para>
    /// </summary>
    internal static ReplayFileInfo FromFile(string path)
    {
        var info = new ReplayFileInfo
        {
            FilePath = path ?? "",
            FileName = string.IsNullOrEmpty(path) ? "" : Path.GetFileNameWithoutExtension(path),
            Encrypted = !(path ?? "").EndsWith(ReplayConstants.ExtensionNoEncrypt,
                StringComparison.OrdinalIgnoreCase),
        };

        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                info.Error = "file_gone";
                return info;
            }
            info.FileBytes = fi.Length;
            info.FileTime = fi.LastWriteTime;
        }
        catch
        {
            // 文件信息都读不到，后面的解码没有意义。
            info.Error = "io_error";
            return info;
        }

        ReplayData data;
        try
        {
            if (!ReplayManager.ReadReplayData(out data, path))
            {
                info.Error = "decode_failed";
                return info;
            }
        }
        catch
        {
            info.Error = "decode_failed";
            return info;
        }

        if (data == null)
        {
            info.Error = "decode_failed";
            return info;
        }

        // 缺省值与旧版 Str/Bool/Int/Dbl 四个辅助方法逐字一致：缺字符串记空串，缺数值记 0。
        info.SongName = data.SongName ?? "";
        info.ArtistName = data.ArtistName ?? "";
        info.JudgeMode = data.JudgeMode ?? "";
        info.LevelPath = data.LevelPath ?? "";
        info.InternalLevelName = data.InternalLevelName ?? "";
        info.IsOfficialLevel = data.IsOfficialLevel == true;
        info.HitMarginLimit = data.HitMarginLimit ?? "";
        info.ModVersionText = data.ModVersion ?? "";
        info.StartTileIndex = data.StartTile ?? 0;
        info.EndTileIndex = data.EndTile ?? 0;
        info.BpmValue = data.Bpm ?? 0.0;
        info.PitchValue = data.Pitch ?? 0;
        info.PlaybackSpeed = data.PlaybackSpeed ?? 0.0;
        info.QuickPitched = data.QuickPitched == true;
        info.PercentXacc = data.PercentXacc ?? 0.0;

        info.KeyCount = data.KeyEvents != null ? data.KeyEvents.Count : 0;
        info.HitCount = data.HitSnapshots != null ? data.HitSnapshots.Count : 0;
        info.DurationSeconds = LastKeyPosition(data);
        info.JudgmentCounts = ParseJudgments(data.JudgmentList ?? "");
        info.Accuracy = SpectreReplay.Features.Compat.HitMarginCompat.Accuracy(info.JudgmentCounts);
        return info;
    }

    /// <summary>按键流里最晚的那个时间点。没有按键就是 0。</summary>
    private static double LastKeyPosition(ReplayData data)
    {
        if (data.KeyEvents == null || data.KeyEvents.Count == 0)
            return 0.0;
        double max = 0.0;
        // 线性扫：文件本来就已经全读进内存了，这点开销可以忽略，
        // 而排序会改变列表顺序——将来若有人拿它干别的，会踩到意外副作用。
        for (int i = 0; i < data.KeyEvents.Count; i++)
        {
            double p = data.KeyEvents[i].SongPosition;
            if (p > max) max = p;
        }
        return max;
    }

    /// <summary>
    /// 解析录制时写下的 <c>JudgmentList</c>（形如 <c>[0,3,17,…]</c>）。
    ///
    /// **必须容错**：这个字符串来自玩家可编辑的文件，缺失、截断、含非数字
    /// 都会出现。解析失败返回 null（界面据此不画判定分布），而不是抛异常
    /// 或者画一排 0——后者看起来像「这局全是 TooEarly」，是错误信息。
    /// </summary>
    internal static int[] ParseJudgments(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        string s = raw.Trim();
        if (s.Length < 2 || s[0] != '[' || s[s.Length - 1] != ']') return null;
        s = s.Substring(1, s.Length - 2);
        if (s.Length == 0) return null;

        string[] parts = s.Split(',');
        var counts = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int v) || v < 0)
            {
                return null;
            }
            counts[i] = v;
        }
        return counts;
    }

    /// <summary>判定桶总数（排除 Auto 之类不计入的项？不排——玩家要看的是全量分布）。</summary>
    internal int JudgmentTotal()
    {
        if (JudgmentCounts == null) return 0;
        int sum = 0;
        for (int i = 0; i < JudgmentCounts.Length; i++) sum += JudgmentCounts[i];
        return sum;
    }

    private static readonly string[] JudgmentNames =
    {
        "TooEarly", "VeryEarly", "EarlyPerfect", "PerfectMinus",
        "XPerfect", "PerfectPlus", "LatePerfect", "VeryLate",
        "TooLate", "Multipress", "FailMiss", "FailOverload",
        "Auto", "OverPress", "Midspin", "FailedFloor",
    };

    /// <summary>与 <see cref="JudgmentNames"/> 逐项平行的文案键。</summary>
    private static readonly string[] JudgmentLocaleKeys =
    {
        "playback.judge.tooearly", "playback.judge.veryearly",
        "playback.judge.earlyperfect", "playback.judge.perfectminus",
        "playback.judge.xperfect", "playback.judge.perfectplus",
        "playback.judge.lateperfect", "playback.judge.verylate",
        "playback.judge.toolate", "playback.judge.multipress",
        "playback.judge.failmiss", "playback.judge.failoverload",
        "playback.judge.auto", "playback.judge.overpress",
        "playback.judge.midspin", "playback.judge.failedfloor",
    };

    /// <summary>能显示多少行（上限；实际只画计数 &gt; 0 的那些）。</summary>
    internal static int JudgmentRowCount => JudgmentNames.Length;

    /// <summary>
    /// 第 <paramref name="row"/> 行该显示什么。返回 false = 这行不画
    /// （计数为 0，或本版游戏根本没有这个判定）。
    /// </summary>
    internal bool JudgmentRow(int row, out string localeKey, out int count)
    {
        localeKey = null;
        count = 0;
        if (JudgmentCounts == null || row < 0 || row >= JudgmentNames.Length)
            return false;
        if (!SpectreReplay.Features.Compat.HitMarginCompat.TryResolve(JudgmentNames[row], out var margin))
            return false;   // 本版游戏没这个成员：不画，而不是画一个 0
        int idx = (int)margin;
        if ((uint)idx >= (uint)JudgmentCounts.Length)
            return false;
        count = JudgmentCounts[idx];
        if (count <= 0)
            return false;
        localeKey = JudgmentLocaleKeys[row];
        return true;
    }

}
