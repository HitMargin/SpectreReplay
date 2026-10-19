// 真跑 ReplayCrypto（链接的是真源码，不是复刻）——验新容器格式的三条安全性质。
//
// 2026-10-18 改造前的版本是**复刻**旧算法（自己 Derive("qwerty") 再 Crypt），
// 于是"验的东西"和"跑的东西"是两份代码：ReplayCrypto 改成什么样它都照样绿。
// 这正是本项目反复踩过的形态（复刻算法与被测算法会一起错），所以现在全部
// 改调真实实现。
//
// **旧上游格式（固定密钥 qwerty/potato）的读侧回落已于 2026-10-19 整体移除**
// （用户裁定）。原先这里的 ⑥「上游 Spectre 的 .sprp 仍可解开」与 ⑦「旧路径确定性
// 对照组」随之删除——它们守护的东西已经不存在了，留着只会把已删除的算法
// 重新钉进测试里。现在只剩：
//
// 验七件事：
//   ① 往返：Encrypt → Decrypt 一致
//   ② 密文不含明文 magic（否则只是混淆不是加密）
//   ③ **同一明文加密两次必须不同**——这是新格式相对旧格式最实质的改进。
//      旧格式 IV 固定 ⇒ 确定性；随机 salt+IV 之后这条才成立。
//   ④ 篡改任意一个字节必须被拒（encrypt-then-MAC）。旧格式做不到：
//      CBC 下改一个字节只会让明文跟着烂，不会被"证明被改过"。
//   ⑤ 篡改 IV 也必须被拒——IV 不参与派生，改它密钥不变、CBC 仍能解，
//      只有 MAC 覆盖 IV 才拦得下（改 salt 拦得住是因为密钥变了，表达不了缺陷）
//   ⑥ 非本模组容器一律被拒——旧上游格式回落删掉之后的护栏
//   ⑧ v2 容器仍可解开（2026-10-19 升级到 v3 的格式保险）
using System;
using System.IO;
using System.Linq;

namespace UiPreview
{
    internal static class ReplayCryptoCheck
    {
        // 与 ReplayCrypto 的新格式头布局**必须一致**：
        //   v3 = 魔数 4 + 版本 1 + 迭代次数 4 + salt 16 + IV 16 + MAC 32 = 73。
        // 改容器头的人要同时改这里，两处对不上时 ④⑤ 会去改错偏移量
        // （表现为"篡改没生效"而不是"判据失败"）。
        private const int HeaderLen = 4 + 1 + 4 + 16 + 16 + 32;

        // v2 的头部（无迭代次数字段）与 IV 偏移，只用于读夹具的越界断言。
        private const int HeaderLenV2 = 4 + 1 + 16 + 16 + 32;

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 回放加密往返与容器格式 ──");
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
                //    （IV 位于 魔数 4 + 版本 1 + 迭代 4 + salt 16 之后。）
                byte[] ivSwapped = (byte[])enc.Clone();
                ivSwapped[4 + 1 + 4 + 16] ^= 0x01;
                byte[] bad2 = SpectreReplay.Features.Replay.ReplayCrypto.Decrypt(ivSwapped);
                bool rejected2 = bad2 == null;
                Console.WriteLine($"    {(rejected2 ? "✓" : "✗")} ⑤ 篡改 IV 被拒（IV 参与 MAC，不是仅参与解密）");
                if (!rejected2) fail++;

                // ⑥ 旧格式（上游固定密钥的裸 AES 流）现在必须被**拒绝**。
                //
                //    这条是删除旧格式的护栏：读侧回落一旦被人错误地加回来，
                //    任意非本模组容器的字节都会被当成"可解密"试探，而删除的
                //    意义（固定密钥不再存在于本模组）也就没了。用一段确定性
                //    的伪随机字节模拟"别家的二进制"。
                byte[] foreign = Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 11)).ToArray();
                bool foreignRejected = SpectreReplay.Features.Replay.ReplayCrypto.Decrypt(foreign) == null;
                Console.WriteLine($"    {(foreignRejected ? "✓" : "✗")} ⑥ 非本模组容器被拒（旧格式回落已移除，不做试探性解密）");
                if (!foreignRejected) fail++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ 往返抛异常：" + ex.GetType().Name + ": " + ex.Message);
                fail++;
            }

            // ⑧ v2 容器仍能解开（2026-10-19 升级到 v3：迭代次数写进文件头）。
            //
            //    **这条是格式升级的保险。** v3 把迭代次数从"代码里的常量"
            //    搬进"文件头字段"，读侧必须仍按 v2 的 6 万次去派生——
            //    否则 2026-10-18~19 保存的全部回放会在升级当天集体打不开，
            //    而玩家看到的只是"文件损坏"。夹具是一份真 v2 容器。
            try
            {
                // projectRoot 是**源码目录**（…\SpectreReplay），夹具在仓库根的 tools 下。
                string fixture = Path.GetFullPath(Path.Combine(
                    projectRoot, "..", "tools", "UiPreview", "fixtures", "v2-container.bin"));
                byte[] raw = File.Exists(fixture) ? File.ReadAllBytes(fixture) : null;
                byte[] dec = raw == null ? null : SpectreReplay.Features.Replay.ReplayCrypto.Decrypt(raw);
                bool okV2 = dec != null && dec.Length == 777 && dec[0] == 11 && dec[776] == (byte)((776 * 37 + 11) & 0xFF);
                Console.WriteLine($"    {(okV2 ? "✓" : "✗")} ⑧ v2 容器（迭代次数未入头）仍可解开"
                    + (okV2 ? "（853 B）" : "（夹具缺失或回落逻辑坏了；v2 存量回放会集体打不开）"));
                if (!okV2) fail++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ ⑧ 解 v2 容器抛异常：" + ex.GetType().Name + ": " + ex.Message);
                fail++;
            }

            if (fail == 0)
                Console.WriteLine("    ✓ 新格式三条性质成立（随机化 / 完整性 / 非本容器被拒）");
            return fail;
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
