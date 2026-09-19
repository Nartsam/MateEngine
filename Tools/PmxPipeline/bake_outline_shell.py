"""把预设的几何节点描边外壳烘焙出来，导给 Unity。

计划见 Docs/plans/2026-09-10-blender-graph-transpile.md Phase 3。

描边是**实体化的反向外壳**，不是着色器 pass：几何节点按 1e-4 焊接顶点、重算法线、
沿法线外扩 `宽度 × 遮罩权重`、划分三个 `edge_*` 子材质、再 Flip Faces。

为什么不在 Unity 里重写这套算法：焊接的合并语义、Blender 的法线加权方式、遮罩在 UV
缝上的平均取值（见计划里的坑 7）、三个 `Set Material` 的顺序消耗语义——每一条都要
「逐位对上」才不出缝，重写等于把四处实现细节各赌一次。这里直接让 Blender 算，只负责
把结果搬走。

两次求值拿到两样东西：

  描边权重 = 0  ->  焊接后、未外扩的位置。用它把外壳顶点对回基础顶点（外壳不继承
                   顶点组，骨骼权重与 BlendShape 增量只能按焊接组从基础网格取平均）
  描边权重 = 1  ->  真正的外壳位置/法线/UV/三角形

坑（已实测）：`bpy.data.meshes.new_from_object()` 会**静默丢掉整个外壳**——外壳经
`Geometry to Instance` 变成了实例。必须先加一个只做 Realize Instances 的临时修改器。

用法：

    blender.exe -b --factory-startup -P Tools/PmxPipeline/bake_outline_shell.py -- \
        --pmx <角色>.pmx --preset 渲染预设.blend --out Build/RenderPreset/outline/丽塔
"""
import json
import os
import struct
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy            # noqa: E402
import mathutils      # noqa: E402

import blender_apply_preset as bap   # noqa: E402

# 焊接距离，与几何节点里的 Merge by Distance 一致。对回基础顶点时留一点余量。
MERGE_DISTANCE = 1e-4
MATCH_RADIUS = MERGE_DISTANCE * 1.5

WEIGHT_SOCKET = "Input_7"     # 修改器上的「描边权重」


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    a = {"pmx": None, "preset": None, "out": None}
    i = 0
    while i < len(argv):
        k = argv[i][2:].replace("-", "_") if argv[i].startswith("--") else None
        if k in a and i + 1 < len(argv):
            a[k] = argv[i + 1]
            i += 2
        else:
            raise SystemExit("无法识别的参数: " + argv[i])
    if not all(a.values()):
        raise SystemExit("需要 --pmx --preset --out")
    return a


def say(msg):
    print("[outline] " + msg)
    sys.stdout.flush()


def realize(obj):
    """求值并实体化实例，返回一份临时网格（用完记得 bpy.data.meshes.remove）。"""
    ng = bpy.data.node_groups.new("TmpRealize", "GeometryNodeTree")
    # Blender 3.6 用 node_group.inputs/outputs，4.x 才换成 interface
    ng.inputs.new("NodeSocketGeometry", "Geometry")
    ng.outputs.new("NodeSocketGeometry", "Geometry")
    gi = ng.nodes.new("NodeGroupInput")
    go = ng.nodes.new("NodeGroupOutput")
    rz = ng.nodes.new("GeometryNodeRealizeInstances")
    ng.links.new(gi.outputs[0], rz.inputs[0])
    ng.links.new(rz.outputs[0], go.inputs[0])
    m = obj.modifiers.new("TmpRealize", "NODES")
    m.node_group = ng
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(obj.evaluated_get(dg), depsgraph=dg)
    obj.modifiers.remove(m)
    bpy.data.node_groups.remove(ng)
    return me


def main():
    args = parse_args()
    pmx = os.path.abspath(args["pmx"])
    preset = os.path.abspath(args["preset"])
    out = os.path.abspath(args["out"])
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)

    say("Blender %s" % bpy.app.version_string)
    bpy.ops.wm.read_homefile(use_empty=True)
    plugin = bap.load_plugin_module()
    bap.enable_mmd_tools()
    mesh, arm = bap.import_pmx(pmx)
    bap.make_active(mesh)
    # set_scene_effect 传 False：这里只要几何，场景设置与渲染无关
    plugin.load_presets(preset, bap.FACE_LOCATOR_NAME, bap.HEAD_BONE_NAME,
                        bap.MATCH_MODE_STRIP_SUFFIX, False)

    nodes_mod = next((m for m in mesh.modifiers if m.type == "NODES"), None)
    if nodes_mod is None:
        raise SystemExit("模型上没有几何节点修改器——预设没套上？")

    base_n = len(mesh.data.vertices)
    say("基础网格 顶点 %d 面 %d" % (base_n, len(mesh.data.polygons)))

    prev_weight = nodes_mod[WEIGHT_SOCKET]
    nodes_mod[WEIGHT_SOCKET] = 0.0
    me_zero = realize(mesh)
    nodes_mod[WEIGHT_SOCKET] = prev_weight
    me_full = realize(mesh)

    if len(me_zero.vertices) != len(me_full.vertices):
        raise SystemExit("两次求值顶点数不一致：%d vs %d"
                         % (len(me_zero.vertices), len(me_full.vertices)))
    shell_n = len(me_full.vertices) - base_n
    if shell_n <= 0:
        raise SystemExit("求值结果里没有外壳——Realize Instances 没生效？")
    say("外壳 顶点 %d 面 %d（描边权重 %.3f）"
        % (shell_n, len(me_full.polygons) - len(mesh.data.polygons), prev_weight))

    # ---- 外扩量自检：应当沿法线、且不超过一个固定上限 ----
    offs = []
    for i in range(shell_n):
        d = (me_full.vertices[base_n + i].co - me_zero.vertices[base_n + i].co)
        offs.append(d.length)
    say("外扩距离 最小 %.6f 最大 %.6f 均值 %.6f；为 0 的顶点 %d 个"
        % (min(offs), max(offs), sum(offs) / len(offs),
           sum(1 for x in offs if x <= 1e-7)))

    # ---- 焊接组：把外壳顶点对回基础顶点 ----
    kd = mathutils.kdtree.KDTree(base_n)
    for i, v in enumerate(mesh.data.vertices):
        kd.insert(v.co, i)
    kd.balance()

    weld_offsets = [0]
    weld_indices = []
    empty = 0
    for i in range(shell_n):
        co = me_zero.vertices[base_n + i].co
        hits = [idx for (_, idx, dist) in kd.find_range(co, MATCH_RADIUS)]
        if not hits:
            # 兜底取最近的一个，保证每个外壳顶点都有权重来源
            _, idx, _ = kd.find(co)
            hits = [idx]
            empty += 1
        weld_indices.extend(hits)
        weld_offsets.append(len(weld_indices))
    say("焊接组：共 %d 条引用，平均每个外壳顶点 %.2f 个基础顶点%s"
        % (len(weld_indices), len(weld_indices) / shell_n,
           "；%d 个只能退回最近邻" % empty if empty else ""))

    # ---- 三角形按材质分组 ----
    uv_layer = me_full.uv_layers.active
    if uv_layer is None:
        raise SystemExit("外壳没有 UV 层")
    mats = [m.name if m else None for m in me_full.materials]

    by_mat = {}
    for p in me_full.polygons:
        if p.vertices[0] < base_n:
            continue                      # 基础网格那一半，跳过
        name = mats[p.material_index] if p.material_index < len(mats) else None
        if name is None or not name.startswith("edge"):
            continue
        loops = list(p.loop_indices)
        if len(loops) != 3:
            raise SystemExit("外壳里出现了非三角面（%d 边），几何节点应当只产出三角形"
                             % len(loops))
        by_mat.setdefault(name, []).append(loops)

    # UV 在 CORNER 域，同一顶点的不同角可能有不同 UV。外壳沿用基础网格的 UV 缝，
    # 这里按「首次出现」取值——与 Blender 渲染时逐角取值相比，只在缝上有差别，而缝
    # 两侧的描边颜色本来就来自同一张遮罩的邻近像素。
    uvs = [(0.0, 0.0)] * shell_n
    seen = [False] * shell_n
    for p in me_full.polygons:
        if p.vertices[0] < base_n:
            continue
        for li in p.loop_indices:
            vi = me_full.loops[li].vertex_index - base_n
            if 0 <= vi < shell_n and not seen[vi]:
                uv = uv_layer.data[li].uv
                uvs[vi] = (uv[0], uv[1])
                seen[vi] = True

    submeshes = []
    tris = []
    for name in sorted(by_mat):
        start = len(tris)
        for loops in by_mat[name]:
            for li in loops:
                tris.append(me_full.loops[li].vertex_index - base_n)
        submeshes.append({"material": name, "start": start,
                          "count": len(tris) - start})
        say("子网格 %-14s 三角形 %d" % (name, (len(tris) - start) // 3))

    # ---- 落盘 ----
    bin_path = out + ".outline.bin"
    with open(bin_path, "wb") as f:
        for i in range(shell_n):
            f.write(struct.pack("<3f", *me_full.vertices[base_n + i].co))
        for i in range(shell_n):
            f.write(struct.pack("<3f", *me_full.vertices[base_n + i].normal))
        for uv in uvs:
            f.write(struct.pack("<2f", *uv))
        f.write(struct.pack("<%di" % len(tris), *tris))
        f.write(struct.pack("<%di" % len(weld_offsets), *weld_offsets))
        f.write(struct.pack("<%di" % len(weld_indices), *weld_indices))

    meta = {
        "formatVersion": 1,
        "pmx": pmx,
        "preset": preset,
        "baseVertexCount": base_n,
        "vertexCount": shell_n,
        "indexCount": len(tris),
        "weldIndexCount": len(weld_indices),
        "submeshes": submeshes,
        "outlineWidth": max(offs),
        "bin": os.path.basename(bin_path),
        # 二进制按这个顺序紧排，全部小端；坐标是 Blender 空间（已含 mmd_tools 的 0.08 缩放）
        "layout": ["positions:float3[V]", "normals:float3[V]", "uv0:float2[V]",
                   "triangles:int32[I]", "weldOffsets:int32[V+1]",
                   "weldIndices:int32[W]"],
    }
    with open(out + ".outline.json", "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=1)
    say("已写 %s（%.1f KB）与 %s"
        % (os.path.basename(bin_path), os.path.getsize(bin_path) / 1024.0,
           os.path.basename(out + ".outline.json")))

    bpy.data.meshes.remove(me_zero)
    bpy.data.meshes.remove(me_full)


if __name__ == "__main__":
    main()
