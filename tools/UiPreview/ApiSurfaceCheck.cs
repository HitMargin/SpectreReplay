// 回放按键查询 API（SpectreAPI 的 KeyViewer 拉取接口）的对外契约。
//
// 缺陷背景（2026-11-12 引入接口当天）：回放按键此前只有「推」的三条桥
// （KeyViewerBridge / CheryToolsKvBridge / QuartzLiveBridge），各自把
// SpectreReplay 补进别的模组的私有形状上——形状一变就整桥失明且难发现。
// 新增的「拉」接口让任意键位显示模组主动查回放按键，但它同样是外部契约：
//   · 软依赖 mod 靠反射探测成员名与 ApiVersion——成员被改名/删除时
//     调用方静默失能，本模组毫无异常；
//   · ManagedInput.Note 里的 Raise 事件线是订阅方唯一的推信号，
//     删一行就断了，而判定链完全不受影响（正是「无异常失效」的形态）；
//   · GetHeldReplayKeys 的「无回放返回空数组不返回 null」契约写在
//     XML 注释里——注释不可执行，只有断言能钉住它。
//
// 所以把契约钉在源码形状上：成员存在、委托目标同源（编码域一致）、
// 事件在状态写入之后触发、never-null。变异 MUT-API1/2/3 证明它们会红。
using System;

namespace UiPreview
{
    internal static class ApiSurfaceCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 回放按键查询 API 契约 ──");
            string api = SrcUtil.Source(projectRoot, "State/SpectreAPI.cs");
            string managed = SrcUtil.Source(projectRoot, "Features/Replay/Runtime/ManagedInput.cs");
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

            // ① 对外成员一个不少。软依赖 mod 靠这些名字反射探测：改名=失能=静默。
            //    委托目标一并钉住——拉取接口必须与补丁桥用同一套换算
            //    （KeyViewerBridge.ToNativeVk），另写一份映射迟早错位。
            int b1 = fail;
            Expect(api.Contains("public static bool IsReplayActive", StringComparison.Ordinal),
                "① SpectreAPI.IsReplayActive 不在了——软依赖 mod 的可用性探测全部失灵");
            Expect(api.Contains("public static bool IsReplayKeyHeld(ushort vk) => ManagedInput.IsHeldVk(vk)", StringComparison.Ordinal),
                "① IsReplayKeyHeld 没有委托 ManagedInput.IsHeldVk——另写一份查询会与判定层键码域错位");
            Expect(api.Contains("public static bool WasReplayKeyPressedThisFrame(ushort vk) => ManagedInput.WasPressedThisFrame(vk)", StringComparison.Ordinal),
                "① WasReplayKeyPressedThisFrame 没有委托 ManagedInput.WasPressedThisFrame——按下沿语义与补丁桥分叉");
            Expect(api.Contains("public static ushort UnityKeyToVk(KeyCode code) => KeyViewerBridge.ToNativeVk(code)", StringComparison.Ordinal),
                "① UnityKeyToVk 没有委托 KeyViewerBridge.ToNativeVk——键码换算与 KeyViewer 桥不同源");
            Item(1, "对外成员齐全且委托同源", b1);

            // ② 事件成员与内部 raiser。事件是订阅方的唯一推信号。
            int b2 = fail;
            Expect(api.Contains("public static event Action<ushort, bool> ReplayKeyChanged;", StringComparison.Ordinal),
                "② ReplayKeyChanged 事件没了——外部键位显示再也无法订阅回放按键");
            Expect(api.Contains("internal static void RaiseReplayKeyChanged(ushort vk, bool pressed) => Raise(ReplayKeyChanged, vk, pressed);", StringComparison.Ordinal),
                "② RaiseReplayKeyChanged 与事件脱钩——事件声明在但永远不触发");
            Item(2, "事件成员与 raiser 同在", b2);

            // ③ 接线：Note 里必须发事件，且必须在状态写入**之后**——订阅方回调里
            //    调 GetHeldReplayKeys/IsReplayKeyHeld 读到的必须是本次事件对应的状态。
            int b3 = fail;
            int noteAt = managed.IndexOf("internal static void Note(ushort vk, bool pressed)", StringComparison.Ordinal);
            Expect(noteAt >= 0, "③ ManagedInput.Note 被挪走/改名——回放按键的记账入口没了");
            if (noteAt >= 0)
            {
                int noteEnd = managed.IndexOf("internal static void Restore(", noteAt, StringComparison.Ordinal);
                string body = noteEnd > noteAt
                    ? managed.Substring(noteAt, noteEnd - noteAt)
                    : managed.Substring(noteAt);
                Expect(body.Contains("API.SpectreAPI.RaiseReplayKeyChanged(vk, pressed);", StringComparison.Ordinal),
                    "③ Note 不再发 ReplayKeyChanged——订阅方从此收不到任何按键信号（MUT-API1 的形状）");
                int heldAt = body.IndexOf("Held[vk] = pressed;", StringComparison.Ordinal);
                int raiseAt = body.IndexOf("RaiseReplayKeyChanged(vk, pressed)", StringComparison.Ordinal);
                Expect(heldAt >= 0 && raiseAt > heldAt,
                    "③ 事件在状态写入之前发出——订阅方读快照会看到上一拍的旧状态");
            }
            Item(3, "Note 接线：发事件且在状态写入之后", b3);

            // ④ never-null 契约：无回放时返回空数组。调用方写 foreach 不判 null——
            //    注释里承诺了空数组，注释不可执行，这里替它执行。
            int b4 = fail;
            Expect(api.Contains("if (!SpectreState.is_playing) return new ushort[0];", StringComparison.Ordinal),
                "④ GetHeldReplayKeys 的无回放分支不再返回空数组——never-null 契约破了（MUT-API2 的形状）");
            Expect(api.Contains("ManagedInput.SnapshotHeld()", StringComparison.Ordinal),
                "④ GetHeldReplayKeys 不走 SnapshotHeld 快照——外借了内部可变结构或自造了另一份遍历");
            Item(4, "GetHeldReplayKeys 永不返回 null", b4);

            // ⑤ 两参 Raise 走同一套主线程封送 + 逐订阅者异常隔离（API 门面的头号契约）。
            int b5 = fail;
            int raise2 = api.IndexOf("private static void Raise<T1, T2>(Action<T1, T2> evt, T1 arg1, T2 arg2)", StringComparison.Ordinal);
            Expect(raise2 >= 0, "⑤ 两参 Raise<T1,T2> 重载没了——ReplayKeyChanged 的触发路径断了");
            if (raise2 >= 0)
            {
                int raise2End = api.IndexOf("private static void LogSubscriberError", raise2, StringComparison.Ordinal);
                string body = raise2End > raise2
                    ? api.Substring(raise2, raise2End - raise2)
                    : api.Substring(raise2);
                Expect(body.Contains("RunOnMain", StringComparison.Ordinal),
                    "⑤ 两参 Raise 没走 RunOnMain——外部回调可能从回放线程直接触发");
                Expect(body.Contains("LogSubscriberError", StringComparison.Ordinal),
                    "⑤ 两参 Raise 没做逐订阅者异常隔离——一个 mod 的回调炸了会打断全部订阅者");
            }
            Item(5, "事件触发走主线程封送 + 异常隔离", b5);

            // ⑥ 版本号钉住：软依赖 mod 用它探测能力面。加成员忘升版本 = 调用方
            //    以为能力不存在。下一位加 API 的人会在这里被拦下来同步升到 0.7。
            int b6 = fail;
            Expect(api.Contains("public const string ApiVersion = \"0.6\";", StringComparison.Ordinal),
                "⑥ ApiVersion 不是 0.6——本组契约对应的能力面变了但版本没跟着动（新增成员 = 次版本号 +1）");
            Item(6, "ApiVersion 已随新成员升到 0.6", b6);

            if (fail == 0)
                Console.WriteLine("    ✓ 回放按键查询 API：成员/委托/接线/never-null/封送/版本 全部钉住");
            return fail;
        }
    }
}
