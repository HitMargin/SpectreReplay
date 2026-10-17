namespace SpectreReplay.Features.Replay;

internal static class ReplayConstants
{
    public const ulong Magic = 0x6C70_7263_5250_53;      // "SPRrcplp"
    public const int FormatVersion = 2;

    // 原先这里还有 `LegacyMagic = "CRPrcplp"`（pre-Spectre 旧容器）。
    // 2026-10-18 用户裁定删掉旧回放文件适配时一并移除：解码端不再认第二个魔数，
    // 旧文件由 `ReplayManager.LoadReplay` 报「文件头不匹配」而不是半解析。

    public const ulong MagicMetadata = 0x61_7461_6461_7465_4D;
    public const int MetadataVersion = 2;

    public const ulong MagicKeyEvent = 0x74_6E65_7645_7965_4B;
    public const int KeyEventVersion = 1;

    public const ulong MagicHitContext = 0x74_7865_7443_7469_48;
    // v2：追加 HitContext.HitTick（r150+ 异步输入音频 tick）。
    // 解码端按版本分支读取，v1 旧谱 HitTick 保持 null
    public const int HitContextVersion = 2;

    public const string Extension = ".sprp";
    public const string ExtensionNoEncrypt = ".psprp";
}
