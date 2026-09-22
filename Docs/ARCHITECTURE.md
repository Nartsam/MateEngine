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
| DynamicBone | `.me`/PMX 模型的头发、裙摆、胸部等 MMD 物理近似运行时组件；位于 `Assets/DynamicBone/` |
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
| `Assets/DynamicBone/` | DynamicBone 运行时组件；PMX 离线管线把 MMD 刚体/关节近似转换为这些组件 |
| `Assets/Editor/` | 编辑器脚本，包含命令行构建脚本与编辑器工具 |
| `Assets/Editor/PmxPipeline/` | PMX 离线导入管线的 Unity 侧：解析 PMX、生成网格/骨架/BlendShape、Humanoid Avatar、DynamicBone/碰撞体、装配转译材质与描边外壳、差分对照渲染，并导出 `.me` |
| `Tools/PmxPipeline/` | PMX 渲染复刻管线的 Blender / Python 侧：节点图导出、转译器、描边烘焙、双端差分验收。操作参考见该目录的 `README.md` |
| `Tools/PmxPipeline/hlsl/` | 转译器内联进每个生成着色器的节点求值函数库 |
| `Tools/PmxPipeline/verify/` | Blender 节点求值语义的逐点核对装置（哈希、噪声、曲线顺序） |
| `Tools/PmxPipeline/styles/` | **UTS2 兜底路径**的逐模型风格配置，不属于转译路线 |
| `Assets/PmxTranspileRuntime/` | 转译路线的运行时合成（bloom + 视图变换），alpha 感知 |
| `Assets/Generated/PmxTranspiled/` | 转译出的着色器与材质清单，已忽略；一条命令可重建 |
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
| `Assets/Editor/PmxPipeline/PmxMaterialMapper.cs` | PMX 材质与 Blender preset 参数到 UTS2 材质的映射；贴图材质保留 PMX 贴图原色，只接受有效 preset 颜色，并使用保守的软 toon 阴影、rim 与轮廓参数；matcap/highColor 默认关闭；消费 `PmxStyleConfig` 的逐模型风格覆盖。**注：2026-06-22 试过在此恢复高光/硬卡通追 HoYo，失败已回退，勿重试，见 ADR-0010 失败路线** |
| `Assets/Editor/PmxPipeline/PmxStyleConfig.cs` | 逐模型风格配置（JSON）DTO 与加载器；把材质风格调参从 C# 解耦到数据。Shader 无关旋钮：`materialProfile`、`skinWarmth`、`brightness`、`outlineScale`、`rimStrength`，及逐材质 `baseColor`/`shadeColor`/`outline` 覆盖 |
| `Assets/Editor/PmxPipeline/MERefRenderer.cs` | 差分验收的对照端：建模型、装配转译材质、并入描边外壳、按 Blender 报告对齐相机与方向 uniform、4× 超采样渲染、跑合成，出对照图与材质 ID 通道 |
| `Assets/Editor/PmxPipeline/METranspiledMaterials.cs` | 读转译器的 `bindings.json`，建 LUT 贴图与各贴图，按材质名替换槽位 |
| `Assets/Editor/PmxPipeline/MEOutlineShell.cs` | 把 Blender 烘好的描边外壳并进基础网格：翻转绕序、按焊接组平均骨权重与 BlendShape 位移 |
| `Assets/PmxTranspileRuntime/MEPostCompose.cs` | 离屏线性 HDR 缓冲上的合成：bloom（阈值/降采样/帐篷升采样/累加）+ Filmic 视图变换。**不是 Built-in 后处理栈**，是显式保留 alpha 的自写 pass，不破坏分层透明窗口（见 ADR-0010、ADR-0012） |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/RenderStyleManager.cs` | 运行时可切换渲染风格单例（自举，无需改场景）。内置 `默认/鲜艳/柔和`；`SetStyle` 驱动相机 `RenderStyleImageEffect` 做**保留 alpha 的全屏饱和/对比/亮度**分级（路线 A），随 `SettingsData.renderStyle` 持久化。**不用 Built-in 全屏后处理**——它会破坏分层透明窗口的 alpha（见 ADR-0010）|
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/RenderStyleImageEffect.cs` | 相机 `OnRenderImage` 跑 `Hidden/RenderStyleGrade`；inactive 时纯 passthrough（`默认`=无效果）|
| `Assets/MATE ENGINE - Shaders/Resources/RenderStyleGrade.shader` | 全屏调色 shader，保留源 alpha 且不动全透明像素 → 透明窗口安全。放 Resources 供运行时 `Resources.Load` |
| `Assets/Editor/PmxBlendshapeDump.cs` | 编辑器工具：load `.me` bundle dump 所有 morph 名到 `Build/blendshape_names.txt`（供 Blendshape 本地化字典取真实名）|
| `Assets/MATE ENGINE - Scripts/BlendshapeManager/BlendshapeNameLocalizer.cs` | Blendshape 菜单日文 morph 名→简中静态字典（未命中原样返回）；与字体烤入共享字符集 |
| `Assets/MATE ENGINE - Scripts/AvatarHandlers/PmxModelRenderProfile.cs` | 旧 PMX prefab 运行时后处理配置（**已退役**）。后处理改由 `RenderStyleManager` 全局统管；本组件在 Manager 存在时 `Apply` no-op，仅为兼容已烤进的旧 `.me` 而保留序列化字段。新 `.me` 不再挂载（`PmxMeshBuilder.AddRenderProfile` 停挂）|
| `Assets/MATE ENGINE - Scripts/Startup/LoadingScreenController.cs` | 项目内加载页 UI 和主场景异步加载 |
| `Assets/MATE ENGINE - Scripts/Portable/PortablePaths.cs` | 统一管理运行时数据目录 |
| `Assets/MATE ENGINE - Scripts/Portable/PortableMigration.cs` | 首次启动迁移旧数据 |
| `Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs` | 设置 JSON 读写 |
| `Assets/MATE ENGINE - Scripts/Settings/LanguageDropdownHandler.cs` | 语言下拉同步与 `selectedLocaleCode` 持久化 |
| `Assets/MATE ENGINE - Scripts/Settings/RenderStyleDropdownHandler.cs` | 渲染风格下拉：从 `RenderStyleManager` 填充选项，绑 `SettingsData.renderStyle`，切换调 `RenderStyleManager.SetStyle` |
| `Assets/Editor/RenderStyleSceneSetup.cs` | 编辑器命令（菜单 `MateEngine ▸ Render Style ▸ Add Settings Dropdown` 或 `-executeMethod RenderStyleSceneSetup.AddDropdownBatch`）：克隆设置菜单 Graphics 下拉生成渲染风格下拉并接线，幂等可逆 |
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

Editor-only 的预处理流程：把 PMX 模型、MMD 物理和配套 `.blend` 渲染预设转换成 Humanoid
模型并打包 `.me`。**它依赖 Blender 3.6 与 Unity 编辑器，不进 App**（ADR-0012）。

管线分两段，彼此独立：

| 段 | 做什么 | 状态 |
|---|---|---|
| 几何与物理 | 解析 PMX → 网格/骨架/BlendShape → Humanoid Avatar → MMD 刚体关节转 DynamicBone → `.me` | 已可用，三次材质路线更替都没动它 |
| 材质 | 渲染预设 → 材质 | **主线是节点图转译**；UTS2 映射作为兜底保留 |

### 材质：节点图转译（主线）

预设是一张**无光照纯函数**的节点图，因此可以整体转译成 HLSL 精确复刻，而不是用通用卡通
着色器的旋钮去拟合。三条参数拟合路线（MToon/HoyoToon、UTS2、lilToon）均已证伪，勿重试。

- **怎么跑、每个文件干什么**：`Tools/PmxPipeline/README.md`
- **路线取舍**：ADR-0011；**集成决策**：ADR-0012
- **实测结论与分阶段验收数值**：`Docs/plans/2026-09-10-blender-graph-transpile.md`

数据流：`.blend` →（节点图 JSON + 打包贴图 + 描边外壳）→ 转译器 →（逐材质 HLSL +
`bindings.json`）→ Unity 侧装配到 PMX 网格上。验收靠 Blender 无头渲染与 Unity 渲染的
逐像素差分（轮廓 IoU + 交集内 ΔE76 + 逐材质分解），不靠肉眼判断。

### 材质：UTS2 兜底（保留，非生产路径）

`PmxMaterialMapper` + `PmxStyleConfig` + `Tools/PmxPipeline/styles/<模型名>.style.json`。
`materialProfile = "uts2"` 时启用。它做不到高保真复刻（根因见 ADR-0010/0011），保留是为了
不破坏已经烤出去的旧 `.me`，以及在转译路线出问题时有个能出图的退路。

风格旋钮均为 shader 无关概念：`skinWarmth`/`brightness`/`outlineScale`/`rimStrength`，
以及逐材质 `baseColor`/`shadeColor`/`outline` 覆盖。两种不重编译的迭代循环：改
`style.json` 重跑 `BuildModel`；或构建一次后在 Inspector 里直接调 `Assets/PmxImported/
<模型名>/*.mat`，满意后只跑 `ExportMe`。

### 路径规则

外部输入只通过 CLI 参数、环境变量或本机 ignored 配置注入，**不在业务代码或脚本默认值里
写死** Blender、Unity、PMX、`.blend` 或模型库路径。

- 仓库内的路径一律由脚本自身位置推出（PowerShell 用 `$PSScriptRoot`，C# 用项目相对路径），
  跟着仓库走，换机器换目录都不受影响。
- 仓库外的三个路径按「命令行 > 环境变量 > 本机配置 > 自动探测」解析，全落空则报错说明怎么
  配，不回退到某台开发机的绝对路径。对应关系见 `Tools/PmxPipeline/README.md`。
- 本机配置是 `Library/MateEngineUserData/PmxPipeline/settings.json`（`Library/` 已忽略），
  PowerShell 与 C# 两侧共用这一个文件。
- 生成到 Unity 资产里的内容只保存贴图引用、颜色、float、开关和已解析的数值，不保存外部
  绝对路径。

### 主要入口

| 入口 | 作用 |
|---|---|
| `Tools/PmxPipeline/run_ref_diff.ps1` | 渲染复刻的完整闭环：导出节点图 → 烘描边 → 转译 → 双端渲染 → 差分 |
| `MateEngine.PmxPipeline.MEPmxPipeline.BuildModel` | 由 PMX 构建 prefab |
| `MateEngine.PmxPipeline.MEPmxPipeline.BuildAndExport` | 构建并打包 `.me` |
| `MateEngine.PmxPipeline.MEPmxPipeline.ExportMe` | 只重新打包 `.me` |

### 新增模型

把 `<角色>/` 放进模型库目录，内含与目录同名的 `.pmx` 和 `渲染预设.blend`，然后跑
`run_ref_diff.ps1 -Character <角色> -ExportGraph -BakeOutline`。骨架与物理对任何「MMD 准
标准」模型通用；渲染侧不需要逐模型配置——转译器直接读该角色自己的预设。

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
- `ProjectSettings/Packages/` 当前有两个设置文件被 Git 跟踪（`com.unity.muse.common`、`com.unity.testtools.codecoverage`），且不再整目录忽略；如果 Unity 自动改动这些文件，需确认是否与任务有关，避免无意义提交。
