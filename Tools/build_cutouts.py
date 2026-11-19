"""Rebuild pose layers from the source illustrations and Tools/cutouts.json (Pillow, NumPy)."""
import json
from collections import deque
from pathlib import Path
import xml.etree.ElementTree as ET

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
LAYERS = ASSETS / "Layers"


def largest_component(mask):
    # Isolated warm highlights in hair can otherwise travel with a hand.
    ys, xs = np.nonzero(mask)
    pending = set(zip(ys.tolist(), xs.tolist()))
    largest = []
    while pending:
        first = pending.pop()
        queue = deque([first])
        component = [first]
        while queue:
            y, x = queue.popleft()
            for p in ((y-1,x),(y+1,x),(y,x-1),(y,x+1)):
                if p in pending:
                    pending.remove(p)
                    queue.append(p)
                    component.append(p)
        if len(component) > len(largest):
            largest = component
    result = np.zeros_like(mask)
    if largest:
        result[tuple(np.array(largest).T)] = True
    return result


def part_mask(image, part):
    points = [tuple(map(int, xy.split(","))) for xy in part["outline"].split()]
    area = Image.new("L", image.size)
    ImageDraw.Draw(area).polygon(points, fill=255)
    r, g, b, a = np.asarray(image, dtype=np.int16).transpose(2, 0, 1)
    # Edge ink is recovered around the colour seeds, including between spread fingers.
    colours = {
        "skin": (r > 145) & (r - g > 8) & (r - b > 10) & (g >= b - 5),
        "pink": (r > 120) & (r - g > 27) & (b > g + 3),
        "green": (g >= r - 9) & (g - b > 36) & (r < 199),
        "white": (r > 195) & (g > 189) & (b > 174),
        "red": (r > 85) & (r - g > 28) & (r - b > 14),
        "flower": ((r > 49) & (r > g * 1.35) & (r - b > 14)) | ((g >= r - 7) & (g - b > 23) & (g < 156)),
        "moon": (r - b > 25) & (g - b > 12) & (r >= g) & (r > 109),
    }
    if part["ink"] in ("top", "shape"):
        return np.asarray(area) > 0
    seed = np.logical_or.reduce([colours[c] for c in part["ink"].split()])
    seed &= (np.asarray(area) > 0) & (a > 12)
    if part.get("backdrop") == "hairAbove":
        seed = largest_component(seed & (r - g + 3 > g - b))
        edge = dilate(seed, 5) & ((g < 149) | (a < 245))
        return seed | (edge & (a > 0))
    if part["ink"] == "moon":
        edge = dilate(seed, 5) & (g < 140)
        y = np.indices(seed.shape)[0]
        return (seed | edge) & (y >= 175)
    mask = Image.fromarray(np.uint8(seed) * 255)
    mask = mask.filter(ImageFilter.MaxFilter(5)).filter(ImageFilter.MinFilter(3))
    mask = mask.filter(ImageFilter.MaxFilter(5))
    return (np.asarray(mask) > 0) & (np.asarray(area) > 0)


def backfill(rgba, hole):
    # Continue the nearest exposed background into the occluded area. Work in
    # premultiplied alpha so a sleeve against the desktop uncovers transparency.
    values = rgba.astype(np.float32)
    values[:, :, :3] *= values[:, :, 3:4] / 255
    known = ~hole
    while not known.all():
        total = np.zeros_like(values)
        count = np.zeros(known.shape, dtype=np.float32)
        padded = np.pad(known, 1)
        pixels = np.pad(values, ((1, 1), (1, 1), (0, 0)))
        h, w = known.shape
        for y, x, weight in [(0,1,1),(2,1,1),(1,0,1),(1,2,1),(0,0,.7),(0,2,.7),(2,0,.7),(2,2,.7)]:
            weights = padded[y:y+h, x:x+w] * weight
            total += pixels[y:y+h, x:x+w] * weights[:, :, None]
            count += weights
        edge = ~known & (count > 0)
        values[edge] = total[edge] / count[edge, None]
        known |= edge
    alpha = values[:, :, 3:4]
    values[:, :, :3] *= 255 / np.maximum(alpha, 1)
    return np.uint8(np.clip(np.rint(values), 0, 255))


def save_layer(rgba, name):
    rgba = rgba.copy()
    rgba[rgba[:, :, 3] == 0] = 0
    Image.fromarray(rgba).save(LAYERS / (name + ".png"), optimize=True)


def dilate(mask, size):
    return np.array(Image.fromarray(mask.astype("uint8") * 255).filter(ImageFilter.MaxFilter(size))) > 0


def clear_part(original, mask, style):
    # Sample past the dark antialiased outline as well as the coloured interior.
    # Sampling that outline itself would leave a second set of fingers on the plate.
    erase = dilate(mask, 5)
    sample_hole = dilate(erase, 7)
    ys, xs = np.nonzero(sample_hole)
    yy = slice(max(0, ys.min()-4), min(original.shape[0], ys.max()+5))
    xx = slice(max(0, xs.min()-4), min(original.shape[1], xs.max()+5))
    fill = backfill(original[yy, xx], sample_hole[yy, xx])
    if style == "hairAbove":
        y, x = np.indices(fill.shape[:2])
        y, x = y + yy.start, x + xx.start
        sy = np.rint(141 - (y - 141) * .2).astype(int)
        sx = np.rint(x - (y - sy) * .06).astype(int)
        fill[:] = original[sy, sx]
    elif style == "strands":
        # Borrow along the direction of intact strands below the pendant and
        # along the sleeve seam behind its cord. Bilinear sampling keeps the ink smooth.
        for y, x in zip(*np.nonzero(erase)):
            if y < 217.8 + (x - 95) * .38:
                sx, sy = x + 12, y + 4.5
            else:
                sy = 280 + .16 * (y - 245)
                sx = x - .40 * (sy - y)
            ix, iy = int(sx), int(sy)
            fx, fy = sx - ix, sy - iy
            fill[y - yy.start, x - xx.start] = (
                (1-fy) * ((1-fx)*original[iy,ix] + fx*original[iy,ix+1]) +
                fy * ((1-fx)*original[iy+1,ix] + fx*original[iy+1,ix+1]))
    result = original.copy()
    local = erase[yy, xx]
    result[yy, xx][local] = fill[local]
    return result


def build():
    LAYERS.mkdir(exist_ok=True)
    rigs = ET.Element("rigs")
    table = json.loads(Path(__file__).with_name("cutouts.json").read_text(encoding="utf8"))
    for name, parts in table.items():
        image = Image.open(ASSETS / (name + ".png")).convert("RGBA")
        original = np.array(image)
        base = original.copy()
        rig = ET.SubElement(rigs, "rig", name=name)
        for part in parts:
            mask = part_mask(image, part)
            # The full-dress bow only needs a small continuation of skin above
            # the knees. The rest of the lifted silhouette exposes the desktop.
            if part["ink"] == "top":
                base[mask] = 0
                for x in range(154, 229):
                    ys = np.flatnonzero(mask[:, x])
                    edge = ys[-1] + 1
                    if original[edge+2, x, 3] > 200:
                        base[edge-14:edge, x] = original[edge+2, x]
            else:
                base = clear_part(base, mask, part.get("backdrop"))
                # Keep a little overlap where the wrist enters the fixed cuff.
                px, py = part["pivot"]
                y, x = np.ogrid[:image.height, :image.width]
                joint = (x-px)**2 + (y-py)**2 < 6**2
                base[joint] = original[joint]
            piece = original.copy()
            piece[~mask] = 0
            box = Image.fromarray(piece[:, :, 3]).getbbox()
            x, y, right, bottom = box
            filename = name + "-" + part["name"]
            save_layer(piece[y:bottom, x:right], filename)
            px, py = part["pivot"]
            dx, dy = part.get("travel", (0, 0))
            ET.SubElement(rig, "part", name=filename, x=str(x), y=str(y),
                          pivotX=str(px), pivotY=str(py), angle=str(part["angle"]), dx=str(dx), dy=str(dy))
        save_layer(base, name)
    ET.indent(rigs)
    ET.ElementTree(rigs).write(LAYERS / "poses.xml", encoding="utf-8", xml_declaration=True)


if __name__ == "__main__":
    build()
