# 文档地图

本仓库的文档面向 AI Agent 与维护者。**入口是仓库根的 `AGENTS.md`**，它规定了接手顺序、
硬约束，以及「改了什么就更新哪份」的对应关系。

## 按问题找文档

| 我想知道… | 看哪份 |
|---|---|
| 现在做到哪了、下一步是什么、踩过哪些坑 | `PROGRESS.md` |
| 某个脚本干什么、数据写到哪、目录怎么分 | `Docs/ARCHITECTURE.md` |
| 为什么当初这么选、某条路是不是已经证伪 | `Docs/DECISIONS_RECORD.md` |
| 怎么构建 | `BUILD_MANUAL.md` |
| PMX 渲染复刻管线怎么跑、每个文件干什么 | `Tools/PmxPipeline/README.md` |
| 渲染复刻的实测结论、各阶段验收数值 | `Docs/plans/2026-09-10-blender-graph-transpile.md` |
| 预设的参考数据（节点图、视图变换 LUT、配套插件） | `Docs/RenderPreset/README.md` |
| 某个 Blender 节点的求值语义到底是什么 | `Tools/PmxPipeline/verify/README.md` |

## 各类文档的定位

- **`PROGRESS.md`** —— 状态。任务完成、验证结果、已知问题变化都记这里，含「验证记录」
  流水。踩坑表也在这，因为它回答的是「别再踩一次」。
- **`Docs/ARCHITECTURE.md`** —— 结构。目录、脚本职责、数据路径、启动流程。回答「东西在
  哪、谁负责什么」。
- **`Docs/DECISIONS_RECORD.md`** —— 决策。只记影响长期维护的取舍，普通 Bug 修复和 UI 调整
  不记。后续 ADR 可以修正前面的 ADR，但**必须在原处加指向**。
- **`Docs/plans/`** —— 某条技术路线的完整推导与实测记录，按阶段追加。它不是操作手册。
- **各工具目录下的 `README.md`** —— 操作手册。怎么跑、有哪些开关、文件都干什么。命令行
  用法只写在这里，别的文档引用而不复述。

## 写文档的三条规矩

1. **推翻旧结论就地改写，不要在别处打补丁。** 同一个事实散落多处、或者靠「修正：其实……」
   层层叠加，是文档失真的主要来源。
2. **一个事实只在一个地方展开**，其余位置引用。判断依据是「这句话改了之后，还有哪几处
   要跟着改」——如果不止一处，说明放错地方了。
3. **文档与实际脱节时，先改文档再继续。** 不要留「等会儿再同步」。

## 不在工作树中的历史文档

- `Docs/RENDER_STYLE_DESIGN.md`、`Docs/PMX_TO_VRM.md`：随 VRM+RenderStyle 路线归档，复盘
  见 ADR-0009。
- `Docs/plans/2026-06-22-liltoon-npr-render.md`：随 lilToon 路线归档到分支
  `archive/liltoon-npr-tuning`，结论作废，根因见 ADR-0011。
