# 回放运行时与补丁层 审计报告

审计范围：`SpectreReplay\Features\Replay\Runtime\`（10 个文件，任务书说 9 个，实际 10 个）、
`SpectreReplay\Features\Replay\Patches\`（7 个）、`SpectreReplay\Patch\Shared\`（6 个）、`SpectreReplay\Main.cs`。
游戏侧事实全部取自 `D:\Projects\adofai-libs\source\Assembly-CSharp\`（`Releases.cs` 已核实 `releaseNumber = 150`）。
反射字符串另用装机的 `SkyHook.Unity.dll` 复核过（见「已核查但判定无问题」⑬）。

## 结论摘要

**无 P0。** 最严重的是三条状态泄漏，都属于「停了/关了但没还回去」这一类，与前几个 commit 修的是同一族但
不在同一处：

1. **P1** `ReplayPlayer.Stop()` 不复位 `HitLocked`。玩家在关卡里点回放页的「停止」后，`Patch_Hit` 的
   Prefix 会一直 `return false` 把游戏自己的 `Hit` 整个跳过——本局从此无法判定，只能重开关卡。
2. **P1** 关「回放总开关」（或 UMM 里禁用模组）会卸掉全部补丁，但不停止回放：`GCS.noFail` /
   `GCS.difficulty` / `GCS.hitMarginLimit` 无人还原，`noFail=true` 泄漏到之后所有正常游玩。
   `Pages.cs:325` 的注释还明确承诺这条路径「语义干净」，注释与代码冲突。
3. **P2** `TryStartPlayback` 的两条失败分支还原了难度与 noFail，唯独漏了 `GCS.hitMarginLimit`。

另有两条「静默失效」值得注意：`DisableSendInput = true` 让整条 SendInput 镜像路径恒不执行（离线闸门
⑪b 因此是一条空断言）；`KeyViewerBridge._installed` 不随 UnpatchAll 复位，关开一次模组后键位显示
永久失联且无日志。

整体画像：补丁目标/形参/返回值语义与 r150 源码**全部对得上**（27 个目标逐一核对，见文末），主要风险
不在「挂错地方」，而在**退出路径的状态归还**与**几个恒假/恒真的开关**。

## 发现

### F1 [P1] `ReplayPlayer.Stop()` 不复位 `HitLocked` —— 点「停止」后本局判定全灭

- 位置：
  - `SpectreReplay\Features\Replay\Runtime\ReplayPlayer.cs:45-105`（`Stop()` 全body，无 `HitLocked` 复位）
  - `SpectreReplay\Features\Replay\Patches\Patch_Recording.cs:41`（消费点）
  - 触发点：`SpectreReplay\Pages\Pages.cs:514-518`（回放页「停止」按钮）

- 证据：

```csharp
// Patch_Recording.cs:38-43
var controller = scrController.instance;
if (HitLocked) return false;        // ← Prefix 返回 false = Harmony 跳过原方法，游戏自己的 Hit 不执行
if (!__instance.responsive) return false;

// ReplayPlayer.cs:29-31（Start 置真）
    is_playing = true;
    is_recording = false;
    HitLocked = true;

// ReplayPlayer.cs:95-96（Stop 尾部：只还原 IgnoreMarkFail，没有 HitLocked）
IgnoreMarkFail = false;
SyncEditorNoFailButton();
```

触发链：`Pages.cs:517 PlayActions.StopPlaying()` → `ReplayPlayer.Stop()`（`is_playing=false`，但
`HitLocked` 仍是上一次 `ReplayHit` 里设的 `true`，见 `ReplayPlayer.cs:584`）→ 玩家继续用手柄/键盘打谱 →
`scrPlayer.Hit` 的 Prefix 返回 `false`，游戏命中逻辑一次都不跑。

- 影响：**静的**。玩家看到的是「停止之后键盘完全没反应、星球不再前进」，没有任何报错或提示；
  只有重开关卡（`Patch_StartRewind` 的 Postfix 在 `Patch_Lifecycle.cs:87` 会把 `HitLocked` 复位）或
  卸载回放（`PlayActions.SwitchToRecordMode`，`SpectreState.cs:235`）才恢复。而 `Pages.cs:519-521` 的
  设计意图写得很清楚：「『停止』与『卸载』是两件事：停止只是不再注入按键，回放仍装着」——玩家点停止
  就是为了接着自己打。

- 建议：`ReplayPlayer.Stop()` 里在 `is_playing = false` 之后加 `HitLocked = false;`（与
  `IgnoreMarkFail = false` 同一批收尾）。注意不能只在 `if (data != null)` 分支里加——`Stop()` 必须在
  任何提前 return 之前就复位。

- 置信度：高（`HitLocked` 的全部 8 个读写点已逐一列出，`Stop()` 确实没有复位；`Pages.cs:517` 是唯一
  只调 `StopPlaying()` 而不接 `SwitchToRecordMode()` 的用户入口）

### F2 [P1] 关总开关/禁用模组会卸补丁但不还接管项：`noFail`、难度、`hitMarginLimit` 静默泄漏

- 位置：
  - `SpectreReplay\Main.cs:63-73`（`OnToggle(false)`）
  - `SpectreReplay\Pages\Pages.cs:66`（`ReplayOn` 开关）+ `Pages.cs:1647-1663`（`SaveNow` → `Settings.check` + `Refresh`）
  - `SpectreReplay\Patch\Shared\SpectrePatchHub.cs:246-254`（谓词翻假只 `Unapply`，不跑任何业务收尾）
  - 接管点：`SpectreReplay\Features\Replay\Patches\Patch_Lifecycle.cs:276-307`

- 证据：

```csharp
// Main.cs:63-72
if (!isToggled)
{
    Ui.OnGui.FlushConfig();
    SpectreState.SetGetKeyAsyncEnabled(false);
    SpectrePatchHub.UnpatchAll();   // ← 补丁全卸
    PatchManager.UnpatchAll();      // ← 但没有任何 PlayActions.StopPlaying()
    MainThread.Uninstall();
    AudioRecorder.StopAllMicrophones();
    return true;
}

// Patch_Lifecycle.cs:304-306（被接管的三项）
GCS.hitMarginLimit = StoredHitMarginLimit;
CachedNoFail = scrController.instance.noFail;
scrController.instance.noFail = true;
```

`ReplayPlayer.Stop()` 是三项唯一的还原点（`ReplayPlayer.cs:51` / `83-94`），而它只在 `is_playing` 为真时
执行；关开关这条路径根本不会进它（`is_playing` 被留着为 `true`）。

- 影响：**静的**，且比 F1 更糟——泄漏到「之后的正常游玩」：`scrPlayer.cs:315`（PurePerfectOnly 判失败）、
  `scrPlanet.cs:661`（`hitMargin.IsCounted`）、`scrFloor.cs:939` 都直接读 `GCS.hitMarginLimit`；
  `noFail=true` 则让后面所有关卡永不失败。三项都要等到游戏重启才会被 `ADOStartup.cs:215`
  （`GCS.hitMarginLimit = Persistence.hitMarginLimit`）之类重新覆盖。

  另外这是**注释与代码冲突**：`Pages.cs:317-325` 的注释说删掉「模拟按键」开关是因为「真要停止注入，用
  ReplayOn 总开关——它连补丁一起卸，语义干净」，而实际语义并不干净。

- 建议：两条路径都先走一次 `SpectreState.StopAll()`（现成的，`SpectreState.cs:674-686` →
  `PlayActions.StopPlaying/StopRecording` → `ReplayPlayer.Stop()` 内部把三项还回去），再卸补丁。
  更稳的做法是让 `Replay` 功能的谓词从真翻假时由 Hub 回调一次宿主收尾（Hub 目前没有这个钩子，属结构
  性缺口）；最小改动是在 `Main.OnToggle(false)` 与 `Pages` 那个 `ReplayOn` 行的 `Commit` 回调里各加一句。

- 置信度：高（`Stop()` 是唯一还原点已核对；`Refresh` 的卸补丁路径不碰业务状态已核对）

### F3 [P2] `TryStartPlayback` 失败路径漏还 `GCS.hitMarginLimit`

- 位置：`SpectreReplay\Features\Replay\Patches\Patch_Lifecycle.cs:299-307`（接管）、`333-351`（`!is_playing`
  分支）、`367-382`（`catch` 分支）

- 证据：

```csharp
// :299-307（接管）
StoredHitMarginLimit = (HitMarginLimit)0;
if (data.HitMarginLimit != null && ...) StoredHitMarginLimit = result;
GCS.hitMarginLimit = StoredHitMarginLimit;
CachedNoFail = scrController.instance.noFail;
scrController.instance.noFail = true;

// :335-347（启动被外部阻断时的还原——难度和 noFail 都还了，唯独没有 hitMarginLimit）
if (CachedDifficulty.HasValue) { GCS.difficulty = CachedDifficulty.Value; CachedDifficulty = null; SyncDifficultyLabel(); }
if (CachedNoFail.HasValue) { ... failController.noFail = CachedNoFail.Value; CachedNoFail = null; }
Reject(..., "ApiBlockPlaying set between validation and StartPlaying");
return;
```

同一条漏项在 `catch (Exception ex)` 的 `367-382` 一模一样地重演一遍。

- 影响：回放**没开播**（另一 mod 在窗口期调了 `BlockPlaying`，或 `StartPlaying` 抛异常），但
  `GCS.hitMarginLimit` 已经停在回放文件里的档位（例如 `PerfectsOnly`）。之后正常游玩每一击都可能被判
  失败（`scrPlayer.cs:315`）或计分口径改变（`scrPlanet.cs:661`），直到游戏重启。

- 建议：两处还原块各补一行 `GCS.hitMarginLimit = Persistence.hitMarginLimit;`（与 `ReplayPlayer.Stop()`
  的 `:51` 保持一致）。顺带把「接管」与「还原」抽成一对 helper，避免下次再漏第三项。

- 置信度：高

### F4 [P2] `KeyViewerBridge._installed` 不随 `UnpatchAll` 复位 —— 关开一次模组，键位显示永久失联

- 位置：
  - `SpectreReplay\Features\Replay\Runtime\KeyViewerBridge.cs:37-38, 52-56, 85`
  - `SpectreReplay\Main.cs:68-69`（`UnpatchAll` 清 spec，手动补丁一并消失）
  - `SpectreReplay\Patch\Shared\SpectrePatchHub.cs:295-306`（`UnpatchAll` 只清 `_specs`）

- 证据：

```csharp
// KeyViewerBridge.cs:37-38
private static bool _installed;
private static bool _attemptedThisSession;

// :52-56
try
{
    if (_installed) return;                        // ← 一旦为真，永不进 body
    if (_attemptedThisSession && !force) return;
    ...
    _installed = true;                             // :85，之后没有任何地方复位
```

`PatchManual` 走的是 `_specs`（`SpectrePatchHub.cs:180-207`），`UnpatchAll`/`Initialize` 会 `_specs.Clear()`，
那条挂在 KeyViewer 上的补丁随即被卸掉；但 `KeyViewerBridge` 自己的 `_installed` 是另一个静态类里的字段，
没人清。

- 影响：**静的**。UMM 里禁用一次 SpectreReplay 再启用，或在设置页拨一次总开关再拨回来，之后每次
  `ReplayPlayer.Start()` 里那次 `KeyViewerBridge.TryInstall()` 都在第一行 return——键位显示器整局全黑，
  且因为走的是 `return` 而不是异常，**连一行日志都没有**。

- 建议：给 Hub 加个查询（`HasManualPatch(string id)`），`TryInstall` 改成
  `if (_installed && SpectrePatchHub.HasManualPatch(PatchId)) return;`；或在 `Main.OnToggle(false)` 里显式
  `KeyViewerBridge.Reset()`。注意 `_attemptedThisSession` 的语义（「程序集在但类型不匹配」才置真）是对的，
  不要连带复位它。

- 置信度：高

### F5 [P2] `DisableSendInput = true` 让整条 SendInput 镜像路径恒不执行，并使闸门 ⑪b 成为空断言

- 位置：
  - `SpectreReplay\Features\Replay\Runtime\ManagedInput.cs:44-56`
  - 消费点：`SpectreReplay\Features\Replay\Runtime\ReplayPlayer.cs:68-79, 364, 390-398`；
    `SpectreReplay\Features\Replay\Runtime\KeyboardSimulation.cs:123-164`
  - 离线判据：`tools\UiPreview\CrossPlatformInputCheck.cs:179-180`

- 证据：

```csharp
// ManagedInput.cs:46, 55-56
internal const bool DisableSendInput = true;

internal static bool Mirror
    => !DisableSendInput && is_playing && Application.isFocused;   // ← 恒为 false
```

`Mirror` 的全部消费点（grep 只有两处）因此都是死代码：`ReplayPlayer.cs:68` 的
`if (PlayMode && ManagedInput.Mirror)` 补发抬起、`:364` 的 `bool sendInput = ...`、`:390-394` 的
`CurrKeyState` 维护、`:398` 的 `SendInputs(inputs)`。`FastForward` 里专门为 SendInput 同步
`CurrKeyState` 的那段（`ReplayPlayer.cs:193-197`）也随之只剩「写进去没人读」。

- 影响：三层。
  1. **注释与代码冲突**：`Mirror` 的 XML 注释仍按「会镜像到操作系统、影响其它模组按键显示」来写
     （`ManagedInput.cs:48-54`），而实际上永不发生。`KeyboardSimulation` 里那套 UIPI 错误码诊断
     （`:148-163`）在真机上从未有机会打印。
  2. **判据是空的**：`CrossPlatformInputCheck.cs:179` 断言源码里存在
     `sendInput = ManagedInput.Mirror && !asyncInject` 这个**形状**。因为 `Mirror` 恒假，这条断言
     永远成立、也永远验不到「SendInput 与异步链互斥」这件事本身——正是本次任务要我找的「判据本身是假的」。
  3. 一旦有人把常量翻成 `false`（看成「打开一个功能」），这条从未在真机上跑过的路径立刻带电上线：
     回放期间会真的往 OS 注入按键。

- 建议：要么在常量旁写清「为什么永久禁用 + 谁替代了它（KeyViewerBridge + RDInput 覆盖）」，并把
  `ReplayPlayer.cs:68-79, 390-398` 与 `KeyboardSimulation.SendInputs` 一并删除；要么把关掉这件事做成
  显式配置项。闸门那一行应改成断言「Mirror 恒假，且不存在第二条按键外泄路径」。

- 置信度：高（常量值、两处消费点、闸门断言行号都已核对；`Mirror` 无其它读写）

### F6 [P2] 录制侧时间基准仍用上一帧的 `songposition_minusi`，与回放侧刻意改正的现场公式不对称

- 位置：
  - `SpectreReplay\Features\Replay\Runtime\ReplayRecorder.cs:147, 156, 191`（三处 `SongPosition = conductor.songposition_minusi`）
  - 对照：`SpectreReplay\Features\Replay\Runtime\ReplayPlayer.cs:299-339`（`CurrentSongPosition`）与
    `:251-254` 的注释

- 证据：

```csharp
// ReplayPlayer.cs:251-254（回放侧明确否掉 songposition_minusi）
// 与 PumpInput() 同因：songposition_minusi 在同一帧的 Update 末尾才重算，
// 这里读到的是上一帧的值。按游戏自己的公式现场算。

// ReplayRecorder.cs:143-148（录制侧照旧用那个值）
events.Add(new KeyEvent
{
    KeyCode = (ushort)i,
    IsPressed = true,
    SongPosition = conductor.songposition_minusi   // ← 上一帧的重算结果
});
```

游戏侧事实：`scrConductor.cs:849-858` 在 `Update` 末尾才写 `songposition_minusi`；而
`RecordKeyboardState` 由状态机 Update 的 **Postfix** 调用（`Patch_Playback.cs:22, 102`）。

- 影响：录下来的按键时间戳系统性偏晚约一帧（60fps ≈ 16.7ms）。回放侧用现场公式（比的是「此刻」），
  于是长按起点/多压时序整体偏晚。**命中判定不受影响**（走 `HitSnapshot` 的角度），所以症状是「音画对、
  长条手感略拖」这一类很难复现的抱怨。

- 建议：把 `CurrentSongPosition` 提到共享 helper（现在它是 `ReplayPlayer` 的 private），录制侧三处一并改用。
  若决定保留现状，请在 `ReplayRecorder.RecordKeyboardState` 上写明「录制允许一帧滞后」的理由。

- 置信度：中。确认需要一次实测：同一局同一段按键，分别打日志比较「同帧 `songposition_minusi`」与
  「现场公式值」，看差值是否稳定在 1 帧；若两者恰好相等（例如录制 Postfix 其实跑在下一帧的
  `scrConductor.Update` 之后），本条不成立。

### F7 [P3] `AsyncInputInjector.Inject` 的反射字段校验比 `Injectable` 少一项

- 位置：`SpectreReplay\Features\Replay\Runtime\AsyncInputInjector.cs:85-86`（校验 5 项）vs `:136`（只校验 4 项）、
  使用点 `:175`

- 证据：

```csharp
// :85-86（Injectable：五项全查）
if (_fTimeSec == null || _fTimeSubsecNano == null || _fType == null
    || _fLabel == null || _fKey == null)
    return false;

// :136（Inject：漏了 _fTimeSubsecNano）
if (_fTimeSec == null || _fType == null || _fLabel == null || _fKey == null) return Fail();

// :175（然后就用了它）
_fTimeSubsecNano.SetValue(box, (uint)((rel % 10000000L) * 100L));
```

- 影响：若 `TimeSubsecNano` 这个字段名被 SkyHook 改掉，`Injectable` 仍说「可用」，而每次 `Inject` 都在
  try 里抛 NRE → `Fail()` 记失败帧 + `:210` 打一条 Warning。托管层会兜住（不发散），但每个按键一条警告，
  且 `Healthy` 的语义被「反射缺失」污染成一个每帧抖动的值。

- 建议：抽一个 `private static bool ResolvedOk => _fTimeSec != null && _fTimeSubsecNano != null && _fType != null
  && _fLabel != null && _fKey != null;`，两处都用它。

- 置信度：高（纯静态可读；顺带说明：这五个字段名在装机 `SkyHook.Unity.dll` 里**全部存在且类型一致**，
  见文末⑬，所以这条只是防御性问题，不是当前故障）

### F8 [P3] `KeyboardSimulation.SendInputs(List<INPUT>)` 每帧 `ToArray()` 分配，与 `_inputBuffer` 的复用意图矛盾

- 位置：`SpectreReplay\Features\Replay\Runtime\KeyboardSimulation.cs:123-124`；调用点
  `SpectreReplay\Features\Replay\Runtime\ReplayPlayer.cs:398`

- 证据：

```csharp
// KeyboardSimulation.cs:123-124
internal static void SendInputs(List<INPUT> inputs)
    => SendInputs(inputs.ToArray());        // 每次调用一次数组分配

// ReplayPlayer.cs:12-13（复用的初衷）
// 复用列表，避免 PlayKeyboardEvent 每帧分配 List<INPUT>
private static readonly List<KeyboardSimulation.INPUT> _inputBuffer = new List<KeyboardSimulation.INPUT>(256);
```

- 影响：`PumpInput` 在**有事件的那一帧**都会走到 `:398`，于是每帧一次 `INPUT[]` 分配（`INPUT` 是
  `type` + 64 字节 union，一个 64 键批就是 4KB+）。目前因 F5 恒不执行；一旦 SendInput 被打开就会成为
  每帧 GC 热点。空列表时 `List<T>.ToArray()` 也会 `new T[0]`。

- 建议：补一个 `SendInputs(List<INPUT>, int count)` 或直接持有 `INPUT[]` 缓冲 + 计数，配 `SendInput(n, buf, size)`。

- 置信度：高

### F9 [P3] `GetState` 的 Postfix 对玩法动作无条件覆盖 `__result`，连 `ButtonState.IsUp` 也覆盖成 false

- 位置：`SpectreReplay\Features\Replay\Patches\Patch_InputInjection.cs:45-50`；
  `SpectreReplay\Features\Replay\Runtime\ManagedInput.cs:236-247`

- 证据：

```csharp
// Patch_InputInjection.cs:46-50
internal static void GetState_Postfix(ref bool __result, InputAction inputAction, ButtonState state)
{
    if (!ManagedInput.Active || ManagedInput.NativeTakeover || !IsGameplayAction(inputAction)) return;
    __result = ManagedInput.Matches(inputAction, state);   // ← IsUp 也会被写成 false
}

// ManagedInput.cs:243-245
default: return false;   // IsUp 走这里
```

游戏侧：`RDInputType_Keyboard.cs:179` 的 `IsUp => !Input.GetKey(key)`；`RDInput.cs:327` 的 `GetStateKeys`
对 `IsUp` 走 `GetUpKeys()`。

- 影响：游戏本体从不给玩法动作查 `IsUp`（只有 `WentDown/WentUp/IsDown` 三类，见 `RDInput.cs:82-152` 的
  属性表），所以对原版无害；但 `RDInput.GetState` 是公开 API，别的模组查
  `GetState(InputAction.Action1, ButtonState.IsUp)` 时回放期间会恒得 `false`（= 「键没抬起」），语义反了。
  更麻烦的是这条将来若有人给 `IsUp` 加语义（比如「抬手判定」），覆盖会静默改判。

- 建议：`GetState_Postfix` 开头加 `if (state == ButtonState.IsUp) return;`，与 `ManagedInput.Matches`
  里那条「IsUp 对全部键求反没有意义」的注释对齐。

- 置信度：中（对其它模组的影响面无法从本工作区观测；需要至少一个真实第三方 mod 的 `IsUp` 用法才能定为 P2）

### F10 [P3] `SpectrePatchHub` 的失败清单与「已弹过提示」标志不随重新 Initialize 清空

- 位置：`SpectreReplay\Patch\Shared\SpectrePatchHub.cs:684-685`（`ApplyFailures` / `failureSummaryShown`）、
  `:111-117`（Initialize 清了 `_featureToggles`/`_guardedMethods`/`_guardStats`/`_warnedUnregistered`，
  这两项没清）、`:295-306`（UnpatchAll 同样没清）

- 证据：

```csharp
// :684-685
private static readonly List<string> ApplyFailures = new();
private static bool failureSummaryShown;

// :111-117（Initialize 的清理清单）
_featureToggles.Clear();
_guardedMethods.Clear();
_guardStats.Clear();
_warnedUnregistered.Clear();
_initialRefresh = true;      // ← 唯独这两项没重置
```

- 影响：`Ui.OnGui.PatchFailures`（`Main.cs:123`）读的是 `ApplyFailureCount`。修好某个挂不上的补丁后，
  玩家关开一次模组，设置页顶部仍然常驻那条旧失败数（`RecordApplyFailure` 还会因为 spec.Id 相同而拒绝
  更新详情，`:698-709`），误导排查方向；而 `ShowApplyFailureSummary()` 因 `failureSummaryShown` 恒真
  不会再弹。

- 建议：`Initialize` 的清理块里加 `ApplyFailures.Clear(); failureSummaryShown = false;`。

- 置信度：高

### F11 [P3] `PatchManager` 的 lazy 轮询子系统在本宿主零调用点，且会在线程池线程上应用 Harmony 补丁

- 位置：`SpectreReplay\Patch\Shared\PatchManager.cs:134-142, 239-327`

- 证据：

```csharp
// PatchManager.cs:270（在 ThreadPool 线程上循环）
await Task.Delay(100, token).ConfigureAwait(false);

// PatchManager.cs:304（然后在这个线程上真正打补丁）
_harmony.CreateClassProcessor(registration.PatchType).Patch();
```

grep 全项目：`RegisterLazyPatches` / `ApplyLazyPatchesAsync` 只有定义、没有调用点（本模组的所有开关都走
`SpectrePatchHub.Refresh`）。

- 影响：死代码，但它带着一个后台线程 + `CancellationTokenSource` 生命周期（`UnpatchAll` 里还要
  `task.Wait(2000)` 阻塞主线程），纯负担。更值得写下来的是：**它一旦被接线就是错的**——Harmony 的
  `Patch()`（JIT detour 安装）与 Unity 都不是线程安全的，而且它会与主线程的 `Refresh()` 竞争同一份
  `_appliedPatches`。注释里对此没有任何提示。

- 建议：删除；若要保留，改成主线程队列并在注释里写明「不许在后台线程 `Patch()`」。

- 置信度：高（零调用点已 grep 全项目确认）

### F12 [P3] 两处注释指向不存在/已漂移的位置

- 位置：
  - `SpectreReplay\Features\Replay\Runtime\ReplayAnalyzer.cs:63-67`
  - `SpectreReplay\Features\Replay\Patches\Patch_Playback.cs:84-86`

- 证据：

```csharp
// ReplayAnalyzer.cs:64-65
// 0/负数/NaN/Infinity 的速度会让 CustomJudge.BoundaryRaw 的判定窗口
// 撑成无穷大（每次命中都算最高档）。

// Patch_Playback.cs:85
// Patch_Playback:187 判到没有 KeybdSoundStartTick 就静默 return，
```

- 影响：两处都会把排查者引到错地方。
  1. `CustomJudge` 这个类型在 `Assembly-CSharp` 全部 1235 个 .cs 里**一次都不出现**（已 grep 全目录）；
     实际判定窗口来自 `scrMisc.GetAdjustedAngleBoundaryInDeg`（`scrMisc.cs:254`），本项目代码里也是调它
     （`ReplayAnalyzer.cs:101-104`）。
  2. `Patch_Playback.cs` 里那段「没有 `KeybdSoundStartTick` 就 return」的实际位置是 `:221-231`，不是 187；
     自引用行号在重构后没跟着改。

- 建议：`CustomJudge.BoundaryRaw` 改成 `scrMisc.GetAdjustedAngleBoundaryInDeg`（或直接删掉类型名，只描述
  窗口会变成无穷大）；`Patch_Playback:187` 改成 `Patch_Playback.cs:221`。

- 置信度：高

## 已核查但判定无问题（避免下轮重复劳动）

1. **补丁目标在 r150 全部存在**（逐个核对，行号为游戏源码行）：`scrController.Won_Update:576`、
   `PlayerControl_Update:546`、`Countdown_Update:485`、`Checkpoint_Update:502`、`UpdateInput:2893`、
   `UpdateFreeroam:2813`、`OnLandOnPortal:1934`、`Fail2Action:669`、`Start_Rewind:1517`、
   `Restart:2635`、`QuitToMainMenu:2452`、`TogglePauseGame:2577`；`scrUIController.WipeToBlack:202`；
   `scnEditor.SwitchToEditMode:3171`；`scrConductor.Update:768`；`scrPlanet.MarkFail:1640`；
   `scrFloor.LightUp:1201`；`scrPlayer.Hit:734`、`HitInputEvent:637`、`ValidInputWasReleased:858`、
   `UpdateHoldKeys:699`；`RDInput.GetMain:294`、`GetState:282`、`GetStateKeys:308`、`GetMainHeldKeys:368`；
   `RDInputType_Keyboard.CountSpecialInput:235`；`RDInputType_AsyncKeyboard.GetSpecialInput:246`。
2. **形参与消歧全部对得上**：`GetMain_Postfix(ref int __result, ButtonState state)` ↔ `GetMain(ButtonState)`；
   `GetStateKeys_Postfix(List<AnyKeyCode> keys, ButtonState state)` ↔ `GetStateKeys(List<AnyKeyCode>, ButtonState)`；
   `OnLandOnPortal` 的两个普通形参名 `portalDestination` / `portalArguments` 都在目标上；
   `Patch_Hit.PrefixNew(long? hitTick, bool isAuto)` ↔ `Hit(long? hitTick, bool isAuto = false)`（唯一重载，
   消歧无需第二种信号）；`Patch_AsyncSpecialKeys.PostfixNew(List<AsyncKeyCode> keys)` ↔
   `GetSpecialInput(List<AsyncKeyCode> keys)`（形参名就是 `keys`）。
3. **版本分叉两条各钉一半**：`Patch_Hit` 的 `MaxVersion=149` / `MinVersion=150`、
   `Patch_AsyncSpecialKeys` 的同款分叉——r150 上旧的那条走 `VersionSkipped`（`SpectrePatchHub.cs:335-340`），
   不计入 `IsAvailable` 的失败（`:310-318`），形状正确。
4. **Prefix 返回值语义**：`Patch_TogglePauseGame`（目标返回 `bool`，Prefix 也返回 `bool`）、
   `Patch_UpdateFreeroam`、`Patch_MarkFail`（`ref scrMissIndicator __result` + 返回 bool）、
   `Patch_Hit.Record`——四处都是「false = 跳过原方法」，与 Harmony 语义一致。
   `Patch_HoldKeysCrashGuard` 的 `Exception Finalizer(Exception __exception)` 形态正确（Harmony 允许
   无 `__result` 的 finalizer），且只吞 `is_playing` 时的 `ArgumentOutOfRangeException`（`:44-50`）。
5. **`__instance` 用法**：本次范围内出现的 `__instance`（`Patch_Won_Update`、`Patch_PlayerControlUpdate`、
   `Patch_SwitchToEditMode`、`Patch_Restart`、`Patch_QuitToMainMenu`、`Patch_UpdateFreeroam`、
   `Patch_OnLandOnPortal`、`Patch_Hit`）全部打在实例方法上，无静态目标（与闸门的
   `InstanceInjectionCheck` 结论一致，不重复）。
6. **本范围内没有 Transpiler**（`PatchType.Transpiler` 只出现在 `VideoRender` 那边），所以「Transpiler 标签
   唯一性」不适用于我这份。
7. **`CountSpecialInput` 返回共享静态 buffer 但不会跨帧累积**：`RDInputType_Keyboard.cs:235-238` 先
   `keys.Clear()` 再填，所以 `Patch_KeyboardSpecialKeys` 的 `RemoveAll` 每帧都是干净的；`GetSpecialInput`
   同理（`:246-248`）。两条补丁都刻意不处理 `Escape`（`Patch_SpecialKeys.cs:84, 137-138`），与游戏
   `Cancel()` 共用同一列表的事实一致。
8. **`Patch_ConductorUpdate` / `Patch_CancelSaveKey` 的状态常量正确**：`States.cs:1-11` 的
   `Fail=5, Fail2=6, Won=7` 与 `Patch_Misc.cs:38-40` 完全一致。
9. **`Patch_LightUp` 的 `hitMarginLimit` 换值不影响判定**：判定读点在 `scrPlanet.SwitchChosen:661`，而
   `LightUp` 在 `MoveOneUnitInDirection` 里（`scrPlanet.cs:1196`），同一个 `Hit()` 里判定先于点亮，所以
   换值只作用于 `scrFloor.cs:1212` 的砖块着色，不会改判。换值后 Finalizer 恢复 `StoredHitMarginLimit`，
   与 `ReplayPlayer.Stop():51` 的最终值一致。
   - **该字段兼作闪色开关，换值是有意为之而非「反正无害」**：`scrFloor.cs:1212` 判
     `GCS.hitMarginLimit != HitMarginLimit.PerfectsOnly` 才调 `SetToRandomColor()`（`:1214`）。
     Prefix 换成 `Persistence.hitMarginLimit`，就是让闪色跟随**观看者**当前档位；判定路径仍读录像档位。
     两个需求取值相反，故换值范围必须缩到读开关那一小段，**不能**改成开播时夹一次——那样
     `HitMarginHelper.IsCounted` / `ffxCheckpoint` 会整段读到玩家档位，准确率随玩家设置漂移。
   - **副作用：成片闪色不确定**。`SetToRandomColor`（`scrFloor.cs:1451`）仅在
     `vfx.tileFlashStyle == TileFlashStyle.Rando` 时用 `UnityEngine.Random.Range` 取色（`Christmas`
     用 `conductor.beatNumber + seqID`，确定），且 `dontChangeMySprite` 早退；写成
     `floorRenderer.color` / `topGlow.color` 后保留，影响截图/渲染。要成片确定就得像已删除的
     `PlaybackClamps` 那样夹成 `PerfectsOnly`，与「闪色跟随玩家设置」不可兼得（`Patch_Misc.cs:109-115`）。
   - **异常路径也已覆盖（已修）**：原本用 Postfix 还原，`LightUp` 抛异常时 Postfix 不执行，值会停在
     玩家档位上；`scrPlanet.SwitchChosen:661` 的判定早于点亮，紧接着的下一拍就会按错严格度统计。
     现改为 `PatchType.Finalizer`（`Patch_Misc.cs:132-137`），框架保证正常返回与异常逃逸都会跑；
     异常原样 `return __exception` 放行，不吞不记日志。这是对上游 Spectre 的一处**刻意偏差**
     （上游为 Prefix+Postfix），已在 `tools/port-diff-audit.ps1` 的 `$knownDeviations` 登记。
10. **`CursorTrackSampler` 纯函数边界正确**：空轨迹返回 false；首前/尾后夹取；二分不变量
    `track[lo] <= pos < track[hi]` 成立；`span <= 0` 有除零兜底（`:49-54`）。
11. **`ReplayManager.SaveReplayNew` 的截断保护正确**：`opened` 只在 `FileStream` 构造**之后**置真
    （`:74-76`），因此编码/加密阶段失败不会删掉原文件；`File.Exists` 兜底与注释一致。
12. **`NativeTakeover` / `Usable` / `Injectable` 三者关系**已由闸门 ⑪/⑪b 钉死，逐条复核代码后确认
    现状正确（`ManagedInput.cs:71-85`、`AsyncInputInjector.cs:80-125`），不重复报。
13. **`AsyncInputInjector` 的 6 个反射字符串在装机 DLL 上全部命中**（`SkyHook.Unity.dll`，用
    `Assembly.LoadFile` + `GetType/GetField/GetMethod` 实测）：
    `SkyHookEvent` 是 **struct**（`IsValueType=True`，`Activator.CreateInstance` 合法），字段为
    `TimeSec:Int64` / `TimeSubsecNano:UInt32` / `Type:EventType` / `Label:KeyLabel` / `Key:UInt16`，
    `GetTimeInTicks():Int64` 存在；`SkyHookKeyMapper` 的 `NativeKeyCodeToKeyLabel` / `KeyLabelToNativeKeyCode` /
    `UnityKeyToSkyHookKey` 三个方法都存在且为 public static。写入时的类型（`long`/`uint`/`enum`/`ushort`）
    与字段类型逐项匹配，无静默失效风险。
14. **`GameVersion.ReadRelease` 的反射字符串正确**：`Releases.cs` 里 `public const int releaseNumber = 150`
    （字段名、静态、可 `Convert.ToInt32`）；`GCNS` 在 r150 不存在会走第二个候选 `Releases`，逻辑正确。
15. **`Main.OnToggle(true)` 的 Feature 注册与补丁声明一一对应**：`Replay` / `ReplayManualSave` /
    `ReplayCancelSave` / `ReplayAsyncInput` / `RenderClock` / `EffectRemover` / `SpecialKeys` 七个都在
    `Main.cs:88-112` 注册；本次范围内用到的四个 key（`Replay`、`ReplayManualSave`、`ReplayCancelSave`、
    `ReplayAsyncInput`、`SpecialKeys`）无遗漏，无「声明了但没注册」的孤儿功能。
16. **生命周期清理补丁刻意不带 Feature**：`Patch_WipeToBlack` / `Patch_SwitchToEditMode` / `Patch_Restart` /
    `Patch_QuitToMainMenu`（`Patch_Lifecycle.cs:21-48`）无 Feature 属性 = 恒挂载，与
    `FeatureKeys.cs:29-31` 的注释（「生命周期清理补丁不挂，保证关闭后切场景仍能清残留状态」）一致，
    属设计使然。
17. **`AsyncKeyRecorder` 的跨线程边界干净**：事件线程只做 `ConcurrentQueue.Enqueue`
    （`AsyncKeyRecorder.cs:31`），换算与排序全在主线程 `FlushFrame`（`:41-91`），且「导体判空在出队之前」
    （`:61-63`）——这条顺序是对的，事件留在队列里不会丢。
18. **`ReplayAnalyzer` 的 GCS 改动有 `finally` 兜底**：`contextChanged` 时 `finally` 还原难度与
    `currentSpeedTrial`（`:107-114`），异常路径也不漏。

## 版本适配观察

- **版本基线**：`Releases.cs` 的 `releaseNumber = 150` 与 `Main.cs:83-84` 的
  `Configure(typeof(ADOBase).Assembly, GameVersion.Release, 141, 150)` 一致；`Verified = Release == VerifiedBuild`
  因此为真，探针不会误报「未验证」。
- **r150 的真实形状 vs 本项目假设**（全部已核实，无需改）：
  - `scrPlayer.Hit(long? hitTick, bool isAuto = false)`（`scrPlayer.cs:734`）——r150 起的 `long?` 首参，
    本项目用 `MinVersion=150` 单独一条补丁接住。
  - `scrPlayer.HitInputEvent(bool isAuto = false, InputEventState state = Down, bool handleAll = true)`
    （`:637`）——`Hit` 内部在 `:746` 自调它（`handleAll:false`），所以 r150 上项目不再代发。
  - `scrPlayer.UpdateHoldKeys(long? targetTick = null)` 是 **private**（`:699`）——Harmony 能打，已实测
    形状（`tools\PatchTargetCheck` 覆盖）无需改动。
  - `RDInputType_AsyncKeyboard.GetSpecialInput(List<AsyncKeyCode> keys)` 是 **private void** 且有 `keys`
    形参（`:246`）——r149 的「返回 List」形状已不存在，本项目的两段式版本门控正好对上。
  - `scrController.stateMachine` 不在 `scrController` 自身声明里，来自基类
    `MonsterLove.StateMachine.StateBehaviour`（`StateBehaviour.cs:11-25`，**public 属性**）→
    `Patch_ConductorUpdate` 的 `controller.stateMachine.GetState()` 合法。
  - **两个 `isActive` 不是同一个东西**，这是 r150 异步输入最容易踩的坑：
    `scrConductor.Update` 用全局 `AsyncInputManager.isActive` 决定要不要调 `UpdateInput`
    （`scrConductor.cs:788-800`）；而 `scrController.UpdateInput` 自己按三个
    `RDInput.asyncKeyboard*.isActive` 早退（`scrController.cs:2900-2903`）；
    `PlayerControl_Update` 又只在 `!AsyncInputManager.isActive` 时才调
    `Simulated_PlayerControl_Update()`（`scrController.cs:548-554`）。
    理论上存在「全局真、三个输入类型全假」的组合：那时同步模拟被跳过、`UpdateInput` 又在 `:2902` 早退，
    游戏自己那帧**根本不读输入**。本项目这时 `NativeTakeover=false`，靠 `RDInput` 三个覆盖点活下来——
    这条兜底是必要的，**不要**为了让「路径更统一」把它删掉。
  - Unity 6（6000.3.25f1）仍启用旧 Input Manager：csproj 引 `UnityEngine.InputLegacyModule`，游戏自己
    `RDInputType_Keyboard.cs:179` 也用 `Input.GetKey`。**风险点**：`ReplayRecorder.RecordCursor`
    （`ReplayRecorder.cs:178`）直接读 `Input.mousePosition`，若游戏将来把 Active Input Handling 切成
    「Input System Package (New) only」，这一行会每帧抛 `InvalidOperationException`，而它所在方法没有
    try（只靠 `Patch_PlayerControlUpdate` 的 Guard 吞），表现为鼠标轨迹整段丢失 + 日志刷屏。
    `KeyboardSimulation.SampleKeyState` 走 user32，不受此影响；它的 Unity 回退分支（`:114-120`）也会因为
    `Input.GetKey` 抛错而把整张表填 0（`catch { down = false; }`）——即「录不到键」而不是「录错键」。
- **离线闸门的口径观察**：`docs\verify.md:15` 说已打印 110 条断言，`docs\verify.md:25,61` 仍写 97 条——
  文档内部不一致（非产品代码问题，属文档漂移）。另外 F5 指出的闸门 ⑪b 空断言值得在下一轮补一条
  「Mirror 恒假」的反向判据。
