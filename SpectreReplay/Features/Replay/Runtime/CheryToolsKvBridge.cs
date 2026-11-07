using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// CheryTools（CT）键位显示的实时桥。
///
/// <para><b>为什么不能像 Quartz 那样直接喂边沿。</b>CT 的
/// <c>KvRuntimeHost.PollInputStates</c> 每帧清空 <c>_polledKeys</c>，然后对每个
/// 绑定键直接问 <c>UnityEngine.Input.GetKey</c> / <c>GetKeyDown</c>。它没有
/// 外部驱动接口，也不读 SkyHook/KeyViewer 的抽象——回放按键在托管层，CT
/// 完全看不见。</para>
///
/// <para><b>做法：在 PollInputStates 之后接管那一帧的结果。</b>补丁挂 Postfix，
/// 轮询完立刻把 CT 自己写好的字典改写成回放状态：按住态来自
/// <see cref="ManagedInput"/>，按下沿按帧戳算（<see cref="ManagedInput.WasPressedThisFrame"/>）。
/// 这样雨滴、计数、按住高亮都走 CT 原有渲染链，不碰它的 UI 代码。</para>
///
/// <para><b>为什么还要改 <c>_hasTrackedPressed</c>。</b>CT 用它决定是否继续
/// 刷新按住/统计；只改字典不改它，会出现「键亮了但雨滴与统计不跟」的半截状态。</para>
///
/// <para><b>与镜像开关无关、也不要求窗口聚焦。</b>这里是纯托管替换，物理键在
/// 回放期间本来就被隔离（见 Patch_MirrorEchoSuppressor），所以直接以回放态为准。</para>
/// </summary>
internal static class CheryToolsKvBridge
{
    private const string AssemblyName = "CheryTools.KeyViewer.Preview";
    private const string HostTypeName = "CheryTools.KeyViewer.Preview.KvRuntimeHost";
    private const string PollMethodName = "PollInputStates";
    private const string PatchId = "cherytools-kv-poll";
    private const string UpdateMethodName = "Update";
    private const string UpdatePatchId = "cherytools-kv-update";

    private static bool _installed;
    private static bool _attemptedThisSession;

    private static FieldInfo _polledKeys;
    private static FieldInfo _hasTrackedPressed;
    private static FieldInfo _pollStatePressed;
    private static FieldInfo _pollStateDown;
    private static Type _pollStateType;

    /// <summary>
    /// 模组重新装配时复位。手动补丁会被 Initialize 一并卸掉，但这里的静态
    /// 「已安装」标志不会跟着翻假——不复位就会永远以为还挂着。
    /// </summary>
    internal static void Reset()
    {
        _installed = false;
        _attemptedThisSession = false;
    }

    /// <summary>
    /// 回放开始时尝试挂载。CT 未安装时静默返回（下次回放再试）；程序集在但
    /// 接口形状变了则记一次警告并停止重试。
    /// </summary>
    internal static void TryInstall()
    {
        if (_installed) return;
        if (_attemptedThisSession) return;

        try
        {
            Assembly asm = FindAssembly(AssemblyName);
            if (asm == null) return;                 // CT 未加载：不记标记，下次再试

            Type hostType = asm.GetType(HostTypeName, throwOnError: false);
            if (hostType == null) return;

            // 从这里开始程序集确实在：成败都记，避免每局重复反射。
            _attemptedThisSession = true;

            MethodInfo poll = hostType.GetMethod(PollMethodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo update = hostType.GetMethod(UpdateMethodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            _polledKeys = hostType.GetField("_polledKeys",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _hasTrackedPressed = hostType.GetField("_hasTrackedPressed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _pollStateType = hostType.GetNestedType("KeyPollState",
                BindingFlags.NonPublic | BindingFlags.Public);
            _pollStatePressed = _pollStateType?.GetField("Pressed",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _pollStateDown = _pollStateType?.GetField("Down",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (poll == null || update == null
                || _polledKeys == null || _hasTrackedPressed == null
                || _pollStateType == null
                || _pollStatePressed == null || _pollStateDown == null)
            {
                Debug.LogWarning("[SpectreReplay] CT 键位桥未生效：KvRuntimeHost 的 "
                    + "PollInputStates/_polledKeys/KeyPollState 形状变了"
                    + "（回放时 CheryTools 键位显示将不跟随）");
                return;
            }

            MethodInfo patch = typeof(CheryToolsKvBridge).GetMethod(
                nameof(PollInputStates_Postfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo gatePatch = typeof(CheryToolsKvBridge).GetMethod(
                nameof(Update_Prefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (patch == null || gatePatch == null)
            {
                Debug.LogWarning("[SpectreReplay] CT 键位桥未生效：找不到补丁方法");
                return;
            }

            // **必须先开 Update 的门。** CT 的 Update 在调用 PollInputStates
            // 之前有一道早退：`!_hasTrackedPressed && !Input.anyKey &&
            // !Input.anyKeyDown` 就清空字典并 return。回放期物理键盘被隔离，
            // 两个 Input 查询恒假；首帧 _hasTrackedPressed 也为假，于是
            // PollInputStates 根本不会执行，后置补丁永远等不到。前置把
            // _hasTrackedPressed 钉真，轮询才会每帧发生，由后置补丁决定状态。
            PatchShared.SpectrePatchHub.PatchManual(
                update, gatePatch, PatchShared.PatchType.Prefix, UpdatePatchId,
                FeatureKeys.Replay);
            PatchShared.SpectrePatchHub.PatchManual(
                poll, patch, PatchShared.PatchType.Postfix, PatchId,
                FeatureKeys.Replay);
            _installed = true;
            Debug.Log("[SpectreReplay] 已为 CheryTools 键位显示挂接回放桥"
                + "（KvRuntimeHost.PollInputStates）");
        }
        catch (Exception ex)
        {
            // 显示层失败不能带偏回放本体
            Debug.LogWarning("[SpectreReplay] CT 键位桥挂载失败: " + ex.Message);
        }
    }

    /// <summary>
    /// Update 前置：回放期间强制打开 CT 的轮询门。见挂载处注释——
    /// 物理键盘被隔离时 Input.anyKey/anyKeyDown 恒假，不打开这道门
    /// PollInputStates 一次都不会跑。
    /// </summary>
    private static void Update_Prefix(object __instance)
    {
        if (!ManagedInput.Active || __instance == null || _hasTrackedPressed == null)
            return;
        try
        {
            _hasTrackedPressed.SetValue(__instance, true);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] CT 键位桥开门失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 补丁体：CT 刚轮询完物理键，把这一帧的结果改写成回放状态。
    /// 只在回放期间接管；非回放期原样放行，CT 行为完全不变。
    /// </summary>
    private static void PollInputStates_Postfix(object __instance)
    {
        if (!ManagedInput.Active || __instance == null) return;
        if (_polledKeys == null || _hasTrackedPressed == null
            || _pollStatePressed == null || _pollStateDown == null)
            return;

        try
        {
            object host = __instance;
            if (!TryGetDictionary(host, out IDictionary polled))
                return;

            bool anyPressed = false;
            var keys = new List<object>();
            foreach (object key in polled.Keys) keys.Add(key);

            foreach (object key in keys)
            {
                if (!(key is KeyCode code)) continue;
                ushort vk = KeyViewerBridge.ToNativeVk(code);
                bool pressed = vk != 0 && ManagedInput.IsHeldVk(vk);
                bool down = vk != 0 && ManagedInput.WasPressedThisFrame(vk);
                object state = Activator.CreateInstance(_pollStateType);
                _pollStatePressed.SetValue(state, pressed);
                _pollStateDown.SetValue(state, down);
                polled[key] = state;
                anyPressed |= pressed;
            }

            _hasTrackedPressed.SetValue(host, anyPressed);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] CT 键位桥写入失败: " + ex.Message);
        }
    }

    private static bool TryGetDictionary(object host, out IDictionary polled)
    {
        polled = null;
        object value = _polledKeys.GetValue(host);
        polled = value as IDictionary;
        return polled != null;
    }

    private static Assembly FindAssembly(string name)
    {
        Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].GetName().Name == name) return all[i];
        }
        return null;
    }
}
