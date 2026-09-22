# -*- coding: utf-8 -*-
"""单独验证 Jenkins lookup3 哈希：White Noise 节点走的就是 hash_uint3/4。"""
import os
import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
PTS = [(0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1), (1, 1, 1),
       (-1, 2, -3), (17, 5, 9), (2, 2, 2), (1000, -7, 13)]

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
wn = nt.nodes.new("ShaderNodeTexWhiteNoise")
comb = nt.nodes.new("ShaderNodeCombineXYZ")
wn.noise_dimensions = "3D"
nt.links.new(comb.outputs[0], wn.inputs["Vector"])
nt.links.new(wn.outputs["Value"], emi.inputs["Color"])
emi.inputs["Strength"].default_value = 1.0
nt.links.new(emi.outputs[0], out.inputs["Surface"])
plane.data.materials.append(mat)

tmp = os.path.join(HERE, "pt")
lines = []
for p in PTS:
    comb.inputs["X"].default_value = float(p[0])
    comb.inputs["Y"].default_value = float(p[1])
    comb.inputs["Z"].default_value = float(p[2])
    sc.render.filepath = tmp
    bpy.ops.render.render(write_still=True)
    img = bpy.data.images.load(tmp + ".exr")
    v = img.pixels[0]
    bpy.data.images.remove(img)
    lines.append("%s %.9f" % (list(p), v))
    print("[hash] %s -> %.9f" % (p, v))
with open(os.path.join(HERE, "hash_pts.txt"), "w", encoding="utf-8") as f:
    f.write("\n".join(lines))
