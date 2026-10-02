# Spectre → SpectreReplay 对照分析

> 本文基于对 `D:\Projects\Spectre` 的通读（66,775 行 / 560 文件），
> 每条结论都给出 Spectre 侧的 `文件:行号` 依据。
> Spectre 项目**只读**，本文不修改它。

## 一、Spectre 项目全貌

| 目录 | 文件 | 行数 | 说明 |
|---|---|---|---|
| `Spectre/PacLib` | 335 | 27,360 | 第三方 PACL2 图表编辑器库（内嵌资源），与回放/渲染无关 |
| `Spectre/Features` | 95 | 18,041 | 功能实现 |
| `QuartzUi` | 110 | 9,854 | 独立 UI 工程 |
| `SpectrePatch` | 4 | 1,176 | 补丁应用器库 |
| `tools` | 2 | 172 | |

`Features` 下按行数：`CustomEvents` 3041 / **`Replay` 2998** / `QuartzUi` 2335 /
**`VideoRender` 2162** / `JudgeBar` 1882 / `Editor` 1833 / `CustomJudge` 1223 /
`ResultsPlus` 915 / `Compat` 659 / `Other` 465 / 其余零散。

SpectreReplay 只实现了 `Replay` + `VideoRender` 两块，故本次重点在这两块
及其依赖的框架层（`SpectreState` / `SpectrePatch` / `FeatureKeys`）。

---

## 二、已确认的重大偏差

### 偏差 1：判定复现方式——SpectreReplay 缺失整个 `HitContext` 模型 → ✅ 已补

**状态：已实现（本轮）。** 见下面「修复 C」。以下论证仍然成立，作为实现依据。

**这是最根本的架构差异。**

Spectre 的回放是**双轨制**（两份报告一致确认）：

- **轨道 1（物理轨）`KeyEvent`** —— 真实键盘事件，被 **Win32 `SendInput` 真的打出去**
  （`ReplayPlayer.cs:148` → `KeyboardSimulation.cs:91`）。让游戏自己的输入链路自然运行。
- **轨道 2（状态轨）`HitContext`** —— 每次命中的完整快照，被
  **直接调 `ReplayHit` 复现**（`Patch_Playback.cs:126`），**绕过输入链路**。

两条轨道并行推进，**任一先耗尽即结束**（`Patch_Playback.cs:160-161`）。

`HitContext` 12 个字段（`HitContext.cs:3-30`）承载的是"按下去也不会发生"的东西：
`OverloadCounter`（失败条）、`NoFailHit` + `MarkFail()` + `BlinkForSeconds(3f)`
（miss 指示器）、`CachedAngle`/`TargetExitAngle`（行星出角）、`curFreeRoamSection`。

**为什么两条轨都需要**：

- 只有 `KeyEvent` → 画面能动，但失败条、miss 闪烁、行星出角全错
- 只有 `HitContext` → 数值完全复现，但没有输入特效/按键音/判定音

**判定精度不能靠按键保证**：`ReplayHit` 传 `hitTick = null`（`ReplayPlayer.cs:343`），
让游戏走 `GetHitMarginInDeg` 角度分支。`ReplayPlayer.cs:336-339` 解释了为什么
**绝不能透传录制的 HitTick**：

> 换算歌曲时间要减 `AsyncInputManager.offsetTick` —— 那是【本次运行】启动时才
> 校准的锚点，**每局都不同**。拿录制局的原始 tick 在回放局换算会得到完全错位的
> 时间差 → `GetHitMarginInSec` 判不出计数 → 回放强制 noFail 吞掉失败 →
> **行星原地打转、永不前进**。

**SpectreReplay 只有 `ReplayKeyEvent`，没有 HitContext。** 这意味着：

- 回放判定完全依赖注入的按键被游戏自然识别；
- 只要注入方式有问题（见偏差 2），**判定就完全不触发**；
- 即便触发，判定结果也取决于游戏当时的物理状态，而非录制时的确切上下文；
- 失败条、miss 闪烁、行星出角全都不会复现。

### 偏差 1b：`HitContext_list` 的"回滚删除"机制 → ✅ 已实现（判据按本项目的依据重写）

`Patch_Recording.cs:91-107`：每次 `Hit` 后，若检测到 **margin 统计无变化 + 地板未变**，
就 `RemoveAt(Count-1)` **删掉上一次追加的记录**。

因为游戏在同一砖上可能多次调 `Hit`（多压、auto 迭代、特效），只有 margin 或地板
变化才认定为"一次真实命中"。保留幽灵命中会让回放时多打一次 hit。

初读极易误读为"删掉刚加的"，实际删的是**上一次调用**加的。

**我们的判据没有照抄这一条。** Spectre 拿 `marginTracker.hitMarginsCount`
（判定计数向量）当"状态变了没"的依据，那需要持有整个计数数组与长度约定。
我们改用**命中时刻直接可比的那几个字段**（`HitDedupe.SameHit`）：
地板序号 + 自由漫游区段 + `NoFailHit`/`IsAuto`/`NextFloorAuto`/`RdcAuto`/
`MidspinInfiniteMargin`/`OverloadCounter`。

理由：

- 效果等价——在"同砖重复判定"这一场景下，这些字段都不变。
- **刻意不比较角度**：`angle`/`CachedAngle`/`TargetExitAngle` 每帧都在变，
  算进去则去重几乎永不触发，撤销的记录会留在轨里（正是它要防的事）。
- 地板序号**必须**参与：自由漫游区段内序号会重复，只比状态会在漫游里
  误撤真命中，画面卡在两块砖中间。

多出一条 Spectre 没有的：**地板取不到（`-1`）直接丢弃**，不录。
这种条目在回放时对不上任何砖，录进去等于凭空在错的位置触发一次命中。

### 偏差 2：按键注入方式

**已修正**：我此前说"Spectre 两路并存"，不够准确。真实结构是：

- **按键注入**只有一条路：**Win32 `SendInput`**（`KeyboardSimulation.cs:91`）。
  全仓**没有任何地方**向 `AsyncInputManager.keyQueue` 注入——
  `keyQueue` 只在 `AsyncInputManager.Setup`（游戏自己的 `RuntimeInitializeOnLoadMethod`）
  与 `scrController.UpdateInput` 之间流转，mod 不参与。

- `AsyncKeyRecorder` 是**录制侧**的可选增强（SkyHook 事件 → `keyQueue` 由游戏自己消费），
  由子开关 `ReplayAsyncInput` 控制（`FeatureKeys.cs:39`、`Main.cs:80`），
  `Settings.GetDataFromAsyncInput = false` 默认关闭（`Settings.cs:28`）。

- `Settings.LegacyEngine = true`（`Settings.cs:23`，默认）才是按键注入的**主开关**，
  UI 上的名字是「模拟按键」（`More.cs:160`）。关掉后：
  `KeyEventIndex` **照常推进**（`ReplayPlayer.cs:146` 在 `if` 之外），
  但不构造也不发送任何 INPUT —— 即"纯时间轴驱动器"，
  回放完全靠 `HitContext` 轨驱动。

所以 Spectre 的两条轨是**「KeyEvent 轨」与「HitContext 轨」**，
不是「SendInput 与 keyQueue 两条注入路径」。我之前的表述把
"录制侧的异步采集"误当成了"回放侧的注入路径"。

**SpectreReplay 曾只走 keyQueue 那一路**（已修复为 SendInput），
但仍然缺少 `HitContext` 轨（偏差 1）。

### 偏差 3：`UpdateInput` 在默认设置下不执行

`scrController.cs:2885`：

```csharp
if (!RDInput.asyncKeyboard.isActive && !...Left... && !...Right...)
    return;
```

`asyncKeyboard.isActive` 仅在玩家勾选异步输入时为真
（`AsyncInputManager.Update:147`，持久化键 `useAsynchronousInput`，默认关闭）。

所以 Spectre 自己挂 `UpdateInput` 的那个补丁（`Patch_AsyncInput.cs:12`）也是
**默认不生效的**——它是可选增强，不是主路径。

而 Spectre 的录制/回放主路径挂载点是：
`scrController.PlayerControl_Update` / `Countdown_Update` / `Checkpoint_Update`
（`Patch_Playback.cs:47-52`，三个 `[SpectrePatch]` 挂同一方法）。

### 偏差 4：渲染架构——Spectre 没有「边玩边录」

`Settings.cs:299`：

```csharp
public static int RenderCaptureMode = 0;  // 0 纯净相机 1 游戏画面(含UI)
```

**只有两种画面采集来源，不存在 Live / Offline 两种渲染模式。**
渲染恒为**离线逐帧确定性渲染**：重开关卡 → 回放接管 → 按输出帧率逐帧推进
→ 抓帧 + 抓音 → 编码 → mux。

核心机制（`VideoRenderJob.cs`）：

1. `PlayActions.SwitchToPlayMode()`（`:126`）+ 重开关卡，走既有回放启动链；
2. `AwaitReplayAlive`（`:402-429`）等回放真正接管（`is_playing` 且 conductor 已开唱）；
3. **时间钉扎**：`VideoRender.PinTimeline(audioAnchor)`（`:209`），此后每帧
   `PinnedSongPosition = anchor + clock * pitch`（`:268`）；
   `scrConductor.songposition_minusi` 的读与写都被替换（`VideoRenderPatches.cs:20-38`）；
4. `Time.captureFramerate = fps`（`:546`）+ 5 个 `Time.*` 原生 detour（`unscaled` 族）
   + 1 个 `scrConductor.Update` 的 Transpiler（把 `AudioSettings.dspTime` 等换成确定性值）
   —— 三路覆盖游戏能读到的**全部时间源**；
5. 每帧固定十步：反压排空 → `WaitForEndOfFrame` → 推进时钟 → `mixdown.CaptureFrame()`
   → `frames.Grab(i)` → 音频游标记账 → `TickFrame` → drift 巡检 → `Flush()`
   → 结尾判定。

**SpectreReplay 的「边玩边录」是自创的**，用墙钟 `Stopwatch` 定点对齐帧栅格
（`LiveSession.cs:16-18` 的注释说明了这个设计）。这条路线本身可行，但它带来
Spectre 根本不需要面对的一整类问题：

- 提前收尾（已反复踩坑：`Start_Rewind` 在进关时也会被调、
  `ProbeLevel` 的三条判据都会在加载中间态失效）；
- 音画同步只能靠栅格对齐近似，达不到 Spectre 的逐帧确定性。

### 偏差 3：音频来源——~~取 clip~~ → ✅ 已改为 AudioRenderer

**原状**：从 `scrConductor.song.clip` 取采样，日志恒为
`音频：clip 不可用（clip=null samples=0 frequency=0）`。

**根因不是时机，是原理**：ADOFAI 的歌曲是流式播放的，AudioSource 上
`clip` 恒为 null。

**已修**：改用引擎内建的 `AudioRenderer` 抓最终混音总线
（类型在 `UnityEngine.AudioModule.dll`，反编译的 Assembly-CSharp 里 grep 不到）。
配套改动见提交 `39c4666`：float32 WAV + 4GB 长度钳位 + 分桶缓冲 + 静默自愈。

闸门新增一条断言：手写 float32 WAV 走真实合流参数，验产物确实含音轨。

### 偏差 6：帧序保证——GPU 回读必须只看队首 → ✅ 结构性成立（复核结论与原判断不同）

**原判断"我们缺 Spectre 那套队列"是错的。** 复核后我们的做法不同但同样成立，
而且更简单：

| | Spectre | SpectreReplay |
|---|---|---|
| 保序手段 | 回读**队列**，交付时**只看队首** | **根本不允许并发**：一次只排一路 |
| 缓冲池 | `ConcurrentQueue` | `lock` 保护的 `Queue` |
| 编码队列 | `ConcurrentQueue` | `lock` 保护的 `Queue` |

**并发为零时"乱序"在定义上就不可能发生**，所以不需要队列，也不需要
"只看队首"。而池的 `Rent` 在主线程、`Return` 在泵线程，`lock` 一样安全
（甚至更保守：Spectre 注释里记的那次 `Dequeue` 出 null 崩溃，`lock`
结构上就不会出现）。

`FrameSourceCheck` ② 钉住"pending 检查必须排在排回读之前"。

#### 复核时发现的一处真 bug（不是帧序问题，但更严重）

`Request()` 原本**无条件**返回 true，而 `RequestCleanCamera` 开头有一条
提前返回——游戏相机列表为空就 `return`，**没有排任何回读**。

后果链条：那一帧永远不会有回调，而 `_readbackPending` 也没被置上，
所以下一帧 `Request()` 还能排上——**整个录制不会停**，但每一帧都在等一个
不会来的回调。画面停在黑帧、音频照走、帧栅格不动、**日志里什么都没有**。

相机列表为空在实际运行里很常见：切场景瞬间、切到编辑器、关卡加载中。

修法：`Request()` 的返回值改为由两个 `Request*` 的回传决定，
`_readbackPending` 只在真的排上之后才置；`WholeScreen` 那条也补了
窗口尺寸为 0 的早退（切场景时 `Screen.width` 可能是 0，
`ReadPixels` 会拿到空区域）。

这类"不报错也不停"的缺陷最难发现：一切都在正常推进，只有画面不对。
所以判据选在**配对关系**上——把原 bug 原样放回去，
`① Request() 里仍有无条件 return true` 立刻变红。

### 偏差 7：编码器看门狗 → ⚠️ 复核后**不需要加**（且我们原有那个是错的，已删）

原判断"我们缺三层看门狗"是**错的**，本轮复核后做了减法：

| Spectre 的层 | 我们的处置 |
|---|---|
| `Submit` 侧等待超时（120 s） | ✅ **已有**（`SubmitWaitMs = 120_000`），这才是真正兜住卡死的那一层 |
| `FinishVideo` 排空/退出超时 | ✅ 已有（`Finish(timeoutMs)`） |
| 泵线程"无进展"看门狗 | ❌ **原本有，本轮删掉** |

**删掉的那个是对的删法。** 泵线程原本按"超过 20 秒没有新帧进来就 break"
判断卡死。而渲染的**收尾阶段本来就不再有新帧**——最后一帧送出去之后
ffmpeg 还要排空管道、封装、写文件，这段时间上游不会再有任何 `Submit`，
队列长度就是不变。于是这个看门狗在收尾阶段**必然触发**，把一次成功的
渲染报成失败；报错又发生在用户已经耐心等完之后——"画面明明在动，
最后告诉我失败"。长视频、高分辨率、大码率的封装尤其容易踩到。

**卡死仍然被兜住，机制是反压。** ffmpeg 停止读 stdin → 泵线程不再排空 →
队列被填满 → `Submit` 的等待超时 → 返回 false → 调用方停在这一帧、
不推进帧序号、整场渲染以失败收尾。

"队列满了"比"超过 N 秒没进展"可靠得多：前者是**事实**，后者是猜。
这也是 Spectre 把 120 秒阈值放在 `Submit` 而不是泵线程的原因
（`VideoEncoder.cs:197`）。唯一代价是：泵线程空转只是个后台线程，
不占主线程，界面上无感。

#### 闸门：AvSyncCheck 加 3 条

- ④ 反向对照：产帧期间「队列长度不变」确实会误判（说明那个判据不可靠）；
- ⑤ **源码级**：`VideoEncoder.Pump` 里不许出现 `Elapsed` / `Watchdog`，
  且 `Enqueue` 的实参必须是 `SubmitWaitMs`；
- ⑤ 队列填满模型：ffmpeg 停读时队列必然填满（容量按真实算法算，不写死）。

**⑤ 那一版的第一条是自证**（断言里自己算一遍再判红，把预期反转一下就会红，
却与生产代码毫无关系）。已改为直接检查 `Pump` 的源码——旧看门狗原样放回去
必然变红，实测确认。

**第二条的第一版也是假断言**：只查"文件里出现过 `SubmitWaitMs`"，
于是把调用处实参改成 `int.MaxValue`（真的无限等）照样全绿——
常量还在，只是没人用它了。已改为判**调用点**，两种改法都被抓出来。

这已经是本项目第三次栽在同一类错误上：**检查了一个名字在，
没检查它还起没起作用**。

### 偏差 8：`-vf` 三段式，不止 vflip → ✅ 已补（并实测证明原来真的会坏）

照 Spectre 的 `VideoEncoder.RowFilter()`（`VideoEncoder.cs:343-348`）补齐：

```
(flipRows ? "vflip," : "") + "pad=ceil(iw/2)*2:ceil(ih/2)*2"
                                + ",scale=out_color_matrix=bt709,format=yuv420p"
```

**`pad` 不是保险起见，是实测会死。** 拿真 ffmpeg 试 33x25 的裸帧：

| 参数 | 结果 |
|---|---|
| 裸 `vflip`（原来的写法） | `Terminating thread with error: Invalid argument`，退出码非 0，产物 **0 字节** |
| 加 `pad` | 退出码 **0**，产物 **1560 字节** |

yuv420p 要求偶数宽高。整屏模式的分辨率直接取 `Screen.width/height`，
**用户把窗口拖成奇数宽就必然踩到**，而失败发生在第一帧——界面上的表现是
"渲染了但没有视频"，日志里只有 ffmpeg 自己那一行。

（VP9 不挑奇数，实测能过——但同一组界面里两个编码器行为不一致更难排查，
所以统一补。）

**`scale=out_color_matrix=bt709` 与 `-colorspace/-color_primaries/-color_trc`
不是一回事。** 那三个是给播放器看的**标记**，而 `out_color_matrix` 决定
**转换时用哪张矩阵**——标了 709 但转换时按 601 算，得到的仍是错的像素值。
swscale 对无标记的 RGB 默认按 BT.601 转换，而游戏输出的是 BT.709。

#### 闸门：`FfmpegCheck` ⑩（真跑，不是看字符串）

用**真实的 `FfmpegArgs.Build()` 参数**跑一遍奇数尺寸，并把产物解回来确认
尺寸被补成 34x26（而不是裁掉或拉伸）。

**为什么必须真跑**：断言"`-vf` 里含 `pad`"只能证明参数拼对了，
证明不了 ffmpeg 真的接受这个组合——中间任何滤镜名写错（`ceil` 写成
`ceil` 少个字母之类），源码检查照样全绿。变异测试把 `pad` 去掉后，
断言报的是**真实的退出码 `-542398533`**。

顺带修了两条**判据过时**（不是实现错）：⑧ 原来断言 `vf == "vflip"`，
补上滤镜后就变红了——断言要跟着实现一起长大；⑩ 的尺寸正则只认
`[SAR ...]` 那种写法，于是把 `34x26` 解析成 `0x0`。

### 偏差 9：中间文件扩展名必须跟目标容器 → ✅ 已改

照 Spectre 的 `VideoRenderJob.cs:528-532`：muxer **按扩展名**选容器，
而选 webm 时中间产物编的是 VP9——VP9 流写进 `.mp4` 在 ffmpeg 4.x 是
experimental（拒绝写入，渲染首帧即死）；`.mkv` 通吃 VP9/H.264。

**原来是"碰巧对的"**：两处都直接用目标容器的扩展名，选 webm 时刚好避开。
真正的风险是"目标容器=mp4 而编码器选到 VP9"的组合。更要命的是两个扩展名
曾写在同一个 `ext` 变量里，读代码的人会以为它们必然一致。

现已把两处（`LiveSession` 与 `OfflineSession`）的中间产物一律定为 `.mkv`，
最终产物仍按用户选的容器。`FfmpegCheck` ⑪ **两个文件都查**——
它们本来就是复制出来的，只改一个是最容易犯的错。变异测试只回退
其中一个，立刻被指到具体那行。

### 偏差 10：补丁框架的能力差距 → 部分补齐（按"会不会无声失败"排序，不按功能表补）

`SpectrePatch`（共享库 `D:\Projects\SpectrePatch`）支持：
`MethodNames`/`ClassNames` 候选链、`MinVersion`/`MaxVersion` 版本矩阵、
`AllOverloads`、`PropertyAccessor`（含 `MoveNext`）、`.ctor`/`.cctor`、
`TryingCatch` 共享 finalizer、`DisableAfterExceptions` 异常自禁用、
`Priority`/`Before`/`After` 排序、`Feature` 的 AND/OR 组合。

**没有照单全补。** 判据是「它对应的失败模式在我们这里会不会真的发生」：

| 能力 | 处置 | 理由 |
|---|---|---|
| `TryingCatch` 共享 finalizer | ✅ **补** | 见下，这是唯一一条"会无声失败"的 |
| `DisableAfterExceptions` | ✅ 补（默认 0） | 同上链上的一环 |
| `PatchKind.Transpiler` | ✅ 补（枚举 + 显式分派） | 补齐 enum 才不会新增 kind 时被静默挂成 Finalizer |
| `AllOverloads` | ❌ 不补 | 我们唯一有同名重载的 `Hit` 已在 `ParameterTypes` 上精确锁定 |
| `MethodNames`/`ClassNames` 候选链 | ❌ 不补 | 版本差异交给 `GameCompat` 按形状择路（偏差 15），不靠挂两遍补丁 |
| `MinVersion`/`MaxVersion` | ❌ 不补 | 版本判断集中在 `GameVersion` 一处，比散在每个特性里更难漏 |
| `Priority`/`Before`/`After` | ❌ 不补 | 我们没有多个补丁挂在同一目标且需要定序的场合 |
| `PropertyAccessor` / `.ctor` | ❌ 不补 | 现有目标全是普通方法 |
| `Feature` 的 `&` / `,` 组合 | ❌ 不补 | 每个功能只挂一组，没有共享补丁 |

#### 为什么 `TryingCatch` 是唯一必须补的

Harmony 把补丁体的异常**原样抛进游戏方法的栈里**。我们挂的全是
`Update`/`Hit` 这类主干方法，于是玩家看到的是"游戏崩了/卡死了"，
而日志里那一行的类名是**游戏的**——排查时根本想不到是模组。

更糟的是 Harmony 的 `MAX_HARMS` 机制：同一处补丁连续失败若干次后
**永久跳过**它，于是变成"开关开着却什么都不发生"，同样一声不响。

`Guard` 的三条约束（照 `SpectrePatchHub.cs:615-627`、`:750-812`）：

1. **只吞我们自己补丁体抛的**：按**栈顶帧**比对 `GuardedMethods`，
   认不出来、或不是我们的，一律 `return __exception` 原样上抛。
   这两条出口都不能省——省掉任何一条，Guard 就变成"游戏方法体里任何
   异常都消失"，玩法异常不再报错，玩家只会觉得"这模组有问题"。
   那比不加 Guard 坏得多。
2. **无异常时也判 `__exception == null`**：Harmony 的 Finalizer 在
   没有异常时**也会被调用**，不判会走错分支。
3. **Guard 自己的 catch 必须吞**：兜底层一旦抛出去就变成放大器。

与 `Finalizer` 那一层**不冲突、互不替代**：`Finalizer` 守的是
**游戏方法体自己**抛的异常（如 `UpdateHoldKeys` 的越界），
Guard 守的是**我们补丁体**抛的。两者都挂在同一个方法上时，
Harmony 依次执行，异常先经 Finalizer 再经 Guard（若仍抛出）。

#### `DisableAfterExceptions` 默认必须是 0

给它一个非 0 默认值听起来更"安全"，实际很糟：**一次偶发异常就永久
关掉某项功能**，而界面上看不出任何区别——开关还亮着，功能没了，
重新开模组才恢复。"宁缺毋滥"只该由具体补丁显式选择，不是全局默认。
（这一点与 `GameVersion` 的探测失败返回 -1 而非 0 是同一类判断：
**哨兵值必须让"未知"落到保守的一侧，而不是恰好能被当成有效值。**）

#### 闸门：`PatchGuardCheck`（3 条）

① Prefix/Postfix 真的把 Guard 接上了（判**调用形状** `prefix: hm, finalizer: guard`，
不是"Guard 这词出现过"——Guard 的方法定义本身就会让那种检查通过），
且 Guard 的挂载条件同时排除 Transpiler 与 Finalizer；
② Guard 的三条分支齐全（`__exception == null` 放行、认不出栈帧上抛、
不是我们的上抛），且自己的 catch 吞掉；
③ `DisableAfterExceptions` 无默认值，判据兜 0。

三条各做了变异验证（Guard 无条件挂、删掉「不是我们的就上抛」、
默认值改成 5），全部被检出。第三条的第一版报错理由是错的——
把默认值改掉后它报"找不到字段"，而字段明明在。**报错理由比报错本身
更费时间**：照着"字段不存在"去查声明，怎么也查不出来。已改为先认
字段存在（两种形态都认），再单独判默认值。

### 偏差 13：版本兼容层（`Features/Compat/GameCompat.cs`）

Spectre 编译时只引用一份 `Assembly-CSharp`（r150），凡 r150 改过签名的成员
都走 `GameCompat` 择路（`GameCompat.cs:12`）：

| 成员 | 版本差异 |
|---|---|
| `SwitchChosen` | r150 起多了 `long? hitTick` 形参（`:191`） |
| `PlayerHit` | 同上（`:223`） |
| `HitFiresInputEvents` | r150 起 `scrPlayer.Hit` 内部自调 `HitInputEvent`（`:261`），旧版由补丁 Prefix 代发——不择路会**双触发** |
| `HasUpdateHitErrorMeter` | r150 起误差条喂数搬进游戏自身（`:388`） |
| `DeadTiles` | r150 删 `scrMarginTracker.deadTiles`（`:351`） |
| `ErrorMeterAddHit` | 签名随版本变化（`:320`） |

`HitFiresInputEvents` 那条特别值得注意（`Patch_Recording.cs:44-50` 的注释）：
r150 前游戏 Hit 不自带输入触发，由补丁代发；r150 起游戏自己会触发，
补丁再代发就成**双触发**——一次按键 Any+Action1 各发一次，
且时点早于 `cachedAngle` 回写。

SpectreReplay 没有这一层。当前游戏是 r150+，我的补丁走的是
「r150 前」的语义（自己在 Prefix 里代发输入事件），一旦有对应逻辑就会出错。

---

## 三之二、本轮已修复

### 修复 A：编码队列丢帧 → 阻塞反压（偏差 5）

`VideoEncoder.cs` 的 `BlockingQueue` 原先 `Capacity = 4` 且**满了直接丢帧**。
已改为：

- 容量按 256 MiB 预算算（照 Spectre `VideoEncoder.cs:181`），1080p → 32 帧；
- `Enqueue(item, timeoutMs)` 阻塞等待，**绝不丢帧**；
- `Submit` 返回 bool，失败时调用方**不推进帧序号**（否则音画永久错位）；
- 加了 ffmpeg 进程存活检查，进程已退出立刻失败而非空等。

### 修复 B：提示条屏蔽射线（对照 `SpectreState.cs:338-341`）

`Ui/Status.cs` 原先只设文字与图标。已补：`bar.GetComponentsInChildren<Graphic>(true)`
关掉 `raycastTarget`，并由协程按原值恢复。

**为什么必须**：弹出的通知条会吃掉它下面的点击——玩家正要按砖块，
通知正好盖住，判定就没了。Spectre 明确做了这一步（`:338-341`）。

### 修复 C：补上状态轨——回放从「单轨」变成 Spectre 的「双轨」（偏差 1 + 1b）

**这是本轮最大的一块，也是 SpectreReplay 与 Spectre 最本质的架构差距。**

改动：

| 文件 | 作用 |
| --- | --- |
| `Features/Recording/Patch_HitRecord.cs`（新） | `scrPlayer.Hit` 的 Prefix 采状态；双轨互锁的闸门 |
| `Features/Recording/HitDedupe.cs`（新） | 去重判据（纯逻辑，可离线验证） |
| `Features/Recording/ReplayData.cs` | `ReplayHitContext` 11 字段 + v4 格式 |
| `Features/Recording/KeyRecorder.cs` | `RecordHit` 落库；`Reset`/`Begin` 清轨 |
| `Features/Playback/HitStateReplayer.cs`（新） | 状态轨推进 + 命中复现 |
| `Features/Playback/PlaybackEngine.cs` | 双轨编排、结束条件、互锁标志 |

**为什么必须有它。** 键轨让游戏自然地跑判定，而游戏按**当帧的行星角度**
算判定窗口。回放时注入的按键落在录制容差之外是常态（量化到一帧 +
`SendInput` 有一帧延迟），于是某次判定判成 Fail/Overload → 行星停在原地
不前进 → 用户看到「回放卡住了」，而且**日志里没有任何异常**。状态轨把
`angle`/`TargetExitAngle`/`overloadCounter`/`midspinInfiniteMargin` 按录制值
覆盖回去，判定因此复现，不依赖注入的按键落在哪。

**四处关键决策，每一处都有代价：**

1. **挂在 Prefix 而非 Postfix**（同 Spectre `Patch_Recording.cs:26-35`）。
   `Hit` 一进来就 `SwitchChosen`（`scrPlayer.cs:761`）把行星切到下一块，
   之后再取样拿到的是**下一块砖**的 angle。回放时写回去 = 提前半块砖转过去，
   且逐块累积。

2. **双轨互锁，且 `HitLocked` 必须排在 `IsRecording` 之前。**
   状态轨期间键轨触发的 `scrPlayer.Hit` 必须被掐掉，否则同一块砖判两次。
   顺序是关键：回放时录制是关的，`IsRecording` 恒为 `false`，互锁放在它后面
   就永远走不到。**这一条本轮自己踩过**：初版就写反了，是闸门抓出来的。

3. **绝不透传录制的 HitTick**（同 Spectre `ReplayPlayer.cs:336-343`）。
   录制时干脆不记它。理由见上面的引用：它是 SkyHook 绝对 tick，换算要减
   **本次运行**启动时校准的 `AsyncInputManager.offsetTick`，拿录制局的 tick
   在回放局换算 → 判定算不出数 → 被 noFail 兜住 → **行星原地打转（"卡输入"）**。

4. **不走 `ReplayHitOld` 手工复刻，直接调 `scrPlayer.Hit`。**
   老式复刻要自己调 `SwitchChosen`、喂判定条、管相机脉冲，抄漏一处就与真机
   分叉；直接调 `Hit` 让**游戏自己的代码**推进行星，跨版本自动成立。
   代价是签名跨版本变了（`Hit(bool)` → `Hit(long?, bool)`），必须**按参数形状
   反射**调用并同时认两种形状（照 Spectre `GameCompat.PlayerHit`）。

**为什么 11 个字段不是 12 个**：Spectre 的第 12 个是 `HitTick`，
按上面第 3 点刻意不录。其余 11 个在回放路径上**各自**都被读写，少一个就有一处
状态被回放污染。

**闸门**（`HitTrackCheck`，5 条 + `ReplayCheck` 4 条）：

- 状态轨 11 字段逐项往返（8 个是 bool/float，**恰是"写漏一个也读不出垃圾"的
  类型**，必须逐字段比）；
- v1 / v3 文件仍能读出且状态轨为空（每次加字段都要有一条对应断言——
  这正是 v2 加字段时踩过的同一个坑）；
- 荒谬条数（`int.MaxValue`）被拒（读取发生在点击事件里，无上限就是当场申请 2G）；
- 去重四组边界、互锁顺序、hitTick 为 null、结束条件真比较两个游标、
  反射认两种签名。

**每条断言都做了变异验证**（回退实现必须真的变红）。这一步抓出两件事：
一是我把去重断言的下标写错了（验的是副本，真代码没错）；二是
**`BothTracksMustFinish` 第一版是假断言**——它只查变量名 `hitsDone` 出现过，
而 `bool hitsDone = true;` 照样通过。已改为要求真的出现
`Cursor >= …Hits.Count` 形状的比较。

顺带把去重从 `KeyRecorder` 拆成 `HitDedupe.cs`：原先判据内联在碰引擎的类里，
闸门只能**复刻**一份——那等于自己测自己，真代码改错了副本照样全绿。拆成纯逻辑
文件后预览器直接链接，验的就是真代码。变异测试确认：真代码去掉同地板判断 /
去掉撤销逻辑，闸门均变红。

---

## 三之三、Replay 侧新增偏差（据深读报告补充）

### 偏差 14：无回放前置校验 → ✅ 已加（提交 `79eb052`）

已实现六道：官方谱关卡名 / 谱面指纹 / 移调+BPM / 判定难度 / 起点区间 / 倍速。
ReplayData 升到 v2 承载这些字段，v1 文件仍兼容。

**两处故意不同于 Spectre**（Spectre 强制 14 键存在，缺就报错）：
取不到的字段一律跳过检查；FloorHash 用 `int.MinValue`、
SpeedTrail 用 `<= 0` 作「没记到」的哨兵 —— 0 与 1.0 都是合法值，
拿它们当默认值会让「没记到」与「确实是那个值」无法区分。

### 偏差 15：版本兼容层完全缺失 → ✅ 已加（`Patch/GameCompat.cs`）

**这是本轮实测，不是照抄。** 先在**真程序集**上查了每一行到底适不适用：

| 成员 | 实测结果 | 对我们是否适用 |
|---|---|---|
| `scrPlayer.Hit` | 只有 `Hit(long?, bool)` | ✅ **适用**，是唯一真会打到我们的 |
| `scrPlanet.SwitchChosen` | 只有 `SwitchChosen(long?)` | ❌ 不适用——我们不直接调，由游戏在 `Hit` 内部调 |
| `HitFiresInputEvents` | r150 的 `Hit` 内部确实自调 | ❌ 不适用——我们没有代发 `HitInputEvent` 的代码 |
| `HasUpdateHitErrorMeter` | 存在（3 参） | ❌ 不适用——我们不手动喂判定条 |
| `scrMarginTracker.deadTiles` | **已删除** | ❌ 不适用——我们从没读过它 |

**结论与原判断不同：五项里只有一项真的适用。** Spectre 需要整层是因为它改写
判定与判定条，那几处都要按老签名调；我们的功能面窄，只有"调用 `Hit`"
这一个入口踩在跨版本的缝上。原文档把它们并列成"五处待处理"会误导——
照着做等于为四个用不到的通道各写一份永远不会执行的代码。

**保留的通道**（`SwitchChosen` / `HitInputEvent` / `HasUpdateHitErrorMeter`）：
留着不调用，成本是几十行；将来加功能时**不需要重新踩一遍版本坑**。
理由同 Spectre——那几条注释是写给"下一个加功能的人"的。

**一处实测纠正了 Spectre。** Spectre 的版本探测按 `{"GCNS", "Releases"}`
顺序找 `releaseNumber`（`JALibMissing.cs:30`）。实测本版本：

- `GCNS` **存在**，但只有场景名/分支列表，**没有任何版本字段**；
- 真正的版本在 `Releases.releaseNumber`（Int32，当前值 **150**）。

照 Spectre 的顺序取，今天也能拿到 150（第一个类型里没有该字段，继续找），
但那是**巧合性正确**：GCNS 一旦加上同名字段就会取错。而版本取错的后果是
**升级之后反而走进旧路径**——比取不到更糟。所以我们显式按
"找名为 `releaseNumber` 的 Int32 字段"写，并**以 Releases 优先**，
顺序本身被断言钉住（见下）。

**探测失败返回 -1 而不是 0**：0 会被 `rel >= 150` 判成"旧版"，
于是升级之后走旧路径——最坏的失败方向。

**顺带消除一处重复实现**：上一轮我在 `HitStateReplayer` 里自己写了一份
`FindHitMethod`（按参数个数找 `scrPlayer.Hit`），与兼容层要做的是同一件事。
两份必然走偏，而走偏的那份恰好在**调用命中**的位置——表现为
"回放卡住且日志无异常"。现已合并为 `GameCompat.PlayerHit` 一处。

#### 闸门：`CompatCheck`（7 条）

这一条是本项目少有的能**离线跑真东西**的地方：直接
`Assembly.LoadFrom(adofai-libs\Assembly-CSharp.dll)`，
把与兼容层同形的择路算法跑在**真元数据**上，看它挑中的方法是否就是
游戏自己的定义。

只断言源码里"有按参数个数分支"是没用的——分支写对了但探测条件写错了
（比如按名字取第一个而不看返回类型），源码检查照样全绿。

七条：① `Hit` 选中 r150 重载（带返回类型校验）② `HitFiresInputEvents`
形状探测为 true ③ `SwitchChosen` 选中 1 参 ④ `HitInputEvent` 选中 3 参
④b `handleAll` 传 false ⑤ 版本探测命中 `Releases.releaseNumber = 150`
⑤b **探测顺序为 Releases → GCNS** ⑥ 命中反射只有兼容层一份实现。

**三条断言是变异测试逼出来的**（都已证明回退实现时变红）：

| 变异 | 结果 |
|---|---|
| 探测顺序反成 GCNS 优先 | **第一版漏掉了**，补了 ⑤b |
| 删掉 `Hit` 的 1 参降级分支 | 命中（代码仍能编译） |
| `handleAll` 传 `true` | **第一版漏掉了**，补了 ④b |

后两条的共同点值得记住：**形状对了不等于参数对了**。
`handleAll` 从 false 改成 true 时，上面所有形状检查都还是绿的。

### 偏差 16：`FloorHash` 的可靠性陷阱（即使照抄也要知道）

| 条件 | 算法 | 跨进程稳定？ |
|---|---|---|
| 官方关卡 | 恒 `0` | ✅ |
| `levelData.isOldLevel` | `pathData.GetHashCode()` | ❌ `List<T>` 的**对象身份哈希**，每次运行都不同 → 这条路径**永远无法匹配** |
| 新版谱面 | `ComputeAngleHash` | ⚠️ 依赖 `string.GetHashCode()` 的进程内种子 |

即 Spectre 自己的旧格式路径也有这个 bug。抄之前要知道。

### 偏差 17：哈希的区域设置陷阱

`Patch_Lifecycle.cs:332-336`：

> 数值统一 `InvariantCulture` 格式化：逗号小数区不再因区域差异在他机重算出
> 不同哈希而误报 `play_error2`。

我已在 `FfmpegArgs.cs` 用 `CultureInfo.InvariantCulture` 处理过帧率，
但哈希计算还没接触到。

### 偏差 18：`scrPlayer.UpdateHoldKeys` 的 Finalizer 兜底

`Patch_Playback.cs:26-42` 详述了一个**游戏自身的潜伏 bug**：

> 命中 midspin 时 `Hit()` 尾部会向 `keyTimes` 自补一个待处理按键，函数末段
> midspin 分支的第二次 `RemoveAt(0)` 假定这个补充一定还在。若中途那次 `Hit()`
> 提前返回 false（无响应窗口、忽略输入特效等），第二次 `RemoveAt` 就在空表上
> 抛越界。正常游玩几乎凑不齐触发条件；**回放引擎按量化时间注入按键，偶尔恰好凑齐**。

**回放比真人对它更容易触发。** Spectre 用 Finalizer 吞掉
（仅在 `is_playing` 且异常类型精确匹配时），理由是「抛出只中断当帧的输入模拟——
回放状态每帧由录制数据覆盖，吞掉不影响回放/成片」。

**SpectreReplay 没有这个兜底**——一旦回放触发到这条路径，会抛
`ArgumentOutOfRangeException` 打断输入更新。

### 偏差 19：回放期间的三个状态夹逼

Spectre 回放时改了若干全局状态，都有对应的还原点：

| 状态 | 夹逼方式 |
|---|---|
| `HitLocked`（屏蔽真实 `Hit`） | `Patch_Recording.cs:39` 返回 false；`ReplayHit` 内部 `:335/:344` 临时开合 |
| `GCS.hitMarginLimit` | `Patch_Misc.cs:86/92` 在 `LightUp` 前后夹逼；`ReplayPlayer.Stop():36` 终还原 |
| `noFail` | `TryStartPlayback:225` 备份后强制 true；`Stop():54` 还原 |
| `IgnoreMarkFail` | `Patch_Misc.cs:75` Prefix 令 `__result = null` |

**SpectreReplay 的对应实现**（本轮随修复 C 一并补上）：

| 状态 | 我们的做法 |
|---|---|
| `HitLocked` | ✅ `HitStateReplayer.Locked`，在 `Pump` 里开合；`Patch_HitRecord` 的 Prefix 据此掐掉键轨触发的 `Hit` |
| `noFail` | ✅ `ClampGlobalState` 备份后强制 true，`Stop` 还原 |
| `GCS.hitMarginLimit` | ✅ `scrFloor.LightUp` 前后成对夹逼（`PlaybackClamps`），期间置 `PerfectsOnly` |
| `IgnoreMarkFail` | ✅ `scrPlanet.MarkFail` 的 Prefix，见「修复 D」 |

**`LightUp` 夹逼为什么置 `PerfectsOnly`**：`LightUp()`（`scrFloor.cs:1200`）读
`GCS.hitMarginLimit != PerfectsOnly` 来决定要不要 `SetToRandomColor()`。
夹成 `PerfectsOnly` 就是让它**一定跳过**——砖块颜色是**录进视频里的**，
中途变了成片就与录制不是一回事。方法体跑完（Postfix）立刻恢复成录制档位，
让后续判定照常按录制时的档位飘字。

只夹一次（只在开播时设）不够：游戏会在别处把 `GCS.hitMarginLimit` 写回
玩家真实设置，砖块亮起时读到的可能已经是玩家自己的档位。

### 修复 D：回放期间的 `IgnoreMarkFail`（偏差 19 剩余）

`scrPlanet.MarkFail()`（`scrPlanet.cs:1626-1634`）**无条件** `Instantiate`
一个 failIndicator 并挂到 `next` 行星上。回放期间若有真人误触（或键轨的按键
被游戏判失败），屏幕上会凭空多出一个红色指示器，**而且它跟着行星一路飞出画面**
——成片里就是一团跟着跑的红色鬼影。

做法照 Spectre 的 `Patch_MarkFail`（`Patch_Misc.cs:73-81`）：Prefix 里
`__result = null; return false;`。

**但不能无条件屏蔽**：状态轨要复现录制时的 noFail 闪烁，它自己就得调
`MarkFail()`。所以做成 `MarkFailScope(bool, Action)`：由 `ReplayHit` 在
需要复现时开屏蔽范围调用。

**必须用 try/finally 而不是"设了再改回来"**：`BlinkForSeconds` 之后若还有代码
抛异常（协程启动失败等），裸赋值会让标志**永久留在 true**——
之后玩家整局游玩都不会再看到 miss 指示器，而且完全无从察觉。

---

## 四、下一步

按严重度依次修复，每项补离线断言并验证。优先级：
1. ~~偏差 3（音频取 clip，原理上不成立）~~ ✅
2. ~~偏差 14（校验）~~ ✅
3. ~~偏差 1 + 1b（HitContext 状态轨，工作量最大）~~ ✅
4. ~~偏差 19（回放期间的状态夹逼四项）~~ ✅
5. ~~偏差 15（版本兼容层）~~ ✅
6. ~~偏差 6/8/9（帧序、`-vf` 三段式、中间文件扩展名）~~ ✅
7. ~~偏差 7（编码器看门狗，复核后做了减法）~~ ✅
8. ~~偏差 10（补丁框架：异常兜底 + Transpiler 分派）~~ ✅
9. 状态码 3/5/6 → 6/5/7 对齐