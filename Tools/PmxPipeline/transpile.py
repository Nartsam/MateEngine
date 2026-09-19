"""把 Blender 节点图转译成 Unity 着色器 + 材质装配清单。

计划见 Docs/plans/2026-09-10-blender-graph-transpile.md Phase 1。上游是
`graph_ir.py` 产出的扁平 DAG，下游是 Unity 侧的 `METranspiledMaterials.cs`。

产出三样东西：

    <outdir>/shaders/M??.shader     每个材质一个着色器（自带节点库，无 include）
    <outdir>/<角色>.bindings.json   材质 -> 着色器 / 贴图 / 混合状态 / LUT 数据
    （LUT 数据内嵌在 bindings.json 里，由 Unity 侧建成一张 RGBAFloat 贴图）

为什么分工在这里切一刀：图分析与代码生成迭代频繁，Python 改完立刻能跑；放 Unity
里每次都要等编译。Unity 侧的职责收窄成「按清单装配」。

LUT 走「JSON 里带浮点数、运行时建贴图」而不是烤 PNG：表值在**线性域**，8 位量化
在 Filmic 曲线最陡的暗部会放大成可见色差，而 16 位 PNG 又要额外约定导入设置。

用法：

    Build/.venv/Scripts/python.exe Tools/PmxPipeline/transpile.py \
        --graph Docs/RenderPreset/graphs/丽塔.graph.json \
        --images Build/RenderPreset/images/丽塔 \
        --view-lut Docs/RenderPreset/view_transform_filmic_high_contrast.csv \
        --out Assets/Generated/PmxTranspiled/丽塔
"""
from __future__ import annotations

import argparse
import collections
import io
import json
import os
import re
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from graph_ir import Graph, expand, Ref, Const, GraphError   # noqa: E402

SHADER_PREFIX = "Hidden/MateEngine/Transpiled"

HLSL_TYPE = {"VALUE": "float", "VECTOR": "float3", "RGBA": "float4",
             "SHADER": "MEClosure", "INT": "float", "BOOLEAN": "float"}

# 几何节点修改器写进网格的三个命名属性，在 Unity 侧就是三个全局 uniform。
# 实测全网格恒定（26366 个顶点同值），所以不必真的走顶点流。
ATTR_UNIFORM = {"SUN": "_MESunDir", "FRONT": "_MEHeadFront", "RIGHT": "_MEHeadRight"}

MIX_FN = {
    "MIX": "ME_MixBlend", "ADD": "ME_MixAdd", "MULTIPLY": "ME_MixMult",
    "SUBTRACT": "ME_MixSub", "SCREEN": "ME_MixScreen", "DIVIDE": "ME_MixDiv",
    "DIFFERENCE": "ME_MixDiff", "DARKEN": "ME_MixDark", "LIGHTEN": "ME_MixLight",
}

MATH_OPS = {
    "ADD": "({a} + {b})", "SUBTRACT": "({a} - {b})", "MULTIPLY": "({a} * {b})",
    "DIVIDE": "ME_SafeDivide({a}, {b})", "MULTIPLY_ADD": "({a} * {b} + {c})",
    "POWER": "ME_Power({a}, {b})",
    "GREATER_THAN": "(({a} > {b}) ? 1.0 : 0.0)",
    "LESS_THAN": "(({a} < {b}) ? 1.0 : 0.0)",
    "COMPARE": "ME_Compare({a}, {b}, {c})",
    "MINIMUM": "min({a}, {b})", "MAXIMUM": "max({a}, {b})",
    "ABSOLUTE": "abs({a})", "ROUND": "floor({a} + 0.5)", "FLOOR": "floor({a})",
    "CEIL": "ceil({a})", "FRACT": "frac({a})", "SIGN": "sign({a})",
    "SQRT": "sqrt(max({a}, 0.0))", "INVERSE_SQRT": "rsqrt(max({a}, 1e-8))",
    "SINE": "sin({a})", "COSINE": "cos({a})", "TANGENT": "tan({a})",
    "EXPONENT": "exp({a})", "SMOOTH_MIN": "min({a}, {b})", "SMOOTH_MAX": "max({a}, {b})",
}

VEC_OPS = {
    "ADD": ("Vector", "VECTOR", "({a} + {b})"),
    "SUBTRACT": ("Vector", "VECTOR", "({a} - {b})"),
    "MULTIPLY": ("Vector", "VECTOR", "({a} * {b})"),
    "DIVIDE": ("Vector", "VECTOR", "({a} / max(abs({b}), 1e-8) * sign({b}))"),
    "SCALE": ("Vector", "VECTOR", "({a} * {s})"),
    "NORMALIZE": ("Vector", "VECTOR", "ME_SafeNormalize({a})"),
    "CROSS_PRODUCT": ("Vector", "VECTOR", "cross({a}, {b})"),
    "ABSOLUTE": ("Vector", "VECTOR", "abs({a})"),
    "MINIMUM": ("Vector", "VECTOR", "min({a}, {b})"),
    "MAXIMUM": ("Vector", "VECTOR", "max({a}, {b})"),
    "DOT_PRODUCT": ("Value", "VALUE", "dot({a}, {b})"),
    "LENGTH": ("Value", "VALUE", "length({a})"),
    "DISTANCE": ("Value", "VALUE", "distance({a}, {b})"),
}


class Unsupported(Exception):
    pass


def fnum(x):
    """浮点字面量：精度给足，且一定带小数点（HLSL 里 1 是 int，参与除法会截断）。"""
    if isinstance(x, bool):
        return "1.0" if x else "0.0"
    s = "%.9g" % float(x)
    if not re.search(r"[.eE]", s):
        s += ".0"
    return s


def literal(value, socket_type):
    t = socket_type
    if t == "VALUE" or t == "INT" or t == "BOOLEAN":
        if isinstance(value, (list, tuple)):
            value = value[0] if value else 0.0
        return fnum(value if value is not None else 0.0)
    if t == "VECTOR":
        v = list(value or [0.0, 0.0, 0.0])[:3]
        while len(v) < 3:
            v.append(0.0)
        return "float3(%s)" % ", ".join(fnum(x) for x in v)
    if t == "RGBA":
        v = list(value or [0.0, 0.0, 0.0, 1.0])[:4]
        while len(v) < 4:
            v.append(1.0)
        return "float4(%s)" % ", ".join(fnum(x) for x in v)
    raise Unsupported("无法给类型 %s 生成字面量（值 %r）" % (t, value))


# ---------------------------------------------------------------------------


class Context:
    """跨材质共享的东西：LUT 行分配、贴图清单、告警收集。"""

    def __init__(self, graph, images, view_lut):
        self.graph = graph
        self.luts = graph.data.get("luts", {})
        self.images = {m["name"]: m for m in images.get("images", [])}
        # 第 0 行固定是视图变换，各材质末端都要查它
        self.rows = [{"name": "viewTransform", "kind": "view"}]
        self.row_data = [view_lut["table"]]
        self.row_of = {}
        self.view_lo = view_lut["lo"]
        self.view_hi = view_lut["hi"]
        self.warnings = []

    def row_for(self, table_key):
        if table_key in self.row_of:
            return self.row_of[table_key]
        entry = self.luts.get(table_key)
        if entry is None:
            raise Unsupported("节点图里没有 LUT 表 %s（导出器需为 formatVersion >= 3）"
                              % table_key)
        row = len(self.rows)
        self.rows.append({"name": table_key, "kind": entry["kind"]})
        self.row_data.append(entry["table"])
        self.row_of[table_key] = row
        return row

    def image(self, name):
        m = self.images.get(name)
        if m is None:
            raise Unsupported("贴图清单里没有 %r（先跑 export_blender_graph.py --images）"
                              % name)
        return m


class MaterialEmitter:
    def __init__(self, ctx: Context, name, ascii_id):
        self.ctx = ctx
        self.name = name
        self.ascii_id = ascii_id
        self.lines = []
        self.exprs = {}         # (node_id, socket) -> (hlsl 表达式, 类型)
        self.textures = []      # [{prop, image, file, srgb, interpolation, wrap}]
        self.tex_by_key = {}
        self.counter = 0
        self.notes = []

    # ---- 基础设施 --------------------------------------------------------

    def tmp(self, hint=""):
        self.counter += 1
        h = re.sub(r"[^A-Za-z0-9]", "", hint)[:12]
        return "v%d%s" % (self.counter, ("_" + h) if h else "")

    def decl(self, socket_type, expr, hint=""):
        v = self.tmp(hint)
        self.lines.append("    %s %s = %s;" % (HLSL_TYPE[socket_type], v, expr))
        return v

    def put(self, node, socket, expr, socket_type):
        self.exprs[(node.id, socket)] = (expr, socket_type)

    def val(self, node, ident, want=None):
        """取某个输入插槽的 HLSL 表达式。"""
        src = node.inputs.get(ident)
        if src is None:
            raise Unsupported("%s 缺输入插槽 %s" % (node.type, ident))
        if isinstance(src, Const):
            return literal(src.value, want or src.type)
        e = self.exprs.get((src.node, src.socket))
        if e is None:
            raise Unsupported("%s.%s 的上游 %s.%s 还没生成"
                              % (node.type, ident, src.node, src.socket))
        return e[0]

    def texture(self, image_name, interpolation, extension):
        key = (image_name, interpolation)
        if key in self.tex_by_key:
            return self.tex_by_key[key]
        m = self.ctx.image(image_name)
        prop = "_METex%d" % len(self.textures)
        self.textures.append({
            "prop": prop, "image": image_name, "file": m["file"],
            # Blender 的 Image Texture 对 sRGB 图返回 scene-linear，对 Non-Color
            # 返回原始值。Unity 侧靠建贴图时的 linear 标志复现这一点。
            "srgb": (m.get("colorspace") == "sRGB"),
            "interpolation": interpolation,
            "wrap": extension.lower(),
        })
        self.tex_by_key[key] = prop
        return prop

    # ---- 节点求值 --------------------------------------------------------

    def emit(self, node, needed):
        fn = getattr(self, "_n_" + node.type.lower(), None)
        if fn is None:
            raise Unsupported("未实现的节点类型 %s" % node.type)
        fn(node, needed)

    def _n_implicit_conv(self, n, needed):
        kind = n.props["kind"]
        src = n.inputs["In"]
        stype = src.type if isinstance(src, Ref) else src.type
        a = self.val(n, "In", stype)
        if kind == "RGBA_TO_VALUE":
            self.put(n, "Out", "ME_RgbaToValue(%s)" % a, "VALUE")
        elif kind == "VALUE_TO_RGBA":
            v = self.decl("VALUE", a, "f")
            self.put(n, "Out", "float4(%s, %s, %s, 1.0)" % (v, v, v), "RGBA")
        elif kind == "VALUE_TO_VECTOR":
            v = self.decl("VALUE", a, "f")
            self.put(n, "Out", "float3(%s, %s, %s)" % (v, v, v), "VECTOR")
        elif kind == "VECTOR_TO_VALUE":
            self.put(n, "Out", "ME_VectorToValue(%s)" % a, "VALUE")
        elif kind == "XYZ_PASSTHROUGH":
            want = n.outputs["Out"]
            if want == "VECTOR":
                self.put(n, "Out", "(%s).rgb" % a, "VECTOR")
            else:
                self.put(n, "Out", "float4(%s, 1.0)" % a, "RGBA")
        elif kind == "UNLIT_EMISSION":
            if stype == "RGBA":
                c = a
            elif stype == "VALUE":
                c = "float4(%s, %s, %s, 1.0)" % (a, a, a)
            else:
                c = "float4(%s, 1.0)" % a
            self.put(n, "Out", "ME_Emission(%s)" % c, "SHADER")
        else:
            raise Unsupported("未实现的隐式转换 %s" % kind)

    def _n_attribute(self, n, needed):
        name = n.props.get("attribute_name")
        u = ATTR_UNIFORM.get(name)
        if u is None:
            # Blender 对缺失属性返回 (0,0,0)，会让所有点积退化为 0，没有可用兜底，
            # 所以这里当错误处理而不是静默取零。
            raise Unsupported("未知的几何属性 %r" % name)
        self.put(n, "Vector", u, "VECTOR")
        self.put(n, "Color", "float4(%s, 1.0)" % u, "RGBA")
        self.put(n, "Fac", "ME_VectorToValue(%s)" % u, "VALUE")
        self.put(n, "Alpha", "1.0", "VALUE")

    def _n_new_geometry(self, n, needed):
        self.put(n, "Position", "i.wpos", "VECTOR")
        self.put(n, "Normal", "N", "VECTOR")
        self.put(n, "True Normal", "trueN", "VECTOR")
        self.put(n, "Incoming", "I", "VECTOR")
        self.put(n, "Backfacing", "backfacing", "VALUE")
        self.put(n, "Parametric", "float3(0.0, 0.0, 0.0)", "VECTOR")
        self.put(n, "Tangent", "normalize(i.wtangent.xyz)", "VECTOR")
        self.put(n, "Pointiness", "0.5", "VALUE")
        self.put(n, "Random Per Island", "0.0", "VALUE")
        for s in needed:
            if s in ("Parametric", "Pointiness", "Random Per Island"):
                self.notes.append("Geometry ▸ %s 用了占位值" % s)

    def _n_tex_coord(self, n, needed):
        self.put(n, "UV", "float3(i.uv0, 0.0)", "VECTOR")
        self.put(n, "Normal", "objNormal", "VECTOR")
        self.put(n, "Object", "objPos", "VECTOR")
        self.put(n, "Window", "float3(i.screen.xy / max(i.screen.w, 1e-6), 0.0)", "VECTOR")
        self.put(n, "Camera", "mul(UNITY_MATRIX_V, float4(i.wpos, 1.0)).xyz", "VECTOR")
        self.put(n, "Reflection", "reflect(-I, N)", "VECTOR")
        # Generated 是物体包围盒归一化坐标，需要包围盒 uniform，Phase 2 再接
        self.put(n, "Generated", "objPos", "VECTOR")
        if "Generated" in needed:
            self.notes.append("Texture Coordinate ▸ Generated 暂用物体坐标代替"
                              "（缺包围盒归一化）")

    def _n_uvmap(self, n, needed):
        uv = n.props.get("uv_map") or ""
        if uv not in ("", "UVMap"):
            raise Unsupported("UV Map 节点指定了 %r，模型只有 UVMap" % uv)
        self.put(n, "UV", "float3(i.uv0, 0.0)", "VECTOR")

    def _n_tex_image(self, n, needed):
        img = n.props.get("image") or {}
        name = img.get("name")
        if not name:
            raise Unsupported("Image Texture 没有图片")
        proj = n.props.get("projection") or "FLAT"
        if proj != "FLAT":
            raise Unsupported("Image Texture projection=%s" % proj)
        interp = n.props.get("interpolation") or "Linear"
        prop = self.texture(name, interp, n.props.get("extension") or "REPEAT")
        if "Vector" in n.unlinked:
            # Vector 未接线时 Blender 取活动 UV，不是插槽上那个 (0,0,0)
            uv = "i.uv0"
        else:
            uv = "(%s).xy" % self.val(n, "Vector", "VECTOR")
        if interp in ("Smart", "Cubic"):
            samp = "ME_TexBicubic(%s, %s_TexelSize, %s)" % (prop, prop, uv)
        elif interp == "Closest":
            samp = "tex2D(%s, %s)" % (prop, uv)
            self.notes.append("%s 的 interpolation=Closest 按双线性采样了" % name)
        else:
            samp = "tex2D(%s, %s)" % (prop, uv)
        v = self.decl("RGBA", samp, "tex")
        self.put(n, "Color", v, "RGBA")
        self.put(n, "Alpha", "%s.a" % v, "VALUE")

    def _n_math(self, n, needed):
        op = n.props.get("operation")
        tpl = MATH_OPS.get(op)
        if tpl is None:
            raise Unsupported("Math operation=%s" % op)
        e = tpl.format(a=self.val(n, "Value", "VALUE"),
                       b=self.val(n, "Value_001", "VALUE"),
                       c=self.val(n, "Value_002", "VALUE"))
        if n.props.get("use_clamp"):
            e = "saturate(%s)" % e
        self.put(n, "Value", self.decl("VALUE", e, op.lower()), "VALUE")

    def _n_vect_math(self, n, needed):
        op = n.props.get("operation")
        spec = VEC_OPS.get(op)
        if spec is None:
            raise Unsupported("Vector Math operation=%s" % op)
        socket, stype, tpl = spec
        e = tpl.format(a=self.val(n, "Vector", "VECTOR"),
                       b=self.val(n, "Vector_001", "VECTOR"),
                       c=self.val(n, "Vector_002", "VECTOR"),
                       s=self.val(n, "Scale", "VALUE"))
        self.put(n, socket, self.decl(stype, e, op.lower()), stype)

    def _n_map_range(self, n, needed):
        if n.props.get("data_type") != "FLOAT":
            raise Unsupported("Map Range data_type=%s" % n.props.get("data_type"))
        it = n.props.get("interpolation_type") or "LINEAR"
        v = self.val(n, "Value", "VALUE")
        fmin = self.val(n, "From Min", "VALUE")
        fmax = self.val(n, "From Max", "VALUE")
        tmin = self.val(n, "To Min", "VALUE")
        tmax = self.val(n, "To Max", "VALUE")
        if it == "LINEAR":
            e = "ME_MapRangeLinear(%s, %s, %s, %s, %s)" % (v, fmin, fmax, tmin, tmax)
        elif it == "STEPPED":
            e = "ME_MapRangeStepped(%s, %s, %s, %s, %s, %s)" % (
                v, fmin, fmax, tmin, tmax, self.val(n, "Steps", "VALUE"))
        elif it == "SMOOTHSTEP":
            e = "ME_MapRangeSmooth(%s, %s, %s, %s, %s)" % (v, fmin, fmax, tmin, tmax)
        elif it == "SMOOTHERSTEP":
            e = "ME_MapRangeSmoother(%s, %s, %s, %s, %s)" % (v, fmin, fmax, tmin, tmax)
        else:
            raise Unsupported("Map Range interpolation_type=%s" % it)
        # SMOOTHSTEP / SMOOTHERSTEP 自带钳制，Blender 不再额外套 clamp_range
        if n.props.get("clamp") and it in ("LINEAR", "STEPPED"):
            e = "ME_ClampRange(%s, %s, %s)" % (e, tmin, tmax)
        self.put(n, "Result", self.decl("VALUE", e, "maprange"), "VALUE")

    def _n_mix(self, n, needed):
        if n.props.get("data_type") != "RGBA":
            raise Unsupported("Mix data_type=%s" % n.props.get("data_type"))
        bt = n.props.get("blend_type") or "MIX"
        fn = MIX_FN.get(bt)
        if fn is None:
            raise Unsupported("Mix blend_type=%s" % bt)
        fac = self.val(n, "Factor_Float", "VALUE")
        if n.props.get("clamp_factor"):
            fac = "saturate(%s)" % fac
        e = "%s(%s, %s, %s)" % (fn, fac, self.val(n, "A_Color", "RGBA"),
                                self.val(n, "B_Color", "RGBA"))
        if n.props.get("clamp_result"):
            e = "saturate(%s)" % e
        self.put(n, "Result_Color", self.decl("RGBA", e, "mix"), "RGBA")

    def _n_mix_rgb(self, n, needed):
        bt = n.props.get("blend_type") or "MIX"
        fn = MIX_FN.get(bt)
        if fn is None:
            raise Unsupported("MixRGB blend_type=%s" % bt)
        if n.props.get("use_alpha"):
            self.notes.append("MixRGB 的 use_alpha 未实现")
        # 旧版 MixRGB 无条件钳制 Fac
        e = "%s(saturate(%s), %s, %s)" % (fn, self.val(n, "Fac", "VALUE"),
                                          self.val(n, "Color1", "RGBA"),
                                          self.val(n, "Color2", "RGBA"))
        if n.props.get("use_clamp"):
            e = "saturate(%s)" % e
        self.put(n, "Color", self.decl("RGBA", e, "mixrgb"), "RGBA")

    def _n_valtorgb(self, n, needed):
        ramp = n.props.get("color_ramp")
        if not ramp:
            raise Unsupported("Color Ramp 缺 color_ramp 数据")
        fac = self.val(n, "Fac", "VALUE")
        els = ramp["elements"]
        if (len(els) == 2 and ramp["color_mode"] == "RGB"
                and ramp["interpolation"] == "CONSTANT"):
            # 2 停的 CONSTANT ramp 内联即精确，且**不能**走 LUT：硬阈值的位置会被
            # 量化到 1/256 网格上再被双线性抹平。阈值取两个停位的较大者，与
            # BKE_colorband_evaluate 的 CONSTANT 分支（取下方停位的颜色）一致。
            edge = max(els[0]["position"], els[1]["position"])
            lo, hi = (els[0], els[1]) if els[0]["position"] <= els[1]["position"] \
                else (els[1], els[0])
            e = "((%s > %s) ? %s : %s)" % (fac, fnum(edge),
                                           literal(hi["color"], "RGBA"),
                                           literal(lo["color"], "RGBA"))
        else:
            row = self.ctx.row_for(ramp["table_key"])
            e = "ME_SampleBand(_MELut, %s, ME_LUT_ROWS, %s)" % (fnum(row), fac)
        v = self.decl("RGBA", e, "ramp")
        self.put(n, "Color", v, "RGBA")
        self.put(n, "Alpha", "%s.a" % v, "VALUE")

    def _n_curve_rgb(self, n, needed):
        curve = n.props.get("curve")
        if not curve:
            raise Unsupported("RGB Curves 缺 curve 数据")
        row = self.ctx.row_for(curve["table_key"])
        col = self.decl("RGBA", self.val(n, "Color", "RGBA"), "curvein")
        r = "ME_SampleBand(_MELut, %s, ME_LUT_ROWS, %s.r).r" % (fnum(row), col)
        g = "ME_SampleBand(_MELut, %s, ME_LUT_ROWS, %s.g).g" % (fnum(row), col)
        b = "ME_SampleBand(_MELut, %s, ME_LUT_ROWS, %s.b).b" % (fnum(row), col)
        # 表里每个通道存的是「先本通道曲线、再合成曲线」的复合结果，alpha 原样透传
        e = "lerp(%s, float4(%s, %s, %s, %s.a), %s)" % (
            col, r, g, b, col, self.val(n, "Fac", "VALUE"))
        self.put(n, "Color", self.decl("RGBA", e, "curve"), "RGBA")

    def _n_hue_sat(self, n, needed):
        e = "ME_HueSat(%s, %s, %s, %s, %s)" % (
            self.val(n, "Hue", "VALUE"), self.val(n, "Saturation", "VALUE"),
            self.val(n, "Value", "VALUE"), self.val(n, "Fac", "VALUE"),
            self.val(n, "Color", "RGBA"))
        self.put(n, "Color", self.decl("RGBA", e, "hsv"), "RGBA")

    def _n_separate_color(self, n, needed):
        mode = n.props.get("mode") or "RGB"
        c = self.decl("RGBA", self.val(n, "Color", "RGBA"), "sep")
        if mode == "RGB":
            self.put(n, "Red", "%s.r" % c, "VALUE")
            self.put(n, "Green", "%s.g" % c, "VALUE")
            self.put(n, "Blue", "%s.b" % c, "VALUE")
        elif mode == "HSV":
            h = self.decl("RGBA", "ME_RgbToHsv(%s)" % c, "hsv")
            self.put(n, "Red", "%s.x" % h, "VALUE")
            self.put(n, "Green", "%s.y" % h, "VALUE")
            self.put(n, "Blue", "%s.z" % h, "VALUE")
        else:
            raise Unsupported("Separate Color mode=%s" % mode)
        self.put(n, "Alpha", "%s.a" % c, "VALUE")

    def _n_combxyz(self, n, needed):
        e = "float3(%s, %s, %s)" % (self.val(n, "X", "VALUE"),
                                    self.val(n, "Y", "VALUE"),
                                    self.val(n, "Z", "VALUE"))
        self.put(n, "Vector", self.decl("VECTOR", e, "comb"), "VECTOR")

    def _n_sepxyz(self, n, needed):
        v = self.decl("VECTOR", self.val(n, "Vector", "VECTOR"), "sepxyz")
        self.put(n, "X", "%s.x" % v, "VALUE")
        self.put(n, "Y", "%s.y" % v, "VALUE")
        self.put(n, "Z", "%s.z" % v, "VALUE")

    def _n_mapping(self, n, needed):
        vt = n.props.get("vector_type") or "POINT"
        vec = self.val(n, "Vector", "VECTOR")
        loc = self.val(n, "Location", "VECTOR")
        rot = self.val(n, "Rotation", "VECTOR")
        scl = self.val(n, "Scale", "VECTOR")
        if vt == "POINT":
            e = "ME_MappingPoint(%s, %s, %s, %s)" % (vec, loc, rot, scl)
        elif vt == "TEXTURE":
            e = "ME_MappingTexture(%s, %s, %s, %s)" % (vec, loc, rot, scl)
        elif vt == "VECTOR":
            e = "ME_MappingVector(%s, %s, %s)" % (vec, rot, scl)
        elif vt == "NORMAL":
            e = "ME_MappingNormal(%s, %s, %s)" % (vec, rot, scl)
        else:
            raise Unsupported("Mapping vector_type=%s" % vt)
        self.put(n, "Vector", self.decl("VECTOR", e, "map"), "VECTOR")

    def _n_normal_map(self, n, needed):
        space = n.props.get("space")
        if space != "TANGENT":
            raise Unsupported("Normal Map space=%s" % space)
        uv = n.props.get("uv_map") or ""
        if uv not in ("", "UVMap"):
            raise Unsupported("Normal Map 指定了 UV %r" % uv)
        col = self.val(n, "Color", "RGBA")
        e = "ME_NormalMapTangent(i.wtangent, N, 2.0 * (%s).rgb - 1.0, %s)" % (
            col, self.val(n, "Strength", "VALUE"))
        self.put(n, "Normal", self.decl("VECTOR", e, "nmap"), "VECTOR")

    def _n_vect_transform(self, n, needed):
        vt = n.props.get("vector_type") or "VECTOR"
        cf = n.props.get("convert_from")
        ct = n.props.get("convert_to")
        v = self.val(n, "Vector", "VECTOR")
        if (cf, ct) != ("OBJECT", "CAMERA"):
            raise Unsupported("Vector Transform %s -> %s 未实现" % (cf, ct))
        if vt == "NORMAL":
            e = "ME_SafeNormalize(ME_WorldToBlenderCamera(UnityObjectToWorldNormal(%s)))" % v
        elif vt == "VECTOR":
            e = "ME_WorldToBlenderCamera(UnityObjectToWorldDir(%s))" % v
        else:   # POINT
            e = ("ME_WorldToBlenderCamera(mul(unity_ObjectToWorld, float4(%s, 1.0)).xyz"
                 " - _WorldSpaceCameraPos)" % v)
        self.put(n, "Vector", self.decl("VECTOR", e, "vtrans"), "VECTOR")

    def _n_tex_noise(self, n, needed):
        # TODO(Phase 2)：Blender 的 Perlin 噪声 + Generated 坐标都还没落地。
        # 本预设里唯一一处是 `头发` 的发丝颗粒，Scale 2000 属于逐像素级高频，
        # 位置只要差一点噪声就完全去相关，因此先用均值占位。
        self.notes.append("Noise Texture 用 0.5 均值占位（发丝颗粒缺失）")
        v = self.decl("VALUE", "0.5", "noisestub")
        self.put(n, "Fac", v, "VALUE")
        self.put(n, "Color", "float4(%s, %s, %s, 1.0)" % (v, v, v), "RGBA")

    def _n_tex_voronoi(self, n, needed):
        self.notes.append("Voronoi Texture 用常量占位（胸衣的程序化花纹缺失）")
        v = self.decl("VALUE", "0.5", "voronoistub")
        self.put(n, "Distance", v, "VALUE")
        self.put(n, "Color", "float4(%s, %s, %s, 1.0)" % (v, v, v), "RGBA")
        self.put(n, "Position", "float3(0.0, 0.0, 0.0)", "VECTOR")
        self.put(n, "W", "0.0", "VALUE")
        self.put(n, "Radius", "0.0", "VALUE")

    # ---- 闭包类节点 ------------------------------------------------------

    def _n_bsdf_transparent(self, n, needed):
        e = "ME_Transparent(%s)" % self.val(n, "Color", "RGBA")
        self.put(n, "BSDF", self.decl("SHADER", e, "transp"), "SHADER")

    def _n_bsdf_diffuse(self, n, needed):
        self.put(n, "BSDF", self.decl("SHADER", "ME_BlackBsdf()", "diff"), "SHADER")

    def _n_bsdf_glossy(self, n, needed):
        self.put(n, "BSDF", self.decl("SHADER", "ME_BlackBsdf()", "gloss"), "SHADER")

    def _n_emission(self, n, needed):
        c = self.val(n, "Color", "RGBA")
        s = self.val(n, "Strength", "VALUE")
        e = "ME_Emission(%s * float4(%s, %s, %s, 1.0))" % (c, s, s, s)
        self.put(n, "Emission", self.decl("SHADER", e, "emit"), "SHADER")

    def _n_mix_shader(self, n, needed):
        # Mix Shader 的三个插槽标识符是 Fac / Shader / Shader_001
        e = "ME_MixShader(%s, %s, %s)" % (self.val(n, "Fac", "VALUE"),
                                          self.val(n, "Shader", "SHADER"),
                                          self.val(n, "Shader_001", "SHADER"))
        self.put(n, "Shader", self.decl("SHADER", e, "mixsh"), "SHADER")


# ---------------------------------------------------------------------------


SHADER_TEMPLATE = """// 由 Tools/PmxPipeline/transpile.py 自动生成，请勿手改。
// 源：{graph}
// 材质：{mat_name}
// 扁平节点 {node_count} 个，贴图 {tex_count} 张
Shader "{shader_name}"
{{
    Properties
    {{
{properties}
    }}

    SubShader
    {{
        Tags {{ "RenderType" = "{render_type}" "Queue" = "{queue_tag}" }}
        Cull {cull}
        ZWrite {zwrite}
        Blend {blend}

        Pass
        {{
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            #define ME_LUT_ROWS {lut_rows}
            #define ME_VT_LO {vt_lo}
            #define ME_VT_HI {vt_hi}

{library}

            sampler2D _MELut;
            float3 _MESunDir;
            float3 _MEHeadFront;
            float3 _MEHeadRight;
{samplers}

            struct appdata
            {{
                float4 vertex  : POSITION;
                float3 normal  : NORMAL;
                float4 tangent : TANGENT;
                float2 uv      : TEXCOORD0;
            }};

            struct v2f
            {{
                float4 pos      : SV_POSITION;
                float2 uv0      : TEXCOORD0;
                float3 wnormal  : TEXCOORD1;
                float3 wpos     : TEXCOORD2;
                float4 wtangent : TEXCOORD3;
                float4 screen   : TEXCOORD4;
                float3 opos     : TEXCOORD5;
                float3 onormal  : TEXCOORD6;
            }};

            v2f vert(appdata v)
            {{
                v2f o;
                o.pos      = UnityObjectToClipPos(v.vertex);
                o.uv0      = v.uv;
                o.wnormal  = UnityObjectToWorldNormal(v.normal);
                o.wpos     = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wtangent = float4(UnityObjectToWorldDir(v.tangent.xyz),
                                    v.tangent.w * unity_WorldTransformParams.w);
                o.screen   = ComputeScreenPos(o.pos);
                o.opos     = v.vertex.xyz;
                o.onormal  = v.normal;
                return o;
            }}

            float4 frag(v2f i, float facing : VFACE) : SV_Target
            {{
                // EEVEE 对非背面剔除的材质会把法线翻向观察者，这里照做
                float3 N = normalize(i.wnormal) * ((facing >= 0.0) ? 1.0 : -1.0);
                float3 I = normalize(_WorldSpaceCameraPos - i.wpos);
                float3 trueN = normalize(cross(ddy(i.wpos), ddx(i.wpos))) *
                               ((facing >= 0.0) ? 1.0 : -1.0);
                float backfacing = (facing >= 0.0) ? 0.0 : 1.0;
                float3 objPos = i.opos;
                float3 objNormal = normalize(i.onormal);

{body}

                MEClosure cl = {root};
                float alpha = ME_ClosureAlpha(cl);
                // 视图变换在片元末端逐像素做：全屏后处理会破坏分层窗口的逐像素
                // alpha（见 ADR-0010 / 记忆 no-fullscreen-postprocessing-transparent-window）
                float3 disp = ME_ViewTransform(_MELut, 0.0, ME_LUT_ROWS,
                                               max(cl.radiance, 0.0),
                                               ME_VT_LO, ME_VT_HI);
                return float4(disp, alpha);
            }}
            ENDCG
        }}
    }}
}}
"""


def load_view_lut(path):
    """读实测的视图变换曲线，转成「display sRGB 的线性值」。

    渲染目标是 sRGB 读写的：着色器返回线性值、硬件负责编码。表里直接存反 sRGB 后的
    值，就省掉在着色器里来回换一趟，落到 PNG 的字节等于 Blender 的显示值。
    """
    lo = hi = None
    rows = []
    with io.open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            if line.startswith("#"):
                m = re.search(r"log2\(x\)\s*-\s*(-?[\d.]+)\)\s*/\s*\((-?[\d.]+)\s*-\s*(-?[\d.]+)\)",
                              line)
                if m:
                    lo = float(m.group(3))
                    hi = float(m.group(2))
                continue
            parts = line.split(",")
            if parts[0] == "index":
                continue
            rows.append(float(parts[3]))
    if lo is None or hi is None:
        # CSV 头里的注释是权威来源；取不到就用导出脚本里写死的同一组值
        lo, hi = -12.473931188, 4.026068812
    if len(rows) != 257:
        raise Unsupported("视图变换表有 %d 项，应为 257" % len(rows))

    def srgb_to_linear(c):
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4

    table = [[srgb_to_linear(max(0.0, min(1.0, v)))] * 3 + [1.0] for v in rows]
    return {"lo": lo, "hi": hi, "table": table}


BLEND_STATE = {
    # blend_method -> (RenderType, Queue 标签, ZWrite, Blend, 队列号)
    "OPAQUE": ("Opaque", "Geometry", "On", "Off", 2000),
    "CLIP": ("TransparentCutout", "AlphaTest", "On", "Off", 2450),
    "HASHED": ("Opaque", "Geometry", "On", "Off", 2000),
    "BLEND": ("Transparent", "Transparent", "Off", "SrcAlpha OneMinusSrcAlpha", 3000),
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--graph", required=True)
    ap.add_argument("--images", required=True, help="export_blender_graph.py --images 的目录")
    ap.add_argument("--view-lut", required=True)
    ap.add_argument("--out", required=True, help="生成物根目录（Assets 下）")
    ap.add_argument("--character")
    args = ap.parse_args()

    graph = Graph.load(args.graph)
    if graph.data.get("formatVersion", 0) < 3:
        print("[transpile] !! 节点图是旧格式（formatVersion=%s），缺 LUT 表与 "
              "interpolation，请重跑 export_blender_graph.py"
              % graph.data.get("formatVersion"))
        return 2
    with io.open(os.path.join(args.images, "images.json"), encoding="utf-8") as f:
        images = json.load(f)
    ctx = Context(graph, images, load_view_lut(args.view_lut))

    character = args.character or os.path.basename(args.out)
    lib = io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                               "hlsl", "me_nodes.hlsl"), encoding="utf-8").read()
    lib = "\n".join("            " + l if l.strip() else "" for l in lib.splitlines())

    shader_dir = os.path.join(args.out, "shaders")
    os.makedirs(shader_dir, exist_ok=True)

    pending = []        # 生成成功、等 LUT 行数定下来再落盘
    materials = []
    failed = []
    for idx, name in enumerate(sorted(graph.materials)):
        ascii_id = "M%02d" % idx
        try:
            ex, root = expand(graph, name)
        except GraphError as e:
            failed.append((name, "展开失败：%s" % e))
            continue
        for w in ex.warnings:
            ctx.warnings.append("[%s] %s" % (name, w))

        # 只生成真正被消费的输出
        needed = collections.defaultdict(set)
        for n in ex.nodes.values():
            for src in n.inputs.values():
                if isinstance(src, Ref):
                    needed[src.node].add(src.socket)
        if isinstance(root, Ref):
            needed[root.node].add(root.socket)

        em = MaterialEmitter(ctx, name, ascii_id)
        try:
            for nid in ex.order:
                n = ex.nodes[nid]
                if nid not in needed:
                    continue          # 输出没人要，是死节点
                em.emit(n, needed[nid])
            if isinstance(root, Ref):
                root_expr = em.exprs[(root.node, root.socket)][0]
            else:
                root_expr = "ME_Emission(%s)" % literal(
                    root.value, root.type if root.type in ("RGBA", "VALUE") else "RGBA")
        except Unsupported as e:
            failed.append((name, str(e)))
            continue

        mm = graph.materials[name]
        blend = mm.get("blend_method") or "OPAQUE"
        rt, qtag, zwrite, blend_cmd, queue = BLEND_STATE.get(blend, BLEND_STATE["OPAQUE"])
        cull = "Back" if mm.get("use_backface_culling") else "Off"
        shader_name = "%s/%s/%s" % (SHADER_PREFIX, character, ascii_id)

        props = "\n".join(
            '        %s ("%s", 2D) = "white" {}' % (t["prop"], t["image"])
            for t in em.textures)
        props = (props + "\n" if props else "") + \
            '        _MELut ("LUT (自动填充)", 2D) = "white" {}'
        samplers = "\n".join(
            "            sampler2D %s;\n            float4 %s_TexelSize;"
            % (t["prop"], t["prop"]) for t in em.textures)

        src = SHADER_TEMPLATE.format(
            graph=os.path.basename(args.graph), mat_name=name,
            node_count=len(ex.nodes), tex_count=len(em.textures),
            shader_name=shader_name, properties=props, render_type=rt,
            queue_tag=qtag, cull=cull, zwrite=zwrite, blend=blend_cmd,
            lut_rows="{LUT_ROWS}", vt_lo=fnum(ctx.view_lo), vt_hi=fnum(ctx.view_hi),
            library=lib, samplers=samplers,
            body="\n".join("            " + l.strip() for l in em.lines),
            root=root_expr)
        pending.append((os.path.join(shader_dir, ascii_id + ".shader"), src))

        materials.append({
            "name": name, "ascii": ascii_id, "shader": shader_name,
            "textures": em.textures,
            "blendMethod": blend, "shadowMethod": mm.get("shadow_method"),
            "backfaceCulling": bool(mm.get("use_backface_culling")),
            "alphaThreshold": mm.get("alpha_threshold"),
            "renderType": rt, "queue": queue,
            "nodeCount": len(ex.nodes), "notes": sorted(set(em.notes)),
        })

    rows = len(ctx.rows)
    for path, src in pending:
        with io.open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(src.replace("{LUT_ROWS}", fnum(rows)))

    flat = []
    for table in ctx.row_data:
        for px in table:
            flat.extend(float(x) for x in px)
    bindings = {
        "character": character,
        "graph": os.path.basename(args.graph),
        "imagesDir": args.images.replace("\\", "/"),
        "lut": {"width": 257, "rows": ctx.rows, "data": flat},
        "viewTransform": {"lo": ctx.view_lo, "hi": ctx.view_hi},
        "materials": materials,
        "unsupported": [{"name": n, "reason": r} for n, r in failed],
    }
    bpath = os.path.join(args.out, "%s.bindings.json" % character)
    with io.open(bpath, "w", encoding="utf-8", newline="\n") as f:
        json.dump(bindings, f, ensure_ascii=False, indent=1)

    print("[transpile] 生成 %d 个着色器，%d 个材质未支持，LUT %d 行"
          % (len(materials), len(failed), rows))
    for n, r in failed:
        print("[transpile]   跳过 %-10s %s" % (n, r))
    for m in materials:
        for note in m["notes"]:
            print("[transpile]   注意 %-10s %s" % (m["name"], note))
    for w in sorted(set(ctx.warnings)):
        print("[transpile]   !! %s" % w)
    print("[transpile] 清单 -> %s" % bpath)
    return 0


if __name__ == "__main__":
    sys.exit(main())
