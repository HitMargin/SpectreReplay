// 键盘音起始时间戳：不能因为起录帧拿不到导体就永久缺着。
//
// 缺陷形态（2026-10-06 修）：Patch_Playback 起录键盘音时，若那一帧
// ADOBase.conductor 为空（切场景/关卡加载中的常态），原实现直接 `return`。
// 后果不是"少一个字段"：
//   ① 同时跳过本帧的 Record_kbdstate（那一帧按键没采样）；
//   ② KeybdSoundStartTick 永久缺着 —— 回放侧判到没有它就静默 return，
//      **整段键盘音消失且没有任何提示**（HasKeybdSound 仍为 true，
//      没有别处会报），玩家只觉得"键盘声没了"。
// 修法：起录时记 0.0 占位 + 每帧试着补真值；回放侧对 <=0 的基准直接不播
//      （拿 0 当基准会把 wav 从头对齐到歌曲中段，比不播更糟）。
//
// 判据落在**控制流形状**上：起录块内不许出现导体判空后的 return，
// 且必须存在"把缺 tick 补上"的那一段。
using System;
using System.IO;
using System.Linq;

namespace UiPreview
{
    internal static class KeybdSoundTickCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 键盘音起始时间戳 ──");
            string ok = Path.Combine(projectRoot, "Features", "Replay", "Patch_Playback.cs");
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

            // ② 必须存在"缺 tick 就补真值"的分支。
            //    判据要盯**比较式本身**，不能只查标识符出现过：
            //    pendingTick 这个名字在 TryGetValue 的 out 声明里也有，
            //    于是把比较式改成恒假（MUT-A2）后闸门照样全绿——假断言。
            if (!code.Contains("pendingTick == 0.0", StringComparison.Ordinal))
            {
                Console.WriteLine("    ✗ ② 没有可到达的补起始时间戳分支"
                    + "（比较式 pendingTick == 0.0 不在，起录帧拿不到导体时 tick 会永久停在 0）");
                fail++;
            }

            // ③ 回放侧必须拒掉 <=0 的基准（拿 0 当基准会把 wav 对齐错一大段）
            if (!code.Contains("keybdStartTick <= 0.0", StringComparison.Ordinal))
            {
                Console.WriteLine("    ✗ ③ 回放侧没有拒掉非正起始时间戳（0 基准会把键盘声对齐错位）");
                fail++;
            }

            if (fail == 0)
                Console.WriteLine("    ✓ 起始时间戳三条判据都成立");
            return fail;
        }
    }
}
