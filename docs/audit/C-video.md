# 视频渲染与音频 审计报告

## 结论摘要

没有 P0（未发现会崩溃、会写坏存档/回放、或静默损坏用户数据的路径）。最严重的三条都是 P1/P2 级"成片被毁"或"失败无声"：

1. **`RenderTemp` 是固定路径，且被三处无条件删除**（新作业开工、TearDown、延迟清理线程）。主线程等不及收尾时（`TearDown` 只等 5 秒）会把编码器/临时目录交给一个后台线程，而 `CloseJob()` 之后 `StartRender` 立刻放行——**下一次渲染会在旧收尾线程还在读 `temp_video.*` 时把目录删掉重建**，最长可在一个延迟清理线程里挂 40 分钟后删掉**新**渲染的临时文件。见 F1。
2. **`-shortest` 在音频短于视频时截断的是视频结尾**，而收尾照常报 `Completed`；`VideoAudioTrack` 自己就有"音频丢帧→重启"的设计（RestartBudget=3），也就是说触发条件是它预期会发生的。见 F10。
3. **mux 失败/超时后成片半成品留在输出目录且无人清理**（`VideoConverter` 同一位置会 `File.Delete`，这里没有）。见 F2。

其余集中在 ffmpeg 子进程（stderr 并发读、NVENC 探测的管道满死锁、捆绑 exe 从不探测）与音频（WAV 头采样率取"请求值"而非实际值、`AudioClip` 泄漏、RIFF 奇数字节块不对齐）。

## 发现

### F1 [P1] `RenderTemp` 固定路径 + 三处无条件删除：跨作业互相销毁

- 位置：
  - `SpectreReplay\Features\VideoRender\Pipeline\VideoRenderJob.cs:995`（固定目录名）
  - `SpectreReplay\Features\VideoRender\Pipeline\VideoRenderJob.cs:998-1000`（下次开工先删）
  - `SpectreReplay\Features\VideoRender\Pipeline\VideoRenderJob.cs:1382-1402`（TearDown 直接删）
  - `SpectreReplay\Features\VideoRender\Pipeline\VideoRenderJob.cs:1413`、`:1450-1473`（把删除挂到 40 分钟后的清理线程）
  - `SpectreReplay\Features\VideoRender\Pipeline\VideoRender.cs:92-125`（`StartRender` 只挡 `Job != null`）

- 证据：

```csharp
// VideoRenderJob.cs:995-1000
tempDir = Path.Combine(Main.mod.Path, "RenderTemp");
try
{
    if (Directory.Exists(tempDir))
        Directory.Delete(tempDir, true);
    Directory.CreateDirectory(tempDir);
}
```

```csharp
// VideoRenderJob.cs:1456-1467 —— 延迟清理删的是同一个固定路径
if (!target.Join(TimeSpan.FromMinutes(40)))
{ ... return; }
try { deferredEncoder?.Dispose(); } catch ...
if (!string.IsNullOrEmpty(deferredTempDir))
{
    if (Directory.Exists(deferredTempDir))
        Directory.Delete(deferredTempDir, true);
}
```

触发条件（两段，都不需要用户做奇怪的事）：

- **① 主线程等不及**：`TearDown` 对收尾线程 `Join(5s)` 超时（`VideoRenderJob.cs:1337-1347`）→ 走 `DeferCleanupAfterWorker` → `CloseJob()` 把 `Job` 置 null → 玩家马上再点一次渲染（`StartRender` 只检查 `Job != null`）→ 新作业 `PrepareWorkspace` 对同一个 `RenderTemp` 先 `Delete`。
- **② 延迟清理线程迟到**：①里挂出去的线程最长等 40 分钟，它到期后无条件删 `RenderTemp`——**此时那一份可能已经是新渲染正在写的临时目录**。

- 影响：静默。`Directory.Delete` 遇到被 ffmpeg 占用的文件会抛 `IOException` 被 `catch` 掉只写日志（`VideoRenderJob.cs:1002-1006`），于是**目录内容仍然是旧的**，新作业把 `temp_video.mp4` / `audio.wav` 覆盖到旧文件上、旧 mux 同时在读，两边都坏；或者延迟线程把新作业的 `audio.wav` 删掉，新作业在 `MuxWith` 处报 `mixdown file missing`（`VideoEncoder.cs:293-294`）——一次几十分钟的渲染白跑。注释 `VideoRenderJob.cs:1379-1381` 明确写着"留一个 RenderTemp 目录……远好过毁掉用户等了很久的成片"，但**下一轮开工的第一件事就是删掉它**，注释与代码冲突。

- 建议：`tempDir` 改成每次作业唯一（`RenderTemp\<yyyyMMdd_HHmmss_fff>`，或 `Path.Combine(Path.GetTempPath(), "SpectreReplay", Guid)`）；延迟清理只删自己那一份。另外让 `StartRender` 知道"上一次收尾还没结束"——`VideoRender` 里加一个 `FinalizePending` 标志，为真时拒绝新作业并给一条可读提示（或排队），而不是开一个新作业去抢同一个目录。

- 置信度：**高**（机制纯代码可推；触发需要一次 >5s 的收尾，慢盘/大文件/杀软扫描即满足）

### F2 [P2] mux 失败/超时后留下半成品成片，且没有任何人再引用它

- 位置：`SpectreReplay\Features\VideoRender\Pipeline\VideoRenderJob.cs:648-664`（收尾失败上报）、`:723-749`（`RescueMuxFailure`）、`SpectreReplay\Features\VideoRender\Pipeline\VideoEncoder.cs:291-304`（`MuxWith`）

- 证据：

```csharp
// VideoRenderJob.cs:652-664
if (finalizeError != null)
{
    if (finalizeErrorFromMux)
        RescueMuxFailure(finalizeError);
    System.Runtime.ExceptionServices.ExceptionDispatchInfo
        .Capture(finalizeError).Throw();
}
```

```csharp
// VideoConverter.cs:164-172 —— 同一类失败，转换侧会删半截文件
if (!ok)
{
    try { File.Delete(targetPath); } catch { }
    state.Failed++;
```

触发条件：`MuxWith` 的 ffmpeg 非零退出或 `RunAndWait` 30 分钟超时被 `Kill`（`VideoEncoder.cs:511-517`）——磁盘满、输出目录被同步盘占用、成片极大、杀软锁文件。

- 影响：输出目录里留一个**大小非零、无法播放**的 `Render_*.mp4`；`ReportEnd(Failed)` 会把 `VideoRender.Current.Output` 清空（`VideoRenderJob.cs:1108-1109`），界面上是"渲染失败"，玩家却能在文件夹里看到"成片"，误以为只是播放器问题。更糟的是"转换"功能会把目录里所有 `.mp4` 当源（`VideoConverter.cs:64-67`），对这个坏文件再失败一次。`RescueMuxFailure` 只额外拷出 `_videoonly` / `_audioonly`，**没有删除或改名那份半成品**。

- 建议：`MuxWith` 失败（异常/超时）后删除 `finalVideoPath`（它一定是本次刚写的）；若想保留证据，改名为 `*_partial.mp4` 并在通知里说明"成片不完整，已抢救出原始音视频流"。与 `VideoConverter.ConvertOne` 的失败清理保持同一策略。

- 置信度：**高**

### F3 [P2] `RunAndWait` 的 stderr 由异步事件线程追加、主线程读取，无锁；且无上限

- 位置：`SpectreReplay\Features\VideoRender\Pipeline\VideoEncoder.cs:487-521`（本项）、对照 `:541-574`（管道路径是加锁的）

- 证据：

```csharp
// VideoEncoder.cs:498-519
StringBuilder stderr = new StringBuilder();
using (Process mux = Process.Start(info))
{
    mux.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
    mux.OutputDataReceived += (_, e) => { };
    mux.BeginErrorReadLine();
    mux.BeginOutputReadLine();
    if (!mux.WaitForExit(RunAndWaitTimeoutMs))
    {
        try { mux.Kill(); } catch { }
        ...
        throw new TimeoutException(...);          // ← 此刻事件线程可能仍在 AppendLine
    }
    if (mux.ExitCode != 0)
        throw new InvalidOperationException("mux failed, exit code " + mux.ExitCode + ": " + stderr);
}
```

触发条件：`VideoConverter` 的 30 分钟超时分支（此时进程刚被 `Kill`，读取端一定还在跑），或非零退出后异步读取尚未排空（`WaitForExit(int)` 不保证 handler 已跑完，只有无参 `WaitForExit()` 才保证）。

- 影响：`StringBuilder` 同时被 `AppendLine` 与 `ToString()` 访问，可能抛 `ArgumentOutOfRangeException`/`IndexOutOfRangeException`——**把"mux 失败"变成"转换线程抛了个与 ffmpeg 无关的异常"**，真实原因（例如 `Could not find tag for codec`）丢失；轻则错误消息被截断。另外这里没有 `StderrCapChars` 那种上限，长转换（几十分钟、损坏输入刷 warning）可以让这个 `StringBuilder` 无界增长。

- 建议：照 `AppendStderr`/`StderrTail` 的做法加一把锁并限制到 4KB；或在 `await` 完成后补一次无参 `mux.WaitForExit()` 再读（先 `WaitForExit(timeout)` 判超时、再 `WaitForExit()` 等 handler 排空）。

- 置信度：**高**（`StringBuilder` 非线程安全是确定事实；是否真撞上取决于时序）

### F4 [P2] NVENC 探测重定向了 stdout/stderr 却从不读取：管道满即死锁，且负结果永久缓存

- 位置：`SpectreReplay\Features\VideoRender\Pipeline\VideoEncoder.cs:439-478`

- 证据：

```csharp
// VideoEncoder.cs:447-469
var info = new ProcessStartInfo
{
    FileName = exe,
    Arguments = "-hide_banner -loglevel error -y -f lavfi ... -c:v h264_nvenc -frames:v 1 -f null -",
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardError = true,
    RedirectStandardOutput = true
};
using (Process probe = Process.Start(info))
{
    if (probe == null) nvencWorks = false;
    else if (!probe.WaitForExit(5000))
    {
        try { probe.Kill(); } catch { }
        nvencWorks = false;          // ← 超时一律判"没有 NVENC"
    }
    else nvencWorks = probe.ExitCode == 0;
}
```

`BeginErrorReadLine` / `BeginOutputReadLine` 一次都没调，也没读 `probe.StandardError`——**孩子写满管道缓冲（Windows 默认约 4KB）就会阻塞在 `WriteFile` 上，永远退不出**，于是 `WaitForExit(5000)` 必然超时。

触发条件：ffmpeg 在探测命令上输出超过管道容量。`-loglevel error` 下正常是几行；但驱动报错时 ffmpeg 会打多行（NVENC 初始化失败 + fallback 信息 + 设备列表），旧版 ffmpeg 在无 GPU 的机器上尤其啰嗦。

- 影响：**有 NVENC 的机器被静默判成不可用**。`nvencProbedExe == exe` 后这个负结果**没有冷却**（`VideoEncoder.cs:43-44` 注释明确写"负结果不设冷却重试"），整个会话都用 `libx264`/`libvpx-vp9` 软编——渲染慢数倍，而玩家在设置页只看到提示从 `h264_nvenc (GPU)` 变成 `libx264 (CPU)`，无从判断是驱动问题还是探测失败。同类问题在 `PathProbeSucceeded`（`:147-174`）也存在，但它**没重定向任何流**，所以不会死锁——两处写法不一致正是漏读流的来源。

- 建议：与 `Spawn` 一致地 `BeginErrorReadLine()`（回调里直接丢弃或只留尾部），或干脆把 `RedirectStandardError/Output` 去掉（探测只关心退出码，输出没人要）。同时给 NVENC 负结果加与 `pathWorks` 相同的冷却，让"驱动刚装好"不必重启游戏。

- 置信度：**高**（管道满死锁是 `RedirectStandard*` 的经典形态；触发概率取决于 ffmpeg 输出量，故定为 P2 而非 P1）

### F5 [P2] 捆绑的 `ffmpeg.exe` 从不做能力探测：坏 exe 要等重启回放、劫持相机之后才失败

- 位置：`SpectreReplay\Features\VideoRender\Pipeline\VideoEncoder.cs:103-134`（`LocateEncoder` 只做 `File.Exists`）、`SpectreReplay\Features\VideoRender\Pipeline\VideoRender.cs:114-119`（只在 PATH 回退时探测）

- 证据：

```csharp
// VideoEncoder.cs:115-125 —— 找到就返回，不验证它能不能跑
string[] candidates =
{
    Path.Combine(modDir, "ffmpeg.exe"),
    Path.Combine(gameRoot, "ffmpeg.exe")
};
foreach (string candidate in candidates)
    if (File.Exists(candidate)) { locatedEncoder = candidate; return locatedEncoder; }
```

```csharp
// VideoRender.cs:114-119 —— 只有走到 PATH 回退才探测
string encoderPath = VideoEncoder.LocateEncoder();
if (encoderPath == "ffmpeg" && !VideoEncoder.PathProbeSucceeded())
{
    TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.ffmpeg_missing"), 5f, NotifType.Warning);
    return;
}
```

触发条件：模组目录/游戏根目录里的 `ffmpeg.exe` 损坏、被杀软隔离后残留 0 字节文件、架构不符、缺依赖 DLL、或版本太老不认 `libvpx-vp9`/`h264_nvenc`。**游戏自带的那份是好的，但"模组目录优先"意味着任何一次手工替换/半途解压都会命中。**

- 影响：`StartRender` 一路放行 → `Phases()` 先 `SwitchToPlayMode()` + `editor.Play()` 重开关卡、劫持三台相机（`VideoFrameSource.cs:133-135`），到 `encoder.Begin()` 之后的第一次 `Submit` 才抛 `encoder process exited unexpectedly`（`VideoEncoder.cs:220-222`）。玩家看到的是"渲染失败：encoder process exited unexpectedly\nffmpeg: ..."（英文技术串），而**整段重启回放、切模式、劫持相机的副作用已经发生**，收尾还要再走一遍 `ExitPlaytest`。这与 PATH 分支给出的"找不到 ffmpeg"提示完全不对等。

- 建议：在 `StartRender` 里对**实际将要使用的那条路径**也跑一次探测（`-version`，5 秒超时，按 exePath 缓存，正结果会话内有效），失败时用同一条本地化提示并给出路径；`PathProbeSucceeded` 可以改造成接受 exePath 的统一实现（顺带修掉 F4 的重复探测逻辑）。

- 置信度：**高**（代码路径确定；触发依赖用户环境）

### F6 [P2] 混音轨的采样率/声道数只在构造时取一次，渲染中途换音频设备不会被发现

- 位置：`SpectreReplay\Features\VideoRender\Pipeline\VideoAudioTrack.cs:48-51`、`:176-203`、`:69-93`

- 证据：

```csharp
// VideoAudioTrack.cs:48-51 —— 只在这里读一次
SampleRate = AudioSettings.outputSampleRate;
Channels = ChannelsOf(AudioSettings.speakerMode);
file = new FileStream(wavPath, FileMode.Create);
WriteHeader(0);

// :73-92 —— 每帧按 构造时的 Channels 算长度，按 构造时的 SampleRate 解释时长
int perChannel = AudioRenderer.GetSampleCountForCaptureFrame();
int floats = Math.Max(0, perChannel) * Channels;
...
target.CopyTo(managed);
Buffer.BlockCopy(managed, 0, bytes, 0, floats * 4);
```

触发条件：渲染期间玩家切换音频输出设备（插拔耳机、切 HDMI、独占模式切换导致 Unity 重初始化音频）。这不是臆测：游戏自己把这件事当一等事件处理——`scrConductor.Update` 第一句就是

```csharp
// 反编译源码 scrConductor.cs:770-774
if (isAudioOutputDeviceChanged)
{
    scrController.CheckForAudioOutputChange();
    isAudioOutputDeviceChanged = false;
}
```

- 影响：两种，都很难查——① `AudioRenderer.Render` 因格式变化返回 false → 整场渲染以 `rendervideo.audio_failed (AudioRenderer.Render returned false at frame N)` 结束（至少是明确失败）；② 更隐蔽的是 `GetSampleCountForCaptureFrame()` 按新采样率返回样本数，我们仍用旧 `SampleRate` 写头 → **成片整体变速变调**（48k 数据写成 44.1k 就是慢 8.8%），而通知说"渲染完成"。

- 建议：`CaptureFrame` 每帧（或每 30 帧）比一次 `AudioSettings.outputSampleRate` / `speakerMode` 与构造快照，不一致就 `Dead = true` + `DeathReason = "audio format changed mid-render"`，让失败比"变速成片"更容易被玩家理解；若要继续支持，只能在切换点分段落盘并重写头（成本高，建议先做"明确失败"）。

- 置信度：**中**（机制确定；`AudioRenderer` 在设备切换后到底是返回 false 还是继续按新格式给样本，需要实跑插拔耳机确认）

### F7 [P2] `AudioRecorder.Cleanup` 在线程未退出时既不销毁也不清空 `recordingClip` → 原生内存泄漏

- 位置：`SpectreReplay\State\AudioRecorder.cs:602-609`（对照 `:438-441` 的 `Join(1000)`）

- 证据：

```csharp
// AudioRecorder.cs:602-609
if (recordingClip != (UnityEngine.Object)null)
{
    if (processingThread == null || !processingThread.IsAlive)
    {
        UnityEngine.Object.Destroy(recordingClip);
        recordingClip = null;
    }
}
```

```csharp
// AudioRecorder.cs:438-441 —— 只等 1 秒
if (processingThread != null && processingThread.IsAlive)
{
    processingThread.Join(1000);
}
```

触发条件：采集线程 1 秒内没退出。它的循环体是 `ProcessRange`（按 `chunkSize=4096` 分块，块数 = `remaining/4096`，环形缓冲绕回时一次要走两段）+ 每轮 `Thread.Sleep(10)`（`:228`）；一次 `StopRecording` 撞上大块 `ProcessRange` 或 `Microphone.GetPosition` 卡顿就会超时。

- 影响：`Destroy` 被跳过且 `recordingClip` 保持旧引用；下次 `StartRecording` → `startMicrophone` 直接把 `recordingClip` 换成新 clip（`AudioRecorder.cs:653`），**旧 clip 再也没有任何路径能销毁**——`Microphone.Start` 返回的 `AudioClip` 是原生资源，不会随 GC 释放。每次泄漏约 `maxRecordingLength × 采样率 × 声道 × 4B`（默认 10s 立体声 ≈ 3.4MB），长会话反复"录一段键盘音"会累积。

- 建议：把销毁改成"线程退出后由自己或下一次进入时执行"——采集线程在 `while` 结束时把 `recordingClip` 拿出来 `Destroy` 并置空（此时它自己的引用已失效，安全）；或维护一个 `pendingDestroy` 列表，每帧检查 `!IsAlive` 再销毁。

- 置信度：**高**（逻辑确定；泄漏量与触发频率相关）

### F8 [P2] WAV 头写的是"请求的"采样率，不是实际 clip 的；预热路径还会沿用上一次的值

- 位置：`SpectreReplay\State\AudioRecorder.cs:147-153`（预热分支不更新参数）、`:357`（头部用 `recordingFrequency`）、`:643-660`（`Microphone.Start` 才是真值来源）

- 证据：

```csharp
// AudioRecorder.cs:147-153 —— 已有一路在录时，形参被忽略
bool prewarmed = Microphone.IsRecording(microphoneDevice);
currentFilePath = filePath;
if (!prewarmed)
{
    recordingFrequency = frequency;
    maxRecordingLength = duration;
}

// :357 —— 头永远按 recordingFrequency 写
byte[] header = CreateWAVHeader(1, recordingFrequency, 16, 0);

// :653 —— 实际生效的是 Unity 给的 clip
recordingClip = Microphone.Start(deviceName, true, maxRecordingLength, recordingFrequency);
```

触发条件：① 别的模组（或本模组上一次未 `Microphone.End` 的会话）已经在该设备上录音 → `prewarmed=true` → `recordingFrequency` 保留上一次的值，而 `ProcessRange` 用的是 `recordingClip.channels`/真实数据；② 设备不支持 44100（大量 USB 声卡只有 48000），Unity 的 `Microphone.Start` 会用设备实际参数建 clip。

- 影响：**头里的采样率与数据不符 → 整段录音变速变调**（数据 48000 写成 44100 就慢 8.8%），而哈希校验照样通过（哈希只覆盖 PCM 字节，`VerifyAudioFile` 从偏移 44 开始读，`AudioRecorder.cs:528-543`），所以**校验链路完全不会发现**。玩家听到的是"我录的键盘音变调了/对不上节奏"。

- 建议：头部与后续逻辑统一以 `recordingClip.frequency` / `recordingClip.channels` 为准（在 `startMicrophone` 之后就回写 `recordingFrequency = recordingClip.frequency`），预热路径也要从**当前** clip 取参数；`CreateWAVHeader` 的调用点改成传 `recordingClip.frequency`。

- 置信度：**中**（Unity 在"频率不支持"时是回退重采样还是用别的 rate 建 clip 需要实跑确认；但代码确实从不读 `clip.frequency`，这条无论如何都该修）

### F9 [P2] `WavLoader.ParseHeader` 不处理 RIFF 奇数长度块的填充字节（pad byte）

- 位置：`SpectreReplay\State\WavLoader.cs:143-223`

- 证据：

```csharp
// WavLoader.cs:190-193 + 195-222
if (num > 16)
{
    binaryReader.ReadBytes(num - 16);      // fmt 扩展：读 num-16，未跳 pad
}
else
{
    if (text3 == "data") { ... break; }
    binaryReader.ReadBytes(num);           // 其他块：读 num，未跳 pad
}
```

RIFF 规定块大小为奇数时，块体后必须补一个填充字节（不计入块大小）。代码读完 `num` 字节就进下一次循环读 4 字节块名，**没有 `if ((num & 1) == 1) ReadByte()`**。

触发条件：`data` 块之前出现奇数长度的块。最常见的是 `LIST`/`INFO`/`id3 ` 元数据块，而元数据里一旦有奇数个字节（中文 UTF-8 标签、单字节标签 + NUL 边界）就会命中；Audacity/foobar 等导出的 WAV、带 BWF 信息的素材都可能带这些块。

- 影响：读指针错 1 字节 → 后续"块名"是乱码 → 落到 `else` 分支用乱码当长度 `ReadBytes`（可能抛 `EndOfStreamException`），或恰好凑出一个合法长度把 `data` 块的偏移读错 1 字节。前者的表现是 `[WavLoader] 不是 WAVE 格式` / `无效的块大小` 之类与真实原因毫无关系的报错；后者更坏——**整段音频错位 1 字节，出来是满幅噪声**，而 `IsValid` 全过、`SetData` 成功、`VerifyAudioFile` 也可能通过（哈希只覆盖 44 字节之后的数据，与头解析无关）。

- 建议：在读块体之后统一 `if ((num & 1) == 1) binaryReader.ReadByte();`（`fmt ` 的扩展字节与 `else` 分支都要；`data` 分支读完就 `break`，不受影响）。顺带把 `int num = binaryReader.ReadInt32()` 的 `num < 0` 判断与 `Position + num > Length` 的加法改成 `long`（当前 `Position + num` 理论上可溢出成负数绕过检查，然后由 `ReadBytes` 抛异常兜住）。

- 置信度：**高**（RIFF 规范与代码的对应关系可逐字核对）

### F10 [P2] `-shortest` 在音频短于视频时截断的是**视频结尾**，而收尾照报 `Completed`

- 位置：`SpectreReplay\Features\VideoRender\Pipeline\VideoEncoder.cs:291-304`（参数与注释）、`SpectreReplay\Features\VideoRender\Pipeline\VideoAudioTrack.cs:129-157`（音频侧确实会变短）、`SpectreReplay\Features\VideoRender\Pipeline\VideoRenderJob.cs:633-670`（照常报完成）

- 证据：

```csharp
// VideoEncoder.cs:296-302
string args = "-y -i " + Quote(rawVideoPath) + " -i " + Quote(wavPath)
    + " -map 0:v:0 -map 1:a:0 -c:v copy " + audio + " -ac 2 -shortest"
    + " " + Quote(finalVideoPath);
```

```csharp
// VideoAudioTrack.cs:134-156 —— 音频可能整段缺样本（静默重启/放弃）
if (silentFrames >= fps && restarts < RestartBudget) { ... AudioRenderer.Stop(); AudioRenderer.Start(); ... }
if (restarts >= RestartBudget && silentFrames >= giveUpFrames) { Dead = true; ... }
```

`-c:v copy` + `-shortest` 时，ffmpeg 在**最短的那条流结束**时停止写封装——视频包还没写完的部分被丢弃。注释（`:297-299`）写的是"有它则按短轨收尾"，并把它描述成"尾部一段无声空转"的无害优化；实际丢的是**画面**。

触发条件：混音轨比视频短。代码自己预期会发生：`OnSilence` 的 3 次重启会**丢掉每分钟的采样**（整段"静默"期间 `CaptureFrame` 直接 `return`，`:75-79`），`GiveUpAfterSeconds=5` 之后再放弃。任何一次重启/静默都让音频永久短一截。

- 影响：成片**结尾被静默截掉**，`ReportEnd(Completed)` + "渲染完成"通知，玩家要到播放时才发现"最后几秒没了"。这与 `hardStop` 那条"宁可明确失败也不交半截视频"的设计原则（`VideoRenderJob.cs:594-603`）直接冲突。

- 建议：mux 后校验两条轨时长（`ffprobe` 不便依赖，可让 mux 用 `-map` + 不加 `-shortest`，再用 `mixdown.Seconds` 与 `delivered/fps` 对比，超出阈值就按失败上报并说明"音频采集丢帧，成片结尾可能不完整"）；或者 `-shortest` 换成"补齐到视频长度"（去掉 `-shortest`，让 ffmpeg 给音频补静音），二者选一个并把语义写进注释。

- 置信度：**高**（`-shortest` + `copy` 的语义确定；"音频会变短"由本文件自己的重启逻辑保证）

### F11 [P2] `UnscaledDeltaPin` 撤销失败后没有自愈机会：游戏会一直读到固定 `1/armedFps`

- 位置：`SpectreReplay\Features\VideoRender\Patches\VideoRenderPatches.cs:167-200`（`Uninstall` 保留失败项）、`:98-109`（只在**下一次 Install** 时重试）、`:226-230`（钉扎体无条件返回虚拟值）

- 证据：

```csharp
// VideoRenderPatches.cs:194-199
if (!undone)
    failed.Add(d);
detours.Clear();
foreach (IDisposable d in failed)
    detours.Add(d);          // 留着，下次 Install/Uninstall 再试
```

```csharp
// :98-109 —— 唯一的"下次"是下一次渲染开始
internal static void Install()
{
    if (detours.Count > 0)
    {
        Uninstall();
        if (detours.Count > 0) { Debug.LogWarning(... "本次不钉扎（走真实时钟）"); return; }
    }
```

- 影响：若两次 `Dispose()` 都失败（注释说"首次失败常常是目标对象正在销毁、下一帧就正常了"，但**没有任何机制安排"下一帧再试"**），detour 会一直挂在 `Time.unscaledDeltaTime` / `unscaledTime` / `realtimeSinceStartup` 上，钉扎体读的是静态字段 `armedFps`（渲染帧率）——**整个游戏的所有 unscaled 时间从此变成固定步长**（动画、DOTween、Timeout 全部失真），直到玩家再点一次渲染（触发 `Install` 里的重试）或重启游戏。下一次渲染还会因为 `detours.Count > 0` 而**主动放弃钉扎**，等于把"成片确定性"也一起降级。

- 建议：失败项挂一个重试调度（例如 `MainThread` 上每 N 帧或下一次 `Uninstall` 时再试，并在 30 秒后仍失败就 `Debug.LogError` + 提示玩家重启）；把"游戏仍被钉扎"这个状态暴露到设置页/日志摘要，而不是只在渲染开场时静默降级。

- 置信度：**中**（MonoMod `Dispose` 连续两次失败罕见；但"无自愈"是确定的代码事实，且后果是全游戏级）

### F12 [P3] `Quote()` 的转义不含"尾随反斜杠倍增"，且两处重复实现

- 位置：`SpectreReplay\Features\VideoRender\Pipeline\VideoEncoder.cs:576-577`、`SpectreReplay\Features\VideoRender\Pipeline\VideoConverter.cs:191-192`

- 证据：

```csharp
// VideoEncoder.cs:576-577（VideoConverter.cs:191-192 逐字相同）
private static string Quote(string value)
    => "\"" + value.Replace("\"", "\\\"") + "\"";
```

按 CRT/`CommandLineToArgvW` 的规则，`"` 前若紧跟 `\` 需要按"2n 个反斜杠 + 引号"倍增，否则一个**以反斜杠结尾**的值会把自己的闭合引号转义掉、把后续参数整体吞进这一个实参。

- 影响：当前不可触发——所有 `Quote()` 调用点传的都是文件路径（Windows 文件名不能含 `"`、也不会以 `\` 结尾），参数里也没有 shell 展开（`UseShellExecute=false`，`%`/`&`/`^` 不生效）。这是**脆弱代码**而非现网 bug：将来只要有人把目录路径（如 `Settings.RenderOutputDir`）直接拼进命令、或支持非 Windows 平台的分隔符，就会静默切错参数。两处逐字重复还意味着修一处漏一处。

- 建议：抽一个共享的 `FfmpegArgs.Quote`（按 2n 规则倍增尾随反斜杠），`VideoConverter` 与 `VideoEncoder` 都调它；`tools\ffmpeg-probe.ps1` 里那份 PowerShell 版本也一样处理，避免"探针通过、产品参数不同"。

- 置信度：**高**（规范与实现对得上；但当前无触发路径，故 P3）

### F13 [P3] RenderClock 的 `TranspilerReady` 只要求"替换了任意一处"，不验证 `dspTime` 这一处

- 位置：`SpectreReplay\Features\VideoRender\Patches\VideoRenderClockPatches.cs:114-145`

- 证据：

```csharp
// VideoRenderClockPatches.cs:114-144
int replaced = 0;
foreach (CodeInstruction instruction in body) { ... replaced++; ... yield return instruction; }
if (replaced == 0)
    throw new InvalidOperationException($"[SpectreReplay] RenderClock transpiler: no time-source calls found ...");
RenderClockSource.TranspilerReady = true;      // ← 只要求 > 0
```

- 影响：`VideoRenderJob` 用 `RenderClockSource.TranspilerReady && Settings.RenderClockOn` 决定走确定性时钟（`VideoRenderJob.cs:364`），日志会打印 `renderClock: deterministic RenderClock path (transpiler active)`。如果游戏将来把 `AudioSettings.dspTime` 从 `scrConductor.Update` 里挪走（只留 `Time.unscaledTime*`），替换数仍然 > 0、闸门仍然绿，而**hitsound 调度与倒计时走真实 dsp**——成片里音效时刻与画面脱钩，日志却宣称确定性。当前 150 版 `scrConductor.Update`（反编译源码 `:768-800`）确实两次 `AudioSettings.dspTime` + 两次 `Time.unscaledTimeAsDouble`，共 4 处可替换，所以现状成立。

- 建议：分别记 `dspReplaced` / `unscaledReplaced`，`TranspilerReady` 要求 `dspReplaced > 0`（这是"确定性"的必要条件），并把替换计数写进日志。

- 置信度：**高**（代码事实；后果依赖未来版本，故 P3）

### F14 [P3] `Patch_RenderClockAsyncPosition` 用 `0.0` 顶替 `calibration_i`，当前靠另一条补丁拦住才不出错

- 位置：`SpectreReplay\Features\VideoRender\Patches\VideoRenderClockPatches.cs:148-167`；对照游戏源码 `AsyncInputUtils.cs:7-14`

- 证据：

```csharp
// VideoRenderClockPatches.cs:160-165
__result = RenderClockSource.ActiveClock.SongPosition(
    conductor.dspTimeSong,
    conductor.song.pitch,
    conductor.addoffset,
    0.0);                       // ← 游戏公式这里是 scrConductor.calibration_i
```

```csharp
// 反编译源码 AsyncInputUtils.cs:7-13
public static double GetSongPosition(scrConductor conductor, long nowTick)
{
    if (!GCS.d_oldConductor && !GCS.d_webglConductor)
        return ((double)nowTick / 10000000.0 - conductor.dspTimeSong - (double)scrConductor.calibration_i)
            * (double)conductor.song.pitch - conductor.addoffset;
```

- 影响：`GetSongPosition` 的唯一调用链是 `scrPlanet.AsyncRefreshAngles`（`scrPlanet.cs:567-570`）← `AsyncInputUtils.AdjustAngle`（`:26-32`）← `scrPlayer.cs` 七处；而 `Patch_VideoLockAsyncAngle`（`VideoRenderPatches.cs:42-47`）在渲染期间让 `AdjustAngle` 直接 `return false`，**所以这条补丁现在不可达**。但它的正确性依赖于"另一条补丁把唯一的调用者拦住"——一旦有人为了修旋转而放宽那条守卫，行星角度会整体偏 `calibration_i × pitch`（玩家在设置里配的 inputOffset 越大偏得越多），而且没有任何断言会红。

- 建议：把 `0.0` 改成 `scrConductor.calibration_i`，与游戏公式逐字一致；并在注释里写明"当前由 Patch_VideoLockAsyncAngle 拦断，但公式必须自洽，不要依赖调用者被拦"。

- 置信度：**高**（公式差异可逐字核对）

### F15 [P3] 低输出帧率下 `AudioRenderer` 的采集节奏未经校验，唯一仪表是一条日志

- 位置：`SpectreReplay\Features\VideoRender\Pipeline\VideoAudioTrack.cs:69-93`、`SpectreReplay\Features\VideoRender\Pipeline\VideoRenderJob.cs:502-533`

- 证据：

```csharp
// VideoRenderJob.cs:505-532（每 5 秒巡检一次，只打日志）
double audioSeconds = mixdown.Seconds;
if (audioSeconds <= 0.0) audioSeconds = frameIndex / (double)fps;
else if (audioSeconds < clockSeconds - 0.25) audioSeconds = clockSeconds;
clockSeconds = Math.Max(clockSeconds, audioSeconds);
...
Debug.Log($"[SpectreReplay] renderClock f={frameIndex} audio={clockSeconds:0.0000}s ideal={ideal:0.0000}s "
        + $"drift={(clockSeconds - ideal) * 1000.0:0.0}ms ...");
```

- 触发条件：`Settings.RenderFps` 可拖到 15（`ConfigManager.cs:632` 钳 `15..480`，`Pages.cs:963` 滑条同范围）。15 fps 意味着每输出帧之间隔 66ms 真实时间，而 `CaptureFrame` 每帧只调一次 `AudioRenderer.Render`。若 `AudioRenderer` 的采集环形缓冲容量小于"一帧应得的样本数"（48000Hz × 66ms ≈ 3200 样本/声道），多出的部分会被覆盖丢弃。

- 影响：音频变短 → 与 F10 串联：`-shortest` 把视频结尾截掉。这条**目前只是待证假设**，`AudioRenderer` 的缓冲深度没有文档也没有反射可查。

- 建议 / 怎么确认：代码已经每 5 秒打一行 `drift=`，用 15fps 档跑一段 30 秒的谱面就能判：若 `drift` 单调向负方向增长（`audio` 落后 `ideal`），即样本被丢。真命中时的修法是在 `VideoRenderJob` 的捕获循环里按 `AudioSettings.GetConfiguration().dspBufferSize` 分多次调 `mixdown.CaptureFrame()`，或在低 fps 档位强制起一个"音频泵"。

- 置信度：**低**（需要实跑 15fps 看 `drift` 日志才能确认；写在这里是因为它与 F10 的后果串联，且验证成本只有一行日志）

## 已核查但判定无问题（避免下轮重复劳动）

- `VideoRender\Pipeline\VideoEncoder.cs:576-577` 与 `VideoConverter.cs:191-192`：命令串**不经过 shell**（`UseShellExecute=false`），`%`/`&`/`^`/`|` 都不展开；Windows 路径不可能含 `"`，故当前无参数注入面（脆弱性见 F12，不是现网 bug）。
- `VideoEncoder.cs:523-545`：管道编码器**已**重定向 stderr 并 `BeginErrorReadLine()`，`AppendStderr` 加锁且限 4096 字符 → 不会出现"只写不读 stderr 导致管道满"的死锁；stdout 未重定向，但输出目标是文件，ffmpeg 不往 stdout 写。
- `VideoEncoder.cs:235-267`：`FinishVideo` 的三个等待点都有界（`JoinPump` 10 分钟、`WaitForExit` 10 分钟、失败即 `Kill`），`ExitCode != 0` 会把 stderr 尾部带进异常 → 不会永久挂死主线程。
- 奇数宽高（`RowFilter` 的 `pad=ceil(iw/2)*2:ceil(ih/2)*2`，`VideoEncoder.cs:356-361`）：`tools\ffmpeg-probe.ps1` 已用真 ffmpeg 跑通 33×25，且 `RenderWidth/Height` 钳在 `320..7680 / 240..4320`（`ConfigManager.cs:630-631`）→ 不重复报。
- `VideoFrameSource.cs:215-260` 与 `CursorOverlay.cs` 的合成数学（热点在箭尖、行序镜像、分辨率缩放、越界钳制、预乘 alpha 只乘一次）：已由 `tools\UiPreview\CursorOverlayCheck.cs` 的 ①~⑦ 覆盖（含真画一遍再按像素验），不重复。
- `WavLoader` 的 44 字节头写读往返、6 种坏头拒绝、四种合法位深、多声道下混：已由 `WavRoundTripCheck` 覆盖；本报告只报它没覆盖的"奇数字节块对齐"（F9）。
- `VideoConverter.cs:108-148`：webm/mkv(VP9) → **mp4** 用 `-c:v copy` 是**可行**的（`tools\convert-probe.ps1` 用真 VP9 mkv 跑到 mp4 验过）；只有 **mov** 会因为 movenc 拒收 VP9 而必须重编码，代码已单独分支（`:132-137`）→ 不是 bug。（顺带：探针脚本 `:77` 带 `-movflags +faststart` 而产品代码刻意去掉，属探针与产品参数不同，不影响结论。）
- `VideoFrameSource.cs:176-177`：`AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32, ...)` 显式要求 RGBA32、编码器声明 `-pixel_format rgba`，通道序一致（ARGB32 的 RT 读回成 RGBA32 由 Unity 做 swizzle）；`ffmpeg-probe.ps1` 里"真 BGRA"指的是探针自己造的输入，不是产品路径。
- `VideoRenderClockPatches.cs:44-51`：`TimeD/TimeF/Delta` 统一走 `UnscaledDeltaPin` 的虚拟时钟，与原生 detour **共用同一实现**，不存在"Transpiler 一套、detour 一套"的双时钟分叉。
- 音画对齐（RenderClock 路径）：视频时间轴 = 帧号/fps（钉扎到歌曲线性推进），混音轨是歌曲**实时**采样；歌曲本身按 `pitch` 实时播放，所以两条轴都是"歌曲秒"，慢渲染只会让音频长于视频、被 `-shortest` 截掉多余的音频尾部，**不产生累计漂移**（反向见 F10）。默认关闭的 RenderClock 与默认开启的音频游标兜底（`clockSeconds` 跟随 `mixdown.Seconds`）都成立。
- `VideoRenderJob.cs:1315-1420` 的幂等性：`tornDown` / `controlReleased` / `targetsRestored` / `endDetermined` 四个闸都在，`OnDestroy` 与协程收尾重复进入不会二次删目录、二次 `Destroy`。
- `VideoRenderJob.cs:1033-1055`：全局状态恢复逐项 `try/catch`，一项抛异常不会带走后面（不会留下永久静音/异常帧率）。
- `VideoRenderJob.cs:902-910`：停止键先 `Enum.IsDefined` 再转 `KeyCode`，越界配置不会把整场渲染判死。
- 取消链路：`VideoEncoder.Submit` 每 50ms 检查一次 `canceled`，`Flush` 的每次 `Submit` 都传了回调；Finalizing 阶段刻意忽略取消（`VideoRenderJob.cs:646-650` 注释说明理由，且有 10/30 分钟超时兜底）→ 不算"取消后仍生效"。
- `SystemCursor.cs:50-64`：所有 user32/gdi32 P/Invoke 都在 `Application.platform` **运行时**守卫之后（工程 `DefineConstants` 只有 `DEBUG;TRACE`，用 `#if UNITY_STANDALONE_WIN` 会永远走空分支——注释里已写明这个理由），非 Windows 平台返回 `Failed` 而不是 `DllNotFoundException`。GDI 句柄（`hbmColor`/`hbmMask`/DC/DIB/`AllocHGlobal`）全部在 `finally` 里释放，`GetDIBits` 失败路径也走到 `finally`。
- `AudioRecorder.cs:59-70`：`wavDataSizePosition` 赋值后无任何读取（`FinalizeWavFile` 直接写绝对偏移 40），是死字段；不影响行为，故不单独立案。
- `AudioRecorder.cs:296-328` 与 `:598-601`：`bufferLock` 覆盖"采集线程写盘"与"主线程关流"的交界，`hashLock` 保护 `hashAlgorithm`，`volatile isRecording/recordingClip` 只用于可见性——注释里对"volatile 不是锁"的说明与实现一致。

## 版本适配观察

**核实方式**：反射 `D:\Projects\adofai-libs\*.dll`（就是 `SpectreReplay.csproj` 链接的那批游戏自带程序集，`Private=false`），对照 `D:\Projects\adofai-libs\source\Assembly-CSharp\`（`releaseNumber = 150`）与已装游戏 Unity 6000.3.25f1。

- `UnityEngine.AudioModule.dll` → `UnityEngine.AudioRenderer` **存在且为 public**，方法恰为 `Start()` / `Stop()` / `GetSampleCountForCaptureFrame()` / `Render(NativeArray<float>)` —— `VideoAudioTrack.cs:52/73/82/120/139/140` 的用法与之一致（注意 `AudioRenderer` 在 Unity 文档里仍标为实验性，行为不保证跨版本稳定）。
- `UnityEngine.AudioSettings` 现有属性：`dspTime`、`outputSampleRate`、`speakerMode`、`driverCapabilities`、`audioSpatialExperience` —— `ReflectionTargetCheck` 只核了前三个，`VideoAudioTrack` 用到的 `dspBufferSize` 走 `AudioSettings.GetConfiguration()`（存在）。**`AudioRenderer` 的采集缓冲深度不在任何可读 API 里**，这正是 F15 只能靠日志确认的原因。
- `UnityEngine.CoreModule.dll` → `Time.unscaledTimeAsDouble` / `unscaledTime` / `unscaledDeltaTime` / `realtimeSinceStartup` / `realtimeSinceStartupAsDouble` 五个 getter 全在，`Time.captureFramerate` 可读可写（Int32）；`AsyncGPUReadback.Request(Texture, int, TextureFormat, Action)` 存在；`RenderTexture.Create()` / `Release()` / `IsCreated()` 存在。
- `UnityEngine.ScreenCaptureModule.dll` → `ScreenCapture.CaptureScreenshotIntoRenderTexture(RenderTexture)` 存在（`VideoFrameSource.cs:175`）。**语义待实跑**：该方法把拷贝安排在帧末，而代码在 `WaitForEndOfFrame` 之后紧接着发 `AsyncGPUReadback.Request`，两者在 GPU 命令流里的先后决定拍到的是"本帧"还是"上一帧"；若为后者，WholeScreen 模式会恒定慢一帧（16ms 级别的固定偏移，不会累积）。`tools\ffmpeg-probe.ps1` 验的是滤镜链而不是这条时序，故不重复报，但值得在实跑时对照帧号确认。
- `scrConductor.Update`（反编译 `scrConductor.cs:768-800`）确有两处 `AudioSettings.dspTime` 和两处 `Time.unscaledTimeAsDouble`，Transpiler 有 4 处可替换 → 现状 `TranspilerReady` 成立（隐患见 F13）。
- `AsyncInputUtils.GetSongPosition`（`AsyncInputUtils.cs:7-14`）的公式含 `scrConductor.calibration_i`；本项目的同名补丁把 calibration 传 `0.0`（见 F14）。`calibration_i => (float)currentPreset.inputOffset / 1000f`（`scrConductor.cs:233`），即玩家设置里的输入偏移，故一旦可达就是玩家可感知的偏移。
- `scrConductor` 自带 `isAudioOutputDeviceChanged` → `scrController.CheckForAudioOutputChange()`（`scrConductor.cs:770-774`），证明"渲染中途换音频输出设备"是游戏自己都会处理的一等事件 → F6 的触发条件不是理论构造。
- `scrPlanet.AsyncRefreshAngles`（`scrPlanet.cs:567-570`）是 `AsyncInputUtils.GetSongPosition` 的唯一可达上层，唯一调用者是 `AsyncInputUtils.AdjustAngle`（`AsyncInputUtils.cs:26-32`），后者被 `Patch_VideoLockAsyncAngle` 在渲染期整段拦断 → F14 当前不可达的结论成立。
- 播放倍速：`scnEditor.Play()` 对程序化调用无条件把倍速重置为 1x（`scnEditor.cs:7204-7205`），随后 `RemakePath` 里推导 `song.pitch`——本项目用 `Patch_VideoSpeedOverride` 挂在 `RemakePath` 的 Prefix 上，时机（between Play 的赋值与 pitch 推导）与源码顺序一致，`VideoRenderJob.cs:282-288` 的 `QuickPitched ? PlaybackSpeed : 1.0` 也与 `Patch_Lifecycle.cs:298` 的期望同式。
- 分辨率/帧率可达范围：`RenderWidth 320..7680`、`RenderHeight 240..4320`、`RenderFps 15..480`（`ConfigManager.cs:630-632`）。最高档 7680×4320 时单帧 RGBA 缓冲 132MB，`QueueCapacity` 会降到 2、回读池上限 3+2=5 → 约 660MB 托管大对象，`MaxInFlight` 的注释说"防止内存失控"，实际边界仍在该量级；不是 bug，但在 8K 档位上值得留一条"内存不足时错误信息要可读"的兜底（当前 OOM 会以 `OutOfMemoryException` 走 `Drive()` 的 `NotifyFailed`，消息是英文 `Exception of type 'System.OutOfMemoryException' was thrown.`）。
- `AudioSpeakerMode` 到声道的映射（`VideoAudioTrack.cs:207-218`）：`Dolby`/`ProLogic` 落到 default=2，与"编码成 2 声道"的处理一致；`GetConfiguration().dspBufferSize` 只进日志。