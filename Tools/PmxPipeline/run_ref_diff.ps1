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
    pwsh Tools/PmxPipeline/run_ref_diff.ps1 -Character 丽塔

.EXAMPLE
    # 换角度、只重跑 Unity 一侧（复用上次的 Blender 基准）
    pwsh Tools/PmxPipeline/run_ref_diff.ps1 -Character 丽塔 -View three_quarter -SkipBlender
#>
[CmdletBinding()]
param(
    [string] $Character = "丽塔",
    [string] $ExamplesRoot = "D:\Program Files\MMD\MateEngine\examples",
    [string] $Pmx,
    [string] $Preset,
    [string] $Graph,

    [string] $View = "front",
    [int]    $Res = 1024,
    [string] $SunEuler,
    [string] $Prefab,

    [string] $OutDir = "Build/RefDiff",
    [string] $BlenderExe = "D:\Program Files\Blender\3.6\blender.exe",
    [string] $UnityExe = "D:\Program Files\Unity\Hub\Editor\6000.2.6f2\Editor\Unity.exe",

    # transpiled = 用节点图转译出的着色器（Phase 1 起的主线）
    # uts8       = 保留 UTS2 兜底，用来对照基线
    [ValidateSet("transpiled", "uts2")]
    [string] $Profile = "transpiled",

    # 重新从 .blend 导出节点图与打包贴图。只有换预设或改导出器时才需要，
    # 平时复用 Docs/RenderPreset/graphs/ 与 Build/RenderPreset/images/。
    [switch] $ExportGraph,

    [switch] $SkipBlender,
    [switch] $SkipUnity
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repo

if (-not $Pmx)    { $Pmx    = Join-Path $ExamplesRoot "$Character\删披风删骨骼_修复.pmx" }
if (-not $Preset) { $Preset = Join-Path $ExamplesRoot "$Character\渲染预设.blend" }
if (-not $Graph)  { $Graph  = Join-Path $repo "Docs/RenderPreset/graphs/$Character.graph.json" }

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

$imagesDir   = "Build/RenderPreset/images/$Character"
$transpDir   = "Assets/Generated/PmxTranspiled/$Character"
$bindings    = Join-Path $repo "$transpDir/$Character.bindings.json"
$viewLut     = Join-Path $repo "Docs/RenderPreset/view_transform_filmic_high_contrast.csv"

if ($ExportGraph) {
    $exportLog = Join-Path $outFull "export_graph.log"
    Invoke-Step "导出节点图与贴图" $BlenderExe @(
        "-b", $Preset, "--factory-startup",
        "-P", "Tools/PmxPipeline/export_blender_graph.py", "--",
        "--out", $Graph, "--images", $imagesDir) $exportLog $exportLog
    Show-Lines $exportLog '^\[export_graph\]'
}

if ($Profile -eq "transpiled") {
    Write-Host "==== 转译节点图 -> HLSL" -ForegroundColor Cyan
    & $py (Join-Path $repo "Tools/PmxPipeline/transpile.py") `
        --graph $Graph --images $imagesDir --view-lut $viewLut --out (Join-Path $repo $transpDir)
    if ($LASTEXITCODE -ne 0) { throw "转译失败" }
}

if (-not $SkipBlender) {
    $blenderLog = Join-Path $outFull "blender.log"
    $a = @("-b", "--factory-startup", "-P", "Tools/PmxPipeline/blender_apply_preset.py", "--",
           "--pmx", $Pmx, "--preset", $Preset, "--out", $refPng, "--report", $refJson,
           "--res", "$Res", "--view", $View, "--bg", $bg)
    if (Test-Path $Graph) { $a += @("--expect", $Graph) }
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
    if ($Profile -eq "transpiled") { $a += @("-bindings", $bindings) }

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
