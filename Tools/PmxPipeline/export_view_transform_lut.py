"""导出 Blender 视图变换曲线，供 Unity 侧烤成一维 LUT。

背景：渲染预设配套插件把场景设为 view_transform='Filmic'、look='High Contrast'
（见 Docs/RenderPreset/README.md）。Unity Built-in 不做这层变换，不复刻则高光会
烧成纯白、整体对比也不对——历史上反复出现的「惨白」判定有一部分根因在此。

做法：不去解析 OCIO 配置链（易误组装），而是**实测**——渲染已知的线性值，读回
显示端的编码值。已验证该变换是**纯逐通道**的（高饱和色与 HDR 值的通道间偏差为
0.00000），所以一维 LUT 足够，且可以放在片元着色器末端逐像素套用，不破坏 alpha。

参数化沿用 Blender 自己的方式（config.ocio 中 Filmic Log 的 lg2 allocation）：

    t = (log2(x) - LO) / (HI - LO)        LO = -12.473931188, HI = 4.026068812

着色器里对 t 做 saturate 后查表即可。x <= 0 直接取表首项。

用法：

    blender.exe -b --factory-startup -P Tools/PmxPipeline/export_view_transform_lut.py \
        -- --out Docs/RenderPreset/view_transform_filmic_high_contrast.csv

**必须用 Blender 3.6**（与预设配套插件一致；5.0 的色彩管理默认已换成 AgX）。
"""
import math
import os
import sys

import bpy

# Blender config.ocio: Filmic Log 的 lg2 allocation 范围
LO = -12.473931188
HI = 4.026068812
N = 257  # 与 EEVEE 自身 ColorRamp/Curves 的表长一致（CM_TABLE + 1）

VIEW_TRANSFORM = "Filmic"
LOOK = "High Contrast"


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = None
    for i, a in enumerate(argv):
        if a == "--out" and i + 1 < len(argv):
            out = argv[i + 1]
    return out


def lin_to_srgb(c):
    c = max(0.0, c)
    return 12.92 * c if c <= 0.0031308 else 1.055 * (c ** (1 / 2.4)) - 0.055


def setup():
    for coll in (bpy.data.objects, bpy.data.materials, bpy.data.meshes):
        for x in list(coll):
            coll.remove(x)

    bpy.ops.mesh.primitive_plane_add(size=20)
    bpy.data.objects[0].name = "Plane"

    cam_data = bpy.data.cameras.new("Cam")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = 4
    cam = bpy.data.objects.new("Cam", cam_data)
    bpy.context.collection.objects.link(cam)
    cam.location = (0, 0, 5)

    sc = bpy.context.scene
    sc.camera = cam
    sc.render.engine = "BLENDER_EEVEE"
    sc.render.resolution_x = sc.render.resolution_y = 4
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_depth = "16"
    sc.eevee.use_bloom = False  # bloom 是独立一层，不属于视图变换
    sc.view_settings.view_transform = VIEW_TRANSFORM
    sc.view_settings.look = LOOK
    sc.view_settings.exposure = 0.0
    sc.view_settings.gamma = 1.0

    world = bpy.data.worlds.new("W")
    world.use_nodes = False
    world.color = (0, 0, 0)
    sc.world = world

    # Color 直连 Surface = 无光照自发光，与渲染预设的输出方式一致
    mat = bpy.data.materials.new("probe")
    mat.use_nodes = True
    nt = mat.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out_node = nt.nodes.new("ShaderNodeOutputMaterial")
    rgb = nt.nodes.new("ShaderNodeRGB")
    nt.links.new(rgb.outputs[0], out_node.inputs["Surface"])
    bpy.data.objects["Plane"].data.materials.append(mat)
    return rgb


def measure(rgb, value, tmp_png):
    rgb.outputs[0].default_value = (value, value, value, 1.0)
    bpy.context.scene.render.filepath = tmp_png
    bpy.ops.render.render(write_still=True)
    img = bpy.data.images.load(tmp_png)
    # Blender 载入 PNG 时会做 sRGB->linear，这里反向还原成显示端编码值
    px = img.pixels[0]
    bpy.data.images.remove(img)
    return lin_to_srgb(px)


def main():
    out_path = parse_args()
    if not out_path:
        raise SystemExit("需要 --out <file.csv>")
    out_path = os.path.abspath(out_path)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    tmp_png = os.path.join(os.path.dirname(out_path), "_vt_probe.png")

    rgb = setup()
    rows = []
    for i in range(N):
        t = i / (N - 1)
        log2x = LO + t * (HI - LO)
        x = 2.0 ** log2x
        rows.append((i, t, x, measure(rgb, x, tmp_png)))

    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(f"# Blender {bpy.app.version_string} "
                f"view_transform={VIEW_TRANSFORM} look={LOOK} exposure=0 gamma=1\n")
        f.write(f"# t = (log2(x) - {LO}) / ({HI} - {LO})，着色器里 saturate(t) 后查表\n")
        f.write("# index,t,scene_linear,display_srgb\n")
        for i, t, x, y in rows:
            f.write(f"{i},{t:.9f},{x:.9e},{y:.9f}\n")

    if os.path.exists(tmp_png):
        os.remove(tmp_png)
    print(f"[export_view_transform_lut] {N} 项 -> {out_path}")


if __name__ == "__main__":
    main()
