using System;
using System.Collections.Generic;
using System.IO;
using SpectreReplay.Features.Replay;

namespace UiPreview
{
    /// <summary>
    /// 逐份回放的键盘音覆盖值（音量 / 偏移 / 静音）：尾部可选段，接在键盘音段之后。
    ///
    /// <para><b>为什么必须离线钉住。</b>这一段的失败方式与其它尾部段同族——全都**没有异常**：
    /// ① 段没写出来（或写了但旧段挤掉）→ 播放仍旧按全局设置，听感「差不多」，没人报；
    /// ② 位置回退不对 → 覆盖值读到垃圾，表现成「音量突然是 0」「静音开关失效」；
    /// ③ 变长/错位 → 严重时整份回放加载失败，而回收到的只有一句「文件损坏」。
    /// 尤其 ③：覆盖值本身很小，改坏一个字节就能让后面所有段的偏移全乱。</para>
    ///
    /// <para>与 <see cref="CursorTrackCheck"/> / <see cref="KeybdAudioCheck"/> 同一套思路：
    /// 段靠「旧解码器读到轨迹就返回」实现双向兼容，所以「没有这一段」与「有这一段」、
    /// 「跟音频段共存」与「只有轨迹没有音频」，都是独立断言而不是顺带覆盖。</para>
    /// </summary>
    internal static class KeybdPrefsCheck
    {
        // 段魔数的流内字节（小端拼出 "KbdPrefs"）
        private static readonly byte[] KbdPrefsMagic = { 0x4B, 0x62, 0x64, 0x50, 0x72, 0x65, 0x66, 0x73 };

        // 段长：魔数 8 + 段版本 4 + 标志位 1 + 音量 4 + 偏移 4。
        // 定长是刻意的：某一项没有也照写 4 字节，否则解码端无法按固定偏移跳过。
        private const int SectionLen = 8 + 4 + 1 + 4 + 4;

        internal static int Run()
        {
            Console.WriteLine("  ── 逐份回放键盘音覆盖值（尾部可选段） ──");
            int fail = 0;
            fail += RoundTrip();
            fail += NotWrittenWhenEmpty();
            fail += CoexistsWithCursorAndAudio();
            fail += CursorOnlyWithoutAudio();
            fail += RejectsFutureVersion();
            fail += GarbageTrailerIgnored();
            fail += HasAnySemantics();
            fail += DataContainerTransfer();
            if (fail == 0) Console.WriteLine("    ✓ 键盘音覆盖值全部通过");
            return fail;
        }

        // ── ⑴ 往返 ──────────────────────────────────────────────

        /// <summary>
        /// 三种覆盖项的典型组合各自往返。单项必须单独验：三项共用一条 <c>HasAny</c>
        /// 却各占一个标志位，只测「全有」会漏掉「只置了某一位而写入端漏写它」这类错。
        /// </summary>
        private static int RoundTrip()
        {
            int fail = 0;
            fail += RoundTripOne("⑴a 只有音量", new KeybdPrefs { HasVolume = true, Volume = 7 });
            fail += RoundTripOne("⑴b 只有偏移", new KeybdPrefs { HasOffset = true, OffsetMs = -120 });
            fail += RoundTripOne("⑴c 只有静音", new KeybdPrefs { IsMuted = true });
            fail += RoundTripOne("⑴d 音量+偏移", new KeybdPrefs
            {
                HasVolume = true, Volume = 3, HasOffset = true, OffsetMs = 85
            });
            fail += RoundTripOne("⑴e 全有（音量+偏移+静音）", new KeybdPrefs
            {
                HasVolume = true, Volume = 10, HasOffset = true, OffsetMs = -40, IsMuted = true
            });
            return fail;
        }

        private static int RoundTripOne(string name, KeybdPrefs prefs)
        {
            try
            {
                var src = new Replay
                {
                    Metadata = new Replay.ReplayMetadata { SongName = "PrefsRT" },
                    KeybdPrefs = prefs,
                };
                Replay back = ReplayDecoder.Decode(ReplayEncoder.Encode(src));
                var bad = new List<string>();
                if (!Same(prefs, back.KeybdPrefs))
                    bad.Add("源 " + Describe(prefs) + " vs 读回 " + Describe(back.KeybdPrefs));
                return Report(name, bad);
            }
            catch (Exception ex)
            {
                return ReportException(name, ex);
            }
        }

        // ── ⑵ 没有覆盖值就不写段 ────────────────────────────────

        /// <summary>
        /// 全默认时**一个字节都不写**。这条不是洁癖：段存在与否是「有没有覆盖」的
        /// 唯一表示。若写成「段在但全 0」，读取端拿到 HasAny=true、音量 0，
        /// 全局设置的音量就被这份回放静默清零——听感上等同于文件坏了。
        /// </summary>
        private static int NotWrittenWhenEmpty()
        {
            int fail = 0;
            try
            {
                var src = new Replay { Metadata = new Replay.ReplayMetadata { SongName = "PrefsNone" } };
                src.KeyEvents.Add(new KeyEvent { KeyCode = 80, IsPressed = true, SongPosition = 0.5 });

                byte[] payload = ReplayCompression.Decompress(ReplayEncoder.Encode(src));
                fail += Check("⑵a 没有覆盖值 → 明文里搜不到该段魔数",
                    IndexOfBytes(payload, KbdPrefsMagic) < 0, "明文里出现了 KbdPrefs 魔数");

                Replay back = ReplayDecoder.Decode(ReplayEncoder.Encode(src));
                fail += Check("⑵b 没有覆盖值 → 解码得 default 且 HasAny=false",
                    !back.KeybdPrefs.HasAny
                    && !back.KeybdPrefs.HasVolume && !back.KeybdPrefs.HasOffset
                    && !back.KeybdPrefs.IsMuted
                    && back.KeybdPrefs.Volume == 0 && back.KeybdPrefs.OffsetMs == 0,
                    Describe(back.KeybdPrefs));
            }
            catch (Exception ex)
            {
                fail += ReportException("⑵ 没有覆盖值", ex);
            }
            return fail;
        }

        // ── ⑶ 与轨迹 + 音频共存 ─────────────────────────────────

        /// <summary>
        /// 三段都在时全部读对。
        ///
        /// <para>这是「探测未知魔数必须回退流位置」的**第二个回归点**：覆盖段紧跟在
        /// 音频段之后，而音频段的负载是变长的——任何一端把「段不存在」当成
        /// 「位置停在这」处理，覆盖值就会从负载中间开始读。</para>
        /// </summary>
        private static int CoexistsWithCursorAndAudio()
        {
            try
            {
                var src = new Replay { Metadata = new Replay.ReplayMetadata { SongName = "PrefsAll" } };
                src.KeyEvents.Add(new KeyEvent { KeyCode = 80, IsPressed = true, SongPosition = 0.5 });
                src.CursorTrack.Add(new CursorSample { SongPosition = 1.5, X = 0.25f, Y = 0.75f });
                src.KeybdAudio = SampleAudio();
                src.KeybdPrefs = new KeybdPrefs
                {
                    HasVolume = true, Volume = 4, HasOffset = true, OffsetMs = 33, IsMuted = true
                };

                Replay back = ReplayDecoder.Decode(ReplayEncoder.Encode(src));
                var bad = new List<string>();
                if (back.CursorTrack.Count != 1 || Math.Abs(back.CursorTrack[0].Y - 0.75f) > 1e-6f)
                    bad.Add("轨迹读坏（位置回退不对？）");
                if (!back.KeybdAudio.HasData
                    || back.KeybdAudio.SampleCount != src.KeybdAudio.SampleCount
                    || !SameBytes(back.KeybdAudio.Payload, src.KeybdAudio.Payload))
                    bad.Add("音频段读坏（位置回退不对？）");
                if (!Same(src.KeybdPrefs, back.KeybdPrefs))
                    bad.Add("覆盖值不一致：" + Describe(back.KeybdPrefs));

                // 顺带把布局钉住：段必须正好在明文末尾、长度定长、标志位与字段对得上。
                // 断开这一点时前面三条通常还能过（读到的是别处的字节），却已经错位了。
                byte[] payload = ReplayCompression.Decompress(ReplayEncoder.Encode(src));
                int at = IndexOfBytes(payload, KbdPrefsMagic);
                if (at < 0) bad.Add("明文里找不到覆盖段");
                else
                {
                    if (at + SectionLen != payload.Length)
                        bad.Add("段不在末尾或长度不是定长 " + SectionLen);
                    if (payload[at + 12] != 0x07)
                        bad.Add("标志位应为 0x07（三项都有），实为 0x" + payload[at + 12].ToString("X2"));
                    if (BitConverter.ToInt32(payload, at + 13) != 4)
                        bad.Add("音量字段不符");
                    if (BitConverter.ToInt32(payload, at + 17) != 33)
                        bad.Add("偏移字段不符");
                }
                return Report("⑶ 轨迹 + 音频 + 覆盖值三者共存（回退位置回归点）", bad);
            }
            catch (Exception ex)
            {
                return ReportException("⑶ 三者共存", ex);
            }
        }

        // ── ⑷ 只有轨迹、没有音频，但有覆盖值 ────────────────────

        /// <summary>
        /// 没有音频段时，音频探测器在覆盖段开头只剩 21 字节（&lt; 音频段头的 32），
        /// 会走「剩余不足 → 直接 return default」的早退分支。这条分支**不能吞掉**
        /// 后面的覆盖段，也不能因为早退就把位置留在别处。
        /// </summary>
        private static int CursorOnlyWithoutAudio()
        {
            try
            {
                var src = new Replay { Metadata = new Replay.ReplayMetadata { SongName = "PrefsNoAudio" } };
                src.CursorTrack.Add(new CursorSample { SongPosition = -1.0, X = 0.1f, Y = 0.2f });
                src.CursorTrack.Add(new CursorSample { SongPosition = 2.0, X = 0.5f, Y = 0.8f });
                src.KeybdPrefs = new KeybdPrefs { HasOffset = true, OffsetMs = -75 };
                // KeybdAudio 保持 default：这正是「轨迹之后直接跟覆盖段」的布局

                Replay back = ReplayDecoder.Decode(ReplayEncoder.Encode(src));
                var bad = new List<string>();
                if (back.CursorTrack.Count != 2 || Math.Abs(back.CursorTrack[1].X - 0.5f) > 1e-6f)
                    bad.Add("轨迹读坏");
                if (back.KeybdAudio.HasData)
                    bad.Add("源里没有音频段，读回却有");
                if (!Same(src.KeybdPrefs, back.KeybdPrefs))
                    bad.Add("覆盖值被音频探测吞掉/错位：" + Describe(back.KeybdPrefs));
                return Report("⑷ 只有轨迹（无音频）+ 覆盖值", bad);
            }
            catch (Exception ex)
            {
                return ReportException("⑷ 只有轨迹 + 覆盖值", ex);
            }
        }

        // ── ⑸ 段版本上界 ────────────────────────────────────────

        /// <summary>未来版本必须明确拒绝：定长布局里多出字段，按 v1 解析就是读垃圾。</summary>
        private static int RejectsFutureVersion()
        {
            try
            {
                byte[] payload = PayloadWithAll(out _);
                int at = IndexOfBytes(payload, KbdPrefsMagic);
                if (at < 0) return Report("⑸ 段版本上界", new List<string> { "找不到覆盖段，测试无效" });
                byte[] tampered = (byte[])payload.Clone();
                tampered[at + 8] = 2;   // 段版本 int32 最低字节：1 → 2
                try
                {
                    ReplayDecoder.Decode(ReplayCompression.Compress(tampered));
                    return Report("⑸ 段版本上界", new List<string> { "未来版本未被拒绝，反而解析成功" });
                }
                catch (InvalidDataException ex)
                {
                    bool ours = (ex.Message ?? "").IndexOf("KeybdPrefs version", StringComparison.OrdinalIgnoreCase) >= 0;
                    return ours
                        ? Report("⑸ 段版本上界（KeybdPrefs version too new）", new List<string>())
                        : Report("⑸ 段版本上界", new List<string> { "被拒但不是本段守卫：" + ex.Message });
                }
            }
            catch (Exception ex)
            {
                return ReportException("⑸ 段版本上界", ex);
            }
        }

        // ── ⑹ 垃圾尾部 ──────────────────────────────────────────

        /// <summary>
        /// 覆盖段位置上是垃圾（魔数不符 / 长度不足）。
        ///
        /// <para>两种情况都必须**安全返回 default 且不抛**：旧回放在此已 EOF，
        /// 未来版本的尾部也可能是不认识的段。把它们判成「文件损坏」等于
        /// 让所有存量文件一次性打不开；而更隐蔽的是位置没回退——那样前面
        /// 已经读对的轨迹与音频会被这一次探测的重读弄丢。</para>
        /// </summary>
        private static int GarbageTrailerIgnored()
        {
            int fail = 0;
            try
            {
                byte[] payload = PayloadWithCursorAndAudio(out Replay src);

                // (a) 够长（≥ 段长）但魔数不匹配：整段视为未知尾部
                byte[] junkLong = Append(payload, 24, 0xAB);
                Replay backLong = ReplayDecoder.Decode(ReplayCompression.Compress(junkLong));
                var bad = new List<string>();
                if (backLong.KeybdPrefs.HasAny)
                    bad.Add("垃圾尾部被当成覆盖值：" + Describe(backLong.KeybdPrefs));
                if (backLong.CursorTrack.Count != src.CursorTrack.Count
                    || Math.Abs(backLong.CursorTrack[0].X - 0.30f) > 1e-6f)
                    bad.Add("轨迹被垃圾尾部影响");
                if (!backLong.KeybdAudio.HasData
                    || backLong.KeybdAudio.SampleCount != src.KeybdAudio.SampleCount)
                    bad.Add("音频段被垃圾尾部影响");
                fail += Report("⑹a 垃圾尾部（魔数不符）→ default、已读段完好", bad);

                // (b) 不足一个段头（10 字节）：走「剩余不足」早退分支，同样不许抛
                byte[] junkShort = Append(payload, 10, 0xAB);
                Replay backShort = ReplayDecoder.Decode(ReplayCompression.Compress(junkShort));
                var bad2 = new List<string>();
                if (backShort.KeybdPrefs.HasAny)
                    bad2.Add("截短的尾部被当成覆盖值：" + Describe(backShort.KeybdPrefs));
                if (backShort.CursorTrack.Count != src.CursorTrack.Count
                    || !backShort.KeybdAudio.HasData)
                    bad2.Add("截短尾部影响已读段");
                fail += Report("⑹b 尾部不足一个段头 → default、不抛", bad2);
            }
            catch (Exception ex)
            {
                fail += ReportException("⑹ 垃圾尾部", ex);
            }
            return fail;
        }

        // ── ⑺ HasAny 语义 ───────────────────────────────────────

        /// <summary>
        /// <c>HasAny</c> 是「要不要写段」与「要不要覆盖全局」的唯一开关，
        /// 必须严格等于三个 Has 的或。特别钉住 Volume 写 0 的情况：
        /// 那是**有覆盖**（音量 0 = 该静音档），不能和「没设」混为一谈。
        /// </summary>
        private static int HasAnySemantics()
        {
            int fail = 0;
            fail += Check("⑺a 全默认 → HasAny=false", !new KeybdPrefs().HasAny, "");
            fail += Check("⑺b 只有音量 → HasAny=true",
                new KeybdPrefs { HasVolume = true, Volume = 7 }.HasAny, "");
            fail += Check("⑺c 只有偏移 → HasAny=true",
                new KeybdPrefs { HasOffset = true, OffsetMs = -5 }.HasAny, "");
            fail += Check("⑺d 静音 → HasAny=true",
                new KeybdPrefs { IsMuted = true }.HasAny, "");
            fail += Check("⑺e 音量=0 仍算有覆盖（静音档，不是没设）",
                new KeybdPrefs { HasVolume = true, Volume = 0 }.HasAny, "");
            return fail;
        }

        // ── ⑻ 数据容器搬运 ──────────────────────────────────────

        /// <summary>
        /// 保存链路是 Replay → ReplayData →(存储)→ ReplayData → Replay，编码器本身
        /// 完全正常也可能整段丢值：只要 From/ToReplayData 里少写一行赋值，文件里就
        /// **永远存不进**覆盖值，而只经过编解码器的往返断言照样全绿。
        /// </summary>
        private static int DataContainerTransfer()
        {
            try
            {
                var src = new Replay { Metadata = new Replay.ReplayMetadata { SongName = "PrefsMove" } };
                src.KeybdPrefs = new KeybdPrefs { HasVolume = true, Volume = 9, IsMuted = true };

                ReplayData data = ReplayDecoder.ToReplayData(src);
                var bad = new List<string>();
                if (!Same(src.KeybdPrefs, data.KeybdPrefs))
                    bad.Add("保存方向丢值：" + Describe(data.KeybdPrefs));

                Replay back = ReplayDecoder.FromReplayData(data);
                if (!Same(src.KeybdPrefs, back.KeybdPrefs))
                    bad.Add("装载方向丢值：" + Describe(back.KeybdPrefs));

                // Reset 之后必须回到「没有覆盖」：否则录制下一份时会带上上一份的设置
                data.Reset();
                if (data.KeybdPrefs.HasAny)
                    bad.Add("Reset() 没清空覆盖值");
                return Report("⑻ 数据容器搬运（To/FromReplayData + Reset 清空）", bad);
            }
            catch (Exception ex)
            {
                return ReportException("⑻ 数据容器搬运", ex);
            }
        }

        // ── 测试夹具与工具 ──────────────────────────────────────

        /// <summary>
        /// 容器层只看 <see cref="KeybdAudioBlob.HasData"/>，不校验负载是不是真能解码
        /// （那是 KeybdAudioCheck 的职责）。这里手搓一小段负载即可，避免为了
        /// 「段位置对不对」再跑一遍重量级的 wav 压缩。
        /// </summary>
        private static KeybdAudioBlob SampleAudio() => new()
        {
            Codec = KeybdAudioCodec.CodecAdpcm,
            SampleRate = 16000,
            Channels = 1,
            SampleCount = 1234,
            Payload = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 },
        };

        private static byte[] PayloadWithAll(out Replay src)
        {
            src = new Replay { Metadata = new Replay.ReplayMetadata { SongName = "PrefsFmt" } };
            src.KeyEvents.Add(new KeyEvent { KeyCode = 80, IsPressed = true, SongPosition = 0.5 });
            src.CursorTrack.Add(new CursorSample { SongPosition = 1.0, X = 0.3f, Y = 0.4f });
            src.KeybdAudio = SampleAudio();
            src.KeybdPrefs = new KeybdPrefs
            {
                HasVolume = true, Volume = 6, HasOffset = true, OffsetMs = -20, IsMuted = true
            };
            return ReplayCompression.Decompress(ReplayEncoder.Encode(src));
        }

        /// <summary>轨迹 + 音频、**没有**覆盖段的布局：给垃圾尾部断言当「已知好前缀」。</summary>
        private static byte[] PayloadWithCursorAndAudio(out Replay src)
        {
            src = new Replay { Metadata = new Replay.ReplayMetadata { SongName = "PrefsJunk" } };
            src.KeyEvents.Add(new KeyEvent { KeyCode = 80, IsPressed = true, SongPosition = 0.5 });
            src.CursorTrack.Add(new CursorSample { SongPosition = 1.0, X = 0.3f, Y = 0.4f });
            src.KeybdAudio = SampleAudio();
            return ReplayCompression.Decompress(ReplayEncoder.Encode(src));
        }

        private static byte[] Append(byte[] head, int count, byte value)
        {
            var merged = new byte[head.Length + count];
            Array.Copy(head, merged, head.Length);
            for (int i = 0; i < count; i++) merged[head.Length + i] = value;
            return merged;
        }

        private static bool Same(KeybdPrefs a, KeybdPrefs b)
            => a.HasVolume == b.HasVolume && a.Volume == b.Volume
               && a.HasOffset == b.HasOffset && a.OffsetMs == b.OffsetMs
               && a.IsMuted == b.IsMuted && a.HasAny == b.HasAny;

        private static string Describe(KeybdPrefs p)
            => "HasVolume=" + p.HasVolume + " Volume=" + p.Volume
               + " HasOffset=" + p.HasOffset + " OffsetMs=" + p.OffsetMs
               + " IsMuted=" + p.IsMuted + " HasAny=" + p.HasAny;

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