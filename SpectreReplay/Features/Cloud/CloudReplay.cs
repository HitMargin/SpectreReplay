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
        }

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
                    if (cloud.Contains(name)) { r.Skipped++; continue; }

                    string err;
                    if (SteamCloud.UploadFile(RemoteNameOf(name), path, out err))
                    {
                        r.Uploaded++;
                        cloud.Add(name);

                        // 配套键盘音：<无扩展名>.wav
                        string wav = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".wav");
                        if (File.Exists(wav))
                        {
                            try
                            {
                                if (new FileInfo(wav).Length <= MaxWavBytes)
                                {
                                    SteamCloud.UploadFile(RemoteNameOf(Path.GetFileName(wav)), wav, out err);
                                }
                            }
                            catch (Exception) { }
                        }
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
                    // 云端名字是外部数据：只接受纯文件名，拒绝任何路径成分
                    if (string.IsNullOrEmpty(name) || name != e.Name.Substring(e.Name.LastIndexOf('/') + 1))
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
        /// 启动时自动回填（由 Main.OnToggle 调用）。只在开关打开时做事。
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
                yield break;
            }
            DoRestoreOnce();
        }

        private static void DoRestoreOnce()
        {
            try
            {
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
