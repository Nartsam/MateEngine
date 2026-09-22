<#
.SYNOPSIS
    一条命令跑完差分验收闭环：Blender 基准图 → Unity 对照图 → 差异数值。

.DESCRIPTION
    计划见 Docs/plans/2026-09-10-blender-graph-transpile.md Phase 0。三环分别是：
      1. Tools/PmxPipeline/blender_apply_preset.py      （Blender 基准）
      2. Assets/Editor/PmxPipeline/MERefRenderer.cs     （Unity 对照）
      3. Tools/PmxPipeline/compare_renders.py           （差异统计）

    两个渲染器都用 Start-Process -Wait 串行等待。Unity 批处理实例会抢工程锁，不阻塞
    地并发启动必然失败；Blender 串行则是为了不和 Unity 抢 GPU。

    所有产物落在 -OutDir（默认 Build/RefDiff，已被 .gitignore 忽略）。

.EXAMPLE
    pwsh Tools/PmxPipeline/run_ref_diff.ps1 -Character 特工丽塔

.EXAMPLE
    # 换角度、只重跑 Unity 一侧（复用上次的 Blender 基准）
    pwsh Tools/PmxPipeline/run_ref_diff.ps1 -Character 特工丽塔 -View three_quarter -SkipBlender
#>
[CmdletBinding()]
param(
    # 角色名同时是 -ModelsRoot 下的子目录名和所有产物的键（节点图、贴图、描边、着色器）。
    [string] $Character = "特工丽塔",
    # 下面三个是**本机相关**的外部路径，留空时按「环境变量 > 本机配置 > 自动探测」解析，
    # 见 Tools/PmxPipeline/README.md。不写死默认值，否则换机器或换盘就失效。
    [string] $ModelsRoot,
    [string] $Pmx,
    [string] $Preset,
    [string] $Graph,

    [string] $View = "front",
    [int]    $Res = 1024,
    [string] $SunEuler,
    [string] $Prefab,

    [string] $OutDir = "Build/RefDiff",
    [string] $BlenderExe,
    [string] $UnityExe,

    # transpiled = 用节点图转译出的着色器（Phase 1 起的主线）
    # uts8       = 保留 UTS2 兜底，用来对照基线
    [ValidateSet("transpiled", "uts2")]
    [string] $Profile = "transpiled",

    # 重新从 .blend 导出节点图与打包贴图。只有换预设或改导出器时才需要，
    # 平时复用 Docs/RenderPreset/graphs/ 与 Build/RenderPreset/images/。
    [switch] $ExportGraph,

    # 重新烘焙描边外壳。产物不存在时会自动烘一次，这个开关用于强制重烘。
    [switch] $BakeOutline,

    # 不并描边外壳（用于把「描边的差异」和「着色的差异」分开量）
    [switch] $NoOutline,

    # 两侧同时关掉 bloom。bloom 在 Filmic 的高光肩部会把小差异放大成大色差，
    # 关掉它才能判断某处的偏差到底出在材质求值还是出在后期合成。
    [switch] $NoBloom,

    [switch] $SkipBlender,
    [switch] $SkipUnity
)

$ErrorActionPreference = "Stop"
$script:repo = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$repo = $script:repo
Set-Location $repo

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

$BlenderExe = Resolve-External $BlenderExe "ME_BLENDER" "blenderPath" `
    ${function:Find-BlenderExe} "Blender 3.6" "-BlenderExe"
$UnityExe = Resolve-External $UnityExe "ME_UNITY" "unityPath" `
    ${function:Find-UnityExe} "Unity 编辑器" "-UnityExe"
$ModelsRoot = Resolve-External $ModelsRoot "ME_MODELS_ROOT" "modelsRoot" $null `
    "模型库目录（内含 <角色>/*.pmx 与 渲染预设.blend）" "-ModelsRoot"

$charDir = Join-Path $ModelsRoot $Character
if (-not $Preset) { $Preset = Join-Path $charDir "渲染预设.blend" }
if (-not $Graph)  { $Graph  = Join-Path $repo "Docs/RenderPreset/graphs/$Character.graph.json" }

# PMX 文件名各角色不统一，而且同目录下常有武器、伞、鱼竿等配件的独立 .pmx。按约定
# 顺序定位角色本体：与目录同名 > 带 "_修复" > 目录里唯一的一个。都对不上就列出候选
# 让调用方用 -Pmx 指定，不猜。
if (-not $Pmx) {
    if (-not (Test-Path $charDir)) { throw "找不到角色目录：$charDir" }
    $cands = @(Get-ChildItem $charDir -Filter *.pmx -File)
    if ($cands.Count -eq 0) { throw "$charDir 下没有 .pmx" }
    $named = @($cands | Where-Object { $_.BaseName -eq $Character })
    $fixed = @($cands | Where-Object { $_.BaseName -like "*_修复" })
    if ($named.Count -eq 1)      { $Pmx = $named[0].FullName }
    elseif ($fixed.Count -eq 1)  { $Pmx = $fixed[0].FullName }
    elseif ($cands.Count -eq 1)  { $Pmx = $cands[0].FullName }
    else { throw "$charDir 下有多个 .pmx 且没有与角色同名的，请用 -Pmx 指定：`n  " +
                 (($cands.Name) -join "`n  ") }
    Write-Host "     PMX 自动定位：$Pmx" -ForegroundColor DarkGray
}

foreach ($p in @($Pmx, $Preset, $BlenderExe, $UnityExe)) {
    if (-not (Test-Path $p)) { throw "找不到：$p" }
}

$outFull  = (New-Item -ItemType Directory -Force -Path (Join-Path $repo $OutDir)).FullName
$refPng   = Join-Path $outFull "$Character-$View-blender.png"
$refJson  = Join-Path $outFull "$Character-$View-blender.json"
$tstPng   = Join-Path $outFull "$Character-$View-unity.png"
$tstJson  = Join-Path $outFull "$Character-$View-unity.json"
$diffJson = Join-Path $outFull "$Character-$View-diff.json"
$idPng    = Join-Path $outFull "$Character-$View-unity-ids.png"

# 透明背景是硬要求：轮廓统计靠 alpha 掩码，不透明背景会让掩码变成整张图、IoU 恒为 1。
$bg = "transparent"

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
        NoNewWindow = $true; Wait = $true; PassThru = $true
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

$py = Join-Path $repo "Build/.venv/Scripts/python.exe"
if (-not (Test-Path $py)) {
    throw @"
缺少虚拟环境。依赖只装在仓库内，不碰全局环境。先执行：
  python -m venv Build/.venv
  Build/.venv/Scripts/python.exe -m pip install numpy pillow
"@
}
$env:PYTHONIOENCODING = "utf-8"

$imagesDir   = Join-Path $repo "Build/RenderPreset/images/$Character"
$transpDir   = "Assets/Generated/PmxTranspiled/$Character"
$bindings    = Join-Path $repo "$transpDir/$Character.bindings.json"
$viewLut     = Join-Path $repo "Docs/RenderPreset/view_transform_filmic_high_contrast.csv"
$outlineMeta = Join-Path $repo "Build/RenderPreset/outline/$Character.outline.json"

if ($ExportGraph) {
    $exportLog = Join-Path $outFull "export_graph.log"
    Invoke-Step "导出节点图与贴图" $BlenderExe @(
        "-b", $Preset, "--factory-startup",
        "-P", (Join-Path $repo "Tools/PmxPipeline/export_blender_graph.py"), "--",
        "--out", $Graph, "--images", $imagesDir) $exportLog $exportLog
    Show-Lines $exportLog '^\[export_graph\]'
}

if ($BakeOutline -or -not (Test-Path $outlineMeta)) {
    $bakeLog = Join-Path $outFull "bake_outline.log"
    Invoke-Step "烘焙描边外壳" $BlenderExe @(
        "-b", "--factory-startup",
        "-P", (Join-Path $repo "Tools/PmxPipeline/bake_outline_shell.py"), "--",
        "--pmx", $Pmx, "--preset", $Preset,
        "--out", (Join-Path $repo "Build/RenderPreset/outline/$Character")) $bakeLog $bakeLog
    Show-Lines $bakeLog '^\[outline\]'
}

if ($Profile -eq "transpiled") {
    Write-Host "==== 转译节点图 -> HLSL" -ForegroundColor Cyan
    & $py (Join-Path $repo "Tools/PmxPipeline/transpile.py") `
        --graph $Graph --images $imagesDir --view-lut $viewLut --out (Join-Path $repo $transpDir)
    if ($LASTEXITCODE -ne 0) { throw "转译失败" }
}

if (-not $SkipBlender) {
    $blenderLog = Join-Path $outFull "blender.log"
    $a = @("-b", "--factory-startup", "-P", (Join-Path $repo "Tools/PmxPipeline/blender_apply_preset.py"), "--",
           "--pmx", $Pmx, "--preset", $Preset, "--out", $refPng, "--report", $refJson,
           "--res", "$Res", "--view", $View, "--bg", $bg)
    if (Test-Path $Graph) { $a += @("--expect", $Graph) }
    if ($NoBloom) { $a += "--no-bloom" }
    if ($SunEuler) { $a += @("--sun-euler", $SunEuler) }

    Invoke-Step "Blender 基准图" $BlenderExe $a $blenderLog $blenderLog
    Show-Lines $blenderLog '^\[apply_preset\] (材质槽|SUN|相机|已渲染|!!)'
}

if (-not $SkipUnity) {
    $unityLog = Join-Path $outFull "unity.log"
    # Unity 自己用 -logFile 写日志，不需要重定向 stdout
    $a = @("-batchmode", "-quit", "-projectPath", $repo, "-logFile", $unityLog,
           "-executeMethod", "MateEngine.PmxPipeline.MERefRenderer.RenderRef",
           "-blenderReport", $refJson, "-out", $tstPng, "-report", $tstJson,
           "-res", "$Res", "-bg", $bg, "-view", $View, "-idOut", $idPng)
    if ($Prefab) { $a += @("-prefab", $Prefab) } else { $a += @("-pmx", $Pmx) }
    if ($NoBloom) { $a += @("-noBloom", "1") }
    if ($Profile -eq "transpiled") { $a += @("-bindings", $bindings) }
    if ($Profile -eq "transpiled" -and -not $NoOutline) { $a += @("-outline", $outlineMeta) }

    Invoke-Step "Unity 对照图" $UnityExe $a $null $unityLog
    Show-Lines $unityLog '^\[MERefRenderer\]'
}

Write-Host "==== 差异统计" -ForegroundColor Cyan
$cmpArgs = @("--ref", $refPng, "--test", $tstPng, "--out-dir", $outFull,
             "--report", $diffJson)
if ((Test-Path $idPng) -and (Test-Path $tstJson)) {
    $cmpArgs += @("--ids", $idPng, "--id-map", $tstJson)
}
& $py (Join-Path $repo "Tools/PmxPipeline/compare_renders.py") @cmpArgs
if ($LASTEXITCODE -ne 0) { throw "差异统计失败" }

Write-Host ""
Write-Host "产物目录：$outFull" -ForegroundColor Green
Write-Host "  并排对照图：$(Join-Path $outFull 'diff_side_by_side.png')"
