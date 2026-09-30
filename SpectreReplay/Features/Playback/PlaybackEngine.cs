using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using SkyHook;
using UnityEngine;
using SpectreReplay.Patching;
using SpectreReplay.Features.Recording;
using Debug = UnityEngine.Debug;

namespace SpectreReplay.Features.Playback
{
    /// <summary>
    /// 回放引擎：**往游戏的按键队列里注入 SkyHookEvent**。
    ///
    /// 为什么这么做，而不是直接调 scrPlayer.HitInputEvent：
    ///   HitInputEvent(bool isAuto, InputEventState state, bool handleAll) 不带按键信息。
    ///   它是"某个键已经被解析成按下/松开之后"才被调用的下游，单独调它等于
    ///   无差别地敲一下，判定、按键标识、连击全对不上。
    ///
    /// 正确的注入点在 AsyncInputManager.keyQueue：
    ///   scrController.UpdateInput 每帧从它取空队列 → 按时间戳排序 →
    ///   对每条事件 new AsyncKeyCode(Key, Label) 并更新 keyMask/keyDownMask →
    ///   ProcessKeyInputs。整条链路没有任何按时间的过滤，队列里的都会被当真。
    ///   所以把构造好的 SkyHookEvent 塞进去，游戏会把它当真实按键走完全流程——
    ///   判定、飘字、特效、连击数全部与真人游玩一致，我们不必复刻任何判定逻辑。
    ///
    /// 时间字段怎么填：反编译 SkyHookEvent.GetTimeInTicks 的 IL 得到
    ///     (TimeSec * 10_000_000 + TimeSubsecNano / 100) * (Frequency / 10_000_000)
    /// 反解即可。填"当前"而不是"录制时的绝对时刻"——绝对时刻早就过期了，
    /// 队列排序只需要彼此的相对顺序，而同一帧注入的事件相对顺序与录制一致。
    /// </summary>
    internal static class PlaybackEngine
    {
        private static ReplayData _data;
        private static int _cursor;
        private static double _position;
        private static string _sourcePath;

        // SkyHookEvent 没有公开构造函数，用无初始化实例 + 反射写字段。
        private static readonly Type EventType_ = typeof(SkyHookEvent);
        private static readonly FieldInfo FTimeSec = EventType_.GetField("TimeSec");
        private static readonly FieldInfo FSubsec = EventType_.GetField("TimeSubsecNano");
        private static readonly FieldInfo FType = EventType_.GetField("Type");
        private static readonly FieldInfo FLabel = EventType_.GetField("Label");
        private static readonly FieldInfo FKey = EventType_.GetField("Key");

        internal static bool IsPlaying => _data != null;
        internal static string CurrentPath => _sourcePath;
        internal static string LastError { get; private set; } = "";

        /// <summary>回放总长（秒）。未载入时为 0。</summary>
        internal static double ReplayLength => _data == null ? 0.0 : _data.Length;

        /// <summary>游标归零但不卸载——离线渲染需要"从头再放一遍"。</summary>
        internal static void ResetCursor()
        {
            _cursor = 0;
            _position = 0;
        }

        /// <summary>构造器是否可用——不可用时整个回放不能工作，提前挡在界面上。</summary>
        internal static bool CanInject =>
            FTimeSec != null && FSubsec != null && FType != null
            && FLabel != null && FKey != null;

        internal static string Describe()
        {
            if (!CanInject)
            {
                return "SkyHookEvent 结构不匹配，无法回放";
            }
            if (_data == null)
            {
                return LastError.Length > 0 ? LastError : "—";
            }
            return (_data.SongName.Length > 0 ? _data.SongName : "?")
                + (_data.DifficultyName.Length > 0 ? " · " + _data.DifficultyName : "")
                + "   " + _data.Keys.Count + " keys"
                + "   " + ((_cursor * 100) / Math.Max(1, _data.Keys.Count)) + "%";
        }

        internal static bool Load(string path)
        {
            Stop();
            try
            {
                _data = Recording.ReplayFile.Read(path);
                _sourcePath = path;
                _cursor = 0;
                _position = 0;
                LastError = "";
                // playback 功能是按"是否有载入的回放"门控的，所以载入成功后
                // 必须让补丁层重挂一次，否则 UpdateInput 的 Prefix 还没上，
                // 注入的事件要等到下一次 Refresh 才会被消费。
                Patching.PatchLoader.Refresh();
                Debug.Log("[SpectreReplay] 已载入回放：" + path
                    + "（" + _data.Keys.Count + " 按键 / " + _data.Length.ToString("0.0") + "s）");
                return true;
            }
            catch (Exception ex)
            {
                _data = null;
                LastError = ex.Message;
                Debug.LogError("[SpectreReplay] 回放读取失败：" + ex);
                return false;
            }
        }

        internal static void Stop()
        {
            if (_data == null)
            {
                return;
            }
            _data = null;
            _cursor = 0;
            _position = 0;
            // 同 Load：门控随状态变化，必须重挂才能真正摘掉 Prefix
            Patching.PatchLoader.Refresh();
        }

        /// <summary>
        /// 每帧调用。把时间已到的事件注入队列。
        /// 必须早于 scrController.UpdateInput 执行，否则这一帧注入的会等到下一帧。
        /// </summary>
        internal static void Pump()
        {
            if (_data == null || !CanInject)
            {
                return;
            }
            double pos;
            try
            {
                pos = scrConductor.instance?.songposition_minusi ?? 0.0;
            }
            catch
            {
                return;
            }
            // 位置回退 = 重开/跳关：游标跟着退，否则会一次性注入积压的一大串
            if (pos < _position - 0.001)
            {
                _cursor = 0;
                while (_cursor < _data.Keys.Count
                    && _data.Keys[_cursor].SongPosition < pos)
                {
                    _cursor++;
                }
            }
            _position = pos;

            var keys = _data.Keys;
            int injected = 0;
            long ticks = Stopwatch.GetTimestamp();
            while (_cursor < keys.Count && keys[_cursor].SongPosition <= pos)
            {
                var ev = keys[_cursor++];
                try
                {
                    AsyncInputManager.keyQueue.Enqueue(Build(ev, ticks));
                    injected++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SpectreReplay] 注入失败 @" + pos.ToString("0.000")
                        + "：" + ex.Message);
                }
            }
            if (_cursor >= keys.Count && pos > _data.Length + 1.5)
            {
                Debug.Log("[SpectreReplay] 回放结束");
                Stop();
            }
        }

        /// <summary>构造一条 SkyHookEvent。</summary>
        private static SkyHookEvent Build(ReplayKeyEvent src, long stopwatchTicks)
        {
            // 反解 GetTimeInTicks：
            //   ticks = (TimeSec * 1e7 + TimeSubsecNano / 100) * (Frequency / 1e7)
            // 所以先把 Stopwatch 刻度换成 100ns 单位，再拆成秒 + 纳秒。
            double hundredNano = stopwatchTicks * 1e7 / Stopwatch.Frequency;
            long sec = (long)(hundredNano / 1e7);
            uint nano = (uint)((hundredNano - sec * 1e7) * 100.0);

            var ev = (SkyHookEvent)FormatterServices.GetUninitializedObject(EventType_);
            FTimeSec.SetValue(ev, sec);
            FSubsec.SetValue(ev, nano);
            FType.SetValue(ev, src.IsPressed
                ? SkyHook.EventType.KeyPressed
                : SkyHook.EventType.KeyReleased);
            FLabel.SetValue(ev, (KeyLabel)src.Label);
            FKey.SetValue(ev, src.Key);
            return ev;
        }
    }

    /// <summary>把回放泵接到输入处理之前。</summary>
    internal static class Patch_Playback
    {
        /// <summary>
        /// 挂在 UpdateInput 的 **Prefix**：必须早于游戏自己从队列取事件，
        /// 否则这一帧注入的要等到下一帧才被看到，凭空多一帧延迟。
        /// </summary>
        [GamePatch(typeof(scrController), "UpdateInput", PatchKind.Prefix, "playback")]
        private static void PumpPrefix()
        {
            try
            {
                PlaybackEngine.Pump();
            }
            catch (Exception ex)
            {
                Debug.LogError("[SpectreReplay] 回放泵异常：" + ex);
                PlaybackEngine.Stop();
            }
        }
    }
}