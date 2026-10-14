# MateMmdPhysics.dll：Bullet 3.25 的 LinearMath / BulletCollision / BulletDynamics 加 mmd_physics.cpp。
# Bullet 源码由 build.ps1 拉取到 bullet3/（不入库）；静态链接 CRT，App 机器上不需要装 VC++ 运行库。
cmake_minimum_required(VERSION 3.20)
project(MateMmdPhysics CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(BULLET_SRC "${CMAKE_CURRENT_SOURCE_DIR}/bullet3/src")
if(NOT EXISTS "${BULLET_SRC}/btBulletDynamicsCommon.h")
    message(FATAL_ERROR "缺少 Bullet 源码，请先运行 build.ps1")
endif()

# 不用 Bullet 自带的 *All.cpp 合并编译：btVector3.cpp 里定义的 BT_USE_SSE_IN_API 会泄漏到
# 同一编译单元的后续头文件，MSVC 下 btQuaternion.h 编译失败。
file(GLOB_RECURSE BULLET_SOURCES
    "${BULLET_SRC}/LinearMath/*.cpp"
    "${BULLET_SRC}/BulletCollision/*.cpp"
    "${BULLET_SRC}/BulletDynamics/*.cpp")
add_library(MateMmdPhysics SHARED mmd_physics.cpp ${BULLET_SOURCES})
target_include_directories(MateMmdPhysics PRIVATE "${BULLET_SRC}")
target_compile_definitions(MateMmdPhysics PRIVATE _CRT_SECURE_NO_WARNINGS)
if(MSVC)
    # 源文件是无 BOM 的 UTF-8；不加 /utf-8 时 MSVC 按系统代码页（GBK）读，中文注释会吞掉下一行。
    target_compile_options(MateMmdPhysics PRIVATE /utf-8 /W1 /MP /bigobj)
endif()
