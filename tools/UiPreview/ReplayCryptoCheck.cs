// 真跑 ReplayCrypto（链接的是真源码，不是复刻）——验新容器格式的三条安全性质。
//
// 2026-10-18 改造前的版本是**复刻**旧算法（自己 Derive("qwerty") 再 Crypt），
// 于是"验的东西"和"跑的东西"是两份代码：ReplayCrypto 改成什么样它都照样绿。
// 这正是本项目反复踩过的形态（复刻算法与被测算法会一起错），所以现在全部
// 改调真实实现，只在需要旧格式对照时按旧常量本地算。
//
// 验七件事：
//   ① 往返：Encrypt → Decrypt 一致
//   ② 密文不含明文 magic（否则只是混淆不是加密）
//   ③ **同一明文加密两次必须不同**——这是新格式相对旧格式最实质的改进。
//      旧格式 IV 固定 ⇒ 确定性；随机 salt+IV 之后这条才成立。
//   ④ 篡改任意一个字节必须被拒（encrypt-then-MAC）。旧格式做不到：
//      CBC 下改一个字节只会让明文跟着烂，不会被"证明被改过"。
//   ⑤ 换 salt 也必须被拒——salt 参与 MAC，防"改 IV 看首个明文块"的预言机
//   ⑥ 旧格式（上游 Spectre 的真 .sprp）仍能解开：读侧回落没坏
//   ⑦ 旧格式的确定性仍在（对照组）：同一明文用旧路径加密两次必须相同，
//      用来证明 ③ 的差异确实来自新格式而不是测试写错
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UiPreview
{
    internal static class ReplayCryptoCheck
    {
        // 与 ReplayCrypto 的新格式头布局**必须一致**：魔数 4 + 版本 1 + salt 16
        // + IV 16 + MAC 32 = 69。改容器头的人要同时改这里，两处对不上时
        // ④⑤ 会去改错偏移量（表现为"篡改没生效"而不是"判据失败"）。
        private const int HeaderLen = 4 + 1 + 16 + 16 + 32;

        // 旧格式常量：只用于"上游文件能不能解开"这一组的本地复算。
        private static byte[] LegacyDerive(string s, int len)
        {
            using SHA256 sha = SHA256.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            return h.Take(len).ToArray();
        }

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 回放加密往返与跨模组互读 ──");
            int fail = 0;

            byte[] plain = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();

            // ① 往返（真实实现）
            try
            {
                byte[] enc = SpectreReplay.Features.Replay.ReplayCrypto.Encrypt(plain);
                byte[] dec = enc == null ? null : SpectreReplay.Features.Replay.ReplayCrypto.Decrypt(enc);
                bool same = dec != null && plain.SequenceEqual(dec);
                Console.WriteLine($"    {(same ? "✓" : "✗")} ① Encrypt→Decrypt 往返一致（{plain.Length} 字节）");
                if (!same) fail++;

                // ② 密文不得含明文 magic "SPRrcplp"
                byte[] magic = { 0x53, 0x50, 0x52, 0x72, 0x63, 0x70, 0x6C, 0x70 };
                int idx = enc == null ? -1 : IndexOf(enc, magic);
                bool clean = enc != null && idx < 0;
                Console.WriteLine($"    {(clean ? "✓" : "✗")} ② 密文里不含明文 magic"
                    + (clean ? "" : "（偏移 " + idx + "）—— 那说明只是混淆"));
                if (!clean) fail++;

                // ③ 同一明文加密两次必须不同（随机 salt + IV）
                byte[] enc2 = SpectreReplay.Features.Replay.ReplayCrypto.Encrypt(plain);
                bool nondeterministic = enc != null && enc2 != null && !enc.SequenceEqual(enc2);
                Console.WriteLine($"    {(nondeterministic ? "✓" : "✗")} ③ 同一明文两次加密结果不同"
                    + (nondeterministic ? "" : "（确定性！固定 IV 的旧缺陷回来了）"));
                if (!nondeterministic) fail++;

                // ④ 篡改**正文第一个字节**必须被拒。
                //
                //    **篡改位置是这条判据的全部价值所在。** 第一版改的是最后一个字节，
                //    那种改法会破坏 CBC 的 padding ⇒ 解密自己就抛异常 ⇒ 返回 null，
                //    于是「删掉 MAC 校验」的变异照样绿（实测踩到）。CBC 的性质是
                //    「改密文第 N 块只会毁掉第 N+1 块的明文」，所以改**首块**会得到
                //    「首块乱码 + 其余全部正确」，padding 完好、不抛异常 —— 只有
                //    MAC 能拦住它。这条才不会假绿。
                byte[] tampered = (byte[])enc.Clone();
                tampered[HeaderLen] ^= 0x01;          // HeaderLen 正好是正文起点
                byte[] bad = SpectreReplay.Features.Replay.ReplayCrypto.Decrypt(tampered);
                bool rejected = bad == null;
                Console.WriteLine($"    {(rejected ? "✓" : "✗")} ④ 篡改正文首字节被拒（encrypt-then-MAC 生效）\n"
                    + (rejected ? "" : "      ← 没有 MAC 的话 CBC 只毁首块、padding 完好，会解出垃圾而不报错"));
                if (!rejected) fail++;

                // ⑤ 篡改 **IV** 必须被拒。
                //
                //    同理，第一版验的是「改 salt 被拒」—— 那个变异表达不了缺陷：
                //    salt 参与**密钥派生**，改它密钥就变了，解密必然失败，与 MAC
                //    在不在无关。IV 不参与派生，改它 ⇒ 密钥不变、CBC 仍能解，
                //    只是首个明文块被可控翻转。**只有 MAC 覆盖 IV 才能拦下它。**
                //    （IV 位于 魔数 4 + 版本 1 + salt 16 之后。）
                byte[] ivSwapped = (byte[])enc.Clone();
                ivSwapped[4 + 1 + 16] ^= 0x01;
                byte[] bad2 = SpectreReplay.Features.Replay.ReplayCrypto.Decrypt(ivSwapped);
                bool rejected2 = bad2 == null;
                Console.WriteLine($"    {(rejected2 ? "✓" : "✗")} ⑤ 篡改 IV 被拒（IV 参与 MAC，不是仅参与解密）");
                if (!rejected2) fail++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ 往返抛异常：" + ex.GetType().Name + ": " + ex.Message);
                fail++;
            }

            // ⑦ 对照组：旧路径确实是确定性的
            //   这条不测产品代码，测的是"③ 的差异来自新格式"这个归因。
            try
            {
                byte[] k = LegacyDerive("qwerty", 32);
                byte[] v = LegacyDerive("potato", 16);
                byte[] a = Crypt(plain, k, v, true);
                byte[] b = Crypt(plain, k, v, true);
                bool deterministic = a.SequenceEqual(b);
                Console.WriteLine($"    {(deterministic ? "✓" : "✗")} ⑦ 对照组：旧格式（固定 IV）确实确定");
                if (!deterministic) fail++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ 对照组抛异常：" + ex.Message);
                fail++;
            }

            // ⑥ 跨模组互读：拿 Spectre 的真 .sprp 喂真实实现
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
            try
            {
                byte[] raw = File.ReadAllBytes(sprp);
                byte[] dec = SpectreReplay.Features.Replay.ReplayCrypto.Decrypt(raw);
                byte[] plainGz = dec == null ? null : Gunzip(dec);
                bool ok = plainGz != null && plainGz.Length >= 8
                    && plainGz[0] == 0x53 && plainGz[1] == 0x50 && plainGz[2] == 0x52
                    && plainGz[3] == 0x63 && plainGz[4] == 0x72 && plainGz[5] == 0x70;
                Console.WriteLine($"    {(ok ? "✓" : "✗")} ⑥ 上游 Spectre 的旧格式文件仍可解开"
                    + (ok ? $"（{Path.GetFileName(sprp)}，AES {raw.Length}B → gzip {dec.Length}B → 明文 {plainGz.Length}B）"
                          : "（读侧回落坏了）"));
                if (!ok) fail++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ ⑥ 解开 Spectre 文件抛异常：" + ex.GetType().Name + ": " + ex.Message);
                fail++;
            }

            if (fail == 0)
                Console.WriteLine("    ✓ 新格式三条性质成立（随机化 / 完整性 / 旧文件仍可读）");
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