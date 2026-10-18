# 变异验证（移植 Spectre 后重写）：把修复退回旧样子，看闸门会不会变红。
#
# 移植后离屏闸门只保留三层：LocCheck（文案完整性）/ HardTextCheck（硬编码中文）/
# ReplayCheck（回放格式往返，真实编解码源码）。以下变异分别证明这三组断言真的会红：
#   MUT-P1   ReplayEncoder.WriteMetadata 交换 SongName/ArtistName 写入顺序
#   MUT-P2   ReplayDecoder.ReadCount 去掉越界防护（垃圾 count 必须被拒）
#   MUT-P3   Loc 键恢复大小写 typo（ui.noKey）→ LocCheck 必须红（反向对照 -ExpectGreen 全绿）
#   MUT-P4   页面写死中文 → HardTextCheck 必须红（反向对照 -ExpectGreen 全绿）
#   MUT-P5   文案表文件里写死界面中文（Loc.Hint 改回中文常量）→ 必须红
#   MUT-P6   删掉某语言的一条运行时文案（LocalizationManager 的 rendervideo.disabled）
#            → LocRuntimeCheck 必须红；这条专门证明“14 语言一致性”不是摆设
#   MUT-P7   只删中文表的一条键（render.progress）→ 表对账必须红
#             这类漂移在“页面引用到的键”视角下完全看不见，只有全量对账才抓得到
#   MUT-P0   等价改写（改注释措辞）→ 闸门必须保持全绿，证明"全绿"本身可达
#             注意：-ExpectGreen 只对**真的不该变红**的变异成立。P1~P4 都是
#             故意的缺陷注入，配 -ExpectGreen 一定红（那是设计如此，不是 bug）。
#
# 用法：
#     powershell -NoProfile -ExecutionPolicy Bypass -File tools\mutate.ps1 MUT-P1
#     powershell -NoProfile -ExecutionPolicy Bypass -File tools\mutate.ps1 MUT-P3 -ExpectGreen
# 跑完自动还原原文件。
#
# 注意：这个文件必须存成 UTF-8 带 BOM（同 verify.ps1）。
#
# 目标不在离屏套件里的变异（MUT-PORT1）：执行器按 $portAuditMutants 分派到
# tools\port-diff-audit.ps1，以它的退出码判定 —— 跑 UiPreview 对它永远是
# "0 失败"，判不出真假。
#>
param(
    [Parameter(Mandatory = $true)][string]$Which,
    [switch]$ExpectGreen,
    # MUT-PORT1 这类变异的目标没链进离屏套件，判据在独立审计脚本里。
    # 执行器默认按 $portAuditMutants 自动分派；此开关用于强制走审计路径。
    [switch]$PortAudit
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $root 'SpectreReplay'
$utf8 = New-Object Text.UTF8Encoding($false)
$touched = New-Object System.Collections.ArrayList

# 目标不在离屏套件里的变异：它们改的文件没链进 UiPreview（见 UiPreview.csproj 的
# 链接清单），于是编译进不去、跑 UiPreview 永远是"0 失败"，被误判成"断言是假的"。
# 真正抓它们的是独立的 tools\port-diff-audit.ps1，必须分派过去跑。
# 键 = 变异名；File = 审计报告里应当点名的文件——判据要对准"抓到的是这处漂移"，
# 光看退出码会把"本来就有别的未登记差异"误当成"这条变异被检出"。
$portAuditMutants = @{
    'MUT-PORT1' = @{ File = 'KeyboardSimulation.cs'; Why = 'port-diff-audit：未登记差异' }
}

# 相对路径的解析根。绝大多数变异改主项目源码（SpectreReplay\ 下），
# 但也有要改闸门自身的（tools\UiPreview\*.cs）。用 'repo:' 前缀显式指定仓库根，
# 不搞"猜路径"——猜错的失败方式是静默改到别的位置或抛"找不到"。
function PathOf([string]$rel) {
    if ($rel.StartsWith('repo:')) { return Join-Path $root $rel.Substring(5) }
    return Join-Path $src $rel
}

function Backup([string]$rel) {
    $p = PathOf $rel
    if (-not (Test-Path $p)) { throw "找不到 $rel" }
    # 临时名要剥掉 repo: 前缀——冒号不是合法路径字符
    $tag = (($rel -replace '^repo:', '') -replace '[\\/]', '__')
    $saved = Join-Path $env:TEMP ('mut_' + $Which + '_' + $tag)
    Copy-Item $p $saved -Force
    [void]$touched.Add(@{ Rel = $rel; Saved = $saved })
}

function Lines([string]$rel) { [IO.File]::ReadAllLines((PathOf $rel)) }

function SetLines([string]$rel, [string[]]$out) {
    [IO.File]::WriteAllLines((PathOf $rel), $out, $utf8)
}

function ReplaceExact([string]$rel, [string]$old, [string]$new) {
    if ($args.Count -ne 0) {
        throw ($rel + '：ReplaceExact 多出 ' + $args.Count + ' 个实参')
    }
    $lines = Lines $rel
    $hit = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Contains($old)) {
            $lines[$i] = $lines[$i].Replace($old, $new)
            $hit++
        }
    }
    if ($hit -ne 1) { throw "$rel 里 '$old' 命中 $hit 次（应为 1）" }
    SetLines $rel $lines
}

function Cleanup() {
    $failed = @()
    foreach ($t in $touched) {
        $dst = PathOf $t.Rel
        if (-not (Test-Path $t.Saved)) {
            $failed += ($t.Rel + ' 的备份不见了（' + $t.Saved + '）')
            continue
        }
        try {
            Copy-Item $t.Saved $dst -Force -ErrorAction Stop
            Remove-Item $t.Saved -Force -ErrorAction Stop
        } catch {
            $failed += ($t.Rel + ' 还原失败：' + $_.Exception.Message)
            continue
        }
        # Copy-Item 会保留源文件的 LastWriteTime，还原后该文件比上一次
        # 构建产物还旧 —— 增量构建据此判定"没变过"，继续沿用带变异的
        # 旧二进制，于是**下一次**变异跑的是上一次的代码（已实测：
        # MUT-P3 还原后 MUT-P4 仍报 ui.nokey）。顶到当下即可破除。
        (Get-Item $dst).LastWriteTime = Get-Date
    }
    if ($failed.Count -gt 0) {
        throw ('还原失败，源码可能仍停在变异态：' + ($failed -join '；'))
    }
}

trap {
    Cleanup
    Write-Host ("变异失败并已还原：" + $_.Exception.Message) -ForegroundColor Red
    exit 2
}

$ENC = 'Features\Replay\ReplayEncoder.cs'
$DEC = 'Features\Replay\ReplayDecoder.cs'
$LOC = 'Ui\Loc.cs'
$ONGUI = 'Ui\OnGui.cs'
$LMMGR = 'State\LocalizationManager.cs'

# ── 变异名唯一性自检（2026-10-18 实测踩到）────────────────────────────
# PowerShell 的 switch 是**字符串分派**：同一个 label 出现两次时，**只有第一个
# 会被选中，第二个成为永不执行的死代码** —— 而它的失效样子完全无声：
# 名字在脚本里搜得到、块里注释写得明明白白、跑 -Which <名> 也有输出，
# 只是跑的是别人的那条变异。
#
# 本次实测：三条加密变异插进来用了 MUT-C1/C2/C3 这组已经被「旧配置迁移」占用的
# 名字，于是它们永远轮不到执行。**计数还从 104 掉到 101**（去重后按名字数），
# 反而更容易被误读成「插进去的块丢了」——真正的病灶是重名。
#
# 注释里不要用 ASCII 双引号：PowerShell 在注释中仍把它当字符串定界符。
$__selfPath = $MyInvocation.MyCommand.Path
$__selfText = [IO.File]::ReadAllText($__selfPath)
$__swIdx = $__selfText.IndexOf('switch ($Which)')
# 锚点必须**拼接出来**，不能直接写成字面量：否则 IndexOf 会先命中本行自己，
# 把检查范围截成"自检代码本身"，于是永远查不到重名(实测踩到，静默空转)。
$__throwAnchor = 'throw "' + '未知变异'
$__thrIdx = $__selfText.IndexOf($__throwAnchor)
if ($__swIdx -ge 0 -and $__thrIdx -gt $__swIdx) {
    $__swBody = $__selfText.Substring($__swIdx, $__thrIdx - $__swIdx)
    $__labels = @([regex]::Matches($__swBody, "'(MUT-[A-Z0-9]+)'\s*\{") |
        ForEach-Object { $_.Groups[1].Value })
    $__dups = @($__labels | Group-Object | Where-Object { $_.Count -gt 1 })
    if ($__dups.Count -gt 0) {
        $__msg = (@($__dups | ForEach-Object { $_.Name + ' x' + $_.Count }) -join ', ')
        throw ('变异名重复：' + $__msg + ' —— switch 只会选中第一条，同名的其余定义永远不执行')
    }
}

switch ($Which) {
    # 无操作对照：只把一行注释改个说法，语义完全不变。它用来证明这套
    # 流程本身不会"无论如何都红"——如果连它都红，说明闸门或变异器件坏了，
    # 那 P1~P4 的红也不能当作证据。
    'MUT-P0' {
        Backup $LOC
        ReplaceExact $LOC '    /// 界面文案。中文/英文两套，按游戏语言自动选。' '    /// 界面文案。中文/英文两套，随游戏语言走。'
    }
    # Loc.cs / LocalizationManager.cs 在主检查里整文件豁免，所以藏在这两个文件里的
    # 界面中文必须有独立判据。这条变异把修复退回旧样子：Hint 从文案表键改回中文常量。
    'MUT-P5' {
        Backup $LOC
        ReplaceExact $LOC 'internal static string Hint => T("ui.hint");' 'internal const string Hint = "F9 开关界面 · ESC 关闭";'
    }
    # 运行时文案键检查（LocRuntimeCheck）的自证：删掉**某一个语言**里的一条键。
    # 选中德语那一条，因为它是文件里最后一份，DeleteLine 只删这一行不会误伤别处。
    # 这类缺陷在中文/英文环境下永远看不见（只有把游戏切成德语的玩家会看到原始键名），
    # 正是最该由闸门兜住、而旧闸门完全没覆盖的一类。
    'MUT-P6' {
        Backup $LMMGR
        ReplaceExact $LMMGR '["rendervideo.disabled"] = "Die Videoaufnahme ist in den Einstellungen deaktiviert",' '["rendervideo.disabled_REMOVED"] = "Die Videoaufnahme ist in den Einstellungen deaktiviert",'
    }
    # 表对账的自证：只删中文表里的一条键（英文表留着）。
    # 这模拟的是“改中文文案时手滑删了一行”“新键只加到了英文表”这类真实漂移。
    'MUT-P7' {
        Backup $LOC
        ReplaceExact $LOC '{ "render.progress", "进度" },' '{ "render.progress_REMOVED", "进度" },'
    }
    'MUT-P1' {
        Backup $ENC
        $ls = Lines $ENC
        $o1 = $ls | Select-String -SimpleMatch 'WriteStr(w, m.SongName);' | Select-Object -First 1
        $o2 = $ls | Select-String -SimpleMatch 'WriteStr(w, m.ArtistName);' | Select-Object -First 1
        if (-not $o1 -or -not $o2) { throw 'MUT-P1：找不到 SongName/ArtistName 写入' }
        $i1 = $o1.LineNumber - 1; $i2 = $o2.LineNumber - 1
        $tmp = $ls[$i1]
        $ls[$i1] = $ls[$i2]
        $ls[$i2] = $tmp
        SetLines $ENC $ls
    }
    'MUT-P2' {
        Backup $DEC
        ReplaceExact $DEC '        if (raw > maxByStream)' '        if (false && raw > maxByStream)'
    }
    'MUT-P3' {
        Backup $LOC
        ReplaceExact $LOC '{ "ui.nokey", "Not bound" },' '{ "ui.noKey", "Not bound" },'
    }
    'MUT-P4' {
        Backup $ONGUI
        ReplaceExact $ONGUI 'GUILayout.Label(text, Gui.NoteStyle, GUILayout.ExpandWidth(true));' 'GUILayout.Label("渲染中", Gui.NoteStyle, GUILayout.ExpandWidth(true));'
    }
    # ── 移植完整性（PortedCompletenessCheck）的自证 ──────────────────────
    #
    # 这一组把三个真缺口逐个退回"未接上"的旧样子。它们都是同一类形态：
    # 数据类搬过来了、喂它的代码没搬 —— 编译通过、闸门（在加这组断言之前）全绿，
    # 而功能对玩家等于不存在。
    'MUT-P9' {
        # ③：只删 Analyzer 的调用点，保留补丁 —— 证明判据盯的是调用点而非声明。
        # 只数**代码行**：方法名在注释里也出现（"为什么要在这里动手"那段），
        # 按裸文本匹配会命中 2 次，而第二处是注释，删不到真正的调用。
        Backup 'Pages\Pages.cs'
        $ls = Lines 'Pages\Pages.cs'
        $n = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            $t = $ls[$i].TrimStart()
            if ($t.StartsWith('//') -or $t.StartsWith('*')) { continue }
            if ($ls[$i] -match 'ReplayAnalyzer\.Analyze') { $ls[$i] = '                rep = null;'; $n++ }
        }
        if ($n -ne 1) { throw "MUT-P9：代码行里 Analyze 命中 $n 次（应为 1）" }
        SetLines 'Pages\Pages.cs' $ls
    }
    'MUT-P10' {
        # ④：只删 DescribeEncoder 的调用点 —— NVENC 降级回到"界面上看不见"。
        # 同 P9：只数代码行（注释里也提了这个方法名）。
        Backup 'Pages\Pages.cs'
        $ls = Lines 'Pages\Pages.cs'
        $n = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            $t = $ls[$i].TrimStart()
            if ($t.StartsWith('//') -or $t.StartsWith('*')) { continue }
            if ($ls[$i] -match 'VideoEncoder\.DescribeEncoder') { $ls[$i] = '                name = "";'; $n++ }
        }
        if ($n -ne 1) { throw "MUT-P10：代码行里 DescribeEncoder 命中 $n 次（应为 1）" }
        SetLines 'Pages\Pages.cs' $ls
    }
    'MUT-P11' {
        # ⑤：装弹后不告诉玩家"要重开才会播" —— 本轮修的第二个真 bug。
        #    点「回放」只把 PlayMode 置真，真正启动要等 Start_Rewind（重开/进关），
        #    而原来只弹一句 status.loaded（"已载入"），读起来像已经开始了。
        #    退回旧样子：把 playback.ready 的引用换成一个与状态无关的键。
        Backup 'Pages\Pages.cs'
        $ls = Lines 'Pages\Pages.cs'
        $n = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            $t = $ls[$i].TrimStart()
            if ($t.StartsWith('//') -or $t.StartsWith('*')) { continue }
            if ($ls[$i] -match 'Loc\.F\("playback\.ready"') { $ls[$i] = $ls[$i] -replace 'playback\.ready', 'playback.idle'; $n++ }
        }
        if ($n -ne 1) { throw "MUT-P11：代码行里 playback.ready 命中 $n 次（应为 1）" }
        SetLines 'Pages\Pages.cs' $ls
    }
    'MUT-P12' {
        # ⑪b：把"更改保存目录"的控件删掉，退回**只读展示**。
        #    这是本行的原始形态：界面**看着有这一项**（有一行 Gui.KeyValue 把路径
        #    摆出来），却没有任何控件能改它。Must 判据是 Loc 键出现在代码行里，
        #    所以只把那个键换成别的键即可复现"控件不再存在"。
        #    注意不能改成 record.savedir（那是标签自己的键，仍然合法存在），
        #    否则判据看着有键、实际是标签的——那就成了同义反复。
        Backup 'Pages\Pages.cs'
        $ls = Lines 'Pages\Pages.cs'
        $n = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            $t = $ls[$i].TrimStart()
            if ($t.StartsWith('//') -or $t.StartsWith('*')) { continue }
            if ($ls[$i] -match 'record\.changedir') {
                $ls[$i] = $ls[$i] -replace 'record\.changedir', 'record.savedir.pick'
                $n++
            }
        }
        if ($n -ne 1) { throw "MUT-P12：代码行里 record.changedir 命中 $n 次（应为 1）" }
        SetLines 'Pages\Pages.cs' $ls
    }
    'MUT-P13' {
        # ⑪：把 SavePath 的真消费方改掉（SpectreState 不再按它写回放），
        #     于是保存目录变成"有字段、有界面、但写到哪里都不影响"。
        #     复现的是 ⑪ 判据里"有真消费方"那一半。
        #    **必须把三处一起改掉**：判据是"符号在该文件里出现过"，只改一处的话
        #    另外两处（tmp.wav 的写入与改名）还留着 Settings.SavePath，断言照样绿
        #    ——那这条变异就证明不了判据。三处都在 SpectreState.cs 里。
        Backup 'State\SpectreState.cs'
        $ls = Lines 'State\SpectreState.cs'
        $n = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            $t = $ls[$i].TrimStart()
            if ($t.StartsWith('//') -or $t.StartsWith('*')) { continue }
            if ($ls[$i] -match 'Settings\.SavePath') {
                $ls[$i] = $ls[$i] -replace 'Settings\.SavePath', 'Main.mod.Path'
                $n++
            }
        }
        if ($n -ne 3) { throw "MUT-P13：代码行里 Settings.SavePath 命中 $n 次（应为 3）" }
        SetLines 'State\SpectreState.cs' $ls
    }
    'MUT-U6' {
        # ④：把阻塞式原生选择框**搬回画控件的方法体里**（不再 Defer）。
        #    这是本行最容易被写成的形态——"点了就弹框"读起来最直观。
        #    但它是在 IMGUI 的 Layout/Repaint 那一趟中途钻进原生模态窗，
        #    事件队列配对会错位。判据 ④ 必须因此变红。
        #    做法：把 Defer 调用换成直接调用，同时保证 PickFolder 仍出现在
        #    **画控件的方法体**里（判据靠"宿主方法名"识别，所以必须搬进去）。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' '                MainThread.Defer(PickSaveDirDeferred);' '                FileBrowser.PickFolder(SaveDirText(), null, null, null);'
    }
    'MUT-U7' {
        # ④b：同一形态的**第二个调用点**（渲染输出目录的选择框在 OnGui.RowInput 里）。
        #     做法与 U6 相同：把 Defer 换成直接调用，让 PickFolder 落在
        #     **画控件的方法体**（RowInput）里。
        #     这条单独存在，是因为 U6 只覆盖 Pages.cs——判据 ④ 只查一个文件的话，
        #     后加的那个调用点根本不会被拦（这正是"修了 A 处忘了 B 处"的形态）。
        Backup 'Ui\OnGui.cs'
        ReplaceExact 'Ui\OnGui.cs' '                MainThread.Defer(PickOutDirDeferred);' '                FileBrowser.PickFolder(Settings.SavePath, null, null, null);'
    }
    'MUT-U8' {
        # ⑤：把输入框底色改回**比卡片更暗**——就是 2026-10-12 用户报「键控还是黑的」
        #     的真实原因。控件确实是自绘的（占位文案都画得出来），错的是颜色：
        #     凹槽不比平面亮，看着仍是个黑洞。
        #     这一条能成立是因为该缺陷**在算术上可判**（Rec.709 亮度比较），
        #     所以判据 ⑤ 必须因此变红。
        Backup 'Ui\Gui.cs'
        ReplaceExact 'Ui\Gui.cs' 'internal static readonly Color FieldBg     = new Color(0.21f, 0.24f, 0.30f, 1f);' 'internal static readonly Color FieldBg     = new Color(0.07f, 0.08f, 0.11f, 1f);'
    }
    'MUT-U9' {
        # ⑥：把按钮的自定义样式**去掉**，退回吃内建皮肤（= 用户看到的"原生控件"）。
        #    注意这**不是**"改用 GUILayout.Button"——参考实现 ADOFAIMacro 也在用
        #    内建 Button，只是传了带圆角贴图的 GUIStyle（UIUtils.cs:326/674）。
        #    所以缺陷形态是"没传样式"，变异体也必须照这个形状注入。
        #    **必须按行号改**：'Gui.ButtonStyle,' 在 Pages.cs 里合法出现 4 次
        #    （回放/停止/停录/更改目录），ReplaceExact 会命中 4 次直接 throw。
        Backup 'Pages\Pages.cs'
        $ls = Lines 'Pages\Pages.cs'
        $n = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            $t = $ls[$i].TrimStart()
            if ($t.StartsWith('//') -or $t.StartsWith('*')) { continue }
            # 只动「停止录制」那一行（其余三处是别的按钮，判据覆盖的是"有没有样式"）
            if ($ls[$i] -match 'record\.stop' -and $ls[$i] -match 'Gui\.ButtonStyle') {
                $ls[$i] = $ls[$i] -replace 'Gui\.ButtonStyle', 'GUIStyle.none'
                $n++
            }
        }
        if ($n -ne 1) { throw "MUT-U9：record.stop + Gui.ButtonStyle 命中 $n 次（应为 1）" }
        SetLines 'Pages\Pages.cs' $ls
    }
    'MUT-U10' {
        # ⑥b：滑条退回内建 HorizontalSlider（轨道/滑块贴图来自内建皮肤，
        #      UMM 只改它的 fixedHeight/border/padding/margin、从不换贴图）。
        Backup 'Ui\OnGui.cs'
        ReplaceExact 'Ui\OnGui.cs' '            float now = Gui.Slider(track, value, min, max);' '            float now = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(Gui.CtrlW));'
    }
    'MUT-U11' {
        # ⑥d：把圆角贴图的缓存命中判据改成**永假**（每帧重建贴图）。
        #     Unity 贴图不受 GC 管理，控件每帧重画 ⇒ 持续泄漏显存。
        #
        #    两个约束（都是本项目踩过的）：
        #      ① ReplaceExact 是**逐行** .Contains，锚点必须是单行——
        #         写成多行字符串永远命中 0 次、脚本自己 throw。
        #      ② 条件不能写成字面 `false`：那会让 `return hit;` 成为不可达代码
        #         （CS0162），若开了 warnings-as-errors 就变成"编译不过"而非
        #         "被断言抓到"。用编译器证明不了的永假条件，语义等价又干净。
        #      `hit` 是 out 参数，无论条件真假都被赋值，不会有未赋值错误。
        Backup 'Ui\RoundedTex.cs'
        ReplaceExact 'Ui\RoundedTex.cs' `
            '            if (Cache.TryGetValue(key, out Texture2D hit) && hit != null)' `
            '            if (key.W == -1)'
    }
    'MUT-U12' {
        # ⑩：滑条右侧退回**只读显示**——正是用户 2026-10-17 要求改掉的那个形态。
        #     做法：把自绘输入的 Draw 换成 GUILayout.Label（只读），同时保留
        #     后面的提交逻辑不管（判据只看 RowSlider 体内还有没有 .Draw）。
        #     **为什么这条是本条的"正确变异形状"**：缺陷是"数值不可编辑"，
        #     而"不可编辑"最朴素的复现就是把控件换回 Label——不是删掉整段。
        Backup 'Ui\OnGui.cs'
        ReplaceExact 'Ui\OnGui.cs' `
            '            bool commit = st.Draw(field, Gui.LabelStyle, "", out canceled);' `
            '            GUILayout.Label(st.Text, Gui.LabelStyle, GUILayout.Width(FieldW)); bool commit = false; canceled = false;'
    }
    'MUT-U13' {
        # ⑪：滑条丢掉 hover 判定（把 hot 写死 false）。
        #     形态对准判据：判据要求 RowSlider/Slider 体内**同时**有 Hover( 与
        #     hover 色。这条只干掉"判鼠标位置"那一半——留下的 hover 色恒不生效。
        #     **不能只删颜色**：那样判据也红，但红的原因不是"没做 hover"，
        #     而是"没做完 hover"，两种形态要各有一条变异才分得清。
        Backup 'Ui\Gui.cs'
        ReplaceExact 'Ui\Gui.cs' `
            '                bool hot = Hover(track) || GUIUtility.hotControl == id;' `
            '                bool hot = false;'
    }
    'MUT-U14' {
        # ⑪b：开关丢掉 hover（只留非悬停色）。同 U13，钉的是"判了没换色"
        #      的另一半。
        Backup 'Ui\Gui.cs'
        ReplaceExact 'Ui\Gui.cs' `
            '            bool hot = Hover(track);' `
            '            bool hot = false;'
    }
    'MUT-U24' {
        # ⑫h：异步输入开关不读游戏侧状态（退回'有开关但没效果'的形态）。
        #      做法：把游戏侧判据换成读我们自己的设置值——编译照样过、
        #      提示照样画，但游戏侧没开时玩家永远不知道为什么。
        #      用唯一行做锚：RDC.useAsyncInput 在 Pages.cs 只出现一次。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' '                gameOn = RDC.useAsyncInput;' '                gameOn = GetDataFromAsyncInput;'
    }
    'MUT-U32' {
        # (4) 滑条那行不再登记说明(只删一处：其余四处仍在，整文件搜 Tip( 的假判据会绿)。
        # **两行一起删**：这条调用跨两行，只删首行会留下孤立的续行，括号深度
        # 从此错位，HardTextCheck 的 Debug.Log 记账跟着失效，误报两条不存在的
        # 硬编码中文(实测)。也不能写成 Gui.Tip2(...)：那是编译错误，证明的是
        # '编译不过'而不是'被判据抓到'。
        Backup 'Ui\OnGui.cs'
        $ls = Lines 'Ui\OnGui.cs'
        $n = 0
        $out2 = New-Object 'Collections.Generic.List[string]'
        $skip = 0
        foreach ($ln in $ls) {
            if ($skip -gt 0) { $skip--; $n++; continue }
            if ($ln.Contains('Gui.Tip(new Rect(track.x - LabelW, track.y,')) { $skip = 1; $n++; continue }
            $out2.Add($ln)
        }
        if ($n -ne 2) { throw ('MUT-U32 unexpected count=' + $n) }
        SetLines 'Ui\OnGui.cs' $out2.ToArray()
    }
    'MUT-U33' {
        # (6) ⑬f：只删掉**一个**行助手里的行距(其余五处仍在)。
        #      整文件搜 RowGap(); 的假判据会绿——这正是这条变异要证明的事。
        #      锚在 RowHotkey 的 GUILayout.EndHorizontal(); 之后那一段：
        #      该处 RowGap(); 紧跟着一个空行与 if (Event.current == null，
        #      三行合起来在文件里唯一。
        Backup 'Ui\OnGui.cs'
        $ls = Lines 'Ui\OnGui.cs'
        $n = 0
        $out2 = New-Object 'Collections.Generic.List[string]'
        # 锚定 RowHotkey 那一处：它的 RowGap(); 紧跟在 EndHorizontal(); 后面，
        # 而**紧跟其后的非空行**是 if (Event.current == null || _capturing。
        # 用"前一行 + 后一行"双锚，避免依赖行号(行号会随无关改动漂移)。
        for ($i = 0; $i -lt $ls.Count; $i++) {
            $ln = $ls[$i]
            $hit = $false
            if ($ln.Trim() -eq 'RowGap();' -and $i -gt 0 -and $ls[$i - 1].Trim() -eq 'GUILayout.EndHorizontal();') {
                $j = $i + 1
                while ($j -lt $ls.Count -and $ls[$j].Trim() -eq '') { $j++ }
                if ($j -lt $ls.Count -and $ls[$j].Contains('if (Event.current == null || _capturing != GetCaptureId')) {
                    $hit = $true
                }
            }
            if ($hit) { $n++; continue }
            $out2.Add($ln)
        }
        if ($n -ne 1) { throw ('MUT-U33 unexpected count=' + $n) }
        SetLines 'Ui\OnGui.cs' $out2.ToArray()
    }
    'MUT-U34' {
        # ⑯：去特效总开关退回上游的默认开。
        #      判据必须因此变红——它改的是**关卡内容本身**，默认开意味着
        #      "只想录一局"的人一进关就看到被摘干净的谱面。
        #      **注意这条变异的形态**：只把 false 改回 true，行首那截
        #      `public static bool EffectRemoverOn` 一个字节都没动。
        #      所以判"EffectRemoverOn 出现过"的假判据在它面前照样绿，
        #      必须判等号右边的值。
        Backup 'State\Settings.cs'
        ReplaceExact 'State\Settings.cs' 'public static bool EffectRemoverOn = false;' 'public static bool EffectRemoverOn = true;'
    }
    'MUT-U35' {
        # ⑰：把关卡文案改回「允许覆盖存档」（就是用户报的那句原文）。
        #      判据必须因此变红。同样，键名 effects.enablesave 没动，
        #      只有中文值变——判键名存在与否的假判据抓不到。
        Backup 'Ui\Loc.cs'
        ReplaceExact 'Ui\Loc.cs' '{ "effects.enablesave", "允许覆盖关卡" },' '{ "effects.enablesave", "允许覆盖存档" },'
    }
    'MUT-U36' {
        # ⑱b：只把去特效**其中一个**子分组的收尾实参删掉（退回"不挂总开关"）。
        #      判据必须因此变红。
        #      **这条变异特意只改六组里的一组**：若判据写成"全文件出现过
        #      }, EffectRemoverOn);"，另外五份会让它照样绿——那正是本变异
        #      要暴露的假判据形态。
        Backup 'Pages\Pages.cs'
        # **锚不接受多行**（ReplaceExact 是逐行 Contains），而这一行在文件里
        # 出现 6 次。所以用 Lines 定位：找 effects.decor.note 的下一行。
        $ls = Lines 'Pages\Pages.cs'
        $hit = -1
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($ls[$i].Trim() -eq 'OnGui.Note("effects.decor.note");') { $hit = $i + 1; break }
        }
        if ($hit -lt 0) { throw 'MUT-U36 unexpected count=-1（找不到 effects.decor.note）' }
        if ($ls[$hit].Trim() -ne '}, EffectRemoverOn);') { throw ('MUT-U36 anchor mismatch: ' + $ls[$hit].Trim()) }
        $ls[$hit] = '            });'
        SetLines 'Pages\Pages.cs' $ls
    }
    'MUT-U37' {
        # ⑱c：录制页某一行保存策略的末参删掉（只删一行，其余八行仍在）。
        #      同样只动一行，专门暴露"数出现过"的假判据。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' 'OnGui.Row("record.failsave", FailSave, v => { FailSave = v; Commit(); }, ReplayOn);' 'OnGui.Row("record.failsave", FailSave, v => { FailSave = v; Commit(); });'
    }
    'MUT-U38' {
        # ⑱d：渲染参数整块的 Dim 去掉（退回"总开关关了照样能调"）。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' 'Gui.Section("render.options", Gui.Accent, () => Gui.Dim(!RenderOn, DrawRenderOptions));' 'Gui.Section("render.options", Gui.Accent, () => DrawRenderOptions());'
    }
    'MUT-U39' {
        # ⑱e：键盘音音量那一行的末参删掉（偏移那行仍在）。
        #      这两行是**多行调用**，末参在第三行——正因如此，判据必须
        #      "判到语句结束"，只判第一行会漏掉它。
        Backup 'Pages\Pages.cs'
        # 同上：末参在**第三行**，ReplaceExact 是逐行匹配，用不了多行锚。
        # 用 Lines 定位：找 record.keybd.volume 起的那一段，把它的收尾行末参删掉。
        $ls = Lines 'Pages\Pages.cs'
        $start = -1
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($ls[$i].Contains('OnGui.RowSlider("record.keybd.volume"')) { $start = $i; break }
        }
        if ($start -lt 0) { throw 'MUT-U39 找不到 record.keybd.volume' }
        $end = -1
        for ($i = $start; $i -lt $ls.Count; $i++) {
            if ($ls[$i].Trim().EndsWith(');')) { $end = $i; break }
        }
        if ($end -lt 0) { throw 'MUT-U39 找不到该调用的收尾行' }
        if ($ls[$end].Trim() -ne 'KeybdSoundRecordActive);') { throw ('MUT-U39 anchor mismatch: ' + $ls[$end].Trim()) }
        # 正确删法：把**上一行**末尾的 `,` 改成 `);`，然后删掉末参那一行整行。
        # 只删末参那行会留下 `..., Commit(); },` 这样半句——编译不过，变异就
        # 退化成"预览器起不来"，证明力归零。
        $prev = $end - 1
        if (-not $ls[$prev].TrimEnd().EndsWith(',')) { throw ('MUT-U39 上一行不以逗号结尾：' + $ls[$prev].Trim()) }
        $ls[$prev] = $ls[$prev].TrimEnd().Substring(0, $ls[$prev].TrimEnd().Length - 1) + ');'
        $ls[$end] = $null
        $out2 = New-Object 'Collections.Generic.List[string]'
        foreach ($ln in $ls) { if ($null -ne $ln) { $out2.Add($ln) } }
        SetLines 'Pages\Pages.cs' $out2.ToArray()
    }
    'MUT-U26' {
        # (1)(2)只留 1 个都算没做。删掉某条 .tip 中文表项。
        Backup 'Ui\Loc.cs'
        ReplaceExact 'Ui\Loc.cs' ('{ "render.fps.tip", "成片帧率。越高越流畅，渲染耗时按比例增长。" },') ('')
    }
    'MUT-U27' {
        # (2) 只删英文表项(中英漂移)。
        Backup 'Ui\Loc.cs'
        ReplaceExact 'Ui\Loc.cs' ('{ "render.fps.tip", "Output framerate. Higher means smoother, and render time grows proportionally." },') ('')
    }
    'MUT-U28' {
        # (3) 删掉整段说明框绘制(内容齐但接线断)。
        Backup 'Ui\OnGui.cs'
        ReplaceExact 'Ui\OnGui.cs' ('            Gui.DrawTooltip();') ('')
    }
    'MUT-U29' {
        # (3) 把清空挪到页末(等价于没清空：本帧登记完好清掉)。
        Backup 'Ui\OnGui.cs'
        ReplaceExact 'Ui\OnGui.cs' ('            Gui.ResetTips();') ('')
    }
    'MUT-U30' {
        # (1) 开关不再登记说明。
        Backup 'Ui\Gui.cs'
        ReplaceExact 'Ui\Gui.cs' ('            Tip(RowHit(track, LabelW), labelKey);') ('')
    }
    'MUT-U31' {
        # (5) 说明框改成 Layout 趟也画。
        Backup 'Ui\Gui.cs'
        ReplaceExact 'Ui\Gui.cs' ('            if (Event.current.type != EventType.Repaint)') ('            if (false)')
    }
    'MUT-U25' {
        # ⑫i：把已删除的「界面语言跟随游戏」开关加回来（防回归变异）。
        #      用单引号字符串拼出双引号，避免在 here-string 外层写转义。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' ('                DrawAsyncInputRow();') ('                DrawAsyncInputRow(); OnGui.Row(' + [char]34 + 'record.followlang' + [char]34 + ', true, v => { });')
    }
    'MUT-U22' {
        # ⑫e：卸载顺序颠倒——把 UnLoadReplay 与 SwitchToRecordMode 对调。
        #      形态：State 已清空却还把 PlayMode 翻回去之后才换 data，
        #      中间态里 PlayMode/RecordMode 与 data 不一致（回放中却读到空 data）。
        #      **必须按行索引改**：这两行各自唯一，但要'对调'而不是'替换成别的文本'，
        #      ReplaceExact 表达不了（它是逐行 Contains，多行锚点命中 0 次——本轮实测）。
        Backup 'State\SpectreState.cs'
        $ls = Lines 'State\SpectreState.cs'
        $n = 0
        for ($i = 0; $i -lt $ls.Count - 1; $i++) {
            if ($ls[$i] -match 'UnLoadReplay\(out data\)' -and $ls[$i+1] -match 'SwitchToRecordMode') {
                $tmp = $ls[$i]
                $ls[$i] = $ls[$i+1]
                $ls[$i+1] = $tmp
                $n++
            }
        }
        if ($n -ne 1) { throw ('MUT-U22 unexpected count=' + $n) }
        SetLines 'State\SpectreState.cs' $ls
    }
    'MUT-U23' {
        # ⑫g：砍掉空闲态那处的卸载入口（'刚载入就又卸不掉'）。
        #      **必须按行号改**：DrawUnloadButton 出现 2 次。
        Backup 'Pages\Pages.cs'
        $ls = Lines 'Pages\Pages.cs'
        $n = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($ls[$i] -match 'PlayMode && data != null') {
                $ls[$i] = $ls[$i] -replace 'PlayMode && data != null', 'false'
                $n++
            }
        }
        if ($n -ne 1) { throw ('MUT-U23 unexpected count=' + $n) }
        SetLines 'Pages\Pages.cs' $ls
    }
    'MUT-U20' {
        # ⑫c：官方关卡跳转**不清 customLevelPaths**。
        #      形态：internalLevelName 写了、场景也重载了，但上一关的自制谱路径还留着。
        #      scnGame.Update 里是 internalLevelName ?? customLevelPaths[index] 二选一，
        #      残留值会把关卡抢走 —— 现象是'点开这一关，跳进了别的关'。
        Backup 'State\SpectreState.cs'
        ReplaceExact 'State\SpectreState.cs' 'GCS.customLevelPaths = null;' 'GCS.customLevelPaths = GCS.customLevelPaths;'
    }
    'MUT-U21' {
        # ⑫d：官方关卡那颗按钮退回纯提示文字（就是用户报'不能直接打开'的原样）。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' 'OpenReplayLevel.GoOfficial();' 'Noop();'
    }
    'MUT-U19' {
        # ⑮f：就绪判断退回只看 TranspilerReady（= 上游形状）。
        #      这是本轮真修过的 bug：TranspilerReady 只置 true 不置回 false，
        #      补丁卸载不清它 ⇒ '开→渲染→关→再渲染' 仍走确定性轨，
        #      一边帧号算 PinnedSongPosition、一边游戏读真实 dspTime，两条时间轴混用。
        Backup 'Features\VideoRender\VideoRenderJob.cs'
        ReplaceExact 'Features\VideoRender\VideoRenderJob.cs' 'bool useRenderClock = RenderClockSource.TranspilerReady && Settings.RenderClockOn;' 'bool useRenderClock = RenderClockSource.TranspilerReady;'
    }
    'MUT-U16' {
        # ⑮a：把渲染时钟补丁的 Feature 摘掉——退回上游那种'无条件常开'。
        #      判据 ⑮a 必须因此变红：开关会变成假的（界面上能拨、补丁却永远挂着）。
        Backup 'Features\VideoRender\VideoRenderClockPatches.cs'
        ReplaceExact 'Features\VideoRender\VideoRenderClockPatches.cs' 'PatchType.Transpiler, Feature = FeatureKeys.RenderClock)]' 'PatchType.Transpiler)]'
    }
    'MUT-U17' {
        # ⑮b：删掉 Main 里的 SetFeatureToggle 注册（换成注册一个不存在的功能名）。
        #      未注册的功能其补丁按'关闭'处理 ⇒ 开关永远打不开功能，
        #      而界面上毫无异常（玩家以为是自己没配对渲染参数）。
        Backup 'Main.cs'
        ReplaceExact 'Main.cs' 'SpectrePatchHub.SetFeatureToggle(FeatureKeys.RenderClock,' 'SpectrePatchHub.SetFeatureToggle(FeatureKeys.RenderManualSave,'
    }
    'MUT-U18' {
        # ⑮d：只写不读——保存时写 render_clock_on，读取时不写回 Settings。
        #      形态：开关能存进 Configs.json，但重启后永远回到默认（关）。
        #      这正是'⑦c 双向持久化'要抓的一半（另一半是只读不写）。
        Backup 'State\ConfigManager.cs'
        ReplaceExact 'State\ConfigManager.cs' 'Settings.RenderClockOn = ints.Value<bool?>' 'Settings.RenderClockOn = Settings.RenderClockOn; if (false) Settings.RenderClockOn = ints.Value<bool?>'
    }
    'MUT-U15' {
        # ⑪c：输入框丢掉 hover。
        #     把它替换成一个语义等价但不含 Hover( 的写法——注意**不能留未使用变量**
        #     （会让变异体"编译不过"而不是"被断言抓到"，证明力归零）。
        Backup 'Ui\TextField.cs'
        ReplaceExact 'Ui\TextField.cs' `
            '            bool hot = Gui.Hover(box);' `
            '            bool hot = false;'
    }
    # ── 界面控制流 / 接线（UiLogicShapeCheck）─────────────────────
    'MUT-U1' {
        # ① 退回旧样子：停止录制按钮改回借用「取消保存」文案。
        #    这是本轮修掉的第三个真 bug——两个动作不同，点它想停录会连本局
        #    待存数据一起丢掉，玩家以为只是暂停了录制。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' 'Loc.T("record.stop")' 'Loc.T("record.cancelsavekey")'
    }
    'MUT-U2' {
        # ② 退回**旧样子**：整行改回内建的 GUILayout.TextField，宽度写死 360。
        #    这一条同时复现两个缺陷，正是 2026-10-12 用户截图报的那个：
        #      (a) 内建输入框吃 ADOFAI 自己的深色皮肤（UMM 只重设
        #          label/button/slider/toggle，从不碰 GUI.skin.textField）
        #          ⇒ 深色卡片上是一条黑框；
        #      (b) 宽度写死 360，与别的行的控件列（CtrlW=240）不对齐，
        #          且整页包在 ScrollView 里，窄窗口下 LabelW+360 先溢出。
        #    **必须定位到那一行**：'_outDir.Draw' 在代码里只出现一次，
        #    而 'GUILayout.Width(Gui.CtrlW)' 合法出现 5 次，整文件替换会命中 5 次。
        # 替换行必须**自己撑得住编译**：Take 掉的那一句同时声明了 commit 并给
        # canceled 赋值，所以要一起补上，否则变异体是"编译不过"而不是"被断言抓到"。
        Backup 'Ui\OnGui.cs'
        ReplaceExact 'Ui\OnGui.cs' `
            '            bool commit = _outDir.Draw(box, Gui.LabelStyle, Loc.T("render.outdir.empty"), out canceled);' `
            '            _outDir.Text = GUILayout.TextField(_outDir.Text ?? "", GUILayout.Width(360f)); bool commit = false; canceled = false;'
    }
    'MUT-U4' {
        # ② 只复现**皮肤来源**那一半（宽度仍走 Gui.CtrlW，保持合法）：
        #    把自绘调用换成内建 GUILayout.TextField。UMM 从不重设
        #    GUI.skin.textField，它会沿用 ADOFAI 的深色皮肤 ⇒ 深色卡片上一条黑框。
        #    单独造这一条，是为了证明 ② 的"不许用内建输入框"那一句**自己也活着**
        #    ——U2 同时改了两处，看不出是哪一句在拦。
        Backup 'Ui\OnGui.cs'
        ReplaceExact 'Ui\OnGui.cs' `
            '            bool commit = _outDir.Draw(box, Gui.LabelStyle, Loc.T("render.outdir.empty"), out canceled);' `
            '            _outDir.Text = GUILayout.TextField(_outDir.Text ?? "", GUILayout.Width(Gui.CtrlW)); bool commit = false; canceled = false;'
    }
    'MUT-U5' {
        # ② 只复现**宽度**那一半（仍是自绘输入框，皮肤来源合法）：
        #    把取矩形时的宽度改回写死 360。它与别的行的控件列（CtrlW=240）不对齐，
        #    整页包在 ScrollView 里时窄窗口下先溢出。同 U4：单独造一条才分得清
        #    是 ② 的哪一句在拦。
        Backup 'Ui\OnGui.cs'
        ReplaceExact 'Ui\OnGui.cs' `
            '            Rect box = GUILayoutUtility.GetRect(Gui.CtrlW, 24f,' `
            '            Rect box = GUILayoutUtility.GetRect(360f, 24f,'
    }
    'MUT-U3' {
        # ③ 退回旧样子：分组边框改用 GetLastRect()。它返回的是"最后一个控件的
        #    矩形"而非组的矩形，描边会画在最后一个子控件上（卡片矮一截）；
        #    紧跟 Begin* 调用它还会直接抛异常。
        Backup 'Ui\Gui.cs'
        ReplaceExact 'Ui\Gui.cs' 'bottom = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true)).yMax;' 'bottom = GUILayoutUtility.GetLastRect().yMax;'
    }
    'MUT-K1' {
        # 运行时文案键存在性：把 14 张语言表里的 note.play_error2 全部改名。
        #   只删中文那一处不够——本断言判的是"键在运行时表里有没有定义"，
        #   逐语言对账归 LocRuntimeCheck 管（那边专治"只有中文看得见"）。
        #   这里要复现的是"键根本不存在"，14 处必须一起改。
        # note.play_error2 = 谱面/哈希不匹配，只在异常路径上被引用
        # （正常玩一整局走不到），漏了在中英文环境都发现不了；
        # GetLocalizedText 查不到时 return key，玩家会看到 "note.play_error2" 本身。
        Backup 'State\LocalizationManager.cs'
        $ls = Lines 'State\LocalizationManager.cs'
        $n = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($ls[$i] -match '\["note\.play_error2"\]') {
                $ls[$i] = $ls[$i] -replace '\["note\.play_error2"\]', '["note.play_error2_REMOVED"]'
                $n++
            }
        }
        if ($n -ne 14) { throw "MUT-K1：note.play_error2 命中 $n 次（应为 14 = 语言数）" }
        SetLines 'State\LocalizationManager.cs' $ls
    }
    'MUT-A1' {
        # 键盘音起始时间戳：退回旧样子——起录块内导体判空后 return。
        # 这会让 KeybdSoundStartTick 永久缺失 ⇒ 回放侧静默 return ⇒
        # **整段键盘音消失且没有任何提示**（HasKeybdSound 仍为 true）。
        # 同时那一帧的 Record_kbdstate 也被跳过（按键少采一帧）。
        Backup 'Features\Replay\Patch_Playback.cs'
        ReplaceExact 'Features\Replay\Patch_Playback.cs' 'data.doubles[KeybdSoundStartTick] = 0.0;' 'if (ADOBase.conductor == null) { return; }'
    }
    'MUT-A2' {
        # 键盘音起始时间戳：删掉"缺 tick 就补真值"的分支（把补记那一段判成恒假）。
        # 起录帧拿不到导体时 tick 会停在 0，之后再也补不上来。
        Backup 'Features\Replay\Patch_Playback.cs'
        ReplaceExact 'Features\Replay\Patch_Playback.cs' '&& pendingTick == 0.0)' '&& false)'
    }
    'MUT-A3' {
        # 键盘音起始时间戳：回放侧不再拒非正基准（拿 0 当基准会把 wav 从头
        # 对齐到歌曲中段，听起来像乱响，比不播更糟）。
        Backup 'Features\Replay\Patch_Playback.cs'
        ReplaceExact 'Features\Replay\Patch_Playback.cs' 'if (keybdStartTick <= 0.0)' 'if (false)'
    }
    'MUT-C1' {
        # 旧配置迁移：把 LoadConfigs 里的迁移调用删掉 = 回到"旧文件被静默按默认值读"。
        # 旧结构（顶层 PascalCase）与读取侧要的 Sets/Ints/Sets 分区名不同，
        # 不迁移时玩家原来的选择（如 Surface=1 整屏取帧）会被静默改回默认。
        Backup 'State\ConfigManager.cs'
        ReplaceExact 'State\ConfigManager.cs' 'MigrateLegacyConfig(json);' '// migrated'
    }
    'MUT-C2' {
        # 旧配置迁移：把"排除新版文件"的判据删掉。它防的是误判——
        # 没有它，一次正常保存后的新版文件会被再迁移一次。
        Backup 'State\ConfigManager.cs'
        ReplaceExact 'State\ConfigManager.cs' 'if (json["Sets"] != null || json["Ints"] != null || json["Strings"] != null)' 'if (false)'
    }
    'MUT-C3' {
        # 旧配置迁移：映射表漏一个键（AutoSave 不搬）。这正是本轮第一版真漏的两个之一，
        # 由"旧键全部有归属"那条判据抓到。
        Backup 'State\ConfigManager.cs'
        ReplaceExact 'State\ConfigManager.cs' 'MigrateBool(json, sets, "AutoSave", "AutoSave", Settings.AutoSave);' '// dropped'
    }
    'MUT-T1' {
        # 补丁目标预检的覆盖率自证：把 nameof(...) 的解析能力去掉。
        # 那会让三条补丁重新变成"未校验"——而它们正是偏差 21「钉时间轴
        # 从未生效」的修复所在（songposition_minusi 读写两端 + AdjustAngle）。
        # 覆盖率判据（解析条数 vs 源码声明数）必须因此变红。
        Backup 'repo:tools\UiPreview\PatchTargetCheck.cs'
        ReplaceExact 'repo:tools\UiPreview\PatchTargetCheck.cs' 'if (string.IsNullOrEmpty(s.MethodName))' 'if (false)'
    }
    'MUT-R1' {
        # 运行期反射目标：把 SetupNotification 的形参形状改掉 = 生产代码按旧签名调。
        # TriggerMessage 每弹一次提示就跑这一句，取不到就抛 MissingMethodException，
        # 现象是"提示根本不弹"而根因只有一行异常（探针不报这类按名字取的调用）。
        Backup 'State\SpectreState.cs'
        ReplaceExact 'State\SpectreState.cs' '[typeof(float), typeof(float), typeof(bool)]' '[typeof(float), typeof(float), typeof(bool), typeof(int)]'
    }
    'MUT-L1' {
        # 关卡探测判据：把移植后的判据退回 gameworld 布尔量。
        # 那正是错了三次的那条路——"表示在关卡里的布尔量"其实描述的是"玩到哪一步"，
        # 全都晚于"进关"这个时刻（第三版 gameworld 只在首次命中后置 true，整局都不录）。
        Backup 'Features\Replay\Patch_Lifecycle.cs'
        ReplaceExact 'Features\Replay\Patch_Lifecycle.cs' 'if (PlayMode)' 'if (PlayMode && ADOBase.controller.gameworld)'
    }
    'MUT-L2' {
        # 孤儿检查的自证：把 LevelProbeCheck 从 Driver 里摘掉。
        # 这正是它此前的真实状态——存在、写得好、一次都没跑，而闸门只会更绿。
        Backup 'repo:tools\UiPreview\Driver.cs'
        $ls = Lines 'repo:tools\UiPreview\Driver.cs'
        $n = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($ls[$i] -match 'LevelProbeCheck\.Run\(\)') { $ls[$i] = '            int levelFail = 0;'; $n++ }
        }
        if ($n -ne 1) { throw "MUT-L2：LevelProbeCheck.Run() 命中 $n 次（应为 1）" }
        SetLines 'repo:tools\UiPreview\Driver.cs' $ls
    }
    'MUT-V1' {
        # 批量转格式入口：退回"零调用点"的旧样子。
        # VideoConverter 搬了过来但一直没接线（Spectre 在 uGUI 页调），
        # 玩家完全看不到这个功能。删掉调用即回到那个状态。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' '_convertState = VideoConverter.Start(dir,' '_convertState = null; // mutated'
    }
    'MUT-V2' {
        # 转换目标容器**写死**（不看 Settings.ConvertTarget）。
        # 这类错法界面完全看不出——文件确实生成了，只是格式不是玩家选的那个。
        #
        # **2026-10-12 随判据 ⑦ 一起改**：方案 B 把转换格式与渲染格式解耦，
        # 转换处现在读 ConvertTarget 而不是 RenderOutputFormat，锚点必须跟着换。
        # 否则锚点命中 0 次、脚本自己 throw（exit 2，闸门不红）——本项目踩过。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' '(VideoEncoder.OutContainer)Mathf.Clamp(ConvertTarget, 0, 3), ff);' 'VideoEncoder.OutContainer.Mp4, ff);'
    }
    'MUT-V3' {
        # 转换目标**退回只读展示**（= 用户 2026-10-12 截图报的那个状态）。
        # 这一条盯的是"有界面但改不了"那一族缺陷的第 4 例：界面上有一行、
        # 看着像能选，实际只显示不可改。
        #    **变异体必须自己编译得过**（本项目踩过：删掉变量声明会变成
        #    "编译不过"而非"被断言抓到"，证明力归零）。所以不是简单删行，
        #    而是把可编辑的下拉换成等价的只读 Gui.KeyValue——两行一起换。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' `
            '                OnGui.RowDropdown("convert.target", new[] { "mp4", "mkv", "webm", "mov" },' `
            '                Gui.KeyValue("convert.target", "mp4", Gui.DimCol);'
        ReplaceExact 'Pages\Pages.cs' `
            '                    ConvertTarget, i => { ConvertTarget = i; Commit(); });' `
            '                // removed'
    }
    'MUT-V4' {
        # 转换目标**不落盘**：把 Settings.ConvertTarget 的持久化读回去掉。
        # 后果：界面上能选、当场生效，但重启后变回默认——正是本项目
        # 缺陷①「设置存不下来」那一族的形态（选了但记不住）。
        Backup 'State\ConfigManager.cs'
        ReplaceExact 'State\ConfigManager.cs' 'Settings.ConvertTarget = Mathf.Clamp(ints.Value<int?>("convert_target") ?? Settings.RenderOutputFormat, 0, 3);' '// dropped'
    }
    'MUT-V5' {
        # ⑫b：把「打开这一关」那颗**按钮**的文案换掉（= 入口不再是打开关卡）。
        #     这是 2026-10-12 用户报「为什么缺少打开定位关卡的按钮」的那个缺口。
        #
        #     **变异体必须编译得过**（本项目踩过）：不能把 if 条件删掉、
        #     留下后面两行悬空的实参。只换按钮内部的文案键即可——
        #     语法完整，而那一行不再出现 playback.openlevel。
        #     注意判据 ⑫b 要求"同一行同时出现 Button 与 playback.openlevel"，
        #     所以必须动 Button 那一行；只改 .official/.missing 那两条提示
        #     （它们是 Label）是证明不了判据的。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' `
            'if (GUILayout.Button(Loc.T(editorReplay ? "playback.openlevel.editor" : "playback.openlevel"),' `
            'if (GUILayout.Button(Loc.T(editorReplay ? "playback.idle" : "playback.idle"),'
    }
    'MUT-V6' {
        # ⑬：编辑器跳转**不等场景就绪**就定位起始砖。
        #     后果：谱面还没加载完就 SelectFloor，要么无效要么抛——
        #     而界面看不出异常，只是"编辑器打开的位置不对"。
        Backup 'State\SpectreState.cs'
        ReplaceExact 'State\SpectreState.cs' 'if (editor != null && !editor.isLoading' 'if (editor != null && true'
    }
    'MUT-V7' {
        # ⑬：编辑器定位把 cameraJump 写成 false（照搬离线渲染那一套）。
        #     离线渲染里镜头由回放接管所以用 false；交互式定位必须 true，
        #     否则打开关卡后镜头不跟到起始砖——玩家看到"打开了但视角在别处"。
        Backup 'State\SpectreState.cs'
        ReplaceExact 'State\SpectreState.cs' 'editor.SelectFloor(editor.floors[floor], cameraJump: true);' 'editor.SelectFloor(editor.floors[floor], cameraJump: false);'
    }
    'MUT-V8' {
        # ⑭：命中分布图去掉缓存（每帧重建贴图）。
        #     Unity 贴图不受 GC 管理，而图每帧都画 ⇒ 持续泄漏显存。
        #     条件不能写成字面 false（会让后续代码变不可达）；用编译器
        #     证明不了的永假条件。`_tex`/`_builtCount` 都是字段，无未赋值问题。
        Backup 'Ui\HitScatter.cs'
        ReplaceExact 'Ui\HitScatter.cs' '            if (_tex != null && ReferenceEquals(_builtFrom, source)' '            if (_builtCount == int.MinValue && ReferenceEquals(_builtFrom, source)'
    }
    'MUT-V9' {
        # ⑦：把"已被别的 mod 接管"的探测判据改坏，让 takeover 识别失效。
        #    这是 2026-10-12 用户日志里那条误导性失败的原始形态：真因是
        #    Spectre 的同类 transpiler 先占了 scrConductor.Update，而文案把
        #    排查方向带到"游戏改了 Update"上去。判据 ⑦ 要求三件套齐
        #    （先物化 + 探测 + 文案不再断言 game changed），这里动第二件。
        #    ReplaceExact 是逐行 Contains，锚点必须是单行。
        Backup 'Features\VideoRender\VideoRenderClockPatches.cs'
        ReplaceExact 'Features\VideoRender\VideoRenderClockPatches.cs' '                && (mi.DeclaringType?.Name == "RenderClockSource"))' '                && (mi.DeclaringType?.Name == "NopeNope"))'
    }
    'MUT-V10' {
        # ⑦：让"已被接管"的探测**永假**（探测失效，但结构看着原样）。
        #
        #    **为什么这个变异换过三次**：最贴近的形态是"把探测搬到 yield 迭代
        #    之后"，但 ReplaceExact 只能逐行替换，表达不了"搬移代码"。前两版
        #    （改物化行 / 改 AddRange 行）都因为另一行还在而被判据放过——
        #    那是**变异写不出来**，不是判据假。诚实记在这里，免得下轮重复试。
        #    现在改用可表达的等价缺陷：给探测条件再加一个恒假合取项，
        #    于是"已被接管"这条路再也进不去，行为与探测失效完全一致。
        Backup 'Features\VideoRender\VideoRenderClockPatches.cs'
        ReplaceExact 'Features\VideoRender\VideoRenderClockPatches.cs' '                && (mi.DeclaringType?.Name == "RenderClockSource"))' '                && (mi.DeclaringType?.Name == "RenderClockSource") && mi.Name == "NeverMatches"))'
    }
    'MUT-V11' {
        # ⑧：把 RenderBuffer 的原生容器退回 4096 对齐的分桶（上游 c36e7b3 的形态）。
        #    Unity 按传入数组的长度推进采集环形缓冲的读指针：长度大于实际样本会把
        #    读指针推过写指针，此后 GetSampleCountForCaptureFrame() 恒为 0、
        #    dspTime 冻结，于是走"静默→重启"、每次 Start() 只换来一个缓冲又立刻
        #    归零，三次预算用尽即判死。而"退回精确尺寸"的兜底分支从不触发，
        #    因为 Render 返回的是 true。
        #    2026-10-16 上游实测确认（第 1 帧 perChannel=256 正常、第 2 帧起恒 0）。
        #    锚点单行且全仓唯一；变异体用内联算式保持语法完整（不引用不存在的变量，
        #    否则会变成"编译不过"而不是"被断言抓到"，证明力归零）。
        Backup 'Features\VideoRender\VideoAudioTrack.cs'
        ReplaceExact 'Features\VideoRender\VideoAudioTrack.cs' '        _render = new NativeArray<float>(floats, Allocator.Persistent, NativeArrayOptions.ClearMemory);' '        _render = new NativeArray<float>(((floats + 4095) / 4096) * 4096, Allocator.Persistent, NativeArrayOptions.ClearMemory); // bucketed'
    }
    'MUT-V12' {
        # ⑨：把卡片底色退回"白底 + GUI.backgroundColor 乘算"（2026-10-16 修复前的形态）。
        #    这是**乘算**：卡片里圆角按钮烘好的底色会被再乘一遍卡片色，
        #    hover 与 normal 的色差从 20/23/26 被压成 3/3/5（≈1%，肉眼不可辨）。
        #    现象是"按钮没有 hover"，真因是"hover 被乘没了"。
        #    同项目里页签当初正因这条改成独立样式，而 Section 内按钮漏了——所以
        #    判据钉的是形态（Gui.cs 不许再出现 backgroundColor 赋值），不是那一颗按钮。
        Backup 'Ui\Gui.cs'
        ReplaceExact 'Ui\Gui.cs' '            _pad.normal.background = RoundedTex.Get(2, 2, 0, CardBg);' '            _pad.normal.background = Texture2D.whiteTexture;'
    }
    'MUT-V13' {
        # ⑨ 的第二半：底图对了，但又在 Section 里设回 backgroundColor（乘算还有效）。
        #    只钉"底图是卡色贴图"会漏掉这种形态：底色看着对，按钮 hover 照样被抹平。
        #    锚点单行且全仓唯一（整个 Gui.cs 里只有这一处把 CardBg 赋给 backgroundColor）。
        Backup 'Ui\Gui.cs'
        ReplaceExact 'Ui\Gui.cs' '            float top = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true)).y;' '            GUI.backgroundColor = CardBg; float top = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true)).y;'
    }    'MUT-X1' {
        # 跨线程可见性：把泵线程写、主线程读的 pumpFailure 退回普通字段。
        # 这类缺陷编译通过、跑起来也基本正常（x86 弱内存模型下常常碰巧看到新值），
        # 只在真机 JIT 重排时表现为"偶发卡住/状态不更新"——典型的"看着在工作"。
        Backup 'Features\VideoRender\VideoEncoder.cs'
        ReplaceExact 'Features\VideoRender\VideoEncoder.cs' 'private volatile Exception pumpFailure;' 'private Exception pumpFailure;'
    }
    'MUT-X2' {
        # 跨线程可见性：采集线程的停止标志退回普通字段。
        Backup 'State\AudioRecorder.cs'
        ReplaceExact 'State\AudioRecorder.cs' 'private volatile bool isRecording = false;' 'private bool isRecording = false;'
    }
    'MUT-E1' {
        # 回放列表退回只 glob 默认扩展名（.sprp）。LoadReplayAuto 三种格式全能读，
        # 只列一种的后果是另外两种文件"磁盘上有、界面上没有"——不报错、日志无异常。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' 'Features.Replay.ReplayManager.ReadableExtensions' 'new[] { Features.Replay.ReplayConstants.Extension }'
    }
    'MUT-N1' {
        # 持久化枚举护栏：把 HitMarginLimit 的 IsDefined 校验删掉 = 回到旧样子。
        # `Enum.TryParse("99")` 会成功并给出 (HitMarginLimit)99，然后被**直接赋给
        # GCS.hitMarginLimit**——scrFloor.LightUp 与判定路径都读它。回放文件是
        # 玩家可编辑的文本，这一项比 ReplayAnalyzer 那处更敏感。
        Backup 'Features\Replay\Patch_Lifecycle.cs'
        ReplaceExact 'Features\Replay\Patch_Lifecycle.cs' '&& Enum.IsDefined(typeof(HitMarginLimit), result))' ')'
    }
    'MUT-N2' {
        # 同族：ReplayAnalyzer 的 difficulty 护栏删掉。影响面小一些（只算错
        # 窗口显示宽度），但同属"数字串被当成合法枚举值"。
        Backup 'Features\Replay\ReplayAnalyzer.cs'
        ReplaceExact 'Features\Replay\ReplayAnalyzer.cs' '&& Enum.IsDefined(typeof(Difficulty), recorded))' ')'
    }
    'MUT-D1' {
        # Load() 启动行：把它从 try 之前**移到 try 里面**。
        # 这正是铁律⑤的地基：少了这行，"Load 抛异常"与"Load 根本没跑"在
        # Player.log 上长得一模一样（历史上 24 轮偏差核对全白做的根因）。
        # **第一版只改缩进、没挪位置**——判据看的是"在不在 try 前"，照旧全绿，
        # 于是是一条假变异（变异形状必须对准判据，这条老教训的第 N 次）。
        Backup 'Main.cs'
        $ls = Lines 'Main.cs'
        $logIdx = -1; $tryIdx = -1
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($logIdx -lt 0 -and $ls[$i] -match 'UnityEngine\.Debug\.Log\("\[SpectreReplay\] Load\(\)') { $logIdx = $i }
            if ($tryIdx -lt 0 -and $logIdx -ge 0 -and $ls[$i].Trim() -eq 'try') { $tryIdx = $i; break }
        }
        if ($logIdx -lt 0 -or $tryIdx -lt 0) { throw "MUT-D1：找不到启动行或 try（logIdx=$logIdx tryIdx=$tryIdx）" }
        $out = New-Object System.Collections.Generic.List[string]
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($i -eq $logIdx) { continue }
            $out.Add($ls[$i])
            if ($i -eq $tryIdx) { $out.Add('            ' + $ls[$logIdx].Trim()) }
        }
        SetLines 'Main.cs' ($out.ToArray())
    }
    'MUT-F1' {
        # 文案回退链：从**英文表**里注释掉一个键 = 回到"英文缺键"状态。
        # 这正是 LocFallbackCheck 存在的理由：LocCheck 只验"最终有没有露键名"，
        # 而英文缺键会走③静默降级到中文、不露键名 ⇒ 原本全绿。
        # 玩家切到英文看到中文，而中文环境下永远发现不了。
        Backup 'Ui\Loc.cs'
        $ls = Lines 'Ui\Loc.cs'
        $n = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            # 注意：正则写在单引号里，双引号**不要**写成 ""。PowerShell 单引号串中的
        # 双引号就是普通字符；写 "" 会变成两个引号、匹配不上（第一版就这么失败）。
        if ($ls[$i] -match '\{\s*"page\.record",\s*"Recording"\s*,?\s*\}') { $ls[$i] = '// ' + $ls[$i].Trim(); $n++ }
        }
        if ($n -ne 1) { throw "MUT-F1：英文 page.record 条目命中 $n 次（应为 1）" }
        SetLines 'Ui\Loc.cs' $ls
    }
    'MUT-I1' {
        # 补丁形参注入：把修好的 `conductor` 退回 `__instance`（Spectre 的原样）。
        # AsyncInputUtils.GetSongPosition 是**静态方法**，而 Harmony 的 __instance
        # 只在目标非静态时注入（内部是合成形参 MethodBodyReader+ThisParameter，
        # 不是按名匹配）⇒ 恒为 null ⇒ 走 `__result = 0.0` 分支 ⇒ 恒返回 0。
        # 编译零警告、运行零异常，是"静默拿到空值"那类。
        Backup 'Features\VideoRender\VideoRenderClockPatches.cs'
        ReplaceExact 'Features\VideoRender\VideoRenderClockPatches.cs' 'internal static bool Prefix(scrConductor conductor, ref double __result)' 'internal static bool Prefix(scrConductor __instance, ref double __result)'
    }
    'MUT-G1' {
        # 日志前缀：把一处 [SpectreReplay] 退回 [Spectre] = 回到移植时的原样。
        # 真实环境里 Spectre 模组确实装着并会跑（Player.log 第 127 行），
        # 两边都打 [Spectre] 时：日志无法区分来源，且铁律⑤要找的
        # `[SpectreReplay] Load() 进入` 会被 `[Spectre] ...` 混淆。
        Backup 'State\MainThread.cs'
        ReplaceExact 'State\MainThread.cs' '"[SpectreReplay] 主线程队列积压 ' '"[Spectre] 主线程队列积压 '
    }
    'MUT-W1' {
        # WAV 写侧契约：把 PCM 格式标签从 1 改成 6（非 PCM）。
        # 这正是 round 44 审出"读侧比写侧健壮、但其宽容从未被检验"后补的断言。
        # 写侧这么改 ⇒ 产出的文件读侧会以"不支持的音频格式"拒掉，
        # 键盘音整段失效而日志只有一行 error。
        Backup 'State\AudioRecorder.cs'
        ReplaceExact 'State\AudioRecorder.cs' 'BitConverter.GetBytes((short)1).CopyTo(header, 20);' 'BitConverter.GetBytes((short)6).CopyTo(header, 20);'
    }
    'MUT-K2' {
        # ⑨b：把键盘音的界面入口**整段**删掉 = 退回 round 49 之前的原样。
        # 那一态里三项设置有定义、有落盘、Patch_Playback 有四处消费，但玩家
        # 在界面上**一行都改不了**——MicrophoneOffset 是校准值，键盘声偏早/偏晚
        # 时它是唯一修正手段，没界面等于没这个功能。
        # **必须整段删（Gui.Section("record.keybd" … 配对的 });）**：
        # 第一版只删含键名的那些行，把 {、注释、}); 留在原处 ⇒ 花括号失配，
        # 于是 HardTextCheck 报出 6 条「[SpectreReplay] 硬编码」——那是判据被
        # 畸形代码带偏，不是这条变异真抓到了 6 个缺陷（"多条断言同时红"的老病症）。
        Backup 'Pages\Pages.cs'
        $ls = Lines 'Pages\Pages.cs'
        $start = -1
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($ls[$i] -match 'Gui\.Section\("record\.keybd"') { $start = $i; break }
        }
        if ($start -lt 0) { throw "MUT-K2：找不到 Gui.Section 里 record.keybd 的起点" }
        $end = -1
        for ($j = $start; $j -lt $ls.Count -and $j -lt $start + 24; $j++) {
            if ($ls[$j].Trim() -eq '});') { $end = $j; break }
        }
        if ($end -lt 0) { throw "MUT-K2：找不到该段收尾（起点行 $($start+1)）" }
        $out2 = New-Object System.Collections.Generic.List[string]
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($i -ge $start -and $i -le $end) { continue }
            $out2.Add($ls[$i])
        }
        SetLines 'Pages\Pages.cs' ($out2.ToArray())
    }
    'MUT-K4' {
        # ⑲a：把「已最新」的判据从 checkedOnce 改回 newest != null。
        #
        # 这正是**第一版实现的真实缺陷**，不是为凑变异硬造的：反编译 IL 证明
        # ParseRepository 只在 current < release 时才 stfld NewestVersion，
        # 版本相等时它保持 null。所以挂 newest != null 会让「已最新」永远不亮，
        # 玩家刚查完界面仍停在「未检查」——功能做了却看不见。
        # 判据 ⑲a 要求 else if 的条件必须是 checkedOnce。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' 'else if (checkedOnce)' 'else if (newest != null)'
    }
    'MUT-K4b' {
        # ⑲a2：把读 Params.LastUpdateCheck 的实现掏空，只留方法名与 try/catch
        # 的外壳（让人误以为接线还在）。掏空后「查过没有」无从判断，
        # 「已最新」这一态永远不可能亮——判据 ⑲a2 必须因此变红。
        Backup 'Pages\Pages.cs'
        $ls = Lines 'Pages\Pages.cs'
        $start = -1
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($ls[$i].Contains('private static bool LastUpdateCheckSeen()')) { $start = $i; break }
        }
        if ($start -lt 0) { throw 'MUT-K4b: cannot find LastUpdateCheckSeen' }
        # 方法体收尾：从签名后第一个顶格（4 空格缩进）的 } 往下找
        $end = -1
        for ($j = $start + 1; $j -lt $ls.Count -and $j -lt $start + 80; $j++) {
            if ($ls[$j] -eq '        }') { $end = $j; break }
        }
        if ($end -lt 0) { throw 'MUT-K4b: cannot find end of method body' }
        $out4 = New-Object System.Collections.Generic.List[string]
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($i -eq $start + 1) {
                $out4.Add('            return false;')
                $i = $end - 1
                continue
            }
            $out4.Add($ls[$i])
        }
        SetLines 'Pages\Pages.cs' ($out4.ToArray())
    }
    'MUT-K5' {
        # ⑲b：版本比较方向写反。`newest > current` 才是"有新版"，
        # 改成 `<` 之后这一支永远不亮——功能看着接好了、从不响，
        # 而玩家只会以为自己一直是最新。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' 'newest > current' 'newest < current'
    }
    'MUT-K6' {
        # ⑲c：当前版本改读 Info.Version（string）再 new Version(...)。
        # 编译得过（Version 构造器接受 string），但版本串不合规时会抛，
        # 而 OnGUI 抛异常的后果是整个设置面板被 UMM 收掉。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' 'return Main.mod != null ? Main.mod.Version : null;' 'return Main.mod != null && Main.mod.Info != null ? new Version(Main.mod.Info.Version) : null;'
    }
    'MUT-K7' {
        # ⑲d：把 info.json 的 Repository 清空。UMM 那边 IsNullOrEmpty 直接
        # 跳过 ⇒ 它根本不去下载版本清单 ⇒ NewestVersion 永远 null，
        # 而"字段还在"会让只判存在的弱判据照样绿——所以判据必须判非空。
        Backup 'info.json'
        ReplaceExact 'info.json' '"Repository": "https://raw.githubusercontent.com/HitMargin/SpectreReplay/master/repo.json"' '"Repository": ""'
    }
    'MUT-K3' {
        # LocRuntimeCheck 反向判据（表里不许有没人取的键）的自证。
        # 做法是给中文表加一条运行时永远取不到的键，而不是删一条：反向判据要抓的
        # 正是「表比代码多」，删键只会让正向那条（每语言都齐）变红，两个方向各须
        # 一条变异，否则分不清是哪一半在守。
        Backup $LMMGR
        $ls = Lines $LMMGR
        $at = -1
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($ls[$i].Contains('["note.cancel_save"] = ')) { $at = $i; break }
        }
        if ($at -lt 0) { throw 'MUT-K3: cannot find note.cancel_save line' }
        $out3 = New-Object System.Collections.Generic.List[string]
        for ($i = 0; $i -lt $ls.Count; $i++) {
            $out3.Add($ls[$i])
            if ($i -eq $at) { $out3.Add('            ["note.dead_probe_key"] = "who fetches this?",') }
        }
        SetLines $LMMGR ($out3.ToArray())
    }
    'MUT-D2' {
        # ⑩b：把回放驱动开关那段整段删掉 = 回到 round 53 之前的原样。
        # Settings.LegacyEngine 名字骗人（听着像"废弃的自研引擎"），实际它控制
        # 整个按键模拟回放链路：ReplayPlayer.cs:38 关掉后不回松开已按住的键、
        # :133 关掉后一个键都不注入。上游 Spectre 把它摆在回放设置首页
        # （标签「模拟按键」），我们移植时丢了 UI ⇒ 只剩 Configs.json 一条路。
        Backup 'Pages\Pages.cs'
        $ls = Lines 'Pages\Pages.cs'
        $start = -1
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($ls[$i] -match 'Gui\.Section\("playback\.drive"') { $start = $i; break }
        }
        if ($start -lt 0) { throw "MUT-D2：找不到 playback.drive 段起点" }
        $end = -1
        for ($j = $start; $j -lt $ls.Count -and $j -lt $start + 24; $j++) {
            # 收尾是 }); 之后紧跟另一行 Gui.Section，所以取第一个独立 });
            if ($ls[$j].Trim() -eq '});') { $end = $j; break }
        }
        if ($end -lt 0) { throw "MUT-D2：找不到该段收尾（起点行 $($start+1)）" }
        $out2 = New-Object System.Collections.Generic.List[string]
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($i -ge $start -and $i -le $end) { continue }
            $out2.Add($ls[$i])
        }
        SetLines 'Pages\Pages.cs' ($out2.ToArray())
    }
    # ── 2026-10-18 保存按钮实时刷新(用户报：拨「允许覆盖关卡」不实时生效)──
    'MUT-U40' {
        # ⑳a：把「允许覆盖关卡」拨动后的刷新删掉 = 退回用户报的那个样子。
        #      只删这一处：总开关那段里的 Restore/Refresh 仍在，整文件搜
        #      RefreshEditorSaveButtons 的假判据会绿。
        Backup 'Pages\Pages.cs'
        $ls = Lines 'Pages\Pages.cs'
        $n = 0
        $out2 = New-Object 'Collections.Generic.List[string]'
        foreach ($l in $ls) {
            if ($l.Trim() -eq 'EffectRemover.RefreshEditorSaveButtons();' -and $n -lt 2) {
                $n++
                # 第一处是「允许覆盖关卡」那行(总开关那处带缩进在 else 里)
                if ($n -eq 1) { continue }
            }
            $out2.Add($l)
        }
        if ($n -ne 2) { throw ('MUT-U40 unexpected count=' + $n) }
        SetLines 'Pages\Pages.cs' $out2.ToArray()
    }
    'MUT-U41' {
        # ⑳b：总开关只留「关掉→恢复」那一半(打开那半删掉)= 修复前的原样。
        #      这正是那条判据要抓的形态：只判出现过 RestoreEditorSaveButtons 会绿。
        Backup 'Pages\Pages.cs'
        $ls = Lines 'Pages\Pages.cs'
        $n = 0
        $out2 = New-Object 'Collections.Generic.List[string]'
        $skip = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($skip -gt 0) { $skip--; continue }
            # **不要写成跨行 if**：Windows PowerShell 在行尾是字符串字面量时不续行，
            # 下一行的 -and 会被当成新语句起始（实测整份 mutate.ps1 因此解析失败）。
            # 先取值再单行判断。
            $isElse = $ls[$i].Trim() -eq 'else'
            $isBrace = ($i + 1) -lt $ls.Count -and $ls[$i + 1].Trim() -eq '{'
            $isCall = ($i + 2) -lt $ls.Count -and $ls[$i + 2].Trim() -eq 'EffectRemover.RefreshEditorSaveButtons();'
            if ($isElse -and $isBrace -and $isCall) {
                $skip = 2; $n++
                if (($i + 3) -lt $ls.Count -and $ls[$i + 3].Trim() -eq '}') { $skip = 3 }
                continue
            }
            $out2.Add($ls[$i])
        }
        if ($n -ne 1) { throw ('MUT-U41 unexpected count=' + $n) }
        SetLines 'Pages\Pages.cs' $out2.ToArray()
    }
    'MUT-U42' {
        # ㉑a：把异步那条补丁的 Feature 摘掉 = 只挂轮询一条(静默半残)。
        #      这正是这条判据要抓的形态：只数「出现过 FeatureKeys.SpecialKeys」
        #      的假判据会绿(还有一处)。
        Backup 'Features\Replay\Patch_SpecialKeys.cs'
        $ls = Lines 'Features\Replay\Patch_SpecialKeys.cs'
        $n = 0
        $removed = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($ls[$i].Contains('Feature = FeatureKeys.SpecialKeys')) {
                $n++
                if ($n -eq 2) {
                    # **锚点必须与判据同源。** 第一版写的是 -replace ', Feature = ...'，
                    # 而实际代码里 Feature 前面是换行不是逗号 ⇒ 一次都没替换到；
                    # 计数却用 Contains(另一个条件) ⇒ 不 throw、变异静默没改文件，
                    # 闸门全绿被误判成"判据是假的"(实测)。现在按同一子串替换并
                    # 用替换后的差异做自检。
                    $before = $ls[$i]
                    # **必须整段删掉，不能加后缀。** 加 _DISABLED 的话原串仍是
                    # 前缀，任何子串计数判据都会照样命中（实测 MUT-U42 第一次
                    # 就是这么假绿的）。这里保留行尾的逗号/右括号，语法仍然完整。
                    $ls[$i] = $ls[$i].Replace('Feature = FeatureKeys.SpecialKeys, ', '')
                    $ls[$i] = $ls[$i].Replace('Feature = FeatureKeys.SpecialKeys)', ')')
                    if ($ls[$i] -ne $before) { $removed++ }
                }
            }
        }
        if ($n -ne 2) { throw ('MUT-U42 unexpected count=' + $n) }
        if ($removed -ne 1) { throw ('MUT-U42 锚点没改动任何字节(removed=' + $removed + ')') }
        SetLines 'Features\Replay\Patch_SpecialKeys.cs' $ls
    }
    'MUT-U43' {
        # ㉑b：把 Main 里 SpecialKeys 的注册删掉 = 回到「未注册按关闭处理」。
        Backup 'Main.cs'
        $ls = Lines 'Main.cs'
        $n = 0
        $out2 = New-Object 'Collections.Generic.List[string]'
        $skip = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            if ($skip -gt 0) { $skip--; continue }
            if ($ls[$i].Contains('FeatureKeys.SpecialKeys,')) {
                $n++
                # 整条调用跨多行：从这里扫到以 ');' 结尾那行一起删
                for ($j = $i; $j -lt $ls.Count; $j++) {
                    if ($ls[$j].TrimEnd().EndsWith(');')) { $skip = $j - $i; break }
                }
                continue
            }
            $out2.Add($ls[$i])
        }
        if ($n -ne 1) { throw ('MUT-U43 unexpected count=' + $n) }
        SetLines 'Main.cs' $out2.ToArray()
    }
    'MUT-U44' {
        # ㉑c：把键盘侧的 Escape 守卫删掉 = 允许放开取消键。
        #      判据要抓的是「存在 Escape 护栏」而不是「文件里提过 Escape」。
        Backup 'Features\Replay\Patch_SpecialKeys.cs'
        ReplaceExact 'Features\Replay\Patch_SpecialKeys.cs' 'if (code == KeyCode.Escape || code == KeyCode.None)' 'if (code == KeyCode.None)'
    }
    'MUT-U45' {
        # ⑧c：把旧格式扩展名塞回可读清单 = 列表又开始列已删除的格式。
        #      判据要抓的是「清单里不许有旧扩展名」;塞在注释里不算(HasCodeUse 剥注释)。
        #      故意**保留原句**(只在其后追加一项)，这样才暴露子串判据。
        Backup 'Features\Replay\ReplayManager.cs'
        ReplaceExact 'Features\Replay\ReplayManager.cs' '        ReplayConstants.Extension,           // .sprp  加密（默认）' '        ReplayConstants.Extension,           // .sprp  加密（默认） ".crpl2", ".crpl",'
    }
    'MUT-U46' {
        # ⑧b：把 `.psprp`(不解密变体)从可读清单删掉 = 少列一种格式。
        #      那种文件磁盘上有、列表里没有，玩家点不了也找不到。
        Backup 'Features\Replay\ReplayManager.cs'
        ReplaceExact 'Features\Replay\ReplayManager.cs' '        ReplayConstants.ExtensionNoEncrypt,  // .psprp 不解密' ''
    }
    'MUT-CR1' {
        # ③ 确定性回归：把 salt 与 IV 两个随机源都固定 = 退回旧格式那个缺陷。
        #      必须**两个都固定**才表达得了这条缺陷：只固定 IV 时 salt 仍随机、
        #      密钥仍每次不同，密文照样不同(第一版就是这么"没变红"的)。
        Backup 'Features\Replay\ReplayCrypto.cs'
        ReplaceExact 'Features\Replay\ReplayCrypto.cs' 'rng.GetBytes(salt);' 'for (int i = 0; i < salt.Length; i++) salt[i] = 0x5A;'
        ReplaceExact 'Features\Replay\ReplayCrypto.cs' 'rng.GetBytes(iv);' 'for (int i = 0; i < iv.Length; i++) iv[i] = 0x33;'
    }
    'MUT-CR2' {
        # ④⑤ 完整性回归：删掉 MAC 比对 = 篡改不再被拒。
        #      CBC 的性质是"改首块只毁首块、padding 完好"，所以只有 MAC 能拦。
        Backup 'Features\Replay\ReplayCrypto.cs'
        ReplaceExact 'Features\Replay\ReplayCrypto.cs' 'if (!ConstantTimeEquals(expected, mac))' 'if (false)'
    }
    'MUT-CR3' {
        # ⑤ MAC 覆盖范围回归：不再把 IV 纳入 HMAC。
        Backup 'Features\Replay\ReplayCrypto.cs'
        ReplaceExact 'Features\Replay\ReplayCrypto.cs' 'Buffer.BlockCopy(iv, 0, buf, p, IVLen); p += IVLen;' 'p += IVLen;'
    }
    'MUT-D11' {
        # ② 抽稀回归：退回「每格取第一个」——1499 击只画 500 根，
        #      统计里的 -166.4ms 恰好不在所在格首位，图上就找不到它。
        Backup 'Ui\HitScatterMath.cs'
        ReplaceExact 'Ui\HitScatterMath.cs' '                float a = Math.Abs(samples[i]);' '                float a = -1f;'
    }
    'MUT-D12' {
        # ① 着色回归：退回「Perfect 恒宽于 Pure」的错误假设——
        #      用 original 的顺序比较，让中间那档永远不可达。
        Backup 'Ui\HitScatterMath.cs'
        ReplaceExact 'Ui\HitScatterMath.cs' '            if (absMs <= tightMs) return Band.Best;' '            if (absMs <= wideMs) return Band.Best;'
    }    'MUT-D7' {
        # ㉓a 入口回归：文件名退回普通 Label，点不开详情。
        Backup 'Pages\Pages.cs'
        $lines = Lines 'Pages\Pages.cs'
        $hit = 0
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Contains('Gui.ClickableLabel(Path.GetFileNameWithoutExtension(path),')) {
                $lines[$i] = $lines[$i].Replace('Gui.ClickableLabel(Path.GetFileNameWithoutExtension(path),', 'GUILayout.Label(Path.GetFileNameWithoutExtension(path),')
                $hit++
            }
        }
        if ($hit -ne 1) { throw "MUT-D7 命中 $hit 次" }
        SetLines 'Pages\Pages.cs' $lines
    }
    'MUT-D8' {
        # ㉓b 副作用回归：读详情改用会碰键盘音的 LoadReplay。
        Backup 'Features\Replay\ReplayFileInfo.cs'
        ReplaceExact 'Features\Replay\ReplayFileInfo.cs' 'if (!ReplayManager.ReadReplayData(out data, path))' 'if (!ReplayManager.LoadReplay(out data, path))'
    }
    'MUT-D9' {
        # ㉓c 收起回归：删文件后不再收起展开位（下一帧去读不存在的路径）。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' 'ReplayInfoPanel.Forget(path);' ''
    }
    'MUT-D10' {
        # ㉓d 字面量回归：把判定文案键的**字面量数组整个拿掉**，只留动态拼接。
        #      这正是该判据拦的形态——键不再是字面量，Loc 反向判据会把
        #      那 16 个 key 判成死键（或者有人为了让判据过而把死键留在表里）。
        #      注意不能只改取值那一行：数组还在，字面量计数照样达标（假阴性）。
        Backup 'Features\Replay\ReplayFileInfo.cs'
        $lines = Lines 'Features\Replay\ReplayFileInfo.cs'
        $start = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Contains('string[] JudgmentLocaleKeys')) { $start = $i; break }
        }
        if ($start -lt 0) { throw 'MUT-D10 锚点未找到' }
        $end = -1
        for ($i = $start + 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim() -eq '};') { $end = $i; break }
        }
        if ($end -lt 0) { throw 'MUT-D10 收尾未找到' }
        $out = @()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($i -gt $start -and $i -lt $end) { continue }
            $out += $lines[$i]
        }
        SetLines 'Features\Replay\ReplayFileInfo.cs' $out
    }    'MUT-D3' {
        # ㉒b 两段式回归：把「第一次点击只进入待确认」那道早退去掉，
        #      第一次点击就直接删 —— 手滑一次那局回放就没了。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' 'if (!pending)' 'if (false)'
    }
    'MUT-D4' {
        # ㉒c 连带卸载回归：删掉当前载入的那份后不清 _loadedPath，
        #      界面会一直写着「已载入」一个不存在的文件。
        Backup 'Pages\Pages.cs'
        $lines = Lines 'Pages\Pages.cs'
        $start = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Contains('private static void AfterReplayDeleted')) { $start = $i; break }
        }
        if ($start -lt 0) { throw 'MUT-D4 锚点未找到' }
        $hit = 0
        for ($i = $start; $i -lt [Math]::Min($start + 40, $lines.Count); $i++) {
            if ($lines[$i].Contains('_loadedPath = "";')) {
                $lines[$i] = $lines[$i].Replace('_loadedPath = "";', '// mutated')
                $hit++
            }
        }
        if ($hit -ne 1) { throw "MUT-D4 命中 $hit 次" }
        SetLines 'Pages\Pages.cs' $lines
    }
    'MUT-D5' {
        # ㉒d 列表缓存回归：删完不刷缓存，被删的那行还会挂 1 秒，
        #      玩家再点一下就是删一个不存在的路径。
        Backup 'Pages\Pages.cs'
        $lines = Lines 'Pages\Pages.cs'
        $start = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Contains('private static void AfterReplayDeleted')) { $start = $i; break }
        }
        if ($start -lt 0) { throw 'MUT-D5 锚点未找到' }
        $hit = 0
        for ($i = $start; $i -lt [Math]::Min($start + 40, $lines.Count); $i++) {
            if ($lines[$i].Contains('_replayCache = null;')) {
                $lines[$i] = $lines[$i].Replace('_replayCache = null;', '// mutated')
                $hit++
            }
        }
        if ($hit -ne 1) { throw "MUT-D5 命中 $hit 次" }
        SetLines 'Pages\Pages.cs' $lines
    }
    'MUT-D6' {
        # ㉒a 入口回归：列表行里不再画删除按钮（功能整个消失）。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' 'DrawDeleteFileButton(captured);' ''
    }    'MUT-PORT1' {
        # 移植保真度：在 KeyboardSimulation.cs 里引入一处**上游没有、也登记不上**的
        # 改动 = 模拟"抄错"。port-diff-audit 应当报"未登记差异"并 exit 1。
        # 这条守的是 round 60 立的约定：每处与上游的差异都必须挂到一条修改记录。
        Backup 'Features\Replay\KeyboardSimulation.cs'
        ReplaceExact 'Features\Replay\KeyboardSimulation.cs' 'internal static class KeyboardSimulation' 'internal static class KeyboardSimulation { /* accidental drift */ internal static int __driftProbe = 1;'
    }
    default {
        throw "未知变异 $Which（可用：MUT-P0…P11 / MUT-U1 / U2 / U3 / U4 / U5 / MUT-K1 / MUT-A1 / A2 / A3 / MUT-C1 / C2 / C3 / MUT-T1 / MUT-R1 / MUT-L1 / MUT-L2 / MUT-V1 / MUT-V2 / MUT-V3 / MUT-V4 / MUT-V5 / MUT-V6 / MUT-V7 / MUT-V8 / MUT-V9 / MUT-V10 / MUT-V11 / MUT-V12 / MUT-V13 / MUT-X1 / MUT-X2 / MUT-E1 / MUT-N1 / MUT-N2 / MUT-D1 / MUT-F1 / MUT-I1 / MUT-G1 / MUT-W1 / MUT-K2 / MUT-D2 / MUT-P12 / MUT-P13 / MUT-U6 / MUT-U7 / MUT-U8 / MUT-U9 / MUT-U10 / MUT-U11 / MUT-U12 / MUT-U13 / MUT-U14 / MUT-U15 / MUT-U16 / MUT-U17 / MUT-U18 / MUT-U19 / MUT-U20 / MUT-U21 / MUT-U22 / MUT-U23 / MUT-U24 / MUT-U25 / MUT-U26 / MUT-U27 / MUT-U28 / MUT-U29 / MUT-U30 / MUT-U31 / MUT-PORT1）"
    }
}

# ── 分派：目标不在离屏套件里的变异，改跑独立审计脚本 ──────────────────
# 不这么分派的话，那些变异恒报"0 失败"⇒被记成"断言是假的"，而判据根本没被跑到。
$pa = $portAuditMutants[$Which]
if ($PortAudit -or $pa) {
    if (-not $pa) { $pa = @{ File = ''; Why = '（-PortAudit 强制）' } }
    $audit = Join-Path $root 'tools\port-diff-audit.ps1'
    if (-not (Test-Path $audit)) {
        Cleanup
        Write-Host '✗ 找不到 tools\port-diff-audit.ps1' -ForegroundColor Red
        exit 2
    }
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $audit 2>&1
    $code = $LASTEXITCODE
    Cleanup
    Write-Host ("已还原 " + $Which) -ForegroundColor Yellow
    if ($code -eq 3) {
        Write-Host "✗ port-diff-audit 环境缺失（exit 3）——无法判定 $Which" -ForegroundColor Red
        exit 2
    }
    $txt = ($out | Out-String)
    $hitFile = ($pa.File -eq '') -or ($txt -match [regex]::Escape($pa.File))
    $hitKind = $txt -match '未登记差异'
    # 三条同时成立才算"被检出"：审计非零、报的是未登记差异、且点名本次改的文件。
    # 只看退出码会把"本来就有别的未登记差异"误当成"这条变异被检出"（假通过）。
    $detected = ($code -ne 0) -and $hitFile -and $hitKind
    if ($ExpectGreen) {
        if (-not $detected) {
            Write-Host ("✓ $Which 反向对照成立：审计保持通过") -ForegroundColor Green
            exit 0
        }
        Write-Host ("✗ $Which 反向对照被检出") -ForegroundColor Red
        exit 1
    }
    if ($detected) {
        Write-Host ("✓ $Which 被检出（port-diff-audit exit $code：" + $pa.Why + "）") -ForegroundColor Green
        exit 0
    }
    if ($code -eq 0) {
        Write-Host ("✗ $Which 没有让审计变红——这条断言是假的（exit 0）") -ForegroundColor Red
    } else {
        Write-Host ("✗ $Which 让审计变红，但报的不是本次注入的漂移（未点名 $($pa.File)）") -ForegroundColor Red
        $out | Select-Object -Last 15 | ForEach-Object { Write-Host ('  ' + $_) }
    }
    exit 1
}

# 跑闸门
Push-Location (Join-Path $root 'tools\UiPreview')
$b = & dotnet build UiPreview.csproj -c Release --no-incremental --verbosity quiet 2>&1
$berrs = @($b | Select-String -Pattern 'error CS')
if ($berrs.Count -gt 0) {
    Pop-Location; Cleanup
    Write-Host '✗ 预览器自身编译不过（检查代码写错了，不是被测对象）' -ForegroundColor Red
    $berrs | Select-Object -First 5 | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) -ForegroundColor Red }
    exit 2
}
$out = & dotnet run -c Release 2>&1
Pop-Location

$bad = @($out | Where-Object { $_ -match '^\s+✗' })
$good = @($out | Where-Object { $_ -match '^\s+✓' })
foreach ($b2 in $bad) { Write-Host ('  ' + $b2.Trim()) -ForegroundColor Red }
Write-Host ('  断言 ' + ($good.Count + $bad.Count) + ' 条，失败 ' + $bad.Count)

Cleanup
Write-Host ("已还原 " + $Which) -ForegroundColor Yellow

if ($ExpectGreen) {
    if ($bad.Count -eq 0) {
        Write-Host ("✓ $Which 反向对照成立：闸门保持全绿") -ForegroundColor Green
        exit 0
    }
    Write-Host ("✗ $Which 反向对照被检出（失败 " + $bad.Count + " 条）") -ForegroundColor Red
    exit 1
}

if ($bad.Count -eq 0) {
    Write-Host ("✗ $Which 没有让闸门变红——这条断言是假的") -ForegroundColor Red
    exit 1
}
Write-Host ("✓ $Which 被检出（失败 " + $bad.Count + " 条）") -ForegroundColor Green
exit 0
