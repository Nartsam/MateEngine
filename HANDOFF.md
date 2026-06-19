# HANDOFF — PMX 导入与原神风渲染管线

本文件用于跨对话交接。新对话**先读 `AGENTS.md` → `PROGRESS.md` → 本文件 → `Docs/DECISIONS_RECORD.md` ADR-0008**，即可完全接手。本文件聚焦「PMX 离线导入 → `.me` → 渲染」这条任务线。

## 目标

让 MateEngine 原生支持加载 PMX 模型，并复刻原神/崩坏/P5X 风格 NPR 渲染。方案：**Unity Editor 离线自动化管线**（不做运行时 PMX 加载器），产出 `.me` AssetBundle，经现有 `VRMLoader.LoadAssetBundleModel` 加载。决策与背景见 `Docs/DECISIONS_RECORD.md` **ADR-0008**。

## 关键环境事实

- Unity：`D:\Program Files\Unity\Hub\Editor\6000.2.6f2\Editor\Unity.exe`（版本必须精确 6000.2.6f2）。
- **渲染管线是 Built-in**（非文档曾写的 URP；已在 ADR/ARCHITECTURE 修正）。工程内置 Built-in NPR 着色器：UTS2(`Assets/MATE ENGINE - Packages/Toon/`)、Poiyomi、lilToon。**不要迁移 URP**。
- Blender：`D:\Program Files\Blender\3.6\blender.exe`（M4 用 3.6；5.0.1 的「模型预设导入.py」插件有路径兼容问题，勿用）。
- 测试模型（外部，不在仓库）：`D:\Program Files\MMD\MateEngine\models\特工丽塔\删披风删骨骼.pmx`，对应渲染预设 `丽塔未变身预设.blend`（EEVEE 实时 NPR 节点 + 绑头骨的「面部定位」SDF 空物体 + Bloom 0.08 暖色/Filmic/High-Contrast）。
- 用户为个人自用，**不计较许可证/版权**。模型数量少，**接受离线预处理**。

## 管线代码（全部纯编辑器程序集，`Assets/Editor/PmxPipeline/`，不进运行时）

| 文件 | 职责 |
|---|---|
| `PmxModel.cs` | PMX 2.0/2.1 数据模型（原始值，无坐标转换） |
| `PmxReader.cs` | PMX 二进制解析器（按规范，处理变长索引/编码/权重/形态键/刚体/关节） |
| `PmxMeshBuilder.cs` | 解析结果 → Unity GameObject：网格/骨架/蒙皮/BlendShape/材质 + 调 humanoid/physics + 存 prefab+资产 |
| `PmxHumanoid.cs` | MMD 日文骨名→Unity Humanoid 字典 + A-pose→T-pose + `AvatarBuilder.BuildHumanAvatar` |
| `PmxPhysics.cs` | PMX 动态刚体/关节 → DynamicBone 链（按 头发/裙/胸/其他 分类调参） |
| `MEPmxPipeline.cs` | batch 入口 + 菜单 `MateEngine/PMX Pipeline/*` + 诊断工具 |

生成产物在 `Assets/PmxImported/<模型名>/`（prefab、mesh `.asset`、`_Avatar.asset`、材质、贴图）——**已 gitignore**（可由 PMX 重新生成）。`.me` 输出到 `Build/PmxModels/<名>.me`（Build/ 已 gitignore）。

## 如何运行（batch；先确保无 Unity 实例占用工程锁）

```bash
UNITY="/d/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe"
PROJ="d:/OI/Code/Unity/MateEngine"
PMX="D:/Program Files/MMD/MateEngine/models/特工丽塔/删披风删骨骼.pmx"

# 构建 prefab（解析→网格→humanoid→物理）
"$UNITY" -batchmode -quit -projectPath "$PROJ" -logFile "$PROJ/Logs/pmx.log" \
  -executeMethod MateEngine.PmxPipeline.MEPmxPipeline.BuildModel -pmx "$PMX"

# 打包 .me（默认 prefab=Assets/PmxImported/丽塔/丽塔.prefab，可 -prefab/-out 覆盖）
"$UNITY" -batchmode -quit -projectPath "$PROJ" -logFile "$PROJ/Logs/pmx.log" \
  -executeMethod MateEngine.PmxPipeline.MEPmxPipeline.ExportMe
```

诊断工具（同样 `-executeMethod`，输出到 `Logs/`）：`ValidateParse`(解析报告)、`DumpSkeleton`(骨架→pmx_bones.txt)、`DumpSkinning`(蒙皮/付与→pmx_skin.txt)、`DumpOrphans`(孤儿几何→pmx_orphans.txt)。
菜单等价项：`MateEngine/PMX Pipeline/...`。
首次 batch 会全量导入工程，约 20–40s；之后约 20s。日志里 `Debug.Log` 会带 stacktrace 行，非报错；真报错看 `error CS` 或 `... failed`。

**运行验证只能在 App 内**：用户启动 MateEngine（编辑器 Play 主场景 `Mate Engine Main`，或构建 exe），用「加载自定义模型」选 `.me`。物理/动画/口型只在运行时可见，截图需用户提供。

## 当前状态：M1–M5 全部完成并经用户运行验收

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | PMX 解析 + 网格/骨架/BlendShape/基础材质 | ✅ 验收（朝向/左右/缩放/形态键正常） |
| M2 | Humanoid Avatar（映射+T-pose） | ✅ 验收（腿打弯、动作自然） |
| M3 | 物理 → DynamicBone | ✅ 基本验收（裙/发摆动，参数已调顺） |
| M5 | `.me` 打包 + App 加载 | ✅ 验收（能加载、能跳舞、**口型能动**） |

特工丽塔实测数据：26366 顶点 / 26 材质 / 268 骨骼 / 59 顶点 morph（11/11 舞蹈口型形态键齐全）/ 545 刚体（37 条 DynamicBone 链）/ humanoid 53 骨映射。

### 已踩的坑与定论（务必延续，别回退）

1. **坐标转换**：MMD→Unity 用 **180° 绕 Y（负 X 负 Z）+ 保持原缠绕**（纯旋转、无镜像）。负 Z 单轴=反射会左右镜像（刘海遮错眼）。缩放 0.08（约 1.56m）。UV 翻 V。
2. **Hips=腰**：MMD 上半身/下半身 是 腰 的兄弟，腰 是脊柱与双腿唯一共同祖先。映射 Hips=下半身 会失败。LowerArm=ひじ、Hand=手首（捩 twist 骨作未映射中间骨保留）。
3. **付与/「D」変形ボーン**：腿部蒙皮挂在 足D/ひざD/足首D（仅靠 付与 weight 1.0 旋转、是控制骨的兄弟非子级），引擎驱动控制骨时蒙皮不动→腿直。`BuildSkinRemap` 把「付与源非自身祖先」的变形骨蒙皮重指到源控制骨（几何等价），并向死分支传播（足先EX）。捩骨因源是祖先、靠父子级跟随，不重指。
4. **口型**：`UniversalBlendshapes` 只驱动 VRM；非 VRM 走 `AvatarDanceShapeConverter` 的 bypass，靠舞蹈片段按 transform 路径直驱形态键。引擎片段把形态键绑到 `Body` 路径，故 **SMR 必须挂在 root 下的子物体 `Body`**（骨架仍在 root）。⚠️ 内置 3 个舞蹈片段无形态键轨；口型依赖舞蹈模组本身含面部 morph。
5. **孤儿几何（删除残留）**：用户在 PMX 编辑器删飘带/下摆后，残留顶点被重指到 `操作中心`(bone0，原点不变形)→拖动时粘连/拉伸。`FindOrphanVerts` 把主权重落在非变形骨（操作中心/IK）的顶点判为孤儿，丢弃含孤儿顶点的面。
6. **DynamicBone 调参**（手感主观、需迭代）：当前 头发 damping0.3/elasticity0.25/stiffness0.55/inert0.2；裙 0.3/0.15/0.3/0.15。**高 stiffness 限幅、elasticity 给回弹、适中 damping**（过高=月球漂浮+归位慢，过低=剧烈振荡）。建议后续改为 Play 模式实时调参再回填生成器。

## 待完成工作（下一步从这里继续）

按优先级：

1. **M3.5 — 服装碰撞体（消穿模）**：当前无碰撞体，裙子穿腿/身体。给腿(UpperLeg/LowerLeg)、躯干加 `DynamicBoneCollider`（工程已有 `Assets/DynamicBone/Scripts/DynamicBoneCollider.cs`），并把碰撞体列进各 DynamicBone 的 `m_Colliders`。在 `PmxPhysics.cs` 里据 humanoid 骨自动生成胶囊碰撞体即可。这是用户多次提到的剩余可见问题。

2. **M4 — 原神风渲染（核心诉求「太素」）**：
   - 路线已定（ADR-0008 + 用户选择）：**Blender 3.6 无头提取 `.blend` 预设每材质节点参数 → JSON → 自动映射到 UTS2/lilToon**，并复刻脸部 SDF（绑头骨方向物体）+ 全局后处理（暖色 Bloom/类 Filmic 高对比）。
   - 写 `blender --background --python <dump.py>` 脚本，读预设材质节点树（toon ramp / matcap-sphere / 描边 / rim / emission）导出 JSON；再在 `PmxMeshBuilder` 的材质阶段（目前是 Built-in `Standard`）改挂 UTS2 或 lilToon 并按 JSON 填参。
   - 输入贴图已就绪：模型自带 `衣服a.png` 底色 + `mc1/mc3.png`(sphere/matcap, blend=2) + `toon2/toon3.png`(色阶)；脸用 `脸.png`。材质清单见 `Logs`（或重跑 `ValidateParse`）。
   - 无预设的模型：做一套通用「原神/崩坏/P5X 风」UTS2/lilToon 基准 + 后处理。
   - ⚠️ 透明/描边：当前 Standard 材质对 alpha 处理错误，导致部分表情贴片(照れ/はぅ/なごみ)显示为实心色块——M4 用 NPR 材质设正确的 cutout/transparent 后应解决。

3. **一键化**：把 `BuildModel`+`ExportMe`（+未来 M4 材质）合成单命令 `MEPmxPipeline.BuildAndExport -pmx ... -preset ... -out ...`，即 ADR-0008 终态流水线。

4. **泛化到其他模型**：当前在特工丽塔上验证。换模型时注意：骨名变体（全角/半角数字已兼容）、是否有 上半身2/UpperChest、形态键命名、物理分类关键词。`PmxHumanoid.Map` 字典可能需补候选名。

### 已知遗留限制（非阻塞，记录备查）

- **材质 morph 类表情**（如部分 照れ 腮红）在 MMD 是材质 morph 非顶点 morph，当前只导入顶点 morph→这类不会成为 BlendShape。如需，未来在运行时组件里处理或转纹理。
- **脚趾 足先EX**：靠孤儿/重指传播跟随足首，精度一般（脚尖区域，影响极小）。
- **眼球 LookAt**：`AvatarMouseTracking` 的眼球追随是 VRM 专属（`Vrm10Instance`），非 VRM 模型失效；头部追随正常。可接受降级。
- **VMD 物理→DynamicBone 是有损近似**，非 MMD 刚体仿真。

## 硬约束（来自 AGENTS.md，务必遵守）

- Built-in 渲染管线，勿迁 URP。禁止全局安装依赖（第三方件只能放仓库内）。运行时写入走 `PortablePaths`。不删/改已序列化字段。改完同步更新 `PROGRESS.md` / `Docs/`。Steam 依赖不可恢复。
- 提交：仓库存在大量 lilToon 着色器的「假脏」改动（CRLF 噪声，见 ADR-0006），**提交时只 stage 本任务相关文件**，勿带入无关 shader 改动。生成产物 `Assets/PmxImported/` 已 gitignore。
