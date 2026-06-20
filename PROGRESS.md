# MateEngine Progress

## 说明

本文档维护项目的当前进度、任务列表。执行时先完成“正在处理”下堆积的任务，再处理其他项。阶段性进展完成后务必同步更新本进度文档。

- 上次更新：2026-06-21（完成回退后的文档一致性审查；**VRM+RenderStyle 路径已归档**，`.me` 管线确认为唯一生产路径。详见 `Docs/DECISIONS_RECORD.md` ADR-0009）

## 当前状态

当前仓库是 MateEngine 个人 fork，处于二次开发早期整理阶段。Steam 依赖移除和绿色便携化已完成代码改造。

当前分支：`main`

当前 Git 状态要点：

- Steamworks.NET 包、Steam API/插件/UI 脚本已移除。
- 新增 `Assets/Editor/BuildScript.cs`（命令行构建）、`Assets/MATE ENGINE - Scripts/Portable/`（便携路径管理）。
- 新增嵌入包 `Packages/com.unity.addressables/`，修复运行时默认访问 `Application.persistentDataPath`。
- 新增 `Assets/boot.config` 作为文档标记；`BuildScript.CreateLaunchBatch()` 构建后生成 `MateEngine.bat` 以 `-cache-path` 启动。
- 本地 Addressables group 已关闭 `Use Asset Bundle Cache`。
- 启动完成后的菜单启动音效已禁用；默认 UI 语言已改为简体中文。
- Unity 内置 Splash 已关闭；新增项目内加载场景作为首屏。
- 右键径向菜单打开后固定在展开瞬间的位置；Chibi/大头缩身功能已屏蔽。
- 新增 `.gitattributes`，固定 Unity 文本资产和源码使用 LF，降低 Windows 构建/打开 Unity 后的假脏状态。
- 已补齐嵌入 Addressables 包中被旧 `.gitignore` 误忽略的 `Build` 子目录源码；`.gitignore` 的 Unity 生成目录规则已限制为仓库根目录。
- 托盘右键菜单已全部汉化为简体中文。
- 设置面板 X 按钮改为隐藏窗口到托盘（不再退出）；双击托盘图标恢复窗口；只有右键菜单"退出"才真正退出。
- 设置面板主滚动列表通过 `SettingsMenuScrollBoundsLimiter` 在运行时动态适配实际内容高度：测量最底部元素（Delete AI History）→ 调整 VLG `padding.bottom` → CSF PreferredSize 自然适配 Content 高度 → 强制 Clamped + 滚回顶部。无需手动调整 Content SizeDelta 或禁用 CSF。
- 当前只保留 `Docs/ARCHITECTURE.md` 与 `Docs/DECISIONS_RECORD.md` 两个项目设计文档；`Docs/RENDER_STYLE_DESIGN.md`、`Docs/PMX_TO_VRM.md` 已随 VRM+RenderStyle 路径归档，不存在于当前工作树。
- `Assets/PmxImported/`、`Build/PmxModels/` 等 PMX 生成物是本地忽略产物；当前工作树中可能保留历史 `.me`/`.vrm` 验证输出，不作为仓库源状态或生产路线依据。

## 正在处理

- PMX 模型导入与原神/崩坏/P5X 风格渲染离线管线正在贯通。当前结论与状态：
  - 实际渲染管线是 **Built-in**（非文档原先所写 URP），已修正 `Docs/ARCHITECTURE.md`。
  - 工程已内置 UTS2（`MATE ENGINE - Packages/Toon/`）、Poiyomi、lilToon 等 Built-in NPR 卡通着色器。
  - 用户接受离线预处理、模型数量少。推荐走 **Unity Editor 离线管线 → `.me`**：编辑器内导入 PMX（保留原始日文 MMD 形态键与物理）→ 配 Humanoid Avatar → MMD 物理转 DynamicBone（已在工程内）→ 用 UTS2/lilToon 还原 `.blend` 预设外观 → `MateEngine/ME Model Exporter` 打包 `.me` → 现有 `VRMLoader.LoadAssetBundleModel` 加载。该路线下载逻辑（动画控制器赋值、组件注入、舞蹈口型）全部复用现有实现。
  - 关键依据：`AvatarDanceShapeConverter` 对带 MMD 形态键（まばたき/あ/い/う/え/お…）的模型走 `bypassForThisAvatar` 直驱路径，保留原始形态键即可让舞蹈口型原生工作——这正是之前 PMX→VRM 丢口型的根因。
  - 里程碑：M1 解析器 → M2 Humanoid → M3 物理转 DynamicBone → M4 Blender 无头提取预设映射 UTS2/lilToon → M5 一键 batch + `.me` 打包。
  - **M1a 已实测通过**：`Assets/Editor/PmxPipeline/`（PmxModel/PmxReader/MEPmxPipeline，纯编辑器程序集）batch 解析「特工丽塔/删披风删骨骼.pmx」成功——PMX2.0/UTF-16LE、26366 顶点、26 材质、268 骨骼、61 形态键（59 顶点 morph）、545 刚体（520 动态）、933 关节；**11/11 舞蹈口型形态键齐全**；材质用 `衣服a.png` 底色 + `mc1/mc3.png` sphere(matcap, blend=2)，正好对口 UTS2/lilToon。
  - **M1b 已实测通过**（用户视觉验收）：`PmxMeshBuilder` 在编辑器构建 prefab+网格+骨架+59 BlendShape(日文名)+基础材质，输出 `Assets/PmxImported/丽塔/`。坐标转换坑：MMD→Unity 必须用 **180° 绕 Y（负 X 负 Z）+ 保持原缠绕**（纯旋转、无镜像）；最初误用负 Z 单轴=反射，导致左右镜像（刘海遮错眼）。MMD 默认缩放 0.08（约 1.56m）。已知点：少数表情（照れ 腮红等）在 MMD 是材质 morph 非顶点 morph，不会成为 BlendShape，待后续单独处理。
  - **M2 已实测通过**（batch：avatar isValid/isHuman=True，53 骨骼映射，无左右互换报错）：`PmxHumanoid` 用 MMD 日文骨名→Humanoid 字典 + 手臂 A-pose→T-pose 归一化 + `AvatarBuilder.BuildHumanAvatar`。关键映射：**Hips=腰**（上半身/下半身 是 腰 的兄弟，腰 是脊柱与双腿唯一共同祖先；映射 Hips=下半身 会失败）。LowerArm=ひじ、Hand=手首（捩 twist 骨作为未映射中间骨保留）。生成 `丽塔_Avatar.asset` 并挂到 prefab 的 Animator。
  - **M3 已 batch 构建**（待运行验证）：`PmxPhysics` 把 PMX 动态刚体(PhysicsMode≠0)按链根（父骨非动态的动态骨）转 DynamicBone（工程已内置，`m_Roots` 支持多链）。特工丽塔：3 个 DynamicBone 组件、37 条链（hair=9 / skirt=26 / breast=2），按 头发/裙子/胸/其他 分类套经验参数。v1 不加碰撞体（裙子可能穿腿），待后续。DynamicBone 仅运行时仿真，需 Play 模式或 App 验证。
  - **M5 已 batch 导出**（待 App 运行验证）：`MEPmxPipeline.ExportMe` 复用 AssetBundle 打包逻辑把 prefab 连依赖打成 `.me`（排除 .cs，脚本在 player 解析）。产物 `Build/PmxModels/丽塔.me`（3MB，35 依赖）。加载路径核实：AvatarHandler 多用 `animator.GetBoneTransform`（humanoid），我们的 avatar 有效；舞蹈口型走 MMD 形态键 bypass；物理走 DynamicBone。VRM 专属眼球 LookAt（`Vrm10Instance`）会失效（非 VRM 模型），属可接受降级。
  - **M5 首次运行验证发现 3 个问题，已诊断+修复（待复测）**。用 `DumpSkinning` 诊断坐实根因，未靠猜：
    1. 腿不打弯/角色飘（我的转换 bug）：腿部蒙皮权重挂在 MMD「D」変形ボーン（左足D/右足D/ひざD/足首D，755/709/415... 顶点），这些骨骼仅靠 付与(inherit weight 1.0) 获得旋转、且是控制骨(足/ひざ/足首)的兄弟而非子级，引擎驱动控制骨时蒙皮不动→腿直。**修复**：`BuildSkinRemap` 把「付与源非自身祖先」的变形骨蒙皮权重重指到源控制骨（几何等价，D 覆盖控制骨），并向死分支传播（足先EX）。本模型重指 26 骨。捩(twist)骨因源是祖先、靠父子级跟随，不重指、无损失。
    2. 拖动时部分服装固定在空间中/拉伸：受影响部位均为 DynamicBone 动态骨；`m_Inert` 0.3–0.5 过高导致拖动时布料滞留世界空间。**修复**：`m_Inert` 降到 ~0（裙 0、发 0.05），布料跟随身体、仅靠运动摆动。
    3. 服装穿模：无碰撞体固有问题，**暂缓**（后续 M3.5 给腿/躯干加 DynamicBoneCollider）。
  - **第二轮复测：腿已修复✓；新发现 口型不动 + 头发剧烈抖动，已修（待三测）**：
    4. 口型/表情不动：`UniversalBlendshapes` 只驱动 VRM（VRMBlendShapeProxy/Vrm10），非 VRM 模型走 `AvatarDanceShapeConverter` 的 bypass，靠舞蹈片段按 transform 路径直驱形态键。引擎舞蹈片段把形态键曲线绑到 `Body` 路径（converter 的 candidatePaths={"Body","Model/Body","Face"}），而我们 SMR 在 root → 不命中。**修复**：把 SkinnedMeshRenderer 挂到 root 下的子物体 `Body`（骨架仍在 root）。⚠️注意：抽查的 3 个内置舞蹈片段都**没有**形态键曲线，若用户导入的舞蹈也无面部 morph 轨，任何模型侧修复都无法产生口型——需用确含 morph 的舞蹈验证。
    5. 头发发梢剧烈抖动（欠阻尼振荡）：DynamicBone 参数过软。**修复**：大幅提高 m_Damping(发 0.6)/m_Stiffness(0.3)，降 elasticity，m_Inert 0.1-0.15。
  - 穿模(碰撞体)与拖动粘连若复测仍在，再单独诊断/加碰撞体。
  - **第三轮复测：口型已动✓、腿正常✓；粘连根因找到并修复，头发继续调**：
    6. 粘连/拉伸（用户已用 PMX 编辑器删过飘带/下摆）：`DumpOrphans` 诊断出 **20 个 `衣饰` 顶点被权重到 `操作中心`(bone0，原点处不变形的视点骨)**，最大距骨 11.3u——是删除后残留几何被 PMX 编辑器重指到 bone0 的垃圾面，原点不跟身体动→粘连。**修复**：`FindOrphanVerts` 把主权重落在非变形骨（操作中心/IK）的顶点判为孤儿，构网格时丢弃含孤儿顶点的面（本模型丢 40 面）。属模型瑕疵，导入器自动兜底。
    7. 头发上轮过僵→这轮反而过飘/幅度大/归位慢（欠刚度欠弹性、阻尼偏高）。再调：stiffness↑(发 0.55)、elasticity↑(0.25)、damping↓(0.3)。物理手感主观，建议改用 Play 模式实时调参再回填。
  - **M3.5 已代码完成并 batch 导出（待 App 运行复测）**：`PmxPhysics` 在 Humanoid Avatar/Animator 可用时，按腿部与髋部骨骼自动生成裙摆专用 `DynamicBoneCollider`，碰撞体作为对应骨骼的子物体随动画运动。第三轮根据“下摆被撑起、头发被顶起/穿模”的截图修正过强问题：碰撞体只绑定到 Skirt 类 DynamicBone，Hair/Breast/Other 不再吃身体碰撞；移除横向髋部胶囊和胸/上身胶囊，保留更保守的大腿/小腿/髋/骨盆共 6 个碰撞体；裙摆粒子半径降为 `0.012`。Unity batch 已重新生成特工丽塔 prefab，并导出 `Build/PmxModels/丽塔.me`；仍需在 App 内加载验证裙摆/头发状态。
  - `.gitattributes` 已给 vendored lilToon shader 路径增加 `text eol=crlf` 例外：仓库 blob 仍规范化，Windows 工作树允许 CRLF，避免 Unity/包文件反复产生 shader 换行假脏。
  - **M4 高保真 HoYo 复刻尝试（UTS2 多轮调试）已封存，根因沉淀进下方踩坑表**：用 Blender 无头抽预设映射 UTS2（`PmxRenderPreset`/`PmxMaterialMapper`/`PmxPipelineOptions`/`BuildAndExport`/运行时 `PmxModelRenderProfile`）经多轮 App 验收，逐一定位并修复了发紫、脸部碎裂、偏暗偏素、后处理失效等根因。结论不是放弃 `.me` 或 UTS2 基线，而是不再把 UTS2 当作高保真 HoYo 多通道语义复刻方案；clean-baseline、`PmxStyleConfig` 和 `.me` 导出仍是当前生产路径的一部分。

  **M4 踩坑表（PMX->Unity UTS2 全流程）**：

  | 现象 | 根因 | 解决 |
  |---|---|---|
  | 模型左右镜像 | 仅取负 Z + 反转缠绕 = 镜面反射 | MMD->Unity 绕 Y 180 (同时取负 X 和 Z) + 保持原始缠绕 + scale 0.08 |
  | 腿不弯/部位悬空 | 蒙皮权重落在 付与/D 变形骨（仅靠 inherit 驱动） | BuildSkinRemap：inherit 权重重定向到源控制骨 |
  | 粘连/拉伸 | PMX 编辑器删面残留的孤立顶点（蒙皮到 操作中心/IK 骨） | FindOrphanVerts 丢弃远离主骨的面 |
  | 跳舞口型/表情不动 | UniversalBlendshapes 仅 VRM；舞蹈绑定 Body 路径 | SMR 放在 Body 子物体；保留日文形态键名走 bypass |
  | 全身发紫 | MetalMap + mc1/mc3 被当 matcap 叠加 | matcap 全关；highColor 仅预设显式给有效非黑色才启用 |
  | **脸部碎裂** | 脸 UV 被错配 Body_LightMap(身体 UV) 当阴影位置图 | 不把 LightMap/Facemap 当 UTS2 阴影位置图（UTS2 无此通道语义） |
  | 脸部碎感（次因） | 共面贴片同 renderQueue z-fight；双面渲染 | 按材质序号递增 renderQueue；强制单面 CullMode=2 |
  | 偏暗/偏素/皮肤偏红 | 相机无 PostProcessLayer；皮肤暖色偏移 | 给相机接通后处理；皮肤暖色中性化，由 style.json 微调 |
  | 渲染参数改一次重跑整链 | 风格参数写死在 C# | PmxStyleConfig 解耦到逐模型 JSON + Editor 实时调 .mat |
  | HoYo 游戏贴图找不到 | 贴图打包在 .blend 里、无散文件 | 阶段 0 Blender 无头抽图 |
  | Blender 5.0.1 抽图报错 | 插件路径不匹配 | 用 Blender 3.6 |
  | VRM 加载后整模型几乎不可见 | MToon _Color 取自预设 [0,0,0,0] -> 全黑/透明 | 有贴图材质白色 tint；运行时 NormalizeTint 降级守卫 |

  - **风格解耦（已完成）**：`PmxStyleConfig`（`Tools/PmxPipeline/styles/<模型>.style.json`）把材质风格调参从 C# 移到逐模型 JSON（`skinWarmth`/`brightness`/`outlineScale`/`rimStrength` + 逐材质覆盖），支持"改 JSON 重跑"与"Editor 实时调 `*.mat`"两种不重编译的迭代循环（见 `Docs/ARCHITECTURE.md`）。
  - ~~**VRM+RenderStyle 备选路线（已并入 ADR-0008）**~~ **[ARCHIVED 2026-06-21]**：该路径 N0-N3 实现完成后实测不可行（HoyoToon 渲染异常 + SpringBone 物理模型不兼容）。代码归档至 `archive/vrm-detour` 分支。复盘见 `Docs/DECISIONS_RECORD.md` ADR-0009。`.me` 管线（UTS2 + DynamicBone）确认为唯一生产路径。

## 下一步

- `.me` 管线（UTS2 + DynamicBone）已确认为唯一生产路径。后续迭代方向：
  - 继续打磨 UTS2 材质参数（逐模型 `style.json` 调参，见 `PmxStyleConfig`）
  - M3.5 碰撞体 App 内运行复测
  - 接通相机后处理（Bloom + ColorGrading，已在 `PmxModelRenderProfile` 有代码）
  - 处理材质 morph 类表情（照れ 腮红等，非顶点 morph，需单独方案）

## 已完成

### 文档渲染管线表述修正

- 核实工程实际运行在 **Built-in 渲染管线**：URP 包不在 `manifest.json`/`packages-lock.json`/`PackageCache`/嵌入包中；`GraphicsSettings`/`QualitySettings` 未指派任何 SRP；装的是 Built-in 后处理 Stack `com.unity.postprocessing 3.5.1`；默认角色 Zome 用 MToon 的 Built-in 变体。`Assets/Settings/` 下的 `PC_RPAsset` 等 URP 资产为惰性残留。
- 修正 `Docs/ARCHITECTURE.md` 快速事实表、技术栈表、目录结构表中将渲染管线/着色器误标为 URP 的描述，并补注 UTS2 位置与 URP 残留资产说明；修正 `Docs/DECISIONS_RECORD.md` ADR-0005 中“URP 渲染问题”表述。

### Steam 依赖移除

- 删除 Steamworks.NET SDK 包。
- 删除 Steam 初始化、Steam Workshop、Steam DRM、Steam 原生插件和上传按钮相关脚本。
- 清理混用 Steam 逻辑的脚本。
- 保留部分旧序列化字段，避免破坏 Unity 场景和旧 JSON 兼容性。

### 构建脚本

- 新增 `Assets/Editor/BuildScript.cs`。
- 使用 `BuildScript.BuildWindows` 显式指定加载场景和主场景：
  `Assets/MATE ENGINE - Scenes/Mate Engine Loading.unity`
  `Assets/MATE ENGINE - Scenes/Mate Engine Main.unity`
- 构建产物输出到 `Build/MateEngine.exe`。

### .gitignore 清理

- 忽略 `UserSettings/`、`.vs/`、Steam 残留文件、Burst debug 目录、LLMManager 运行时配置等。
- `Build/`、`Library/`、`Temp/`、`Logs/`、`obj/` 等本地输出目录不提交。
- `.gitattributes` 不再忽略；仓库通过该文件固定 Unity 文本资产、源码和文档的换行策略。
- Unity 生成目录 ignore 规则使用根目录锚定，避免误忽略嵌入包内部的 `Build/` 等源码目录；`ProjectSettings/Packages` 不再整目录忽略。

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

### 启动体验与默认语言

- `MenuAudioHandler` 不再启动 `PlayStartupDelayed()`，启动完成后的菜单启动音效不会播放；保留 `startupSounds` 等序列化字段，避免破坏主场景、旧场景或 Voice Pack 引用。
- `SettingsData.selectedLocaleCode` 新默认值改为 `zh`。
- `LanguageDropdownHandler` 对空/非法语言代码优先回退到 `zh`，并同步写回设置。
- `Localization Settings.asset` 的项目语言和启动兜底语言改为 `zh`，启动选择器保留命令行 `-language=`，移除系统语言优先选择，避免英文系统首次启动时覆盖简中默认。
- 新增 `Mate Engine Loading.unity` 和 `LoadingScreenController`，运行时创建 Canvas 加载页并异步加载主场景。
- `ProjectSettings` 已关闭 Unity 内置 Splash Screen，避免启动早期 Splash 渲染异常。

### 右键径向菜单

- `MenuActions` 保留打开瞬间按目标骨骼定位右键径向菜单，但菜单打开后不再逐帧跟随人物骨骼，避免舞蹈或大幅移动时菜单漂移。
- 主场景中的 `Chibi` 径向菜单按钮已设为 inactive；`UISetOnOff.ToggleChibiMode()` 和 `ChibiToggle.ToggleChibiMode()` 保留兼容入口但不再执行大头缩身变形。
- `CircleSelector` 已将 inactive 源按钮排除在运行时圆环按钮列表之外，避免隐藏 Chibi 按钮后选择扇区和可点击按钮错位。

### 托盘菜单汉化与隐藏到托盘

- 托盘右键菜单所有英文标签改为简体中文：场景中 14 个序列化 `TrayAction.label` 和代码中 2 个硬编码字符串。
- 设置面板 X 按钮（`UISetOnOff.CloseApp()`）从 `Application.Quit()` 改为 `RemoveTaskbarApp.HideMainWindow()`，点击后隐藏程序窗口，只保留托盘图标。
- `RemoveTaskbarApp` 新增 `HideMainWindow()`/`ShowMainWindow()` 静态方法，使用 `ShowWindow` Win32 API 控制窗口可见性。
- `TrayIcon` 新增 `WM_LBUTTONDBLCLK` 处理和 `OnDoubleClick` 回调；`SystemTray.Awake()` 注册双击回调调用 `ShowMainWindow()`。
- 只有托盘右键菜单中的"退出"项调用 `Application.Quit()` 真正退出程序。
- `UISetOnOff.CloseApp()` 在隐藏窗口前先调用 `CloseSettingsPanel()`：通过 `GameObject.Find("SettingsMenuCanvas")` 定位设置面板根容器并 `SetActive(false)`，确保双击托盘恢复后只显示角色、无残留设置菜单。`SettingsMenuCanvas` 是面板显隐单位（默认 inactive，打开入口即 `SetOnOff(SettingsMenuCanvas)`），名称全场景唯一；点 X 时面板可见即该容器必然 active，故 `GameObject.Find` 可靠命中。不再依赖可能 inactive、名称不唯一的子面板对象（Debugging / Main Menu / FooterToggles）。
- 关闭面板与隐藏窗口之间存在渲染时序差：`SetActive(false)` 只改对象状态、当帧画面尚未重绘，若立即 `ShowWindow(SW_HIDE)`，窗口前缓冲区会定格在仍带菜单的上一帧，双击托盘恢复时先显示该旧帧再刷新，表现为设置菜单闪一下。改为协程 `HideAfterPanelClosed()`（`yield return null` + `WaitForEndOfFrame`）放行一帧、让无菜单画面渲染并呈现后再 `HideMainWindow()`，恢复时即为干净画面。

### 移除 Discord、设置菜单广告与 DLC 模型

- 主场景 `Mate Engine Main.unity` 屏蔽（`m_IsActive: 0`）以下对象，不改业务代码、不删第三方库，保留序列化引用，安全可逆：
  - Discord：`DiscordPresence`（运行时 Rich Presence 连接）、`Discord RPC`（设置面板"DISCORD 状态显示"勾选框整行）、`Discord`（Discord 图标 + 加入服务器按钮）。`DiscordRPC` 库与 `DiscordPresence.cs` 保留以免破坏编译，对象 inactive 后运行时不再连接。
  - 设置菜单内容区底部 3 块推广（紧邻 `= DEBUG`/强制垃圾清理之后）：屏蔽 3 个标题 section `= STEAM DLC`、`= MINECRAFT`、`= FOOD SYSTEM`，以及它们在 `Category Background` 下对应的背景卡片 `Image (12)`~`Image (14)`（3 个）。标题与背景是分离的同级对象，故两处都要屏蔽；`Image (11)` 是 `= DEBUG` 的背景卡片，必须保留并收紧高度。
  - 设置面板 ScrollRect 内容根（fileID 6157687972013927576）高度从 `SizeDelta.y: 5000` 缩小到 `3948`，`Image (11)` 高度从 `220` 收紧到 `180`，使滚动区止于"强制垃圾清理"（= DEBUG）调试按钮底边；同时将该 ScrollRect 的 `Movement Type` 从 Elastic 改为 Clamped，避免在底部继续滚动时弹性越界露出被移除推广区留下的空白。
- 新增 `SettingsMenuScrollBoundsLimiter` 并挂载到 `SettingsMenuCanvas`：设置面板打开时按 `Delete AI History` 按钮的实际世界坐标重新计算主设置 ScrollRect Content 高度，下一帧再复核一次，并在运行时强制 `MovementType.Clamped`、停止惯性、夹住当前滚动位置。该兜底覆盖 Unity 运行时或布局刷新把被移除推广区高度带回来的情况。
- `AvatarLibraryMenu.dlcAvatars` 列表清空，移除模型选项中 3 个 `fileType: DLC` 模型（Aldina / Lazuli / Ayrina）；保留默认 `Built-in` 模型与用户导入的 VRM。DLC 项由 `RefreshUI()` 按列表运行时动态生成，故从数据源清空而非屏蔽 UI。

## 可选后续方向

- 程序化空闲动作：自主张望、自主踱步、随机兴趣点。
- 渲染升级：为特定角色挂载专用 Shader，或通过 `.me` Mod 管理高级材质。
- 外部通信接口：`HttpListener`、WebSocket、VMC Protocol、OSC over UDP。
- 继续收敛便携化边界，补充运行时写入审计。

## 已知问题与风险

| 问题 | 状态 | 备注 |
|---|---|---|
| 当前变更尚未提交 | 已处理 | 任务变更完成后提交 |
| 默认 Avatar 分发风险 | 持续关注 | Zome 不可随自制 build 分发 |
| Unity 版本敏感 | 持续关注 | 必须使用 `6000.2.6f2` |
| 外部写入审计 | 静态已完成，待运行复核 | 需运行构建产物后确认磁盘和注册表实际行为 |
| 开机自启动写注册表 | 已知例外 | 用户主动启用/关闭时写入或删除 `HKCU\...\Run` |
| 多实例启动器可执行文件名 | 待修复 | `BuildScript` 输出 `MateEngine.exe`，但 `LaunchMateEngineInstance.cs` 默认值与主/Update 场景序列化值仍为 `MateEngineX.exe`；若使用多实例按钮会找错 exe |
| 被跟踪的恢复/备份/样例产物 | 待确认 | `Assets/_Recovery/0.unity`、`UMotionData/AutoBackups/`、`ExportedMods/*.me`、`AssetBundles/`、`Sync/dance_sync.json` 等当前被 Git 跟踪；是否清理需逐项确认用途，避免误删上游样例或有效测试资源 |

## 错误提醒

以下为近期设置菜单滚动区修复中经过的 3 轮错误尝试，留作警示。

### 设置菜单布局的特殊约束

Content 仅有一个 RectTransform 子元素 MenuPanel（60px）。其下 **"Main Menu" 是普通 Transform（非 RectTransform）**，所有设置条目（= GENERAL、= AI、= DEBUG 等）均为 "Main Menu" 下的绝对定位 RectTransform 子元素。VLG 和 CSF **完全看不见** "Main Menu" 及其子元素——开发者用巨大的 VLG `m_Bottom`（4600）人为撑高 Content，使绝对定位条目有足够空间显示。

### 错误 1：VLG Bottom 设为 0 + CSF PreferredSize

- **现象**：Content 被压缩到 ~400px（VLG 首选高度 = 340 + 60 + 0），仅显示顶部少量内容，大部分条目不可见
- **教训**：VLG Bottom=0 切断了绝对定位条目唯一的空间来源

### 错误 2：VLG Bottom 设为 0 + CSF Unconstrained

- **现象**：VLG 可用空间巨大（3948 - 340 - 0 = 3608），ChildForceExpandHeight 将 MenuPanel 从 60px 拉伸到 3608px → 其下"Main Menu"及其绝对定位子元素全部向下偏移 → 部分内容偏移到 Content 范围之外不可见
- **教训**：禁用 CSF 后 VLG 的 ChildForceExpandHeight 会按可用空间拉伸孩子，破坏绝对定位布局

### 错误 3：SetSizeWithCurrentAnchors 裁剪 Content + 禁用 CSF

- **现象**：底部显示正常，但顶部缺失（标题、按钮图标不可见）。`verticalNormalizedPosition = 1f` 无法修复
- **教训**：`SetSizeWithCurrentAnchors` 绕过布局系统直接修改 Content 尺寸，与锚点/pivot 系统产生非预期偏移。**不应与布局系统对抗，而应与之协同**——调整 VLG 参数让 CSF 自然产生正确结果

### 正确做法

运行时调整 VLG `padding.bottom` → Canvas.ForceUpdateCanvases() → CSF PreferredSize 读取新的 VLG 首选高度 → 自然设置 Content 高度。整个过程走标准布局管线，无任何副作用。

## 验证记录

| 日期 | 验证项 | 结果 | 备注 |
|---|---|---|---|
| 2026-06-18 | Steam 依赖移除后构建 | 已通过 | 零编译错误 |
| 2026-06-18 | 外部写入静态审计 | 已完成 | 剩余命中为编辑器/移动端路径、旧数据只读迁移或用户主动功能 |
| 2026-06-19 | 便携化代码改造 | 代码完成，待运行验证 | 含 Player.log、Addressables patch、cache-path 重定向、PowerShell 兜底清理 |
| 2026-06-19 | LocalLow 空目录排查（多轮迭代） | 代码完成，待构建/运行复核 | 逐层定位到 Unity C++ 原生引擎 Cache 初始化 → 修复：`-cache-path` 命令行参数（主）+ 后台 PowerShell 清理（兜底） |
| 2026-06-19 | 启动音效禁用与默认简中 | 静态验证完成，待 Unity 运行复核 | `git diff --check` 无空白错误；未运行 Unity 构建 |
| 2026-06-19 | 自定义加载场景替代 Unity Splash | 静态验证完成，待 Unity 运行复核 | Unity Splash 已关闭；Build 场景顺序为 Loading -> Main |
| 2026-06-19 | 右键径向菜单固定与 Chibi 功能屏蔽 | 静态验证完成，待 Unity 运行复核 | `git diff --check` 无空白错误；未运行 Unity Play Mode |
| 2026-06-19 | `.gitattributes` 换行策略 | 静态验证完成 | Unity 文本资产和源码固定为 LF；二进制资源标记为 binary |
| 2026-06-19 | 径向菜单隐藏 Chibi 后点击失效修复 | 静态验证完成，待 Unity 运行复核 | inactive 源按钮不再参与 `CircleSelector` 的按钮实例和命中计算 |
| 2026-06-19 | Addressables 嵌入包缺失 `Build` 目录修复 | Unity 批处理编译检查通过 | 补齐 `Packages/com.unity.addressables/Editor/Build` 与 `Tests/Editor/Build`；原 `CS0234/CS0246` 不再出现 |
| 2026-06-19 | 托盘菜单汉化与隐藏到托盘 | 代码完成，待构建/运行复核 | 托盘右键菜单全部中文；X 按钮隐藏窗口；双击托盘恢复；退出仅限右键菜单 |
| 2026-06-19 | X 按钮隐藏前关闭设置面板 | 代码完成，待运行复核 | 根因：原先按子面板名（Debugging/Main Menu）查找，子面板可能 inactive 且名称不唯一，`GameObject.Find` 时灵时不灵；改为关闭唯一且必为 active 的根容器 `SettingsMenuCanvas` |
| 2026-06-19 | 双击恢复时设置菜单闪烁修复 | 代码完成，待运行复核 | 渲染时序差：关面板后用协程放行一帧（`yield null` + `WaitForEndOfFrame`）再隐藏窗口，避免窗口前缓冲区定格在带菜单旧帧 |
| 2026-06-20 | 移除 Discord / 设置菜单广告 / DLC 模型 | 静态复核完成，待运行复核 | 场景屏蔽 Discord 三对象 + 3 块推广 section（STEAM DLC/MINECRAFT/FOOD SYSTEM）；保留并收紧 `Image (11)` 作为 DEBUG 背景，仅屏蔽推广背景 `Image (12)`~`Image (14)`；ScrollRect 内容高度止于调试按钮底边，并改为 Clamped 防止底部弹性越界空白；清空 `AvatarLibraryMenu.dlcAvatars`，模型选项仅留 Built-in 与用户导入 VRM |
| 2026-06-20 | 修复设置菜单滚动区空白（SettingsMenuScrollBoundsLimiter） | 代码完成，待构建/运行复核 | 根因：广告区移除后 VLG Bottom 4600 被 CSF PreferredSize 撑出 ~1000px 空白。最终方案：Limiter 运行时调整 VLG `padding.bottom` → CSF 自然适配 Content 高度。详见 `Docs/DECISIONS_RECORD.md` ADR-0007。 |
| 2026-06-20 | 文档渲染管线表述修正（URP→Built-in） | 静态核实完成 | 多源证据确认实际为 Built-in；仅改文档，未动代码或工程设置 |
| 2026-06-20 | PMX M3.5 服装碰撞体 | batch 生成/导出通过，待 App 运行复测 | 第三轮收敛过强碰撞：Skirt 专用下半身碰撞体，Hair/Breast/Other 不绑定身体碰撞；`components=3 chains=37 colliders=6`，裙摆粒子半径 `0.012`；`ExportMe` 已更新 `Build/PmxModels/丽塔.me` |
| 2026-06-20 | lilToon shader 换行假脏修复 | 静态验证完成 | `.gitattributes` 为 `Assets/MATE ENGINE - Shaders/jp.lilxyzw.liltoon-1.8.5/Shader/*.shader` 增加 `text eol=crlf` 路径级例外，`git check-attr` 验证命中 |
| 2026-06-20 | PMX M4 高保真 HoYo 复刻尝试（UTS2 多轮） | 经验沉淀，`.me` 路线保留 | 多轮 App 验收逐一定位并修复发紫/脸碎裂/偏暗偏素/后处理失效等根因；现象与解法详见本页上方 M4 踩坑表。结论是 UTS2 不适合作为高保真 HoYo 语义复刻方案，但 clean-baseline、`PmxStyleConfig` 和 `.me` 打包仍保留 |
| 2026-06-20 | 渲染风格架构设计 + 风格解耦 | VRM 架构已归档，风格解耦保留 | "通用 VRM + App 内置可切换渲染风格"备选路线已并入 ADR-0008，并于 2026-06-21 归档（复盘见 ADR-0009）；`PmxStyleConfig` 解耦落地并服务当前 `.me`/UTS2 生产路径；旧设计文档已从当前工作树删除 |
| 2026-06-20 | lilToon shader 换行假脏 index 清理 | 已完成 | `git add --renormalize` 配合 `.gitattributes` 的 `eol=crlf` 例外，消除 58 个 vendored lilToon shader 的 index 行尾 mismatch；`git status` 不再显示该批假脏 |
| 2026-06-21 | 回退后文档一致性审查 | 已完成 | ADR-0008、`Docs/ARCHITECTURE.md`、`PROGRESS.md` 与 `MEPmxPipeline` 注释同步到 `.me` 唯一生产路径；确认 `Docs/RENDER_STYLE_DESIGN.md`/`Docs/PMX_TO_VRM.md` 不在当前工作树 |
