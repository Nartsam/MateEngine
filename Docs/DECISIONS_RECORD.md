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

构建产物每次启动时，Unity 内置 Splash Screen 都会显示异常的破碎背景、黑色块和横向条纹。项目引用的 Splash 背景图 `DevICONBig.png` 本身正常，异常更像 Unity 启动早期 Splash 渲染/采样问题，而不是主场景或 URP 渲染问题。

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
- `.gitignore` 不再忽略 `.gitattributes`，确保换行策略可随仓库同步。

原因：

- 项目主要资产是 Unity YAML 文本序列化文件，统一 LF 可以减少跨平台和命令行构建后的假修改。
- 二进制资源不能参与换行转换，否则可能损坏资源。
- 规则提交到仓库后，新环境不依赖开发者个人 Git 配置。

后果：

- 后续新增 Unity 文本资产默认按 LF 入库。
- 已经存在的历史文件不会自动重写；如需完全规范化，必须单独执行一次受控的 renormalize 提交。
- 开发者仍应在打开 Unity 或构建后检查 `git status`，只提交任务相关文件。
