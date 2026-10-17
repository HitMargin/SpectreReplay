<#
  用 round 58 确立的方法：把两边源码剥注释归一化后比对，找出「说不出理由的差异」。
  只处理两边都有的同名文件（移植保真度核验）。
#>
$ErrorActionPreference = 'Stop'
$ours   = 'D:\Projects\SpectreReplay\SpectreReplay'
$theirs = 'D:\Projects\Spectre\Spectre'
if (-not (Test-Path $ours))   { Write-Output "✗ 源目录不存在：$ours";   exit 3 }
if (-not (Test-Path $theirs)) { Write-Output "✗ 源目录不存在：$theirs"; exit 3 }

$ourFiles = @(Get-ChildItem $ours -Recurse -File -Filter *.cs -ErrorAction Stop |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.FullName -notmatch '_legacy_srp' })
if ($ourFiles.Count -eq 0) { Write-Output "✗ 没扫到任何 .cs"; exit 3 }
Write-Output "我们的文件数 = $($ourFiles.Count)"

function CodeOnly2([string]$t) {
    $t = [regex]::Replace($t, '(?m)^\s*//.*$', ' ')
    $t = [regex]::Replace($t, '/\*.*?\*/', ' ', 'Singleline')
    $t = [regex]::Replace($t, '\s+', ' ')
    $t = $t -replace 'SpectreReplay\.', '' -replace 'Spectre\.', ''
    $t = $t -replace 'namespace SpectreReplay;', '' -replace 'namespace Spectre;', ''
    return $t.Trim()
}

$matched = @()
$oursOnly = @()
foreach ($f in $ourFiles) {
    $rel = $f.FullName.Substring($ours.Length + 1)
    $t = Join-Path $theirs $rel
    if (Test-Path $t) { $matched += [PSCustomObject]@{ Rel = $rel; Ours = $f.FullName; Theirs = $t } }
    else { $oursOnly += $rel }
}
Write-Output "上游也有同名文件的 = $($matched.Count)"
Write-Output "我们独有的 = $($oursOnly.Count)"
if ($oursOnly.Count -gt 0) {
    Write-Output "── 我们独有（这些是新建的，不是移植，无需比对）──"
    $oursOnly | Sort-Object | ForEach-Object { "  " + $_ }
}

Write-Output ""
Write-Output "=== 逐个比对（剥注释归一化后）==="
$identical = 0; $diff = 0
foreach ($m in ($matched | Sort-Object Rel)) {
    $a = CodeOnly2 ([IO.File]::ReadAllText($m.Ours))
    $b = CodeOnly2 ([IO.File]::ReadAllText($m.Theirs))
    if ($a -eq $b) { $identical++ }
    else {
        $diff++
        $la = ($a -split ' '); $lb = ($b -split ' ')
        $d = Compare-Object $lb $la
        $extra = @($d | Where-Object { $_.SideIndicator -eq '=>' } | ForEach-Object { $_.InputObject })
        $missing = @($d | Where-Object { $_.SideIndicator -eq '<=' } | ForEach-Object { $_.InputObject })
        Write-Output "  [差异] $($m.Rel)  token 我们 $($la.Count) / 上游 $($lb.Count)"
        if ($extra.Count -gt 0)   { Write-Output ("       我们多出: " + (($extra   | Select-Object -First 12) -join ' ')) }
        if ($missing.Count -gt 0) { Write-Output ("       我们缺少: " + (($missing | Select-Object -First 12) -join ' ')) }
    }
}
# ── 有意偏离上游的登记表（round 59 立的约定）────────────────────────────
# 每处差异都必须能挂到一条记录上（round 编号 / 原因）。**挂不上 = 抄错，必须修。**
# 匹配方式：子串命中"我们多出的 token"里任意一条即算已登记。
# 新增一处"有意改动上游"的修改时，必须在这里加一行，否则闸门会红。
$knownDeviations = @(
    @{ Pat = '[SpectreReplay]';                       Why = 'round 31 日志前缀（与真实 Spectre 模组区分，铁律⑤靠它）' }
    @{ Pat = 'Enum.IsDefined';                        Why = 'round 18 Enum.TryParse 数字串陷阱' }
    @{ Pat = 'StateFail';                             Why = 'round 43 States 魔数改具名常量' }
    @{ Pat = 'ReadableExtensions';                    Why = 'round 16 回放列表漏两种可读格式' }
    @{ Pat = 'libx264 -preset veryfast -crf 23';      Why = 'mov 复用 movenc 且拒绝 VP9，必须重编码' }
    @{ Pat = 'volatile';                              Why = '跨线程字段可见性（pumpFailure 等 9 处）' }
    @{ Pat = 'conductor,';                            Why = 'round 25 静态方法上 __instance 无效' }
    @{ Pat = 'conductor == null';                    Why = 'round 58 丢键修复（先出队后解引用）' }
    @{ Pat = '(conductor null)';                     Why = 'round 58 同类守卫：录制侧 ReplayRecorder 补上回放侧早就有的导体判空（漏了会 NRE 灌进游戏 Won_Update）' }
    @{ Pat = 'conductor null || conductor.song';     Why = 'round 58 AsyncKeyRecorder 的导体判空（先出队后解引用会整批丢键）' }
    @{ Pat = 'global using PatchShared';              Why = '我们自写补丁层，接线方式与上游不同' }
    @{ Pat = 'RenderOn';                              Why = '渲染总开关（原先绑 ReplayOn 会连带关录制）' }
    @{ Pat = 'rendervideo.disabled';                 Why = 'RenderOn 守卫的提示文案' }
    @{ Pat = 'Settings.KeybdSoundRecordActive';      Why = 'round 49 键盘音三项接线的连带改动' }
    @{ Pat = 'pendingTick) == 0.0';                  Why = 'round 早 键盘音起始时间戳补真值' }
    @{ Pat = 'System.Collections.Generic;';          Why = 'using 区按我们自己的需要裁剪' }
    @{ Pat = 'Load() 进入';                          Why = 'round 19 Load() 启动行（铁律⑤地基）' }
    @{ Pat = 'OnGui.OnGUI';                          Why = '我们自写的 UI 装配，上游是 Quartz 壳' }
    @{ Pat = 'PatchShared;';                         Why = '命名空间不同（SpectreReplay）' }
    @{ Pat = 'namespace SpectreReplay';              Why = '命名空间不同' }
    @{ Pat = 'double.NaN';                           Why = '渲染时钟的 NaN 哨兵（round 早 钉时间轴）' }
    @{ Pat = 'Debug.Log';                            Why = 'round 31 日志前缀改名的连带面（Debug.Log/LogWarning/LogError 全部改前缀）' }
    @{ Pat = 'ReplayConstants.Extension';            Why = 'round 16 ReadableExtensions：扩展名改为从常量集中取' }
    @{ Pat = 'VideoEncoder.OutContainer.Mov';        Why = 'mov 分支必须重编码（movenc 拒 VP9）' }
    @{ Pat = 'Patch_ConductorUpdate.StateWon';       Why = 'round 43 States 魔数改具名常量' }
    @{ Pat = 'StateFail';                            Why = 'round 43 States 魔数改具名常量' }
    @{ Pat = 'tickConductor';                        Why = 'round 早 键盘音起始时间戳补真值（局部名）' }
    @{ Pat = 'AudioRecorder.Instance.IsRecording';   Why = 'round 早 键盘音补真值的判据之一' }
    @{ Pat = 'KeybdSoundStartTick';                  Why = 'round 早 键盘音起始时间戳字段' }
    @{ Pat = 'HasKeybdSound';                        Why = 'round 早 键盘音链路的连带字段' }
    @{ Pat = 'songposition_minusi';                  Why = 'round 早/mov 渲染钉时间轴锚在它上面' }
    @{ Pat = 'LocalizationManager.GetLocalizedText'; Why = '我们的提示走本地化管理器（上游用 Quartz 自己的）' }
    @{ Pat = 'NotifType.Warning';                    Why = 'TriggerMessage 的通知等级枚举' }
    @{ Pat = 'Application.quitting';                 Why = '退出时落盘（round 早 加）' }
    @{ Pat = 'modEntry.On';                          Why = 'UMM 生命周期回调按我们的 UI 装配接线' }
    @{ Pat = 'PatchManager.Initialize';              Why = '我们自写的补丁层初始化（上游是 PatchLoader）' }
    @{ Pat = 'SpectrePatchHub.LogTag';               Why = '日志前缀统一为 SpectreReplay' }
    @{ Pat = 'SpectrePatchHub.ApplyFailureCount';    Why = '补丁挂载失败计数（我们自写补丁层的能力）' }
    @{ Pat = 'Ui.OnGui.FlushConfig';                 Why = '关窗/退出时统一收口落盘' }
    @{ Pat = 'Settings.CalibAdviceOn';               Why = 'ResultsPlus/CalibAdvice 开关在 Main.cs 注册' }
    @{ Pat = 'Settings.ResultsPlusOn';               Why = '同上' }
    @{ Pat = 'VideoRender.PinnedSongPosition';       Why = '渲染时钟钉时间轴（round 早）' }
    @{ Pat = 'System.Collections.Generic.List';      Why = 'using 区按我们自己的需要裁剪' }
    @{ Pat = 'UMM';                                  Why = '上游用 Quartz 壳，我们用 UMM 自带 OnGUI' }
    @{ Pat = 'data.doubles';                         Why = 'round 早 键盘音起始时间戳写在 doubles 分区' }
    @{ Pat = 'conductor.';                           Why = 'round 25 静态方法上 __instance 无效，改具名 conductor' }
    @{ Pat = 'Ui.OnGui.';                            Why = '我们的 UI 装配入口（上游是 SpectreUiPages / Quartz）' }
    @{ Pat = 'PatchShared.GameVersion.Release';      Why = '我们自写补丁层读游戏版本的方式' }
    @{ Pat = 'Ui.OnGui.PatchFailures';               Why = '补丁挂载失败在设置页展示（我们自写补丁层）' }
    @{ Pat = 'song.pitch';                           Why = 'round 25 具名 conductor 后的连带引用（conductor.song.pitch）' }
    @{ Pat = 'ex.Message';                           Why = 'round 31 日志前缀改名的连带面（异常信息拼进日志）' }
    # ── 2026-10-12 RenderClock transpiler 静默让位 ──────────────────
    # 上游在 replaced == 0 时一律抛「game Update likely changed」。我们改了：
    # 先物化指令列表，探测是否已有别的 mod 的 RenderClockSource 调用，
    # 是则静默让位（不置 TranspilerReady，渲染走 audio-cursor 兜底）。
    # 背景：Spectre 与 SpectreReplay 同时启用时，Spectre 的同类 transpiler
    # 先占了 scrConductor.Update，我们只会得到 replaced == 0，而那句诊断
    # 把原因说成游戏改了 Update——排查方向整个被带偏。
    # （注释里别用 ASCII 双引号：PowerShell 在注释中仍会把它当字符串定界符，
    #  引号不成对就会报 Missing closing quote / subexpression。）
    @{ Pat = 'var body';                             Why = '2026-10-12 静默让位：指令必须先物化才能探测（foreach+yield 是惰性的）' }
    @{ Pat = 'body.AddRange';                        Why = '同上：物化指令列表' }
    @{ Pat = 'ci.operand';                           Why = '同上：探测已被别的 mod 替换过的指令' }
    @{ Pat = 'mi.DeclaringType';                    Why = '同上：按声明类型名认出别的程序集那份 RenderClockSource' }
    @{ Pat = 'Assembly.GetName';                    Why = '同上：日志里报出是哪个 mod 接管的' }
    # ── 2026-10-16 离线渲染音频捕获从不成功（成片无音轨）──────────────────
    # 真因：上游 c36e7b3 把 AudioRenderer.Render 的原生容器也改成 4096 对齐
    # 的分桶（长度 ≥ 本帧样本数），假设 Unity 按容量足够判定。实测（Unity
    # 6000.3.21f1）容器偏大时 Render 仍返回 true，但捕获就此停住——下一帧
    # GetSampleCountForCaptureFrame 恒为 0，于是静默→重启，每次 Start() 只
    # 换来一帧，三次预算用尽即判死。
    # 证据：上游成片在 c36e7b3 之前全部带音轨（09-19 及更早共 16 个），之后
    # 再无一次成功；10-16 日志里上游自己渲染同样失败。
    # 修法：native 精确等于 floats，分桶只留给托管数组（那才是 GC/LOH 来源）。
    # （注释里别用 ASCII 双引号：PowerShell 在注释中仍把它当字符串定界符。）
    @{ Pat = 'AudioRenderer.Render';                Why = '2026-10-16 混音轨原生容器必须精确尺寸（分桶会让捕获静默停住）' }
    @{ Pat = 'native.CopyTo';                       Why = '同上：数据落在精确容器里，拷贝跟着实际渲染目标走' }
    @{ Pat = 'native.IsCreated';                    Why = '同上：原生容器按需精确重建' }
    @{ Pat = 'native.Length';                       Why = '同上：容器长度必须等于本帧样本数' }
    # ── 2026-10-17 移除 CustomJudge 特性（用户裁决）────────────────────
    # 用户原话：「CustomJudge 并不属于replay和视频渲染的部分啊」。判定改写整套
    # （档位/状态/档案/改判定的补丁）都不是本项目的事，已整体删除。
    # 但判定窗口**数学**是 replay 真需要的（散点图按原版窗口归一化着色、找不到
    # 游戏函数时兜底），故摘出为 Features\Compat\MarginBoundary.Raw。
    # 上游那边这个函数叫 CustomJudge.BoundaryRaw。
    @{ Pat = 'MarginBoundary.Raw';                  Why = '2026-10-17 CustomJudge 移除：窗口数学摘到 Compat（replay 仍需要）' }
    @{ Pat = 'Features.Compat.MarginBoundary.Raw';  Why = '同上：调用点改指新家' }
    @{ Pat = 'Features.Compat';                    Why = '2026-10-17 CustomJudge 移除：窗口数学落到 Compat 命名空间（上游在 Features.CustomJudge）' }
    @{ Pat = 'perfectWins.Add';                     Why = '同上：散点图完美窗口（原调 CustomJudge.BoundaryRaw）' }
    # -- 2026-10-17 RenderClock 做成实验性开关（用户要求）------------------
    # 上游这条 transpiler 是无条件常开的；我们把它挂到 FeatureKeys.RenderClock
    # 上，默认关，由玩家在渲染页自己打开。差异只在这一层接线，
    # 让位逻辑/时间源替换本身与上游一致。
    @{ Pat = 'FeatureKeys.RenderClock';              Why = '2026-10-17 渲染时钟做成实验性开关（默认关）' }
    @{ Pat = 'Settings.RenderClockOn';               Why = '同上：开关的 Settings 字段与谓词' }
    @{ Pat = 'SpectrePatchHub.SetFeatureToggl';      Why = '同上：Main 里注册该 Feature 的谓词' }
    @{ Pat = 'PatchType.Transpiler';                 Why = '2026-10-17 Attribute 表达式的连带 token（开关接线本身）' }
    @{ Pat = 'Settings.ReplayOn';                    Why = '2026-10-17 渲染时钟谓词与回放总开关相乘（ReplayOn && RenderClockOn）' }
    # -- 2026-10-17 渲染作业的就绪判断与开关一起判 --------------------------
    # 上游只判 TranspilerReady。我们加了 Feature 开关，而该字段只置 true
    # 不置回 false（补丁卸载不清它），所以必须 AND 上开关，否则
    # '开→渲染→关→再渲染' 会继续走确定性轨、两条时间轴混用。
    @{ Pat = 'RenderClockSource.TranspilerReady';  Why = '2026-10-17 就绪判断必须 AND 开关（TranspilerReady 卸载后不清）' }
    @{ Pat = 'pureWins.Add';                        Why = '同上：散点图纯判窗口' }
)

Write-Output ""
Write-Output "=== 汇总：完全一致 $identical / 有差异 $diff ==="

# ── 可追溯性判定：**每一个**多出的 token 都要挂上登记 ──────────────────
# 第一版判的是"有没有任一条登记命中"⇒ 只要有 [SpectreReplay] 前缀这一条命中，
# 同文件里注入的任何漂移都会被判成"已登记"（实测 MUT-PORT1 就是这么漏过的：
# 加了 7 个 token 的假漂移，仍报 ✓ 1 条已登记）。这是"假通过"的又一种形态——
# **用"存在一个解释"代替"每个差异都有解释"**。现在改成逐 token 判定。
$unexplained = 0
foreach ($m in ($matched | Sort-Object Rel)) {
    $a = CodeOnly2 ([IO.File]::ReadAllText($m.Ours))
    $b = CodeOnly2 ([IO.File]::ReadAllText($m.Theirs))
    if ($a -eq $b) { continue }
    $extra = @((Compare-Object ($b -split ' ') ($a -split ' ')) |
        Where-Object { $_.SideIndicator -eq '=>' } | ForEach-Object { $_.InputObject })

    # Compare-Object 把**相邻**差异合成一段，所以拿到的不是单 token 而是片段
    # （`(conductor conductor.song null)`、`global using PatchShared`、`StateFail`…）。
    # 判定必须做在**拼接后的差异文本**上，不能逐 token（逐 token 那两版都失败过）。
    #
    # 做法是"覆盖相减"：把该文件的差异文本里所有**已登记模式**出现过的片段抠掉，
    # 再把 C# 关键字忽略；剩下的若还有 `A.B` 点号标识符或 `__x` 这种"新名字"的
    # 形状，就说明有差异没挂上记录。
    # 注意不能用 -like 做匹配：PowerShell 的 -like 会把 [ ] 当字符类通配符，
    # `'__p' -like '*[SpectreReplay]*'` 返回 True，任何含 S 的名字都会被放过。
    $covered = ($extra -join ' ')
    foreach ($k in $knownDeviations) {
        $covered = [regex]::Replace($covered, [regex]::Escape($k.Pat), ' ')
    }
    foreach ($kw in @('if','else','for','foreach','while','do','return','void','const','new','using',
                      'internal','public','private','static','class','struct','enum','namespace','true',
                      'false','null','var','in','out','ref','this','base','try','catch','finally','switch',
                      'case','break','continue','throw','is','as','get','set','value','partial','readonly',
                      'global')) {
        $covered = [regex]::Replace($covered, ('\b' + $kw + '\b'), ' ')
    }
    # 只留"看起来像新造的名字"：点号连接的多段标识符，或 __x 前缀
    $suspects = @([regex]::Matches($covered, '[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+|__\w+') |
        ForEach-Object { $_.Value } | Sort-Object -Unique)

    if ($suspects.Count -gt 0) {
        $unexplained++
        Write-Output "  ✗ 未登记差异：$($m.Rel)"
        Write-Output "       挂不上的名字：$(($suspects | Select-Object -First 14) -join ' ')"
        Write-Output "       ⇒ 若这是有意改动，请在 `$knownDeviations 里加一行（含原因）；若是抄错，修代码。"
    } else {
        Write-Output "  ✓ $($m.Rel)  →  差异全部有归属"
    }
}

Write-Output ""
if ($unexplained -eq 0) {
    Write-Output "✓ 移植保真度通过：全部差异均可追溯"
    exit 0
} else {
    Write-Output "✗ 有 $unexplained 处差异挂不上修改记录（抄错或漏登记）"
    exit 1
}
