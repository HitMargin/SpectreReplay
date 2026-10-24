# 全项目审计报告（2026-10-24）

本轮由 Lead + 5 名审计员并行完成，覆盖 `SpectreReplay\`（71 个源文件）与 `tools\`（工具链、CI、i18n）。
明细见同目录 `A-replay-core.md` / `B-replay-runtime.md` / `C-video.md` / `D-ui-state.md` / `E-tooling.md`，
协议与口径见 `README.md`。

## 一、范围与方法

- **代码对照游戏**：全部补丁目标、反射字符串、判定口径都与 `D:\Projects\adofai-libs\source\Assembly-CSharp`
  （反编译，`Releases.cs` 里 `releaseNumber = 150`，即当前装机版本）逐条核对，不是靠记忆推断。
- **双向核查**：既找「代码怎么坏」，也核对「注释声称的语义」是否成立。注释与代码冲突按**发现**记，不按风格记。
- **基线**：审计开始时 `tools\verify.ps1 -SkipDeploy -Ci` 全绿（110 条断言 0 失败，7 个只读审计全过）。
  本轮所有修复都以「改完仍然全绿」为前提。
- **不重复既有门禁**：`docs\verify.md` 已覆盖的形状判据（65 条界面形状、跨平台输入 14 组、变异 152 条）
  只在「判据本身是假的」时才上报（见 B-F4、E-F1）。

## 二、结论摘要

**P0 = 0，P1 = 7，P2 = 26，P3 = 32。本轮修掉 7 条。**

> **口径更正（2026-10-24，经用户裁定）**：审计员 D 的 **F1「去特效的另存为会覆盖原谱」不是缺陷**。
> 原实现只禁 `SaveLevelEditorAction`（Ctrl+S 与两颗保存按钮，目标就是当前 `levelPath`，等于覆盖原谱），
> 而「另存为」走 `SaveLevelAsEditorAction` 是**刻意保留的出口**——去完特效把干净版存到别处正是这个功能的用途。
> Lead 一度把前缀下沉到共同出口 `scnEditor.SaveLevel`，等于把唯一合法的保存方式也堵死，属**过度拦截**，已撤销。
> 教训与「六条铁律」同类：拦截点选得越靠近底层，越容易把设计者有意保留的旁路一并杀掉；
> 判「这是漏洞」之前要先问「这条路为什么存在」。

没有 P0，最扎实的三条正面结论：

1. **27 个 Harmony 补丁目标在 r150 上全部存在、形参匹配、Prefix 返回值语义正确**（B 逐个列了游戏源码行号）；
2. `AsyncInputInjector` 的 6 个反射字符串在装机 `SkyHook.Unity.dll` 上逐项命中（`SkyHookEvent` 是 struct，
   五个字段类型全对）——**没有「改名静默失效」的隐患**；
3. 回放容器各段读写逐字段对称，加密构造（随机 salt/IV、双密钥、encrypt-then-MAC、先验 MAC、定长比较）无问题。

最该紧张的四类问题（都不在闸门视野内）：

| 类别 | 代表 | 为什么闸门看不见 |
|---|---|---|
| **旧回放兼容** | A-F1：v1 回放 100% 加载失败 | 闸门只做「当前版本的往返」，从不构造 v1 文件 |
| **状态泄漏** | B-F1/F2：`HitLocked`、关模组不 Stop | 判据看的是「补丁挂上没有」，不看「状态还回去没有」 |
| **玩家磁盘数据** | D-F2：关掉去特效后仍保存会写坏原谱 | 没有任何判据写真实文件 |
| **跨作业/跨线程生命周期** | C-F1、Lead-M1：固定临时目录互删、Uninstall 后后台线程当主线程 | 需要「两次渲染重叠」「在跑时关模组」这类时序，离线套件不构造 |

## 三、本轮已修（7 条，均通过闸门复核）

| # | 严重度 | 问题 | 修法 | 证据 |
|---|---|---|---|---|
| 1 | **P1** | **v1 旧回放全部加载失败**：快照段最短记录长度把 v2 才有的 `HitTick` 标志算进去（42 vs 41），`remaining/42` 对任意 N 条都算出 N−1 ⇒ 每一份 r149 及更早（含上游 Spectre 存量）回放在 `ReadCount` 被拒，报成「文件损坏」 | 按段版本选最小值（`MinHitSnapshotBytesV1 = 41`） | 新增断言 ⑹，并用变异体证明会红：改回 42 后报 `Record count 3 exceeds remaining stream (123 bytes)` |
| 2 | **P1** | `ReplayPlayer.Stop()` 漏 `HitLocked = false` ⇒ 关卡里点「停止」后 `Patch_Recording` 的 Prefix 恒 `return false`，游戏自己的 `scrPlayer.Hit` 整局被跳过，只能重开关卡 | Stop 末尾复位 | B-F1 |
| 3 | **P1** | 关模组/关回放总开关只卸补丁不停回放/录制/渲染 ⇒ `noFail`、`difficulty`、`hitMarginLimit` 无人还原，泄漏进之后所有正常游玩；渲染作业带着钉扎时钟继续跑而它依赖的补丁已不在 | `OnToggle(false)` 里先 `StopPlaying` → `StopRecording` → `CancelRender`，再卸补丁 | B-F2 + Lead 独立复现；`ReplayPlayer.Stop` 是唯一还原路径 |
| 4 | **P1** | 渲染临时目录是**固定路径** `RenderTemp`，且被三处无条件删：收尾线程超时后清理被推迟到后台（最长 40 分钟），而 `CloseJob()` 已放行下一轮 ⇒ 延迟线程删掉**新作业正在写**的目录 | 每作业唯一目录 `RenderTemp\job_<时间戳>`；历史残留只清 12 小时前的 | C-F1；`VideoRenderJob.cs:995-1013`、`:1437-1487` |
| 5 | P2 | mux 失败会在输出目录留下打不开的半成品成片（`-y` 建进程时就截断建好目标文件） | 失败时删目标文件（原始流已由 `RescueMuxFailure` 另存） | C-F2 |
| 6 | P2 | 回放启动的两条失败分支还原了难度与 noFail，唯独漏 `GCS.hitMarginLimit` | 两处都补 `GCS.hitMarginLimit = Persistence.hitMarginLimit`（与 Stop 同口径） | B-F3 |
| 7 | P2 | `KeyViewerBridge._installed` 不随 `UnpatchAll` 复位 ⇒ 关开一次模组后键位显示器永久全黑且无日志 | 加 `Reset()`，模组每次启用时调用 | B-F5 |

附带一条 Lead 侧的跨模块加固：`MainThread` 原来的判据是 `_pump == null ⇒ 认为自己是主线程`。
`Uninstall()` 之后 `_pump` 变 null，**仍在跑的收尾/采集线程再调 `Run` 会被当成主线程、就地执行 Unity API**
（正是关模组那一拍最容易崩的地方）。改成 Install 时快照主线程身份，并把 `_pump`/`_mainThread` 标 `volatile`。

## 四、未修但已确认（按「为什么这轮不改」分组）

### A. 需要改文件格式（不能静默改，要配迁移）
- **A-F1 关联（Lead）**：回放完整性校验（`AngleHash` / `TimeHash` / `SpeedHash` / `floorHash`）落盘用的是
  `string.GetHashCode()`。它在同一运行时内确定，但**不是跨运行时契约**：游戏换 Unity/Mono 版本后，
  存量回放会重算出不同哈希、被误判成 `note.play_error2`（“关卡数据错误”）。建议换稳定算法
  （FNV-1a / SHA-256 截断）并加「旧字段可回退」的一次性迁移。
- **A-F2**：512 MB 解压上限 + `ReadCount` 只验「count 与剩余字节成比例」（不验绝对量）⇒ 几百 KB 的
  `.psprp`（无 MAC，任何人可造）可换出约 2~3.8 GB 托管分配。修法要同时动上限与预分配策略。
- **D-F8**：X-ACC 少乘 `scrMarginTracker.cs:216` 的 `0.9875^checkpointsUsed` ⇒ 用检查点录的回放显示偏高，
  且当前格式无法反推（需录制时存 `checkpointsUsed`）。

### B. 需要实机确认（离线只能推到「可疑」）
C-F4（NVENC 探测重定向 stdout/stderr 却从不读 → 管道满死锁，负结果还永久缓存）、
C-F5（捆绑 `ffmpeg.exe` 只 `File.Exists` 不探测能力）、
C-F6（混音轨采样率/声道只在构造时取一次，换输出设备会静默变速）、
C-F7（`AudioRecorder.Cleanup` 在线程未退出时不销毁 `recordingClip` → 原生内存泄漏）、
C-F8（WAV 头写“请求的”采样率而非 `clip.frequency`）、
C-F11（`UnscaledDeltaPin` 撤销失败后无自愈）。
这些都要在游戏里跑一遍渲染/换设备/反复开关才能定性，且改错会伤成片——不建议盲改。

### C. 设计权衡（作者已在注释里写明取舍，不建议单方面改）
- **C-F10**：`-shortest` 在音频短于视频时截的是**视频结尾**且仍报 `Completed`。作者意图明确
  （“有它则按短轨收尾”），真正的根因是音频链会静默重启丢样本（C-F6/F7）。改行为前应先修音频链。
- **B-F4**：`DisableSendInput = true` 让整条 `SendInput` 镜像路径成为死代码，
  连带使离线闸门 CrossPlatformInputCheck ⑪b 成为**空断言**。删开关或删路径都能解决，但要先定“是否还打算支持 SendInput”。
- **D-F2**：`LevelData.Decode` 就地改写谱面无快照 ⇒ 关掉去特效后内存里仍是去特效版，再保存即写坏。
  正确修法是「进编辑器前拍一份原始快照、关开关时还原」，属于新机制而非补丁。
  **残留风险**：游戏侧 `scnEditor.GetExportLevelFiles`（`scnEditor.cs:7345`）也直接写 `levelPath`，
  本轮的 `SaveLevel` 前缀覆盖不到「导出/上传」这条路。

### D. 工具链（审计工具自身的可信度）
- **E-F1 [P1]**：`port-diff-audit.ps1:1013-1030` 最后只把「点号标识符 / `__x`」当未登记差异，
  **裸数字、标识符、运算符漂移一律假绿**（实测把 `VideoEncoder.cs` 的 `30_000` 改 `15_000` 仍印通过）。
  它挡不住移植期最常见的手误（常量、比较方向、加减号）。
- **E-F2 [P2]**：`verify.ps1` / `mutate.ps1` 只看 `✗` 文本行，**不看退出码与「N 项不通过」**，
  也没有断言行数下限 ⇒ 删掉一个 Check 的注册只会让数字变小，不会红。
- **E-F3 [P2]**：部分 PS 审计不剥注释 ⇒ `SpectrePatch.cs:59-61` 的用法示例被当成真补丁，
  `__instance` 报 17 处（真值 16）。**`docs/verify.md:57` 的 17 与 verify 输出的 32/16/7 里，
  凡是把注释算进去的数字都不可引用。**
- **E-F4 [P2]**：`gen-locdata.ps1` 无 BOM（PS 5.1 下直接 ParserError）；两个生成器产物并不逐字节相同；
  且**没有判据校验 `LocData.cs` 与 `source.json` 同步**——改文案忘生成会全绿。
- **E-F5 [P2]**：变异回归不进 CI，`mutate-regression-last.log` 停在 10-17「76/76」，
  而 `mutate.ps1` 现有 **155** 个唯一标签 ⇒ 79 条从未进过全量回归。\n\n## 五、按区域的发现索引\n\n| 区域 | 报告 | 条数 | P1 要点 |\n|---|---|---|---|\n| 回放编解码与数据层 | A-replay-core.md | 8 | v1 旧回放全挂（已修）；解压炸弹 |\n| 回放运行时与补丁层 | B-replay-runtime.md | 12 | HitLocked 泄漏（已修）；关模组泄漏（已修） |\n| 视频渲染与音频 | C-video.md | 15 | 固定临时目录互删（已修）；ffmpeg 三处进程风险 |\n| 界面、状态与兼容层 | D-ui-state.md | 16 | 去特效关掉后内存仍是去特效版（未修）；F1「另存为」经裁定不是缺陷 |\n| 工具链 / CI / 文档 / i18n | E-tooling.md | 14 | port-diff 判定口径只认点号标识符 |\n\nLead 侧另行确认：`MainThread` 主线程判据（已修）、关模组收尾（已修）、\n落盘哈希依赖运行时 `string.GetHashCode()`（未修，见四.A）、版本门控与文档不符（见六）。\n\n## 六、版本适配专项（r150 / Unity 6）\n\n- `info.json` 的 `GameVersion: 2.7.0` 与 `Releases.releaseNumber = 150` 一致；\n  `Main.cs` 的 `Configure(..., 141, 150)` 与装机版本（150）一致，下限 141 是政策值。\n- **版本门控的实际形态与文档不符**：`SpectrePatch.cs:72-78` 教的是「核心补丁 pin 到 `VerifiedBuild`，\n  游戏更新自动停用（fail-closed）」，但**没有任何补丁真的 pin**（全仓只有两处 `MaxVersion = 149` 的版本分叉）。\n  于是游戏更新到 r151 时，核心录制补丁会照常挂上（fail-open），只在日志里留一行「未验证」，界面无提示。\n  建议：要么给录制/渲染这类「错数据比没功能更糟」的补丁补上 pin，要么把文档改成描述现状。\n- 版本分叉两处均正确：`Hit(long? hitTick, bool isAuto)` 与 `GetSpecialInput(List<AsyncKeyCode>)` 的 r150 新签名\n  （`MaxVersion = 149` / `MinVersion = 150` 各钉一半）。\n- Unity 侧：游戏已是 Unity 6000.3.25f1（Unity 6），本项目仍是 `netstandard2.1` + Mono + 裸 `Thread`，\n  这套原语在 Unity 6 上仍可用；`AsyncGPUReadback` / `ScreenCapture` / `AudioSettings` 的用法已按装机程序集核对。\n\n## 七、验证证据\n\n```\n初始基线   tools\\verify.ps1 -SkipDeploy -Ci   → 110 条断言 0 失败，7 个只读审计全过\n修复后     tools\\verify.ps1 -SkipDeploy -Ci   → 111 条断言 0 失败，全部通过（新增 v1 那条）\n移植保真度  tools\\port-diff-audit.ps1          → ✓ 全部差异均可追溯（新增 18 条登记，见脚本 2026-10-24 段）\n新增断言    ⑹ v1 旧回放仍能解码          → 绿；把守卫改回旧写法后 → 红（Record count 3 exceeds…）\n主项目      dotnet build -c Release         → 0 警告 0 错误\n```\n\n> 引用「闸门全绿」前请先确认 `git status` 为空：`port-diff-audit` 对脏工作区没有任何提示，\n> 同一命令几分钟内能给出相反结论（本轮 E 就踩到过一次并自行更正）。\n> 另：本机 `Mods\\SpectreReplay` 为空，而 `-SkipDeploy` 只跳过部署动作、**不跳过 sha256 比对**，\n> 所以本机要跑绿得用 `-Ci`（它会跳过部署一致性与上游工作区两项）。\n\n## 八、下轮建议（按性价比排序）\n\n1. **修 E-F1 / E-F2**：审计工具自身假绿是元级风险——port-diff 扩展到裸数字/运算符，verify 加退出码与行数下限。\n2. **回放哈希换稳定算法** + 一次性迁移（四.A），否则换 Unity 版本时存量回放会集体变「损坏」。\n3. **补历史格式回归**：目前只有一份「当前版本往返」，任何历史段版本都没有样本（v1 那条是本轮补的第一份）。\n4. **给核心补丁补版本 pin**（六），或改文档承认 fail-open。\n5. **在真实游戏里跑一遍**：渲染完成/取消、换音频设备、关开模组、编辑器另存为——本轮修的都是这几条路径。

## 九、未修的 P1 清单（下轮从这里开始）

| # | 问题 | 为什么这轮不动 | 建议入口 |
|---|---|---|---|
| 1 | **D-F2 去特效应答后内存仍是去特效版**：`LevelData.Decode` 后就地改写、无快照；关掉总开关只恢复保存按钮（`Pages.cs:1163`），内存里仍是去特效版，再保存即写坏；`SaveBackup` 还会自动写一份残缺 `backup.adofai` | 改动面比「几个列表」大：它还**新增** `backgroundSettings` / `cameraSettings` / `decorationSettings`，并清 `decorations`、改轨道颜色与不透明度。盲写「恢复」在无法实机验证的前提下比现状更危险 | 二选一：① `Decode` 前 `levelData.Encode()` 存一份 JSON，关开关时按存档重解码；② 进编辑器前拍一份深副本。两者都要实机确认「重解码不会破坏编辑器瞬时状态」 |
| 2 | **E-F1 port-diff 判据太窄**：只把「点号标识符 / `__x`」当未登记差异，裸数字、比较方向、运算符漂移一律假绿（实测 `30_000`→`15_000` 仍通过） | 这是审计工具自身的改法，动它要先把现有 39 个有差异文件的归一化口径重新校准，否则一夜之间全红 | 扩展归一化：把裸数字与运算符也纳入 token 流；用「先跑基线、再逐文件收紧」的方式迁移 |

> 这两条都不影响已修的 8 条，也不改变「当前闸门全绿」的事实；它们是**已知且已定位**的遗留项，
> 不是未发现的盲区——这正是本轮审计想要达到的状态。\n
