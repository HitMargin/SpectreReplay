<#
  用了 `__result` 的 Postfix / passthrough Prefix，其目标方法是否真有返回值。
  Harmony 的 __result 只在目标方法非 void 时才注得进值；给 void 方法配 __result
  = 永远拿到默认值（同类"静默拿错值"家族）。
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'SpectreReplay'

# 游戏程序集位置：① 环境变量 ADOFAI_LIBS（CI 用）② 仓库同级的 adofai-libs
# ③ 历史绝对路径。理由同 instance-injection-audit.ps1。
$libs = if ($env:ADOFAI_LIBS -and (Test-Path $env:ADOFAI_LIBS)) { $env:ADOFAI_LIBS }
        elseif (Test-Path (Join-Path $root '..\adofai-libs')) {
            (Resolve-Path (Join-Path $root '..\adofai-libs')).Path }
        else { 'D:\Projects\adofai-libs' }
$asmPath = Join-Path $libs 'Assembly-CSharp.dll'
if (-not (Test-Path $asmPath)) {
    Write-Output "✗ 找不到游戏程序集：$asmPath（设 ADOFAI_LIBS 指到 adofai-libs 目录）"
    exit 3
}
$asm = [Reflection.Assembly]::LoadFrom($asmPath)

# **当前 build 号：版本门控的判据。** 补丁可带 MinVersion/MaxVersion，只在特定
# build 段挂载。本脚本必须知道当前是哪一段，否则分叉对里"属于别的段"的那条会被
# 拿当前 DLL 反射、当成缺陷报出来（本轮实测：r149 的 List 返回值补丁对着 r150 的
# void 目标）。反射 Releases.releaseNumber；拿不到就保持 -1 = 不启用门控跳过，
# 宁可多报也不静默漏判。
$release = -1
$relType = $asm.GetType('Releases')
if ($relType) {
    $relField = $relType.GetField('releaseNumber', [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static)
    if ($relField) { $release = [int]$relField.GetValue($null) }
}
# 读不到源目录时 Get-ChildItem 会去递归遍历父目录、在无关路径抛 AccessDenied
# （round 38 实测），报错指不到"路径指错了"这个根因。先在此处拦住。
if (-not (Test-Path $src)) {
    Write-Output "✗ 源目录不存在：$src"
    exit 3
}
$files = @(Get-ChildItem $src -Recurse -File -Filter *.cs -ErrorAction Stop |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.FullName -notmatch '\\tools\\' })
if ($files.Count -eq 0) {
    Write-Output "✗ 在 $src 下没扫到任何 .cs——计数为 0 之前先证明自己扫到了（假干净比误报危险）"
    exit 3
}

Write-Output "=== 用了 __result 的补丁，其目标方法的返回类型 ==="

$bad = @()
$gated = 0   # 因版本门控跳过判定的补丁数（正常现象，仅作可见性）
foreach ($f in $files) {
    $t = [IO.File]::ReadAllText($f.FullName)
    if ($t -notmatch '__result') { continue }
    $rel = $f.FullName.Replace("$src\", '')
    $lines = $t -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '\[SpectrePatch\(') { continue }
        $attr = $lines[$i].Substring($lines[$i].IndexOf('[SpectrePatch('))
        for ($j = $i + 1; $j -lt $lines.Count -and $j -lt $i + 4; $j++) {
            $attr += ' ' + $lines[$j]
            if ($lines[$j] -match '\)\s*$') { break }
        }
        $mType = [regex]::Match($attr, 'typeof\(\s*([\w\.]+)\s*\)')
        if (-not $mType.Success) { continue }
        $tn = $mType.Groups[1].Value
        $mn = [regex]::Match($attr, '"([^"]+)"')
        $m = ''
        if ($mn.Success) { $m = $mn.Groups[1].Value }
        else {
            $nn = [regex]::Match($attr, 'nameof\(\s*[\w\.]+\.(\w+)\s*\)')
            if ($nn.Success) { $m = $nn.Groups[1].Value }
        }
        if (-not $m) { continue }
        $sig = ''
        for ($j = $i + 1; $j -lt $lines.Count -and $j -lt $i + 6; $j++) {
            $sm = [regex]::Match($lines[$j], 'internal\s+static\s+[\w\<\>\[\]\?\.]+\s+\w+\s*\(([^)]*)\)')
            if ($sm.Success) { $sig = $sm.Groups[1].Value; break }
        }
        if ($sig -notmatch '__result') { continue }

        # **版本门控**：这条补丁只在其声明的 build 区间内挂载。区间不含当前 build 时，
        # 它针对的是另一版方法签名——用当前 DLL 反射它没有意义（典型：r149- 的
        # List 返回值补丁 vs r150+ 的 void 目标）。跳过并显式打印，不计入问题数。
        $minV = 0; $maxV = 0
        $g = [regex]::Match($attr, 'MinVersion\s*=\s*(\d+)')
        if ($g.Success) { $minV = [int]$g.Groups[1].Value }
        $g = [regex]::Match($attr, 'MaxVersion\s*=\s*(\d+)')
        if ($g.Success) { $maxV = [int]$g.Groups[1].Value }
        if ($release -gt 0 -and (($minV -gt 0 -and $release -lt $minV) -or ($maxV -gt 0 -and $release -gt $maxV))) {
            Write-Output ("  [门控跳过 r{0}] {1}  {2}.{3}  生效区间 r{4}-r{5}" -f $release, $rel, $tn, $m, $minV, $maxV)
            $gated++
            continue
        }

        $type = $asm.GetType($tn)
        $ret = '类型未找到'
        if ($type) {
            $pi = $type.GetProperty($m, [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance)
            if ($pi) { $ret = "property:" + $pi.PropertyType.Name }
            else {
                $mi = $type.GetMethod($m, [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Static)
                if ($mi) { $ret = if ($mi.ReturnType.Name -eq 'Void') { 'VOID <<< __result 拿不到值' } else { $mi.ReturnType.Name } }
            }
        }
        Write-Output ("  {0}" -f $rel)
        Write-Output ("    目标 {0}.{1}   返回={2}" -f $tn, $m, $ret)
        # 只有这两种才是真缺陷：__result 配 void 目标 = 永远拿默认值；
        # 类型/方法反射不到同样是坏的（补丁根本没挂在存在的东西上）。
        if ($ret -like 'VOID*' -or $ret -eq '类型未找到') {
            $bad += ("{0}  {1}.{2}  => {3}" -f $rel, $tn, $m, $ret)
        }
    }
}

if ($release -gt 0) {
    Write-Output ("（当前 build r{0}；版本门控跳过 {1} 条，未参与判定）" -f $release, $gated)
} else {
    Write-Output "（releaseNumber 反射失败，版本门控未启用——带 MinVersion/MaxVersion 的补丁也按当前 DLL 判定）"
}
Write-Output ("=== 问题数: " + $bad.Count + " ===")
$bad | ForEach-Object { Write-Output ("  " + $_) }
# **必须给退出码。** 原先这份脚本只打印不判定——verify.ps1 靠退出码判定，
# 于是它永远绿。exit 3 留给「脚本自己没跑成」，1 才是「代码有问题」。
if ($bad.Count -gt 0) { exit 1 }
