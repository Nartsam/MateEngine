# MateEngine Architecture

本文档记录系统如何运作，目标是让 AI 和维护者快速定位代码，不写论文式说明。

## 快速事实

| 项 | 当前值 |
|---|---|
| Unity | `6000.2.6f2`，必须精确匹配 |
| 启动场景 | `Assets/MATE ENGINE - Scenes/Mate Engine Loading.unity` |
| 主场景 | `Assets/MATE ENGINE - Scenes/Mate Engine Main.unity` |
| 渲染管线 | Built-in（内置渲染管线）。注意：`Assets/Settings/` 下存在 `PC_RPAsset`/`Mobile_RPAsset`/`UniversalRenderPipelineGlobalSettings` 等 URP 残留资产，但 URP 包并未安装（不在 `manifest.json`/`packages-lock.json`/`PackageCache`/嵌入包中），`GraphicsSettings`/`QualitySettings` 也未指派任何 SRP，因此实际运行在 Built-in。这些 URP 资产为惰性残留，不要据此误判为 URP。 |
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
| MToon（Built-in 变体） | 默认 VRM 卡通着色器。默认角色 Zome 材质即使用 `Assets/MATE ENGINE - Packages/VRM/MToon/Shaders/MToon.shader`（Built-in 变体，非 URP 变体） |
| UTS2 / Poiyomi / lilToon | 工程内已内置的 Built-in NPR 卡通着色器，可用于高级/原神风渲染。UTS2 位于 `Assets/MATE ENGINE - Packages/Toon/`（Unity-Chan Toon Shader 2），Poiyomi/lilToon 位于 `Assets/MATE ENGINE - Shaders/`。主要通过 Mod 或角色资源使用 |
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
| `Assets/MATE ENGINE - Shaders/` | Poiyomi、lilToon、Mochie 等 Shader 资源（MToon 在 `MATE ENGINE - Packages/VRM/MToon/`，UTS2 在 `MATE ENGINE - Packages/Toon/`） |
| `Assets/Editor/` | 编辑器脚本，包含命令行构建脚本与编辑器工具 |
| `Assets/Editor/PmxPipeline/` | PMX 离线导入管线：解析 PMX、生成网格/骨架/BlendShape、Humanoid Avatar、DynamicBone/碰撞体、Blender 渲染预设映射，并导出 `.me` |
| `Tools/PmxPipeline/` | PMX 离线管线辅助脚本（Blender background 渲染预设导出）与 `styles/<模型名>.style.json` 逐模型风格配置 |
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
| `Assets/Editor/PmxPipeline/MEPmxPipeline.cs` | PMX 离线管线 batch/menu 入口；`BuildModel`、`BuildAndExport`、`ExportMe` 通过 CLI 或本机配置取路径 |
| `Assets/Editor/PmxPipeline/PmxPipelineOptions.cs` | PMX 管线路径和参数聚合；优先 CLI，其次 ignored 本机配置 `Library/MateEngineUserData/PmxPipeline/settings.json`，最后项目相对默认输出 |
| `Assets/Editor/PmxPipeline/PmxMeshBuilder.cs` | PMX 网格、骨架、BlendShape、Humanoid、材质、物理与 prefab 构建 |
| `Assets/Editor/PmxPipeline/PmxPhysics.cs` | PMX 动态刚体/关节转换为 DynamicBone 链，并按 Humanoid 腿部/髋部骨生成 Skirt 专用 DynamicBoneCollider |
| `Assets/Editor/PmxPipeline/PmxRenderPreset.cs` | Blender 渲染预设 JSON DTO 与材质匹配逻辑 |
| `Assets/Editor/PmxPipeline/PmxMaterialMapper.cs` | PMX 材质与 Blender preset 参数到 UTS2 材质的映射；贴图材质保留 PMX 贴图原色，只接受有效 preset 颜色，并使用保守的软 toon 阴影、rim 与轮廓参数；matcap/highColor 默认关闭；消费 `PmxStyleConfig` 的逐模型风格覆盖 |
| `Assets/Editor/PmxPipeline/PmxStyleConfig.cs` | 逐模型风格配置（JSON）DTO 与加载器；把材质风格调参从 C# 解耦到数据。Shader 无关旋钮：`materialProfile`、`skinWarmth`、`brightness`、`outlineScale`、`rimStrength`，及逐材质 `baseColor`/`shadeColor`/`outline` 覆盖 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/PmxModelRenderProfile.cs` | PMX prefab 运行时后处理配置；加载时临时应用 Bloom/ColorGrading（**注意：当前主场景相机未挂 PostProcessLayer，Built-in 后处理 Volume 不生效**，需后续接通），卸载时还原/清理 |
| `Assets/MATE ENGINE - Scripts/Startup/LoadingScreenController.cs` | 项目内加载页 UI 和主场景异步加载 |
| `Assets/MATE ENGINE - Scripts/Portable/PortablePaths.cs` | 统一管理运行时数据目录 |
| `Assets/MATE ENGINE - Scripts/Portable/PortableMigration.cs` | 首次启动迁移旧数据 |
| `Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs` | 设置 JSON 读写 |
| `Assets/MATE ENGINE - Scripts/Settings/LanguageDropdownHandler.cs` | 语言下拉同步与 `selectedLocaleCode` 持久化 |
| `Assets/MATE ENGINE - Scripts/Settings/MenuAudioHandler.cs` | 菜单交互音效；启动完成音效已禁用 |
| `Assets/MATE ENGINE - Scripts/Settings/MenuActions.cs` | 右键径向菜单开关、阻塞状态和打开瞬间定位；菜单打开后位置保持固定 |
| `Assets/MATE ENGINE - Scripts/VRMLoader/VRMLoader.cs` | 模型加载、组件注入、模型路径持久化 |
| `Assets/MATE ENGINE - Scripts/VRMLoader/AvatarLibraryMenu.cs` | 头像库 UI、缩略图、选择与删除 |
| `Assets/MATE ENGINE - Scripts/Settings/MEModHandler.cs` | `.me` Mod 运行时加载 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/AvatarAnimatorController.cs` | 角色动画状态机 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/AvatarMouseTracking.cs` | 头部、眼睛、脊柱跟随鼠标 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/HandHolder.cs` | 手部 IK 跟随鼠标 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/PetVoiceReactionHandler.cs` | 鼠标悬停触发身体区域反应 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/AvatarTaskbarController.cs` | 任务栏/窗口边缘坐立 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/AccessoiresHandler.cs` | 配饰跟随骨骼 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/ChibiToggle.cs` | 旧 Chibi/大头缩身组件；入口保留但当前 fork 中功能已屏蔽 |
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

## PMX 离线管线

PMX 管线是 Editor-only 预处理流程，目标是把 PMX 模型、MMD 物理和 Blender NPR 渲染预设转换为 Humanoid 模型，打包为 `.me` 格式（UTS2 材质 + DynamicBone 物理）。操作流程见 `Docs/PMX_TO_VRM.md`。注意：曾探索的 VRM+RenderStyle 路径（ADR-0009）已于 2026-06-21 归档（复盘见 `Docs/DECISIONS_RECORD.md` ADR-0010）。

路径规则：

- 外部输入只通过 CLI 参数或本机 ignored 配置注入，不在业务代码中写死 Blender、PMX、`.blend`、模型目录或导出文件名。
- CLI 优先级最高：`-pmx`、`-preset`、`-blender`、`-out`、`-outputRoot`、`-tempRoot`、`-modelName`。
- 本机默认值保存在 `Library/MateEngineUserData/PmxPipeline/settings.json`，仅供菜单和本机复用；`Library/` 已忽略，不提交。
- 缺少必需的 Blender/PMX/preset 输入时，batch 入口报清晰错误；不会回退到某个开发机绝对路径。
- 生成到 Unity 资产里的内容只保存贴图引用、颜色、float、开关和已解析的后处理数值，不保存外部绝对路径。
- Blender preset 的贴图节点默认 `baseColor` 可能是黑色占位值；导入器不会把这类颜色直接乘到 PMX 贴图上，而是保留贴图原色，并把 preset 作为 toon 参数、matcap、后处理等 hints 使用。缺少专用 SDF/lightmap 时，UTS2 映射优先使用浅色 shade tint 与软 feather，避免硬切大色块。

主要 batch 入口：

- `MateEngine.PmxPipeline.MEPmxPipeline.BuildModel`
- `MateEngine.PmxPipeline.MEPmxPipeline.BuildAndExport`
- `MateEngine.PmxPipeline.MEPmxPipeline.ExportMe`

### 风格调参（解耦，无需改代码）

材质风格不再写死在 C# 里，而是逐模型的 `Tools/PmxPipeline/styles/<模型名>.style.json`（缺失则用代码默认；`-style <path>` 可覆盖）。旋钮均为 shader 无关概念，便于将来切换材质阶段（如 HoYo 着色器）后沿用。两种迭代循环：

1. **配置循环（无需重编译）**：编辑 `<模型名>.style.json`（`skinWarmth`/`brightness`/`outlineScale`/`rimStrength`，或逐材质 `baseColor`/`shadeColor`/`outline`），重跑 `BuildModel`（不必跑 Blender）即可。改的是数据，不触发 C# 编译。
2. **Editor 实时循环（最快，无需重编译/重建材质）**：构建一次后，在 Unity 编辑器里直接选中生成的材质 `Assets/PmxImported/<模型名>/*.mat`，在 Inspector 拖 UTS2 滑块实时预览；满意后只跑 `ExportMe`（仅重新打包 prefab → `.me`，约 10 秒，不跑 Blender、不重建材质）。

> 注意：`materialProfile` 当前仅 `uts2`。`PmxMaterialMapper` 是当前材质阶段实现；将来 vendor HoYo Built-in 着色器后，新增对应 profile 分支，配置框架与 Editor 工作流不变。

### 新增模型流程

提供 PMX（+ 可选 `.blend` 渲染预设），跑 `BuildAndExport -pmx <pmx> [-preset <blend> -blender <exe>]`。骨架/物理对任何「MMD 准标准」模型通用；需要补的是：① 该模型一份 `styles/<模型名>.style.json`；② 若材质命名非中文 HoYo 习惯，可能要扩 `PmxMaterialMapper.Classify` 的候选词。`<模型名>` 由 `-modelName` 或 PMX 内部名解析得到（决定输出目录与风格文件名）。

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
- 右键径向菜单只在打开瞬间根据人物骨骼定位；不要恢复打开后的逐帧骨骼跟随，否则舞蹈等场景会导致菜单漂移。
- Chibi/大头缩身功能已在菜单和代码入口屏蔽；保留字段和组件是为了避免破坏场景、Prefab、Mod 设置引用。隐藏径向菜单项时应保持源按钮 inactive，并由 `CircleSelector` 在运行时过滤，避免按钮列表和命中扇区错位。
- 删除旧字段可能破坏 Unity 场景、Prefab、Inspector 引用或旧 JSON 兼容性。
- 运行时新增写入如果绕过 `PortablePaths`，会破坏绿色便携目标。
- 升级 Addressables 时不能直接恢复注册表包；必须复核 `AddressablesImpl`、`ContentCatalogProvider`、`AssetBundleProvider` 是否重新访问 `Application.persistentDataPath` 或重新触发本地 bundle 的 `UnityEngine.Caching`。
- 升级或重建 Addressables 包后，须确认 `Packages/com.unity.addressables/Runtime/AddressablesPortablePaths.cs` 路径计算与 `PortablePaths` 一致。正确启动方式为 `MateEngine.bat`（`-cache-path` 命令行参数），兜底清理由 `SchedulePostExitCleanup()` 处理。`boot.config` 中的 `cache-path` 行仅供文档标记。
- 默认 Avatar 不可随自制 build 分发。
- `ProjectSettings/Packages` 当前在 `.gitignore` 中，需要避免无意义提交 Unity 本地设置。
