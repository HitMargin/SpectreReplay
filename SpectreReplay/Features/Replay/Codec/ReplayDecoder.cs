using System;
using System.Globalization;
using static SpectreReplay.Features.Replay.ReplayKeys;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace SpectreReplay.Features.Replay;

internal static class ReplayDecoder
{
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
        replay.HitSnapshots = ReadHitSnapshots(r);
        return replay;
    }

    /// <summary>
    /// 粗判一段字节像不像本模组的回放。
    ///
    /// **判据必须看最外层**，不能再看魔数：2026-10-18 加了压缩包装层之后，
    /// 文件开头是压缩标记（0/1/2）或旧布局的 gzip 头（0x1F），
    /// 而 <see cref="ReplayConstants.Magic"/> 已经躺在那层里面了。
    /// 旧版判据在这里会恒定返回 false —— 而它零调用点，所以没人发现。
    /// </summary>
    public static bool IsNewFormat(byte[] data)
    {
        if (data == null || data.Length < ReplayConstants.CompressionHeaderLen) return false;
        byte head = data[0];
        return head == ReplayConstants.CompressionRaw
            || head == ReplayConstants.CompressionGzip
            || head == ReplayConstants.CompressionBrotli
            || head == 0x1F;   // 旧布局：整条 gzip 流
    }

    // 每条记录的最小字节数：用于把文件里的 count 收敛到"流里真能装下这么多条"，
    // 避免损坏/截断文件把 count 读成垃圾值后直接拿去做 List 容量
    private const int MinKeyEventBytes = 8 + 2 + 1;      // double + ushort + bool
    private const int MinHitSnapshotBytes = 4 + 8 + 4 + 1 + 1 + 1 + 8 + 8 + 1 + 1 + 4 + 1; // 含末尾 HitTick 最小 1B

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

    private static byte[] Decompress(byte[] data) => ReplayCompression.Decompress(data);

    private static Replay.ReplayMetadata ReadMetadata(BinaryReader r)
    {
        var magic = r.ReadUInt64();
        if (magic != ReplayConstants.MagicMetadata)
            throw new InvalidDataException($"Bad metadata magic: 0x{magic:X}");

        var ver = r.ReadInt32();
        // 段版本上界校验（与 ReadKeyEvents/ReadHitSnapshots 同理）：metadata 尾部按
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
        // 同 ReadHitSnapshots：段版本显式上界，避免未来版本按当前布局静默错位
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

    private static List<HitSnapshot> ReadHitSnapshots(BinaryReader r)
    {
        var magic = r.ReadUInt64();
        if (magic != ReplayConstants.MagicHitSnapshot)
            throw new InvalidDataException($"Bad hit context magic: 0x{magic:X}");

        var ver = r.ReadInt32();
        // 段版本必须显式上界校验：文件级 version 已经查过"不超过 FormatVersion"，
        // 但各段（key events / hit contexts）有各自的版本号。只按"ver >= 2"决定
        // 读不读 HitTick，遇到未来版本（ver=3，尾部追加了新字段）就会**按 v2 解析**：
        // 每条记录少读若干字节，从第 2 条起全部错位 —— 症状是"回放只推进一格就卡住"
        // （floorID 门永不成立），且没有任何报错。宁可明确拒绝，也别静默错位。
        if (ver > ReplayConstants.HitSnapshotVersion)
            throw new InvalidDataException(
                $"HitSnapshot version {ver} too new (known: {ReplayConstants.HitSnapshotVersion})");
        var count = ReadCount(r, MinHitSnapshotBytes);
        var contexts = new List<HitSnapshot>(count);
        for (int i = 0; i < count; i++)
        {
            contexts.Add(new HitSnapshot
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

    public static Replay FromReplayData(ReplayData replayData)
    {
        if (replayData == null) return null;
        var replay = new Replay();
        var m = replay.Metadata;

        // 缺失语义与旧字典版逐字一致：字符串缺失记空串，数值/布尔缺失记 0 / false。
        m.SceneName = replayData.SceneName ?? "";
        m.SongName = replayData.SongName ?? "";
        m.ArtistName = replayData.ArtistName ?? "";
        m.FileName = replayData.FileName ?? "";
        m.JudgeMode = replayData.JudgeMode ?? "";
        m.LevelPath = replayData.LevelPath ?? "";
        m.InternalLevelName = replayData.InternalLevelName ?? "";
        m.HitMarginLimit = replayData.HitMarginLimit ?? "";
        m.HoldBehavior = replayData.HoldBehavior ?? "";
        m.LoadedMods = replayData.LoadedMods ?? "";
        m.DeviceID = replayData.DeviceId ?? "";
        m.ModVersion = replayData.ModVersion ?? "";
        m.KeybdSoundFileName = replayData.KeybdSoundFileName ?? "";
        m.KeybdSoundHash = replayData.KeybdSoundHash ?? "";

        m.StartTile = replayData.StartTile ?? 0;
        // EndTile 缺失时旧版 GetInt 返回 0，于是 TotalFloorCount 落成 1。这个怪癖必须保留，
        // 否则缺字段的回放会从“1 层”变成“0 层”，CRC 与进位判断都会跟着漂。
        m.TotalFloorCount = (replayData.EndTile ?? 0) + 1;
        m.FloorHash = replayData.FloorHash ?? 0;
        m.SpeedHash = replayData.SpeedHash ?? 0;
        m.TimeHash = replayData.TimeHash ?? 0;
        m.Pitch = replayData.Pitch ?? 0;
        m.LevelID = replayData.LevelId ?? 0;
        m.AudioBufferSize = replayData.AudioBufferSize ?? 0;

        m.Bpm = replayData.Bpm ?? 0.0;
        m.SpeedTrail = replayData.SpeedTrail ?? 0.0;
        m.PlaybackSpeed = replayData.PlaybackSpeed ?? 0.0;
        m.KeybdSoundStartTick = replayData.KeybdSoundStartTick ?? 0.0;

        m.IsOfficialLevel = replayData.IsOfficialLevel ?? false;
        m.SpeedTrailMode = replayData.SpeedTrailMode ?? false;
        m.QuickPitched = replayData.QuickPitched ?? false;
        m.IfNoFail = replayData.IfNoFail ?? false;

        m.PercentXacc = replayData.PercentXacc ?? 0.0;
        m.MaximumUsedKeys = replayData.MaximumUsedKeys ?? 0;
        m.JudgmentList = replayData.JudgmentList ?? "";

        replay.KeyEvents = replayData.KeyEvents.ConvertAll(k => new KeyEvent
        {
            KeyCode = k.KeyCode,
            IsPressed = k.IsPressed,
            SongPosition = k.SongPosition
        });

        replay.HitSnapshots = replayData.HitSnapshots;
        return replay;
    }

    public static ReplayData ToReplayData(Replay replay)
    {
        var replayData = new ReplayData();
        replayData.Reset();
        var m = replay.Metadata;

        replayData.SceneName = m.SceneName;
        replayData.SongName = m.SongName;
        replayData.ArtistName = m.ArtistName;
        replayData.FileName = m.FileName;
        replayData.JudgeMode = m.JudgeMode;
        replayData.LevelPath = m.LevelPath;
        replayData.InternalLevelName = m.InternalLevelName;
        replayData.HitMarginLimit = m.HitMarginLimit;
        replayData.HoldBehavior = m.HoldBehavior;
        replayData.LoadedMods = m.LoadedMods;
        replayData.DeviceId = m.DeviceID;
        replayData.ModVersion = m.ModVersion;
        replayData.KeybdSoundFileName = m.KeybdSoundFileName;
        replayData.KeybdSoundHash = m.KeybdSoundHash;

        replayData.StartTile = m.StartTile;
        replayData.EndTile = m.TotalFloorCount - 1;
        replayData.FloorHash = m.FloorHash;
        replayData.SpeedHash = m.SpeedHash;
        replayData.TimeHash = m.TimeHash;
        replayData.Pitch = m.Pitch;
        replayData.LevelId = m.LevelID;
        replayData.AudioBufferSize = m.AudioBufferSize;

        replayData.Bpm = m.Bpm;
        replayData.SpeedTrail = m.SpeedTrail;
        replayData.PlaybackSpeed = m.PlaybackSpeed;
        replayData.KeybdSoundStartTick = m.KeybdSoundStartTick;

        replayData.IsOfficialLevel = m.IsOfficialLevel;
        replayData.SpeedTrailMode = m.SpeedTrailMode;
        replayData.QuickPitched = m.QuickPitched;
        replayData.IfNoFail = m.IfNoFail;

        replayData.PercentXacc = m.PercentXacc;
        replayData.MaximumUsedKeys = m.MaximumUsedKeys;
        replayData.JudgmentList = m.JudgmentList;

        foreach (var k in replay.KeyEvents)
            replayData.KeyEvents.Add(new KeyEvent
            {
                KeyCode = k.KeyCode,
                IsPressed = k.IsPressed,
                SongPosition = k.SongPosition
            });

        replayData.HitSnapshots = replay.HitSnapshots;
        return replayData;
    }
}
