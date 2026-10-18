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
    @{ Pat = 'ADOBase.conductor';                     Why = '官方关卡回放要记真实 BPM：走 conductor 而不是 scnGame.instance（后者在编辑器/重开时点可能为 null，而这段在 try 里，一抛就是整次录制失败）' }
    @{ Pat = 'c.bpm';                                 Why = '同上：conductor.bpm 与 levelData.bpm 是同一个值（scrConductor.SetupConductorWithLevelData 抄的），取不到返回 0' }
    @{ Pat = 'CurrentBpm';                            Why = '同上：把「官方关卡 BPM 恒为 0」的硬编码换成活取值，详情面板才不会显示成问号' }
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
    # 旧格式仅保留在解密侧作回落(存量文件与上游 Spectre 的 .sprp 都得读得开)，
    # 写侧只产出新格式。下面这批是 BCL 密码学 API 的名字，不是游戏 API，
    # 不会随游戏版本漂移;逐条登记而不是用一条宽泛串盖住整个文件。
    @{ Pat = 'HashAlgorithmName.SHA256';             Why = '2026-10-18 加密重写：PBKDF2 显式指定 HMAC-SHA256(与默认 SHA1 不同)' }
    @{ Pat = 'RandomNumberGenerator';                Why = '2026-10-18 加密重写：salt 与 IV 每文件随机取(确定性缺陷的根治点)' }
    @{ Pat = 'rng.GetBytes';                         Why = '2026-10-18 加密重写：同上，随机源取数' }
    @{ Pat = 'HMACSHA256';                           Why = '2026-10-18 加密重写：encrypt-then-MAC 的完整性校验' }
    @{ Pat = 'ComputeMac';                           Why = '2026-10-18 加密重写：MAC 计算入口(覆盖魔数+版本+salt+IV+正文)' }
    @{ Pat = 'ConstantTimeEquals';                   Why = '2026-10-18 加密重写：定长时间比较(避免提前返回泄漏前缀匹配)' }
    @{ Pat = 'DecryptV2';                            Why = '2026-10-18 加密重写：新容器版本的解密分支' }
    @{ Pat = 'DecryptLegacy';                        Why = '2026-10-18 加密重写：旧格式仅解密回落(写侧不再产出)' }
    @{ Pat = 'LegacyKeyStorageKey';                  Why = '2026-10-18 加密重写：上游那两个常量改名后仅服务回落解密' }
    @{ Pat = 'LegacyIVStorageKey';                   Why = '2026-10-18 加密重写：同上' }
    @{ Pat = 'LegacyDerive';                         Why = '2026-10-18 加密重写：旧派生的独立函数(无盐无迭代，只读路径用)' }
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
