# AGENTS.md

本文档是给 AI AGENTS 与维护者看的项目级规则文件。进入本仓库后，先读本文件，再根据任务读取其他文档。

## 项目定位

MateEngine 是基于 GitHub 开源桌面宠物软件 Mate-Engine 的个人 fork。当前二次开发目标是做成绿色便携、Steam-free、local-first 的个人自用桌面宠物软件，支持 PMX 模型加载、渲染风格调整、LLM/TTS 接入等高级功能，并在此基础上长期维护和扩展。

核心方向：

- 保留 Mate Engine 的桌面宠物体验：VRM 角色、透明置顶、拖拽、坐任务栏/窗口、待机、跳舞、触摸反应。
- 保留并增强本地 Mod 能力：`.me` Mod、AssetBundle、自定义动画、材质和 Shader。
- 所有运行时用户数据尽量集中在程序目录 `UserData/`，便于备份、迁移和清理。
- AI 聊天、本地 LLM、外部通信接口等功能可以作为可选扩展，但不能破坏基础桌宠体验。

## 接手顺序

1. 读 `AGENTS.md`，确认目标、硬约束和文档维护规则。
2. 读 `PROGRESS.md`，确认当前完成状态、正在处理的问题和下一步。
3. 涉及系统结构、脚本职责、数据路径时读 `Docs/ARCHITECTURE.md`。
4. 涉及技术路线、兼容性、构建方式、数据格式等重要取舍时读 `Docs/DECISIONS_RECORD.md`。
5. 涉及构建时读 `BUILD_MANUAL.md`。
6. 涉及 PMX 导入或渲染复刻时，读 `Tools/PmxPipeline/README.md`（怎么跑、文件都干什么），
   需要背景或实测数据再读 `Docs/plans/2026-09-10-blender-graph-transpile.md`。
   **注意**：渲染复刻走的是**节点图转译成 HLSL**（ADR-0011），不是用通用卡通着色器调参——
   MToon/HoyoToon、UTS2、lilToon 三条参数拟合路线均已证伪，勿重试（ADR-0009/0010/0011）。

## 硬性约束

- 禁止全局安装依赖。确需依赖时，只能放在当前仓库/工作树内，并优先使用虚拟环境或项目内工具目录。
- 禁止污染仓库外部文件、全局环境变量、全局注册表或系统目录。Unity 编辑器自身缓存和 License 等我们无法控制的行为除外。
- Unity 版本必须精确匹配 `6000.2.6f2`。
- 主场景是 `Assets/MATE ENGINE - Scenes/Mate Engine Main.unity`。
- 不要打开或改动 `DEV` / `InDev` 场景，除非用户明确要求。
- 构建阶段默认使用 Mono。命令行构建走 `Assets/Editor/BuildScript.cs`。
- 默认 Avatar（Zome）版权为 All Rights Reserved，不可随自制 build 重新分发。
- 修改 Unity 资产时必须保留必要 `.meta` 文件，避免破坏引用。
- 不要随意删除或重命名已序列化字段。即使业务代码不再使用，也可能被场景、Prefab、Inspector 或旧 JSON 引用。
- 任务完成后，必须同步更新相关文档（对应关系见下方「改了什么就更新哪份」）。文档描述必须能对应项目的实际状态；发现文档与实际脱节时，先改文档再继续。

## 核心功能列表

- VRM 0.x / 1.x 角色加载与角色库管理。
- 透明、置顶、无边框桌面窗口。
- 角色状态机：Idle、Walk、Drag、Dance、Sit 等。
- 鼠标跟随：头部、眼睛、脊柱、手部 IK。
- 任务栏/窗口边缘坐立。
- 触摸区域、语音/音效反应、表情和 Blendshape 编辑。
- `.me` Mod 运行时加载。
- MMD / 自定义舞蹈播放与多角色同步。
- 本地设置、头像、缩略图、Mod、AI 历史、截图等便携化存储。
- 可选本地 LLM 聊天。
- 可选开机自启动；该功能由用户主动开启时需要写入 `HKCU\...\Run`。

## 开发规则

- 深度参考现有代码风格，包括命名、缩进、Unity 生命周期方法、单例/静态工具模式。
- 优先做小范围、可验证的改动。不要为了“顺手整理”做大规模无关重构。
- 运行时新增文件写入**必须**优先走 `PortablePaths`；不要直接使用 `Application.persistentDataPath`、`%TEMP%`、`%APPDATA%`、`%USERPROFILE%` 等外部路径。
- 对旧路径、旧 PlayerPrefs、旧 JSON 字段做迁移时，要保留回退和异常处理。
- 移除功能时优先保持序列化兼容：字段可以保留，行为可以禁用。
- Steam 依赖已移除。不要重新引入 Steamworks.NET、Steam Workshop、Steam DRM 或 `steam_api64.dll`。
- 改动主相机、窗口、截图、UI 渲染，或模型根节点的缩放、瞬移、动画与 IK 的执行顺序时，除普通 VRM 外还要加载一个转译 PMX 模型（`.me`）验证：转译模型由独立的模型相机渲染，在主相机绘制 UI 之前合入（`MEModelBloom`，ADR-0015，`Docs/ARCHITECTURE.md`「材质：节点图转译（主线）」一节）；它的物理骨骼由 `MmdPhysics` 在动画与 IK 之后驱动（ADR-0017）。只用 VRM 测试发现不了这两条路径上的问题。
- 文档同步的对应关系见下方「改了什么就更新哪份」。

## 关键文档

| 文档 | 作用 |
|---|---|
| `PROGRESS.md` | 当前状态、下一步、验证记录、已知问题、踩坑表 |
| `Docs/ARCHITECTURE.md` | 技术栈、目录结构、核心系统、数据路径 |
| `Docs/DECISIONS_RECORD.md` | 架构决策记录（ADR） |
| `BUILD_MANUAL.md` | 构建环境和构建命令 |
| `Tools/PmxPipeline/README.md` | PMX 渲染复刻管线：怎么跑、文件职责、环境与路径解析 |
| `Docs/plans/2026-09-10-blender-graph-transpile.md` | 渲染复刻的实测结论与分阶段记录 |
| `Docs/RenderPreset/README.md` | 预设的参考数据：节点图、视图变换 LUT、配套插件副本 |
| `Tools/PmxPipeline/verify/README.md` | 节点求值语义的逐点核对装置与已核实结论 |
| `README.md` | 上游/用户向项目介绍，不作为 AI 记忆主文档 |

### 改了什么就更新哪份

| 改动 | 必须同步的文档 |
|---|---|
| 任何任务完成、验证结果、已知问题变化 | `PROGRESS.md`（含「验证记录」一行） |
| 目录结构、核心脚本职责、数据路径、启动流程 | `Docs/ARCHITECTURE.md` |
| 技术路线取舍、兼容性结论、数据格式决定 | `Docs/DECISIONS_RECORD.md` 新增 ADR |
| PMX 管线的文件增删、命令行开关、路径约定 | `Tools/PmxPipeline/README.md` |
| 渲染复刻的实测结论、阶段验收数值 | `Docs/plans/2026-09-10-blender-graph-transpile.md` |
| 新核实的 Blender 节点语义 | `Tools/PmxPipeline/verify/README.md` 的结论表 |

**推翻旧结论时就地改写，不要在别处打补丁。** 同一个事实在多处重复描述、或者靠「修正：
其实……」层层叠加，是文档失真的主要来源。ADR 可以被后续 ADR 修正，但原处必须加指向。
