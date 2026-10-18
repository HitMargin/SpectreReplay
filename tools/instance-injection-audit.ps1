<#
  系统排查：我们的补丁里声明 `__instance` 的，核对**目标方法是否静态**。
  Harmony 的 __instance 只在目标非静态时注入（内部是合成形参
  MethodBodyReader+ThisParameter，不是按名匹配）。静态目标上写 __instance
  ⇒ 恒拿到 null ⇒ 补丁照跑但拿到空值（静默失效）。
  本脚本直接拿**真 dll** 反射判定静不静态（不靠源码文本匹配），最可靠。
#>
$ErrorActionPreference = 'Stop'
$root = Join-Path (Split-Path -Parent $PSScriptRoot) 'SpectreReplay'

# 游戏程序集位置：① 环境变量 ADOFAI_LIBS（CI 用）② 仓库同级的 adofai-libs
# ③ 历史绝对路径。写死绝对路径时 CI 必挂，而这三个审计是闸门登记过的真门禁
# ——挂掉的表现会被当成"CI 环境有问题"，与"代码真有问题"混在一起。
$libs = if ($env:ADOFAI_LIBS -and (Test-Path $env:ADOFAI_LIBS)) { $env:ADOFAI_LIBS }
        elseif (Test-Path (Join-Path (Split-Path -Parent $PSScriptRoot) '..\adofai-libs')) {
            (Resolve-Path (Join-Path (Split-Path -Parent $PSScriptRoot) '..\adofai-libs')).Path }
        else { 'D:\Projects\adofai-libs' }
$asmPath = Join-Path $libs 'Assembly-CSharp.dll'
if (-not (Test-Path $asmPath)) {
    Write-Output "✗ 找不到游戏程序集：$asmPath（设 ADOFAI_LIBS 指到 adofai-libs 目录）"
    exit 3
}
$asm = [Reflection.Assembly]::LoadFrom($asmPath)

# **必须先证明源目录读到了东西。** 实测（round 38）：root 解析错时
# Get-ChildItem 不会静默返回 0，而是**递归遍历父目录**并在无关路径上抛
# AccessDenied（如 WinSAT）——报错完全指不到真正的原因（"路径指错了"）。
# 加这一句把失败提前到根因上。
if (-not (Test-Path $root)) {
    Write-Output "✗ 项目根目录不存在：$root"
    exit 3
}
# **必须排除 tools\**（round 56 补）：这个脚本扫的是整仓，而 tools\UiPreview 下的
# 检查类里**真的有 `[SpectrePatch(` 字样**——PatchTargetCheck.cs:162-169 专门处理过
# "示例代码里的属性声明"，InstanceInjectionCheck.cs:57 也在扫它。目前那些行不构成
# "声明了 __instance 的补丁方法"，所以本脚本的 16 条结论仍然正确；但这是**巧合而非
# 保证**——哪天某个检查类为了自证写一个带 __instance 的样例，计数就会从 16 变 17，
# 而没人知道多出来的那一条是工具自己的代码。与其余四个审计的过滤条件保持一致。
$files = @(Get-ChildItem $root -Recurse -File -Filter *.cs -ErrorAction Stop |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.FullName -notmatch '\\tools\\' })
if ($files.Count -eq 0) {
    Write-Output "✗ 在 $root 下没扫到任何 .cs——计数为 0 之前先证明自己扫到了（假干净比误报危险）"
    exit 3
}

$rows = @()
foreach ($f in $files) {
    $t = [IO.File]::ReadAllText($f.FullName)
    if ($t -notmatch 'SpectrePatch\(') { continue }
    $rel = $f.FullName.Replace("$root\", '')
    $lines = $t -split "`n"

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch 'SpectrePatch\(') { continue }
        # 属性可能跨行，先把整段属性拼出来
        $attr = $lines[$i]
        for ($j = $i + 1; $j -lt $lines.Count -and $j -lt $i + 4; $j++) {
            $attr += ' ' + $lines[$j]
            if ($lines[$j] -match '\)\s*$') { break }
        }
        $mType = [regex]::Match($attr, 'typeof\(\s*([\w\.]+)\s*\)')
        if (-not $mType.Success) { continue }
        $typeName = $mType.Groups[1].Value
        # 方法名：字符串字面量，或 nameof(X.Y)
        $mName = [regex]::Match($attr, '"([^"]+)"')
        $method = ''
        if ($mName.Success) { $method = $mName.Groups[1].Value }
        else {
            $nName = [regex]::Match($attr, 'nameof\(\s*[\w\.]+\.(\w+)\s*\)')
            if ($nName.Success) { $method = $nName.Groups[1].Value }
        }
        if ([string]::IsNullOrEmpty($method)) { continue }

        # 补丁方法签名（往下找 internal static ... ( )）
        $sig = ''
        for ($j = $i + 1; $j -lt $lines.Count -and $j -lt $i + 6; $j++) {
            if ($lines[$j] -match 'internal\s+static\s+[\w\<\>\[\]\?\.]+\s+\w+\s*\(([^)]*)\)') { $sig = $Matches[1]; break }
        }
        if ($sig -notmatch '__instance') { continue }

        # 反射真程序集
        $type = $asm.GetType($typeName)
        $verdict = '类型不在 Assembly-CSharp'
        if ($type) {
            $mi = $type.GetMethod($method, [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Static)
            if ($mi -eq $null) { $verdict = '方法没找到（名字/形参变了）' }
            elseif ($mi.IsStatic) { $verdict = 'STATIC <<< __instance 无效，恒为 null' }
            else { $verdict = '实例方法 OK' }
        }
        $rows += [PSCustomObject]@{ File = $rel; Target = "$typeName.$method"; Verdict = $verdict }
    }
}

Write-Output "=== 声明了 __instance 的补丁（共 $($rows.Count) 处）==="
$rows | Sort-Object Verdict, File | Format-Table -AutoSize -Wrap | Out-String | Write-Output
$bad = @($rows | Where-Object { $_.Verdict -like 'STATIC*' })
Write-Output ("其中静态目标: " + $bad.Count)
if ($bad.Count -gt 0) {
    Write-Output "── 需修（静态目标上的 __instance 拿不到值）──"
    $bad | ForEach-Object { "  $($_.File)  $($_.Target)" }
    exit 1
}
Write-Output "全部是实例方法，__instance 用法正确"
