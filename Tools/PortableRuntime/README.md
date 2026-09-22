# Windows 便携启动入口

`BuildScript.BuildWindows` 在 Unity Player 构建完成后调用 `build_launcher.ps1`，
将同名 `MateEngine.exe` 替换为原生入口。用户仍直接双击 exe；无需 PowerShell、
Visual Studio 或项目源码。`.bat` 只是转发命令行参数的便利入口。

## 构建

使用项目现有 `BuildScript.BuildWindows` 即可。开发机需安装 Visual Studio 的
**使用 C++ 的桌面开发**工作负载（MSVC x64 和 Windows SDK），由系统 `vswhere` 定位。
编译使用 `/MT`，入口不依赖额外的 VC Runtime DLL。脚本不下载或安装任何依赖。

需要单独处理已完成的 Unity 构建时，在仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PortableRuntime/build_launcher.ps1 `
  -PlayerPath "$PWD/Build/MateEngine.exe"
```

输出必须位于仓库内；临时编译文件位于 `Build/PortableRuntime/`。
编译器环境和临时目录设置仅影响构建子进程。原生步骤失败时，正式构建入口报错并
删除未完成的 exe，避免留下会绕过便携启动的原始 Unity exe。

## 文件职责与运行顺序

- `PortableMain.cpp`：以 exe 位置解析 `UserData/`，准备便携目录，在当前进程设置
  `TEMP/TMP=UserData/Cache/Temp`，加载同目录的 `UnityPlayer.dll`，重定向其导入表中的
  `SHGetKnownFolderPath(FOLDERID_LocalAppDataLow)`，最后原样调用 `UnityMain`。
- `ExecutableResources.cs`：构建时复制原 Unity exe 的全部 Windows 资源，包括图标、
  版本和 DPI/UAC 清单；不执行原始 exe。
- `build_launcher.ps1`：定位 MSVC、编译、复制资源并替换构建入口。

业务数据继续走 `PortablePaths`；Unity 原生路径位于
`UserData/Unity/<company>/<product>/`，原生临时目录位于 `UserData/Cache/Temp/`。
这两类原生目录由同一份程序的实例共享；`--datadir` 仍只隔离业务数据。

重定向仅改变当前进程中 UnityPlayer 的一个导入地址和进程环境，不修改磁盘上的
UnityPlayer、系统目录、用户环境或注册表。其余 Known Folder 查询按原样交还 Windows。
旧 `ConfigureUnityCache`、空目录删除、退出 PowerShell 和无效 `-cache-path` 均已移除。

## 兼容性与验收

支持当前 `6000.2.6f2` Windows x64 Mono 构建。该实现使用 Windows PE 导入名，
不写死 Unity 内部函数偏移；Unity 升级仍须重新验证导入接口和首次路径查询时机。
如果无法安装重定向，入口提示启动失败，禁止继续运行未便携化的 Player。

检查时应从启动前开始监测 LocalLow，覆盖正常退出、强制终止及再次启动；只检查退出后的
目录存在性会被旧清理逻辑掩盖。还应在含空格/中文的发行目录中直接运行 exe，确认模型
与 Localization 资源加载。当前结果和证据见 `PROGRESS.md`、ADR-0014。

移出项目时只复制 `MateEngine.exe`、`UnityPlayer.dll`、`UnityCrashHandler64.exe`、
`MateEngine_Data/`、`MonoBleedingEdge/`、`D3D12/` 及需要迁移的 `UserData/`。
`Build/PortableRuntime/` 和 `Build/PortableAudit/` 属于构建/验证产物，不是发行依赖。
外部模型引用仍需连同模型文件迁移；默认 Zome 的分发限制见项目规则。
完整 ZIP 打包命令及更新步骤见 `BUILD_MANUAL.md` 的「制作自用发行包」。
