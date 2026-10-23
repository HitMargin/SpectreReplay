using System;
using System.IO;
using System.IO.Compression;

namespace SpectreReplay.Features.Replay;

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
        // **预分配提示必须与压缩比挂钩。** expected 已按 512MB 上界校验，但那是
        // 「解出来不超过」的上限，不是「值得预分配」的依据：一个几字节的畸形
        // 文件声称 512MB，就会在验证任何内容之前让我们白拿 512MB 托管内存
        // （MemoryStream(capacity) 立即分配整块）。真实回放的压缩比在几十倍
        // 以内，给 200 倍余量；声称超出它的按 0 处理，MemoryStream 自行增长
        // ——ReadWithLimit 本来就边读边限量，正确性不受影响，只是放弃预分配。
        int hint = expected <= (long)data.Length * 200 ? expected : 0;
        return ReadWithLimit(decoder, hint);
    }

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