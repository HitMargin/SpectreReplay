# 离线验证清单

跑一遍：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify.ps1
```

四个参数：`-SkipBuild` / `-SkipPreview` / `-SkipDeploy`（游戏在跑时 dll 会被锁，
用 `-SkipDeploy` 跳过部署那一节）/ `-SkipAudits`（跳过只读审计那一节）。

五件事绑成一次判定，任何一项红就返回非零：主项目构建、离线验证套件、
**只读审计**（7 个离线审计脚本）、线上 dll 与构建产物的 sha256 一致、
`D:\Projects\Spectre` 工作区干净。

> **当前打印 57 条断言 0 失败**（2026-10-16 实测）。这个数字会随轮次变，
> 引用前先跑一遍 `verify.ps1` 看真实条数。**注意它是「✓ 行数」而不是判据数**
> ——`UiLogicShapeCheck` 现有 13 条判据只印 1 行，所以加判据这个数字不变。

## 套件构成

闸门在 `tools\UiPreview`，`Driver.cs` 汇总所有 `*Check.cs`。**新增 .cs 检查文件
会被 `EnableDefaultCompileItems` 自动收进来，但必须在 `Driver.cs` 里登记
`<类名>.Run(...)`**——不登记就是孤儿检查，一次都不跑而闸门只会更绿
（`PortedCompletenessCheck.NoOrphanChecks` 守这件事，变异 `MUT-L2`）。

| 组 | 验的是什么 |
|---|---|
| LocCheck | 页面引用的键在两种语言下都齐；两张表键集合一致；中文/英文都能解析成文案、不返回键名本身 |
| HardTextCheck | 没有被写死的用户可见中文；文案表文件里没有漏网的中文字符串 |
| ReplayCheck | 链接真实 ReplayEncoder/Decoder 的回放格式往返（五种数据形状含巨大 count 被干净拒绝） |
| WavRoundTripCheck | 键盘音 WAV 的写读往返：写侧头部契约**取自真源码**、读侧接受范围覆盖写侧产出、拒 6 种坏头（非 PCM/位深 12,64/声道 0,负/FormatTag 0）、四种合法位深全接受、位深下限常量与判定一致 |
| LocRuntimeCheck | 两个方向：**正向**——被取词的运行时键（`GetLocalizedText` 字面量 + `SetPhase` 第二实参）在全部 14 种语言下都齐；**反向**——表里定义的键必须真有人取，死键即为红（2026-10-17 加，此前表里有 315 个键、真正在用的只有 33 个） |
| PortedCompletenessCheck | 移植搬来的模块都真有调用点：ReplayAnalyzer 显示、DescribeEncoder 降级提示、playback.ready 装弹提示、VideoConverter 入口、**转换目标来自 `Settings.ConvertTarget` 且必须可编辑 + 双向持久化（⑦/⑦b/⑦c）**、回放列表覆盖全部可读格式、键盘音三项有界面入口（⑨a/⑨b）、回放驱动开关有界面入口（⑩/⑩b）、保存目录有界面入口（⑪/⑪b）、**「打开这一关」按钮（⑫/⑫b）+ 编辑器跳转等就绪后定位起始砖（⑬）+ 命中分布图复用缓存（⑭）+ 渲染时钟开关四件齐（⑮a~⑮f）**、**去特效总开关默认关（⑯）+ 中文文案不得把关卡称作存档（⑰）**、没有孤儿检查 |
| UiLogicShapeCheck | 界面控制流形状：停止录制不许借用「取消保存」文案、文本框必须**自绘**（不许用内建输入框）且宽度走 Gui.CtrlW、分组边框不许用 GetLastRect、**阻塞式原生目录选择框必须 Defer 到 OnGUI 之外（两个调用点各一条）**、**输入框底色必须比卡片更亮**、**按钮必须传自定义样式 + 滑条必须自绘 + 圆角贴图必须有缓存**（2026-10-12 用户报「UI 还是原生控件」后加的）、**混音轨 Render 容器必须精确**（⑧）、**卡片不许用 GUI.backgroundColor 染色**（⑨，乘算会抹平按钮 hover）、**更新检查三态不许合并 + 版本比较方向 + 读 ModEntry.Version + info.json 两个字段非空**（⑲a~⑲d，2026-10-17 加） |
| MissingRuntimeKeyCheck | 运行时键必须有定义（`GetLocalizedText` 查不到时 `return key`，玩家会看到原始键名） |
| KeybdSoundTickCheck | 键盘音起始时间戳：起录块不许导体判空后 return、必须能补真值、回放侧拒非正基准 |
| ConfigMigrationCheck | 旧配置迁移：迁移真的被调用、不误判新版文件、每个旧键都有归属 |
| PatchTargetCheck | 补丁目标在**真程序集**上存在且形参对得上；解析覆盖率 == 源码声明数 |
| ReflectionTargetCheck | 按名字取的游戏成员存在：`Notification.SetupNotification` 形参、`Time` 5 属性 getter、`AudioSettings` 3 属性 |
| LevelProbeCheck | 关卡探测判据：现判据认得出 scnGame/scnEditor；三个历史错判据仍然错；认得出 4 个非游玩场景；移植后仍用场景名不许退回 `gameworld` |
| CrossThreadFieldCheck | 9 个跨线程字段都带 `volatile`（UI 线程读、编码线程写） |
| ReplayCryptoCheck | 加密往返、密文无明文 magic、错密钥被拒、**与真实 Spectre 模组的 .sprp 文件实测互通** |
| EnumParseGuardCheck | 持久化枚举带 `Enum.IsDefined` 护栏（`Enum.TryParse("99")` 会成功并给出越界值） |
| LoadMarkerCheck | `Load()` 第一句是 Debug.Log 启动行且**在 try 之前**（异常路径也带得到） |
| LocFallbackCheck | 文案回退链每一级都活着：两张表对称、缺键时静默降级到另一种语言、双缺才露键名 |
| InstanceInjectionCheck | 16 处 `__instance` 全部作用于**实例方法**（Harmony 只对非静态目标注入） |
| LogPrefixCheck | 全部日志用 `[SpectreReplay]` 前缀（真实 Spectre 模组也装着会跑，前缀是区分来源的唯一依据） |

**总数 58 个 ✓ 行**（2026-10-17 实测 58 ✓ / 0 ✗）。这个数字会随轮次变，
引用前先跑一遍 `verify.ps1` 看真实条数。

> **口径修正（2026-10-12）：`✓ 行数 ≠ 断言单元数`，早先"两者相等"的说法是错的。**
> 实测有两处**折叠**：`PortedCompletenessCheck` 有 15 个判据却只印 2 行
> （「随移植搬来的模块都已被接上」+ 孤儿检查），`UiLogicShapeCheck` 有 **13 个判据**
> 只印 1 行（「十三条界面形状判据都成立」）。其余 *Check 基本一行一判据。
> 于是加判据**不会**让 `mutate.ps1` 印的「断言 N 条」变大——2026-10-12 加了
> ⑪/⑪b/④ 三条后该数字仍是 57，而三条都由 MUT-P12/P13/U6 证明会红。
> **引用条数时说明是"✓ 行数"还是"判据数"，别把前者当日志里的真值。**

每加一条断言，**必须用 `tools\mutate.ps1 -Which MUT-X` 证明它会红**（自动还原）。
已累计 10 条以上假断言、2 条假变异——形态见 `docs\SPECTRE_COMPARISON.md`
与记忆库的「六条铁律」。`-ExpectGreen` 是反向对照（证明"全绿"本身可达）。
`tools\mutate-regression.ps1` 全量回归 **95 条**变异（计数口径：`mutate.ps1` 里 `'MUT-XXX' {` 块的去重数；2026-10-17 实测 95）。新增 `MUT-V11` 守「Render 容器必须精确尺寸」；
`MUT-V12`/`MUT-V13` 分别守判据⑨的两半（卡片底图必须是卡色贴图、
不许再出现 `GUI.backgroundColor` 赋值——乘算会抹平按钮 hover）。
2026-10-17 另加两条：`MUT-U34` 守「去特效总开关必须默认关」（⑯）、
`MUT-U35` 守「中文文案不得把关卡称作存档」（⑰）。
`MUT-K3` 守 LocRuntimeCheck 的反向那一半（往表里塞一条没人取的键 ⇒ 必须红）。
`MUT-K4`~`MUT-K7` 守更新检查的几条判据（⑲a 已最新必须依据「查过没有」而非 `newest != null`、⑲a2 必须真读 `Params.LastUpdateCheck`、⑲b 比较方向、⑲c 读 ModEntry.Version、⑲d info.json 两个字段非空）。

> **⑲a 的判据本身改过一次，值得记下来。** 第一版要求「已最新」挂在 `newest != null` 上，
> 它能通过，但守的语义是错的——反编译 IL 证明 `ParseRepository` 只在
> `current < release` 时才 `stfld NewestVersion`，**版本相等时保持 null**。
> 于是「已最新」永远亮不起来，界面一直停在「未检查」，玩家刚查完也看不到结果。
> 判据若只验「三态文案都出现过」，就会替这个错误设计背书。
> 现在钉的是**分界条件本身**（必须是 `checkedOnce`）。这是「判据不能只验存在、要验语义形状」的又一例。

> **变异锚点会跟着代码改而失效，且失效的样子是 exit 2 而不是红。** 删掉
> `GUILayout.TextField` 那次，MUT-U2 的锚点（`TextField` + `CtrlW` 那一行）
> 直接命中 0 次、脚本 `throw`——闸门没红，是变异自己跑不起来。改完生产代码
> 记得回头核对被它钉住的那几条变异名，`-Which` 单跑一遍。

> **MUT-PORT1 走的是另一条判据。** 它改的 `KeyboardSimulation.cs` 没链进离屏
> 套件（见 `UiPreview.csproj` 的链接清单），跑 UiPreview 永远是"0 失败"——
> 真正的判据是 `tools\port-diff-audit.ps1`。`mutate.ps1` 按 `$portAuditMutants`
> 自动分派（也可用 `-PortAudit` 强制），且要求审计**点名本次改的那个文件**，
> 不只看退出码——否则"本来就有别的未登记差异"会被误当成"这条变异被检出"。

## 只读审计（闸门第 3 节，`-SkipAudits` 可跳过）

| 脚本 | 验什么 |
|---|---|
| `tools\harmony-shape-audit.ps1` | 补丁方法签名形状：Prefix 返回 void/bool、Finalizer 返回 Exception、Transpiler 返回 `IEnumerable<CodeInstruction>` |
| `tools\instance-injection-audit.ps1` | `__instance` 只对实例方法（同上 InstanceInjectionCheck，另给非 C# 视角） |
| `tools\patch-sig-audit.ps1` | 普通形参与目标方法同名同型 |
| `tools\result-injection-audit.ps1` | `__result` 的目标都有返回类型（void 上配 `__result` 拿不到值） |
| `tools\deadcode-audit.ps1` | 零调用点的静态方法，按「设计使然 / Spectre 有调用方但按裁决不补 / 自己造成的」三分，**退出码只对第三类敏感** |
| `tools\settings-ui-audit.ps1` | 每个设置项是否有界面入口（报告型，恒 exit 0） |
| `tools\port-diff-audit.ps1` | 移植保真度：与上游 Spectre 逐文件比对，**每处差异必须在登记表里有记录**。这是**真门禁**，挂不上的差异直接 exit 1 |

审计脚本在闸门里**只印一行摘要**，出问题才贴详情（`deadcode-audit` 是报告型脚本，
一次吐上百行，全量贴会把真正的 ✗ 埋掉）。

## 手动真跑脚本（不在闸门里，需手动跑）

改编码/转换参数后跑，它们调**游戏自带那个真 ffmpeg.exe**：

| 脚本 | 验什么 |
|---|---|
| `tools\ffmpeg-probe.ps1` | 编码参数四容器端到端：真 BGRA → 滤镜链 → 中间文件 → mux，产物音画双流俱在；奇数宽高 33×25 跑通 `pad`（裸 `vflip` 会 exit -542398533、产物 0 字节） |
| `tools\convert-probe.ps1` | 转换参数四容器；**目标为 mov 时视频必须重编码**——mov 复用 movenc 且拒绝 VP9 |
| `tools\deadcode-audit.ps1` | 零调用点的静态方法（同时也在闸门第 3 节跑，报告型） |

## 验不了的

只有一件，而且只有这一件：

**回放与视频渲染的最终效果只能在游戏里实跑确认。** 闸门守的是"不会静默废掉
整条链路"那类规则（用真 ffmpeg 跑参数、用真程序集反射核对补丁目标），
不是"画面好看"。当前 `Params.xml` 里 `com.spectre.replay` 仍是
`Enabled="false"`，`Load()` 一次都没跑过。

## 改完记得跑

预览器绿灯**不等于**主项目能编译：预览器是 `net10.0-windows`，只链接
`Ui\Loc.cs`、`Features\Replay` 下若干纯逻辑文件与检查自身；主项目是
`netstandard2.1`。两者编译的不是同一份源码集。
`verify.ps1` 把两边绑在一起就是为了这个。

另外两个会反复咬人的工具坑（改之前必读）：

1. `edit` 工具写 `tools\*.ps1` 会剥掉 UTF-8 BOM，PowerShell 5.1 按 ANSI 读会
   中文乱码报 ParserError，且常伪装成"被测对象全有问题"（脚本读到 0 字节）。
2. 注释剥离器（`HardTextCheck.StripComments` 与 `SrcUtil.Clean`）必须让
   `inStr` 优先于 `inChar`——字符串内的撇号（如 `"the game's"`）若切了
   `inChar`，其后整个文件的注释都剥不掉。
