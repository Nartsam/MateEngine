"""无头复刻「导入 PMX → 应用渲染预设 → 渲染」的完整手工流程，并渲出基准图。

这是差分验收闭环的 Blender 一侧（见 Docs/plans/2026-09-10-blender-graph-transpile.md
Phase 0）。它要复刻的手工流程是：

    ① 用 mmd_tools 导入 PMX
    ② 物体模式下点选角色网格（使其成为活动物体）
    ③ 侧边栏 RenderTools ▸ 导入预设，选中 渲染预设.blend
    ④ 视图着色方式切到「渲染」

**不重写插件逻辑**：第 ③ 步直接 import 插件模块并调用它的 load_presets()，所以
不存在「复刻得像不像」的问题——跑的就是同一份代码。插件唯一依赖的 UI 状态是
bpy.context.object（活动物体），无头下由第 ② 步显式设置即可。

用法：

    blender.exe -b --factory-startup -P Tools/PmxPipeline/blender_apply_preset.py -- \
        --pmx "<角色>/删披风删骨骼_修复.pmx" \
        --preset "<角色>/渲染预设.blend" \
        --out out/ref_front.png \
        --report out/ref_front.json

其余参数：

    --expect    预设节点图 JSON（Docs/RenderPreset/graphs/<角色>.graph.json），
                给了就逐槽比对节点树，报告里给出「一致 N/26」
    --view      front/back/left/right/three_quarter，或直接给方位角度数（如 35）
    --bg        transparent（默认）或 sRGB 十六进制（如 808080）。注意给的是**场景
                线性**颜色，背景同样要过 Filmic 视图变换，所以出图里的背景不等于这个
                值（实测 808080 渲出来是 8C8C8C）。做差分请以 alpha 轮廓为准，别依赖背景。
    --res       正方形边长，默认 1024
    --samples   EEVEE 渲染采样数，默认 64
    --margin    相机框选留白系数，默认 1.06
    --sun-euler "X,Y,Z"（度）——覆盖预设 `灯光` 物体的朝向，也就是日光方向。
                差分验收必须显式钉死它：预设默认是 60,0,0，但它是自由参数，
                不同朝向下画面差别极大。
    --save-blend 存一份应用完预设的 .blend，方便在 GUI 里人工核对

--report 里的 drivers 段给出实测的 SUN / RIGHT / FRONT，即 Unity 侧要对齐的
三个 uniform。

**必须用 Blender 3.6**（插件按 3.6 API 写；5.x 已无 node_group.outputs / eevee.use_bloom）。

只读外部素材，产物全部落在 --out / --report / --save-blend 指定的路径。不写用户
偏好（addon_utils.enable 不持久化），不改全局环境。
"""
import hashlib
import importlib.util
import json
import math
import os
import sys
import traceback

import bpy
from mathutils import Vector

# 插件默认值，与用户手工操作时对话框里的默认值一致
FACE_LOCATOR_NAME = "面部定位"
HEAD_BONE_NAME = "頭"
MATCH_MODE_STRIP_SUFFIX = 2  # "剪去后缀"，插件默认项
SET_SCENE_EFFECT = True

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
VENDOR_PLUGIN = os.path.join(REPO_ROOT, "Docs", "RenderPreset", "vendor", "模型预设导入.py")
INSTALLED_PLUGIN = os.path.join(
    os.path.dirname(bpy.app.binary_path), "3.6", "scripts", "addons", "模型预设导入.py"
)

log = []


def say(msg):
    """双写：stdout 给人看（带前缀便于从 Blender 噪声里 grep），log 进报告。"""
    log.append(msg)
    print("[apply_preset] " + msg)
    sys.stdout.flush()


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    args = {
        "pmx": None, "preset": None, "out": None, "report": None,
        "save_blend": None, "res": 1024, "view": "front", "bg": "transparent",
        "margin": 1.06, "samples": 64, "expect": None, "sun_euler": None,
    }
    i = 0
    while i < len(argv):
        a = argv[i]
        key = a[2:].replace("-", "_") if a.startswith("--") else None
        if key in args and i + 1 < len(argv):
            v = argv[i + 1]
            args[key] = int(v) if key in ("res", "samples") else (
                float(v) if key == "margin" else v)
            i += 2
        else:
            raise SystemExit("无法识别的参数: " + a)
    if not args["pmx"] or not args["preset"]:
        raise SystemExit("需要 --pmx <file.pmx> --preset <渲染预设.blend>")
    return args


def load_plugin_module():
    """加载配套插件。优先用仓库里的副本，并与已安装版对比哈希，不一致时告警。"""
    path = VENDOR_PLUGIN
    if not os.path.exists(path):
        path = INSTALLED_PLUGIN
        say("仓库副本缺失，改用已安装插件: " + path)
    digest = hashlib.sha256(open(path, "rb").read()).hexdigest()[:16]
    say("插件来源 %s (sha256:%s)" % (os.path.basename(path), digest))
    if path == VENDOR_PLUGIN and os.path.exists(INSTALLED_PLUGIN):
        other = hashlib.sha256(open(INSTALLED_PLUGIN, "rb").read()).hexdigest()[:16]
        if other != digest:
            say("!! 警告：仓库副本与已安装插件不一致 (installed sha256:%s)" % other)
    # 不让 exec_module 在 vendor/ 下留 __pycache__，免得污染仓库
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("preset_importer", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def enable_mmd_tools():
    import addon_utils
    # persistent=False 且不调 save_userpref，所以不污染用户偏好
    addon_utils.enable("mmd_tools", default_set=False, persistent=False)
    if not hasattr(bpy.ops.mmd_tools, "import_model"):
        raise SystemExit("mmd_tools 未能启用")
    say("mmd_tools 已启用")


def import_pmx(pmx_path):
    """导入 PMX。注意 ImportPmx.execute 会把异常吞成 report 后仍返回 FINISHED，
    所以成功与否只能靠核查产物判断。"""
    before = set(bpy.data.objects.keys())
    bpy.ops.mmd_tools.import_model(filepath=pmx_path, scale=0.08)
    new = [bpy.data.objects[n] for n in bpy.data.objects.keys() if n not in before]
    meshes = [o for o in new if o.type == "MESH"]
    if not meshes:
        raise SystemExit("PMX 导入失败：没有产生任何网格物体（见上方 mmd_tools 报错）")
    mesh = max(meshes, key=lambda o: len(o.data.polygons))
    arms = [o for o in new if o.type == "ARMATURE"]
    say("导入 %d 个物体；网格 %r（%d 面 / %d 材质槽），骨架 %r" % (
        len(new), mesh.name, len(mesh.data.polygons), len(mesh.material_slots),
        arms[0].name if arms else None))
    return mesh, (arms[0] if arms else None)


def make_active(obj):
    """等价于手工流程第 ② 步：物体模式下点选角色网格。"""
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    if bpy.context.object is not obj:
        raise SystemExit("无法把 %r 设为活动物体" % obj.name)
    say("活动物体 = %r（插件唯一依赖的 UI 状态已就位）" % bpy.context.object.name)


def preset_material_names(preset_path):
    with bpy.data.libraries.load(preset_path) as (data_from, _):
        return list(data_from.materials)


def base_name(name):
    """还原数据块的「本名」。插件会给旧数据块加 actual_ 前缀，Blender 遇到重名会加
    .001 后缀——两者都与材质是否正确无关，比对前必须剥掉。"""
    if name.startswith("actual_"):
        name = name[len("actual_"):]
    head, sep, tail = name.rpartition(".")
    return head if sep and tail.isdigit() and len(tail) == 3 else name


def tree_signature(nodes):
    """节点树指纹：节点数 + 类型直方图 + 引用到的节点组本名。

    不用 library_weak_reference 判断材质是否来自预设——实测那个标记在插件的
    重命名/remap 之后并不可靠（丽塔 26 个槽里有 10 个丢了标记，但节点树证明
    它们确实是预设材质）。节点树本身才是硬证据。
    """
    types = {}
    groups = []
    for n in nodes:
        t = n["type"] if isinstance(n, dict) else n.type
        types[t] = types.get(t, 0) + 1
        if t == "GROUP":
            g = n.get("group") if isinstance(n, dict) else (
                n.node_tree.name if n.node_tree else None)
            if g:
                groups.append(base_name(g))
    return {"count": len(nodes), "types": types, "groups": sorted(groups)}


def inspect(mesh, arm, preset_path, lib_mat_names, expect_graph):
    """核查预设是否真的生效。这些判据就是「无头能否完整复刻」的证据。"""
    rep = {}
    expect_mats = (expect_graph or {}).get("materials", {})

    slots = []
    matched = 0
    for s in mesh.material_slots:
        m = s.material
        nodes = list(m.node_tree.nodes) if (m and m.use_nodes) else []
        sig = tree_signature(nodes)
        entry = {"slot": s.name, "material": m.name if m else None, "signature": sig}

        exp = expect_mats.get(base_name(m.name)) if m else None
        if exp is not None:
            esig = tree_signature(exp["tree"]["nodes"])
            same = (esig["count"] == sig["count"] and esig["types"] == sig["types"]
                    and esig["groups"] == sig["groups"])
            entry["matches_preset"] = same
            matched += 1 if same else 0
            if not same:
                entry["expected_signature"] = esig
        else:
            entry["matches_preset"] = None
        slots.append(entry)

    rep["slots"] = slots
    rep["slot_total"] = len(slots)
    rep["slot_matched"] = matched
    if expect_mats:
        say("材质槽 %d 个，节点树与预设一致的 %d 个" % (len(slots), matched))
        bad = [s["slot"] for s in slots if s["matches_preset"] is not True]
        if bad:
            say("!! 与预设不一致的槽：" + ", ".join(bad))
    else:
        say("材质槽 %d 个（未提供 --expect，跳过节点树比对）" % len(slots))

    # 几何节点修改器：描边等实体化效果靠它，且它负责写 SUN/RIGHT/FRONT 属性
    mods = []
    for m in mesh.modifiers:
        d = {"name": m.name, "type": m.type}
        if m.type == "NODES":
            ng = m.node_group
            d["node_group"] = ng.name if ng else None
            d["outputs"] = [{"name": o.name, "attr": o.default_attribute_name}
                            for o in ng.outputs] if ng else []
        mods.append(d)
    rep["modifiers"] = mods
    nodes_mods = [m for m in mods if m["type"] == "NODES"]
    say("修改器 %d 个（几何节点 %d 个：%s）" % (
        len(mods), len(nodes_mods), ", ".join(m["name"] for m in nodes_mods)))
    attrs = sorted({o["attr"] for m in nodes_mods for o in m.get("outputs", [])
                    if o["attr"]})
    rep["geo_output_attrs"] = attrs
    say("几何节点输出属性名：" + (", ".join(attrs) if attrs else "(无)"))

    # 面部定位：必须 parent 到头骨，脸部 SDF 阴影靠它跟随头部转动
    loc = bpy.data.objects.get(FACE_LOCATOR_NAME)
    rep["face_locator"] = None
    if loc:
        rep["face_locator"] = {
            "name": loc.name, "parent": loc.parent.name if loc.parent else None,
            "parent_type": loc.parent_type, "parent_bone": loc.parent_bone,
            "world_loc": [round(v, 6) for v in loc.matrix_world.translation],
        }
        say("面部定位：parent=%r type=%s bone=%r 世界坐标=%s" % (
            loc.parent.name if loc.parent else None, loc.parent_type, loc.parent_bone,
            ["%.3f" % v for v in loc.matrix_world.translation]))
    else:
        say("!! 面部定位物体缺失")
    rep["head_bone_present"] = bool(
        arm and HEAD_BONE_NAME in {b.name for b in arm.data.bones})
    say("头骨 %r 存在：%s" % (HEAD_BONE_NAME, rep["head_bone_present"]))

    rep["drivers"] = read_drivers(mesh)

    sc = bpy.context.scene
    rep["scene_effect"] = {
        "use_bloom": sc.eevee.use_bloom,
        "bloom_intensity": round(sc.eevee.bloom_intensity, 6),
        "bloom_color": [round(c, 6) for c in sc.eevee.bloom_color],
        "view_transform": sc.view_settings.view_transform,
        "look": sc.view_settings.look,
    }
    say("场景效果：bloom=%s(%.3f) view_transform=%s look=%s" % (
        sc.eevee.use_bloom, sc.eevee.bloom_intensity,
        sc.view_settings.view_transform, sc.view_settings.look))

    # PMX 的刚体/关节有上千个，其中刚体是 hide_render 的 MESH——按 hide_render
    # 过滤才能只剩真正参与渲染的物体
    renderable = sorted(o.name for o in bpy.context.scene.collection.objects
                        if o.type in {"MESH", "LIGHT"} and not o.hide_render)
    rep["scene_objects_total"] = len(bpy.context.scene.collection.objects)
    rep["scene_renderable_objects"] = renderable
    say("场景物体 %d 个（可渲染 %d：%s）" % (
        rep["scene_objects_total"], len(renderable), ", ".join(renderable)))
    return rep


DRIVER_ATTRS = ("SUN", "RIGHT", "FRONT")


def read_drivers(mesh):
    """从几何节点求值后的网格上直接读出 SUN / RIGHT / FRONT。

    这三个属性由「Geometry Nodes」修改器写入，分别来自 `灯光`（世界日光方向）和
    `面部定位`（头部朝向基向量）两个物体的朝向。它们就是 Unity 侧要对齐的三个
    uniform，与其在转译时重推节点运算，不如在这里直接测出来。
    """
    deps = bpy.context.evaluated_depsgraph_get()
    ev = mesh.evaluated_get(deps)
    me = ev.data
    out = {}
    for name in DRIVER_ATTRS:
        a = me.attributes.get(name)
        if a is None or len(a.data) == 0:
            out[name] = None
            say("!! 属性 %s 不存在" % name)
            continue
        vals = [tuple(round(c, 6) for c in d.vector) for d in a.data]
        first = vals[0]
        uniform = all(v == first for v in vals)
        out[name] = {"domain": a.domain, "data_type": a.data_type,
                     "count": len(vals), "constant": uniform, "value": list(first)}
        say("%-5s = (%8.5f, %8.5f, %8.5f)  %s（%d 个元素）" % (
            name, first[0], first[1], first[2],
            "全网格一致" if uniform else "!! 逐元素不同", len(vals)))

    for obj_name in ("灯光", FACE_LOCATOR_NAME):
        o = bpy.data.objects.get(obj_name)
        if o:
            out[obj_name] = {
                "rotation_euler_deg": [round(math.degrees(v), 4)
                                       for v in o.rotation_euler],
                "matrix_world": [[round(v, 6) for v in row] for row in o.matrix_world],
            }
    return out


def world_bbox(objs):
    lo = Vector((math.inf,) * 3)
    hi = Vector((-math.inf,) * 3)
    for o in objs:
        for c in o.bound_box:
            p = o.matrix_world @ Vector(c)
            for i in range(3):
                lo[i] = min(lo[i], p[i])
                hi[i] = max(hi[i], p[i])
    return lo, hi


# 绕 Z 的方位角（度）。mmd_tools 导入后模型正面朝 -Y，所以 yaw=0 时相机在 -Y 侧。
# yaw 为正时相机绕 +Z 逆时针移动。--view 也可以直接给角度数值。
VIEWS = {
    "front": 0.0, "back": 180.0,
    "left": 90.0, "right": -90.0,
    "three_quarter": 35.0,
}


def setup_camera(mesh, view, margin):
    """正交相机框住模型。正交 + 固定框选让 Unity 侧可以精确复刻同一视图。"""
    lo, hi = world_bbox([mesh])
    center = (lo + hi) * 0.5
    height = hi.z - lo.z
    width = max(hi.x - lo.x, hi.y - lo.y)
    scale = max(height, width) * margin

    cam_data = bpy.data.cameras.new("RefCam")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = scale
    cam = bpy.data.objects.new("RefCam", cam_data)
    bpy.context.scene.collection.objects.link(cam)

    yaw = VIEWS[view] if view in VIEWS else float(view)
    dist = max(scale * 4.0, 10.0)
    yr = math.radians(yaw)

    # 相机默认看向 -Z。先绕 X 转 90° 让它看向 +Y，再绕 Z 转 -yaw，
    # 视线方向变为 (sin yaw, cos yaw, 0)；相机则放在中心的反方向 dist 处。
    cam.location = center + Vector((-math.sin(yr) * dist, -math.cos(yr) * dist, 0.0))
    cam.rotation_euler = (math.radians(90.0), 0.0, -yr)
    bpy.context.scene.camera = cam
    say("相机 view=%s (yaw=%.1f°) ortho_scale=%.4f 中心=(%.4f, %.4f, %.4f)" % (
        view, yaw, scale, center.x, center.y, center.z))
    return {"view": view, "yaw_deg": yaw, "ortho_scale": round(scale, 6),
            "center": [round(v, 6) for v in center],
            "location": [round(v, 6) for v in cam.location],
            "rotation_euler_deg": [90.0, 0.0, -yaw]}


def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def setup_render(args):
    sc = bpy.context.scene
    sc.render.engine = "BLENDER_EEVEE"
    sc.render.resolution_x = sc.render.resolution_y = args["res"]
    sc.render.resolution_percentage = 100
    sc.render.film_transparent = (args["bg"] == "transparent")
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.render.image_settings.color_depth = "16"  # 差分比对要避免 8bit 量化噪声
    sc.eevee.taa_render_samples = args["samples"]
    if args["bg"] != "transparent":
        w = bpy.data.worlds.new("RefWorld")
        w.use_nodes = False
        # --bg 收 sRGB 十六进制（如 3b3b3b）；世界色是线性的，要先反 sRGB
        h = args["bg"].lstrip("#")
        w.color = tuple(srgb_to_linear(int(h[i:i + 2], 16) / 255.0)
                        for i in (0, 2, 4))
        sc.world = w


def main():
    args = parse_args()
    pmx = os.path.abspath(args["pmx"])
    preset = os.path.abspath(args["preset"])
    say("Blender %s" % bpy.app.version_string)
    say("PMX    = " + pmx)
    say("预设   = " + preset)

    # 从空场景起步，避免 factory-startup 自带的方块/灯/相机干扰。
    # 必须在启用插件之前做——read_homefile 会重跑插件的 load_post 处理器。
    bpy.ops.wm.read_homefile(use_empty=True)

    plugin = load_plugin_module()
    enable_mmd_tools()

    mesh, arm = import_pmx(pmx)
    make_active(mesh)

    lib_mats = preset_material_names(preset)
    say("预设库含 %d 个材质" % len(lib_mats))

    expect_graph = None
    if args["expect"]:
        with open(os.path.abspath(args["expect"]), encoding="utf-8") as f:
            expect_graph = json.load(f)
        say("比对基准 = %s（%d 个材质）" % (
            os.path.basename(args["expect"]), len(expect_graph.get("materials", {}))))

    # ③ 等价于点击「导入预设」——直接调用插件自己的函数
    plugin.load_presets(preset, FACE_LOCATOR_NAME, HEAD_BONE_NAME,
                        MATCH_MODE_STRIP_SUFFIX, SET_SCENE_EFFECT)
    say("load_presets() 返回正常")

    if args["sun_euler"]:
        sun = bpy.data.objects.get("灯光")
        if not sun:
            raise SystemExit("预设未提供 `灯光` 物体，无法设置 --sun-euler")
        sun.rotation_euler = [math.radians(float(v))
                              for v in args["sun_euler"].split(",")]
        bpy.context.view_layer.update()
        say("已把 `灯光` 朝向设为 (%s)°" % args["sun_euler"])

    rep = inspect(mesh, arm, preset, lib_mats, expect_graph)
    rep["cam"] = setup_camera(mesh, args["view"], args["margin"])
    setup_render(args)

    if args["save_blend"]:
        p = os.path.abspath(args["save_blend"])
        os.makedirs(os.path.dirname(p), exist_ok=True)
        bpy.ops.wm.save_as_mainfile(filepath=p, copy=True)
        say("已保存场景 " + p)

    if args["out"]:
        out = os.path.abspath(args["out"])
        os.makedirs(os.path.dirname(out), exist_ok=True)
        bpy.context.scene.render.filepath = out
        bpy.ops.render.render(write_still=True)
        rep["render"] = {"path": out, "res": args["res"], "bg": args["bg"],
                         "samples": args["samples"]}
        say("已渲染 " + out)

    rep["blender"] = bpy.app.version_string
    rep["pmx"] = pmx
    rep["preset"] = preset
    rep["log"] = log
    if args["report"]:
        p = os.path.abspath(args["report"])
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            json.dump(rep, f, ensure_ascii=False, indent=2)
        say("已写报告 " + p)
    say("完成")


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:
        traceback.print_exc()
        print("[apply_preset] !! 失败")
        sys.stdout.flush()
        sys.exit(1)
