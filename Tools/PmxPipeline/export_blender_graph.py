"""完整导出 Blender 着色器 / 几何节点图，供节点图→HLSL 转译使用。

与 dump_render_preset.py（关键词猜测出一个扁平 UTS2 字典、且从不记录连线）不同，
本脚本把图**当作图**导出：节点组作为可复用定义、每个节点的类型专属属性、每个插槽
的默认值、以及全部连线。产物用于机器分析与转译，见 ADR-0011。

用法：

    blender.exe -b "<渲染预设>.blend" --factory-startup \
        -P Tools/PmxPipeline/export_blender_graph.py -- --out <输出>.json

**必须用 Blender 3.6。** 5.0.1 已移除 node_group.outputs 与 eevee.use_bloom，
预设配套的导入插件在其上无法运行，观感也不保证一致。

注意：渲染预设 .blend 是没有场景的纯材质库，本脚本导出的 scene 段落是**死数据**，
不代表实际生效的设置——真正生效的由配套插件写入（bloom / Filmic / High Contrast），
详见 Docs/RenderPreset/README.md。
"""
import json
import os
import sys

import bpy


def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    out = None
    for i, a in enumerate(argv):
        if a == "--out" and i + 1 < len(argv):
            out = argv[i + 1]
    return out


def val(v):
    """Serialize a socket default_value / property to plain JSON."""
    if v is None:
        return None
    if isinstance(v, (bool, int, float, str)):
        return v
    try:
        return [val(x) for x in v]
    except TypeError:
        return str(v)


# Node-type specific properties that carry the actual math. Missing any of these
# makes the dump non-reproducible, so the list is deliberately generous.
NODE_PROPS = (
    "operation", "blend_type", "data_type", "clamp", "clamp_type", "use_clamp",
    "interpolation_type", "vector_type", "convert_from", "convert_to",
    "space", "uv_map", "attribute_name", "attribute_type", "distribution",
    "component", "mode", "invert", "from_instancer", "target",
    "projection", "extension", "feature", "noise_dimensions",
    "gradient_type", "falloff", "hole_tolerant", "use_alpha", "use_pixel_size",
    "layer_name", "bands_direction", "filter_type",
    # Easy to miss, and each one silently changes the result:
    # - clamp_factor / clamp_result: ShaderNodeMix clamping
    # - interpolation: TexImage filtering. "Smart" makes EEVEE sample bicubic,
    #   which matters when the result feeds a hard threshold (face SDF).
    # - voronoi_dimensions / distance: Voronoi metric
    "clamp_factor", "clamp_result", "interpolation",
    "voronoi_dimensions", "distance",
)


def dump_curve_mapping(cm):
    if cm is None:
        return None
    try:
        cm.initialize()
    except Exception:
        pass
    out = {
        "black_level": val(getattr(cm, "black_level", None)),
        "white_level": val(getattr(cm, "white_level", None)),
        "clip_min_x": getattr(cm, "clip_min_x", None),
        "clip_min_y": getattr(cm, "clip_min_y", None),
        "clip_max_x": getattr(cm, "clip_max_x", None),
        "clip_max_y": getattr(cm, "clip_max_y", None),
        "use_clip": getattr(cm, "use_clip", None),
        "extend": getattr(cm, "extend", None),
        "curves": [],
    }
    for c in cm.curves:
        out["curves"].append({
            "extend": getattr(c, "extend", None),
            "points": [
                {"location": val(p.location), "handle_type": p.handle_type}
                for p in c.points
            ],
        })
    return out


def dump_color_ramp(ramp):
    if ramp is None:
        return None
    return {
        "color_mode": ramp.color_mode,
        "interpolation": ramp.interpolation,
        "hue_interpolation": getattr(ramp, "hue_interpolation", None),
        "elements": [
            {"position": e.position, "color": val(e.color), "alpha": e.alpha}
            for e in ramp.elements
        ],
    }


def dump_image(img):
    if img is None:
        return None
    return {
        "name": img.name,
        "filepath": img.filepath,
        "source": img.source,
        "colorspace": getattr(img.colorspace_settings, "name", None),
        "alpha_mode": getattr(img, "alpha_mode", None),
        "size": [img.size[0], img.size[1]] if img.size else None,
        "packed": bool(img.packed_file),
    }


def dump_socket(s, is_output):
    d = {"name": s.name, "identifier": s.identifier, "type": s.type}
    if not is_output and hasattr(s, "default_value"):
        d["default"] = val(s.default_value)
    if getattr(s, "hide_value", False):
        d["hide_value"] = True
    return d


def dump_node(n):
    d = {
        "name": n.name,
        "label": n.label,
        "type": n.type,
        "bl_idname": n.bl_idname,
        "mute": n.mute,
        "location": [round(n.location[0], 1), round(n.location[1], 1)],
        "parent": n.parent.name if n.parent else None,
    }
    for p in NODE_PROPS:
        if hasattr(n, p):
            try:
                d[p] = val(getattr(n, p))
            except Exception:
                pass
    if n.type == "GROUP" and getattr(n, "node_tree", None):
        d["group"] = n.node_tree.name
    if hasattr(n, "image"):
        d["image"] = dump_image(n.image)
    if hasattr(n, "color_ramp"):
        d["color_ramp"] = dump_color_ramp(n.color_ramp)
    if hasattr(n, "mapping") and n.bl_idname in (
        "ShaderNodeRGBCurve", "ShaderNodeVectorCurve", "ShaderNodeFloatCurve",
    ):
        d["curve"] = dump_curve_mapping(n.mapping)
    if n.type == "TEX_IMAGE" and hasattr(n, "texture_mapping"):
        tm = n.texture_mapping
        d["texture_mapping"] = {
            "translation": val(tm.translation), "rotation": val(tm.rotation),
            "scale": val(tm.scale),
        }
    d["inputs"] = [dump_socket(s, False) for s in n.inputs]
    d["outputs"] = [dump_socket(s, True) for s in n.outputs]
    return d


def dump_tree(tree):
    if tree is None:
        return None
    nodes = [dump_node(n) for n in tree.nodes]
    links = []
    for l in tree.links:
        links.append({
            "from_node": l.from_node.name, "from_socket": l.from_socket.identifier,
            "to_node": l.to_node.name, "to_socket": l.to_socket.identifier,
            "valid": l.is_valid, "mute": l.is_muted,
        })
    out = {"nodes": nodes, "links": links}
    # Group interface (Blender 4.x moved this to tree.interface)
    iface = getattr(tree, "interface", None)
    if iface is not None:
        items = []
        for it in iface.items_tree:
            e = {"item_type": it.item_type, "name": it.name}
            if it.item_type == "SOCKET":
                e.update({
                    "in_out": it.in_out, "socket_type": it.socket_type,
                    "default": val(getattr(it, "default_value", None)),
                    "min": val(getattr(it, "min_value", None)),
                    "max": val(getattr(it, "max_value", None)),
                })
            items.append(e)
        out["interface"] = items
    else:
        out["interface"] = [
            {"item_type": "SOCKET", "in_out": "INPUT", "name": s.name,
             "socket_type": s.bl_socket_idname, "default": val(getattr(s, "default_value", None))}
            for s in getattr(tree, "inputs", [])
        ] + [
            {"item_type": "SOCKET", "in_out": "OUTPUT", "name": s.name,
             "socket_type": s.bl_socket_idname}
            for s in getattr(tree, "outputs", [])
        ]
    return out


def dump_scene():
    sc = bpy.context.scene
    ee = getattr(sc, "eevee", None)
    vs = getattr(sc, "view_settings", None)
    out = {
        "render_engine": sc.render.engine,
        "film_transparent": sc.render.film_transparent,
        "resolution": [sc.render.resolution_x, sc.render.resolution_y],
        "display_device": getattr(sc.display_settings, "display_device", None),
    }
    if vs:
        out["view_transform"] = vs.view_transform
        out["look"] = vs.look
        out["exposure"] = vs.exposure
        out["gamma"] = vs.gamma
        out["use_curve_mapping"] = vs.use_curve_mapping
        if vs.use_curve_mapping:
            out["view_curve"] = dump_curve_mapping(vs.curve_mapping)
    if ee:
        for p in ("use_bloom", "bloom_intensity", "bloom_threshold", "bloom_radius",
                  "bloom_color", "use_gtao", "gtao_distance", "gtao_factor",
                  "use_ssr", "use_soft_shadows", "shadow_cube_size",
                  "shadow_cascade_size", "taa_samples", "taa_render_samples",
                  "use_overscan", "light_threshold"):
            if hasattr(ee, p):
                out[p] = val(getattr(ee, p))
    return out


def dump_objects():
    objs = []
    for ob in bpy.data.objects:
        e = {"name": ob.name, "type": ob.type,
             "location": val(ob.location), "rotation_euler": val(ob.rotation_euler),
             "scale": val(ob.scale), "parent": ob.parent.name if ob.parent else None,
             "hide_render": ob.hide_render, "hide_viewport": ob.hide_viewport}
        if ob.type == "LIGHT" and ob.data:
            ld = ob.data
            e["light"] = {
                "type": ld.type, "color": val(ld.color), "energy": ld.energy,
                "specular": getattr(ld, "specular_factor", None),
                "angle": getattr(ld, "angle", None),
                "shadow": getattr(ld, "use_shadow", None),
            }
        if ob.type == "CAMERA" and ob.data:
            e["camera"] = {"lens": ob.data.lens, "type": ob.data.type,
                           "clip_start": ob.data.clip_start, "clip_end": ob.data.clip_end}
        if ob.type == "MESH" and ob.data:
            me = ob.data
            e["mesh"] = {
                "verts": len(me.vertices), "polys": len(me.polygons),
                "uv_layers": [l.name for l in me.uv_layers],
                "color_attributes": [
                    {"name": c.name, "domain": c.domain, "data_type": c.data_type}
                    for c in getattr(me, "color_attributes", [])
                ],
                "attributes": [
                    {"name": a.name, "domain": a.domain, "data_type": a.data_type}
                    for a in getattr(me, "attributes", [])
                ],
                "materials": [m.name if m else None for m in me.materials],
            }
            e["modifiers"] = [{"name": m.name, "type": m.type} for m in ob.modifiers]
        objs.append(e)
    return objs


def main():
    out_path = parse_args()
    data = {
        "formatVersion": 2,
        "blender": bpy.app.version_string,
        "file": bpy.data.filepath,
        "scene": dump_scene(),
        "objects": dump_objects(),
        "node_groups": {ng.name: dump_tree(ng) for ng in bpy.data.node_groups},
        "materials": {
            m.name: {
                "use_nodes": m.use_nodes,
                "blend_method": getattr(m, "blend_method", None),
                "shadow_method": getattr(m, "shadow_method", None),
                "alpha_threshold": getattr(m, "alpha_threshold", None),
                "use_backface_culling": getattr(m, "use_backface_culling", None),
                "show_transparent_back": getattr(m, "show_transparent_back", None),
                "diffuse_color": val(m.diffuse_color),
                "tree": dump_tree(m.node_tree) if m.use_nodes else None,
            }
            for m in bpy.data.materials
        },
        "worlds": {w.name: dump_tree(w.node_tree) if w.use_nodes else None
                   for w in bpy.data.worlds},
        "images": [dump_image(i) for i in bpy.data.images],
    }
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
    print(f"[export_graph] groups={len(data['node_groups'])} "
          f"materials={len(data['materials'])} -> {out_path}")


if __name__ == "__main__":
    main()
