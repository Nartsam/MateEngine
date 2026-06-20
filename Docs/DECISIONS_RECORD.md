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
| ADR-0008 | 2026-06-20 | PMX 模型离线导入管线（Editor → `.me`） | Accepted |
| ADR-0009 | 2026-06-20 | 通用 VRM + App 内置可切换渲染风格（含仿 HoYo） | Proposed |


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


## ADR-0008: PMX 模型离线导入管线（Editor → `.me`）

状态：Accepted（**渲染/输出部分已被 ADR-0009 取代**）

> 注：本 ADR 的"用 UTS2 映射 `.blend` 预设并烘进 `.me`"这一**渲染与输出**路线，经多轮 App 验收发现 UTS2 无法匹配 HoYo 多通道贴图语义（详见 `Docs/PMX_TO_VRM.md` §8 避坑指南），已由 **ADR-0009**（通用 VRM + App 内置可切换渲染风格）取代。前端（PMX 解析、Humanoid、物理检测、BlendShape）与离线管线框架仍有效并被复用；`.me` 导出保留为兼容路径。

背景：

需要把模之屋等来源的官方 PMX 模型（自带骨骼+物理）引入桌宠，并复刻其在原游戏中的 NPR 观感（原神/崩坏/P5X 风）。曾尝试两条路均失败：

- **PMX→VRM（Blender 插件导出）**：衣服骨骼物理丢失导致穿模、胳膊/手腕关节动作不自然、口型（MMD 形态键）丢失。
- **把 `.blend` EEVEE 预设烘焙进模型**：部件多、逐个烘焙繁琐，且预设用 EEVEE 实时 NPR、与 Cycles 烘焙不兼容，烘出全黑贴图。

关键认知（来自读代码与文件）：

- 工程实际渲染管线是 Built-in，内置 UTS2（`Assets/MATE ENGINE - Packages/Toon/`）、Poiyomi、lilToon 等 NPR 卡通着色器，足以复刻目标风格，无需迁移 URP。
- `.me` 下游全部现成：`MEModelExporter`（菜单 `MateEngine/ME Model Exporter`）把任意 prefab 连依赖打包成 `.me`；`VRMLoader.LoadAssetBundleModel` → `FinalizeLoadedModel` 自动赋动画控制器、注入全部 AvatarHandler。只要产出一个配好的 Humanoid prefab，桌宠逻辑全复用。
- 舞蹈口型：`AvatarDanceShapeConverter.HasMmdBlendshapes` 对带原始日文形态键（`まばたき/あ/い/う/え/お/にこり/怒り/困る/真面目/笑い` 等）的模型走 `bypassForThisAvatar` 直驱路径——舞蹈片段按名直接驱动模型自带形态键。**保留 PMX 原始日文形态键即可让口型原生工作**，这是之前 PMX→VRM 丢口型的根因与解法。
- `.blend` 预设本质是每材质一套 EEVEE 实时 NPR 节点树 + 绑头骨（頭）的「面部定位」空物体（脸部 SDF 阴影方向）+ 场景级 Bloom/Filmic/High-Contrast；不可烘焙，但可读取其参数翻译到 UTS2/lilToon。
- 用户接受离线预处理、模型数量少。

决策：

- 采用 **Editor 离线自动化管线**，不做运行时 PMX 加载器。
- PMX 导入用**仓库内自写脚本解析器**（PMX 2.0/2.1 规范），放在 `Assets/Editor/PmxPipeline/`，纯编辑器程序集；不使用 MMD4Mecanim（交互式、需手点 EULA/处理，无法 batch）。
- 用 `AvatarBuilder.BuildHumanAvatar` + MMD 日文骨名→Unity Humanoid 字典生成 Humanoid Avatar，导入时归一化到 T-Pose。
- 保留原始日文形态键作为 Unity BlendShape（顶点 morph），供舞蹈口型直驱路径使用。
- PMX 刚体/关节启发式转换为 DynamicBone 链（工程已内置 `Assets/DynamicBone`），近似 MMD 物理；同时按 Humanoid 腿部/髋部骨自动生成裙摆专用 `DynamicBoneCollider` 并只填入 Skirt 类 `DynamicBone.m_Colliders`，避免头发/胸部/饰品被下半身碰撞体顶开。碰撞体采用保守的大腿/小腿/髋部胶囊和骨盆球，并给裙摆 DynamicBone 小粒子半径，减少“骨点没穿但网格面穿”的情况。
- 所有外部路径（Blender、PMX、`.blend` 预设、导出 `.me`、输出根目录、临时目录、模型名）通过 `PmxPipelineOptions` 注入。优先级为 CLI 参数，其次 ignored 本机配置 `Library/MateEngineUserData/PmxPipeline/settings.json`，最后仅使用项目相对默认输出目录；不提供开发机绝对路径 fallback。
- 渲染：Blender 无头（`blender --background --python Tools/PmxPipeline/dump_render_preset.py -- --preset <blend> --out <json>`）提取 `.blend` 预设每材质节点参数导出 JSON，Unity 侧解析为 `PmxRenderPreset` 并由 `PmxMaterialMapper` 映射到 UTS2。Opaque 使用 `UnityChanToonShader/Toon_DoubleShadeWithFeather`，透明贴片使用 `UnityChanToonShader/Toon_DoubleShadeWithFeather_TransClipping`。无预设模型套用通用 NPR 基准，预设缺字段时局部 fallback。
- Blender 预设中贴图驱动节点组的 `baseColor`/diffuse socket 默认值不能无条件信任：本模型大量材质导出为 `[0,0,0,0]`，这不是实际主色。Unity 映射规则是：带贴图的 PMX 材质保留贴图原色（BaseColor=白，仅保留 alpha）；只有明显有效的 preset 颜色才作为无贴图材质/特殊材质输入；toon 阴影采用浅色 tint 与软 feather，避免在缺少脸部 SDF/专用 lightmap 时产生大块硬色块；matcap 保留原贴图颜色，不额外染色/压暗；rim 仅在 preset 明确提供时启用。
- 透明材质判定不能只看 Blender 节点树里是否存在 Alpha/Transparent BSDF，也不能扫描整张贴图是否有 alpha。许多 NPR 节点组会给所有材质保留透明分支，同一张大贴图也可能只在局部贴片区域有 alpha。当前规则是：PMX/Preset 实际 alpha < 1 直接透明；贴图 alpha 只按当前材质三角面实际使用的 UV 采样判定。
- 场景级 Bloom/ColorGrading 不写入主场景；PMX prefab root 挂 `PmxModelRenderProfile` 保存已解析数值，模型加载时临时应用到当前 `PostProcessVolume`，卸载/销毁时还原。若当前场景没有可用 Volume，组件会在模型下创建临时全局 `PostProcessVolume`，模型卸载时一并清理。
- 一键 batch 流水线：`BuildModel -pmx <path> [-preset <blend>] [-blender <exe>] [-outputRoot <assetFolder>] [-modelName <name>]`；`BuildAndExport -pmx <path> [-preset <blend>] [-blender <exe>] [-out <file.me>]`；`ExportMe [-prefab <assetPath>] [-out <file.me>]`。`BuildAndExport` 使用本次 `BuildModel` 返回的 prefab 路径，不再依赖固定角色名或固定 prefab 路径。

原因：

- 离线管线复用现有 `.me`/AvatarHandler/舞蹈口型全部能力，避开 PMX→VRM 的物理/关节/口型损失。
- 自写解析器对 Humanoid 映射、形态键保留、物理提取完全可控，且可 batch 自动化。
- Built-in NPR 着色器已在工程内，迁移 URP 成本与风险过高。

后果：

- 新增编辑器程序集 `Assets/Editor/PmxPipeline/`（仅编辑器，不进运行时）。
- 物理→DynamicBone、Humanoid 胶囊碰撞体、预设→UTS2/lilToon 均为有损近似，碰撞体尤其需要在“防穿模”和“不撑开服装/头发”之间逐模型微调；视觉/动作保真度需人工验收。
- `.me` 用 AssetBundle 打包 UTS2/lilToon 着色器时需验证着色器变体剥离（避免粉红），实施阶段复核。
- 本机路径复用依赖 ignored 配置文件；新机器必须显式传 CLI 参数或先写入本机配置。
- Blender preset dump 和 UTS2 映射只能复刻通用 NPR 参数；高保真脸部 SDF、自定义 shader 和更细的材质 morph 留给后续阶段。

### 相关文件

- `Assets/Editor/PmxPipeline/`（PMX 解析、Humanoid 生成、物理转换、材质映射、`.me` 打包、batch 驱动）
- `Tools/PmxPipeline/dump_render_preset.py`（Blender background 渲染预设导出）
- `Assets/MATE ENGINE - Scripts/AvatarHandlers/PmxModelRenderProfile.cs`（PMX 模型运行时后处理）
- `Assets/Editor/MEModelExporter.cs`（现有 `.me` 打包逻辑，将被 batch 流程复用）
- `Assets/MATE ENGINE - Scripts/AvatarHandlers/AvatarDanceShapeConverter.cs`（口型直驱路径依据）

## ADR-0009: 通用 VRM + App 内置可切换渲染风格（含仿 HoYo）

状态：**Proposed**（详细设计见 `Docs/RENDER_STYLE_DESIGN.md`）

### 背景
ADR-0008 把材质烘进 `.me`，导致改风格要重生成、格式不通用、UTS2 近似 HoYo 始终对不上颜色/分区。用户希望最终产物是**通用 VRM**，并把渲染风格放进软件、内置数套可切换风格，逼近 HoYo（崩坏3）原效果。仅个人自用，不考虑许可证。

### 决策
1. **模型格式改通用 VRM 1.0**：物理用 VRM10 SpringBone（复用 `PmxPhysics` 检测逻辑改输出）；额外 HoYo 贴图（LightMap/MetalMap/FaceSDF/ramp）打包进 glTF 并以 `extras` 元数据标注。VRM 在外部按 MToon 正常加载，额外图仅本软件读取。
2. **渲染风格放进软件**：`RenderStyleManager` 挂在 `VRMLoader.FinalizeLoadedModel`，加载时按当前风格替换材质、缓存原材质可还原；设置里实时切换。首批风格：`mtoon`(默认不改)、`hoyo_hi3`(精确)、`toon_generic`(降级)。
3. **着色器选 HoyoToon（HI3 变体）**，只看效果；vendor 进仓库，需先验证 Built-in + Unity 6000.2 兼容（N0）。
4. **后处理改场景级**（相机加 PostProcessLayer + 全局 Volume），与模型解耦，提供鲜艳感。
5. `PmxStyleConfig` 的 shader 无关旋钮保留为"每风格/每模型"调参；`.me` 路径保留兼容但主输出转 VRM。

### 影响
- 渲染风格与模型文件解耦：换风格不重生成模型。
- HoYo 高保真仅在本软件内成立；通用性靠 MToon 兜底。
- 既有 Humanoid/BlendShape/物理检测复用；UTS2 材质映射平移到运行时或弃用。

### 风险
- HoyoToon 在 Built-in + Unity6 的兼容性（最大不确定，N0 先验证）；运行时换材质的着色器变体剥离；脸 SDF 在自由视角的表现。详见设计文档 §10。

