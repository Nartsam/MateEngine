# -*- coding: utf-8 -*-
"""实测 Blender RGB Curves 的求值顺序：合成曲线 C 与逐通道曲线谁先。

做法：给 RGB Curves 一个已知常量输入，C 与 R 各设一条明显非恒等的曲线，用
Standard 视图变换渲一张 EXR（无色彩管理、无 bloom），读出像素，再和两种顺序在
Python 里算出来的结果比。
"""
import os
import bpy

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "curve_probe.exr")

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

mat = bpy.data.materials.new("probe")
mat.use_nodes = True
nt = mat.node_tree
for n in list(nt.nodes):
    nt.nodes.remove(n)
out = nt.nodes.new("ShaderNodeOutputMaterial")
emi = nt.nodes.new("ShaderNodeEmission")
cur = nt.nodes.new("ShaderNodeRGBCurve")
rgb = nt.nodes.new("ShaderNodeRGB")

IN = (0.25, 0.60, 0.85)
rgb.outputs[0].default_value = (IN[0], IN[1], IN[2], 1.0)
emi.inputs["Strength"].default_value = 1.0
nt.links.new(rgb.outputs[0], cur.inputs["Color"])
nt.links.new(cur.outputs[0], emi.inputs["Color"])
nt.links.new(emi.outputs[0], out.inputs["Surface"])
cur.inputs["Fac"].default_value = 1.0

cm = cur.mapping
# C 曲线（cm[3]）：把中点压到 0.2，明显非恒等
cm.curves[3].points[1].location = (1.0, 1.0)
cm.curves[3].points.new(0.5, 0.2)
# R 曲线（cm[0]）：把中点抬到 0.85
cm.curves[0].points.new(0.5, 0.85)
cm.update()

plane.data.materials.append(mat)
bpy.ops.render.render(write_still=True)

ev = cm.evaluate
c3, c0, c1, c2 = cm.curves[3], cm.curves[0], cm.curves[1], cm.curves[2]
print("[probe] 输入            = %s" % (IN,))
print("[probe] 逐通道(合成(x)) = (%.6f, %.6f, %.6f)"
      % (ev(c0, ev(c3, IN[0])), ev(c1, ev(c3, IN[1])), ev(c2, ev(c3, IN[2]))))
print("[probe] 合成(逐通道(x)) = (%.6f, %.6f, %.6f)"
      % (ev(c3, ev(c0, IN[0])), ev(c3, ev(c1, IN[1])), ev(c3, ev(c2, IN[2]))))

img = bpy.data.images.load(OUT)
px = list(img.pixels[:4])
print("[probe] 渲染结果        = (%.6f, %.6f, %.6f)" % (px[0], px[1], px[2]))
