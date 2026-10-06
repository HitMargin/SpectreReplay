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
#>
param(
    [Parameter(Mandatory = $true)][string]$Which,
    [switch]$ExpectGreen
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $root 'SpectreReplay'
$utf8 = New-Object Text.UTF8Encoding($false)
$touched = New-Object System.Collections.ArrayList

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
    foreach ($t in $touched) {
        $dst = PathOf $t.Rel
        Copy-Item $t.Saved $dst -Force
        Remove-Item $t.Saved -Force
        # Copy-Item 会保留源文件的 LastWriteTime，还原后该文件比上一次
        # 构建产物还旧 —— 增量构建据此判定"没变过"，继续沿用带变异的
        # 旧二进制，于是**下一次**变异跑的是上一次的代码（已实测：
        # MUT-P3 还原后 MUT-P4 仍报 ui.nokey）。顶到当下即可破除。
        (Get-Item $dst).LastWriteTime = Get-Date
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
    'MUT-P8' {
        # ①/②：把整个 Patch_HitError.cs 删掉 = 回到"没搬这两个补丁"的旧状态
        #       HitErrorData/CalibrationAdvisor 就此没有任何读写方
        $p = PathOf 'Features\Replay\Patch_HitError.cs'
        if (-not (Test-Path $p)) { throw 'MUT-P8：找不到 Patch_HitError.cs' }
        Copy-Item $p (Join-Path $env:TEMP ('mut_' + $Which + '_hit')) -Force
        [void]$touched.Add(@{ Rel = 'Features\Replay\Patch_HitError.cs'; Saved = (Join-Path $env:TEMP ('mut_' + $Which + '_hit')) })
        Remove-Item $p -Force
    }
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
    # ── 界面控制流 / 接线（UiLogicShapeCheck）─────────────────────
    'MUT-U1' {
        # ① 退回旧样子：停止录制按钮改回借用「取消保存」文案。
        #    这是本轮修掉的第三个真 bug——两个动作不同，点它想停录会连本局
        #    待存数据一起丢掉，玩家以为只是暂停了录制。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' 'Loc.T("record.stop")' 'Loc.T("record.cancelsavekey")'
    }
    'MUT-U2' {
        # ② 退回旧样子：文本框宽度写死 360，与别的行的控件列（CtrlW=240）不对齐，
        #    且整页包在 ScrollView 里，窄窗口下 LabelW+360 先溢出。
        #    **必须按行号改**：'GUILayout.Width(Gui.CtrlW)' 在 OnGui.cs 里合法出现
        #    5 次（滑条/下拉×2/按键框/文本框），整文件替换会命中 5 次。
        Backup 'Ui\OnGui.cs'
        $ls = Lines 'Ui\OnGui.cs'
        $n = 0
        for ($i = 0; $i -lt $ls.Count; $i++) {
            $t = $ls[$i].TrimStart()
            if ($t.StartsWith('//') -or $t.StartsWith('*')) { continue }
            # 只动 TextField 那一行（RowInput 的控件）
            if ($ls[$i] -match 'GUILayout\.TextField' -and $ls[$i] -match 'Gui\.CtrlW') {
                $ls[$i] = $ls[$i] -replace 'Gui\.CtrlW', '360f'
                $n++
            }
        }
        if ($n -ne 1) { throw "MUT-U2：代码行里 TextField+CtrlW 命中 $n 次（应为 1）" }
        SetLines 'Ui\OnGui.cs' $ls
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
        # 转换目标容器引入第二处来源：不看 render.format，写死 mp4。
        # 这类错法界面完全看不出——文件确实生成了，只是格式不是玩家选的那个。
        Backup 'Pages\Pages.cs'
        ReplaceExact 'Pages\Pages.cs' '(VideoEncoder.OutContainer)Mathf.Clamp(RenderOutputFormat, 0, 3), ff);' 'VideoEncoder.OutContainer.Mp4, ff);'
    }
    'MUT-X1' {
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
    'MUT-PORT1' {
        # 移植保真度：在 KeyboardSimulation.cs 里引入一处**上游没有、也登记不上**的
        # 改动 = 模拟"抄错"。port-diff-audit 应当报"未登记差异"并 exit 1。
        # 这条守的是 round 60 立的约定：每处与上游的差异都必须挂到一条修改记录。
        Backup 'Features\Replay\KeyboardSimulation.cs'
        ReplaceExact 'Features\Replay\KeyboardSimulation.cs' 'internal static class KeyboardSimulation' 'internal static class KeyboardSimulation { /* accidental drift */ internal static int __driftProbe = 1;'
    }
    default {
        throw "未知变异 $Which（可用：MUT-P0…P11 / MUT-U1 / U2 / U3 / MUT-K1 / MUT-A1 / A2 / A3 / MUT-C1 / C2 / C3 / MUT-T1 / MUT-R1 / MUT-L1 / MUT-L2 / MUT-V1 / MUT-V2 / MUT-X1 / MUT-X2 / MUT-E1 / MUT-N1 / MUT-N2 / MUT-D1 / MUT-F1 / MUT-I1 / MUT-G1 / MUT-W1 / MUT-K2 / MUT-D2 / MUT-PORT1）"
    }
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