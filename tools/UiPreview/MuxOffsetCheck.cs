// 音画偏移（mux 侧 -itsoffset）。
//
// 缺陷背景（用户实测成片，2026-11-11）：音频比画面提前约 120ms —— 恰好是玩家的
// 扬声器校准量（inputOffset=120ms）。根因链：实机游玩时游戏内部视觉时间轴比音频
// 时间轴滞后校准量（scrConductor.calibration_i = currentPreset.inputOffset/1000），
// 这道滞后被扬声器的硬件延迟补偿抵消，所以实机看起来是准的；离线渲染没有那道
// 硬件补偿，校准被原样烧进成片 —— 音频比画面提前校准那么多。用户实测 4 个倒计时
// tick（1.284/1.617/1.950/2.284s）与排程公式严丝合缝，音乐起播 2.617s；第一击的
// 视觉锚点落在排程后 ≈150ms 的帧上，与 120ms 校准 + 量化噪声一致。
//
// 修法（方案 A，用户批准）：合成时把整条混音轨平移校准量（-itsoffset），
// 不动钉扎时间轴（方案 B 要把滞后烘进逐帧时钟，风险高被否）。
//
// 三种历史错法都必须钉死：
//   ① 只支持正值 —— 负校准（音频应提前）时对音频输入用负 -itsoffset，起始 PTS
//     被推到负数，多数 muxer 直接拒绝或丢掉首段；负向必须走视频侧正 itsoffset。
//   ② 跟随模式读不到校准 —— 快照搬到工作线程里读游戏状态，或跟随分支读成 0，
//     全新会话的第一次渲染拿到 0 偏移，退回修复前。
//   ③ 显式覆盖断线 —— 设置存了却没接到 mux（ConfigManager 有键、界面无控件、
//     或控件不写字段），玩家改了没反应；settings-ui-audit 的老病。
//   ④ 抢救命令掉链 —— mux 失败后打印的手动合成命令漏掉偏移，照抄合出来的
//     成片又退回「没有实机对齐」。
//
// 判据全部落在**剥注释后的控制流**上：只验「文件里出现过 -itsoffset」会被
// 删调用点、留死代码骗过。
using System;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class MuxOffsetCheck
    {
        private static bool Has(string code, string pattern)
            => Regex.IsMatch(code, pattern);

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 音画偏移（mux itsoffset）──");
            string enc = SrcUtil.Source(projectRoot, "Features/VideoRender/Pipeline/VideoEncoder.cs");
            string job = SrcUtil.Source(projectRoot, "Features/VideoRender/Pipeline/VideoRenderJob.cs");
            string settings = SrcUtil.Source(projectRoot, "State/Settings.cs");
            string cfg = SrcUtil.Source(projectRoot, "State/ConfigManager.cs");
            string pages = SrcUtil.Source(projectRoot, "Pages/Pages.cs");

            int fail = 0;
            void Expect(bool ok, string msg)
            {
                Console.WriteLine($"    {(ok ? "✓" : "✗")} " + msg);
                if (!ok) fail++;
            }

            // ① 偏移是 MuxWith 的**方法参数**，不是构造参数或全局读取：
            //    收尾在工作线程跑（StartFinalizeOnWorker），构造点有两处
            //    （预启动线程 / resize 重建），穿参数才能保证「主线程快照、
            //    线程只读」这条纪律有落点。
            Expect(Has(enc, @"void MuxWith\(string wavPath,\s*double audioOffsetSec\)"),
                "① MuxWith 没有偏移参数 —— 收尾线程只能自己读游戏状态，跨线程纪律破功");

            // ② 正负两条注入分支都存在，且**都由偏移参数驱动**：
            //    正值 → 音频 -i 前插 itsoffset；负值 → 视频 -i 前插 itsoffset
            //    （对音频用负 itsoffset 会把起始 PTS 推到负数，muxer 拒收）。
            //    零值 → 两个前缀都是空串，命令行与无偏移时逐字一致。
            Expect(Has(enc, @"string videoPrefix = offset < 0 \? ""-itsoffset """),
                "②a 负偏移分支缺失 —— 音频应提前时无人把视频延后，负校准被静默丢弃");
            Expect(Has(enc, @"string audioPrefix = offset > 0 \? ""-itsoffset """),
                "②b 正偏移分支缺失 —— 音频应延后时命令行原样，实机滞后不再生效");
            Expect(!Has(enc, @"string args = ""-y -itsoffset"),
                "②c 偏移被硬编码进 args 常量 —— 零偏移不再逐字回到历史命令行");

            // ③ 两个前缀必须真流进 args：只声明不拼接 = 分支形同虚设。
            Expect(Has(enc, @"string args = ""-y "" \+ videoPrefix \+ ""-i """),
                "③a videoPrefix 没有拼进 args —— 负偏移分支白写");
            Expect(Has(enc, @"\+ audioPrefix \+ ""-i """),
                "③b audioPrefix 没有拼进 args —— 正偏移分支白写");

            // ④ itsoffset 注入恰好两处（正分支 + 负分支）。注释已被剥掉，
            //    多出来的第三处说明有人在绕参数注入硬编码；少一处说明分支被删。
            //    （救援命令在 VideoRenderJob 里单独计数，见 ⑥。）
            Expect(enc.Split(new[] { "\"-itsoffset " }, StringSplitOptions.None).Length - 1 == 2,
                "④ VideoEncoder 里 itsoffset 注入点不是恰好 2 处 —— 注入机制被旁路或分支被删");

            // ⑤ 快照形状：-1 跟随游戏校准（scrConductor.calibration_i），≥0 显式毫秒。
            //    这是 MUT-MUX2 钉的那条 —— 跟随分支读成 0 时，全新会话的渲染
            //    拿到 0 偏移，用户校准原样烧进成片，退回修复前。
            Expect(Has(job,
                    @"muxAudioOffsetSec = Settings\.RenderAudioOffsetMs < 0\s*\?\s*\(double\)scrConductor\.calibration_i\s*:\s*Settings\.RenderAudioOffsetMs / 1000\.0;"),
                "⑤ 偏移快照没有「-1 跟随校准」语义 —— 跟随分支断了，校准被静默丢成 0");
            Expect(job.Contains("private double muxAudioOffsetSec;"),
                "⑤b 偏移快照字段没了 —— 快照被搬回工作线程或就地读取");

            // ⑤c 调用点把快照带进 MuxWith，且本地快照来自字段（主线程先取、线程里用）。
            Expect(Has(job, @"double audioOffset = muxAudioOffsetSec;"),
                "⑤c 收尾没有先在主线程落一份偏移局部快照 —— 线程里直接读字段");
            Expect(job.Contains("enc.MuxWith(wav, audioOffset);"),
                "⑤c MuxWith 调用点没传偏移 —— 编译能过但偏移永远不生效");
            Expect(!job.Contains("MuxWith(wav);"),
                "⑤c 还留着单参调用 MuxWith(wav) —— 改签名漏改调用点");

            // ⑥ 抢救命令与 MuxWith 同参：手动合成是 mux 失败后的唯一退路，
            //    漏掉偏移就退回「没有实机对齐」。
            Expect(Has(job, @"string videoInput = offset < 0\s*\?\s*""-itsoffset """),
                "⑥a 抢救命令的负偏移分支缺失 —— 手动合成退回无对齐");
            Expect(Has(job, @"string audioInput = offset > 0\s*\?\s*""-itsoffset """),
                "⑥b 抢救命令的正偏移分支缺失 —— 手动合成退回无对齐");
            Expect(job.Split(new[] { "\"-itsoffset " }, StringSplitOptions.None).Length - 1 == 2,
                "⑥c VideoRenderJob 里 itsoffset 出现次数不是恰好 2 处 —— 抢救命令与 MuxWith 不同参");

            // ⑦ 设置项四件套：字段、落盘、读取、界面。
            Expect(settings.Contains("public static int RenderAudioOffsetMs = -1;"),
                "⑦a Settings 字段没了 —— 设置项整体退役但消费点还在");
            Expect(cfg.Contains("render_audio_offset_ms = Settings.RenderAudioOffsetMs,"),
                "⑦b ConfigManager 没落盘 —— 玩家改的偏移下次启动就丢");
            Expect(Has(cfg, @"Mathf\.Clamp\(ints\.Value<int\?>\(""render_audio_offset_ms""\) \?\? Settings\.RenderAudioOffsetMs, -1, 500\)"),
                "⑦c ConfigManager 没读取或没夹合法域 —— 手改配置的越界值悬空");
            Expect(Has(pages, @"OnGui\.Row\(""render\.audio\.offset\.follow"", RenderAudioOffsetMs < 0,"),
                "⑦d 界面没有「跟随校准」开关 —— 显式/跟随只能靠手改配置");
            Expect(Has(pages, @"OnGui\.RowSlider\(""render\.audio\.offset"", Mathf\.Max\(0, RenderAudioOffsetMs\)"),
                "⑦e 界面没有毫秒滑条 —— 玩家改不了显式偏移（audit 的「无界面但被消费」老病）");

            return fail;
        }
    }
}
