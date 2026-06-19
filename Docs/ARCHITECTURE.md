# MateEngine Architecture

本文档记录系统如何运作，目标是让 AI 和维护者快速定位代码，不写论文式说明。

## 快速事实

| 项 | 当前值 |
|---|---|
| Unity | `6000.2.6f2`，必须精确匹配 |
| 启动场景 | `Assets/MATE ENGINE - Scenes/Mate Engine Loading.unity` |
| 主场景 | `Assets/MATE ENGINE - Scenes/Mate Engine Main.unity` |
| 渲染管线 | URP |
| 主要语言 | C# |
| 构建目标 | Windows x86_64 |
| 开发期构建 | Mono |
| 命令行构建脚本 | `Assets/Editor/BuildScript.cs` |
| 构建产物 | `Build/MateEngine.exe` |
| 默认 UI 语言 | 简体中文（`zh`） |

## 技术栈

| 组件 | 作用 |
|---|---|
| Unity 6 + C# | 主引擎和运行时逻辑 |
| UniVRM / VRM10 | VRM 0.x / 1.x 模型加载 |
| UniWindowController | 透明、置顶、桌面窗口控制 |
| Newtonsoft.Json | 设置和数据 JSON 序列化 |
| MToon (URP) | 默认卡通着色器 |
| Poiyomi / lilToon | 可选高级 Shader，主要通过 Mod 或角色资源使用 |
| LLMUnity / Qwen 2.5 1.5b | 可选本地 LLM 聊天 |
| VRM SpringBone | 头发、衣物等物理摆动 |

## 目录结构

| 路径 | 职责 |
|---|---|
| `Assets/MATE ENGINE - Scenes/` | 正式场景。当前使用 `Mate Engine Loading.unity` 启动，再进入 `Mate Engine Main.unity` |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/` | 角色动画、跟随、交互、跳舞、坐立等运行时逻辑 |
| `Assets/MATE ENGINE - Scripts/VRMLoader/` | VRM 加载、头像库、多实例启动 |
| `Assets/MATE ENGINE - Scripts/Settings/` | 设置菜单、设置持久化、Mod 加载入口 |
| `Assets/MATE ENGINE - Scripts/Portable/` | 便携路径和旧数据迁移 |
| `Assets/MATE ENGINE - Scripts/Tools/` | UI 工具、删除按钮、截图、自启动等辅助功能 |
| `Assets/AddressableAssetsData/` | Addressables 配置和 Localization 资源分组 |
| `Assets/LLMUnity/` | 本地 LLM 插件和运行时 |
| `Assets/MATE ENGINE - Packages/` | 内置第三方 Unity 包 |
| `Assets/MATE ENGINE - Shaders/` | MToon、Poiyomi、lilToon 等 Shader 资源 |
| `Assets/Editor/` | 编辑器脚本，当前包含命令行构建脚本 |
| `Packages/com.unity.addressables/` | 本地嵌入的 Addressables 包；运行时路径被 patch 为便携缓存 |
| `ProjectSettings/` | Unity 项目设置 |
| `Packages/` | Unity Package Manager 清单 |
| `Build/` | 本地构建产物，已忽略不提交 |
| `UserData/` | 构建产物运行时用户数据根目录 |

## 启动与持久化流程

1. Unity 启动运行时；Unity 内置 Splash Screen 已关闭。
2. `PortablePaths` 在 `BeforeSplashScreen` 阶段初始化。
3. Editor 和命令行构建进程使用项目内 `Library/MateEngineUserData/`，避免构建期写入用户目录。
4. Build 产物默认使用 `{exeDir}/UserData/`。
5. 如果传入 `--datadir <name>`，则使用 `{exeDir}/UserData/<name>/`。
5.5. Unity 原生引擎默认在 `{persistentDataPath}/Unity/{Company}_{Product}/` 初始化 Cache 目录（早于 C# 执行）。**主修复**：`Build/MateEngine.bat` 以 `-cache-path=../UserData/Cache/UnityCache` 命令行参数启动——这是 Unity C++ 引擎原生支持的参数，全程将默认缓存重定向到便携路径。**兜底**：`PortablePaths.PostSceneLoadCleanup()` 清理启动残留 + `PortablePaths.SchedulePostExitCleanup()` 后台 PowerShell 进程在主进程退出后清理 exit 残留。`boot.config` 中的 `cache-path` 行仅供文档标记（Unity 6 不从 boot.config 读取该键）。
6. Build 产物不再回退到 `Application.persistentDataPath`。如果 `UserData/` 不可写，会记录错误并保持便携目标路径，避免静默写入用户目录。
7. `Mate Engine Loading.unity` 作为第一个场景显示项目内加载页；`LoadingScreenController` 运行时创建 Canvas UI，并异步加载 `Mate Engine Main.unity`。
8. Build 产物首次启动时，`PortableMigration` 从旧位置迁移已有数据，并写入 `.migrated` 标记。Windows 旧路径通过 `%USERPROFILE%\AppData\LocalLow\<company>\<product>` 手工计算，只读检查旧数据，避免访问 `Application.persistentDataPath` 本身造成目录创建副作用。
9. `SaveLoadHandler`、`VRMLoader`、Mod、LLM、截图等系统从便携路径读写数据。
10. Unity Standalone 默认 `Player.log` 已禁用，构建产物不会生成 `%USERPROFILE%\AppData\LocalLow\Shinymoon\MateEngineX\Player.log`。
11. Localization 仍依赖 Addressables，但本项目使用 `Packages/com.unity.addressables/` 本地嵌入包，避免 Addressables 运行时初始化访问 `Application.persistentDataPath`，并让本地 `StreamingAssets` bundle 不再触发 `UnityEngine.Caching`。
12. UI 语言默认使用简体中文：`Localization Settings.asset` 的项目语言与兜底选择器为 `zh`，`SaveLoadHandler.SettingsData.selectedLocaleCode` 新建设置默认 `zh`，命令行 `-language=` 仍可覆盖。
13. `MenuAudioHandler` 保留菜单打开、关闭、按钮等交互音效，但不再播放启动完成后的 `startupSounds`；相关序列化字段保留用于场景和 Voice Pack 兼容。
14. 本地 Addressables group 的 `Use Asset Bundle Cache` 已关闭；`BuildScript.BuildWindows` 会先重建 Addressables content，再构建 player，防止旧 catalog 把过时缓存配置打入新 build。
15. 如果外部历史残留或 Unity 其他路径只创建了空的旧 LocalLow 目录，`BuildScript` 和 `PortablePaths` 会在构建开始、构建结束、编辑器退出前、运行初始化后、正常退出时删除空目录；非空目录一律保留。

## 核心脚本

| 脚本 | 职责 |
|---|---|
| `Assets/Editor/BuildScript.cs` | 先重建 Addressables content，再显式指定主场景构建 Windows x64 |
| `Assets/MATE ENGINE - Scripts/Startup/LoadingScreenController.cs` | 项目内加载页 UI 和主场景异步加载 |
| `Assets/MATE ENGINE - Scripts/Portable/PortablePaths.cs` | 统一管理运行时数据目录 |
| `Assets/MATE ENGINE - Scripts/Portable/PortableMigration.cs` | 首次启动迁移旧数据 |
| `Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs` | 设置 JSON 读写 |
| `Assets/MATE ENGINE - Scripts/Settings/LanguageDropdownHandler.cs` | 语言下拉同步与 `selectedLocaleCode` 持久化 |
| `Assets/MATE ENGINE - Scripts/Settings/MenuAudioHandler.cs` | 菜单交互音效；启动完成音效已禁用 |
| `Assets/MATE ENGINE - Scripts/VRMLoader/VRMLoader.cs` | 模型加载、组件注入、模型路径持久化 |
| `Assets/MATE ENGINE - Scripts/VRMLoader/AvatarLibraryMenu.cs` | 头像库 UI、缩略图、选择与删除 |
| `Assets/MATE ENGINE - Scripts/Settings/MEModHandler.cs` | `.me` Mod 运行时加载 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/AvatarAnimatorController.cs` | 角色动画状态机 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/AvatarMouseTracking.cs` | 头部、眼睛、脊柱跟随鼠标 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/HandHolder.cs` | 手部 IK 跟随鼠标 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/PetVoiceReactionHandler.cs` | 鼠标悬停触发身体区域反应 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/AvatarTaskbarController.cs` | 任务栏/窗口边缘坐立 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/AccessoiresHandler.cs` | 配饰跟随骨骼 |
| `Assets/MATE ENGINE - Scripts/BlendshapeManager/BlendshapeManager.cs` | Blendshape 编辑和持久化 |
| `Assets/MATE ENGINE - Scripts/Tools/SystemStartHandler.cs` | 开机自启动，用户开启时写注册表 |

## 便携数据路径

Build 产物优先使用 `{exeDir}/UserData/`。Editor 和命令行构建进程使用项目内 `Library/MateEngineUserData/`，避免 `InitializeOnLoadMethod` 或构建辅助逻辑在 `%USERPROFILE%\AppData\LocalLow\Shinymoon\` 下创建空目录。

| 数据 | 当前路径 |
|---|---|
| 主设置 | `UserData/settings.json` |
| 头像库 | `UserData/avatars.json` |
| 头像文件 | `UserData/VRM/` |
| 缩略图 | `UserData/Thumbnails/` |
| Mod | `UserData/Mods/` |
| Blendshape | `UserData/Blendshapes/` |
| 舞蹈同步 | `UserData/Sync/` |
| 临时缓存 | `UserData/Cache/` |
| Unity 原生缓存 | `UserData/Cache/UnityCache/` |
| Addressables 目录缓存 | `UserData/Cache/com.unity.addressables/` |
| `.me` 缓存 | `UserData/Cache/ME_Cache/` |
| AI 历史和 Prompt | `UserData/AI/` |
| LLM 设置、模型和 RAG 存档 | `UserData/LLM/` |
| MEValueChanger | `UserData/MEValueChanger/` |
| 截图 | `UserData/Screenshots/` |
| 可选 DiscordRPC 文件日志 | `UserData/Cache/discord.log` |
| 可选 DiscordRPC 头像缓存 | `UserData/Cache/DiscordRPC/Avatars/` |

例外：

- `SystemStartHandler` 在用户主动启用/关闭开机自启动时写入或删除 `HKCU\...\Run`，普通启动不触碰。
- `PortableMigration` 只读检查旧 LocalLow 和旧 LLMUnity AppData 目录用于迁移，不向旧位置写入。
- 嵌入的 `Packages/com.unity.addressables/` 已将运行时路径从 `Application.persistentDataPath` 改为便携缓存路径，本地 bundle 跳过 Unity 默认缓存查询，本地 group 已关闭 `Use Asset Bundle Cache`。
- 旧 `PlayerPrefs` 只读迁移，不再调用 `Set*`/`Delete*`/`Save()`。
- `--datadir`、`--savefile` 和 RAG 存档路径被归一化到 `UserData/` 子路径内。
- `DiscordManager.registerUriScheme` 默认关闭；当前主场景使用 `DiscordPresence`，不涉及 URI scheme 注册。
- Unity 编辑器自身可能在 `%LOCALAPPDATA%\Unity\` 等处写入；MateEngine 自身不在构建期写入 `LocalLow\Shinymoon`。

## Mod 系统

`.me` 文件本质是 ZIP，内含 AssetBundle、元数据和 `scene_links.json`。它可以包含自定义 Shader、AnimatorController、AnimationClip、音频、粒子等资源。

约束：

- AssetBundle 不能直接包含 C# 源码脚本。
- 需要逻辑扩展时，应通过预编译 DLL 或现有扩展点接入。
- `MEManipulator` 是关键扩展点，可覆盖部分内置 Controller 逻辑。
- Steam Workshop 已移除，不应恢复相关上传、订阅和 DRM 逻辑。

## 构建

构建说明见 `BUILD_MANUAL.md`。关键点：

- `EditorBuildSettings.asset` 中场景列表可能为空，不能依赖 `-buildWindows64Player`。
- 命令行构建使用 `-executeMethod BuildScript.BuildWindows`。
- `BuildScript.BuildWindows` 会先调用 Addressables content build；构建场景顺序为 `Mate Engine Loading.unity` → `Mate Engine Main.unity`。直接使用 `File -> Build Settings -> Build` 时，需要先手动重建 Addressables。
- 构建输出到 `Build/MateEngine.exe`。
- 构建同时生成 `Build/MateEngine.bat`，以 `-cache-path=../UserData/Cache/UnityCache` 参数启动——这是推荐的启动方式。
- 不需要 Steamworks SDK。

## 已知陷阱

- Unity 版本不匹配会触发资产重导入，甚至造成兼容问题。
- 不要打开 `DEV` / `InDev` 场景。
- 加载场景不应依赖 Localization 或 Addressables；首屏只使用直接序列化引用的 Sprite/TMP 字体和普通 Unity UI。
- 删除旧字段可能破坏 Unity 场景、Prefab、Inspector 引用或旧 JSON 兼容性。
- 运行时新增写入如果绕过 `PortablePaths`，会破坏绿色便携目标。
- 升级 Addressables 时不能直接恢复注册表包；必须复核 `AddressablesImpl`、`ContentCatalogProvider`、`AssetBundleProvider` 是否重新访问 `Application.persistentDataPath` 或重新触发本地 bundle 的 `UnityEngine.Caching`。
- 升级或重建 Addressables 包后，须确认 `Packages/com.unity.addressables/Runtime/AddressablesPortablePaths.cs` 路径计算与 `PortablePaths` 一致。正确启动方式为 `MateEngine.bat`（`-cache-path` 命令行参数），兜底清理由 `SchedulePostExitCleanup()` 处理。`boot.config` 中的 `cache-path` 行仅供文档标记。
- 默认 Avatar 不可随自制 build 分发。
- `ProjectSettings/Packages` 当前在 `.gitignore` 中，需要避免无意义提交 Unity 本地设置。
