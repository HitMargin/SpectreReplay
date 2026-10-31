using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SpectreReplay.Features.Compat;

/// <summary>
/// Harmony 运行时取证（2026-11-01 调查：「+ 挂载 38/41」与 11 行后的执行链
/// 「看不到自己的前缀」在静态分析下不可能同时成立——两个版本的 0Harmony
/// 反编译都读写同一字典。本探针让机器自己说话）。
///
/// <para>背景：本机是 <b>UMM + MelonLoader 双加载器</b>共存（doorstop 注入的
/// MelonLoader 带 0Harmony 2.10.2，UMM 目录带 2.3.6，二者哈希不同）。所有 mod
/// 编译期引用 2.3.6.0，但运行期绑到哪一份由宿主加载顺序决定——绑定分裂时
/// 「写在一个字典、读在另一个字典」正是观察到的形态。</para>
///
/// <para>三条证据链一次抓全：
/// ① typeof(Harmony) 的程序集身份 + 全部 *Harmony* 已加载程序集（有几份、各自位置）；
/// ② 写→读 roundtrip：拿自己程序集里的空方法当靶，Patch 后立刻
///    <c>Harmony.GetPatchInfo</c> 读——一致则写读同世界，分裂当场现形；
/// ③ 两个候选存储字典的实时条目数（哪本账有、哪本账空）。</para>
///
/// 每会话只跑一次，5~7 行日志。查清后本类整体退役。
/// </summary>
internal static class HarmonyForensics
{
    private static bool _done;

    internal static void Run()
    {
        if (_done) return;
        _done = true;
        try
        {
            Assembly asm = typeof(Harmony).Assembly;
            Debug.Log("[SpectreReplay] Harmony取证: typeof(Harmony) = " + asm.GetName().FullName
                + " @ " + (string.IsNullOrEmpty(asm.Location) ? "(byte[] 载入，无文件位置)" : asm.Location));

            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                if (n.IndexOf("Harmony", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Debug.Log("[SpectreReplay] Harmony取证: 已加载 " + a.GetName().FullName
                    + " @ " + (string.IsNullOrEmpty(a.Location) ? "(byte[] 载入)" : a.Location));
            }

            // ② 写→读 roundtrip（靶是自己的空方法，读完立刻拆，不污染任何人的链）
            MethodInfo target = typeof(HarmonyForensics).GetMethod(
                nameof(RoundTripTarget), BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo prefix = typeof(HarmonyForensics).GetMethod(
                nameof(RoundTripPrefix), BindingFlags.NonPublic | BindingFlags.Static);
            const string probeOwner = "SpectreReplay:forensics";
            var h = new Harmony(probeOwner);
            try
            {
                h.Patch(target, prefix: new HarmonyMethod(prefix));
                Patches info = Harmony.GetPatchInfo(target);
                bool seen = info?.Prefixes != null
                    && info.Prefixes.Any(p => p.owner == probeOwner);
                Debug.Log("[SpectreReplay] Harmony取证: 写→读 roundtrip = "
                    + (seen ? "一致（同字典）" : "★★分裂：写进去了，Harmony.GetPatchInfo 读不到★★"));
                bool enumerated = Harmony.GetAllPatchedMethods().Any(m => m == target);
                Debug.Log("[SpectreReplay] Harmony取证: GetAllPatchedMethods 含靶 = " + enumerated
                    + "，全量补丁方法数 = " + Harmony.GetAllPatchedMethods().Count());
            }
            finally
            {
                try { h.UnpatchAll(probeOwner); }
                catch (Exception ex) { Debug.Log("[SpectreReplay] Harmony取证: roundtrip 拆除失败 " + ex.Message); }
            }

            // ③ 两本候选账的实时条目数——哪本有货哪本空，直接指认读写分裂点
            DumpStorage(asm, "HarmonyLib.PatchManager", "PatchInfos");
            DumpStorage(asm, "HarmonyLib.HarmonySharedState", "state");
        }
        catch (Exception ex)
        {
            Debug.Log("[SpectreReplay] Harmony取证: 探针自身异常 " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static void DumpStorage(Assembly asm, string typeName, string fieldName)
    {
        Type t = asm.GetType(typeName);
        if (t == null)
        {
            Debug.Log("[SpectreReplay] Harmony取证: 存储 " + typeName + " 不存在于 typeof(Harmony) 程序集");
            return;
        }
        FieldInfo f = t.GetField(fieldName,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (f == null)
        {
            Debug.Log("[SpectreReplay] Harmony取证: 存储 " + typeName + "." + fieldName + " 字段不存在");
            return;
        }
        object dict = f.GetValue(null);
        string shape = dict == null ? "null" : dict.GetType().FullName;
        int count = -1;
        try
        {
            PropertyInfo cnt = dict?.GetType().GetProperty("Count");
            if (cnt != null) count = (int)cnt.GetValue(dict);
        }
        catch { /* 读不到条目数不阻断其它取证 */ }
        Debug.Log("[SpectreReplay] Harmony取证: 存储 " + typeName + "." + fieldName
            + " = " + shape + "，条目 " + count);
    }

    private static void RoundTripTarget() { }

    private static bool RoundTripPrefix() => true;
}
