#  系统排查：每个补丁方法的**普通形参**（非 __ 注入项）是否都存在于目标方法上。
#
#  为什么重要：Harmony 对**普通参数**是按名注入的（从原方法的实参里取同名的），
#  写错名字或类型不匹配时行为是"注入 null / 默认值"而不是抛异常——
#  上一轮抓到的 __instance 静态问题正是这一类。官方规则：
#    "Each patch method can get all the arguments of the original method..."
#    普通参数必须与原方法形参**同名**才能拿到值。
#  另外也核对形参个数：多余/缺失的普通参数说明签名漂移了。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'SpectreReplay'
$asm = [Reflection.Assembly]::LoadFrom('D:\Projects\adofai-libs\Assembly-CSharp.dll')
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

$rows = @()
foreach ($f in $files) {
    $t = [IO.File]::ReadAllText($f.FullName)
    if ($t -notmatch 'SpectrePatch\(') { continue }
    $rel = $f.FullName.Replace("$src\", '')
    $lines = $t -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $at = $lines[$i].IndexOf('[SpectrePatch(')
        if ($at -lt 0) { continue }
        # 属性整段（可能跨行）
        $attr = $lines[$i].Substring($at)
        for ($j = $i + 1; $j -lt $lines.Count -and $j -lt $i + 4; $j++) {
            $attr += ' ' + $lines[$j]
            if ($lines[$j] -match '\)\s*$') { break }
        }
        $mType = [regex]::Match($attr, 'typeof\(\s*([\w\.]+)\s*\)')
        if (-not $mType.Success) { continue }
        $typeName = $mType.Groups[1].Value
        $method = ''
        $mn = [regex]::Match($attr, '"([^"]+)"')
        if ($mn.Success) { $method = $mn.Groups[1].Value }
        else {
            $nn = [regex]::Match($attr, 'nameof\(\s*[\w\.]+\.(\w+)\s*\)')
            if ($nn.Success) { $method = $nn.Groups[1].Value }
        }
        if ([string]::IsNullOrEmpty($method)) { continue }

        # 属性访问器（PropertyAccessor.Get/Set）、Transpiler、Finalizer 的形参**不按名注入**。
        # Transpiler 拿到的是整段 IEnumerable<CodeInstruction>，Harmony 不按名从目标
        # 方法取实参——所以它的 "instructions" 与目标 "Update()" 无参这件事无关。
        # 原实现算出了 $isTranspiler 却从没用过，于是把这个合法形状报成"缺形参"。
        $isAccessor = $attr -match 'PropertyAccessor\.'
        $isTranspiler = $attr -match 'PatchType\.Transpiler'
        $isFinalizer = $attr -match 'PatchType\.Finalizer'
        $byName = -not ($isAccessor -or $isTranspiler -or $isFinalizer)

        # 补丁方法签名
        $sig = $null
        for ($j = $i + 1; $j -lt $lines.Count -and $j -lt $i + 6; $j++) {
            $sm = [regex]::Match($lines[$j], 'internal\s+static\s+[\w\<\>\[\]\?\.]+\s+\w+\s*\(([^)]*)\)')
            if ($sm.Success) { $sig = $sm.Groups[1].Value; break }
        }
        if ($sig -eq $null) { continue }

        # 解析普通形参名（排除 __ 开头的注入项）
        $ordinary = @()
        foreach ($p in ($sig -split ',')) {
            $q = $p.Trim()
            if ($q -eq '') { continue }
            # 取最后一个标识符作为形参名（兼容 `ref double __result`、`bool isAuto = false`）
            $nm = [regex]::Match($q, '([A-Za-z_]\w*)\s*(=\s*.*)?$')
            if ($nm.Success) {
                $pname = $nm.Groups[1].Value
                if (-not $pname.StartsWith('__')) { $ordinary += $pname }
            }
        }
        # 非按名注入的类型根本没有『普通形参』这回事。
        if (-not $byName) { continue }
        if ($ordinary.Count -eq 0) { continue }

        # 反射真程序集
        $type = $asm.GetType($typeName)
        $verdict = '目标类型/方法未找到'
        if ($type) {
            # **属性也算目标。** 形如 songposition_minusi 是 scrConductor 的属性，
            # 只查 GetMethod 会把它报成"方法未找到"——那是对目标形状的误判，
            # 不是补丁写错了。
            $mi = $type.GetMethod($method, [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Static)
            if (-not $mi) {
                $pi = $type.GetProperty($method, [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Static)
                if ($pi) { $verdict = 'OK (属性访问器)' }
            }
            if ($mi) {
                $targetParams = $mi.GetParameters() | ForEach-Object { $_.Name }
                $missing = @($ordinary | Where-Object { $targetParams -notcontains $_ })
                if ($missing.Count -eq 0) { $verdict = 'OK' }
                else {
                    $verdict = "缺形参: " + ($missing -join ',')
                    if ($isAccessor) { $verdict += "（属性访问器，正常）" }
                }
            }
        }
        $rows += [PSCustomObject]@{
            File = $rel; Target = "$typeName.$method"
            Sig = $sig; Verdict = $verdict
        }
    }
}

Write-Output "=== 带普通形参的补丁（共 $($rows.Count) 处）==="
$rows | Sort-Object Verdict, File | Format-Table -Wrap | Out-String | Write-Output
$bad = @($rows | Where-Object { $_.Verdict -notlike 'OK*' -and $_.Verdict -notlike '*正常*' })
Write-Output ("需关注的: " + $bad.Count)
$bad | ForEach-Object { "  $($_.File)  $($_.Target)  sig=($($_.Sig))  => $($_.Verdict)" }

# **必须给退出码。** 原先到这里就结束了——verify.ps1 靠退出码判定，
# 于是这份审计永远绿，哪怕上面印着"需关注的: N"。
# exit 3 留给「脚本自己没跑成」，exit 1 才是「代码有问题」。
if ($bad.Count -gt 0) { exit 1 }
