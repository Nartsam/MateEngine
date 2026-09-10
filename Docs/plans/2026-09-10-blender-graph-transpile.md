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

### Phase 0：基础设施与差分闭环（先做，它是后面所有阶段的验收工具）

- [ ] 把 `export_blender_graph.py` 接进管线，产出丽塔的完整节点图 JSON（已有产物在 `Docs/RenderPreset/graphs/`）
- [ ] 写 Blender 无头脚本：加载 PMX → 应用预设（复刻插件行为）→ 固定相机/光照 → 渲染基准图
- [ ] 写 Unity 侧对应的固定相机渲染
- [ ] 写像素差异比对脚本，能输出逐材质、逐区域的差异统计
- **闸门**：能对同一组参数各出一张图并给出差异数值。此时差异必然很大，这不重要——重要的是**闭环能跑**。

### Phase 1：转译器骨架 + 最简材质

- [ ] 实现节点求值函数框架与图遍历（拓扑排序、组展开、隐式类型转换）
- [ ] 先只支持一条最短路径：`基础贴图 → 调色 → 输出`
- [ ] LUT 烘焙器（ColorRamp / RGB Curves，257 项）
- [ ] 视图变换 LUT（Filmic + High Contrast）
- **闸门**：单个平涂材质的 Unity 渲染与 Blender 基准图差异进入个位数灰阶。

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
