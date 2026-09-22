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

<角色>/*.pmx ─[Assets/Editor/PmxPipeline]─> 网格 / 骨架 / BlendShape / Humanoid / DynamicBone
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
包含原始日文 BlendShape、现有 DynamicBone 物理近似、转译材质和描边。
**本工具不接入 bloom**，Filmic 视图变换在材质内执行；半透明混合与线性合成仍有已知差异。

导出模型带 `MEModelDirections` 组件，跟随头骨更新脸部 SDF 朝向，日光保持预设的世界方向。
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
| `dump_render_preset.py` | **已弃用**：早期按关键词猜出一个扁平 UTS2 字典、从不导出连线。保留仅为追溯 |

### Unity 侧

| 文件 | 职责 |
|---|---|
| `Assets/Editor/PmxPipeline/MEModelExportValidation.cs` | 重新加载实际 bundle，检查网格/形态键、材质、贴图色彩空间、出图与头部方向更新 |
| `Assets/PmxTranspileRuntime/MEModelDirections.cs` | 按头骨更新脸部 SDF 方向，通过 PropertyBlock 隔离各模型实例 |
| `Assets/Editor/PmxPipeline/MERefRenderer.cs` | 对照端：建模型、装配转译材质、并入描边外壳、按 Blender 报告对齐相机与方向 uniform、4× 超采样渲染、跑合成、出对照图与材质 ID 通道 |
| `Assets/Editor/PmxPipeline/METranspiledMaterials.cs` | 读 `bindings.json`，建 LUT 贴图与各贴图，按材质名替换槽位 |
| `Assets/Editor/PmxPipeline/MEOutlineShell.cs` | 把烘好的描边外壳并进基础网格：翻转绕序、按焊接组平均骨权重与 BlendShape 位移 |
| `Assets/Editor/PmxPipeline/IdPass.shader` | 材质 ID 通道，供差分做逐材质分解 |
| `Assets/PmxTranspileRuntime/MEPostCompose.{cs,shader}` | 离屏线性 HDR 缓冲上的合成：bloom（阈值/降采样/帐篷升采样/累加）+ 视图变换。alpha 感知，不破坏分层窗口 |
| `Assets/Generated/PmxTranspiled/<角色>/` | 转译产物（已忽略，一条命令可重建） |

## 改动时必须知道的事

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
