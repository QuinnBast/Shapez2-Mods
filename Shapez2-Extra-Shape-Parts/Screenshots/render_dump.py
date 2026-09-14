"""Draws whole shapes from the meshes `esp.dump` writes, including the vanilla ones.

Everything else in this folder draws shapes from outlines transcribed out of the mod's own source,
which can only ever check the mod against itself. This reads the real meshes the game is holding -
`part_G_6.obj` is vanilla's hexagonal RectHex, mesh and all - so it is the only drawing here that
can answer "does our part look like theirs".

Every triangle, projected straight down and drawn low to high, which is the top-down view the parts
have to be told apart in. The vertex colours say which triangles are the dark border: the game
treats red below 0.05 as outline, so this does too.

Not top faces only - that was the first version and it was wrong. Vanilla's border is a flange
*below* the top face and wider than it, so filtering to the top face draws a vanilla part with no
border at all.

    esp.dump            in the in-game console, writes to <persistent>/extra-shape-parts
    python render_dump.py
"""

import math
import os

from PIL import Image, ImageDraw

from render import BG, CARD, RULE, INK, MUTED, ACCENT, EDGE, SS, canvas, card, finish, font, text
from render import R, GAP, LAYER

DUMP = os.path.join(os.environ["SPZ2_PERSISTENT"], "extra-shape-parts")
OUT = os.path.dirname(os.path.abspath(__file__))

BODY = (176, 190, 197)


def load(code, part_count):
    """Triangles from one dumped part, as (points, is_outline), sorted bottom to top."""
    path = os.path.join(DUMP, f"part_{code}_{part_count}.obj")
    if not os.path.exists(path):
        return None

    verts, faces, outline = [], [], {}
    for line in open(path, encoding="utf-8"):
        if line.startswith("v "):
            verts.append(tuple(float(v) for v in line.split()[1:4]))
        elif line.startswith("f "):
            faces.append(tuple(int(i) - 1 for i in line.split()[1:4]))
        elif line.startswith("# color "):
            bits = line.split()
            outline[int(bits[2])] = float(bits[3]) < 0.05

    if not verts:
        return None

    # Every triangle, not just the top face, sorted low to high so the drawing order reproduces
    # what the camera sees from above.
    #
    # This matters more than it sounds: vanilla's black border is a *flange below the top face*,
    # wider than the face it sits under, so filtering to the top face alone renders a vanilla part
    # with no border at all - which is exactly the wrong conclusion to draw from a picture meant to
    # compare borders.
    triangles = []
    for a, b, c in faces:
        points = [(verts[i][0], verts[i][2]) for i in (a, b, c)]
        height = sum(verts[i][1] for i in (a, b, c)) / 3.0
        triangles.append((height, points, all(outline.get(i, False) for i in (a, b, c))))

    triangles.sort(key=lambda t: t[0])
    return [(points, is_outline) for _height, points, is_outline in triangles]


def draw_shape(draw, triangles, cx, cy, radius_px, count, layers=1):
    """The placement ShapeItemRenderer uses: rotate part j by j/count*360, push it along the
    bisector at (j+0.5)/count*360 by ShapeInnerGap, scale layer i by 0.76^i."""
    half = math.radians(0.5 * 360.0 / count)
    ox, oz = math.sin(half) * GAP, math.cos(half) * GAP

    for i in range(layers):
        scale = LAYER ** i
        for j in range(count):
            a = math.radians(j * 360.0 / count)
            ca, sa = math.cos(a), math.sin(a)
            for points, is_outline in triangles:
                out = []
                for px, pz in points:
                    x, z = px * scale + ox, pz * scale + oz
                    out.append((cx + (x * ca + z * sa) * radius_px / R,
                                cy - (-x * sa + z * ca) * radius_px / R))
                draw.polygon(out, fill=EDGE if is_outline else BODY)


VANILLA_HEX = [("H", "CubeHex"), ("G", "RectHex"), ("F", "FlowerHex")]
OURS = [("E", "Gear"), ("K", "Cross"), ("I", "Bar"), ("D", "Diamond"), ("O", "Dot"),
        ("M", "Dome"), ("T", "Wedge"), ("Z", "Sawblade"), ("B", "Flower"), ("L", "Leaf")]


def sheet():
    cols, cell_w, cell_h, pad = 5, 264, 248, 34
    rows = 1 + (len(OURS) + cols - 1) // cols
    W = pad * 2 + cols * cell_w
    head = 176
    H = head + rows * cell_h + pad + 40

    image, draw = canvas(W, H)
    title = font("arialbd.ttf", 42)
    sub = font("arial.ttf", 20)
    label = font("arialbd.ttf", 21)
    mono = font("consola.ttf", 15)

    text(draw, (pad, 44), "Hexagonal parts, from the real meshes", title, INK)
    text(draw, (pad, 96), "Drawn from what esp.dump wrote, so the three on the top row are "
                          "vanilla's own hexagonal meshes and not a stand-in.", sub, MUTED)
    text(draw, (pad, 124), "Top faces, straight down, six parts each - the view a player has to "
                           "tell them apart in.", sub, MUTED)

    for i, (code, name) in enumerate(VANILLA_HEX):
        x = pad + i * cell_w
        card(draw, x + 6, head, cell_w - 12, cell_h - 22)
        tris = load(code, 6)
        cx = x + cell_w / 2
        if tris:
            draw_shape(draw, tris, cx * SS, (head + 100) * SS, 76 * SS, 6)
        text(draw, (cx, head + 176), name, label, ACCENT, anchor="ma")
        text(draw, (cx, head + 204), f"vanilla  {code}", mono, MUTED, anchor="ma")

    for i, (code, name) in enumerate(OURS):
        col, row = i % cols, i // cols
        x = pad + col * cell_w
        y = head + cell_h + row * cell_h
        card(draw, x + 6, y, cell_w - 12, cell_h - 22)
        tris = load(code, 6)
        cx = x + cell_w / 2
        if tris:
            draw_shape(draw, tris, cx * SS, (y + 100) * SS, 76 * SS, 6)
        text(draw, (cx, y + 176), name, label, INK, anchor="ma")
        text(draw, (cx, y + 204), f"this mod  {code}", mono, MUTED, anchor="ma")

    finish(image, W, H, os.path.join(OUT, "hexagonal-real.png"))


if __name__ == "__main__":
    if not os.path.isdir(DUMP):
        raise SystemExit(f"No dump at {DUMP}. Run esp.dump in the in-game console first.")
    sheet()
