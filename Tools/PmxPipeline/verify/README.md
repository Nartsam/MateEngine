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

注意：`probe_noise.py` 必须用**连线的** Combine XYZ 喂坐标。直接写
`nz.inputs["Vector"].default_value` 是无效的——未连线时 Blender 根本不读它。
