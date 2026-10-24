# 界面、状态与兼容层 审计报告

> **⚠️ 本报告 F1 已被 Lead 撤回（2026-10-24，经用户裁定）。**
> 用户明确表示：**「另存为」是刻意保留的出口**——去完特效要把干净版存到别处，正是这个功能的用途。
> 原实现只禁 `SaveLevelEditorAction`（Ctrl+S 与两颗保存按钮，目标就是当前 `levelPath`，等于覆盖原谱），
> 而 `SaveLevelAsEditorAction` 那条链是设计者有意留的旁路。Lead 一度把前缀下沉到两者的共同出口
> `scnEditor.SaveLevel`，等于把唯一合法的保存方式也堵死，属**过度拦截**，已撤销（补丁文件已删除）。
> **F2 仍然成立**：关掉开关后内存里仍是去特效版，此时再 Ctrl+S 会写坏原谱——那条才是不该动的。
> 保留本节以免下轮有人照着 F1 再改一遍。

## 结论摘要

- 最严重的是**去特效对磁盘的副作用**：模组只拦了 `SaveLevelEditorAction`（Ctrl+S）与两颗按钮，
  但编辑器「另存为」(按钮 / Ctrl+Shift+S) 走的是另一条链，且对话框默认就填着原谱名与目录 ⇒
  **按一次回车即可把原谱覆盖成「去特效版」**（F1）。叠加 F2（关掉开关不还原内存里的谱面）后，
  这是玩家唯一产物的静默损坏。
- 没有无条件触发的 P0（不存在"什么都没做就坏档"的路径），但 F1/F2 都是**一次用户动作即数据永久损坏**，
  按协议口径归 P1 并在下面标出与 P0 的边界。
- 兼容层与游戏 r150 逐条对齐（HitMargin 枚举、IsCounted、Accuracy、XAcc 分子/分母都对得上，
  GameCompat 的 6 处反射在 r150 全部命中）；唯一实质性口径差是 **X-ACC 少了 `0.9875^checkpointsUsed`
  惩罚**（F8，用检查点录的回放会显示偏高）。
- 界面层问题集中在「状态没有清理」与「每帧重复计算」：改键捕获态不清、提示条射线恢复有两份实现会互踩、
  每次 IMGUI 趟都重跑一遍完整命中分析（F4/F6/F7）。
- Settings/ConfigManager 里有约 8 个开关、上百个字段**只持久化、从不被读取**（JudgeBar/JudgeText/
  PlanetColor 等未移植功能），兼容层对应还有 4 个无调用点的语义辅助函数（F12）。

## 发现

### F1 [P1] 去特效开着时「另存为」仍能写盘，对话框默认就是原谱名/原目录 ⇒ 一次回车覆盖原谱
- 位置：
  - `SpectreReplay\Features\EffectRemover\EffectRemover.cs:42`、`:61-70`
  - `SpectreReplay\Features\EffectRemover\Patch_EffectRemover.cs:18-22`
  - 游戏侧：`D:\Projects\adofai-libs\source\Assembly-CSharp\scnEditor.cs:1758`（按钮）、`:1479`（快捷键）、
    `:5901`（对话框预填名）、`:5914-5928`（Save → SaveLevel）、`:5860-5867`（真正写文件）
- 证据：模组只把「禁止存盘」做在**两颗控件**和**一个 Action** 上：

```csharp
// EffectRemover.cs:61-70
private static void SetEditorSaveButtons(scnEditor editor, bool enabled)
{
    if (editor == null) return;
    if (SceneManager.GetActiveScene().name != "scnEditor") return;
    if (editor.popupUnsavedChangesSave != null)
        editor.popupUnsavedChangesSave.interactable = enabled;
    if (editor.buttonSave != null)
        editor.buttonSave.interactable = enabled;
}
```

```csharp
// Patch_EffectRemover.cs:18-22 —— 只拦了 SaveLevelEditorAction
[SpectrePatch(typeof(SaveLevelEditorAction), "Execute", PatchType.Prefix, Feature = FeatureKeys.EffectRemover)]
internal static bool Prefix() => EffectRemover.OnSaveLevelEditorActionPrefix();
```

而游戏里「另存为」是**另一条链**，与 `SaveLevelEditorAction` 无关：

```csharp
// scnEditor.cs:1758
buttonSaveAs.onClick.AddListener(() => { SaveLevelAs(); });
// scnEditor.cs:1479
keybindManager.RegisterKeybind(new EditorKeybind(KeyModifier.Shift | KeyModifier.Control, KeyCode.S), new SaveLevelAsEditorAction());
// ADOFAI.Editor.Actions\SaveLevelAsEditorAction.cs:14-18
public override void Execute(scnEditor editor) { editor.DeselectAnyUIGameObject(); editor.SaveLevelAs(newLevel); }
```

`SaveLevelAs` 的对话框**默认名就是当前谱名、默认目录是上次使用的目录**，选完直接 `SaveLevel()` 写回
目标路径（`customLevel = scnGame.instance`，`ADOBase.levelPath => scnGame.instance.levelPath`，即写的正是
`customLevel.levelPath`）：

```csharp
// scnEditor.cs:5901（预填名）与 5914-5928（写回）
string filename = ((newLevel || string.IsNullOrEmpty(customLevel.levelPath)) ? "level" : Path.GetFileNameWithoutExtension(customLevel.levelPath));
...
string text = SanitizeLevelPath(levelPath); ... customLevel.levelPath = text; ... SaveLevel();
// scnEditor.cs:5860-5867
public void SaveLevel() { ... string data = levelData.Encode(); RDFile.WriteAllText(ADOBase.levelPath, data); ... }
```

- 触发条件：`去特效` 总开关开 → 在编辑器里打开某张谱（LevelData.Decode 已把事件摘掉）→ 点「另存为」
  或按 Ctrl+Shift+S → 对话框预填同名同目录 → 直接回车。
- 影响：**原谱被静默覆盖成已摘除特效的版本**，磁盘上唯一一份美术/事件全丢，无回收站、无备份（`EnableSave`
  的语义本来是「不许存」，这里被绕过）。静默：不报错，游戏还提示「保存成功」。
- 建议：
  1. 把禁令下沉到**唯一写盘点**：额外补 `[SpectrePatch(typeof(scnEditor), "SaveLevel", PatchType.Prefix)]`，
     前缀返回 `EffectRemover.EditorSaveEnabled`（缺 `scnEditor.instance` 时返回 true）。这一处能一次覆盖
     Ctrl+S / Save 按钮 / 另存为 / 未保存弹窗 / `NewLevel` 的全部路径。
  2. 同时把 `buttonSaveAs.interactable` 一起管（`scnEditor.cs:325` 声明、`:1758` 接线），否则按钮看着能点、点了没反应。
  3. 保留现有 `SaveLevelEditorAction` 前缀（它给的是「这次操作被拒绝」的语义，比只禁用按钮更可靠）。
- 置信度：高（模组侧与游戏侧代码都逐行核过；`customLevel == scnGame.instance` 见 `scnEditor.cs:1654`）。

### F2 [P1] 关掉去特效开关不会还原内存里的谱面，之后保存即写坏；SaveBackup 还会自动写一份残缺谱
- 位置：`SpectreReplay\Features\EffectRemover\EffectRemover.cs:72-133`（就地改写 `levelData`）、
  `SpectreReplay\Features\EffectRemover\Patch_EffectRemover.cs:14-15`（Decode 前缀挂点）、
  `SpectreReplay\Pages\Pages.cs:1157-1169`（关开关时恢复保存按钮）、
  游戏侧 `scnEditor.cs:7042-7062`（SaveBackup）
- 证据：`Remove` 直接对内存里的 `levelData` 做破坏性改写，没有任何快照：

```csharp
// EffectRemover.cs:130-132
HashSet<LevelEventType> eventSet = new HashSet<LevelEventType>(events);
if (levelData.levelEvents != null)
    levelData.levelEvents.RemoveAll(data => data != null && eventSet.Contains(data.eventType));
// 同类：levelData.decorations?.Clear()（:177）；backgroundSettings/cameraSettings 被换成 GCS 默认值（:151,168）
```

```csharp
// Pages.cs:1157-1169 —— 关掉开关时只恢复按钮，不重载谱面
OnGui.Row("effects.on", EffectRemoverOn, v =>
{
    EffectRemoverOn = v;
    Commit();
    if (!v) { EffectRemover.RestoreEditorSaveButtons(); }   // ← 保存按钮立刻又可点
    else { EffectRemover.RefreshEditorSaveButtons(); }
});
```

- 触发条件：开去特效 → 进编辑器（Decode 已改掉内存里的 `levelData`）→ 发现特效没了 → 回设置页把总开关**关掉**
  （此时 `EditorSaveEnabled` 变真、按钮恢复可点）→ 回编辑器按 Ctrl+S 或点保存。
- 影响：`levelData` 在整局编辑器会话内**永久是缺事件的**（Decode 只在加载时跑一次），因此存下来的是去特效版，
  即使用户已经把功能关掉。这是「关了还生效」的状态泄漏 + 磁盘损坏。
  附带：编辑器的 `SaveBackup()` 在 `unsavedChanges` 为真时会**自动**写 `backup.adofai`，内容同样是
  `levelData.Encode()`（去特效版）——用户没点任何保存，磁盘上也多出一份残缺谱。
- 建议：
  1. 最短路径：关掉总开关时强制重载当前谱面（走编辑器自身的「重新打开该谱」路径），
     或禁止在 editor 场景会话内关开关（提示「请先退出编辑器」）。
  2. 更稳：不要就地改 `levelData`。Decode 后把原始事件列表快照到旁路表（key = LevelData 实例），关闭时按快照还原；
     同时加 F1 的 `SaveLevel` 前缀，形成双保险。
  3. `SaveBackup` 写的是新文件，可只做「文件名加 `.effectremoved` 后缀」避免冒充真实备份。
- 置信度：高（内存改写与开关路径直读代码；`SaveBackup` 的触发条件见 `scnEditor.cs:7044`）。

### F3 [P2] 配置里的热键键值是「裸整数 → (KeyCode) 强转」，没有 Enum.IsDefined 护栏
- 位置：`SpectreReplay\State\Settings.cs:367-368`、`SpectreReplay\State\ConfigManager.cs:622-623`、
  消费点 `SpectreReplay\Features\Replay\Patches\Patch_Misc.cs:45`、`:66`；对照 `ConfigManager.cs:645-648`
- 证据：

```csharp
// Settings.cs:367-368 —— 直接把 JSON 里的整数当 KeyCode，不校验
SpectreState.save_button = (KeyCode)SaveButton;
SpectreState.cancel_save_button = (KeyCode)CancelSaveButton;

// ConfigManager.cs:622-623 —— 原样读入，也没有区间/合法性判断
Settings.SaveButton = ints.Value<int?>("save_button") ?? Settings.SaveButton;
Settings.CancelSaveButton = ints.Value<int?>("cancel_save_button") ?? Settings.CancelSaveButton;

// Patch_Misc.cs:45 —— 直接喂给 Input
if (!Input.GetKeyDown(save_button)) return;
```

同一份配置里的 `render_stop_key` 走的是另一套（`ConfigManager.cs:645-648` 自述：「合法集合由 Enum.IsDefined
在轮询处把关（见 VideoRenderJob.StopRequestedByHotkey）」）。两个热键字段一个护栏一个没有。
- 触发条件：玩家手改 `Configs.json`（本项目的配置文件本来就是「用户会手工编辑」——`ConfigManager.cs:514-518` 明说），
  或旧版本写入过其他键值域，使 `save_button` 落在 `KeyCode` 定义域外（如 9999、-1）。
- 影响：`Input.GetKeyDown((KeyCode)9999)` 在运行期把非法值喂给 Unity 原生层，行为随 Unity 版本而定：
  多数情况静默返回 false（热键永久失效，且界面把键名显示成数字，玩家看不出是键坏了）；
  极端情况抛 `ArgumentException` 打断 `scrConductor.Update` 的 Postfix。两种都难排查。
- 建议：在 `Settings.check()` 里加护栏，与 `RenderStopKey` 对齐：
  ```csharp
  if (!System.Enum.IsDefined(typeof(KeyCode), SaveButton)) SaveButton = 290;      // F9
  if (!System.Enum.IsDefined(typeof(KeyCode), CancelSaveButton)) CancelSaveButton = 291; // F10
  ```
  顺带把 `RenderStopKey` 的校验也提到 `check()`，让「三个热键同一套规则」在配置入口就成立，而不是分散在消费点。
- 置信度：高（缺护栏是代码事实）；「非法值在 Unity 6 下的确切行为」为中——需要一次运行时实验
  （写 `save_button: 9999` 启动，观察日志/是否抛）才能把影响措辞钉死。

### F4 [P2] 改键等待期间按下的键会**同时**触发旧热键（保存/取消保存）
- 位置：`SpectreReplay\Ui\OnGui.cs:537-552`（捕获扫描）、`SpectreReplay\Features\Replay\Patches\Patch_Misc.cs:42-57`、`:61-84`
- 证据：改键走的是 `Input.GetKeyDown` 全表扫描，而热键消费走的是**另一个补丁里的独立轮询**，两者没有互斥：

```csharp
// OnGui.cs:537-551
foreach (KeyCode k in (KeyCode[])Enum.GetValues(typeof(KeyCode)))
{
    if (k == KeyCode.Mouse0 || k == KeyCode.Mouse1 || k == KeyCode.Mouse2) continue;
    if (!Input.GetKeyDown(k)) continue;
    _capturing = 0;
    set((int)k);
    Settings.CommitSafe();
    return;
}
```

```csharp
// Patch_Misc.cs:45 —— 同一帧、同一颗键，另一条链也在读
if (!Input.GetKeyDown(save_button)) return;
```

页面侧把「保存键」绑成 `k => { SaveButton = k; Commit(); }`（`Pages.cs:108-109`），即**旧值**在下一次 `Settings.check`
之前仍然生效。
- 触发条件：设置页点「保存键」进入等待 → 按下当前已绑定的那颗键（默认 F9）。
- 影响：本帧既完成改键、又让 `Patch_ConductorUpdate` 看到 `Input.GetKeyDown(save_button)`；若此刻 `PendingSave`
  为真且处于 Fail/Won 状态，会**顺带把当前这局存盘**（玩家只是想改个键）。不损坏数据，但与意图相反，且难复现。
- 建议：加一个进程级互斥标志，例如 `SpectreState.HotkeyCaptureActive`，`OnGui` 进入等待时置真、退出时置假，
  `Patch_ConductorUpdate` / `Patch_CancelSaveKey` 开头 `if (HotkeyCaptureActive) return;`。
  同时 `UMM 面板可见` 本身也适合作为抑制条件（面板打开时热键不该改游戏状态）。
- 置信度：中（两条链的触发条件都直读；具体是否同一帧取决于 `scrConductor.Update` 与 OnGUI 的先后——
  Unity 里 Update 先于 OnGUI，因此「先存盘、后改键」或「先改键、后存盘」两种情况都会发生，`Input.GetKeyDown`
  在同一帧内对两处都为真）。

### F5 [P2] 每次都重跑完整命中分析：一帧内多个 IMGUI 趟各分析一遍，含 N 次游戏侧窗口函数 + 4 个 List + 排序
- 位置：`SpectreReplay\Pages\Pages.cs:747-795`（调用点）、`:542-556`（触发分支）、
  `SpectreReplay\Features\Replay\Runtime\ReplayAnalyzer.cs:76-78`、`:101-104`、`:121-133`、`:53-74`
- 证据：`DrawTimingSummary` 在「已装弹、未开播」分支里被**直接调用**，而 IMGUI 每帧会对同一个 OnGUI 跑
  Layout / Repaint（以及 MouseMove 等事件）多趟：

```csharp
// Pages.cs:747-756 —— 没有缓存，画一次就算一次
private static void DrawTimingSummary()
{
    if (data == null) return;
    string reason;
    ReplayAnalyzer.Report rep;
    try { rep = ReplayAnalyzer.Analyze(data, out reason); }   // ← 每一趟都跑
```

```csharp
// ReplayAnalyzer.cs:76-78 —— 每次调用都新建 3 个满容量 List
var samples = new List<float>(data.HitSnapshots.Count);
var perfectWins = new List<float>(data.HitSnapshots.Count);
var pureWins = new List<float>(data.HitSnapshots.Count);
// :101-104 —— 每命中一次都调一次游戏侧窗口函数（非纯算术，含 GCS/CustomJudge 读取）
var win = scrMisc.GetAdjustedAngleBoundaryInDeg(GCS.difficulty, bpmTimesSpeed, pitch, 1.0);
perfectWins.Add((float)(win.Perfect * degToMs));
// :121-133 —— 再 OrderBy（一次排序 + 缓冲区）+ 3 次 LINQ 遍历（Average/Count/Average）
```

另外 `:53-74` 在分析期间**临时改写全局** `GCS.difficulty` 与 `GCS.currentSpeedTrial`（finally 里还原）。
- 触发条件：设置页停在「回放」页、已装弹一份回放且关卡已加载（`PlayMode && data != null`，`Pages.cs:542`）。
  一局 1000~2000 击时，每帧 2~4 趟 ⇒ 每秒数百次全量分析。
- 影响：设置页停在回放页时持续 GC 与 CPU 抖动（正是本项目在别处极力避免的「节奏游戏里每帧小对象分配」）；
  同时每帧两次改写 `GCS.difficulty` 是「临时全局状态」型脆弱代码——任何在 `finally` 之前抛出的异常路径、
  或未来有人在循环里加 `yield`/回调，都会把玩家难度改坏。
- 建议：
  1. 按 `(data 实例, data.HitSnapshots.Count, GCS.difficulty, conductor.bpm, pitch)` 做结果缓存（数据不可变，快照数变了才算脏）。
  2. `GetAdjustedAngleBoundaryInDeg` 的结果只随 `bpmTimesSpeed` 变化，可以按 `floor.speed` 去重后再算。
  3. 把「临时改 GCS」的那段改成纯函数入参（`Analyze(data, difficulty, speedTrial)`），不要动全局。
- 置信度：高（调用点与分配点都直读；每帧多趟是 IMGUI 的固定行为）。

### F6 [P2] HitScatter 缓存判据不足：换关/换难度后分布图沿用旧窗口；`Reset()` 无任何调用点
- 位置：`SpectreReplay\Ui\HitScatter.cs:33-47`（缓存）、`:49-59`（Reset）、调用点 `SpectreReplay\Pages\Pages.cs:808`
- 证据：

```csharp
// HitScatter.cs:39-46 —— 判据只有「同一个 data 对象 + 样本数相同」
if (_tex != null && ReferenceEquals(_builtFrom, source)
    && _builtCount == rep.SamplesMs.Count)
{
    return _tex;
}
Build(rep, source);
```

而 `rep.PerfectWinMs` / `PureWinMs` 是按**当前关卡**的 `floor.speed` 与 `GCS.difficulty` 算出来的
（`ReplayAnalyzer.cs:101-104`）——同一个 `data`、同样的样本数，换一关就会得到完全不同的窗口与配色，
缓存却认为「没变」。另外 `Reset()` 的注释写着「换回放 / 关页面时调用」，但全仓库没有调用点
（`grep HitScatter.Reset` 仅命中定义本身）。
- 触发条件：载入同一份回放 → 在 A 关看分布图 → 退回换到 B 关（不同难度或不同 BPM）→ 分布图的
  柱高/绿黄红分档仍是 A 关的尺子；或卸载回放后不重进页面，静态字段仍钉住上一份 `ReplayData`。
- 影响：图形与「这一击偏差多少 ms 算 Perfect」不一致（误导玩家的练习判断）；`ReplayData` 被静态字段
  长期持有（卸载回放后那份数据不会释放）。不崩溃、无日志。
- 建议：
  1. 缓存键加窗口指纹：`_builtWins = (rep.PerfectWinMs.Count > 0 ? rep.PerfectWinMs[0] : 0f, ...)`，或直接在
     `Report` 上带一个 `ContextVersion`，变了就重建。
  2. 在 `PlayActions.UnloadReplay()` / `PlayActions.SwitchToPlayMode()` 里调 `HitScatter.Reset()`，让注释成真。
- 置信度：高（缓存键与窗口来源都直读；`Reset()` 无调用点已用全仓 grep 确认）。

### F7 [P2] 提示条的「射线屏蔽/恢复」有两份实现，交叉触发时会把恢复值记成 false（永久不再响应点击）
- 位置：`SpectreReplay\Ui\Status.cs:99-160`（`SuppressRaycasts`/`RestoreRoutine`/`RestoreNow`）与
  `SpectreReplay\State\SpectreState.cs:625-643`（`TriggerMessage` + `RestoreGraphics`）
- 证据：两处对同一个 `Notification.instance.bar` 做同一件事，各自「取当前值 → 关 → 延时恢复」，没有共享状态：

```csharp
// Status.cs:106-113
r.Graphics = n.bar.GetComponentsInChildren<Graphic>(true);
r.RaycastTargets = new bool[r.Graphics.Length];
for (int i = 0; i < r.Graphics.Length; i++) { ... r.RaycastTargets[i] = r.Graphics[i].raycastTarget; r.Graphics[i].raycastTarget = false; }

// SpectreState.cs:625-628 —— 同样的动作，另一份实现
var graphics = n.bar.GetComponentsInChildren<Graphic>(true);
var states = graphics.Select(g => g.raycastTarget).ToArray();
foreach (var g in graphics) g.raycastTarget = false;
n.StartCoroutine(RestoreGraphics(graphics, states, dur));
```

- 触发条件：两条提示在恢复窗口内重叠（例如「回放已载入」后 4 秒内又发生一次保存提示——`Status.Say`
  的等待是 `0.5+0.5+seconds+0.3`，`TriggerMessage` 是 `0.5+0.5+dur+0.3`）。后触发者取到的原值是 `false`。
- 影响：通知条上的 Graphic 会被恢复成 `false`（本该恢复成预制体的 `true`），此后通知条**永久不接收点击**。
  现象很小（提示条本来就不该挡点击），但这属于「两份实现互相踩」的隐蔽状态损坏，且随实现继续分叉只会更糟。
- 建议：只保留一份。最省事的做法是让 `Ui.Status.Say` 直接调 `SpectreState.TriggerMessage`（后者已做
  `MainThread` 编组，前者没有——顺带修掉跨线程调用的隐患），或把「射线恢复」抽成带引用计数的
  `NotificationRaycastGuard.Push(seconds)`，两处共用。
- 置信度：中（两处代码事实确凿；「重叠」需要在同一实例上先 `Say` 再 `TriggerMessage`，可在游戏内用
  「载入回放 + 立刻手动保存」构造）。

### F8 [P2] X-ACC 少乘了官方的 `0.9875^checkpointsUsed`：用检查点录的回放会显示得比游戏高
- 位置：`SpectreReplay\Features\Compat\HitMarginCompat.cs:337-350`（分母）、`:356-362`（XAcc）、
  消费点 `SpectreReplay\State\SpectreState.cs:706-718`、`SpectreReplay\Features\Replay\Data\ReplayFileInfo.cs:151-155`、
  `SpectreReplay\Ui\..\Pages\ReplayInfoPanel.cs:245-250`；游戏侧 `scrMarginTracker.cs:209-217`
- 证据：模组只复刻到「分子/分母」这一步：

```csharp
// HitMarginCompat.cs:337-350
internal static double XAccDenominator(int[] counts) { ... return sum + SumSet(counts, FailedFloorFamily); }
// :356-362
internal static double XAcc(int[] counts) { ... return XAccNumerator(counts) / denominator; }
```

而游戏把结果又乘了一个检查点惩罚（`scrMarginTracker.cs:209-217`，非编辑器场景）：

```csharp
double num8 = hitsWithWeights / (double)(playerHitMarginCount + hits);
if ((bool)scnEditor.instance) { percentXAcc = (float)num8; }
else { percentXAcc = (float)(num8 * Math.Pow(0.9875, scrController.checkpointsUsed)); }
```

- 触发条件：回放是在**用过检查点**的一局里录的（`scrController.checkpointsUsed > 0`）。
- 影响：回放详情页的 X-ACC、以及 `SpectreState.CreateFileName`（`:747-751`）写进文件名的百分比，都会比游戏
  当时显示的结算值高；用的检查点越多差得越多（每用一次 ×0.9875）。玩家会认为「回放记错了」。
  注：`hitMargins`/`hitMarginsCount` 里没有任何字段能反推 `checkpointsUsed`，所以**当前格式下无法还原**。
- 建议：录制时把 `scrController.checkpointsUsed` 记进 `ReplayData`（与 `JudgeMode`/`SpeedTrail` 同级），
  显示时按同样公式乘上去；老回放没有该字段时按 0 处理并在详情页标注「未记录检查点使用次数」。
  只改显示不改判定——`IsCounted` / `Accuracy` 两条口径已与游戏完全一致（见「已核查」）。
- 置信度：高（公式与调用点都对照过源码）；影响面为中（只有用过检查点的回放受影响，而练习/分段录制恰好常用检查点）。

### F9 [P3] `HitScatterMath.Band` 的两条注释把 Perfect / Pure 写反了（代码是对的，注释会把人带进沟）
- 位置：`SpectreReplay\Ui\HitScatterMath.cs:8-16`（Band 注释）、`:47-63`（Windows / Classify）
- 证据：

```csharp
// HitScatterMath.cs:10-14
/// <summary>落在较紧的窗口内（通常是 Perfect）。</summary> Best = 0,
/// <summary>落在较宽的窗口内、但超出较紧的那个（通常是 Pure）。</summary> Middle = 1,

// :48-58 —— 同一文件里又说明 Perfect 比 Pure 宽，并取 min/max
/// 两档窗口的紧/宽。**Perfect 恒宽于 Pure**（45° vs 30° 上限）...
tightMs = Math.Min(perfectMs, pureMs);
wideMs = Math.Max(perfectMs, pureMs);
```

既然 Perfect(45°) 恒宽于 Pure(30°)，`tightMs` 就是 **Pure**，`Best` 应当是「在 Pure 窗口内」，`Middle` 才是「在 Perfect 窗口内」。
门禁自己也是这么用的：`tools\UiPreview\HitScatterMathCheck.cs:24` 调 `Windows(45f, 30f)` 后断言 `tight*0.5 → Best`。
- 影响：只影响可维护性，但方向明确——下一个人照注释「修」`Classify`，就会把绿/黄两档整体对调，
  而门禁只断言「三档都可达」，不会红。
- 建议：把两条注释改成「Best = Pure（最内档）/ Middle = Perfect」，并在 `FullScale` 注释里点明「以 Pure 窗口为尺」。
- 置信度：高（同文件内自相矛盾，且与门禁用例、游戏侧 45/30 上限都对得上）。

### F10 [P3] 改键扫描只排掉 Mouse0~2，鼠标侧键仍可被绑成保存键（与注释的「鼠标键不适合」不符）
- 位置：`SpectreReplay\Ui\OnGui.cs:537-543`
- 证据：

```csharp
foreach (KeyCode k in (KeyCode[])Enum.GetValues(typeof(KeyCode)))
{
    // 鼠标键不适合当保存键：分不清左中右，而且点哪都触发
    if (k == KeyCode.Mouse0 || k == KeyCode.Mouse1 || k == KeyCode.Mouse2) continue;
    if (!Input.GetKeyDown(k)) continue;
```

`KeyCode` 里还有 Mouse3~Mouse6（侧键/额外键），它们不在排除列表里。
- 触发条件：改键等待时按鼠标侧键。
- 影响：侧键被绑成「手动保存」。第三方鼠标驱动常把侧键映射成其他功能，玩家按一下侧键就存一局（或更糟：
  在别的程序里误触也一样，因为用的是全局 `Input`）。与注释声明的策略不一致。
- 建议：改成区间判断 `if (k >= KeyCode.Mouse0 && k <= KeyCode.Mouse6) continue;`，并把注释改成「所有鼠标键」；
  同时排除 `KeyCode.None`。
- 置信度：高。

### F11 [P3] 「等待按键时独占 Esc」的注释不成立；且 `_capturing` / `_remapRow` 在关面板时不清，下次打开会「自己等着」
- 位置：`SpectreReplay\Ui\OnGui.cs:466-469`（注释）、`:526-531`（Esc 分支）、`:67-76`（OnHide）、
  `SpectreReplay\Ui\TextField.cs:135-145`（同一项目里的反证）、`SpectreReplay\Pages\Pages.cs:202-204`（`_remapRow`）
- 证据：`RowHotkey` 的注释声称：

```csharp
/// 等待期间 Esc 取消**并独占 Esc**——
/// 否则玩家想绑 Esc，窗口却先被 UMM 关掉，一来一回永远绑不上。
```

但同项目的 `TextField` 已经写明了真相：

```csharp
// TextField.cs:135-140
// Esc = 放弃本次编辑。**这里只能管到 IMGUI 这一层**：
// UMM 关面板走的是它自己的轮询——Param.EscapeHotkey.Up()
// 里是 Input.GetKeyUp(KeyCode.Escape)（UnityModManager.decompiled.cs:3384 与 :7864-7879），
// 直接读硬件状态，Event.Use() 拦不住。
```

`OnGui.cs:526-531` 的 `Event.current.Use()` 同样拦不住，且该分支把 Esc 当「取消」写死，所以
「绑 Esc」本来就不可能。更实际的问题是 `OnHide` 只做 `CommitText()`/`FlushConfig()`，不清 `_capturing`
（`:37`）与 `Pages._remapRow`（`Pages.cs:203`）。
- 触发条件：点「保存键」进入等待 → 按 Esc（或直接点 UMM 关闭）→ 面板关闭 → 再打开设置页并回到该行。
- 影响：该行仍显示「请按键」并继续吃下一次按键（`RowKeyRemap` 的路径更严重：`Pages.cs:213-233` 的
  捕获态会让**下一次任意按键**被写成新的键位，玩家以为只是打开了设置页）。静默改配置。
- 建议：注释改成实际语义（Esc = 取消 + 面板关闭；不支持绑 Esc）；`OnHide` 里补 `_capturing = 0`。
  `Pages` 侧的 `_remapRow` 需要一个 `Pages.ResetCapture()` 由 `OnHide` 调用（它现在是 `private`）。
- 置信度：高（注释与代码、以及同项目另一处注释三方冲突）。

### F12 [P3] 下拉展开后点页面别处不会收起，只能再点自己或换页
- 位置：`SpectreReplay\Ui\OnGui.cs:414-464`（`RowDropdown` 与 `_openRow`）
- 证据：`_openRow` 只在三种情况改变：再点同一颗触发按钮（`:428`）、点另一颗下拉（`:428`）、切换页签（`:225`），
  没有任何「点击落在展开区之外就关闭」的判断：

```csharp
if (GUI.Button(dd, shown, Gui.ButtonStyle)) { _openRow = _openRow == seq ? -1 : seq; }
...
if (_openRow != seq) { return; }
```
- 触发条件：展开「编码器 / 画质 / 格式 / 画面来源」任一下拉，然后去点别的行、拖滑条、点按钮。
- 影响：展开的选项列表一直挂在页面上，把后面的行往下顶；玩家会以为「这一页布局坏了」。不崩溃。
- 建议：在 `RowDropdown` 末尾加一次全局判定：`if (_openRow == seq && Event.current.type == EventType.MouseDown
  && !展开区.Contains(mouse)) _openRow = -1;`（展开区的矩形可在画完后用 `GUILayoutUtility.GetLastRect()` 取）。
- 置信度：高。

### F13 [P3] 兼容层 4 个语义辅助函数零调用点；Settings 里约 8 类开关只持久化、从不被读取（未移植功能以「已实现」的样子留在配置里）
- 位置：`SpectreReplay\Features\Compat\HitMarginCompat.cs:154-156`（`IsComboPerfect`）、`:200`（`IsTimeless`）、
  `:207-208`（`FilterName`）、`:255-256`（`IsJudgeTracked`）；`SpectreReplay\State\Settings.cs:68-128`、
  `:175-230`（JudgeText/JudgeFx/JudgeFilter/JudgeColor/JudgeBar*/PlanetColor*/MemoryOptimizer/EditorBugfix/
  BGM/EditorUI/Timeline/CustomEvents/EnableProEvents）；`SpectreReplay\State\ConfigManager.cs:248-281`、`:568-601`
- 证据：全仓 grep 这四个函数名，只有定义处命中；同样 grep `JudgeBarOn` / `JudgeTextOn` / `PlanetColorOn` /
  `MemoryOptimizerOn` / `EditorBugfixOn` / `BGAModeOn` / `CustomEventsOn` / `EnableProEvents` / `TimelineOn` /
  `BetterUndoRedoOn` / `JudgeBarSensitivity` / `PlanetColorPlayerOn`，命中集合**只含 Settings.cs 与 ConfigManager.cs**
  （以及 `Patch\Shared\FeatureKeys.cs` 里同名的字符串常量）。而设置页只有 5 个页签
  （`Ui\OnGui.cs:48-51`：record / playback / render / effects / about），没有这些功能的任何入口。
  这些字段的注释却按「已实现」写（例如 `HitMarginCompat.cs:149-155` 详细规定了「全 P 连击口径」、
  `:206-208` 规定了「勾选 Perfect = 三档完美飘字全部隐藏」）。
- 影响：
  1. `Configs.json` 里躺着上百个「改了也没用」的键，与 `Configs.json` 是「用户会手工编辑的文件」这个前提
     直接冲突（`ConfigManager.cs:514-518` 明确承认这一点）：用户照着键名去改，什么都不会发生。
  2. 那 4 个辅助函数是**没有任何门禁能验的规格书**——注释描述的语义（例如 FilterName 的归并规则）从未被执行过，
     等真移植时照抄大概率是错的（`FilterName` 的注释本身就有内部矛盾：它说「勾选 XPerfect（独立槽）= 只隐藏 XPerfect」，
     但实现 `IsInnerPerfect(margin) ? "Perfect" : ...` 会把 XPerfect 也归并成 "Perfect"，独立槽永远取不到）。
- 建议：二选一，别留在中间态。
  1. 保留但显式标注：在 `Settings` 对应字段上写「**未移植功能，仅保留上游配置兼容；本模组当前不读取**」，
     并把 `ConfigManager` 里这些键的写入改成「只在文件里已存在时回写」（避免首次启动就凭空生成 60 个死键）。
  2. 或者删掉（配置向后兼容靠 `Value<T>` 的缺省即默认值，删掉不会破坏旧文件）。
  `FilterName` 那处内部矛盾无论如何都要改：要么删函数，要么让独立槽真的生效。
- 置信度：高（调用点缺失已用全仓 grep 确认；`FilterName` 的自相矛盾是直读代码即见）。

### F14 [P3] 改一次热键写两遍盘，且两处写盘语义不同（一处带 `Settings.check` + 补丁刷新，一处不带）
- 位置：`SpectreReplay\Ui\OnGui.cs:548-551`（`set(...)` 后紧跟 `Settings.CommitSafe()`）、
  `SpectreReplay\Pages\Pages.cs:108-111`（`set` 内部已经 `Commit()`）、`SpectreReplay\State\Settings.cs:384-394`（`CommitSafe`）、
  `Pages.cs:1647-1663`（`SaveNow`）
- 证据：

```csharp
// OnGui.cs:548-551
_capturing = 0;
set((int)k);               // Pages 里是 k => { SaveButton = k; Commit(); } → SaveNow()：写盘 + check + Refresh
Settings.CommitSafe();     // 又写一遍盘（只写盘）
```

```csharp
// Pages.cs:1621-1629 —— Commit 在鼠标没按住时立刻走 SaveNow
private static void Commit()
{
    if (Input.GetMouseButton(0)) { _pendingCommit = true; return; }
    SaveNow();
}
```
- 触发条件：改任意一个热键（正常路径，每次改键必走）。
- 影响：配置全量序列化 + 写文件执行两次（文件不大，属浪费而非故障）。更要紧的是**语义分叉**：若 `Commit()`
  因为 `Input.GetMouseButton(0)` 为真而推迟（例如玩家用鼠标点住按钮的同时按了键），`CommitSafe()` 会先把新键值
  写进磁盘，而 `SpectreState.save_button` 仍是旧值（`check()` 要等 `FlushPending`）——此时若游戏崩溃/被杀，
  磁盘上是新键、内存里是旧键，下次启动才一致。
- 建议：`RowHotkey` 不要自己调 `Settings.CommitSafe()`，把落盘完全交给调用方传进来的 `set`（它已经做了）；
  或反过来把 `set` 简化成只赋值、由 `RowHotkey` 统一调 `Pages.Commit()`（带 check + Refresh 的那个）。
- 置信度：高。

### F15 [P2] 回放列表硬性只画最近 40 份，既没有分页也没有提示，超出的文件在界面上完全无法载入/删除
- 位置：`SpectreReplay\Pages\Pages.cs:336-344`
- 证据：

```csharp
int shown = 0;
foreach (string path in files)
{
    // 只列最近 40 个：回放攒到几百个时把整页全画出来，
    // 设置页会长到没法用，而真正要放的多半就是最近那几局。
    if (shown++ >= 40) { break; }
```
- 触发条件：`SavePath` 下可读回放超过 40 份（重度玩家/录视频的用户很快会到）。
- 影响：第 41 份及更早的回放**在设置页里不可见**——不能载入、不能看详情、不能用模组自带的（两段式）删除。
  玩家只能去文件管理器手删，而那正是这套 UI 想替代的操作。没有任何提示告诉玩家「还有更多」。
  典型受害场景：想找回上周那份练习录——它不在列表里，玩家会认为「回放丢了」。
- 建议：至少加一行提示 + 提供翻页/「只显示最近 N 份」下拉；或改成「超过 40 份时按修改时间分组折叠」。
  若门禁里断言了这个 40（`UiLogicShapeCheck` 有大量形状判据），请把断言改成「有上限 + 有提示」。
- 置信度：高（截断是代码事实；「门禁是否只断言存在」不影响该缺陷成立）。

### F16 [P3] `LocalizationManager.GetLanguages` 遇到 14 种之外的语言时不回落英文（保留上一次的语言），与 `Ui.Loc` 的规则不一致
- 位置：`SpectreReplay\State\LocalizationManager.cs:598-602`、对照 `SpectreReplay\Ui\Loc.cs:44-71`
- 证据：

```csharp
// LocalizationManager.cs:598-602 —— 查不到就什么都不做
internal static void GetLanguages()
{
    if (langlist.TryGetValue(RDString.language, out var code))
        currentLanguage = code;
}

// Loc.cs:56-63 —— 同一件事在 UI 层是回落英文
if (CodeByLanguage.TryGetValue(RDString.language, out string code)) { CurrentLanguage = code; }
else { CurrentLanguage = "en"; }
```
- 触发条件：玩家在游戏里把语言切到荷兰语/芬兰语等（不在 14 种内）。进程内 `currentLanguage` 保持**切换前**的值。
- 影响：游戏内提示（`note.*`）会继续用旧语言显示，而设置页文案回落英文——同一个面板里两套语言。
  静态初始化时 `currentLanguage = "en"` 掩盖了这个 bug，只有「先中文再切到荷兰语」才暴露。
- 建议：`else currentLanguage = "en";`（与 `Loc.RefreshLanguage` 完全对称）。
- 置信度：高。

## 已核查但判定无问题（避免下轮重复劳动）

- `SpectreReplay\Ui\LocData.cs`（生成文件，只核结构/键集）：14 种语言 × 325 键，**每语言键数都是 325、并集 325**，
  无任何缺键/多键（脚本逐行统计）；其中 79 个 `.tip` 键。结构与头注释「14 × 325」一致。
- `Ui\Loc.cs:84-104` 回退链 current → en → zh → 键名；`CodeByLanguage` 的 14 个值与 `LocData.Languages` 的 14 个键完全重合，
  所以 `CurrentLanguage` 永远落在表内，正常路径不会露键名。
- 无自建 ScrollView：全仓不存在 `BeginScrollView`/`EndScrollView`，滚动由 UMM 提供；`Gui.cs:663-680` 说明了为什么
  不能用 `Screen.*` 当可视区边界（改用反射取 `GUIClip.visibleRect`）。不存在配对失衡。
- `Ui\TextField.cs:186-221` `GUI.BeginClip/EndClip` 用 `try/finally`，占位文案分支中途 `return` 也能正确 EndClip。
- `Ui\Gui.cs:394-476` 自绘滑条：MouseDown 抢 hotControl / MouseDrag 跟随 / MouseUp 释放；`IsDim` 时整段不消费事件；
  `GetControlID` 的调用顺序由固定行序决定，没有动态增减导致的 ID 漂移。
- `Ui\Gui.cs:104-144` 样式/字体缓存以 `GUI.skin` 与 `GUI.skin.font` 变化为失效条件，换字体后重建，不会串字体。
- `Ui\RoundedTex.cs:71-89` 贴图缓存键含 w/h/radius/颜色/圆角位，调用点只用 64×64、32×32、2×2 三种尺寸，缓存有界
  （不会随窗口尺寸增长）；`HideAndDontSave` 避免切场景被销毁成假 null。
- 滑条拖动期间的落盘推迟闭环完整：`Pages.cs:1621-1629` 推迟 → `OnGui.cs:141-144` 帧末 `FlushPending()` →
  `OnHide`/`OnSave` 走 `FlushPending(true)` 强制落地。
- 两处原生目录选择框都在 `MainThread.Defer` 里执行（`OnGui.cs:803`、`Pages.cs:1569`），不在 IMGUI 事件栈里开模态框。
- `Settings` 默认值与界面区间一致：`RenderFps=60`(15~480)、`RenderTailSeconds=3`(0~15)、`KeybdSoundVolume=10`(0~100，
  消费端 `/10f` 恰好 1.0)、`RenderStopKey=292`(F11) 与 `SaveButton=290`(F9)/`CancelSaveButton=291`(F10) 互不冲突。
- `ConfigManager.MigrateLegacyConfig:389-398` 不会误判新文件：`Sets`/`Ints`/`Strings` 任一存在即直接返回；新格式把全部字段
  收在分区内，不会在顶层出现 `Surface/Fps/Encoder/RecordingOn` 这些识别键。
- `ConfigManager.LoadConfigs:510-806` 五个分区各自 `try`，坏值只废掉本分区；`JsonException`/`IOException`/
  `UnauthorizedAccessException`/兜底 `Exception` 全部吞住并降级默认值，不会让 mod 加载失败。
- `EffectRemover.GetConditionalEventTags/ShouldPreserve:201-240`：条件事件（`SetConditionalEvents`）引用到的装饰物会被保留，
  避免「摘完装饰后条件分支永不触发」；tag 键与游戏侧一致，判定逻辑正确。
- `Ui\Status.cs` 的全部调用点都在 `Pages.cs`（OnGUI 主线程），当前不存在「后台线程弹提示」的实例。
- `HitMarginCompat.IsPerfect(margin, classic)` 与游戏 `IsPerfect(asInClassicExperience)` 同构：classic 只把
  `EarlyPerfect/LatePerfect` 补进完美集合，不影响 `XPerfect/PerfectMinus/PerfectPlus/Auto/Midspin/Multipress`。

## 版本适配观察

**HitMargin 与判定口径（r150 实测对得上，逐条列出依据）**

- `HitMargin.cs` 就是 16 个成员：`TooEarly, VeryEarly, EarlyPerfect, PerfectMinus, XPerfect, PerfectPlus, LatePerfect,
  VeryLate, TooLate, Multipress, FailMiss, FailOverload, Auto, OverPress, Midspin, FailedFloor`——与 `HitMarginCompat.cs:9-17`
  的说明一字不差；索引 0–3（`TooEarly..PerfectMinus`）在两版本同义这一点也成立。
- `HitMarginHelper.IsCounted`（`HitMarginHelper.cs:138-153`）与项目 `HitMarginCompat.IsCounted:164-195` 的 r150 分支
  逐字一致（`None` = classic 完美 ∪ {VeryEarly,VeryLate}；`PerfectsOnly` = classic 完美；`PurePerfectOnly` = 非 classic 完美）。
  且 `scrPlanet.SwitchChosen` 里确实是 `if (hitMargin.IsCounted(GCS.hitMarginLimit) || ...)` 决定是否进账，这条口径是实跑相关的。
- `Accuracy`：`scrMarginTracker.CalculatePercentAcc:189-197` 与 `HitMarginCompat.Accuracy:389-417` 同式——
  `num = PerfectHitMargins + SemiPerfectHitMargins`、`num2 = hitMargins.Count + GetDeaths()`、`+ Perfect×1e-4 − FailedFloor×1e-4`。
  项目用「判定分布数组」还原 `hitMargins.Count` 的前提成立：`AddHitInternal`（`:125-146`）无条件给 `hitMarginsCount[(int)hit] += count`。
- `XAcc`：分子 `GetHitsWithWeights(PlayerHitMarginTypes)`（`scrMarginTracker.cs:174-177,199`）与 `HitMarginCompat.XAccWeights:271-303`
  逐项相同（含 r150 的 `FailedFloor=0.2`、`Auto` 不计权）；分母 `playerHitMarginCount + GetHits(FailedFloor)`（`:209`）与
  `XAccDenominator` 相同。**唯一差异是缺 `Math.Pow(0.9875, scrController.checkpointsUsed)`（见 F8）**。
- `HitMarginLimit` 只有 `None/PerfectsOnly/PurePerfectOnly` 三值（`HitMarginLimit.cs`），项目的 `switch` 覆盖完整，
  「`PurePerfectOnly` 只认完美族、不是时间窗 Pure」这一点项目注释写对了（`HitMarginCompat.cs:158-163`）。

**GameCompat 六处反射在 r150 的命中情况（逐处核对 `D:\Projects\adofai-libs\source\Assembly-CSharp\`）**

| 兼容点 | r150 实际形状 | 命中 |
|---|---|---|
| `scrPlanet.SwitchChosen` | `public scrPlanet SwitchChosen(long? hitTick)`（`scrPlanet.cs:648`） | ✓ arity 1 分支，传 null |
| `scrPlayer.Hit` | `public bool Hit(long? hitTick, bool isAuto = false)`（`scrPlayer.cs:734`） | ✓ arity 2 分支 |
| `scrPlayer.HitInputEvent` | `public bool HitInputEvent(bool isAuto=false, InputEventState state=Down, bool handleAll=true)`（`scrPlayer.cs:637`） | ✓ 3 参、`handleAll:false` |
| `scrHitErrorMeter.AddHit` | `public void AddHit(float angleDiff, HitMargin hitMargin, float marginScale=1f, scrPlanet planet=null, scrFloor hitFloor=null)`（`scrHitErrorMeter.cs:108`） | ✓ 5 参分支 |
| `scrMarginTracker.deadTiles` | **字段不存在**（r150 只有 `RegisterDeadTiles` 方法） | ✓ 回落 `GetHits(FailedFloor)`，正是 r150 该用的口径 |
| `scrController.UpdateHitErrorMeter` | `public void UpdateHitErrorMeter(scrFloor, scrPlayer, scrPlanet)`（`scrController.cs:1909`） | ✓ 存在 ⇒ `HitFiresInputEvents=false` 时由游戏自己喂误差条 |

- 一处**注释行号漂移**：`GameCompat.cs:179` 写「scrPlayer.cs:754 `HitInputEvent(isAuto, InputEventState.Down, handleAll: false)`」，
  实际 754 行是 `ADOBase.controller.UpdateHitErrorMeter(hitFloor, this, scrPlanet2)`；`HitInputEvent` 定义在 637。
  行为不受影响（传参顺序本来就是对的），但核对者会被带到错误行——建议改成 `:637` 并补一句「调用点在 `Hit` 内」。
- `Notification.SetupNotification(float delay = 3f, float scale = 1f, bool reset = true)`（`Notification.cs:45`）：
  `Status.cs:75-79` 的 3 参反射签名正确。

**r141–148 分支无法在本仓库验证**：本仓库只引用 r150 的 Assembly-CSharp，`HitMarginCompat.IsCounted` 的旧版分支依赖的
`scrMisc.IsValidHit` 在 150 源码里已不存在。该分支只能靠上游 Spectre 的经验与注释，属于「未经事实基线验证的代码」
（`HitMarginCompat.cs:157-163` 已声明来源）。建议在能拿到 148 程序集时补一次对照，否则把该分支的注释降级为「推测」。

**Unity 6 相关**：`Gui.Viewport()` 通过反射取 `UnityEngine.GUIClip.visibleRect`（`Gui.cs:685-723`），取不到时退回
`Screen.width/height`。在 UMM 的 `GUI.Window` 坐标系里退回分支会让提示框越界（代码注释已承认这是「难看但不崩」的降级）；
Unity 6 下该属性仍是 internal static，反射路径可用。