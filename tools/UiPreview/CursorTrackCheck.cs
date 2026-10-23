using System;
using System.Collections.Generic;
using System.IO;
using SpectreReplay.Features.Replay;

namespace UiPreview
{
    /// <summary>
    /// 指针轨迹：容器段（真实编解码源码）与按时间取样（真实 Sampler 源码）。
    ///
    /// <para><b>为什么值得单独一套断言。</b>轨迹段是尾部可选段，靠「旧解码器不查
    /// EOF」实现双向兼容——这条兼容路径一旦被改动（比如有人顺手加个 EOF 校验、
    /// 或把段挪到中间），旧文件照样读、新文件会静默丢轨迹，游戏中毫无症状，
    /// 只有成片里指针凭空消失/跳回才会被发现。这里把三种布局（无段/未知尾部/
    /// 正常段）、段版本上界、count 防护与插值语义全部钉住。</para>
    /// </summary>
    internal static class CursorTrackCheck
    {
        // 段魔数的流内字节（小端拼出 "MousePts"）
        private static readonly byte[] MousePtsMagic = { 0x4D, 0x6F, 0x75, 0x73, 0x65, 0x50, 0x74, 0x73 };

        internal static int Run()
        {
            Console.WriteLine("  ── 指针轨迹（容器段 + 按时间取样） ──");
            int fail = 0;
            fail += RoundTrip();
            fail += OldFileWithoutSection();
            fail += UnknownTrailerIgnored();
            fail += RejectsFutureVersion();
            fail += RejectsHugeCount();
            fail += SamplerChecks();
            if (fail == 0) Console.WriteLine("    ✓ 指针轨迹全部通过");
            return fail;
        }

        // ── 容器段 ──────────────────────────────────────────────

        private static List<CursorSample> SampleTrack()
        {
            // 含负 songposition（倒计时区间）与单调推进的典型轨迹
            return new List<CursorSample>
            {
                new() { SongPosition = -2.5, X = 0.10f, Y = 0.20f },
                new() { SongPosition = -0.5, X = 0.15f, Y = 0.25f },
                new() { SongPosition = 1.0,  X = 0.30f, Y = 0.40f },
                new() { SongPosition = 3.0,  X = 0.50f, Y = 0.80f },
                new() { SongPosition = 9.75, X = 0.70f, Y = 0.30f },
            };
        }

        private static int RoundTrip()
        {
            try
            {
                var src = new Replay { Metadata = new Replay.ReplayMetadata { SongName = "CursorRT" } };
                src.CursorTrack = SampleTrack();
                byte[] encoded = ReplayEncoder.Encode(src);
                Replay back = ReplayDecoder.Decode(encoded);

                var bad = new List<string>();
                if (back.CursorTrack.Count != src.CursorTrack.Count)
                    bad.Add("样本数不符 " + back.CursorTrack.Count + " vs " + src.CursorTrack.Count);
                for (int i = 0; i < src.CursorTrack.Count && i < back.CursorTrack.Count; i++)
                {
                    var a = src.CursorTrack[i];
                    var b = back.CursorTrack[i];
                    if (Math.Abs(a.SongPosition - b.SongPosition) > 1e-9
                        || Math.Abs(a.X - b.X) > 1e-6f
                        || Math.Abs(a.Y - b.Y) > 1e-6f)
                    { bad.Add("样本[" + i + "] 不一致"); break; }
                }
                return Report("⑴ 轨迹往返（含倒计时负区间）", bad);
            }
            catch (Exception ex)
            {
                return ReportException("⑴ 轨迹往返", ex);
            }
        }

        /// <summary>旧构建写出的文件没有尾部段：截掉再解码，空轨迹、其余轨完好。</summary>
        private static int OldFileWithoutSection()
        {
            try
            {
                byte[] payload = PayloadWithTrack(out var src);
                int at = IndexOfBytes(payload, MousePtsMagic);
                if (at < 0) return Report("⑵ 旧文件无轨迹段", new List<string> { "找不到轨迹段，测试无效" });
                byte[] truncated = new byte[at];
                Array.Copy(payload, truncated, at);
                Replay back = ReplayDecoder.Decode(ReplayCompression.Compress(truncated));
                var bad = new List<string>();
                if (back.CursorTrack.Count != 0)
                    bad.Add("应得空轨迹，实得 " + back.CursorTrack.Count + " 条");
                if (back.KeyEvents.Count != src.KeyEvents.Count)
                    bad.Add("旧段受损：KeyEvents 数不符");
                return Report("⑵ 旧文件无轨迹段 → 空轨迹、旧段完好", bad);
            }
            catch (Exception ex)
            {
                return ReportException("⑵ 旧文件无轨迹段", ex);
            }
        }

        /// <summary>尾部是别的数据（未来版本的新段）：与本模组魔数不符即整体忽略。</summary>
        private static int UnknownTrailerIgnored()
        {
            try
            {
                byte[] payload = PayloadWithTrack(out _);
                int at = IndexOfBytes(payload, MousePtsMagic);
                if (at < 0) return Report("⑶ 未知尾部", new List<string> { "找不到轨迹段，测试无效" });
                byte[] withJunk = new byte[at + 24];
                Array.Copy(payload, withJunk, at);
                // 一段不像任何已知魔数的尾部
                for (int i = 0; i < 24; i++) withJunk[at + i] = 0xAB;
                Replay back = ReplayDecoder.Decode(ReplayCompression.Compress(withJunk));
                var bad = new List<string>();
                if (back.CursorTrack.Count != 0)
                    bad.Add("未知尾部应被忽略，实得 " + back.CursorTrack.Count + " 条轨迹");
                return Report("⑶ 未知尾部整体忽略", bad);
            }
            catch (Exception ex)
            {
                return ReportException("⑶ 未知尾部", ex);
            }
        }

        /// <summary>段版本显式上界：未来版本必须拒绝而非按当前布局解析（防错位）。</summary>
        private static int RejectsFutureVersion()
        {
            try
            {
                byte[] payload = PayloadWithTrack(out _);
                int at = IndexOfBytes(payload, MousePtsMagic);
                if (at < 0) return Report("⑷ 段版本上界", new List<string> { "找不到轨迹段，测试无效" });
                byte[] tampered = (byte[])payload.Clone();
                tampered[at + 8] = 2; // version int32 的最低字节：1 → 2
                try
                {
                    ReplayDecoder.Decode(ReplayCompression.Compress(tampered));
                    return Report("⑷ 段版本上界", new List<string> { "未来版本未被拒绝，反而解析成功" });
                }
                catch (InvalidDataException ex)
                {
                    bool ours = (ex.Message ?? "").IndexOf("CursorTrack version", StringComparison.OrdinalIgnoreCase) >= 0;
                    return ours
                        ? Report("⑷ 段版本上界（CursorTrack version too new）", new List<string>())
                        : Report("⑷ 段版本上界", new List<string> { "被拒但不是轨迹段的版本守卫：" + ex.Message });
                }
            }
            catch (Exception ex)
            {
                return ReportException("⑷ 段版本上界", ex);
            }
        }

        /// <summary>count 防护同样覆盖轨迹段：巨大 count 走 ReadCount 拒绝。</summary>
        private static int RejectsHugeCount()
        {
            try
            {
                byte[] payload = PayloadWithTrack(out _);
                int at = IndexOfBytes(payload, MousePtsMagic);
                if (at < 0) return Report("⑸ 轨迹段 count 防护", new List<string> { "找不到轨迹段，测试无效" });
                byte[] tampered = (byte[])payload.Clone();
                byte[] huge = BitConverter.GetBytes(0x7FFFFFFF);
                Array.Copy(huge, 0, tampered, at + 12, 4); // magic(8)+ver(4) 之后是 count
                try
                {
                    ReplayDecoder.Decode(ReplayCompression.Compress(tampered));
                    return Report("⑸ 轨迹段 count 防护", new List<string> { "巨大 count 未被拒绝" });
                }
                catch (InvalidDataException ex)
                {
                    bool ours = (ex.Message ?? "").IndexOf("Record count", StringComparison.OrdinalIgnoreCase) >= 0;
                    return ours
                        ? Report("⑸ 轨迹段 count 防护（ReadCount）", new List<string>())
                        : Report("⑸ 轨迹段 count 防护", new List<string> { "被拒但不是 ReadCount：" + ex.Message });
                }
            }
            catch (Exception ex)
            {
                return ReportException("⑸ 轨迹段 count 防护", ex);
            }
        }

        private static byte[] PayloadWithTrack(out Replay src)
        {
            src = new Replay
            {
                Metadata = new Replay.ReplayMetadata { SongName = "CursorFmt" },
            };
            src.KeyEvents.Add(new KeyEvent { KeyCode = 80, IsPressed = true, SongPosition = 0.5 });
            src.CursorTrack = SampleTrack();
            byte[] encoded = ReplayEncoder.Encode(src);
            return ReplayCompression.Decompress(encoded);
        }

        // ── 按时间取样 ──────────────────────────────────────────

        private static int SamplerChecks()
        {
            int fail = 0;
            var track = SampleTrack();

            // 样本点精确命中
            fail += Check("⑹ 样本点精确命中", CursorTrackSampler.Sample(track, 1.0, out float x, out float y)
                && Math.Abs(x - 0.30f) < 1e-6 && Math.Abs(y - 0.40f) < 1e-6, $"got ({x:0.####},{y:0.####})");

            // 区间线性插值：[-0.5, 1.0] 的中点 0.25 → X/Y 各取中值
            fail += Check("⑺ 区间线性插值（中点）", CursorTrackSampler.Sample(track, 0.25, out x, out y)
                && Math.Abs(x - 0.225f) < 1e-5 && Math.Abs(y - 0.325f) < 1e-5, $"got ({x:0.####},{y:0.####})");

            // 倒计时负区间：[-2.5, -0.5] 的 1/4 处 -2.0 → X = 0.10 + (0.15-0.10)*0.25
            fail += Check("⑻ 倒计时负区间插值", CursorTrackSampler.Sample(track, -2.0, out x, out y)
                && Math.Abs(x - 0.1125f) < 1e-5 && Math.Abs(y - 0.2125f) < 1e-5, $"got ({x:0.####},{y:0.####})");

            // 首前/尾后夹取
            fail += Check("⑼ 首前夹取", CursorTrackSampler.Sample(track, -99.0, out x, out y)
                && Math.Abs(x - 0.10f) < 1e-6 && Math.Abs(y - 0.20f) < 1e-6, $"got ({x:0.####},{y:0.####})");
            fail += Check("⑽ 尾后夹取", CursorTrackSampler.Sample(track, 99.0, out x, out y)
                && Math.Abs(x - 0.70f) < 1e-6 && Math.Abs(y - 0.30f) < 1e-6, $"got ({x:0.####},{y:0.####})");

            // 空表 / 单样本
            fail += Check("⑾ 空表返回 false", !CursorTrackSampler.Sample(new List<CursorSample>(), 1.0, out _, out _), "");
            fail += Check("⑿ 单样本恒取该点", CursorTrackSampler.Sample(
                new List<CursorSample> { new() { SongPosition = 5.0, X = 0.9f, Y = 0.1f } },
                -1.0, out x, out y) && Math.Abs(x - 0.9f) < 1e-6 && Math.Abs(y - 0.1f) < 1e-6,
                $"got ({x:0.####},{y:0.####})");

            return fail;
        }

        // ── 断言小工具 ──────────────────────────────────────────

        private static int Check(string name, bool ok, string detail)
        {
            if (ok) { Console.WriteLine("    ✓ " + name); return 0; }
            Console.WriteLine("    ✗ " + name + (string.IsNullOrEmpty(detail) ? "" : "：" + detail));
            return 1;
        }

        private static int Report(string name, List<string> bad)
        {
            if (bad.Count == 0) { Console.WriteLine("    ✓ " + name); return 0; }
            foreach (var p in bad) Console.WriteLine("    ✗ " + name + "：" + p);
            return bad.Count;
        }

        private static int ReportException(string name, Exception ex)
        {
            Console.WriteLine("    ✗ " + name + "：抛异常 " + ex.GetType().Name + ": " + ex.Message);
            return 1;
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
    }
}
