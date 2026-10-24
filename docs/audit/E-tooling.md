# 工具链 / CI / 构建配置 / i18n 审计报告

## 结论摘要

- **最严重的一条是「移植保真度」这条真门禁的判据口径**：`port-diff-audit.ps1` 最后只把「点号标识符
  （`A.B`）」和 `__x` 形状当作"未登记差异"，裸数字、裸标识符、运算符、字符串字面量的漂移一律
  落到"差异全部有归属"。我在**临时导出**的 HEAD 上做了实证：把
  `VideoEncoder.cs:38` 的 `ProbeRetryMs = 30_000` 改成 `15_000`（重试预算砍半，真实行为漂移），
  审计仍然印 `✓ 移植保真度通过`，退出码 0。也就是说文档里"加了它，抄错上游从此会被抓到"（`docs/verify.md:115`）
  只对"新造了一个带点号的名字"这一种抄错成立。
- **断言有两个独立信道，而闸门只看其中一个**：`Driver.cs` 既**打印** `✗` 行、又**返回**失败计数
  （`Driver.cs:95-100`），但 `verify.ps1:146-164` 与 `mutate.ps1:1918-1934` 都只解析 `✗` 文本、
  **完全不看 `dotnet run` 的退出码**，也不看 Driver 印的「N 项不通过」。今天 26 个 `*Check` 恰好都
  印了 `✗`，所以没出事；这是一条纯约定的链路，没有任何判据守着它。
- 其次是**注释被当成代码**：三个只读补丁审计都直接对原文跑正则，`Patch/Shared/SpectrePatch.cs:59-61`
  那段用法示例（`[SpectrePatch(typeof(scrPlayer), "Hit", ...)]` + 下一行注释里的
  `internal static bool Prefix(scrPlayer __instance)`）被当成一条**真实补丁**，于是
  `harmony-shape-audit` 报「共 42 处」（C# 侧覆盖率自检数出的真值是 41 条声明），
  `instance-injection-audit` 报「共 17 处」（真值 16）。`docs/verify.md:57` 的「17 处」、
  `verify.ps1:58` 的「32 处」、`:60` 的「7 处」全是陈旧/被幽灵行污染的数字。
- i18n 本身**健康**：14 语言 × 325 键、12 个 locale JSON 键集与 `source.json` 完全一致、JSON 全部合法、
  `LocData.cs` 与生成器产物**逐字节相同**（我实跑复现）。但生成器本身有三处硬伤：
  `gen-locdata.ps1` **没有 UTF-8 BOM**（PowerShell 5.1 下直接 ParserError，实测），
  两个生成器**产物并不相同**（README 宣称"逐字节相同"是假的），`gen-locdata.py` 把
  `ROOT` 写死成 `D:\Projects\SpectreReplay`（换机器/CI 就写错位置）。
- 没有 P0。下面 F1~F8 是 P1/P2，其余 P3。分类口径按协议：只报真 bug、隐性问题、脆弱代码、
  版本适配风险、注释与代码不符。

## 发现

### F1 [P1] 「移植保真度」判据只认点号标识符，裸数字/裸标识符/运算符漂移全部假绿

- 位置：`tools/port-diff-audit.ps1:1013`、`:1022`、`:1030`、`:1044-1050`
- 证据（判据的最后一步：把已登记模式与 C# 关键字抠掉，只留"像新造名字"的东西）：

```powershell
$covered = ($extra -join ' ')
foreach ($k in $knownDeviations) { ... $covered = [regex]::Replace($covered, [regex]::Escape($k.Pat), ' ') }
foreach ($kw in @('if','else','for',...)) { $covered = [regex]::Replace($covered, ('\b' + $kw + '\b'), ' ') }
$suspects = @([regex]::Matches($covered, '[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+|__\w+') | ...)
if ($suspects.Count -gt 0) { $unexplained++ ... }
```

  触发条件与实测：把 HEAD 导出到临时目录，只改 `SpectreReplay/Features/VideoRender/Pipeline/VideoEncoder.cs:38`
  的 `private const int ProbeRetryMs = 30_000;` → `15_000;`，重跑
  `powershell -File tools/port-diff-audit.ps1`：

```
[差异] Features\VideoRender\Pipeline\VideoEncoder.cs  token 我们 1766 / 上游 1713
✓ Features\VideoRender\Pipeline\VideoEncoder.cs  →  差异全部有归属
✓ 移植保真度通过：全部差异均可追溯      (exit 0)
```

  同类可假绿的构造（都能在 `$covered` 里留下"不含点号"的残渣）：
  ① 数字：`crf 23`→`28`、`WaitForExit(3000)`→`100`、超时/重试/预算常量；
  ② 裸标识符：把 `bpm` 改成 `bpm2`、局部名/字段名拼错但类型仍能编译（`_cache` vs `_cached`）；
  ③ 运算符：`a - b` → `a + b`、`>=` → `>`（`+`/`-`/`>` 都不是"新名字"）；
  ④ 字符串字面量：日志文案、ffmpeg 参数值里**不含点号**的部分（`"-preset veryfast"` → `"-preset medium"`，
  这一条另有 `libx264 -preset veryfast -crf 23` 整串登记在案，所以整串被登记吃掉）。
- 影响：**静默**。这是本仓唯一挡「抄错上游」的门（`docs/verify.md:115` 明确称它是"真门禁"），
  而移植期最常见的手误 —— 常量抄错、比较方向抄反、加减号抄错 —— 恰好全部落在它的盲区里。
  比"没这个门"更糟的是它给出了"已核验"的结论。
- 建议：把判据从"形状匹配"换成"**逐 token 位置比对**"（`Compare-Object` 默认按值集合比较，
  会丢掉顺序与重复，这本身就是第二重盲区）。最小改法：
  ① 用**行级** diff（`git diff --no-index --word-diff` 或自写 LCS），对每个 hunk 要求登记表
  里有一条 `Pat` 命中该 hunk 的**完整文本**（而不是命中全文件 diff 拼接串的任意位置）；
  ② 对"数字/运算符/字符串"这三类变化，强制要求登记表条目带 `File` 限定，
  并在报告里把该 hunk 原文打出来（现在连改了什么都不显示：上面实测的输出里根本没有 `15_000`）。
- 置信度：高（有可复现实验）。

### F2 [P2] 断言有"打印"和"返回码"两个信道，闸门只读前者，且没有断言行数下限

- 位置：`tools/verify.ps1:146-164`、`tools/mutate.ps1:1918-1934`、`tools/UiPreview/Driver.cs:95-100`
- 证据：

```powershell
# verify.ps1:146-154
$out = & dotnet run -c Release -v q --no-build -- $outDir --click 2>&1
$bad  = @($lines | Where-Object { $_ -match '^\s+✗' })
$good = @($lines | Where-Object { $_ -match '^\s+✓' })
Write-Host ('  断言 ' + ($good.Count + $bad.Count) + ' 条，失败 ' + $bad.Count)
```

```csharp
// Driver.cs:95-100 —— 这个返回值就是进程退出码，但上面两个脚本都不看它
int total = locFail + hardFail + ... + orphanFail;
Console.WriteLine(total == 0 ? "  全部通过" : $"  {total} 项不通过");
return total;
```

  触发条件：任何检查把失败计入返回值、但**没有印出以空白开头 + `✗` 的那一行**（早退 `return 1`、
  印成 `Console.WriteLine("✗ …")`（顶格）、或把 `✗` 写在多行字符串里），
  `verify.ps1` 就只看到别的检查的 ✓ 行，印「断言 N 条，失败 0」并判绿；`mutate.ps1:1948`
  同理会把该变异判成「没有让闸门变红——这条断言是假的」，从而**冤枉判据本身**。
  今天 26 个 `*Check` 恰好都规范地印了 `✗`（我逐个 grep 过），所以是"靠约定活到现在"。
- 影响：一条新检查（或一次缩进改动）就能让判据静默失效，且症状是"闸门变绿"或"变异被误判为假"，
  两种都比报错难查。`verify.ps1:159-164` 只防住"0 条断言"，防不住"少了几条断言"。
- 建议：`verify.ps1` 里补 `if ($LASTEXITCODE -ne 0 -and $bad.Count -eq 0) { 判红 }`；
  `mutate.ps1:1918` 之后同样补退出码判定。再补一条**断言总数下限**（如 `-lt 100` 判红，
  数值随轮次上调），因为"删掉一个 Check 的注册"目前只会让数字变小、不会红
  （`PortedCompletenessCheck.NoOrphanChecks` 只防"存在但没注册"，不防"注册被删"）。
- 置信度：高（机制确定；当前无违规者，属结构性缺陷）。

### F3 [P2] 三个只读补丁审计把注释里的用法示例当成真实补丁，计数因此虚高且可能张冠李戴

- 位置：`SpectreReplay/Patch/Shared/SpectrePatch.cs:59-61`（被误读的注释）、
  `tools/harmony-shape-audit.ps1:33-53`、`tools/instance-injection-audit.ps1:48-79`、
  `tools/patch-sig-audit.ps1:40-80`、`tools/result-injection-audit.ps1:52-79`
- 证据：

```csharp
// SpectrePatch.cs:59-61
// 方法级补丁声明，应用器见 SpectrePatchHub：
//     [SpectrePatch(typeof(scrPlayer), "Hit", PatchType.Prefix)]
//     internal static bool Prefix(scrPlayer __instance) { ... }
```

```powershell
# harmony-shape-audit.ps1:33-38, 50-51 —— 读原文、不剥注释；签名正则会命中注释里的那行
$t = [IO.File]::ReadAllText($f.FullName)
if ($t -notmatch 'SpectrePatch\(') { continue }
...
$sm = [regex]::Match($lines[$j], 'internal\s+static\s+([\w\<\>\[\]\?\.]+)\s+(\w+)\s*\(([^)]*)\)')
```

  实测数量对不上：C# 侧 `PatchTargetCheck`（唯一做了**覆盖率自检**的，`PatchTargetCheck.cs:57-75`，
  用剥过注释的 `SrcUtil.Clean` 数声明）印「声明 41 条」；`harmony-shape-audit.ps1` 印
  「共 42 处」。41 + 1 = 42，多出来的那条正是 `SpectrePatch.cs:59-61` 这对注释行拼出来的幽灵补丁
  （属性取自 `//` 行，签名取自下一行 `//` 里的 `internal static bool Prefix(...)`，`Prefix` 返回 bool
  合规 ⇒ 判 OK）。同理 `instance-injection-audit.ps1` 印「共 17 处」而真值是 16
  （幽灵行 `scrPlayer.Hit` 反射到真实例方法，判 OK）。C# 侧 `InstanceInjectionCheck.cs:50-51`
  同样读原文（`File.ReadAllText`，未 `Clean`），所以也报 17 —— 两个实现**一致地错**，
  这正是"注释与代码冲突时冲突本身是发现"。
- 影响：① `tools/verify.ps1:58,60` 与 `docs/verify.md:57` 里的"32 处 / 16 处 / 17 处 / 7 处"
  全部不可引用（真值 42 / 17 / 16 / 8，且 42 与 17 各含 1 条幽灵）；② 幽灵行的 verdict 是"碰巧 OK"，
  一旦有人改写这段示例（例如示例里写上 `MaxVersion = 149`，或补一条 `__instance` 静态目标的**反例**），
  审计会立刻变红且指向一个不存在的补丁，排查方向被带偏；③ 更一般地，"只验存在不验语义"的
  正则在这个仓库里已被反复惩罚（`docs/verify.md:85` 的 `CountClosedArgs` 就是为此加的），
  但四个 PS 审计仍是裸正则。
- 建议：四个 `.ps1` 统一改成先剥注释再扫（把 `deadcode-audit.ps1:38-44` 那三行 `[regex]::Replace`
  抽成共用片段，或直接调用 `deadcode-audit` 的实现）；并且把"解析条数"与"剥注释后的声明条数"
  做成**同一条自检**（照抄 `PatchTargetCheck.cs:57-75`），条数对不上就 `exit 3`。
  同类：`InstanceInjectionCheck.cs:50` 应改成 `SrcUtil.Clean(File.ReadAllText(f))`。
- 置信度：高（41 vs 42 的差值可算、幽灵行可定位）。

### F4 [P2] `MissingRuntimeKeyCheck` 的第三个数据源已经搬走，判定的一半恒为空且无"0 命中"守卫

- 位置：`tools/UiPreview/MissingRuntimeKeyCheck.cs:53-61`、`:66`
- 证据：

```csharp
// ③ Ui\Loc.cs 的页面键也一并算"有定义"（两个表服务不同调用方，互不取代）
string ui = File.ReadAllText(Path.Combine(projectRoot, "Ui", "Loc.cs"));
var uiHits = Regex.Matches(ui, "\\{\\s*\"([a-z][a-z0-9_]*(?:\\.[a-z0-9_]+)+)\"\\s*,");
foreach (Match m in uiHits) definedUi.Add(m.Groups[1].Value);
```

  但键值表早已搬进 `Ui\LocData.cs`（`Ui\Loc.cs:3` 自己写着「文案数据在 LocData，本文件只放选择逻辑」）。
  实测：对 `Ui/Loc.cs` 跑同一个正则，命中 **0** 条；对 `Ui/LocData.cs` 跑 `["key"]` 形状命中 **4550** 条
  （14×325）。于是 `definedUi` 恒为空集，这条判定静默退化成"只查 LocalizationManager"。
- 影响：① 判定覆盖面缩水一半而不报错（"绿着的闸门其实有一项没跑"的又一形态）；
  ② 方向性误报：某个运行时键若只在**界面表**（14 语言）里定义，本检查会把它报成"运行时键没有定义"，
  指向错误结论。当前恰好没有这种键，所以是潜伏的。
- 建议：把第 ③ 段的数据源改成 `Ui\LocData.cs`（正则换成 `\["([a-z][a-z0-9_]*(\.[a-z0-9_]+)+)"\]`），
  并加一句 `if (definedUi.Count == 0) { 印 ✗ + fail++; }`——`deadcode-audit.ps1:33-36`、
  `harmony-shape-audit.ps1:26-29` 都是同一条纪律（"计数为 0 之前先证明自己扫到了"）。
- 置信度：高（正则实测 0 命中，数据源位置有代码自证）。

### F5 [P2] `gen-locdata.ps1` 没有 UTF-8 BOM：PowerShell 5.1 下直接语法错误；且两个生成器产物并不相同

- 位置：`tools/i18n/gen-locdata.ps1:1`（文件头三字节 `E4 BD A0`，无 BOM）、`:101`；
  `tools/i18n/gen-locdata.py:13-16`、`:83`；`tools/i18n/README.md:13`
- 证据（三条独立事实，均实测）：

  ① **无 BOM**（`tools/i18n/gen-locdata.ps1` 是全仓唯一一个"含非 ASCII 但无 BOM"的 `.ps1`；
  其余 13 个 `.ps1` 全带 BOM）。用 PowerShell 5.1 跑它：

```
At ...\gen-locdata.ps1:31 char:1
+ )
+ ~  Unexpected token ')' in expression or statement.
At ...:94 char:14
+ $lines.Add("// 鏈枃浠剁敱 tools/i18n/... 鐢熸垚锛屼笉瑕佹墜鏀广€?* ...")
+              ~  You must provide a value expression following the '/' operator.
exit=1
```

  ② **两个生成器产物不同**（README:13「两个生成器逻辑等价，产物逐字节相同」为假）：
  `.ps1` 产物 428359 字符 / 498887 字节，与仓库里的 `LocData.cs` **逐字节相同**；
  `.py` 产物 428348 字符 / 498876 字节，差异 11 字符，全在头部注释：
  `.py` 写「本文件由 `_gen_locdata.py` 生成」（**这个文件名不存在**，早已改名为 `gen-locdata.py`），
  `.ps1` 写「本文件由 `tools/i18n/gen-locdata.ps1` 生成」。谁最后跑，`LocData.cs` 就带哪个头。

  ③ **`.py` 路径写死**：`ROOT = r"D:\Projects\SpectreReplay"`（`:13`），
  换机器/CI/换个检出目录时，`python tools/i18n/gen-locdata.py` 会写到 `D:\Projects\SpectreReplay`
  ——要么 FileNotFoundError，要么**改了另一份检出**而当前工作副本纹丝不动（"我重新生成了呀？"）。

- 影响：`tools/i18n/README.md:26-28` 的推荐流程在这台机器上（Windows PowerShell）**跑不起来**；
  而 `verify.ps1` 调子脚本用的正是 `powershell`（5.1，`verify.ps1:80`），仓库的既有约定
  （`verify.ps1:2-4`、`docs/verify.md:148`）也是"`tools\*.ps1` 必须带 BOM"。这是仓库自己立的规矩
  被自己破的一处，且破的是唯一一个会**写产品代码**的脚本。
- 建议：给 `gen-locdata.ps1` 补 BOM（与其余 13 个一致）；把 `.py` 的 `ROOT` 改成
  `os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))`；
  两边的头部注释统一成同一句（或干脆让头部写"由 tools/i18n/ 下的生成器生成"），
  然后**在 CI 里加一条 diff 判据**（见 F6）。README:13 的"逐字节相同"要么改成事实、要么改成"产物等价"。
- 置信度：高（BOM 与两次生成的字节数都是实测）。

### F6 [P2] 没有任何判据校验"`LocData.cs` 与 `source.json`/`locales` 同步"——改文案忘生成会全绿

- 位置：`tools/UiPreview/*`（无一处引用 `tools/i18n`，见下）、`tools/verify.ps1:56-105`（审计登记表里没有生成器）
- 证据：全仓 grep `source\.json|gen-locdata`，命中只出现在 `tools/i18n/*` 与
  `HardTextCheck.cs:22`/`README` 的**注释**里，没有任何检查读取 `source.json` 或 `locales/*.json`。
  现有 i18n 判据全部只看**产物**：`LocCheck`（页面键在 14 套表里齐不齐）、
  `LocFallbackCheck`（表间键集对称、回退链）、`LocLanguageCheck`（语言清单覆盖）、
  `LocRuntimeCheck`（键有没有人取）。
- 触发条件：改 `source.json` 里某条 `zh`/`en` 译文（或 `locales/ja.json` 的某条值），**不跑生成器**。
  键集合没变 ⇒ `LocCheck`/`LocFallbackCheck` 全绿 ⇒ `verify.ps1` 全绿，而玩家看到的仍是旧文案。
  反向同理：手改 `LocData.cs` 一个字符也不会被任何判据发现（只有那句注释求你别这么干）。
- 影响：静默。多语言项目里"改了一处译文没生效"是最常见的返工来源，而本项目有 325×14 条。
- 建议：在 `verify.ps1` 里加一节「文案表可重现」——把 `source.json`+`locales` 生成到
  `%TEMP%`，与 `SpectreReplay/Ui/LocData.cs` 做 `Get-FileHash` 比对，不等就判红（并把 diff 的前几行打出来）。
  这一节同时能兜住 F5 的两个生成器不一致问题（选哪个生成器当基准要显式写进脚本）。
- 置信度：高（grep 无命中；生成器可重现性我已独立验证）。

### F7 [P2] 变异回归完全不进 CI，最后一份全量证据停在 10-17 的 76/76，而文档宣称 152/155 条

- 位置：`tools/mutate-regression.ps1`（全文）、`mutate-regression-last.log`（工作区里那份，10-17 14:29）、
  `.github/workflows/build.yml:89-92`（CI 只跑 `verify.ps1 -Ci`）、`docs/verify.md:72-75`
- 证据：`mutate-regression-last.log` 内容只有

```
================ 全量变异回归结果 ================
通过 76 / 76
全部变异按预期工作
```

  而 `tools/mutate.ps1` 现在有 **155** 个唯一标签（我把 `'(MUT-[A-Za-z0-9]+)'\s*\{` 在全文匹配：
  155 处，去重 155；其中 152 处位于行首，另 3 处与 `}` 同行——`docs/verify.md:75` 说的
  "152 条 / 另有 3 块…会数成 153"即指这个；真实值是 155）。也就是说 **79 条变异自加入以来
  没进过任何一次全量回归**。`mutate-regression.ps1:28-29` 的发现正则本身是对的（能发现 155 条），
  问题在于它没人跑、也没进 CI。
- 影响：变异锚点失效的形态是 `exit 2`（锚点命中 0 次/预览器编译不过），它与"断言是假的"在
  `mutate-regression.ps1:61-63` 里分开统计——很好，但如果半年不跑，`exit 2` 会攒成一大堆，
  那时没人分得清哪几条是本次改动造成。`docs/verify.md:94-97` 自己记着"变异锚点会跟着代码改而失效"，
  却没有任何自动机制去发现它。
- 建议：CI 加一步 `./tools/mutate-regression.ps1 -Filter <本轮改动涉及的前缀>`（或每晚全量），
  以及一个**廉价的干跑模式**：只做锚点校验（施加变异 → 断言文件字节确实变了 → 立刻还原，
  不编译不跑套件），这样锚点失效能在几十秒内被抓到。`mutate.ps1` 里已有
  "`ReplaceExact` 命中必须恰好 1 次"的自检，抽出来单独跑即可。
- 置信度：高（日志时间戳与条数都是实测）。

### F8 [P2] 五处「找不到游戏程序集就跳过并返回 0」，而 CI 现在**一定**有 adofai-libs

- 位置：`tools/UiPreview/PatchTargetCheck.cs:30-35`、`tools/UiPreview/InstanceInjectionCheck.cs:31-36`、
  `tools/UiPreview/ReflectionTargetCheck.cs:24`、`:47`、`:114`、`tools/UiPreview/LocLanguageCheck.cs:274`；
  对照 `.github/workflows/build.yml:45-60`（CI 显式 clone adofai-libs 并在失败时 `exit 1`）、`:26`（`ADOFAI_LIBS`）
- 证据：

```csharp
// PatchTargetCheck.cs:30-35
string dll = FindGameAssembly();
if (dll == null)
{
    Console.WriteLine("    · 找不到游戏程序集 Assembly-CSharp.dll，跳过");
    return 0;
}
```

  设计理由写在 `LocLanguageCheck.cs:260-262`：「读不到游戏程序集时打印「跳过」并**不判红**
  ——CI 里可能没装游戏」。这条理由在 `build.yml:45-60` 落地后**已不成立**：CI 现在把 adofai-libs
  clone 到 `<ws>/adofai-libs` 并设了 `ADOFAI_LIBS`，克隆失败那一步直接红。
- 影响：CI 里 `ADOFAI_LIBS` 一旦写错（或 clone 到别的目录名），
  **7 项里最硬的那几项（补丁目标预检 / 反射目标 / __instance / 语言枚举桩）会集体跳过并判绿**，
  而恰好这些是"改了游戏 API 才会红"的那一类——最需要它们的时刻正是 CI。
  与 `docs/verify.md:33` 的"不登记就是孤儿检查，一次都不跑而闸门只会更绿"是同一形态，只是这次是"跳过"。
- 建议：CI 模式下（`ADOFAI_LIBS` 已设）找不到程序集必须判红；本地可选 `-SkipGameAsm`。
  最小改法：`if (dll == null) { if (Environment.GetEnvironmentVariable("ADOFAI_LIBS") != null) { ✗; return 1; } · 跳过; return 0; }`，
  五处同一改法。
- 置信度：高（跳过分支持代码可读；CI 配置可读）。

### F9 [P3] `-SkipBuild` 是死参数；`-SkipDeploy` 只跳过部署动作、不跳过 sha256 比对（与文档口径不符）

- 位置：`tools/verify.ps1:15`（声明）、`:107`（唯一一处构建，无条件执行）、`:182-211`；
  `docs/verify.md:9-13`
- 证据：全文只有 `:15` 出现 `$SkipBuild`，`:124`/`:168`/`:173`/`:182`/`:215` 分别守卫其余四项，
  而 `Step '主项目构建'` 没有守卫。`:182` 的 `if (-not $SkipDeploy)` 只包住 deploy 调用，
  随后的 `:200-211` 仍然比对 `bin\Release\SpectreReplay.dll` 与线上 dll 的 sha256。
- 影响：① 想跳过构建的人会照跑一遍（小事）；② 更要紧的是"没部署过"的机器上，
  按文档用 `-SkipDeploy` 仍会得到 `✗ 线上 dll 不存在` 而以 exit 2 收场 —— 我实测就是这样
  （本机 `Mods\SpectreReplay` 为空）。文档说 `-SkipDeploy`「跳过部署那一节」（`docs/verify.md:9-10`），
  与行为不符，容易让人误判"闸门坏了"。
- 建议：`Step '主项目构建'` 前加 `if (-not $SkipBuild) { ... }`；把 `-SkipDeploy` 的语义改成
  "整节跳过"，或新增 `-SkipDeployCheck` 并同步改 `docs/verify.md:9-13`。
- 置信度：高。

### F10 [P3] 计数与事实漂移：断言 110 vs 文档 97/98；Unity 版本；SDK 要求；审计标签

- 位置：`docs/verify.md:25`（「当前打印 97 条断言 0 失败」）、`:61`（「总数 97 个 ✓ 行」+ 文中「升到 98」）、
  `README.md:122`、`README_zh.md:122`、`README.md:130`、`tools/verify.ps1:58-63`
- 证据：
  - 我实跑 `dotnet run -c Release`：**110 ✓ / 0 ✗**（`README.md:126` 未给数字，`docs/verify.md` 给的是 97/98）。
    `:28` 已经自我提醒"这个数字会随轮次变"，但两处正文数字仍是旧的，而且 `:25` 与 `:61` 互相矛盾（97 / 98）。
  - `README.md:122` 与 `README_zh.md:122` 写 **Unity 6000.3.21f1**；实际安装的游戏 exe 是
    `6000.3.25f1_e1dba0a9aba4`，`docs/audit/README.md:11` 也写 6000.3.25f1 —— 三处里两处新一处旧。
  - `README.md:130` 写「.NET SDK 7.0 or newer; the test harness targets `net10.0-windows`」——
    自相矛盾（net10 目标必须 SDK 10），CI 也是 `dotnet-version: 10.0.x`（`build.yml:83`）。
  - `verify.ps1:58-60` 的标签是**硬编码**的旧数字：`Harmony 补丁签名形状（32 处）`（真值 42）、
    `__instance 只对实例方法（16 处）`（审计印 17，真值 16，见 F3）、`普通形参与目标同名同型（7 处）`（真值 8）。
    这些标签是人在闸门里唯一能看到的信息，所以错得最危险。
- 影响：P3（不影响行为），但"引用前先跑一遍"的忠告已经在文档里出现两次，说明这类漂移反复发生。
- 建议：`verify.ps1` 的标签不要写死条数，改成运行时从子脚本输出的 `共 N 处` 抓取（`:85` 那个
  summary 正则已经能抓到 `共 \d+ 个`，把 `处` 加进去即可）；`docs/verify.md:25/61` 合并成一句
  并标明"以实跑为准"；README 两处 Unity 版本同步成 6000.3.25f1；`:130` 改成 "SDK 10.0 or newer"。
- 置信度：高（数字全部实测）。

### F11 [P3] `info.json` 的 `GameVersion` 与实机版本不一致；`repo.json` 的 `DownloadUrl` 指向 HTML 页面

- 位置：`SpectreReplay/info.json:9`、`:11`、`repo.json:6`
- 证据：

```json
"Version": "0.1.0",  "GameVersion": "2.7.0",
"Repository": "https://raw.githubusercontent.com/HitMargin/SpectreReplay/master/repo.json"
```
```json
"DownloadUrl": "https://github.com/HitMargin/SpectreReplay/releases/latest"
```

  实机 `Application.version` 是 **3.4.0**（`A Dance of Fire and Ice_Data\globalgamemanagers` 头部
  的版本串 `1.0 / 1.0 / 3.4.0`，与 `scrVersionText.cs:12` 的 `v{Application.version}` 用法一致），
  而 `Release.releaseNumber = 150`（`adofai-libs/source/Assembly-CSharp/Releases.cs:3`）。
  `Main.cs:83-84` 的 `Configure(..., 141, 150)` 表示"最低支持 r141、已验证到 r150"，
  与 `info.json` 的 `GameVersion: 2.7.0` 不是同一个口径（r141 ≈ 2.7.0 时代，r150 已是 3.4.0）。
- 影响：`GameVersion` 在 UMM 里是展示字段（上游 Spectre 的 `info.json` 干脆不写这个字段），
  玩家会看到"为 2.7.0 构建"；若将来 UMM 用它做兼容门，整包会被判不兼容。
  `DownloadUrl` 指向 `/releases/latest`（HTML 页）而非 zip 资产：UMM 的 `ParseRepository` 只把它
  存进 `ModEntry.NewestVersion`/下载链接，玩家点"更新"拿到的是一个网页；若 UMM 直接下载该 URL，
  会得到一个 HTML 文件。置信度：**中**——需要 UMM 0.33 的 `CheckModUpdates`/`ParseRepository`
  IL 才能确认"点了更新会怎样"（`Pages.cs:1316-1320` 记的 IL 结论只覆盖了版本比较，没覆盖下载行为）。
- 建议：`GameVersion` 改成 `3.4.0`（或删掉该字段，与上游对齐）；`repo.json` 的 `DownloadUrl`
  改成具体资产 URL（`.../releases/download/v0.1.0/SpectreReplay-v0.1.0.zip`），
  这样 `build.yml:168-178` 打出来的包名与它会一一对应。
- 置信度：GameVersion 不一致=高（版本串实测）；DownloadUrl 影响=中。

### F12 [P3] `mutate.ps1` 被硬中断时会留下变异态源码，备份只在不带指引的 `%TEMP%` 里

- 位置：`tools/mutate.ps1:68-76`（`Backup` 写 `$env:TEMP\mut_<Which>_<tag>`）、`:100-124`（`Cleanup`）、
  `:126-130`（`trap`）
- 证据：`trap` 只能捕获**终止性错误**并调用 `Cleanup`；`Ctrl+C`、关窗口、被 `kill`、断电都不会走 `trap`，
  也不会走脚本末尾各 `exit` 分支前的手工 `Cleanup`。此时产品源码停在变异态，
  唯一副本是 `%TEMP%\mut_MUT-XXX_Features__Replay__Codec__ReplayEncoder.cs` 这种文件名，
  而脚本里**没有任何"上次有未还原的变异"检查**，也没有把备份路径印出来。
- 影响：源码停在**故意注入的缺陷**上，且下一次构建/提交会把它带上。
  `mutate-regression.ps1` 连跑 155 条时被打断的概率不低。
- 建议：`Backup` 同时写一份 `%TEMP%\mut_pending.json`（记录 Rel/备份路径），
  脚本启动时若发现该文件存在就先报错并要求 `-Recover`；或改用 `try/finally` 包住整个 `switch`。
- 置信度：中（机制确定；未实际制造硬中断来验证，避免污染工作区——这也是为什么只报"机制"）。

### F13 [P3] 审计登记表没有反向断言：新增 `tools\*audit.ps1` 不登记不会被发现

- 位置：`tools/verify.ps1:56-70`（登记表）、`:73-77`（只校验"登记了但磁盘上没有"）、
  `tools/UiPreview/PortedCompletenessCheck.cs` 的 `NoOrphanChecks`（只管 `tools/UiPreview/*.cs`）
- 证据：登记表的校验方向是单向的——表里有而磁盘没有 ⇒ 判红；磁盘有而表里没有 ⇒ **无判据**。
  `NoOrphanChecks` 守的是 `Driver.cs` 的注册（C# 侧），不守 `verify.ps1` 的审计表。
- 影响：`verify.ps1:46-50` 的注释已经写明这条教训（"四个补丁审计脚本写完后只在当轮手工跑过，
  从没进过闸门…不登记 = 一次都不自动跑 = 等于没写"），但防它的机制只存在于人的记忆里；
  `tools\ffmpeg-probe.ps1`、`tools\convert-probe.ps1` 是**有意**不进闸门的（`docs/verify.md:120-128`），
  所以不能简单地说"磁盘上所有 ps1 都必须登记"。
- 建议：给审计脚本一个可识别的标记（如文件名后缀 `-audit.ps1` 全部必须登记，或脚本首行写
  `# gate: manual` 表示有意手动），然后加一条判据比对磁盘与登记表。
- 置信度：高（单方向校验可读）。

### F14 [P3] `LangVersion=preview` 无必要，且会把构建绑到"当前装的 SDK"

- 位置：`SpectreReplay/SpectreReplay.csproj:6`
- 证据：产品代码里没有用到任何 preview 特性（grep `record`/`required`/`init;`/`scoped`/`file class`
  在 `SpectreReplay\` 下 0 命中；命中的 7 处全是 `params`，C# 1.0 就有）。
- 影响：`preview` 的语义随 SDK 变（本地 10.0.402、CI `10.0.x` 都跟最新），一旦某个
  preview 特性升格时改了行为/诊断级别，同一份代码在"换了 SDK 的机器上"编译结果不同；
  而本项目把 `netstandard2.1` + `DisableImplicitFrameworkReferences` + 手工 31 个 `HintPath`
  已经做得很克制，这一处是唯一一个"随环境漂"的开关。
- 建议：改成 `<LangVersion>10.0</LangVersion>`（file-scoped namespace 的最低版本），
  或 `latest`（语义固定到已发布版本）。
- 置信度：高（特性扫描为 0）。

## 已核查但判定无问题（避免下轮重复劳动）

- `tools/i18n/locales/*.json`（12 个）+ `source.json`：JSON 全部合法；`source.json` 325 键、无重复、
  字段固定 `key/zh/en`；12 个 locale 的 `entries` 各 325 条，**与 source 的键集缺失 0、多余 0**。
  与 `LocData.cs` 的 14 块 × 325 键对得上（4550 条）。
- `SpectreReplay/Ui/LocData.cs`：UTF-8 **带 BOM**、**LF**（CRLF=0）；用 `gen-locdata.ps1` 在临时目录
  重跑（pwsh 7）产出的文件与仓库里那份**逐字节相同**（498887 B）——生成器可重复，数据没漂。
- 7 个只读审计在 **HEAD** 上全过（我把 `git archive HEAD` 导出到临时目录单独验证：
  `port-diff-audit` exit 0「✓ 移植保真度通过」，其余 6 个 exit 0）。工作区里那批 `M`（未提交）
  文件是**别人在途的改动**（审计期间从 3 个涨到 6 个：`VideoEncoder.cs`/`VideoRenderJob.cs`/
  `MainThread.cs`/`ReplayDecoder.cs`/`ReplayPlayer.cs`/`Main.cs`），它们会让 port-diff 报
  「未登记差异」——那不是 HEAD 的状态，也不是本次审计的结论。**教训（建议写进 `docs/verify.md`）：
  引用\"闸门全绿\"前必须先 `git status --porcelain` 为空**，否则同一个命令在几分钟内可以给出
  两个相反的结论；我这次就先报错了一次，`port-diff-audit` 没有任何\"工作区脏\"提示可以拦住它。
- 离线套件：110 ✓ / 0 ✗（`dotnet build` 0 warning 0 error；`dotnet run` 4.9s）。
  `.tmp_verify_baseline.log`（10-24 15:22，`-Ci`）确为「全部通过」，与我的独立复核一致。
- 仓库卫生：`bin/`、`obj/`、`.vs/`、`.tmp_*`、`verify-last.log`、`mutate-regression-last.log`、
  `_frames/`、`_msg*.txt` 均在 `.gitignore` 里；`git ls-files` 里**没有任何** `bin/`/`obj/` 路径
  （`tools/UiPreview` 32 个跟踪文件全是源码 + `fixtures/v2-container.bin`）；根目录那 8 个 `.tmp_*`
  与两个 `*-last.log` 都被忽略，属工作区残留而非误入库。
- `.gitattributes:1` 的 `* text=auto eol=lf`：不会剥掉 BOM（git 的 text 规范化只动 CRLF），
  所以"BOM 约定"与 LF 约定可以共存；`.ps1` 行尾为 LF 对 PowerShell 5.1/7 都无害。
- `SpectreReplay.csproj`：`Private=false` 在 `ItemDefinitionGroup`（`:44-49`）与每条 `Reference`
  上都写了（双重保险）；31 个 `HintPath` 全为相对路径 `..\..\adofai-libs\`，
  与 `build.yml:17-24` 的工作区布局（`<ws>/SpectreReplay` + `<ws>/adofai-libs`）自洽；
  `DisableImplicitFrameworkReferences=true` + 显式 `netstandard.dll`/`mscorlib` 引用能编过（实测 0 error）；
  `info.json` 走 `CopyToOutputDirectory=PreserveNewest`（`:40-42`）所以产物里有它，
  `build.yml:97-108` 的断言与之一致。
- `SpectreReplay.slnx`：只含主项目（`SpectreReplay/SpectreReplay.csproj`）。这是**有意**的
  ——`UiPreview` 是 `net10.0-windows` 且反向链接主项目源码，`verify.ps1:124-166` 单独构建它，
  `verify.ps1` 的存在理由（`:8-12`）正是"两边源码集不同、可能预览器绿而主项目不编译"。
  故不报"解决方案漏项目"。
- `.github/workflows/build.yml`：`-Ci` 只跳过两项依赖本机安装的检查（`:89-92` + `verify.ps1:173/215`），
  其余全跑；`Assert build output exists`（`:97-108`）用 `Write-Error` + `exit 1` 在 upload 之前判红
  （修掉了"upload-artifact 找不到文件只发 warning"的历史坑）；release 阶段的
  `info.json Version` vs tag 比对（`:147-164`）覆盖了预发布语义。
  **但**：CI 不做 `dotnet build` 的独立退出码判定——它整条链路都走 `verify.ps1`，
  所以 F2（只看 `✗` 行）同样作用于 CI。
- `tools/ffmpeg-probe.ps1` / `tools/convert-probe.ps1`：按 `docs/verify.md:120-128` 属**有意手动跑**
  （要真 ffmpeg），不进闸门是设计而非遗漏；本次未执行（会调外部编码器，且与本轮审计目标无关）。
- `tools/deadcode-audit.ps1` 当前「需人工判定 0 个」，`exit $needReview.Count` 与
  `verify.ps1:83` 的 `-eq 0` 判定一致；其白名单按**方法名**全局生效（`Save`/`Has`/`IsPerfect` 等）
  是个已记录的裁决（`:80-87` 逐条写了理由），故不另报。

## 版本适配观察

- **Unity 版本**：实机 Unity **6000.3.25f1**（exe `ProductVersion=6000.3.25f1_e1dba0a9aba4`），
  而 `README.md:122`/`README_zh.md:122` 与 `port-diff-audit.ps1:224` 仍写 6000.3.21f1。
  与本轮相关的实际形状：`AudioRenderer.Render` 的"容器必须精确等于本帧样本数"这条结论
  （`README.md:109-113`、`port-diff-audit.ps1` 里 2026-10-16 那条登记）是在 6000.3.21f1 上得到的，
  现在引擎升到 .25f1 后**没有重新验证过**的那部分应记为待确认项（离线套件不覆盖真音频捕获，
  `docs/verify.md:130-137` 也承认只有回放/渲染最终效果验不了）。
- **游戏版本**：`Releases.releaseNumber = 150`；`GameVersion.cs:22` 先找 `GCNS.releaseNumber`
  再回落 `Releases`（该版本两个类都在，`GCNS.cs` 里没有 `releaseNumber` 字段 → 实际走 `Releases`）。
  `info.json` 的 `GameVersion: "2.7.0"` 与实机 `Application.version = 3.4.0` 不同代（见 F11）。
  `PatchTargetCheck.cs:25` 把 `GameRelease` 写死成 `150`，与 `Main.cs:84` 的已验证上界一致；
  但它是**第二份**写死的版本号（另一份在 `Main.cs`），升版本时要同时改两处——建议让
  `PatchTargetCheck` 从 `Releases.releaseNumber` 反射读取（它本来就已经把 `Assembly-CSharp` 载入了）。
- **netstandard2.1 + 手写引用集**：在 Unity 6 + Mono 上工作正常（0 error），
  但 `DisableImplicitFrameworkReferences=true` 意味着**新增一个 BCL 类型就可能要手工补 DLL**
  （`System.IO.Compression` 就是为此加的，`:78-85` 有注释）。这条不是缺陷，是升级时要记住的代价：
  编译器不会提示"你少了个引用集"，只会报某个类型不存在。