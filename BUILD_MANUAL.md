# MateEngine 构建说明

## 环境要求

- **Unity**：6000.2.6f2（必须精确匹配）
- **Visual Studio 2022**：勾选「使用 Unity 的游戏开发」工作负载
- IL2CPP 构建额外需要「使用 C++ 的桌面开发」工作负载；Mono 构建不需要

## 构建步骤

### 方式一：命令行构建（推荐）

```powershell
& "<6000.2.6f2 版 Unity 安装文件夹>\Unity.exe" `
  -batchmode -nographics -quit `
  -projectPath "<项目路径>" `
  -executeMethod BuildScript.BuildWindows `
  -logFile "<项目路径>\Build\build.log"
```

& "D:\Program Files\Unity\Hub\Editor\6000.2.6f2\Editor\Unity.exe" -batchmode -nographics -quit -projectPath "." -executeMethod BuildScript.BuildWindows -logFile ".\Build\build.log"

实时查看监控： Get-Content ".\Build\build.log" -Wait

构建产物输出到 `Build/MateEngine.exe`。首次构建约 5-10 分钟（含资产导入），后续约 2-5 分钟。

`BuildScript.BuildWindows` 会先重建 Addressables content，再构建 player，避免旧的 `catalog.bin` 沿用过时的本地 bundle 缓存配置。构建场景顺序固定为：

1. `Assets/MATE ENGINE - Scenes/Mate Engine Loading.unity`
2. `Assets/MATE ENGINE - Scenes/Mate Engine Main.unity`

### 方式二：Unity 编辑器菜单构建

1. Unity Hub 打开项目
2. 打开场景 `Assets/MATE ENGINE - Scenes/Mate Engine Loading.unity`
3. 菜单执行 `Build → Build Windows x64`

如需直接使用 `File → Build Settings → Build`，先手动执行一次 `Window → Asset Management → Addressables → Groups → Build → New Build → Default Build Script`；否则 Player 可能继续带入旧的 Addressables catalog。

## 构建产物位置

| 位置 | 说明 |
|---|---|
| `Build/` | 最终可执行文件和运行时数据 |
| `Library/` | 资产导入缓存（项目内，可删除后重建） |
| `Temp/` | 编译临时文件（编辑器关闭后自动清理） |

## 注意事项

- 本项目已移除 Steam 依赖，无需 Steamworks SDK
- `Build/` 目录已在 `.gitignore` 中，不会被提交
- 首次打开项目时 Unity 会导入全部资产，耗时较长属正常现象
- 构建脚本位于 `Assets/Editor/BuildScript.cs`
- 构建脚本会先重建 Addressables content，再构建 Player；命令行构建和编辑器菜单 `Build → Build Windows x64` 走的是同一套流程
- Unity 内置 Splash Screen 已关闭。启动首屏由 `Mate Engine Loading.unity` 提供，再异步进入主场景
- 构建脚本会强制关闭 Unity Player Log；运行构建产物时不应生成 `%USERPROFILE%\AppData\LocalLow\Shinymoon\MateEngineX\Player.log`
- MateEngine 自身的构建期初始化数据写入项目内 `Library/MateEngineUserData/`，不应在 `%USERPROFILE%\AppData\LocalLow\Shinymoon\` 下创建空目录
- Addressables 使用项目内嵌入包 `Packages/com.unity.addressables/`；运行时 Localization/Addressables 初始化不应再访问 `Application.persistentDataPath`，本地 bundle 也不应再因为缓存查询创建 `%USERPROFILE%\AppData\LocalLow\Shinymoon\MateEngineX\` 空目录
- 构建开始、构建结束和编辑器退出前仍会清理可能残留的空 `%USERPROFILE%\AppData\LocalLow\Shinymoon\MateEngineX\` 目录；目录非空时不会删除
- 运行时用户数据默认写入构建目录旁的 `UserData/`。请把便携版放在可写目录运行，Build 产物不会再回退写入 `Application.persistentDataPath`
- Unity 编辑器本身仍可能写入 Unity 的全局缓存、License 或设置目录；这类行为不由项目脚本控制
