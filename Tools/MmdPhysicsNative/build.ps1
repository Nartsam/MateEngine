# 构建 MateMmdPhysics.dll 并复制到 Assets/Plugins/x86_64/。
# 需要 git、CMake 与带 C++ 工具集的 Visual Studio 2022；Bullet 源码拉取到本目录的 bullet3/（不入库），
# 中间文件放在 Build/MmdPhysicsNative/。Unity 打开工程时会锁住已加载的 DLL，替换前先关掉编辑器。
param([string]$Config = "Release")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here "../..")).Path
$bullet = Join-Path $here "bullet3"
$tag = "3.25"

if (-not (Test-Path (Join-Path $bullet "src/btBulletDynamicsCommon.h"))) {
    git clone --depth 1 --branch $tag --filter=blob:none --sparse https://github.com/bulletphysics/bullet3 $bullet
    if ($LASTEXITCODE -ne 0) { throw "拉取 Bullet 失败" }
    git -C $bullet sparse-checkout set src/LinearMath src/BulletCollision src/BulletDynamics
    if ($LASTEXITCODE -ne 0) { throw "设置 Bullet 稀疏检出失败" }
}

$build = Join-Path $repo "Build/MmdPhysicsNative"
cmake -S $here -B $build -G "Visual Studio 17 2022" -A x64
if ($LASTEXITCODE -ne 0) { throw "CMake 配置失败" }
cmake --build $build --config $Config
if ($LASTEXITCODE -ne 0) { throw "编译失败" }

$dest = Join-Path $repo "Assets/Plugins/x86_64"
Copy-Item (Join-Path $build "$Config/MateMmdPhysics.dll") $dest -Force
Write-Host "已更新 $dest/MateMmdPhysics.dll"
