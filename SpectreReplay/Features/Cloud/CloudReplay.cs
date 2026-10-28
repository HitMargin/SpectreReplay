using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using SpectreReplay.Features.Replay;

namespace SpectreReplay.Features.Cloud
{
    /// <summary>
    /// 回放的 Steam 云同步（业务层）：把 <see cref="SteamCloud"/> 的裸文件操作
    /// 组合成"保存即上云 / 启动时按需回填"两件事。
    ///
    /// 云端布局：<c>SpectreReplay/Replays/&lt;回放文件名&gt;</c> 及其同名
    /// <c>.wav</c>。与游戏自身 Auto-Cloud 的 <c>User/*.sav</c> 完全隔离。
    ///
    /// **上传是"写进本地云缓存"，不是"传完才算成功"。** Steam 客户端在
    /// 游戏退出时统一批量上传（实测日志：HTTP upload ... success → Upload OK）。
    /// 所以这里同步调用不会卡网络，强杀进程也不会丢——缓存里的改动下次启动会补传。
    /// </summary>
    internal static class CloudReplay
    {
        /// <summary>
        /// 键盘音上云的体积上限（48 MB）。
        ///
        /// 不回传太大的理由有二：① Steam 单文件硬上限 100 MiB，长录音会撞上；
        /// ② 这是在保存回放的那一帧同步读+写的，几十 MB 的 wav 会让结算画面
        /// 卡一下。48 MB 覆盖十几分钟的录音，同时离硬上限还留了一倍余量——
        /// 实测玩家的正常录音就在 12～25 MB 这一档，设 24 MB 会把它们挡在外面。
        /// 回放本体是压缩过的几十 KB～几 MB，从来不设限。
        /// </summary>
        internal const int MaxWavBytes = 48 * 1024 * 1024;

        /// <summary>云端体积/数量软阈值（只用于 UI 提醒，不做任何自动删除）。</summary>
        internal const int SoftCount = 300;
        internal const long SoftBytes = 700L * 1024 * 1024;

        /// <summary>
        /// 模组自身配置文件在云端的名字：<c>SpectreReplay/Configs.json</c>。
        ///
        /// 故意**不放在 <c>Replays/</c> 下**：那个目录是「一局回放 + 同名 wav」的
        /// 集合，回放列表/统计/批量补传都按它枚举；配置混进去会被当成一份坏回放
        /// 列出来，或者被「云端已有同名文件」的判据顺手跳过。
        /// </summary>
        internal const string RemoteConfigName = SteamCloud.RemoteRoot + "/Configs.json";

        /// <summary>两次配置上传之间的最小间隔（毫秒）。</summary>
        private const int ConfigUploadIntervalMs = 5000;

        // ── 配置文件上传的去重 / 节流状态 ───────────────────────────────────
        //
        // 设置页每拨一下开关就会调一次 ConfigManager.SaveConfigs，不节流的话拨十下
        // 就是十次「读全文件 + 一次 P/Invoke + 一次字节封送」。三个字段各管一件事：
        //   _configUploaded  上次真正写进云端的那份内容（逐字节比较，相同就整段跳过）
        //   _configUploadTick 上次尝试上传的时刻（失败的也记，坏文件不该被反复重试）
        //   _configPending   因节流窗口被推迟的「最后一次改动」，由补传协程收尾
        private static int _configUploadTick = int.MinValue;
        private static byte[] _configUploaded;
        private static bool _configPending;
        private static bool _configPumpActive;
        // 补传协程的启动时刻：仅用于「泵被销毁、finally 没跑到」时的自愈判定
        private static int _configPumpStartTick;
        // 「启动时从云端恢复配置」是否已给出结论。未落定前一律不上传：
        // 否则「本地配置缺失 + Steam 比设置页晚就绪 + 玩家先改了一项」会把
        // 云端那份还没读过的备份用一份近乎默认的配置覆盖掉，之后再也恢复不回来。
        private static bool _configRestoreSettled;

        /// <summary>云端回放统计（UI 每帧要读，必须缓存）。</summary>
        internal struct Stats
        {
            internal bool Ready;       // steam_api64 可用
            internal bool AppEnabled;  // 本游戏的云同步在客户端里开着
            internal int Files;        // 云端回放相关文件数（含 wav）
            internal int Replays;      // 其中回放本体数
            internal long Bytes;
        }

        private static Stats _stats;
        private static int _statsTick = int.MinValue;

        /// <summary>上传结果。</summary>
        internal struct UploadResult
        {
            internal int Uploaded;
            internal int Skipped;
            internal int Failed;
            internal string LastError;
        }

        /// <summary>恢复结果。</summary>
        internal struct RestoreResult
        {
            internal int Restored;
            internal int Failed;
            internal string LastError;
        }

        /// <summary>云端一局回放（列表用；只有 UI 需要的三样东西）。</summary>
        internal struct CloudEntry
        {
            internal string Name;       // 云端文件名（纯文件名，不含目录）
            internal int Size;          // 回放本体字节数（不含配套 wav）
            /// <summary>
            /// Steam 记录的写入时间（RTime32，unix 秒）；0 = 不知道。
            ///
            /// <para>文件名虽然以 <c>yyyyMMddHHmmss</c> 开头，但那是**录制端**的
            /// 本地时间，跨时区、跨机器都对不上；详情面板要显示「什么时候录的」，
            /// 应该用云端自己的时间戳。</para>
            /// </summary>
            internal uint Timestamp;
        }

        /// <summary>取云端统计（默认 2 秒缓存：OnGUI 一帧要跑好几趟）。</summary>
        internal static Stats GetStats(bool force = false)
        {
            if (!force && _statsTick != int.MinValue
                && (uint)(Environment.TickCount - _statsTick) < 2000u)
            {
                return _stats;
            }

            var s = new Stats
            {
                Ready = SteamCloud.Ready,
                AppEnabled = SteamCloud.AppCloudEnabled,
            };
            if (s.Ready)
            {
                List<SteamCloud.Entry> list = SteamCloud.List(SteamCloud.RemoteReplayDir + "/");
                s.Files = list.Count;
                foreach (var e in list)
                {
                    s.Bytes += e.Size;
                    if (!e.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) s.Replays++;
                }
            }

            _stats = s;
            _statsTick = Environment.TickCount;
            return s;
        }

        /// <summary>统计作废（刚写过云端后调用，让 UI 立刻反映新数字）。</summary>
        internal static void InvalidateStats()
        {
            _statsTick = int.MinValue;
            // 列表与统计是同一份云端文件的两种看法：写过云端后两者一起作废，
            // 否则会出现「状态行说 3 个回放、列表里还是 2 行」的自相矛盾。
            _listTick = int.MinValue;
        }

        private static List<CloudEntry> _listCache;
        private static int _listTick = int.MinValue;

        /// <summary>
        /// 一局回放保存成功后调用：回放本体（以及配套键盘音）写入云端缓存。
        /// 返回回放本体是否真的写进云端（供保存提示里加一句"已同步"）。
        ///
        /// 绝不抛异常——它挂在 <see cref="SpectreState.SaveFile"/> 的保存成功
        /// 路径上，任何异常都会让玩家看到"回放保存失败"，而本地文件其实已经写好了。
        /// </summary>
        internal static bool OnReplaySaved(string replayFullPath, string wavFullPath)
        {
            if (!Settings.CloudSyncOn) return false;
            try
            {
                if (!SteamCloud.Ready)
                {
                    return false;   // 非 Steam 版/离线：静默跳过，不影响本地保存
                }
                if (string.IsNullOrEmpty(replayFullPath) || !File.Exists(replayFullPath))
                {
                    return false;
                }

                string remoteReplay = RemoteNameOf(Path.GetFileName(replayFullPath));
                string err;
                if (!SteamCloud.UploadFile(remoteReplay, replayFullPath, out err))
                {
                    Debug.LogWarning("[SpectreReplay] 回放上传云端失败（" + err + "）：" + replayFullPath);
                    return false;
                }

                if (!string.IsNullOrEmpty(wavFullPath) && File.Exists(wavFullPath))
                {
                    try
                    {
                        long len = new FileInfo(wavFullPath).Length;
                        if (len <= MaxWavBytes)
                        {
                            if (!SteamCloud.UploadFile(RemoteNameOf(Path.GetFileName(wavFullPath)), wavFullPath, out err))
                            {
                                Debug.LogWarning("[SpectreReplay] 键盘音上传云端失败（" + err + "）：" + wavFullPath);
                            }
                        }
                        else
                        {
                            Debug.Log("[SpectreReplay] 键盘音过大（" + (len / 1024 / 1024) + " MB），跳过云同步：" + wavFullPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[SpectreReplay] 键盘音云同步异常（不影响本地文件）：" + ex.Message);
                    }
                }

                InvalidateStats();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 云同步异常（不影响本地保存）：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 把本地回放目录里所有**云端还没有**的回放（含同名 wav）传上去。
        /// 用于"装了 Cloud 功能之前的存量回放"一次性铺上去。
        /// </summary>
        internal static UploadResult UploadAllLocal()
        {
            var r = new UploadResult();
            try
            {
                if (!SteamCloud.Ready) { r.LastError = "not_available"; return r; }

                string dir = Settings.SavePath;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    r.LastError = "no_dir";
                    return r;
                }

                // 云端已有文件的集合（名字集合足够：回放名带时间戳，重名=同一局）
                var cloud = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in SteamCloud.List(SteamCloud.RemoteReplayDir + "/"))
                    cloud.Add(Path.GetFileName(e.Name));

                var files = new List<string>();
                foreach (string ext in ReplayManager.ReadableExtensions)
                {
                    try { files.AddRange(Directory.GetFiles(dir, "*" + ext)); }
                    catch (Exception) { }
                }

                foreach (string path in files)
                {
                    string name = Path.GetFileName(path);
                    // 配套键盘音：<无扩展名>.wav
                    string wav = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".wav");

                    if (cloud.Contains(name))
                    {
                        // 本体已经在云端，但**声音仍要单独看一眼**。这是「本体传上去了、
                        // wav 因超过 48 MB 被跳过」留下的残缺：只看本体的人永远发现不了，
                        // 而玩家换台机器就只看得见画面、听不到声音。
                        UploadWavIfMissing(wav, cloud);
                        r.Skipped++;
                        continue;
                    }

                    string err;
                    if (SteamCloud.UploadFile(RemoteNameOf(name), path, out err))
                    {
                        r.Uploaded++;
                        cloud.Add(name);
                        UploadWavIfMissing(wav, cloud);
                    }
                    else
                    {
                        r.Failed++;
                        r.LastError = err;
                    }
                }

                InvalidateStats();
                LogQuotaHint();
            }
            catch (Exception ex)
            {
                r.LastError = ex.GetType().Name;
                Debug.LogWarning("[SpectreReplay] 批量上传失败：" + ex.Message);
            }
            return r;
        }

        /// <summary>
        /// 补传一份配套键盘音（云端没有才传），超过上限就跳过并留日志。
        ///
        /// <para>调用点有两处：本体刚传完，以及本体**早已**在云端。后者是关键——
        /// 「本体传上去了、wav 因超限被跳过」是一种真实存在的残缺状态，只在传完本体
        /// 时顺手带一下的做法会永远补不回来。返回是否真的传了，方便调用方统计。</para>
        /// </summary>
        private static bool UploadWavIfMissing(string wavPath, HashSet<string> cloud)
        {
            try
            {
                if (string.IsNullOrEmpty(wavPath) || !File.Exists(wavPath)) return false;
                string wavName = Path.GetFileName(wavPath);
                if (cloud.Contains(wavName)) return false;
                if (new FileInfo(wavPath).Length > MaxWavBytes)
                {
                    Debug.Log("[SpectreReplay] 键盘音过大，跳过云同步：" + wavPath);
                    return false;
                }
                string err;
                if (!SteamCloud.UploadFile(RemoteNameOf(wavName), wavPath, out err))
                {
                    Debug.LogWarning("[SpectreReplay] 键盘音上传云端失败（" + err + "）：" + wavPath);
                    return false;
                }
                cloud.Add(wavName);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 键盘音云同步异常（不影响本地文件）：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 列出云端现有的回放，按文件名**从新到旧**排。
        ///
        /// <para>排序放在这里而不是 UI：文件名以 <c>yyyyMMddHHmmss</c> 开头，
        /// 降序就是最新在前——这是业务事实，写死一次比每处调用各排一次可靠。</para>
        ///
        /// <para>云端名字是外部数据：只接受纯文件名，且只认
        /// <see cref="ReplayManager.ReadableExtensions"/> 里的扩展名，否则配套的
        /// <c>.wav</c> 与别的杂文件都会被当成回放列出来。</para>
        ///
        /// <para>不抛异常：它跑在 OnGUI 里，异常会让 UMM 收掉整个设置面板。</para>
        /// </summary>
        internal static List<CloudEntry> ListCloudReplays()
        {
            // 2 秒缓存，与 GetStats 同一档：OnGUI 一帧要画好几趟，而
            // SteamCloud.List 是一次 P/Invoke 全量枚举 + 逐项 UTF8 封送。
            // 返回的是缓存本体，**调用方只许读、不要改**。
            if (_listCache != null
                && (uint)(Environment.TickCount - _listTick) < 2000u)
            {
                return _listCache;
            }

            var result = new List<CloudEntry>();
            if (Settings.CloudSyncOn && SteamCloud.Ready)
            {
                try
                {
                    // 重名不该出现，但云端是共享存储：真出现时只列一次。
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in SteamCloud.List(SteamCloud.RemoteReplayDir + "/"))
                    {
                        string name = Path.GetFileName(e.Name);
                        if (!IsReplayFile(name) || !seen.Add(name)) continue;
                        result.Add(new CloudEntry { Name = name, Size = e.Size, Timestamp = e.Timestamp });
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SpectreReplay] 读取云端回放列表失败：" + ex.Message);
                }
            }

            result.Sort((a, b) => string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase));
            _listCache = result;
            _listTick = Environment.TickCount;
            return result;
        }

        /// <summary>
        /// 云端文件名是否是一个纯文件名：非空、无目录成分、不是 . 或 ..、不含非法文件名字符。
        /// 云端名字是外部数据（别的机器写的/手改过的），这里是唯一的防线。
        ///
        /// <para><b>为什么要单独判 <c>.</c> 与 <c>..</c>：</b><c>Path.GetFileName("..")</c>
        /// 返回 <c>".."</c>、<c>Path.GetFileName(".")</c> 返回 <c>"."</c>，两者都等于自身，
        /// 光靠「取到的文件名与入参相等」这条根本挡不住。放过去的话
        /// <see cref="RemoteNameOf"/> 会拼出 <c>.../Replays/..</c>，
        /// 于是「拒绝任何路径成分」的承诺是假的。</para>
        /// </summary>
        private static bool IsPureFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name == "." || name == "..") return false;
            if (name != Path.GetFileName(name)) return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            return true;
        }

        /// <summary>
        /// 云端文件名是否是一份回放：纯文件名 + 可读扩展名。
        ///
        /// 与 <see cref="LoadFromCloud"/> 里的校验同源——列表在这儿把不可播的挡掉，
        /// 那边再挡一次，因为播放请求可能来自缓存下来的旧列表。
        ///
        /// <para>入参在 <see cref="ListCloudReplays"/> 里已经过 <see cref="Path.GetFileName(System.String)"/>，
        /// 本来就该是纯文件名；这里用 <see cref="IsPureFileName"/> 而不是再写一遍
        /// 相等判断，是为了让 <c>"."</c> / <c>".."</c> 这类边界只维护一份。</para>
        /// </summary>
        private static bool IsReplayFile(string name)
        {
            if (!IsPureFileName(name)) return false;
            foreach (string ext in ReplayManager.ReadableExtensions)
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// 只读云端那一份，**不装载**。
        ///
        /// <para>和 <see cref="LoadFromCloud"/> 的区别是它没有任何全局副作用：不调
        /// <c>LoadReplayFromMemory</c>、不碰 <c>SpectreState</c>、也不去下载配套的
        /// wav。用途只有一个——列表里展开一份云端回放的详情。</para>
        ///
        /// <para>详情面板要显示歌曲、判定数这些只有解码后才知道的字段，而云端列表
        /// 本身只带文件名、大小、时间。为此把正在播的那份顶掉，或者先在磁盘上写一份
        /// 再读，都是不该有的副作用：前者会让玩家「看一眼详情」就丢了当前回放，
        /// 后者会把只存在于云端的文件弄成本地已有，<see cref="RestoreMissing"/>
        /// 之后就不再补它了。</para>
        /// </summary>
        /// <param name="replayData">解码结果；失败时为 null。</param>
        /// <param name="remoteFileName">云端文件名（不含目录）。会做纯文件名校验。</param>
        /// <param name="error">失败原因；成功时为 null。</param>
        internal static bool PeekReplay(out ReplayData replayData, string remoteFileName, out string error)
        {
            replayData = null;
            error = null;

            if (!Settings.CloudSyncOn)
            {
                error = "cloud_off";
                return false;
            }
            if (!SteamCloud.Ready)
            {
                error = "not_available";
                return false;
            }

            // 与 LoadFromCloud 同一条底线：云端名字是外部数据，不接受任何路径成分。
            if (!IsPureFileName(remoteFileName))
            {
                error = "bad_name";
                return false;
            }

            byte[] raw;
            if (!SteamCloud.Download(RemoteNameOf(remoteFileName), out raw, out error))
            {
                return false;
            }

            // ReadReplayData 只做解密/解码，不动全局状态。它返回的 name 只用于日志，
            // 这里传云端文件名，出问题时的日志才对得上列表里那一行。
            if (!ReplayManager.ReadReplayData(out ReplayData meta, raw, remoteFileName))
            {
                error = "decode_failed";
                return false;
            }

            replayData = meta;
            return true;
        }

        /// <summary>
        /// 直接从云端装载一局回放并立即切换过去——**不落地任何文件**。
        ///
        /// <para>与 <see cref="RestoreMissing"/> 的区别是意图：那个是「把文件补回磁盘」，
        /// 这个是「我要现在看这一局」。后者不该为了播放而在玩家存档目录里留下副本，
        /// 否则云回放看得越多，本地目录越脏，且下次 <see cref="RestoreMissing"/> 会
        /// 因为「本地已有」而跳过真正该补的文件。</para>
        ///
        /// <para>失败不抛异常：调用方是 UI 点击，错误用 <paramref name="error"/> 带回去显示。</para>
        ///
        /// <para><b>键盘音同样校验。</b>云端那份是外部数据——可能是别的机器上传的、
        /// 被截断的、或旧版本写的。不校验的话坏音频会一路播成噪音，而那时没人会
        /// 想到是哈希不对。</para>
        ///
        /// <para><b>与 <see cref="PeekReplay"/> 的分工：</b>这里会装载、会改全局状态；
        /// 只想看详情就别用这个，否则「展开一行」等于把玩家正在看的回放顶掉。</para>
        /// </summary>
        /// <param name="replayData">装载结果；失败时为默认值。</param>
        /// <param name="remoteFileName">云端文件名（不含目录）。会做纯文件名校验。</param>
        /// <param name="error">失败原因；成功时为 null。</param>
        /// <param name="keybdSoundLost">
        /// 成功时：这局本来带键盘音，但最终没能播出来（云端缺文件、超限、
        /// 或哈希对不上）。画面照放，UI 需要就这一点说一句话——否则玩家
        /// 只会觉得「怎么没声音」，而原因在云端。
        /// </param>
        internal static bool LoadFromCloud(out ReplayData replayData, string remoteFileName,
            out string error, out bool keybdSoundLost)
        {
            replayData = new ReplayData();
            error = null;
            keybdSoundLost = false;

            if (!Settings.CloudSyncOn)
            {
                error = "cloud_off";
                return false;
            }
            if (!SteamCloud.Ready)
            {
                error = "not_available";
                return false;
            }

            // 云端名字是外部数据：只接受纯文件名，拒绝任何路径成分
            // （与 RestoreMissing 同一条底线，否则 "../../xxx" 能越出目录）。
            if (!IsPureFileName(remoteFileName))
            {
                error = "bad_name";
                return false;
            }
            string name = remoteFileName;

            byte[] raw;
            if (!SteamCloud.Download(RemoteNameOf(name), out raw, out error))
            {
                return false;
            }

            // 先只解码、不产生任何全局副作用：拿到 KeybdAudio / KeybdSoundFileName
            // 才知道该不该去下载 wav。这一步失败就直接退出，不会留下半装载状态。
            ReplayData meta;
            if (!ReplayManager.ReadReplayData(out meta, raw, name))
            {
                error = "decode_failed";
                return false;
            }

            byte[] wav = null;
            // 音频已经嵌在回放体内时，云端**没有也不需要**同名 .wav。
            // 照旧去下载只会白跑一次 P/Invoke（Steam 那边还会记一条「文件不存在」），
            // 再在下面打一条误导性的 warning——而这份回放其实一点都不缺声音。
            if (meta.KeybdAudio.HasData)
            {
                Debug.Log("[SpectreReplay] 键盘音来自回放体内，跳过 wav 下载：" + name);
            }
            else if (meta.KeybdSoundFileName is string wavName && !string.IsNullOrEmpty(wavName))
            {
                // 与主回放同一个判据：`".."` / `"."` 能穿过「与自身相等」那种写法，
                // 这里必须用 IsPureFileName 才能真的挡住。
                if (IsPureFileName(wavName))
                {
                    byte[] wavData;
                    string wavErr;
                    if (SteamCloud.Download(RemoteNameOf(wavName), out wavData, out wavErr))
                    {
                        if (wavData != null && wavData.Length <= MaxWavBytes)
                        {
                            wav = wavData;
                        }
                        else
                        {
                            Debug.LogWarning("[SpectreReplay] 云端键盘音过大或为空，跳过播放：" + wavName);
                        }
                    }
                    else
                    {
                        // wav 缺失不是致命错误：画面照放，只是没声音。
                        Debug.LogWarning("[SpectreReplay] 云端键盘音缺失（" + wavErr + "）：" + wavName);
                    }
                }
                else
                {
                    Debug.LogWarning("[SpectreReplay] 云端回放记录的键盘音文件名不合法，已忽略：" + wavName);
                }
            }

            if (!ReplayManager.LoadReplayFromMemory(out replayData, raw, name, wav))
            {
                error = "load_failed";
                return false;
            }

            // 「本该有键盘音、最后却没有」才算丢失。用 HasKeybdSound 而不是
            // wav != null：引擎还会校验哈希，wav 下回来了但和元数据对不上时
            // 同样等于没声音，而那种情况不看这里根本不会有人发现。
            //
            // 「本该有」有两种来源：元数据记了 wav 文件名（旧格式，靠云端那份 .wav），
            // 或者音频已经嵌在回放体内（新格式，不需要任何额外文件）。后者若仍按
            // 「没 wav 就算丢」来判，会给嵌入型回放报一条假提示——它的声音一直都在。
            // 而嵌入型一旦真的解不出声音（HasKeybdSound == false），那是回放体本身
            // 出了问题，不是「云端缺 wav」，所以也不归到这条提示上。
            bool expectedKeybdSound = !string.IsNullOrEmpty(meta.KeybdSoundFileName)
                || meta.KeybdAudio.HasData;
            keybdSoundLost = expectedKeybdSound
                && !meta.KeybdAudio.HasData
                && !SpectreState.HasKeybdSound;
            return true;
        }

        /// <summary>
        /// 把云端的**一份**回放（连配套键盘音）下载到本地保存目录。
        ///
        /// <para>与 <see cref="RestoreMissing"/> 是同一件事的两个粒度：那个是「把丢的
        /// 都补回来」，这个是「我就要这一份」。**同样不覆盖本地已有文件**——本地那份是
        /// 玩家实际在用的，云端那份可能来自另一台机器；真要覆盖就先删本地再下。</para>
        ///
        /// <para>回放本体落地之后，键盘音失败只记日志、不算整次下载失败：画面能看，
        /// 只是没声音，这与 <see cref="LoadFromCloud"/> 对同一件事的处理保持一致。</para>
        /// </summary>
        internal static bool DownloadOne(string remoteName, out string error, out bool alreadyLocal)
        {
            error = null;
            alreadyLocal = false;
            try
            {
                if (!SteamCloud.Ready) { error = "not_available"; return false; }

                // 云端名字是外部数据：只接受纯文件名，拒绝任何路径成分
                if (!IsPureFileName(remoteName)) { error = "bad_name"; return false; }
                string name = remoteName;

                string dir = Settings.SavePath;
                if (string.IsNullOrEmpty(dir)) { error = "no_dir"; return false; }
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string localPath = Path.Combine(dir, name);
                string wavName = Path.GetFileNameWithoutExtension(name) + ".wav";
                string localWav = Path.Combine(dir, wavName);

                // 本体与键盘音**分开判断**：本地有本体、没有 wav 是很常见的一种残缺
                // （换机时只拷了回放文件，或早期版本留下的 wav 被手动删掉）。若在这里
                // 因为「本体已存在」就整体返回，玩家点多少次下载都补不回那段声音。
                bool bodyThere = File.Exists(localPath);
                if (!bodyThere && !DownloadTo(name, localPath, out error)) return false;

                bool wavFetched = false;
                try
                {
                    if (!File.Exists(localWav) && SteamCloud.Exists(RemoteNameOf(wavName)))
                    {
                        if (DownloadTo(wavName, localWav, out string wavErr)) wavFetched = true;
                        else Debug.LogWarning("[SpectreReplay] 云端键盘音下载失败（" + wavErr + "）：" + wavName);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SpectreReplay] 云端键盘音下载异常：" + ex.Message);
                }

                // 「本地已有」= 本体本来就在、这次也没补回声音。只补回键盘音也是一次
                // 真实的下载，不能报成「已存在」，否则玩家不知道声音其实已经修好了。
                bool changed = !bodyThere || wavFetched;
                alreadyLocal = !changed;

                if (changed)
                {
                    // 列表缓存要作废，否则刚下回来的文件在界面上要等一拍才出现
                    Pages.Pages.InvalidateReplayList();
                    InvalidateStats();
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
                Debug.LogWarning("[SpectreReplay] 云端单份下载失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>把云端一个文件写进本地路径（单份下载的公共半步）。</summary>
        private static bool DownloadTo(string remoteFileName, string localPath, out string error)
        {
            if (!SteamCloud.Download(RemoteNameOf(remoteFileName), out byte[] data, out error))
            {
                return false;
            }
            File.WriteAllBytes(localPath, data ?? new byte[0]);
            return true;
        }

        /// <summary>
        /// 从云端删掉**一份**回放（连配套键盘音）。
        ///
        /// <para>**只动云端，本地文件原样保留。** 这条边界必须守住：玩家点「删除」
        /// 的地方就在云端列表里，若顺手把本地那份也删了，等于用一个云操作按钮
        /// 做了不可逆的本地破坏，而它旁边就有一颗语义明确的本地删除。</para>
        ///
        /// <para>配套 wav 按「回放名去扩展名 + .wav」派生（见 SpectreState 里
        /// <c>KeybdSoundFileName = FileName + ".wav"</c>）。云端没有这份 wav 不算失败：
        /// 旧回放本来就可能没录键盘音，或者上传时因超限被跳过。</para>
        /// </summary>
        internal static bool DeleteOne(string remoteName, out string error, out bool wavDeleted)
        {
            error = null;
            wavDeleted = false;
            try
            {
                if (!SteamCloud.Ready) { error = "not_available"; return false; }

                // 云端名字是外部数据：只接受纯文件名，拒绝任何路径成分
                if (!IsPureFileName(remoteName)) { error = "bad_name"; return false; }
                string name = remoteName;
                if (!SteamCloud.Exists(RemoteNameOf(name))) { error = "not_found"; return false; }

                if (!SteamCloud.Delete(RemoteNameOf(name), out error)) return false;

                try
                {
                    string wavName = Path.GetFileNameWithoutExtension(name) + ".wav";
                    if (SteamCloud.Exists(RemoteNameOf(wavName)))
                    {
                        if (SteamCloud.Delete(RemoteNameOf(wavName), out string wavErr)) wavDeleted = true;
                        else Debug.LogWarning("[SpectreReplay] 云端键盘音删除失败（" + wavErr + "）：" + wavName);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SpectreReplay] 云端键盘音删除异常：" + ex.Message);
                }

                InvalidateStats();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
                Debug.LogWarning("[SpectreReplay] 云端单份删除失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 把本地**一份**回放（连配套键盘音）传到云端。
        ///
        /// <para>与 <see cref="UploadAllLocal"/> 的关系只是粒度：那个是「存量一次性铺上去」，
        /// 这个是「我刚录的这一份」。云上已有同名文件时返回 <paramref name="alreadyCloud"/>，
        /// **不覆盖**——与批量上传同一条判据：云端内容以先到者为准，模组不做强制覆盖同步。</para>
        /// </summary>
        internal static bool UploadOne(string localPath, out string error, out bool alreadyCloud)
        {
            error = null;
            alreadyCloud = false;
            try
            {
                if (!SteamCloud.Ready) { error = "not_available"; return false; }
                if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) { error = "not_found"; return false; }

                string name = Path.GetFileName(localPath);
                bool bodyThere = SteamCloud.Exists(RemoteNameOf(name));
                if (!bodyThere && !SteamCloud.UploadFile(RemoteNameOf(name), localPath, out error)) return false;

                // 配套键盘音：<无扩展名>.wav，超限就跳过（与保存时同一条规则）。
                // **本体已在云端也要查这一步**：否则「本体传上去了、wav 因超限被跳过」
                // 这种残缺永远补不上——玩家在另一台机器上就只看得见画面、听不到声音。
                bool wavUploaded = false;
                try
                {
                    string wavPath = Path.Combine(
                        Path.GetDirectoryName(localPath) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(name) + ".wav");
                    string wavRemote = RemoteNameOf(Path.GetFileName(wavPath));
                    if (File.Exists(wavPath) && !SteamCloud.Exists(wavRemote))
                    {
                        long len = new FileInfo(wavPath).Length;
                        if (len <= MaxWavBytes)
                        {
                            if (SteamCloud.UploadFile(wavRemote, wavPath, out string wavErr)) wavUploaded = true;
                            else Debug.LogWarning("[SpectreReplay] 键盘音上传云端失败（" + wavErr + "）：" + wavPath);
                        }
                        else
                        {
                            Debug.Log("[SpectreReplay] 键盘音过大（" + (len / 1024 / 1024) + " MB），跳过云同步：" + wavPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SpectreReplay] 键盘音云同步异常（不影响本地文件）：" + ex.Message);
                }

                // 什么都没传才算「云端已有」；只补传了键盘音也要如实说上传过
                bool changed = !bodyThere || wavUploaded;
                alreadyCloud = !changed;
                if (changed)
                {
                    InvalidateStats();
                    LogQuotaHint();
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
                Debug.LogWarning("[SpectreReplay] 云端单份上传失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 把云端有、本地没有的回放（含 wav）写回本地保存目录。
        ///
        /// **只回填缺失的，绝不覆盖本地已有文件**：本地那份是玩家实际在用的，
        /// 而云端那份可能是另一台机器上的旧版本。回填目标是"文件没丢"，
        /// 不是"以云端为准做同步"——后者在没有冲突 UI 的情况下会静默吃掉本地进度。
        /// </summary>
        internal static RestoreResult RestoreMissing()
        {
            var r = new RestoreResult();
            try
            {
                if (!SteamCloud.Ready) { r.LastError = "not_available"; return r; }

                string dir = Settings.SavePath;
                if (string.IsNullOrEmpty(dir))
                {
                    r.LastError = "no_dir";
                    return r;
                }
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var list = SteamCloud.List(SteamCloud.RemoteReplayDir + "/");
                foreach (var e in list)
                {
                    string name = Path.GetFileName(e.Name);
                    // 云端名字是外部数据：只接受纯文件名，拒绝任何路径成分。
                    // 这里用与 LoadFromCloud / DownloadOne / DeleteOne 同一个判据（能挡 "." / ".."）；
                    // 再叠一条「取最后一个 / 之后的部分必须与 basename 相等」是**故意保留**的：
                    // Windows 上 Path.GetFileName 会把带反斜杠的名字截成 basename，只靠
                    // IsPureFileName 会把这类畸形条目悄悄改名落盘；严格相等则跳过它们。
                    if (!IsPureFileName(name)
                        || name != e.Name.Substring(e.Name.LastIndexOf('/') + 1))
                    {
                        continue;
                    }

                    string localPath = Path.Combine(dir, name);
                    if (File.Exists(localPath)) continue;   // 本地已有：不覆盖

                    byte[] data;
                    string err;
                    if (!SteamCloud.Download(e.Name, out data, out err))
                    {
                        r.Failed++;
                        r.LastError = err;
                        continue;
                    }

                    try
                    {
                        File.WriteAllBytes(localPath, data ?? new byte[0]);
                        r.Restored++;
                    }
                    catch (Exception ex)
                    {
                        r.Failed++;
                        r.LastError = ex.GetType().Name;
                        Debug.LogWarning("[SpectreReplay] 云回放写回本地失败：" + localPath + " " + ex.Message);
                    }
                }

                if (r.Restored > 0)
                {
                    // 回放列表缓存要作废，否则新回填的文件在界面上要等一秒才出现
                    Pages.Pages.InvalidateReplayList();
                    InvalidateStats();
                }
            }
            catch (Exception ex)
            {
                r.LastError = ex.GetType().Name;
                Debug.LogWarning("[SpectreReplay] 云端恢复失败：" + ex.Message);
            }
            return r;
        }

        /// <summary>
        /// 配置文件（Configs.json）写盘成功之后调用：把这份配置写进云端缓存。
        ///
        /// <para>三件事依次发生：① 内容与上次传上去的逐字节相同 → 直接返回，连
        /// P/Invoke 都不打（设置页连拨两个开关、关窗与退出各写一次都会撞上）；
        /// ② 距上次尝试不足 5 秒 → 只记「待传」，由补传协程在窗口结束时收尾；
        /// ③ 其余情况立即上传。</para>
        ///
        /// <para><b>绝不抛异常，也绝不把失败传回去。</b>调用点在
        /// <see cref="ConfigManager.SaveConfigs"/> 的成功路径上：那里一旦抛出，玩家会
        /// 看到「设置没存上」，而本地文件其实已经写好了；返回值只是给排查用的，
        /// 保存方不该因为云同步失败而改口说本地保存失败。</para>
        /// </summary>
        /// <returns>这份内容是否已经（刚才或此前）写进云端缓存。</returns>
        internal static bool OnConfigSaved(string localPath)
        {
            if (!Settings.CloudSyncOn) return false;
            try
            {
                if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) return false;

                // SteamAPI 与 Unity 协程都只该在主线程碰。SpectreAPI.SaveSettings 是
                // 对外 API，别的 mod 完全可能从后台线程调；这里转一手再干活，
                // 调用方立刻拿到返回（不为了云同步去阻塞别人的线程）。
                if (!MainThread.IsMainThread)
                {
                    MainThread.Run(() => OnConfigSaved(localPath));
                    return true;
                }

                if (!SteamCloud.Ready) return false;   // 非 Steam 版/离线：静默跳过

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(localPath);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SpectreReplay] 读取配置文件失败，跳过云同步（不影响本地保存）：" + ex.Message);
                    return false;
                }
                return UploadConfigBytes(localPath, bytes);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 配置文件云同步异常（不影响设置保存）：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 退出前把云同步整个失能（<see cref="Main.OnQuitting"/> 在 FlushConfig 之前调用）。
        ///
        /// <para>**为什么退出路径不能再碰 Steam。** 退出时序里游戏自己的
        /// <c>SteamClient.Shutdown()</c>（scrController.OnApplicationQuit）跑在
        /// Application.quitting 事件之前——编辑器 ExitPlayMode 的崩溃栈实证：
        /// <c>Internal_ApplicationQuit → OnQuitting</c> 执行时 Steam 上下文已销毁。
        /// FlushConfig → SaveConfigs → OnConfigSaved 若在此时上传，会拿悬空的
        /// RemoteStorage 句柄调 FileWrite，原生访问违例崩掉整个进程，
        /// 托管 try/catch 拦不住（Editor.log 里只有原生栈、没有 Managed Stacktrace）。
        /// 正常 Steam 版同样存在这个窗口，只是退出时配置内容通常与运行中最后
        /// 一次上传相同、被指纹去重短路遮住了。</para>
        ///
        /// <para>退出瞬间写进云缓存的文件也来不及上传：Steam 客户端在进程退出后
        /// 才批量同步本地缓存。砍掉这次上传的代价是配置备份最多滞后一个会话——
        /// 下次会话 <c>_configUploaded</c> 从 null 起步，第一次写盘就全量补传。</para>
        /// </summary>
        internal static void PrepareForExit()
        {
            SteamCloud.Disable("quitting");
        }

        /// <summary>
        /// 现在允许上传配置吗。
        ///
        /// <para><b>为什么要有这道闸。</b>启动时有一次「从云端恢复配置」。在它给出
        /// 结论之前上传，就可能把云端那份还没读过的备份覆盖成本机的近默认配置：
        /// 触发条件是「本地配置缺失（换机）＋ Steam 比设置页晚就绪 ＋ 玩家先改了
        /// 一项」——那一下 SaveConfigs 会先让本地文件存在，恢复流程随后看到文件
        /// 已存在就永久跳过，云备份就没了。</para>
        ///
        /// <para>90 秒兜底：万一恢复流程因异常没跑起来，也不能让云备份整个会话
        /// 都不更新。</para>
        /// </summary>
        private static bool ConfigUploadAllowedNow()
        {
            if (_configRestoreSettled) return true;
            // 不恢复的配置下没有「还没读过的备份」可言，直接放行。
            if (!Settings.CloudSyncOn || !Settings.CloudRestoreOnLoad) return true;
            if (Time.realtimeSinceStartup > 90f) return true;
            Debug.Log("[SpectreReplay] 云端配置恢复尚未完成，暂不上传（避免覆盖还没读过的备份）");
            return false;
        }

        /// <summary>
        /// 按「内容指纹 + 节流窗口」决定现在传、稍后传还是不传。
        /// <paramref name="bytes"/> 为 null 时表示手头没有内容（退化到让
        /// <see cref="SteamCloud.UploadFile"/> 自己读盘）。
        /// </summary>
        private static bool UploadConfigBytes(string localPath, byte[] bytes)
        {
            if (!ConfigUploadAllowedNow()) return false;

            // ① 内容没变：整段跳过。设置页的「读-改-写」里，同一份内容被写回
            //    好几次是常态（拨开关后在别的页翻一下、关窗、退出各写一次）。
            if (bytes != null && _configUploaded != null && BytesEqual(bytes, _configUploaded))
            {
                return true;
            }

            // ② 节流：窗口内只记待传，由补传协程在窗口过去后补上最后一份。
            //    _configUploaded == null 表示这还是本次会话第一次上传，放行。
            if (_configUploaded != null
                && (uint)(Environment.TickCount - _configUploadTick) < (uint)ConfigUploadIntervalMs)
            {
                _configPending = true;
                StartConfigUploadPump();
                return false;
            }

            return UploadConfigNow(localPath, bytes);
        }

        /// <summary>
        /// 手动把当前配置上传到云端（玩家点界面上那颗按钮）。
        ///
        /// <para><b>为什么手动这条要绕开自动那条的三道关。</b>去重、节流、「启动恢复
        /// 还没落定」都是为了拦住**自动行为**误伤；一个人明确点了「上传」，这三道
        /// 关就都成了阻力：点了没反应比点了覆盖更难理解。</para>
        /// </summary>
        internal static bool UploadConfigManual(out string error)
        {
            error = null;
            if (!Settings.CloudSyncOn) { error = "cloud_off"; return false; }
            if (!SteamCloud.Ready) { error = "not_available"; return false; }

            string localPath = LocalConfigPath();
            if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
            {
                error = "no_config";
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(localPath);
            }
            catch (Exception ex)
            {
                error = "read_failed";
                Debug.LogWarning("[SpectreReplay] 读取本地配置失败，无法手动上传：" + ex.Message);
                return false;
            }
            if (bytes == null || bytes.Length == 0)
            {
                error = "empty_config";
                return false;
            }

            if (!SteamCloud.Upload(RemoteConfigName, bytes, out string err))
            {
                error = err ?? "upload_failed";
                Debug.LogWarning("[SpectreReplay] 手动上传配置失败（" + error + "）：" + localPath);
                return false;
            }

            // 记成「已上传」：否则紧接着的任何一次自动保存都会把同一份内容再推一遍。
            _configUploaded = bytes;
            _configUploadTick = Environment.TickCount;
            // 玩家已经明确选择「以本机这份为准」，启动恢复的闸门没必要再挡自动备份。
            _configRestoreSettled = true;
            // 手动这份比任何挂起的自动补传都新，清掉待传避免它反把旧内容推上去。
            _configPending = false;
            Debug.Log("[SpectreReplay] 已手动上传配置到 Steam 云（" + bytes.Length + " 字节）");
            return true;
        }

        /// <summary>
        /// 手动从云端取回配置，**覆盖本地**。
        ///
        /// <para><b>覆盖前先留一份本地备份</b>（同名 + .bak）：这是整个模组里少数
        /// 会主动推翻本地文件的地方，玩家点错一次就得能救回来。</para>
        /// </summary>
        internal static bool RestoreConfigManual(out string error)
        {
            error = null;
            if (!Settings.CloudSyncOn) { error = "cloud_off"; return false; }
            if (!SteamCloud.Ready) { error = "not_available"; return false; }

            string localPath = LocalConfigPath();
            if (string.IsNullOrEmpty(localPath))
            {
                error = "no_config_path";
                return false;
            }
            if (!SteamCloud.Exists(RemoteConfigName))
            {
                error = "not_found";
                return false;
            }

            if (!SteamCloud.Download(RemoteConfigName, out byte[] data, out string err)
                || data == null || data.Length == 0)
            {
                error = err ?? "download_failed";
                Debug.LogWarning("[SpectreReplay] 手动取回配置失败（" + error + "）");
                return false;
            }

            if (File.Exists(localPath))
            {
                try
                {
                    File.Copy(localPath, localPath + ".bak", true);
                    Debug.Log("[SpectreReplay] 已备份本地配置到 " + localPath + ".bak");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SpectreReplay] 备份本地配置失败（继续覆盖）：" + ex.Message);
                }
            }

            try
            {
                File.WriteAllBytes(localPath, data);
            }
            catch (Exception ex)
            {
                error = "write_failed";
                Debug.LogWarning("[SpectreReplay] 写入云端配置失败：" + ex.Message);
                return false;
            }

            // 本地与云端此刻一致：既不算「待上传」，也别让启动恢复再来一次。
            _configUploaded = data;
            _configRestoreSettled = true;
            _configPending = false;

            ApplyRestoredConfig(localPath);
            Debug.Log("[SpectreReplay] 已从 Steam 云手动取回配置（" + data.Length + " 字节）");
            return true;
        }

        /// <summary>
        /// 云端配置备份的元信息（有没有 / 多大 / 什么时候），给界面显示用。
        /// 任何一步失败都只返回 false，不抛异常。
        /// </summary>
        internal static bool TryGetConfigBackupInfo(out int size, out uint timestampUnix)
        {
            size = 0;
            timestampUnix = 0;
            if (!SteamCloud.Ready) return false;
            try
            {
                List<SteamCloud.Entry> list = SteamCloud.List(RemoteConfigName);
                if (list == null) return false;
                foreach (var e in list)
                {
                    // List 是前缀匹配，云端若存在 Configs.json.bak 之类会一并回来，这里只认全等。
                    if (!string.Equals(e.Name, RemoteConfigName, StringComparison.Ordinal)) continue;
                    size = e.Size;
                    timestampUnix = e.Timestamp;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 读取云端配置信息失败：" + ex.Message);
            }
            return false;
        }

        /// <summary>真正打一次云端写入（调用方已判过开关、就绪与节流）。</summary>
        private static bool UploadConfigNow(string localPath, byte[] bytes)
        {
            // 唯一的写入点，闸门放在这里才是真的拦住所有路径（补传协程也走这里）。
            if (!ConfigUploadAllowedNow()) return false;
            // 先盖时间戳再动手：失败（比如云端配额满、名字被拒）时也不该被
            // 下一次写盘立刻重试——那等于把节流绕过去了。
            _configUploadTick = Environment.TickCount;
            string err;
            try
            {
                if (bytes != null)
                {
                    // 直接用刚写下去的那串字节，不再读一次盘：中间若又被写了一次，
                    // 读盘会拿到更新的内容，而时间戳已经记在这次上传上，容易对不上。
                    if (!SteamCloud.Upload(RemoteConfigName, bytes, out err))
                    {
                        Debug.LogWarning("[SpectreReplay] 配置上传云端失败（" + err + "）：" + localPath);
                        return false;
                    }
                }
                else if (!SteamCloud.UploadFile(RemoteConfigName, localPath, out err))
                {
                    Debug.LogWarning("[SpectreReplay] 配置上传云端失败（" + err + "）：" + localPath);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 配置上传云端异常（不影响设置保存）：" + ex.Message);
                return false;
            }

            _configUploaded = bytes;
            Debug.Log("[SpectreReplay] 配置已写入 Steam 云缓存（退出游戏时由客户端上传）："
                + (bytes != null ? bytes.Length : 0) + " 字节");
            return true;
        }

        /// <summary>
        /// 起一个短协程，在节流窗口过去后把「最后一次」配置补传上去。
        ///
        /// <para>没有这一步，节流就等于「丢掉窗口内的最后一次改动」：玩家连拨两个
        /// 开关，第二下被跳过后可能再没有新的写盘（设置页就这么关着），云端于是
        /// 永远停在第一个开关的状态——而这正是玩家换机器后会发现的差异。</para>
        ///
        /// <para>泵没装（极早期 / 关模组之后）时没有推迟能力，直接放弃这一次：
        /// 下一次写盘会重新走一遍，不会永久丢；这条路径是正常的，不打日志。</para>
        /// </summary>
        private static void StartConfigUploadPump()
        {
            // 自愈：关模组会把 MainThread 的泵整体销毁，正在等的协程的 finally
            // **不保证**跑到（Unity 停协程不一定 Dispose 迭代器）。这时
            // _configPumpActive 会永久卡在 true，之后每一次被节流的补传都不会再
            // 被安排。超过「窗口 + 3 秒余量」还挂着「在跑」，就当它已经死了；
            // 真有两条同时活着也无害——它们都在主线程上跑，先跑的那条清掉
            // _configPending 并上传，后跑的那条看到它已为 false 会立刻退出。
            if (_configPumpActive
                && (uint)(Environment.TickCount - _configPumpStartTick)
                    < (uint)(ConfigUploadIntervalMs + 3000))
            {
                return;
            }
            _configPumpActive = true;
            _configPumpStartTick = Environment.TickCount;
            try
            {
                MainThread.Run(() =>
                {
                    if (MainThread.StartCoroutine(UploadConfigWhenAllowed()) == null)
                    {
                        _configPumpActive = false;
                        _configPending = false;
                    }
                });
            }
            catch (Exception ex)
            {
                _configPumpActive = false;
                _configPending = false;
                Debug.LogWarning("[SpectreReplay] 配置延迟上传安排失败（不影响设置保存）：" + ex.Message);
            }
        }

        /// <summary>节流窗口一过就补传最后一份配置（只在 <see cref="_configPending"/> 时跑）。</summary>
        private static System.Collections.IEnumerator UploadConfigWhenAllowed()
        {
            try
            {
                while (_configPending)
                {
                    uint elapsed = (uint)(Environment.TickCount - _configUploadTick);
                    if (elapsed >= (uint)ConfigUploadIntervalMs) break;
                    float wait = (ConfigUploadIntervalMs - elapsed) / 1000f;
                    // WaitForSecondsRealtime 不受 timeScale 影响：设置页可能是暂停的
                    yield return new WaitForSecondsRealtime(wait < 0.25f ? 0.25f : wait);
                }

                if (!_configPending) yield break;
                _configPending = false;

                // 窗口期内开关可能被关掉、Steam 也可能掉线：那就什么都不做，
                // 下一次写盘会重新走完整判据。
                if (!Settings.CloudSyncOn || !SteamCloud.Ready) yield break;

                string path = LocalConfigPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) yield break;

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SpectreReplay] 读取配置文件失败，放弃这次延迟上传：" + ex.Message);
                    yield break;
                }

                // 窗口期内玩家可能又把开关拨回去了（内容回到上次传上去的那份）：
                // 这时补传是纯粹的重复写，按同一份指纹判据跳过。
                if (_configUploaded != null && BytesEqual(bytes, _configUploaded)) yield break;

                UploadConfigNow(path, bytes);
            }
            finally
            {
                _configPumpActive = false;
            }
        }

        /// <summary>
        /// 启动时把云端那份 Configs.json 取回本地——**只补「本地没有」这一种情况**。
        ///
        /// <para>与 <see cref="RestoreMissing"/> 同一条底线：本地那份是玩家正在用的，
        /// 云端那份可能来自另一台机器（甚至是同账号下别的存档环境同步上来的旧配置），
        /// 自动覆盖等于静默吃掉玩家刚调好的设置。所以本地文件存在就什么都不做。</para>
        ///
        /// <para><b>取回后必须同时装进内存。</b>只写盘不生效的话，会被退出时的自动落盘
        /// 立刻盖掉：Main.OnQuitting → FlushConfig 无条件把内存设置写回同一路径，而
        /// 内存里此刻还是「文件缺失 → 默认值」那一份。结果不只是找回白做，还会顺着
        /// 上传把云端那份备份也改成默认值。所以写盘成功后走
        /// <see cref="ApplyRestoredConfig"/>（载入 → 归一化 → 刷新补丁开关），
        /// 与设置页保存后的收尾同一条管线。</para>
        ///
        /// <para>改内存的安全前提：**「本地缺失」本身就证明本次会话还没写过盘**——
        /// 玩家任何一次设置改动都会先落盘，文件存在就走不到这里，因此不存在
        /// 「覆盖掉玩家刚改的东西」。</para>
        ///
        /// <para>绝不抛异常：它挂在启动路径上，没网/Steam 未就绪都是常见情况。</para>
        /// </summary>
        /// <returns>是否真的从云端取回并写回了本地。</returns>
        internal static bool RestoreConfigIfMissing()
        {
            try
            {
                if (!SteamCloud.Ready) return false;

                // 一踏进决策逻辑就落定：这次会话要不要从云端恢复配置，结论只有一份。
                // 上传钩子等的就是它（见 ConfigUploadAllowedNow），所以必须在任何
                // 「会不会恢复」的分支之前置位，包括本地已存在这种「不恢复」。
                _configRestoreSettled = true;

                string localPath = LocalConfigPath();
                if (string.IsNullOrEmpty(localPath))
                {
                    Debug.Log("[SpectreReplay] 云端配置取回跳过：拿不到本地配置路径");
                    return false;
                }
                if (File.Exists(localPath)) return false;   // 本地已有：绝不覆盖

                if (!SteamCloud.Exists(RemoteConfigName))
                {
                    Debug.Log("[SpectreReplay] 本地配置缺失，云端也没有 Configs.json（本次用默认设置）");
                    return false;
                }

                byte[] data;
                string err;
                if (!SteamCloud.Download(RemoteConfigName, out data, out err))
                {
                    Debug.LogWarning("[SpectreReplay] 云端配置下载失败（" + err + "）：" + localPath);
                    return false;
                }
                if (data == null || data.Length == 0)
                {
                    Debug.LogWarning("[SpectreReplay] 云端配置为空，已忽略：" + RemoteConfigName);
                    return false;
                }

                string dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(localPath, data);

                // 本地这份现在与云端逐字节一致：记下来，免得紧接着的一次写盘
                // 把同一份内容再往云端重复写一遍。
                _configUploaded = data;

                // **只写盘不生效的话，这次找回会在一分钟后被自己抹掉。**
                // 内存里此刻还是「配置缺失 → 默认值」那一份，而退出时
                // Main.OnQuitting → FlushConfig 会**无条件**把内存设置重写回同一路径；
                // 于是玩家一退游戏，刚找回的配置就被默认值盖掉（还会顺着上传把云端
                // 那份也改成默认值）。所以这里走与设置页保存完全相同的收尾管线
                // （见 Pages.SaveNow：载入/归一化/刷新补丁开关）。
                //
                // 时机是安全的：Steam 就绪通常只要一两秒，那时还在主菜单；而「本地配置
                // 缺失」本身就意味着本次会话还没写过盘（任何一次设置改动都会先落盘，
                // 文件存在就不会走到这里），所以不存在「覆盖玩家刚改的东西」。
                ApplyRestoredConfig(localPath);

                Debug.Log("[SpectreReplay] 已从 Steam 云取回配置文件（" + data.Length
                    + " 字节）并应用：" + localPath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 云端配置取回失败（不影响启动）：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 把刚取回的配置装进内存——否则退出时的自动落盘会用默认值把它盖掉（见
        /// <see cref="RestoreConfigIfMissing"/> 的说明）。
        ///
        /// <para>三步与「设置页保存」之后的收尾同源（见 Pages.SaveNow）：
        /// 载入 → <see cref="Settings.check"/> 归一化（键表 / 热键 / 语言）→ 刷新补丁开关。
        /// 任一步失败只记日志：这时配置文件已经好好躺在磁盘上了，下次启动必然生效；
        /// 本次会话沿用载入前的值，也比让启动路径抛异常强得多。</para>
        /// </summary>
        private static void ApplyRestoredConfig(string localPath)
        {
            try
            {
                ConfigManager.LoadConfigs(localPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 云端配置载入内存失败（本次会话沿用原设置，磁盘上已找回）：" + ex.Message);
                return;
            }

            string modPath = null;
            try { modPath = Main.mod != null ? Main.mod.Path : null; }
            catch (Exception) { }

            try
            {
                Settings.check(modPath ?? "");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 云端配置归一化失败（键表/热键可能要到下次启动才生效）：" + ex.Message);
            }

            try
            {
                PatchShared.SpectrePatchHub.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 云端配置应用后刷新补丁开关失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 本地配置文件的绝对路径：优先用 <see cref="ConfigManager.configPath"/>
        /// （与写盘同源，SaveConfigs 每次都会更新它）；没跑过 Load 时按模组目录兜底。
        /// </summary>
        private static string LocalConfigPath()
        {
            string path = ConfigManager.configPath;
            if (!string.IsNullOrEmpty(path)) return path;
            try
            {
                var entry = Main.mod;
                if (entry != null && !string.IsNullOrEmpty(entry.Path))
                    return Path.Combine(entry.Path, "Configs.json");
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>逐字节比较（配置只有几 KB，比哈希更直接、没有碰撞也没有额外分配）。</summary>
        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// 启动时自动回填（由 Main.OnToggle 调用）。只在开关打开时做事。
        ///
        /// 回填两样东西：模组自己的配置文件（本地缺失才取回，见
        /// <see cref="RestoreConfigIfMissing"/>）与云端有、本地没有的回放。
        ///
        /// **不能立即执行。** 模组的 OnToggle 比游戏打印
        /// "Startup: Initialize Steam SDK" 更早——那一刻 SteamAPI 还没初始化，
        /// 访问器返回空指针（实测：日志顺序 补丁挂载 → Initialize Steam SDK）。
        /// 所以这里起一个协程，每秒问一次"就绪了吗"，最多等 60 秒；就绪后
        /// 才真正回填。等不到就只记一行日志，绝不影响游戏启动。
        /// </summary>
        internal static void RestoreOnStartup()
        {
            if (!Settings.CloudSyncOn || !Settings.CloudRestoreOnLoad) return;
            try
            {
                // 泵没装（极早期/异常）时动作为 null：退化成"现在就试一次"
                if (MainThread.StartCoroutine(RestoreWhenReady()) == null)
                {
                    DoRestoreOnce();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 启动时云回填安排失败（不影响启动）：" + ex.Message);
            }
        }

        private static System.Collections.IEnumerator RestoreWhenReady()
        {
            float deadline = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < deadline && !SteamCloud.Ready)
            {
                yield return new WaitForSecondsRealtime(1f);
            }
            if (!SteamCloud.Ready)
            {
                Debug.Log("[SpectreReplay] Steam 云 60 秒内未就绪（原因：" + SteamCloud.Reason
                    + "），本次跳过云回填");
                // 不会再有恢复动作了：放开上传闸，别让配置在整个会话里都不同步。
                _configRestoreSettled = true;
                yield break;
            }
            DoRestoreOnce();
        }

        private static void DoRestoreOnce()
        {
            try
            {
                // 配置先做：它只有一份、几 KB，且「本地缺失」是换机后最先暴露的问题
                //（设置全丢 / 语言被重置），比回放更该第一时间补回来。
                // 本地已存在时这里什么都不做（绝不覆盖）。
                RestoreConfigIfMissing();

                RestoreResult r = RestoreMissing();
                if (r.Restored > 0)
                {
                    Debug.Log("[SpectreReplay] 已从 Steam 云回填 " + r.Restored + " 个回放文件"
                        + (r.Failed > 0 ? "（" + r.Failed + " 个失败）" : ""));
                }
                else if (r.Failed > 0)
                {
                    Debug.LogWarning("[SpectreReplay] 云回填有 " + r.Failed + " 个失败：" + r.LastError);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 启动时云回填失败（不影响启动）：" + ex.Message);
            }
        }

        private static string RemoteNameOf(string localFileName)
            => SteamCloud.RemoteReplayDir + "/" + localFileName;

        /// <summary>超出软阈值时只在日志里说一句（UI 自己会显示当前占用）。</summary>
        private static void LogQuotaHint()
        {
            try
            {
                Stats s = GetStats(true);
                if (s.Files > SoftCount || s.Bytes > SoftBytes)
                {
                    Debug.Log("[SpectreReplay] Steam 云回放占用偏高：" + s.Files + " 个文件，"
                        + (s.Bytes / 1024 / 1024) + " MB（软阈值 " + SoftCount + " 个 / "
                        + (SoftBytes / 1024 / 1024) + " MB）");
                }
            }
            catch (Exception) { }
        }
    }
}
