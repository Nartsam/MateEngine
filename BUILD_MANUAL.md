# MateEngine 构建说明

## 环境要求

- **Unity**：6000.2.6f2（必须精确匹配）
- **Visual Studio 2022**：勾选「使用 Unity 的游戏开发」工作负载
- IL2CPP 构建额外需要「使用 C++ 的桌面开发」工作负载；Mono 构建不需要

## 构建步骤

### 方式一：命令行构建（推荐）

```powershell
& "D:\Program Files\Unity\6000.2.6f2\Editor\Unity.exe" `
  -batchmode -nographics -quit `
  -projectPath "<项目路径>" `
  -executeMethod BuildScript.BuildWindows `
  -logFile "<项目路径>\Build\build.log"
```

构建产物输出到 `Build/MateEngine.exe`。首次构建约 5-10 分钟（含资产导入），后续约 2-5 分钟。

### 方式二：Unity 编辑器手动构建

1. Unity Hub 打开项目
2. 打开场景 `Assets/MATE ENGINE - Scenes/Mate Engine Main.unity`
3. `File → Build Settings → Add Open Scenes`
4. 确认 Platform 为 `Windows`，Architecture 为 `x86_64`
5. 点击 `Build`，选择输出目录

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
