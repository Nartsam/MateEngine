# -*- coding: utf-8 -*-
"""逐点取 Blender 噪声值：把 Noise Texture 的 Vector 设成常量，整张图就是一个值。

避开 UV / 像素映射这类容易搞错的中间环节——之前正是这里把比对结果搞成了噪音。
"""
import os
import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
SCALE, DETAIL, ROUGH, DIST = 8.1, 2.0, 0.5, 0.0
PTS = [(0.0, 0.0, 0.0), (0.37, 0.11, 0.0), (1.3, -2.7, 0.55), (5.0, 5.0, 5.0),
       (0.5, 0.5, 0.5), (-1.25, 3.75, 2.5), (10.1, 0.2, -0.3), (2.0, 2.0, 2.0)]
WS = [0.0, 0.73]

sc = bpy.context.scene
sc.render.engine = "BLENDER_EEVEE"
sc.eevee.use_bloom = False
sc.eevee.taa_render_samples = 1
sc.view_settings.view_transform = "Standard"
sc.view_settings.look = "None"
sc.render.resolution_x = sc.render.resolution_y = 2
sc.render.image_settings.file_format = "OPEN_EXR"
sc.render.image_settings.color_depth = "32"

for o in list(bpy.data.objects):
    bpy.data.objects.remove(o, do_unlink=True)
bpy.ops.mesh.primitive_plane_add(size=100)
plane = bpy.context.object
cd = bpy.data.cameras.new("c"); cd.type = "ORTHO"; cd.ortho_scale = 1.0
cam = bpy.data.objects.new("c", cd); sc.collection.objects.link(cam)
cam.location = (0, 0, 5); sc.camera = cam

mat = bpy.data.materials.new("m"); mat.use_nodes = True
nt = mat.node_tree
for n in list(nt.nodes):
    nt.nodes.remove(n)
out = nt.nodes.new("ShaderNodeOutputMaterial")
emi = nt.nodes.new("ShaderNodeEmission")
nz = nt.nodes.new("ShaderNodeTexNoise")
comb = nt.nodes.new("ShaderNodeCombineXYZ")
nt.links.new(comb.outputs[0], nz.inputs["Vector"])
nz.inputs["Scale"].default_value = SCALE
nz.inputs["Detail"].default_value = DETAIL
nz.inputs["Roughness"].default_value = ROUGH
nz.inputs["Distortion"].default_value = DIST
emi.inputs["Strength"].default_value = 1.0
nt.links.new(nz.outputs["Fac"], emi.inputs["Color"])
nt.links.new(emi.outputs[0], out.inputs["Surface"])
plane.data.materials.append(mat)

tmp = os.path.join(HERE, "pt")
lines = []
for dim in ("3D", "4D"):
    nz.noise_dimensions = dim
    for p in PTS:
        ws = WS if dim == "4D" else [None]
        for w in ws:
            comb.inputs["X"].default_value = p[0]
            comb.inputs["Y"].default_value = p[1]
            comb.inputs["Z"].default_value = p[2]
            if w is not None:
                nz.inputs["W"].default_value = w
            sc.render.filepath = tmp
            bpy.ops.render.render(write_still=True)
            img = bpy.data.images.load(tmp + ".exr")
            v = img.pixels[0]
            bpy.data.images.remove(img)
            lines.append("%s %s %s %.8f" % (dim, p, w, v))
            print("[pt] %s vec=%s w=%s -> %.8f" % (dim, p, w, v))
with open(os.path.join(HERE, "noise_pts.txt"), "w", encoding="utf-8") as f:
    f.write("\n".join(lines))
