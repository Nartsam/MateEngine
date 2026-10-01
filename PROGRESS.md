# MateEngine Progress

## 说明

本文档维护项目的当前进度、任务列表。执行时先完成“正在处理”下堆积的任务，再处理其他项。阶段性进展完成后务必同步更新本进度文档。

- 上次更新：2026-10-01（Image Texture alpha 语义修正：寻梦者头发亮斑、大月下头巾长期偏差一并解决；描边按图集修正；用户实机测试通过）

## 当前状态

当前仓库是 MateEngine 个人 fork，处于二次开发早期整理阶段。Steam 依赖移除和绿色便携化已完成代码改造。

当前分支：`main`

当前 Git 状态要点：

- Steamworks.NET 包、Steam API/插件/UI 脚本已移除。
- 新增 `Assets/Editor/BuildScript.cs`（命令行构建）、`Assets/MATE ENGINE - Scripts/Portable/`（便携路径管理）。
- 新增嵌入包 `Packages/com.unity.addressables/`，修复运行时默认访问 `Application.persistentDataPath`。
- `Tools/PortableRuntime/` 原生入口已正式接入构建；启动前将 Unity 的 LocalLow 查询和本进程 TEMP/TMP 转到软件目录，移除 `-cache-path`、`Assets/boot.config`、主动缓存初始化和全部退出清理（ADR-0014）。
- 本地 Addressables group 已关闭 `Use Asset Bundle Cache`。
- 启动完成后的菜单启动音效已禁用；默认 UI 语言已改为简体中文。
- Unity 内置 Splash 已关闭；新增项目内加载场景作为首屏。
- 右键径向菜单打开后固定在展开瞬间的位置；“设置/使用 LLM 对话”按钮位置已交换；Chibi/大头缩身功能已屏蔽。
- 角色主 Model 根节点默认位置上移到 y=-0.74，保留透明窗口、相机和窗口尺寸不变，降低踮脚类动作底部裁切风险。
- 新增 `.gitattributes`，固定 Unity 文本资产和源码使用 LF，降低 Windows 构建/打开 Unity 后的假脏状态。
- 已补齐嵌入 Addressables 包中被旧 `.gitignore` 误忽略的 `Build` 子目录源码；`.gitignore` 的 Unity 生成目录规则已限制为仓库根目录。
- 托盘右键菜单已全部汉化为简体中文。
- 设置面板 X 按钮改为隐藏窗口到托盘（不再退出）；双击托盘图标恢复窗口；只有右键菜单"退出"才真正退出。
- 设置面板主滚动列表通过 `SettingsMenuScrollBoundsLimiter` 在运行时动态适配实际内容高度：测量最底部元素（Delete AI History）→ 调整 VLG `padding.bottom` → CSF PreferredSize 自然适配 Content 高度 → 强制 Clamped + 滚回顶部。无需手动调整 Content SizeDelta 或禁用 CSF。
- 设计文档为 `Docs/ARCHITECTURE.md` 与 `Docs/DECISIONS_RECORD.md`；PMX 渲染复刻另有 `Tools/PmxPipeline/README.md`（操作参考）与 `Docs/plans/2026-09-10-blender-graph-transpile.md`（实测结论）。`Docs/RENDER_STYLE_DESIGN.md`、`Docs/PMX_TO_VRM.md` 已随 VRM+RenderStyle 路径归档，不在工作树中。
- `Assets/PmxImported/`、`Build/PmxModels/` 等 PMX 生成物是本地忽略产物；当前工作树中可能保留历史 `.me`/`.vrm` 验证输出，不作为仓库源状态或生产路线依据。

## 正在处理

### 主程序 Bloom 接入（已实现，用户实机观感可接受）

用户已确认仅对节点图转译 PMX 启用，保留原 alpha，外圈辉光接入后看实机再定（ADR-0015）。
`MEModelBloom` 已接入独立 HDR 模型渲染与主相机 UI 前合成，复用现有 `.me` 的 LUT 与线性
输出开关，无需重导出现有丽塔/寻梦者。复用设置面板已有 Bloom 开关并保留保存状态；关闭
仍做线性混合与 Filmic。转译模型使用新合成时停用旧 Unity Bloom，普通模型恢复旧路径。

合成写回主相机时的两处处理（2026-09-25 验收后补上）：

- 先按合成结果的覆盖率（alpha ≥ 0.5）给模型补写主相机深度，再写颜色。之后由主相机绘制的
  光环、舞蹈拖尾、粒子、食物、悬停特效仍被模型遮挡，位于模型前方时照常可见。
- 输出按桌面窗口的预乘 alpha 合成：视图变换前还原未预乘颜色，之后乘回覆盖率。边缘与材质内
  做视图变换的旧路径更接近，边缘辉光随覆盖率衰减。离线差分沿用原公式，九角色成绩不受影响。

**同日修掉整体偏暗的主因（与 Bloom 无关）**：`MEModelDirections` 把日光方向按固定世界方向
写入，而主场景的 `Model` 节点绕 Y 转了 180°，日光相对角色被转到背面。丽塔同一姿势在主场景的
不透明区平均显示亮度为 0.276，空场景为 0.369；改为随模型根节点旋转后主场景为 0.370。旧 `.me`
存的数值不变，无需重导出，只需新版主程序。

**2026-10-01 修掉低头时的「阴阳脸」**：脸部阴影是硬边判断且按日光左右翻转，鼠标在脸下方时头部
低头，半张脸进入阴影，鼠标稍左右移动就整块换边。按用户选择，脸部 SDF 只认头部左右转向，去掉
俯仰与侧倾（ADR-0015）；身体不受影响，头部正视时与 Blender 逐位相同。验证：低头 30° 时脸部朝向
偏移 0°，左右转 30° 时转动 30°，其余各项数值不变。

**同日修掉寻梦者头发的大块亮斑与「缺一块」**：头发的「高光」贴图 98.6% 像素 alpha 为 0，
发丝纹理全在 alpha 里，Blender 在 Alpha 未接线时输出 rgb·a，转译器却直接取 rgb，整片头发成了平滑
亮斑且底色偏亮。转译器已按 Blender 语义修正（`Tools/PmxPipeline/verify/probe_image_alpha.py` 实测），
寻梦者已重导出；丽塔的材质不受影响，无需重导出。

**同日修掉丽塔腿部轮廓的零星橙线**（预设自身问题，Blender 里同样存在）：衣物描边采样 Body 光照图，
腿部 UV 却在 Body_Parts 图集，约 5% 的描边碰巧落进橙色皮肤档。导出时按底层图集把这些描边改挂到预设
备有的 `edge_clothes2`；寻梦者预设没有备用描边，改用同一描边着色器换 Leg 光照图的变体（ADR-0013）。
两个角色已按原路径覆盖重导出 `Build/PmxModels/*_transpiled.me`，用户实机测试通过。

丽塔、寻梦者与同公式的独立参考合成误差 0/255、alpha 误差 0。关 Bloom 时边缘部分覆盖像素
对旧路径的平均误差为 2.38/255、3.41/255（原公式为 4.64、7.74）。抬起左臂后，身后辅助物体在
模型不透明处透出 0 px、空白处全部可见，前方辅助物体可见。菜单、开关、窗口遮挡、截图尺寸、
模型卸载、模型父节点旋转 180° 后光照不变（误差 0.03/255）与正式主场景的自动接入验证通过。
证据在 `Build/BloomValidation/{rita,dreamseeker}/`。
`Build/MateEngine.exe` 已按以上修复重建；独立数据目录的实际启动分别记录 `bloom=True/False`，
测试数据已清理，原自用 ZIP 仍为 09-22 版本。用户已确认修复后实际窗口的静态观感可以接受。
App 实际窗口与 Blender 之间尚无逐像素对比，不能据此断言完全一致；离线差分、主程序与同公式
参考合成的对照及坐标系不变性检查分别提供既有证据。

RTX 4070、512² / 4× MSAA 的 20 帧同步渲染（含单像素回读与补深度）约为：丽塔开/关
0.77/0.55 ms，寻梦者 0.87/0.69 ms（逐次波动约 ±0.2 ms）。这不是完整 App 帧耗；实际窗口默认 1536×1024，Ultra 画质
为 8× MSAA，真实帧耗与显存占用未测。真实桌面的动画、拖拽/穿透与贴窗遮挡仍需交互验收。

### PMX 渲染复刻：节点图转译（当前主线）

目标是让「PMX 模型 + 配套 `.blend` 渲染预设」在本软件中呈现与 Blender/EEVEE 一致的观感。
路线是**把预设的节点图整体转译成 HLSL**，不是用通用卡通着色器调参（背景见 ADR-0011，
三条参数拟合路线均已证伪）。

- **怎么跑、文件都干什么**：`Tools/PmxPipeline/README.md`
- **全部实测结论与分阶段记录**：`Docs/plans/2026-09-10-blender-graph-transpile.md`
- **集成决策**：ADR-0012（离线工具定位；只有 bloom 必须改 App 渲染管线）

Phase 0–3.6 已完成并通过用户肉眼验收。九个角色、正背两视角的当前成绩：

| 角色 | 材质 | front | back |
|---|---|---|---|
| 小蕾耶拉 | 28/28 | 0.9991 / 1.44 | 0.9991 / 1.19 |
| 灯 | 31/31 | 0.9983 / 1.73 | 0.9983 / 1.08 |
| 特工丽塔 | 30/30 | 0.9980 / 1.84 | 0.9980 / 1.63 |
| 灯-战损版 | 31/31 | 0.9986 / 1.89 | 0.9987 / 3.52 |
| 李素裳 | 29/29 | 0.9983 / 1.90 | 0.9984 / 1.47 |
| 蕾耶拉 | 34/34 | 0.9983 / 1.90 | 0.9983 / 1.37 |
| 大格蕾修 | 34/34 | 0.9988 / 2.51 | 0.9989 / 2.00 |
| 寻梦者 | 32/33 | 0.9982 / 2.34 | 0.9982 / 1.68 |
| 大月下 | 34/34 | 0.9984 / 1.63 | 0.9991 / 1.85 |

（轮廓 IoU / 交集内平均 ΔE76。对照：UTS2 基线 0.9677 / 11.00。寻梦者少的那一个是预设库里
的遗留材质，引用了一张失效图片，且没挂在网格上。）

**Phase 4 的离线导出部分已接通**：`export_model.ps1` 显式指定 PMX、Blend 和 `.me` 输出，
保存转译材质、LUT/贴图、描边网格，并附带随头骨更新 SDF 方向的运行时组件。丽塔与寻梦者
通过实际 AssetBundle 重载与渲染一致性验证。操作见 `Tools/PmxPipeline/README.md`。
离线工具按 ADR-0013 只打包模型资源；2026-09-23 主程序已接入 Bloom、线性合成与 UI 隔离，
本期保留原 alpha（ADR-0015）。本机 `Build/MateEngine.exe` 已重新构建并通过丽塔实际导入
及 Bloom 开关状态检查。其他旧程序需完整更新运行文件，才能执行新运行时逻辑。

寻梦者、大月下两行为 2026-10-01 修正 Image Texture 的 alpha 语义后重测（其余七个角色逐位不变）：
直通 alpha 的贴图在 Alpha 输出未接线时，Blender 输出 rgb·a，转译器原先直接取 rgb。大月下 `头巾`
长期未收敛的 ΔE ≈ 8 即源于此（正/背 8.51/7.53 → 0.64/1.73），寻梦者头发 5.83/3.83 → 2.15/0.80。

**仍未收敛的一处**（不阻塞集成，细节见计划文档 Phase 3.6 末尾）：蕾耶拉 `裙带` 的金粉颗粒
比基准更多更亮。噪声与哈希已逐点验证无误，问题在下游。

### PMX 几何与物理管线（已可用，随渲染路线一起维护）

`Assets/Editor/PmxPipeline/` 负责解析 PMX、建网格/骨架/BlendShape、配 Humanoid Avatar、
把 MMD 刚体关节近似成 DynamicBone，并打包 `.me`。这部分与材质路线无关，三次材质路线更替
都没有动它。踩过的坑见下方「PMX 几何与物理踩坑表」。

遗留项：

- M3.5 裙摆碰撞体需要在 App 内运行复测。
- 材质 morph 类表情（照れ 腮红等，非顶点 morph）需单独方案。

## 下一步

- **PMX 渲染复刻 Phase 4 实机验收**：静态观感用户已接受；动画、拖拽/穿透与贴窗遮挡等交互仍待检查。外圈辉光按实机效果后续再定。
- M3.5 裙摆碰撞体 App 内运行复测。
- 处理材质 morph 类表情（照れ 腮红等，非顶点 morph，需单独方案）。

## 已完成

### 自用发行包与本地产物整理

- 原生便携入口改造已提交为 `5010c432`。基于该代码对应、已验证的现有构建制作
  `Build/Releases/MateEngine-Windows-x64.zip`，打包命令和更新步骤见 `BUILD_MANUAL.md`。
  包内只含运行文件和许可说明，不含个人 `UserData`、开发工具、实验输出；当前含默认 Zome，限个人使用。
- 清理了 134 个文件、2 个空目录，共 22,261,714 字节（约 21.23 MiB）：已归档失败路线的
  `Logs/`、旧 Burst 调试目录、一次性原生调试产物及早期临时渲染图/日志。
  详细删除清单为 `Build/cleanup-report.json`，当前便携性验收所引用的核心证据仍在 `Build/PortableAudit/`。
- 按用户明确选择保留六份旧丽塔模型及现有设置；同时保留当前两份转译模型、导出资产、
  九角色/四视角验收数据、Bloom 标定 EXR、无 bloom 基线、舞蹈资源、Python 环境和 Unity/LLM 依赖。
  `Library/MateEngineUserData/Cache/LLMUnityBuild` 含构建时暂存的依赖，不能按普通无用缓存删除。

### PMX 几何与物理踩坑表

这些是 PMX → Unity 的几何/骨骼/物理转换踩过的坑，与材质路线无关，**当前仍然有效**。

| 现象 | 根因 | 解决 |
|---|---|---|
| 模型左右镜像 | 仅取负 Z + 反转缠绕 = 镜面反射 | MMD→Unity 绕 Y 180°（同时取负 X 和 Z）+ 保持原始缠绕 + scale 0.08 |
| 腿不弯/部位悬空 | 蒙皮权重落在 付与/D 变形骨（仅靠 inherit 驱动，是控制骨的兄弟而非子级） | `BuildSkinRemap`：把 inherit 权重重定向到源控制骨 |
| 拖动时服装粘连/拉伸 | ① DynamicBone `m_Inert` 过高，布料滞留世界空间；② PMX 编辑器删面后残留顶点被重指到 `操作中心`(bone0) | ① `m_Inert` 降到 ~0；② `FindOrphanVerts` 丢弃主权重落在非变形骨的面 |
| 跳舞口型/表情不动 | `UniversalBlendshapes` 只驱动 VRM；舞蹈片段把形态键曲线绑在 `Body` 路径上 | SkinnedMeshRenderer 放到 root 下的 `Body` 子物体；保留日文形态键名走 `AvatarDanceShapeConverter` 的 bypass |
| 头发抖动 / 过飘 | DynamicBone 参数欠阻尼或欠刚度 | 物理手感主观，建议用 Play 模式实时调参再回填 |
| Humanoid Avatar 构建失败 | 映射 `Hips = 下半身` | `Hips = 腰`（腰是脊柱与双腿唯一共同祖先）；`LowerArm = ひじ`、`Hand = 手首`，捩骨作为未映射中间骨保留 |

> 注意：`AvatarDanceShapeConverter` 对带 MMD 形态键（まばたき/あ/い/う/え/お…）的模型走
> `bypassForThisAvatar` 直驱路径，**保留原始形态键就能让舞蹈口型原生工作**——这正是当初
> PMX→VRM 丢口型的根因。另外，抽查的内置舞蹈片段都没有形态键曲线，若用户导入的舞蹈也无
> 面部 morph 轨，任何模型侧修复都产生不了口型。

### 材质路线的三次失败（勿重试）

| 路线 | 结论 | 复盘 |
|---|---|---|
| PMX→VRM + MToon / HoyoToon | HoyoToon 靠数据挖掘网格的顶点色重建切线，PMX 没有 | ADR-0009 |
| UTS2 参数微调 | 目标是 LightMap/SDF 贴图驱动的多通道语义，UTS2 不吃；且离线盲烤无预览 | ADR-0010 |
| lilToon 参数调优 | 同因：通用着色器只暴露二十几个旋钮，拟合维度不足，理论上无法收敛 | ADR-0011，产物归档在分支 `archive/liltoon-npr-tuning` |

三条路线共享同一个错误前提：**把 `.blend` 预设当成一组材质参数**。实际它是 712 个计算
节点的图。`PmxStyleConfig` / `PmxMaterialMapper` / `styles/*.style.json` 作为 UTS2 兜底
保留，不得破坏，但不再是生产路径。


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
- 2026-09-22 基线监测证实：携带旧 `-cache-path` 参数仍在启动和退出时创建 LocalLow 目录，之后才被清理；旧“全程重定向”结论已撤回。
- 用户确认采用 Windows 原生入口：`Tools/PortableRuntime/PortableMain.cpp` 在同一进程内、`UnityMain` 之前重定向 UnityPlayer 的 LocalLow 查询到 `UserData/Unity`；仅为当前进程设置 `TEMP/TMP=UserData/Cache/Temp`。
- `BuildScript.BuildWindows` 自动使用本机 MSVC/Windows SDK 编译入口并保留原 Unity exe 的 12 项 Windows 资源（图标、版本、DPI/UAC 清单）；发行版无需这些构建工具。入口安装失败则构建失败，不留下可运行的原始 Unity exe。
- 已移除 `ConfigureUnityCache()`、所有空目录清理、退出 PowerShell、无效 `Assets/boot.config`；`.bat` 仅为便利启动方式，直接 exe、自启等无需额外缓存参数。
- 正式构建正常退出、强制终止，以及只复制发行文件到含中文/空格目录后无参数启动均通过；模型实际加载且 LocalLow 目标目录全程零创建/删除事件。不会把旧清理掩盖的“退出后不存在”当成不创建。
- 业务代码的数据写入（LLM、DiscordRPC、截图等）收敛到 `UserData/`；`--datadir` 等参数限制在 `UserData/` 子路径内；旧 `PlayerPrefs` 只读迁移。Unity 原生路径的残留问题见 ADR-0014。

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
| 当前变更尚未提交 | 待提交 | 主程序 Bloom 接入、09-25 验收修复及文档复核仍在工作树中 |
| 默认 Avatar 分发风险 | 持续关注 | Zome 不可随自制 build 分发 |
| Unity 版本敏感 | 持续关注 | 必须使用 `6000.2.6f2` |
| 外部写入审计 | LocalLow 修复已验收 | 正式 Windows 入口通过启动/正常退出/强制结束/独立目录测试；全盘、注册表和原生崩溃转储尚未完整审计，见 ADR-0014 |
| 开机自启动写注册表 | 已知例外 | 用户主动启用/关闭时写入或删除 `HKCU\...\Run` |
| 多实例启动器可执行文件名 | 待修复 | `BuildScript` 输出 `MateEngine.exe`，但 `LaunchMateEngineInstance.cs` 默认值与主/Update 场景序列化值仍为 `MateEngineX.exe`；若使用多实例按钮会找错 exe |
| 窗口贴边组件停用时空引用 | 正常退出时复现，待修复 | 旧 `Build/PortableAudit/final-normal.log:83–90` 已记录 `AvatarWindowHandler.OnDisable → ClearSnapAndHide → SetOtherQuadsActive` 的 `NullReferenceException`（源码第 805 行）；09-23 的 `Build/BloomValidation/app-final-on.log`、`app-final-off.log` 再次在退出时记录同一异常，未在 Bloom 任务中扩展修复范围 |
| lilToon 构建预处理异常 | 最终构建成功，异常未处理 | 09-25 的 `Build/BloomValidation/build.log:1763` 记录 `Scene file not found: ''`，随后报 `Optimization failed`；构建继续并成功生成 Player，不能描述为无异常构建 |
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
| 2026-09-25 | Bloom 修复报告复核与用户静态观感确认 | 源码及既有产物对照完成，静态观感验收通过 | 核对补深度、预乘输出、日光根节点坐标系及旧包兼容性；双角色 JSON 的误差、辅助物体遮挡、旋转不变性和耗时与报告一致，构建与开/关启动日志相符。参考合成使用同一算法，不能证明实际窗口与 Blender 逐像素一致；UI、遮挡、尺寸及卸载验证为离屏/调用路径检查，真实交互和实际尺寸性能仍待验收。按用户本次反馈修正残留“观感待验收”措辞，并记录边缘仍有误差、深度近似与 pass 3/4 均清零透明 RGB。此轮未重跑 Unity；仅同步文档，改动尚未提交 |
| 2026-10-01 | Image Texture alpha 语义 | 九角色离线差分、寻梦者重导出与用户实机测试通过 | 探针实测 STRAIGHT 图 Alpha 未接线时 Color=rgb·a（(0.8,0.6,0.4)、a=0.25 → (0.2,0.15,0.1)），接线或 Non-Color 为原 rgb。转译器修正后正面差分：七个角色逐位不变；寻梦者 2.67→2.34（头发 5.83→2.15），大月下 3.01→1.63（头巾 8.51→0.64）；背面 寻梦者 2.38→1.68、大月下 5.51→1.85。寻梦者 Bloom 验证全部通过，歪头渲染中发丝高光正常 |
| 2026-10-01 | 描边按底层图集修正 | 两角色重导出、验证与用户实机测试通过 | 腿部描边按 Body 光照图采样时 59/1156 面落进橙色档，按 Body_Parts 为 0。导出时改挂 `edge_clothes2` 共 4862 面；bundle 往返误差 0，Bloom 验证全部通过；主场景渲染仅腿脚 118 px 变化，橙色 110 px → 0。寻梦者 24/2187 面同类问题，预设无备用描边，改用换 Leg 光照图的变体 2884 面，渲染仅腰腿 991 px 变化、橙色 244 → 44 |
| 2026-10-01 | 脸部阴影去掉头部俯仰 | 两角色验证与构建通过，待用户实机复看 | 用户反馈低头时半张脸阴影且随鼠标左右微动整块换边。原因是预设脸部 SDF 的硬边阈值与按日光左右翻转，叠加鼠标跟踪的低头（最多 30°）。按用户选择把 FRONT/RIGHT 投影到根节点水平面；低头 30° 朝向偏移 0.000°、左右转 30° 转动 30.000°，其余验证数值与上一轮逐位相同 |
| 2026-09-25 | 主程序 Bloom 验收与修复 | 两角色验证、构建与实际启动通过，用户观感可接受 | 审查发现辅助物体失去模型遮挡、边缘在预乘值上做视图变换两处问题并修复：补写模型深度、运行时预乘输出。用户实机反馈仍明显偏暗，查明日光方向按世界方向写入、被主场景 `Model` 节点的 180° 旋转转到角色背面（主场景平均亮度 0.276 vs 正确 0.369），改为随模型根节点，修后 0.370，新增父节点旋转不变性检查（误差 0.03/255）。验证新增边缘对旧路径误差（丽塔 4.64→2.38、寻梦者 7.74→3.41，alpha 误差 0）与抬臂姿势下的辅助物体遮挡（透出 0 px）；原有检查照常通过。审查时一度认为编辑器 `GetComponent` 缺失返回“假 null”使 `??` 失效，经 6000.2.6f2 编辑模式与 Play Mode 实测均为真 null，原写法无误；仍改用 `TryGetComponent`，并改为先校验再拆除旧绑定、失败不再每帧重试。自动验证未覆盖真实桌面交互，静态观感由用户另行确认 |
| 2026-09-23 | 转译 PMX 主程序 Bloom | 双角色/主场景离屏验证、Windows 构建及实际加载通过 | 丽塔/寻梦者：512²、4× MSAA 同采样可见像素平均 RGB 误差 0/255，alpha 最大误差 0；开关、半透明 UI、辅助 Renderer、深度遮挡、尺寸变化和卸载恢复通过。正式主场景的真实菜单出图通过；新版 App 的独立配置分别记录 `bloom=True/False`，已清理测试进程与数据。证据 `Build/BloomValidation/rita/`、`dreamseeker/`、`build.log` 与 `app-final-*.log`；遗留缺脚本警告及退出空引用仍在，真实桌面观感/交互尚待用户验收 |
| 2026-09-23 | 接手文档、关键实现与已有证据核验 | 静态核验完成 | 阅读项目规则、进度、架构、关键 ADR、构建说明与 PMX 管线文档；核对 Unity 版本、Built-in 配置、暂停调色状态、模型加载入口、原生便携入口及已有发行/运行记录。确认多实例 exe 名错配仍在，补记历史 `AvatarWindowHandler` 停用阶段空引用；未运行 Unity 构建、App 或渲染差分，历史验收结果不作为本次重新实测 |
| 2026-09-22 | 自用发行包与本地清理 | ZIP 完整性及清理范围核验通过 | 现有构建打包为 212,247,860 字节 ZIP；287 个文件逐项与源文件 SHA-256 一致，文件清单不含 UserData/开发产物，摘要在 `Build/Releases/release-manifest.json`。清理仅涉及 Git 忽略产物；核实旧代码已有归档分支、空 Unity 文件夹 GUID 无引用并连同其 meta 删除；保留用户指定的旧模型/设置及后续 Bloom 所需数据。未重复运行 Unity 构建 |
| 2026-09-22 | Windows 原生便携入口 | 正式构建与运行通过 | CDB 定位原生崩溃报告启动检查，旧 `-cache-path` 与 Diagnostics 开关不能阻止路径创建；新入口重定向 LocalLow 和进程 TEMP/TMP，移除全部外部清理。正式构建正常退出、强制结束、含中文/空格的独立发行目录无参数运行全程零目标目录事件；模型加载成功；12 项 exe 资源哈希一致。证据 `Build/PortableAudit/`，含 `build-final.log` 与 `final-*.json`；临时进程及测试副本已清理。未作全盘/注册表或真实原生崩溃转储验收 |
| 2026-09-22 | 无 bloom 导出模型的 App 导入 | Windows Mono 构建及实际加载通过 | `BuildScript.BuildWindows` 成功；新版 App 使用独立 `--datadir PmxExportSmoke` 自动加载丽塔 `.me`，角色库生成对应记录，无模型加载或着色器错误。保留 `Build/PmxExport/app-build.log`、`app-smoke.log` 与导入记录；测试进程和独立数据目录已清理。主场景仍输出历史 Steam/Upload 缺脚本警告，未在本任务中改场景；完整动画/透明窗口观感仍需人工验收 |
| 2026-09-22 | 独立 PMX/Blend → `.me` 工具 | 两角色导出与 bundle 往返验收通过 | 丽塔、寻梦者：材质/贴图色彩空间/网格/形态键一致；256² RGBA 平均误差 0/255；头骨引用、转头更新和共享材质隔离通过。寻梦者未使用的失效 `hair` 材质正确跳过。CLI 拒绝仓库外输出与缺失输入；无 bloom，App 验证另记 |
| 2026-09-22 | 接手文档与实现核验 | 静态核验完成 | 核对主线入口、`GradeEnabled = false` 与九角色正背共 18 份已有差分 JSON；修正架构文档的调色启用表述、App 合成验收边界和色差摘要。转译材质/描边仍仅在对照渲染时装配，正式 `.me` 与 App 集成待 Phase 4。本次未运行 Unity 构建或 App |
| 2026-09-22 | PMX 渲染：管线扩到九个角色 | 通过（用户肉眼验收） | 正背视角轮廓 IoU 按四位小数为 0.9980–0.9991，交集内平均 ΔE76 为 1.08–5.51（大月下背面为最大值）。换角色暴露并修掉 8 个缺陷：`Mix` 只支持 RGBA（大月下 20 个材质退回原材质）、`HASHED` 错映射成「不透明 + 直接写 alpha」（战损灯头发被打成筛子，IoU 0.9616）、没复现 EEVEE 的「Show Backface」深度预通道（灯的长发鬼影）、导出器从不导出输出插槽默认值（灯头顶高光变黑斑）、正交相机视线向量、`Noise Texture` 4D、RGB Curves 复合顺序、失效贴图引用。另补齐 Blender 报告的 AO/SSR/采样数记录，并新增 `Tools/PmxPipeline/verify/` 逐点核对哈希与噪声。详见计划文档 Phase 3.5/3.6 |
| 2026-09-22 | 管线外部路径改为按本机解析 | 已验证 | Blender/Unity/模型库改走「命令行 > 环境变量 > 本机配置 > 自动探测」，不再写死默认值；仓库内路径本来就由 `$PSScriptRoot` 推出、跟着仓库走。移走本机配置后实测能自动探测到 Blender 与 Unity，模型库给出可操作的报错 |
| 2026-09-19 | PMX 渲染：Phase 0–3 完成（转译器 / bloom / 描边） | 通过（用户肉眼验收） | 丽塔四视角 IoU 0.998、平均 ΔE 1.6–1.9。bloom 靠实测 EEVEE 的点扩散函数复刻；描边直接搬 Blender 几何节点求值出的外壳；4× 超采样对齐 Blender 的着色超采样。详见计划文档 Phase 0/1/2.5/3 |
| 2026-09-10 | PMX 渲染：重新评估复刻路线，改走节点图转译 | 分析完成，实施未开始 | lilToon 路线（迭代到 Phase 2.18）与 UTS2 路线同因失败：目标是 `.blend` 预设那张 712 个计算节点的图，通用卡通着色器只暴露二十几个可调项，**参数拟合维度不足、理论上无法收敛**；且旧导出脚本从不导出节点连线，算法在第一步就已丢失。**决定性发现：该预设是无光照纯函数**（Color 直连 Surface，实测与 Emission 逐位相同；唯一的灯 energy=0，World 为空；全量枚举确认零 EEVEE 光照/屏幕空间节点），因此可整体转译成 HLSL 做到精确复刻而非近似。已核实：对模型的硬要求仅「一套名为 `UVMap` 的 UV + 材质名匹配」，丽塔 PMX 全部满足，**不需要顶点色**（ADR-0009 的顶点色障碍不适用于本路线）。全部未提交产物已归档到分支 `archive/liltoon-npr-tuning`。详见 ADR-0011 与 `Docs/plans/2026-09-10-blender-graph-transpile.md` |
| 2026-06-22 | PMX 渲染：UTS2 烤法微调追 HoYo（❌失败已回退） | 两轮越改越差，全部 `git checkout` 回退到基线并重烤兜底 | B1（材质恢复高光）→ 腿变"油脂圆柱"（UTS2 柔和镜面≠官方硬边高光）；硬卡通 pass（feather→0.02/加深阴影/MetalMap高光/描边×2/brightness1.0）→ 惨白看不清+头发碎纹理。**根因：目标是 LightMap/SDF 贴图驱动，UTS2 不吃；且离线盲烤无预览无法收敛。UTS2 参数微调路线作废，勿重试。** 基线烤法保留为兜底（偏素但不崩）。下一步另起：lilToon（先小场景实测再上）。详见 ADR-0010 失败路线。 |
| 2026-06-22 | 右键径向菜单按钮位置与角色底部裁切余量 | 静态验证完成，待 Unity 运行复核 | 主场景 Buttons[0]/[4] 交换 Settings/Chat 引用；Model 根节点 y -0.8 -> -0.74；未改窗口尺寸、相机参数或按钮事件。 |
| 2026-06-18 | Steam 依赖移除后构建 | 已通过 | 零编译错误 |
| 2026-06-18 | 外部写入静态审计 | 已完成 | 剩余命中为编辑器/移动端路径、旧数据只读迁移或用户主动功能 |
| 2026-06-19 | 便携化代码改造 | 旧实现，部分已被替代 | Player.log 关闭与 Addressables patch 保留；缓存参数和 PowerShell 方案的错误结论已撤回，见 ADR-0014 |
| 2026-06-19 | LocalLow 空目录排查（多轮迭代） | 旧结论已撤回 | 当时未充分验证创建时机，误将 `-cache-path` 当成原生重定向；2026-09-22 的事件监测与原生调用栈定位见 ADR-0014 |
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
| 2026-06-21 | 运行时可切换渲染风格（步骤2）落地 | 代码完成+batch 编译通过，待 App 运行验证 | 新增 `RenderStyleManager`（专属全局 PostProcessVolume 经相机既有 PostProcessLayer 生效，内置 默认/鲜艳/柔和）+ `RenderStyleDropdownHandler` + `RenderStyleSceneSetup`（克隆 Graphics 下拉接线，已 batch 应用入主场景，scene 完整性已核验）；`SettingsData.renderStyle` 持久化；`VRMLoader.FinalizeLoadedModel` 加钩子；`PmxModelRenderProfile` 后处理职责退役（Manager 存在时 no-op，`PmxMeshBuilder` 停挂）。纠正"相机无 PostProcessLayer"误判。HoYo 着色器仍 deferred。见 ADR-0010 |
| 2026-06-21 | 渲染风格三次反馈：grade 染菜单根因 + 暂停 + 布局修正 | 代码改动完成，待 App 复验 | ① 根因：全场景仅 1 相机，全屏 grade 同时染模型与 Screen-Space-Camera 菜单 → 全屏 grade **暂停**(`GradeEnabled=false`)，待选模型专属途径（模型专用相机/路线B）；② `鲜艳` contrast>1 压黑 → 改 contrast=1 靠 saturation+brightness；③ 布局正解：下拉放 GENERAL 末尾新行 (-104.9,-360) + GENERAL 之后 section 整体下移 120，Limiter 自动加长滚动区（前两次放组内空位撞 FPS/AUDIO）；④ 回答"氛围光"=3 盏场景光(`ColorController`)，只照模型不碰 UI、能提亮但不能提贴图饱和。模型在 layer10、加载 .me 未重层。见 ADR-0010 三次反馈 |
| 2026-06-21 | 渲染风格二次反馈修复 + 鲜艳路线分析 | 代码+资产改动完成，待 App 复验 | ① 坐实"切风格无变化"根因：UTS2 烤进 `_Use_BaseAs1st=1` 致改阴影色被忽略+无饱和旋钮→材质层调不动鲜艳；② 下拉移到 GENERAL 右列空位 (219.85,-260) 避开 AUDIO 重叠；③ 字体多 atlas 烤入（776→874 字 0 缺失，精确字形+`m_IsMultiAtlasTexturesEnabled`）；④ Blendshape 用 `PmxBlendshapeDump` 取 59 真实 morph 名建字典，显示 `中文 / 原文`。**鲜艳度路线分析**写入 ADR-0010（推荐路线 A：alpha-preserving image effect）。见 ADR-0010 二次反馈 |
| 2026-06-21 | 渲染风格 App 验证反馈修复（4 项） | 代码+资产改动完成，待 App 复验 | ① **透明被破坏**根因坐实：全屏后处理不保留分层窗口 alpha（切风格出现灰色矩形）→ `RenderStyleManager` **移除全部后处理**，改纯逐材质 UTS2 叠加（亮度/阴影对比/高光，可逆，不碰帧缓冲）；② 下拉标题 `画质`→静态 `渲染风格`（禁用其 LocalizeStringEvent）+ 间距下移到 y=-360；③ **方框乱码**：静态 SDF 回退字体 `Chinese (Simple)` 缺字形且无运行时 source font → 编辑器命令临时切 Dynamic 挂 TTF `TryAddCharacters` 烤入再转回 Static 清 ref（776→819 字，0 缺失，不打包 TTF）；④ Blendshape 日文 morph 名→简中（`BlendshapeNameLocalizer` 字典）。材质层不能做整屏饱和，更强鲜艳需 alpha-preserving image effect 或着色器（待定）。见 ADR-0010 修正 |
