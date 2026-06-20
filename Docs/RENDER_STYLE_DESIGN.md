# 渲染风格架构设计：通用 VRM + App 内置可切换风格

> 状态：**草案（待审定）**　｜　最后更新：2026-06-20
> 关联：`Docs/DECISIONS_RECORD.md` ADR-0008（PMX 离线管线）、本文对应 ADR-0009（提案）
> 前置事实：仅供个人自用，**不考虑着色器/资源许可证**；着色器选型只看"效果最好"。

---

## 1. 背景与目标

### 现状问题
- 当前 PMX 管线把**渲染材质烘进 `.me`**，导致：①改风格要对每个模型重跑构建；②`.me` 是本项目专有格式，模型不通用；③用 UTS2 近似 HoYo 游戏着色器，颜色/分区始终对不上（UTS2 没有 HoYo 的 LightMap 多通道、脸部 SDF 等语义）。
- 已查实：主场景相机**未挂 PostProcessLayer**，Built-in 后处理 Volume 全部失效。

### 目标
1. **模型文件走通用 VRM**（可在任意 VRM 应用加载），不与本项目深度耦合。
2. **渲染风格放进软件**：内置数套可切换风格（MToon 默认、仿 HoYo 等），加载时套用、设置里实时切换；风格与模型文件**解耦**（换风格不重生成模型）。
3. **仿 HoYo 效果尽量逼近原作**（终点目标 B）。
4. 对**外来的普通 VRM**也能套通用 toon 风格（优雅降级，不报错）。
5. 渲染风格的切换功能放在“设置菜单”中的“一般”组内，“画质”选项的下方。

### 非目标
- 不追求把 HoYo 效果做成可在**其它** VRM 应用里也正确显示（那需要对方也有同款着色器）。本设计只保证：VRM 在别处按 MToon 正常加载，**仿 HoYo 仅在本软件内**精确还原。
- 不重写桌宠的状态机/IK/交互等既有系统。

---

## 2. 关键约束与"那个绕不开的前提"

**仿 HoYo 要准，依赖 HoYo 专有输入贴图，而标准 VRM 不携带它们。**

- 标准 VRM（MToon）只带：底色、法线、自发光、MToon 阴影/ramp/matcap/轮廓。
- HoYo 观感由这些**额外图**驱动：`LightMap`（R=AO、G=阴影阈值、B=高光强度、A=高光/ramp 选择，按 HI3 约定）、`MetalMap`、**脸部 SDF/Facemap**、`ramp` 渐变图。
- **关键事实（已核实）**：当前 模之屋 模型源目录里**只有 MMD 简化贴图**（mc1/mc3、toon2/3、脸.png、衣服a.png、表情.png…）；HoYo 游戏贴图（`Body_Color`/`Body_LightMap`/`Avatar_Tex_MetalMap`/`Avatar_Girl02_Facemap`）**一张都不是散文件，全部打包在 `丽塔未变身预设.blend` 里**，需经 Blender 无头抽取。

**解法**：PMX→VRM 导出时把这些额外图**打包进 VRM**（作为额外 glTF 贴图 + 一段元数据标明每个材质用哪几张）。这样的 VRM 在别处仍按 MToon 正常加载（通用性保留），额外图只被本软件的仿 HoYo 风格读取。

### .blend 渲染预设的新角色（架构变更后）
- 旧角色：被 `dump_render_preset.py` 解析节点参数 → 映射到 UTS2 烘进 `.me`。
- **新角色：HoYo 游戏贴图的唯一来源**。Blender 无头抽图仍是 PMX→VRM 的**必需前置步骤**，产物从"UTS2 提示"变为"打包进 VRM 给 HoYo 着色器使用的真实贴图"，并提供"材质→各图"的对应关系写入 VRM 元数据。同时 .blend 渲染结果仍作为**视觉验收基准**。
- 例外：若某模型的这些图本就是 PMX 旁的散文件，则对该模型 .blend 可省；当前模型不可省。

---

## 3. 总体架构

### 职责拆分

| 层 | 承载内容 | 备注 |
|---|---|---|
| **模型文件（VRM 1.0）** | 网格、骨架、Humanoid、表情/口型、**物理=VRM10 SpringBone**、底色/法线等标准贴图、**额外 HoYo 贴图 + 元数据** | 通用、可移植 |
| **软件（App 运行时）** | **RenderStyleManager** + 数套风格实现 + 风格着色器（vendored）+ 设置项 | 加载时套用、可实时切换 |

### 加载时数据流

```
用户加载模型(VRM/.me)
   └─ VRMLoader.FinalizeLoadedModel(model)         ← 既有统一接入点（已遍历所有 SkinnedMeshRenderer）
        └─ RenderStyleManager.Apply(model)
             ├─ 读取当前风格(来自设置: "mtoon" | "hoyo" | ...)
             ├─ 收集每材质的源贴图( MToon 槽位 + VRM extras 里的 HoYo 贴图元数据 )
             ├─ 为每个材质创建/替换为风格着色器材质，绑定对应贴图槽
             └─ 缓存原材质以便切换/还原
   设置里切换风格 → RenderStyleManager.Reapply(currentModel)
```

---

## 4. App 内渲染风格系统（RenderStyleManager）

### 4.1 接入点
- 复用 `Assets/MATE ENGINE - Scripts/VRMLoader/VRMLoader.cs` 的 `FinalizeLoadedModel(model, path, bundle)`：在它启用 SMR 之后调用 `RenderStyleManager.Instance.Apply(model, payload)`。
- `payload` 携带从 VRM extras 解析出的 HoYo 贴图元数据（见 §5.2）；`.me`/普通 VRM 无此元数据时为空，风格据此降级。

### 4.2 风格接口

```csharp
public interface IRenderStyle {
    string Id { get; }                 // "mtoon" / "hoyo_hi3" / "toon_generic"
    string DisplayName { get; }
    // 把 model 上所有 SMR 的材质换成本风格的着色器材质。
    void Apply(GameObject model, RenderStyleContext ctx);
    // 还原为加载时的原始材质（切换风格/卸载时用）。
    void Restore(GameObject model);
}

// 每材质的源信息：标准贴图 + 可选 HoYo 额外贴图（来自 VRM extras）。
public sealed class RenderStyleMaterialSource {
    public string materialName;
    public Texture baseColor, normal, emission;     // 标准 VRM/MToon 可得
    public Texture lightMap, metalMap, faceSdf, ramp; // HoYo 额外图，缺则 null
    public Color baseTint; public bool isFace, isHair, isSkin; // 分类提示
}
```

- `RenderStyleManager`：注册表（Id→IRenderStyle）+ 当前选择（来自设置）+ `Apply/Reapply/RestoreAll`。
- **缓存原材质**：Apply 前记录每个 SMR 的 `sharedMaterials`，存到一个挂在 model 上的轻量组件（如 `AppliedRenderStyleState`），供 Restore/切换使用，避免污染原始资产。

### 4.3 风格清单（首批）

| Id | 着色器 | 输入需求 | 缺图时降级 |
|---|---|---|---|
| `mtoon`（默认） | VRM MToon（原样保留） | VRM 自带 | —（什么都不改）|
| `hoyo_hi3` | vendored HoYo HI3 着色器 | LightMap/MetalMap/FaceSDF/ramp | 退化为通用 toon（仅底色+法线）|
| `toon_generic`（可选） | 现 UTS2 / 简化 toon | 底色+法线 | —— |

- 默认 `mtoon`：不动 VRM 原材质，保证任何模型都能正常显示。
- `hoyo_hi3`：仅当模型带 HoYo 元数据时给出精确效果；否则提示并降级。

### 4.4 设置与持久化
- 在设置里新增"渲染风格"下拉（值=风格 Id）。存入 `UserData/settings.json`（走 `PortablePaths`，遵守硬约束，不写外部路径）。
- 切换时对 `currentModel` 即时 `Reapply`，无需重载模型。
- 可选：**逐模型覆盖**（某模型固定用某风格），存在头像库条目里；首版可先做全局，后续再加。

---

## 5. 模型格式：VRM 载荷

### 5.1 物理 → VRM10 SpringBone
- 复用现 `PmxPhysics` 的链/根/碰撞体**检测逻辑**，输出从 `DynamicBone` 改为 **`VRM10SpringBone`**（项目已内置 VRM10）：
  - 动态刚体链 → SpringBone joint 链；分类(Hair/Skirt/Breast) 的阻尼/刚度参数映射到 SpringBone 的 `stiffness/drag/gravity`。
  - 腿/髋胶囊 → `VRM10SpringBoneCollider` + ColliderGroup。
- 物理随模型走，VRM 原生、可移植；本软件和其它 VRM 应用都能用。

### 5.2 额外 HoYo 贴图打包
- 把 `LightMap / MetalMap / FaceSDF / ramp` 作为**额外贴图**写入 glTF（UniGLTF 支持导出额外 image/texture）。
- 元数据写进 glTF `extras`（或自定义扩展键 `MATEENGINE_hoyo_maps`），结构：
  ```json
  { "version": 1, "profile": "hoyo_hi3",
    "materials": [
      { "name": "皮肤", "lightMap": <texIndex>, "metalMap": <texIndex>, "ramp": <texIndex> },
      { "name": "脸",   "faceSdf": <texIndex> }
    ] }
  ```
- 加载侧：在 VRM 导入流程里解析该 extras，构造 §4.2 的 `RenderStyleMaterialSource.lightMap/...`。
- 兼容性：不认识该 extras 的应用直接忽略，VRM 仍按 MToon 正常显示。

### 5.3 与 `.me` 的关系
- PMX 管线**主输出改为 VRM**；`.me` 路径保留（现有 mod 兼容），且 `RenderStyleManager` 对 `.me` 加载的模型同样生效（只是 `.me` 不带 HoYo 元数据时按降级处理，或在 `.me` 内自带额外图的清单）。
- 是否完全弃用 PMX→`.me`：建议保留为可选导出，默认产 VRM。

---

## 6. HoYo 着色器选型（只看效果）

- 目标模型是**崩坏3（HI3）**（贴图前缀 `Avatar_Rita_C7`）。
- 推荐 **HoyoToon**（社区项目，覆盖原神/星铁/崩坏3/绝区零，对 HI3 的 LightMap/Material 约定支持最完整，公认还原度最高）。HI3 变体直接吃我们手里的 `Body_LightMap/MetalMap/Facemap`。
- 备选：原神向的 **Festivity / Genshin-Impact-Unity-Shader**（最早即 Built-in，集成最省心）及其各 fork——但它偏原神，对 HI3 贴图布局不一定贴合。
- **选定：HoyoToon（HI3 变体）**，理由=效果最好且对口本模型。许可证按用户要求不纳入考量。

### 选型待验证（Phase 0）
1. **Built-in RP 兼容**：本项目是 Built-in（非 URP）。需确认所选 HoyoToon 变体有 Built-in 通路（HoyoToon 有 Built-in 支持，但需核实具体版本）。
2. **Unity 6000.2.6f2 编译**：核实着色器在该版本无报错（宏/关键字/CBUFFER 等）。
3. **`.me`/Player 构建里的着色器变体剥离**：运行时换材质要求着色器变体进 build 不被剥离（加 `Always Included Shaders` 或 shader variant collection）。
4. **脸部 SDF 方向**：HI3 脸 SDF 依赖头骨朝向/光向，需核实在桌宠自由旋转下表现正常。

---

## 7. 既有改动的迁移

| 既有 | 去向 |
|---|---|
| `PmxPhysics`（链/根/碰撞体检测） | **逻辑复用**，输出改 VRM10 SpringBone |
| `PmxMaterialMapper`（UTS2 映射） | **平移到运行时风格**（`toon_generic` 或弃用）；HoYo 由新风格实现 |
| `PmxStyleConfig`（skinWarmth/brightness/outline/rim 等 shader 无关旋钮） | **保留**，作为"每风格/每模型"的可调参数（MToon、HoYo 都能用）|
| `PmxModelRenderProfile`（后处理组件） | 重定位：后处理改为**场景级**（见 §8），或并入风格系统 |
| Humanoid/BlendShape 构建 | VRM 原生，直接复用 |

---

## 8. 后处理（鲜艳感的正确杠杆）
- 给主场景相机加 **PostProcessLayer**（一次性场景改动），并放一个**场景级全局 Volume**（Bloom + 中性 tonemap + 适度饱和/对比）。
- 这样后处理对所有模型生效、与模型文件解耦、可在设置里开关。
- 这是参考图"鲜艳"的真正来源；与风格系统正交，可独立推进。

---

## 9. 分阶段实施（建议顺序）

| 阶段 | 内容 | 验收 |
|---|---|---|
| **N0** | HoyoToon HI3 着色器**选型验证**：vendor 进仓库，Built-in + Unity6 编译通过，手动在一个材质上插齐贴图看效果 | 编辑器里单材质渲染正确 |
| **N1** | **RenderStyleManager 接入点原型**：FinalizeLoadedModel 调用，先实现 `mtoon`(不改) + 一个最简风格切换；缓存/还原打通 | 加载模型能切风格、能还原 |
| **N2** | **`hoyo_hi3` 风格**：把贴图绑到 HoyoToon 槽位，分类(脸/发/肤/衣)接对应通道；缺图降级 | 现有 丽塔（带额外图）渲染接近参考 |
| **N3** | **PMX→VRM 导出**：UniVRM 导出 + SpringBone 物理 + 额外贴图打包 + extras 元数据 | 导出的 VRM 在本软件 HoYo 正确、在外部按 MToon 正常 |
| **N4** | **设置 UI + 持久化**：渲染风格下拉、实时切换、`settings.json` 存取 | 重启后保持选择 |
| **N5** | **后处理接通**（§8）+ 文档/清理 | 鲜艳感生效；ARCHITECTURE/ADR 更新 |

> N0/N1 风险最低、信息量最大，建议先行验证架构与着色器，再投入 N3 的 VRM 导出。

---

## 10. 风险与待验证

1. **HoyoToon Built-in + Unity6 兼容**（见 §6）——最大不确定性，N0 先验证。
2. **运行时换材质性能**：仅加载/切换时一次性操作，影响可控；注意 `Instantiate` 材质避免泄漏。
3. **着色器变体剥离**：构建时需保证 HoYo 变体进包。
4. **脸 SDF 在桌宠自由视角下**的正确性。
5. **VRM 额外贴图体积**：LightMap/SDF 增大文件；可接受（少量模型）。
6. **默认 Zome 头像**：是 All-Rights-Reserved 的 VRM，风格系统对它默认用 `mtoon` 不动，避免改变官方观感。

---

## 11. 验收标准
- 通用 VRM 模型在本软件可加载，且能在 `mtoon` / `hoyo_hi3` 间实时切换。
- 我们 PMX 来源的 丽塔（VRM，带额外图）在 `hoyo_hi3` 下观感明显接近 Blender 参考（皮肤白皙、颜色鲜艳、脸部无碎裂、分区/高光正确）。
- 物理（头发/裙摆）以 SpringBone 正常摆动、无穿模回退。
- 外来普通 VRM 在 `hoyo_hi3` 下优雅降级为通用 toon，不报错。
- 选择持久化、重启保持。
