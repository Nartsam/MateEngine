# PMX 渲染复刻管线

把「PMX 模型 + 配套 `.blend` 渲染预设」离线转换成 MateEngine 能加载的资源，并让渲染
结果与 Blender/EEVEE 逐像素对齐。

**这是一条离线工具链，不进 App**：它依赖 Blender 3.6 与 Unity 编辑器，两者都不可能随
程序分发。App 只负责加载转换结果。决策背景见 `Docs/DECISIONS_RECORD.md` ADR-0012。

- 路线取舍与全部实测结论：`Docs/plans/2026-09-10-blender-graph-transpile.md`
- 为什么不走「通用卡通着色器调参」：ADR-0011
- 预设的参考数据（节点图、视图变换 LUT、配套插件副本）：`Docs/RenderPreset/README.md`

## 一句话原理

预设是一张**无光照纯函数**的节点图（Color 直连 Material Output ▸ Surface）。既然是纯
函数，就可以整体转译成 HLSL 精确复刻，而不是用通用着色器的二十几个旋钮去拟合。

## 数据流

```
<角色>/渲染预设.blend
   ├─[export_blender_graph.py]─> Docs/RenderPreset/graphs/<角色>.graph.json   节点图（含连线）
   │                             Build/RenderPreset/images/<角色>/            预设内打包的贴图
   └─[bake_outline_shell.py]───> Build/RenderPreset/outline/<角色>.outline.*  描边外壳

节点图 ─[transpile.py]─> Assets/Generated/PmxTranspiled/<角色>/
                            shaders/M??.shader        逐材质 HLSL
                            <角色>.bindings.json      材质→着色器/贴图/混合状态/LUT

<角色>/*.pmx ─[Assets/Editor/PmxPipeline]─> 网格 / 骨架 / BlendShape / Humanoid / 刚体物理
                                               │
                        描边外壳 ─[MEOutlineShell]┘
                                               │
                        bindings ─[METranspiledMaterials]┘
                                               │
                                        [MERefRenderer] ─> 对照图
                                        [ExportMe]      ─> .me
```

## 导出可导入 App 的模型

关闭本项目的 Unity 编辑器，在仓库根执行：

```powershell
pwsh -NoProfile -File Tools/PmxPipeline/export_model.ps1 `
    -Pmx "D:/模型/角色.pmx" `
    -Preset "D:/模型/渲染预设.blend" `
    -Out "Build/PmxModels/角色.me"
```

三个参数分别指定角色本体、配套渲染预设和输出文件；输入不要求放在约定的模型库结构中。
相对路径按仓库根解析，输出必须留在仓库内。Blender/Unity 沿用下方的本机路径解析规则，
也可传 `-BlenderExe`、`-UnityExe`；版本在启动前检查为 Blender 3.6 与本项目精确 Unity 版本。
Python 使用仓库内 `Build/.venv`。

工具每次依次执行：导出完整节点图和贴图 → 烘焙描边并提取方向属性 → 转译 HLSL →
生成 Humanoid prefab、保存材质/贴图/LUT/描边网格 → 打包并重新加载 AssetBundle 验证 →
写出 `.me`。实际使用的材质缺转译或贴图时导出失败；预设库中未使用的遗留材质可以跳过。
新入口不读取旧配置里的角色名、导出路径或风格覆盖，也不改写本机配置。

输出为 App 已支持的**原始 AssetBundle 格式 `.me`**。在角色库的模型导入入口选择该文件。
包含原始日文 BlendShape、刚体物理、转译材质和描边。物理把 PMX 的刚体与关节原样写进 `MmdPhysics` 组件，
App 运行时交给 Bullet 模拟（ADR-0017）；需要按角色调整时写 `physics/<PMX 文件名>.physics.json`，网格的逐角色调整
（如头发法线平滑，ADR-0018）写 `mesh/<PMX 文件名>.mesh.json`（见下表）。
2026-10-08 之前导出的 `.me` 里是 DynamicBone 近似（ADR-0016），照常可用。**新 `.me` 需要包含 `MmdPhysics`
与 `MateMmdPhysics.dll` 的新版 App**，旧 exe 加载后没有物理。
**模型文件不携带屏幕空间 bloom**。支持 `MEModelBloom` 的新版 App 会在线性 HDR 上混合，
再执行 Bloom/Filmic；沿用设置面板已有的 Bloom 开关并保留其状态，alpha 不外扩。
旧 App 或独立材质预览仍由材质执行 Filmic，半透明在显示域混合的差异仍存在。

导出模型带 `MEModelDirections` 组件，跟随头骨的左右转向更新脸部 SDF 朝向（低头/抬头不影响脸部阴影，见 ADR-0015）；日光方向随模型根节点旋转，
保持预设中日光相对角色的关系（App 的模型父节点绕 Y 转了 180°）。
**App 需要使用包含此组件的新版构建**；AssetBundle 不携带 C# 实现，旧 exe 无法执行它。

中间产物按 PMX/预设路径组合隔离，键为路径哈希：

| 路径 | 内容 |
|---|---|
| `Build/PmxExport/<键>/` | 图、原始贴图、描边、逐步日志及 bundle 验证预览 `export-preview.png` |
| `Assets/Generated/PmxTranspiled/Export_<键>/` | 生成的着色器与绑定清单 |
| `Assets/PmxImported/Export_<键>/<PMX文件名>/` | 可重新打包的 prefab、网格、Avatar、材质与贴图资产 |

以上均为忽略的本地产物。最终 `.me` 已包含加载所需的资产，不依赖外部 PMX/Blend 路径。

## 跑一次完整闭环

```powershell
pwsh Tools/PmxPipeline/run_ref_diff.ps1 -Character 特工丽塔 -View front
```

它串起：导出节点图（`-ExportGraph`）→ 烘描边外壳（`-BakeOutline`，产物不存在时自动
烘一次）→ 转译 → Blender 基准图 → Unity 对照图 → 差分统计。产物全在
`Build/RefDiff/`（已忽略）。

一次约 90 秒，大头在 Unity 侧的 4× 超采样。

常用开关：

| 开关 | 用途 |
|---|---|
| `-View front\|three_quarter\|right\|back` | 换视角。侧面/四分之三才能验出 matcap 的左右镜像 |
| `-ExportGraph` | 重新从 `.blend` 导出节点图与贴图。**改过导出器就必须带上** |
| `-BakeOutline` | 强制重烘描边外壳 |
| `-NoOutline` | 不并描边，把「描边的差异」和「着色的差异」分开量 |
| `-NoBloom` | 两侧同时关 bloom。Filmic 的高光肩部会把小差异放大成大色差，关掉才能判断偏差出在材质求值还是后期合成 |
| `-SkipBlender` / `-SkipUnity` | 复用上一次的一侧产物 |
| `-Profile uts2` | 切回已证伪的 UTS2 兜底路径，仅用于对照基线 |

出两联对比图（左 Blender、右 MateEngine）：

```powershell
Build/.venv/Scripts/python.exe Tools/PmxPipeline/make_compare.py `
    --ref Build/RefDiff/特工丽塔-front-blender.png `
    --test Build/RefDiff/特工丽塔-front-unity.png `
    --out Build/RefDiff/对比.png --crop 165,15,860,1010 --scale 0.95 --label
```

## 环境与路径

仓库内的路径全部由脚本自身位置（`$PSScriptRoot`）推出来，**跟着仓库走**，换机器、换
目录、换盘都不受影响。仓库之外的三个路径是本机相关的，按下面的顺序解析：

**命令行参数 > 环境变量 > 本机配置 > 自动探测**，全落空则报错说明怎么配，不会回退到
某台开发机的绝对路径。

| 东西 | 命令行 | 环境变量 | 本机配置键 | 自动探测 |
|---|---|---|---|---|
| Blender **3.6** | `-BlenderExe` | `ME_BLENDER` | `blenderPath` | 各固定盘的 `Program Files\Blender\3.6\`、`Blender Foundation\Blender 3.6\` |
| Unity 编辑器 | `-UnityExe` | `ME_UNITY` | `unityPath` | 按 `ProjectSettings/ProjectVersion.txt` 的精确版本找 Unity Hub 安装目录（含 Hub 自定义安装位置） |
| 模型库目录 | `-ModelsRoot` | `ME_MODELS_ROOT` | `modelsRoot` | 无法探测，必须指定 |

本机配置是 `Library/MateEngineUserData/PmxPipeline/settings.json`。`Library/` 已被 Git
忽略，所以它是本机私有的，不会进仓库——**换机器时需要重建这个文件，或者用环境变量**。

**Blender 必须是 3.6。** 5.x 移除了 `node_group.outputs` 与 `eevee.use_bloom`，预设配套
的导入插件在其上跑不起来，观感也不保证一致。

Python 依赖（numpy + Pillow）只装在仓库内的 `Build/.venv`：

```powershell
python -m venv Build/.venv
Build/.venv/Scripts/python.exe -m pip install numpy pillow
```

## 模型库的约定

```
<ModelsRoot>/<角色>/
    <角色>.pmx          # 或带 _修复 后缀；见下
    渲染预设.blend
```

同一目录下常有武器、伞、鱼竿等配件的独立 `.pmx`。定位角色本体的顺序是：**与目录同名 >
带 `_修复` 后缀 > 目录里唯一的一个**；都对不上就列出候选让调用方用 `-Pmx` 指定，不猜。

角色名（`-Character`）同时是模型目录名和所有产物的键——节点图、贴图、描边、着色器目录
都用它。

## 文件职责

### `Tools/PmxPipeline/`

| 文件 | 职责 |
|---|---|
| `export_model.ps1` | 独立导出入口：显式 PMX + Blend → `.me`，无参考渲染与 bloom 集成 |
| `PmxTools.ps1` | 导出与差分入口共用的本机工具定位、串行进程执行和日志摘要 |
| `run_ref_diff.ps1` | 闭环入口。串起导出/烘焙/转译/双端渲染/差分，并解析本机外部路径 |
| `export_blender_graph.py` | 从 `.blend` 导出**完整节点图**（含连线、插槽默认值、节点组接口、色带与曲线的 257 项烤表）与预设内打包的贴图 |
| `graph_ir.py` | 节点图中间表示：展开节点组、解析 Reroute、按 `data_type` 判定活动插槽、插入隐式类型转换 |
| `transpile.py` | IR → 逐材质 HLSL + `bindings.json`。节点求值库内联自 `hlsl/me_nodes.hlsl` |
| `hlsl/me_nodes.hlsl` | 节点求值函数库：Blender 的数学算子、HSV、混合模式、ColorRamp/曲线采样、视图变换、法线贴图、Jenkins 哈希、Perlin 3D/4D 噪声、Voronoi、随机 alpha 阈值、闭包模型 |
| `bake_outline_shell.py` | 在 Blender 里求值预设自带的几何节点，把描边外壳（焊接 + 重算法线 + 外扩）连同权重烤成二进制 |
| `blender_apply_preset.py` | 基准端：导入 PMX、套预设、按指定视角无头渲染，并把场景设置/材质槽/日光方向写进报告 JSON |
| `compare_renders.py` | 差分统计：轮廓 IoU、交集内 ΔE76（均值/中位/P95）、逐材质分解、热力图 |
| `make_compare.py` | 两联对比图（左 Blender、右 MateEngine），供肉眼验收 |
| `export_view_transform_lut.py` | 实测导出 Filmic + High Contrast 的 257 项视图变换曲线 |
| `probe_bloom_kernel.py` | 实测 EEVEE bloom 的点扩散函数（不靠重建算法，直接量） |
| `verify/` | 逐点核对那些「没文档、写错了也不报错」的节点语义。见该目录的 README |
| `styles/` | **UTS2 兜底路径**的逐模型风格配置，不属于转译路线 |
| `physics/` | 逐角色物理配置 `<PMX 文件名>.physics.json`：只写要改的项，字段为 `PmxRigidPhysicsOptions` 或 `MmdPhysics` 的公开字段（如 `jointStopErp`、`loosenPattern`、`followPattern`、`maskOffPattern`），拼错字段名时导出报错。目前有丽塔（`jointStopErp: 0.2` 压住坐下时裙摆的抖动；胸部用 `jointStopErpPattern`/`jointStopErpMatched` 保持 0.475，并用 `maskOffPattern`/`maskOffGroups` 去掉与第 0 组（手臂、身体）的碰撞，免得被手臂挤到一边）与寻梦者（头发 `jointStopErp: 0.3`，衣物另设 0.2：压住待机抖动、坐下时外套下摆稳定垂下，头顶翘起的头发不耷拉；每节限位只有 ±1°～±5° 的前刘海用 `followPattern` 改为跟随骨骼；胸部同样去掉与第 0 组的碰撞） |
| `mesh/` | 逐角色网格配置 `<PMX 文件名>.mesh.json`：字段为 `PmxMeshOptions` 的公开字段，拼错时导出报错。目前只有寻梦者（`smoothNormalsPattern: "^头发$"`、`smoothNormalsRadius: 0.05`，再按头部形状混合 `proxyNormalsWeight: 0.75`，修转头时头发阴影断成几截、脸侧发束亮而侧面暗的断层，ADR-0018） |
| `dump_render_preset.py` | **已弃用**：早期按关键词猜出一个扁平 UTS2 字典、从不导出连线。保留仅为追溯 |

### Unity 侧

| 文件 | 职责 |
|---|---|
| `Assets/Editor/PmxPipeline/MEModelExportValidation.cs` | 重新加载实际 bundle，检查网格/形态键、材质、贴图色彩空间、出图与头部方向更新 |
| `Assets/Editor/PmxPipeline/PmxPhysics.cs` | PMX 刚体/关节 → DynamicBone 链与碰撞体（ADR-0016）；导出流程已不用它，只留给验证装置的 `-physicsTunings` 对照 |
| `Assets/Editor/PmxPipeline/PmxRigidPhysics.cs` | PMX 刚体/关节原样写进 `MmdPhysics`，运行时由 Bullet 模拟；`PmxRigidPhysicsOptions` 的锁根节、去掉悬挂刚体、偏移锁归零（`offsetLocks`）三条处理默认开启，放宽关节限位（`loosenPattern`/`loosenDegrees`）、改为跟随骨骼（`followPattern`）、去掉与某些分组的碰撞（`maskOffPattern`/`maskOffGroups`）留作逐角色调整；读取 `physics/` 下的逐角色配置（ADR-0017） |
| `Assets/Editor/PmxPipeline/PmxMeshAdjust.cs` | 导出网格的逐角色调整：读取 `mesh/` 下的配置；按材质名对法线做小范围平均（只与朝向相近的邻居平均，双面发片不会互相抵消），再可按头部的近似椭球混合（`proxyNormalsWeight`，椭球取高于头骨的那部分顶点的包围盒）。导出流程与验证装置的 `-physicsSmoothNormals` 共用 |
| `Assets/PmxPhysicsRuntime/MmdPhysics.cs` | 刚体物理的运行时组件；原生库 `Assets/Plugins/x86_64/MateMmdPhysics.dll`，源码与构建脚本在 `Tools/MmdPhysicsNative/` |
| `Assets/Editor/PmxPipeline/MEPhysicsValidation.cs` | 物理验收：不跑物理 / 包内原物理 / 按 PMX 重建的 DynamicBone / 刚体物理多份同步跑 App 动画，出数值报告、对照图和逐帧并排录像 |
| `Assets/PmxTranspileRuntime/MEModelDirections.cs` | 按头骨更新脸部 SDF 方向，通过 PropertyBlock 隔离各模型实例 |
| `Assets/PmxTranspileRuntime/MEModelBloom.cs` | App 转译模型的 HDR/Bloom/Filmic 接入，保留主相机与 UI 引用、原 alpha 和已有 Bloom 开关；写回时补模型深度，输出预乘颜色 |
| `Assets/Editor/PmxPipeline/MEModelBloomValidation.cs` | 实际 bundle 的主相机合成与参考合成对照，并验证边缘相对旧路径的误差改善、辅助物体遮挡、UI、alpha、开关、尺寸变化、模型卸载和主场景自动接入 |
| `Assets/Editor/PmxPipeline/MERefRenderer.cs` | 对照端：建模型、装配转译材质、并入描边外壳、按 Blender 报告对齐相机与方向 uniform、4× 超采样渲染、跑合成、出对照图与材质 ID 通道 |
| `Assets/Editor/PmxPipeline/METranspiledMaterials.cs` | 读 `bindings.json`，建 LUT 贴图与各贴图，按材质名替换槽位 |
| `Assets/Editor/PmxPipeline/MEOutlineShell.cs` | 把烘好的描边外壳并进基础网格：翻转绕序、按焊接组平均骨权重与 BlendShape 位移；导出 `.me` 时按底层图集修正描边材质（改挂备用描边或生成换图变体），离线差分不启用；导出日志「描边按底层图集改挂」列出改动 |
| `Assets/Editor/PmxPipeline/IdPass.shader` | 材质 ID 通道，供差分做逐材质分解 |
| `Assets/PmxTranspileRuntime/MEPostCompose.{cs,shader}` | 离屏线性 HDR 缓冲上的合成：bloom（阈值/降采样/帐篷升采样/累加）+ 视图变换。alpha 感知，不破坏分层窗口 |
| `Assets/Generated/PmxTranspiled/<角色>/` | 转译产物（已忽略，一条命令可重建） |

## 改动时必须知道的事

App 合成的独立验收（关闭本项目的 Unity 编辑器，必须启用图形设备，不能带 `-nographics`）：

```powershell
& "<Unity 6000.2.6f2 的安装目录>/Editor/Unity.exe" -batchmode -quit `
    -projectPath "$PWD" -executeMethod MateEngine.PmxPipeline.MEModelBloomValidation.Run `
    -bloomModel "$PWD/Build/PmxModels/特工丽塔_transpiled.me" `
    -logFile "$PWD/Build/BloomValidation/validation.log"
```

输出在 `Build/BloomValidation/`，包括数值报告、Bloom 开关对比、相机空间 UI、窗口遮挡与辅助物体
遮挡（抬起左臂后的绿色前后景）、模型父节点旋转 180° 后的画面（应与原画面一致），核验低头 30° 时脸部朝向不变、左右转 30° 时随之转动，并记录关 Bloom
时边缘对旧路径的误差，以及 20 帧同步渲染
（含单像素回读开销）的 Bloom 开关耗时。
此检查使用 512² / 4× MSAA，同采样比较主程序接入与离线合成；不等于 Blender 差分中
每轴 4× 超采样的九角色成绩，也不替代正式主场景的窗口与交互验收。

物理验收（同样需要图形设备；只读 `.me` 与 PMX，不改模型）：

```powershell
& "<Unity 6000.2.6f2 的安装目录>/Editor/Unity.exe" -batchmode -quit -projectPath "$PWD" `
    -executeMethod MateEngine.PmxPipeline.MEPhysicsValidation.Run `
    -physicsModel "$PWD/Build/PmxModels/寻梦者_transpiled.me" -pmx "D:/模型/寻梦者.pmx" `
    -logFile "$PWD/Build/PhysicsValidation/run.log"
```

在固定待机姿势（rest）、待机动画（idle）、转头（鼠标跟踪上限）、拖拽（与 App 一样从待机用 0.25 秒过渡进去，叠加
`AvatarSwayController` 量级的摆动）、三段舞蹈、坐下（1.5 秒过渡）、App 的待机轮播（idles：载入模型所用控制器的
女性待机混合树 20 段，每段 10 秒、过渡 1 秒，与主场景的设置相同）这些场景里，报告骨骼相对动画姿态的偏角、主要绑在
链根骨上的顶点位移（腰头、衣摆是否被拉离身体）、网格裂开（绑定姿态下重合、权重不同的顶点对被拉开的距离，并列出
超过 0.3 cm 的时段）、长链与竖直方向的夹角、骨骼嵌入 PMX 身体碰撞体的深度，并在各项最差的一帧及场景结束时出图
（`-physicsOut` 指定目录，默认 `Build/PhysicsValidation/<模型名>/`）。刚体方案另报关节被拉开的距离（平移锁死的
关节两端分开多远，即 Bullet 约束的弹性）、静止姿势最后 2 秒各材质顶点的逐帧抖动与各刚体组抖得最厉害的刚体、
物理偏移逐帧的二阶差分（减去动画本身的运动，运动中也能量抖动；与帧率有关，对比时帧率要一致），以及每帧物理
耗时。默认对照的方案是不跑物理（rest）、包内原物理（current）、按 PMX 重建的 DynamicBone（rebuilt）和刚体物理
（rigid，ADR-0017）。可选开关：

| 开关 | 用途 |
|---|---|
| `-physicsTunings "名称:字段=值,…;名称2:…"` | 同时比较多组 `PmxPhysicsTuning`，每组一列；取 `none` 时不跑 DynamicBone 重建 |
| `-physicsRigid "名称:字段=值,…;名称2:…"` | 同时比较多组刚体物理设置，每组一列；字段可以是 `MmdPhysics` 的运行时设置（如 `boneWrite=Position`、`solverIterations=30`、`jointStopErp=0.2`、`jointStopErpPattern=^qz_`、`fixedTimeStep=0.008333`），也可以是 `PmxRigidPhysicsOptions` 的导出选项（`lockAnchors`、`pruneHanging`、`offsetLocks`、`loosenPattern`、`loosenDegrees`、`followPattern`、`maskOffPattern`，整数数组用 `+` 连接如 `maskOffGroups=0+1`）；`followAnchors=false` 关掉输出延迟补偿作对照；`boneWrite=Position` 即按 MMD 原样写回位置；取 `none` 时不跑刚体物理。报告里的「骨段伸长」量衣物被拉长的程度 |
| `-physicsVideo dance1,drag` | 这些场景逐帧把各方案并排渲染成 `video-<场景>/f*.jpg`（默认右后方斜看全身），再用 ffmpeg 按 50 帧/秒合成视频 |
| `-physicsVideoZoom 0.4` / `-physicsVideoYaw 90` | 录像改为对准腰部衣物放大（取景半高，米）/ 机位绕竖轴的角度（0 为正面，默认 150） |
| `-physicsShowBodies <正则>` | 名称匹配的动态刚体（橙）与会和它们碰撞的运动学刚体（青）画成不受遮挡的半透明形状，叠在模型上看刚体实际在哪 |
| `-physicsVideoEvery 165` / `-physicsVideoFrom 30 -physicsVideoTo 41` | 长场景只录一部分：每隔若干帧录一帧（先粗看整段找时刻）/ 只录这段时间（秒） |
| `-physicsTintMaterials "皮肤\|袜子"` | 名称匹配的 PMX 材质各画成一种纯色（对应关系写在报告开头），看出图里露出来的是哪个材质 |
| `-physicsVideoBone Chest` | 录像对准该人形骨骼（`HumanBodyBones` 名）放大，取景半高用 `-physicsVideoZoom`（缺省 0.25 米） |
| `-physicsDt 0.0060606` | 每帧步长按 App 的帧率取（这里是 165 帧/秒）；缺省 1/50 秒。只在高帧率下出现的抖动必须这样复现 |
| `-physicsIdleSeconds 4` | 待机轮播每段的时长，缺省与 App 相同的 10 秒 |
| `-physicsTrackBones <正则>` | 报告名称匹配的骨骼相对不跑物理的位移、转角、转动的逐帧二阶差分（量抖动）及抖得最厉害的几个时刻（如 `胸上2` 看胸部被拉到哪里，`Hair_F` 看刘海抖不抖、在动画哪个时刻抖） |
| `-physicsTrackCsv 1` | 把 `-physicsTrackBones` 的逐帧偏离另存为 `track-<场景>.csv`（转动向量取在不跑物理那份骨骼的局部轴上；`rest` 行记骨骼本身的转动角速度），看偏离随时间怎样变、与头部转动是什么关系 |
| `-physicsMaskOff "rigid-x=^Bone_C1_Head_Hair_F:0"` | 让某个方案里名称匹配的刚体不再与指定分组碰撞，用来排查某处碰撞是不是抖动的来源 |
| `-physicsPokeThrough "^皮肤2$>^(衣饰\|衣服)$"` | 内层材质从外层穿出来的距离：内层每个顶点配上 1 cm 内最近的外层顶点（只留绑定姿态下在其下方的），量越过外层切面多远，按「内层材质/主骨骼」分组，列出超过 0.1 cm 的时段并在最深一帧出图。蒙皮本身会让很多地方越过零点几毫米，要和「不跑物理」那栏对照 |
| `-physicsPokeFocus 皮肤2/下半身` | 穿出的时段列表与出图只看名称匹配的分组 |
| `-physicsSmoothNormals "rigid-s5=^头发$:0.05;rigid-p7=^头发$:0.05:0.75"` | 给指定方案换一份调整过法线的网格（`PmxMeshAdjust`，平滑半径单位米，可选的第三项为按头部形状混合的比例），对照头发明暗；方案名取 `-physicsRigid` 建出的名字。要对比平滑前后，用平滑前导出的 `.me` |
| `-physicsHeadRoll 15` | `head` 场景再叠加左右歪头（度），待机动画里常见 |
| `-physicsScenarios rest,drag` | 只跑指定场景 |
| `-physicsSnapshots 6` | 每个场景均匀取若干时刻，从正面拍腰部 |
| `-physicsDebugChain <链根骨名>` | 场景结束时逐粒子输出位置、相对不跑物理的偏移和最近的碰撞体 |

报告里每个场景有一行「姿态抽查」（中间时刻头与双手的位置），可用来核对出图与骨骼是否一致。批处理里整个验证都在
同一帧内完成，装置给蒙皮开了 `forceMatrixRecalculationPerRender`，否则所有出图都定格在第一次渲染的姿势上
（2026-10-07 之前的对照图都受此影响）。即便如此，逐帧录像在相邻两帧之间偶尔仍会显示上一帧的蒙皮（不跑物理那栏的整张脸也会上下跳），**不能用录像逐帧判断抖动**，要看骨骼转角、顶点位移这些数值指标。

刚体物理依赖原生库 `Assets/Plugins/x86_64/MateMmdPhysics.dll`。改了 `Tools/MmdPhysicsNative/` 的源码后，关掉
Unity 编辑器（它会锁住已加载的 DLL）再运行 `Tools/MmdPhysicsNative/build.ps1`：首次会把 Bullet 3.25 源码拉到
该目录的 `bullet3/`，中间文件放 `Build/MmdPhysicsNative/`，需要 CMake 和带 C++ 工具集的 Visual Studio 2022。

- **改了导出器就必须重跑导出。** 导出器每扩一个字段，旧 JSON 就会缺字段而**静默**退化
  （漏 LUT 表、漏插值方式、漏输出插槽默认值各坑过一次）。`transpile.py` 现在硬卡
  `formatVersion`，但扩字段时要记得同时抬版本号。
- **没有依据就不要假设默认值。** Blender 报告里曾经不记录 AO/SSR/采样数，等于默认
  「它们是关的」——这种假设错了只会表现为某处偏亮偏暗，极难定位。
- **拿不到的值要报错，不要退化成 0。** 静默取 0 会让一整条链变黑，而差分上只是一小块。
- **语义存疑就去实测，别靠记忆。** `verify/` 就是为此存在的。已经栽过：Jenkins 哈希的
  初始常量、RGB Curves 的复合顺序、`Vector` 未连线时 Blender 忽略插槽默认值。
- **验收看数值，不只看图。** 每次改完跑一遍闭环，看轮廓 IoU 与逐材质 ΔE 分解。当前
  九个角色的基准在计划文档 Phase 3.6 一节。
