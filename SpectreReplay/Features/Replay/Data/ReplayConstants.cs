namespace SpectreReplay.Features.Replay;

internal static class ReplayConstants
{
    public const ulong Magic = 0x6C70_7263_5250_53;      // "SPRrcplp"
    public const int FormatVersion = 2;

    public const byte CompressionRaw = 0;      // 不压缩（小回放省一次 CPU）
    public const byte CompressionGzip = 1;     // 新布局里的 gzip 分支（保留给跨版本读）
    public const byte CompressionBrotli = 2;   // 新布局默认
    public const int CompressionHeaderLen = 5;

    public const ulong MagicMetadata = 0x61_7461_6461_7465_4D;
    // v3：KeybdSoundStartTick 改为 tri-state——"没有基准"写 NaN，不再拿 0 当哨兵
    //     （0 现在是合法基准：录制恰好从歌曲 0 秒起的情况）。仅语义变更，无新增
    //     字段；旧 v2 的 0 在解码时迁成 NaN（见 ReplayDecoder.ReadMetadata）。
    public const int MetadataVersion = 3;

    public const ulong MagicKeyEvent = 0x74_6E65_7645_7965_4B;
    public const int KeyEventVersion = 1;

    public const ulong MagicHitSnapshot = 0x74_7865_7443_7469_48;
    // v2：追加 HitSnapshot.HitTick（r150+ 异步输入音频 tick）。
    // 解码端按版本分支读取，v1 旧谱 HitTick 保持 null
    public const int HitSnapshotVersion = 2;

    // 指针轨迹段：追加在 HitSnapshots 之后、永远是负载的最后一段。
    // 解码器读完 HitSnapshots 不查 EOF，旧构建对新尾部字节天然无视——
    // 本段的存在性就是版本判定，FormatVersion 与各旧段版本都不用动。
    // 魔数按流内小端字节拼出 "MousePts"（与 KeyEvent/HitCtext 同一套惯例）。
    public const ulong MagicCursorTrack = 0x7374_5065_7375_6F4D;
    public const int CursorTrackVersion = 1;

    // 键盘音段：追加在指针轨迹之后，同样是「尾部可选段」。
    // 旧构建读完 CursorTrack 就返回，天然无视这一段；新构建没有这一段时
    // 退回「同目录同名 .wav」的老路径。魔数按流内小端拼出 "KbdAudio"。
    public const ulong MagicKeybdAudio = 0x6F69_6475_4164_624B;
    public const int KeybdAudioVersion = 1;

    // 键盘音播放覆盖段：追加在键盘音段之后，同样属于「尾部可选段」。
    // 旧构建连键盘音段都读不到，更碰不到这一段；新构建没有这一段时
    // 就是「完全跟随全局设置」。魔数按流内小端拼出 "KbdPrefs"。
    public const ulong MagicKeybdPrefs = 0x7366_6572_5064_624B;
    public const int KeybdPrefsVersion = 1;

    // 覆盖段的标志位：某一项「有没有」靠标志位表达，不靠长度——
    // 段布局必须定长，解码端才能按固定偏移跳过；写在常量里是为了让
    // 写入端与读取端共用同一份位定义，避免一边改了另一边没改。
    public const byte KeybdPrefsFlagHasVolume = 1 << 0;
    public const byte KeybdPrefsFlagHasOffset = 1 << 1;
    public const byte KeybdPrefsFlagIsMuted = 1 << 2;

    public const string Extension = ".sprp";
    public const string ExtensionNoEncrypt = ".psprp";
}