// Steam 云的生命周期接线。
//
// 缺陷史（_RE 工程，Unity 编辑器 Play，2026-10-28 实测）：
//   退出 Play 崩掉整个进程，Editor.log 里只有原生栈、没有 Managed Stacktrace：
//     ExitPlayMode → Internal_ApplicationQuit → Main.OnQuitting → FlushConfig
//     → SaveConfigs → OnConfigSaved → UploadConfigNow → SteamCloud.Upload
//     → SteamAPI_ISteamRemoteStorage_FileWrite ← 崩在这（原生访问违例）。
//
//   时序根因：游戏自己的 SteamClient.Shutdown()（scrController.OnApplicationQuit）
//   跑在 Application.quitting 事件**之前**——对象销毁先于 quit 事件链。此时模组
//   在运行中缓存好的 RemoteStorage 接口指针已经悬空，而 SteamCloud.Ensure 的
//   唯一防护是「指针非零」——上下文销毁不改变指针值，判空永远通过。悬空句柄
//   打进 FileWrite 就是进程级崩溃，托管 try/catch 拦不住。
//
//   加深一层的坑：**只清指针挡不住重探测**。Disable 若只置 _iface = Zero，
//   Ensure 的 1 秒重试会在下一次 Ready 查询时从「半销毁」的 steam_api 里拿回
//   非零句柄，失能就地复活。所以失能必须是持久标志，且 Ensure 先看标志再看指针。
//
//   编辑器特有的第三层：Play 会话之间模组域不重载，静态 _iface 跨会话残留——
//   上一个 Play 的句柄指向的上下文早已销毁。装配路径必须有 ResetProbe。
//
// 判据落在接线形状上——只验「方法存在」会被删调用点、留方法骗过。
using System;

namespace UiPreview
{
    internal static class CloudLifecycleCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── Steam 云生命周期接线 ──");
            string main = SrcUtil.Source(projectRoot, "Main.cs");
            string cloud = SrcUtil.Source(projectRoot, "Features/Cloud/SteamCloud.cs");
            int fail = 0;
            void Expect(bool ok, string msg)
            {
                if (ok) return;
                Console.WriteLine("    ✗ " + msg);
                fail++;
            }

            // ① OnQuitting 里失能必须先于落盘。FlushConfig 会触发配置上传
            //    （SaveConfigs → OnConfigSaved），失能晚于它 = 悬空句柄照用。
            int quitAt = main.IndexOf("void OnQuitting(", StringComparison.Ordinal);
            if (quitAt < 0)
            {
                Expect(false, "① 找不到 OnQuitting（退出落盘收口被挪走了？）");
            }
            else
            {
                int toggleAt = main.IndexOf("bool OnToggle(", quitAt, StringComparison.Ordinal);
                string body = toggleAt > quitAt
                    ? main.Substring(quitAt, toggleAt - quitAt)
                    : main.Substring(quitAt);
                int prep = body.IndexOf("PrepareForExit()", StringComparison.Ordinal);
                int flush = body.IndexOf("FlushConfig()", StringComparison.Ordinal);
                Expect(prep >= 0, "① OnQuitting 没调 PrepareForExit —— 退出路径拿着悬空句柄上传，原生崩溃（_RE 实测）");
                Expect(prep >= 0 && flush >= 0 && prep < flush,
                    "① OnQuitting 里失能必须排在 FlushConfig 之前 —— 反过来 = 先崩再失能");
            }

            // ② 装配路径必须重置探测状态。编辑器 Play 会话之间静态字段不重载，
            //    上一个 Play 的悬空 _iface 不清，下一次 Play 直接踩上去。
            int toggle2 = main.IndexOf("bool OnToggle(", StringComparison.Ordinal);
            Expect(toggle2 >= 0 && main.IndexOf("SteamCloud.ResetProbe()", toggle2, StringComparison.Ordinal) >= 0,
                "② OnToggle 装配时没调 SteamCloud.ResetProbe —— 编辑器跨 Play 会话残留悬空 _iface");

            // ③ Disable 必须置持久标志，不能只清指针。
            int disableAt = cloud.IndexOf("internal static void Disable(", StringComparison.Ordinal);
            if (disableAt < 0)
            {
                Expect(false, "③ 找不到 SteamCloud.Disable（失能入口被挪走/改名了？）");
            }
            else
            {
                int resetAt = cloud.IndexOf("internal static void ResetProbe(", disableAt, StringComparison.Ordinal);
                string body = cloud.Substring(disableAt, resetAt > disableAt ? resetAt - disableAt : 400);
                Expect(body.Contains("_disabled = true", StringComparison.Ordinal),
                    "③ Disable 只清了指针没置 _disabled —— Ensure 的 1 秒重试会把句柄重新探回来，失能就地复活");
            }

            // ④ ResetProbe 必须把失能标志也翻回来。翻了但不复位 = 退出过一次之后
            //    云同步在整个编辑器会话里永久哑掉，且界面无任何提示（静默跳过）。
            int reset2 = cloud.IndexOf("internal static void ResetProbe(", StringComparison.Ordinal);
            if (reset2 < 0)
            {
                Expect(false, "④ 找不到 SteamCloud.ResetProbe");
            }
            else
            {
                string body = cloud.Substring(reset2, Math.Min(600, cloud.Length - reset2));
                Expect(body.Contains("_disabled = false", StringComparison.Ordinal),
                    "④ ResetProbe 没把 _disabled 翻回 false —— 退出过一次云同步就永久哑掉（静默跳过）");
            }

            // ⑤ Ensure 必须先看失能标志再探测。判据不是「出现 _disabled」而是
            //    「_disabled 的闸门排在接口访问之前」——顺序反了等于没挡。
            int ensureAt = cloud.IndexOf("private static void Ensure()", StringComparison.Ordinal);
            if (ensureAt < 0)
            {
                Expect(false, "⑤ 找不到 SteamCloud.Ensure");
            }
            else
            {
                int gate = cloud.IndexOf("if (_disabled) return;", ensureAt, StringComparison.Ordinal);
                int probe = cloud.IndexOf("SteamAPI_SteamRemoteStorage_v016()", ensureAt, StringComparison.Ordinal);
                Expect(gate >= 0 && probe >= 0 && gate < probe,
                    "⑤ Ensure 的失能闸门必须排在探测（访问器调用）之前 —— 反了就是拿悬空句柄再探一次");
            }

            if (fail == 0)
                Console.WriteLine("    ✓ 退出先失能再落盘 / 装配重置探测 / 失能是持久标志，三处接线都在");
            return fail;
        }
    }
}
