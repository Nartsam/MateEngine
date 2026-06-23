# Decision Records

记录影响长期维护的架构决策。普通 Bug 修复、UI 调整和排查过程不记入。

## 索引

| ID | 日期 | 决策 | 状态 |
|---|---|---|---|
| ADR-0001 | 2026-06-18 | Steam 移除与绿色便携化基础 | Accepted |
| ADR-0002 | 2026-06-18 | 自定义构建系统 | Accepted |
| ADR-0003 | 2026-06-19 | 嵌入并 patch Addressables | Accepted |
| ADR-0004 | 2026-06-19 | Unity 原生缓存重定向 | Accepted |
| ADR-0005 | 2026-06-19 | 项目内自定义加载场景替代 Unity Splash | Accepted |
| ADR-0006 | 2026-06-19 | Git 换行与二进制属性策略 | Accepted |
| ADR-0007 | 2026-06-20 | 设置菜单滚动区域运行时高度适配 | Accepted |
| ADR-0008 | 2026-06-20 | PMX 离线导入管线与渲染路线取舍 | Accepted |
| ADR-0009 | 2026-06-21 | VRM+RenderStyle 弯路复盘：.me 回归为主路径 | Accepted |


## ADR-0001: Steam 移除与绿色便携化基础

状态：Accepted

背景：

当前 fork 目标是个人自用绿色便携软件——删除程序目录即可清理所有运行时数据，不在用户目录留下残留。上游项目依赖 Steamworks.NET SDK、Steam Workshop 和 DRM。

决策：

- 删除 Steamworks.NET SDK、Steam API 脚本、Steam 原生插件和上传相关 UI。
- 保留 `AvatarEntry.isSteamWorkshop`、`steamFileId` 等序列化字段，避免破坏 Unity 场景和旧 JSON。
- 运行时数据统一写入 `{exeDir}/UserData/`，由 `PortablePaths` 作为唯一入口。
- Editor 模式使用项目内 `Library/MateEngineUserData/`，避免构建期触碰 `%USERPROFILE%\AppData\LocalLow\`。
- Build 产物不因 `UserData/` 不可写而回退到 `Application.persistentDataPath`——只记录错误并保持便携路径。
- 关闭 `PlayerSettings.usePlayerLog`，构建脚本强制 `usePlayerLog = false`。
- 旧 `PlayerPrefs` 只读迁移，不再写入或删除旧键。
- 所有运行时写入（LLM、DiscordRPC、截图、缓存等）收敛到 `UserData/`。
- `--datadir`、`--savefile` 等命令行参数被限制在 `UserData/` 子路径内，防止路径逃逸。
- `PortableMigration` 在首次启动时从旧路径迁移数据：旧路径按 `%USERPROFILE%\AppData\LocalLow\<company>\<product>` 手工计算并只读检查，不通过 `Application.persistentDataPath` 访问（访问该属性即创建目录）。

原因：

- 绿色便携目标要求所有运行时数据在程序目录内。
- Editor/构建期避免触碰用户目录，防止构建进程副作用。
- 静默 fallback 会掩盖便携失败并在用户目录留下数据。

后果：

- 新增运行时写入必须接入 `PortablePaths`。
- 构建和运行不再依赖 Steamworks SDK。
- 开机自启动写 `HKCU\...\Run` 是用户主动启用功能的必要例外。
- Unity 编辑器自身缓存和 License 仍属编辑器正常行为。


## ADR-0002: 自定义构建系统

状态：Accepted

背景：

`EditorBuildSettings.asset` 场景列表为空，Unity 命令行 `-buildWindows64Player` 无法找到场景。另外，Unity 原生 Cache 系统需要通过命令行参数在 C++ 层重定向。

决策：

- 新增 `Assets/Editor/BuildScript.cs`，使用 `BuildPipeline.BuildPlayer` 显式指定场景和输出。
- 构建前先执行 Addressables content build，防止旧 catalog 混入新 Player。
- 构建成功后生成 `Build/MateEngine.bat`，以 `-cache-path=../UserData/Cache/UnityCache` 参数启动 exe，并转发用户附加参数（`%*`）。
- `Build/MateEngine_Data/boot.config` 中的 `cache-path` 行作为文档标记保留（Unity 6 不从 boot.config 读取该键）。

原因：

- 构建入口不依赖编辑器 Build Settings 场景列表。
- `-cache-path` 是 Unity C++ 引擎原生支持的**命令行参数**，能在引擎全生命周期重定向默认缓存——这是唯一在 C++ 层全程生效的机制。
- `.bat` 启动器对用户透明，双击即可启动。

后果：

- 修改主场景路径或输出名称时必须同步更新 `BuildScript.cs`。
- 推荐用户使用 `MateEngine.bat` 启动；直接双击 exe 的场景由 ADR-0004 兜底覆盖。


## ADR-0003: 嵌入并 patch Addressables

状态：Accepted

背景：

项目通过 `com.unity.localization` 使用多语言，Localization 依赖 Addressables。Addressables 2.7.2 运行时在 `AddressablesImpl.InitializeAsync`、`ContentCatalogProvider` 等多处无条件访问 `Application.persistentDataPath`，仅访问该属性即创建 `LocalLow\Shinymoon\MateEngineX` 空目录。本地 bundle 的 `AssetBundleProvider` 还会通过 `Caching.IsVersionCached` 触发 Unity 默认缓存初始化。

决策：

- 将 Addressables 2.7.2 从 `Library/PackageCache` 嵌入到 `Packages/com.unity.addressables/`，作为项目内本地包。
- 新增 `AddressablesPortablePaths`，独立计算与 `PortablePaths` 一致的缓存根。
- Patch 所有 `Application.persistentDataPath` 访问为便携缓存路径。
- 本地 bundle 的缓存查询改为只对远程路径启用，避免 `StreamingAssets` bundle 触发 `UnityEngine.Caching`。
- 关闭所有本地 Addressables group 的 `Use Asset Bundle Cache`。
- 构建脚本先重建 Addressables content 再构建 Player。

原因：

- 修改 `Library/PackageCache` 不可持续；嵌入包是 Unity 支持的项目级覆写方式，改动可提交、可审计。
- 整体替换 Localization/Addressables 成本过高。
- Addressables 程序集不能直接依赖项目脚本程序集，便携缓存助手放在嵌入包内。

后果：

- 升级 Addressables 时必须重新应用并验证本地 patch。
- 直接删除嵌入包或恢复注册表包会重新引入问题。
- Addressables 远程目录缓存（如未来启用）会写入 `UserData/Cache/com.unity.addressables/`。


## ADR-0004: Unity 原生缓存重定向与退出兜底清理

状态：Accepted

背景：

前几轮修复已消除所有 C# 层 `Application.persistentDataPath` 访问，但构建产物运行时仍在 `LocalLow\Shinymoon\MateEngineX` 下产生空目录。根因在 Unity C++ 原生引擎：

- **启动时**：C++ 层初始化默认 Cache 系统，在 `{persistentDataPath}/Unity/{Company}_{Product}/` 创建目录树——早于任何 C# 代码。
- **退出时**：C++ 引擎在 shutdown 阶段访问 `Caching.defaultCache` flush 元数据，重建目录树——晚于所有 C# 钩子（`Application.quitting` 之后）。

关键发现：`cache-path` 是 Unity **命令行参数**（`-cache-path`），不是 `boot.config` 键。Unity 6 不从 boot.config 读取它。

决策（双层防御）：

**第一层 — 主修复**：`BuildScript.CreateLaunchBatch()` 生成 `MateEngine.bat`，以 `-cache-path=../UserData/Cache/UnityCache` 启动。这是 Unity C++ 引擎原生支持的参数，全程将默认缓存重定向到便携路径——启动不创建目录，退出也不创建。

**第二层 — 兜底**：`PortablePaths.SchedulePostExitCleanup()` 在 `Application.quitting` 时派生分离的 PowerShell 后台进程：等待主进程退出（30s 超时）→ 检查目录为空 → 删除目录及空父目录。覆盖用户直接双击 exe（未用 .bat）的场景。

另外保留 `CleanupEmptyLegacyDataPath()` 在 `BeforeSplashScreen`、`AfterSceneLoad`、`Application.quitting` 各节点的调用，以及 `ConfigureUnityCache()` 的 C# 层缓存设置。

原因：

- `-cache-path` 是唯一能在 C++ 引擎层全程重定向默认缓存的机制。
- 兜底进程处理用户可能直接双击 exe 的边界情况。
- 后台进程无僵尸风险（脚本路径有限确定，必然退出），无资源泄漏（不持有文件句柄或锁）。

后果：

- 使用 `MateEngine.bat` 启动时，`LocalLow\Shinymoon\MateEngineX` **全程不创建**。
- 直接双击 exe 时，空目录在退出后 ~1 秒被后台进程删除。
- 崩溃或被任务管理器终止时，空目录残留到下次启动（由启动清理处理）。
- Editor 模式不受影响（`#if !UNITY_EDITOR` 守卫）。
- 如果未来 Unity 版本支持 boot.config 的 `cache-path` 键，已注入的行可直接生效。


## ADR-0005: 项目内自定义加载场景替代 Unity Splash

状态：Accepted

背景：

构建产物每次启动时，Unity 内置 Splash Screen 都会显示异常的破碎背景、黑色块和横向条纹。项目引用的 Splash 背景图 `DevICONBig.png` 本身正常，异常更像 Unity 启动早期 Splash 渲染/采样问题，而不是主场景渲染问题。

决策：

- 关闭 Unity 内置 Splash Screen（`m_ShowUnitySplashScreen = 0`）。
- 新增 `Assets/MATE ENGINE - Scenes/Mate Engine Loading.unity` 作为 Build 第一个场景。
- 新增 `LoadingScreenController`，在加载场景内运行时创建 Canvas、背景图、Logo、中文“加载中...”文本和进度条。
- 加载场景不依赖 Localization 或 Addressables，直接用序列化资源引用，避免首屏引入额外初始化路径。
- `BuildScript.BuildWindows()` 和 `EditorBuildSettings.asset` 都按 `Loading -> Main` 顺序列出场景。

原因：

- 项目内加载页由普通 Unity 场景渲染，避开内置 Splash 的启动早期渲染异常。
- 运行时创建 UI 可以减少手写 Unity 场景 YAML 的复杂度，同时仍保持首屏视觉完全可控。
- 保留现有 Splash 图片资源，后续如需换品牌图只需改加载场景脚本引用或 UI 生成逻辑。

后果：

- Build 启动流程变为 `Mate Engine Loading.unity` 异步加载 `Mate Engine Main.unity`。
- 如果新增或改名主场景，必须同步更新 `BuildScript.cs`、`EditorBuildSettings.asset` 和 `LoadingScreenController.mainScenePath`。
- Unity Splash 设置保留旧 logo/background 引用但不再显示。


## ADR-0006: Git 换行与二进制属性策略

状态：Accepted

背景：

在 Windows 上打开 Unity 编辑器或执行命令行构建后，Git 会把部分 Unity 文本资产标记为 modified，但 `git diff` 没有内容差异。根因是本机 Git `core.autocrlf=true` 与 Unity 资源重写、文件状态刷新共同作用，容易产生换行相关的假脏状态。

决策：

- 新增仓库级 `.gitattributes`。
- 将源码、文档、JSON/XML/TXT 以及 Unity 文本序列化资产（`.meta`、`.unity`、`.prefab`、`.asset`、`.mat` 等）固定为 `text eol=lf`。
- 将图片、音频、模型、插件 DLL、压缩包和生成二进制显式标记为 `binary`。
- 对 vendored lilToon shader 源码目录增加路径级例外：`Assets/MATE ENGINE - Shaders/jp.lilxyzw.liltoon-1.8.5/Shader/*.shader text eol=crlf`。这些第三方 shader 在 Windows/Unity 环境中会以 CRLF 工作树形式反复出现，例外规则保持仓库 blob 规范化，同时避免 line-ending-only 假脏。
- `.gitignore` 不再忽略 `.gitattributes`，确保换行策略可随仓库同步。

原因：

- 项目主要资产是 Unity YAML 文本序列化文件，统一 LF 可以减少跨平台和命令行构建后的假修改。
- 二进制资源不能参与换行转换，否则可能损坏资源。
- 规则提交到仓库后，新环境不依赖开发者个人 Git 配置。

后果：

- 后续新增 Unity 文本资产默认按 LF 入库。
- lilToon vendored shader 在 Windows 工作树中允许 CRLF；修改这些 shader 时仍由 Git 规范化入库，不应把整包换行改写混入功能提交。
- 已经存在的历史文件不会自动重写；如需完全规范化，必须单独执行一次受控的 renormalize 提交。
- 开发者仍应在打开 Unity 或构建后检查 `git status`，只提交任务相关文件。


## ADR-0007: 设置菜单滚动区域运行时高度适配

状态：Accepted

背景：

设置菜单滚动区 Content 下仅有一个 RectTransform 子元素 MenuPanel（60px）。其下 **"Main Menu" 是普通 Transform（`!u!4`，非 RectTransform）**——所有设置条目（= GENERAL、= AUDIO、= AI、= DEBUG 等）均作为 "Main Menu" 下的绝对定位 RectTransform 子元素排列。

Unity 的 VerticalLayoutGroup（VLG）和 ContentSizeFitter（CSF）**完全不识别**普通 Transform 及其子元素。开发者使用巨大的 VLG `m_Bottom`（Main: 4600, Update: 5000）人为撑高 Content，使绝对定位条目有足够的显示空间。

移除 3 块推广 section（= STEAM DLC、= MINECRAFT、= FOOD SYSTEM）后，运行时 VLG 首选高度（Top + MenuPanel + Bottom = 340 + 60 + 4600 = 5000）比实际内容底部（~3913）多出约 1000px 的可滚动空白。

决策：

- 新增 `SettingsMenuScrollBoundsLimiter`，挂载于 `SettingsMenuCanvas`。
- 场景保持 VLG Bottom 原始值（4600/5000）和 CSF PreferredSize（`m_VerticalFit: 2`）不变。
- `Apply()` 在 OnEnable（及下一帧复核）时：
  1. `Canvas.ForceUpdateCanvases()` 确保布局最新
  2. 通过 `bottomElement.GetWorldCorners()` 获取最底部元素（Delete AI History）的世界坐标
  3. 转换为 Content 局部空间，得到 Content 所需高度 `height = -minY + bottomPadding`
  4. 计算新 VLG Bottom：`newBottom = height - padding.top - sum(active RectTransform children heights)`
  5. 设置 `vlg.padding.bottom = newBottom`
  6. 再次 `Canvas.ForceUpdateCanvases()` → CSF PreferredSize 自然将 Content 设为正确高度
  7. 设置 `scrollRect.movementType = Clamped`、`verticalNormalizedPosition = 1f`
- Content 的 `m_SizeDelta.y` 和 ScrollRect 的 `m_MovementType` 场景值被运行时覆盖，不作为配置依据。

原因：

- 调整 VLG padding 走标准布局管线，CSF 自然适配——不与布局系统对抗。
- 之前尝试的 3 种错误方案及其教训记录在 `PROGRESS.md` 的"错误提醒"节：
  1. VLG Bottom=0 + CSF PreferredSize → Content 被压缩到 ~400px
  2. VLG Bottom=0 + CSF Unconstrained → VLG 拉伸 MenuPanel 致布局偏移
  3. SetSizeWithCurrentAnchors + 禁用 CSF → 锚点/pivot 偏移致顶部截断

后果：

- 广告区移除后滚动区自动适配，无多余空白。
- Limiter 的 `bottomElement` 引用指向 Delete AI History（当前最底部元素）。

### 在滚动区域添加新内容的操作指南

**情况 A：新内容加在 `bottomElement` 上方**（在现有 section 之间插入）

1. 在场景中将新 GameObject 作为 "Main Menu"（普通 Transform）的子元素
2. 设置 AnchoredPosition.y 使其位于正确位置
3. 将下方所有 section 的 AnchoredPosition.y 向下推移以腾出空间
4. **Limiter 无需任何修改**——`bottomElement` 仍是 Delete AI History，其世界坐标随上方推移自动下移，Limiter 自动重新计算 Content 高度

**情况 B：新内容加在 `bottomElement` 下方**（新内容成为新的最底部）

1. 同上添加新 GameObject
2. **更新 Limiter 的 `bottomElement` 引用**，指向新的最底部元素的 RectTransform
3. Limiter 代码无需改动

**情况 C：在 Content 下直接添加新的 RectTransform 子元素**（VLG 可见的孩子）

1. Limiter 的 `activeChildHeight` 计算会自动包含新孩子
2. VLG Bottom 计算自动调整
3. CSF 自动适配
4. **无需任何 Limiter 修改**

### 相关文件

- `Assets/MATE ENGINE - Scripts/Settings/SettingsMenuScrollBoundsLimiter.cs`
- `Assets/MATE ENGINE - Scenes/Mate Engine Main.unity`（Content、VLG、CSF、ScrollRect 组件）
- `Assets/MATE ENGINE - Scenes/Mate Engine Update.unity`（同上结构，但 Update 场景未挂载 Limiter）


## ADR-0008: PMX 离线导入管线与渲染路线取舍

状态：Accepted

背景：

- 目标是把 PMX 模型引入桌宠，同时尽量保留 MMD 骨骼、物理、日文形态键和 NPR 外观。用户接受离线预处理，模型数量少，因此不需要运行时 PMX 加载器。
- PMX→VRM 曾导致衣物物理、关节动作和口型丢失；把 `.blend` EEVEE 预设直接烘焙成贴图也不可行。项目实际运行在 Built-in 渲染管线，工程内已有 UTS2、Poiyomi、lilToon 和 DynamicBone。
- `.me` 加载链已能复用 AvatarHandler、舞蹈、交互和 Mod 机制。只要产出 Humanoid prefab 并保留 MMD 形态键，桌宠运行时逻辑无需另起一套。

决策：

- 采用 Editor-only 离线自动化管线：解析 PMX，生成网格、骨架、BlendShape、Humanoid Avatar、材质和物理组件，再打包为 `.me`。PMX 解析器放在 `Assets/Editor/PmxPipeline/`，不引入交互式导入工具作为生产依赖。
- 保留原始日文形态键作为 Unity BlendShape。舞蹈口型依赖 `AvatarDanceShapeConverter` 的 MMD 形态键直驱路径，这也是 PMX→VRM 口型丢失的主要修复点。
- PMX 刚体/关节近似转换为 DynamicBone 链，并按 Humanoid 腿部/髋部骨生成裙摆专用 DynamicBoneCollider。该物理是可用近似，不追求与 MMD 求解器完全一致。
- `.blend` 渲染预设只作为材质参数来源：Blender background 导出 JSON，Unity 侧由 `PmxRenderPreset` 与 `PmxMaterialMapper` 映射到 UTS2/lilToon。UTS2 是当前 clean-baseline，不再声明为高保真 HoYo 多通道贴图语义复刻方案。
- `PmxStyleConfig` 保留为逐模型风格调参层。未来新增 shader profile 必须先在本项目 Built-in + Unity 6000.2.6f2 环境实测通过。
- 所有外部路径通过 `PmxPipelineOptions` 注入。优先级为 CLI 参数、本机 ignored 配置、项目相对默认值，禁止把开发机绝对路径写入业务代码。

已否决路线：

- 通用 VRM + App 内置 RenderStyle 曾作为替代路线实现到 N0-N3。它试图用 VRM 1.0、VRM10 SpringBone、HoyoToon 和运行时材质替换来解耦模型与渲染风格。
- 该路线已归档，不再作为生产路径。根因和保留改进见 ADR-0009。

后果：

- 当前 PMX 生产路径是 Editor 离线导入 → UTS2/lilToon + DynamicBone → `.me`。VRM 加载仍是项目原有能力，但 PMX→VRM 导出不再使用。
- 物理、碰撞体和材质映射都是有损近似，需要逐模型验收。M4 踩坑表保留在 `PROGRESS.md`，用于避免重复踩坑。
- `.me` AssetBundle 打包必须复核脚本排除、shader 变体和依赖收集。导出文件名、输出路径和临时目录都必须走 `PmxPipelineOptions`。

相关文件：

- `Assets/Editor/PmxPipeline/`：PMX 解析、Humanoid、材质、物理、`.me` 打包和 batch 入口。
- `Tools/PmxPipeline/dump_render_preset.py`：Blender background 渲染预设导出。
- `Tools/PmxPipeline/styles/`：逐模型风格配置。
- `Assets/MATE ENGINE - Scripts/AvatarHandlers/PmxModelRenderProfile.cs`：PMX prefab 运行时后处理配置。
- `Assets/MATE ENGINE - Scripts/AvatarHandlers/AvatarDanceShapeConverter.cs`：MMD 形态键口型直驱依据。

## ADR-0009: VRM+RenderStyle 弯路复盘：.me 回归为主路径

**状态**：Accepted　｜　**日期**：2026-06-21

### 背景

ADR-0008 记录过一个备选方案：通用 VRM + App 内置可切换 RenderStyle。该方案旨在用 vendored HoyoToon shader 在运行时逼近 HI3 游戏观感，同时保持 VRM 的通用性。它从 N0 到 N3 全部实现并通过编译/绑定验证，但在真实模型（丽塔 PMX→VRM）上实测失败。

### 决策

**放弃 VRM+RenderStyle 路径，回归 .me（UTS2 + DynamicBone）作为唯一生产路径。** 所有 VRM 相关代码归档至 `archive/vrm-detour` 分支，文档标注为已归档。

### 根因

1. **HoyoToon shader 渲染异常**：`HoyoToon/Honkai Impact/Character Part 1` 在本项目 Built-in RP + Unity 6000.2.6f2 环境下渲染结果"惨白/超亮"。MToon 的 `_ShadeColor`（0.30→0.55→0.90）、`_GiEqualization`（0→0.10→0.40）、HoyoToon 的 `_EnvColor`（white→0.45 gray）**全部调参均无肉眼可见变化**，说明 shader 内部渲染模型与项目光照环境存在根本性不兼容。

2. **VRM SpringBone 物理模型不同**：DynamicBone（.me）使用粒子模拟（含惯性 `m_Inert`、弹性 `m_Elasticity`），VRM SpringBone 使用关节模拟（仅 stiffness + drag + gravity）。相同参数值在两种引擎下产生不同行为。将 DynamicBone 参数（Stiffness=0.30/0.55, Gravity=0.001/0.0015）原封搬运到 VRM SpringBone 后物理仍然异常（后摆翘起 90°、裙摆穿模）。

3. **反馈循环过长**：每轮尝试需要"改代码 → 5 分钟 batch 导出 → 用户 reload 验证"，无法快速迭代。

### 保留的改进

从弯路中提取并保留到 .me 路径的改进：
- `PmxMaterialMapper` 的 clean-baseline 修复（matcap 禁用、rim 范围限定、render queue 排序、退化颜色守卫）
- `PmxPhysics` 的 DynamicBone 参数调优
- `PmxPipelineOptions.HasExtension()` 扩展名守卫（防止 .vrm exportPath 劫持 .me 输出）
- UTS2 踩坑表（已移至 `PROGRESS.md` M4 节）

### 教训

1. **shader 兼容性必须在选型前实测验证**，不能仅凭 README 声明。"HoyoToon 设计为 Built-in Pipeline"不等于"在任意 Built-in 项目中正确渲染"。
2. **物理引擎差异是架构性障碍**，不能用参数调优弥补。选型前应做 A/B 对比验证。
3. **视觉迭代需要短反馈循环**。batch 模式导出无法替代 Editor Play 模式的即时预览。

### 影响

- .me 管线确认为唯一生产路径。VRM 加载功能保留（项目原生能力），但 PMX→VRM 导出不再使用。
- `archive/vrm-detour` 分支保留完整 VRM 实现，如需未来重新评估可从中恢复。
- VRM 相关文档已删除（内容全量在 `archive/vrm-detour` 分支）。


## ADR-0010: 运行时可切换渲染风格（RenderStyleManager）——alpha-preserving image effect（全屏后处理与透明窗口不兼容）

状态：Accepted

### 背景

ADR-0009 回退了"通用 VRM + 可切换 RenderStyle"弯路，但弯路中"**运行时可切换渲染风格**"这一架构本身是健全的——失败的是它捆绑的 VRM 转换与 HoyoToon shader。用户需求是在不重生成 `.me` 的前提下，于设置菜单中手动切换渲染风格，并以"逼近 HoYo 原效果"为终点。

调研推翻了两个先前的错误前提：

1. **后处理早已接通**。主场景 Main Camera（fileID 330585543）的第 4 个组件就是 `PostProcessLayer`（guid `948f4100...`，`volumeLayer=0xFFFFFFFF` 监听所有层）。先前用字面量 grep 未命中而误判"后处理是断的"。真正的问题在 `PmxModelRenderProfile`：它 `FindObjectsOfTypeAll<PostProcessVolume>()` **劫持场景里默认关闭的 Bloom/AO Volume** 去改写，导致 grade 不可靠。

2. **HoYo 着色器路线短期无解**（见 ADR-0009）：HoyoToon 在本项目 Built-in RP + Unity 6000.2.6f2 下"惨白/超亮"且不可调。因此着色器作为单独、先小场景实测的 R&D，本期不承诺。

### 决策

剥离复用弯路里健全的"可切换风格"架构（去掉 VRM 转换与 HoyoToon），落地为运行时叠加体系，**不需要重生成 `.me`**。新增 `RenderStyleManager`（场景持久单例，`RuntimeInitializeOnLoadMethod` 自举，无需改场景即存在），内置风格表 `默认/鲜艳/柔和`，`SetStyle(code)` 实时切换、随 `SettingsData.renderStyle` 持久化。

> ⚠️ **首版（全局后处理）已被透明窗口证伪——见下方"修正"。** 现行方案为**纯逐材质**。

设置 UI：`Settings/RenderStyleDropdownHandler.cs`（沿用 `LanguageDropdownHandler` 模式，绑 `SettingsData.renderStyle`，onValueChanged → `RenderStyleManager.SetStyle`）。场景内下拉由编辑器命令 `RenderStyleSceneSetup`（菜单 `MateEngine ▸ Render Style ▸ …`，亦可 `-executeMethod`）**克隆现有 Graphics（画质）下拉**生成——让 Unity 正确处理 fileID/序列化，幂等且可经 git 回退。标题文本设为静态 `渲染风格`（禁用其 `LocalizeStringEvent`，因为所有设置标题走本地化 key 而非字面量）。

`PmxModelRenderProfile` 的后处理职责退役：`PmxMeshBuilder.AddRenderProfile` 不再挂该组件；`Apply` 在 `RenderStyleManager` 存在时（运行时恒成立）no-op。**序列化字段保留**，兼容旧 `.me`。

### 修正：全局后处理与透明窗口根本不兼容（2026-06-21 实测）

首版让 `RenderStyleManager` 拥有专属全局 `PostProcessVolume`（Bloom + ColorGrading，经相机既有 `PostProcessLayer` 生效）。用户实测：切到 `鲜艳/柔和` 后画面**比默认更灰**，且**屏幕出现一大块半透明灰色矩形**（软件窗口本体）。

根因：MateEngine 渲染进**逐像素 alpha 的分层窗口**（`WS_EX_LAYERED`，见 `WinApi.cs`/`AvatarWindowHandler.cs`），相机 `clear=(0,0,0,0)`，由 OS 按 alpha 合成。Unity Built-in 全屏后处理（Bloom/ColorGrading via PostProcessLayer）**不保留 alpha**，把透明区域填成不透明灰 → 桌宠透明/穿透被破坏。佐证：项目自带的 `bloom` 设置默认关闭（`SettingsHandlerToggles` 只 `SetActive` 场景 Bloom Volume），说明上游早已知后处理在此不可用。

→ 这与 ADR-0009 的 HoyoToon 同类：**某个渲染手段在本项目特定环境下根本不可用，必须实测才能发现**。先前"后处理是主视觉杠杆"的前提被推翻。

**因此后处理彻底移除**，风格改为**纯逐材质（UTS2）覆盖**：运行时遍历 `Renderer`，按风格乘 `_Unlit_Intensity`(亮度)、`_1st/_2nd_ShadeColor`(阴影深浅→对比)、`_HighColor`(高光)，缓存原值可逆还原，非 UTS2 材质优雅跳过。只动模型自身像素、绝不碰帧缓冲/背景 → **透明与穿透始终保留**。代价：UTS2 无真正"饱和度"旋钮，材质层只能调亮度/对比，不能做整屏饱和提升。

### 字体方框乱码（tofu）修复

设置/Blendshape 菜单的中文走静态 SDF 回退字体 `Asia Fonts/Chinese (Simple)`（`m_AtlasPopulationMode=0`，仅预烤 `characterSequence` 内字形；`默认` 在表内、`鲜艳柔和渲染风格` 不在 → 方框）。该资产 `m_SourceFontFile` 为空（避免打包 ~10MB TTF），故**运行时切 Dynamic 不可行**。修复：编辑器命令 `RenderStyleSceneSetup.BakeFontGlyphs` 临时把该资产切 Dynamic 并挂回 `NotoSansSC-Medium.ttf`、`TryAddCharacters` 烤入所需字形、再切回 Static 并清空 source ref（不打包 TTF）。烤入 `默认鲜艳柔和渲染风格` + Blendshape 中文译名全部字形（776→819 字，0 缺失）。

### Blendshape 菜单日文→简中

`BlendshapeManager` 原样显示 mesh 的 morph 名（PMX 模型为日文，如 まばたき/笑い）。新增 `BlendshapeNameLocalizer`（运行时静态 JP→CN 字典，覆盖标准 MMD morph 集，未命中原样返回），`BlendshapeManager` 显示名走 `Localize()`。字典的中文字形由上面的字体烤入覆盖（二者共享字符集，`AllLocalizedChars()` 供烤入端取字）。

### 原因

- 纯逐材质只动模型像素，是唯一能保证透明窗口不被破坏的整体调色途径；全屏后处理与分层窗口互斥（实测）。
- 克隆已知良好的下拉节点 → 避免手写 TMP_Dropdown YAML 子树的高风险；设置滚动区布局约束见 ADR-0007（本条目加在 `bottomElement` 之上、`= GENERAL` 内，属"情况 A"，Limiter 自动重算）。
- 字体走"临时 Dynamic 烤入再转回 Static"→ 既补齐字形又不给便携包增加 TTF 体积。

### 后果

- 用户可在设置中实时切 `默认/鲜艳/柔和`，可逆、`renderStyle` 重启记忆，**不破坏透明/穿透**。
- 材质层调亮度/对比，**不能做整屏饱和**；要更强"鲜艳/逼近 HoYo"需 alpha-preserving 自定义 image effect（保留源 alpha 的饱和/对比 blit）或着色器 R&D —— 两者都须实测验证，留作下一步。
- HoYo 着色器仍 deferred（ADR-0009）。
- 新增/改动文件：`RenderStyleManager.cs`(纯材质)、`RenderStyleDropdownHandler.cs`、`RenderStyleSceneSetup.cs`(Editor：加下拉/改标题/烤字体)、`BlendshapeNameLocalizer.cs`、`BlendshapeManager.cs`、`SaveLoadHandler.cs`(+`renderStyle`)、`VRMLoader.cs`(钩子)、`PmxModelRenderProfile.cs`/`PmxMeshBuilder.cs`(退役/停挂)、`Asia Fonts/Chinese (Simple).asset`(烤字形)、主场景。

### 待办 / 验证

- 运行 App：加载 `丽塔_m4.me`，切 `默认/鲜艳/柔和` → 确认实时、可逆、重启记忆，且**透明/穿透完好**（无灰色矩形）；确认 `鲜艳/柔和` 不再比默认更灰。
- 确认下拉标题显示 `渲染风格`、选项 `默认/鲜艳/柔和` 与 Blendshape 中文名**不再是方框**。
- **视觉复核**下拉在 `= GENERAL` 组的位置/间距。

### 二次反馈修复（2026-06-21）

App 实测反馈 4 项，逐一定位：

1. **逐材质风格"切了没变化"——根因坐实**：`PmxMaterialMapper` 烤进的 UTS2 材质设了 `_Use_BaseAs1st=1` + `_Use_1stAs2nd=1`（让 UTS2 用 base 色推导 1st/2nd 阴影），所以运行时改 `_1st/_2nd_ShadeColor` **完全被忽略**；`_HighColor_Power≈0` 使高光乘子也无效；只剩 `_Unlit_Intensity` 微调 → 肉眼无差。**结论：材质层（在现有 UTS2 烤法下）几乎无法表达"鲜艳"**，UTS2 也没有真正的饱和度旋钮。

2. **设置布局**：`= GENERAL` 是 2 列绝对定位网格（左 x≈-105、右 x≈220），各 section 在 Main Menu 空间按**全单位**定位（GENERAL y=280、`= AUDIO` y=-60），GENERAL 的子项也用全单位（非 0.2327 内容缩放）。所以在底部加新行（y<-340）会撞进 AUDIO。修复：把下拉停进 Discord RPC 行的**空闲右列** (219.85, -260)，不加新行、不挪 section、不撞 AUDIO。

3. **字体方框**：见上节字体烤入。补充：原子集 2048×1024 静态 atlas 接近满（~819 字），一次性烤完整 kana 区间会溢出（`TryAddCharacters` 返回 ok=False + 大量 missing）。修复：① 只烤**精确所需**字形（59 个 morph 原名 + 译名 + 风格串，不烤整段 kana 区间）；② 临时开 `m_IsMultiAtlasTexturesEnabled` 让溢出落到第 2 张 atlas（UGUI 运行时按 atlas 分 submesh 渲染）。最终 776→874 字、0 缺失。

4. **Blendshape 日文→简中**：用 `PmxBlendshapeDump`（`-executeMethod PmxBlendshapeDump.DumpRita`）从 `.me` dump 出 59 个**真实 morph 名**作为字典 key（精确覆盖 `困る2左`/`ウィンク２`/半角 `ｳｨﾝｸ２右` 等变体），显示格式改为 **`中文 / 原文`**（用户要求，便于核对）。

### 鲜艳度改进路线分析（回答用户"分析改进路线"）

终点 B"逼近 HoYo 鲜艳"在本项目两大硬约束下（① 透明分层窗口禁用全屏后处理；② 现有 UTS2 烤法材质层调不动饱和）可选路线：

- **路线 A：alpha-preserving 自定义 image effect（推荐，首选）**。相机 `OnRenderImage` 做饱和/对比/亮度，输出保留源 alpha（`float4(graded.rgb, src.a)`）→ 真正的整屏调色且**不破坏透明**。可作用于现有 `.me`、无需重烤、能让 `鲜艳` 立刻肉眼可见。风险低（关键就是保留 alpha），须截图验证透明。
- **路线 B：换 lilToon / Poiyomi 重烤 `.me`**（项目已 vendored 两者）。它们有逐材质 HSV/饱和度（lilToon `_MainTexHSVG`），可运行时调饱和且天然不碰帧缓冲。代价：离线管线改造（PMX→lilToon/Poiyomi 而非 UTS2）+ 重导出 + 重调，工作量大。
- **路线 C：修/重调 UTS2 烤法**（`_Use_BaseAs1st=0` 启用真实阴影色 + ramp + rim）。能加对比/层次（治"偏暗/平"），但仍**无法饱和**（治不了"偏素"），且 UTS2 路线已多轮迭代。
- **路线 D：HoYo 专用 NPR 着色器**。已证伪（HoyoToon，ADR-0009），长期 R&D。
- **路线 E：离线预饱和贴图**。粗暴、不可逆，不推荐。

建议：**先上路线 A**（快、整屏真饱和、修复"切风格无变化"、透明安全），其后视效果决定是否再上 B（逐材质艺术可控）。A 的风格分级即可承载 `默认/鲜艳/柔和`。

### 实现：路线 A（已落地，2026-06-21）

用户选"先 A、效果好再考虑 B"。落地：

- 新增 shader `Assets/MATE ENGINE - Shaders/Resources/RenderStyleGrade.shader`（`Hidden/RenderStyleGrade`，放 Resources 保证进包供运行时 `Resources.Load`）：全屏 blit 做 饱和/对比/亮度，**关键是保留源 alpha 且不动全透明像素**（`mask=step(0.0001,src.a)`，`return float4(lerp(src.rgb,graded,mask), src.a)`）——这正是 Built-in PostProcessLayer 做不到、会把透明涂灰的地方。
- 新增 `RenderStyleImageEffect`（相机 `OnRenderImage`；inactive 时纯 passthrough，故 `默认` 与无效果逐字节一致）。
- `RenderStyleManager` 改为驱动该 image effect（移除无效的逐材质叠加）：风格 = `saturation/contrast/brightness`。`默认`(passthrough)、`鲜艳`(sat1.45/con1.10/bri1.04)、`柔和`(sat1.15/con0.96/bri1.03)。运行时找 `Camera.main` 挂 effect + 建材质。
- 相机本就有 `PostProcessLayer`（无激活效果时 passthrough、不破坏透明），本 effect 追加其后、独立运行。
- **待验证**：透明/穿透在 `鲜艳/柔和` 下完好（image effect 保留 alpha），且 `鲜艳` 明显更艳。若 PostProcessLayer 与本 effect 的 OnRenderImage 顺序有冲突，再调整。

### 三次反馈：路线 A 全屏 grade 会染到菜单（根因 + 暂停）

App 实测：① 切风格模型有明显变化，但**设置菜单颜色也跟着变**；② `鲜艳` 像单纯拉高对比、暗部直接黑掉看不见。

根因（架构级，确认无误）：

- **全场景只有 1 个相机**（`m_ClearFlags=2` SolidColor、bg alpha=0 透明）。它既渲染模型又渲染 **Screen-Space-Camera 的菜单 Canvas**（场景里 `m_RenderMode:1` 的若干 Canvas）。`OnRenderImage` 作用于该相机的最终图像 → 模型 + 相机空间 UI 一起被 grade。Overlay（`m_RenderMode:0`）的 Canvas 在 image effect 之后合成、不受影响，但部分菜单是 Camera 模式（可能为了 blur）→ 被染色。
- **模型在 layer 10**（专用层，`Model` 物体），但运行时加载的 `.me` 在 `customModelOutput` 下、`VRMLoader` **未重设层** → 不保证在 layer 10。
- `鲜艳` 用了 contrast>1（绕 0.5 轴）→ 暗部被压成纯黑（已改 contrast=1，靠 saturation+轻 brightness）。

→ 全屏 grade 要做到"只影响模型"，需 **模型专用相机**（layer 10 grade 相机 + 主相机排除该层 + 改 clearFlags + 加载时把 `.me` 重设到 layer 10 + 双相机透明合成）——属相机架构手术，且在透明窗口上风险高、无头不可验证。**因此本期把全屏 grade 暂停**（`RenderStyleManager.GradeEnabled=false`，effect 常 inactive，菜单不再被染），等用户定方向再实现。下拉与持久化保留。

**模型专属（不碰 UI）的鲜艳途径**（待用户选）：① 模型专用 grade 相机（保留路线 A 的整屏真饱和但只作用模型，相机手术）；② 路线 B：lilToon/Poiyomi 重烤（逐材质饱和，天然只动模型）；③ `氛围光`（见下）只能提亮/染色、做不了贴图饱和。

### 设置布局（第三次，确认正确做法）

`= GENERAL` 等 section 是 Main Menu（普通 Transform）下的**绝对定位 RectTransform 兄弟**，不会自动重排。正确做法（ADR-0007 情况A）：把下拉放在 GENERAL **末尾新行**（左列 (-104.9,-360)，比 Discord 低一个行距 100），并把 GENERAL **之后的所有 section 整体下移**（按 sibling index > GENERAL 过滤，下移 120）；滚动区由 `SettingsMenuScrollBoundsLimiter` 依据下移后的 bottomElement **自动加长**，无需改 Limiter。前两次错误：放进 GENERAL 内部空位会撞 FPS/AUDIO（section 不会让位）。

### 关于"氛围光"（回答用户）

`氛围光` = `ColorController` 驱动的 **3 盏场景 Light**（`SettingsHandlerLights` → 每盏 intensity/saturation/hue；`ApplyFadeToTarget` 里 `light.color=HSVToRGB(...)`、`light.intensity=...`）。它**只照亮模型等被光照几何、不影响 UI**（UI 不受光照），所以是**天然"只影响模型"的杠杆**：调高强度可直接治"偏暗"，调 saturation/hue 给模型染色。但它改的是**光照**，**不能提升贴图本身的饱和度**（治不了"偏素"的根本）。可作为即时可用的提亮手段，与路线 A/B 互补。

### 决策：放弃"模型专用 grade 相机"（2026-06-22，仍有效）

用户指出与官方效果的差距**不止饱和度**：头发/配饰的**高光**靠改亮度/对比/饱和度做不出来。坐实——高光是**视线+法线+光照相关的镜面反射**，属 3D 着色，**任何屏幕空间 grade（含模型专用 grade 相机）都不可能产生**。grade 相机只能补"色调"那一半，却要付相机手术代价，**因此彻底放弃**（`RenderStyleManager.GradeEnabled` 维持 false）。

### ❌ 失败路线（务必勿重试）：UTS2 烤法参数微调追 HoYo NPR（2026-06-22，已全部回退）

**试了两轮，越改越差，已 `git checkout` 全部回退到基线烤法、重烤 `丽塔_m4.me` 作兜底。**

- **第一轮 B1（材质恢复高光）**：抽 `HighlightIntent` + `ApplyHighlightUTS2`，对 hair/cloth/metal 用 `*_LightMap`/`MetalMap` 遮罩的近白 UTS2 镜面高光（`_Is_SpecularToHighColor`）。`highColor` 统计从 ~0 升到 14。
  - **结果：更糟。** 腿（裤袜）变成"**附着油脂的圆柱体**"——UTS2 的 Blinn-Phong 是**柔和扩散**的，根本不是官方那种**硬边高光色块**；叠在本就偏软的卡通上 = 塑料/油腻感。
- **第二轮 硬卡通 pass**：feather 0.17–0.30 → **0.02**（硬切阴影）、阴影色加深、高光改 MetalMap-only、`outlineScale`→2.0、`brightness`→1.0、皮肤 `_Set_SystemShadowsToBase=1`。
  - **结果：差得更远。** 整模型**惨白到看不清**、塑料感未消、**头发出现破碎纹理**。盲烤把多个耦合参数一起推过头，直接崩。

**为什么这条路走不通（根因，写死别再撞）：**
1. **目标是贴图/LightMap 驱动的**。HoYo 官方靠 LightMap 各通道决定 阴影 ramp / AO / 高光强度+锐度，脸用 **SDF**，腿袜/布料那条**锐利高光是各向异性**。**UTS2 不吃这些** → 只能盲目逼近，参数一硬就溢出成惨白/碎面。
2. **离线烤 = 无实时预览的盲调循环**。每轮 = Blender dump + bundle 重烤（数分钟）且看不到结果，根本无法收敛；两轮单调变差即证。
3. UTS2 的 `_Is_SpecularToHighColor` 镜面瓣**先天柔和**，做不出参考里的硬边高光块。

**结论**：**UTS2 参数微调路线作废**，不要再在 `PmxMaterialMapper` 里调 feather/shade/highColor/outline 试图追 HoYo。基线烤法（matcap 关、highColor 仅在 preset 给有效色时开、软 feather）保留为**兜底渲染**——它"偏素但不崩"。

**下一步（本期不做，单列）**：上 **lilToon**（能吃 LightMap、有各向异性发丝/布料高光、有 HSV 饱和），但**必先按 ADR-0009 教训**——在本工程 Built-in + 6000.2.6f2 用**小场景实测** lilToon 渲染正常（HoyoToon 当年就是在这步惨白翻车），确认后再改管线，**不再盲改盲烤**。`PmxStyleConfig.materialProfile`（注释已写 "reserved for a future profile"）是接入点。
