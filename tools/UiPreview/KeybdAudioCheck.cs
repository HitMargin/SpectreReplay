using System;
using System.Collections.Generic;
using System.IO;
using SpectreReplay.Features.Replay;

namespace UiPreview
{
    /// <summary>
    /// 嵌入键盘音：容器段（真实编解码源码）与压缩编解码（真实 Codec 源码）。
    ///
    /// <para><b>为什么必须离线钉住。</b>这一段的三类缺陷在游戏里全都**没有异常**：
    /// ① 段顺序/位置读错 → 键盘音静默消失，玩家以为本来就没录；
    /// ② 用原始 wav 的 PCM 哈希去校验有损重编码 → 每份新回放都判「校验失败 → 无声」；
    /// ③ 负载长度/样本数没做上界 → 一个畸形字段直接分配几个 GB。
    /// 前两条只表现为「没声音」，第三条是崩溃，没人会联想到是格式段的问题。</para>
    ///
    /// <para>与 <see cref="CursorTrackCheck"/> 同一套思路：段是尾部的可选段，
    /// 靠「旧解码器读完轨迹就返回」实现双向兼容，所以这里把「与轨迹共存」也当成
    /// 独立断言——那正是「未知魔数要回退位置」这条修复的回归点。</para>
    /// </summary>
    internal static class KeybdAudioCheck
    {
        // 段魔数的流内字节（小端拼出 "KbdAudio"）
        private static readonly byte[] KbdAudioMagic = { 0x4B, 0x62, 0x64, 0x41, 0x75, 0x64, 0x69, 0x6F };

        // 段头长度：魔数8 + 段版本4 + 编码4 + 采样率4 + 声道4 + 样本数4 + 负载长度4
        private const int HeadLen = 8 + 4 + 4 + 4 + 4 + 4 + 4;

        internal static int Run()
        {
            Console.WriteLine("  ── 嵌入键盘音（容器段 + 压缩编解码） ──");
            int fail = 0;
            fail += RoundTrip();
            fail += CoexistsWithCursorTrack();
            fail += OldFileWithoutSection();
            fail += SectionIsLastInPlaintext();
            fail += RejectsFutureVersion();
            fail += RejectsHugePayloadLength();
            fail += CodecRoundTrip();
            fail += CodecRobustness();
            if (fail == 0) Console.WriteLine("    ✓ 嵌入键盘音全部通过");
            return fail;
        }

        // ── 容器段 ──────────────────────────────────────────────

        private static KeybdAudioBlob SampleBlob()
        {
            // 用 Codec 真压一段合成音频，而不是手搓随机字节：段里存的本来就是压缩负载，
            // 手搓的字节会把「Codec 是否可逆」这条漏掉。
            byte[] wav = MakeKeybdWav(1.0);
            return KeybdAudioCodec.Encode(wav, KeybdAudioCodec.QualityExtreme, out _);
        }

        private static int RoundTrip()
        {
            try
            {
                var src = new Replay { Metadata = new Replay.ReplayMetadata { SongName = "KbdRT" } };
                src.KeybdAudio = SampleBlob();
                if (!src.KeybdAudio.HasData)
                    return Report("⑴ 键盘音段往返", new List<string> { "测试用压缩块为空，测试无效" });

                Replay back = ReplayDecoder.Decode(ReplayEncoder.Encode(src));
                var bad = new List<string>();
                if (!back.KeybdAudio.HasData) bad.Add("读回后没有音频段");
                else
                {
                    if (back.KeybdAudio.Codec != src.KeybdAudio.Codec) bad.Add("Codec 不一致");
                    if (back.KeybdAudio.SampleRate != src.KeybdAudio.SampleRate) bad.Add("采样率不一致");
                    if (back.KeybdAudio.Channels != src.KeybdAudio.Channels) bad.Add("声道数不一致");
                    if (back.KeybdAudio.SampleCount != src.KeybdAudio.SampleCount) bad.Add("样本数不一致");
                    if (!SameBytes(back.KeybdAudio.Payload, src.KeybdAudio.Payload)) bad.Add("负载字节不一致");
                }
                return Report("⑴ 键盘音段往返（字段 + 负载逐字节）", bad);
            }
            catch (Exception ex)
            {
                return ReportException("⑴ 键盘音段往返", ex);
            }
        }

        /// <summary>
        /// 音频段与指针轨迹共存。
        ///
        /// <para>这是「探测未知魔数必须回退流位置」的**唯一回归点**：旧实现直接 return，
        /// 位置已前进 8 字节，紧随其后的音频段整体错位，表现为「有轨迹但没声音」。</para>
        /// </summary>
        private static int CoexistsWithCursorTrack()
        {
            try
            {
                var src = new Replay { Metadata = new Replay.ReplayMetadata { SongName = "KbdBoth" } };
                src.KeybdAudio = SampleBlob();
                src.CursorTrack = new List<CursorSample>
                {
                    new() { SongPosition = -1.5, X = 0.1f, Y = 0.2f },
                    new() { SongPosition = 2.0, X = 0.5f, Y = 0.8f },
                };

                Replay back = ReplayDecoder.Decode(ReplayEncoder.Encode(src));
                var bad = new List<string>();
                if (back.CursorTrack.Count != src.CursorTrack.Count)
                    bad.Add("轨迹条数 " + back.CursorTrack.Count + " vs " + src.CursorTrack.Count);
                else if (Math.Abs(back.CursorTrack[1].X - 0.5f) > 1e-6f)
                    bad.Add("轨迹内容不一致");
                if (!back.KeybdAudio.HasData)
                    bad.Add("音频段被轨迹段挤掉（位置未回退？）");
                else if (back.KeybdAudio.SampleCount != src.KeybdAudio.SampleCount)
                    bad.Add("音频段读到错位数据");
                return Report("⑵ 与指针轨迹共存（回退位置回归点）", bad);
            }
            catch (Exception ex)
            {
                return ReportException("⑵ 与指针轨迹共存", ex);
            }
        }

        /// <summary>旧构建写出的文件没有音频段：HasData=false，且旧段完好（走旁挂 wav 回落）。</summary>
        private static int OldFileWithoutSection()
        {
            try
            {
                byte[] payload = PayloadWithAudio(out Replay src);
                int at = IndexOfBytes(payload, KbdAudioMagic);
                if (at < 0) return Report("⑶ 旧文件无音频段", new List<string> { "找不到音频段，测试无效" });
                byte[] truncated = new byte[at];
                Array.Copy(payload, truncated, at);

                Replay back = ReplayDecoder.Decode(ReplayCompression.Compress(truncated));
                var bad = new List<string>();
                if (back.KeybdAudio.HasData) bad.Add("旧文件不该有音频段");
                if (back.CursorTrack.Count != src.CursorTrack.Count) bad.Add("轨迹受损");
                if (back.KeyEvents.Count != src.KeyEvents.Count) bad.Add("按键事件受损");
                return Report("⑶ 旧文件无音频段 → HasData=false、旧段完好", bad);
            }
            catch (Exception ex)
            {
                return ReportException("⑶ 旧文件无音频段", ex);
            }
        }

        /// <summary>音频段必须是明文里的最后一段（旧构建读到轨迹就返回，永远碰不到它）。</summary>
        private static int SectionIsLastInPlaintext()
        {
            try
            {
                byte[] payload = PayloadWithAudio(out Replay src);
                int at = IndexOfBytes(payload, KbdAudioMagic);
                var bad = new List<string>();
                if (at < 0) bad.Add("找不到音频段");
                else
                {
                    int expect = payload.Length - (HeadLen + src.KeybdAudio.Payload.Length);
                    if (at != expect)
                        bad.Add("段位置 " + at + " ≠ 期望 " + expect + "（后面还有别的段？）");
                    int lenField = BitConverter.ToInt32(payload, at + 28);
                    if (lenField != src.KeybdAudio.Payload.Length)
                        bad.Add("长度字段 " + lenField + " vs " + src.KeybdAudio.Payload.Length);
                }
                return Report("⑷ 音频段是明文最后一段（旧构建读不到）", bad);
            }
            catch (Exception ex)
            {
                return ReportException("⑷ 音频段位置", ex);
            }
        }

        private static int RejectsFutureVersion()
        {
            try
            {
                byte[] payload = PayloadWithAudio(out _);
                int at = IndexOfBytes(payload, KbdAudioMagic);
                if (at < 0) return Report("⑸ 段版本上界", new List<string> { "找不到音频段" });
                byte[] tampered = (byte[])payload.Clone();
                tampered[at + 8] = 2;   // 段版本 int32 最低字节：1 → 2
                try
                {
                    ReplayDecoder.Decode(ReplayCompression.Compress(tampered));
                    return Report("⑸ 段版本上界", new List<string> { "未来版本未被拒绝" });
                }
                catch (InvalidDataException ex)
                {
                    bool ours = (ex.Message ?? "").IndexOf("KeybdAudio version", StringComparison.OrdinalIgnoreCase) >= 0;
                    return ours
                        ? Report("⑸ 段版本上界（KeybdAudio version too new）", new List<string>())
                        : Report("⑸ 段版本上界", new List<string> { "被拒但不是本段守卫：" + ex.Message });
                }
            }
            catch (Exception ex)
            {
                return ReportException("⑸ 段版本上界", ex);
            }
        }

        /// <summary>负载长度来自文件、是外部输入：声称比流还长时必须拒绝，而不是先分配再失败。</summary>
        private static int RejectsHugePayloadLength()
        {
            try
            {
                byte[] payload = PayloadWithAudio(out _);
                int at = IndexOfBytes(payload, KbdAudioMagic);
                if (at < 0) return Report("⑹ 负载长度上界", new List<string> { "找不到音频段" });
                byte[] tampered = (byte[])payload.Clone();
                Array.Copy(BitConverter.GetBytes(0x7FFFFFFF), 0, tampered, at + 28, 4);
                try
                {
                    ReplayDecoder.Decode(ReplayCompression.Compress(tampered));
                    return Report("⑹ 负载长度上界", new List<string> { "巨大负载长度未被拒绝" });
                }
                catch (InvalidDataException ex)
                {
                    bool ours = (ex.Message ?? "").IndexOf("KeybdAudio payload", StringComparison.OrdinalIgnoreCase) >= 0;
                    return ours
                        ? Report("⑹ 负载长度上界（不预分配）", new List<string>())
                        : Report("⑹ 负载长度上界", new List<string> { "被拒但不是本段守卫：" + ex.Message });
                }
            }
            catch (Exception ex)
            {
                return ReportException("⑹ 负载长度上界", ex);
            }
        }

        // ── 压缩编解码 ──────────────────────────────────────────

        private static int CodecRoundTrip()
        {
            int fail = 0;
            try
            {
                byte[] wav = MakeKeybdWav(2.0);
                KeybdAudioBlob b = KeybdAudioCodec.Encode(wav, KeybdAudioCodec.QualityBalanced, out string err);
                fail += Check("⑺ 编码成功", b.HasData, err ?? "");
                if (!b.HasData) return fail;

                // 压缩率：键盘音是「长静音 + 短脉冲」，正常应远好于 4 倍
                fail += Check("⑻ 压缩率优于 4 倍",
                    b.Payload.Length * 4 < wav.Length,
                    $"{wav.Length} → {b.Payload.Length} 字节");

                fail += Check("⑼ 解码样本数一致",
                    KeybdAudioCodec.TryDecodeToSamples(b, out float[] s, out int rate, out string de2)
                    && s.Length == b.SampleCount && rate == b.SampleRate,
                    de2 ?? "");

                // 时长必须守恒：降采样只改采样率，不该改长度
                double srcDur = 2.0;
                double outDur = (double)b.SampleCount / Math.Max(1, b.SampleRate);
                fail += Check("⑽ 时长守恒", Math.Abs(outDur - srcDur) < 0.02, $"{outDur:F3}s vs {srcDur:F3}s");

                // 导出：必须是一个头部自洽的标准 wav
                byte[] outWav = KeybdAudioCodec.DecodeToWav(b);
                bool headOk = outWav != null && outWav.Length > 44
                    && IsTag(outWav, 0, 'R', 'I', 'F', 'F')
                    && IsTag(outWav, 8, 'W', 'A', 'V', 'E')
                    && IsTag(outWav, 36, 'd', 'a', 't', 'a')
                    && BitConverter.ToInt32(outWav, 40) == outWav.Length - 44;
                fail += Check("⑾ 导出 wav 头自洽", headOk, "");
                if (headOk)
                {
                    int oRate = BitConverter.ToInt32(outWav, 24);
                    int oCh = BitConverter.ToInt16(outWav, 22);
                    int oBits = BitConverter.ToInt16(outWav, 34);
                    double oDur = (double)(outWav.Length - 44) / Math.Max(1, oRate * oCh * (oBits / 8));
                    fail += Check("⑿ 导出时长与原音一致", Math.Abs(oDur - outDur) < 0.02, $"{oDur:F3}s vs {outDur:F3}s");
                }
            }
            catch (Exception ex)
            {
                fail += ReportException("⑺-⑿ 压缩编解码", ex);
            }
            return fail;
        }

        private static int CodecRobustness()
        {
            int fail = 0;
            // 未知编码格式：拒绝，不抛异常
            var unknown = new KeybdAudioBlob
            {
                Codec = 99, SampleRate = 16000, Channels = 1,
                SampleCount = 1000, Payload = new byte[10],
            };
            fail += Check("⒀ 未知编码格式被拒",
                !KeybdAudioCodec.TryDecodeToSamples(unknown, out _, out _, out _)
                && KeybdAudioCodec.DecodeToWav(unknown) == null, "");

            // 荒谬样本数：必须先判上界，不能拿它去分配
            var huge = new KeybdAudioBlob
            {
                Codec = KeybdAudioCodec.CodecAdpcm, SampleRate = 16000, Channels = 1,
                SampleCount = int.MaxValue, Payload = new byte[16],
            };
            fail += Check("⒁ 荒谬样本数被拒（不 OOM）",
                !KeybdAudioCodec.TryDecodeToSamples(huge, out _, out _, out _), "");

            // 空块：两条路线都不能崩
            var empty = new KeybdAudioBlob();
            fail += Check("⒂ 空块不崩",
                !empty.HasData && KeybdAudioCodec.DecodeToWav(empty) == null, "");

            // 非 wav 字节：编码必须失败而不是抛
            KeybdAudioBlob junk = KeybdAudioCodec.Encode(new byte[] { 1, 2, 3, 4, 5 }, KeybdAudioCodec.QualityBalanced, out string je);
            fail += Check("⒃ 非法 wav 被拒", !junk.HasData && !string.IsNullOrEmpty(je), je ?? "");
            return fail;
        }

        // ── 工具 ────────────────────────────────────────────────

        private static byte[] PayloadWithAudio(out Replay src)
        {
            src = new Replay { Metadata = new Replay.ReplayMetadata { SongName = "KbdFmt" } };
            src.KeyEvents.Add(new KeyEvent { KeyCode = 80, IsPressed = true, SongPosition = 0.5 });
            src.CursorTrack.Add(new CursorSample { SongPosition = 1.0, X = 0.3f, Y = 0.4f });
            src.KeybdAudio = SampleBlob();
            return ReplayCompression.Decompress(ReplayEncoder.Encode(src));
        }

        /// <summary>合成「像键盘音」的 wav：扫频 + 长静音 + 短促脉冲串，44.1k mono 16bit。</summary>
        private static byte[] MakeKeybdWav(double seconds)
        {
            const int rate = 44100;
            int n = (int)(seconds * rate);
            var pcm = new short[n];
            var rnd = new Random(4242);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate;
                double v;
                if (t < 0.4)
                {
                    double f = 200 + 3000 * t;
                    v = 0.35 * Math.Sin(2 * Math.PI * f * t);
                }
                else if (t < seconds * 0.6)
                {
                    v = 0.0004 * (rnd.NextDouble() * 2 - 1);   // 静音段（略含底噪）
                }
                else
                {
                    double phase = t % 0.15;
                    v = phase < 0.008
                        ? 0.9 * Math.Exp(-phase * 500) * (rnd.NextDouble() * 2 - 1)
                        : 0.0003 * (rnd.NextDouble() * 2 - 1);
                }
                if (v > 1) v = 1; else if (v < -1) v = -1;
                pcm[i] = (short)(v * 32767);
            }
            int dataLen = n * 2;
            var wav = new byte[44 + dataLen];
            WriteTag(wav, 0, "RIFF");
            BitConverter.GetBytes(36 + dataLen).CopyTo(wav, 4);
            WriteTag(wav, 8, "WAVE");
            WriteTag(wav, 12, "fmt ");
            BitConverter.GetBytes(16).CopyTo(wav, 16);
            BitConverter.GetBytes((short)1).CopyTo(wav, 20);
            BitConverter.GetBytes((short)1).CopyTo(wav, 22);
            BitConverter.GetBytes(rate).CopyTo(wav, 24);
            BitConverter.GetBytes(rate * 2).CopyTo(wav, 28);
            BitConverter.GetBytes((short)2).CopyTo(wav, 32);
            BitConverter.GetBytes((short)16).CopyTo(wav, 34);
            WriteTag(wav, 36, "data");
            BitConverter.GetBytes(dataLen).CopyTo(wav, 40);
            Buffer.BlockCopy(pcm, 0, wav, 44, dataLen);
            return wav;
        }

        private static void WriteTag(byte[] b, int off, string tag)
        {
            for (int i = 0; i < 4; i++) b[off + i] = (byte)tag[i];
        }

        private static bool IsTag(byte[] b, int off, char a, char c, char d, char e)
            => b != null && b.Length >= off + 4 && b[off] == (byte)a && b[off + 1] == (byte)c
               && b[off + 2] == (byte)d && b[off + 3] == (byte)e;

        private static bool SameBytes(byte[] x, byte[] y)
        {
            if (x == null || y == null) return x == y;
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
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

        private static int Check(string name, bool ok, string detail)
        {
            if (ok) { Console.WriteLine("    ✓ " + name); return 0; }
            Console.WriteLine("    ✗ " + name + (string.IsNullOrEmpty(detail) ? "" : "：" + detail));
            return 1;
        }

        private static int Report(string name, List<string> bad)
        {
            if (bad.Count == 0) { Console.WriteLine("    ✓ " + name); return 0; }
            foreach (string p in bad) Console.WriteLine("    ✗ " + name + "：" + p);
            return bad.Count;
        }

        private static int ReportException(string name, Exception ex)
        {
            Console.WriteLine("    ✗ " + name + "：抛异常 " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }
}
