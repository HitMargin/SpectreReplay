# Spectre → SpectreReplay 对照分析

> ## ⚠️ 阅读前必读：本文大部分内容已随架构替换而失效（2026-10-04 起）
>
> **提交 `f96aa86`「整体移植 Spectre 的 replay 与视频渲染，废弃自研引擎」把整个
> replay + video render 换成了 Spectre 的源码。** 自研引擎（`PlaybackEngine` /
> `PlaybackClamps` / `KeyRecorder` / `LevelWatcher` / `LiveSession` /
> `OfflineSession` / `PatchLoader` / `HitStateReplayer` / `PlaybackValidator` /
> `RenderService`）**已全部停用，主项目里一个都不在了**（当时的存档目录已删除，
> 需要翻旧实现时用 `git show f96aa86^:<路径>`）。
>
> 因此下面这些偏差描述的代码**已经不存在于运行路径**，它们的"已修"结论
> 只对旧架构成立，读的时候不要当成现状：
>
> | 失效条目 | 原因 |
> |---|---|
> | 偏差 18（`UpdateHoldKeys` Finalizer） | 兜底在 `PlaybackClamps`，已停用；Spectre 侧对应物是 `Patch_Playback.cs` 的 `Patch_HoldKeysCrashGuard`（已随移植带入） |
> | 偏差 19（noFail / hitMarginLimit / IgnoreMarkFail 夹逼） | 在 `PlaybackClamps` 与 `PlaybackEngine`；Spectre 侧是 `Patch_Misc.cs` 的 `Patch_MarkFail` / `Patch_LightUp`（已随移植带入） |
> | 偏差 22（未禁 `UpdateFreeroam`） | `HitStateReplayer` 的事；Spectre 侧是 `Patch_Misc.cs` 的 `Patch_UpdateFreeroam`（已随移植带入） |
> | 偏差 23（`Stop()` 只有按钮一个调用点） | `PlaybackEngine` 的事；Spectre 侧是 `Patch_Lifecycle.cs` 的四条拆除链（已随移植带入） |
> | 偏差 24（hitbox 死亡绕过 noFail） | 屏障在 `PlaybackClamps.cs`，已停用 |
> | 偏差 15 / 10 / 6 / 7 / 8 / 9 | 全部关于 `PatchLoader` + 自研 `VideoEncoder` / `FrameSource`，均已替换 |
> | 第七节「累计的变异验证」（MUT-A…MUT-BZ） | 那一轮变异的对象已停用；现行变异见 `tools\mutate.ps1`（MUT-P*/U*/K1/A*/C*/T1/R1/L*/V*） |
>
> **仍然有效的部分**：偏差 1 / 1b / 2 / 3 / 3a / 4 / 14 / 16 / 17 / 20 的分析与论证
> （架构差异、按键注入、音频来源、前置校验、哈希陷阱、命中误差分析），
> 以及第七之二节「其余六个特性目录为什么判定为不补」的结论。
>
> **现行架构与闸门现状看这两份**：`docs\verify.md`（57 条断言与用法，
> 数字随轮次变，引用前先跑一遍 `verify.ps1`）、记忆库项目条目（SpectreReplay）。

> 本文基于对 `D:\Projects\Spectre` 的通读（66,775 行 / 560 文件），
> 每条结论都给出 Spectre 侧的 `文件:行号` 依据。
> Spectre 项目**只读**，本文不修改它。
>
> 写于自研引擎时代（2026-10-02 ~ 10-03），保留作为历史记录。

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

### 偏差 3a：`UpdateInput` 在默认设置下不执行

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

**这就是我们也从不在 `UpdateInput` 上挂钩的原因。** 挂在那里的补丁在默认
设置下不会执行——补丁"挂上了"、日志无异常、功能却完全不生效。

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

`FrameSourceCheck` ③ 用**行为**验这件事：上一路没回来时排不上第二路
（并发为零 → 帧序天然正确），回调到达立刻恢复。

#### 复核时发现的两处真 bug（不是帧序问题，但更严重）

**甲：`Request()` 原本无条件返回 true。** `RequestCleanCamera` 开头有一条
提前返回——游戏相机列表为空就 `return`，**没有排任何回读**。

后果链条：那一帧永远不会有回调，在飞标记也没被置上，所以下一帧
`Request()` 还能排上——**整个录制不会停**，但每一帧都在等一个不会来的
回调。画面停在黑帧、音频照走、帧栅格不动、**日志里什么都没有**。

相机列表为空在实际运行里很常见：切场景瞬间、切到编辑器、关卡加载中。

修法：`Request()` 的返回值改为由两个 `Request*` 的回传决定，
没排上就不进入"在飞"状态；`WholeScreen` 那条也补了窗口尺寸为 0 的早退
（切场景时 `Screen.width` 可能是 0，`ReadPixels` 会拿到空区域）。

**乙：整屏取帧的回调是同步的，置位落在清位之后 → 永久卡死。**

`RequestWholeScreen()` 走 `Texture2D.ReadPixels`，而
`NativeArrayToBytes` 在返回之前就**同步**调用了 `OnWholeScreenReady`。
原写法是"先发请求、拿到 true 再置位"，于是回调里的**清位发生在置位之前**——
置位之后再没有人清它。第 2 帧起 `Request()` 恒返回 false：

- Live 模式：成片是**第一帧的静止画面**（时长与音轨都对，看着能用、内容全错）；
- 离线模式：编码器只收到 1 帧，渲染三分钟的歌产出一帧的视频；
- 全程不报错、不停机、日志全无。只有 `Surface = 整屏`（非默认）才走这条路，
  所以默认路径一直是好的——这正是"选了某个选项才废"那一类。

修法：把"在飞"状态抽成不碰引擎的 `ReadbackGate`
（`SpectreReplay\Features\VideoRender\ReadbackGate.cs`，链接进离屏预览器）：
**先置位、再发请求**，请求返回"没排上"时再把置位收回去，
请求体抛异常时也要把闸门放开。同步与异步两种回调顺序因此都成立。

这类"不报错也不停"的缺陷最难发现：一切都在正常推进，只有画面不对。
所以甲、乙的判据都不落在"代码里有没有 `return false`"上：

- 甲 → 接线断言（`⑤`：两条取帧路都必须经过闸门、返回值来自被调方、
  不许无条件 `return true`；`⑥`：两个回调都必须通知闸门放开）；
- 乙 → **行为断言**（`①`）：时序问题读源码怎么看都像对的，只能按同步顺序
  真的跑一遍。把乙原样放回去（变异 `MUT-CB`：只把置位搬到请求之后），
  断言 ① 立刻变红，而结构断言 ⑤/⑥ 保持绿——**源码形状本来就"像对的"**。

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

### 状态码 `3/5/6` → 结算状态：✅ 已改（原判断的注释本身就是错的）

最后一笔，也是唯一一个**原判断自己就写错了**的偏差。

原注释写的是「只在 3/5/6 状态（游玩中/暂停/结算）响应」。照真程序集读枚举：

```
0=None  1=Start  2=Countdown  3=Checkpoint
4=PlayerControl  5=Fail  6=Fail2  7=Won
```

`3/5/6` 是**存档点 / 失败 / 失败二**，与注释说的"游玩中/暂停/结算"
完全是两回事，而**真正的结算状态 7 被漏掉了**。

后果正是"保存键在游玩中没反应"——正常一局走 `PlayerControl(4)`，
压根不在这三个数里，而玩家恰恰是在游玩中想存一局。唯一能触发的是
打了存档点或者已经失败了，那时数据里只剩最后一段。

Spectre 用的是 `6/5/7`（`Fail`/`Fail2`/`Won`，`Patch_Misc.cs:41` 与 `:57`），
即**只在结算后存**。已照此改用枚举名，不再写魔数。

**为什么不包含 `PlayerControl`**（即"游玩中不能存"）：回放数据必须是
**跑完的一整局**，存到一半的数据重放出来是断的。这个取舍是 Spectre 定的，
不是本项目拍的板。

顺带一条通用教训：**魔数 + 注释是最容易双双失真的写法**。注释里的
"游玩中/暂停/结算"读起来完全合理，只有把枚举真值打出来才看得出它对不上。
凡是状态判定，一律用枚举名——这样游戏改版时是**编译错误**，
而不是静默指向另一个状态。

#### 闸门：`StateCheck`（2 条）

① 快捷键按 `States` 枚举名判 `Fail`/`Fail2`/`Won`，且**不含**
`PlayerControl`/`Checkpoint`/`Start`/`Countdown`/`None`，这一段里
**不允许出现任何状态魔数**，也不许"先转 int 再比"；
② **从真程序集读 `States` 枚举值**并与写死的期望逐个比对。

② 不是多余的：① 只验"代码写了 `States.Fail`"，验不了 `States.Fail`
此刻是不是 5。游戏改枚举时 ② 会红而 ① 仍然绿，那时就需要重新确认
哪个状态才是结算。

（`Enum.GetName` 只需要元数据、不需要加载 UnityEngine，
所以这也能离线跑——与 `CompatCheck` 同一个前提。）

两条都做了变异验证：退回魔数 `3/5/6`、把 `PlayerControl` 加进去，
都被检出并指到具体理由。

### 偏差 13：版本兼容层

已被偏差 15 那轮覆盖（实测后确认五项里只有 `scrPlayer.Hit` 真的适用），
此处不再重复。

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

## 四、清单之外新发现的偏差（重列特性文件时顺手查出来的）

### 偏差 20：`ReplayAnalyzer` 整块缺失——但它要的数据我们早就存了

重列 Spectre 的 `Features/Replay` 文件清单时，发现两个我们完全没有对应物的
文件：`ReplayAnalyzer.cs` 与 `ReplayCrypto.cs`。

**`ReplayAnalyzer.cs`（:9-16）——补了。** 它从已录的 `HitContext` 直接还原
每次命中的毫秒误差，不进游戏、不回放：

```
ms = 角差(rad) × 60 / (π × pitch × bpm×speed) × 1000
```

**关键在于：`CachedAngle − TargetExitAngle` 这个角差我们早就存了**——
它是偏差 1/1b 补的状态轨字段之一。但此前那个差值**只被回放用来推进行星、
从没被读出来看过**：录进去、写回去、推角度，仅此而已。它其实就是
"这一击偏了多少"，而这正是玩家最想知道的事（整体偏早，还是某几处
单独拖后腿）。

已补 `Features/Playback/HitTimingAnalyzer.cs`（纯计算，不碰引擎），
并在待播区显示统计：已分析击数 / 偏早 / 偏晚 / 中位数 / 均值 / 标准差 / 范围。

三处与 Spectre 的差异，都是有意为之：

- **不算 Perfect/Pure 窗口宽度**。（**2026-10-17 更新**：现由
  `Features/Compat/MarginBoundary.Raw` 提供——它就是从已删除的
  `CustomJudge.BoundaryRaw` 摘出来的窗口数学，见下「CustomJudge 移除」一节。）
- **`TimingReport()` 返回 null 表示"算不了"，不返回空报告**。
  画一个空分组会让人以为"分析结果是 0"，而真实原因只是"还没载入"。
- **每次重算，不加缓存**。它只在 OnGUI 里调，而逐砖 speed 会随关卡推进变化，
  缓存需要一个"什么时候变脏"的判据——那个判据比算术本身更容易错。

三个跳过规则照 Spectre（:84-98）：`IsAuto`、midspin 无限判定、
`bpm×speed` 趋零。**跳过的要计入 `TotalHits` 并在界面上说出来**，
否则玩家会以为分析漏了数据，或者更糟——以为那些击的误差是 0。

#### 闸门：`TimingCheck`（5 条，链接真实源码）

① 角差 0 → 0 ms；② **符号方向**（正 = 晚按）；③ 手算基准
`0.05 rad @ bpm×speed=120` → 7.9577 ms；④ 角度↔弧度往返一致；
⑤ 跳过的命中计入总数、不进统计。

**必须链接真实源码而不是复刻**：复刻的算法和被测算法写错时会**一起错**，
断言全绿而实现是坏的。③ 那条钉住 `π` 与 `60`/`1000`——任一处写错都会得到
一个"看着合理"的毫秒数，**不会报错**，只是让玩家以为自己偏手 20ms。

两条都做了变异验证：把 `π` 换成 `180`（实得 0.1389 ms）、把符号反转
（实得 −7.9577 ms），都被检出。

顺带避掉一个自己埋的坑：逐砖 `bpm×speed` 传进 `List<int>` 时用了
`(int)Math.Round` 而非强转——强转会在 bpm=120.5 的谱面上截成 120，
误差 0.4%，累积成一整局的系统性偏差。

### `ReplayCrypto.cs`（:12-13）——不补，而且不该补

它的"加密"是：

```csharp
private const string KeyStorageKey = "qwerty";
private const string IVStorageKey  = "potato";
```

硬编码密钥、明文可见。这是**混淆**（防玩家直接翻文件改难度），
不是加密（防别人读懂）。硬套 AES 在这里只带来三样东西：
密钥在源码里、IV 固定所以确定性强、玩家改不了自己的回放文件。

我们不复制它的格式、也不加自己的加密：回放是**本地单人练习数据**，
加密对它没有意义；而一个"看起来有加密、其实钥匙就在旁边"的设计
反而会让人误以为它安全。同理 旧容器格式(`.crpl` JSON / `.crpl2` CRP2)
也不移植——我们只有 `.sprp` 一种容器(2026-10-18 旧格式适配已整体删除，见 `ReplayManager` 顶部注释)。

### `Compat/HitMarginCompat.cs`（341 行）——不补，但**核过一遍确认不用补**

这份差点被我当成漏项。它解决的是真问题，值得说清楚为什么我们不需要。

r150 重排了 `HitMargin`：**删掉 `Perfect`**，新增 XPerfect / PerfectPlus /
PerfectMinus / Midspin / FailedFloor，其后所有成员索引整体后移
（LatePerfect 4→6、VeryLate 5→7、TooLate 6→8、Auto 10→12、OverPress 11→13）。
而**枚举成员是编译期内联常量**：同一个 DLL 只能携带一套数值，所以任何直接
写下的 `HitMargin.Xxx` 在另一个版本上都会**静默指向别的成员**。
Spectre 的做法是运行期按**名字**解析（`Enum.TryParse`），并额外拒绝纯数字串
——`Enum.TryParse` 会把 `"6"` 当合法值，那是版本相关的陷阱。

照真程序集读当前的 `HitMargin`（16 个成员）：

```
0=TooEarly   1=VeryEarly  2=EarlyPerfect  3=PerfectMinus  4=XPerfect
5=PerfectPlus 6=LatePerfect 7=VeryLate   8=TooLate       9=Multipress
10=FailMiss 11=FailOverload 12=Auto     13=OverPress    14=Midspin
15=FailedFloor
```

**我们不需要那 341 行**，因为**全项目一处 `HitMargin.Xxx` 都没写**（grep 确认）：
判定记账用 `margin.ToString()` 取名字当字典键（`KeyRecorder.cs:438`），
补丁签名用的是 `HitMargin` **类型**本身（`Patch_Recording.cs:253`）。
类型引用在跨版本时不会错，错的是**成员**。

我们唯一持久化的枚举是 `HitMarginLimit`（只有 3 个成员：
`None`/`PerfectsOnly`/`PurePerfectOnly`，且 `PlaybackEngine.cs:335`
按 `Enum.GetValues` 的长度做了越界退回）。3 个成员的枚举没有
`HitMargin` 那种大规模重排的风险。

**这条结论只对本机 r150 成立**，所以它不该只是一段散文。

先记一个顺带查出来的事实，它改变了这条风险的描述：**写死一个
**被删掉**的成员是编译错误，不是静默漂移**——实测把
`HitMargin.Perfect`（r149 才有）写进代码，编译直接报
`CS0117: “HitMargin”未包含“Perfect”的定义`。

所以 Spectre 那 341 行真正防的是**另一种情况**：成员**还在、但索引挪了**
（`LatePerfect` 4→6、`TooLate` 6→8…）。那种情况编译通过、
运行也正常，只是**判定显示错**——那才是必须靠断言守的地方。

已加断言（`StateCheck` ③）：扫全部源文件，**不许出现任何
`HitMargin.Xxx` 成员引用**，只豁免 `Type` 上的静态成员
（`GetValues`/`IsDefined`/`TryParse`…，那些是运行期查表，不漂移）。

两条都做了变异验证：
- 写死 `HitMargin.LatePerfect` → 立刻被指名到文件与成员；
- 把真实成员名塞进静态白名单试图豁免 → 变红（附带的编译错误本身
  也说明"被删的成员"那条路径是编译期就断的）。

**这就是清单需要复核的第二个理由**：
第一处是偏差 20（漏掉的功能），第二处是这里（差点当成漏项、
实则不需要，而"不需要"这个结论原本没有任何断言守着）。

---

## 五、复核其余特性目录时又挖出来的（偏差 21 / 22）

把 Spectre 剩下 12 个特性目录（Other / Editor / CustomJudge / JudgeBar /
ResultsPlus / Perf / PlanetColors / CustomEvents / EffectRemover / QuartzUi）
连同 89 处 `[SpectrePatch]` 挂点逐个核完。绝大多数判为**产品形态差异**
（自定义判定、判定条美化、编辑器 UI、星球配色），不影响录制·回放·渲染的
正确性，不补。但核出来两条真缺口：

### 偏差 21：离线渲染的"钉时间轴"**从来没有生效过**

这是本轮最重的一条。原判"我们钉了 dspTime，应该能钉住歌曲位置"，
实测**一帧都没钉住**，而且没有任何症状。

**为什么无效**（全部来自真程序集反编译 `scrConductor.cs:768-880`）：

| 步骤 | 时机上发生了什么 | 后果 |
|---|---|---|
| `:852` | `songposition_minusi = (dspTime - dspTimeSong - calibration_i) * pitch - addoffset;` 在 `Update` **内部**算完 | 我们挂在 Postfix 上，改 dspTime 时这个值早已定死 |
| `:779` | 下一帧 `dspTime = AudioSettings.dspTime` **无条件**覆盖 | 我们写进去的值根本留不住 |

结果：画面照出，只是"离线"这件事其实按真实时间在跑。掉帧时音画就错开，
而日志里什么都没有——**看上去在工作的缺陷最难发现**。

**修法**：钉点换成 `scrConductor.songposition_minusi` 的**属性访问器**，
读写两端都接管（`Accessor = PropertyAccessor.Get` 的 Postfix 改
`__result`，`Accessor = PropertyAccessor.Set` 的 Prefix 改 `value`）。
照 Spectre 的 `VideoRenderPatches.cs:16-38`。

**为什么必须两端都做**：只做 Get 的话 `_songposition_minusi` 仍是真实时间，
下一次 `:858` 的 `deltaSongPos = new - old` 会变成"钉扎 − 真实"的巨大跳变；
只做 Set 的话所有读点仍拿真值。缺哪一端都退回"没钉住"。

**顺带的坑**：`Update()` 的结束判据原来读 `songposition_minusi`。
读端被接管之后读出来永远是钉扎值（i/fps，单调递增），`pos < -0.5`
永远为假 → 歌曲早就放完了渲染还在跑。改成读写补丁里抄下来的真实位置，
初值用 `double.NaN`（判据要能区分"还没收到"——给 0 会在第一帧就收尾）。

**另一处值域差异**：Spectre 是 `anchorSongPosition + clock.Time * pitch`
（**乘 pitch**），我们不乘。因为我们钉的是 `songposition_minusi` 本身，
它与谱面里每个砖的 `entryTime` **同一域**（都已按 pitch 缩放过）；
把 Spectre 的公式照搬过来会多乘一次，pitch=1.5 的曲子每帧都快 50%，
而画面"看着基本对得上"。这条已钉成断言（MUT-P 验证）。

为此给补丁框架加了 `PropertyAccessor { None, Get, Set }`：
`GamePatchAttribute.Accessor` **必须是 public 字段**——具名特性实参只能写
public 可写字段，写成 internal 会报 CS0617「不是有效的命名特性参数」，
而那句话完全指不到访问级别这一层（已钉成断言，MUT-S 验证）。

### 偏差 22：回放期间没禁游戏自己的 `UpdateFreeroam`

Spectre 的 `Patch_UpdateFreeroam`（`Patch_Misc.cs:100-115`）我们完全没有，
而状态轨又在按录制数据写同一批字段——两边同帧各写一次。

**自由漫游段结束那一帧，游戏会一口气做完**（`scrController.cs:2817-2873`）：
淡出整段砖块、给行星挂 `DOMove`、锁输入、设无敌帧、移动相机，
然后重写行星状态：

```
:2861  playerOne.lastHit = …            ← 状态轨也要写
:2867  SetTargetExitAngle(num4)         ← 状态轨也要写
:2868  SetSnappedLastAngle(snapped)    ← 状态轨也要写
:2871  TweenCosmeticAngle(…)
:2872  gameworld = true                 ← 下面这条更麻烦
:2875  curFreeRoamSection++
```

**`gameworld` 全仓库只有三处赋值**（`scnGame` / `scnEditor` 进关时，加这里），
但读点遍布判定条、失败条、暂停菜单、社交按钮、相机跟随——
一次误翻就把一大片本该关闭的表现层全打开。这就是此前
"渲染里混进了编辑器状态"那类现象的机制来源。

而且 `curFreeRoamSection++` 是**第二个真相源**：区段号应由状态轨按录制数据
推进（`Pump` 里 `int section = controller.curFreeRoamSection` 对位）。

**修法**：`HitStateReplayer` 加 `[GamePatch(typeof(scrController),
"UpdateFreeroam", PatchKind.Prefix, "playback")]`，门控条件照抄 Spectre
——下一条命中的区段号与当前一致就禁。**探针失败时放行而不是禁**：
读不到区段号就把整个自由漫游段掐掉，砖块会永久停在起点。

---

## 六、退出关卡时的拆除链（偏差 23）

### 偏差 23：`PlaybackEngine.Stop()` 只有界面按钮一个调用点

`Stop()` 全仓三个调用点：界面上的"停止"按钮、`Pump()` 的异常兜底、
离线渲染收尾。也就是说**只要玩家不点那个按钮，回放就不会停**——
哪怕他已经重开、换关、退回主菜单了。

而 `LevelWatcher.FinishCurrentLevel()` 只管录制与渲染
（`KeyRecorder.End` + `RenderService.OnLevelEnd`），
它连 `PlaybackEngine.Stop()` 都不调。这不是漏写，是两边从来没交接过：
LevelWatcher 的判据是**场景名变了**（5 帧迟滞），
而 `scnEditor.SwitchToEditMode` **根本不换场景**，任何看场景名的判据
都发现不了那次离开。

Spectre 的 `Patch_Lifecycle.cs:16-42` 挂了四条，我们一条都没有。

**残留的具体后果**（`Stop()` 里的三件事一件都没跑）：

| 没跑的那件 | 残留 |
|---|---|
| `KeyboardSimulation.ReleaseAll()` | 回放中途按下的键一直卡着，玩家的下一个物理按键只会被当成一次松开，之后判定全乱 |
| `HitStateReplayer.Unload()` | 上一局的数据留着，跟着新关卡的 `UpdateFreeroam` 继续按上一局角度推进行星 |
| `RestoreGlobalState()` | `noFail` 仍是 `true`、`GCS.hitMarginLimit` 仍是录制时那档，**之后正常游玩也跟着生效** |

### 四条挂点，逐个对着真程序集核过

选哪四条不是照抄 Spectre，是把真程序集里**所有**通往
「离开游玩状态」的公共入口数了一遍：

| 挂点 | 为什么是它 | 覆盖掉什么 |
|---|---|---|
| `scrUIController.WipeToBlack` | UI 级擦黑 | PauseMenu 换关（`PauseMenu.cs:1434/:1463` **绕过 Restart**）、关卡选择/ TARO 界面回退 |
| `scrController.Restart` | 重开关卡 | `scrController.cs:2626` |
| `scrController.QuitToMainMenu` | 退回主菜单 | `scrController.cs:2447` |
| `scnEditor.SwitchToEditMode` | **不换场景**，只切游戏状态 | 任何"看场景名"的判据都看不见的那次离开 |

`scrLoader.LoadSceneWithTransition`（`scrLoader.cs:107`）是场景切换的唯一实现，
它的调用点只有 6 处，全都从上面四个方法里出去
（`scrController.cs:2300/2447/2626`、`scnEditor.cs:1605`、
`scnCLS.cs:2216`、`scnCalibration.cs:527`）。

**为什么全是 Prefix**：`LoadSceneWithTransition` 起一个 0.3 秒的 DOTween 擦黑，
擦完才 `SceneManager.LoadScene`。Postfix 时旧场景的 `scrController` 还活着、
按住的键还按着——拆得太晚。

**为什么不设 Feature 名**，以及它踩到的那个坑：

`"playback"` 读的是 `PlaybackEngine.IsPlaying`，**没载入回放时就是 false**，
而 `PatchLoader.Refresh()` 在门控为假时 `continue`，**连 `TryApply` 都不进、
`Failures` 里也不会有它**。于是"载入过回放 → 退出去"能拆，"没载入过 → 退出去"
拆不了，而日志里只有一行：

```
补丁 N/N-4 已挂载（playback 组只在载入回放时挂上，计数偏少是正常的）
```

那句话本来是为 playback 组写的，**反而替这个缺陷打了掩护**。

顺带查出 `FeatureGate.IsEnabled` 压根没有空串分支：
"不写 Feature"被当成"功能名是空串"去查表，查不到 → false。
所以 `PatchLoader.Refresh()` 里"不设门控的补丁"这个类别**从来就没存在过**。
已修：空 Feature 现在明确定义为「不设门控 = 恒挂载」，
而**未声明的非空名字仍然判关闭**（否则把所有开关都废掉也能全绿）。

### 断言（`LifecycleCheck`，八条）

上一轮的经验是**检查了一个名字在，没检查它还起没起作用**。
这里最容易踩且最难查的一种形状是"补丁压根没挂载"——不报失败、计数正常。
所以除了"四条挂点在不在"，还钉了三件别的事：

- ⑥ **真的去反射游戏程序集**，核对四个目标方法存在且**形参个数**对。
  源码级断言只能证明"我们写了四条挂点"，证明不了"这四条挂得上"；
  游戏改名时只会多一行「（目标不存在）」，拆除链就静静地少了一条。
- ③ 反面判据：把 `IsEnabled` 整个改成 `return true` 也必须变红。
- ④ 方法体必须**紧跟签名**，且**只能调 `Teardown()` 一个调用**。
  第一版少了前半句，"方法体被掏空"这种变异会一路找到**下一个**挂点的
  方法体、读到人家的 `Teardown()`，闸门全绿（真·踩到了）。

### 变异（MUT-W / X / Y / Z / AA / AB / AC / AD / AE）

九条全部被检出，各自由它设计时针对的那条断言抓到。两条值得记：

- **MUT-W 第一版是错的变异**：我把 `WipeToBlackPrefix` 的方法体掏空，
  结果判据一路找到下一个方法体照样读到 `Teardown()`，闸门全绿。
  改成"整条链退回不存在"——那才是这一版的真·旧样子。
  教训：**变异形状必须对准判据**，否则"被检出"也是空的。
- **MUT-AB 第一版也踩了同一个坑**：替换整个方法体（连 `{` `}` 一起）时，
  判据 ④ 的"方法体在签名后面"会先一步变红——那是另一条断言的功劳。
  改成只替换 `Teardown()` 那一行，才真的验到它想验的"必须共用一个入口"。

### 偏差 24：回放期激光/尖刺会直接把行星打死 —— 死亡在 `noFail` 保护之外

死法有两条路。**普通失败**（激光之外的一切：`scrPlanet.MarkFail`、
失败判定、飞行动作掉出砖块）都走 `scrPlayer.Die(overload, multipress,
failMessage)`，**第 4 形参 `hitbox` 是 false** —— 真源码
`scrPlayer.cs:325-371` 把整段 `noFail` 保护包在 `if (!hitbox)` 里，
所以偏差 19 加的 `ClampGlobalState()`（`controller.noFail = true`）
**真的兜住了它们**。

**但激光/尖刺/装饰物走的是另一条**：`LaserSpawner.cs:154`、
`Mawaru.cs:818`、`PuzzleRoom.cs:81`、`DivineIntervention.cs:1874`、
`scrPlanet.cs:1249`、`scrDecoration.cs:453` 全部调
`DieByHitbox()` / `Die(…, hitbox: true)`。这一支**直接跳到
`scrPlayer.cs:381 planetarySystem.Die(...)` + `alive = false` +
`OnPlayerDied` + `MarkDeath`**，`noFail` 一眼都看不到。

后果不是"回放多输了一次"，而是**成片断在一砖**：
`song.Stop()` 之后帧栅格还在按墙钟推进（`LiveSession` 不管游戏死没死），
录出来的是一段静止画面，`Player.log` 里一句异常都没有。

修法在 `PlaybackClamps.cs:88-143`：一个 `scrPlayer.Die` 的 **Prefix**，
条件只有 `PlaybackEngine.IsPlaying || !hitbox`（返回 true 放行），
命中时打一行 `Debug.LogWarning("[SpectreReplay] 回放期挡住一次 hitbox 死亡…")`
并 `return false`。

**三件事必须一起钉住**（`BarrierCheck` 六条断言）：

1. **它挂在 `scrPlayer.Die` 上，真程序集里第 4 形参真名叫 `hitbox`**
   且类型 `bool`（`overload:Boolean, multipress:Boolean, failMessage:String,
   hitbox:Boolean`）。前缀是按**形参名**绑定的，名字写错**永远挂不上**，
   而日志里只有「（目标不存在）」一行。
2. **只拦 `hitbox == true`**，普通失败必须放行——否则把偏差 19 刚加的
   `noFail` 保护整个废掉了。
3. **不许顺手扩到 `scrPlanet.Die` / `scrSpike.Die`**：那些是行星自己的
   死亡动画路径，与行星死亡判定无关，拦下来会变成"打不死"。

**顺带查清一件事**：`scrPlayer.cs:363-368` 那个 `noFail` 抢救分支在
`return` 之前**仍然**做 `controller.noFailInfiniteMargin = true; Hit(null);
controller.noFailInfiniteMargin = false;`。我们的屏障在 `hitbox == true`
时就返回了，**够不着这条路径**，所以这条被记下来的 noFail 事件不会被
屏障吃掉——这点写在屏障的注释里，否则后来人会以为屏障把它一起拦了。

**一句话记这个偏差的教训**：偏差 19 加 `noFail = true` 时，判据只看了
"普通失败都被兜住"，**没问 `noFail` 在真源码里到底管到哪**。
`if (!hitbox)` 那个括号就在那儿，差一行没读。

---

## 七、累计的变异验证

`tools\mutate.ps1`（把修复退回旧样子、跑离屏套件、判红或报"这条断言
是假的"、**自动还原**）。累计二十三条变异（其中一条反向对照），逐条
验证结果如下：

| 变异 | 内容 | 被哪条断言抓到 |
|---|---|---|
| MUT-O | 删掉 Set 侧（只留 Get） | ② 写端没被接管 |
| MUT-P | 钉扎值照抄 Spectre 乘 pitch | ④ 钉扎值乘了 pitch |
| MUT-Q | freeroam 门控恒放行 | ⑧ 门控条件不是"对位就禁" |
| MUT-R | 结束判据改回读 `songposition_minusi` | ⑤ 结束判据读的是被钉住的读端 |
| MUT-S | `Accessor` 改回 internal | ① 不是 public 字段 |
| MUT-T | 先替换 value 之后才抄真值 | ② 没有在替换前抄 |
| MUT-U | 门控改成 `区段+1 == 当前` | ⑧ 同 MUT-Q |
| MUT-V | `GetGetMethod(true)` → `(false)` | ① ResolveTarget 没翻译属性名 |
| MUT-W | 整条拆除链退回"不存在"（真·旧样子） | ①/②/⑤ 四条挂点都没有 |
| MUT-X | 退出挂点改 Postfix | ① 挂成了 Postfix |
| MUT-Y | 给退出挂点设 `"playback"` 门控 | ② 退出挂点设了功能门控 |
| MUT-Z | 删掉空 Feature 的早退分支 | ③ IsEnabled 没处理空 Feature |
| MUT-AA | `Teardown()` 不调 `Stop()` | ⑤ try 里没有 `PlaybackEngine.Stop()` |
| MUT-AB | 一条挂点自己内联 `Stop()` | ④ 方法体不是只调 `Teardown()` |
| MUT-AC | `RestoreGlobalState()` 挪到早退之后 | ⑦ 写在早退之后 |
| MUT-AD | `HitLocked` 互锁改 `return true` | ⑧ 早退返回的是 true |
| MUT-AE | 目标方法名改 `WipeToBlackXXX` | ①/②/④ 挂点找不到 |
| MUT-AF | 整条死亡屏障退回"不存在" | ①② |
| MUT-AG | 屏障门控改成 `if (!hitbox)` | ②③ |
| MUT-AH | 屏障门控改成 `if (!PlaybackEngine.IsPlaying)` | ② |
| MUT-AI | 屏障的 `return false;` 改成 `return true;` | ② |
| MUT-AJ | 屏障目标改 `typeof(scrPlanet), "Die"` | ①②⑤ |
| MUT-AK | 注释掉 `MarkFailScope` 的 `finally` | LifecycleCheck ④ + BarrierCheck ⑥ |
| MUT-AL | 屏障条件里加 `scrMisc.GetHitMarginInSec(...) > 0` | ①（另被 ②③，共 5 条） |
| MUT-AM | 同一处改成 `FeatureGate.IsEnabled("ScrMisc")` | ①（第一轮 ① 漏了，见七之二） |
| MUT-AN | `Settings.cs` 加 `BGAMode` 开关 | ③ |
| MUT-AO | `HitPostfix` 调 `__instance.AddHit(hit);` | ④ |
| MUT-AP | `LiveSession` 时钟改回 `Time.realtimeSinceStartup` | ⑥ |
| MUT-AQ | `PatchLoader` 插反射加载自身程序集的 `CountSelfPatches()` | ⑤ |
| MUT-AR | 把封禁词写进**注释**，要求**保持全绿** | 无（反向对照成立） |

累计**二十三条**，其中二十二条必须变红、MUT-AR 必须**保持全绿**。

**顺带修掉闸门自身的一个毛病**：游戏在跑时 `deploy.ps1` 抛"dll 被锁"，
原先那个 throw 会把整个 `verify.ps1` 掀掉——后面「Spectre 未被污染」
整段不跑、末尾也没有汇总，看上去像「闸门崩了」而不是「部署这一项没过」。
游戏开着是常态不是异常，已包成 try/catch 记成一项失败。

**又一条自己的错断言**：⑧ 最初只写了"不是对位就禁"的单一后果，
而 MUT-Q 是恒放行（从不禁），报出来的却是"会在无关的区段掐掉自由漫游"
——**报错方向与实际相反，照着去查会查半天查不出问题**。
已改成把两种坏法都写出来，并提示"先看它到底返回了什么"。

### 变异脚本自己也坏过两次

这两条都是**工具层的坑**，而且都会伪装成"断言是假的"——
即变异根本没施加、闸门如实全绿。

**① `ReplaceExact` 多出的实参被静默吃掉。**
MUT-AN / AO / AQ 三条**同时**失效：闸门 116 条失败 0，
而脚本报 exit 1「这条断言是假的」。根因实测确认：PowerShell 的
**命令调用模式**里 `+` 是**普通实参**、不是运算符——
`F 'a' 'b' + "`n" + 'c'` 会把 `$b` 定成 `'b'`，把
`+`、换行、`+`、`c` 当成额外实参**丢掉**（实测 `extra=4: +|+|third`）。
`ReplaceExact` 只声明两个形参，多余的被静默忽略 ⇒ **变异根本没生效**。
修法两条：给 `ReplaceExact` 加 `if ($args.Count -ne 0) { throw ... }`
（把这个形状永久堵死），且所有多半行的替换值写进括号
`('a' + "`n" + 'b')`。

**② 给 `mutate.ps1` 加了 `[switch]$ExpectGreen`。**
反向对照（把封禁词写进注释、要求**保持全绿**）撞上了脚本原本的语义
——"红了才算通过"，反向对照天然会 exit 1。要么被误当成坏变异而改掉，
要么被人手工豁免，那等于没有这个对照。现在 `-ExpectGreen` 时
0 失败 → `✓ <名> 反向对照成立：闸门保持全绿` exit 0；
有失败 → exit 1 并提示"**判据过严**（忘了剥注释、或没忽略大小写）"。

**③ BOM 事故的第二种形态：把文件改成两个 BOM。**
为修①我写的"补 BOM"脚本本身判断失误，在**已有** BOM 的文件上又加了一个，
文件头变成 `239,187,191,239,187,191`；PS 5.1 把多余的 U+FEFF 当命令 token，
报 `Cleanup : The term 'Cleanup' is not recognized`——**连早已存在的
MUT-AK 也一起挂掉**。定位靠 `git diff` 里 `-﻿<#` / `+﻿﻿<#` 那一行。
可靠写法：先 `ReadAllBytes` → 首 3 字节不是 `239,187,191` 才动手；
动手前先判断文本首字符是否已是 `[char]0xFEFF`，是就先 `Substring(1)`。

---

## 七之二、其余六个特性目录：为什么判定为「不补」

偏差 21–24 那四轮是"核着核着挖出真缺口"。剩下这六个目录反过来——
**核完了，一条缺口都没有**。那一轮的结论是"绝大多数判为产品形态差异，
不补"，但它**停在了印象上**：没有说清每一个目录里到底有什么、
凭哪条依据不补。所以本轮把六条结论钉成断言。

**为什么"不补"本身需要断言**（理由写在 `FeatureParityCheck.cs:1-35`）：
半年后有人看到 Spectre 里有一个 `scrMisc.GetHitMarginInSec` 的补丁，
就会想"这个我们好像没有"，于是补一段进去。补进去的后果不是少一个功能：

- **改判定的补丁一旦接上，录下来的回放在别的机器上就飘**，
  因为判定窗口不再是游戏自己那套；
- **屏蔽 `FailAction` 的补丁会让"玩家按错了键"不再失败**，
  录出来的按键全是命中的，回放看起来"很完美"但与真人对不上，
  而日志里一句异常都没有。

**断言方向是反的**：不是"必须有 X"，而是"**不许出现** X"。这样有人
想补这些东西时，闸门立刻红，并告诉他为什么。

### 六个目录逐个核完的结论

| 目录 | 文件数 | 内容与不补的依据 |
|---|---|---|
| **Other** | 8 | turnaround/暂停时长计算、decode-pause 转译器、异步输入缓存性能、pro events、内存优化器、特殊按键、max-used-keys 统计、judge 文字显示。**没有一个碰录制·回放·渲染的正确性** |
| **Editor** | 11 | `SpeedMath.cs:105-146` 只读；`Patch_SpeedPauseConverter.cs` 改 `editor.events` 后调游戏自己的 `ApplyEventsToFloors()`；`Patch_BpmBeatCalculator.cs` 注册 `"encode", false`（**从不序列化**，`:348-353` 除零时故意什么都不写）；`Patch_EditorBugfixes.cs:65` 只在拖编辑器时间轴时才够得着。**唯一真影响是 `Patch_BGAMode.cs`**（8 处补丁，默认关 `FeatureKeys.BGAMode`）——见下 |
| **CustomJudge** | 4 | 只有 `Patch_RealJudge.cs` 改判定，受 `CustomRealJudgeOn`/`CustomOnlyOn` 约束（**默认关**）⇒ **使用约束**，不是缺口。`Patch_TierJudge.cs` 只读 `ref HitMargin`；`TierJudgeState` 已由 `Start_Rewind` Postfix 自动重置（与 `PlaybackEngine.Begin()` 同一个挂点）。**2026-10-17 用户裁决：整套移除**，见下节 |

#### CustomJudge 移除（2026-10-17）

用户原话：「**CustomJudge 并不属于replay和视频渲染的部分啊**」。

复核后确认这个判断成立：

- 上游那两份**真正改游戏判定**的补丁（`Patch_RealJudge.cs` /
  `Patch_TierJudge.cs`）**我们根本没移植**——搬进来的所谓「自定义判定」
  是一个**没有判定改写能力的判定系统**，只剩档位 UI、档案读写与状态账本。
- 真正被 replay 用到的只有**一处**：判定窗口**数学**
  `BoundaryRaw`（散点图按原版 Perfect/Pure 窗口归一化着色；
  `GameCompat.Boundary` 找不到游戏函数时兜底）。
- **没有任何第三方 mod 依赖 `SpectreAPI`**：扫 `Mods\` 下全部 dll，只有
  `Spectre.dll` 与 `SpectreReplay.dll` 自己含 `SpectreAPI/GetTierDefinitions/
  GetLastHitTier` 字符串 ⇒ 删对外接口是安全的。

处置：窗口数学摘为 `Features/Compat/MarginBoundary.Raw`（只留数学，
不留任何判定改写能力），其余**整体删除**——`Features/CustomJudge/`
目录、`Settings` 的 CustomJudge/CustomTier* 字段、`ConfigManager` 的
`CustomJudgeData` 与读写分支、`SpectreAPI` 的档位 API 面、
`SpectreState` 的自定义 xacc 分支、`FeatureKeys` 三个常量、
14 语言 ×2 条 `customjudge.*` 文案。旧配置里的 `CustomJudge` 节点
**被忽略而非报错**（`JObject` 取不到就是 null）。
| **JudgeBar** | 10 | `Patch_JudgeBar.cs:13` 解锁 `DifficultyUIMode.ShowAll`、`:27 UpdateLayout`、`:50 ADOFAI.ErrorMeterTick.Show`；`Patch_FailureJudgement.cs` 14 处挂点（含 `scrPlayer.Die` 的 Prefix/Postfix，`:72 if (multipress \| hitbox) return;`）。默认关（`Spectre\Main.cs:90` + `Settings.cs:199 JudgeBarOn = false`）。`FailureJudgement.cs:75-76/:99` 只读；`JudgeBarMeter.cs:197-198` 改的 `meter.sensitivity`/`tickLife` 是**自身组件**（`:333-334` 还原）；`OverloadBarUI.cs` 是 Overlay 画布，纯外观 |
| **ResultsPlus** | 3 | `Patch_HitError.cs:9/:15` 只收集命中误差（**我们用 `HitTimingAnalyzer` 覆盖了同一份数据**）；`Patch_ResultsText.cs:22/:32/:55/:87` 是暂停弹窗/状态变更/keyFrequency 去重/结算文字 |
| **Perf** | 1 | `FeaturePatchCost.cs`（82 行）反射 **Spectre 自己的程序集**数 `[SpectrePatch]` 声明数——**它不挂任何补丁**，只是个自测面板 |

**一条值得单独记的**：`Patch_BGAMode.cs:122` 是 `scrController.FailAction`
的 Prefix `return false`，`:107` 是 `ShowHitText` 的 Prefix `return false`。
这正是上面那条"屏蔽失败"的典型。它默认关，`Patch_BGAMode.cs:114 MarkFail`
与我们 `PlaybackClamps.cs:37-46` 的 `if (!IgnoreMarkFail) return true` 优雅相让，
`scnEditor.SwitchToEditMode` Postfix 与我们的 `Patch_Lifecycle.cs:109-113` Prefix
不交叉——所以**结论是使用约束：不要在录制期间开 BGA 模式**，不是"要补"。

### 「不补」里唯一一条需要论证的：没有 `TogglePauseGame` 挂点

Spectre 在 `scrController.TogglePauseGame` 上挂了前缀，在
"编辑器 playMode + auto + 回放中"时把暂停按掉
（`Spectre\Features\Replay\Patch_Misc.cs:11-31`）。**我们没有这个挂点**，
所以要确认"没有它"确实安全。两个时钟分道扬镳：

- **帧栅格是墙钟**：`LiveSession.cs:374` `_startWall + _frameIndex / (double)Settings.Fps`，
  时钟是 `Stopwatch`（`:36`），**暂停期间照样推进**——这是**故意的**
  （`:26-29` 说明为什么不用 `Time.realtimeSinceStartup`：那个跟随
  timeScale，一次慢动作就够音画错位）。
- **回放游标读 `conductor.songposition_minusi`**：暂停时游戏停了
  （`scrConductor.cs:776 bool pause = AudioListener.pause;`，`:797-800`
  `if (!paused) UpdateInput()`——暂停时 `UpdateInput` 不跑），
  游标不动 ⇒ 按键不注入。

于是暂停期间"墙钟走、游标停"：

- 对**离线渲染**无害：`OfflineSession.cs:225` 把歌曲位置钉在
  `_frameIndex / (double)_fps`，暂停不改这个值，渲染照样逐帧推进
  ——这正是"离线"该有的行为。
- 对**实时录制**只是多录一段静止画面。编辑器的 `pausedInPlayMode`
  还会顺带关掉相机自定义帧率（`scnEditor.cs:1052-1056`
  `SetCustomFrameRate(enable: false)`），那只影响游戏自己往四边形上贴的
  纹理，**不影响我们 `cam.Render()` 到 RenderTexture 的逐帧捕获**
  （`FrameSource.cs:131-153` 走的是 `cam.targetTexture` + `cam.Render()`）。

所以"不补 `TogglePauseGame`"是有依据的，不是漏抄。**断言 ⑥ 钉的就是
这个依据**：帧栅格必须用墙钟，不能改成 `Time.realtimeSinceStartup`。

### 断言（`FeatureParityCheck`，六条，12 个计数）

| 断言 | 内容 | 对应的"不补"依据 |
|---|---|---|
| ① `NoBannedApiInOurCode` | 剥注释后扫全部 `.cs`（跳过 `\obj\`/`\bin\`/`tools`），20 个封禁词出现即红 | 六个目录里所有碰游戏侧 API 的入口。**刻意不含 `SwitchChosen`**——`GameCompat.cs:140` 有该方法但全仓零调用点，封了会逼出无谓删改 |
| ② `JudgeRewriteTargetsAreShapedAsSpectreAssumed` | **真反射真程序集**：`scrMisc.GetHitMarginInDeg/7`、`GetHitMarginInSec/5`、`HitMarginHelper.IsCounted/2`、`scrPlayer.Hit/2` 必须**在**；`scrMisc.GetHitMargin/6`、`scrMisc.IsValidHit/1` 必须**不在** | r150 已删 `IsValidHit`，Spectre 那些 `MaxVersion = 149` 的分支在本版本**挂不上**⇒"不补"零成本 |
| ③ `NoParitySwitches` | 只扫 `Config\Settings.cs`，九个开关名出现即红 | 判据是"**声明了**"而非"默认关"——这三组补丁一旦打开就改判死与判定，后置默认值兜不住 |
| ④ `OurHitAccountingIsUnpolluted` | 我们对 `scrMarginTracker.AddHit` 只有一个**只读 Postfix**（`Patch_Recording.cs:252`），且全仓**无任何 `.AddHit(` 调用** | `AddHit` 是判定记账入口。一旦我们也调它（哪怕"顺手修一下"），录下来的 `hitMargins` 与真实判定对不上，回放飘字全错而无异常 |
| ⑤ `NoPerfSelfScan` | 全仓不得出现 `Assembly.GetTypes` / `Assembly.Load(From)?(` | 抄了 Perf 目录那个自我统计，或在运行时动态取补丁目标——两者都该走源码级判据 |
| ⑥ `EditorPauseDoesNotDesync` | `LiveSession.cs` 必须 `new Stopwatch(` 且不得出现 `Time.realtimeSinceStartup\|unscaledTime\|deltaTime\|time`；`OfflineSession.cs` 必须 `PinnedSongPosition = _frameIndex / (double)_fps` | 上节那条论证 |

**① 的一个设计选择值得记**：判据是"**去注释后**的代码里出现即红"，
不是"出现即可疑"。注释里提某个 API 是必要的（要说明为什么不补），
代码里出现就是真的要动那条链路。封禁词表带目录与理由，
报错形如 `✗ ① Settings 里出现了 scrMisc ← CustomJudge：改判定会让回放飘`。

**② 是这组断言里唯一一条会真去反射游戏程序集的**，而且它有
"必须不在"的**反向子判据**（`scrMisc.IsValidHit` 已删）。
只查"在"的话，游戏哪天把它加回来 we'd 毫无反应；只查"不在"的话，
Spectre 改用新 API 名 we'd 毫无反应。**两个方向都要钉。**

### 变异（MUT-AL … MUT-AR，七条）

七条全部被检出（`MUT-AR` 是反向对照，要求**保持全绿**）：

| 变异 | 内容 | 被哪条断言抓到 |
|---|---|---|
| MUT-AL | 在死亡屏障的条件里加 `scrMisc.GetHitMarginInSec(...) > 0` | ①（另被 ②③ 抓到，共 5 条） |
| MUT-AM | 同一处改成 `FeatureGate.IsEnabled("ScrMisc")`（字符串 + 大小写混写的绕法） | ① + ②③（共 4 条） |
| MUT-AN | `Settings.cs` 里加 `internal static bool BGAMode = false;` | ③ |
| MUT-AO | `HitPostfix` 收下 `__instance` 并调 `__instance.AddHit(hit);` | ④ |
| MUT-AP | `LiveSession` 的 `Stopwatch` 改名、时钟改回 `Time.realtimeSinceStartup` | ⑥ |
| MUT-AQ | `PatchLoader.cs` 里插一个反射加载自身程序集的 `CountSelfPatches()` | ⑤ |
| MUT-AR | 把封禁词写进**注释**，要求闸门**保持全绿** | 无（反向对照成立） |

**MUT-AM 揪出了 ① 的一个真漏洞**：第一轮它只被 `BarrierCheck` ②③
顺手抓到，**① 根本没抓到**——因为 ① 的正则**区分大小写**，
`FeatureGate.IsEnabled("ScrMisc")` 里的 `ScrMisc` 躲过去了。
已把 ① 改成 `\b token \b` + `RegexOptions.IgnoreCase`。
实测全仓仅 3 处忽略大小写后命中（`GetHitMarginInDeg` 在
`HitStateReplayer.cs:274` 注释、`FailAction` 在 `PlaybackClamps.cs:98` 注释、
`SpectrePatch` 5 处全在注释），而注释已被剥掉 ⇒ **更严的判据零误伤**。
教训与 MUT-AB 那次一样：**变异形状必须对准判据**——
只有当变异恰好躲开某条断言时，才算真的验到了那条断言。

**为 MUT-AR 给 `mutate.ps1` 加了 `[switch]$ExpectGreen`**（见下节）。

---

## 七之三、UI 逻辑：116 条全绿而 11 个缺陷一条没抓到（偏差 25）

用户报的是**「你这个 UI 逻辑不合理」**。这一节记的不是"改了什么"，
而是**为什么闸门全绿却漏掉了整整一轮界面问题**——那才是值得写下来的部分。

### 缺陷清单（11 条，按严重度）

| # | 缺陷 | 玩家的现象 | 修法 |
|---|---|---|---|
| 1 | `Settings.AsyncInput` 死开关（只存不读） | 勾了「使用高精度输入（SkyHook）」，什么都不会变 | 删字段 + 删配置读写 + 删界面行 + 删文案 |
| 2 | `DrawStatus` 只画渲染进度 | **开着录制打一整局，设置页顶部一片空白** | 改成 if/else 链：渲染中/录制中/回放中/待播/空闲/全关 |
| 3 | 渲染页 Live 分支的「开始渲染」是摆设 | 正常流程下点它永远什么都不发生 | Live 支不给按钮，只留一句说明；离线支才给 |
| 4 | `RenderService.Busy` / `MarkBusy` 零调用点 | `StartManual` 开头的 `if (Busy) return` 恒不成立 | 删死代码（不是补实现） |
| 5 | `RenderStatus.BarFill` 零调用点 | 进度条永远满格（Live 的 Progress 恒为 1） | 删；进度条改用 `FramesTotal > 0` 判空，实时模式不画 |
| 6 | `ReplayDir` 的 `OutputDir` 分支两支相同 | 看着像"输出目录也能改回放位置"，改了没反应 | 删伪分支，理由写进注释 |
| 7 | `DrawPlaybackState` 开头 `if (!IsPlaying) return;` | 没载入回放时整块消失，分不清是没载入还是载入失败 | 分组无条件绘制；空态说"没载入"，失败态摆 `LastError` |
| 8 | `RowInput` 每敲一键提交一次 | 删路径时中间态被逐个存盘并立刻用掉 | 草稿 + 提交（回车 / 点别处；**控件自绘**，焦点自己管——见下「黑框」一节） |
| 9 | `OnGui.Refresh()` / `OnGui.Describe()` 两个空桩 | 无（零调用点） | 删 |
| 10 | `RenderStatus.EtaSeconds` 全仓无人写 | 离线渲染明知还要等几分钟，看不到还剩多久 | `OfflineSession` 加 `_startWall` 并写一次带条件的真赋值 |
| 11 | `FramesTotal` 只在离线写，实时不清零 | 上次离线留下的总帧数让实时进度条瞬间满格 | 实时开录与收尾各清零一次 |

**"代码在、但没接到界面上"是这一轮的共同形态**：缺陷 1/4/5/6/9/10
全仓 grep 就能看出是死代码；2/3/7/8 是逻辑本身自洽、只是**说谎**——
顶栏明明有录制却什么都不画，播放按钮明明在却永远是空操作。
**这一类对任何"检查代码存在/不存在什么"的闸门都是隐形的。**

### 闸门为什么一条都没抓到

三条原因，每条都能独立解释这一轮：

1. **没有界面层的断言。** 原有 116 条覆盖的是：数据往返、ffmpeg 参数、
   判定复现、补丁挂载顺序、版本兼容、六目录封禁。**界面那层只有
   `LocCheck`（文案齐不齐）和 `HardTextCheck`（有没有写死中文）**，
   两者都只管**字符串在不在**，管不了**控制流的形状**。
2. **`LocCheck` 反而把新文案也变成了盲区。** `status.idle` /
   `status.recording` / `status.playing` / `status.pending_replay`
   四条文案从建库起就在 `Loc.cs` 里，两种语言都齐全，闸门每次都绿——
   而它们**一次也没被引用过**。文案齐 ≠ 界面说得上话。
3. **`OnGui` 与 `Pages` 之间没有任何契约。** 两边各自编译通过就万事大吉，
   而"顶栏该不该画、按钮该不该给"恰恰是两个文件之间的约定。

### 断言（`UiLogicCheck`，六组 20 条）

判据一律落在**控制流形状**上，不落"某字符串出现过"——后者对假开关
与摆设按钮永远绿。具体：

| 组 | 断言 | 抓什么 |
|---|---|---|
| ① `NoDeadSwitches` | 5 条 | `AsyncInput` 的字段、配置读、配置写、界面行、文案键**五处都得没有** |
| ② `StatusLineCoversEveryCase` | 3 条 | 不许再有 `if (!rendering) return;`；四个来源逐个点名；四条文案都**被引用** |
| ③ `RenderPageButtonsAreNotDecorative` | 5 条 | live 支**不许出现** `render.start`；离线支必须先写目标再给按钮；必须有 `offline_noreplay`；`if (running)` 里同时出现 `OfflineSession.IsRunning` 与 `LiveSession.IsRunning`；`StartManual` 必须返回 `bool` 且 live 支 `return false` |
| ④ `PlaybackPageNeverGoesBlank` | 2 条 | `playback.current` 分组必须早于第一个 `return;`；空态与失败态**各自取用** `idle` 与 `idle_err`（不接受别的键） |
| ⑤ `TextInputDoesNotCommitEveryKeystroke` | 3 条 | 不许有 `Action<string>` 回调；提交必须由**自绘控件**的返回值驱动（`_outDir.Draw(...)` 拿到 `commit` 才 `CommitText()`）；宽度必须走 `Gui.CtrlW` |
| ⑥ `RenderProgressHasARealTotal` | 4 条 | 离线写 `FramesTotal`；实时**两处**清零；界面必须 `int total = …FramesTotal; if (total <= 0) return;`；离线必须写**带条件的** `EtaSeconds` |

**⑥c 是这 20 条里唯一一条抓到真 bug 的**：第一版判据写"用
`FramesTotal > 0` 判空"，闸门报红，回去读代码发现真代码有 bug——
`fill = Clamp01((float)FramesDone / FramesTotal)` 里 `0/0` 得 **NaN**，
`Mathf.Clamp01` 的 `<` / `>` 对 NaN 全为假所以 NaN 原样返回，
`fill <= 0f` 也为假，于是 NaN 一路进 `GUI.DrawTexture(r.width * NaN)`
抛 `ArgumentException` 打断整个 OnGUI。**症状是点开渲染页窗口直接没了，
且恰好发生在"还没开始渲染就翻到这一页"**——最容易撞上的时候。
判据与代码都是这轮改的。

### 变异（MUT-AS … MUT-BZ，17 条）

17 条全部按预期走完（`MUT-BZ` 是反向对照，要求**保持全绿**）：

| 变异 | 内容 | 被哪条抓到 |
|---|---|---|
| MUT-AS | 加回 `AsyncInput` 字段 | ①a |
| MUT-AT | 加回 `GetBool(d, "AsyncInput", …)` | ①b + ①c |
| MUT-AU | 加回 `{ "AsyncInput", … }` 写盘 | ①c |
| MUT-AV | 顶栏退回 `if (!rendering) { return; }` | ②a |
| MUT-AW | 漏掉「录制中」那一支 | ②b |
| MUT-AX | 把 `render.start` 复制进 live 支 | ③a |
| MUT-AY | 离线支的目标文案换成 `Loc.T("render.start")` | ③b |
| MUT-AZ | `if (running)` → `if (true)` | ③d |
| MUT-BA | `StartManual` 退回 `void` | ③e |
| MUT-BB | 把 `playback.current` 分组挪回早退**之后** | ④a |
| MUT-BC | 空状态文案换成一个拼错的键 | ④b（另被 LocCheck 抓到 3 条） |
| MUT-BD | 文本框退回每键提交 | ⑤a |
| MUT-BE | 控件名改回含点号 | ⑤c |
| MUT-BF | 进度条 `int total = 1;`（拿掉判空） | ⑥c |
| MUT-BG | 实时只留**开录那一处**清零 | ⑥b |
| MUT-BH | `EtaSeconds = -1;` 钉死 | ⑥d |
| MUT-BZ | 把三个封禁词写进**注释**，要求保持全绿 | 无（反向对照成立） |

### 这一轮自己踩的四个坑（都属于"假通过"）

**① 一次 exit 0 的假证明。** 修 `mutate.ps1` 时我用
`[IO.File]::ReadAllLines` + `$keep=$l[0..481]` 截断重写，**把脚本尾部的
执行器与 exit 逻辑一起截掉了**，剩下 482 行只有变异定义没有 runner。
于是接着跑 MUT-AS…MUT-AW 时 `Backup` 改了源文件、后面没有 `Cleanup`，
**5 个源文件被留在变异态**，`$env:TEMP` 下留了 4 个备份。
更严重的是它**伪装成通过**：`MUT-AS` 报 `exit=0 … 失败 4`，看着像
"变异被检出"，其实那 4 条红是**先前遗留的变异态**造成的。
差点把一条假证明写进文档。⇒ **`exit=0` 必须配"失败条名确实是本次变异
的目标"才算数**，条名对不上就是假通过。

**② 一次变异把源码写坏，而闸门没反应。** `ReplaceExact` 是逐行
`Contains`/`Replace` 后**原样写回单行**——我给替换值拼了多行 + `` `n ``，
于是在文件里留下裸换行符，源码当场坏掉。源文本判据读不动坏源码时
**可能一条都不红**，`exit 0` 于是又是一次假通过。
⇒ **跨行结构的变异一律按行列表改**（参照脚本里原有的 `MUT-T` 写法），
且改完要核实结构前提（本轮 `MUT-BB` 就是先 throw"分组不在早退之前"
才没搬错方向）。

**③ 一个锚点在同一文件里出现两次。** `MUT-BG` 想只改实时收尾那一处
`RenderStatus.FramesTotal = 0;`，而两处文本完全一样 ⇒ `ReplaceExact`
报"命中 2 次"、exit 2。已改成按行号改，并在动手前**核实方向**
（第二处后面必须挨着「录制完成」日志），方向判反会把它证明成另一件事。

**④ 一个变异揭示了判据本身的漏洞。** `MUT-BH` 第一版把
`EtaSeconds = elapsed > 0.05 ? … : -1;` 改成 `-1; if (elapsed > 0.05) …`
——结果是 **exit 1，闸门保持全绿**。原因：⑥d 只查 `EtaSeconds\s*=`，
而"钉死成 -1"照样满足。**"有一个赋值"和"这个赋值有意义"是两件事。**
已把 ⑥d 改成要求一次带条件的真赋值（`(?!\-1\s*;)[^;]*[<>]`）。

### 顺带记下的一个文案坑

`LocCheck.CollectUsedKeys()` 用正则 `"([a-z][a-z0-9]*\.[a-z0-9_.]+)"`
扫 `Pages.cs` 与 `OnGui.cs` 的**原文**收集文案键——它**读的是整个文件、
连注释一起读**。于是任何形如 `小写.小写` 的字符串都会变成一条"漏翻文案键"，
**连在注释里举例都不行**。

> **2026-10-12 更新：** 当时踩到它的是控件名
> `private const string RowInputControlName = "…"`（点号接小写单词，长得和
> 真正的文案键一模一样），于是定成 `"OutDirBox"`，并由 ⑤c 钉住。
> 该常量已随内建输入框一起删掉（改成自绘控件、焦点自己管，不再需要
> `SetNextControlName`/`GetNameOfFocusedControl`），**这条坑本身仍然成立**——
> 以后再往这两个文件里写"带点号的短标识符"，同一个假红会再来一次。

---

## 八、按严重度逐项修复，已全部完成

按严重度逐项修复，每项补离线断言并做变异验证。**已全部完成**：

1. ~~偏差 3（音频取 clip，原理上不成立）~~ ✅
2. ~~偏差 14（校验）~~ ✅
3. ~~偏差 1 + 1b（HitContext 状态轨，工作量最大）~~ ✅
4. ~~偏差 19（回放期间的状态夹逼四项）~~ ✅
5. ~~偏差 15（版本兼容层）~~ ✅
6. ~~偏差 6/8/9（帧序、`-vf` 三段式、中间文件扩展名）~~ ✅
7. ~~偏差 7（编码器看门狗，复核后做了减法）~~ ✅
8. ~~偏差 10（补丁框架：异常兜底 + Transpiler 分派）~~ ✅
9. ~~状态码 3/5/6（原注释本身就错了）~~ ✅
10. ~~偏差 20（命中误差分析，清单之外发现）~~ ✅
11. ~~偏差 21（离线渲染时间钉扎从未生效）~~ ✅
12. ~~偏差 22（回放期未禁 `UpdateFreeroam`）~~ ✅
13. ~~偏差 23（退出关卡时的拆除链 + 空 Feature 语义）~~ ✅
14. ~~偏差 24（回放期 hitbox 死亡绕过 `noFail`）~~ ✅ `9642b4e`
15. ~~其余六个特性目录：把"不补"的结论钉成反向断言~~ ✅
16. ~~偏差 25（UI 逻辑 11 条：死开关、顶栏空白、摆设按钮、每键提交…）~~ ✅ 本轮

---

## 九、这一轮改了什么、没改什么

本轮**没有**新增功能，全是"照着 Spectre 逐项比对之后"的修正。三类：

**一类是补缺口**：状态轨（偏差 1+1b）、回放期状态夹逼（19）、
版本兼容层（15）、`-vf` 三段式（8）、中间文件扩展名（9）、
补丁异常兜底（10）、结算状态判据、离线渲染时间钉扎（21）、
回放期禁 `UpdateFreeroam`（22）、退出时的拆除链（23）、
回放期 hitbox 死亡屏障（24）。

**一类是复核后**减法**：编码器看门狗（7）——原判断"缺三层"是错的，
真正兜住卡死的那层我们早已有，而泵线程里那个看门狗会在收尾阶段
**必然误判**，本轮把它删了。帧序（6）也不是缺功能，而是做法不同
（我们不允许并发回读，比 Spectre 的队列更简单）。

还有一类是**复核后确认"不用补"**：其余六个特性目录（Other / Editor /
CustomJudge / JudgeBar / ResultsPlus / Perf）核完一条缺口都没有，
结论钉成反向断言（见七之二）。这类"什么也没改"同样要写进文档，
否则半年后没人记得它是**查过的**，只会以为是**没人看过**。

**一类是复核时揪出的真 bug**（原对照文档里没有的）：
- `FrameSource.Request()` 无条件返回 true，相机列表为空时那一帧永远
  等不到回调 → 画面黑、音频走、日志全无（偏差 6 复核时发现）
- 保存快捷键的状态魔数 `3/5/6` 实际是存档点/失败/失败二，
  结算状态 7 被漏掉 → 保存键在游玩中没反应
- `-vf` 缺 `pad`，奇数宽高时 libx264 **直接失败**、产物 0 字节
  （偏差 8，用真 ffmpeg 实测证明）
- **`FeatureGate.IsEnabled` 没有空串分支**，所以"不写 Feature"被当成
  "功能名是空串"去查表 → 查不到 → false → 所有不设门控的补丁**一个都挂不上**，
  而 `Failures` 里一条都没有（偏差 23 连带查出的）。
  更麻烦的是日志那句「playback 组计数偏少是正常的」恰好替它打了掩护。
- **闸门自己的一条工具教训**：`edit` 每次写 `tools\*.ps1` 都会**剥掉 UTF-8 BOM**，
  之后 PowerShell 把中文读成乱码、脚本直接解析失败（报一个指向语法块的
  ParserError，完全指不到编码）。已改成每次改完补回 BOM。
- **`ReplaceExact` 静默吃掉了额外实参**，于是 MUT-AN / AO / AQ 三条变异
  **根本没施加**、闸门如实全绿、脚本却报「这条断言是假的」。
  根因：PowerShell 命令调用模式里 `+` 是普通实参，`F 'a' 'b' + "`n" + 'c'`
  会把后面的全丢掉。已加 `if ($args.Count -ne 0) { throw }` 永久堵死
  （详见第七节的"变异脚本自己也坏过两次"）。
- **封禁词判据区分大小写**，被 `FeatureGate.IsEnabled("ScrMisc")` 绕过一次
  （MUT-AM）。已改成忽略大小写，实测零误伤。
- **整轮 UI 逻辑缺陷（偏差 25）在 116 条断言全绿的情况下全部漏网**，
  而那一轮唯一抓到的新真 bug 是**进度条的 NaN**（`0/0` → `Clamp01`
  对 NaN 失效 → `DrawTexture` 抛异常打断整个 OnGUI）。
  原因不是断言写得不好，而是**闸门没有界面层**——详见七之三。

**一类是"清单本身有洞"**：偏差 20。清单列的是我当时已知的偏差，
而重列 Spectre 的特性文件才看见 `ReplayAnalyzer` 这个整块缺失——
**它要的数据我们早就存了，只是从来没人读**。
已记在文档里提醒：清单本身也需要复核。

### 偏差清单当前状态

| 偏差 | 状态 |
|---|---|
| 1 / 1b 状态轨 | ✅ `654d733` |
| 19 回放期状态夹逼 | ✅ `bb0a0e8` |
| 15 版本兼容层 | ✅ `183e2fe` |
| 7 编码器看门狗 | ✅ `cd9640b`（做减法） |
| 6 / 8 / 9 帧序、`-vf`、扩展名 | ✅ `a5da0f7` |
| 10 补丁框架 | ✅ `b277ffc`（按需补） |
| 状态码 3/5/6 | ✅ `1dfb6b7` |
| 20 命中误差分析 | ✅ 本轮 |
| 21 离线渲染时间钉扎 | ✅ 本轮（修的是"一直没生效"） |
| 22 回放期未禁 UpdateFreeroam | ✅ 本轮 |
| 23 退出时的拆除链 | ✅ 本轮（修的是"只有按钮能停"） |
| 24 hitbox 死亡绕过 noFail | ✅ `9642b4e`（修的是"屏障压根不存在"） |
| 25 UI 逻辑 11 条 | ✅ 本轮（修的是"代码在、但没接到界面上"；另附进度条 NaN 真 bug） |
| 其余六个特性目录 | ✅ 本轮钉成反向断言（**结论是不补**，见七之二） |
| 3 / 14 音频取 clip、校验 | ✅ 更早轮次 |
| 16 / 17 哈希陷阱 | ✅ 已在 `FfmpegArgs` 侧按不变文化处理 |
| 2 / 3a / 4 / 5 / 18 | ✅ 不是缺陷，见各节说明 |

### 未修的四项，说明为什么

- **偏差 2（按键注入方式）**：Spectre 用 Win32 `SendInput`，我们也用。
  架构一致，无需改。
- **偏差 4（渲染架构）**：Spectre 只有离线逐帧确定性渲染，我们是「边玩边录」。
  这是**产品形态差异**而非缺陷，且边玩边录对玩家更友好。改成 Spectre 那套
  意味着重写整个渲染管线，收益不匹配风险。
- **偏差 18（`UpdateHoldKeys` 越界）**：已用 Finalizer 兜住
  （提交 `554b8fd`），属于"已修"，不再是待办。
- **`TogglePauseGame` 的编辑器内回放暂停屏蔽**（七之二）：Spectre 在
  playMode + auto + 回放中把暂停按掉。论证已写在那节——我们的帧栅格是
  墙钟、游标读游戏暂停即停，两者天然不会错位。**属于"核过确认不用补"，
  不是待办。**

### 仍然成立的五条限制

1. **离屏闸门不覆盖 GEOMETRY/CLICKS**——`GUILayout` 是声明式的，
   复刻不出布局与事件派发。
2. **回放与视频渲染的最终效果只能在游戏里实跑确认。** 闸门守的是
   "不会静默废掉整条链路"的那类规则（用**真 ffmpeg** 跑奇数宽高、
   用**真程序集**反射择路），不是"画面好看"。
3. **`States` 与 `scrPlayer.Hit` 的取值以本机 r150 为准**。
   闸门会在这两处读到不同值时变红，那是有意的：
   **它意味着需要重新确认，而不是可以忽略。**
4. **① 判据扫的是"**去注释后**的代码"，所以注释里可以自由讨论某个 API**
   （那正是"为什么不补"的记录方式）。代价是：有人把真正的调用**拼进
   注释里的字符串**再反射执行，那条路是通的——但那已经不是"顺手补一段"
   了，而是有人刻意绕闸门。MUT-AR 与 MUT-BZ 就是守这一半的：
   它们把封禁词写进注释并要求闸门**保持全绿**，
   证明"必要说明不会误伤"这半边也在跑。
5. **`UiLogicCheck` 判的是控制流形状，不是玩家看到的画面。**
   它能钉住"实时支里不许有开始渲染按钮"，钉不住"这个按钮点下去
   有没有反应"——IMGUI 是声明式的，闸门没有布局也没有事件派发。
   尤其 `RowInput` 那三条：它们能证明提交**只在回车时发生**，
   证明不了"玩家按了回车真的提交了"（失焦提交、焦点在别的控件上
   这些情况都得真机点一遍）。
