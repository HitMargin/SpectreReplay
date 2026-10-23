<#
  用 round 58 确立的方法：把两边源码剥注释归一化后比对，找出「说不出理由的差异」。
  只处理两边都有的同名文件（移植保真度核验）。
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$ours   = Join-Path $repoRoot 'SpectreReplay'

# 上游 Spectre 源码位置：① 环境变量 SPECTRE_UPSTREAM（CI 用）
# ② 仓库同级的 Spectre\Spectre ③ 历史绝对路径。
# 这条审计是**真门禁**：找不到上游时不能静默跳过，否则"移植保真度"
# 在 CI 里等于没跑，而闸门只会更绿。
$theirs = if ($env:SPECTRE_UPSTREAM -and (Test-Path $env:SPECTRE_UPSTREAM)) { $env:SPECTRE_UPSTREAM }
          elseif (Test-Path (Join-Path $repoRoot '..\Spectre\Spectre')) {
              (Resolve-Path (Join-Path $repoRoot '..\Spectre\Spectre')).Path }
          elseif (Test-Path 'D:\Projects\Spectre\Spectre') { 'D:\Projects\Spectre\Spectre' }
          else { $null }
if (-not $theirs) {
    Write-Output "✗ 找不到上游 Spectre 源码：设 SPECTRE_UPSTREAM 指到 ...\Spectre\Spectre"
    exit 3
}
if (-not (Test-Path $ours))   { Write-Output "✗ 源目录不存在：$ours";   exit 3 }

$ourFiles = @(Get-ChildItem $ours -Recurse -File -Filter *.cs -ErrorAction Stop |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })
if ($ourFiles.Count -eq 0) { Write-Output "✗ 没扫到任何 .cs"; exit 3 }
Write-Output "我们的文件数 = $($ourFiles.Count)"

# **改名归一化：我方主动改过的类型名，比对前先折回上游名。**
#
# 2026-10-21 把 HitContext → HitSnapshot（12 字段的逐帧状态快照，不含判定结果）。
# 这是一次**纯标识符改名**，wire 格式与行为一字未动。但如果不折回，比对端会
# 把它当成漂移，而且**报出来的位置极具误导性**：上游 Replay DTO 的属性本来
# 就叫 HitContexts，改名前我方 WriteHitContexts(writer, replay.HitContexts) 与上游
# **逐 token 相同**，那几句根本没进差异集；改名后我方变成 HitSnapshots，与上游
# 不再相同，于是整段差异被“撑开”，把同一句里历来无人登记的东西（w.Write 等）
# 一起顶成未登记。也就是说：不折回就只能逐处补登记，而每改一次上下文，补的
# 那批登记就得重排一次——登记表会被一次改名搅成噪声，门禁反而更难判读。
#
# 折回只作用于**我方**文本（上游文本必须保持原样，否则是在改上游）。语义上它
# 和上面第 33 行抹掉 `SpectreReplay.` 前缀是同一类操作：都是把“我方已知的、有意
# 的、非行为性的命名差”先约掉，让剩下的差异只表示真正的功能差异。
#
# 约定：**下面的登记表里 `Pat` 一律写折回后的名字**（即上游形 data.HitContexts，
# 而不是我方源码里的 data.HitSnapshots）。新增改名时，在这里补一行即可。
$renameFold = @(
    @{ Ours = 'HitSnapshot'; Upstream = 'HitContext' }   # 2026-10-21 类型改名，子串折回同时覆盖 HitSnapshots/MagicHitSnapshot/HitSnapshotVersion/ReadHitSnapshots/WriteHitSnapshots
)

function CodeOnly2([string]$t, [switch]$Our) {
    $t = [regex]::Replace($t, '(?m)^\s*//.*$', ' ')
    $t = [regex]::Replace($t, '/\*.*?\*/', ' ', 'Singleline')
    $t = [regex]::Replace($t, '\s+', ' ')
    $t = $t -replace 'SpectreReplay\.', '' -replace 'Spectre\.', ''
    $t = $t -replace 'namespace SpectreReplay;', '' -replace 'namespace Spectre;', ''
    if ($Our) { foreach ($r in $renameFold) { $t = $t -replace $r.Ours, $r.Upstream } }
    return $t.Trim()
}

# **匹配键是文件名，不是相对路径。**
#
# 原先按 `$ours` 下的相对路径去上游同名位置找（`Join-Path $theirs $rel`）。
# 2026-10-19 把 Features 拆进职责子目录（Replay\Codec、VideoRender\Pipeline …）
# 之后，那些文件的相对路径在上游**全部对不上**——它们会被静默归进
# 「我们独有，无需比对」，于是移植保真度门禁对已移动的文件**永久失效**，
# 而闸门只会更绿。这正是本仓库反复记录过的失效形态。
#
# 文件名做键是安全的：已核对本仓 61 个 .cs **无重名**；上游同名文件若有多份
# （如 AssemblyInfo.cs / GlobalUsings.cs），按「路径尾部最长匹配」消歧——
# 拆分只动了我们的目录层级，上游目录结构没变，所以尾部匹配是稳定的。
$theirFiles = @(Get-ChildItem $theirs -Recurse -File -Filter *.cs -ErrorAction Stop |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })
$theirsByName = @{}
foreach ($tf in $theirFiles) {
    if (-not $theirsByName.ContainsKey($tf.Name)) { $theirsByName[$tf.Name] = @() }
    $theirsByName[$tf.Name] += $tf.FullName
}

# **改过名的移植文件要显式登记。** 上游叫 X、我们改叫 Y 时，按文件名查不到 →
# 整份文件落进「我们独有，无需比对」→ 门禁对它**永久失效**，而闸门只会更绿。
# 这与上面 §40-44 的目录搬家是同一种失效形态，只是规模小、更难注意到。
# 键 = 我方文件名，值 = 上游文件名。只登记「类型改名」这类真正的名字差异；
# 纯目录层级差异仍由下面的尾部最长匹配解决，别拿它绕路。
# 别名只负责“让它进比对”。真正的名字差由下面的 $renameFold 在归一化阶段折回，
# 所以 HitSnapshot.cs 折回后与上游 HitContext.cs 内容一致，不会以差异身份出现。
$fileAlias = @{
    'HitSnapshot.cs' = 'HitContext.cs'   # 2026-10-21 HitContext → HitSnapshot
}

$matched = @()
$oursOnly = @()
$aliased = 0
foreach ($f in $ourFiles) {
    $rel = $f.FullName.Substring($ours.Length + 1)
    $lookupName = $f.Name
    if ($fileAlias.ContainsKey($f.Name)) { $lookupName = $fileAlias[$f.Name] }
    $candidates = $theirsByName[$lookupName]
    if ($candidates -and $candidates.Count -gt 0) {
        # 同名多份时取相对路径尾部重合最长的那个
        $best = $candidates[0]
        $bestScore = -1
        foreach ($c in $candidates) {
            $cRel = $c.Substring($theirs.Length + 1)
            $a = $rel -split '[\\/]'
            $b = $cRel -split '[\\/]'
            $score = 0
            for ($i = 1; $i -le [Math]::Min($a.Count, $b.Count); $i++) {
                if ($a[$a.Count - $i] -eq $b[$b.Count - $i]) { $score++ } else { break }
            }
            if ($score -gt $bestScore) { $bestScore = $score; $best = $c }
        }
        if ($lookupName -ne $f.Name) { $aliased++ }
        $matched += [PSCustomObject]@{ Rel = $rel; Ours = $f.FullName; Theirs = $best }
    }
    else { $oursOnly += $rel }
}
Write-Output "上游也有同名文件的 = $($matched.Count)"
if ($aliased -gt 0) {
    # 只报实际用上的那几对，并把「我们→上游」两个名字都打出来：核对时一眼能看出
    # 配对的是哪两个文件，而不是只知道"有 N 个靠改名配对"。
    $pairs = ($fileAlias.GetEnumerator() | ForEach-Object { $_.Key + ' → 上游 ' + $_.Value }) -join '；'
    Write-Output ('  其中按改名配对 = ' + $aliased + '（' + $pairs + '；理由见脚本里 $fileAlias 处的注释）')
}
Write-Output "我们独有的 = $($oursOnly.Count)"
if ($oursOnly.Count -gt 0) {
    Write-Output "── 我们独有（这些是新建的，不是移植，无需比对）──"
    $oursOnly | Sort-Object | ForEach-Object { "  " + $_ }
}

Write-Output ""
Write-Output "=== 逐个比对（剥注释归一化后）==="
$identical = 0; $diff = 0
foreach ($m in ($matched | Sort-Object Rel)) {
    $a = CodeOnly2 ([IO.File]::ReadAllText($m.Ours)) -Our
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
    @{ Pat = 'ADOBase.conductor';                     Why = '官方关卡回放要记真实 BPM：走 conductor 而不是 scnGame.instance（后者在编辑器/重开时点可能为 null，而这段在 try 里，一抛就是整次录制失败）' }
    @{ Pat = 'c.bpm';                                 Why = '同上：conductor.bpm 与 levelData.bpm 是同一个值（scrConductor.SetupConductorWithLevelData 抄的），取不到返回 0' }
    @{ Pat = 'CurrentBpm';                            Why = '同上：把「官方关卡 BPM 恒为 0」的硬编码换成活取值，详情面板才不会显示成问号' }
    @{ Pat = 'global using PatchShared';              Why = '我们自写补丁层，接线方式与上游不同' }
    @{ Pat = 'RenderOn';                              Why = '渲染总开关（原先绑 ReplayOn 会连带关录制）' }
    @{ Pat = 'rendervideo.disabled';                 Why = 'RenderOn 守卫的提示文案' }
    @{ Pat = 'Settings.KeybdSoundRecordActive';      Why = 'round 49 键盘音三项接线的连带改动' }
    # 注：`Settings.Backup`（主保存失败不走备份）的登记在文件末尾那批**带 File = 限定**
    # 的条目里 —— 泛名字必须限定文件，否则等于给全仓开后门。
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
    # 判定窗口**数学**是 replay 真需要的(散点图按原版窗口归一化着色)。
    # 最初摘为 Features\Compat\MarginBoundary.Raw(上游那边叫 CustomJudge.BoundaryRaw)，
    # 2026-10-18 用户问「不能改成完全用游戏自带的代码？」后**连那份复刻也删了**：
    # scrMisc.GetAdjustedAngleBoundaryInDeg 是 public static，复刻里没有自研逻辑，
    # 而「不许被别的 mod 改写」的理由随 r150 上游改挂判定入口而失效。
    @{ Pat = 'scrMisc.GetAdjustedAngleBoundaryInDeg'; Why = '2026-10-18 改用游戏自带窗口边界(删掉本地复刻 MarginBoundary.Raw)' }
    @{ Pat = 'win.Perfect';                         Why = '同上：一次取回四条边结构体，散点图取 Perfect/Pure 两条' }
    @{ Pat = 'GCS.difficulty';                     Why = '同上：难度要作为实参传给游戏函数(复刻版读的是同一个全局，改成显式传参)' }
    @{ Pat = 'win.Pure';                            Why = '同上：从结构体取 Pure 边(复刻版是按枚举分支算一条，现在一次取全四条)' }
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
    # -- 2026-10-18 特殊按键过滤（移植上游 Patch_AllowSpecialKeys）----------
    # 上游这条补丁**没有 Feature 门控**（常开）。我们把它挂到
    # FeatureKeys.SpecialKeys 上，且谓词是「回放开 && 至少勾了一个键」：
    # 这条路径在每帧输入处理上，没勾键的玩家不该为用不到的功能付委托调用。
    # 代价是**必须注册**——未注册的功能按关闭处理，只留一行告警，
    # 现象是「勾了键、回放里照样没有」，与「游戏本来就不录这些键」分不开。
    @{ Pat = 'FeatureKeys.SpecialKeys';             Why = '2026-10-18 特殊按键过滤做成 Feature 门控（上游常开）' }
    @{ Pat = 'Settings.AllowedKeyboardSpecialKeys'; Why = '同上：谓词读勾选集合（勾了才挂补丁）' }
    @{ Pat = 'Settings.AllowedAsyncSpecialKeys';    Why = '同上：异步那一组的勾选集合' }
    # -- 2026-10-18 删掉旧回放文件适配(用户裁定)--------------------------
    # 用户原话：「重构整个回放文件 删掉旧的回放文件适配」。删的是：旧容器
    # (.crpl 旧版 JSON / .crpl2 CRP2 二进制)的读写两条路径 + 魔数 LegacyMagic
    # + 全部 33 处 JSON 字段名映射。保住的是 ReplayCrypto 加解密与 .psprp。
    # 下面两条是这轮删除的连带 token(上游当然还留着那些分支)。
    @{ Pat = 'ReplayConstants.Magic';              Why = '2026-10-18 删旧格式：解码端只认一个魔数(上游还认 LegacyMagic)' }
    @{ Pat = 'raw.Length';                          Why = '2026-10-18 删旧格式：LoadReplay 合一后新增的过短文件判定' }
    # -- 2026-10-18 加密层整体重写(用户要求：换掉固定密钥，用动态派生)--------
    # 旧实现是 SHA256("qwerty") 单次散列当 AES 密钥 + SHA256("potato") 当固定 IV，
    # 两个都是源码里的短字符串 ⇒ 同一份明文每次加密得到相同密文(确定性)，
    # 且密文可被任意篡改而无从察觉。新实现：PBKDF2(HMAC-SHA256, 6万次迭代,
    # 每文件随机 salt) 派生加密密钥与 MAC 密钥两把 + 随机 IV + encrypt-then-MAC。
    # 写侧只产出新格式。**旧上游格式的读侧回落已于 2026-10-19 按用户裁定整体移除**
    # （两个短字串常量 / LegacyKey / LegacyIV / LegacyDerive / DecryptLegacy 全删，
    #  非本模组容器一律拒绝、不做试探性解密），因此下面不再有那几条登记。
    # 这批是 BCL 密码学 API 的名字，不是游戏 API，
    # 不会随游戏版本漂移;逐条登记而不是用一条宽泛串盖住整个文件。
    @{ Pat = 'HashAlgorithmName.SHA256';             Why = '2026-10-18 加密重写：PBKDF2 显式指定 HMAC-SHA256(与默认 SHA1 不同)' }
    @{ Pat = 'RandomNumberGenerator';                Why = '2026-10-18 加密重写：salt 与 IV 每文件随机取(确定性缺陷的根治点)' }
    @{ Pat = 'rng.GetBytes';                         Why = '2026-10-18 加密重写：同上，随机源取数' }
    @{ Pat = 'HMACSHA256';                           Why = '2026-10-18 加密重写：encrypt-then-MAC 的完整性校验' }
    @{ Pat = 'ComputeMac';                           Why = '2026-10-18 加密重写：MAC 计算入口(覆盖魔数+版本+salt+IV+正文)' }
    @{ Pat = 'ConstantTimeEquals';                   Why = '2026-10-18 加密重写：定长时间比较(避免提前返回泄漏前缀匹配)' }
    @{ Pat = 'DecryptV2';                            Why = '2026-10-18 加密重写：新容器版本的解密分支' }
    @{ Pat = 'Pbkdf2Iterations';                     Why = '2026-10-18 加密重写：迭代次数常量化' }
    @{ Pat = 'SaltLen';                              Why = '2026-10-18 加密重写：容器头字段长度常量' }
    @{ Pat = 'MacLen';                               Why = '2026-10-18 加密重写：同上' }
    @{ Pat = 'HeaderLen';                            Why = '2026-10-18 加密重写：容器头总长(测试侧也要用同一数值)' }
    @{ Pat = 'buf, p,';                              Why = '2026-10-18 加密重写：MAC 输入拼装的连带 token' }
    @{ Pat = 'aes.';                                 Why = '2026-10-18 加密重写：Aes 实例的属性设置(Key/IV/CreateDecryptor)' }
    @{ Pat = 'Aes.Create';                           Why = '2026-10-18 加密重写：AES 实例创建(上游也调，但重写后调用点变了)' }
    @{ Pat = 'Buffer.BlockCopy';                     Why = '2026-10-18 加密重写：容器头/正文的字节拼装' }
    @{ Pat = 'CryptoStreamMode.Write';               Why = '2026-10-18 加密重写：CryptoStream 的写法方向' }
    @{ Pat = 'cs.Write';                             Why = '2026-10-18 加密重写：CryptoStream 写入口' }
    @{ Pat = 'a.Length';                             Why = '2026-10-18 加密重写：定长比较与派生里的数组长度取值' }
    @{ Pat = 'b.Length';                             Why = '2026-10-18 加密重写：同上' }
    @{ Pat = 'body.Length';                          Why = '2026-10-18 加密重写：正文长度(MAC 输入与解密缓冲)' }
    @{ Pat = 'data.Length';                          Why = '2026-10-18 加密重写：容器总长' }
    @{ Pat = 'cipherText.Length';                    Why = '2026-10-18 加密重写：密文长度' }
    @{ Pat = 'Encoding.UTF8.GetBytes';               Why = '2026-10-18 加密重写：口令材料转字节(PBKDF2 输入)' }
    @{ Pat = 'hmac.ComputeHash';                     Why = '2026-10-18 加密重写：MAC 计算' }
    @{ Pat = 'kdf.GetBytes';                         Why = '2026-10-18 加密重写：PBKDF2 派生输出(一次取加密+MAC 两把)' }
    @{ Pat = 'Magic.Length';                         Why = '2026-10-18 加密重写：容器头拼接与 MAC 覆盖范围都要用它' }
    @{ Pat = 'ms.ToArray';                           Why = '2026-10-18 加密重写：CryptoStream 收尾取字节' }
    # -- 2026-10-18 压缩层换 Brotli + 包装层(用户要求最高级压缩)-------------
    # 压缩从 Encoder 内嵌的 gzip 抽成独立包装层 ReplayCompression.cs：
    # [1B 算法号][4B 原文长度][压缩体]，算法 0=raw/1=gzip/2=Brotli(q6)，
    # 旧 gzip 流(解密后首字节 0x1F)仅解压侧回落。Encoder/Decoder 各改一行接线。
    @{ Pat = 'ReplayCompression.';                   Why = '2026-10-18 压缩层换 Brotli(q6)：包装层独立成 ReplayCompression.cs，编解码两端接线到它' }
    # -- 2026-10-18 .psprp（不解密变体）原本永远打不开 ----------------------
    # 列表会列出 .psprp，点「回放」却走解密分支（首字节不是 SRC2）⇒ 直接判损坏。
    # 修法：按扩展名在 ReadReplayData 里自动判定是否需要解密——扩展名是这条信息
    # 唯一的权威来源（保存时由 SaveReplayNew 决定写哪种），不该要求每个调用点记得传。
    @{ Pat = 'filePath.EndsWith';                    Why = '2026-10-18 修 .psprp 打不开：按扩展名自动跳过解密（上游没有这个变体的载入路径）' }
    @{ Pat = 'StringComparison.OrdinalIgnoreCase';    Why = '2026-10-18 同上：扩展名大小写不敏感' }
    @{ Pat = 'ReplayConstants.Compression';          Why = '2026-10-18 同上：算法号/头长常量(Raw=0/Gzip=1/Brotli=2/HeaderLen=5)' }
    # -- 2026-10-19 渲染健壮性收口（RT 创建失败 / 读回失败 / 取消语义）------
    # 上游 RenderTexture.Create() 无视返回值、三台子相机不判空。一旦显存拒绝
    # （分辨率超限/后台无设备），异步回读每帧 hasError，上游靠黑帧顶替把整片
    # 渲染成一路黑却上报成功。这里改成创建失败即抛，交给正常的失败上报路径。
    @{ Pat = 'RenderTexture.Create';                 Why = '渲染健壮性：Create() 返回 false 时明确报错，不再产出全黑成片' }
    @{ Pat = 'texture.Create';                       Why = '同上：接住返回值的连带 token（上游是裸调用）' }
    @{ Pat = 'cam.Bgcamstatic';                      Why = '同上：子相机判空后再取原 targetTexture' }
    @{ Pat = 'cam.BGcam';                            Why = '同上：同上' }
    @{ Pat = 'Object.Destroy';                       Why = '同上：创建失败时先销毁半成熟的 RT 再抛' }
    @{ Pat = 'VideoRender.Current.FramesTotal';      Why = '渲染健壮性：hardStop 命中要明确失败，避免静默产出截断视频' }
    @{ Pat = 'rendervideo.capture_overrun';          Why = '同上：帧数超上限的提示文案键（14 种语言都有定义）' }
    @{ Pat = 'UnscaledDeltaPin.Uninstall';           Why = '渲染健壮性：全局状态恢复逐项兜底（任一项抛不能带走后面的项）' }
    @{ Pat = 'FindObjectsInactive.Include';          Why = '渲染健壮性：单参重载默认 Exclude，与注释承诺的覆盖未激活对象矛盾' }
    @{ Pat = 'Path.GetExtension';                    Why = '渲染健壮性：救援命令要按目标容器选音频编码器（webm 只认 Opus）' }
    @{ Pat = 'string.Equals';                        Why = '同上：扩展名比较的连带 token' }
    # -- 2026-10-19 编码器管道 stderr 捕获 ----------------------------------
    # 上游只重定向 stdin。ffmpeg 因未知编码器/参数不支持而立即退出时，用户只看到
    # exited unexpectedly，真实原因全丢。这里加 stderr 重定向并把它拼进异常消息。
    @{ Pat = 'started.ErrorDataReceived';            Why = '编码器 stderr 捕获：接上异步读错误流（否则抛错时没有原因）' }
    @{ Pat = 'started.BeginErrorReadLine';           Why = '同上：启动异步读取' }
    @{ Pat = 'pipeStderr';                           Why = '同上：stderr 环形缓冲（只留尾部，失败原因在最后几行）' }
    @{ Pat = 'stderrLock';                           Why = '同上：泵线程与主线程共用的锁' }
    @{ Pat = 'e.Data';                               Why = '同上：ErrorDataReceived 回调取行' }
    @{ Pat = 'pipe.ExitCode';                        Why = '同上：退出码非零时附上 stderr 尾部' }
    @{ Pat = 'string.IsNullOrEmpty';                 Why = '同上：stderr 为空时不加任何后缀' }
    @{ Pat = 'tail.Length';                          Why = '同上：同上' }
    # -- 2026-10-19 回放详情补 Accuracy（用户报「有X-ACC少了ACC」）------------
    # 上游只有 xacc。Accuracy 是另一项指标（完美占比 + 内档奖励），官方式子
    # 在 scrMarginTracker.CalculatePercentAcc。我们不新增回放字段，直接由
    # JudgmentList 判定分布还原——老回放也能显示，格式不用改。
    @{ Pat = 'Accuracy(';                            Why = '2026-10-19 详情面板补 Accuracy：由判定分布按官方公式还原(老回放无此字段)' }
    @{ Pat = 'SumSet';                               Why = '同上：按名解析的判定集合求和助手' }
    @{ Pat = 'FailedFloorFamily';                    Why = '同上：官方从 Accuracy 里扣 FailedFloor 的 1e-4/个' }
    @{ Pat = 'counts.Length';                        Why = '同上：判定桶数组的长度(越界保护)' }
    # -- 2026-10-20 回放没开始时的可诊断性（用户报「等待回放开始超时」）--------
    # 上游每处拒绝都是 TriggerMessage(...); return;：通知几秒后消失、**不进日志**，
    # 于是「回放没开始」在 Player.log 里表现为什么都没发生。九条校验全这样，
    # 排查时只能一条条猜。这里把拒绝收口到 Reject(msg, detail)：玩家仍只看到
    # 原本那条本地化文案，现场值（录制/当前的地板哈希、难度、起点砖等）另打一行
    # Debug.Log。**故意不用 TriggerMessage 拼诊断**——那会把内部值漏到界面上，
    # 且绕开本地化（HardTextCheck 会直接判红，实测踩过）。
    @{ Pat = 'Reject(';                              Why = '2026-10-20 拒绝启动回放时留日志：Reject(玩家文案, 现场值)，通知内容不变' }
    @{ Pat = 'note.play_error';                      Why = '同上：被收口的九条本地化文案键（原先每处各自 TriggerMessage）' }
    @{ Pat = 'scnGame.instance.levelData';           Why = '同上：地板哈希与 pitch/bpm 的现场值要打进日志' }
    @{ Pat = 'GCS.internalLevelName';                Why = '同上：InternalLevelName 校验的现场值（录/现 两边）' }
    @{ Pat = 'GCS.checkpointNum';                    Why = '同上：起点砖校验的现场值' }
    @{ Pat = 'scrPlayerManager.instance';            Why = '同上：单人校验的现场值（players.Count）' }
    @{ Pat = 'players.Count';                        Why = '同上：单人校验的现场值（Compare-Object 会把片段切在点号处）' }
    @{ Pat = 'scene.name';                           Why = '同上：Start_Rewind 入口日志要带场景名与模式' }
    @{ Pat = 'data.ints';                            Why = '同上：现场值取自回放数据（StartTile/EndTile/FloorHash/Pitch）' }
    @{ Pat = 'data.strings';                         Why = '同上：现场值取自回放数据（InternalLevelName/JudgeMode/SongName）' }
    @{ Pat = 'state.ToString';                       Why = '2026-10-20 渲染超时快照：把 States 枚举转成日志文本' }
    @{ Pat = 'debug_skip_Verification';              Why = '同上：入口日志紧邻这条调试开关判断' }
    # 超时那一刻要能看出停在哪：是我们自己的标志没翻、被别的 mod 阻断，
    # 还是压根没进 PlayMode——三种情况修法完全不同，而原先日志只有一句
    # 「failed: 等待回放开始超时」。快照本身不改任何行为。
    @{ Pat = 'SpectreAPI.SnapshotBlocks';            Why = '2026-10-20 渲染超时快照：打出当前玩法的阻断来源（别的 mod 可能挂了 BlockPlaying）' }
    @{ Pat = 'SpectreState.PlayMode';                Why = '同上：快照要含 PlayMode/RecordMode/is_playing 三个标志' }
    @{ Pat = 'SpectreState.RecordMode';              Why = '同上' }
    @{ Pat = 'SpectreState.is_playing';              Why = '同上' }
    @{ Pat = 'blockText';                            Why = '同上：阻断来源拼成一行文本（不引 LINQ，保持与上游一致的手写循环）' }
    @{ Pat = 'System.Text.StringBuilder';            Why = '同上：快照的拼接缓冲（用全名，免得动 using 区）' }
    @{ Pat = 'kv.Key';                               Why = '同上：遍历阻断表拼 k:v' }
    @{ Pat = 'kv.Value';                             Why = '同上' }
    @{ Pat = 'scrController.instance';               Why = '同上：快照要报出当前 States（Countdown/PlayerControl 之外都算没接管）' }
    # -- 2026-10-18 渲染自己装回录像倍速（用户报「等待回放开始超时」且画面没变慢）--
    # 游戏源码 scnEditor.Play() 对程序化调用无条件写 playbackSpeed=1x
    # （scnEditor.cs:7204-7205，holdingControl 只有真按住 Ctrl 才为真），
    # 紧接着 RemakePath → SetupConductorWithLevelData 用它推导 song.pitch
    # （scrConductor.cs:378-395）。TryStartPlayback 的倍速校验因此每轮都拒，
    # StartPlaying 从未执行。上游没有这条是因为它同样只能靠玩家手动按住 Ctrl。
    # 上游 RemakePath 的 Prefix 钩子是 Play() 内部唯一经过的注入点：
    # 早于 Play() 会被覆盖，晚于 Play() 则 pitch 已按 1x 算完。
    @{ Pat = 'VideoRender.ApplySpeedOverrideTo';     Why = '2026-10-18 渲染期倍速注入：RemakePath Prefix 是 Play() 内部唯一的钩子（见 VideoRender 注释）' }
    @{ Pat = 'VideoRender.VideoRender.Active';       Why = '同上：只有渲染作业在跑时才钉倍速（正常游玩不干预）' }
    @{ Pat = 'VideoRender.VideoRender.ApplySpeedOverrideTo'; Why = '同上：Start_Rewind Prefix 每轮再补一次（重开关卡会重写 playbackSpeed）' }
    @{ Pat = 'editor.playbackSpeed';                 Why = '同上：写回编辑器倍速（pitch 由它推导）' }
    @{ Pat = 'GCS.editorQuickPitchedPlaying';        Why = '同上：校验式 actualSpeed 的另一半（QuickPitched ? playbackSpeed : 1）' }
    @{ Pat = 'VideoRender.SpeedOverride';            Why = '同上：渲染期倍速钉扎的三个状态字段' }
    @{ Pat = 'VideoRender.SpeedOverrideArmed';       Why = '同上' }
    @{ Pat = 'VideoRender.SpeedOverrideQuickPitched'; Why = '同上' }
    @{ Pat = 'scnEditor.RemakePath';                 Why = '同上：注入点的目标方法' }
    @{ Pat = 'ADOBase.editor';                       Why = '同上：从 ADOBase 取编辑器实例（可能为 null，ApplySpeedOverrideTo 内部判空）' }
    # -- 同上：等待阶段快速失败并报出真实原因 ------------------------------
    @{ Pat = 'Patch_StartRewind.LastReject';         Why = '2026-10-18 等待失败要报真实原因：拒绝记录接线到渲染失败通知' }
    @{ Pat = 'Patch_StartRewind.ResetRejectLog';     Why = '同上：作业启动时清掉上一轮残留' }
    @{ Pat = 'rendervideo.replay_not_taken';         Why = '同上：新增的 14 语言文案键（关卡已跑但回放未接管）' }
    @{ Pat = 'CursorOverlay.Draw'; File = 'VideoFrameSource.cs'; Why = '2026-10-20 录制鼠标指针：交付像素前把箭头合成进 RGBA 帧（上游没有这个功能）' }
    @{ Pat = 'flight.CursorDrawn'; File = 'VideoFrameSource.cs'; Why = '同上：回读是异步的，指针位置必须随 Flight 一起排队，交付时才知道该不该画' }
    @{ Pat = 'flight.MouseX'; File = 'VideoFrameSource.cs'; Why = '同上：Grab 那一刻的指针位置（交付时现读会甩尾两三帧）' }
    @{ Pat = 'flight.MouseY'; File = 'VideoFrameSource.cs'; Why = '同上' }
    @{ Pat = 'flight.ScreenW'; File = 'VideoFrameSource.cs'; Why = '同上：像素坐标要按当时窗口尺寸换算，窗口中途变化也不会画错位置' }
    @{ Pat = 'flight.ScreenH'; File = 'VideoFrameSource.cs'; Why = '同上' }
    @{ Pat = 'flights.Enqueue'; File = 'VideoFrameSource.cs'; Why = '同上：改成先构造 Flight 再入队（要先填指针采样字段）' }
    @{ Pat = 'head.CursorDrawn'; File = 'VideoFrameSource.cs'; Why = '同上：出队后按这一帧自己记的开关/位置决定画不画' }
    @{ Pat = 'head.MouseX'; File = 'VideoFrameSource.cs'; Why = '同上' }
    @{ Pat = 'head.MouseY'; File = 'VideoFrameSource.cs'; Why = '同上' }
    @{ Pat = 'head.ScreenW'; File = 'VideoFrameSource.cs'; Why = '同上' }
    @{ Pat = 'head.ScreenH'; File = 'VideoFrameSource.cs'; Why = '同上' }
    @{ Pat = 'Input.mousePosition.x'; File = 'VideoFrameSource.cs'; Why = '同上：指针位置来源（游戏没有软件光标，硬件光标不在 backbuffer 里）' }
    @{ Pat = 'Input.mousePosition.y'; File = 'VideoFrameSource.cs'; Why = '同上（Unity 的鼠标坐标是左下原点，CursorOverlay 内部转左上）' }
    @{ Pat = 'Input.mousePresent'; File = 'VideoFrameSource.cs'; Why = '2026-10-20 录制鼠标指针：没有指针设备时不要画一个停在原地的箭头' }
    @{ Pat = 'Screen.width'; File = 'VideoFrameSource.cs'; Why = '同上：按采样那一刻的窗口尺寸换算归一化坐标' }
    @{ Pat = 'Screen.height'; File = 'VideoFrameSource.cs'; Why = '同上' }
    @{ Pat = 'frames.DrawCursor'; File = 'VideoRenderJob.cs'; Why = '2026-10-20 录制鼠标指针：渲染作业把开关交给画面源，开关才真的作用于像素' }
    @{ Pat = 'Settings.RenderCursorOn'; File = 'VideoRenderJob.cs'; Why = '同上：开关字段' }
    @{ Pat = 'Settings.RenderCursorOn'; File = 'ConfigManager.cs'; Why = '同上：开关的持久化读写（render_cursor_on）' }
    @{ Pat = 'rendervideo.wait_timeout';             Why = '同上：失败通知从「只说超时」改为「超时 + 真实原因」' }
    @{ Pat = 'SpectreState.data.bools.TryGetValue';  Why = '同上：倍速来源读录像的 QuickPitched 门控' }
    @{ Pat = 'controller.state';                     Why = '同上：早退判据要同时看关卡是否已在跑' }
    @{ Pat = 'States.Countdown';                     Why = '同上：Countdown/PlayerControl 视为关卡已开始' }
    @{ Pat = 'States.PlayerControl';                 Why = '同上' }
    @{ Pat = 'Features.Replay';                      Why = '同上：渲染侧要引用回放命名空间里的拒绝记录（using 行）' }
    @{ Pat = 'FeatureKeys.Replay';                   Why = '同上：RemakePath 钩子挂回放总开关' }
    @{ Pat = 'FeatureKeys.Replay,';                  Why = '同上：属性实参里的逗号形态' }
    @{ Pat = 'PatchType.Prefix';                     Why = '同上：钩子类型' }
    @{ Pat = 'scnEditor.RemakePath';                 Why = '同上：nameof 形式的注入点' }
    @{ Pat = '__instance';                          Why = '同上：注入钩子的目标实例形参（本仓补丁层约定，上游没有这条补丁）' }
    # -- 2026-10-19 收尾后台化：合成阶段不再压在主线程 ---------------------
    # 上游把 FinishVideo（等泵线程排空积压帧）与 MuxWith（第二个 ffmpeg 合成音轨）
    # 直接跑在协程里，也就是主线程上。两者都是「等别人干完活」：排空要等队列写进
    # 管道，合成要把整个成片读一遍写一遍。卡顿时长与成片大小成正比，而它偏偏发生在
    # 用户最想看到结果的那一刻。
    # 搬走的依据是它们只碰 Process / File / BlockingCollection，不读任何 Unity 状态。
    # 泛名字（Thread / TimeSpan / ExceptionDispatchInfo）一律用 File 限定到本文件。
    @{ Pat = 'finalizeWorker';          File = 'VideoRenderJob.cs'; Why = '2026-10-19 收尾后台化：工作线程句柄（volatile 结果标志同批）' }
    @{ Pat = 'finalizeDone';            File = 'VideoRenderJob.cs'; Why = '同上：工作线程写、主线程读的完成标志' }
    @{ Pat = 'finalizeError';           File = 'VideoRenderJob.cs'; Why = '同上：把工作线程的异常带回主线程统一上报' }
    @{ Pat = 'finalizeErrorFromMux';    File = 'VideoRenderJob.cs'; Why = '同上：区分 mux 失败（视频流已完整，值得抢救）与排空失败' }
    @{ Pat = 'StartFinalizeOnWorker';   File = 'VideoRenderJob.cs'; Why = '同上：收尾线程的启动入口' }
    @{ Pat = 'enc.FinishVideo';         File = 'VideoRenderJob.cs'; Why = '同上：把排空调用移进闭包（先在主线程抓编码器引用）' }
    @{ Pat = 'enc.MuxWith';             File = 'VideoRenderJob.cs'; Why = '同上：把合成调用移进闭包' }
    @{ Pat = 'finalizeWorker.IsAlive';  File = 'VideoRenderJob.cs'; Why = '同上：TearDown 判断线程是否还在跑（在跑就跳过会冲突的清理）' }
    @{ Pat = 'finalizeWorker.Join';     File = 'VideoRenderJob.cs'; Why = '同上：TearDown 的**有界**等待（5 秒，不是 12 分钟——12 分钟等于把卡顿换成假死）' }
    @{ Pat = 'finalizeWorker.Start';    File = 'VideoRenderJob.cs'; Why = '同上：启动线程' }
    @{ Pat = 'System.Threading.Thread'; File = 'VideoRenderJob.cs'; Why = '同上：裸 Thread 而非 Task.Run——主项目是 netstandard2.1 + Mono，Thread 是确定可用的原语' }
    @{ Pat = 'TimeSpan.FromSeconds';    File = 'VideoRenderJob.cs'; Why = '同上：TearDown 的有界等待时长' }
    @{ Pat = 'System.Runtime.ExceptionServices.ExceptionDispatchInfo'; File = 'VideoRenderJob.cs'; Why = '同上：保留工作线程异常的原始堆栈（直接 throw 会把堆栈重置到重抛那一行）' }
    @{ Pat = 'movflags';                File = 'VideoEncoder.cs'; Why = '2026-10-19 去掉 +faststart：它要求 moov 前置，ffmpeg 只能把整个成片重写一遍搬过去；本地文件播放不需要它' }
    # -- 2026-10-23 跳转到关卡后段后按键模拟消失 ----------------------------
    # 上游 FastForward 的游标会被紧随其后的 Start() 清零（Start 里无条件
    # PlayIndex/KeyEventIndex = 0），且两个查找循环「找不到目标就保留初值 0」。
    # 大关卡事件上万，追平要几百帧（每帧上限 64），追平瞬间又被判定为已到尾部直接
    # StopPlaying。修法：先 StartPlaying 再 FastForward；游标兜底到末尾；跳转后重建
    # 仍按住的键。
    @{ Pat = 'HitSnapshots.Count;';     File = 'ReplayPlayer.cs'; Why = '2026-10-23 跳转游标兜底到末尾（找不到目标时留在 0 会从头重灌整局）' }
    @{ Pat = 'KeyEvents.Count;';        File = 'ReplayPlayer.cs'; Why = '同上：按键游标兜底到末尾，且放在边界检查外面（targetFloor 越界时也要兜底）' }
    @{ Pat = 'ManagedInput.Restore';    File = 'ReplayPlayer.cs'; Why = '同上：跳转后重建仍按住的键（只恢复状态不写帧戳，避免凭空多出按下沿）' }
    @{ Pat = 'Restore';                 File = 'ManagedInput.cs'; Why = '同上：只恢复按住状态、不写 PressedFrame 的入口' }
    @{ Pat = 'CurrKeyState.Length';     File = 'ReplayPlayer.cs'; Why = '同上：跳转后把重建出的按住状态同步给 SendInput 路径' }
    @{ Pat = 'ev.KeyCode';              File = 'ReplayPlayer.cs'; Why = '同上：重建跳转点时按录制数据里的键码还原' }
    @{ Pat = 'ManagedInput.IsHeldVk';   File = 'ReplayPlayer.cs'; Why = '同上：读回重建后的按住状态' }
    @{ Pat = 'Settings.key_code_convert'; File = 'ReplayPlayer.cs'; Why = '同上：重建时必须走同一张映射表，与 PumpInput 保持一致' }
    # -- 2026-10-19 时间钉扎的跨作业状态安全（用户报「取消渲染后下一次时钟全炸」）--
    # 上游两处都更脆：Uninstall 把撤不掉的 detour 连同列表一起丢掉（detour 仍挂在
    # Time.* 上但已无人知晓），且 PinnedDelta 用 Time.captureFramerate 做除数
    # （收尾会把它还原成 0 ⇒ 1/0 = Infinity ⇒ 星球位置 NaN）。修法是"失败项留着"
    # + "帧率快照"。这些名字全是这个文件独有的局部/成员名，但仍逐个限定 File。
    @{ Pat = 'failed.Add';              File = 'VideoRenderPatches.cs'; Why = '2026-10-19 撤销失败的 detour 收集起来（原实现直接丢弃）' }
    @{ Pat = 'detours.Add';             File = 'VideoRenderPatches.cs'; Why = '同上：把撤不掉的放回列表，下次再试' }
    @{ Pat = 'detours.Count';           File = 'VideoRenderPatches.cs'; Why = '同上：Install 里据此判断是否有残留并先尝试撤销' }
    @{ Pat = 'd.Dispose';               File = 'VideoRenderPatches.cs'; Why = '同上：带重试的撤销（首次失败常因目标正在销毁）' }
    @{ Pat = 'ex2.Message';             File = 'VideoRenderPatches.cs'; Why = '同上：重试仍失败的日志' }
    @{ Pat = 'armedFps';                File = 'VideoRenderPatches.cs'; Why = '2026-10-19 帧率快照：钉扎位移不再读会被还原成 0 的 Time.captureFramerate' }
    @{ Pat = 'Time.captureFramerate';   File = 'VideoRenderPatches.cs'; Why = '同上：只在 Install 时读一次做快照，不再当除数' }
    # 主线程等不及收尾线程时的延迟清理（见 DeferCleanupAfterWorker）。清理不能就地做
    # （会 Kill 掉工作线程正等的 ffmpeg、删掉它正读的 raw 视频），也不能不做
    # （编码器持有进程与帧队列，临时目录几百 MB）。所以挂到工作线程之后，在另一个
    # 后台线程里 Join 它、再回收。
    @{ Pat = 'DeferCleanupAfterWorker'; File = 'VideoRenderJob.cs'; Why = '2026-10-19 收尾后台化：主线程等不及收尾线程时的延迟清理入口' }
    @{ Pat = 'target.Join';             File = 'VideoRenderJob.cs'; Why = '同上：等的是**收尾线程**，不是清理线程自己（写成 waiter.Join 会 Join 自己而死锁）' }
    @{ Pat = 'waiter.Start';            File = 'VideoRenderJob.cs'; Why = '同上：启动清理线程' }
    @{ Pat = 'TimeSpan.FromMinutes';    File = 'VideoRenderJob.cs'; Why = '同上：延迟清理的等待上限（覆盖 FinishVideo 10 分钟 + MuxWith 30 分钟）' }
    @{ Pat = 'Directory.Delete';        File = 'VideoRenderJob.cs'; Why = '同上：延迟清理里删临时目录（与 TearDown 里同一操作，只是时机推到收尾之后）' }
    @{ Pat = 'Directory.Exists';        File = 'VideoRenderJob.cs'; Why = '同上：删之前先确认存在' }
    # ══════════════════════════════════════════════════════════════════
    # -- 2026-10-19 补登记：被"相对路径匹配"长期漏掉的历史漂移 -----------
    #
    # **这批不是新债，是审计以前看不见的地方。**
    # 2026-10-19 把 Features 拆进职责子目录后，旧的按**相对路径**匹配
    # （Join-Path $theirs $rel）对移动过的文件全部落空，它们被静默归进
    # 「我们独有，无需比对」——61 个文件只真正比对了 34 个，其余 27 个
    # 一整个月都在门禁视野之外，而闸门一直是绿的。
    # 改成按**文件名**匹配后比对数 34→44，下面这 5 个文件才**第一次**进入
    # 比对，历史漂移于是暴露出来。
    #
    # 每一条都用 File = 限定到具体文件。这些名字（Mathf.Clamp / File.Copy /
    # File.Exists / Path.Combine）是泛称，不限定文件就等于给全仓开一道后门：
    # 任何文件的同类漂移都会被这条记录顺手放过。
    # ══════════════════════════════════════════════════════════════════

    # -- AudioRecorder：多声道下混 + WAV 头 32 位溢出防护（2026-10-19）------
    # 上游把每帧样本当单声道写，采样率/声道来自设备时长度字段会算错；
    # 混音轨渲染出来的 wav 因此时长不对。改成按 channels 下混成单声道，
    # 并且 dataSize 用 long 算、超 int 上限时明确报错而不是静默截断。
    @{ Pat = 'BitConverter.GetBytes';   File = 'AudioRecorder.cs'; Why = '2026-10-19 WAV 头重写：dataSize 用 long 算后再写回 4 字节字段' }
    @{ Pat = 'fileStream.Flush';        File = 'AudioRecorder.cs'; Why = '2026-10-19 每块落盘后 Flush：崩溃时已录部分不丢' }
    @{ Pat = 'int.MaxValue';            File = 'AudioRecorder.cs'; Why = '2026-10-19 WAV 数据超 32 位长度字段上限时明确报错（上游静默截断）' }
    @{ Pat = 'Math.Max';                File = 'AudioRecorder.cs'; Why = '2026-10-19 下混循环里的边界取值' }
    @{ Pat = 'Math.Min';                File = 'AudioRecorder.cs'; Why = '同上' }
    @{ Pat = 'Mathf.Clamp';             File = 'AudioRecorder.cs'; Why = '2026-10-19 下混后仍需 16bit 限幅（与上游同一个限幅式）' }

    # -- ConfigManager：删掉 CustomJudge 那一整段 DTO（2026-10-17 用户裁决）--
    # 用户原话「CustomJudge 并不属于 replay 和视频渲染的部分」。
    # 上游的 ConfigData 里带 CustomJudgeData（tier_on/tier_inner/… 十来个数组）。
    # 对应地，我们这边多了渲染相关的 Sets 字段（RenderWidth/Fps/…）要先落盘。
    @{ Pat = 'File.WriteAllText';       File = 'ConfigManager.cs'; Why = '配置落盘：与上游同一个 File.WriteAllText，但调用点因 DTO 裁剪而移动' }
    @{ Pat = 'Formatting.Indented';     File = 'ConfigManager.cs'; Why = '沿用上游的缩进格式（配置文件要人能看懂/手改）' }
    @{ Pat = 'ints.Value';              File = 'ConfigManager.cs'; Why = '读配置时按 JTokenType 分派取值的连带 token' }
    @{ Pat = 'json.ToString';           File = 'ConfigManager.cs'; Why = '同上：序列化出口' }
    @{ Pat = 'JTokenType.Boolean';      File = 'ConfigManager.cs'; Why = '读配置按 JSON 类型分派（上游用强类型反序列化，我们改成容错读取）' }
    @{ Pat = 'JTokenType.Float';        File = 'ConfigManager.cs'; Why = '同上' }
    @{ Pat = 'JTokenType.Integer';      File = 'ConfigManager.cs'; Why = '同上' }
    @{ Pat = 'JTokenType.String';       File = 'ConfigManager.cs'; Why = '同上' }
    @{ Pat = 'Mathf.Clamp';             File = 'ConfigManager.cs'; Why = '读回数值时的区间钳制（防止手改配置写出越界值）' }
    @{ Pat = 't.Type';                  File = 'ConfigManager.cs'; Why = '按 JSON 类型分派' }
    @{ Pat = 't.Value';                 File = 'ConfigManager.cs'; Why = '同上：取实际值' }
    @{ Pat = 'Settings.AutoSave';       File = 'ConfigManager.cs'; Why = 'DTO 裁剪：CustomJudge 段删除后的连带字段（上游还留着一堆 Custom* 设置）' }
    @{ Pat = 'Settings.CancelSaveButton'; File = 'ConfigManager.cs'; Why = '同上' }
    @{ Pat = 'Settings.ConvertTarget';  File = 'ConfigManager.cs'; Why = '视频渲染设置（上游没有这一整组）' }
    @{ Pat = 'Settings.GetDataFromAsyncInput'; File = 'ConfigManager.cs'; Why = '异步输入开关的落盘（round 早）' }
    # -- 2026-10-21 补 CancelSave 读盘侧（写盘侧本就有，读侧漏了）--------------
    # Sets.CancelSave 一直在写，LoadFromFile 里却没有对应的读回；手改成 false
    # 后重启会被静默读回默认 true（玩家看到「这个开关自己又开了」）。
    @{ Pat = 'Settings.CancelSave';     File = 'ConfigManager.cs'; Why = '2026-10-21 补读盘：该开关的持久化读回（上游没有这个按键补丁开关）' }
    @{ Pat = 'sets.Value';              File = 'ConfigManager.cs'; Why = '2026-10-21 补读盘的连带 token：容错读取 bool 键（与上面 ints.Value 同一种取法）' }
    @{ Pat = 'Settings.JudgeBarPositionY'; File = 'ConfigManager.cs'; Why = 'DTO 裁剪：判定条位置设置（上游 JudgeBar 整组已删）' }
    @{ Pat = 'Settings.RenderCaptureMode'; File = 'ConfigManager.cs'; Why = '视频渲染设置（上游没有）' }
    @{ Pat = 'Settings.RenderEncoderMode'; File = 'ConfigManager.cs'; Why = '同上' }
    @{ Pat = 'Settings.RenderFps';      File = 'ConfigManager.cs'; Why = '同上' }
    @{ Pat = 'Settings.RenderHeight';   File = 'ConfigManager.cs'; Why = '同上' }
    @{ Pat = 'Settings.RenderOutputDir'; File = 'ConfigManager.cs'; Why = '同上' }
    @{ Pat = 'Settings.RenderOutputFormat'; File = 'ConfigManager.cs'; Why = '同上' }
    @{ Pat = 'Settings.RenderQualityMode'; File = 'ConfigManager.cs'; Why = '同上' }
    @{ Pat = 'Settings.RenderTailSeconds'; File = 'ConfigManager.cs'; Why = '同上' }
    @{ Pat = 'Settings.RenderWidth';    File = 'ConfigManager.cs'; Why = '同上' }
    @{ Pat = 'Settings.SaveButton';     File = 'ConfigManager.cs'; Why = 'DTO 裁剪的连带字段' }

    # -- Settings：删 CustomJudge / JudgeBar / PlanetColors 整组，加渲染组 --
    # 与 ConfigManager 同源（2026-10-17 用户裁决）。EffectRemoverOn 默认值
    # 由 true 改 false 是 2026-10-19 的决定：去特效会改变成片观感，
    # 不该默认开。
    @{ Pat = 'ConfigManager.SaveConfigs'; File = 'Settings.cs'; Why = '设置落盘入口（上游同名，调用点因裁组而移动）' }
    @{ Pat = 'Configs.json';            File = 'Settings.cs'; Why = '配置文件路径拼装' }
    @{ Pat = 'Main.mod.Path';           File = 'Settings.cs'; Why = '同上：从 mod 目录推配置路径' }
    @{ Pat = 'System.Exception';        File = 'Settings.cs'; Why = '落盘失败的异常捕获（用全名免得动 using 区）' }
    @{ Pat = 'System.IO.Path.Combine';  File = 'Settings.cs'; Why = '同上：路径拼装' }
    @{ Pat = 'VanillaMarginColors.Length'; File = 'Settings.cs'; Why = '2026-10-18 判定窗口颜色改从游戏自带色表取（删掉上游的 Features.Other.JudgeTextDefaults）' }

    # -- SpectreAPI：删掉 CustomJudge 整组 API（2026-10-17）---------------
    # 上游这里导出 HitSign/TierCount/GetTierCounts 等一整套自定义判定接口。
    # 换成我们的渲染阶段查询（PhaseNames 等）。
    @{ Pat = 'Enum.GetNames';           File = 'SpectreAPI.cs'; Why = '2026-10-19 渲染阶段名表改成 Enum.GetNames 动态取（上游没有这套查询）' }
    @{ Pat = 'PhaseNames.Length';       File = 'SpectreAPI.cs'; Why = '同上：枚举值越界时回落 "Idle" 的边界判断' }
    @{ Pat = 'Features.VideoRender.VideoRender.CurrentPhase'; File = 'SpectreAPI.cs'; Why = '2026-10-19 新增渲染状态查询接口（上游无）' }
    @{ Pat = 'Features.VideoRender.VideoRender.Phase'; File = 'SpectreAPI.cs'; Why = '同上：阶段枚举类型' }

    # -- SpectreState：删 ReplaySaveFormat 三路保存 + 「打开这一关」新功能 --
    # 前者是 2026-10-18 用户裁定删旧容器格式的连带面（上游还有 New/Crp2/Json
    # 三条路径与 debug_save_format 开关）。
    # 后者是我们自己加的功能：从回放列表直接打开关卡/官方关卡。
    @{ Pat = 'OpenReplayLevel';         File = 'SpectreState.cs'; Why = '2026-10-19 新增「打开这一关」：从回放详情跳进关卡（上游无此功能）' }
    @{ Pat = 'Go(string levelPath';     File = 'SpectreState.cs'; Why = '同上：入口方法签名' }
    @{ Pat = 'string.IsNullOrEmpty(levelPath)'; File = 'SpectreState.cs'; Why = '同上：空路径早退' }
    @{ Pat = 'data.ints.TryGetValue';   File = 'SpectreState.cs'; Why = '同上：起点砖取自回放数据（StartTile）' }
    @{ Pat = 'data.bools.TryGetValue';  File = 'SpectreState.cs'; Why = '同上：跳转时按回放数据恢复「速度试炼」开关（SpeedTrailMode）' }
    @{ Pat = 'Math.Max';                File = 'SpectreState.cs'; Why = '同上：起点砖下限钳制' }
    @{ Pat = 'editor.floors';           File = 'SpectreState.cs'; Why = '同上：编辑器模式下定位到起始砖' }
    @{ Pat = 'editor.isLoading';        File = 'SpectreState.cs'; Why = '同上：等编辑器加载完再定位' }
    @{ Pat = 'editor.SelectFloor';      File = 'SpectreState.cs'; Why = '同上：选中起始砖' }
    @{ Pat = 'scnEditor.levelToOpenOnLoad'; File = 'SpectreState.cs'; Why = '同上：编辑器要打开的关卡路径' }
    @{ Pat = 'GCS.sceneToLoad';         File = 'SpectreState.cs'; Why = '同上：目标场景' }
    @{ Pat = 'GCS.previousScene';       File = 'SpectreState.cs'; Why = '同上：返回场景' }
    @{ Pat = 'GCS.speedTrialMode';      File = 'SpectreState.cs'; Why = '同上：速度试炼模式要跟着回放数据走' }
    @{ Pat = 'GCS.practiceMode';        File = 'SpectreState.cs'; Why = '同上' }
    @{ Pat = 'GCS.currentSpeedTrial';   File = 'SpectreState.cs'; Why = '同上：速度试炼的实际倍速' }
    @{ Pat = 'GCS.customLevelPaths';    File = 'SpectreState.cs'; Why = '同上：自定义关卡路径（scrController 不在时的兜底）' }
    @{ Pat = 'GCS.loadCustomFromBundle'; File = 'SpectreState.cs'; Why = '同上' }
    @{ Pat = 'ADOBase.isScnGame';       File = 'SpectreState.cs'; Why = '同上：判断当前是否在游玩场景' }
    @{ Pat = 'ADOBase.loader.LoadSceneWithTransition'; File = 'SpectreState.cs'; Why = '同上：带转场切场景' }
    @{ Pat = 'controller.LoadCustomLevel'; File = 'SpectreState.cs'; Why = '同上：控制器在时直接载入关卡' }
    @{ Pat = 'scrController.deaths';    File = 'SpectreState.cs'; Why = '同上：重开关卡要清死亡数' }
    @{ Pat = 'MainThread.StartCoroutine'; File = 'SpectreState.cs'; Why = '同上：等编辑器就绪的协程' }
    @{ Pat = 'System.Collections.IEnumerator'; File = 'SpectreState.cs'; Why = '同上：协程返回类型' }
    @{ Pat = 'WipeDirection.StartsFromRight'; File = 'SpectreState.cs'; Why = '同上：转场方向' }
    @{ Pat = 'PlayActions.StopPlaying';  File = 'SpectreState.cs'; Why = '同上：跳转前先停掉当前回放/录制' }
    @{ Pat = 'PlayActions.StopRecording'; File = 'SpectreState.cs'; Why = '同上' }
    @{ Pat = 'PlayActions.SwitchToRecordMode'; File = 'SpectreState.cs'; Why = '同上' }
    @{ Pat = 'playback.openlevel.official'; File = 'SpectreState.cs'; Why = '同上：官方关卡入口的文案键' }
    @{ Pat = 'ReplayManager.UnLoadReplay'; File = 'SpectreState.cs'; Why = '同上：跳转前卸载当前回放' }
    @{ Pat = 'Settings.SavePath';       File = 'SpectreState.cs'; Why = '同上：跳转时要用回放所在目录' }
    @{ Pat = 'Ui.Loc.T';                File = 'SpectreState.cs'; Why = '同上：文案走我们的 Loc（上游用 LocalizationManager 直取）' }
    @{ Pat = 'Directory.CreateDirectory'; File = 'SpectreState.cs'; Why = '备份目录创建（上游在 SaveReplay 内部做）' }
    @{ Pat = 'File.Copy';               File = 'SpectreState.cs'; Why = '2026-10-19 备份改成直接复制主文件，不再重跑保存管线（PBKDF2 一局两遍半秒卡顿）' }
    @{ Pat = 'File.Exists';             File = 'SpectreState.cs'; Why = '同上：复制前确认源文件在' }
    @{ Pat = 'Path.Combine';            File = 'SpectreState.cs'; Why = '同上：备份路径拼装' }
    @{ Pat = 'Settings.Backup';         File = 'SpectreState.cs'; Why = '2026-10-19 主保存失败时不走备份：谓词改成 Settings.Backup && savedPath != null（上游无条件）。savedPath 为 null 时它是字符串拼接得裸串 "sprp"，File.Copy 抛异常被吞后再多报一条备份失败，把真因盖住' }
    # ══════════════════════════════════════════════════════════════════
    # -- 2026-10-19 判定模式自动对齐（用户问「判定和录制时对不上能不能自动调整」）--
    # 上游遇到 JudgeMode 与当前 GCS.difficulty 不符时**直接拒绝播放**。同一份按键
    # 数据在「标准」下是 Perfect、在「严格」下会变成 Early/Late，于是「判定与录制
    # 时对不上」。改为临时套用录像的难度、回放结束还原；玩家自己的默认难度存在
    # Persistence，全程不动。回放文件是玩家可编辑的文本，所以解析要 IsDefined 兜底。
    @{ Pat = 'CachedDifficulty.HasValue'; File = 'ReplayPlayer.cs'; Why = '2026-10-19 判定模式自动对齐：只有真接管过难度才还原（null = 本局没动过）' }
    @{ Pat = 'CachedDifficulty.Value';  File = 'ReplayPlayer.cs'; Why = '同上：取回接管前的难度写回 GCS.difficulty' }
    @{ Pat = 'SyncEditorDifficultyLabel'; File = 'SpectreState.cs'; Why = '2026-10-19 难度被改动后刷新编辑器标签（游戏侧 UpdateDifficultyDisplay 是 private，只抄它的两行显示赋值）' }
    @{ Pat = 'RDConstants.data.bullseyeSprites'; File = 'SpectreState.cs'; Why = '同上：靶心图标按难度下标取，与游戏 UpdateDifficultyDisplay 同一来源' }
    @{ Pat = 'RDString.Get';            File = 'SpectreState.cs'; Why = '同上：难度名走游戏的本地化键 enum.Difficulty.*' }
    @{ Pat = 'scnEditor.instance';      File = 'SpectreState.cs'; Why = '同上：难度选择器挂在编辑器上（编辑器不在时为 null，内部判空）' }
    @{ Pat = 'selector.bullseyeImage';  File = 'SpectreState.cs'; Why = '同上：靶心图片字段（游戏侧 public，可安全直写）' }
    @{ Pat = 'selector.editorDifficultyText'; File = 'SpectreState.cs'; Why = '同上：难度文本字段（游戏侧 public，可安全直写）' }
    @{ Pat = 'System.Exception';        File = 'SpectreState.cs'; Why = '同上：纯显示刷新失败不该影响回放本身，兜住并记一行' }
    # -- 2026-10-19 取消渲染后界面误报「渲染完成」（用户截图指出）---------
    # 上游靠 Snapshot.Output 非空来判「已完成」。但 Output 在 BuildOutputPath 之后
    # 立刻赋值，取消时它照样非空 ⇒ 取消显示成「渲染完成：xxx.mp4」。
    # 改为显式记录终态 Outcome，并在 ReportEnd（所有结束路径的唯一收口）里写入。
    @{ Pat = 'Outcome.None';            File = 'VideoRender.cs'; Why = '2026-10-19 显式记录渲染终态：无结果（不再用 Output 非空推断完成）' }
    @{ Pat = 'Current.Result';          File = 'VideoRender.cs'; Why = '同上：快照里的终态字段，界面据此分支显示；新一轮开始时复位' }
    @{ Pat = 'RenderClockSource.ActiveClock'; File = 'VideoRender.cs'; Why = '2026-10-19 上一轮异常退出的残留钉扎要在启动前清掉（否则下一轮拿到混合时间源，回放不动）' }
    @{ Pat = 'VideoRender.Outcome.Canceled'; File = 'VideoRenderJob.cs'; Why = '2026-10-19 终态分派：ReportEnd 把 Canceled 映射成界面可读的终态' }
    @{ Pat = 'VideoRender.Current.Result'; File = 'VideoRenderJob.cs'; Why = '同上：终态在唯一收口处写回快照，避免四个取消点各写一遍漏掉' }
    @{ Pat = 'SpectreAPI.RenderEndStatus'; File = 'VideoRenderJob.cs'; Why = '同上：按 Completed/Canceled/Failed 三态分派（成功保留 Output，其余清空以免显示未生成的路径）' }
    @{ Pat = 'rendervideo.failed';      File = 'VideoRenderJob.cs'; Why = '同上：失败通知的文案键（成功/取消/失败三态各一条）' }
    # -- 2026-10-19 渲染结束后退出试玩（用户报「渲染完了还在播放」）---------
    # 上游渲染第一步把编辑器切进 playtest，但整条收尾链里没有任何一步退回去，
    # 于是成功/失败/取消三种结局都停在 playtest 里继续跑。
    @{ Pat = 'editor.playMode';         File = 'VideoRenderJob.cs'; Why = '2026-10-19 收尾退出试玩：玩家若已自己按过 Esc 就不再重复重置编辑器' }
    @{ Pat = 'editor.SwitchToEditMode'; File = 'VideoRenderJob.cs'; Why = '同上：照抄游戏的官方退出入口，不自己拼还原动作' }
    @{ Pat = 'VideoRender.CaptureInProgress'; File = 'VideoRenderJob.cs'; Why = '2026-10-19 捕获期时间轴被重置要中止：Start_Rewind 二次触发会让已算好的锚点失效' }
    @{ Pat = 'VideoRender.Current.Output'; File = 'VideoRenderJob.cs'; Why = '2026-10-19 终态写入与 mux 失败抢救共用这一字段' }
    @{ Pat = 'VideoRender.Current.Error'; File = 'VideoRenderJob.cs'; Why = '同上：失败原因写进快照供界面显示' }
    @{ Pat = 'unpinnedNow.ToString';    File = 'VideoRenderJob.cs'; Why = '2026-10-19 锚点自愈日志要打出重锚前后的两个时间值' }
    @{ Pat = 'Math.Abs';                File = 'VideoRenderJob.cs'; Why = '同上：偏差取绝对值与阈值比较' }
    @{ Pat = 'rendervideo.playback_restarted'; File = 'VideoRenderJob.cs'; Why = '2026-10-19 捕获期回放被重置：终态要报「回放被重新开始」而不是「已取消」' }
    @{ Pat = 'rendervideo.playback_restarted'; File = 'LocalizationManager.cs'; Why = '同上：该文案键在本地化表里的定义行（14 种语言）' }
    # -- 2026-10-19 提前还相机（用户报收尾期「镜厅」）----------------------
    # 上游只在 TearDown 的 frames.Dispose() 里还相机，那时后台收尾可能已跑了几十秒，
    # 期间三台相机全钉在我们的 RT 上、屏幕无人清屏。改为 ReleaseGameControl 时立刻还，
    # 且用构造时抓的引用（渲染中切过场景会让 scrCamera.instance 指向新相机）。
    @{ Pat = 'camStatic.targetTexture'; File = 'VideoFrameSource.cs'; Why = '2026-10-19 提前还相机：照构造时抓的引用还原，不用 scrCamera.instance（切场景后会打偏）' }
    @{ Pat = 'camBackground.targetTexture'; File = 'VideoFrameSource.cs'; Why = '同上：第二台相机' }
    @{ Pat = 'camMain.targetTexture';   File = 'VideoFrameSource.cs'; Why = '同上：主相机' }
    @{ Pat = 'cam.camobj';              File = 'VideoFrameSource.cs'; Why = '同上：构造时从 scrCamera 取三台相机的入口' }
    # -- 2026-10-19 捕获期预览相机（镜厅不只发生在收尾，整个捕获期都缺清屏）----
    # 三台相机被劫持到我们的 RT 之后，backbuffer 没有任何相机清屏：世界冻在最后一帧，
    # 而 IMGUI 每帧照常往上叠 ⇒ 越叠越亮、文字重影。补一台只清屏 + 贴回当前帧的相机。
    @{ Pat = 'CameraClearFlags.SolidColor'; File = 'VideoFrameSource.cs'; Why = '2026-10-19 捕获期预览相机：每帧擦净 backbuffer，否则上一帧 IMGUI 留残影（镜厅来源）' }
    @{ Pat = 'Color.black';             File = 'VideoFrameSource.cs'; Why = '同上：擦屏底色' }
    @{ Pat = 'Graphics.Blit';           File = 'VideoFrameSource.cs'; Why = '同上：把当前捕获纹理贴到已被本相机清黑的 backbuffer' }
    @{ Pat = 'Object.DontDestroyOnLoad'; File = 'VideoFrameSource.cs'; Why = '同上：预览宿主跨场景存活，随 RestoreTargets 一起销毁' }
    @{ Pat = 'previewCamera.';          File = 'VideoFrameSource.cs'; Why = '同上：预览相机的 clearFlags/cullingMask/depth/allowHDR/allowMSAA/backgroundColor 全套设置（各参数用意见代码注释）' }
    @{ Pat = 'previewHost.AddComponent'; File = 'VideoFrameSource.cs'; Why = '同上：预览宿主上挂机相机与贴图组件（相机自身实现不了 OnPostRender，必须独立组件接收）' }
    @{ Pat = 'Source.texture';          File = 'VideoFrameSource.cs'; Why = '同上：贴图组件从帧源取当前捕获纹理' }
    @{ Pat = 'VideoRender.Outcome.Completed'; File = 'VideoRenderJob.cs'; Why = '2026-10-19 显式终态：成功分支（保留 Output 供界面显示文件名）' }
    @{ Pat = 'VideoRender.Outcome.Failed'; File = 'VideoRenderJob.cs'; Why = '同上：失败分支（Output 清空，界面改显示错误原因）' }
    @{ Pat = 'VideoRender.SuppressSceneExitCancel'; File = 'VideoRenderJob.cs'; Why = '2026-10-19 Esc 取消：我们主动调 SwitchToEditMode 时抑制回调，避免正常收尾被误判成取消' }
    @{ Pat = 'wantPin.ToString';        File = 'VideoRenderJob.cs'; Why = '2026-10-19 锚点自愈日志：打出重锚前的钉扎值' }
    @{ Pat = 'Enum.TryParse';           File = 'Patch_Lifecycle.cs'; Why = '2026-10-19 判定模式自动对齐：从回放数据解析目标难度（原处是直接比较后拒绝播放）' }
    @{ Pat = 'Features.VideoRender.VideoRender.CancelForSceneExit'; File = 'Patch_Lifecycle.cs'; Why = '2026-10-19 Esc 退出试玩要同步取消渲染作业（否则相机仍钉在 RT 上 ⇒ 镜厅 + 卡死）' }
    @{ Pat = 'VideoRender.VideoRender.NotifyPlaybackRestarted'; File = 'Patch_Lifecycle.cs'; Why = '2026-10-19 捕获期 Start_Rewind 二次触发 = 时间轴失效，通知作业中止' }
    @{ Pat = 'System.Diagnostics.Stopwatch'; File = 'VideoRenderJob.cs'; Why = '2026-10-20 等待预算改真实秒数：必须用 Stopwatch，Time.realtimeSinceStartup / unscaledTime 已被 UnscaledDeltaPin 换成虚拟时钟（等待期 TickFrame 一次不调 ⇒ 读出来冻住）' }
    @{ Pat = 'waitClock'; File = 'VideoRenderJob.cs'; Why = '同上：真实时钟句柄' }
    @{ Pat = 'Elapsed.TotalSeconds'; File = 'VideoRenderJob.cs'; Why = '同上：Stopwatch 的计时读数（超时日志里打真实等了多久，方便下次看到底是慢还是真死）' }
    @{ Pat = 'AudioListener.pause'; File = 'VideoRenderJob.cs'; Why = '2026-10-20 等待期每帧解除全局音频暂停：倒计时与歌曲调度全靠 AudioSettings.dspTime，暂停 ⇒ 停在 Countdown 不动' }
    @{ Pat = 'songNow.ToString'; File = 'VideoRenderJob.cs'; Why = '2026-10-20 倒计时就放行：这条日志是复测判据——出现它就说明成片从倒计时开始录（上游直接等 PlayerControl，没有倒计时提前录制的需求）' }
    @{ Pat = 'countdownEnd.ToString'; File = 'VideoRenderJob.cs'; Why = '2026-10-20 倒计时长度按谱面派生（countdownTicks×crotchetAtStart÷countdownSpeedMultiplier）：这条日志把算出来的终点打出来，用户复测时能直接核对成片开头是否覆盖整段倒计时' }
    @{ Pat = 'Mathf.Max'; File = 'VideoRenderJob.cs'; Why = '同上：拍数与倍率都做了下限保护（拍数至少 1、countdownSpeedMultiplier 至少 0.01），避免除零或空转；上游没有「按谱面派生倒计时窗口」这段' }
    @{ Pat = 'rendervideo.replay_not_taken'; File = 'VideoRenderJob.cs'; Why = '2026-10-20 超时兜底文案：没有具体拒绝原因时也要报一条可读的话，而不是空着' }
    # ══════════════════════════════════════════════════════════════════
    # -- 2026-10-20 ReplayData 去字典化（用户："别拿 dict 表存字段"）--
    # 旧实现是四张 Dictionary<string,T>，33 个字段全靠字符串键寻址：每次读写都要算
    # 字符串哈希，键名写成 "bpm" 还是 "Bpm" 编译器一点忙帮不上，错了只能运行时炸。
    # 改成强类型可空自动属性：string 用 null 表示缺失，int?/bool?/double? 用
    # HasValue 表示缺失——这与旧版 ContainsKey 的"这个键在不在"**逐字等价**，
    # 所以全仓 143 处存在性判断是保义搬迁，没有一处退化成"缺失即默认值"。
    # 顺带 KeyEvent_list/HitContext_list 去掉下划线后缀改成 KeyEvents/HitSnapshots
    # （后者 2026-10-21 又随类型改名 HitContext→HitSnapshot 变成复数形），
    # reset() 改成 Reset()（C# 命名惯例）。
    #
    # 为什么按 `data.` 前缀登记而不是按文件：同一次改名横跨 12 个文件，逐文件写 12×33
    # 行只是噪音。`data.` 在本仓始终就是回放容器（SpectreState.data），`old.` 只在
    # ReplayDecoder 的旧格式桥里出现，`d.` 只在 SpectreAPI 的实参里出现——三者都是
    # 限定过的形态，不是 File.Copy 那种"任何文件都可能撞上"的泛名字。
    # 反过来：裸属性名（Bpm / StartTile …）绝不全局登记，只在 ReplayData.cs 里
    # 用 File 限定放行，否则等于给全仓开一道后门。
    @{ Pat = 'data.FileName'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["FileName"] → 强类型属性 data.FileName（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.KeybdSoundFileName'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["KeybdSoundFileName"] → 强类型属性 data.KeybdSoundFileName（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.KeybdSoundHash'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["KeybdSoundHash"] → 强类型属性 data.KeybdSoundHash（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.SongName'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["SongName"] → 强类型属性 data.SongName（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.ArtistName'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["ArtistName"] → 强类型属性 data.ArtistName（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.SceneName'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["SceneName"] → 强类型属性 data.SceneName（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.JudgeMode'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["JudgeMode"] → 强类型属性 data.JudgeMode（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.InternalLevelName'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["InternalLevelName"] → 强类型属性 data.InternalLevelName（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.LevelPath'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["LevelPath"] → 强类型属性 data.LevelPath（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.HitMarginLimit'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["HitMarginLimit"] → 强类型属性 data.HitMarginLimit（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.HoldBehavior'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["HoldBehavior"] → 强类型属性 data.HoldBehavior（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.LoadedMods'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["LoadedMods"] → 强类型属性 data.LoadedMods（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.DeviceId'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["DeviceId"] → 强类型属性 data.DeviceId（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.ModVersion'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["ModVersion"] → 强类型属性 data.ModVersion（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.JudgmentList'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["JudgmentList"] → 强类型属性 data.JudgmentList（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.StartTile'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["StartTile"] → 强类型属性 data.StartTile（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.EndTile'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["EndTile"] → 强类型属性 data.EndTile（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.FloorHash'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["FloorHash"] → 强类型属性 data.FloorHash（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.SpeedHash'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["SpeedHash"] → 强类型属性 data.SpeedHash（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.TimeHash'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["TimeHash"] → 强类型属性 data.TimeHash（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.Pitch'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["Pitch"] → 强类型属性 data.Pitch（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.LevelId'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["LevelId"] → 强类型属性 data.LevelId（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.AudioBufferSize'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["AudioBufferSize"] → 强类型属性 data.AudioBufferSize（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.MaximumUsedKeys'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["MaximumUsedKeys"] → 强类型属性 data.MaximumUsedKeys（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.IsOfficialLevel'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["IsOfficialLevel"] → 强类型属性 data.IsOfficialLevel（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.SpeedTrailMode'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["SpeedTrailMode"] → 强类型属性 data.SpeedTrailMode（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.QuickPitched'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["QuickPitched"] → 强类型属性 data.QuickPitched（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.IfNoFail'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["IfNoFail"] → 强类型属性 data.IfNoFail（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.Bpm'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["Bpm"] → 强类型属性 data.Bpm（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.SpeedTrail'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["SpeedTrail"] → 强类型属性 data.SpeedTrail（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.PercentXacc'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["PercentXacc"] → 强类型属性 data.PercentXacc（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.PlaybackSpeed'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["PlaybackSpeed"] → 强类型属性 data.PlaybackSpeed（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'data.KeybdSoundStartTick'; Why = '2026-10-20 去字典化：旧 data.strings/ints/bools/doubles["KeybdSoundStartTick"] → 强类型属性 data.KeybdSoundStartTick（字符串键哈希与拼写错误一起消失）' }
    @{ Pat = 'replayData.'; File = 'ReplayDecoder.cs'; Why = '同上：旧格式桥 FromReplayData 的形参从 old 改名 replayData，取值改为 replayData.属性 ?? 默认值（四个字典辅助方法已随之删除）' }
    @{ Pat = 'd.';     File = 'SpectreAPI.cs';    Why = '同上：GetLoadedReplayInfo 取值从 GetStr(d, 键) 辅助方法改为 d.属性 ?? 缺省，该辅助方法已删除' }
    @{ Pat = 'HasValue'; File = 'ReplayData.cs'; Why = '同上：FirstMissingRequiredKey 用 HasValue 判必填字段是否缺失（旧版是四张键清单 + 字典 ContainsKey，键名写错会静默漏检）' }
    @{ Pat = 'Clear';    File = 'ReplayData.cs'; Why = '同上：Reset() 清空两个事件流' }
    @{ Pat = 'Settings.RenderStopOn';  File = 'ConfigManager.cs'; Why = '2026-10-20 渲染中止热键（既有在途功能，非本次重构）：新设置项的持久化读写' }
    @{ Pat = 'Settings.RenderStopKey'; File = 'ConfigManager.cs'; Why = '同上：热键码字段的持久化读写' }
    @{ Pat = 'data.KeyEvents';   Why = '2026-10-20 去字典化随行的列表改名：旧 KeyEvent_list → KeyEvents（列表本身不是字典，改名只为去掉下划线后缀、与 C# 命名惯例一致）' }
    @{ Pat = 'data.HitContexts'; Why = '同上：旧 HitContext_list → HitContexts（去掉下划线后缀）。注意本条 Pat 写的是归一化后的上游形：我方源码现在写作 data.HitSnapshots，比对前已被 $renameFold 折回（见该处注释）' }
    @{ Pat = 'SpectreState.data.HitContexts'; Why = '同上：渲染侧经 SpectreState 读同一个容器（前缀更长，单列一条以免与上一条的减法顺序相争）' }
    @{ Pat = 'KeyEvents.';   File = 'ReplayDecoder.cs'; Why = '同上：旧格式桥里的 old.KeyEvent_list.ConvertAll 改名后留下的成员片段（receiver 已被 old. 那条吃掉）' }
    @{ Pat = 'HitContexts.'; File = 'ReplayDecoder.cs'; Why = '同上：旧格式桥里的 HitContext_list 改名后的成员访问片段（我方现写作 HitSnapshots.，比对前已折回）' }
    @{ Pat = 'ReplayDecoder.FromReplayData'; File = 'ReplayManager.cs'; Why = '2026-10-20 去字典化随行的桥方法改名：ConvertFromOldFormat → FromReplayData（旧名字描述的是重构前那个字典世界，已不存在，留着只会误导）' }
    @{ Pat = 'ReplayDecoder.ToReplayData';   File = 'ReplayManager.cs'; Why = '同上：ConvertToOldFormat → ToReplayData' }
    @{ Pat = 'Input.GetKeyDown'; File = 'VideoRenderJob.cs'; Why = '2026-10-20 渲染中止热键（既有在途功能，与本轮去字典化无关）：渲染循环里轮询该按键以请求停止' }
    @{ Pat = 'Settings.RenderStopOn';  File = 'VideoRenderJob.cs'; Why = '同上：读取“启用中止热键”开关' }
    @{ Pat = 'Settings.RenderStopKey'; File = 'VideoRenderJob.cs'; Why = '同上：读取中止热键码，交给 Enum.IsDefined 判有效' }
    # -- 2026-10-21 渲染起点改成玩家在编辑器里选中的那块砖 -------------------
    # 上游只认 ADOBase.editor.Play() 的默认起点（0 号砖或 GCS.checkpointNum 的
    # 残留值），所以从中间砖渲染要先手动起播一次。改成读选区后多了这段取值/钳制/日志。
    @{ Pat = 'Main.mod'; File = 'VideoRenderJob.cs'; Why = '2026-10-21 选区读失败时留一行日志（按 Play() 的 else 分支取 0 这件事要能查；上游没有选区起点这段）' }
    @{ Pat = 'e.Message'; File = 'VideoRenderJob.cs'; Why = '同上：把异常信息拼进那行日志（否则只知道失败了不知道为什么）' }
    @{ Pat = 'Mathf.Clamp'; File = 'VideoRenderJob.cs'; Why = '2026-10-21 起点砖按 floors.Count / floorLimit 钳制：选区是玩家手点的，可能越界（游戏 Play() 自己也会钳）' }
    # -- 2026-10-21 检查点开局（从中间砖渲染）也要放行 -----------------------
    # 从中间砖开始时 OnMusicScheduled 走 checkpoint 分支：Scrub 把音乐 seek 到
    # 该砖前 4 拍（倒计时领奏），然后 ChangeState(States.Checkpoint)。整段倒计时
    # 因此在 States.Checkpoint 里播，永远不出现 States.Countdown；上游的放行条件
    # 只认 Countdown / PlayerControl，那条路只覆盖「从 0 号砖开始」。
    @{ Pat = 'States.Checkpoint'; File = 'VideoRenderJob.cs'; Why = '2026-10-21 检查点开局放行：从中间砖渲染时倒计时整个在 Checkpoint 里播，只认 Countdown 会整段录不到（含 GET READY）' }
    # -- 2026-10-21 系统光标（真实 Windows 箭头，不是手绘）--------------------
    # 游戏不画软件光标，硬件光标也不在 backbuffer 里，所以帧里本来没有箭头。
    # 上游没有这个功能；这里按帧抓 DrawIconEx(DI_NORMAL) 出来的位图，
    # 在交付像素时按 premultiplied alpha 合成（句柄无效则回退手绘箭头）。
    @{ Pat = 'SystemCursor.TryAcquire'; File = 'VideoFrameSource.cs'; Why = '2026-10-21 系统光标：抓真实 Windows 光标位图（上游没有这个功能，手绘箭头只是兜底）' }
    @{ Pat = 'flight.Cursor'; File = 'VideoFrameSource.cs'; Why = '同上：光标图在 Grab 那一刻随 Flight 排队，交付时再取会与帧错位' }
    @{ Pat = 'head.Cursor'; File = 'VideoFrameSource.cs'; Why = '同上：出队后按这一帧自己记下的光标图合成' }
    # -- 2026-10-22 回放输入改成跨平台（用户报「跨平台的按键模拟就是屁用没有」）----
    # 上游只有 SendInput 一条路（Windows user32 + 要求窗口聚焦）。非 Windows / 未聚焦时
    # RDInput 收不到任何东西：mainPressCount 恒 0 ⇒ keyTimes 永不灌入 ⇒ 一次 Hit 都不发生；
    # GetMainHeldKeys 恒空 ⇒ holdKeys 恒空 ⇒ holding 恒假 ⇒ CheckPreHoldFail 把长按判死。
    # 现在把回放状态并进 RDInput 自己的查询结果（Patch_RDInput），游戏原有判定链自然成立。
    @{ Pat = 'ManagedInput.Clear';      File = 'ReplayPlayer.cs'; Why = '同上：开始/停止/跳转时清空托管按键状态，避免上一段的按住残留' }
    @{ Pat = 'ManagedInput.BeginFrame'; File = 'ReplayPlayer.cs'; Why = '同上：按下/抬起只在该事件到达的那一帧可见（同帧幂等，PlayKeyboardEvent 一帧被调两次）' }
    @{ Pat = 'ManagedInput.Note';       File = 'ReplayPlayer.cs'; Why = '同上：把回放按键记进托管状态，由 Patch_RDInput 并进 RDInput 的查询结果' }
    @{ Pat = 'ManagedInput.Enabled';    File = 'ReplayPlayer.cs'; Why = '同上：只在 SendInput 走不通（非 Windows / 未聚焦 / 关掉模拟按键）时启用托管注入，两条路同开会把一次按键数成两次' }
    @{ Pat = 'KeyboardSimulation.Available'; File = 'ReplayPlayer.cs'; Why = '同上：SendInput 是 user32 导出，非 Windows 上调用会抛 DllNotFoundException 而不是失败返回；先问平台' }
    @{ Pat = 'KeyboardSimulation.Available'; File = 'KeyboardSimulation.cs'; Why = '同上：平台探针本体（Application.platform 判 Windows），非 Windows 上 SendInputs 直接返回、SampleKeyState 退回 Unity Input' }
    @{ Pat = 'Application.platform';    File = 'KeyboardSimulation.cs'; Why = '同上：判据用运行时平台而不是 #if UNITY_STANDALONE_WIN——本 DLL 在 Unity 之外编译，UNITY_* 符号一个都没定义' }
    @{ Pat = 'RuntimePlatform.WindowsPlayer'; File = 'KeyboardSimulation.cs'; Why = '同上：平台探针的两个正面取值（Windows 播放器 / 编辑器）' }
    @{ Pat = 'RuntimePlatform.WindowsEditor'; File = 'KeyboardSimulation.cs'; Why = '同上' }
    @{ Pat = 'pbKeyState.Length';       File = 'KeyboardSimulation.cs'; Why = '同上：非 Windows 回退路上按缓冲区长度逐键采样' }
    @{ Pat = 'Application.isFocused';   File = 'ReplayPlayer.cs'; Why = '上游就是这一句（SendInput 需要窗口焦点）；现在额外用它决定托管注入是否让位' }
    @{ Pat = 'ManagedInput.Active';     File = 'ReplayPlayer.cs'; Why = '2026-10-22：「模拟按键」开关（原 Settings.LegacyEngine）已删除——它关掉后会让长条判定失效、键位显示变暗，是「关掉就坏」的假开关。判据回到只看 is_playing' }
    @{ Pat = 'ev.IsPressed';            File = 'ReplayPlayer.cs'; Why = '同上：托管状态按事件的按下/抬起更新' }
    @{ Pat = 'ManagedInput.Mirror';   File = 'ReplayPlayer.cs'; Why = '2026-10-22 三轮反馈后重构：SendInput 降级为纯镜像（只影响别的模组的按键显示），游戏输入由 ManagedInput 覆盖。镜像成败不改变任何判定，所以它的条件可以软（开关+聚焦）' }
    @{ Pat = 'ManagedInput.Mirror';   File = 'ManagedInput.cs'; Why = '同上：镜像开关本体。绝不能再参与「注不注入」的判断——前两版就是让它决定注入，判错导致两条路一起死' }
    @{ Pat = 'SampleKeyState';          File = 'KeyboardSimulation.cs'; Why = '同上：采样键盘状态的跨平台入口（Windows 用 GetKeyboardState，其余平台用 Input.GetKey 逐键）' }
    @{ Pat = 'Input.GetKey';            File = 'KeyboardSimulation.cs'; Why = '同上：非 Windows 的采样回退，与游戏自身 RDInputType_Keyboard 用同一份数据源' }
    @{ Pat = 'KeyboardSimulation.SampleKeyState'; File = 'ReplayRecorder.cs'; Why = '同上：录制侧改走 SampleKeyState，非 Windows 上不再直接调 user32' }
    # ══════════════════════════════════════════════════════════════════
    # -- 2026-10-22 跨平台回放输入（用户：「跨平台的按键模拟就是屁用没有」）--
    # 游戏读键盘的唯一入口 RDInput 两条实现都落在物理输入上（Input.GetKey* /
    # AsyncInput.GetKey*），回放数据一条也写不进去。这一整块是上游没有的能力：
    #   · 托管输入覆盖 RDInput 的三个查询点（Patch_InputInjection.cs 是新文件）；
    #   · 合成 SkyHook 事件让游戏异步链与订阅 KeyUpdated 的模组都看到按键，
    #     并在 KeyViewer 的 KeyInput.GetKey 上补一层桥接（都是新文件）。
    # 新文件本身不在比对清单里，这里登记的是**被它们牵连的既有文件**。
    @{ Pat = 'AsyncInputInjector';      File = 'ReplayPlayer.cs'; Why = '2026-10-22 跨平台回放输入：PumpInput 把事件经合成 SkyHook 事件送进异步链；判据用 Injectable（能否广播）而不是 Usable（游戏是否出队），否则非 PlayerControl 状态下订阅 KeyUpdated 的键位显示模组整段失明' }
    @{ Pat = 'AsyncInputInjector.Injectable'; File = 'ReplayPlayer.cs'; Why = '同上：广播判据本体' }
    @{ Pat = 'AsyncInputInjector.Healthy'; File = 'ReplayPlayer.cs'; Why = '同上：只有实际入队过才算能用（Usable 只是看起来能用），失败时交回托管覆盖兜底' }
    @{ Pat = 'AsyncInputInjector.Inject'; File = 'ReplayPlayer.cs'; Why = '同上：真正入队的入口' }
    @{ Pat = 'ev.SongPosition';        File = 'ReplayPlayer.cs'; Why = '同上：事件按它自己的歌曲位置换算 tick，不能用外层「现在」——否则漏帧补发的早先按键会被整体后移，判定角度跟着偏' }
    @{ Pat = 'GCS.d_oldConductor';     File = 'ReplayPlayer.cs'; Why = '同上：歌曲位置要按 conductor 的两套公式分别求（老 conductor 与 dsp 计时器的基准不同），照抄 scrConductor.cs:850-857' }
    @{ Pat = 'GCS.d_webglConductor';   File = 'ReplayPlayer.cs'; Why = '同上：WebGL 走老 conductor 分支' }
    @{ Pat = 'scrConductor.calibration_i'; File = 'ReplayPlayer.cs'; Why = '同上：歌曲位置的校准量，不做这一步回放会整体偏移' }
    @{ Pat = 'song.time';              File = 'ReplayPlayer.cs'; Why = '同上：老 conductor 分支的位置来源' }
    @{ Pat = 'KeyViewerBridge.TryInstall'; File = 'ReplayPlayer.cs'; Why = '2026-10-22 键位显示：回放开始时补挂 KeyViewer 的 KeyInput.GetKey 桥接（这些模组只读 Unity Input 或它们自己被补丁过的入口，而回放走托管覆盖 + SkyHook 合成事件，两条都不经过 Unity Input）' }
    @{ Pat = 'ReplayAngleGuard.IsSane'; File = 'Patch_Playback.cs'; Why = '2026-10-22 命中推进角度健全性：conductor.Rewind() 会把 crotchetAtStart 置 0（scrConductor.cs:355），下一次 Update 才补回，中间算出的角度是 ±Infinity，而 Infinity 让 `angle >= CurrAngle` 恒真，整份命中快照会在一帧内被吃完（现象是「播放一瞬间全部模拟完」且回不来）' }
    @{ Pat = 'float.IsInfinity';       File = 'Patch_Playback.cs'; Why = '同上：判据必须是 IsFinite。只判 NaN 不够——NaN 在 >= 里恒假本来就不吃数据，真正危险的是 ±Infinity' }
    @{ Pat = 'float.IsNaN';            File = 'Patch_Playback.cs'; Why = '同上：IsFinite 的另一半' }
    @{ Pat = 'hc.curFreeRoamSection';  File = 'Patch_Playback.cs'; Why = '同上：自由漫游段切换时命中快照要同步跳过，否则同一段会反复取到不属于当前段的快照' }
    @{ Pat = 'GetAngle(scrController controller)'; File = 'SpectreState.cs'; Why = '2026-10-22 同上：回放推进循环用补丁入参 __instance 判定砖块，却用无参 GetAngle() 取角度；两个 controller 不是同一个对象时砖块取自 A、角度取自 B，B 的星球角度可以任意大 ⇒ 恒真。必须支持传入调用方持有的那个实例' }
    @{ Pat = 'controller.chosenPlanet'; File = 'SpectreState.cs'; Why = '同上：具名 controller 后的连带引用' }
    @{ Pat = 'controller.playerOne';   File = 'SpectreState.cs'; Why = '同上：反向旋转判定' }
    @{ Pat = 'planetarySystem.isCW';   File = 'SpectreState.cs'; Why = '同上：反向旋转的判据（isCW 为假时角度取反，上游没有这个重载）' }
    @{ Pat = 'float.NaN';              File = 'SpectreState.cs'; Why = '同上：取不到 controller / 星球时返回 NaN，让上层 IsSane 拦下而不是拿 0 去比较（0 会误判成「还没到」）' })


Write-Output ""
Write-Output "=== 汇总：完全一致 $identical / 有差异 $diff ==="

# ── 可追溯性判定：**每一个**多出的 token 都要挂上登记 ──────────────────
# 第一版判的是"有没有任一条登记命中"⇒ 只要有 [SpectreReplay] 前缀这一条命中，
# 同文件里注入的任何漂移都会被判成"已登记"（实测 MUT-PORT1 就是这么漏过的：
# 加了 7 个 token 的假漂移，仍报 ✓ 1 条已登记）。这是"假通过"的又一种形态——
# **用"存在一个解释"代替"每个差异都有解释"**。现在改成逐 token 判定。
$unexplained = 0
foreach ($m in ($matched | Sort-Object Rel)) {
    $a = CodeOnly2 ([IO.File]::ReadAllText($m.Ours)) -Our
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
    # 登记项可带 File = 文件名（不含目录）：只在比对**该文件**时生效。
    # 泛名字（Mathf.Clamp / File.Copy / File.Exists）必须限定文件，否则等于
    # 给全仓开一道后门：任何文件的同类漂移都会被这条记录顺手放过。
    $selfName = [IO.Path]::GetFileName($m.Rel)
    foreach ($k in $knownDeviations) {
        if ($k.ContainsKey('File') -and $k.File -ne $selfName) { continue }
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
