// 键盘音起始时间戳：不能因为起录帧拿不到导体就永久缺着。
//
// 缺陷形态（2026-10-06 修）：Patch_Playback 起录键盘音时，若那一帧
// ADOBase.conductor 为空（切场景/关卡加载中的常态），原实现直接 `return`。
// 后果不是"少一个字段"：
//   ① 同时跳过本帧的 Record_kbdstate（那一帧按键没采样）；
//   ② KeybdSoundStartTick 永久缺着 —— 回放侧判到没有它就静默 return，
//      **整段键盘音消失且没有任何提示**（HasKeybdSound 仍为 true，
//      没有别处会报），玩家只觉得"键盘声没了"。
// 修法：起录时记 NaN 占位 + 每帧试着补真值；回放侧只拒 NaN 基准（缺字段等价
//      于 NaN）。**不能拿 0 当占位**：0 是合法基准（录制恰从歌曲 0 秒起），
//      与"没记到"撞一起就再也分不开——那样的回放会被整段静音，而导出端照样有声。
//
// 缺陷形态（2026-10-27 修）：回放侧曾写成 `keybdStartTick <= 0.0` 一律不播。
// 负数是**合法**基准——起录帧常落在倒计时里，此时 songposition_minusi 为负，
// 录制侧原样记下；按 num = songpos - tick 对齐，恰在录制那一刻从 wav 头起播。
// 旧的 <=0 把这类回放**整段静音**（导出端走 AudioListener 实时混音仍有声，
// 于是表现成"回放没声、导出却有"）。
//
// 语义定稿（MetadataVersion 3）：基准是 tri-state，**只有 NaN 是"没有基准"**。
//   null/NaN  = 缺字段或录制侧始终没拿到导体 → 整段不播；
//   负数/0/正数 = 真实基准，**一律照放**。
// 0 之所以不能当哨兵：录制恰从歌曲 0 秒起时它也是真值，与"没记到"撞车后
// 分不开，只能整段静音。旧文件的 0 由解码侧按版本迁成 NaN（ReplayDecoder）。
//
// 判据落在**控制流形状 + 比较式本身**上：起录块内不许出现导体判空后的 return；
// 必须存在"把 NaN 占位补成真值"的那一段；回放侧只许拒 NaN。
// 每条都配反向断言（禁止旧写法回潮）——只写正向的话，`!double.IsNaN(...)`
// 这种"取反了就恒假"的改法仍然包含同样的子串，闸门会假绿。
using System;
using System.IO;
using System.Linq;
using System.Text;
using SpectreReplay.Features.Replay;

namespace UiPreview
{
    internal static class KeybdSoundTickCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 键盘音起始时间戳 ──");
            string ok = Path.Combine(projectRoot, "Features", "Replay", "Patches", "Patch_Playback.cs");
            if (!File.Exists(ok))
            {
                Console.WriteLine("    ✗ 找不到 Patch_Playback.cs");
                return 1;
            }
            // 剥注释：判据禁止的 return 若写在解释"为什么不许"的注释里会常红
            string code = SrcUtil.Clean(File.ReadAllText(ok));

            int fail = 0;

            // ① 起录块里不许有"导体判空 → return"。
            //    取 StartRecording 之后到本块结束之间的代码段来判。
            int startAt = code.IndexOf("StartRecording(", StringComparison.Ordinal);
            if (startAt < 0)
            {
                Console.WriteLine("    ✗ ① 找不到 StartRecording 调用（补丁形状变了？）");
                fail++;
            }
            else
            {
                int blockEnd = code.IndexOf("Record_kbdstate(", startAt, StringComparison.Ordinal);
                if (blockEnd < 0) blockEnd = code.Length;
                string block = code.Substring(startAt, blockEnd - startAt);
                if (block.Contains("return", StringComparison.Ordinal))
                {
                    Console.WriteLine("    ✗ ① 起录块内在导体判空后 return —— 会同时丢掉本帧按键采样"
                        + "并让 KeybdSoundStartTick 永久缺失（键盘音整段消失且无提示）");
                    fail++;
                }
            }

            // ② 必须存在"缺 tick 就补真值"的分支，且占位必须是 NaN。
            //    判据要盯**比较式本身**，不能只查标识符出现过：pendingTick 这个名字
            //    在模式匹配的声明里也有，于是把比较式改成恒假（MUT-A2）后闸门照样全绿。
            if (!code.Contains("double.IsNaN(pendingTick)", StringComparison.Ordinal)
                || code.Contains("!double.IsNaN(pendingTick)", StringComparison.Ordinal))
            {
                Console.WriteLine("    ✗ ② 没有可到达的补起始时间戳分支"
                    + "（比较式 double.IsNaN(pendingTick) 不在或被取反，起录帧拿不到导体时 tick 会永久停在占位值）");
                fail++;
            }
            //    占位必须是 NaN 而不是 0：0 是合法基准，拿它当"待补"会把真实 0 基准
            //    的回放判成缺字段（正是本轮要修的那类整段静音）。
            if (code.Contains("data.KeybdSoundStartTick = 0.0", StringComparison.Ordinal))
            {
                Console.WriteLine("    ✗ ② 起录占位又写回了 0.0 —— 0 是合法基准，与\"没记到\"无法区分，"
                    + "真实 0 基准的回放会被整段静音（而导出端仍有声）");
                fail++;
            }

            // ③ 回放侧只拒 NaN，**0 与负数都照放**。
            //    正向断言比较式在，反向断言各种"退回去"的写法都不在：
            //    取反（恒假）、拒 0（真实 0 基准被静音）、拒非正（负数被静音）。
            if (!code.Contains("double.IsNaN(keybdStartTick)", StringComparison.Ordinal)
                || code.Contains("!double.IsNaN(keybdStartTick)", StringComparison.Ordinal))
            {
                Console.WriteLine("    ✗ ③ 回放侧没有按 NaN 判\"没有基准\""
                    + "（比较式 double.IsNaN(keybdStartTick) 不在或被取反：缺基准的回放会被"
                    + "拿 0/NaN 去对齐，键盘声整体挪到歌曲中段，比不播更糟）");
                fail++;
            }
            if (code.Contains("keybdStartTick == 0.0", StringComparison.Ordinal))
            {
                Console.WriteLine("    ✗ ③ 回放侧又把 0 当\"没有基准\" —— 录制恰从歌曲 0 秒起的回放"
                    + "会被整段静音，而导出端仍有声；0 现在是合法基准");
                fail++;
            }
            if (code.Contains("keybdStartTick <= 0.0", StringComparison.Ordinal)
                || code.Contains("keybdStartTick < 0.0", StringComparison.Ordinal))
            {
                Console.WriteLine("    ✗ ③ 回放侧把负基准也当无效（<= 0.0 / < 0.0）——"
                    + "倒计时起录的回放会整段静音，而导出端仍有声；负数应照放");
                fail++;
            }

            if (fail == 0)
                Console.WriteLine("    ✓ 起始时间戳三条形状判据都成立");

            // ④ 形状判据证明不了**语义**：0.0 会不会被解码侧迁成 NaN、null 会不会
            //    落成 0、旧 v2 文件里的 0 哨兵会不会被当成合法基准 —— 这三件只有
            //    真的编一遍再解一遍才知道。旧版正是栽在"两边各自看着都对"：
            //    录制侧写 0 占位、解码侧把缺失也还原成 0，合起来就是真实 0 基准
            //    （和缺基准）的回放全被整段静音，而导出端照样有声。
            fail += TickRoundTrip();

            if (fail == 0)
                Console.WriteLine("    ✓ 起始时间戳（形状 + 编解码往返）全部通过");
            return fail;
        }

        // ── ④ 真实编解码往返 ─────────────────────────────────────

        /// <summary>
        /// tri-state 语义的端到端往返：录制侧写什么 → 文件里落什么 → 播放侧读到什么。
        ///
        /// <para>走的是**生产路径**（<c>FromReplayData</c> → <c>Encode</c> → <c>Decode</c>
        /// → <c>ToReplayData</c>），不是另写一份等价逻辑——重写一遍就只是在测重写的那份。</para>
        /// </summary>
        private static int TickRoundTrip()
        {
            int fail = 0;
            try
            {
                // 真实 0.0 基准：录制恰好从歌曲 0 秒起。必须原样读回 0.0——
                // 这一条就是本轮修复的核心（旧版把它当"没有基准"整段静音）。
                fail += CheckTick("⑷ 真实 0.0 基准往返", 0.0,
                    v => v.HasValue && v.Value == 0.0,
                    v => Describe(v));
                // 负基准：倒计时起录的常态（实测 -0.57），必须原样读回。
                fail += CheckTick("⑸ 负基准往返", -0.57,
                    v => v.HasValue && Math.Abs(v.Value + 0.57) < 1e-9,
                    v => Describe(v));
                // 无基准：null 进去 null 出来。
                fail += CheckTick("⑹ 无基准往返", null,
                    v => !v.HasValue,
                    v => Describe(v));

                // 无基准在**文件里**必须是 NaN，不能是 0：若它落成 0，上一条能过只是
                // 因为解码侧又把它当哨兵，而真正的 0.0 基准就会被连坐（⑷ 会红）。
                // 这里直接读原始字节，把"两边凑巧对上"这条退路堵死。
                byte[] plain = EncodePlaintext(null);
                double raw = ReadRawTick(plain);
                if (!double.IsNaN(raw))
                {
                    Console.WriteLine("    ✗ ⑺ 无基准落盘值 —— 文件里写的是 " + raw.ToString("R")
                        + "，必须是 NaN（写 0 会与真实 0.0 基准撞车）");
                    fail++;
                }
                else
                {
                    Console.WriteLine("    ✓ ⑺ 无基准落盘为 NaN（不是 0）");
                }

                // 旧 v2 文件：0 是"没记到"的哨兵（旧录制侧补不上导体就写 0）。
                // 解码必须把它迁成"无基准"，否则旧回放会被对齐到歌曲开头——
                // 正是"回放没声/乱响而导出有声"的另一半来源。
                byte[] legacyPlain = EncodePlaintext(0.0);
                PatchMetadataVersion(legacyPlain, 2);
                double? legacy = ReplayDecoder.ToReplayData(
                    ReplayDecoder.Decode(ReplayCompression.Compress(legacyPlain))).KeybdSoundStartTick;
                if (legacy.HasValue)
                {
                    Console.WriteLine("    ✗ ⑻ 旧 v2 文件迁移 —— 0 哨兵读回成 " + Describe(legacy)
                        + "，应迁成\"无基准\"（旧文件里的 0 从来不是真基准）");
                    fail++;
                }
                else
                {
                    Console.WriteLine("    ✓ ⑻ 旧 v2 文件的 0 哨兵迁成无基准");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ ④ 起始基准往返：抛异常 " + ex.GetType().Name + ": " + ex.Message);
                fail++;
            }
            return fail;
        }

        private static int CheckTick(string name, double? src, Func<double?, bool> ok, Func<double?, string> show)
        {
            double? got = DecodeTick(src);
            if (ok(got))
            {
                Console.WriteLine("    ✓ " + name);
                return 0;
            }
            Console.WriteLine("    ✗ " + name + " —— 写 " + Describe(src) + "，读回 " + show(got));
            return 1;
        }

        private static string Describe(double? v)
            => !v.HasValue ? "null" : double.IsNaN(v.Value) ? "NaN" : v.Value.ToString("R");

        /// <summary>录制侧写入 → 存盘 → 播放侧读回，走生产路径。</summary>
        private static double? DecodeTick(double? src)
        {
            var data = new ReplayData
            {
                SongName = "TickRT",
                SceneName = "scnGame",
                JudgeMode = "Normal",
                KeybdSoundStartTick = src,
            };
            byte[] bytes = ReplayEncoder.Encode(ReplayDecoder.FromReplayData(data));
            return ReplayDecoder.ToReplayData(ReplayDecoder.Decode(bytes)).KeybdSoundStartTick;
        }

        /// <summary>拿到未压缩的明文负载（与真实文件同一份布局）。</summary>
        private static byte[] EncodePlaintext(double? src)
        {
            var data = new ReplayData
            {
                SongName = "TickRT",
                SceneName = "scnGame",
                JudgeMode = "Normal",
                KeybdSoundStartTick = src,
            };
            return ReplayCompression.Decompress(ReplayEncoder.Encode(ReplayDecoder.FromReplayData(data)));
        }

        // 文件头：魔数 8 + FormatVersion 4 + metadata 魔数 8 ⇒ 段版本在偏移 20
        private const int MetadataVersionOffset = 8 + 4 + 8;

        private static void PatchMetadataVersion(byte[] plain, int version)
        {
            plain[MetadataVersionOffset] = (byte)version;
            plain[MetadataVersionOffset + 1] = 0;
            plain[MetadataVersionOffset + 2] = 0;
            plain[MetadataVersionOffset + 3] = 0;
        }

        /// <summary>
        /// 按真实布局走到 KeybdSoundStartTick 的偏移。
        ///
        /// <para>不能写死偏移：前面的字符串是变长的（7 位长度前缀 + UTF8）。
        /// 这里的字段个数必须与 <c>ReplayEncoder.WriteMetadata</c> 逐字一致——
        /// 对不上会读到错的偏移，进而"验的东西不是它"。个数不符时直接抛，
        /// 比悄悄错位好。</para>
        /// </summary>
        private static double ReadRawTick(byte[] plain)
        {
            using var ms = new MemoryStream(plain);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            r.ReadUInt64();            // 文件魔数
            r.ReadInt32();             // FormatVersion
            r.ReadUInt64();            // metadata 魔数
            r.ReadInt32();             // metadata 段版本
            r.ReadInt32();             // StartingFloorId
            r.ReadInt32();             // TotalFloorCount
            for (int i = 0; i < 14; i++) r.ReadString();   // 14 个字符串字段
            for (int i = 0; i < 7; i++) r.ReadInt32();     // StartTile…AudioBufferSize
            for (int i = 0; i < 3; i++) r.ReadDouble();    // Bpm / SpeedTrail / PlaybackSpeed
            return r.ReadDouble();                         // KeybdSoundStartTick
        }
    }
}
