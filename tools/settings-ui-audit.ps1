<#
  「有定义 + 有消费 + 无 UI」审计（round 49 修了键盘音三项，这条把其余一次扫完）。
  判定规则：
    A. 在 Settings.cs 里有 public static 字段
    B. 在 Settings.ConfigManager 之外的地方被读取（Settings.X 或直接 X）
    C. 在 Pages.cs / OnGui.cs 里**没有任何控件引用它**
  三类输出：
    1) wired      —— 有 UI 控件（正常）
    2) internal   —— 只在非界面代码里用（可能是纯状态/派生量，需人看）
    3) dead-ish   —— 全仓零读取（与 deadcode-audit 重叠，仅提示）
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $root 'SpectreReplay'
if (-not (Test-Path $src)) { Write-Output "✗ 源目录不存在：$src"; exit 3 }

$files = @(Get-ChildItem $src -Recurse -File -Filter *.cs -ErrorAction Stop |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })
if ($files.Count -eq 0) { Write-Output "✗ 没扫到任何 .cs"; exit 3 }

$texts = @{}
foreach ($f in $files) {
    $t = [IO.File]::ReadAllText($f.FullName)
    $t = [regex]::Replace($t, '(?m)//.*$', '')
    $t = [regex]::Replace($t, '/\*.*?\*/', '', 'Singleline')
    $texts[$f.FullName] = $t
}

$settingsFile = $files | Where-Object { $_.Name -eq 'Settings.cs' }
$uiFiles = @($files | Where-Object { $_.Name -eq 'Pages.cs' -or $_.Name -eq 'OnGui.cs' })
$uiText = ($uiFiles | ForEach-Object { $texts[$_.FullName] }) -join "`n"

# 纯 UI 几何/主题字段：由 Quartz 壳管，不在设置页露面属正常
$uiOwned = @(
    'UiPanelWidthFrac','UiPanelHeightFrac','UiPanelOpacity','UiPanelPosX','UiPanelPosY',
    'UiAccentR','UiAccentG','UiAccentB','UiFlyDuration','UiHotkey','UiPowerOn','TextSize'
)

$rows = @()
foreach ($m in [regex]::Matches($texts[$settingsFile.FullName],
    '(?m)^\s*public\s+static\s+(?:bool|int|float|string)\s+(\w+)')) {
    $name = $m.Groups[1].Value
    if ($uiOwned -contains $name) { $rows += [PSCustomObject]@{ N=$name; Cat='UI 壳自管' }; continue }

    # 界面上有没有控件引用。
    # **两代实现都错过，坑不一样，都记下**：
    #   第一代：把整份 UI 源码 join 成一个串再 -match ⇒ 某处有同名局部变量/参数
    #           就误判"有控件"（报了 108 个"无界面"，而 AutoSave 明明有控件）。
    #   第二代：改成逐行就近，但**控件调用常常跨行**——`OnGui.RowDropdown(
    #            "render.format", new[]{...},` 的下一行才是 `RenderOutputFormat, i=>...`。
    #           只认控件那一行 ⇒ RenderOutputFormat 等 5 个 Render* 全被误判成无界面，
    #           "无界面"虚高到 107（真实值应更小）。
    #   现在：认出控件起始行后，**往后看 4 行**（够覆盖 new[] {...} 的多行数组字面量）。
    $inUi = $false
    foreach ($uf in $uiFiles) {
        $lines = $texts[$uf.FullName] -split "`n"
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -notmatch 'OnGui\.(Row|RowSlider|RowHotkey|RowDropdown|RowInput)\(') { continue }
            for ($j = $i; $j -lt $lines.Count -and $j -lt $i + 5; $j++) {
                if ($lines[$j] -match ("\b" + [regex]::Escape($name) + "\b")) { $inUi = $true; break }
                # 这一句以分号结束就停，别把后续控件算进来
                if ($j -gt $i -and $lines[$j] -match ';') { break }
            }
            if ($inUi) { break }
        }
        if ($inUi) { break }
    }
    # 约定连线：RowInput 这类控件只收文案键，字段靠约定读写。判它有没有界面入口，
    # **要看未剥注释的 UI 源码里有没有显式点名该字段**（2026-10-06 round 52 起，
    # Pages.cs 对 render.outdir 就补了这么一句「对应 Settings.RenderOutputDir」）。
    # 剥了注释会连这句一起消失，所以这里单独读一份原文来判。
    $inDoc = $false
    if (-not $inUi) {
        foreach ($uf in $uiFiles) {
            $raw = [IO.File]::ReadAllText($uf.FullName)
            # 形态一：注释里写「…Settings.X」且带绑定/对应语义
            $pat1 = '(控件|对应|绑定|字段|走)[^r]{0,20}Settings\.' + [regex]::Escape($name) + '\b'
            # 形态二：「Settings.X 由…读写」
            $pat2 = 'Settings\.' + [regex]::Escape($name) + '\s*(由|是|作为)'
            if ($raw -match $pat1 -or $raw -match $pat2) { $inDoc = $true; break }
        }
    }
    $inUi = $inUi -or $inDoc
    # 非界面代码里有没有**真读取**。
    # **纯持久化不算消费**：ConfigManager 的 save/load（`X = Settings.X` 与
    # `Settings.X = sets.Value<bool?>("X") ?? …`）只是把值搬进搬出，不改变任何行为。
    # round 54 实测：LegacyPauseCalculation 被上一版数成"消费点 6 处"，而真实是
    # 1 定义 + 2 持久化 + 1 FeatureKeys 常量 + 1 load 行 = **零行为消费**。
    #
    # **但 Settings.cs 自己不能整个排除**：round 55 发现 CurrLanguage / KeyCodeConvert /
    # InputedKey 的**真实消费就在 Settings.cs 里**（语言切换 :377/378/385、键位重映射
    # :390/393/399/404）。整份排除会把它们误判成零读取。正确做法是只排除**定义那一行**。
    $consumed = 0
    $declPat = '(?m)^\s*public\s+static\s+(?:bool|int|float|string)\s+' + [regex]::Escape($name) + '\b'
    foreach ($k in $texts.Keys) {
        if ($uiFiles | Where-Object { $_.FullName -eq $k }) { continue }
        if ($k -match 'ConfigManager\.cs$') { continue }
        if ($k -match 'FeatureKeys\.cs$') { continue }
        $hay = $texts[$k]
        if ($k -eq $settingsFile.FullName) {
            # 只把"声明那一行"抠掉，同一文件里的其余读取照算
            $hay = [regex]::Replace($hay, $declPat, '')
        }
        $consumed += ([regex]::Matches($hay, '\b' + [regex]::Escape($name) + '\b')).Count
    }

    if ($inUi) { $cat = 'wired（有控件）' }
    elseif ($consumed -gt 0) { $cat = '!! 无界面但被消费' }
    else { $cat = '零读取（纯默认值）' }
    $rows += [PSCustomObject]@{ N=$name; Cat=$cat; C=$consumed }
}

$noUi = @($rows | Where-Object { $_.Cat -like '!!*' })
Write-Output "=== Settings 字段分类（共 $($rows.Count) 项）==="
Write-Output ("  wired（有控件）      : " + @($rows | Where-Object { $_.Cat -like 'wired*' }).Count)
Write-Output ("  UI 壳自管            : " + @($rows | Where-Object { $_.Cat -like 'UI*' }).Count)
Write-Output ("  !! 无界面但被消费    : " + $noUi.Count)
Write-Output ("  零读取（纯默认值）   : " + @($rows | Where-Object { $_.Cat -like '零读取*' }).Count)

if ($noUi.Count -gt 0) {
    Write-Output ""
    Write-Output "── 无界面但被真消费（玩家改不了）──"
    $noUi | Sort-Object N | ForEach-Object { "  {0,-34} 消费点 {1} 处" -f $_.N, $_.C }
}

# **零读取清单必须印出来。** round 54 改口径后这一类从 0 变成 83 项，
# 而最初脚本只印"无界面"那一类——于是这 83 个"有定义、有落盘、但不控制任何
# 行为"的设置成了**看不见的数字**。它们不是无害的：`LegacyPauseCalculation`
# 就是典型（上游 gate 两个编辑器补丁，我们没搬那两个类、Main.cs 也没注册
# SetFeatureToggle ⇒ 玩家改它毫无效果且无提示）。
# 印出来的目的不是让人去删，而是让人能判断每一个是"有意留着的兼容字段"
# 还是"该接线却忘了接"。
$dead = @($rows | Where-Object { $_.Cat -like '零读取*' })
if ($dead.Count -gt 0) {
    Write-Output ""
    Write-Output "── 零读取：有定义、有落盘，但不控制任何行为（共 $($dead.Count) 项）──"
    Write-Output "（报告型。删不删要逐项判断：有的是有意留着的旧版兼容字段，"
    Write-Output "  有的是「上游 gate 一个我们没搬的特性」——后者改了没效果也没提示，"
    Write-Output "  玩家会以为功能坏了。已核实的样本：LegacyPauseCalculation 属后者。）"
    $dead | Sort-Object N | ForEach-Object { "  " + $_.N }
}
# **恒为 0**：报告型脚本。真实的门禁在 PortedCompletenessCheck（只钉已补项）。
exit 0
