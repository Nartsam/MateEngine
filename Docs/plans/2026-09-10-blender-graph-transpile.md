# Blender 节点图转译复刻计划

> **一句话**：不再用"挑一个通用卡通着色器再调参数"的方式逼近 `.blend` 渲染预设，改为把预设的节点图**逐节点转译**成 HLSL。目标是数学精确复刻，不是视觉近似。
>
> 本文写于 2026-09-10，取代 `2026-06-22-liltoon-npr-render.md`（该计划连同其全部产物已归档到分支 `archive/liltoon-npr-tuning`）。

## Goal

给定「PMX 角色模型 + 配套 `.blend` 渲染预设」，离线生成 `.me`，使其在本软件中的渲染结果与在 Blender 中看到的一致。

先跑通「丽塔」一个角色，但**架构按通用管线设计**——转译器是一次性投入，为单角色写死不会更省事，而丽塔恰好是已知四个预设中最复杂的一个（唯一用到程序化噪声、唯一需要 6 张 LUT）。

## 为什么之前的路线必然失败（根因，勿重蹈）

两条独立的原因，任何一条都足以致命：

1. **导出环节从第一步就丢了算法。** `Tools/PmxPipeline/dump_render_preset.py` 遍历节点树时**从不记录 `node_tree.links`**，只按关键词（`"shade"`/`"高光"`/`"outline"` 等）猜测插槽含义，塞进一个 UTS2 形状的扁平字典。节点图的拓扑结构完全没有被导出过。之后所有工作都建立在这份有损摘要上。
2. **拟合维度不足。** 目标是一张 712 个计算节点的图，而 UTS2 / lilToon 只暴露二十几个可调项。参数拟合在理论上就无法收敛——这不是"没调好"，是无解。ADR-0010 记录的 UTS2 死路、以及后来 lilToon 迭代到 Phase 2.18 仍"总差一点"，都是同一个原因。

## 关键发现：这套预设是无光照纯函数

**这是整个新方案成立的前提，已实测确认。**

材质把 Color 直接接进 `Material Output ▸ Surface`。实测该写法与 Emission 输出**逐位相同**（`probe_raw_*.png` 与 `probe_emission_*.png` 哈希一致），而 Diffuse BSDF 在无灯环境下渲染为纯黑。预设文件里唯一的灯是 `energy = 0.0` 的太阳，World 为空。

光照是节点图自己伪造的——有个组就叫 `虚拟日光`，实质是：

```
ndl = dot(SUN * 2.8, N_world)
t   = saturate((ndl + 1) * 0.5)
s   = saturate(t * 0.5 + 0.5) ^ 2
ao  = (lightmap.g > 0.25) ? 1.0 : 0.5
out = s * ao
```

对四个预设**每一棵树**做全量 `bl_idname` 枚举确认：`ShaderToRGB`、`AmbientOcclusion`、`Bevel`、`LightPath`、`LayerWeight`、`Fresnel`、`BsdfPrincipled`、`VertexColor` 均为 **0 次**；四个 `worlds` 字典全为空。128 个材质中 **124 个是纯函数**，其余 4 个是每个预设里的 `表情` 材质（mmd_tools 导入残留，作者未套预设，在 energy=0 下求值为黑）。

**因此整个渲染是一个可以被完整搬走的片元函数**：输入是贴图、UV、法线、视线方向、三个方向向量；输出是最终颜色。不依赖 Blender 的光照引擎、阴影贴图、全局光照或任何屏幕空间缓冲。

## Architecture

```
渲染预设.blend ──[export_blender_graph.py]──> 节点图 JSON
                                                  │
                                        [转译器] ─┼─> HLSL 着色器
                                                  ├─> LUT 贴图（ColorRamp / RGB Curves / 视图变换）
                                                  └─> uniform 清单
PMX ──[现有 PmxPipeline]──> 网格/骨骼/BlendShape/物理
                              │
                    [描边烘焙] └──> 外壳子网格（焊接法线 + 权重 + 描边色烤进顶点流）
                                                  │
                                            [ExportMe] ──> .me
                                                  │
                              [差分验收] Blender 无头渲染 vs Unity 渲染 → 像素差异
```

**只替换材质环节。** `Assets/Editor/PmxPipeline/` 的 PMX 解析、网格构建、Humanoid、物理、`.me` 打包全部照用，`PmxStyleConfig.materialProfile` 是现成接入点。UTS2 映射保留作兜底。

## Tech Stack

- **Blender 3.6.19**（便携安装于 `D:\Program Files\Blender\3.6\`）。**必须是 3.6**：5.0.1 既没有 `eevee.use_bloom` 也没有 `node_group.outputs`，配套插件在其上无法运行。
- Unity 6000.2.6f2，Built-in 渲染管线（不迁 URP）。
- 现有 `Assets/Editor/PmxPipeline/`。

## 已核实的事实清单（勿重复推导）

### 预设的应用流程

由配套插件 `模型预设导入.py` 完成（副本存于 `Docs/RenderPreset/vendor/`，原件在 `D:\Program Files\Blender\3.6\3.6\scripts\addons\`）。用户操作是：导入 PMX → 物体模式选中模型 → N 键侧边栏 → RenderTools ▸ 导入预设 → 选 `.blend`。

插件实际做的事：

| 步骤 | 行为 |
|---|---|
| 材质配对 | 默认「剪去后缀」模式，把现有材质改名为 `actual_*`，按名字换上预设材质。**撞名问题由插件自动处理，无需手工重指材质槽** |
| 节点组导入 | 导入全部 node_groups；对每个 `users == 0` 的几何节点组，把输出接口的 `default_attribute_name` 设为接口名，并自动添加 NODES 修改器 |
| 对象链接 | 把预设里的 `灯光` 和 `面部定位` 链接进场景 |
| **面部定位绑骨** | **自动 parent 到骨骼 `頭`**（`parent_type='BONE'`），并做矩阵补偿。**所以脸部 SDF 阴影确实跟随头部转动** |
| **场景效果** | 见下，这一条极易被忽略 |

### 场景效果：真正生效的设置来自插件，不是 `.blend`

```python
scene.eevee.use_bloom        = True
scene.eevee.bloom_intensity  = 0.08
scene.eevee.bloom_color      = hex_to_rgb(0xFFE4D9)   # 暖白
scene.view_settings.view_transform = 'Filmic'
scene.view_settings.look           = 'High Contrast'
```

> **陷阱（本项目已踩过一次）**：`渲染预设.blend` 是**没有场景的纯材质库**，它内部存的那份 scene 设置（`use_bloom=False`、`look='None'`）是**死数据**，读它会得到错误结论。真正生效的是插件写进用户文件的上述设置。判断"什么设置在生效"必须以插件为准。

### 视图变换曲线（实测）

`Filmic` + `High Contrast`，逐通道独立（四组含高饱和与 HDR 值的测试，通道间偏差全为 `0.00000`）→ **可以烤成一张一维 LUT，在片元着色器末端逐像素套用，alpha 不受影响**。

| 线性输入 | look=None | **look=High Contrast（实际生效）** |
|---|---|---|
| 0.01 | 0.10111 | **0.05127** |
| 0.04 | 0.24117 | **0.17894** |
| 0.18 | 0.50021 | **0.50028** |
| 0.50 | 0.69496 | **0.74499** |
| 1.00 | 0.80723 | **0.86294** |
| 8.00 | 0.98315 | **0.99292** |

注意 **线性 1.0 只映射到 0.863，Blender 里画面永远不会出现纯白**。Unity 不做这层变换会直接烧成白色。历史上反复出现的"惨白"判定，有一部分根因在此，而不全是材质调坏了。

### 着色器需要的外部输入（只有三个）

由几何节点修改器写成网格命名属性，实测全网格恒定 → **在 Unity 里就是三个 `float3` uniform**：

| Uniform | 来源 | 默认值（Blender 轴） | 语义 |
|---|---|---|---|
| `_SunDir` | `灯光` 对象局部 +Z 的世界方向 | `(0, -0.866025, 0.5)` | 表面指向光源 |
| `_HeadBack` | `面部定位` 局部 +Y 的世界方向（属性名 `FRONT`） | `(0, 1, 0)` | **头部背向，不是面朝方向** |
| `_HeadRight` | `面部定位` 局部 +X 的世界方向（属性名 `RIGHT`） | `(1, 0, 0)` | 决定 SDF 左右翻转 |

后两个由头骨 `頭` 驱动。`FRONT` 的符号语义已实测核对：丽塔脸部材质面积加权平均法线为 `(0, -0.8955, -0.4451)`，模型面朝 Blender −Y，故 `FRONT` 指头后方。光在正面时 `dot(FRONT,SUN) ≈ -0.866` → 阈值 ≈0.067 → 脸几乎全亮，行为正确。

属性缺失时 Attribute 节点返回 `(0,0,0)`，会让所有点积退化为 0，**没有可用兜底**。

### 脸部 SDF 数据流

```
flip      = step(0, dot(SUN, RIGHT))
uv'.x     = flip ? uv.x : 1.0 - uv.x          // Blender 用 extension=REPEAT 的 frac(-u) 实现
facemap   = tex2D(Facemap, uv')                // 采样方式见「坑」一节
threshold = dot(FRONT, SUN) * 0.5 + 0.5
SDF       = (luma709(facemap) > threshold) ? 1 : 0
```

### 描边

几何节点做的实体化外壳，**不是着色器 pass**：

```
offset = N_welded × 0.0012 × W
```

- `N_welded` = 按 `1e-4` 距离**焊接之后重算**的顶点法线。实测 14132 个外壳顶点：与焊接后法线吻合中位数 `1.0000`（92.9% 完全吻合），与原网格法线仅 `0.9863`（44.9%）。**`Merge by Distance` 在这里的职责就是平滑法线**，把 UV 缝/材质缝上分裂的顶点焊回去，否则外壳会在缝上裂开（丽塔 26366 → 14346 点，焊掉 45%）。
- `0.0012` 是对象局部单位的全局常量，模型世界包围盒高 `1.5663`，即身高的 **0.0766%**。**没有任何缩放补偿**，Unity 导入若换了单位缩放必须同比换算。
- `W` = 手绘遮罩（头发用 `hairmask`，脸用 `描边遮罩`，均打包在 `.blend` 内）× 用户权重。两张图实测 100% 像素 `r==g==b`，约 92% 为 1.0、3~5% 为 0.0，其余是手绘过渡带。
- 部位划分靠 4 个 `Set Material` 串联，采用**顺序消耗**语义（`Material Selection` 在它所作用的那份几何上重新求值，已用最小用例实测验证）。丽塔实测：`edge_clothes 23748` / `edge_hair 2446` / `edge_skin 1570` 面，后两者恰等于 `头发` 和 `脸` 材质的面数。
- 颜色：`edge_skin` 与 `edge_hair` 是常量；`edge_clothes` 按 **Body_LightMap 的 alpha 通道**做五档 one-hot 查表（丝绸 0.90 / 金属 0.60 / 布料 0.35 / 皮革 0.20 / 皮肤 0.05，容差均 0.05）。
- `Flip Faces` + 四个 `edge_*` 材质全部 `use_backface_culling = True` = 标准反向外壳。
- 外壳规模：27764 面 / 14346 点，继承 `UVMap` 与全部 661 个骨骼顶点组，可正常蒙皮。

### 对模型侧的硬要求只有两条

1. 一套名为 `UVMap` 的 UV
2. 材质名与预设一致

丽塔 PMX 实测全部满足：PMX 2.0、`additional_uv = 0`、mmd_tools 导入后 `uv_layers = ['UVMap']`、26 个材质名 1:1 全对上。

**不需要顶点色**——这一点推翻了 ADR-0009 的适用范围：当年 HoyoToon 因 PMX 缺顶点色而失败的结论本身没错，但那是 HoyoToon 的需求，**这套 `.blend` 预设从不读顶点色**（四个预设 `VERTEX_COLOR` 节点数为 0）。

另外核实：预设**完全没有使用** PMX 的 `edge_color` / `edge_size` / 逐顶点 `edge_scale`。网格上存在 `mmd_edge_scale` 属性但无任何节点引用。

## 工作量

| 项目 | 数量 |
|---|---|
| 参与运算的节点（四预设合计） | 712 |
| 节点求值函数（类型 × 属性组合去重） | 50（排除 `表情` 的 BSDF 则 48） |
| 节点组实现（按结构去重） | **41** |
| 需要烘焙的 LUT | 7（ColorRamp 6 个全在丽塔 + RGB Curves 1 个四预设共用） |

> **别被"四个预设共享 27 个同名组"误导**——那只在名字层面成立。做名字无关的结构比对后，**9 个同名组在不同预设里结构不同**（例：`描边权重` 在丽塔是 11 节点/10 连线，在灯是 31 节点/30 连线）。实际要落地 41 份实现。
>
> **这正是必须写转译器而非手写着色器的最强论据**：手写意味着 41 份人工劳动且每换一个角色重来；转译器只是多跑几遍。

已确认的死代码，不计入工作量：`昼夜控制`（夜间分支被常量焊死，且唯一使用点无出边）、`法线贴图转换`、`matcap`（非 hair 版）、`Geometry Nodes` 的着色器侧——四个预设中均不可达。

## 必须写进转译器的坑

每一条都是"不知道就会静默出错、且极难定位"的类型。

1. **隐式类型转换用 Rec.709 亮度。** Blender 把 Color 转 Float 用 `0.2126r + 0.7152g + 0.0722b`，不是算术平均（实测 `RGB(0.1,0.6,0.9)` → `0.515360`，算术平均应为 `0.533333`）。可达连线里有 **23 条 `RGBA→VALUE`**，用错会全线偏色。
2. **`ShaderNodeMix` 的残留连线。** 有 **8 条连线**（每预设 2 条，**全在 `SDF` 组**）挂在非活动 `data_type` 的 `A_Float`/`B_Float` 插槽上，是作者切换过类型的残留。Blender 求值只看活动变体，转译器必须按 `data_type=RGBA` 只取 `A_Color`/`B_Color`/`Result_Color`。读错的表现是**人脸阴影完全不对**。
3. **人脸 SDF 图的采样方式是 `Smart`** → EEVEE 走 `texture_bicubic`（4 抽头 B 样条），而其输出直接进一个 CONSTANT ramp 做硬阈值（阈值因预设而异：0.3417 / 0.325 / 0.315）。用双线性会让阴影分界线的形状和位置可见偏移。**这是全图唯一一处采样滤波影响外观的地方。**
4. **ColorRamp / RGB Curves 必须按 EEVEE 的方式烤 257 项表。** EEVEE 自身实现为 `sampler_offset = 0.5/257.0`、`sampler_scale = 1.0 - 1.0/257.0`。照做即逐位一致；`257`（`CM_TABLE + 1`）是写死的魔数。2 停 CONSTANT 的 ramp 走 `valtorgb_opti_constant`，内联 `fac > edge ? c2 : c1` 即精确，不占 LUT。
5. **描边颜色查表的容差极窄。** 实测 Body_LightMap alpha 的实际取值为 `0.05 / 0.18 / 0.36 / 0.59 / 0.85`，其中 `0.85` 距档位中心 `0.90` 只有 `0.049`，而容差是 `0.05`。运行时若用双线性 + mipmap 采样，跨材质 ID 边界的混合值会掉出所有档位变成黑边。**对策：离线把描边颜色逐顶点烤成顶点色**，既消隐患又省一次采样。
6. **描边导出陷阱。** 外壳经 `Geometry to Instance` 变成实例，`bpy.data.meshes.new_from_object()` 和「转换为网格」**都会静默丢掉整个外壳**（输出仍是 30408 面、无 `edge_*` 材质）。必须先 `Realize Instances`。
7. **UV 缝上的遮罩采样是按平均 UV 取的。** `Capture Attribute` 在 POINT 域求值而 `UVMap` 是 CORNER 域，Blender 做算术平均（实测同一顶点两角 u=0.1 与 0.9 → 捕捉到 0.5000）。这是原作固有行为，移植时要**主动决定复刻还是修正**。
8. **两张描边遮罩标了 sRGB**，而几何节点的 Image Texture 读的是色彩管理后的 scene-linear 缓冲，手绘中灰读出来约 0.216 而非 0.5。近二值的遮罩下影响很小，但要逐位一致就得复刻。

## 待解决的设计问题

**Bloom 与透明分层窗口。** 预设实际启用了 bloom（强度 0.08、暖白 `#FFE4D9`），参考截图中肩臂轮廓外的辉光清晰可见，是观感的一部分。但 bloom 是屏幕空间效果，而本项目已记录「全屏后处理会破坏分层窗口 alpha」（ADR-0010）。

区别在于：当年破坏 alpha 是因为用了 Unity 自带后处理栈，它直接丢弃 alpha。辉光要溢出到模型轮廓之外，意味着 **alpha 也要相应向外扩散并带部分透明度**——分层窗口支持逐像素 alpha，所以原理上可行，但必须自己写 alpha 感知的 bloom，不能用现成方案。

三个选项待定：(a) 自写 alpha-preserving bloom；(b) 先不做 bloom，接受这一点差异；(c) 把 bloom 限制在模型自身渲染纹理内再合成。**建议主线先不做，等转译主体验收通过后再单独攻。**

## 分阶段实施

> 每阶段都有明确的**客观验收判据**，不再依赖肉眼判断。

### Phase 0：基础设施与差分闭环（先做，它是后面所有阶段的验收工具）—— **已完成 2026-09-18**

- [x] 把 `export_blender_graph.py` 接进管线，产出丽塔的完整节点图 JSON（产物在 `Docs/RenderPreset/graphs/`）
- [x] 写 Blender 无头脚本：加载 PMX → 应用预设（复刻插件行为）→ 固定相机/光照 → 渲染基准图
      —— `Tools/PmxPipeline/blender_apply_preset.py`
- [x] 写 Unity 侧对应的固定相机渲染 —— `Assets/Editor/PmxPipeline/MERefRenderer.cs`
- [x] 写像素差异比对脚本，能输出逐材质、逐区域的差异统计 —— `Tools/PmxPipeline/compare_renders.py`
- [x] 一条命令跑完三环 —— `Tools/PmxPipeline/run_ref_diff.ps1`（全程约 20 秒）
- **闸门：已通过。**

#### 闭环怎么跑

```
pwsh Tools/PmxPipeline/run_ref_diff.ps1 -Character 丽塔 -View front
```

默认走转译材质（`-Profile transpiled`），全程约 50 秒：转译 → Blender 基准 →
Unity 对照 → 差异统计。常用开关：

| 开关 | 作用 |
|---|---|
| `-Profile uts2` | 换回 UTS2 兜底材质，用来对照基线 |
| `-ExportGraph` | 重新从 `.blend` 导出节点图与解包贴图（换预设或改导出器时才需要） |
| `-SkipBlender` / `-SkipUnity` | 复用上一次的某一侧 |

产物落在 `Build/RefDiff/`（已被 .gitignore 忽略）。Python 依赖装在仓库内的
`Build/.venv`，不碰全局环境。

把「转译误差」和「缺 bloom 的误差」分开量：

```
blender.exe -b --factory-startup -P Tools/PmxPipeline/blender_apply_preset.py -- \
    --pmx <...> --preset <...> --out <...> --res 1024 --view front \
    --bg transparent --no-bloom
```

#### 建立起来的对齐保证

- **坐标映射是推导出来的，不是调出来的**：mmd_tools 用 `Vector(pv.co).xzy * 0.08`，
  Unity 侧 `PmxMeshBuilder.Conv` 用 `(-px, py, -pz) * 0.08`，消去 PMX 坐标得
  `U = (-B.x, B.z, -B.y)`。yaw 两边通用。
- **取景不各算各的**：Unity 侧直接读 Blender 报告里的 `cam` 段。
- **姿势对齐**：`PmxHumanoid` 会在捕获 bindposes 之后把手臂掰成 T 姿势（Humanoid
  Avatar 需要），prefab 因此停在 T 姿势。渲染时把骨骼写回 bindpose 即精确还原 PMX
  原始姿势，prefab 本身不动。
- **背景不进统计**：Blender 的世界背景同样要过 Filmic（给 808080 渲出来是 8C8C8C）。
  两边都用透明背景渲，统计只在 alpha 轮廓交集内做。

#### 基线数值（UTS2 材质，Phase 1 开始前）

| 指标 | 数值 |
|---|---|
| 轮廓 IoU | 0.9677 |
| 仅基准有 / 仅对照有 | 4186 px / **0 px** |
| 交集内 ΔE76 平均 / 中位 / P95 | 11.00 / 9.40 / 21.20 |

「仅对照有 0 px」是对齐精确的硬证据：Unity 轮廓完全落在 Blender 轮廓之内，任何
相机或姿势偏移都会让两个方向同时出现独有像素。那 4186 px 就是 Blender 有而 Unity
还没有的描边。

#### 已知局限

- 逐材质分区用的是 Unity 侧的 ID 通道。Blender 压在轮廓**内侧**的描边像素会被算到
  它盖住的那个材质头上——这是有意为之，描边差异本来就该记在被影响的材质名下。
- 主渲染有抗锯齿、ID 通道没有，材质交界处约 4% 像素无法归类，统计时直接跳过。
- 正面视角只能看到 26 个材质里的 20 个，其余被遮挡。要覆盖全部材质需要多角度跑。

### Phase 1：转译器骨架 + 最简材质 —— **已完成 2026-09-19**

- [x] 实现节点求值函数框架与图遍历（拓扑排序、组展开、隐式类型转换）
      —— `Tools/PmxPipeline/graph_ir.py`
- [x] LUT 烘焙器 —— 不在 Python 里重算 B 样条与贝塞尔，改由 `export_blender_graph.py`
      在 Blender 里用 `ColorRamp.evaluate` / `CurveMapping.evaluate` 烤表，写进
      节点图 JSON 的 `luts` 段（formatVersion 3）。丽塔实得 7 张表
- [x] 视图变换 LUT（Filmic + High Contrast）—— 用实测的
      `Docs/RenderPreset/view_transform_filmic_high_contrast.csv`，存成
      「display sRGB 的线性值」，直接由 sRGB 渲染目标编码回去
- [x] 代码生成器：IR → HLSL —— `Tools/PmxPipeline/transpile.py`
      + `Tools/PmxPipeline/hlsl/me_nodes.hlsl`（节点求值库，内联进每个着色器）
- [x] Unity 侧装配 —— `Assets/Editor/PmxPipeline/METranspiledMaterials.cs`
- **闸门：已通过，且远超预期**——不是「单个平涂材质」，是 **30/30 个材质全部转译**。

#### 验收数值（丽塔，front，1024²）

| 指标 | Phase 0 基线（UTS2） | 转译后 | 转译后（两边都关 bloom） |
|---|---|---|---|
| 交集内 ΔE76 平均 | 11.00 | 4.17 | **2.17** |
| 交集内 ΔE76 中位 | 9.40 | 1.41 | **0.68** |
| 交集内 ΔE76 P95 | 21.20 | 18.72 | **10.97** |
| RGB 平均绝对误差 | — | 6.9/255 | **3.6/255** |
| 轮廓 IoU | 0.9677 | 0.9676 | 0.9676 |

轮廓 IoU 没动是对的：差的 4201 px 全是 Blender 有而 Unity 还没有的**描边**（Phase 3）。

逐材质（关 bloom 后）中位 ΔE：`脸 0.00`、`皮肤 0.46`、`手套 0.48`、`袜子 0.59`、
`眼白 0.61`、`前裙 0.68`——大面积材质已进入「肉眼无差」区间。

#### 最重要的发现：bloom 现在是第一误差源

关掉 bloom 后平均 ΔE 从 4.17 掉到 2.17、中位从 1.41 掉到 0.68，**一半的剩余误差来自
没做 bloom**。逐材质取样也全部指向它：**所有 24 个材质在 Unity 侧都偏暗**，且偏暗
幅度与「该材质周围有多亮」成正比——`袜子`（大片暗色）只差 −0.9，`衣服`（暗紫贴着亮
皮肤）差 −19.7，`眉毛`（暗细条嵌在亮脸上）差 −75.7。这是 bloom 的典型特征，不是着色
器算错。

**因此把「决定 bloom 方案」从 Phase 4 提前**，见下方新的 Phase 2.5。诊断入口已加好：
`blender_apply_preset.py --no-bloom` 可以渲一张不带 bloom 的基准图，用来把「转译本身
的误差」和「缺 bloom 的误差」分开量。

#### 顺带查出并修掉的两个静默错误

1. **Reroute 断链（严重）。** `graph_ir.py` 按名字找 Reroute 的输入插槽 `Input`，
   但 Blender 的 Reroute 插槽标识符**不固定**：丽塔 18 个 Reroute 里 13 个是
   `Input`、3 个是 `input.001`、2 个是 `input`。5 个断链的后果是上游被当成「没接
   线」→ 取插槽默认值（黑）→ **16 个材质的整条 `调色` 链被算成黑色**，而展开过程
   一条告警都不报。修法是「Reroute 只有一个输入，取唯一那条连线」。
   修完 `HUE_SAT` 从 15 处涨到 31 处、`CURVE_RGB` 从 8 处涨到 24 处。
2. **非活动插槽上的残留连线。** 已按计划在 `graph_ir.py` 里按活动 `data_type` 取插槽，
   并对非活动插槽上的连线**报警而非忽略**。丽塔实得 2 条，全在 `脸` 的 `SDF` 组，
   与事先的预判一致。

#### 还欠的三处（都已在产物里显式标注）

- `头发` 的 Noise Texture（发丝颗粒）用 0.5 均值占位。Scale 2000 属于逐像素级高频，
  位置差一点噪声就完全去相关，真要复刻得连 Blender 的整数哈希一起搬。
- `胸衣` 的 Voronoi（裙料上的细闪）用常量占位。关 bloom 后该材质 ΔE 只剩 2.02，
  说明它此前 13.85 的误差主要来自 bloom，Voronoi 本身是小头。
- `Texture Coordinate ▸ Generated` 用物体坐标代替（缺包围盒归一化）。上面两项要做
  真的，得先把它补上。

#### 架构决定：Python 出着色器，Unity 只负责绑定

#### 架构决定：Python 出着色器，Unity 只负责绑定

转译器写在 Python 侧（`Tools/PmxPipeline/transpile.py`），产出：

```
Assets/Generated/PmxTranspiled/<角色>/shaders/M??.shader   每材质一个（已 gitignore）
Assets/Generated/PmxTranspiled/<角色>/<角色>.bindings.json 装配清单 + LUT 浮点数据
Build/RenderPreset/images/<角色>/                          从 .blend 解包出的 19 张贴图
```

落地时与原设想的三处偏差：

- **一材质一着色器**，不是一个大着色器。常量直接内联成字面量，贴图属性按需声明，
  没有 uber-shader 的分支与变体问题。30 个无光照着色器的编译开销可以忽略。
- **节点求值库内联进每个着色器**而不是 `.cginc`：生成物落在带中文名的目录下，
  相对 include 路径多一层不确定性，而库只有两百多行。
- **LUT 走 JSON 里的浮点数 + 运行时建 `RGBAFloat` 贴图**，不烤 PNG：表值在线性域，
  8 位量化在 Filmic 曲线最陡的暗部会放大成可见色差，16 位 PNG 又要额外约定导入设置。

Unity 侧只读 `bindings.json` 建材质，不参与图分析。这样分工的理由：

- 图分析与代码生成迭代频繁，Python 改完立刻能跑；放 Unity 里每次都要等编译。
- 节点图 JSON 本来就是 Python（Blender）产出的，同一侧处理省一次格式往返。
- Unity 侧的职责收窄成"按清单装配"，正好接进既有的 `materialProfile` 扩展点
  （Phase 4）。

**`bindings.json` 必须带混合状态**，不能只有贴图和颜色——`眼睛2` 那个 bug 的一半
原因就是队列与混合模式错了（见上方 Phase 2 核验项）。

#### 节点类型清单（已全部实现）

由 `graph_ir.py` 按需展开后实测得出（丽塔全材质，`graph_ir.py <graph.json>` 可随时重跑）：

```
MATH 274   MIX 188   IMPLICIT_CONV 137   VECT_MATH 86   VALTORGB 82
TEX_IMAGE 63   ATTRIBUTE 48   MAP_RANGE 40   NEW_GEOMETRY 32   HUE_SAT 31
SEPARATE_COLOR 31   CURVE_RGB 24   NORMAL_MAP 15   MIX_RGB 6   MAPPING 3
TEX_COORD 3   UVMAP 2   COMBXYZ 2   VECT_TRANSFORM 1   TEX_NOISE 1   TEX_VORONOI 1
BSDF_TRANSPARENT 2   MIX_SHADER 2   BSDF_DIFFUSE 1   BSDF_GLOSSY 1
```

BSDF_* 与 MIX_SHADER **全部来自 `表情` 一个材质**——它走 mmd_tools 的
`MMDShaderDev`（真 BSDF），是预设作者唯一没做风格化的材质。

闭包模型只用「辐射 + 透射」两项就覆盖了这四种节点，并与 EEVEE 的合成方式一致：
`alpha = 1 - avg(transmittance)`、`RGB = radiance`。两条实测探针为它背书：

- `Color` 直连 Surface，四种 blend_method 下输出 alpha **恒为 1**——颜色的 alpha
  **不会**变成材质透明度（这个否定结论同样重要，它排除了「眼部细条要靠贴图 alpha
  抠形」的猜想）。
- 纯 Transparent BSDF（Color = 0.2724 灰）单独渲：RGB=0、alpha=0.7294；压在白色自
  发光面前：0.5569 = sRGB(0.272394 × 1.0)。即透射项是**乘在背后颜色上**的。

由此也定出一处待办：Unity 侧对 BLEND 材质用的是显示域混合，而 EEVEE 是**线性域合成
后再过视图变换**。要彻底一致就得渲到离屏线性缓冲、末端统一做一次变换——这与 bloom
的做法是同一件事，一并放进 Phase 2.5。

#### 已补上的稳健性缺口

- **`is_active_output`**（已加进 `NODE_PROPS`）。
- **`interpolation` 此前根本没被导出。** 旧的节点图 JSON 里 TEX_IMAGE 全都没有这个
  键——`NODE_PROPS` 是后来补的，而图没有重导。人脸 SDF 图用的 `Smart` 因此被当成
  双线性。现已重导，实测 62 处 `Linear` + 1 处 `Smart`，与事先的预判完全一致。
  教训：**导出器改了就必须重跑导出**，JSON 里缺键和值为空长得一样。
- **`factor_mode`**（ShaderNodeMix 的 Factor 是统一还是逐分量）。

#### 基准 PMX 的选定

`examples/丽塔/删披风删骨骼.pmx` 与 `models/特工丽塔/删披风删骨骼.pmx` 内容完全相同
（sha256 前缀 `ddfa6d57`），但 **`删披风删骨骼_修复.pmx` 是另一份文件**（`c80d8cd1`）。
差分两侧必须用同一份，现统一用 **`删披风删骨骼_修复.pmx`**（较新，且 Blender 侧一直
用的是它）。`run_ref_diff.ps1` 的默认值已按此设定。

### Phase 2：逐特性推进

按依赖顺序，每一步都用 Phase 0 的差分闭环验收：

- [ ] `虚拟日光` + `布林冯光照模型`（半兰伯特 + 高光）
- [ ] `ligthmap.*` + `ramp.*`（LightMap 驱动的卡通阴影）
- [ ] `法线贴图`（注意：`法线贴图转换` 组是死代码，实际走的是别处）
- [ ] `matcap.*`（视空间球面高光，`VECT_TRANSFORM OBJECT→CAMERA`，注意 Blender 相机空间是 −Z 朝前）
- [ ] `金属`
- [ ] `SDF` + `SDF.tex`（脸部阴影，注意坑 2 与坑 3）
- [ ] `alpha采样`
- [ ] 丽塔专有：`hairP`（Noise）、`胸衣`（Voronoi + Generated 坐标）
- **闸门**：每一步的差异统计单调下降，且能定位到具体材质。

> **核验项已关闭（2026-09-19）**：眼睛问题在转译后**自愈**，两处成因都被消掉了——
> 颜色不再来自 `PmxMaterialMapper` 写死的浅蓝，而是转译出的 `(0,0,0)` 近黑；队列也
> 按 Blender 的 `blend_method = BLEND` 排进了透明队列（3000+）而不是 alpha-test 2458。
> 三联对比图（`Build/RefDiff/对比-脸部-三联.png`）里右侧旧 UTS2 那只眼睛顶部的浅蓝
> 缺块清晰可见，中间转译版没有。逐材质 ΔE：`眼睛2` 从 51.99 降到 10.72（关 bloom 后）。
> 以下为当时的分析记录，保留备查。
>
> **原核验项（2026-09-18 用户报告）**：Phase 0 基线里角色眼球顶部被一层浅蓝膜盖住，
> 看着像缺了一块。已查明该层是材质 `眼睛2`——PMX 里它本就无贴图、`alpha=0.300`，
> 预设给的是 `(0, 0, 0, 0.3)` 近黑薄膜，而 `PmxMaterialMapper.cs:525` 把它按关键词
> 归类成「眼睛」后刷成了写死的 `(0.82, 0.88, 1.0)` 浅蓝——正好相反的两端。
>
> 用户确认 PMXEditor 里模型本身正常，且 2026-06-25 的旧 Unity 截图里眼睛也正常，
> 说明某个历史阶段它是对的。已排除风格配置（`丽塔.style.json` 无眼部覆盖），成因未查明。
>
> **绘制顺序已查实**（用户 2026-09-18 回忆 + 材质数据佐证）。眼部材质的渲染队列：
> `脸 2000 / 眼白 2002 / 眼睛 2006 / 眼睛1 2007`，而 **`眼睛2` 是 2458**——它被排进
> alpha-test 队列，在其余眼部之后绘制，且走的是不透明裁剪语义而非真 alpha 混合。
> 于是它不是「淡淡压一层」而是「盖上去一块」，配合那个写死的浅蓝色，就成了用户看到的
> 「眼球顶部缺一块」。
>
> **根因是两件事叠加**：颜色错（浅蓝 vs 近黑）+ 混合模式与队列错（alpha-test 2458
> vs 预设的 `blend_method = BLEND`）。转译器必须把 `blend_method` / `shadow_method` /
> `use_backface_culling` 一并落到 Unity 材质的队列与混合状态上，不能只管颜色。
>
> 逐材质表里 `眼睛2` 那行（基线 ΔE 51.99）是客观判据。

### Phase 2.5：离屏线性缓冲 + bloom（**由 Phase 4 提前至此**）

提前的依据见 Phase 1 的验收数值：关掉 bloom 后平均 ΔE 直接减半。它已经不是「观感上
的加分项」，而是当前最大的单一误差源。

两件事其实是同一件事——都需要**渲到离屏线性 HDR 缓冲，在末端做一次逐像素合成**：

- 视图变换目前逐材质在片元末端做。对不透明材质这完全正确；但对 `眼睛2` / `裙内侧`
  这两个 BLEND 材质，Unity 是在显示域混合，而 EEVEE 是线性域合成后再变换。
- bloom 必须在做视图变换**之前**、在线性域上取亮部。

- [ ] 渲到 `RenderTextureFormat.ARGBHalf` 的线性缓冲，着色器只输出 scene-linear 辐射
      与 alpha，不再各自做视图变换
- [ ] 末端一个 alpha 感知的合成 pass：bloom（阈值/半径/强度/暖白色按插件写入的
      `intensity 0.08`、`color #FFE4D9`）→ 视图变换 → 输出
- [ ] 分层窗口下验证 alpha 不被破坏（这正是 ADR-0010 里踩过的坑，区别在于当年用的是
      Unity 自带后处理栈，它直接丢 alpha；自写的 pass 可以让 alpha 跟着辉光一起外扩）
- **闸门**：两边都开 bloom 时的差异，逼近现在「两边都关 bloom」的水平（平均 ΔE 2.17）。

### Phase 3：描边

- [ ] 离线外壳烘焙：删除排除集（POINT 域语义）→ 焊接 → 重算法线 → 采样遮罩得 `W` → 划分 3 个子材质 → 烤描边颜色进顶点色
- [ ] 顶点着色器外扩 + `Cull Front` 的 unlit 材质
- [ ] 骨骼权重与 BlendShape 增量按焊接组取平均
- **闸门**：轮廓位置与粗细的差异进入可接受范围；无接缝裂口、无黑边。

### Phase 4：集成与验收

- [ ] 接进 `materialProfile`，走 `ExportMe` 出 `.me`
- [ ] AssetBundle 变体不剥离（自生成的着色器变体确定，风险低于 lilToon）
- [ ] App 内实测：透明窗口、动画、移动窗口下渲染稳定
- [ ] 决定 bloom 方案
- [ ] 写 ADR，更新 `ARCHITECTURE.md` / `PROGRESS.md`

## 风险与回退

- **最大风险是量而非难度**：41 份节点组实现、712 个计算节点。对策就是转译器形态本身。
- **第二是 Filmic/look 容易被漏**，且在透明窗口约束下不能用标准全屏 tonemap 通道。对策：逐像素在片元着色器末端做。
- **第三是那 8 条残留连线**，静默读错且极难定位。对策：转译器显式按活动 `data_type` 取插槽，并对非活动插槽上的连线**报警而非忽略**。
- 丽塔的 Voronoi / Noise / bicubic 是局部精度问题，不阻塞主线，可留到后期。
- **全程保留 `materialProfile = "uts2"` 兜底**，任何改动不得破坏它。

## 与旧文档的关系

- `Docs/plans/2026-06-22-liltoon-npr-render.md`：已归档到 `archive/liltoon-npr-tuning`，结论作废，根因见本文开头。
- ADR-0008 中「`.blend` 渲染预设只作为材质参数来源」的定位是错的——预设不是一组参数，是一个函数。已在 ADR-0011 中修正。
- ADR-0009（HoyoToon 因 PMX 缺顶点色而失败）事实正确但**不适用于本路线**，本预设不读顶点色。
- ADR-0010（UTS2 调参死路）结论正确，本文补充了更深一层的根因（维度不足 + 导出丢算法）。
