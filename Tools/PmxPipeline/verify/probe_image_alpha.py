# -*- coding: utf-8 -*-
"""实测 Image Texture 的 Color 输出与 alpha 的关系。

Blender 的 GPU 节点会按「Alpha 输出是否接线」改写 Color 输出：直通 alpha 的图在 Alpha
没接线时输出 rgb·a。转译器若直接取 rgb，alpha 为 0 处的残留颜色会整片显出来（寻梦者
头发的「高光」图）。这里用一张已知 RGBA 的浮点图分三种接法各渲一次，读回像素比对。
"""
import os
import bpy

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "image_alpha_probe.exr")
RGB = (0.8, 0.6, 0.4)
ALPHA = 0.25

sc = bpy.context.scene
sc.render.engine = "BLENDER_EEVEE"
sc.eevee.use_bloom = False
sc.eevee.taa_render_samples = 1
sc.view_settings.view_transform = "Standard"
sc.view_settings.look = "None"
sc.render.resolution_x = sc.render.resolution_y = 4
sc.render.image_settings.file_format = "OPEN_EXR"
sc.render.image_settings.color_depth = "32"
sc.render.filepath = OUT[:-4]

for o in list(bpy.data.objects):
    bpy.data.objects.remove(o, do_unlink=True)
bpy.ops.mesh.primitive_plane_add(size=100)
plane = bpy.context.object
bpy.ops.object.camera_add(location=(0, 0, 5), rotation=(0, 0, 0))
sc.camera = bpy.context.object

# 内存里生成的图不会上传给 GPU，必须先存盘再按文件加载。用 32 位浮点 EXR：不经色彩
# 管理、没有量化误差（存成 PNG 会被套视图变换，源颜色就不再是上面的值）。
SRC = os.path.join(os.path.dirname(OUT), "image_alpha_probe_src.exr")
gen = bpy.data.images.new("probe_src", 2, 2, alpha=True, float_buffer=True)
gen.pixels[:] = [RGB[0], RGB[1], RGB[2], ALPHA] * 4
sc.render.image_settings.color_mode = "RGBA"
gen.save_render(SRC, scene=sc)
img = bpy.data.images.load(SRC, check_existing=False)
img.alpha_mode = "STRAIGHT"
img.use_half_precision = False   # 默认按半精度上传 GPU，会带来约 2e-4 的偏差

mat = bpy.data.materials.new("probe")
mat.use_nodes = True
plane.data.materials.append(mat)


def render(colorspace, link_alpha):
    img.colorspace_settings.name = colorspace
    nt = mat.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    emi = nt.nodes.new("ShaderNodeEmission")
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = img
    tex.interpolation = "Closest"
    nt.links.new(tex.outputs["Color"], emi.inputs["Color"])
    nt.links.new(emi.outputs[0], out.inputs["Surface"])
    if link_alpha:
        # 让 Alpha 输出真正被消费、又不改变结果：Strength = 1 + 0·alpha
        mul = nt.nodes.new("ShaderNodeMath")
        mul.operation = "MULTIPLY"
        mul.inputs[1].default_value = 0.0
        add = nt.nodes.new("ShaderNodeMath")
        add.inputs[1].default_value = 1.0
        nt.links.new(tex.outputs["Alpha"], mul.inputs[0])
        nt.links.new(mul.outputs[0], add.inputs[0])
        nt.links.new(add.outputs[0], emi.inputs["Strength"])
    else:
        emi.inputs["Strength"].default_value = 1.0
    bpy.ops.render.render(write_still=True)
    res = bpy.data.images.load(OUT, check_existing=False)
    px = list(res.pixels[:3])
    bpy.data.images.remove(res)
    return px


cases = [
    ("Linear", False, "直通 alpha，Alpha 未接线"),
    ("Linear", True, "直通 alpha，Alpha 已接线"),
    ("Non-Color", False, "Non-Color，Alpha 未接线"),
]
print("[probe] 图像 rgb=%s alpha=%.2f，rgb·a=%s" % (RGB, ALPHA, tuple(round(c * ALPHA, 4) for c in RGB)))
for cs, linked, label in cases:
    px = render(cs, linked)
    print("[probe] %-22s → (%.4f, %.4f, %.4f)" % (label, px[0], px[1], px[2]))
os.remove(SRC)
