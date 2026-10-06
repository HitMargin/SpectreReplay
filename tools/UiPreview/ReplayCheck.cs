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
                if (back.HitContexts.Count != src.HitContexts.Count)
                    bad.Add("HitContexts 数不符 " + back.HitContexts.Count + " vs " + src.HitContexts.Count);
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
                for (int i = 0; i < src.HitContexts.Count && i < back.HitContexts.Count; i++)
                {
                    var a = src.HitContexts[i]; var b = back.HitContexts[i];
                    if (a.CurrentFloorID != b.CurrentFloorID
                        || Math.Abs(a.CachedAngle - b.CachedAngle) > 1e-9
                        || Math.Abs(a.TargetExitAngle - b.TargetExitAngle) > 1e-9
                        || a.IsAuto != b.IsAuto || a.NoFailHit != b.NoFailHit
                        || a.MidspinInfiniteMargin != b.MidspinInfiniteMargin)
                    { bad.Add("HitContexts[" + i + "] 不一致"); break; }
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
        /// </summary>
        private static int RejectsGarbage()
        {
            try
            {
                Replay src = Sample(1, 1, 9);
                byte[] full = ReplayEncoder.Encode(src);
                byte[] payload = GzipDecompressTest(full);

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

                byte[] recompressed = GzipCompressTest(payload);
                var got = ReplayDecoder.Decode(recompressed);
                Console.WriteLine("    ✗ ⑸ 巨大 count 未被拒绝，反而解出了 "
                    + got.KeyEvents.Count + " 个事件");
                return 1;
            }
            catch (InvalidDataException)
            {
                Console.WriteLine("    ✓ ⑸ 巨大 count 被干净拒绝（InvalidDataException）");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ ⑸ 巨大 count 未被 InvalidDataException 拒绝："
                    + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static byte[] GzipDecompressTest(byte[] data)
        {
            using var input = new System.IO.MemoryStream(data);
            using var gz = new System.IO.Compression.GZipStream(input,
                System.IO.Compression.CompressionMode.Decompress);
            using var output = new System.IO.MemoryStream();
            gz.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] GzipCompressTest(byte[] data)
        {
            using var output = new System.IO.MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(output,
                       System.IO.Compression.CompressionLevel.Optimal))
                gz.Write(data, 0, data.Length);
            return output.ToArray();
        }

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
                r.HitContexts.Add(new HitContext
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
