using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace SpectreReplay.Features.Replay;

internal static class ReplayCrypto
{
    // ── 容器头 ─────────────────────────────────────────────────────────
    // 文件头魔数。4 字节足够区分：旧格式第一个字节是密文，等概率命中 "SRC2"
    // 的概率是 1/2^32。
    // 魔数不变：容器版本靠第 5 字节区分，v2/v3 共用同一个魔数。
    private static readonly byte[] Magic = { (byte)'S', (byte)'R', (byte)'C', (byte)'2' };
    private const byte VersionV2 = 2;
    private const byte VersionV3 = 3;

    private const int SaltLen = 16;
    private const int IVLen = 16;
    private const int MacLen = 32;      // HMAC-SHA256
    private const int KeyLen = 32;      // AES-256
    private const int MacKeyLen = 32;

    // 头部总长（含 MAC）：
    //   v2 = 魔数 4 + 版本 1 + salt 16 + IV 16 + MAC 32 = 69
    //   v3 = 魔数 4 + 版本 1 + 迭代 4 + salt 16 + IV 16 + MAC 32 = 73
    private const int HeaderLenV2 = 4 + 1 + SaltLen + IVLen + MacLen;
    private const int HeaderLenV3 = 4 + 1 + 4 + SaltLen + IVLen + MacLen;

    private const int IterationsV2 = 60000;

    private const int IterationsV3 = 10000;

    // 迭代次数的合法区间。**上限必须有**：文件头来自磁盘，可以是任意值；
    // 没有上限时一份手改过的文件写 iterations=2^31 就能把游戏卡死几十秒
    // ——解密发生在主线程，这是一条直接的 DoS 路径。
    private const int MinIterations = 1000;
    private const int MaxIterations = 1000000;

    // 口令材料，不是密钥本身：要经 PBKDF2 + 每文件随机盐才变成密钥。
    // 串里的 "v2" 不能改：改了已有文件的密钥就全变了。
    private const string Passphrase =
        "SpectreReplay/replay-container/v2|7f3a91c2d8e04b6a|9c5e17d3ab28f064|replay";

    /// <summary>
    /// PBKDF2 派生，一次算出「加密密钥 + MAC 密钥」两把。
    ///
    /// **必须拆两把**：同一把密钥既加密又做 MAC 是已知的弱构造
    /// （密钥复用会把两种用途的代数关系带进攻击面）。这里取 64 字节，
    /// 前 32 字节加密、后 32 字节做 HMAC。
    /// </summary>
    private static void Derive(byte[] salt, int iterations, out byte[] encKey, out byte[] macKey)
    {
        using var kdf = new Rfc2898DeriveBytes(
            Encoding.UTF8.GetBytes(Passphrase), salt, iterations, HashAlgorithmName.SHA256);
        byte[] both = kdf.GetBytes(KeyLen + MacKeyLen);
        encKey = new byte[KeyLen];
        macKey = new byte[MacKeyLen];
        Buffer.BlockCopy(both, 0, encKey, 0, KeyLen);
        Buffer.BlockCopy(both, KeyLen, macKey, 0, MacKeyLen);
    }

    internal static byte[] Encrypt(byte[] plainText)
    {
        if (plainText == null)
        {
            Debug.Log("Encrypt Error: null input");
            return null;
        }
        try
        {
            byte[] salt = new byte[SaltLen];
            byte[] iv = new byte[IVLen];
            using (var rng = RandomNumberGenerator.Create())
            {
                // 每文件都重新取：盐与 IV 一旦复用，确定性缺陷立刻回来。
                rng.GetBytes(salt);
                rng.GetBytes(iv);
            }

            Derive(salt, IterationsV3, out byte[] encKey, out byte[] macKey);

            byte[] cipherText;
            using (Aes aes = Aes.Create())
            {
                aes.Key = encKey;
                aes.IV = iv;
                using MemoryStream ms = new MemoryStream();
                using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    cs.Write(plainText, 0, plainText.Length);
                cipherText = ms.ToArray();
            }

            byte[] mac = ComputeMac(macKey, VersionV3, IterationsV3, salt, iv, cipherText);

            byte[] output = new byte[HeaderLenV3 + cipherText.Length];
            int p = 0;
            Buffer.BlockCopy(Magic, 0, output, p, Magic.Length); p += Magic.Length;
            output[p++] = VersionV3;
            WriteInt32(output, p, IterationsV3); p += 4;
            Buffer.BlockCopy(salt, 0, output, p, SaltLen); p += SaltLen;
            Buffer.BlockCopy(iv, 0, output, p, IVLen); p += IVLen;
            Buffer.BlockCopy(mac, 0, output, p, MacLen); p += MacLen;
            Buffer.BlockCopy(cipherText, 0, output, p, cipherText.Length);
            return output;
        }
        catch (Exception ex)
        {
            Debug.Log("Encrypt Error: " + ex.Message);
            return null;
        }
    }

    internal static byte[] Decrypt(byte[] cipherText)
    {
        if (cipherText == null)
        {
            Debug.Log("Decrypt Error: null input");
            return null;
        }
        if (!IsNewContainer(cipherText))
        {
            Debug.Log("Decrypt Error: 不是本模组的回放容器（文件头不匹配）");
            return null;
        }
        return DecryptV2(cipherText);
    }

    /// <summary>
    /// 新容器判定：魔数 + 版本是 2 或 3。
    ///
    /// **两代共用同一个魔数**，版本是第 5 字节：v2 没有迭代次数字段，
    /// v3 有。区分它们只需要看这一字节，不需要试探性解密。
    /// </summary>
    internal static bool IsNewContainer(byte[] data)
    {
        if (data == null || data.Length < HeaderLenV2) return false;
        for (int i = 0; i < Magic.Length; i++)
            if (data[i] != Magic[i]) return false;
        byte v = data[Magic.Length];
        return v == VersionV2 || v == VersionV3;
    }

    /// <summary>
    /// 解 v2/v3 容器。两代的区别只有一个：v3 在版本号之后多 4 字节迭代次数。
    ///
    /// **v2 的迭代次数写死 6 万**，因为它的头部没存这个值——这是 v3 存在的理由。
    /// </summary>
    private static byte[] DecryptV2(byte[] data)
    {
        try
        {
            int p = 0;
            p += Magic.Length;
            byte version = data[p++];
            if (version != VersionV2 && version != VersionV3)
            {
                Debug.Log("Decrypt Error: 未知容器版本 " + version);
                return null;
            }
            int iterations = IterationsV2;
            if (version == VersionV3)
            {
                if (data.Length < HeaderLenV3)
                {
                    Debug.Log("Decrypt Error: v3 容器头不完整");
                    return null;
                }
                iterations = ReadInt32(data, p); p += 4;
                if (iterations < MinIterations || iterations > MaxIterations)
                {
                    // **这里必须拒绝而不是"夹到区间内"**：把头里的值改小再解，
                    // 解出来的明文与 MAC 无关，但会让攻击者以极低成本反复试探。
                    Debug.Log("Decrypt Error: 容器头的迭代次数超出合法区间: " + iterations);
                    return null;
                }
            }

            byte[] salt = new byte[SaltLen];
            byte[] iv = new byte[IVLen];
            byte[] mac = new byte[MacLen];
            Buffer.BlockCopy(data, p, salt, 0, SaltLen); p += SaltLen;
            Buffer.BlockCopy(data, p, iv, 0, IVLen); p += IVLen;
            Buffer.BlockCopy(data, p, mac, 0, MacLen); p += MacLen;

            int bodyLen = data.Length - p;
            if (bodyLen <= 0)
            {
                Debug.Log("Decrypt Error: 新格式文件没有正文");
                return null;
            }
            byte[] body = new byte[bodyLen];
            Buffer.BlockCopy(data, p, body, 0, bodyLen);

            Derive(salt, iterations, out byte[] encKey, out byte[] macKey);

            // **先验 MAC 再解**。顺序反过来的话，篡改过的密文会先喂进 CBC——
            // 好一点是抛出与"文件损坏"分不开的异常，坏一点是产出垃圾明文
            // （CBC 的填充校验只有约 1/256 的漏网率）而被当成正常回放读进去。
            byte[] expected = ComputeMac(macKey, version, iterations, salt, iv, body);
            if (!ConstantTimeEquals(expected, mac))
            {
                Debug.Log("Decrypt Error: 完整性校验失败（文件被修改过，或不是本模组产出的回放）");
                return null;
            }

            using Aes aes = Aes.Create();
            aes.Key = encKey;
            aes.IV = iv;
            using MemoryStream ms = new MemoryStream();
            using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                cs.Write(body, 0, body.Length);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Debug.Log("Decrypt Error: " + ex.Message);
            return null;
        }
    }

    private static void WriteInt32(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static int ReadInt32(byte[] buf, int offset)
        => buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24);

    /// <summary>
    /// HMAC 覆盖「魔数 + 版本 + [迭代次数] + salt + IV + 密文」。
    ///
    /// 把 salt 与 IV 也纳入 MAC 是必须的：只签密文的话，攻击者可以改 salt/IV
    /// 再看解出来什么（IV 篡改会可控地翻转首个明文块），而那是一种可观测的预言机。
    ///
    /// **迭代次数也纳入 MAC（v3）**，且按版本决定是否写进 MAC 输入——
    /// v2 的头部没有这个字段，它的 MAC 自然也不覆盖它；两种布局必须各自
    /// 自洽，否则存量 v2 文件全部验签失败。
    /// </summary>
    private static byte[] ComputeMac(byte[] macKey, byte version, int iterations,
        byte[] salt, byte[] iv, byte[] body)
    {
        using var hmac = new HMACSHA256(macKey);
        int iterLen = version >= VersionV3 ? 4 : 0;
        int total = Magic.Length + 1 + iterLen + SaltLen + IVLen + body.Length;
        byte[] buf = new byte[total];
        int p = 0;
        Buffer.BlockCopy(Magic, 0, buf, p, Magic.Length); p += Magic.Length;
        buf[p++] = version;
        if (iterLen > 0)
        {
            WriteInt32(buf, p, iterations); p += 4;
        }
        Buffer.BlockCopy(salt, 0, buf, p, SaltLen); p += SaltLen;
        Buffer.BlockCopy(iv, 0, buf, p, IVLen); p += IVLen;
        Buffer.BlockCopy(body, 0, buf, p, body.Length);
        return hmac.ComputeHash(buf);
    }

    /// <summary>
    /// 定长时间比较。用 <c>SequenceEqual</c> 会提前返回，把"前几个字节对上了"
    /// 泄漏成时间差——对本地回放威胁很小，但比较函数是通用的，写成正确形态没有成本。
    /// </summary>
    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
