# PMX → `.me` 转换操作手册（Runbook）

> **⚠️ 注意 (2026-06-21)**：VRM 导出路径（阶段 3–5）已归档，代码在 `archive/vrm-detour` 分支。复盘见 `Docs/DECISIONS_RECORD.md` ADR-0010。本文档现描述 **`.me` 管线**（阶段 0–2）及 §8 踩坑表（适用 UTS2 调试）。
>
> 状态：`.me` 管线已实现可用。VRM 部分已归档。
> 最后更新：2026-06-21

---

## 0. 这份文档是什么

把一个 PMX 模型（+ 可选 `.blend` 渲染预设）转换成**通用 VRM**的端到端操作流程。采用**分阶段 runbook**：每个可自动化阶段对应一条 Unity/Blender 命令，**阶段之间留人工介入检查点**——人或 AI Agent 都能照此严格执行，且当自动化处理不了（典型：物理穿模、材质偏差）时可以在 Unity 编辑器里手动修改后继续，而非从输入到输出的黑盒。

### 设计原则
- **不是黑盒**：每阶段产物都是可在 Unity 编辑器里查看/编辑的真实资产（prefab、SpringBone、材质、网格）。
- **可手动介入**：关键阶段后有"检查点"，列出该看什么、怎么手动改、改动如何被后续阶段保留。
- **易扩展**：新增/替换阶段 = 加一节文档 + 一个 batch 方法，不动其它阶段。
- **可封装**：需要一键时，用一个薄脚本顺序调用各阶段命令即可（见 §6）。

---

## 1. 输入 / 输出约定

| | 内容 | 必需 | 说明 |
|---|---|---|---|
| 输入 | PMX 模型 | **必需** | 网格/骨架/物理/形态键来源 |
| 输入 | `.blend` 渲染预设 | 可选 | **HoYo 游戏贴图（LightMap/MetalMap/Facemap/Body_Color）的来源**。当前 模之屋 模型这些图打包在 `.blend` 里、无散文件，故对仿 HoYo 效果**实际必需**；若模型的图本就是 PMX 旁散文件则可省 |
| 输出 | **VRM（主产物）** | — | 通用、可移植；物理=SpringBone；HoYo 贴图打包进 glTF + 元数据（阶段 3，待实现）|
| 输出 | `.me`（旧/可选） | — | 本项目专有 AssetBundle 格式。**保留但默认不作为主产物**，详见 §7 |

---

## 2. 前置条件

- Unity **精确 6000.2.6f2**（硬约束）。
- Blender：用 `D:\Program Files\Blender\3.6`（**不要用 5.0.1**：`模型预设导入.py` 插件在 5.0.1 有路径不匹配 bug）。
- 仓库内已内置 UniVRM（UniGLTF / VRM / VRM10），无需额外安装。
- 外部路径只通过 CLI 参数或本机 ignored 配置注入（不写死在代码里）。
- 不打开 DEV/InDev 场景；不改主场景除非该阶段明确要求。

---

## 3. 流程总览

| 阶段 | 名称 | 类型 | 入口 | 状态 |
|---|---|---|---|---|
| 0 | Blender 抽图（HoYo 贴图） | 自动 | `dump_render_preset.py` | 已实现 |
| 1 | 解析校验 | 自动 | `ValidateParse` / `DumpSkeleton` / `DumpSkinning` / `DumpOrphans` | 已实现 |
| 2 | 构建 prefab（网格/骨架/Humanoid/表情/物理/材质） | 自动 | `BuildModel` | 已实现 |
| **A** | **检查点：骨架/蒙皮/孤立面** | **人工** | Unity 编辑器 + Dump 日志 | — |
| 3 | 物理 → SpringBone | 自动 | （待实现，现为 DynamicBone） | **待实现** |
| **B** | **检查点：物理/穿模实时调** | **人工** | Unity Play + SpringBone/Collider Inspector | — |
| 4 | 材质：MToon 基底 + 打包 HoYo 贴图/元数据 | 自动 | （阶段 3 内，待实现） | **待实现** |
| **C** | **检查点：材质/风格调参** | **人工** | `styles/<模型>.style.json` 或 Editor 调 `*.mat` | 已实现（配置） |
| 5 | 导出 VRM | 自动 | （待实现 `BuildAndExportVrm`） | **待实现** |
| — | （旧）导出 `.me` | 自动 | `ExportMe` / `BuildAndExport` | 已实现 |
| 6 | 验收 | 人工 | App 加载 + 切风格 + 对比 `.blend` 参考 | — |

---

## 4. 逐阶段操作

> 命令均为 PowerShell；替换尖括号路径。`-modelName` 决定输出目录与风格文件名；不传则取 PMX 内部名。

### 阶段 0 — Blender 抽图（当 HoYo 贴图在 .blend 内时必需）
把 `.blend` 内打包的游戏贴图导出，并生成"材质→用哪几张图"的 JSON。
```powershell
& "D:\Program Files\Blender\3.6\blender.exe" --background --factory-startup --disable-autoexec `
  --python "<repo>\Tools\PmxPipeline\dump_render_preset.py" -- `
  --preset "<model>\丽塔未变身预设.blend" `
  --out "<repo>\Logs\pmx_render_preset.json" `
  --texture-root "<repo>\Logs\pmx_render_preset_textures"
```
产物：`Logs/pmx_render_preset.json` + `Logs/pmx_render_preset_textures/*.png`（含 LightMap/MetalMap/Facemap）。

### 阶段 1 — 解析校验（建议先跑，确认 PMX 可读 + 骨架健康）
```powershell
# 解析报告（材质/形态键/物理统计、口型形态键是否齐）
Unity ... -executeMethod MateEngine.PmxPipeline.MEPmxPipeline.ValidateParse -pmx "<pmx>"
# 需要时：骨架/蒙皮/孤立几何诊断
Unity ... -executeMethod MateEngine.PmxPipeline.MEPmxPipeline.DumpSkeleton  -pmx "<pmx>"
Unity ... -executeMethod MateEngine.PmxPipeline.MEPmxPipeline.DumpSkinning  -pmx "<pmx>"
Unity ... -executeMethod MateEngine.PmxPipeline.MEPmxPipeline.DumpOrphans   -pmx "<pmx>" -dist 3.0
```
（`Unity ...` = `& "D:\Program Files\Unity\Hub\Editor\6000.2.6f2\Editor\Unity.exe" -batchmode -quit -projectPath "<repo>" -logFile "<repo>\Logs\<name>.log"`）

### 阶段 2 — 构建 prefab
解析 PMX → 网格 + 骨架 + Humanoid Avatar + BlendShape + 物理 + 材质，生成 prefab 到 `Assets/PmxImported/<模型名>/`。
```powershell
Unity ... -executeMethod MateEngine.PmxPipeline.MEPmxPipeline.BuildModel `
  -pmx "<pmx>" -preset "<blend>" -blender "D:\Program Files\Blender\3.6\blender.exe"
```
> 传 `-preset` 会在构建内部自动跑阶段 0。日志关注：`Materials: ...`、`DynamicBone: components/chains/colliders`、`humanoid: valid mappedBones`。

### 检查点 A — 骨架 / 蒙皮 / 孤立面（人工）
看什么 / 怎么手动改：
- **关节别扭 / T-pose 不对**：查 `Assets/Editor/PmxPipeline/PmxHumanoid.cs` 的骨骼映射词典；A→T-pose 由其 LevelArm/LevelSegment 控制。手动改词典或在生成的 Avatar 上微调。
- **腿不弯/部位不动**：`DumpSkinning` 看是否蒙皮到了 `付与/D` 骨（足D 等）；`BuildSkinRemap` 已处理常见情形，特殊骨可补规则。
- **粘连/拉伸**：`DumpOrphans` 找远离主骨的孤立顶点（PMX 编辑器删面残留）；`FindOrphanVerts` 已丢弃，阈值 `-dist` 可调。
- **手动改 prefab**：直接在 `Assets/PmxImported/<模型>/` 的 prefab 上改也行，但**重跑阶段 2 会覆盖**——稳定的手改应回写到管线词典/配置或放到阶段 3 之后。

### 阶段 3 — 物理 → SpringBone（**待实现**）
将复用现有 `PmxPhysics` 的链/根/碰撞体检测，输出 **VRM10 SpringBone**（替代 DynamicBone）。现状：阶段 2 暂仍生成 DynamicBone（供 `.me` 路径）。

### 检查点 B — 物理 / 穿模实时调（人工，**核心可手动介入点**）
当头发穿身体、裙摆穿腿等自动化解决不了时：
- 在 Unity **Play 模式**下选中模型，用 **UniVRM 的 SpringBone 编辑器 + gizmo** 实时拖：
  - **碰撞体**（腿/髋/胸胶囊）的半径与位置——防穿模主力；
  - SpringBone 的 `stiffness / drag / gravity`——过软=穿模/甩动过大，过硬=僵硬。
- 调好后**重新导出 VRM**（阶段 5）。因为是 VRM 标准件，**任何 VRM 工具也能改**。
- 想让改动可复现/自动化：把参数回写到管线的物理配置段（规划中，思路同 `PmxStyleConfig`）。

### 阶段 4 — 材质：MToon 基底 + 打包 HoYo 贴图（**待实现**）
VRM 基础材质用 MToon（保证通用、外部可显示）；把阶段 0 抽出的 HoYo 贴图作为额外 glTF 贴图打包，并写 `extras` 元数据（材质→各图）。仿 HoYo 的精确渲染由 **App 内风格系统**在加载时读这些图完成（见架构文档）。

### 检查点 C — 材质 / 风格调参（人工）
两种循环（不必重编译）：
- **配置循环**：编辑 `Tools/PmxPipeline/styles/<模型>.style.json`（`skinWarmth/brightness/outlineScale/rimStrength` 或逐材质 `baseColor/shadeColor/outline`），重跑阶段 2。
- **Editor 实时循环**：构建一次后在 Inspector 调 `Assets/PmxImported/<模型>/*.mat`，实时预览，满意后只重导出。

### 阶段 5 — 导出 VRM（**待实现**）
`BuildAndExportVrm`（规划）：阶段 0→2→3→4 后用 UniVRM 导出 `.vrm`（含 SpringBone、打包贴图、元数据）到 `Build/PmxModels/<模型>.vrm`。
> 旧路径（已实现）：`BuildAndExport` / `ExportMe` 产 `.me`，见 §7。

### 阶段 6 — 验收（人工）
App 加载产物 → 设置里在 `mtoon` / `hoyo_hi3` 间切换 → 对比 `.blend` 渲染参考：皮肤白皙、颜色鲜艳、脸无碎裂、分区/高光正确、头发裙摆物理正常、口型/表情可动。

---

## 5. 手动介入点汇总

| 阶段后 | 可手动改什么 | 在哪改 | 如何保留 |
|---|---|---|---|
| A | 骨骼映射、孤立面阈值、蒙皮重定向 | `PmxHumanoid.cs` 词典 / `-dist` 参数 | 改管线代码/参数，重跑生效 |
| B | 物理参数、碰撞体 | Unity Play + SpringBone/Collider Inspector | 重导出 VRM 落盘；或回写物理配置 |
| C | 材质颜色/轮廓/风格 | `styles/<模型>.style.json` 或 `*.mat` | 配置随模型版本控制；`.mat` 改动随导出落盘 |
| 6 | 最终观感取舍 | 切换 App 风格 / 后处理设置 | App 设置持久化 |

---

## 6. 自动化封装

- 现状一键（产 `.me`）：`MEPmxPipeline.BuildAndExport`（内部串阶段 0→2→ 导出）。
- 目标一键（产 VRM）：`BuildAndExportVrm`（阶段 0→2→3→4→5，待实现）。
- 需要外层脚本时，写一个薄 PowerShell 顺序调用上面命令即可；**不要**做成跳过检查点的黑盒——保留"先 BuildModel、人工过检查点 A/B/C、再导出"的能力。

---

## 7. 旧 `.me` 导出路径（保留，不删除）

`.me` 是本项目专有的运行时 mod 格式（ZIP 内含 AssetBundle）。新架构主产物转向 VRM 后，`.me` **保留为可选/兼容路径，不删除**（入口：`MEPmxPipeline.ExportMe` / `BuildAndExport`）。其用途、实现时踩过的坑与解决方法已写进代码注释（见 `Assets/Editor/PmxPipeline/MEPmxPipeline.cs` 的 `ExportMe`/`ExportInternal` 注释块）与本节。

要点（详见代码注释）：
- **用途**：把构建好的 prefab 连同贴图/材质/着色器打包成单文件 `.me`，走现有 `.me` 加载链直接在 App 内显示；自带渲染、无需 App 侧风格系统。
- **坑：AssetBundle 不能含 `.cs` 脚本**——脚本对玩家程序集解析；依赖收集时已排除 `.cs`。
- **坑：着色器变体剥离**——UTS2/lilToon 打进 bundle 后若变体被剥离会显示粉红；需 Always Included Shaders / 变体集合（实施时复核）。
- **坑：Addressables content 重建副作用**——导出过程可能顺带删 `Assets/AddressableAssetsData/link.xml`；提交前需还原。
- **坑：导出文件名被本机 settings 的 `modelName/exportPath` 钉死**——曾导致输出名是 `丽塔_m4_stylized.me` 而非预期名；用 `-out` 或清本机配置可控。

---

## 8. 踩坑与解决（PMX→Unity 全流程，经验沉淀）

> 这些是实现 M1–M4 过程中实际踩到并修复的，新接手者/AI Agent 照此可少走弯路。代码里对应位置均有内联注释。

| 现象 | 根因 | 解决 |
|---|---|---|
| 模型左右镜像（刘海遮错眼） | 仅取负 Z + 反转缠绕 = 镜面反射 | MMD→Unity 用绕 Y 轴 180° 旋转（**同时取负 X 和 Z**）+ **保持原始三角缠绕** + scale 0.08 + 翻 UV V |
| 腿不弯/部位悬空不动 | 蒙皮权重落在 `付与/D` 变形骨（足D 等，仅靠 inherit 驱动） | `BuildSkinRemap`：把 inherit 驱动骨的权重重定向到源控制骨 |
| 粘连/拉伸 | PMX 编辑器删面残留的孤立顶点（蒙皮到 操作中心/IK 骨） | `FindOrphanVerts` 丢弃远离主骨的面（`-dist` 阈值）|
| 跳舞口型/表情不动 | `UniversalBlendshapes` 仅 VRM；舞蹈片段绑定 `Body` 路径 | SMR 放在名为 `Body` 的子物体；**保留日文形态键名**走口型直驱旁路 |
| 全身发紫 | MetalMap（金属遮罩，非 sphere）+ mc1/mc3 被当 UTS2 matcap 视空间 additive 叠加；按 kind 注入紫色 highColor | matcap 全关；highColor 仅预设显式给有效非黑色才启用；阴影/底色/rim 中性化 |
| **脸部碎裂**（重点坑） | `脸`(脸 UV) 被错配 **Body_LightMap(身体 UV)** 当 `_Set_1st_ShadePosition`，用脸 UV 采身体光照图 → 错位硬边阴影碎块 | **彻底不把 LightMap/Facemap 当 UTS2 阴影位置图**（UTS2 无 HoYo LightMap 通道语义）。注：双面剔除不是此问题主因 |
| 脸部仍有碎感（次因） | 共面脸部贴片同 renderQueue z-fight；脸材质双面渲染背面 | 按材质序号递增 renderQueue；HoYo 单面模型**强制单面剔除** `_CullMode=2`（保持原缠绕，正面朝外）|
| 偏暗/偏素/皮肤偏红 | ① 主场景相机**无 PostProcessLayer**，我们加的 Bloom/ColorGrading **全程未生效**；② 皮肤多处暖色偏移 | 提亮非正路；鲜艳感需**给相机接通后处理**。皮肤暖色偏移中性化，改由 `style.json` 的 `skinWarmth/brightness` 数据微调 |
| 渲染参数改一次要重跑整链 | 风格参数写死在 C# | `PmxStyleConfig` 把风格解耦到逐模型 JSON；并支持 Editor 实时调 `*.mat` + 仅重导出 |
| HoYo 游戏贴图找不到 | 这些图打包在 `.blend` 里、PMX 旁无散文件 | 阶段 0 Blender 无头抽图（`--texture-root`）|
| Blender 5.0.1 抽图路径报错 | `模型预设导入.py` 插件在 5.0.1 路径不匹配 | 用 Blender **3.6** |

---

## 9. 当前实现状态（对照架构文档 N0–N5）

- ✅ 阶段 0/1/2、旧 `.me` 导出、`PmxStyleConfig` 解耦、踩坑修复。
- ⏳ 阶段 3（SpringBone）、阶段 4（MToon+打包贴图）、阶段 5（VRM 导出）、App 风格系统、后处理接通 —— 见 `Docs/RENDER_STYLE_DESIGN.md` N0–N5，待实现。
- 待 VRM 导出落地前，主产物暂仍是 `.me`（§7）。
