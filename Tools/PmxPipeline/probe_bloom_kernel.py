"""实测 EEVEE bloom 的点扩散函数（PSF），供 Unity 侧复刻。

为什么要测而不是照着 EEVEE 源码重写：bloom 是多级降采样 + tent 上采样的组合，
迭代次数由 `radius` 与分辨率一起决定，权重与 `sample_scale` 的具体取值很容易记错，
而一旦记错就是「看起来有辉光但半径不对」这种难以定位的偏差。直接测出核的形状与能量，
再在 Unity 侧拟合，和本项目测视图变换曲线是同一套做法。

做法：黑底上放一个已知大小的自发光方块，开/关 bloom 各渲一张 32 位 EXR，相减即得核
（背景是纯黑，所以关 bloom 那张在方块以外恒为 0）。输出径向平均剖面与能量占比。

    blender.exe -b --factory-startup -P Tools/PmxPipeline/probe_bloom_kernel.py -- \
        --out Build/RefDiff/bloom_probe --res 1024 --intensity 0.08 --radius 6.5
"""
import math
import os
import sys

import bpy
import numpy as np


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    a = {"out": "Build/RefDiff/bloom_probe", "res": 1024, "intensity": 0.08,
         "radius": 6.5, "threshold": 0.8, "knee": 0.5, "square": 16, "level": 10.0}
    i = 0
    while i < len(argv):
        k = argv[i][2:].replace("-", "_")
        if k in a and i + 1 < len(argv):
            v = argv[i + 1]
            a[k] = int(v) if k in ("res", "square") else (
                v if k == "out" else float(v))
            i += 2
        else:
            raise SystemExit("无法识别的参数: " + argv[i])
    return a


def setup(args):
    bpy.ops.wm.read_homefile(use_empty=True)
    sc = bpy.context.scene
    sc.render.engine = "BLENDER_EEVEE"
    sc.render.film_transparent = True
    sc.render.resolution_x = sc.render.resolution_y = args["res"]
    sc.render.resolution_percentage = 100
    sc.render.image_settings.file_format = "OPEN_EXR"
    sc.render.image_settings.color_depth = "32"
    sc.render.image_settings.color_mode = "RGBA"
    # EXR 存线性值，不过视图变换——测核只关心线性域的能量分布
    sc.view_settings.view_transform = "Standard"
    sc.eevee.taa_render_samples = 1

    cam_data = bpy.data.cameras.new("cam")
    cam_data.type = "ORTHO"
    # 正交宽度 = res 个「世界单位」，于是 1 世界单位 = 1 像素，方块边长可以精确给定
    cam_data.ortho_scale = float(args["res"])
    cam = bpy.data.objects.new("cam", cam_data)
    sc.collection.objects.link(cam)
    cam.location = (0, 0, 100)
    sc.camera = cam

    bpy.ops.mesh.primitive_plane_add(size=float(args["square"]))
    quad = bpy.context.object
    m = bpy.data.materials.new("emit")
    m.use_nodes = True
    m.blend_method = "OPAQUE"
    nt = m.node_tree
    nt.nodes.clear()
    o = nt.nodes.new("ShaderNodeOutputMaterial")
    e = nt.nodes.new("ShaderNodeEmission")
    e.inputs["Color"].default_value = (1, 1, 1, 1)
    e.inputs["Strength"].default_value = args["level"]
    nt.links.new(e.outputs["Emission"], o.inputs["Surface"])
    quad.data.materials.append(m)
    return sc


def render_exr(sc, path):
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)
    img = bpy.data.images.load(path + ".exr")
    w, h = img.size
    px = np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4)
    bpy.data.images.remove(img)
    return px


def main():
    args = parse_args()
    out = os.path.abspath(args["out"])
    os.makedirs(os.path.dirname(out), exist_ok=True)
    sc = setup(args)

    ee = sc.eevee
    ee.use_bloom = False
    off = render_exr(sc, out + "_off")

    ee.use_bloom = True
    ee.bloom_intensity = args["intensity"]
    ee.bloom_radius = args["radius"]
    ee.bloom_threshold = args["threshold"]
    ee.bloom_knee = args["knee"]
    ee.bloom_color = (1.0, 1.0, 1.0)     # 测核时用白色，颜色是后乘的
    on = render_exr(sc, out + "_on")

    diff = (on - off)[..., :3].mean(axis=2)
    h, w = diff.shape
    cy, cx = h / 2.0 - 0.5, w / 2.0 - 0.5
    yy, xx = np.mgrid[0:h, 0:w]
    r = np.sqrt((yy - cy) ** 2 + (xx - cx) ** 2)

    src_total = float(off[..., :3].mean(axis=2).sum())
    glow_total = float(diff.sum())
    print("[bloom] 分辨率 %d  方块边长 %d px  发光强度 %.1f" %
          (args["res"], args["square"], args["level"]))
    print("[bloom] intensity=%.4f radius=%.3f threshold=%.3f knee=%.3f"
          % (args["intensity"], args["radius"], args["threshold"], args["knee"]))
    print("[bloom] 源能量 %.1f   辉光能量 %.1f   占比 %.5f"
          % (src_total, glow_total, glow_total / max(src_total, 1e-9)))

    # 径向平均剖面：半径按 1.25 倍几何级数分箱，核的尾巴很长，线性分箱看不出形状
    edges = [0.0]
    while edges[-1] < w / 2.0:
        edges.append(max(edges[-1] * 1.25, edges[-1] + 1.0))
    print("[bloom] 径向剖面（半径 px -> 平均值 -> 该环累计能量占比）：")
    cum = 0.0
    for i in range(len(edges) - 1):
        m = (r >= edges[i]) & (r < edges[i + 1])
        n = int(m.sum())
        if n == 0:
            continue
        v = float(diff[m].mean())
        e = float(diff[m].sum())
        cum += e
        print("    %7.1f-%7.1f  n=%7d  均值 %.6e  环能量 %8.2f  累计 %.4f"
              % (edges[i], edges[i + 1], n, v, e, cum / max(glow_total, 1e-9)))


if __name__ == "__main__":
    main()
