param([Parameter(Mandatory=$true)][string]$PlayerPath)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$player = [IO.Path]::GetFullPath($PlayerPath)
if (!$player.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Player output must be inside the repository.'
}
if (!(Test-Path -LiteralPath $player)) { throw "Unity Player executable not found: $player" }

$work = Join-Path $repo 'Build\PortableRuntime'
$temporary = Join-Path $work 'Temp'
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
# This script runs in its own build process. Keep compiler/Add-Type temporaries
# inside the repository without changing the user's persistent environment.
$env:TEMP = $temporary
$env:TMP = $temporary
$env:VSCMD_SKIP_SENDTELEMETRY = '1'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ build tools are required for the portable Windows entry point.' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Install the Visual Studio Desktop development with C++ workload before building.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$stagedPlayer = Join-Path $work 'MateEngine.exe'
$source = Join-Path $PSScriptRoot 'PortableMain.cpp'
$object = Join-Path $work 'PortableMain.obj'

# Capture vcvars in a child and pass its environment only to the compiler.
# Unicode command lines avoid command-file encoding and console code-page changes.
$setup = [Diagnostics.ProcessStartInfo]::new()
$setup.FileName = $env:ComSpec
$setup.Arguments = '/d /u /c "call "' + $vcvars + '" >nul && set"'
$setup.UseShellExecute = $false
$setup.CreateNoWindow = $true
$setup.RedirectStandardOutput = $true
$setup.RedirectStandardError = $true
$setup.StandardOutputEncoding = [Text.Encoding]::Unicode
$process = [Diagnostics.Process]::Start($setup)
$outputTask = $process.StandardOutput.ReadToEndAsync()
$errorTask = $process.StandardError.ReadToEndAsync()
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "Visual Studio environment setup failed: $($errorTask.Result)" }
$environment = @{}
foreach ($line in ($outputTask.Result -split "`r?`n")) {
    $separator = $line.IndexOf('=')
    if ($separator -gt 0) { $environment[$line.Substring(0, $separator)] = $line.Substring($separator + 1) }
}

$compiler = [Diagnostics.ProcessStartInfo]::new()
$compiler.FileName = Join-Path $environment['VCToolsInstallDir'] 'bin\Hostx64\x64\cl.exe'
$compilerArgs = @('/nologo','/MT','/O2','/W4','/WX','/EHsc','/utf-8','/DUNICODE','/D_UNICODE',
    $source,"/Fo$object","/Fe$stagedPlayer",'/link','/SUBSYSTEM:WINDOWS','shell32.lib','ole32.lib','uuid.lib','user32.lib')
$compiler.Arguments = ($compilerArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '
$compiler.WorkingDirectory = $work
$compiler.UseShellExecute = $false
$compiler.CreateNoWindow = $true
$compiler.RedirectStandardOutput = $true
$compiler.RedirectStandardError = $true
foreach ($key in $environment.Keys) { $compiler.EnvironmentVariables[$key] = $environment[$key] }
$process = [Diagnostics.Process]::Start($compiler)
$outputTask = $process.StandardOutput.ReadToEndAsync()
$errorTask = $process.StandardError.ReadToEndAsync()
$process.WaitForExit()
Write-Host $outputTask.Result
if ($process.ExitCode -ne 0) { throw "Portable entry point compilation failed: $($errorTask.Result)" }

Add-Type -Path (Join-Path $PSScriptRoot 'ExecutableResources.cs')
[ExecutableResources]::Copy($player, $stagedPlayer)
Copy-Item -LiteralPath $stagedPlayer -Destination $player -Force
Write-Host "[PortableRuntime] Installed portable Windows entry point: $player"
