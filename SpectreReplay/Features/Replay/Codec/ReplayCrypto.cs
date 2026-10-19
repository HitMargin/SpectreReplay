using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 回放文件加解密。**两层格式并存**：新格式（v2）与上游 Spectre 的旧格式。
///
/// <para><b>旧格式的两个真缺陷</b>（2026-10-18 改造前实测）：</para>
/// <list type="number">
/// <item><b>密钥就是源码里两个字符串的 SHA256</b>——`SHA256("qwerty")` 单次散列，
///   没有迭代、没有盐。拿到 dll 的人一行代码就能算回密钥，所谓加密只防住了
///   "用文本编辑器打开看一眼"。</item>
/// <item><b>IV 是常量</b>（`SHA256("potato")`）。CBC 下固定 IV 的后果是**确定性**：
///   同一份明文每次加密得到逐字节相同的密文。两份回放只要内容一样，
///   文件就一样——这条比密钥弱更严重，它让"我有两份相同战绩"变成可观测事实。</item>
/// <item>顺带还没有**完整性校验**：密文被改一个字节，CBC 解出来是垃圾或抛异常，
///   但"被改过"这件事本身无从证明。</item>
/// </list>
///
/// <para><b>新格式（v2）</b>解决上面三条：</para>
/// <list type="bullet">
/// <item>口令经 <b>PBKDF2</b>（HMAC-SHA256，6 万次迭代）派生，派生结果再拆成
///   加密密钥与 MAC 密钥两把——迭代把"拿到 dll 就能算回密钥"的成本抬高，
///   盐让预计算表失效。</item>
/// <item><b>每文件随机 salt + 随机 IV</b>，从 <see cref="RandomNumberGenerator"/> 取。
///   同一份内容加密两次，密文必须不同。</item>
/// <item><b>encrypt-then-MAC</b>：HMAC-SHA256 覆盖「魔数 + 版本 + salt + IV + 密文」。
///   解密先验 MAC 再解，篡改在第一步就被拒掉，不会喂给 CBC 产生垃圾。</item>
/// </list>
///
/// <para><b>为什么不做「绑定机器」</b>：回放是玩家自己机器上的本地数据，绑
/// <c>SystemInfo.deviceUniqueIdentifier</c> 会在重装系统、换电脑、换主板后让全部
/// 存量回放变成"文件损坏"——而玩家不会想到是模组换了密钥。也不做「玩家输入口令」：
/// 每次开回放要记一串密码，体验代价与收益完全不匹配。所以密钥材料仍是程序内固定的，
/// 变的是**派生方式**与**每次加密的随机性**。</para>
///
/// <para><b>旧格式保留在解密侧作回落</b>：玩家已有的、以及上游 Spectre 录的 `.sprp`
/// 全是旧格式，必须继续读得开（套件里 ③ 就拿 Spectre 的真文件验）。
/// **写侧只产出新格式**，落盘那一刻就换代，旧的只在读路径上活。</para>
/// </summary>
internal static class ReplayCrypto
{
    // ── 旧格式（仅解密回落，不再用于加密）──────────────────────────────
    // 上游 Spectre 的常量，逐字不动：换掉它就再也解不开存量文件。
    private const string LegacyKeyStorageKey = "qwerty";
    private const string LegacyIVStorageKey = "potato";

    // ── 新格式（v3；v2 仍在解密侧）────────────────────────────────────
    // 文件头魔数。4 字节足够区分：旧格式第一个字节是密文，等概率命中 "SRC2"
    // 的概率是 1/2^32；且旧格式解出来是 gzip 流，头部必然 1F 8B，不会再被当成新格式。
    // 魔数不变：容器版本靠第 5 字节区分，两代共用同一个魔数。
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

    // **v2 的迭代次数只能写死。** v2 文件头里没有这个字段，读的时候除了
    // 当年的常量没有别的依据——改它等于让 2026-10-18~19 保存的全部回放
    // 解不开。v3 把它写进文件头，正是为了让这种调整不再是一次格式事故。
    private const int IterationsV2 = 60000;

    // v3 的迭代次数。**6 万 → 1 万的依据是"这笔开销挡不住谁"**：
    // 口令材料就编译在 dll 里，拿到 dll 的人无论迭代多少次都能算出密钥，
    // 迭代只是把某一次计算乘以常数。而保存发生在游戏结算的主线程上，
    // Mono 上 6 万次实测 150~250ms，一局两遍（主回放 + 备份）= 半秒级卡顿
    // （2026-10-19 用户报「保存时游戏会卡」）。降低迭代换来的流畅是真实的，
    // 失去的只是"已经拦不住的人多算几毫秒"。
    private const int IterationsV3 = 10000;

    // 迭代次数的合法区间。**上限必须有**：文件头来自磁盘，可以是任意值；
    // 没有上限时一份手改过的文件写 iterations=2^31 就能把游戏卡死几十秒
    // ——解密发生在主线程，这是一条直接的 DoS 路径。
    private const int MinIterations = 1000;
    private const int MaxIterations = 1000000;

    // 口令材料。**不是密钥本身**：它要经 PBKDF2 + 每文件随机盐才变成密钥。
    // 分两段拼接是为了不再出现旧格式那种"一个短单词直接当种子"的形态。
    // 串里的 "v2" 是历史命名，**不要改**：改了 v2 存量的密钥就全变了。
    private const string Passphrase =
        "SpectreReplay/replay-container/v2|7f3a91c2d8e04b6a|9c5e17d3ab28f064|replay";

    private static readonly byte[] LegacyKey;
    private static readonly byte[] LegacyIV;

    static ReplayCrypto()
    {
        LegacyKey = LegacyDerive(LegacyKeyStorageKey, 32);
        LegacyIV = LegacyDerive(LegacyIVStorageKey, 16);
    }

    /// <summary>
    /// 旧格式的派生，逐字保留以兼容存量文件。
    /// SHA256 单次散列，无盐无迭代——这正是被替换掉的东西，所以单独一个函数。
    /// </summary>
    private static byte[] LegacyDerive(string storageKey, int length)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(storageKey));
        if (hash.Length >= length)
        {
            byte[] take = new byte[length];
            Buffer.BlockCopy(hash, 0, take, 0, length);
            return take;
        }
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
            result[i] = hash[i % hash.Length];
        return result;
    }

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
        if (IsNewContainer(cipherText))
            return DecryptV2(cipherText);
        return DecryptLegacy(cipherText);
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
    /// 旧格式解密：上游 Spectre 与本模组 v1 产出的全部存量文件都走这里。
    /// 只解密，永不用于加密。
    /// </summary>
    private static byte[] DecryptLegacy(byte[] cipherText)
    {
        try
        {
            using Aes aes = Aes.Create();
            aes.Key = LegacyKey;
            aes.IV = LegacyIV;
            using MemoryStream ms = new MemoryStream();
            using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                cs.Write(cipherText, 0, cipherText.Length);
            return ms.ToArray();
        }
        catch (CryptographicException ex)
        {
            Debug.Log("Decrypt Error: " + ex.Message);
            return null;
        }
    }

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
