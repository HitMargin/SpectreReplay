// 离线验证：不启动游戏也能验的那部分。
//
// **这一整套原本是 UI 预览器**——链接真实的 Gfx/PageBuilder/PanelHost，
// 把 Unity 换成桩件，离屏渲染四页设置界面、并逐个控件做点击命中测试。
// 界面改成 UMM 自带的 OnGUI 之后，它整类失效了：
//
//   · GUILayout 是**声明式**的，没有 RectTransform、没有锚点、没有 sizeDelta，
//     桩件模拟不出 IMGUI 的布局与事件派发；
//   · 命中测试要回答"这个点被谁吃掉了"，而 GUILayout 的命中由引擎的
//     控件矩形当场算出来，离屏复刻它等于重写一遍引擎。
//
// 所以预览与几何断言整个删掉，不留半吊子替代品——**一个验不了真东西的
// 检查比没有检查更糟**：它会让人以为界面是对的。
//
// 留下的是验**数据层**的部分，与界面怎么画无关：
//   · ReplayCheck     回放文件格式往返（坏掉的话回放与渲染全废）
//   · FfmpegCheck     ffmpeg 参数拼装，并真跑一遍
//   · LocCheck        文案表完整性（漏键 = 界面上露出原始键名）
//   · HardTextCheck   用户可见的中文有没有被写死
//   · PatchTargetCheck 补丁有没有挂在会被前置 return 挡掉的方法上
//     （回放与录制曾经因为这个而**完全不工作**且毫无异常，日志里看不出来）
//   · LevelLifecycleCheck 进关的那次 rewind 有没有被误判成「玩家重开」
//     （「刚开始玩就显示渲染完成」出现过两轮，根因各不相同）
//   · LevelProbeCheck   「在不在关卡里」的判据选对了没有
//     （这个判据错了三次，每次都把功能整个废掉）
//   · AvSyncCheck       音画同步栅格：丢帧会让两者永久错位
//   · ValidatorCheck     回放前置校验的六条规则，含「取不到就放行」
//   · HitTrackCheck      状态轨（双轨回放）的去重、互锁、绝不透传 HitTick
//     （缺了它判定一旦判失败行星就不前进，表现为「回放卡住」且无日志）
//   · ClockPinCheck      离线渲染钉的是 songposition_minusi 的**属性访问器**，
//     不是 dspTime。改 dspTime 在 :852 之后才生效、下一帧又被 :779 覆盖回去，
//     等于一帧都没钉住——而画面照出，只看「离线」这个名字
//     根本看不出它其实按真实时间在跑
//   · CompatCheck        版本兼容层**在真游戏程序集上**反射，确认选中的
//     重载形状正确（源码级断言验不了"择路选对了"这件事）
//   · FrameSourceCheck    帧来源的请求/回调配对：pending 只在真的排上
//     回读之后才置位。原来的无条件 return true 会让"相机列表为空"那一帧
//     永远等不到回调——画面黑、音频走、日志全无，却什么都不停
//   · PatchGuardCheck     补丁体异常兜底：Guard 只吞自己的异常，
//     「认不出 / 不是我们的」两个上抛出口都必须还在
//   · StateCheck          结算状态判据：从**真程序集**读 States 枚举。
//     原来的 3/5/6 其实是 Checkpoint/Fail/Fail2，结算状态 7 被漏掉了
//   · LifecycleCheck      离开关卡时必须真的拆除回放：四条退出挂点
//     都是 Prefix、不设功能门控（空 Feature 曾被当成查不到的功能名
//     → false → 补丁一个都不挂且不报失败）、四条共用一个入口。
//     Stop() 原先只有界面按钮一个可靠调用点——玩家不点就一直不停，
//     noFail 与判定窗口会带着录制时那档继续生效
//
// 界面本身只能靠游戏里开一次 UMM 窗口看。这是 GUILayout 的代价，
// 换来的是不用再维护 Canvas/EventSystem/ScrollRect/TMP 那一整套。
using System;

namespace UiPreview
{
    internal static class Driver
    {
        internal static int Run(string outDir, string[] args)
        {
            // 数据格式往返：坏掉的话回放和渲染全废，而这种 bug 只在真机
            // 读到文件时才暴露。
            int replayFail = ReplayCheck.Run();

            // ffmpeg 参数：拼错不会报错，只会静默地不出片，所以真跑一遍。
            int ffFail = FfmpegCheck.Run();

            // 文案完整性：漏一个键，界面上就是空白或原始键名。
            int locFail = LocCheck.Run(LocCheck.CollectUsedKeys());

            // 硬编码中文：英文语言的用户会在英文界面里看到夹进来的中文。
            int hardFail = HardTextCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));

            // 挂载点：挂在会被前置 return 挡掉的方法上，回放与录制会
            // 静默失效——日志里没有任何异常，只能靠断言守住。
            int patchFail = PatchTargetCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));

            // 关卡生命周期：进关的重 wind 必须与玩家重开区分开。
            // 这一类 bug 每次根因都不同，只能靠断言钉住判定规则。
            int levelFail = LevelLifecycleCheck.Run();

            // 「在关卡里吗」的判据：选错一次就整局都不录，已经错过三次。
            int probeFail = LevelProbeCheck.Run();

            // 音画同步：丢一帧不是少张图，是音画从此永久错位。
            int avFail = AvSyncCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));

            // 回放前置校验：谱面一改就会在错的位置触发判定，且无任何报错。
            int validFail = ValidatorCheck.Run();

            // 状态轨：只靠键轨的话，判定一旦判失败行星就不前进，
            // 表现为「回放卡住」且无任何日志。
            int hitFail = HitTrackCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));

            // 版本兼容层：真的去反射游戏程序集，确认选中的重载形状正确。
            int compatFail = CompatCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));

            // 帧来源的请求/回调配对：pending 只在真的排上回读之后才置位。
            int frameFail = FrameSourceCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));

            // 补丁体异常兜底：只吞自己的，且两个上抛出口都在。
            int guardFail = PatchGuardCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));

            // 结算状态判据：从真程序集读 States，顺带钉住枚举值。
            int stateFail = StateCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));

            // 命中误差换算：链接真实源码，钉住 π / 60 / 1000 那几处换算。
            int timingFail = TimingCheck.Run();

            // 离线渲染的时间钉扎：钉的是 songposition_minusi 的属性访问器。
            // 原来改 dspTime 一帧都没钉住（:852 先算完、下一帧 :779 覆盖回来），
            // 而画面照出、日志无异常——这类缺陷只能靠断言守。
            int clockFail = ClockPinCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));

            // 退出关卡时的拆除链：Stop() 原本只有界面按钮一个可靠调用点，
            // 玩家不点就永远不停（noFail / 判定窗口 / 按住的键全留着）。
            // 这组断言里有一条真的去反射游戏程序集，确认四条挂点挂得上。
            int lifecycleFail = LifecycleCheck.Run(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"));

            int total = replayFail + ffFail + locFail + hardFail + patchFail
                + levelFail + probeFail + avFail + validFail + hitFail
                + compatFail + frameFail + guardFail + stateFail + timingFail
                + clockFail + lifecycleFail;
            Console.WriteLine();
            Console.WriteLine(total == 0
                ? "  全部通过"
                : $"  {total} 项不通过");
            return total;
        }

        /// <summary>进程入口。outDir 保留是为了不改 verify.ps1 的调用方式。</summary>
        internal static int Main(string[] args)
        {
            return Run(args.Length > 0 ? args[0] : "preview", args);
        }
    }
}