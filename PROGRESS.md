# MateEngine Progress

## 说明

本文档维护项目的当前进度、任务列表。执行时先完成“正在处理”下堆积的任务，再处理其他项。阶段性进展完成后务必同步更新本进度文档。

- 上次更新：2026-06-19

## 当前状态

当前仓库是 MateEngine 个人 fork，处于二次开发早期整理阶段。Steam 依赖移除和绿色便携化已完成代码改造。

当前分支：`main`

当前 Git 状态要点：

- Steamworks.NET 包、Steam API/插件/UI 脚本已移除。
- 新增 `Assets/Editor/BuildScript.cs`（命令行构建）、`Assets/MATE ENGINE - Scripts/Portable/`（便携路径管理）。
- 新增嵌入包 `Packages/com.unity.addressables/`，修复运行时默认访问 `Application.persistentDataPath`。
- 新增 `Assets/boot.config` 作为文档标记；`BuildScript.CreateLaunchBatch()` 构建后生成 `MateEngine.bat` 以 `-cache-path` 启动。
- 本地 Addressables group 已关闭 `Use Asset Bundle Cache`。

## 正在处理

- 暂无

## 下一步

- 等待指定

## 已完成

### Steam 依赖移除

- 删除 Steamworks.NET SDK 包。
- 删除 Steam 初始化、Steam Workshop、Steam DRM、Steam 原生插件和上传按钮相关脚本。
- 清理混用 Steam 逻辑的脚本。
- 保留部分旧序列化字段，避免破坏 Unity 场景和旧 JSON 兼容性。

### 构建脚本

- 新增 `Assets/Editor/BuildScript.cs`。
- 使用 `BuildScript.BuildWindows` 显式指定主场景：
  `Assets/MATE ENGINE - Scenes/Mate Engine Main.unity`
- 构建产物输出到 `Build/MateEngine.exe`。

### .gitignore 清理

- 忽略 `UserSettings/`、`.vs/`、Steam 残留文件、Burst debug 目录、LLMManager 运行时配置等。
- `Build/`、`Library/`、`Temp/`、`Logs/`、`obj/` 等本地输出目录不提交。

### 绿色便携化

- 新增 `PortablePaths.cs` 统一管理运行时数据目录，Editor 使用 `Library/MateEngineUserData/`，Build 产物使用 `{exeDir}/UserData/`。
- 新增 `PortableMigration.cs` 从旧 LocalLow/LLMUnity AppData 路径迁移历史数据（按路径手工计算，只读检查，不通过 `Application.persistentDataPath` 访问）。
- 关闭 `PlayerSettings.usePlayerLog`，构建脚本强制 `usePlayerLog = false`，禁止 Build 产物写入 `Player.log`。
- Build 产物不因 `UserData/` 不可写而回退到 `Application.persistentDataPath`。
- 嵌入 `Packages/com.unity.addressables/` 并 patch：移除所有 `Application.persistentDataPath` 访问，本地 bundle 绕过 Unity 默认缓存查询，关闭本地 group `Use Asset Bundle Cache`。构建脚本先重建 Addressables content 再构建 Player。
- **主修复 — 启动器**：`BuildScript.CreateLaunchBatch()` 生成 `Build/MateEngine.bat`，以 `-cache-path=../UserData/Cache/UnityCache` 参数启动 exe（Unity C++ 引擎原生支持的参数，全程重定向默认缓存）。
- **兜底 — 退出清理**：`PortablePaths.SchedulePostExitCleanup()` 派生后台 PowerShell 进程，等待主进程退出后清理可能残留的空 `LocalLow\Shinymoon\MateEngineX` 目录。
- **辅助 — 多点空目录清理**：构建开始/结束、编辑器退出、`BeforeSplashScreen`、`AfterSceneLoad`、`Application.quitting` 各节点删除空旧目录（非空不删）。
- `Assets/boot.config` 中的 `cache-path` 行作为文档标记保留（Unity 6 是命令行参数，非 boot.config 键）。
- 所有运行时写入（LLM、DiscordRPC、截图、缓存等）收敛到 `UserData/`；`--datadir` 等参数限制在 `UserData/` 子路径内；旧 `PlayerPrefs` 只读迁移。

## 可选后续方向

- 程序化空闲动作：自主张望、自主踱步、随机兴趣点。
- 渲染升级：为特定角色挂载专用 Shader，或通过 `.me` Mod 管理高级材质。
- 外部通信接口：`HttpListener`、WebSocket、VMC Protocol、OSC over UDP。
- 继续收敛便携化边界，补充运行时写入审计。

## 已知问题与风险

| 问题 | 状态 | 备注 |
|---|---|---|
| 当前变更尚未提交 | 未处理 | 需要先验证再 commit |
| 默认 Avatar 分发风险 | 持续关注 | Zome 不可随自制 build 分发 |
| Unity 版本敏感 | 持续关注 | 必须使用 `6000.2.6f2` |
| 外部写入审计 | 静态已完成，待运行复核 | 需运行构建产物后确认磁盘和注册表实际行为 |
| 开机自启动写注册表 | 已知例外 | 用户主动启用/关闭时写入或删除 `HKCU\...\Run` |

## 验证记录

| 日期 | 验证项 | 结果 | 备注 |
|---|---|---|---|
| 2026-06-18 | Steam 依赖移除后构建 | 已通过 | 零编译错误 |
| 2026-06-18 | 外部写入静态审计 | 已完成 | 剩余命中为编辑器/移动端路径、旧数据只读迁移或用户主动功能 |
| 2026-06-19 | 便携化代码改造 | 代码完成，待运行验证 | 含 Player.log、Addressables patch、cache-path 重定向、PowerShell 兜底清理 |
| 2026-06-19 | LocalLow 空目录排查（多轮迭代） | 代码完成，待构建/运行复核 | 逐层定位到 Unity C++ 原生引擎 Cache 初始化 → 修复：`-cache-path` 命令行参数（主）+ 后台 PowerShell 清理（兜底） |
