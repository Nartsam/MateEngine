"""把基准图与对照图拼成一张两联对比图，供肉眼验收。

默认两联（左 Blender 基准、右当前渲染）。不再带旧 UTS2 那一栏——那是已经证伪的
路线，留着只会把真正要比的两张图挤小。

    Build/.venv/Scripts/python.exe Tools/PmxPipeline/make_compare.py \
        --ref  Build/RefDiff/丽塔-front-blender.png \
        --test Build/RefDiff/丽塔-front-unity.png \
        --out  Build/RefDiff/对比-全身.png \
        [--crop x0,y0,x1,y1] [--scale 1.0] [--label]
"""
import argparse
import io
import os

from PIL import Image, ImageDraw

BG = (128, 128, 128, 255)
GAP = 12


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ref", required=True)
    ap.add_argument("--test", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--crop", help="x0,y0,x1,y1（像素，作用于原图）")
    ap.add_argument("--scale", type=float, default=1.0)
    ap.add_argument("--label", action="store_true", help="在角上标注哪边是哪边")
    args = ap.parse_args()

    panes = []
    for path in (args.ref, args.test):
        im = Image.open(path).convert("RGBA")
        if args.crop:
            box = tuple(int(v) for v in args.crop.split(","))
            im = im.crop(box)
        if args.scale != 1.0:
            w, h = im.size
            # 放大用最近邻（看清像素），缩小用 Lanczos（避免摩尔纹）
            f = Image.NEAREST if args.scale > 1.0 else Image.LANCZOS
            im = im.resize((int(w * args.scale), int(h * args.scale)), f)
        panes.append(im)

    w, h = panes[0].size
    canvas = Image.new("RGBA", (w * 2 + GAP, h), BG)
    for i, im in enumerate(panes):
        canvas.alpha_composite(im, (i * (w + GAP), 0))

    if args.label:
        d = ImageDraw.Draw(canvas)
        for i, text in enumerate(("Blender", "MateEngine")):
            d.text((i * (w + GAP) + 8, 8), text, fill=(255, 255, 255, 255))

    os.makedirs(os.path.dirname(os.path.abspath(args.out)) or ".", exist_ok=True)
    canvas.convert("RGB").save(args.out)
    print("[compare] 已写 %s（%d×%d）" % (args.out, canvas.width, canvas.height))


if __name__ == "__main__":
    main()
