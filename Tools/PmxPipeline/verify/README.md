# 节点求值的逐点验证

转译器里有一批**没有文档、只能靠读源码或实测确定**的语义：Jenkins lookup3 哈希的
初始常量、Perlin 噪声的归一化系数、RGB Curves 里合成曲线与逐通道曲线的先后。写错
了不会报错，只会让某个材质偏一点，在整图差分里淹没掉。

这里的脚本把这些点单独拎出来测：让 Blender 渲一张 2×2 的 EXR、只输出某个节点的值，
再和 `noise_reference.py`（HLSL 实现的 Python 对照版）逐点比。

```
blender.exe -b --factory-startup -P Tools/PmxPipeline/verify/probe_hash.py
blender.exe -b --factory-startup -P Tools/PmxPipeline/verify/probe_noise.py
blender.exe -b --factory-startup -P Tools/PmxPipeline/verify/probe_curve_order.py
blender.exe -b --factory-startup -P Tools/PmxPipeline/verify/probe_image_alpha.py
```

产物（`*.txt` / `*.exr`）落在脚本同级目录，用完自行清理。

## 已核对的结论（2026-09-22，Blender 3.6.19）

| 项 | 结论 |
|---|---|
| `hash_uint2/3/4` 初始常量 | `0xdeadbeef + (n << 2) + 13`，**`+13` 不能漏** |
| Perlin 3D / 4D | 与 EEVEE 吻合到 1e-6（float32 精度），24 个采样点 |
| `noise_scale3 / noise_scale4` | 0.9820 / 0.8344 |
| RGB Curves 求值顺序 | `cm[ch](cm[3](x))`——**先合成曲线、再逐通道曲线** |
| Noise Texture 的 Vector 未连线 | 忽略插槽默认值，改用 Generated 坐标 |
| Image Texture 的 Color 与 alpha（2026-10-01） | STRAIGHT 图在 **Alpha 输出未接线时 Color = rgb·a**，接线时为原 rgb；Non-Color 不受 alpha 影响（rgb=(0.8,0.6,0.4)、a=0.25 实测 (0.2,0.15,0.1) / (0.8,0.6,0.4) / (0.8,0.6,0.4)）。PREMUL 图在 Alpha 接线时 rgb/a，按 GPU 节点源码实现、未单独实测。漏掉这条曾让寻梦者头发 ΔE 5.83、大月下头巾 ΔE 8.51 |

注意：`probe_image_alpha.py` 的测试图必须先存成浮点 EXR 再按文件加载，并关掉
`use_half_precision`——内存里生成的图不会上传 GPU，半精度上传会带来约 2e-4 的偏差。

注意：`probe_noise.py` 必须用**连线的** Combine XYZ 喂坐标。直接写
`nz.inputs["Vector"].default_value` 是无效的——未连线时 Blender 根本不读它。
