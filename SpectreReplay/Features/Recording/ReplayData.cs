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

    /// <summary>
    /// 一次命中发生时，行星/玩家的全部关键状态。
    ///
    /// 照 Spectre 的 Features/Replay/HitContext.cs（12 个字段）。
    ///
    /// **为什么必须有这条轨，而不能只靠按键轨。** 按键轨（KeyEvent）把
    /// 「玩家按了哪个键」喂回游戏，走完全相同的判定链路。但**判定结果不
    /// 一定相同**：游戏按当帧的行星角度算判定窗口，而回放时注入的按键落在
    /// 录制容差之外是完全正常的（量子化到一帧 + 注入延迟）。一旦某次判定
    /// 判成 Fail/Overload，行星就停在原地不前进——「回放卡住了」。
    ///
    /// 状态轨则把命中瞬间的内部状态**原样写回去**：angle、targetExitAngle、
    /// overloadCounter、midspinInfiniteMargin 全部按录制值覆盖，
    /// 判定结果因此与录制一致。Spectre 是**双轨**回放（见
    /// Patch_Playback.cs:99-161）：两条轨都跑完才算回放结束。
    ///
    /// 字段一个不多一个不少地照抄 Spectre——不是抄懒，是这些字段**各自**
    /// 都会被回放路径读写，少一个就有一处状态被回放污染。
    /// </summary>
    internal struct ReplayHitContext
    {
        /// <summary>命中时的行星角度（已按 isCW 取正负，见 AngleOf）。</summary>
        internal float Angle;

        /// <summary>命中时的绝对角度（chosenPlanet.angle 原值）。</summary>
        internal double CachedAngle;

        /// <summary>命中时的目标退出角度。</summary>
        internal double TargetExitAngle;

        /// <summary>命中时是否处于 noFail 无限判定。</summary>
        internal bool NoFailHit;

        /// <summary>命中时是否由 auto 触发。</summary>
        internal bool IsAuto;

        /// <summary>下一块砖是否是 auto。</summary>
        internal bool NextFloorAuto;

        /// <summary>命中时录制到的 RDC.auto。</summary>
        internal bool RdcAuto;

        /// <summary>命中时是否处于 midspin 无限判定。</summary>
        internal bool MidspinInfiniteMargin;

        /// <summary>命中时的 overload 计数（必须原样写回，否则过载条对不上）。</summary>
        internal float OverloadCounter;

        /// <summary>命中时的地板序号（回放时用于对位，见 HitCursor 同步）。</summary>
        internal int CurrentFloorId;

        /// <summary>命中时的自由漫游区段序号（自由漫游里地板序号会重复）。</summary>
        internal int FreeRoamSection;
    }

    /// <summary>一次游玩采到的全部数据。</summary>
    internal sealed class ReplayData
    {
        internal string SongId = "";
        internal string SongName = "";
        internal string DifficultyId = "";
        internal string DifficultyName = "";
        internal string Mapper = "";

        // ── 校验用元数据（照 Spectre 的 ReplayKeys 分组）────────────
        //
        // 这些字段**不用于展示，只用于回放前的完整性校验**。
        // 没有它们就无法判断「这个回放是不是在这个谱面、这个难度、
        // 这个速度下录的」——而谱面一改，同一个地板序号就对应不同的砖，
        // 回放会在错误的位置触发判定，且没有任何报错。
        //
        // Spectre 把这些存在弱类型字典里（LegacyReplayData.strings/ints/…），
        // 好处是能承载「本版本根本没有的字段」。这里功能少，用强类型字段，
        // 换来编译期检查与更短的代码。

        /// <summary>录制时的场景名（scnGame / scnEditor）。</summary>
        internal string SceneName = "";

        /// <summary>判定难度名（GCS.difficulty.ToString()）。</summary>
        internal string JudgeMode = "";

        /// <summary>官方关卡的内置关卡名（内部关卡才非空）。</summary>
        internal string InternalLevelName = "";

        /// <summary>是否官方关卡。</summary>
        internal bool IsOfficialLevel;

        /// <summary>起点的地板序号（GCS.checkpointNum）。</summary>
        internal int StartTile;

        /// <summary>终点的地板序号（通关时的 currentSeqID）；未通关为 -1。</summary>
        internal int EndTile = -1;

        /// <summary>
        /// 谱面角度序列的指纹。
        ///
        /// **int.MinValue 表示"没记到"**，校验器见到就跳过谱面检查。
        /// 官方关卡记 0（合法值，与 Spectre 一致，见 Patch_Lifecycle.cs:157-159）。
        /// 默认值不给 0：0 是官方谱的合法指纹，用它当默认值会让
        /// 「没记到」伪装成「官方谱」，从而静默跳过了本该做的校验。
        /// </summary>
        internal int FloorHash = int.MinValue;

        /// <summary>谱面移调（半音）。</summary>
        internal int Pitch;

        /// <summary>谱面 BPM。</summary>
        internal double Bpm;

        /// <summary>录制时是否开了 noFail。</summary>
        internal bool IfNoFail;

        /// <summary>
        /// 倍速模式下的实际速度倍率。
        ///
        /// **<= 0 表示"没记到"**，校验器见到就跳过倍速检查。
        /// 默认值不给 1.0：1.0 是一个合法的真实倍率，用它当"没记到"的默认值，
        /// 会让「没记到」与「确实是 1 倍速」无法区分——
        /// 前者该放行，后者该拦。
        /// </summary>
        internal double SpeedTrail;

        /// <summary>是否处于倍速模式。</summary>
        internal bool SpeedTrailMode;

        /// <summary>
        /// 录制时的判定窗口（HitMarginLimit 枚举的数值）。
        ///
        /// 回放时应用它，飘字与判定结果才与录制时一致（照 Spectre 的
        /// StoredHitMarginLimit，见 Patch_Lifecycle.cs:219-223）。
        ///
        /// 0 = 不设限。默认值给 0 是**有意**的：读不到时回放必须全部收下
        /// 注入的按键，判太严会让回放中途卡住——那比判定档位显示错更糟。
        /// </summary>
        internal int HitMarginLimit;

        /// <summary>通关时间（歌曲时间轴上的终点）。</summary>
        internal double Length;

        /// <summary>玩家输入（已按时间升序）。</summary>
        internal readonly List<ReplayKeyEvent> Keys = new List<ReplayKeyEvent>();

        /// <summary>各判定的计数。键是 HitMargin 的成员名，值是次数。</summary>
        internal readonly Dictionary<string, int> Margins = new Dictionary<string, int>();

        /// <summary>各判定的首次时刻（用于回放时的飘字对齐）。键同上。</summary>
        internal readonly Dictionary<string, List<double>> MarginTimes =
            new Dictionary<string, List<double>>();

        /// <summary>
        /// 状态轨：每次命中按时间升序。与 Keys 并行存在，**不替代**它。
        /// 键轨负责让游戏「动起来」，状态轨负责让判定「复现」。
        /// </summary>
        internal readonly List<ReplayHitContext> Hits = new List<ReplayHitContext>();

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
        private const int Version = 4;

        /// <summary>
        /// 状态轨每条的上限。
        ///
        /// 读取时**必须**有上限：段头给的是文件自报的条数，一个损坏或
        /// 恶意构造的文件能让它变成 int.MaxValue，没上限就是当场
        /// new List(2G) 或在分配 Path 上耗尽内存——而整个读取过程
        /// 就在玩家的按键事件里同步发生，界面直接卡死。
        /// 正常一局几千条，这个上限高到不可能误伤真的回放。
        /// </summary>
        private const int MaxHits = 2_000_000;

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

                // v2：校验用元数据。放在 Keys 之前，且**整体按版本号读写**——
                // 只按字段逐个加会让 v1 文件从这里起整体错位，
                // 症状是「回放只推进一格就卡住」且没有任何报错。
                // 照 Spectre ReplayDecoder 的教训（段版本必须显式上界校验）。
                WriteStr(w, data.SceneName);
                WriteStr(w, data.JudgeMode);
                WriteStr(w, data.InternalLevelName);
                w.Write(data.IsOfficialLevel);
                w.Write(data.StartTile);
                w.Write(data.EndTile);
                w.Write(data.FloorHash);
                w.Write(data.Pitch);
                w.Write(data.Bpm);
                w.Write(data.IfNoFail);
                w.Write(data.SpeedTrail);
                w.Write(data.SpeedTrailMode);
                // v3：录制时的判定窗口，回放时应用
                w.Write(data.HitMarginLimit);
                // v4：状态轨条数。
                //
                // **条数必须写在定长头里，不能像 Margins 那样把集合放在变长段里。**
                // 变长段必须整体连续才能跳过；一旦把状态轨塞在 Keys 之后，
                // 读 v1~v3 文件时就得为了跳过它而**解析整个 Keys 段**——
                // 那是为了一份用不上的数据去走一遍最长的解析路径。
                // 放定长头里，v1~v3 的读取逻辑一行都不用改。
                w.Write(data.Hits.Count);

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
                // 状态轨的实体段。放最后，读端也可以最后才解析——
                // 就算这里有脏数据，前面的 Margins/Keys 已经读完了。
                foreach (var h in data.Hits)
                {
                    w.Write(h.Angle);
                    w.Write(h.CachedAngle);
                    w.Write(h.TargetExitAngle);
                    w.Write(h.NoFailHit);
                    w.Write(h.IsAuto);
                    w.Write(h.NextFloorAuto);
                    w.Write(h.RdcAuto);
                    w.Write(h.MidspinInfiniteMargin);
                    w.Write(h.OverloadCounter);
                    w.Write(h.CurrentFloorId);
                    w.Write(h.FreeRoamSection);
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
                // v2 才有的校验元数据。v1 文件没有这些字段，
                // 读完 Length 之后紧接的就是 Margins 段——
                // 所以必须按版本号整体跳过，**不能逐字段判存在性**。
                if (version >= 2)
                {
                    d.SceneName = ReadStr(r);
                    d.JudgeMode = ReadStr(r);
                    d.InternalLevelName = ReadStr(r);
                    d.IsOfficialLevel = r.ReadBoolean();
                    d.StartTile = r.ReadInt32();
                    d.EndTile = r.ReadInt32();
                    d.FloorHash = r.ReadInt32();
                    d.Pitch = r.ReadInt32();
                    d.Bpm = r.ReadDouble();
                    d.IfNoFail = r.ReadBoolean();
                    d.SpeedTrail = r.ReadDouble();
                    d.SpeedTrailMode = r.ReadBoolean();
                    // v3 才有。按版本号读，不逐字段判存在性——
                    // 理由见上面那段注释。
                    if (version >= 3)
                    {
                        d.HitMarginLimit = r.ReadInt32();
                    }
                }
                // v4 才有：状态轨条数。v1~v3 文件到这里直接进 Margins 段，
                // 所以这个 int32 **只在 version >= 4 时读**。
                int hitCount = 0;
                if (version >= 4)
                {
                    hitCount = r.ReadInt32();
                    if (hitCount < 0 || hitCount > MaxHits)
                    {
                        throw new System.IO.InvalidDataException(
                            string.Format(Ui.Loc.T("render.err.checkpoints"), hitCount));
                    }
                }
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
                // 状态轨实体段在 Keys 之后（见 Write 里的说明）。
                //
                // 读取顺序必须与写入顺序**完全一致**。这里与 Keys 的顺序
                // 是反的（写：Hits → Keys；读：Keys → Hits），因为 Keys
                // 是格式里最老的一段、位置不能动——动它就要求 v1 也要改。
                if (hitCount > 0)
                {
                    d.Hits.Capacity = hitCount;
                    for (int i = 0; i < hitCount; i++)
                    {
                        d.Hits.Add(new ReplayHitContext
                        {
                            Angle = r.ReadSingle(),
                            CachedAngle = r.ReadDouble(),
                            TargetExitAngle = r.ReadDouble(),
                            NoFailHit = r.ReadBoolean(),
                            IsAuto = r.ReadBoolean(),
                            NextFloorAuto = r.ReadBoolean(),
                            RdcAuto = r.ReadBoolean(),
                            MidspinInfiniteMargin = r.ReadBoolean(),
                            OverloadCounter = r.ReadSingle(),
                            CurrentFloorId = r.ReadInt32(),
                            FreeRoamSection = r.ReadInt32(),
                        });
                    }
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