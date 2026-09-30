using System;
using System.Collections.Generic;
using System.Reflection;
using SkyHook;
using UnityEngine;
using SpectreReplay.Patching;

namespace SpectreReplay.Features.Recording
{
    /// <summary>
    /// 按键采集器。
    ///
    /// 时间基准的关键：SkyHook 给的是**绝对时钟**（GetTimeInTicks，100ns 单位），
    /// 与游戏帧无关。所以事件排队再晚到帧里处理，落到歌曲时间轴上的位置也不变——
    /// 这是"高精度输入"的全部价值所在。若改成在帧里轮询 Input.GetKeyDown，
    /// 误差就是整个帧时长（60FPS 下最坏 ±16ms，谱面上就是半个判定）。
    ///
    /// 换算公式（与游戏自己的 GetHitMarginInSec 同构）：
    ///     songPos = (eventTicks + dspNow - wallNow) / 1e7 - conductor.dspTimeSong - addoffset
    /// 逐帧只取**一对**基准（dspTime 与系统时钟），因为取得越接近越准，
    /// 而一帧内多次取只会引入额外的抖动。
    /// </summary>
    internal static class KeyRecorder
    {
        private sealed class RawEvent
        {
            internal long Ticks;
            internal ushort Key;
            internal int Label;
            internal bool IsPressed;
        }

        // SkyHook 回调在游戏自己的输入线程上跑，不是主线程。
        // 单个 Queue<T> 的并发入队+出队在 .NET/Mono 上是安全的；这里之所以
        // 还额外加锁，是因为回调侧要读 _reject（主线程写）。
        private static readonly Queue<RawEvent> Pending = new Queue<RawEvent>();
        private static readonly List<RawEvent> Batch = new List<RawEvent>(256);
        private static readonly object Gate = new object();

        private static readonly ReplayData Current = new ReplayData();

        private static bool _recording;
        private static bool _hookReady;
        private static bool _rejectMouse = true;

        internal static bool IsRecording => _recording;
        internal static ReplayData Data => Current;

        // ── 生命周期 ────────────────────────────────────────────
        internal static void Begin(scrConductor conductor, scrFloor floor)
        {
            if (_recording)
            {
                return;
            }
            EnsureHook();
            lock (Gate)
            {
                Pending.Clear();
            }
            Reset(Current, conductor, floor);
            _recording = true;
            Debug.Log("[SpectreReplay] 开始录制");
        }

        /// <summary>结束录制并按需落盘。返回文件路径；未落盘返回 null。</summary>
        internal static string End(bool autoSave)
        {
            if (!_recording)
            {
                return null;
            }
            _recording = false;
            lock (Gate)
            {
                DrainInto(Current, null);
            }
            Current.Length = SongPositionOfNow();
            if (Current.IsEmpty || !autoSave)
            {
                return null;
            }
            try
            {
                string dir = Settings.ReplayDir;
                System.IO.Directory.CreateDirectory(dir);
                string safe = SafeName(Current.SongName, Current.DifficultyName);
                string stamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string path = System.IO.Path.Combine(
                    dir, safe + "_" + stamp + ".srp");
                ReplayFile.Write(path, Current);
                Debug.Log("[SpectreReplay] 回放已保存：" + path
                    + "（按键 " + Current.Keys.Count + " 个）");
                return path;
            }
            catch (Exception ex)
            {
                Debug.LogError("[SpectreReplay] 回放保存失败：" + ex);
                return null;
            }
        }

        private static void Reset(ReplayData d, scrConductor conductor, scrFloor floor)
        {
            d.Keys.Clear();
            d.Margins.Clear();
            d.MarginTimes.Clear();
            d.Length = 0;
            // 元数据纯装饰（只影响文件名与界面展示）。游戏的歌曲/难度对象
            // （AudioSource / Level）在程序集里拿不到完整元数据——Assembly-CSharp
            // 的 GetTypes() 只返回 2047 个类型，里面没有 AudioSource，
            // 而 scrConductor.song 的静态类型却印作它。
            // 所以这里不写编译期依赖，改成运行时按字段名反射，取不到就用空串。
            // 好处：游戏更新改了这些字段也只影响文件名，不会连带编译失败或崩游戏。
            d.SongName = Probe(conductor, "song", "title")
                ?? Probe(conductor, "song", "name") ?? "";
            d.SongId = Probe(conductor, "song", "ID") ?? "";
            d.DifficultyName = Probe(floor, "speedToggle", "title")
                ?? Probe(floor, "speedToggle", "difficulty")
                ?? Probe(floor, "level", "name") ?? "";
            d.DifficultyId = Probe(floor, "speedToggle", "ID") ?? "";
            d.Mapper = Probe(floor, "speedToggle", "author") ?? "";
        }

        /// <summary>按 "持有者字段 → 目标字段" 两级反射取值，任一级缺失返回 null。</summary>
        private static string Probe(object holder, string member, string field)
        {
            if (holder == null)
            {
                return null;
            }
            try
            {
                object inner = GetMember(holder, member);
                if (inner == null)
                {
                    return null;
                }
                object v = GetMember(inner, field);
                return v as string;
            }
            catch
            {
                return null;
            }
        }

        private static object GetMember(object target, string name)
        {
            Type t = target.GetType();
            while (t != null)
            {
                FieldInfo f = t.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static);
                if (f != null)
                {
                    return f.GetValue(target);
                }
                PropertyInfo p = t.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static);
                if (p != null && p.GetIndexParameters().Length == 0)
                {
                    return p.GetValue(target, null);
                }
                t = t.BaseType;
            }
            return null;
        }

        // ── SkyHook 回调 ────────────────────────────────────────
        private static void EnsureHook()
        {
            if (_hookReady)
            {
                return;
            }
            _hookReady = true;
            try
            {
                // KeyUpdated 是 **静态只读字段**（不是 event 属性），所以不能 `+=`，
                // 必须取到 UnityEvent 本体再 AddListener。
                SkyHookManager.KeyUpdated?.AddListener(OnKey);
                // StartHook 是静态方法；isHookActive 是实例属性，要走单例取。
                var mgr = SkyHookManager.Instance;
                if (mgr == null || !mgr.isHookActive)
                {
                    SkyHookManager.StartHook();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[SpectreReplay] SkyHook 初始化失败，本次退化为无 SkyHook 输入："
                    + ex.Message);
                _hookReady = false;
            }
        }

        private static void OnKey(SkyHookEvent ev)
        {
            if (!_recording || !ReferenceEquals(ev, null))
            {
                return;
            }
            // 鼠标键不参与：它们没有"判定"语义，录进去只会让回放时的视角乱跳。
            if (_rejectMouse && IsMouse(ev.Label))
            {
                return;
            }
            var e = new RawEvent
            {
                Ticks = ev.GetTimeInTicks(),
                Key = ev.Key,
                Label = (int)ev.Label,
                IsPressed = ev.Type == SkyHook.EventType.KeyPressed,
            };
            lock (Gate)
            {
                Pending.Enqueue(e);
            }
        }

        private static bool IsMouse(KeyLabel label)
            => label == KeyLabel.MouseLeft || label == KeyLabel.MouseRight
            || label == KeyLabel.MouseX2 || label == KeyLabel.MouseMiddle;

        // ── 每帧推进 ────────────────────────────────────────────
        /// <summary>由补丁在 UpdateInput 之后调用：把排队的原始事件换算成歌曲时间。</summary>
        internal static void Pump(scrConductor conductor)
        {
            if (!_recording || conductor == null || conductor.song == null)
            {
                lock (Gate)
                {
                    Pending.Clear();
                }
                return;
            }
            lock (Gate)
            {
                DrainInto(Current, conductor);
            }
        }

        /// <summary>
        /// 队列 → ReplayData 的换算。必须在 Gate 内调用。
        /// 单遍完成：先把待处理事件按时间戳排好，再逐个映射成歌曲时间。
        /// 一帧内 dsp/墙钟基准只取一次——取得越近越准，多次取只会多引入抖动。
        /// </summary>
        private static void DrainInto(ReplayData d, scrConductor conductor)
        {
            if (Pending.Count == 0)
            {
                return;
            }
            Batch.Clear();
            while (Pending.TryDequeue(out RawEvent e))
            {
                Batch.Add(e);
            }
            Batch.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));

            long wallNow = DateTime.Now.Ticks;
            double dspNowTicks = AudioSettings.dspTime * 1e7;
            double songOrigin = conductor == null ? 0.0 : SongOriginSeconds(conductor);

            for (int i = 0; i < Batch.Count; i++)
            {
                RawEvent e = Batch[i];
                double pos = (e.Ticks + dspNowTicks - wallNow) / 1e7 - songOrigin;
                if (pos < 0)
                {
                    pos = 0;      // 玩家在开局前就按了：钳到 0，不丢
                }
                d.Keys.Add(new ReplayKeyEvent
                {
                    Key = e.Key,
                    Label = e.Label,
                    IsPressed = e.IsPressed,
                    SongPosition = pos,
                });
            }
            Batch.Clear();
        }

        /// <summary>歌曲时间轴原点在墙钟上的位置（秒）。</summary>
        private static double SongOriginSeconds(scrConductor conductor)
        {
            try
            {
                return conductor.dspTimeSong + conductor.addoffset;
            }
            catch
            {
                return 0;
            }
        }

        internal static double SongPositionOfNow()
        {
            var c = scrConductor.instance;
            if (c == null || c.song == null)
            {
                return 0;
            }
            return c.dspTimeSongPosZero;
        }

        private static string SafeName(string song, string diff)
        {
            string s = (string.IsNullOrEmpty(song) ? "unknown" : song)
                + "_" + (string.IsNullOrEmpty(diff) ? "-" : diff);
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            {
                s = s.Replace(c, '_');
            }
            return s.Length > 48 ? s.Substring(0, 48) : s;
        }

        // ── 判定记账 ────────────────────────────────────────────
        /// <summary>记录一次判定。由 AddHit 补丁调用。</summary>
        internal static void RecordMargin(HitMargin margin, double songPosition)
        {
            if (!_recording)
            {
                return;
            }
            string name = margin.ToString();
            Current.Margins.TryGetValue(name, out int n);
            Current.Margins[name] = n + 1;
            if (!Current.MarginTimes.TryGetValue(name, out var list))
            {
                list = new List<double>(64);
                Current.MarginTimes[name] = list;
            }
            list.Add(songPosition);
        }
    }
}