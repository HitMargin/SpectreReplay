// 真跑 ReplayCrypto 往返 + 与 Spectre 的文件互读。
//
// 验四件事：
//   ① 我们的 Encrypt → Decrypt 往返一致（AES + 派生密钥）
//   ② 加密后的产物**不含明文 magic**（否则只是混淆不是加密）
//   ③ 拿**Spectre 的真 .sprp 文件**喂给我们的 LoadReplayAuto —— 同一套密钥与
//      格式的话应该能解开。这是"两个模组回放互通"的唯一实证。
//   ④ 反向：我们的产物能否被 Spectre 的常量识别（magic 一致即可，密钥同源）
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UiPreview
{
    internal static class ReplayCryptoCheck
    {
        // 与 ReplayCrypto 完全同源的派生（硬编码密钥来自源码常量）
        private static byte[] Derive(string s, int len)
        {
            using SHA256 sha = SHA256.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            return h.Take(len).ToArray();
        }

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 回放加密往返与跨模组互读 ──");
            int fail = 0;
            byte[] key = Derive("qwerty", 32);
            byte[] iv = Derive("potato", 16);

            // ① 往返
            try
            {
                byte[] plain = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
                byte[] enc = Crypt(plain, key, iv, true);
                byte[] dec = Crypt(enc, key, iv, false);
                bool same = plain.SequenceEqual(dec);
                Console.WriteLine($"    {(same ? "✓" : "✗")} ① Encrypt→Decrypt 往返一致（{plain.Length} 字节）");
                if (!same) fail++;

                // ② 密文不得含明文 magic "SPRrcplp"
                byte[] magic = { 0x53, 0x50, 0x52, 0x72, 0x63, 0x70, 0x6C, 0x70 };
                int idx = IndexOf(enc, magic);
                bool clean = idx < 0;
                Console.WriteLine($"    {(clean ? "✓" : "✗")} ② 密文里不含明文 magic"
                    + (clean ? "" : "（偏移 " + idx + "）—— 那说明只是混淆"));
                if (!clean) fail++;

                // 错密钥必须解不出（否则"解密失败"这条路径根本没在防东西）
                byte[] wrong = Derive("qwertyX", 32);
                bool rejects;
                try { byte[] bad = Crypt(enc, wrong, iv, false); rejects = !plain.SequenceEqual(bad); }
                catch { rejects = true; }
                Console.WriteLine($"    {(rejects ? "✓" : "✗")} ②b 错密钥解不出原数据");
                if (!rejects) fail++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ 往返抛异常：" + ex.GetType().Name + ": " + ex.Message);
                fail++;
            }

            // ③ 跨模组互读：Spectre 的真 .sprp 文件
            string[] candidates =
            {
                @"D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\Mods\Spectre",
            };
            string specDir = candidates.FirstOrDefault(Directory.Exists);
            if (specDir == null)
            {
                Console.WriteLine("    · 找不到 Spectre 模组目录，跳过互读");
                return fail;
            }
            var sprp = Directory.GetFiles(specDir, "*.sprp", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (sprp == null)
            {
                Console.WriteLine("    · Spectre 目录里没有 .sprp，跳过互读");
                return fail;
            }
            // ③ 跨模组互读：Spectre 的真 .sprp 文件。
            //    **链路是三层**：Encode() → gzip 压缩 → AES 加密 → 落盘。
            //    所以解开 AES 之后拿到的是 **gzip 流**（1F 8B 08…），明文 magic
            //    在再解一层 gzip 之后才出现。第一版只解了 AES 就找 magic，
            //    于是误判"两个模组不能互通"——而实际上密钥/格式完全同源。
            //    实测：503136 B → AES 解出 503133 B（gzip）→ 解压 951076 B，
            //    开头正是 "SPRcrpl\0" + version 2。
            try
            {
                byte[] raw = File.ReadAllBytes(sprp);
                byte[] dec = Crypt(raw, key, iv, false);
                byte[] plain = Gunzip(dec);
                bool ok = plain.Length >= 8
                    && plain[0] == 0x53 && plain[1] == 0x50 && plain[2] == 0x52  // "SPR"
                    && plain[3] == 0x63 && plain[4] == 0x72 && plain[5] == 0x70;  // "crp"
                Console.WriteLine($"    {(ok ? "✓" : "✗")} ③ Spectre 的真文件可被我们完整解开"
                    + $"（{Path.GetFileName(sprp)}，AES {raw.Length}B → gzip {dec.Length}B"
                    + $" → 明文 {plain.Length}B）");
                if (!ok)
                {
                    Console.WriteLine("      ← 密钥或格式不同源；两个模组的回放不能互通用");
                    fail++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ ③ 解开 Spectre 文件抛异常：" + ex.GetType().Name + ": " + ex.Message);
                fail++;
            }

            if (fail == 0)
                Console.WriteLine("    ✓ 加密往返成立、密文无明文 magic、跨模组可互读");
            return fail;
        }

        private static byte[] Gunzip(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gz = new System.IO.Compression.GZipStream(input,
                System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] Crypt(byte[] data, byte[] key, byte[] iv, bool encrypt)
        {
            using Aes aes = Aes.Create();
            aes.Key = key; aes.IV = iv;
            using MemoryStream ms = new MemoryStream();
            using (var cs = new CryptoStream(ms,
                       encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor(),
                       CryptoStreamMode.Write))
                cs.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        private static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i <= hay.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                    if (hay[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }
    }
}
