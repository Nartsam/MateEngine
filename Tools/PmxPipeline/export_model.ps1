<#
.SYNOPSIS
    指定 PMX 和配套 Blender 3.6 预设，导出 MateEngine 可导入的 .me。
.EXAMPLE
    pwsh Tools/PmxPipeline/export_model.ps1 -Pmx "D:/模型/角色.pmx" -Preset "D:/模型/渲染预设.blend" -Out "Build/PmxModels/角色.me"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Pmx,
    [Parameter(Mandatory)] [string] $Preset,
    [Parameter(Mandatory)] [string] $Out,
    [string] $BlenderExe,
    [string] $UnityExe
)

$ErrorActionPreference = "Stop"
$script:repo = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$repo = $script:repo
. (Join-Path $PSScriptRoot "PmxTools.ps1")

function Full-Path([string] $Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repo $Path))
}

$Pmx = Full-Path $Pmx
$Preset = Full-Path $Preset
$Out = Full-Path $Out
if ([IO.Path]::GetExtension($Pmx) -ine ".pmx" -or [IO.Path]::GetExtension($Preset) -ine ".blend") {
    throw "输入必须是 .pmx 模型与 .blend 渲染预设。"
}
if (-not $Out.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetExtension($Out) -ine ".me") {
    throw "-Out 必须指定当前仓库内的 .me 文件，例如 Build/PmxModels/角色.me。"
}
foreach ($path in @($Pmx, $Preset)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "找不到输入文件：$path" }
}
$BlenderExe = Resolve-External $BlenderExe "ME_BLENDER" "blenderPath" `
    ${function:Find-BlenderExe} "Blender 3.6" "-BlenderExe"
$UnityExe = Resolve-External $UnityExe "ME_UNITY" "unityPath" `
    ${function:Find-UnityExe} "Unity 编辑器" "-UnityExe"
foreach ($path in @($BlenderExe, $UnityExe)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "找不到工具：$path" }
}
$requiredUnity = (Select-String -LiteralPath (Join-Path $repo "ProjectSettings/ProjectVersion.txt") -Pattern '^m_EditorVersion:\s*(\S+)').Matches[0].Groups[1].Value
$unityVersion = (Get-Item -LiteralPath $UnityExe).VersionInfo.ProductVersion.Split('_')[0]
if ($unityVersion -ne $requiredUnity) { throw "Unity 必须为 $requiredUnity，当前为 $unityVersion。" }
$blenderVersion = (Get-Item -LiteralPath $BlenderExe).VersionInfo.ProductVersion
if ($blenderVersion -notmatch '^3\.6($|\.)') { throw "Blender 必须为 3.6，当前为 $blenderVersion。" }
$py = Join-Path $repo "Build/.venv/Scripts/python.exe"
if (-not (Test-Path -LiteralPath $py)) {
    throw "缺少仓库内 Python：先执行 python -m venv Build/.venv，再执行 Build/.venv/Scripts/python.exe -m pip install numpy pillow。"
}
if (Get-Process Unity -ErrorAction SilentlyContinue) {
    # Unity 的工程锁由编辑器维护；只检查本项目的锁，允许别的项目保持打开。
    $lockPath = Join-Path $repo "Temp/UnityLockfile"
    if (Test-Path -LiteralPath $lockPath) { throw "请先关闭此项目的 Unity 编辑器，再执行导出。" }
}

$name = [IO.Path]::GetFileNameWithoutExtension($Pmx)
$hash = [Security.Cryptography.SHA256]::Create()
try {
    $key = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes("$Pmx`n$Preset"))).Replace("-", "").Substring(0, 12).ToLowerInvariant()
} finally { $hash.Dispose() }
# 同名模型与不同预设各用独立工作目录，且每次重建图和贴图，避免历史产物串入。
$work = Join-Path $repo "Build/PmxExport/$key"
$generated = Join-Path $repo "Assets/Generated/PmxTranspiled/Export_$key"
$assets = "Assets/PmxImported/Export_$key"
$graph = Join-Path $work "model.graph.json"
$images = Join-Path $work "images"
$outline = Join-Path $work "model"
$bindings = Join-Path $generated "Export_$key.bindings.json"
New-Item -ItemType Directory -Force -Path $work | Out-Null

Push-Location $repo
try {
    $log = Join-Path $work "graph.log"
    Invoke-Step "1/4 导出节点图与贴图" $BlenderExe @(
        "--background", "--factory-startup", "--disable-autoexec", $Preset,
        "--python-exit-code", "1", "--python", (Join-Path $PSScriptRoot "export_blender_graph.py"),
        "--", "--out", $graph, "--images", $images) $log $log
    Show-Lines $log '^\[export_graph\]'

    $log = Join-Path $work "outline.log"
    Invoke-Step "2/4 烘焙描边与方向属性" $BlenderExe @(
        "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
        "--python", (Join-Path $PSScriptRoot "bake_outline_shell.py"), "--",
        "--pmx", $Pmx, "--preset", $Preset, "--out", $outline) $log $log
    Show-Lines $log '^\[outline\]'

    $log = Join-Path $work "transpile.log"
    Invoke-Step "3/4 转译材质" $py @(
        "-X", "utf8", "-B", (Join-Path $PSScriptRoot "transpile.py"),
        "--graph", $graph, "--images", $images,
        "--view-lut", (Join-Path $repo "Docs/RenderPreset/view_transform_filmic_high_contrast.csv"),
        "--out", $generated) $log $log
    Show-Lines $log '^\[transpile\]'

    $log = Join-Path $work "unity.log"
    Invoke-Step "4/4 保存模型资产并打包 .me" $UnityExe @(
        "-batchmode", "-quit", "-buildTarget", "Win64", "-projectPath", $repo,
        "-executeMethod", "MateEngine.PmxPipeline.MEPmxPipeline.BuildTranspiledAndExport",
        "-pmx", $Pmx, "-modelName", $name, "-outputRoot", $assets,
        "-bindings", $bindings, "-outline", "$outline.outline.json", "-out", $Out,
        "-logFile", $log) "" $log
    if (-not (Test-Path -LiteralPath $Out -PathType Leaf)) { throw "Unity 未生成输出：$Out" }
    Show-Lines $log '\[MEPmxPipeline\]'
    Write-Host "已导出：$Out" -ForegroundColor Green
    Write-Host "日志：$work"
} finally { Pop-Location }
