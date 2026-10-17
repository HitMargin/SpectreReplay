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

    // ── 新格式（v2）──────────────────────────────────────────────────
    // 文件头魔数。4 字节足够区分：旧格式第一个字节是密文，等概率命中 "SRC2"
    // 的概率是 1/2^32；且旧格式解出来是 gzip 流，头部必然 1F 8B，不会再被当成新格式。
    private static readonly byte[] Magic = { (byte)'S', (byte)'R', (byte)'C', (byte)'2' };
    private const byte Version = 2;

    private const int SaltLen = 16;
    private const int IVLen = 16;
    private const int MacLen = 32;      // HMAC-SHA256
    private const int KeyLen = 32;      // AES-256
    private const int MacKeyLen = 32;

    // 迭代次数：Mono 上实测约 40-90ms（一次保存/一次载入各付一次）。
    // 不能太低——那是"更高级"的全部意义所在；也不必追高，回放不是高价值目标。
    private const int Pbkdf2Iterations = 60000;

    // 口令材料。**不是密钥本身**：它要经 PBKDF2 + 每文件随机盐才变成密钥。
    // 分两段拼接是为了不再出现旧格式那种"一个短单词直接当种子"的形态。
    private const string Passphrase =
        "SpectreReplay/replay-container/v2|7f3a91c2d8e04b6a|9c5e17d3ab28f064|replay";

    // 头部总长：魔数 + 版本 + salt + IV + MAC
    private const int HeaderLen = 4 + 1 + SaltLen + IVLen + MacLen;

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
    private static void DeriveV2(byte[] salt, out byte[] encKey, out byte[] macKey)
    {
        using var kdf = new Rfc2898DeriveBytes(
            Encoding.UTF8.GetBytes(Passphrase), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
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

            DeriveV2(salt, out byte[] encKey, out byte[] macKey);

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

            byte[] mac = ComputeMac(macKey, salt, iv, cipherText);

            byte[] output = new byte[HeaderLen + cipherText.Length];
            int p = 0;
            Buffer.BlockCopy(Magic, 0, output, p, Magic.Length); p += Magic.Length;
            output[p++] = Version;
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
        if (IsV2(cipherText))
            return DecryptV2(cipherText);
        return DecryptLegacy(cipherText);
    }

    /// <summary>新格式判定：只看前 4 字节，不与旧格式的密文凭概率混淆。</summary>
    internal static bool IsV2(byte[] data)
    {
        if (data == null || data.Length < HeaderLen) return false;
        for (int i = 0; i < Magic.Length; i++)
            if (data[i] != Magic[i]) return false;
        return data[Magic.Length] == Version;
    }

    private static byte[] DecryptV2(byte[] data)
    {
        try
        {
            int p = 0;
            p += Magic.Length;
            byte version = data[p++];
            if (version != Version)
            {
                Debug.Log("Decrypt Error: 未知容器版本 " + version);
                return null;
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

            DeriveV2(salt, out byte[] encKey, out byte[] macKey);

            // **先验 MAC 再解**。顺序反过来的话，篡改过的密文会先喂进 CBC——
            // 好一点是抛出与"文件损坏"分不开的异常，坏一点是产出垃圾明文
            // （CBC 的填充校验只有约 1/256 的漏网率）而被当成正常回放读进去。
            byte[] expected = ComputeMac(macKey, salt, iv, body);
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
    /// HMAC 覆盖「魔数 + 版本 + salt + IV + 密文」。
    ///
    /// 把 salt 与 IV 也纳入 MAC 是必须的：只签密文的话，攻击者可以改 salt/IV
    /// 再看解出来什么（IV 篡改会可控地翻转首个明文块），而那是一种可观测的预言机。
    /// </summary>
    private static byte[] ComputeMac(byte[] macKey, byte[] salt, byte[] iv, byte[] body)
    {
        using var hmac = new HMACSHA256(macKey);
        int total = Magic.Length + 1 + SaltLen + IVLen + body.Length;
        byte[] buf = new byte[total];
        int p = 0;
        Buffer.BlockCopy(Magic, 0, buf, p, Magic.Length); p += Magic.Length;
        buf[p++] = Version;
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
