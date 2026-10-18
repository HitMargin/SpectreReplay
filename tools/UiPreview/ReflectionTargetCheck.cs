// 运行期反射目标核对：代码里用 PatchManager.GetMethodInfo/GetPropertyGetter/
// GetFieldInfo **按名字**取的游戏成员，必须真的存在于对应程序集。
//
// 为什么单独一条：这些调用不走 [SpectrePatch] 的探针通道，探针不会报它们。
// 取不到时 GetMethodInfo 抛 MissingMethodException——抛在**热点路径**上
// （TriggerMessage 每弹一次提示就跑一次、UnscaledDeltaPin 每次渲染安装跑一次），
// 表现为"提示不弹"或"钉时间轴没装上"，而根因只有一行异常。
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace UiPreview
{
    internal static class ReflectionTargetCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 运行期反射目标 ──");
            string game = Find(SrcUtil.GameAssembly("Assembly-CSharp.dll"));
            string core = Find(SrcUtil.GameAssembly("UnityEngine.CoreModule.dll"));
            if (game == null || core == null)
            {
                Console.WriteLine("    · 找不到游戏/引擎程序集，跳过");
                return 0;
            }

            Assembly asmGame = Assembly.LoadFrom(game);
            Assembly asmCore = Assembly.LoadFrom(core);
            int fail = 0;

            // ① Notification.SetupNotification 的形参形状必须与**生产代码里的调用**一致。
            //    判据从源码里读出来，不能在本文件里写死一份：
            //    写死的话改生产代码的签名（MUT-R1）[typeof(float),typeof(float),typeof(bool),typeof(int)]
            //    这道判据照样全绿——又是"拿副本当判据对象"。
            Type notif = asmGame.GetType("Notification");
            if (notif == null)
            {
                Console.WriteLine("    ✗ Notification 类型不存在（TriggerMessage 会抛）");
                fail++;
            }
            else
            {
                var want = CallSiteTypes(projectRoot, "SetupNotification");
                if (want == null || want.Length == 0)
                {
                    Console.WriteLine("    · SpectreState.cs 里找不到 SetupNotification 调用点，跳过形参核对");
                }
                else
                {
                    var m = notif.GetMethod("SetupNotification",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, want, null);
                    if (m == null)
                    {
                        Console.WriteLine("    ✗ Notification.SetupNotification("
                            + string.Join(",", want.Select(t => t.Name)) + ") 不存在"
                            + "（TriggerMessage 每次弹提示都会抛 MissingMethodException）");
                        fail++;
                    }
                    else
                    {
                        Console.WriteLine("    ✓ Notification.SetupNotification 形参 "
                            + string.Join("/", m.GetParameters().Select(p => p.ParameterType.Name)));
                    }
                }
            }

            // ② UnscaledDeltaPin 钉的五个 Time 属性 getter（UnityEngine.CoreModule）
            string[] timeProps =
            {
                "unscaledDeltaTime", "unscaledTime", "unscaledTimeAsDouble",
                "realtimeSinceStartup", "realtimeSinceStartupAsDouble",
            };
            Type timeT = asmCore.GetType("UnityEngine.Time");
            if (timeT == null)
            {
                Console.WriteLine("    ✗ UnityEngine.Time 不存在");
                fail++;
            }
            else
            {
                int miss = 0;
                foreach (string p in timeProps)
                {
                    var pi = timeT.GetProperty(p,
                        BindingFlags.Public | BindingFlags.Static);
                    if (pi == null || pi.GetGetMethod() == null) miss++;
                }
                if (miss > 0)
                {
                    Console.WriteLine($"    ✗ UnityEngine.Time 少了 {miss} 个属性 getter"
                        + "（UnscaledDeltaPin 的 critical 项钉不上会静默降级）");
                    fail++;
                }
                else
                {
                    Console.WriteLine($"    ✓ UnityEngine.Time {timeProps.Length} 个属性 getter 都在");
                }
            }

            // ③ AudioSettings.dspTime / outputSampleRate / speakerMode（VideoAudioTrack 用）
            Type audio = asmCore.GetType("UnityEngine.AudioSettings");
            if (audio == null)
            {
                // AudioSettings 在 UnityEngine.AudioModule，不在 CoreModule。
                // 路径同样走 SrcUtil：写死绝对路径会让这条判据在 CI 里静默降级成
                // 「找不到 AudioSettings，跳过」——而混音轨起录抛异常只在游戏里才看得见。
                string audioDll = Find(SrcUtil.GameAssembly("UnityEngine.AudioModule.dll"));
                audio = audioDll == null ? null : Assembly.LoadFrom(audioDll).GetType("UnityEngine.AudioSettings");
            }
            if (audio == null)
            {
                Console.WriteLine("    · 找不到 AudioSettings，跳过");
            }
            else
            {
                string[] want = { "dspTime", "outputSampleRate", "speakerMode" };
                int miss = want.Count(p => audio.GetProperty(p, BindingFlags.Public | BindingFlags.Static) == null);
                if (miss > 0)
                {
                    Console.WriteLine($"    ✗ AudioSettings 少了 {miss} 个属性（混音轨起录会抛）");
                    fail++;
                }
                else
                {
                    Console.WriteLine($"    ✓ AudioSettings {want.Length} 个属性都在");
                }
            }

            if (fail == 0)
                Console.WriteLine("    ✓ 运行期反射目标全部命中");
            return fail;
        }

        /// <summary>
        /// 从 SpectreState.cs 的 SetupNotification 调用点里读出实参类型数组。
        /// 形如：GetMethodInfo(typeof(Notification), "SetupNotification",
        ///             [typeof(float), typeof(float), typeof(bool)])
        /// 返回 null 表示没找到调用点。
        /// </summary>
        private static Type[] CallSiteTypes(string projectRoot, string methodName)
        {
            string f = Path.Combine(projectRoot, "State", "SpectreState.cs");
            if (!File.Exists(f)) return null;
            string text = SrcUtil.Clean(File.ReadAllText(f));
            int at = text.IndexOf("\"" + methodName + "\"", StringComparison.Ordinal);
            if (at < 0) return null;
            // 从调用点往后找第一个 [ ... ]
            int open = text.IndexOf('[', at);
            if (open < 0) return null;
            int close = text.IndexOf(']', open);
            if (close < 0) return null;
            string body = text.Substring(open + 1, close - open - 1);
            var list = new System.Collections.Generic.List<Type>();
            // **必须写 foreach (Match m ...)，不能用 var**：Regex.Matches 返回
            // MatchCollection，其元素是 object，var 拿到 object 就没有 Groups
            // （CS1061。上一轮已记过这个坑，这次又踩）。
            var hits = System.Text.RegularExpressions.Regex.Matches(
                body, @"typeof\(\s*([\w\.]+)\s*\)");
            foreach (System.Text.RegularExpressions.Match m in hits)
            {
                string n = m.Groups[1].Value;
                Type t = n == "float" ? typeof(float)
                    : n == "bool" ? typeof(bool)
                    : n == "int" ? typeof(int)
                    : n == "double" ? typeof(double)
                    : n == "string" ? typeof(string)
                    : null;
                if (t != null) list.Add(t);
            }
            return list.Count == 0 ? null : list.ToArray();
        }

        private static string Find(string path) => File.Exists(path) ? path : null;
    }
}
