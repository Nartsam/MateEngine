# 共享的本机工具定位与串行进程执行。调用方先设置 $repo / $script:repo。
# ---- 本机相关的外部路径 ------------------------------------------------------
# 仓库里的一切都由 $repo（从 $PSScriptRoot 推出来）拼，跟着仓库走，换机器换目录都不受
# 影响。但 Blender、Unity、模型库在仓库之外，只能按本机情况解析：
# 命令行 > 环境变量 > 本机配置 > 自动探测。全落空就报错说清楚怎么配，绝不回退到某台
# 开发机的绝对路径。
$localCfgPath = Join-Path $repo "Library/MateEngineUserData/PmxPipeline/settings.json"
$localCfg = $null
if (Test-Path $localCfgPath) {
    try { $localCfg = Get-Content $localCfgPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { Write-Warning "本机配置解析失败，已忽略：$localCfgPath" }
}

function Get-SearchRoots {
    $roots = @()
    try {
        $roots += (Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" -ErrorAction Stop |
                   ForEach-Object { $_.DeviceID + "\" })
    } catch { $roots += @("C:\", "D:\") }
    $roots += @($env:ProgramFiles, ${env:ProgramFiles(x86)})
    $roots | Where-Object { $_ } | Select-Object -Unique
}

function Find-UnityExe {
    # 版本必须与 ProjectSettings/ProjectVersion.txt 精确一致，不能「就近找一个」
    $verFile = Join-Path $repo "ProjectSettings/ProjectVersion.txt"
    if (-not (Test-Path $verFile)) { return $null }
    $m = Select-String -Path $verFile -Pattern '^m_EditorVersion:\s*(\S+)'
    if (-not $m) { return $null }
    $ver = $m.Matches[0].Groups[1].Value
    $cands = @()
    # Unity Hub 支持自定义安装位置，它把路径记在这个文件里
    $hubCfg = Join-Path $env:APPDATA "UnityHub\secondaryInstallPath.json"
    if (Test-Path $hubCfg) {
        # 这个文件是 JSON 字符串，必须按 JSON 解析——去引号会把 \ 转义原样留下
        $alt = $null
        try { $alt = Get-Content $hubCfg -Raw | ConvertFrom-Json } catch { }
        if ($alt) { $cands += (Join-Path $alt "$ver\Editor\Unity.exe") }
    }
    foreach ($root in Get-SearchRoots) {
        $cands += (Join-Path $root "Program Files\Unity\Hub\Editor\$ver\Editor\Unity.exe")
        $cands += (Join-Path $root "Unity\Hub\Editor\$ver\Editor\Unity.exe")
    }
    $cands | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Find-BlenderExe {
    # 必须 3.6：5.x 移除了 node_group.outputs 与 eevee.use_bloom，预设插件跑不起来
    $cands = @()
    foreach ($root in Get-SearchRoots) {
        $cands += (Join-Path $root "Program Files\Blender\3.6\blender.exe")
        $cands += (Join-Path $root "Program Files\Blender Foundation\Blender 3.6\blender.exe")
        $cands += (Join-Path $root "Blender\3.6\blender.exe")
    }
    $cands | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Resolve-External {
    param([string] $Value, [string] $EnvName, [string] $CfgKey,
          [scriptblock] $Probe, [string] $What, [string] $Param)
    if ($Value) { return $Value }
    $v = [Environment]::GetEnvironmentVariable($EnvName)
    if ($v) { return $v }
    if ($localCfg -and ($localCfg.PSObject.Properties.Name -contains $CfgKey) -and $localCfg.$CfgKey) {
        return $localCfg.$CfgKey
    }
    if ($Probe) {
        $v = & $Probe
        if ($v) { Write-Host "     自动探测到 $What : $v" -ForegroundColor DarkGray; return $v }
    }
    throw @"
找不到 $What。按任一方式指定（优先级从高到低）：
  1. 命令行   $Param <路径>
  2. 环境变量 ME 前缀的 $EnvName
  3. 本机配置 在 $localCfgPath 里加一项 "$CfgKey": "<路径>"
     该文件在 Library/ 下，已被 Git 忽略，属于本机私有配置。
"@
}

function Invoke-Step {
    param(
        [string]   $Label,
        [string]   $Exe,
        [string[]] $ArgList,
        [string]   $StdoutPath,   # 给自己不写日志的程序（Blender）用
        [string]   $FailLog       # 失败时回显尾部的日志
    )
    Write-Host "==== $Label" -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    # Start-Process 会把 ArgumentList 数组直接用空格拼接、不加引号，含空格的路径
    # （"D:\Program Files\..."）会被拆成两个参数。必须自己引用。
    # 这里不能改用调用运算符 & ——Unity.exe 是 GUI 子系统程序，& 不会等它退出，
    # 并发启动的批处理实例会抢工程锁（见记忆 unity-batch-runs-sequential-and-vrm-out）。
    $quoted = $ArgList | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }
    $sp = @{
        FilePath = $Exe; ArgumentList = $quoted
        WindowStyle = "Hidden"; Wait = $true; PassThru = $true
        WorkingDirectory = $script:repo
    }
    if ($StdoutPath) {
        $sp.RedirectStandardOutput = $StdoutPath
        $sp.RedirectStandardError = "$StdoutPath.err"
    }
    $proc = Start-Process @sp
    $sw.Stop()
    Write-Host ("     用时 {0:N1} 秒，退出码 {1}" -f $sw.Elapsed.TotalSeconds, $proc.ExitCode)
    if ($proc.ExitCode -ne 0) {
        if ($FailLog -and (Test-Path $FailLog)) { Get-Content $FailLog -Tail 40 | Write-Host }
        if ($StdoutPath -and (Test-Path -LiteralPath "$StdoutPath.err")) {
            Get-Content -LiteralPath "$StdoutPath.err" -Tail 40 | Write-Host
        }
        throw "$Label 失败（退出码 $($proc.ExitCode)），日志：$FailLog"
    }
}

function Show-Lines([string] $Path, [string] $Pattern) {
    if (-not (Test-Path $Path)) { return }
    # Unity 批处理会把每条日志写进 -logFile 两遍（相邻两行完全相同），这里折叠掉
    $prev = $null
    Select-String -Path $Path -Pattern $Pattern | ForEach-Object {
        if ($_.Line -ne $prev) { Write-Host ("     " + $_.Line) }
        $prev = $_.Line
    }
}
