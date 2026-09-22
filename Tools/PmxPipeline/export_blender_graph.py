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
import hashlib
import json
import os
import re
import sys

import bpy

# EEVEE 把 ColorRamp / RGB Curves 都烤成 CM_TABLE + 1 = 257 项的一维表，这个长度是
# 写死的魔数。表由 Blender 自己求值（ColorRamp.evaluate / CurveMapping.evaluate），
# 不在 Python 里重算 B 样条与贝塞尔——重算等于再实现一遍 colorband.cc 与
# curve_mapping.cc，属于白担风险。
CM_TABLE_SIZE = 257

# 表内容 -> key。同一条 ramp 在多个材质里复用，按内容去重后整份 JSON 只多几十 KB。
LUTS = {}
LUT_WARNINGS = []


def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    out = None
    images = None
    for i, a in enumerate(argv):
        if a == "--out" and i + 1 < len(argv):
            out = argv[i + 1]
        elif a == "--images" and i + 1 < len(argv):
            images = argv[i + 1]
    return out, images


# 仓库根。脚本位于 <repo>/Tools/PmxPipeline/ 下。
REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))


def require_in_repo(path, what):
    """确保产物落在仓库内。

    曾经因为调用方传了相对路径、而 Blender 的工作目录不是仓库根，把 19 张贴图写进了
    C 盘根目录的 Build 里——正好违反「零外部污染」这条硬约束，而且没有任何报错。相对路径在这里
    一律按仓库根解析，解析完仍在仓库外就直接失败，不静默写出去。
    """
    p = path if os.path.isabs(path) else os.path.join(REPO_ROOT, path)
    p = os.path.abspath(p)
    if os.path.commonpath([p, REPO_ROOT]) != REPO_ROOT:
        raise SystemExit("[export_graph] !! %s 落在仓库外，拒绝写入：%s" % (what, p))
    return p


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
    # - is_active_output: 一棵树里可以有多个 Material Output / Group Output，只有
    #   活动的那个生效。不导它就只能「取第一个」，属于静默取错分支。
    # - factor_mode: ShaderNodeMix 的 Factor 是统一还是逐分量，决定读哪个插槽。
    "is_active_output", "factor_mode",
)


def _lut_key(kind, payload, table):
    h = hashlib.sha1(
        json.dumps(payload, sort_keys=True, ensure_ascii=False).encode("utf-8")
    ).hexdigest()[:16]
    key = "%s_%s" % (kind, h)
    LUTS.setdefault(key, {"kind": kind, "size": len(table), "table": table})
    return key


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
    out["table_key"] = _curve_table_key(cm, out)
    return out


def _curve_table_key(cm, payload):
    """烤 RGB 曲线的 257 项表。

    求值顺序是「**先合成曲线 cm[3]、再逐通道曲线 cm[0..2]**」，即
    cm[ch](cm[3](x))，和 BKE_curvemapping_evaluateRGBF 一致。cm[3] 是 UI 上的 C
    通道，cm[0..2] 是 R/G/B。

    这个顺序是实测定的，不是照文档抄的：给 C 与 R 各设一条明显非恒等的曲线、喂已知
    常量、用 Standard 视图变换渲一张 EXR，结果只和这一种顺序对得上。写反了不会报错，
    只有在 C 与某条通道曲线都非恒等时才显形——大月下那块大纱的自发光走的正是这条
    链（Strength 5.1），整片偏亮 ΔE 8。
    """
    n = len(cm.curves)
    if n < 4:
        LUT_WARNINGS.append("曲线只有 %d 条，按逐通道恒等处理" % n)
    combined = cm.curves[3] if n >= 4 else None

    # 表的定义域是各通道首末控制点的 x（Blender 的 mintable/maxtable）。Python 不
    # 暴露这两个值，用控制点反推；本项目四个预设全是 [0,1]，越界就报警而不是默默
    # 按 [0,1] 烤。
    for i, c in enumerate(cm.curves):
        if not c.points:
            continue
        x0, x1 = c.points[0].location[0], c.points[-1].location[0]
        if abs(x0) > 1e-6 or abs(x1 - 1.0) > 1e-6:
            LUT_WARNINGS.append(
                "曲线通道 %d 的定义域是 [%.4f, %.4f]，不是 [0,1]，"
                "烤表按 [0,1] 采样会偏" % (i, x0, x1))

    table = []
    for i in range(CM_TABLE_SIZE):
        x = i / float(CM_TABLE_SIZE - 1)
        row = []
        for ch in range(3):
            v = cm.evaluate(combined, x) if combined else x
            row.append(cm.evaluate(cm.curves[ch], v) if ch < n else v)
        # 第 4 项对齐 BKE_curvemapping_table_RGBA 的布局（它放的是 cm[3] 自己的表）
        row.append(cm.evaluate(combined, x) if combined else x)
        table.append(row)
    return _lut_key("curve", payload, table)


def dump_color_ramp(ramp):
    if ramp is None:
        return None
    d = {
        "color_mode": ramp.color_mode,
        "interpolation": ramp.interpolation,
        "hue_interpolation": getattr(ramp, "hue_interpolation", None),
        "elements": [
            {"position": e.position, "color": val(e.color), "alpha": e.alpha}
            for e in ramp.elements
        ],
    }
    # ColorRamp.evaluate 就是 BKE_colorband_evaluate，与 EEVEE 烤表用的是同一个函数
    d["table_key"] = _lut_key("ramp", d, [
        list(ramp.evaluate(i / float(CM_TABLE_SIZE - 1)))
        for i in range(CM_TABLE_SIZE)
    ])
    return d


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
    # 输出插槽的 default_value 也必须导出：Value / RGB 这类常量节点没有输入，常量
    # 就存在输出插槽上。漏掉它不会报错，只会让整条链静默变成 0——灯的头发高光因此
    # 变成一块黑斑。Shader 类插槽没有 default_value，由 hasattr 挡掉。
    if hasattr(s, "default_value"):
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


_UNSAFE = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


def _image_basename(img):
    """把 Blender 的图片数据块名变成一个安全的 PNG 文件名。

    数据块名常带原扩展名与去重后缀（`眼睛.png`、`hair_s.bmp.001`、`裙子.png.001`），
    直接拼 `.png` 会得到 `眼睛.png.png` 这类名字，先剥掉。
    """
    name = _UNSAFE.sub("_", img.name).strip()
    name = re.sub(r"\.(\d{3})$", r"_\1", name)
    name = re.sub(r"\.(png|jpg|jpeg|tga|bmp|tif|tiff|exr|dds|psd)$", "",
                  name, flags=re.IGNORECASE)
    return (name or "image") + ".png"


def extract_images(out_dir):
    """把预设内**打包**的贴图全部落成 PNG，供 Unity 侧直接读字节。

    预设里 19 张图全是 packed=True，原始文件在作者机器上，本机不存在——不导出就
    没法在 Unity 里复现任何贴图采样。用 Image.save() 写的是像素缓冲本身，不过
    色彩管理；色彩空间（sRGB / Non-Color）随清单一起交给 Unity 决定采样时怎么解码。
    """
    out_dir = os.path.abspath(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    manifest = []
    for img in bpy.data.images:
        if img.size[0] == 0 or img.size[1] == 0:
            print("[export_graph] 跳过空图片 %r" % img.name)
            continue
        fname = _image_basename(img)
        # 必须传绝对路径并显式给 filepath：Image.save() 用的是 image.filepath，
        # 而那是 blend 相对路径（`//原始模型\...`），指向作者机器上的目录。
        path = os.path.join(out_dir, fname)
        prev_format = img.file_format
        try:
            img.file_format = "PNG"
            img.save(filepath=path)
            ok = os.path.exists(path)
            if not ok:
                print("[export_graph] 导出贴图无输出 %r -> %s" % (img.name, path))
        except Exception as e:                                  # noqa: BLE001
            print("[export_graph] 导出贴图失败 %r: %s" % (img.name, e))
            ok = False
        finally:
            img.file_format = prev_format
        if not ok:
            continue
        manifest.append({
            "name": img.name, "file": fname,
            "colorspace": getattr(img.colorspace_settings, "name", None),
            "alpha_mode": getattr(img, "alpha_mode", None),
            "size": [img.size[0], img.size[1]],
            "channels": img.channels,
        })
    with open(os.path.join(out_dir, "images.json"), "w", encoding="utf-8") as f:
        json.dump({"images": manifest}, f, ensure_ascii=False, indent=1)
    print("[export_graph] 导出贴图 %d 张 -> %s" % (len(manifest), out_dir))


def main():
    out_path, images_dir = parse_args()
    out_path = require_in_repo(out_path, "节点图")
    if images_dir:
        images_dir = require_in_repo(images_dir, "贴图目录")
    data = {
        "formatVersion": 5,
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
    # luts 必须在所有树 dump 完之后再取——表是遍历过程中攒出来的
    data["luts"] = LUTS
    for w in sorted(set(LUT_WARNINGS)):
        print("[export_graph] !! %s" % w)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
    print(f"[export_graph] groups={len(data['node_groups'])} "
          f"materials={len(data['materials'])} luts={len(LUTS)} -> {out_path}")
    if images_dir:
        extract_images(images_dir)


if __name__ == "__main__":
    main()
