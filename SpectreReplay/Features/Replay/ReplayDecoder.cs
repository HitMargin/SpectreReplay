using System;
using System.Globalization;
using static SpectreReplay.Features.Replay.ReplayKeys;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace SpectreReplay.Features.Replay;

internal static class ReplayDecoder
{
    private static string GetStr(Dictionary<string, string> d, string k)
    {
        if (d.TryGetValue(k, out var v) && v != null) return v;
        return "";
    }
    private static int GetInt(Dictionary<string, int> d, string k) => d.ContainsKey(k) ? d[k] : 0;
    private static double GetDbl(Dictionary<string, double> d, string k) => d.ContainsKey(k) ? d[k] : 0.0;
    private static bool GetBool(Dictionary<string, bool> d, string k) => d.ContainsKey(k) && d[k];

    public static Replay Decode(byte[] data)
    {
        var decompressed = Decompress(data);
        using var ms = new MemoryStream(decompressed);
        using var r = new BinaryReader(ms);

        var magic = r.ReadUInt64();
        // 只认一个魔数。旧容器（pre-Spectre 的 "CRPrcplp"）已不再支持——
        // 认它等于把「用旧文件头解出新结构」这种半解析留着，坏了比不支持更难查。
        if (magic != ReplayConstants.Magic)
            throw new InvalidDataException($"Unknown magic: 0x{magic:X}");

        var version = r.ReadInt32();
        if (version > ReplayConstants.FormatVersion)
            throw new InvalidDataException($"Format version {version} too new");

        var replay = new Replay();
        replay.Metadata = ReadMetadata(r);
        if (version >= 2)
        {
            string endTimeStr = r.ReadString();
            if (!string.IsNullOrEmpty(endTimeStr))
                replay.EndTime = DateTimeOffset.Parse(endTimeStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        replay.KeyEvents = ReadKeyEvents(r);
        replay.HitContexts = ReadHitContexts(r);
        return replay;
    }

    public static bool IsNewFormat(byte[] data)
    {
        if (data.Length < 8) return false;
        var magic = BitConverter.ToUInt64(data, 0);
        // 只认本格式魔数。旧容器的 `LegacyMagic` 已随旧格式适配一起删除。
        return magic == ReplayConstants.Magic;
    }

    // 每条记录的最小字节数：用于把文件里的 count 收敛到"流里真能装下这么多条"，
    // 避免损坏/截断文件把 count 读成垃圾值后直接拿去做 List 容量
    private const int MinKeyEventBytes = 8 + 2 + 1;      // double + ushort + bool
    private const int MinHitContextBytes = 4 + 8 + 4 + 1 + 1 + 1 + 8 + 8 + 1 + 1 + 4 + 1; // 含末尾 HitTick 最小 1B

    // count 来自文件内容，不是可信输入：负数（最高位置 1）会让 List 容量抛
    // ArgumentOutOfRangeException，巨大值会在读到数据前就 OutOfMemoryException。
    // 上界取"剩余字节数 / 单条最小字节"，既挡住垃圾值又不误伤正常大谱面。
    private static int ReadCount(BinaryReader r, int minRecordBytes)
    {
        int raw = r.ReadInt32();
        if (raw <= 0)
        {
            if (raw < 0)
                throw new InvalidDataException($"Negative record count: {raw}");
            return 0;
        }
        long remaining = r.BaseStream.Length - r.BaseStream.Position;
        long maxByStream = remaining / minRecordBytes;
        if (raw > maxByStream)
            throw new InvalidDataException(
                $"Record count {raw} exceeds remaining stream ({remaining} bytes)");
        return raw;
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        // 解压炸弹防护：gzip 头里的原始长度不可信（可伪造），改为边解压边限量。
        // 上限取 512MB——远超任何真实回放（百万级事件 ≈ 十几 MB），但能挡住
        // 畸形文件把内存吃光导致游戏直接崩溃。
        const long maxDecompressed = 512L * 1024 * 1024;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maxDecompressed)
                throw new InvalidDataException("Decompressed replay exceeds 512MB limit");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static Replay.ReplayMetadata ReadMetadata(BinaryReader r)
    {
        var magic = r.ReadUInt64();
        if (magic != ReplayConstants.MagicMetadata)
            throw new InvalidDataException($"Bad metadata magic: 0x{magic:X}");

        var ver = r.ReadInt32();
        // 段版本上界校验（与 ReadKeyEvents/ReadHitContexts 同理）：metadata 尾部按
        // "ver >= 2"追加 PercentXacc/MaximumUsedKeys/JudgmentList，遇到未来版本
        // 会被按 v2 解析而**跳过新增字段**，后续段整体错位 → 加载失败或回放错乱
        if (ver > ReplayConstants.MetadataVersion)
            throw new InvalidDataException(
                $"Metadata version {ver} too new (known: {ReplayConstants.MetadataVersion})");
        var m = new Replay.ReplayMetadata
        {
            StartingFloorId = r.ReadInt32(),
            TotalFloorCount = r.ReadInt32(),
            SceneName = r.ReadString(),
            SongName = r.ReadString(),
            ArtistName = r.ReadString(),
            FileName = r.ReadString(),
            JudgeMode = r.ReadString(),
            LevelPath = r.ReadString(),
            InternalLevelName = r.ReadString(),
            HitMarginLimit = r.ReadString(),
            HoldBehavior = r.ReadString(),
            LoadedMods = r.ReadString(),
            DeviceID = r.ReadString(),
            ModVersion = r.ReadString(),
            KeybdSoundFileName = r.ReadString(),
            KeybdSoundHash = r.ReadString(),
            StartTile = r.ReadInt32(),
            FloorHash = r.ReadInt32(),
            SpeedHash = r.ReadInt32(),
            TimeHash = r.ReadInt32(),
            Pitch = r.ReadInt32(),
            LevelID = r.ReadInt32(),
            AudioBufferSize = r.ReadInt32(),
            Bpm = r.ReadDouble(),
            SpeedTrail = r.ReadDouble(),
            PlaybackSpeed = r.ReadDouble(),
            KeybdSoundStartTick = r.ReadDouble(),
            IsOfficialLevel = r.ReadBoolean(),
            SpeedTrailMode = r.ReadBoolean(),
            QuickPitched = r.ReadBoolean(),
            IfNoFail = r.ReadBoolean(),
        };
        var startTimeMs = r.ReadInt64();
        m.StartTime = startTimeMs == long.MinValue ? null : DateTimeOffset.FromUnixTimeMilliseconds(startTimeMs);
        m.SpVersion = r.ReadString();
        if (ver >= 2)
        {
            m.PercentXacc = r.ReadDouble();
            m.MaximumUsedKeys = r.ReadInt32();
            m.JudgmentList = r.ReadString();
        }
        return m;
    }

    private static List<KeyEvent> ReadKeyEvents(BinaryReader r)
    {
        var magic = r.ReadUInt64();
        if (magic != ReplayConstants.MagicKeyEvent)
            throw new InvalidDataException($"Bad key event magic: 0x{magic:X}");

        var ver = r.ReadInt32();
        // 同 ReadHitContexts：段版本显式上界，避免未来版本按当前布局静默错位
        if (ver > ReplayConstants.KeyEventVersion)
            throw new InvalidDataException(
                $"KeyEvent version {ver} too new (known: {ReplayConstants.KeyEventVersion})");
        var count = ReadCount(r, MinKeyEventBytes);
        var events = new List<KeyEvent>(count);
        for (int i = 0; i < count; i++)
            events.Add(new KeyEvent
            {
                SongPosition = r.ReadDouble(),
                KeyCode = r.ReadUInt16(),
                IsPressed = r.ReadBoolean()
            });
        return events;
    }

    private static List<HitContext> ReadHitContexts(BinaryReader r)
    {
        var magic = r.ReadUInt64();
        if (magic != ReplayConstants.MagicHitContext)
            throw new InvalidDataException($"Bad hit context magic: 0x{magic:X}");

        var ver = r.ReadInt32();
        // 段版本必须显式上界校验：文件级 version 已经查过"不超过 FormatVersion"，
        // 但各段（key events / hit contexts）有各自的版本号。只按"ver >= 2"决定
        // 读不读 HitTick，遇到未来版本（ver=3，尾部追加了新字段）就会**按 v2 解析**：
        // 每条记录少读若干字节，从第 2 条起全部错位 —— 症状是"回放只推进一格就卡住"
        // （floorID 门永不成立），且没有任何报错。宁可明确拒绝，也别静默错位。
        if (ver > ReplayConstants.HitContextVersion)
            throw new InvalidDataException(
                $"HitContext version {ver} too new (known: {ReplayConstants.HitContextVersion})");
        var count = ReadCount(r, MinHitContextBytes);
        var contexts = new List<HitContext>(count);
        for (int i = 0; i < count; i++)
        {
            contexts.Add(new HitContext
            {
                CurrentFloorID = r.ReadInt32(),
                CurrAngle = r.ReadDouble(),
                OverloadCounter = r.ReadSingle(),
                NoFailHit = r.ReadBoolean(),
                IsAuto = r.ReadBoolean(),
                NextFloorAuto = r.ReadBoolean(),
                CachedAngle = r.ReadDouble(),
                TargetExitAngle = r.ReadDouble(),
                MidspinInfiniteMargin = r.ReadBoolean(),
                RDC_auto = r.ReadBoolean(),
                curFreeRoamSection = r.ReadInt32()
            });
            // v2 起编码端每条末尾追加 HitTick：HasValue(1B) + 有值时 long(8B)。
            // 此前解码端不读这两项——每条错位 9 字节，第 2 条起 CurrentFloorID/
            // CurrAngle 全成垃圾，回放只推进一格就永远卡住（floorID 门永不成立）。
            // v1 旧文件无此追加，按版本跳过保持兼容
            if (ver >= 2)
            {
                bool hasTick = r.ReadBoolean();
                contexts[i].HitTick = hasTick ? r.ReadInt64() : (long?)null;
            }
        }
        return contexts;
    }

    public static Replay ConvertFromOldFormat(LegacyReplayData old)
    {
        if (old == null) return null;
        var replay = new Replay();
        var m = replay.Metadata;

        m.SceneName = GetStr(old.strings, SceneName);
        m.SongName = GetStr(old.strings, SongName);
        m.ArtistName = GetStr(old.strings, ArtistName);
        m.FileName = GetStr(old.strings, FileName);
        m.JudgeMode = GetStr(old.strings, JudgeMode);
        m.LevelPath = GetStr(old.strings, LevelPath);
        m.InternalLevelName = GetStr(old.strings, InternalLevelName);
        m.HitMarginLimit = GetStr(old.strings, HitMarginLimitKey);
        m.HoldBehavior = GetStr(old.strings, HoldBehaviorKey);
        m.LoadedMods = GetStr(old.strings, LoadedMods);
        m.DeviceID = GetStr(old.strings, DeviceID);
        m.ModVersion = GetStr(old.strings, ModVersion);
        m.KeybdSoundFileName = GetStr(old.strings, KeybdSoundFileName);
        m.KeybdSoundHash = GetStr(old.strings, KeybdSoundHash);

        m.StartTile = GetInt(old.ints, StartTile);
        m.TotalFloorCount = GetInt(old.ints, EndTile) + 1;
        m.FloorHash = GetInt(old.ints, FloorHash);
        m.SpeedHash = GetInt(old.ints, SpeedHash);
        m.TimeHash = GetInt(old.ints, TimeHash);
        m.Pitch = GetInt(old.ints, Pitch);
        m.LevelID = GetInt(old.ints, LevelID);
        m.AudioBufferSize = GetInt(old.ints, AudioBufferSize);

        m.Bpm = GetDbl(old.doubles, Bpm);
        m.SpeedTrail = GetDbl(old.doubles, SpeedTrail);
        m.PlaybackSpeed = GetDbl(old.doubles, PlaybackSpeed);
        m.KeybdSoundStartTick = GetDbl(old.doubles, KeybdSoundStartTick);

        m.IsOfficialLevel = GetBool(old.bools, IsOfficialLevel);
        m.SpeedTrailMode = GetBool(old.bools, SpeedTrailMode);
        m.QuickPitched = GetBool(old.bools, QuickPitched);
        m.IfNoFail = GetBool(old.bools, IfNoFail);

        m.PercentXacc = GetDbl(old.doubles, PercentXacc);
        m.MaximumUsedKeys = GetInt(old.ints, MaximumUsedKeys);
        m.JudgmentList = GetStr(old.strings, JudgmentList);

        replay.KeyEvents = old.KeyEvent_list.ConvertAll(k => new KeyEvent
        {
            KeyCode = k.KeyCode,
            IsPressed = k.IsPressed,
            SongPosition = k.SongPosition
        });

        replay.HitContexts = old.HitContext_list;
        return replay;
    }

    public static LegacyReplayData ConvertToOldFormat(Replay replay)
    {
        var old = new LegacyReplayData();
        old.reset();
        var m = replay.Metadata;

        old.strings[SceneName] = m.SceneName;
        old.strings[SongName] = m.SongName;
        old.strings[ArtistName] = m.ArtistName;
        old.strings[FileName] = m.FileName;
        old.strings[JudgeMode] = m.JudgeMode;
        old.strings[LevelPath] = m.LevelPath;
        old.strings[InternalLevelName] = m.InternalLevelName;
        old.strings[HitMarginLimitKey] = m.HitMarginLimit;
        old.strings[HoldBehaviorKey] = m.HoldBehavior;
        old.strings[LoadedMods] = m.LoadedMods;
        old.strings[DeviceID] = m.DeviceID;
        old.strings[ModVersion] = m.ModVersion;
        old.strings[KeybdSoundFileName] = m.KeybdSoundFileName;
        old.strings[KeybdSoundHash] = m.KeybdSoundHash;

        old.ints[StartTile] = m.StartTile;
        old.ints[EndTile] = m.TotalFloorCount - 1;
        old.ints[FloorHash] = m.FloorHash;
        old.ints[SpeedHash] = m.SpeedHash;
        old.ints[TimeHash] = m.TimeHash;
        old.ints[Pitch] = m.Pitch;
        old.ints[LevelID] = m.LevelID;
        old.ints[AudioBufferSize] = m.AudioBufferSize;

        old.doubles[Bpm] = m.Bpm;
        old.doubles[SpeedTrail] = m.SpeedTrail;
        old.doubles[PlaybackSpeed] = m.PlaybackSpeed;
        old.doubles[KeybdSoundStartTick] = m.KeybdSoundStartTick;

        old.bools[IsOfficialLevel] = m.IsOfficialLevel;
        old.bools[SpeedTrailMode] = m.SpeedTrailMode;
        old.bools[QuickPitched] = m.QuickPitched;
        old.bools[IfNoFail] = m.IfNoFail;

        old.doubles[PercentXacc] = m.PercentXacc;
        old.ints[MaximumUsedKeys] = m.MaximumUsedKeys;
        old.strings[JudgmentList] = m.JudgmentList;

        foreach (var k in replay.KeyEvents)
            old.KeyEvent_list.Add(new KeyEvent
            {
                KeyCode = k.KeyCode,
                IsPressed = k.IsPressed,
                SongPosition = k.SongPosition
            });

        old.HitContext_list = replay.HitContexts;
        return old;
    }
}
