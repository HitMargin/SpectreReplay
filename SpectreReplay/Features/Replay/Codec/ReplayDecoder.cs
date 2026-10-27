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
        // 只认一个魔数，其它文件头一律拒绝。
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
        replay.CursorTrack = ReadCursorTrackIfPresent(r);
        replay.KeybdAudio = ReadKeybdAudioIfPresent(r);
        replay.KeybdPrefs = ReadKeybdPrefsIfPresent(r);
        return replay;
    }

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
    private const int MinHitSnapshotBytesV1 = 4 + 8 + 4 + 1 + 1 + 1 + 8 + 8 + 1 + 1 + 4;   // v1：无 HitTick
    private const int MinHitSnapshotBytes = MinHitSnapshotBytesV1 + 1;  // v2：末尾 HitTick 标记最小 1B

    // **最小长度必须随段版本走。** v1 记录是 41 B（没有末尾 HitTick），若一律按 v2 的
    // 42 B 收敛，`remaining / 42` 对任意 N 条都算出 N-1，于是**每一份 v1 旧回放**
    // （r149 及更早、含上游 Spectre 的存量）都会在 ReadCount 里被当成"count 超过流长度"
    // 直接拒绝，报成"文件损坏"。这不是理论风险：v1 文件里快照段之后就是 EOF，
    // remaining 精确等于 41×count，floor(41N/42) < N 对 N≥1 恒成立。
    private const int MinCursorSampleBytes = 8 + 4 + 4;  // double + float + float

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

    // count 过了 ReadCount 的比例校验，但**仍不能直接拿来当 List 的初始容量**：
    // 校验只保证「按最小记录长度算，流里装得下这么多条」，而一个几百 KB 的 .psprp
    // （无 MAC，任何人可造）可以把 count 声明成 remaining/11，于是预分配在先、
    // 读到第二条就失败在后——分配本身已经发生，几百 KB 换出的是 GB 级内存。
    // 所以初始容量封顶；真有多条时 List 自然倍增，代价只是几次数组拷贝。
    private const int MaxPreallocRecords = 4096;

    private static List<T> NewRecordList<T>(int count)
        => new List<T>(count < MaxPreallocRecords ? count : MaxPreallocRecords);

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
        // v3 起"没有基准"写成 NaN，0 是合法基准。旧 v2 用 0 当哨兵，这里迁成 NaN——
        // 与旧回放侧"拒 0"的既有行为完全一致，只让新录的回放能表达真实 0 基准。
        if (ver < 3 && m.KeybdSoundStartTick == 0.0)
            m.KeybdSoundStartTick = double.NaN;
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
        var events = NewRecordList<KeyEvent>(count);
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
        // 按段版本选最小记录长度：v1 是 41 B、v2 是 42 B（见 MinHitSnapshotBytesV1 处注释）
        var count = ReadCount(r, ver >= 2 ? MinHitSnapshotBytes : MinHitSnapshotBytesV1);
        var contexts = NewRecordList<HitSnapshot>(count);
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
            if (ver >= 2)
            {
                bool hasTick = r.ReadBoolean();
                contexts[i].HitTick = hasTick ? r.ReadInt64() : (long?)null;
            }
        }
        return contexts;
    }

    /// <summary>
    /// 尾部可选段：指针轨迹。旧文件在此处已到结尾（或带未知尾部数据）——
    /// 探测到本模组的魔数才读，否则连同其余字节一并忽略，返回空轨迹。
    /// 旧构建读新文件也是同一条路（读完 HitSnapshots 即止），双向兼容
    /// 不动任何版本号。读到了就按 HitSnapshots 同一套规则校验段版本与 count。
    /// </summary>
    private static List<CursorSample> ReadCursorTrackIfPresent(BinaryReader r)
    {
        long remaining = r.BaseStream.Length - r.BaseStream.Position;
        if (remaining < sizeof(ulong) + sizeof(int))
            return [];
        long start = r.BaseStream.Position;
        var magic = r.ReadUInt64();
        if (magic != ReplayConstants.MagicCursorTrack)
        {
            // 未知尾部：可能是更晚版本的段（现在是键盘音段）。
            // **必须回退位置**——原实现直接 return，位置已经前进了 8 字节，
            // 后续段的读取会整体错位 8 字节，把魔数读成垃圾值。
            r.BaseStream.Position = start;
            return [];
        }
        var ver = r.ReadInt32();
        if (ver > ReplayConstants.CursorTrackVersion)
            throw new InvalidDataException(
                $"CursorTrack version {ver} too new (known: {ReplayConstants.CursorTrackVersion})");
        var count = ReadCount(r, MinCursorSampleBytes);
        var track = NewRecordList<CursorSample>(count);
        for (int i = 0; i < count; i++)
            track.Add(new CursorSample
            {
                SongPosition = r.ReadDouble(),
                X = r.ReadSingle(),
                Y = r.ReadSingle()
            });
        return track;
    }

    /// <summary>
    /// 尾部可选段：嵌入的键盘音（位于指针轨迹之后）。
    ///
    /// <para>没有这一段不是错误——旧回放的音频在同目录同名 .wav 里，
    /// 由 <c>ReplayManager.TryLoadKeybdSound</c> 负责回落。所以本方法
    /// 探测不到魔数时返回 <c>default</c> 而不是抛异常。</para>
    ///
    /// <para>长度字段一律按「不超过剩余字节」校验：负载长度来自文件，
    /// 是不可信输入，直接拿它分配内存可以被一个几字节的畸形文件撑爆。</para>
    /// </summary>
    private static KeybdAudioBlob ReadKeybdAudioIfPresent(BinaryReader r)
    {
        long remaining = r.BaseStream.Length - r.BaseStream.Position;
        // 魔数 8 + 段版本 4 + 编码 4 + 采样率 4 + 声道 4 + 样本数 4 + 长度 4
        const int HeadLen = 8 + 4 + 4 + 4 + 4 + 4 + 4;
        if (remaining < HeadLen)
            return default;
        long start = r.BaseStream.Position;
        var magic = r.ReadUInt64();
        if (magic != ReplayConstants.MagicKeybdAudio)
        {
            r.BaseStream.Position = start;
            return default;
        }
        var ver = r.ReadInt32();
        if (ver > ReplayConstants.KeybdAudioVersion)
            throw new InvalidDataException(
                $"KeybdAudio version {ver} too new (known: {ReplayConstants.KeybdAudioVersion})");
        var blob = new KeybdAudioBlob
        {
            Codec = r.ReadInt32(),
            SampleRate = r.ReadInt32(),
            Channels = r.ReadInt32(),
            SampleCount = r.ReadInt32(),
        };
        int payloadLen = r.ReadInt32();
        long after = r.BaseStream.Length - r.BaseStream.Position;
        if (payloadLen < 0 || payloadLen > after)
            throw new InvalidDataException(
                $"KeybdAudio payload {payloadLen} exceeds remaining stream ({after} bytes)");
        blob.Payload = payloadLen == 0 ? null : r.ReadBytes(payloadLen);
        if (blob.Payload != null && blob.Payload.Length != payloadLen)
            throw new InvalidDataException("KeybdAudio payload truncated");
        return blob;
    }

    /// <summary>
    /// 尾部可选段：这一份回放自带的键盘音播放覆盖值（位于键盘音段之后）。
    ///
    /// <para><b>读不到不是错误。</b>旧回放、以及没有逐份覆盖的录制，都走全局设置；
    /// 所以探测不到魔数时返回 <c>default</c>，而不是抛异常。剩余字节不足同样
    /// 返回 <c>default</c>——旧文件在轨迹段之后就是 EOF，不能因此抛 EndOfStream。</para>
    ///
    /// <para>探测失败一律**回退流位置**：本段前面可能还有更晚的尾部段，
    /// 位置偏了几字节就会把后续魔数读成垃圾值（KeybdAudio 段踩过同一个坑）。</para>
    ///
    /// <para>段版本显式上界：本段布局定长，未来版本若在尾部追加字段，
    /// 按 v1 解析会把后续字节读成垃圾，宁可明确拒绝。</para>
    /// </summary>
    private static KeybdPrefs ReadKeybdPrefsIfPresent(BinaryReader r)
    {
        // 魔数 8 + 段版本 4 + 标志位 1 + 音量 4 + 偏移 4
        const int HeadLen = 8 + 4 + 1 + 4 + 4;
        long remaining = r.BaseStream.Length - r.BaseStream.Position;
        // 不足一个完整段（含 0 字节的旧文件）就什么都不读：这里是「没有覆盖值」，
        // 不是「文件损坏」，任何抛异常都会让存量回放整份加载失败。
        if (remaining < HeadLen)
            return default;
        long start = r.BaseStream.Position;
        var magic = r.ReadUInt64();
        if (magic != ReplayConstants.MagicKeybdPrefs)
        {
            r.BaseStream.Position = start;
            return default;
        }
        var ver = r.ReadInt32();
        if (ver > ReplayConstants.KeybdPrefsVersion)
            throw new InvalidDataException(
                $"KeybdPrefs version {ver} too new (known: {ReplayConstants.KeybdPrefsVersion})");
        byte flags = r.ReadByte();
        int volume = r.ReadInt32();
        int offsetMs = r.ReadInt32();
        // 未置位的字段在流里仍占 4 字节（布局定长），但值无意义：统一归零，
        // 让「没有这一项」只有一种表示，搬运/比较时不会带着文件里的垃圾值。
        bool hasVolume = (flags & ReplayConstants.KeybdPrefsFlagHasVolume) != 0;
        bool hasOffset = (flags & ReplayConstants.KeybdPrefsFlagHasOffset) != 0;
        return new KeybdPrefs
        {
            HasVolume = hasVolume,
            Volume = hasVolume ? volume : 0,
            HasOffset = hasOffset,
            OffsetMs = hasOffset ? offsetMs : 0,
            IsMuted = (flags & ReplayConstants.KeybdPrefsFlagIsMuted) != 0,
        };
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
        // null（未起录）写 NaN 而不是 0：0 已是合法基准，拿它当"没有基准"会让
        // 这类回放在播放侧被对齐到歌曲开头（见 Patch_Playback 的基准判定）。
        m.KeybdSoundStartTick = replayData.KeybdSoundStartTick ?? double.NaN;

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
        replay.CursorTrack = replayData.CursorTrack;
        replay.KeybdAudio = replayData.KeybdAudio;
        replay.KeybdPrefs = replayData.KeybdPrefs;
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
        // NaN（文件里没有基准）还原成 null（本机从未起录）；播放侧两者同样按
        // "没有基准"处理。再存回去时 null 又写成 NaN，往返后语义不变。
        replayData.KeybdSoundStartTick = double.IsNaN(m.KeybdSoundStartTick)
            ? (double?)null
            : m.KeybdSoundStartTick;

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
        replayData.CursorTrack = replay.CursorTrack;
        replayData.KeybdAudio = replay.KeybdAudio;
        replayData.KeybdPrefs = replay.KeybdPrefs;
        return replayData;
    }
}
