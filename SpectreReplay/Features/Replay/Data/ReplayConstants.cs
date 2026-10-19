namespace SpectreReplay.Features.Replay;

internal static class ReplayConstants
{
    public const ulong Magic = 0x6C70_7263_5250_53;      // "SPRrcplp"
    public const int FormatVersion = 2;

    // 原先这里还有 `LegacyMagic = "CRPrcplp"`（pre-Spectre 旧容器）。
    // 2026-10-18 用户裁定删掉旧回放文件适配时一并移除：解码端不再认第二个魔数，
    // 旧文件由 `ReplayManager.LoadReplay` 报「文件头不匹配」而不是半解析。

    // ── 压缩包装层（2026-10-18 换 Brotli）────────────────────────────
    //
    // 换压缩算法必须能认出**旧文件**，否则存量 `.sprp` 全部读不开。做法是在
    // 压缩流前面加一层 5 字节包装：`1 字节算法 + 4 字节原文长度`。
    //
    // **为什么不用「试解 gzip 失败再试 Brotli」**：那是抛异常驱动的格式探测，
    // 损坏文件会在两条路径上都失败，最终报出来的错误与「格式不认识」分不开；
    // 而且 Brotli 解随机字节有小概率不抛异常而是吐出垃圾，于是"坏文件"
    // 被当成"合法但内容乱"喂进解码器。
    //
    // **旧文件没有这层包装**：解密之后整个负载就是一条 gzip 流，首字节恒为 0x1F。
    // 新布局的算法号只有 0/1/2，与 0x1F 不相交，所以「读第一个字节」就够。
    // （磁盘上的 `.sprp` 首字节是密文随机值，压缩层在加密层里面。）
    //
    // 实测收益（本机真实回放 908118 B 明文）：
    //   gzip/Optimal 469658 B → Brotli/Optimal 430116 B，**小 8.4%**。
    //   Brotli 耗时 40ms vs gzip 25ms。回放保存是一局一次、载入是一次，
    //   多 15ms 换 8.4% 体积，值得。
    //
    // 运行时可用性已核实：游戏的 `Managed/System.IO.Compression.dll`(4.6.57) 里
    // `BrotliStream` 是 **TypeDef**（用 System.Reflection.Metadata 读过元数据），
    // 不是转发桩。编译期需在 csproj 显式引用该 dll（netstandard facade 只到 GZip）。
    public const byte CompressionRaw = 0;      // 不压缩（小回放省一次 CPU）
    public const byte CompressionGzip = 1;     // 新布局里的 gzip 分支（保留给跨版本读）
    public const byte CompressionBrotli = 2;   // 新布局默认
    public const int CompressionHeaderLen = 5;

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