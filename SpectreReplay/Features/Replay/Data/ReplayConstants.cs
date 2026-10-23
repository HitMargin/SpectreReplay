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
    public const int MetadataVersion = 2;

    public const ulong MagicKeyEvent = 0x74_6E65_7645_7965_4B;
    public const int KeyEventVersion = 1;

    public const ulong MagicHitSnapshot = 0x74_7865_7443_7469_48;
    // v2：追加 HitSnapshot.HitTick（r150+ 异步输入音频 tick）。
    // 解码端按版本分支读取，v1 旧谱 HitTick 保持 null
    public const int HitSnapshotVersion = 2;

    public const string Extension = ".sprp";
    public const string ExtensionNoEncrypt = ".psprp";
}