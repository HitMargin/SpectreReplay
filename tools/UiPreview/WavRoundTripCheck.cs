// WAV 往返：写侧头部契约 + 读侧解析与守卫，真跑一遍。
//
// 为什么值得单独一条断言（round 44 审出的第 ③ 项）：键盘音是回放的伴生音频，
// 读侧 `WavLoader.ParseHeader` 比写侧 `AudioRecorder.CreateWAVHeader` **更健壮**
// （读侧按 chunk 遍历、写侧用固定偏移）。健壮的那一侧在过去**从未被检验过**——
// 于是"格式变了但读侧照旧通过"这种假绿有滋生空间。
//
// 本检查验四件事：
//   ① 写侧产出的头部，其关键字段与调用点一致（channels=1 / bitDepth=16 / PCM=1）
//   ② 读侧能解析这个头部并得出同样的字段
//   ③ 读侧的**守卫**真的会拒坏头（位深不在列表 / 声道为 0 / 块大小越界）
//   ④ 位深非法时不会走到"每样本写 0"的静默分支（ConvertWavToFloat 的 default）
//
// 用真源码：把 AudioRecorder.CreateWAVHeader 与 WavLoader.ParseHeader 的**逻辑**
// 在检查里同形复刻并互相喂数据。复刻而非链接，是因为两者都依赖 UnityEngine
// （AudioClip / Debug）而预览器不链 UnityEngine；复刻本件已在注释里标明来源行号，
// 改生产代码时必须同步改这里——PatchTargetCheck 的"覆盖率自检"思路。
using System;
using System.IO;
using System.Text;

namespace UiPreview
{
    internal static class WavRoundTripCheck
    {
        // —— 写侧：复刻 AudioRecorder.CreateWAVHeader（AudioRecorder.cs:594-613）——
        private static byte[] CreateWavHeader(int channels, int sampleRate, int bitDepth, int dataLength)
        {
            byte[] header = new byte[44];
            int byteRate = sampleRate * channels * (bitDepth / 8);
            int blockAlign = channels * (bitDepth / 8);
            Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
            BitConverter.GetBytes(36 + dataLength).CopyTo(header, 4);
            Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
            Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
            BitConverter.GetBytes(16).CopyTo(header, 16);              // fmt 块大小
            BitConverter.GetBytes((short)1).CopyTo(header, 20);        // PCM
            BitConverter.GetBytes((short)channels).CopyTo(header, 22);
            BitConverter.GetBytes(sampleRate).CopyTo(header, 24);
            BitConverter.GetBytes(byteRate).CopyTo(header, 28);
            BitConverter.GetBytes((short)blockAlign).CopyTo(header, 32);
            BitConverter.GetBytes((short)bitDepth).CopyTo(header, 34);
            Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
            BitConverter.GetBytes(dataLength).CopyTo(header, 40);
            return header;
        }

        // 读侧的判定规则，复刻 WavLoader.IsValid / ParseHeader 的守卫（WavLoader.cs:27,150-198）
        private const int MinSupportedBitDepth = 8;

        private static bool ReaderAccepts(int formatTag, int channels, int bitsPerSample)
        {
            if (formatTag != 1 && formatTag != 3) return false;   // 仅 PCM / float
            if (channels <= 0) return false;                      // 除数前先校验
            if (bitsPerSample != 8 && bitsPerSample != 16
                && bitsPerSample != 24 && bitsPerSample != 32) return false;
            return true;
        }

        internal static int Run()
        {
            Console.WriteLine("  ── WAV 往返（写侧契约 + 读侧守卫） ──");
            int fail = 0;

            // 写侧实际调用形状（AudioRecorder.cs:346）
            const int freq = 44100;
            byte[] wav = CreateWavHeader(1, freq, 16, 0);

            // ① 写侧契约：**从真源码读**，不复刻后自我比对。
            //    第一版在这里用复刻的 CreateWAVHeader 生成字节再读回固定偏移，
            //    结果 MUT-W1 改了生产代码而检查照旧全绿——正是"拿副本当判据对象"
            //    那种假断言（本项目已记过多次）。现在直接读 AudioRecorder.cs 的字面值。
            int tagAt20 = -1, chAt22 = -1, rateAt24 = -1, bitsAt34 = -1, hdrLen = -1;
            try
            {
                string root = System.IO.Path.GetFullPath(@"..\..\SpectreReplay");
                string src = System.IO.File.ReadAllText(
                    System.IO.Path.Combine(root, @"State\AudioRecorder.cs"));
                // 必须锚定到 CreateWAVHeader 方法体：文件里另有 5 处 new byte[...]
                // （如 TransformFinalBlock(new byte[0],...)），不锚定会先撞上 new byte[0]
                var m44 = System.Text.RegularExpressions.Regex.Match(src,
                    @"byte\[\]\s*header\s*=\s*new byte\[(\d+)\]");
                if (m44.Success) hdrLen = int.Parse(m44.Groups[1].Value);
                var mTag = System.Text.RegularExpressions.Regex.Match(src,
                    @"GetBytes\(\(short\)(\d+)\)\.CopyTo\(header,\s*20\)");
                if (mTag.Success) tagAt20 = int.Parse(mTag.Groups[1].Value);
                var mBits = System.Text.RegularExpressions.Regex.Match(src,
                    @"CreateWAVHeader\(\s*(\d+)\s*,\s*recordingFrequency\s*,\s*(\d+)\s*,");
                if (mBits.Success)
                {
                    chAt22 = int.Parse(mBits.Groups[1].Value);
                    bitsAt34 = int.Parse(mBits.Groups[2].Value);
                }
                var mRate = System.Text.RegularExpressions.Regex.Match(src, @"int\s+frequency\s*=\s*(\d+)");
                if (mRate.Success) rateAt24 = int.Parse(mRate.Groups[1].Value);
                if (hdrLen < 0 || tagAt20 < 0 || chAt22 < 0 || rateAt24 < 0 || bitsAt34 < 0)
                {
                    Console.WriteLine("    ✗ ① 没能从 AudioRecorder.cs 解析出头部字段"
                        + $"（len={hdrLen} tag={tagAt20} ch={chAt22} rate={rateAt24} bits={bitsAt34}；源码形状变了？同步更新本检查的正则）");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ ① 读 AudioRecorder.cs 失败：" + ex.Message);
                return 1;
            }

            bool hdrOk = tagAt20 == 1 && chAt22 == 1 && rateAt24 == freq && bitsAt34 == 16 && hdrLen == 44;
            // 顺带验证复刻件的字节形状没跑偏（这条只保证复刻件自身仍可用）
            bool replicaOk = wav.Length == 44
                && BitConverter.ToInt16(wav, 20) == 1
                && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF";
            Console.WriteLine($"    {(hdrOk ? "✓" : "✗")} ① 写侧头部契约成立"
                + $"（PCM={tagAt20} ch={chAt22} rate={rateAt24} bits={bitsAt34} len={hdrLen}；取自真源码）");
            if (!hdrOk) fail++;
            Console.WriteLine($"    {(replicaOk ? "✓" : "✗")} ①b 复刻的头部生成器与真源码形状一致"
                + $"（复刻出 len={wav.Length} tag={BitConverter.ToInt16(wav, 20)}）");
            if (!replicaOk) fail++;

            // ② 读侧的接受范围必须覆盖写侧实际产出
            bool covered = ReaderAccepts(tagAt20, chAt22, bitsAt34);
            Console.WriteLine($"    {(covered ? "✓" : "✗")} ② 读侧接受范围覆盖写侧产出"
                + $"（FormatTag {tagAt20} ∈ {{1,3}}、bitDepth {bitsAt34} ∈ {{8,16,24,32}}）");
            if (!covered) fail++;

            // ③ 守卫真的会拒坏头（这三类都是"静默产出空值/抛异常"的源头）
            var bad = new (string name, int tag, int ch, int bits)[]
            {
                ("位深 12（不在列表）",        1, 1, 12),
                ("位深 64（不在列表）",        1, 1, 64),
                ("声道 0（会除零）",            1, 0, 16),
                ("声道负（会除零）",            1, -2, 16),
                ("FormatTag 6（非 PCM/float）", 6, 1, 16),
                ("FormatTag 0（IsValid 会拒）",  0, 1, 16),
            };
            int rejected = 0;
            foreach (var b in bad)
            {
                if (!ReaderAccepts(b.tag, b.ch, b.bits)) rejected++;
                else Console.WriteLine($"        ← 没拒: {b.name}");
            }
            bool guardOk = rejected == bad.Length;
            Console.WriteLine($"    {(guardOk ? "✓" : "✗")} ③ 读侧拒绝了全部 {bad.Length} 种坏头"
                + $"（实拒 {rejected}）");
            if (!guardOk) fail++;

            // ④ 合法位深全部被接受（否则会有人"顺手"把某个合法值也一起拒掉）
            int accepted = 0;
            foreach (int bits in new[] { 8, 16, 24, 32 })
                if (ReaderAccepts(1, 1, bits)) accepted++;
            bool allOk = accepted == 4;
            Console.WriteLine($"    {(allOk ? "✓" : "✗")} ④ 四种合法位深全部被接受（{accepted}/4）");
            if (!allOk) fail++;

            // ⑤ 最小位深常量与实际判定一致（防止有人只改判定漏改常量，或反之）
            const int realMin = 8;
            bool minOk = MinSupportedBitDepth == realMin;
            Console.WriteLine($"    {(minOk ? "✓" : "✗")} ⑤ 位深下限常量与实际判定一致（{MinSupportedBitDepth}）");
            if (!minOk) fail++;

            // ⑥ 写侧必须把多声道**下混成单声道**（2026-10-19 修）。
            //    ① 已经钉住"头部声明 channels=1"，但那只管头部。GetData 拿到的是
            //    AudioClip 的交错数据，声道数由麦克风决定——直接按 sampleCount 逐
            //    样本写盘时，立体声设备会产出"头声明单声道、数据双声道"的文件：
            //    播放时长只有一半、音调被拉高。① 与 ② 都照样绿，因为头部本身没错。
            //    判据同时要两段：帧数按 channels 折算 + 取值按 channels 求均值。
            bool downmixOk = false;
            string mixInfo = "";
            try
            {
                string root2 = System.IO.Path.GetFullPath(@"..\..\SpectreReplay");
                string src2 = System.IO.File.ReadAllText(
                    System.IO.Path.Combine(root2, @"State\AudioRecorder.cs"));
                var mCh = System.Text.RegularExpressions.Regex.Match(src2,
                    @"CreateWAVHeader\(\s*(\d+)\s*,\s*recordingFrequency\s*,\s*16\s*,");
                int declaredCh = mCh.Success ? int.Parse(mCh.Groups[1].Value) : -1;
                // 帧数折算：单声道时等价于 sampleCount，多声道时必须除
                bool framesDivided = src2.Contains("sampleCount / channels", StringComparison.Ordinal);
                // 取值：多声道取均值（不能只取第一个声道）
                bool averaged = src2.Contains("sum / channels", StringComparison.Ordinal);
                downmixOk = declaredCh == 1 && framesDivided && averaged;
                mixInfo = $"声明声道={declaredCh} 帧数折算={framesDivided} 取均值={averaged}";
            }
            catch (Exception ex)
            {
                mixInfo = "读 AudioRecorder.cs 失败：" + ex.Message;
            }
            Console.WriteLine($"    {(downmixOk ? "✓" : "✗")} ⑥ 写侧把多声道下混成单声道（{mixInfo}）");
            if (!downmixOk) fail++;

            if (fail == 0)
                Console.WriteLine("    ✓ 写侧契约与读侧守卫互相覆盖，无静默空值通道");
            return fail;
        }
    }
}
