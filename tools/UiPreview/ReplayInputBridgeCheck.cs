// ReplayInput 外部驱动契约推桥的接线。
//
// 缺陷背景：这类键位显示模组（RainingKeys 0.5.0 反编译实证，为跨模组通用
// 约定）不轮询任何外部状态——受控模式下只认显式的
// ReplayInput.OnKeyPressed/OnKeyReleased(KeyCode) 调用（Key.Update 见
// controlled=true 直接 return）。桥断在游戏里毫无异常：KV 全程不亮、或回放
// 结束后卡在受控模式（物理轮询已停、画面永远停在最后一次按键）。
//
// 发现方式是**按类型名扫描全部程序集**（契约只约定类型叫 ReplayInput，
// 不约定程序集/命名空间），找到几个驱动几个。判据特别钉住「不许绑死
// 某一家程序集名」——那是同类 mod 多、接口通用的前提下最易犯的回归。
// 变异 MUT-RI1/RI2 证明接线判据会红。
using System;

namespace UiPreview
{
    internal static class ReplayInputBridgeCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── ReplayInput 推桥接线 ──");
            string player = SrcUtil.Source(projectRoot, "Features/Replay/Runtime/ReplayPlayer.cs");
            string main = SrcUtil.Source(projectRoot, "Main.cs");
            string bridge = SrcUtil.Source(projectRoot, "Features/Replay/Runtime/ReplayInputBridge.cs");
            int fail = 0;
            void Expect(bool ok, string msg)
            {
                if (ok) return;
                Console.WriteLine("    ✗ " + msg);
                fail++;
            }
            void Item(int num, string desc, int before)
            {
                if (fail == before)
                    Console.WriteLine("    ✓ " + num.ToString() + " " + desc);
            }

            // ① 回放开始必须发 OnStartInputs 进受控模式。漏了它，这类 KV 仍在
            //    自己轮询物理键，回放按键一颗都不亮（它们看不到托管层）。
            int b1 = fail;
            Expect(player.Contains("ReplayInputBridge.TryInstall();", StringComparison.Ordinal),
                "① ReplayPlayer.Start 不再调 ReplayInputBridge.TryInstall——回放开始时键位显示不进受控模式");
            Item(1, "Start 挂了 TryInstall", b1);

            // ② 回放结束必须发 OnEndInputs 还原。漏了它，KV 卡在受控模式：
            //    物理轮询已停（Key.Update 见 controlled 直接 return），
            //    画面永远停在最后一次按键，只有重开该 KV 能救。
            int b2 = fail;
            Expect(player.Contains("ReplayInputBridge.Shutdown();", StringComparison.Ordinal),
                "② ReplayPlayer.Stop 不再调 ReplayInputBridge.Shutdown——回放结束后键位显示卡死在受控模式（MUT-RI2 的形状）");
            Item(2, "Stop 挂了 Shutdown", b2);

            // ③ 边沿必须从 PumpInput 喂（与 Quartz 实时桥同点、同一过滤块内：
            //    0/热键/Esc 已在块外被排掉），漏了就是回放全程不亮。
            int b3 = fail;
            int qEdgeAt = player.IndexOf("QuartzLiveBridge.Edge(num, ev.IsPressed);", StringComparison.Ordinal);
            int riEdgeAt = player.IndexOf("ReplayInputBridge.Edge(num, ev.IsPressed);", StringComparison.Ordinal);
            Expect(riEdgeAt >= 0,
                "③ PumpInput 不再调 ReplayInputBridge.Edge——回放期间键位显示一颗都不亮（MUT-RI1 的形状）");
            Expect(qEdgeAt < 0 || riEdgeAt > qEdgeAt,
                "③ ReplayInput 的边沿喂点跑到了 Quartz 实时桥之前——各桥的喂点顺序被打乱");
            Item(3, "PumpInput 边沿喂点在位", b3);

            // ④ 跳转/检查点开局必须重播种。这份契约没有状态播种接口，桥的
            //    Reseed 用「OnStartInputs 重建 + 净按住态补 Down」实现；漏了它，
            //    跳转后画面残留上一段的按住高亮（跳过段的松键没人发）。
            int b4 = fail;
            Expect(player.Contains("ReplayInputBridge.Reseed();", StringComparison.Ordinal),
                "④ RebuildHeldInputs 不再调 ReplayInputBridge.Reseed——跳转/检查点开局后残留上一段的按住高亮");
            Item(4, "Rebuild 挂了 Reseed", b4);

            // ⑤ 模组重装配必须复位会话状态（与 KeyViewerBridge/CheryToolsKvBridge
            //    同一纪律），否则关开一次模组后桥带着旧 MethodInfo 与旧会话状态续命。
            int b5 = fail;
            Expect(main.Contains("Features.Replay.ReplayInputBridge.Reset();", StringComparison.Ordinal),
                "⑤ Main.OnToggle 不再复位 ReplayInputBridge——关开一次模组后桥带着旧会话状态续命");
            Item(5, "Main 复位在位", b5);

            // ⑥ 桥本体：按类型名扫描全部程序集（不许绑死某一家——同类 mod 多、
            //    接口通用，绑死等于给其它 KV 判死刑）；四个方法都要解析；
            //    VK→KeyCode 必须走 SkyHook 逆链（与 KeyViewerBridge.ToNativeVk
            //    互逆），并保留 VK_APPS=93 ↔ KeyCode.Menu 互逆特判。
            int b6 = fail;
            Expect(bridge.Contains("AppDomain.CurrentDomain.GetAssemblies()", StringComparison.Ordinal),
                "⑥ 桥不再扫描已加载程序集——按单一程序集名发现等于只支持一家 KV");
            Expect(bridge.Contains("\"ReplayInput\"", StringComparison.Ordinal),
                "⑥ 桥不再按类型名 \"ReplayInput\" 匹配——契约的类型名锚点丢了");
            Expect(bridge.IndexOf("RainingKeys.Bootstrap", StringComparison.Ordinal) < 0,
                "⑥ 桥里绑死了 RainingKeys.Bootstrap——契约是跨模组的，按程序集名特判某一家是回归（用户明确要求通用扫描）");
            Expect(bridge.Contains("\"OnStartInputs\"", StringComparison.Ordinal)
                && bridge.Contains("\"OnEndInputs\"", StringComparison.Ordinal)
                && bridge.Contains("\"OnKeyPressed\"", StringComparison.Ordinal)
                && bridge.Contains("\"OnKeyReleased\"", StringComparison.Ordinal),
                "⑥ 桥没有解析全部四个驱动方法——Start/End/按下/抬起任一缺失都是半截桥");
            Expect(bridge.Contains("NativeKeyCodeToKeyLabel", StringComparison.Ordinal)
                && bridge.Contains("SkyHookKeyToUnityKey", StringComparison.Ordinal),
                "⑥ VK→KeyCode 不走 SkyHook 逆链——另写映射表会与 KeyViewerBridge.ToNativeVk 的正向表漂移");
            Expect(bridge.Contains("if (vk == 93) return KeyCode.Menu;", StringComparison.Ordinal),
                "⑥ 丢了 VK_APPS=93 → KeyCode.Menu 特判——正向表对 Menu 特判 93，反向丢了它 Menu 键永远映射失败");
            Item(6, "桥本体：通用扫描/四方法/逆链/Menu 特判/不绑死一家", b6);

            if (fail == 0)
                Console.WriteLine("    ✓ ReplayInput 推桥：五处接线/通用扫描/逆链/特判 全部钉住");
            return fail;
        }
    }
}
