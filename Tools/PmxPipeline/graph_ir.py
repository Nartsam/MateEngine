"""把 Blender 节点图 JSON 读成可转译的中间表示（IR）。

这是节点图转译路线的地基（Docs/plans/2026-09-10-blender-graph-transpile.md Phase 1），
输入是 export_blender_graph.py 导出的 `<角色>.graph.json`。

做三件事：

1. **按需求解**。从 `Material Output ▸ Surface` 反向走，只保留真正到达输出的节点。
   预设里有成片的死代码（`昼夜控制` 的夜间分支、`法线贴图转换`、非 hair 版 `matcap`
   等），按需求解天然把它们剪掉，不必单独维护一份死代码清单。
2. **展开节点组与 Reroute**。Blender 在组边界两侧用**同一个插槽标识符**
   （外部 `Input_9` ↔ 组内 `Group Input.Input_9`），所以内联是机械的。
3. **显式记录隐式类型转换**。Blender 把 Color 转 Float 用 Rec.709 亮度而非算术
   平均，可达连线里有二十多条这种转换，弄错会全线偏色。IR 里把它变成显式节点。

输出是一张扁平 DAG（`FlatNode` 列表 + 拓扑序），交给代码生成器。

命令行自检：

    python Tools/PmxPipeline/graph_ir.py Docs/RenderPreset/graphs/丽塔.graph.json
    python Tools/PmxPipeline/graph_ir.py <graph.json> --material 皮肤 --dump
"""
from __future__ import annotations

import argparse
import collections
import io
import json
import sys
from dataclasses import dataclass, field

# Blender 隐式把 Color 转 Float 时用的是 Rec.709 亮度，不是算术平均。
# 实测 RGB(0.1, 0.6, 0.9) → 0.515360（算术平均会是 0.533333）。
REC709 = (0.2126, 0.7152, 0.0722)

# 结构性节点：它们不参与运算，只负责连线与边界，展开时全部消解掉
STRUCTURAL = {"REROUTE", "GROUP", "GROUP_INPUT", "GROUP_OUTPUT", "FRAME"}

# Blender 用来表示「点这里加新插槽」的占位插槽，不是真插槽
EXTEND_SOCKET = "__extend__"


@dataclass
class Ref:
    """对某个扁平节点某个输出插槽的引用。"""
    node: str
    socket: str
    type: str


@dataclass
class Const:
    """常量输入（插槽上的 default_value，没有连线时使用）。"""
    value: object
    type: str


@dataclass
class FlatNode:
    id: str
    type: str
    bl_idname: str
    props: dict = field(default_factory=dict)
    inputs: dict = field(default_factory=dict)      # ident -> Ref | Const
    input_names: dict = field(default_factory=dict)  # ident -> 可读名，便于调试
    outputs: dict = field(default_factory=dict)      # ident -> 类型
    path: str = ""                                   # 组内来源路径，排错用


class GraphError(Exception):
    pass


def socket_list(node, key):
    return [s for s in node.get(key, []) if s.get("identifier") != EXTEND_SOCKET]


class Graph:
    def __init__(self, data):
        self.data = data
        self.materials = data.get("materials", {})
        self.node_groups = data.get("node_groups", {})

    @staticmethod
    def load(path):
        with io.open(path, encoding="utf-8") as f:
            return Graph(json.load(f))


class _Tree:
    """一棵树（材质树或节点组树）在某条展开路径上的上下文。"""

    def __init__(self, tree, path, bound_inputs):
        self.nodes = {n["name"]: n for n in tree["nodes"]}
        self.path = path
        # 组节点外部喂进来的东西：ident -> (Ref|Const)。材质树为空。
        self.bound_inputs = bound_inputs or {}

        # to_node -> to_socket -> link，静音的连线直接当不存在
        self.incoming = collections.defaultdict(dict)
        for l in tree["links"]:
            if l.get("mute"):
                continue
            self.incoming[l["to_node"]][l["to_socket"]] = l


class Expander:
    """按需展开：从材质输出反向走，产出扁平 DAG。"""

    def __init__(self, graph: Graph):
        self.graph = graph
        self.nodes: dict[str, FlatNode] = {}
        self.order: list[str] = []          # 拓扑序（后序遍历天然得到）
        self.warnings: list[str] = []
        self._emitting: set[str] = set()    # 环检测
        self._conv_seq = 0

    # ---- 对外入口 -------------------------------------------------------

    def expand_material(self, name):
        mat = self.graph.materials.get(name)
        if mat is None:
            raise GraphError(f"材质不存在：{name}")
        tree = _Tree(mat["tree"], path=f"mat:{name}", bound_inputs=None)

        out = self._find_output_node(tree)
        surface = self._resolve_input(tree, out, "Surface")
        # Surface 解析成常量是**合法**的：描边材质就是一整块平涂色（edge_hair 即如此，
        # 它的 `描边调色` 组是纯直通，喂进去的是组节点插槽上的默认值）。
        return surface

    # ---- 内部 -----------------------------------------------------------

    def _find_output_node(self, tree):
        outs = [n for n in tree.nodes.values() if n["type"] == "OUTPUT_MATERIAL"]
        if not outs:
            raise GraphError(f"{tree.path} 里没有 Material Output 节点")
        if len(outs) > 1:
            # Blender 里可以有多个输出节点，但只有 is_active_output 的那个生效。
            # 导出器目前没导这个标志，遇到就报警而不是瞎猜。
            self.warnings.append(
                f"{tree.path} 有 {len(outs)} 个 Material Output，取第一个；"
                f"若结果不对需在导出器里补 is_active_output")
        return outs[0]

    def _resolve_input(self, tree, node, ident):
        """求解 node 的某个输入插槽，返回 Ref 或 Const。"""
        link = tree.incoming.get(node["name"], {}).get(ident)
        if link is not None:
            src = self._resolve_output(tree, link["from_node"], link["from_socket"])
            if src is not None:
                return self._coerce(src, self._socket_type(node, "inputs", ident),
                                    f"{tree.path}/{node['name']}.{ident}")

        # 没有连线就用插槽自带的 default_value
        for s in socket_list(node, "inputs"):
            if s["identifier"] == ident:
                return Const(s.get("default"), s["type"])
        raise GraphError(f"{tree.path}/{node['name']} 没有输入插槽 {ident}")

    def _resolve_output(self, tree, node_name, socket):
        """求解某个节点的输出插槽，必要时递归进组。返回 Ref/Const/None。"""
        node = tree.nodes.get(node_name)
        if node is None:
            self.warnings.append(f"{tree.path} 引用了不存在的节点 {node_name}")
            return None

        t = node["type"]

        if t == "REROUTE":
            return self._resolve_reroute(tree, node)

        if t == "GROUP_INPUT":
            # 组边界：标识符两侧相同，直接取外部绑定
            if socket in tree.bound_inputs:
                return tree.bound_inputs[socket]
            for s in socket_list(node, "outputs"):
                if s["identifier"] == socket:
                    return Const(s.get("default"), s["type"])
            raise GraphError(f"{tree.path} 的 Group Input 没有插槽 {socket}")

        if t == "GROUP":
            return self._resolve_group(tree, node, socket)

        if node.get("mute"):
            # Blender 对静音节点做「首个同类输入直通到输出」。这个语义要逐节点类型
            # 确认，与其猜错不如报警——实测四个预设里没有静音节点。
            self.warnings.append(
                f"{tree.path}/{node['name']} 是静音节点，尚未实现直通语义")

        self._emit(tree, node)
        flat_id = self._flat_id(tree, node)
        return Ref(flat_id, socket, self._socket_type(node, "outputs", socket))

    def _resolve_reroute(self, tree, node):
        link = tree.incoming.get(node["name"], {}).get("Input")
        if link is None:
            for s in socket_list(node, "inputs"):
                return Const(s.get("default"), s["type"])
            return None
        return self._resolve_output(tree, link["from_node"], link["from_socket"])

    def _resolve_group(self, tree, node, socket):
        gname = node.get("group")
        sub = self.graph.node_groups.get(gname)
        if sub is None:
            raise GraphError(f"{tree.path}/{node['name']} 引用的节点组不存在：{gname}")

        # 先把组节点每个输入插槽在**外层**求解好，再带进组里
        bound = {}
        for s in socket_list(node, "inputs"):
            bound[s["identifier"]] = self._resolve_input(tree, node, s["identifier"])

        sub_path = f"{tree.path}/{node['name']}[{gname}]"
        sub_tree = _Tree(sub, sub_path, bound)

        gouts = [n for n in sub_tree.nodes.values() if n["type"] == "GROUP_OUTPUT"]
        if not gouts:
            raise GraphError(f"节点组 {gname} 里没有 Group Output")
        gout = gouts[0]
        return self._resolve_input(sub_tree, gout, socket)

    def _emit(self, tree, node):
        flat_id = self._flat_id(tree, node)
        if flat_id in self.nodes:
            return
        if flat_id in self._emitting:
            raise GraphError(f"节点图存在环：{flat_id}")
        self._emitting.add(flat_id)

        fn = FlatNode(
            id=flat_id,
            type=node["type"],
            bl_idname=node.get("bl_idname", ""),
            props={k: v for k, v in node.items()
                   if k not in ("name", "label", "type", "bl_idname", "mute",
                                "location", "parent", "inputs", "outputs")},
            outputs={s["identifier"]: s["type"] for s in socket_list(node, "outputs")},
            path=tree.path,
        )
        # 先登记再解输入，这样自引用会被上面的环检测抓到
        self.nodes[flat_id] = fn

        for s in socket_list(node, "inputs"):
            ident = s["identifier"]
            fn.inputs[ident] = self._resolve_input(tree, node, ident)
            fn.input_names[ident] = s["name"]

        self._emitting.discard(flat_id)
        self.order.append(flat_id)   # 输入先入序，天然是拓扑序

    def _coerce(self, src, want, where):
        """插入显式的类型转换节点。Blender 的隐式转换有坑，必须显式化。"""
        if isinstance(src, Const) or src.type == want:
            return src
        pair = (src.type, want)
        if want == "SHADER" and src.type in ("RGBA", "VALUE", "VECTOR"):
            # 整条路线的立足点：Color 直连 Material Output ▸ Surface。Blender 对此
            # 等价于一个 Emission——没有光照参与，材质就是一个纯函数。
            # 见 ADR-0011 / 记忆 blend-preset-is-unlit-pure-function。
            return self._conv(src, want, "UNLIT_EMISSION", where)
        if pair == ("RGBA", "VALUE"):
            return self._conv(src, want, "RGBA_TO_VALUE", where)
        if pair == ("VALUE", "RGBA"):
            return self._conv(src, want, "VALUE_TO_RGBA", where)
        if pair == ("VALUE", "VECTOR"):
            return self._conv(src, want, "VALUE_TO_VECTOR", where)
        if pair == ("VECTOR", "VALUE"):
            # Blender 对 Vector→Float 取三分量算术平均（与 Color 不同，无亮度权重）
            return self._conv(src, want, "VECTOR_TO_VALUE", where)
        if pair in (("RGBA", "VECTOR"), ("VECTOR", "RGBA")):
            return self._conv(src, want, "XYZ_PASSTHROUGH", where)
        self.warnings.append(f"{where}: 未处理的类型转换 {src.type} → {want}")
        return src

    def _conv(self, src, want, kind, where):
        self._conv_seq += 1
        flat_id = f"__conv{self._conv_seq}[{kind}]"
        fn = FlatNode(
            id=flat_id, type="IMPLICIT_CONV", bl_idname="",
            props={"kind": kind, "weights": REC709 if kind == "RGBA_TO_VALUE" else None,
                   "at": where},
            inputs={"In": src}, input_names={"In": "In"},
            outputs={"Out": want}, path=where,
        )
        self.nodes[flat_id] = fn
        self.order.append(flat_id)
        return Ref(flat_id, "Out", want)

    @staticmethod
    def _flat_id(tree, node):
        return f"{tree.path}/{node['name']}"

    @staticmethod
    def _socket_type(node, key, ident):
        for s in socket_list(node, key):
            if s["identifier"] == ident:
                return s["type"]
        raise GraphError(f"{node['name']} 没有 {key} 插槽 {ident}")


def expand(graph, material):
    ex = Expander(graph)
    root = ex.expand_material(material)
    return ex, root


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("graph", help="<角色>.graph.json")
    ap.add_argument("--material", help="只看某个材质；不给则逐个统计")
    ap.add_argument("--dump", action="store_true", help="打印扁平 DAG")
    args = ap.parse_args()

    g = Graph.load(args.graph)
    names = [args.material] if args.material else sorted(g.materials)

    total_fail = 0
    rows = []
    for name in names:
        try:
            ex, root = expand(g, name)
        except GraphError as e:
            total_fail += 1
            print(f"[ir] {name:<8} 展开失败：{e}")
            continue

        kinds = collections.Counter(n.type for n in ex.nodes.values())
        rows.append((name, len(ex.nodes), len(ex.warnings), kinds))
        out_desc = (f"{root.node}.{root.socket}" if isinstance(root, Ref)
                    else f"常量 {str(root.value)[:32]}（平涂材质）")
        print(f"[ir] {name:<8} 扁平节点 {len(ex.nodes):>4}  "
              f"告警 {len(ex.warnings):>2}  输出 {out_desc}")
        for w in ex.warnings:
            print(f"        !! {w}")

        if args.dump:
            for nid in ex.order:
                n = ex.nodes[nid]
                print(f"        {n.type:<16} {nid}")
                for ident, src in n.inputs.items():
                    desc = (f"<- {src.node}.{src.socket}" if isinstance(src, Ref)
                            else f"=  {str(src.value)[:40]}")
                    print(f"            {n.input_names.get(ident, ident):<14} {desc}")

    if rows:
        print()
        print(f"[ir] 共 {len(rows)} 个材质展开成功，{total_fail} 个失败")
        allkinds = collections.Counter()
        for _, _, _, k in rows:
            allkinds.update(k)
        print("[ir] 需要实现的节点类型（按出现次数）：")
        for k, v in allkinds.most_common():
            print(f"        {k:<22} {v}")
    return 1 if total_fail else 0


if __name__ == "__main__":
    sys.exit(main())
