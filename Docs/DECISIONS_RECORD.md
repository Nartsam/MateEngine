# Decision Records

记录影响长期维护的架构决策。普通 Bug 修复、UI 调整和排查过程不记入。

## 索引

| ID | 日期 | 决策 | 状态 |
|---|---|---|---|
| ADR-0001 | 2026-06-18 | Steam 移除与绿色便携化基础 | Accepted |
| ADR-0002 | 2026-06-18 | 自定义构建系统 | Accepted |
| ADR-0003 | 2026-06-19 | 嵌入并 patch Addressables | Accepted |
| ADR-0004 | 2026-06-19 | Unity 原生缓存重定向 | Superseded by ADR-0014 |
| ADR-0005 | 2026-06-19 | 项目内自定义加载场景替代 Unity Splash | Accepted |
| ADR-0006 | 2026-06-19 | Git 换行与二进制属性策略 | Accepted |
| ADR-0007 | 2026-06-20 | 设置菜单滚动区域运行时高度适配 | Accepted |
| ADR-0008 | 2026-06-20 | PMX 离线导入管线与渲染路线取舍 | Accepted |
| ADR-0009 | 2026-06-21 | VRM+RenderStyle 弯路复盘：.me 回归为主路径 | Accepted |
| ADR-0010 | 2026-06-21 | 运行时可切换渲染风格；全屏后处理与透明窗口不兼容 | Accepted |
| ADR-0011 | 2026-09-10 | 复刻 Blender 渲染预设改走节点图转译，放弃参数拟合 | Accepted |
| ADR-0012 | 2026-09-22 | 渲染复刻管线是离线工具；bloom 需要改 App 渲染管线 | Accepted |
| ADR-0013 | 2026-09-22 | 独立命令行模型导出工具，先交付无 bloom 的 `.me` | Accepted |
| ADR-0014 | 2026-09-22 | LocalLow 原生写入调查与启动前路径重定向 | Accepted |
| ADR-0015 | 2026-09-23 | 主程序仅为转译 PMX 启用 Bloom，保留原 alpha 与现有 UI 相机 | Accepted |
| ADR-0016 | 2026-10-02 | PMX 物理按 PMX 关节限制与静态刚体生成，不再按名字套统一参数 | Superseded by ADR-0017 |
| ADR-0017 | 2026-10-07 | PMX 物理改为把刚体与关节原样交给 Bullet 模拟，放弃 DynamicBone 近似 | Accepted（导出已切换，待 App 内验收） |
| ADR-0018 | 2026-10-11 | 头发阴影转头时断开：导出时按角色平滑头发法线，不让日光随头部转动 | Accepted（待 App 内验收） |


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

`EditorBuildSettings.asset` 场景列表为空，Unity 命令行 `-buildWindows64Player` 无法找到场景。便携缓存处理见 ADR-0014。

决策：

- 新增 `Assets/Editor/BuildScript.cs`，使用 `BuildPipeline.BuildPlayer` 显式指定场景和输出。
- 构建前先执行 Addressables content build，防止旧 catalog 混入新 Player。
- 构建成功后生成 `Build/MateEngine.bat`，启动 exe 并转发用户附加参数（`%*`）；直接运行 exe 也受支持。

原因：

- 构建入口不依赖编辑器 Build Settings 场景列表。
- `.bat` 启动器对用户透明，双击即可启动。

后果：

- 修改主场景路径或输出名称时必须同步更新 `BuildScript.cs`。
- `.bat` 仅为便利入口，无需通过启动器保证便携。


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

状态：Superseded by ADR-0014（2026-09-22）

原实现通过 `MateEngine.bat` 传入 `-cache-path`，并在退出时派生 PowerShell 删除空的
LocalLow 目录。2026-09-22 的文件系统事件监测证实：携带该参数仍会在启动、退出时创建
`Shinymoon/MateEngineX`，随后才被清理代码删除。此前“原生参数全程重定向”的结论错误。

匹配当前 Player 的 Unity 原生符号与反汇编显示 `GetCachingManager` 按需初始化；
项目自己的 `ConfigureUnityCache()` 会触发该初始化。替代方案及验收见 ADR-0014。


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
  > **2026-10-02 修正：参数与碰撞体改为直接取自 PMX 关节限制与静态刚体，见 ADR-0016。** 按名字分类套统一参数、按 Humanoid 腿骨猜碰撞体，是刘海过飘、辫子僵直、裙摆根节转动撕开腰部和衣物穿模的共同原因。
- `.blend` 渲染预设只作为材质参数来源：Blender background 导出 JSON，Unity 侧由 `PmxRenderPreset` 与 `PmxMaterialMapper` 映射到 UTS2/lilToon。UTS2 是当前 clean-baseline，不再声明为高保真 HoYo 多通道贴图语义复刻方案。
  > **2026-09-10 修正：上面这条定位是错的，且是后续一系列失败的源头。** `.blend` 预设不是"一组材质参数"，而是一个 712 个计算节点的着色函数。把它降维成扁平参数字典，等于在第一步就丢掉了要复刻的对象。参数来源这一定位只适用于 UTS2 兜底路径；高保真复刻改走节点图转译，见 ADR-0011。
- `PmxStyleConfig` 保留为逐模型风格调参层。未来新增 shader profile 必须先在本项目 Built-in + Unity 6000.2.6f2 环境实测通过。
- 所有外部路径通过 `PmxPipelineOptions` 注入。优先级为 CLI 参数、本机 ignored 配置、项目相对默认值，禁止把开发机绝对路径写入业务代码。

已否决路线：

- 通用 VRM + App 内置 RenderStyle 曾作为替代路线实现到 N0-N3。它试图用 VRM 1.0、VRM10 SpringBone、HoyoToon 和运行时材质替换来解耦模型与渲染风格。
- 该路线已归档，不再作为生产路径。根因和保留改进见 ADR-0009。

后果：

- 当前 PMX 生产路径是 Editor 离线导入 → UTS2/lilToon + DynamicBone → `.me`。VRM 加载仍是项目原有能力，但 PMX→VRM 导出不再使用。
- 物理、碰撞体和材质映射都是有损近似，需要逐模型验收。几何与物理的踩坑记录见 `PROGRESS.md`「PMX 几何与物理踩坑表」；原 M4 表中的 UTS2 材质部分已作废，原表见 `3788b59c^:PROGRESS.md`。
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
- UTS2 踩坑表（曾移至 `PROGRESS.md` M4 节；2026-09-22 该表拆分，UTS2 部分作废，原表见 `3788b59c^:PROGRESS.md`）

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

> **当前状态：全屏调色暂停（`GradeEnabled = false`），下拉与持久化保留。** 下文依次记录全局后处理、逐材质覆盖和自定义 image effect 的历史尝试，暂停原因见「三次反馈」。新转译路线为 bloom 改造相机的决策见 ADR-0012。

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

> **2026-09-10 结果：lilToon 路线也已失败并作废。** 该路线按上述"小场景实测 + 实时预览"的纠正执行，迭代到 Phase 2.18 仍无法收敛，产物已归档到分支 `archive/liltoon-npr-tuning`。
>
> **更深一层的根因（本 ADR 当年没看到的）**：换 lilToon 与换 UTS2 是同一类错误。目标是一张 712 个计算节点的函数，而 lilToon 只暴露二十几个可调项，**参数拟合的维度不足，理论上就无法收敛**——"实时预览"只能让失败来得快一些，不能让它成功。
>
> 另有一条独立死因：当时的 `Tools/PmxPipeline/dump_render_preset.py` **从不导出节点连线**，只按关键词猜测生成扁平字典，算法本身在导出环节就已丢失。见 ADR-0011。

---

## ADR-0011: 复刻 Blender 渲染预设改走节点图转译，放弃参数拟合

状态：Accepted　日期：2026-09-10

### 背景

目标是让「PMX 模型 + 配套 `.blend` 渲染预设」在本软件中呈现与 Blender 中一致的观感。此前三条路线全部失败：PMX→VRM+MToon（ADR-0009）、UTS2 参数微调（ADR-0010）、lilToon 参数调优（迭代到 Phase 2.18，已归档到分支 `archive/liltoon-npr-tuning`）。

2026-09-10 重新分析后确认，这三条路线共享同一个错误前提：**把 `.blend` 预设当成一组材质参数**（ADR-0008 明文如此定位），于是问题被表述为"挑一个通用卡通着色器，调参数逼近它"。

### 根因

两条独立死因，任何一条都足以致命：

1. **导出环节丢失算法。** `Tools/PmxPipeline/dump_render_preset.py` 遍历节点树时从不记录 `node_tree.links`，只按关键词（`"shade"` / `"高光"` / `"outline"`）猜测插槽含义，产出一个 UTS2 形状的扁平字典。节点图的拓扑结构从未被导出过。
2. **拟合维度不足。** 预设展开后是 712 个计算节点，而 UTS2 / lilToon 只暴露二十几个可调项。参数拟合在理论上就无法收敛。

### 决定性发现

**这套预设是无光照纯函数。** 实测：材质把 Color 直接接进 `Material Output ▸ Surface`，该写法与 Emission 输出逐位相同，而 Diffuse BSDF 在无灯时为纯黑；预设中唯一的灯 `energy = 0.0`，World 为空。对四个预设每一棵树做全量 `bl_idname` 枚举，`ShaderToRGB` / `AmbientOcclusion` / `Bevel` / `LightPath` / `LayerWeight` / `Fresnel` / `VertexColor` 均为 0 次。128 个材质中 124 个为纯函数。

因此整个渲染是一个可以被完整搬走的片元函数，不依赖 Blender 的光照引擎、阴影贴图、全局光照或屏幕空间缓冲。

### 决策

- **写一个「节点图 → HLSL」转译器**，逐节点机械翻译，而不是挑现成着色器调参。收敛具有确定性：对不上就是某个节点译错，可逐节点二分定位。
- **写转译器而非手写着色器。** 四个预设虽共享 27 个同名节点组，但按结构去重后实为 **41 份实现**（9 个同名组在不同预设里结构不同）。手写意味着每换一个角色重来。
- **建立自动差分验收闭环**：Blender 无头渲染基准图 vs Unity 渲染，程序化比对像素差异并定位到具体材质。取代"助手调参 / 用户截图判断"的人工循环。
- **描边离线烘焙**：预设的描边是几何节点实体化外壳，其外扩方向是按 `1e-4` 焊接后重算的顶点法线（实测吻合度 92.9%，原网格法线仅 44.9%）。运行时 pass 无法复现该法线，而离线烘焙一步到位。
- 保留 `materialProfile = "uts2"` 作为兜底，不得破坏。

### 修正此前的结论

- **ADR-0008**「`.blend` 渲染预设只作为材质参数来源」——定位错误，已在原处加注。该定位仅适用于 UTS2 兜底路径。
- **ADR-0009**（HoyoToon 因 PMX 缺顶点色失败）——事实正确但**不适用于本路线**。HoyoToon 需要数据挖掘网格的顶点色；本 `.blend` 预设对模型的硬要求只有两条：一套名为 `UVMap` 的 UV，以及与预设一致的材质名。丽塔 PMX 实测全部满足。
- **ADR-0010**（UTS2 死路）——结论正确，已补记更深一层根因。

### 一条方法论教训

`渲染预设.blend` 是**没有场景的纯材质库**，其内部存的 scene 设置是死数据。真正生效的场景效果由配套插件 `模型预设导入.py` 在导入时写入：bloom 开启（强度 0.08、色 `#FFE4D9`）、`view_transform = Filmic`、`look = High Contrast`。

本次分析第一轮读了库里的死数据，得出"bloom 关闭、look=None"的错误结论，直到读插件源码才发现。**判断"什么设置在生效"必须以实际执行路径为准，而不是文件里存了什么。**

### 后果

- 新增 `Tools/PmxPipeline/export_blender_graph.py`（完整节点图导出器，含连线、插槽默认值、组接口、色带与曲线控制点）。原 `dump_render_preset.py` 仅保留供 UTS2 兜底路径使用，不得用于高保真复刻。
- 参考资料落盘：`Docs/RenderPreset/graphs/`（四个预设的完整节点图）、`Docs/RenderPreset/vendor/`（配套导入插件副本）、`Docs/RenderRefs/`（Blender 参考截图）。
- 实施计划见 `Docs/plans/2026-09-10-blender-graph-transpile.md`，其中列有 8 条"不知道就会静默出错"的转译陷阱。
- bloom 与透明分层窗口的冲突已在 ADR-0012 解决：自写了 alpha 感知的合成 pass，并决定为此改造 App 的渲染管线。

### 相关文件

- `Tools/PmxPipeline/export_blender_graph.py`：完整节点图导出器
- `Docs/plans/2026-09-10-blender-graph-transpile.md`：实施计划与已核实事实清单
- `Docs/RenderPreset/`：节点图数据、配套插件副本
- `archive/liltoon-npr-tuning`：lilToon 路线的全部归档产物

## ADR-0012: 渲染复刻管线定位为离线工具；bloom 需要改造 App 渲染管线

状态：Accepted　日期：2026-09-22

### 背景

ADR-0011 决定走节点图转译。转译器、描边烘焙与差分闭环落地后，九个角色正背视角的轮廓 IoU
按四位小数为 0.9980–0.9991、交集内平均 ΔE76 为 1.08–5.51（对照 UTS2 基线 0.9677 / 11.00），用户已肉眼验收通过。
接下来要回答两个集成问题：这套预处理放哪里，以及效果能不能整体烘进离线文件。

### 决策一：预处理是仓库内的离线工具，不内置进 App

这条流程依赖 **Blender 3.6**（求值几何节点得到描边外壳、导出节点图与预设内打包的贴图）
和 **Unity 编辑器**（建网格、出 prefab / AssetBundle）。两者都不可能随程序分发，所以
「选一个 `.pmx` + 对应的渲染预设 `.blend`，转换成可加载格式」在技术上只能是离线工具。
App 只负责加载转换结果。

入口与用法见 `Tools/PmxPipeline/README.md`。

### 决策二：只有 bloom 必须改管线，其余全部可以烘进离线文件

逐项核对的结论：

| 东西 | 能否烘进 `.me` | 理由 |
|---|---|---|
| 逐材质着色（贴图、LUT、色带、法线贴图、matcap、脸部 SDF、程序化噪声） | 能 | 转译产物就是普通 Built-in RP 无光照着色器，不要求管线支持任何特性 |
| 描边 | 能 | 是实体化外壳，网格里多出来的几个子网格而已 |
| Filmic + High Contrast 视图变换 | 能 | 纯逐通道的一维 LUT，可在片元末端逐像素套用 |
| **bloom** | **不能** | 屏幕空间效果，按定义装不进模型文件 |

所以只有 bloom 这一项逼着改管线。它值 ΔE 4.17 → 1.86（"偏暗"与"明亮鲜艳"的差别），
不能不要。

### 决策三：走 A 路线——改造 App 渲染管线，不为了不改管线而降低效果

本路线仍是完整渲染集成目标；2026-09-22 用户将本次工具交付范围限定为无 bloom 导出，
相机改造延后，见 ADR-0013。

需要三处改动：

1. 3D 内容渲进开了 HDR 的离屏线性缓冲。
2. alpha 感知的全屏合成（bloom + 视图变换）——代码已有，即
   `Assets/PmxTranspileRuntime/MEPostCompose.{cs,shader}`。
3. **把 UI 隔离在合成之外**。当前全场景只有一个相机，它同时渲染角色与
   Screen-Space-Camera 的菜单画布，全屏调色会把菜单一起染了——这正是 ADR-0010 里
   `GradeEnabled = false` 记录下的、曾被用户否决的现象。做法是给 3D 内容单开一个相机，
   主相机只画 UI（或把菜单画布改为 Screen-Space-Overlay）。

ADR-0010 的结论没有被推翻：**Built-in 的全屏后处理确实会破坏分层窗口的 alpha**。
`MEPostCompose` 不是 Built-in 后处理栈，它是自写的、显式保留 alpha 的合成 pass。

### 辉光 alpha：本期保留原值，外圈效果后续实机再定

2026-09-23 用户选择本期保留原 alpha，先接入主程序，再按实机效果讨论外圈辉光；见 ADR-0015。

实测 Blender：开 bloom 后轮廓外有 314456 个像素的 RGB 变成非零（最亮到 95/255），但
**alpha 仍然是 0**，与不开 bloom 时一模一样（半透明像素数都是 8828，一个不差）。
Blender 把辉光只写进颜色通道，没碰 alpha。

Blender 的图像编辑器按 RGB 显示、忽略 alpha，所以在 Blender 里看得见那圈辉光；而分层
窗口按 alpha 合成，alpha=0 的像素完全透明、RGB 不参与显示。**照搬 Blender 的话，轮廓外
的辉光在 App 里看不见**。轮廓内部（alpha=1）的辉光正常显示，画面从"偏暗"变"明亮鲜艳"
主要就是这部分。

要让外圈也可见，就得主动把 alpha 跟着辉光外扩（如 `alpha = max(alpha, 辉光亮度 × k)`）。
这是**有意偏离 Blender 的输出**：差分数值会略微变差，但 App 里的观感更接近用户在
Blender 里看到的那张图。等能在 App 里实际看到再定。

### 相关文件

- `Tools/PmxPipeline/README.md`：管线的操作参考
- `Docs/plans/2026-09-10-blender-graph-transpile.md`：Phase 4 一节列有待办
- `Assets/PmxTranspileRuntime/MEPostCompose.{cs,shader}`：alpha 感知合成

## ADR-0013: 独立命令行模型导出工具，先交付无 bloom 的 `.me`

状态：Accepted　日期：2026-09-22

用户要求把最近的模型预处理流程收成仓库内小工具：指定 `.pmx`、配套 `.blend` 和输出路径，
生成可直接从 MateEngine 角色库导入的模型；明确选择命令行入口，并将 bloom 留到后续。

- `export_model.ps1` 串起现有导图、描边、转译和 Unity 构建流程。工具定位与串行进程执行
  提取到 `PmxTools.ps1`，与差分入口共用。中间产物按输入路径组合隔离，全部在仓库内。
- 转译 Material、Texture2D 与浮点 LUT 保存为真实资产，描边合并进已有网格资产，沿用
  原始 BlendShape、Humanoid 与 DynamicBone。新增显式参数 batch 入口，避免本机历史
  角色名、风格或导出路径影响结果，也不改写本机配置。
- SUN/FRONT/RIGHT 在应用预设后从求值网格提取。FRONT/RIGHT 以头骨局部方向序列化，
  `MEModelDirections` 在动画与鼠标跟踪之后写入每个实例的 PropertyBlock；SUN 以模型根节点局部
  方向解释，保持预设中日光相对角色的关系。2026-09-25 修正：原先按 App 世界方向使用，主场景
  `Model` 节点绕 Y 转了 180°，日光被转到角色背面，平均亮度低约 25%。导出时根节点为单位旋转，
  旧包存的数值不变，无需重导出。
- 模型 `.me` 使用 `VRMLoader` 已支持的原始 AssetBundle 格式，与 Mods 扫描链的 ZIP `.me`
  区分。Bundle 不携带 C#，因此 App 必须先重新构建以包含方向组件。
- 描边按底层图集修正（2026-10-01 用户选择，有意偏离 Blender）：预设可能把采样某张光照图的描边材质
  挂到 UV 在另一图集的部位上，按 alpha 分档取色时零星落进 0.05 的橙色皮肤档。导出时逐三角形按焊接组
  找回底层材质，若它不用该贴图，优先换成预设中未被使用、节点数相同、只采样一张贴图且正是底层材质那张
  贴图的描边材质（丽塔的 `edge_clothes2`）；没有则生成「同一描边着色器换图」的变体，新图取底层材质中
  色彩空间相同、文件名共同后缀（如 `_Lightmap`）最长且唯一、至少 4 个字符的那张（寻梦者的 Leg 光照图）。
  两种都判定不出则保持原样。仅导出路径启用，离线差分不受影响。
- 每次导出重新加载实际 bundle，验证网格/形态键、贴图色彩空间、材质及着色器出图，
  并核验转头方向与共享材质隔离。实际用到的未转译材质直接报错。

本期不改相机、UI 或 `MEPostCompose`。Filmic 仍在材质末端执行，没有 bloom，半透明在
显示域混合的差异仍存在。完整的线性合成、bloom 与辉光 alpha 取舍继续按 ADR-0012 后续处理。

## ADR-0014: LocalLow 原生写入调查与启动前路径重定向

状态：Accepted　日期：2026-09-22（用户明确授权正式接入原生入口）

### 已验证事实

- 旧 `-cache-path` 不起重定向作用：原始构建携带该参数运行，FileSystemWatcher 捕捉到
  启动及正常退出各一次 `LocalLow/Shinymoon/MateEngineX` 创建，之后由旧代码删除。
- `GetCachingManager` 是按需初始化；`ConfigureUnityCache` 的 `Caching.GetCacheByPath`
  会先初始化默认缓存，再添加便携缓存。当前本地 Addressables/Localization 和 `.me`
  都走文件加载，不需要 Unity 缓存。移除该调用及退出清理后，退出时不再重建目录，
  但启动时仍创建空目录。
- CDB 使用与本机 `6000.2.6f2` Player 匹配的 PDB，捕捉到启动创建的调用链：
  `Identifiers.StartSession → CrashReporter.CheckPendingNativeCrash →
  PlatformNativeCrashReporting.GetCrashReport → winutils.GetInsightsCrashFilePath →
  GetPersistentDataPathApplicationSpecific`。这是查找上次原生崩溃报告时的副作用。
- 构建前显式设置 `UnityEditor.EngineDiagnostics.EngineDiagnosticsSettings.enabled = false`
  仍未阻止上述路径创建。该 API 保证关闭诊断数据收集，不保证关闭原生报告路径检查；
  见 [Unity 6.2 API 文档](https://docs.unity3d.com/6000.2/Documentation/ScriptReference/EngineDiagnostics.EngineDiagnosticsSettings-enabled.html)。
  `CrashReportHandler.enableCaptureExceptions` 只控制旧 Cloud Diagnostics 的捕获开关；
  本机原生代码不在启动路径检查之前读取它。

### 决策与实现

采用 `Tools/PortableRuntime/PortableMain.cpp` 作为同名 Windows 原生 exe 入口，在同一
进程加载 `UnityPlayer.dll`，将**该模块导入表**的 `SHGetKnownFolderPath` 接到本地处理
函数，再原样调用 `UnityMain`。仅重定向当前用户的 `FOLDERID_LocalAppDataLow`；其他
目录查询交还 Windows。返回 `{exeDir}/UserData/Unity`，Unity 自行追加公司/产品目录。

在加载 Unity 前，仅设置本进程的 `TEMP/TMP=UserData/Cache/Temp`，让原生临时文件及
继承环境的 crash-handler 子进程使用程序内路径；不修改用户/系统环境。不得将原型的
LocalLow 验证表述为完整的磁盘/注册表审计或真正的原生崩溃转储验收。

构建通过 `build_launcher.ps1` 使用已安装的 MSVC 和 Windows SDK，静态链接 CRT，
并复制 Unity 原 exe 的全部 Windows 资源，保留图标、版本和 DPI/UAC 清单。命令行与
项目构建菜单自动安装该入口；如果原生步骤失败，构建失败且删除未完成的 exe。
发行版无需编译器、PowerShell 或项目源码。构建说明见 `Tools/PortableRuntime/README.md`。

移除 `ConfigureUnityCache`、运行时和构建期空目录清理、退出 PowerShell 进程，以及
无效的 `Assets/boot.config` 标记；`.bat` 仅保留启动和参数转发。业务路径及只读历史迁移
保持原有约定。UnityPlayer 磁盘文件、系统目录、用户环境、注册表均不由入口修改。

### 兼容性边界与验证

当前支持 `6000.2.6f2` Windows x64 Mono；使用 PE 导入名，不依赖 Unity 内部函数偏移。
升级 Unity 时必须重新核实导入接口、启动顺序和路径行为。无法安装重定向时禁止继续
启动。Unity 编辑器自身不经过该入口，仍按项目规则视为开发环境例外。

原型通过正常退出、强制结束及再次启动的文件事件检查；正式集成后的构建再次通过
正常退出与强制结束，均成功加载丽塔 `.me`、生成本地头像库，目标 LocalLow 目录
创建/删除事件为零。资源比对确认 12 项 Windows 资源逐项 SHA-256 一致。
仅复制发行文件和测试模型到含中文/空格的独立目录、无参数直接启动也已通过：模型路径全部位于该副本，LocalLow 事件为零。

证据保存在忽略目录 `Build/PortableAudit/`：`baseline-flag.json`、`fixed-normal.json`、
`fixed-nolog.json`、`diagnostics-off.json`、`native-startup.log`、`final-normal.json`、
`final-force.json`、`final-relocated.json`、`resource-verification.json` 与 `build-final.log`。
旧外部目录的删除已得到用户明确授权。

## ADR-0015: 主程序仅为转译 PMX 启用 Bloom，保留原 alpha 与现有 UI 相机

状态：Accepted　日期：2026-09-23（用户明确选择适用范围与 alpha 策略）

- 本期只处理节点图转译的 PMX 模型。普通 VRM 和其他 `.me` 沿用其自身渲染，不套用此预设的 Filmic。
- 合成保留源 alpha；轮廓内部受到 Bloom 影响。运行时合成 pass 3 与写回 pass 4 都会清零 alpha=0 处的 RGB，当前不显示外圈辉光。外圈是否可见留到实机查看后再定。主场景的鼠标穿透读取最终 alpha（阈值 0.1），不得把辉光扩散直接变成新的命中区域。
- 保留原主相机、`Camera.main`、`UniWindowController.currentCamera` 与现有 Canvas 引用。模型使用独立相机按主相机当帧矩阵渲进线性 HDR，在主相机绘制普通内容与 UI 前合入 Bloom + Filmic 结果；覆盖 `MateScreenshotHandler` 的手动 `Camera.Render()`。
- 窗口遮挡 quad 使用专用 `WindowOcclusion` 层，同时参与模型相机的深度绘制；仅在主相机事后写深度无法擦掉已合入的模型颜色。只处理导出时绑定的 Renderer，运行时提示与粒子等辅助对象继续走原路径。
- 合成图本身不带深度。写回主相机时先用模型 Renderer 补写深度（2026-09-25 补），按合成结果 alpha ≥ 0.5 裁剪、剔除方式随原材质，使光环、拖尾等辅助对象仍被模型遮挡；半透明处按不透明近似。此处使用最终合成 alpha，未重算每层材质的裁剪；后层使总 alpha 达标时，原本透明的前层几何也可能写深度，造成局部多遮挡。
- 分层窗口按预乘 alpha 合成。运行时输出预乘颜色：视图变换前还原未预乘值，之后乘回覆盖率，使边缘更接近材质内视图变换的旧路径、边缘辉光随覆盖率衰减（2026-09-25 补）；两角色边缘误差仍非零。离线差分保持原公式，不影响与 Blender 的对照成绩。
- 脸部 SDF 阴影去掉头部俯仰与侧倾，只保留左右转向（2026-10-01 用户选择，目标是桌宠观感而非物理一致）。预设的脸部阴影是硬边阈值，且按日光与头部右方向点积的正负左右翻转；鼠标在脸下方时头部低头，日光相对脸部变成从头顶照下，半张脸进入阴影，鼠标稍左右移动就整块换边。`MEModelDirections` 把 FRONT/RIGHT 投影到模型根节点水平面后再写入；这两个量只在脸部材质里与 SUN 做点积，身体不受影响，头部正视时与预设逐位相同。这是有意偏离 Blender 的处理，仅在头部俯仰时生效。
- 沿用已标定的 `MEBloomSettings.Default`，自动识别转译模型。用户选择复用现有 Bloom 开关并保留已保存状态，不新增调参面板；关闭 Bloom 仍做线性混合与 Filmic。转译模型启用时停用旧的 Unity Bloom volume，切回普通模型后恢复旧开关的作用。现有转译 `.me` 已有线性输出开关与 LUT 第 0 行，可复用；旧包必须搭配新版主程序执行新运行时逻辑。
- 运行时按实际目标尺寸渲染；离线验收的 4× 着色超采样不是本次默认实时配置。离线九角色成绩不能直接作为 App 实时画质或帧耗成绩，需单独记录接入验证。

实现与验证进度见 `PROGRESS.md`；本记录不代表运行验收完成。

## ADR-0016: PMX 物理按 PMX 关节限制与静态刚体生成

状态：Superseded by ADR-0017（2026-10-08）　日期：2026-10-02（用户反馈三处物理问题后排查；重力方案经用户选择）

> **2026-10-07：用户 App 内复看后改走 ADR-0017。** 下面的阻尼映射把 PMX（Bullet 按时间计算）的系数当成了每帧系数，
> 辫子每帧丢 40% 速度，动起来轻飘飘；DynamicBone 链间无连接、刚体间不碰撞，属于模型层面的差距。导出流程切换前，
> `.me` 仍按本 ADR 生成。

### 背景

用户反馈：丽塔动作大时腰部破损露出皮肤；寻梦者麻花辫始终是一条直线、刘海比其他部分晃得多；多数角色衣物
被腿穿过。旧 `PmxPhysics` 只按骨骼名把动态链分成头发/裙摆/胸/其他四类，每类一个 DynamicBone、一组统一参数，
从未读取 PMX 关节；碰撞体按 Humanoid 腿骨猜了 6 个，只给裙摆类。

两个模型（同一转换器出品）的 PMX 数据：刘海每节 ±1°～±3°；寻梦者辫子 15 节 ±1°, ±3°, …, ±29°；丽塔裙摆根节
锁死、往下每节 ±0.6°～±2.4°；寻梦者外套、裙摆整链锁死。身体上有完整的静态胶囊（躯干、上臂、前臂、头、大腿、
小腿），衣物和头发刚体按分组掩码与之碰撞。

### 决策

DynamicBone 与 MMD 的模型不同：刚度只约束粒子**相对动画姿态**的偏离，而 MMD 关节约束的是**相对上一节**的转角；
`m_Gravity` 会扣掉静止姿态下的重力分量。映射规则：

- 每条链（动态骨中父骨非动态者为根，取整个子树）一个 DynamicBone 组件。
- 每节的朝向由父骨刚体决定，它相对上游最多转关节限制（三轴取最大，下限大于上限视为不受限）。沿链累加得到每个
  粒子相对动画姿态的最大偏角 θ，按弦长换算成刚度 `s = 1 − sin(θ/2)`，写成 `m_StiffnessDistrib` 曲线；分叉处取较严者。
- 重力用不扣除静止分量的 `m_Force`（9.8 m/s²，按 60 Hz 每步位移），弹性 0：MMD 关节弹簧为 0，链的形状只由重力和
  限位决定。扣除静止分量的 `m_Gravity` 方案下，作者摆成斜线的辫子静止时不受重力，倾斜时也只弯一点。
- 阻尼按链上刚体的角阻尼在 0.05～0.4 之间插值。Bullet 阻尼是每秒衰减，1.0 时每步清零速度，不做硬换算。
- 根节与身体之间的关节锁死时，从根骨的子骨开始模拟，根骨完全跟随动画。DynamicBone 的根骨朝向由下一节粒子决定，
  否则大腿一顶，锁死的根节被转动，绑在根骨上的上衣下沿、短裙腰头离开身体。
- 碰撞体取自 PMX 静态刚体（盒体近似为沿最长边的胶囊），挂在骨骼下；双方分组掩码都允许才作用于该链。绑定姿态下
  已嵌入的不挂。头发（链根在头骨下方）在 App 待机姿势（`PET_IDLE.anim`）里已嵌入的也不挂：头发本就搭在胸前、
  肩上，胶囊比实际网格粗，碰撞只会把发梢顶出原位（丽塔左侧长鬓发因此内勾）。衣物不适用这条，否则外套又会被
  大腿穿过。
- 可调量集中在 `PmxPhysicsTuning`；`MEPhysicsValidation` 可同时比较多组取值。

### 否决的做法

- 弹性 0.02/0.1/0.2：0.2 能保住鬓发原形，但辫子回到斜直线。
- 碰撞余量 1 cm：静止检查因此排除更多碰撞体（寻梦者 39 → 66），外套穿模复现。
- 给 DynamicBone 增加「相邻两节相对转角」约束（用户选择试做）：鬓发内勾未消失，锁死的衣物链像刚性板一样整片外翻，
  已撤回，`Assets/DynamicBone/` 保持原样。
- 不锁根节：拖拽、坐下时大腿外侧反复撕开。

### 后果

- 只改导出工具，App 运行时代码未动；已有 App 可直接加载新 `.me`，旧 `.me` 需重导出才能获得新物理。
- 锁死根节不参与碰撞，抬腿姿势下裙摆最上一节可能与大腿胶囊相交；头发对待机时已嵌入的身体部位不再有碰撞。
- 旧版 `m_Inert` 的说明（「过高会滞留世界空间」）方向说反了，已在 `PROGRESS.md` 踩坑表就地更正。
- 实测数值与证据见 `PROGRESS.md` 验证记录（2026-10-02）。

## ADR-0017: PMX 物理改为把刚体与关节原样交给 Bullet 模拟

状态：Accepted（导出流程已切换，两角色已重导出；2026-10-15 起暂停，恢复时见 `PROGRESS.md`）　日期：2026-10-07（用户在 A/B 两个方向中选定；阻尼公式由用户看对比视频选定；2026-10-08 改为转向写回并定稿；2026-10-10 按 App 实机反馈改回 10 次迭代、加偏移锁归零；2026-10-15 补上动态刚体输出落后一个子步的延迟，加改为跟随骨骼、去掉分组碰撞两种逐角色调整）

### 背景

ADR-0016 的 DynamicBone 版本在 App 内复看：寻梦者的辫子不再僵直，但动起来轻飘飘；丽塔前裙不再破损，但像纸壳。
前者是阻尼换算错误（PMX 阻尼按时间计算，DynamicBone 按帧；角阻尼 1 被映射成每帧 0.4）。后者与模型修剪有关：
修剪版只保留了前后裙第 0～4 行骨骼，PMX 给这几行的关节限位是 0 与 ±1°～±2°（原版 PMX 刚体与关节逐项相同，
网格一直绑到第 17 行）。

用户问能否保证与 MMD 中原 PMX 的物理一样。DynamicBone 做不到：它把每条链当成一串没有质量的点，链与链之间
没有连接（PMX 裙摆相邻列之间有横向关节，整条裙子连成网），只与静态碰撞体碰撞，参数换算规则也是自定的。
这与渲染路线的教训相同（ADR-0009～0011：通用工具加调参追不上，复刻原始计算才收敛）。

### 决策

- 导出时把 PMX 的全部刚体与关节原样写进模型根节点上的 `MmdPhysics` 组件（`PmxRigidPhysics`），不做参数换算。
  坐标随模型整体绕 Y 转 180°，刚体与关节的局部轴保持 PMX 原样，定义在局部轴上的限位与弹簧原样沿用。
- 运行时用 Bullet 3.25（zlib 许可）模拟，与 MMD 同一种物理引擎：模式 0 为运动学刚体、关节为
  `btGeneric6DofSpringConstraint`、碰撞按 `1<<分组` 与掩码过滤；重力 9.8×10、关节停止 ERP 0.475、关节相连的刚体
  不碰撞，沿用 MMD 系播放器（three.js MMDPhysics、Saba）的做法。物理空间的长度用 PMX 原始单位，Bullet 的碰撞
  余量等绝对量才与 MMD 一致。
- 原生部分封装成 `MateMmdPhysics.dll`：源码与构建脚本在 `Tools/MmdPhysicsNative/`，Bullet 源码由脚本拉到仓库内
  （不入库），成品放 `Assets/Plugins/x86_64/` 随 App 打包；静态链接 CRT。
- 每帧在动画与鼠标跟随 IK 之后执行（执行顺序 10000）：模式 0 刚体跟随骨骼，步进后把模式 1/2 刚体写回骨骼
  （模式 2 只取旋转），父骨先于子骨。默认**转向写回**（`boneWrite = Aim`，有意偏离 MMD）：只写旋转，再把父骨转向
  本骨在物理里的位置，骨段长度不变；链根只有关节允许平移时才写回位置（胸部这类靠平移晃动的结构）。Bullet 的约束
  有弹性，平移锁死的关节在运动中也会被拉开数厘米、被大腿顶住时十几厘米，按 MMD 原样写回位置会让骨段拉长到原来的
  数倍（寻梦者外套跳起时像弹力绳一样伸长）；只写旋转又会丢掉刚体被身体推开的位移，网格穿进身体（用户看视频后否决）。
  物理驱动的骨骼每帧先还原成导出时的局部姿态，再交给动画。根节点一帧内移动过远或缩放变化时重置到当前姿态。
- 求解迭代用 Bullet 默认的 10 次（MMD 也是）。2026-10-08 曾提到 30 次（运动中关节被拉开的量少三到五成），App 实机
  验收后改回：按 App 的帧率（165 帧/秒）复现时，30 次迭代让锁死的衣物更像一块硬板，寻梦者坐下时外套下摆被大腿掀到
  腿上（与竖直方向平均 73°，10 次时 26°），静止时外套、饰带与腿来回推挤的抖动也更明显，带着贴在外套上的辫子一起抖；
  Bullet 关节弹簧的速度系数还要除以迭代次数，30 次时弹簧只剩 1/3。代价是舞蹈中衣物嵌入身体的帧变多（寻梦者外套前摆
  嵌入大腿超过 1 cm 的帧占比 2% → 10%，不跑物理时 31%）。原生库自己推进固定步长：
  每次只让 Bullet 走一个子步，运动学刚体按每个子步实际所在的时刻在上一帧与本帧的目标之间插值，输出取前后两个子步
  之间的线性插值，得到的是比本帧早一个子步时的姿态，再按每个动态刚体沿关节连到的最近一个运动学刚体（刘海连到头、
  胸部连到胸骨）从那时到本帧的整体运动补上（`MmdPhysics.followAnchors`，2026-10-15），只剩相对它的晃动晚一个子步。
  MMD 按帧时刻推进物理，没有这个延迟；不补时头一移动，刘海根骨转向下一节时对准的是落后的位置（待机时约偏 3°，
  随头部速度变化），胸部也拖在胸骨后面。补上后刘海根节逐帧转动的二阶差分待机时减半、舞蹈中少约三成。Bullet 自带的固定步长模式在第一个子步就把运动学刚体移到目标，
  输出又是拿速度往回外推的；2026-10-11 之前按子步序号平分，每帧最后一个子步总落在本帧目标上，而它代表的时刻比本帧
  早 0～1 个子步、每帧不同，渲染帧率（165）高于物理频率（60）时，挂在头上的刘海相对头部忽前忽后地颤（待机时前刘海
  逐帧转动的二阶差分减半，转头时降到 1/5～1/10）。
- PMX 里删了骨骼、只剩刚体的部分照常参与模拟（与 MMD 加载同一文件时一致），只是不驱动骨骼。
- 修剪过的角色若需要更软的效果，做成逐角色的导出调整配置，由用户逐个提出；通用流程保持忠实还原。
- 阻尼公式用新式：Bullet 2.74 起每步乘 (1−d)^dt。更早的版本乘 (1−d·dt)，查不到 MMD 内置的是哪一版；组件保留旧式
  写法（按固定步长精确换算成新式的等效值）。用户对比视频后的结论：寻梦者新式最自然，旧式辫子晃得太剧烈。
- 导出端三条有意偏离 MMD 的处理（`PmxRigidPhysicsOptions`，默认开启；前两条与上面的转向写回一起，2026-10-08 用户
  交由我判断，定为通用规则；第三条 2026-10-10 因用户实机反馈「胸部像被往上拽」加入）：
  - 锁根节：与跟随骨骼的刚体之间六轴全锁死（限位都是 0）的动态刚体改为跟随骨骼，只处理这一节、不沿链传递。Bullet
    的约束有弹性，下面整串刚体一甩，锁死的第一节就被拉偏，带走绑在它骨骼上的上衣下沿和腰头（丽塔最大 7.8 cm，
    寻梦者 6 cm）。不沿链传递是因为整链锁死的外套仍要靠碰撞推开，整链跟随骨骼会被大腿穿过。
  - 去掉悬挂刚体：没有骨骼、也不夹在「跟随骨骼的刚体 → 驱动骨骼的刚体」之间的动态刚体不导出。删了骨骼和网格的
    模型（丽塔留下 366 个，质量是可见裙摆的 1.7 倍）里，它们不驱动任何东西，只作为看不见的配重拖拽可见部分；
    胸部常见的无骨连接件夹在两者之间，不受影响。
  - 偏移锁归零（`offsetLocks = "zero"`）：平移限位上下限相等但不为 0 的轴（把刚体锁在偏离绑定位置的地方）改为锁在
    绑定位置。寻梦者和丽塔用的是同一套乳摇模板，`左胸_後2` 把竖直方向平移锁在 0.34、`左胸_前2` 锁在 0.38，主关节
    `左胸` 却只允许 ±0.11，转动关节又锁死；约束互相矛盾，Bullet 取折中，静止时胸部也被抬高约 2 cm、上翘 10°～26°。
    MMD 参照视频里胸部是正常形状。归零后（10 次迭代、165 帧/秒）静止时胸骨偏离寻梦者 0.3～0.6 cm、6°，丽塔 0.4 cm、
    14°～15°（2026-10-15 胸部关节改回 0.475 后约 7°），侧面近景看形状与不跑物理接近，舞蹈中照常晃动（最大 3～4 cm）；改为不限制（`free`）偏离更大。两角色
    各有 4 个关节命中。

- 逐角色调整的手段（`PmxRigidPhysicsOptions.loosenPattern`/`loosenDegrees`）：子刚体名匹配的关节，把原本允许转动的
  轴放宽到至少 ±N°。曾为丽塔拖拽穿腿放宽到 ±15°，后查明穿腿主要是验证场景直接从拖拽姿势开始造成的（刚体一开始就
  嵌在大腿里），按 App 的 0.25 秒过渡后不放宽也不穿，丽塔目前不用它。
- 逐角色物理配置：`Tools/PmxPipeline/physics/<PMX 文件名>.physics.json`，只写要改的项；字段是
  `PmxRigidPhysicsOptions`（导出时生效）或 `MmdPhysics`（写进组件、运行时生效）的公开字段，与验证装置
  `-physicsRigid` 的写法相同；有未知字段时导出报错（JsonUtility 会静默忽略拼错的字段）。验证装置的刚体方案也先
  套用它。按 PMX 文件名查找，因为导出脚本以它作模型名。
- 软限位（调低关节限位纠正强度 `jointStopErp`）只作为逐角色设置，默认保持 MMD 系播放器的 0.475。它能压住衣物被
  身体顶住时与限位来回推的抖动，但锁死的关节也一起变软；只软化有活动范围的轴更差。同一角色里衣物与头发要不同软硬时，
  用 `jointStopErpPattern`/`jointStopErpMatched` 给子刚体名匹配的关节另设一档。目前丽塔除胸部外 0.2（坐下时前裙抖动降到
  1/4～1/2），胸部保持 0.475（见下）；寻梦者头发 0.3、衣物（bs/zb/wt/wts/qz/xd/td）0.2：坐下时外套下摆垂下还是被大腿掀到腿上，对极细微的
  状态差别敏感，按 8 种略有差别的帧时长各坐一次，全部 0.3 时 3 次翻上腿，衣物 0.2 时 8 次都垂下（平均约 11°）；全部
  0.2 时头顶那缕翘起的头发在重力下耷拉、转头时摆幅翻倍，所以头发保持 0.3。代价是舞蹈中外套前摆嵌入大腿超过 1 cm
  的帧占比 16% → 23%（不跑物理 31%）。
- 改为跟随骨骼（`followPattern`，2026-10-15）：寻梦者的前刘海（每节限位只有 ±1°～±5°，11 个刚体）。耳饰起初也一并
  改了，但它跟随骨骼后成了推不动的碰撞体，把旁边的鬓发顶进身体（静止时嵌入超过 1 cm 的帧 31% → 89%），已撤回。刘海
  刚体的 PMX 角阻尼为 1，按新式阻尼每步角速度清零，头一转动，刘海只能先越过限位再被拉回：头转 270°/s 时每步转 4.5°，
  跳舞时刘海第二节相对头部平均偏 10°、最大 30°，还会在一个子步里突然多偏 16° 再弹回（用户说的「短促、剧烈」）。
  30 次迭代、旧式阻尼、纠正强度 0.475 都没有整体改善。这么小的限位说明作者本意是几乎不动，所以改为跟随头部；其余
  头发的各项指标不变，坐下时外套下摆照常垂下。
- 去掉与某些分组的碰撞（`maskOffPattern`/`maskOffGroups`，2026-10-15）：丽塔胸部不再与第 0 组（手臂、头、身体、腿）
  碰撞。乳摇模板用一个全锁死的关节（`奶子`）把左右胸连成一体，手臂一并拢，两边就被一起挤到一侧（待机轮播中胸骨最多
  偏 4.8 cm、48°）。胸部关节同时用 `jointStopErpPattern` 保持 0.475：丽塔为压住坐下时前裙的抖动整体用 0.2，胸部那一串
  锁死的关节也跟着变软，偏移与形变翻倍。修正后胸骨相对不跑物理：静止 15° → 7°，待机轮播最大 48° → 13°，拖拽最大
  39° → 12°，舞蹈平均 16° → 7°。裁短裙摆没有动上半身：原版与修剪版的胸部刚体、关节逐项相同，问题出在这边的设置。
  寻梦者用同一套模板，关节本来就是 0.3，只去掉碰撞：待机轮播最大 47° → 36°，拖拽最大 39° → 21°；再加 0.475 只多
  改善一点，还要给它的配置加第三档纠正强度，未做。

### 未定

- 步长：默认 1/60 s。1/120 s 使关节被拉开的量再少一些，但耗时翻倍，坐下抖动反而略增，未采用。
- 多角色同屏的耗时：每个角色一个 Bullet 世界，编辑器内按 165 帧/秒推进时寻梦者每帧约 1.1 ms、丽塔约 0.34 ms，
  尚未在 App 里实测。

### 否决的做法

- 继续修 DynamicBone 的换算：只能近似，链间连接、刚体间碰撞、质量都表达不了。
- Unity 自带的 PhysX（刚体 + ConfigurableJoint）：不是同一种引擎，MMD 模型的参数是按 Bullet 调的；ConfigurableJoint
  的 Y/Z 转动限位只能对称，表达不了 PMX 的不对称限位。

### 后果

- App 新增运行时代码和原生 DLL，需要重新构建；`.me` 里的 `MmdPhysics` 只能被新版 App 加载。2026-10-08 起导出
  流程只生成 `MmdPhysics`，之前导出的 `.me`（DynamicBone）照常可用；`PmxPhysics`（DynamicBone 生成）只留给验证
  装置做对照。
- App 侧不需要为坐任务栏、贴窗口另做物理重置：这些操作移动的是窗口和相机，模型在世界空间里不动。
- 物理效果与 PMX 本身的设置一致，作者留下的问题也会照样出现（如丽塔刘海在绑定姿态就嵌进头部胶囊，物理一开被
  顶开）。推断 MMD 里相同，未在 MMD 里核实。
- 仍做不到与 MMD 逐帧相同：Bullet 版本、步长和求解器设置与 MMD 有差别，MMD 的具体设置没有公开。
- 抖动、坐下时衣物的去向对帧率敏感：只在 App 的 165 帧/秒下才明显的现象，按 50 帧/秒的验证看不出来，验证装置
  要用 `-physicsDt` 按 App 的帧率推进。
- 实测数值与证据见 `PROGRESS.md` 验证记录（2026-10-07、2026-10-08、2026-10-10、2026-10-11、2026-10-15）。

## ADR-0018: 头发阴影转头时断开：导出时按角色调整头发法线

状态：Accepted（待 App 内验收）　日期：2026-10-11（用户要求修复，修法由我比较后选定；2026-10-15 按实机反馈加按头部形状混合）

### 背景

寻梦者转头时，头发侧面的阴影断成几截，边缘带着三角形的折角（2026-10-02 查明，见 `PROGRESS.md` 验证记录）。
预设的头发暗面是 `dot(SUN, N)` 的硬阈值，日光固定在模型根节点上；相邻发束的法线朝向差得多，正视时阴影带连贯，
鼠标跟踪转头（最多左右 45°、俯仰 30°）后，有的发束越过阈值、有的没有。Blender 同姿势同样如此，只是参考画面里
头部从不转动。当时用户选择保持与 Blender 一致；2026-10-11 实机看后认为太影响观感，要求修复，并问改原文件好
还是导出时处理好。

### 决策

- 导出时对指定材质的顶点法线做小范围平均（`PmxMeshAdjust.SmoothNormals`）：每个顶点取半径内朝向相近（夹角小于
  90°）的顶点法线，按距离线性衰减加权。只与朝向相近的邻居平均，是因为发片常是正反两面各一份顶点，法线相反，
  混在一起会互相抵消。
- 作为逐角色调整，写在 `Tools/PmxPipeline/mesh/<PMX 文件名>.mesh.json`（字段为 `PmxMeshOptions`，拼错报错）。
  寻梦者：`smoothNormalsPattern = "^头发$"`、`smoothNormalsRadius = 0.05`（米），3547 个顶点。
- 平滑之后再向头部的近似形状混合（`PmxMeshAdjust.ProxyNormals`，2026-10-15）：椭球取同一批顶点里高于头骨（PMX 骨骼
  `頭`）的部分的包围盒，法线取 (p − c)/r² 的方向；低于椭球中心的长发只取水平方向。发片背面那份顶点的法线朝内，混合时
  椭球法线也取朝内的一侧。寻梦者 `proxyNormalsWeight = 0.75`，拟合出的椭球中心离地 1.54 m、半径约 11 × 10 × 11 cm。
- 不改原文件：同样的调整写进 PMX 只能靠外部工具，参数也不便回头调；放在导出端可复现、可撤销，其他角色不受影响。

### 实测

转头扫视（左右 ±45°、俯仰 ±30°）的头部特写对比半径 0 / 1.5 / 3 / 5 cm：1.5 cm 几乎无改善；3 cm 碎块变少但仍在；
5 cm 侧面明暗基本连贯，顺着头发整体的形状走。正视时头发区域与平滑前平均只差 2.4/255，差别超过 20/255 的像素占 5%，
肉眼看不出。光照贴图里画好的阴影（边缘笔直的深色块）不受影响，Blender 正视画面里也有。

2026-10-15 用户实机反馈：转头歪头时，脸侧那缕侧发是亮的，紧挨着的侧面头发却在暗面里，「本该有阴影的地方没有覆盖
阴影」。这缕侧发垂在脸旁，发片本身朝前，平滑只能把相邻发束拉齐，改不了它整体朝前的事实。按头部形状混合 50% /
75% / 100% 对比（转头加 ±15° 歪头）：50% 断层仍在；75% 起头的侧面成为一整片连贯的暗面，明暗分界顺着头的轮廓走；
100% 发束之间的明暗变化几乎消失、显得平。取 75%：正视时头部特写与只平滑相比平均差 3.5/255（截图含脸和背景）。

### 否决的做法

- 让头部材质（脸、头发）的日光随头部转动（扣掉头相对胯部或头的全部转动）：已实现并对比后撤回。App 的相机始终在
  正前方，日光固定在正对相机的模型上，等于光从观察者这边照来，转头后朝向相机的那一面照样是亮的；日光随头部转动后，
  光跟着转到头的正前方，朝向相机的侧面反而成了背光面，脸侧与头发侧面出现大块阴影，比断开更难看。
- 把头发明暗的硬阈值改成柔和过渡：断口变成渐变，形状照样破碎；还要改转译出来的着色器。
- 修改原文件的法线：见上。

### 后果

- 导出的头发与 Blender 参考有差别（只平滑时正视平均 2.4/255，加按头部形状混合后又差 3.5/255），属有意偏离；转译管线的 Blender 差分验收若要逐位对照，
  需去掉该角色的网格配置后导出。
- 其他角色出现同类问题时，写各自的 `mesh/<PMX 文件名>.mesh.json`，半径按头发尺寸调整；头骨不叫 `頭` 时设
  `proxyNormalsBone`。
- 证据：`Build/PhysicsValidation/xmz-hairnormal/` 的 `头发平滑对比.png`、`头发平滑-侧面放大.png`、`否决-日光随头部.png`
  （列依次为现在 / 扣掉头相对胯部的转动 / 扣掉头的全部转动）；`Build/PhysicsValidation/xmz-hairproxy/头发法线对比.png`
  （原始 / 只平滑 / 平滑后按头部形状混合 75%）。
