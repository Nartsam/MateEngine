"""比对 Blender 基准图与 Unity 对照图，给出客观差异数值。

差分验收闭环的第三环（Docs/plans/2026-09-10-blender-graph-transpile.md Phase 0）。
前两环是 blender_apply_preset.py 和 Assets/Editor/PmxPipeline/MERefRenderer.cs。

**不要拿背景做比对。** 两边背景不可能一致：Blender 的世界背景同样要过 Filmic
视图变换（给 808080 渲出来是 8C8C8C），而 Unity 只做 sRGB 编码。所以一切统计都
限制在角色轮廓内，背景只影响观感、不进数值。

输出三组数：

1. **轮廓 IoU** —— 两边 alpha 掩码的交并比。它先于颜色：轮廓对不上说明姿势、
   取景或描边宽度有问题，此时颜色差异无从谈起。
2. **交集内的颜色差异** —— 平均/中位/P95 绝对误差，以及 ΔE（CIE76，在 Lab 上算，
   比 RGB 差值更贴近人眼感受）。
3. **逐材质差异** —— 需要 --ids/--id-map（由 Unity 侧的 -idOut 产出）。转译是按
   材质推进的，这一组数就是进度表：哪个材质已经对上、哪个还差得远。
4. **逐区域差异** —— 把画面切成网格，列出最差的若干格，用于定位「差在哪儿」。

用法：

    python Tools/PmxPipeline/compare_renders.py \\
        --ref Build/RefDiff/rita_front_blender.png \\
        --test Build/RefDiff/rita_front_unity.png \\
        --out-dir Build/RefDiff \\
        --report Build/RefDiff/diff.json

会额外写出一张并排对照图（左=基准，中=对照，右=差异热力图）便于人眼确认。

只依赖 numpy 与 Pillow。若环境缺少，用仓库内的虚拟环境安装，勿装到全局。
"""
import argparse
import json
import os
import sys

import numpy as np
from PIL import Image

ALPHA_THRESHOLD = 0.5  # 判定「属于角色」的 alpha 阈值


def load_rgba(path):
    """读 PNG 为 float32 RGBA，取值 0..1。Blender 出 16bit、Unity 出 8bit，统一归一化。"""
    img = Image.open(path)
    if img.mode != "RGBA":
        img = img.convert("RGBA")
    a = np.asarray(img)
    maxv = 65535.0 if a.dtype == np.uint16 else 255.0
    return a.astype(np.float32) / maxv


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_xyz(rgb):
    m = np.array([[0.4124564, 0.3575761, 0.1804375],
                  [0.2126729, 0.7151522, 0.0721750],
                  [0.0193339, 0.1191920, 0.9503041]], dtype=np.float32)
    return rgb @ m.T


def xyz_to_lab(xyz):
    # D65 白点
    white = np.array([0.95047, 1.00000, 1.08883], dtype=np.float32)
    t = xyz / white
    d = 6.0 / 29.0
    f = np.where(t > d ** 3, np.cbrt(np.maximum(t, 1e-12)), t / (3 * d * d) + 4.0 / 29.0)
    return np.stack([116 * f[..., 1] - 16,
                     500 * (f[..., 0] - f[..., 1]),
                     200 * (f[..., 1] - f[..., 2])], axis=-1)


def srgb_to_lab(rgb):
    return xyz_to_lab(linear_to_xyz(srgb_to_linear(rgb)))


def stats(values):
    if values.size == 0:
        return {"count": 0}
    return {
        "count": int(values.size),
        "mean": float(values.mean()),
        "median": float(np.median(values)),
        "p95": float(np.percentile(values, 95)),
        "max": float(values.max()),
    }


def classify_ids(id_rgba, id_map, tol=12.0):
    """把 ID 通道图上的每个像素归到某个材质编号；无法归类的记为 -1。

    调色板不在这里重算——由 Unity 侧写进报告的 material_ids[name].rgb 给定，避免
    两处实现悄悄跑偏。

    tol 是允许的字节距离。ID 通道是关掉抗锯齿渲的，正常像素应当精确落在调色板上，
    留一点余量只为吸收 8bit 量化。
    """
    h, w = id_rgba.shape[:2]
    rgb = id_rgba[..., :3] * 255.0
    opaque = id_rgba[..., 3] > ALPHA_THRESHOLD

    ids = np.full((h, w), -1, dtype=np.int32)
    best = np.full((h, w), np.inf, dtype=np.float32)
    for name, info in id_map.items():
        ref = np.array(info["rgb"], dtype=np.float32)
        d = np.linalg.norm(rgb - ref, axis=-1)
        hit = (d < best) & opaque
        best[hit] = d[hit]
        ids[hit] = info["id"]
    ids[best > tol] = -1
    return ids


def per_material(delta_e, mask, ids, id_map):
    """逐材质统计。只统计落在 mask（两边轮廓交集）内的像素。"""
    rows = []
    for name, idx in sorted(((k, v["id"]) for k, v in id_map.items()), key=lambda kv: kv[1]):
        m = mask & (ids == idx)
        if m.sum() == 0:
            continue
        d = delta_e[m]
        rows.append({
            "material": name,
            "id": idx,
            "pixels": int(m.sum()),
            "mean_delta_e": float(d.mean()),
            "median_delta_e": float(np.median(d)),
            "p95_delta_e": float(np.percentile(d, 95)),
        })
    rows.sort(key=lambda r: r["mean_delta_e"], reverse=True)
    return rows


def grid_worst(delta_e, mask, grid, top):
    """把画面切成 grid×grid 格，返回差异最大的若干格。用于定位问题区域。"""
    h, w = delta_e.shape
    cells = []
    for gy in range(grid):
        for gx in range(grid):
            y0, y1 = gy * h // grid, (gy + 1) * h // grid
            x0, x1 = gx * w // grid, (gx + 1) * w // grid
            m = mask[y0:y1, x0:x1]
            if m.sum() < 32:  # 像素太少的格子统计无意义
                continue
            d = delta_e[y0:y1, x0:x1][m]
            cells.append({
                "cell": [gx, gy],
                "px_rect": [x0, y0, x1, y1],
                "pixels": int(m.sum()),
                "mean_delta_e": float(d.mean()),
                "p95_delta_e": float(np.percentile(d, 95)),
            })
    cells.sort(key=lambda c: c["mean_delta_e"], reverse=True)
    return cells[:top]


def heatmap(delta_e, mask, vmax):
    """差异热力图：黑→红→黄→白。轮廓外画成深灰以便看清边界。"""
    t = np.clip(delta_e / max(vmax, 1e-6), 0, 1)
    r = np.clip(t * 3.0, 0, 1)
    g = np.clip(t * 3.0 - 1.0, 0, 1)
    b = np.clip(t * 3.0 - 2.0, 0, 1)
    rgb = np.stack([r, g, b], axis=-1)
    rgb[~mask] = 0.15
    return (rgb * 255).astype(np.uint8)


def composite(rgba, bg=0.5):
    """按 alpha 合成到中性灰，供并排图使用。"""
    a = rgba[..., 3:4]
    return rgba[..., :3] * a + bg * (1 - a)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ref", required=True, help="Blender 基准图")
    ap.add_argument("--test", required=True, help="Unity 对照图")
    ap.add_argument("--report", help="差异报告 JSON")
    ap.add_argument("--ids", help="Unity 侧材质 ID 通道图（-idOut 产出）")
    ap.add_argument("--id-map", help="含 material_ids 的 Unity 报告 JSON（-report 产出）")
    ap.add_argument("--out-dir", help="并排对照图与热力图的输出目录")
    ap.add_argument("--grid", type=int, default=8, help="区域统计的网格边长")
    ap.add_argument("--top", type=int, default=8, help="列出差异最大的前 N 格")
    ap.add_argument("--heat-max", type=float, default=40.0,
                    help="热力图上限 ΔE，超过即饱和")
    args = ap.parse_args()

    ref = load_rgba(args.ref)
    test = load_rgba(args.test)
    if ref.shape != test.shape:
        raise SystemExit(f"尺寸不一致：{ref.shape} vs {test.shape}")
    h, w = ref.shape[:2]

    mref = ref[..., 3] > ALPHA_THRESHOLD
    mtest = test[..., 3] > ALPHA_THRESHOLD
    inter = mref & mtest
    union = mref | mtest
    iou = float(inter.sum()) / float(union.sum()) if union.any() else 0.0

    lab_ref = srgb_to_lab(ref[..., :3])
    lab_test = srgb_to_lab(test[..., :3])
    delta_e = np.linalg.norm(lab_ref - lab_test, axis=-1)
    rgb_abs = np.abs(ref[..., :3] - test[..., :3]).mean(axis=-1)

    report = {
        "ref": os.path.abspath(args.ref),
        "test": os.path.abspath(args.test),
        "size": [w, h],
        "silhouette": {
            "ref_pixels": int(mref.sum()),
            "test_pixels": int(mtest.sum()),
            "intersection": int(inter.sum()),
            "union": int(union.sum()),
            "iou": iou,
            "only_in_ref": int((mref & ~mtest).sum()),
            "only_in_test": int((mtest & ~mref).sum()),
        },
        "color_in_intersection": {
            "delta_e76": stats(delta_e[inter]),
            "rgb_abs": stats(rgb_abs[inter]),
        },
        "worst_cells": grid_worst(delta_e, inter, args.grid, args.top),
    }

    if args.ids and args.id_map:
        with open(args.id_map, encoding="utf-8") as f:
            id_map = json.load(f).get("material_ids") or {}
        if not id_map:
            raise SystemExit(f"{args.id_map} 里没有 material_ids 段")
        ids = classify_ids(load_rgba(args.ids), id_map)
        unlabeled = int((inter & (ids < 0)).sum())
        report["per_material"] = per_material(delta_e, inter, ids, id_map)
        report["unlabeled_pixels_in_intersection"] = unlabeled

    print(f"[compare] 尺寸 {w}x{h}")
    print(f"[compare] 轮廓 IoU = {iou:.4f}  "
          f"(仅基准有 {report['silhouette']['only_in_ref']} px / "
          f"仅对照有 {report['silhouette']['only_in_test']} px)")
    de = report["color_in_intersection"]["delta_e76"]
    if de.get("count"):
        print(f"[compare] 交集内 ΔE76  平均 {de['mean']:.2f}  中位 {de['median']:.2f}  "
              f"P95 {de['p95']:.2f}  最大 {de['max']:.2f}")
        ra = report["color_in_intersection"]["rgb_abs"]
        print(f"[compare] 交集内 RGB 平均绝对误差 {ra['mean']:.4f}"
              f"（≈{ra['mean'] * 255:.1f}/255）")
    if "per_material" in report:
        print(f"[compare] 逐材质（{len(report['per_material'])} 个，按平均 ΔE 降序）：")
        for r in report["per_material"]:
            print(f"           {r['material'][:14]:<16} 平均ΔE {r['mean_delta_e']:6.2f}  "
                  f"中位 {r['median_delta_e']:6.2f}  P95 {r['p95_delta_e']:6.2f}  "
                  f"{r['pixels']:>7} px")
        print(f"[compare] 交集内未归类像素 {report['unlabeled_pixels_in_intersection']} 个")

    print(f"[compare] 差异最大的 {len(report['worst_cells'])} 个区域：")
    for c in report["worst_cells"]:
        print(f"           格({c['cell'][0]},{c['cell'][1]}) "
              f"平均ΔE {c['mean_delta_e']:6.2f}  P95 {c['p95_delta_e']:6.2f}  "
              f"{c['pixels']} px")

    if args.out_dir:
        os.makedirs(args.out_dir, exist_ok=True)
        heat = heatmap(delta_e, inter, args.heat_max)
        Image.fromarray(heat).save(os.path.join(args.out_dir, "diff_heatmap.png"))
        side = np.concatenate([
            (composite(ref) * 255).astype(np.uint8),
            (composite(test) * 255).astype(np.uint8),
            heat,
        ], axis=1)
        Image.fromarray(side).save(os.path.join(args.out_dir, "diff_side_by_side.png"))
        print(f"[compare] 已写 {args.out_dir}/diff_heatmap.png 与 diff_side_by_side.png")

    if args.report:
        os.makedirs(os.path.dirname(os.path.abspath(args.report)), exist_ok=True)
        with open(args.report, "w", encoding="utf-8", newline="\n") as f:
            json.dump(report, f, ensure_ascii=False, indent=2)
        print(f"[compare] 已写报告 {args.report}")


if __name__ == "__main__":
    sys.exit(main())
