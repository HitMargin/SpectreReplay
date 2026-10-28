# AGENT.md — 给在本仓库工作的编码代理

> 目标读者：第一次进入本仓库的 AI 编码代理（以及第一天上班的人类）。
> 读完这份就该能改代码、能判断自己有没有真的改对。深挖时看结尾的文档索引。
> 约定：本文里的路径统一用 `/`，在 PowerShell 里两种分隔符等价。

**English tl;dr.** ADOFAI replay / record / render mod for UnityModManager (`netstandard2.1`, game `r150`, Unity `6000.3.25f1`). Build: `dotnet build SpectreReplay.slnx -c Release`. Gate: `tools/verify.ps1` — never trust a green you did not run, and 0 assertions means no verdict. Every new assertion needs a mutant that turns it red (`tools/mutate.ps1 -Which MUT-X`; exit code `2` means the mutant never applied). Localization is generated: edit `tools/i18n/source.json`, run `tools/i18n/gen-locdata.ps1`, never hand-edit `Ui/LocData.cs`. Replay files are encrypted and versioned: add, do not change; old files must keep loading.

## 0. 一句话和三条铁律

**这是什么。** 《A Dance of Fire and Ice》(ADOFAI) 的 UnityModManager 模组 `com.spectre.replay`：正常游玩时自动录制本局按键与判定，之后在游戏内回放，或逐帧渲染成视频。回放与出片都由**游戏自己**执行，所以画面和当初那一局一致，而不是「差不多」。

**三条铁律（违反任意一条，改动就是错的）：**

1. **不许有静默跳过。「没挂上」必须能在日志或设置页看见。**
2. **新断言必须配一条会因它变红的变异。** 没人证明过会红的断言等于没有断言。
3. **绿了不等于验过。** 只有「闸门真跑了 + 断言数非 0 + 工作区干净」时才相信绿。

## 1. 硬约束

| 项 | 值 | 原因 |
|---|---|---|
| 目标框架 | `netstandard2.1`，`LangVersion preview`，`Deterministic` | 运行时是 Unity 的 Mono；语言特性可以用新的，API 不行 |
| 游戏基线 | ADOFAI `3.4.0`（build r150），Unity `6000.3.25f1`，UMM `0.33` | `info.json` 的 `GameVersion` |
| 构建工具 | .NET SDK 10（`SpectreReplay.slnx` 只有新版 SDK 能开） | 仓库用 slnx |
| 游戏依赖 | `D:/Projects/adofai-libs`（放在**仓库的上一层**，`..\..\adofai-libs`） | csproj 的 `HintPath` 全是相对路径；可用 `ADOFAI_LIBS` 覆盖 |
| 上游参照 | `D:/Projects/Spectre` | 闸门会核对它没被污染；移植比对要用 |
| 引用 | **全部 `Private=false`** | 输出目录只允许 `SpectreReplay.dll(+pdb)`；多一个 dll 会污染游戏 |
| 产物 | `SpectreReplay.dll` + `info.json` | UMM 用 `info.json` 的 `EntryMethod` 找入口 |

**运行形态**：不是独立程序，是热补丁进游戏进程的库。`Main.Load` 只做最轻的事（配置/文案/界面回调），重活全在 `OnToggle`（`Main.cs:14`、`:61-150`）。

## 2. 目录地图

```
SpectreReplay/
  Main.cs                 入口：Load 注册、OnToggle 装配/拆卸（顺序见 §8.6）
  info.json               模组元数据：Id / EntryMethod / GameVersion / Version
  SpectreReplay.csproj    netstandard2.1 + 全部 Private=false 引用
  Patch/Shared/           补丁基础设施（可被别的模组复用）
    SpectrePatch.cs       [SpectrePatch] attribute、形状枚举、优先级、版本窗口
    SpectrePatchHub.cs    声明式补丁宿主：注册/求值/挂载/卸载/形状校验/失败汇总
    PatchManager.cs       Harmony 生命周期 + 手工补丁注册 + 按开关批量应用/刷新
    FeatureKeys.cs        功能键常量（attribute 与 SetFeatureToggle 的唯一同源）
    GameVersion.cs        读 releaseNumber 的探针
    AsyncLog.cs           跨线程日志队列（卸载时停泵并清空）
  Features/
    Replay/Codec/         容器：加密信封、压缩封装、编解码、键音编码
    Replay/Data/          回放数据模型与格式常量（ReplayConstants）
    Replay/Patches/       录制/回放/生命周期/输入注入/特殊键 的补丁声明
    Replay/Runtime/       录制器、回放器、按键注入、托管输入层、异步输入
    VideoRender/          Pipeline（逐帧渲染/编解码/mux）+ Patches（时钟钉扎、相机）
    Cloud/               Steam Cloud 同步（SteamCloud P/Invoke + CloudReplay 业务）
    Compat/              与其他模组/游戏版本的兼容层（GameCompat、HitMarginCompat）
    EffectRemover/       去特效：解码期在内存里摘事件 + 编辑器保存拦截
  State/                  Settings（配置字段）、SpectreState（全局状态）、ConfigManager、
                          LocalizationManager、MainThread（主线程调度）、AudioRecorder、WavLoader
  Pages/                 五个页面的绘制逻辑（录制/回放/渲染/去特效/关于）
  Ui/                    OnGui（UMM 面板宿主）、Gui（手绘控件库）、Loc/LocData（文案）、TextField、Status（游戏内提示条）

tools/                    验证与工具链（对产品的改动一律先过这里）
  verify.ps1              总闸门（§4.1）
  _gameroot.ps1           定位游戏根目录 / 模组目录 / ffmpeg
  deploy.ps1              构建并部署到 UMM 模组目录
  mutate.ps1              单条变异（§4.2）
  mutate-regression.ps1   全量变异回归
  *-audit.ps1             只读审计（补丁形状/签名/实例注入/结果注入/死代码/移植比对）
  ffmpeg-probe.ps1        探 ffmpeg 参数与编码器（需游戏自带 ffmpeg）
  convert-probe.ps1       探 键音/音频 转换路径
  UiPreview/              离线验证套件（net10.0-windows，不链接 Features/）
  i18n/                   文案唯一事实源 + 生成器（生成 Ui/LocData.cs）

docs/  README*.md  verify.md  audit/  INTRO_*.md  SPECTRE_COMPARISON.md
```

## 3. 环境准备

```
D:/
  Projects/
    adofai-libs/        mscorlib.dll System*.dll UnityEngine.dll Assembly-CSharp*.dll
                       0Harmony.dll DOTween.dll Newtonsoft.Json.dll System.IO.Compression.dll …
                       还有 source/Assembly-CSharp/（反编译源码，查游戏行为用）
    Spectre/            上游参照（移植比对 + 闸门核对干净）
    SpectreReplay/      本仓库
```

- 可用环境变量覆盖：`ADOFAI_LIBS`、`SPECTRE_UPSTREAM`、`SRM_MODS_ROOT`（模组目录）、`ADOFAI_GAME`（游戏根目录）。
- 游戏本体**不需要**参与 CI 构建；但 `deploy` / `ffmpeg-probe` / 部署一致性检查需要本机装了游戏。
- 本机现实：`dotnet 10.0.4xx` 可用；`python` 可能没有 → 用 `.ps1` 生成器；`ffmpeg` 不在 PATH（用游戏自带那份）。

## 4. 命令速查

### 4.1 闸门

```powershell
dotnet build SpectreReplay.slnx -c Release          # 只编主项目
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify.ps1 -Ci    # CI 形态
```

| 开关 | 作用 |
|---|---|
| `-SkipPreview` | 跳过 `tools/UiPreview` 离线套件 |
| `-SkipDeploy` | **只跳过复制那一步**；sha256 一致性核对在守卫之外，照样会红 |
| `-SkipAudits` | 跳过「只读审计」一节（只读 `SpectreReplay/`、`tools/`、`docs/`） |
| `-Ci` | 跳过两项绑本机的检查：Steam 部署一致性、上游工作区干净 |
| `-SkipBuild` | **死开关**（只出现在参数声明，没有实际使用） |

顺序：脚本编码自检 → 主项目构建 → 离线套件 → 只读审计 → 部署一致性 → 上游工作区干净。**退出码 = 失败项数**。`verify-last.log` 每次重写（已 gitignore）。

### 4.2 变异

```powershell
tools/mutate.ps1 -Which MUT-X9              # 单条：期望退出码 0
tools/mutate.ps1 -Which MUT-X9 -ExpectGreen # 反向对照：这条本来就该绿
tools/mutate.ps1 -Which MUT-C4 -ExpectReview # 迁移类：本来就没有断言
tools/mutate-regression.ps1                 # 全量回归（自己打印 通过 N / M）
tools/mutate-regression.ps1 -Filter Replay  # 只跑名字含关键字的
```

**退出码不是 0/1 两种：**

| 码 | 含义 | 怎么办 |
|---|---|---|
| `0` | 变异被闸门抓到（正常） | 无事 |
| `1` | 断言是假的：变异打进去了，闸门还是绿 | 断言没咬住这条 bug，去补断言 |
| `2` | 变异**没能施加或没能判定**（锚点命中 ≠1、编译不过、断言数为 0、审计环境缺失） | 修变异本身，别当成「已验证」 |

### 4.3 其它

```powershell
tools/deploy.ps1 -Config Release      # 构建 + 部署（游戏在跑会锁 dll，先关游戏）
tools/ffmpeg-probe.ps1                # 探参数（需游戏 ffmpeg）
tools/convert-probe.ps1               # 探音频转换
tools/patch-sig-audit.ps1             # 目标方法签名
powershell -File tools/i18n/gen-locdata.ps1   # 从 source.json 重新生成 LocData.cs
```

审计脚本都可以单独跑；闸门里跑的是同一批。

## 5. 闸门语义：什么时候能相信「全绿」

**恒等式：红 = 有问题；但绿 ≠ 没问题。** 真绿要五件事同时成立：

1. **闸门真的跑了**，不是「上次跑过」。用了跳过开关就要能说清跳了什么、为什么。
2. **断言数不是 0。** 0 条断言的红绿是「没有结论」。
3. **`✓` 行数 ≠ 判据数。** 一条检查可以打很多 ✓；覆盖面看变异，不看 ✓ 数量。
4. **工作区干净。** 有未提交改动时，跨上游的对比（`port-diff-audit`）只能说明「两边不一样」，**不能**归因到某个修复或回归。要下因果结论先提交或 stash。
5. **改了产品代码，就得有一条会因此变红的断言**；只改文档另说。

### 5.1 五种「假绿」（都真实发生过）

| 假绿 | 长什么样 | 怎么破 |
|---|---|---|
| 只查存在不查语义 | 文案表有键、签名能解析，值却是错的 | 比对具体值/形状，别只 `Contains` |
| 变异没施加 | 脚本报「断言是假的」，其实锚点没命中 | 看退出码 2；确认锚点唯一 |
| 把注释里的例子算进去 | grep 到注释中的 `[SpectrePatch]` 示例 | 用仓库自带注释剥离器 |
| 只验一侧 | 预览器绿 ≠ 主项目编得过（编的不是同一套源码） | 两边都跑 |
| 环境缺失静默返回 0 | `PatchTargetCheck` 拿不到游戏程序集就直接 0 | 看它有没有打印「跳过」 |

### 5.2 断言纪律

- **新增检查必须同时加一条变异**证明它能红。
- **变异要退回真实的 bug 形状**，不能只做 `true→false` 这种必然红的偷懒改法。
- **报告数字要说明来源。** 文档里的数字普遍滞后：`docs/verify.md` 写「111 条断言 / 153 条变异」、`tools/i18n/README.md` 写「325 键」、`docs/audit/SUMMARY.md` 写「155 个变异标签」。2026-10-28 04:30 实测是 **153 条 `✓`、417 键、160 个变异标签**。引用前先跑，或直接写「以实跑为准」。

## 6. 架构

### 6.1 补丁层：声明式，开关为假就不挂

补丁写在 attribute 上，不写在方法体里：

```csharp
[SpectrePatch(typeof(scrPlayer), "Hit", PatchType.Prefix, Feature = FeatureKeys.Replay, MinVersion = 150)]
private static bool Hit_Prefix(...) { ... }
```

- **形状**：`Prefix`/`Postfix`/`Transpiler`/`Finalizer`。Prefix 返回 `bool` 可跳过原方法（`false` = 跳过）。
- **`Feature`**：功能分组键，取值只用 `FeatureKeys` 常量。宿主在 `Main.cs:109-133` 用 `SetFeatureToggle(键, () => 条件)` 注册求值器——**条件为假时补丁根本不挂载**，不是「挂上再判」。
- **`MinVersion`/`MaxVersion`**：游戏 build 窗口。r141 是全局地板（`Main.cs:100-101` 的 `Configure(…,141,150)`）；超过 `MaxVersion` 标记 `VersionSkipped`（不算失败）。**只有 r150 改了签名的目标才需要分叉**：全仓仅两处——`Patch_Recording.cs:26/31`、`Patch_SpecialKeys.cs:171/184`。
- **`PatchPriority`**：同一目标上不同形状/多个 Prefix 的先后，别靠声明顺序碰运气。
- **`__instance`**：目标类型必须与 `Clazz` 一致才能注入实例；`instance-injection-audit.ps1` 专查。
- **手工补丁**：`PatchManager.RegisterManualPrefix` / `RegisterManualPatch`，卸载时 `PatchManager.UnpatchManualPrefix` / `UnpatchManualPostfix` 并**自己归零静态标志**。`KeyViewerBridge` 就是这种：`UnpatchAll` 卸补丁，但它的 `_installed` 是普通静态字段，不会跟着翻假。
- **未注册的开关会报警**：attribute 用了 `Feature` 却没 `SetFeatureToggle`，那组补丁永远不挂。改完一定看启动日志的「挂上 x/y」与 `ui.patchfailed (N)`（`Main.cs:144`、`Ui/OnGui.cs:196-205`）。
- **`Feature` 里不许混用分隔符**：`A|B`（或）与 `A&B`（与）是两种语义，混写成 `A|B&C` 会被判为无效并**不挂载**（`SpectrePatchHub.cs:837-851`）。
- **别在补丁体里重复判一次总开关**：attribute 的 `Feature` 已经门控了挂载，再判一次只会让「没挂」和「挂了但什么都没做」混淆。

### 6.2 状态与配置

- 配置在**模组目录**的 `Configs.json`（`Main.cs:26`）；云端同名（`CloudReplay.cs:38-44`）。
- 格式：**分区**的五个顶层对象 `Sets` / `Ints` / `Strings` / `StringLists` / `Colors`，键名 PascalCase（如 `Sets.ReplayOn`）；旧版扁平键（`Surface`/`Fps`/`Encoder`/`RecordingOn`/`OutputDir`…）由 `MigrateLegacyConfig` 先迁进这五个区。每个区**独立 try**：手写坏值只毁自己那一区，不会把整份配置重置成默认（`ConfigManager.cs:536-560`）。
- **读配置是增量、保默认**：每次读都是「取到就用，取不到用当前值」。所以新字段在旧文件里缺键 = 走默认；**改名必须显式读旧键**，否则老用户的值静默丢失。
- 读写失败只记日志，**不能**把整个模组加载带下去（`Main.Load` 只护自己的方法体）。
- 落盘入口只有三个：`Settings.CommitSafe()`（立即写）、`Pages.Commit()`（拖动中延后，松手写）、`OnGui.FlushConfig()`（面板要消失/关模组/退出游戏时强制写）。**不要**在滑块的 OnGUI 回调里直接存盘。
- `Settings` 是静态字段集合，设置页直接读写。加字段**必须向后兼容**（旧文件缺键走默认，不要清空）。
- 默认值不是装饰：`SavePath` 留空 → 回放存模组目录；`RenderOutputDir` 留空 → `SavePath/RenderVideos`。
- 后台线程碰 Unity 只走 `MainThread.Run`（泵是隐藏常驻 MonoBehaviour，卸载时 `Uninstall`）。跨线程字段该 `volatile` 的别只加一半（`CrossThreadFieldCheck` 在盯）。

### 6.3 回放：录制 → 容器 → 回放 → 注入

**录制**

- 启停只有一条收口路径：`SpectreState.PlayActions`（`SpectreState.cs:262-310`）。`Start*/Stop*` 内部各有守卫、重复调用无害，对外事件只在标志真正翻转那一拍发出；入场/重开/退出/转场分别由 `Patch_Lifecycle.cs` 的 `Start_Rewind`、`Restart`、`QuitToMainMenu`、`WipeToBlack` 钩子打到这条收口。
- 时间戳用 `conductor.songposition_minusi`；**回放侧刻意不用它**（那时是旧值）。这个不对称是有意的，别去「统一」。
- 鼠标轨迹按 `Screen.width/height` 归一化，位移 < 1e-4 丢弃；倒计时的负时间原样记录。
- 模组热键不录，但 `PrevKeyState` 要同步——否则松开会被记成孤立的 UP。

**容器（改这里最危险）**

- 三层：加密信封（`SRC2`，PBKDF2 → AES-256-CBC + HMAC-SHA256，**先验 MAC 再解密**）→ 压缩封装（`0` 裸 / `1` gzip / `2` Brotli，带长度；`0x1F` 是历史遗留的裸 gzip）→ 段流。
- 段序固定：`metadata → EndTime(v≥2) → keys → hits → cursor → KbdAudio → KbdPrefs`。后两段是**尾部可选段**（键音波形与其覆盖项）：老代码读完 `cursor` 就返回，天然无视，所以 `FormatVersion` 不用动。版本常量集中在 `Data/ReplayConstants.cs`（`Magic`、`FormatVersion = 2`、`MetadataVersion = 3`、各段版本），**不要散落魔数**。
- 每条读路径都要有界：`ReadCount` + 4096 预分配上限、命中记录最小长度（41 或 42 字节，随段版本）、512MB 解压上限、尾部段版本不认识时**回退而不是猜**。
- 只读本模组格式；Spectre 或别的模组录的文件读不了，这是设计。

**回放与注入**

- 主路径是**托管输入层**：`ReplayPlayer.PumpInput` 把到时的键写进 `ManagedInput`/键状态，游戏以为玩家在按；`ManagedInput` 回答 `RDInput` 查询（`Patch_InputInjection.cs:34-64`）。
- **OS 级 `SendInput` 是永久关闭的镜像**：`ManagedInput.DisableSendInput = true` 让 `Mirror` 恒假，所有消费者因此空转。别「顺手打开」——它与异步广播互斥，同开会把同一颗键捕两次算两遍（`ReplayPlayer.cs:368-370`）。
- **泵必须在游戏读输入之前**：挂在 `PlayerControl_Update`/`UpdateInput` 的 **Prefix**（`Patch_InputInjection.cs:16-30`）。挂 Postfix 就晚一帧。
- **单帧上限**：事件 64 条、命中 256 条；超出留到下一帧，**没有追赶/重对齐**。删掉上限 = 卡帧雪崩。
- **角度闸**：非有限角度时这一帧一条命中都不消费（`Patch_Playback.cs:29-31`）。
- **`holdKeys` 只有一份真相**：每帧清空重建，来自 `RDInput.GetMainHeldKeys` 或 `ManagedInput.Append`。`Append` 必须构造 `AsyncKeyCode`（`ManagedInput.cs:211-220`）：塞 `KeyCode` 会被按引用比较判成「已松开」，长按块直接死。
- **`ManagedInput.Active` 只等于 `is_playing`**（`ManagedInput.cs:44`），不许掺 `isFocused` 或键鼠 DLL 可用性。
- 收场必须干净：`Stop()` 还原难度/`noFail`/`hitMarginLimit`、松开残留键、清托管状态、停键音。收场三处（Start/Stop/FastForward）缺一不可，Stop 里「先快照、再翻标志、最后发松开」的顺序是有意的（`ReplayPlayer.cs:53-67`）。

### 6.4 渲染与音频

- 逐帧推进：每个捕获帧产出一个输出帧；不用墙钟。
- **临时目录按作业隔离**：`RenderTemp/job_<时间戳>`，12 小时清理、5 秒 Join、40 分钟延迟收尾。共用固定目录 = 两个作业互删文件。
- **`Output` 非空不代表完成**；终态显式，`ReportEnd` 是唯一写终态的地方（取消 4 条路径 + 失败 + 超时都汇到它）。
- **音频铁律**：喂给 `AudioRenderer` 的样本数必须**恰好等于**该帧采样数，多一个少一个都会让捕获静默停住。
- 键音 WAV：44 字节头 + PCM；**哈希只覆盖 PCM、跳过 44 字节头**（`WavLoader` 与 `KeybdAudioCodec` 共用这个契约，改一边等于作废两边缓存）。
- **回放内键音覆盖**：`KeybdAudioCodec` 按回放里的按键实时合成键音；音量/偏移/静音三项可**按回放覆盖**（`Data/KeybdPrefs.cs`，落在 `KbdPrefs` 段）。每项各带 `Has*` 标志——**用标志位而不是哨兵值**表示「没覆盖」（`KeybdPrefs.cs:7` 的注释写了理由）；偏移是**替换**全局 `MicrophoneOffset`，不是相加；静音独立于音量值（静音时不看音量）。缺段 = 三项全跟随全局设置。回放里没有 `KbdAudio` 段时退回「同目录同名 `.wav`」的老路径。导出 wav 导的是**叠加覆盖值之后**的结果，不是原始录音。
- **停源，不然上一份键盘音会一直播**：换回放/销毁 clip 时必须停掉 `musicSource`（`Patch_Playback.cs` 的 `WavLoader.musicSource` 登记项），否则旧音频继续出声，导出端 `AudioListener` 会把它原样混进成片——「导出有声、回放没声」这一类怪象多半出在这里。
- ffmpeg：`mp4/mkv/mov` = H.264 + AAC；`webm` = VP9 + Opus 48k。NVENC 先探再用，失败回退 CPU。`-y` 截断残留要删/救；stdin 泵线程与 stderr 排空都不能省。
- 光标叠加默认关（opt-in）；预览相机只影响预览。
- 时钟钉扎有四套机制（输出钉、变速钉、`RenderClock` 转译默认关、非缩放 delta detour），各自防不同漂移，**别合并**。

### 6.5 界面

- 全部**手绘 IMGUI**：`Ui/Gui.cs` 提供 `Fill/Text/Card/Slider/Toggle/TextField/RowHotkey`，页面上没有 Unity 原生控件。
- 宿主是 UMM 面板（`Ui/OnGui.cs`）：`_visible` 由 `OnShow/OnHide` 控制；滚动位置由 UMM 维护，**不自己建 ScrollView**。
- 五个页签固定顺序：录制/回放/视频渲染/去特效/关于；页签宽度固定，避免长短页名让整排跳动。
- **帧状态放静态字段**并在每趟绘制开头归零（`_openRow`/`_openSeq`/`_capturing` 这类）；IMGUI 一帧可能画多趟，放局部变量会让下拉和改键错乱。
- **原生对话框延后一拍**（`_pickOutDirPending` 模式）；在 GUI 事件里直接弹文件框会弄坏这一帧。
- **文本框在关闭面板时提交**（`OnHide` → `FlushConfig`）：玩家拖完滑块直接关窗是常态。
- tooltip 留到整页画完再一次性画（`Gui.cs:767+`）；不要复用 UMM 内部 tooltip 字段。
- 提示统一走 `Ui/Status.Say(key, detail, Level.Info|Warning, extra)`，底层是**游戏自带的 `Notification`**：时长 = 玩家设定（`Settings.NotificationSeconds`，默认 3 秒）+ `extra`，`Status.Warn(...)` 固定 `extra = 2`。别自建一套「设置页顶栏提示」——玩家不进设置页就看不见（注释里记着那次返工）。
- 提示时长落盘为 `notification_seconds`，载入时 `Mathf.Clamp` 到 1–10 秒：手写配置给了 `0` 等于「弹了但看不见」，给 `999` 等于永不消失。
- 文案一律 `Loc.T`/`Loc.F`，界面里不许有字面量（`HardTextCheck` 会红）。
- **控件宽度不许写死**：用 `Gui.CtrlW`（`Ui/Gui.cs:102`）。写死宽度在别的分辨率/缩放上会错位。
- **不要用 `GUI.backgroundColor` 给卡片上色**：它是乘算，会二次压暗；用专用样式（`Ui/OnGui.cs:491`）。
- **不要用内建 `GUILayout.Toggle` / `HorizontalSlider` / `GUI.TextField` 画模组行**：它们不认 UMM 皮肤，用 `Gui.Switch` / `Gui.Slider` / `TextField`。
- 原生目录选择必须延后一拍（`MainThread.Defer`），且**取消不能被当成「清空该设置」**——空结果 = 不改（`Ui/OnGui.cs:803`、`:811-830`）。

### 6.6 文案（i18n）管线

```
tools/i18n/source.json     唯一事实源：[{key, zh, en}, …]，顺序即生成顺序（今天 417 键）
tools/i18n/locales/*.json  12 个语言译文（zh-t ja ko es pt fr de pl cs ro ru vi）
tools/i18n/gen-locdata.ps1 生成 SpectreReplay/Ui/LocData.cs（.py 等价副本）
```

**`Ui/LocData.cs` 是生成物，永远不要手改。** 改文案 = 改源 → 跑生成器 → 让 `LocCheck` 变绿。`Loc.cs` 只剩选择逻辑，不含任何键值。

### 6.7 云端 / 兼容 / 去特效

- **Steam Cloud 同步**（`Features/Cloud/`）：把回放文件与 `Configs.json` 直接放进 Steam 云目录（`steam_api` P/Invoke，UTF-8 文件名），不自己发明同步通道。
- **默认永不覆盖**：批量上传与回填都是「只补缺失、云端先到者为准」，模组不做强制覆盖同步——本地那份才是玩家正在用的，静默覆盖会吃掉进度（`CloudReplay.cs:734`、`:799-801`、`:834`）。唯一例外是玩家手动「从云端取回配置」，那条路会**先给本地留 `.bak`** 再覆盖（`:1031-1033`）。
- **配置上传有落定门**：云端配置还没读完就上传，会把云端备份覆盖成近默认值；所以上传被 5 秒节流 + 「恢复完成后才允许上传」的门挡住（`:928-942`）。
- **本地/云端两套列表同口径**：`Pages.cs:543 FilterReplays` 与 `:568 FilterCloudReplays` 是两份实现、一条口径（注释明写「一模一样」）。改筛选/命名/排序要两边一起改，否则云端会先分叉。
- **云端列表只读字节**：点文件名只就地展开详情，读云端字节、**不装载也不落盘**——看一眼不会顶掉正在放的那一局；播放只走显式按钮。搜索框与本地共用一份实现、两个独立实例，筛选词互不影响。
- **兼容层**（`Features/Compat/`）：`GameCompat` / `HitMarginCompat` 抹平各 build 的命中时机与区间表示差异；补丁目标按版本分叉（见 §6.1）。别把 r150 的形状当成唯一真相。
- **去特效**（`Features/EffectRemover/`）：在**解码期**改内存里的关卡数据（一次性摘事件，不动磁盘文件）；保存拦截只挂 `SaveLevelEditorAction`（Ctrl+S → `SaveLevel`），`SaveLevelAs` **故意不补**——「另存为」仍写带特效的原文件。别把这个当 bug 修，先读注释。

## 7. 变更配方（照着做，别自创流程）

### 7.1 新增/修改一个设置项

1. `State/Settings.cs` 加字段 + 默认值。旧 `Configs.json` 缺键必须能走默认，**不要**顺手清空文件。
2. `Pages/*.cs` 画控件：只用 `Gui.*` 手绘，行结构照邻近实现抄。
3. 若它影响补丁挂载，把条件加到 `Main.cs` 的 `SetFeatureToggle` 里，**不要**写进补丁方法体。
4. 文案：`tools/i18n/source.json` 加键 → 跑生成器 → 12 份译文补齐（缺一条 `LocCheck` 红）。
5. 若会影响外部资源（键鼠钩子、麦克风、云端），关掉时要主动收尾，不要等下次调用。
6. 闸门 + 一条变异：把新判据退回旧行为，确认会红。

### 7.2 新增一个补丁/目标

- 先查签名：`tools/patch-sig-audit.ps1`，以及 `adofai-libs/source/Assembly-CSharp` 里的真实实现。
- attribute 放在 `Features/*/Patches/`；`Clazz`/`Method` 必须与游戏里的真实名字一致，形状与被补语义匹配。
- `Feature` 只用 `FeatureKeys` 常量；新功能键要**同时**加常量并在 `Main.cs:109-133` 注册，否则补丁永远不挂且只在日志里留一行警告。
- 版本差异：只有签名真的变了才用 `MinVersion`/`MaxVersion` 分叉，并在注释里写清两个形状的差别。
- 需要实例时，`__instance` 的类型必须等于 `Clazz`。
- 手工挂载用 `PatchManager.RegisterManualPrefix/RegisterManualPatch`，并在卸载路径里把这类静态标志归零。
- 验证顺序：启动日志「挂上 x/y」→ `ui.patchfailed` 为 0 → 实机 poke 一次。
- **注意 `SpectrePatch.cs:73-76` 的示例已经过期**：那里写 `MaxVersion = GameVersion.VerifiedBuild`，但 `GameVersion` 没有这个成员（实际是 `SpectrePatchHub.VerifiedBuild`，且是 internal）；全仓也没有任何补丁这样用。

### 7.3 改文案 / 加语言键

改 `source.json` → 跑 `tools/i18n/gen-locdata.ps1` → 补齐 12 份 `locales/*.json` → `LocCheck`（键完整、占位符形状一致）与 `HardTextCheck`（界面无字面量）都要绿。`LocData.cs` 一个字节都别手改。

### 7.4 改回放容器

- **只加不改**：老文件必须还能读。加字段就升 `MetadataVersion` 或对应段版本，读侧按版本分支。
- 新尾部段：版本不认识时**回退游标**，不许猜长度。
- 任何 `ReadCount`/长度都要过上限（4096 预分配、512MB 解压）。
- `ReplayCheck` 直接编真实 Encoder/Decoder 做往返；再加一条负例（垃圾 count 必须被拒）。
- 加密前的历史格式不打算读，就别顺手加兼容——半吊子兼容比明确不支持更糟。
- **首选形态是尾部可选段**：本次键音（`KbdAudio`）与覆盖（`KbdPrefs`）就是照这个模子加的——追加在最后、老构建读完 `cursor` 自然无视、`FormatVersion` 不动；段内「有没有某项」用标志位，三态值用 `NaN` 而不是 `0`（见 §8.2）。
- 破坏性变更要同时改 README 与 `info.json` 的版本说明。

### 7.5 加一条离线检查

- 放 `tools/UiPreview/*Check.cs`，命名与既有 `*Check` 一致；用该套件的断言计数机制，别只 `Console.WriteLine`。
- 在 `Driver.cs:37-99` 注册；漏注册会被 `NoOrphanChecks`（`:101-102`）抓到。
- 检查只能依赖纯逻辑：`UiPreview.csproj` **排除 `Features/`**（那些文件反向依赖引擎），只链 `Ui/Pages` 的纯逻辑部分。
- 依赖外部状态（文件/时间/区域）要确定性化，CI 上也要能跑。
- 报错要点名**文件与值**，不要只说「不匹配」。

### 7.6 加一条变异

- 在 `mutate.ps1` 的 `switch` 里加块：`'MUT-Xn' { … }`，一块一行注释写清守哪条断言、为什么必须红。
- 用 `ReplaceExact`，**锚点必须唯一**；标签名也必须唯一（脚本有自检，历史上因重名静默失效）。
- 三种期望：正常（退出 0）、`-ExpectReview`（这条本来就没有断言）、`-ExpectGreen`（反向对照）。
- 退出码 2 表示变异没施加/没能判定：先修锚点，不要改断言去迁就它。
- 定期跑 `mutate-regression.ps1`：变异会静默失效（历史两次：多余实参被吃掉、锚点命中多次）。

### 7.7 改界面

- 只用 `Gui.*` 手绘；不要 `GUI.backgroundColor` 或魔改 `GUIStyle`。
- 新页面要进 `OnGui` 的 `TabKeys`，并加 `page.*` 文案；页签宽度固定。
- 所有帧状态字段在每趟绘制开头归零；原生对话框延后一拍。
- 布局核对用 `tools/UiPreview` 的导出图；那里不能调游戏 API。

## 8. 不许破坏的不变量

### 8.1 补丁与开关

- 开关为假 → **不挂载**，不写「进方法再判」。
- 每个 `Feature` 键必须在 `Main` 注册；未注册要报警。
- attribute 的 `Clazz`/`Method` 必须真实存在。
- `__instance` 类型必须等于 `Clazz`。
- 手工补丁卸载后，自己的静态标志要归零（`KeyViewerBridge._installed` 就是例子）。

### 8.2 容器

- 先验 MAC，再解密。
- 所有计数读取有界（4096 预分配、512MB 解压上限、命中记录最小长度）。
- 尾部段版本不认识时回退，不猜。
- 版本常量只出现在 `ReplayConstants`。
- **尾部可选段**：新段一律追加在最后，`FormatVersion` 不动；老构建读完 `cursor` 就返回（`KbdAudio` / `KbdPrefs` 就是这个范例）。
- **覆盖项用标志位表达**（`KeybdPrefs.HasVolume` / `HasOffset` / `IsMuted`），不要用哨兵值或长度推断——段布局定长，才能按固定偏移跳过。
- `KeybdSoundStartTick` 是 tri-state：**只有 `NaN` 表示「没有基准」**，`0` 与负数都是合法基准（0 = 正好从 0 秒起录）。metadata `ver < 3` 的 `0` 在解码时迁成 `NaN`；再拿 `0` 当哨兵就会吃掉真实的 0 秒起点。
- **可选段没数据就整段不写**：没动过设置的玩家，文件字节与旧版一致。段头要带版本上界，读侧才能安全跳过。

### 8.3 录制 / 回放 / 注入

- 录制时间戳 = `songposition_minusi`；回放刻意不读它。
- 泵在 **Prefix**；单帧 64 事件 / 256 命中上限；角度闸不能删。
- `ManagedInput.Active` 只等于 `is_playing`。
- `holdKeys` 单一来源；`Append` 必须用 `AsyncKeyCode`。
- `Stop` 顺序：快照 → 翻标志 → 发松开；难度/`noFail`/`hitMarginLimit` 必须在所有校验之后接管，并在 Stop 与两条失败路径都还原。
- OS 级 `SendInput` 保持关闭。

### 8.4 渲染 / 音频

- 每帧喂给 `AudioRenderer` 的样本数 **恰好等于**该帧采样数。
- WAV 哈希只覆盖 PCM（跳过 44 字节头）。
- 每作业独立 `RenderTemp/job_<ts>`。
- `ReportEnd` 是唯一写终态的地方。
- ffmpeg 失败要能回退 CPU；`-y` 截断残留要清。

### 8.5 界面 / 文案

- `LocData.cs` 不手改。
- 界面无字面量文案。
- `Loc.F` 的参数个数要与各语言占位符一致。
- 关面板先 `FlushConfig`。
- 新提示一律 `Status.Say`/`Status.Warn`：`extra` 是「在玩家设定值之上多加的秒数」，**不是绝对时长**；写死秒数会让「提示条停留时长」这项设置对那条提示失效。

### 8.6 生命周期 / 线程

关模组的顺序（`Main.cs:61-89`，改它等于改事故清单）：

`FlushConfig` → `StopPlaying` → `StopRecording` → `CancelRender` → `SetGetKeyAsyncEnabled(false)` → `SpectrePatchHub.UnpatchAll` → `PatchManager.UnpatchAll` → `MainThread.Uninstall` → `AudioRecorder.StopAllMicrophones`

提前任何一步的后果，代码注释（`Main.cs:68-76`）写得很具体：跳过 `StopPlaying` 会把玩家留在录像的判定模式里，而 `CachedDifficulty` 还攥着他的原值——下次装弹再 `Stop` 会把泄漏值当成原值写回，玩家真正的设置就永久丢了；跳过 `StopRecording`，is_recording 翻假后这份录制再也不会被保存；跳过 `CancelRender`，渲染会带着钉扎时钟继续跑，而它依赖的补丁马上就不在了。三步都自带「没在跑就早退」的守卫。

**退出进程/退出 Play 的顺序**（`Main.OnQuitting`，2026-10-28 实测过一次原生崩溃）：`CloudReplay.PrepareForExit()` 必须排在 `FlushConfig` **之前**。游戏自己的 `SteamClient.Shutdown()`（`scrController.OnApplicationQuit`）跑在 `Application.quitting` 事件**之前**——对象销毁先于 quit 事件链——所以 FlushConfig 触发的配置上传会拿着悬空的 RemoteStorage 句柄调 FileWrite，原生访问违例崩掉整个进程，托管 try/catch 拦不住（Editor.log 里只有原生栈、没有 Managed Stacktrace）。失能必须是持久标志（`SteamCloud.Disable` 置 `_disabled`，Ensure 先看标志再看指针）：只清指针挡不住 1 秒重试，会从「半销毁」的 steam_api 里把句柄重新探回来。编辑器 Play 会话之间模组域不重载，装配时 `SteamCloud.ResetProbe()` 清悬空缓存（与 `KeyViewerBridge.Reset` 同类）。离线断言：`CloudLifecycleCheck`（变异 MUT-C5/C6/C7）。

其它：

- 后台线程碰 Unity 只走 `MainThread.Run`。
- 卸载时停日志泵（`AsyncLog`）。
- 不要用 `Task`/线程去动 Unity 对象。
- **不要跨程序集重载缓存 `MethodBase`/`Type`**：模组重载后旧对象失效，缓存的补丁目标会指向已卸载的程序集。

## 9. 工具链坑

1. **`verify.ps1` 等脚本必须 UTF-8 with BOM。** PowerShell 5.1 按 ANSI 读脚本，没 BOM 中文变乱码、引号配对失败直接语法错。用 `write/edit` 改完要补 BOM（闸门有编码自检）。
2. **控制台是 GBK。** 脚本里的中文乱码不代表内容错；判断成败看退出码与 `✓/✗`，不要 grep 中文。
3. **注释剥离器要区分 `inStr`/`inChar`。** 历史事故：字符常量里的 `//` 被当注释。数 attribute 请用仓库的工具，别自己写正则。
4. **`mutate.ps1` 的锚点与标签都要唯一**（重名会静默失效）。**锚点会随源码重构腐烂**：2026-10-28 跑 `mutate-regression.ps1 -Filter C` 抓到 3 条已烂掉的（CI5 锚的 `Available` 属性在 `ba92948` 清理注释时删了、CI10 的成员名在 `ReplayPlayer` 里出现两次、CI6 对应的断言根本不存在）。症状分别是 exit 2 / exit 2 / exit 1。**改完源码必须跑一遍变异回归，别只跑闸门。**
5. **退出码 2 会伪装成「断言是假的」**，先分清是哪一种失败。
6. **模组目录判定看是否含 `*/Info.json`**，不是看目录存在。顺序：参数 → `SRM_MODS_ROOT` → `Mods` → `UMMMods` → 默认（`_gameroot.ps1:45-65`）。本机两个目录都有模组，猜错就是「部署成功但游戏没变」。
7. **游戏在跑 → dll 被锁 → 部署失败**；先关游戏，或用 `-SkipDeploy`。
8. **`port-diff` 只认点号标识符/`__x`**（E-F1 未修），移植比对别只靠它。
9. **有 4 个审计会把注释里的例子算进去**（E-F2 未修），计数偏多不代表覆盖更好。
10. **`PatchTargetCheck` 拿不到游戏程序集时静默返回 0**（未修）——CI 上这条等于没跑。
11. **`gen-locdata.ps1` 自己没 BOM**，`.ps1` 与 `.py` 两个生成器差 11 字节（仅头行），改一个要同步另一个。
12. **`LocData.cs` 与 `source.json` 没有一致性检查**：手改生成物闸门不会红。
13. **全量变异回归不在 CI**（E-F5 未修），要自己定期跑。
14. **`.gitattributes` 强制 `text eol=lf`**，别提交 CRLF 差异。
15. **文档口径普遍过期**（2026-10-28 实测）：`Loc.cs:10` 与 `tools/i18n/README.md:3` 写「325 键」，实际 **417**；`docs/verify.md` 写 111 条断言，实际 **153**；变异标签它写 153（`SUMMARY.md` 写 155），实际 **160**——别把「153 条断言」和「153 条变异」看混；`Settings.cs:8` 提到的 `SettingsUI` 类型**不存在**（绘制在 `Pages.Pages.Draw*`）；`SpectrePatch.cs:73-76` 的示例写 `MaxVersion = GameVersion.VerifiedBuild`，但 `GameVersion` 没有这个成员。引用任何数字前先实测。
16. `OnGui.cs` 的成员注释引用「类注释第 3/4 条规则」，但类注释已不在文件里——别去找不存在的规则原文。
17. 用 `Measure-Object -Line` 数行会跳过空行，行号会漂；以 read 工具/编辑器的物理行号为准。
18. 本仓库的工作区经常带着**未提交的改动**（本次写作期间 `Pages.cs` 就被本团队之外的人改过、dll 也被重新构建）。动工前后各看一眼 `git status`，别把别人的半成品算成自己的，也别顺手 revert。

## 10. 风格与提交

- 注释解释**为什么**，不是「这里做了什么」。本仓库的注释密度是刻意的，改动保持同等密度。
- 注释是契约：写着「不要 X」的地方通常对应一次真实事故，删之前先弄清那次事故。
- 日志前缀统一 `[SpectreReplay]`（`SpectrePatchHub.LogTag`）；跨线程日志走 `AsyncLog`。
- 提交信息是中文，形状如 `新增：<做了什么>--<为什么/收益>` 或 `修：<现象>--<根因>`；一个提交一件事。
- `README.md` 英文 / `README_zh.md` 中文，要同步；`docs/` 内部文档是中文。
- 不引第三方 NuGet：能用的 DLL 都在 `adofai-libs`。

## 11. 已知未修 / 雷区

| 项 | 现状 | 风险 |
|---|---|---|
| D-F2 去特效内存残留 | 未修 | 去特效期间摘掉的事件仍被持有 |
| A-F1 `string.GetHashCode` | 跨运行时不稳定 | 别拿它做持久化键 |
| A-F2 `.psprp` 解压无上限 | 未修 | 损坏/恶意文件可能吃满内存 |
| E-F1 `port-diff` 只认点号标识符 | 未修 | 移植比对有盲区 |
| E-F2 审计把注释算进去 | 未修 | 计数偏多 |
| E-F5 变异回归不在 CI | 未修 | 断言腐烂无人发现 |
| C-F4..C-F8 音频/ffmpeg | 部分未验 | 见 `docs/audit/C-video.md` 与源码注释 |
| 版本门控是 fail-open | 文档（`SpectrePatch.cs:167-168`）声称核心补丁 pin 到已验证 build、游戏更新后 fail-closed，但**没有补丁真这么做**：全仓 `MaxVersion` 只用于 r149/r150 签名分叉；`VerifiedBuild` 只进启动日志和 `Verified` 属性 | 游戏升到 r151+ 时补丁照挂，可能按旧语义执行 |

另外：`ui.patchfailed (N)` 是唯一**常驻**的「补丁没挂上」信号；只弹一次的通知会被玩家忽略。

## 12. 离线验不了什么

- 画面是否「跟当初一模一样」、键音、光标位置、渲染观感：只能实机。
- 实机路径：`tools/deploy.ps1` → 进游戏 → `Ctrl+F10` 开面板 → 进关卡录一局 → `F9` 手动存 → **重新载入该关卡**后回放（回放要重开那一关才真正开始）→ 渲染一段短片。
- 热键：`Ctrl+F10` 面板；`F9` 手动保存；`F10` 放弃本局保存；`F11` 中止渲染（设置页可改）。
- 已知需要实机确认的假设：15fps 下 `AudioRenderer` 溢出、`ScreenCapture` 回读帧对齐、`Microphone.Start` 采样率行为、NVENC 探针死锁可达性。

## 13. 提交前清单

- [ ] `dotnet build SpectreReplay.slnx -c Release` 0 警告
- [ ] `tools/verify.ps1` 退出码 0（或能说清跳过了什么）
- [ ] 新断言配了变异，`tools/mutate.ps1 -Which …` 退出 0
- [ ] 改了文案：生成器跑过、12 语言齐、`LocCheck` 绿
- [ ] 改了补丁：启动日志「挂上 x/y」满、`ui.patchfailed` 为 0
- [ ] 改了容器：拿一个旧回放真读一次
- [ ] 改了 `Settings`/`Configs.json` 形状：拿一份**旧的** `Configs.json` 加载一次（缺键要走默认，不能报错吞设置）
- [ ] 工作区只含本次改动（别把别人的半成品一起提交）
- [ ] 提交信息写清「为什么」

## 14. 文档索引

| 想知道 | 去哪 |
|---|---|
| 用户视角、装法、热键 | `README.md`（英）/ `README_zh.md`（中） |
| 闸门逐条检查清单与设计理由 | `docs/verify.md`（**注意数字已过期**） |
| 全项目体检的发现与口径 | `docs/audit/README.md`、`SUMMARY.md`、`A~E` 五份 |
| Spectre 移植历史（大部分已失效） | `docs/SPECTRE_COMPARISON.md`（顶部有失效警告，别当现状） |
| 文案管线 | `tools/i18n/README.md` |
| 游戏侧真实行为 | `adofai-libs/source/Assembly-CSharp`（1235 个 .cs） |

最后一句：**这个仓库的纪律是「每个断言都要能被证伪」。** 最省时间的做法不是快点改完，而是先想清楚「我怎么知道它对了」。
