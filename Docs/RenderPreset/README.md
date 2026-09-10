# 渲染预设参考资料

复刻「PMX 模型 + `.blend` 渲染预设」所需的参考数据。实施计划见
`Docs/plans/2026-09-10-blender-graph-transpile.md`，决策背景见 ADR-0011。

## 目录内容

### `graphs/`

四个角色预设的**完整节点图**，由 `Tools/PmxPipeline/export_blender_graph.py` 导出：
含全部节点、**连线**、插槽默认值、节点组接口、色带与曲线控制点、以及节点类型专属属性。

这是转译器要实现的规格说明。虽然可以从 `.blend` 重新生成，但源文件在仓库之外
（`D:\Program Files\MMD\MateEngine\examples\<角色>\渲染预设.blend`），落盘一份以防丢失。

重新生成：

```
blender.exe -b "<角色>/渲染预设.blend" --factory-startup \
    -P Tools/PmxPipeline/export_blender_graph.py -- --out <输出>.json
```

**必须用 Blender 3.6**（本机便携安装于 `D:\Program Files\Blender\3.6\`）。5.0.1 的
API 已变（无 `node_group.outputs`、无 `eevee.use_bloom`），配套插件在其上无法运行。

### `vendor/模型预设导入.py`

预设配套的 Blender 导入插件副本，作者为预设作者（bilibili）。原件位于
`D:\Program Files\Blender\3.6\3.6\scripts\addons\`，此处仅为防丢失的参考副本。

**它是「预设如何被正确应用」的权威说明**，离线管线必须复刻其行为：

- 材质按「剪去后缀」模式自动配对，撞名由插件处理
- 为每个未被使用的几何节点组自动添加 NODES 修改器，并把输出接口的
  `default_attribute_name` 设为接口名（于是属性名为 `SUN` / `RIGHT` / `FRONT`）
- 把 `面部定位` 空物体自动 parent 到骨骼 `頭`，因此脸部 SDF 阴影跟随头部转动
- **写入场景效果**：`use_bloom=True`、`bloom_intensity=0.08`、
  `bloom_color=#FFE4D9`、`view_transform='Filmic'`、`look='High Contrast'`

> **注意**：`渲染预设.blend` 是没有场景的纯材质库，其内部存的 scene 设置
> （`use_bloom=False`、`look='None'`）是**死数据**。真正生效的是上面这段插件代码。
> 本项目已因读错这份死数据而得出过错误结论一次。

### `view_transform_filmic_high_contrast.csv`

Blender 视图变换曲线的实测数据，257 项，由 `Tools/PmxPipeline/export_view_transform_lut.py`
生成。Unity Built-in 不做这层变换，不复刻则高光会烧成纯白、整体对比也不对。

参数化沿用 Blender 自己的方式（`config.ocio` 中 Filmic Log 的 lg2 allocation）：

```
t = (log2(x) - (-12.473931188)) / (4.026068812 - (-12.473931188))
display = LUT[saturate(t)]
```

**已验证**：该变换是纯逐通道的（高饱和色与 HDR 值的通道间偏差为 `0.00000`），因此
一维 LUT 足够，且可以放在片元着色器末端逐像素套用——**不需要全屏后处理，alpha 不受影响**。
用 16 个独立渲染实测点回验这张表，最大偏差 `0.00002`（约 8 位灰阶的百分之一）。

几个值得记住的点：

| 线性输入 | 显示输出 |
|---|---|
| 0.18（中灰） | 0.500 |
| 0.50 | 0.745 |
| **1.00** | **0.863** |
| 8.00 | 0.993 |

注意 **线性 1.0 只到 0.863，Blender 里画面永远不会出现纯白**。

> 该曲线**不含 bloom**。bloom 是独立的一层屏幕空间效果（强度 0.08、色 `#FFE4D9`），
> 与透明分层窗口的兼容方案尚未确定，见 ADR-0011。

## 参考截图

`Docs/RenderRefs/丽塔/` 下是 Blender 中应用预设后的实际观感，作为复刻目标。
`丽塔-Blender预设-正面-A/B.png` 两张能看清 bloom 造成的轮廓辉光。
