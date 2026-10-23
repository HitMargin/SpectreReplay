using System;
using System.Globalization;
using System.IO;
using SpectreReplay.Features.Replay;
using SpectreReplay.Ui;
using UnityEngine;

namespace SpectreReplay.Pages
{
    /// <summary>
    /// 回放文件的「详情」面板：在列表里点开某一份时，就地展开一段只读摘要。
    ///
    /// <para><b>为什么就地展开而不是弹窗/跳页。</b>回放列表本身就是一个
    /// 「从一堆里挑一份」的界面，点开的意图九成是「这份是哪一局、值不值得放」。
    /// 跳走会丢掉列表位置（回来还得重新找），弹窗在这个 UMM 面板里没有宿主支持
    /// （只能自己画一块浮层，而它会盖住要对比的相邻几行）。就地展开——
    /// 列表与详情同屏可比，代价是页面变长，所以只允许展开一份。</para>
    ///
    /// <para><b>只用一个展开位。</b><see cref="Pages._infoPath"/> 记当前展开的那份；
    /// 点另一份等于把展开位移过去。这样页面最多只长一段，不会因为连点几份
    /// 而把列表挤到屏幕外。</para>
    ///
    /// <para><b>拿数据不装载。</b>走 <see cref="ReplayFileInfo.FromFile"/> →
    /// <see cref="ReplayManager.ReadReplayData"/>，**不碰键盘音、不改播放状态**。
    /// 否则「看一眼文件信息」会把当前正载入的那份回放的键盘音顶掉。</para>
    /// </summary>
    internal static class ReplayInfoPanel
    {
        /// <summary>展开位：当前展开详情的文件路径（空 = 都收着）。</summary>
        internal static string OpenPath = "";

        /// <summary>缓存：换展开位或文件变了才重读。读一份要解密+解码，不能每帧做。</summary>
        private static ReplayFileInfo _cache;
        private static string _cachePath = "";
        private static DateTime _cacheStamp;

        internal static bool IsOpen(string path)
            => !string.IsNullOrEmpty(path)
            && string.Equals(path, OpenPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>点一下切换：已展开的就收起，否则展开这一份。</summary>
        internal static void Toggle(string path)
        {
            if (IsOpen(path))
            {
                OpenPath = "";
                _cache = null;
                _cachePath = "";
                return;
            }
            OpenPath = path ?? "";
            _cache = null;
            _cachePath = "";
        }

        /// <summary>删掉文件后调用：若正展开的是它，收起来。</summary>
        internal static void Forget(string path)
        {
            if (IsOpen(path)) Toggle(path);
            if (_cache != null && string.Equals(_cachePath, path, StringComparison.OrdinalIgnoreCase))
            {
                _cache = null;
                _cachePath = "";
            }
        }

        private static ReplayFileInfo Info(string path)
        {
            // 文件可能被外部改过（换机同步、手改），所以带上修改时间做缓存键。
            DateTime stamp = DateTime.MinValue;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) stamp = fi.LastWriteTimeUtc;
            }
            catch
            {
                // 取不到就当作变了，重读一次；重读失败会走 Error 分支。
            }
            if (_cache != null
                && string.Equals(_cachePath, path, StringComparison.OrdinalIgnoreCase)
                && _cacheStamp == stamp)
            {
                return _cache;
            }
            _cache = ReplayFileInfo.FromFile(path);
            _cachePath = path;
            _cacheStamp = stamp;
            return _cache;
        }

        /// <summary>把这一段画出来。调用方负责判定「这一行确实是展开的那份」。</summary>
        internal static void Draw(string path)
        {
            ReplayFileInfo info;
            try
            {
                info = Info(path);
            }
            catch (Exception ex)
            {
                // OnGUI 抛异常 = UMM 收掉整个设置面板。信息面板纯属附加，绝不能带走页面。
                Debug.LogWarning("[SpectreReplay] 读取回放详情失败：" + ex.Message);
                return;
            }
            if (info == null) return;

            // 展开段整体往左缩进，让它明显隶属上面那一行
            GUILayout.BeginHorizontal();
            GUILayout.Space(14f);
            GUILayout.BeginVertical();
            try
            {
                if (!info.Ok)
                {
                    GUILayout.Label(Loc.T("playback.info.err." + info.Error), Gui.NoteStyle,
                        GUILayout.ExpandWidth(true));
                    return;
                }
                DrawTitle(info);
                DrawFacts(info);
                DrawStats(info);
                DrawJudgments(info);
                DrawFileLine(info);
            }
            finally
            {
                // finally：中途 return 也必须配对，否则布局栈失衡会把后面整页画错。
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>第一行：歌名 — 作者。玩家认一份回放靠这个，不是靠文件名。</summary>
        private static void DrawTitle(ReplayFileInfo info)
        {
            string song = string.IsNullOrEmpty(info.SongName) ? Loc.T("playback.info.unknown") : info.SongName;
            string artist = string.IsNullOrEmpty(info.ArtistName) ? Loc.T("playback.info.unknown") : info.ArtistName;
            GUILayout.Label(Loc.F("playback.info.title", song, artist), Gui.BoldStyle,
                GUILayout.ExpandWidth(true));
        }

        /// <summary>两列 × 两行：难度/BPM、倍速/起点→终点。</summary>
        private static void DrawFacts(ReplayFileInfo info)
        {
            GUILayout.BeginHorizontal();
            Gui.KeyValueNarrow("playback.info.difficulty",
                string.IsNullOrEmpty(info.JudgeMode) ? "?" : info.JudgeMode, Gui.TextCol);
            Gui.KeyValueNarrow("playback.info.bpm",
                info.BpmValue > 0.0 ? info.BpmValue.ToString("0.##") : "?", Gui.TextCol);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Gui.KeyValueNarrow("playback.info.speed", SpeedText(info), Gui.TextCol);
            Gui.KeyValueNarrow("playback.info.floor", FloorText(info), Gui.TextCol);
            GUILayout.EndHorizontal();
        }

        /// <summary>两列 × 三行：命中/按键、时长/Accuracy、X-ACC。</summary>
        ///
        /// **Accuracy 与 X-Accuracy 是两回事，必须都显示。** xacc 按判定档位
        /// 加权（TooEarly 也有 0.2 分），Accuracy 只数"完美命中占多少"再加
        /// 内档奖励——同一局可以 xacc 99.83%、Accuracy 96.5%。官方结算两个都给，
        /// 只显示 xacc 会让玩家以为"我全完美，怎么才 97%"。
        private static void DrawStats(ReplayFileInfo info)
        {
            GUILayout.BeginHorizontal();
            Gui.KeyValueNarrow("playback.info.hits", info.HitCount.ToString(), Gui.TextCol);
            Gui.KeyValueNarrow("playback.info.keys", info.KeyCount.ToString(), Gui.TextCol);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Gui.KeyValueNarrow("playback.info.duration", DurationText(info), Gui.TextCol);
            
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Gui.KeyValueNarrow("playback.info.xacc", XaccText(info), Gui.TextCol);
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 判定分布：只画计数 &gt; 0 的桶，一格一个「名字 数字」。
        ///
        /// **不画全 16 桶。**16 个里一局通常只命中 5～8 种，剩下那排 0 会把
        /// 真正要看的那几个稀释掉；而「这个版本没有这个判定」和「这局没出现」
        /// 又会画成一模一样。只画非零 = 这一排就是这一局的分布本身。
        /// </summary>
        private static void DrawJudgments(ReplayFileInfo info)
        {
            if (info.JudgmentCounts == null) return;
            bool any = false;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("playback.info.judgments"), Gui.SmallStyle,
                GUILayout.Width(Gui.LabelWNarrow));
            for (int row = 0; row < ReplayFileInfo.JudgmentRowCount; row++)
            {
                if (!info.JudgmentRow(row, out string key, out int count)) continue;
                if (any) GUILayout.Space(6f);
                GUILayout.Label(Loc.F(key, count), Gui.SmallStyle, GUILayout.ExpandWidth(false));
                any = true;
            }
            if (!any)
            {
                GUILayout.Label(Loc.T("playback.info.na"), Gui.SmallStyle, GUILayout.ExpandWidth(true));
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(2f);
        }

        /// <summary>最后一行：文件名 · 大小 · 修改时间 · 加密与否 · 模组版本。排查用，压暗。</summary>
        private static void DrawFileLine(ReplayFileInfo info)
        {
            string enc = Loc.T(info.Encrypted ? "playback.info.encrypted" : "playback.info.plain");
            string size = SizeText(info.FileBytes);
            string time = info.FileTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            string ver = string.IsNullOrEmpty(info.ModVersionText) ? "?" : info.ModVersionText;
            GUILayout.Label(Loc.F("playback.info.fileline", info.FileName, size, time, enc, ver),
                Gui.SmallStyle, GUILayout.ExpandWidth(true));
        }

        private static string SpeedText(ReplayFileInfo info)
        {
            // QuickPitched 时回放倍速才不等于 1；否则存档里那个值只是录制时的快照。
            if (!info.QuickPitched) return "1.00x";
            double s = info.PlaybackSpeed;
            return s > 0.0 ? s.ToString("0.00", CultureInfo.InvariantCulture) + "x" : "1.00x";
        }

        private static string FloorText(ReplayFileInfo info)
            => info.EndTileIndex >= info.StartTileIndex
                ? (info.StartTileIndex + 1) + " → " + (info.EndTileIndex + 1)
                : "?";

        private static string DurationText(ReplayFileInfo info)
        {
            double s = info.DurationSeconds;
            if (s <= 0.0) return Loc.T("playback.info.na");
            var ts = TimeSpan.FromSeconds(s);
            return ts.TotalMinutes >= 1.0
                ? (int)ts.TotalMinutes + ":" + ts.Seconds.ToString("00")
                : ts.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        private static string XaccText(ReplayFileInfo info)
        {
            double x = info.PercentXacc;
            return x > 0.0 ? (x * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%"
                        : Loc.T("playback.info.na");
        }

        /// <summary>普通 Accuracy。-1 = 这份回放没有判定分布（老文件或失败局）。
        /// 不钳上限：官方公式的 1e-4 奖励能让满分局算出 100.05%，游戏界面
        /// 同样显示成 100.05%，钳成 100 反而与游戏对不上。</summary>
        private static string AccText(ReplayFileInfo info)
        {
            double a = info.Accuracy;
            return a < 0.0 ? Loc.T("playback.info.na")
                           : (a * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }

        internal static string SizeText(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }
    }
}
