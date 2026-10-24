using System;
using System.Collections.Generic;
using System.IO;
using SpectreReplay.Features.Replay;

namespace UiPreview
{
    /// <summary>
    /// 回放文件格式往返：链接**真实的** Spectre 移植编解码（ReplayEncoder/Decoder）。
    /// 编解码是纯 System 操作，不碰引擎，可以离屏直接跑。
    /// 覆盖：新旧格式往返、空数据、两轨都有数据的形状。
    /// </summary>
    internal static class ReplayCheck
    {
        internal static int Run()
        {
            Console.WriteLine("  ── 回放格式往返（真实编解码源码） ──");
            int fail = 0;
            fail += RoundTrip("⑴ 两轨都有数据的典型局", Sample(3, 3, 5));
            fail += RoundTrip("⑵ 只有键轨（无命中）", Sample(0, 0, 5));
            fail += RoundTrip("⑶ 空回放", Sample(0, 0, 0));
            fail += RoundTrip("⑷ 只有命中无按键", Sample(3, 3, 0));
            fail += RejectsGarbage();
            fail += DecodesV1HitSnapshots();
            if (fail == 0) Console.WriteLine("    ✓ 回放格式往返全部一致");
            return fail;
        }

        private static int RoundTrip(string name, Replay src)
        {
            try
            {
                byte[] encoded = ReplayEncoder.Encode(src);
                Replay back = ReplayDecoder.Decode(encoded);
                var bad = new List<string>();
                if (back.KeyEvents.Count != src.KeyEvents.Count)
                    bad.Add("KeyEvents 数不符 " + back.KeyEvents.Count + " vs " + src.KeyEvents.Count);
                if (back.HitSnapshots.Count != src.HitSnapshots.Count)
                    bad.Add("HitSnapshots 数不符 " + back.HitSnapshots.Count + " vs " + src.HitSnapshots.Count);
                if (back.Metadata == null || back.Metadata.SongName != src.Metadata.SongName)
                    bad.Add("Metadata.SongName 不一致");
                if (back.Metadata.StartingFloorId != src.Metadata.StartingFloorId)
                    bad.Add("Metadata.StartingFloorId 不一致");
                for (int i = 0; i < src.KeyEvents.Count && i < back.KeyEvents.Count; i++)
                {
                    var a = src.KeyEvents[i]; var b = back.KeyEvents[i];
                    if (a.KeyCode != b.KeyCode || a.IsPressed != b.IsPressed
                        || Math.Abs(a.SongPosition - b.SongPosition) > 1e-9)
                    { bad.Add("KeyEvents[" + i + "] 不一致"); break; }
                }
                for (int i = 0; i < src.HitSnapshots.Count && i < back.HitSnapshots.Count; i++)
                {
                    var a = src.HitSnapshots[i]; var b = back.HitSnapshots[i];
                    if (a.CurrentFloorID != b.CurrentFloorID
                        || Math.Abs(a.CachedAngle - b.CachedAngle) > 1e-9
                        || Math.Abs(a.TargetExitAngle - b.TargetExitAngle) > 1e-9
                        || a.IsAuto != b.IsAuto || a.NoFailHit != b.NoFailHit
                        || a.MidspinInfiniteMargin != b.MidspinInfiniteMargin)
                    { bad.Add("HitSnapshots[" + i + "] 不一致"); break; }
                }
                if (bad.Count == 0) { Console.WriteLine("    ✓ " + name); return 0; }
                foreach (var p in bad) Console.WriteLine("    ✗ " + name + "：" + p);
                return bad.Count;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ " + name + "：抛异常 " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// 损坏文件必须被干净拒绝：编码一局真实数据（格式完全合法），
        /// 解压后把 KeyEvents 段的 count 字节改成巨大值再重新压缩——
        /// 只有 ReadCount 的越界防护在位时抛出 InvalidDataException 才算通过；
        /// 其余任何结果（解出成功 / 别的异常）都是防护缺失（红）。
        ///
        /// <para><b>为什么必须核对异常消息（2026-10-19 修）。</b>原先这里是
        /// 「catch (InvalidDataException) 就算通过」，同时用**裸 gzip** 解压生产端数据。
        /// 2026-10-18 压缩层换成 <c>[算法号][长度][Brotli 体]</c> 包装后，裸 gzip 解压
        /// 第一步就抛 InvalidDataException（首字节是算法号不是 gzip 魔数），于是本断言
        /// **再也不会走到 ReadCount**，却因为异常类型恰好相同而始终报绿——MUT-P2
        /// 把 ReadCount 的守卫整个短路掉，它照样绿，这才暴露出来。
        /// 现在解压/压缩都走生产端 <see cref="ReplayCompression"/>，且只认
        /// ReadCount 那条消息；解压阶段的异常会被明确判红。</para>
        /// </summary>
        private static int RejectsGarbage()
        {
            Replay src = Sample(1, 1, 9);
            byte[] full = ReplayEncoder.Encode(src);
            byte[] payload;
            try
            {
                payload = ReplayCompression.Decompress(full);
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ ⑸ 测试自身无效：生产端解压失败 "
                    + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }

            // KeyEvents 段的 magic（小端 0x746E65764579654B = "KeyEvent"）
            byte[] keyMagic = { 0x4B, 0x65, 0x79, 0x45, 0x76, 0x65, 0x6E, 0x74 };
            int at = IndexOfBytes(payload, keyMagic);
            if (at < 0)
            {
                Console.WriteLine("    ✗ ⑸ 找不到 KeyEvents 段，测试无效");
                return 1;
            }
            int countPos = at + 8 + 4; // magic(8) + ver(4) 之后就是 count
            if (countPos + 4 > payload.Length)
            {
                Console.WriteLine("    ✗ ⑸ 流太短，测试无效");
                return 1;
            }
            byte[] huge = BitConverter.GetBytes(0x7FFFFFFF);
            Array.Copy(huge, 0, payload, countPos, 4);

            // 必须走生产端的压缩包装层：裸 gzip 产出的字节首字节是 0x1F，
            // ReplayDecoder 会按「旧布局 gzip」路径解，虽然也能跑到 ReadCount，
            // 但那样测的就不是新格式的实际布局了
            byte[] recompressed = ReplayCompression.Compress(payload);
            try
            {
                var got = ReplayDecoder.Decode(recompressed);
                Console.WriteLine("    ✗ ⑸ 巨大 count 未被拒绝，反而解出了 "
                    + got.KeyEvents.Count + " 个事件");
                return 1;
            }
            catch (InvalidDataException ex)
            {
                // 只认 ReadCount 那条。宽泛地认 InvalidDataException 会让
                // 「解压失败」「魔数不对」「段长越界」等任何更早的拒绝都冒充成功
                string msg = ex.Message ?? "";
                bool fromCountGuard = msg.IndexOf("Record count", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("exceeds remaining stream", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!fromCountGuard)
                {
                    Console.WriteLine("    ✗ ⑸ 被拒绝但**不是** ReadCount 守卫拒的：" + msg);
                    return 1;
                }
                Console.WriteLine("    ✓ ⑸ 巨大 count 被 ReadCount 守卫干净拒绝");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ ⑸ 巨大 count 未被 InvalidDataException 拒绝："
                    + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// v1 旧回放必须仍能解码（2026-10-24 全项目审计修出的回归）。
        ///
        /// <para><b>为什么单独一条。</b>快照段的最短记录长度是 ReadCount 的收敛依据：
        /// v2 记录 42 B（41 B 字段 + 末尾 HitTick 的 1 B 存在标志），v1 记录 41 B。
        /// 早先两版共用一个 42 B 常量，于是 <c>remaining / 42</c> 对任意 N 条都算出
        /// N-1，**每一份 v1 旧回放**（r149 及更早、含上游 Spectre 存量）都在 ReadCount
        /// 里被当成「count 超过流长度」拒绝，报成文件损坏。</para>
        ///
        /// <para>构造方式：用生产端编码一份不含 HitTick 的回放（每条正好 41+1 B），
        /// 再把快照段就地改写成 v1 版式（段版本改 1、每条去掉末尾那 1 B 标志），
        /// 前后两段的字节原样保留。解码成功、条数一致、且 HitTick 为 null 才算通过。</para>
        /// </summary>
        private static int DecodesV1HitSnapshots()
        {
            const string name = "⑹ v1 旧回放（无 HitTick）仍能解码";
            const int v1RecordBytes = 41;
            try
            {
                Replay src = Sample(3, 0, 7);
                foreach (var snap in src.HitSnapshots)
                    snap.HitTick = null;   // 只测 v1 版式：连 v2 的 tick 都不写
                byte[] payload = ReplayCompression.Decompress(ReplayEncoder.Encode(src));

                // 快照段魔数（小端写出即 "HitCtext"）
                byte[] hitMagic = { 0x48, 0x69, 0x74, 0x43, 0x74, 0x65, 0x78, 0x74 };
                int at = IndexOfBytes(payload, hitMagic);
                if (at < 0)
                {
                    Console.WriteLine("    ✗ " + name + "：找不到快照段，测试无效");
                    return 1;
                }
                int countPos = at + 8 + 4;
                int count = BitConverter.ToInt32(payload, countPos);
                if (count != src.HitSnapshots.Count)
                {
                    Console.WriteLine("    ✗ " + name + "：count 读出 " + count + "，与样本 "
                        + src.HitSnapshots.Count + " 不符，测试无效");
                    return 1;
                }
                int recordsPos = countPos + 4;
                int v2RecordBytes = v1RecordBytes + 1;   // 每条都有 1 B 的"有无 tick"标志
                int tailPos = recordsPos + count * v2RecordBytes;
                if (tailPos > payload.Length)
                {
                    Console.WriteLine("    ✗ " + name + "：段长越界，测试无效");
                    return 1;
                }

                var v1 = new List<byte>(payload.Length);
                for (int i = 0; i < at; i++) v1.Add(payload[i]);
                for (int i = 0; i < hitMagic.Length; i++) v1.Add(hitMagic[i]);
                v1.AddRange(BitConverter.GetBytes(1));       // 段版本 = 1
                v1.AddRange(BitConverter.GetBytes(count));
                for (int i = 0; i < count; i++)
                    for (int j = 0; j < v1RecordBytes; j++)
                        v1.Add(payload[recordsPos + i * v2RecordBytes + j]);  // 丢末尾标志
                // **不复制 tailPos 之后的内容。** v1 回放早于「指针轨迹」段，快照段之后
                // 就是 EOF；把新版那 16 B 尾部抄过来会白送 ReadCount 一条余量
                //（139/42 = 3 恰好放行），测出来的就不是真 v1 布局了。

                Replay back = ReplayDecoder.Decode(ReplayCompression.Compress(v1.ToArray()));
                if (back.HitSnapshots.Count != count)
                {
                    Console.WriteLine("    ✗ " + name + "：解出 " + back.HitSnapshots.Count
                        + " 条，期望 " + count);
                    return 1;
                }
                for (int i = 0; i < count; i++)
                {
                    if (back.HitSnapshots[i].HitTick.HasValue)
                    {
                        Console.WriteLine("    ✗ " + name + "：v1 记录不该有 HitTick（第 " + i + " 条）");
                        return 1;
                    }
                    if (back.HitSnapshots[i].CurrentFloorID != src.HitSnapshots[i].CurrentFloorID)
                    {
                        Console.WriteLine("    ✗ " + name + "：第 " + i + " 条 floorID 不一致");
                        return 1;
                    }
                }
                Console.WriteLine("    ✓ " + name);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ " + name + "：抛异常 " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        // 原先这里有一对裸 gzip 的辅助函数。压缩层改包装格式后它们就成了
        // 假断言的来源（见 RejectsGarbage 的注释），已删除——解压/压缩一律
        // 走生产端 ReplayCompression，测试才验得到真实布局。

        private static int IndexOfBytes(byte[] hay, byte[] needle)
        {
            for (int i = 0; i <= hay.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                    if (hay[i + j] != needle[j]) { match = false; break; }
                if (match) return i;
            }
            return -1;
        }

        private static Replay Sample(int hits, int keys, int seedBias)
        {
            var r = new Replay
            {
                Metadata = new Replay.ReplayMetadata
                {
                    StartingFloorId = 0,
                    TotalFloorCount = 100,
                    SceneName = "scnGame",
                    SongName = "Sample<" + seedBias + ">",
                    ArtistName = "PortCheck",
                    JudgeMode = "Perfect",
                    HitMarginLimit = "Perfect",
                }
            };
            for (int i = 0; i < keys; i++)
                r.KeyEvents.Add(new KeyEvent
                {
                    KeyCode = (ushort)(65 + i),
                    IsPressed = i % 2 == 0,
                    SongPosition = 0.5 * (i + 1)
                });
            for (int i = 0; i < hits; i++)
                r.HitSnapshots.Add(new HitSnapshot
                {
                    CurrentFloorID = 7 + i,
                    CurrAngle = 31.0 + i,
                    OverloadCounter = 0.15f * (i + 1),
                    NoFailHit = i % 2 == 0,
                    IsAuto = i == 1,
                    NextFloorAuto = i != 1,
                    CachedAngle = 100.5 * (i + 1),
                    TargetExitAngle = -99.25 * (i + 1),
                    MidspinInfiniteMargin = i % 2 == 1,
                    RDC_auto = i == 2,
                    curFreeRoamSection = i == 2 ? 3 : 0,
                    HitTick = i == 1 ? 1234567L : null
                });
            return r;
        }
    }
}
