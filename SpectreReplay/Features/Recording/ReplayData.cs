using System;
using System.Collections.Generic;

namespace SpectreReplay.Features.Recording
{
    /// <summary>一次按键（按下或松开）在歌曲时间轴上的位置。</summary>
    internal struct ReplayKeyEvent
    {
        /// <summary>Unity 的 KeyCode（ushort 存，兼容旧回放格式）。</summary>
        internal ushort Key;

        /// <summary>
        /// SkyHook 的 KeyLabel（枚举序号，存 int）。
        ///
        /// **保留字段但当前恒为 0。** 它是为 keyQueue 注入准备的——那条路
        /// 要求 Label 与 Key 同时正确（游戏用 new AsyncKeyCode(Key, Label)
        /// 构造按键标识）。现在改用 Win32 SendInput 注入真实按键，游戏侧
        /// 自己从扫描码算出 Label，这个字段不再参与任何逻辑。
        ///
        /// 留着不为将来，是为了不破坏已落盘的 .srp 文件格式：
        /// 写/读两端的字段布局必须一致，删字段会让所有旧回播放不出来。
        /// </summary>
        internal int Label;

        /// <summary>true = 按下，false = 松开。</summary>
        internal bool IsPressed;

        /// <summary>歌曲时间（秒，关卡起点为 0）。</summary>
        internal double SongPosition;
    }

    /// <summary>一次游玩采到的全部数据。</summary>
    internal sealed class ReplayData
    {
        internal string SongId = "";
        internal string SongName = "";
        internal string DifficultyId = "";
        internal string DifficultyName = "";
        internal string Mapper = "";

        /// <summary>通关时间（歌曲时间轴上的终点）。</summary>
        internal double Length;

        /// <summary>玩家输入（已按时间升序）。</summary>
        internal readonly List<ReplayKeyEvent> Keys = new List<ReplayKeyEvent>();

        /// <summary>各判定的计数。键是 HitMargin 的成员名，值是次数。</summary>
        internal readonly Dictionary<string, int> Margins = new Dictionary<string, int>();

        /// <summary>各判定的首次时刻（用于回放时的飘字对齐）。键同上。</summary>
        internal readonly Dictionary<string, List<double>> MarginTimes =
            new Dictionary<string, List<double>>();

        internal bool IsEmpty => Keys.Count == 0 && Margins.Count == 0;
    }

    /// <summary>
    /// 回放文件的二进制格式。
    ///
    /// 设计取舍——为什么不用 JSON：
    ///   一次游玩轻松上千个按键事件，JSON 写出来是几百 KB 且解析慢；
    ///   二进制定长头 + 变长数组，一个 3 分钟的曲子约 20~40 KB，读写都是毫秒级。
    ///   文件头带魔数与版本，读到不认识的文件直接拒绝而不是产出半截数据。
    /// </summary>
    internal static class ReplayFile
    {
        private const uint Magic = 0x53505250;   // "SRPP"
        private const int Version = 1;

        internal static void Write(string path, ReplayData data)
        {
            using (var fs = new System.IO.FileStream(
                path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
            using (var w = new System.IO.BinaryWriter(fs, System.Text.Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(data.Keys.Count);
                w.Write(data.Margins.Count);
                WriteStr(w, data.SongId);
                WriteStr(w, data.SongName);
                WriteStr(w, data.DifficultyId);
                WriteStr(w, data.DifficultyName);
                WriteStr(w, data.Mapper);
                w.Write(data.Length);

                foreach (var kv in data.Margins)
                {
                    WriteStr(w, kv.Key);
                    w.Write(kv.Value);
                }
                // MarginTimes 单独把自己的**项数**也写一遍。
                // 不靠"写入顺序 == 读取顺序"这种隐式对齐——那需要一份跨方法的
                // 全局键序表，一旦有第二个文件在读就会串行错位。
                //
                // 这里原来只写集合不写计数，读取时却按 marginKinds 循环：
                // Margins 与 MarginTimes 的项数一般不同，**回放文件会当场错位**，
                // 读出来不是数据损坏就是直接抛异常。
                w.Write(data.MarginTimes.Count);
                foreach (var kv in data.MarginTimes)
                {
                    WriteStr(w, kv.Key);
                    w.Write(kv.Value.Count);
                    foreach (double t in kv.Value)
                    {
                        w.Write(t);
                    }
                }
                foreach (var k in data.Keys)
                {
                    w.Write(k.Key);
                    w.Write(k.Label);
                    w.Write(k.IsPressed);
                    w.Write(k.SongPosition);
                }
            }
        }

        internal static ReplayData Read(string path)
        {
            using (var fs = new System.IO.FileStream(
                path, System.IO.FileMode.Open, System.IO.FileAccess.Read))
            using (var r = new System.IO.BinaryReader(fs, System.Text.Encoding.UTF8))
            {
                if (r.ReadUInt32() != Magic)
                {
                    throw new System.IO.InvalidDataException(Ui.Loc.T("replay.err.not_replay"));
                }
                int version = r.ReadInt32();
                if (version > Version)
                {
                    throw new System.IO.InvalidDataException(
                        string.Format(Ui.Loc.T("replay.err.version"), version, Version));
                }
                var d = new ReplayData();
                int keyCount = r.ReadInt32();
                int marginKinds = r.ReadInt32();
                d.SongId = ReadStr(r);
                d.SongName = ReadStr(r);
                d.DifficultyId = ReadStr(r);
                d.DifficultyName = ReadStr(r);
                d.Mapper = ReadStr(r);
                d.Length = r.ReadDouble();
                for (int i = 0; i < marginKinds; i++)
                {
                    d.Margins[ReadStr(r)] = r.ReadInt32();
                }
                                // 头部只写了一个 marginKinds，读取时两个集合却都按它循环——
                // 但 Margins 与 MarginTimes 的项数根本不必相等。写入时
                // 已各自带上自己的计数，这里读自己的那份。
                int marginTimeKinds = r.ReadInt32();
                if (marginTimeKinds < 0 || marginTimeKinds > 1_000_000)
                {
                    throw new System.IO.InvalidDataException(
                        string.Format(Ui.Loc.T("render.err.checkpoints"), marginTimeKinds));
                }
                for (int i = 0; i < marginTimeKinds; i++)
                {
                    string name = ReadStr(r);
                    int n = r.ReadInt32();
                    if (n < 0 || n > 1_000_000)
                    {
                        throw new System.IO.InvalidDataException(string.Format(Ui.Loc.T("render.err.checkpoints"), n));
                    }
                    var list = new List<double>(Math.Min(n, 256));
                    for (int j = 0; j < n; j++)
                    {
                        list.Add(r.ReadDouble());
                    }
                    d.MarginTimes[name] = list;
                }
                d.Keys.Capacity = keyCount;
                for (int i = 0; i < keyCount; i++)
                {
                    d.Keys.Add(new ReplayKeyEvent
                    {
                        Key = r.ReadUInt16(),
                        Label = r.ReadInt32(),
                        IsPressed = r.ReadBoolean(),
                        SongPosition = r.ReadDouble(),
                    });
                }
                return d;
            }
        }

        /// <summary>字符串按"字节长度 + UTF-8 字节"写。
        /// 不直接用 BinaryWriter.Write(string) —— 它内部用 7-bit 变长整数编码长度，
        /// 而读的时候若按定长 int32 读就会错位。读写两端都用同一套显式格式。</summary>
        private static void WriteStr(System.IO.BinaryWriter w, string s)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s ?? string.Empty);
            w.Write(bytes.Length);
            w.Write(bytes);
        }

        private static string ReadStr(System.IO.BinaryReader r)
        {
            int len = r.ReadInt32();
            if (len < 0 || len > 1024)
            {
                throw new System.IO.InvalidDataException(
                    string.Format(Ui.Loc.T("render.err.str_len"), len));
            }
            return System.Text.Encoding.UTF8.GetString(r.ReadBytes(len));
        }
    }
}