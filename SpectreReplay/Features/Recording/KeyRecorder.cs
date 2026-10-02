using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using SpectreReplay.Patching;
using SpectreReplay.Features.Playback;

namespace SpectreReplay.Features.Recording
{
    /// <summary>
    /// 按键采集器。
    ///
    /// **用 Win32 GetKeyboardState 轮询，不用 SkyHook 事件。**
    ///
    /// 为什么换：SkyHook 的按键事件是给**异步输入**用的驱动，它只在玩家
    /// 自己勾选了异步输入时才真正被游戏驱动起来（Persistence 的
    /// useAsynchronousInput，默认关闭）。不开异步输入时 KeyUpdated
    /// 几乎不产生事件——实测 50.9 秒的一局只录到 1 个按键。
    /// Spectre 也因此把主录制路径放在 GetKeyboardState 轮询上
    /// （Features/Replay/ReplayRecorder.cs:94），SkyHook 只是可选增强。
    ///
    /// 代价：时间精度等于一帧（60FPS 下最坏 ±16ms）。
    /// 换来的是"任何玩家、任何设置下都能录到"——这才是能用的下限。
    ///
    /// 时间基准：<c>scrConductor.songposition_minusi</c>，
    /// 与游戏自己的判定口径一致。
    /// </summary>
    internal static class KeyRecorder
    {
        /// <summary>
        /// 读 256 个虚拟键的当前状态。第 i 字节的 0x80 位 = 第 i 键是否按下。
        /// 照 Spectre 的 KeyboardSimulation.GetKeyboardState。
        ///
        /// 必须传够 256 字节：Win32 会按缓冲区长度写入，给小了就是内存越界。
        /// 声明里用 IntPtr 接长度，交给系统自己读数组长度，不留人为错误的机会。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetKeyboardState(byte[] pbKeyState);

        private static readonly byte[] PrevState = new byte[256];
        private static readonly byte[] CurrState = new byte[256];

        private static readonly ReplayData Current = new ReplayData();

        private static bool _recording;
        private static bool _pollingReady;

        internal static bool IsRecording => _recording;
        internal static ReplayData Data => Current;

        // ── 生命周期 ────────────────────────────────────────────
        internal static void Begin(scrConductor conductor, scrFloor floor)
        {
            if (_recording)
            {
                return;
            }
            EnsurePolling();
            // 清掉上一局遗留的按键状态：否则新一局开始时 PrevState 里还留着
            // 上一局按住的键，第一帧差分会把它们误判成"刚松开"。
            System.Array.Clear(PrevState, 0, 256);
            Reset(Current, conductor, floor);
            _recording = true;
            Debug.Log("[SpectreReplay] 开始录制");
            // 光写日志玩家看不见。顶栏那行状态得跟着变，
            // 否则"打了一局什么都没发生"与"没录到东西"从外面看一模一样。
            Ui.Status.Say("status.recording");
        }

        /// <summary>结束录制并按需落盘。返回文件路径；未落盘返回 null。</summary>
        internal static string End(bool autoSave)
        {
            if (!_recording)
            {
                return null;
            }
            _recording = false;
            // 收尾再差分一次：最后一次按键到通关之间的按键也要记进去。
            Capture(scrConductor.instance);
            Current.Length = SongPositionOfNow();
            // 终点地板序号：必须在收尾时取（通关那一刻 currentSeqID 才是终点）。
            // 取不到就留 -1，校验器会据此判定「非完整局」并要求精确匹配起点。
            try
            {
                Current.EndTile = scrController.instance.currentSeqID;
            }
            catch
            {
                Current.EndTile = -1;
            }
            if (Current.IsEmpty || !autoSave)
            {
                // 空关（一个键都没按）也会走到这里。以前直接返回 null、
                // 一句话都不说——玩家打完一局看不到任何反馈，只能以为功能坏了。
                // 用警告级：这不是正常结局，得让人多看一眼。
                if (Current.IsEmpty)
                {
                    Ui.Status.Warn("status.saved_none");
                }
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
                // 只给文件名：整条路径塞进顶栏那一行会把界面挤爆
                Ui.Status.Say("status.saved",
                    System.IO.Path.GetFileName(path));
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
            CaptureValidationFields(d);
        }

        /// <summary>
        /// 采集回放前校验所需的元数据。
        ///
        /// 这些与上面的展示元数据不同：**取不到就不填，绝不填假值**。
        /// 校验器看到空值会跳过对应检查（而不是拿一个错的哈希去和谱面比对，
        /// 那种"永远不相等"的假失败比不校验更糟——用户会以为回放坏了）。
        ///
        /// 每项都包 try/catch：录制开始阶段游戏对象可能还在初始化，
        /// 抛异常会连带把整次录制掐掉。
        /// </summary>
        private static void CaptureValidationFields(ReplayData d)
        {
            try
            {
                // 场景名：编辑器和正式关卡是两个场景，回放逻辑不同
                d.SceneName = UnityEngine.SceneManagement.SceneManager
                    .GetActiveScene().name;
            }
            catch { }

            try
            {
                d.JudgeMode = GCS.difficulty.ToString();
                d.StartTile = GCS.checkpointNum;
                d.InternalLevelName = GCS.internalLevelName ?? "";
                d.IfNoFail = scrController.instance != null
                    && scrController.instance.noFail;
                d.SpeedTrailMode = GCS.speedTrialMode || GCS.practiceMode;
                d.SpeedTrail = d.SpeedTrailMode
                    ? (double)GCS.currentSpeedTrial : 0.0;
                d.IsOfficialLevel = ADOBase.isOfficialLevel;
                // 判定窗口：回放时应用它，飘字才与录制一致
                d.HitMarginLimit = (int)GCS.hitMarginLimit;
            }
            catch { }

            // 谱面数据只在正式关卡里有；编辑器内 scnGame 为 null
            try
            {
                var game = scnGame.instance;
                if (game != null && game.levelData != null)
                {
                    var ld = game.levelData;
                    d.Pitch = ld.pitch;
                    d.Bpm = ld.bpm;
                    d.FloorHash = ComputeFloorHash(ld);
                }
            }
            catch { }
        }

        /// <summary>
        /// 谱面角度序列的指纹。
        ///
        /// **这是整套校验里最关键的一项**：回放按地板序号定位，谱面改了之后
        /// 同一个序号对应的可能是完全不同的砖——回放会在错误的位置触发判定，
        /// 而且**没有任何报错**。
        ///
        /// 官方关卡恒返回 0（官方谱不可改，Spectre 也这么做，
        /// 见 Patch_Lifecycle.cs:157-159）。
        ///
        /// 取不到角度数据时返回 int.MinValue —— 一个**永远不可能匹配**的哨兵值。
        /// 用它而不是 0：0 是官方关卡的合法哈希，返回 0 会让
        /// 「取不到数据」伪装成「官方谱」，从而跳过了本该做的校验。
        /// 校验器见到 int.MinValue 会跳过检查。
        /// </summary>
        private static int ComputeFloorHash(ADOFAI.LevelData ld)
        {
            if (ADOBase.isOfficialLevel)
            {
                return 0;
            }
            var angles = ld.angleData;
            if (angles == null || angles.Count == 0)
            {
                return int.MinValue;     // 取不到：让校验器跳过，而不是误判
            }
            // 角度归一化到 [0,360) 再哈希：0 与 360 是同一块砖的两种表示，
            // 不归一化会让同一张谱面算出两个哈希。
            // 用 InvariantCulture 格式化：小数点写成逗号的区域设置会算出
            // 完全不同的字符串，进而误报"谱面不匹配"（Spectre 的同款教训，
            // 见 Patch_Lifecycle.cs:332-336）。
            var sb = new System.Text.StringBuilder(angles.Count * 8);
            for (int i = 0; i < angles.Count; i++)
            {
                float a = angles[i];
                float norm = a >= 0f ? a % 360f : 360f - (-a % 360f);
                sb.Append(norm.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString().GetHashCode();
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

        // ── Win32 轮询采集 ────────────────────────────────────
        /// <summary>
        /// 校验 Win32 键盘状态读取可用。
        ///
        /// 用一次真实调用来验，而不是看 P/Invoke 声明对不对：user32.dll 在
        /// 非 Windows 平台根本不存在，声明错了也只有真调一次才会炸。
        /// 失败只记一次日志，之后每局都会重试（不能像原来那样置个永久标志
        /// 就再也不试——静态字段跨场景活着，第一次时机不对就永久失效）。
        /// </summary>
        private static void EnsurePolling()
        {
            if (_pollingReady)
            {
                return;
            }
            try
            {
                GetKeyboardState(CurrState);
                _pollingReady = true;
                Debug.Log("[SpectreReplay] 键盘轮询采集可用"
                    + "（GetKeyboardState 已验证）");
            }
            catch (Exception ex)
            {
                Debug.LogError("[SpectreReplay] 无法读取键盘状态，本局录不到按键："
                    + ex.Message + "（下一局会再试）");
                _pollingReady = false;
            }
        }

        /// <summary>
        /// 采样一次键盘状态并差分，把变化的键记进 ReplayData。
        ///
        /// 照 Spectre 的 ReplayRecorder.RecordKeyboardState：GetKeyboardState
        /// 返回 256 字节，第 i 字节的 0x80 位表示第 i 个虚拟键当前是否按下。
        /// 与上一帧差分，0→1 记按下、1→0 记松开。
        ///
        /// 一帧内只取一次基准；同一帧被多个补丁点调用时靠 _polled 挡掉，
        /// 否则会连续差分两次、同一对按键被记成四次。
        /// </summary>
        private static void Capture(scrConductor conductor)
        {
            if (!_recording || !_pollingReady)
            {
                return;
            }
            if (conductor == null)
            {
                return;
            }
            if (_lastFrame == Time.frameCount)
            {
                return;
            }
            _lastFrame = Time.frameCount;

            double songPos;
            try
            {
                songPos = conductor.songposition_minusi;
            }
            catch
            {
                return;
            }
            if (songPos < 0)
            {
                songPos = 0;      // 开局前就按了：钳到 0，不丢
            }
            try
            {
                GetKeyboardState(CurrState);
            }
            catch
            {
                return;
            }

            for (int i = 1; i < 256; i++)
            {
                bool down = (CurrState[i] & 0x80) != 0;
                bool was = (PrevState[i] & 0x80) != 0;
                if (down == was)
                {
                    continue;
                }
                // Shift/Ctrl/Alt/Esc 不录：回放时按住修饰键会与玩家自己的
                // 按键叠加，Esc 还会被游戏当成暂停。照 Spectre 的 BlockedKeys。
                ushort key = (ushort)i;
                if (Playback.KeyboardSimulation.IsBlockedKey(key))
                {
                    continue;
                }
                Current.Keys.Add(new ReplayKeyEvent
                {
                    Key = key,
                    Label = 0,
                    IsPressed = down,
                    SongPosition = songPos,
                });
            }
            // PrevState 必须无条件整体更新：被跳过的键（BlockedKeys）这一帧
            // 没记事件，但状态照样要跟上，否则它"松开"的那一刻会被误记一次。
            System.Array.Copy(CurrState, PrevState, 256);
        }

        /// <summary>一帧只采样一次。多点共用同一个守卫。</summary>
        private static int _lastFrame = -1;

        // ── 每帧推进 ────────────────────────────────────────────
        /// <summary>由补丁在游玩循环的每帧更新里调用。</summary>
        internal static void Pump(scrConductor conductor)
        {
            Capture(conductor);
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