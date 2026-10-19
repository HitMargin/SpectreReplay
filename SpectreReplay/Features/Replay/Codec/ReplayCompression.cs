using System;
using System.IO;
using System.IO.Compression;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 回放正文的压缩包装层。**新旧两种布局并存**。
///
/// <para><b>旧布局</b>（无包装）：整个负载就是一条 gzip 流，首字节恒为 gzip 魔数的
/// 低字节 <c>0x1F</c>。注意这里拿到的是**解密之后**的字节——磁盘上的 `.sprp`
/// 首字节是密文的随机值（实测过 20 个存量文件，无一个是 0x1F），压缩层在最里面。</para>
///
/// <para><b>新布局</b>：<c>[1 字节算法号][4 字节原文长度][压缩体]</c>。
/// 算法号见 <see cref="ReplayConstants"/>：0 原文 / 1 gzip / 2 Brotli。</para>
///
/// <para><b>怎么区分</b>：新布局第一个字节只可能是 0/1/2，而 gzip 流第一个字节
/// 恒为 <c>0x1F</c>。两者不相交，所以「读第一个字节」就够，不需要试解。</para>
///
/// <para><b>为什么不靠抛异常探测</b>：那样损坏文件会在两条路径上都失败，
/// 最终报的错误与「格式不认识」分不开；而且 Brotli 解随机字节有小概率
/// 不抛异常而是吐出垃圾，于是坏文件被当成「合法但内容乱」喂进解码器。</para>
///
/// <para><b>实测收益</b>（本机真实回放，908118 B 明文）：gzip/Optimal 469658 B
/// → Brotli/Optimal 430116 B，**小 8.4%**；代价是 25ms → 40ms。文件目前是
/// 几百 KB 级，体积和速度都无感。</para>
///
/// <para><b>运行时可用性已核实（两层）</b>：① 游戏的
/// <c>Managed/System.IO.Compression.dll</c>(4.6.57) 里 <c>BrotliStream</c> 是
/// <b>TypeDef</b>（用 System.Reflection.Metadata 读过元数据，39 个方法），
/// 不是转发桩；② 编译期要显式引用该 dll——<c>netstandard.dll</c> 的 facade
/// 里只到 GZipStream 为止，不加引用会报 CS1069「类型已转发」。
/// csproj 里已加 <c>&lt;Reference Include="System.IO.Compression"&gt;</c>。</para>
/// </summary>
internal static class ReplayCompression
{
    private const long MaxDecompressed = 512L * 1024 * 1024;

    /// <summary>原文不压缩的阈值。低于它时 Brotli 的头开销比省下来的还多。</summary>
    private const int NoCompressBelow = 512;

    internal static byte[] Compress(byte[] data)
    {
        if (data == null) return null;

        if (data.Length < NoCompressBelow)
            return WrapRaw(data);

        byte[] body;
        if (!TryBrotli(data, out body))
            return WrapRaw(data);

        // 压完反而变大就存原文。高熵数据会这样，回放本身不该出现，
        // 但「不压缩」这条分支必须存在——否则那种文件会凭空更大。
        if (body.Length >= data.Length)
            return WrapRaw(data);

        byte[] result = new byte[ReplayConstants.CompressionHeaderLen + body.Length];
        result[0] = ReplayConstants.CompressionBrotli;
        WriteInt32(result, 1, data.Length);
        Buffer.BlockCopy(body, 0, result, ReplayConstants.CompressionHeaderLen, body.Length);
        return result;
    }

    internal static byte[] Decompress(byte[] data)
    {
        if (data == null || data.Length == 0)
            throw new InvalidDataException("Replay payload is empty");

        byte algo = data[0];

        // 旧布局：整条 gzip 流，首字节 0x1F。
        // **不能把它当成算法号**，所以先判掉。
        if (algo == 0x1F)
            return ReadWithLimit(new GZipStream(new MemoryStream(data), CompressionMode.Decompress), 0);

        if (algo != ReplayConstants.CompressionRaw
            && algo != ReplayConstants.CompressionGzip
            && algo != ReplayConstants.CompressionBrotli)
            throw new InvalidDataException($"Unknown compression tag: 0x{algo:X2}");

        if (data.Length < ReplayConstants.CompressionHeaderLen)
            throw new InvalidDataException("Compression header truncated");

        int expected = ReadInt32(data, 1);
        if (expected < 0 || expected > MaxDecompressed)
            throw new InvalidDataException($"Decompressed length {expected} out of range");

        int bodyLen = data.Length - ReplayConstants.CompressionHeaderLen;
        byte[] payload = new byte[bodyLen];
        Buffer.BlockCopy(data, ReplayConstants.CompressionHeaderLen, payload, 0, bodyLen);

        if (algo == ReplayConstants.CompressionRaw)
        {
            if (payload.Length != expected)
                throw new InvalidDataException("Uncompressed body length mismatch with header");
            return payload;
        }

        using var input = new MemoryStream(payload);
        using Stream decoder = algo == ReplayConstants.CompressionBrotli
            ? new BrotliStream(input, CompressionMode.Decompress)
            : new GZipStream(input, CompressionMode.Decompress);
        return ReadWithLimit(decoder, expected);
    }

    /// <summary>
    /// Brotli 一步式压缩。质量档取 6：本机真实回放（1220012 B 明文）实测
    /// q4=441813B/29ms、<b>q6=357401B/62ms</b>、q9=355718B/162ms、q11=314652B/3073ms——
    /// q6 比 q4 小 19% 只多 33ms，q11 的 3 秒卡顿在游戏内不可用，故取 6。
    /// 窗口 22 是 Brotli 上限，小输入用不满、也无额外代价。
    /// 缓冲用 <c>GetMaxCompressedLength</c> 一次开够（它给的是最坏情况上界，
    /// 比流式的 grow-reallocate 少一次拷贝）；理论上不会失败，失败兜底返回 false 走原文包装。
    /// </summary>
    private static bool TryBrotli(byte[] data, out byte[] body)
    {
        body = null;
        try
        {
            byte[] buf = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
            if (!BrotliEncoder.TryCompress(data, buf, out int written, 6, 22))
                return false;
            body = new byte[written];
            Buffer.BlockCopy(buf, 0, body, 0, written);
            return true;
        }
        catch (Exception)
        {
            // 运行时缺 Brotli 原生实现等极端情况：退回原文包装，录制不能因为压缩挂掉。
            return false;
        }
    }

    private static byte[] WrapRaw(byte[] data)
    {
        byte[] raw = new byte[ReplayConstants.CompressionHeaderLen + data.Length];
        raw[0] = ReplayConstants.CompressionRaw;
        WriteInt32(raw, 1, data.Length);
        Buffer.BlockCopy(data, 0, raw, ReplayConstants.CompressionHeaderLen, data.Length);
        return raw;
    }

    /// <summary>
    /// 边解边限量。**不能用 gzip 头里的原始长度**——那是可伪造的，
    /// 用它一次 `new byte[n]` 就能被畸形文件撑爆内存。
    /// <paramref name="hint"/> 仅用于预分配提示（0 = 不提示）。
    /// </summary>
    private static byte[] ReadWithLimit(Stream s, int hint)
    {
        using (s)
        {
            using var output = hint > 0 ? new MemoryStream(hint) : new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxDecompressed)
                    throw new InvalidDataException(
                        "Decompressed output exceeds 512MB cap (corrupt replay or compression bomb)");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }

    private static void WriteInt32(byte[] dst, int offset, int value)
    {
        dst[offset] = (byte)value;
        dst[offset + 1] = (byte)(value >> 8);
        dst[offset + 2] = (byte)(value >> 16);
        dst[offset + 3] = (byte)(value >> 24);
    }

    private static int ReadInt32(byte[] src, int offset)
        => src[offset] | (src[offset + 1] << 8) | (src[offset + 2] << 16) | (src[offset + 3] << 24);
}