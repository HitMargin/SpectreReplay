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

            int total = replayFail + ffFail + locFail + hardFail + patchFail
                + levelFail;
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