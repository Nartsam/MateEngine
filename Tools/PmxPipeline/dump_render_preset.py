import argparse
import json
import math
import os
import sys

import bpy


COLOR_KEYS = (
    "color", "colour", "base", "diffuse", "albedo", "main", "颜色", "色",
)
SHADE_KEYS = (
    "shade", "shadow", "dark", "toon", "阴影", "暗", "影",
)
HIGH_COLOR_KEYS = (
    "highlight", "high color", "highcolor", "specular", "sparkle", "glitter",
    "高光", "闪", "亮",
)
RIM_KEYS = ("rim", "边缘光", "边缘", "轮廓光")
EMISSION_KEYS = ("emission", "emit", "glow", "自发光", "发光")
OUTLINE_KEYS = ("outline", "edge", "描边", "轮廓")
MATCAP_KEYS = ("matcap", "sphere", "spheremap", "反射")
MASK_KEYS = ("mask", "lightmap", "light map", "遮罩", "掩码")
EXPORTED_IMAGES = {}


def parse_args():
    argv = sys.argv
    if "--" in argv:
        argv = argv[argv.index("--") + 1:]
    else:
        argv = []

    parser = argparse.ArgumentParser(description="Dump portable render preset data from a Blender .blend file.")
    parser.add_argument("--preset", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--texture-root")
    return parser.parse_args(argv)


def normalize_name(value):
    return (value or "").strip()


def trim_suffix(value):
    value = normalize_name(value)
    head, dot, tail = value.rpartition(".")
    if dot and tail.isdigit():
        return head
    return value


def color_list(value):
    try:
        vals = list(value)
    except TypeError:
        return None
    if len(vals) < 3:
        return None
    if len(vals) == 3:
        vals.append(1.0)
    return [float(vals[0]), float(vals[1]), float(vals[2]), float(vals[3])]


def float_value(value):
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if math.isfinite(float(value)):
            return float(value)
    return None


def key_match(text, keys):
    text = (text or "").lower()
    return any(k.lower() in text for k in keys)


def safe_file_name(value):
    value = normalize_name(value)
    invalid = '<>:"/\\|?*'
    for char in invalid:
        value = value.replace(char, "_")
    return value.strip().strip(".") or "image"


def export_image(image, texture_root):
    if not image or not texture_root:
        return None
    key = image.name
    if key in EXPORTED_IMAGES:
        return EXPORTED_IMAGES[key]

    base = os.path.splitext(os.path.basename(image.filepath or ""))[0]
    if not base:
        base = image.name
    file_name = safe_file_name(base) + ".png"
    output = os.path.join(texture_root, file_name)
    suffix = 1
    while os.path.exists(output):
        file_name = f"{safe_file_name(base)}_{suffix}.png"
        output = os.path.join(texture_root, file_name)
        suffix += 1

    try:
        os.makedirs(texture_root, exist_ok=True)
        old_path = image.filepath_raw
        old_format = image.file_format
        try:
            image.filepath_raw = output
            image.file_format = "PNG"
            image.save()
        finally:
            image.filepath_raw = old_path
            image.file_format = old_format
        rel = os.path.relpath(output, os.getcwd()).replace("\\", "/")
        EXPORTED_IMAGES[key] = rel
        return rel
    except Exception:
        return None


def image_name(image, preset_dir, texture_root):
    if not image:
        return None
    exported = export_image(image, texture_root)
    if exported:
        return exported
    path = bpy.path.abspath(image.filepath) if image.filepath else ""
    if path:
        try:
            return os.path.relpath(path, preset_dir).replace("\\", "/")
        except ValueError:
            return os.path.basename(path)
    return image.name


def assign_color(material, label, values):
    if not values:
        return
    if key_match(label, EMISSION_KEYS):
        material["emissionColor"] = values
        material["useEmission"] = True
    elif key_match(label, HIGH_COLOR_KEYS):
        material["highColor"] = values
        material["useHighColor"] = True
    elif key_match(label, RIM_KEYS):
        material["rimColor"] = values
        material["useRim"] = True
    elif key_match(label, OUTLINE_KEYS):
        material["outlineColor"] = values
        material["useOutline"] = True
    elif key_match(label, SHADE_KEYS):
        if "shadeColor" not in material:
            material["shadeColor"] = values
        else:
            material["shadeColor2"] = values
    elif key_match(label, COLOR_KEYS):
        material["baseColor"] = values


def assign_float(material, label, value):
    if value is None:
        return
    if key_match(label, OUTLINE_KEYS) and ("width" in label.lower() or "幅" in label):
        material["outlineWidth"] = value
        material["useOutline"] = value > 0.0
    elif key_match(label, HIGH_COLOR_KEYS) and (
        "power" in label.lower() or "strength" in label.lower()
        or "intensity" in label.lower() or "強" in label
    ):
        material["highColorPower"] = value
        material["useHighColor"] = value > 0.0
    elif key_match(label, EMISSION_KEYS) and ("strength" in label.lower() or "intensity" in label.lower() or "強" in label):
        material["emissionStrength"] = value
        material["useEmission"] = value > 0.0
    elif key_match(label, RIM_KEYS) and ("power" in label.lower() or "strength" in label.lower()):
        material["rimPower"] = value
        material["useRim"] = value > 0.0
    elif "alpha" in label.lower() or "透明" in label:
        # Generic node-group Alpha inputs often default to 0 even for opaque
        # materials. Trust Blender's material blend_method / Transparent BSDF
        # instead; Unity samples texture alpha separately for cutout decisions.
        return


def assign_texture(material, label, texture):
    if not texture:
        return
    material.setdefault("images", [])
    if texture not in material["images"]:
        material["images"].append(texture)

    label_and_texture = f"{label} {texture}"
    if key_match(label, MATCAP_KEYS):
        material["matcapTexture"] = texture
        material["useMatcap"] = True
    elif key_match(label, EMISSION_KEYS):
        material["emissionTexture"] = texture
        material["useEmission"] = True
    elif key_match(label_and_texture, HIGH_COLOR_KEYS):
        material["highColorTexture"] = texture
        material["useHighColor"] = True
    elif key_match(label_and_texture, MASK_KEYS):
        material.setdefault("maskTexture", texture)
        material["highColorMaskTexture"] = texture
        material["useHighColor"] = True
    elif key_match(label, SHADE_KEYS):
        material["shadeTexture"] = texture
    elif "toon" in label.lower() or "toon" in texture.lower():
        material["toonTexture"] = texture
    elif "baseTexture" not in material:
        material["baseTexture"] = texture


def walk_node_tree(material, node_tree, preset_dir, texture_root, nodes, prefix="", depth=0, stack=None):
    if not node_tree or depth > 4:
        return
    if stack is None:
        stack = set()

    for node in node_tree.nodes:
        parts = [prefix, node.name, node.label, getattr(node, "type", "")]
        if node.type == "GROUP" and getattr(node, "node_tree", None):
            parts.append(node.node_tree.name)
        label = " ".join(filter(None, parts))
        node_info = {"name": node.name, "label": node.label, "type": node.type}
        if node.type == "GROUP" and getattr(node, "node_tree", None):
            node_info["group"] = node.node_tree.name
        if node.type == "BSDF_TRANSPARENT":
            # Genshin-style NPR node trees commonly keep a Transparent BSDF in
            # every material group for optional clipping/masking. Its presence
            # alone does not mean the final material should use transparent
            # rendering in Unity; use Blender's material blend_method or actual
            # alpha values for that decision.
            material["hasTransparentBsdf"] = True
        if node.type == "TEX_IMAGE":
            tex = image_name(node.image, preset_dir, texture_root)
            node_info["image"] = tex
            assign_texture(material, label, tex)
        if node.type == "TEX_VORONOI":
            material["hasVoronoi"] = True
        if node.type == "VALTORGB":
            material["hasColorRamp"] = True
            ramp = getattr(node, "color_ramp", None)
            if ramp:
                colors = [color_list(element.color) for element in ramp.elements]
                colors = [c for c in colors if c]
                if colors:
                    node_info["colors"] = colors[:8]
                    if key_match(label, HIGH_COLOR_KEYS):
                        material["highColor"] = colors[-1]
                        material["useHighColor"] = True

        for socket in list(getattr(node, "inputs", [])) + list(getattr(node, "outputs", [])):
            socket_label = f"{label} {socket.name}"
            if hasattr(socket, "default_value"):
                c = color_list(socket.default_value)
                if c:
                    assign_color(material, socket_label, c)
                else:
                    assign_float(material, socket_label, float_value(socket.default_value))

        nodes.append(node_info)

        group_tree = getattr(node, "node_tree", None)
        if node.type == "GROUP" and group_tree:
            ptr = group_tree.as_pointer()
            if ptr not in stack:
                next_stack = set(stack)
                next_stack.add(ptr)
                next_prefix = f"{prefix}/{node.name}:{group_tree.name}" if prefix else f"{node.name}:{group_tree.name}"
                walk_node_tree(material, group_tree, preset_dir, texture_root, nodes, next_prefix, depth + 1, next_stack)


def dump_material(mat, preset_dir, texture_root):
    blend_method = getattr(mat, "blend_method", "OPAQUE")
    diffuse = color_list(mat.diffuse_color) if hasattr(mat, "diffuse_color") else [1.0, 1.0, 1.0, 1.0]
    diffuse_alpha = diffuse[3] if diffuse else 1.0
    alpha_from_blend = blend_method in {"BLEND", "HASHED", "CLIP"}
    out = {
        "name": mat.name,
        "trimmedName": trim_suffix(mat.name),
        "alpha": float(diffuse_alpha if alpha_from_blend or not mat.use_nodes else 1.0),
        "hasTransparentBsdf": False,
    }
    if diffuse:
        out["baseColor"] = diffuse
        if not alpha_from_blend and mat.use_nodes:
            out["baseColor"][3] = 1.0
        out["alphaBlend"] = blend_method in {"BLEND", "HASHED"} or out["alpha"] < 0.995
        out["alphaClip"] = blend_method in {"CLIP", "HASHED"}

    if not mat.use_nodes or not mat.node_tree:
        return out

    nodes = []
    walk_node_tree(out, mat.node_tree, preset_dir, texture_root, nodes)
    if out.get("hasVoronoi") and out.get("hasColorRamp"):
        # This preset uses procedural dots/ramp for small sparkling highlights.
        # Unity's v1 mapper approximates that with UTS2 high color instead of
        # trying to bake Blender procedural nodes into local textures.
        out["useProceduralSparkle"] = True
        out["useHighColor"] = True
        out.setdefault("highColor", [0.86, 0.58, 1.0, 1.0])
        out.setdefault("highColorPower", 0.34)
    out["nodeSummary"] = nodes[:128]
    out["alphaClip"] = bool(out.get("alphaClip", False) or out.get("alphaBlend", False))
    return out


def dump_scene():
    scene = bpy.context.scene
    eevee = getattr(scene, "eevee", None)
    view = getattr(scene, "view_settings", None)
    out = {}
    if eevee:
        out["bloomEnabled"] = bool(getattr(eevee, "use_bloom", False))
        out["bloomIntensity"] = float(getattr(eevee, "bloom_intensity", 0.0))
        out["bloomColor"] = color_list(getattr(eevee, "bloom_color", (1.0, 1.0, 1.0)))
    if view:
        out["viewTransform"] = getattr(view, "view_transform", "")
        out["look"] = getattr(view, "look", "")

    locator = None
    for ob in bpy.data.objects:
        if "面部定位" in ob.name or ob.name.lower() in ("face locator", "face_locator"):
            locator = ob
            break
    if locator:
        out["hasFaceLocator"] = True
        out["faceLocatorName"] = locator.name
        out["faceLocatorPosition"] = [float(v) for v in locator.location]
        out["faceLocatorRotation"] = [float(v) for v in locator.rotation_euler]
        out["faceLocatorScale"] = [float(v) for v in locator.scale]
    return out


def main():
    args = parse_args()
    preset = os.path.abspath(args.preset)
    output = os.path.abspath(args.out)
    texture_root = os.path.abspath(args.texture_root) if args.texture_root else None
    preset_dir = os.path.dirname(preset)

    bpy.ops.wm.open_mainfile(filepath=preset)

    data = {
        "formatVersion": 1,
        "sourceBlendName": os.path.basename(preset),
        "scene": dump_scene(),
        "materials": [dump_material(mat, preset_dir, texture_root) for mat in bpy.data.materials],
    }

    os.makedirs(os.path.dirname(output), exist_ok=True)
    with open(output, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)

    print(f"[dump_render_preset] materials={len(data['materials'])} -> {output}")


if __name__ == "__main__":
    main()
