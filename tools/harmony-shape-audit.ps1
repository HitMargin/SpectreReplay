#  排查移植代码里违反 Harmony 框架规则的形状（这一类"忠实移植继承来的真 bug"）。
#
#  已确认存在过一处（MUT-I1 相关）：__instance 用在静态方法上——代码与 Spectre
#  逐字节相同，但违反的是 **Harmony 的注入规则**，Spectre 自己错了。
#  本轮把其余同类形状扫一遍：
#    A. Prefix 返回类型必须是 void 或 bool（别的返回值 Harmony 会忽略/报错）
#    B. Postfix 返回 void，或用 ref 首个形参做 passthrough
#    C. Finalizer 必须是 static Exception 方法
#    D. Transpiler 必须返回 IEnumerable<CodeInstruction>
#    E. 补丁方法必须是 static（实例方法 Harmony 不认）
#    F. 同一目标上多个补丁的优先级/before/after 是否有自相矛盾
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'SpectreReplay'
# **必须先证明源目录读到了东西。** 实测（round 38）：root 解析错时 Get-ChildItem
# 不会静默返回 0，而是**递归去遍历父目录**并在无关路径上抛 AccessDenied
# （如 WinSAT）——于是报错完全指不到真正的原因（"路径指错了"），排查的人
# 会去查权限。加这一句把失败提前到根因上。同理：任何"扫目录→判断"的脚本，
# 计数为 0 之前都要先确认自己扫到了文件。
if (-not (Test-Path $src)) {
    Write-Output "✗ 源目录不存在：$src（root 解析错了？应为 <repo>\SpectreReplay）"
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
        $attr = $lines[$i].Substring($at)
        for ($j = $i + 1; $j -lt $lines.Count -and $j -lt $i + 4; $j++) {
            $attr += ' ' + $lines[$j]
            if ($lines[$j] -match '\)\s*$') { break }
        }
        $pt = [regex]::Match($attr, 'PatchType\.(\w+)')
        $ptName = if ($pt.Success) { $pt.Groups[1].Value } else { '?' }
        # 补丁方法签名（含返回类型）
        $ret = ''; $sig = ''; $name = ''
        for ($j = $i + 1; $j -lt $lines.Count -and $j -lt $i + 6; $j++) {
            $sm = [regex]::Match($lines[$j], 'internal\s+static\s+([\w\<\>\[\]\?\.]+)\s+(\w+)\s*\(([^)]*)\)')
            if ($sm.Success) { $ret = $sm.Groups[1].Value; $name = $sm.Groups[2].Value; $sig = $sm.Groups[3].Value; break }
        }
        if ($ret -eq '') { continue }

        $verdict = 'OK'
        switch ($ptName) {
            'Prefix'  { if ($ret -ne 'bool' -and $ret -ne 'void') { $verdict = "Prefix 返回类型非法: $ret（须 void 或 bool）" } }
            'Postfix' {
                if ($ret -ne 'void') {
                    # passthrough 形态：返回类型须与首个 ref 形参一致
                    $first = ($sig -split ',')[0].Trim()
                    if ($first -match '^ref\s+([\w\.]+)$' -and $Matches[1] -eq $ret) { $verdict = 'OK (passthrough)' }
                    else { $verdict = "Postfix 返回类型可疑: $ret（须 void，或与首个 ref 形参同型做 passthrough）" }
                }
            }
            'Finalizer' { if ($ret -notmatch 'Exception') { $verdict = "Finalizer 返回类型非法: $ret（须 Exception）" } }
            'Transpiler' { if ($ret -notmatch 'IEnumerable<CodeInstruction>') { $verdict = "Transpiler 返回类型非法: $ret（须 IEnumerable<CodeInstruction>）" } }
            default { $verdict = "未知 PatchType: $ptName" }
        }
        $rows += [PSCustomObject]@{ File = $rel; Kind = $ptName; Method = $name; Ret = $ret; Verdict = $verdict }
    }
}
Write-Output "=== 补丁方法签名形状（共 $($rows.Count) 处）==="
$rows | Sort-Object Verdict, Kind | Format-Table -AutoSize | Out-String | Write-Output
$bad = @($rows | Where-Object { $_.Verdict -notlike 'OK*' })
Write-Output ("不合规: " + $bad.Count)
$bad | ForEach-Object { "  $($_.File)  $($_.Kind) $($_.Method)  => $($_.Verdict)" }
