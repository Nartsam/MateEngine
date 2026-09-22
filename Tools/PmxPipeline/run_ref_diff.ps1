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

. (Join-Path $PSScriptRoot "PmxTools.ps1")

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
