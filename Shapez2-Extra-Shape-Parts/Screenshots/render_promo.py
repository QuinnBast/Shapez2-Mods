"""Promo sheets: the ten parts and the twenty-six quest shapes, in quad and in hexagonal.

The quest shapes are the interesting case. They contain vanilla parts as well as ours - `Foundations`
asks for the configuration's own common part under a diamond - and vanilla's meshes are authored, so
they cannot be drawn from an outline the way ours can. This draws them from what `esp.dump` wrote
instead, which means every shape here is the real thing on both sides.

The chains are parsed out of SideQuestCatalog.cs rather than copied, so a change to a chain shows up
here without anyone remembering to update it.

    esp.dump               in the in-game console, once
    python render_promo.py
"""

import io
import math
import os
import re

import render as rq
import render_dump as rd
import render_hex as rh
import thumbnail as tn
from PIL import Image, ImageDraw
from render import (ACCENT, EDGE, INK, MUTED, SS, TINT, GAP, LAYER, R, canvas, card, clockwise,
                    finish, font, outset, text, OUTLINE)

OUT = os.path.dirname(os.path.abspath(__file__))
CATALOG = os.path.join(OUT, "..", "ExtraShapeParts", "SideQuestCatalog.cs")

# Which common parts each configuration offers, for the `1` and `2` placeholders.
COMMONS = {4: ["C", "R"], 6: ["H"]}

NAMES = {"E": "Gear", "K": "Cross", "I": "Bar", "D": "Diamond", "O": "Dot",
         "M": "Dome", "T": "Wedge", "Z": "Sawblade", "B": "Flower", "L": "Leaf"}
ORDER = ["E", "K", "I", "D", "O", "M", "T", "Z", "B", "L"]

# A colour per part for the parts sheets, so ten cards do not read as ten grey blobs.
PART_COLOUR = {"E": "r", "K": "c", "I": "y", "D": "g", "O": "m",
               "M": "b", "T": "r", "Z": "w", "B": "m", "L": "g"}


# --- drawing, from either source ------------------------------------------------------------------

def part_polygons(code, sector, part_count, colour):
    """One part as a list of (points, fill), painted in order.

    Ours come from the generated outline: the border first, then the coloured face on top of it.
    Vanilla's come from the dumped mesh, whose triangles already carry their own body/outline flag.
    """
    tint = TINT.get(colour, TINT["u"])

    if (code, sector) in rh.PREPARED:
        border, body = rh.PREPARED[(code, sector)]
        return [(border, EDGE), (body, tint)]

    triangles = rd.load(code, part_count) or rd.load(code, 4)
    if triangles is None:
        return []
    return [(points, EDGE if is_outline else tint) for points, is_outline in triangles]


def draw_shape(draw, code, cx, cy, radius_px, part_count, sector):
    """`code` is a shape hash with `part_count` pairs per layer."""
    half = math.radians(0.5 * 360.0 / part_count)
    ox, oz = math.sin(half) * GAP, math.cos(half) * GAP

    for layer_index, layer in enumerate(code.split(":")):
        scale = LAYER ** layer_index

        for j in range(part_count):
            part, colour = layer[j * 2], layer[j * 2 + 1]
            if part == "-":
                continue

            angle = math.radians(j * 360.0 / part_count)
            ca, sa = math.cos(angle), math.sin(angle)

            for points, fill in part_polygons(part, sector, part_count, colour):
                projected = []
                for px, pz in points:
                    x, z = px * scale + ox, pz * scale + oz
                    projected.append((cx + (x * ca + z * sa) * radius_px / R,
                                      cy - (-x * sa + z * ca) * radius_px / R))
                draw.polygon(projected, fill=fill)


# --- the chains, read out of the catalogue ----------------------------------------------------------

def chains():
    source = io.open(CATALOG, encoding="utf-8").read()
    source = source[source.index("public static IReadOnlyList<SideQuestChain> All"):]

    out = []
    for block in source.split("new SideQuestChain(")[1:]:
        _slug, title = re.match(r'"([\w-]+)",\s*"([^"]+)"', block).groups()
        steps = [(name, re.findall(r'"([^"]+)"', layers))
                 for name, _amount, layers
                 in re.findall(r'new SideQuestStep\("([^"]+)",\s*(\d+),\s*([^)]*)\)', block)]
        out.append((title, steps))
    return out


def expand(layers, part_count):
    commons = COMMONS[part_count]
    rendered = []
    for pattern in layers:
        slots = len(pattern) // 2
        layer = ""
        for part in range(part_count):
            code, colour = pattern[(part % slots) * 2], pattern[(part % slots) * 2 + 1]
            if code == "1":
                code = commons[0]
            elif code == "2":
                code = commons[min(1, len(commons) - 1)]
            layer += code + colour
        rendered.append(layer)
    return ":".join(rendered)


# --- sheets -------------------------------------------------------------------------------------

def backdrop(width, height):
    """The icon's radial lift, stretched to a sheet. Flat dark reads as a spreadsheet."""
    import render_icon
    square = render_icon.backdrop(max(width, height))
    return square.resize((width, height), Image.BICUBIC)


def parts_sheet(part_count, sector, filename, heading, kicker, badge):
    cols, rows = 5, 2
    cell_w, cell_h, pad = 312, 322, 40
    W = pad * 2 + cols * cell_w
    # Room under the last row's labels for the kicker bar, which was sitting on them.
    H = 120 + rows * cell_h + 44

    image = backdrop(W * SS, H * SS)
    draw = ImageDraw.Draw(image)

    for i, code in enumerate(ORDER):
        col, row = i % cols, i // cols
        x, y = pad + col * cell_w, 120 + row * cell_h
        cx = x + cell_w / 2
        colour = PART_COLOUR[code]
        draw_shape(draw, (code + colour) * part_count, cx * SS, (y + 124) * SS, 116 * SS,
                   part_count, sector)
        text(draw, (cx, y + 250), NAMES[code].upper(), font("arialbd.ttf", 22), INK, anchor="ma")

    # Furniture last and on top. A heading in its own band above the art is a caption; a headline
    # lying across the art is a thumbnail.
    art = image.convert("RGBA")
    tn.band(art, 0, 150 * SS, (0, 0, 0, 150), soft=True)
    tn.punch(art, (pad * SS + 12 * SS, 74 * SS), heading, 100 * SS // 1, tn.YELLOW,
             rotate=-1.5, anchor="lm")
    tn.band(art, (H - 58) * SS, 58 * SS, (0, 0, 0, 200))
    tn.punch(art, (W * SS // 2, (H - 29) * SS), kicker, 30, tn.WHITE, stroke=4 * SS // 2,
             font=tn.black_sans(30 * SS // 2))

    tn.starburst(art, ((W - 118) * SS, 108 * SS), 96 * SS)
    tn.punch(art, ((W - 118) * SS, 108 * SS), badge, 62 * SS // 1, tn.WHITE, stroke=7 * SS // 2)

    finish(art.convert("RGB"), W, H, os.path.join(OUT, filename))


def quests_sheet(part_count, sector, filename, heading, note, badge):
    data = chains()
    widest = max(len(steps) for _title, steps in data)

    # Room for four layers of shape code under the tallest shapes without clipping the card.
    label_w, cell_w, row_h, pad = 250, 268, 262, 40
    W = pad * 2 + label_w + widest * cell_w
    H = 150 + len(data) * row_h + 60

    image = backdrop(W * SS, H * SS)
    draw = ImageDraw.Draw(image)

    title_font = font("arialbd.ttf", 27)
    count_font = font("arial.ttf", 16)
    step_font = font("arialbd.ttf", 16)

    for row, (title, steps) in enumerate(data):
        y = 150 + row * row_h
        # A hairline instead of a card: the rows still need separating, they do not need boxing.
        if row:
            draw.line([(pad * SS, (y - 14) * SS), ((W - pad) * SS, (y - 14) * SS)],
                      fill=(38, 48, 57), width=SS)
        text(draw, (pad + 4, y + row_h / 2 - 34), title, title_font, ACCENT)
        text(draw, (pad + 4, y + row_h / 2 + 2), f"{len(steps)} goals", count_font, MUTED)

        for column, (name, layers) in enumerate(steps):
            code = expand(layers, part_count)
            cx = pad + label_w + column * cell_w + cell_w / 2
            draw_shape(draw, code, cx * SS, (y + 104) * SS, 84 * SS, part_count, sector)
            text(draw, (cx, y + 206), name.upper(), step_font, INK, anchor="ma")

    art = image.convert("RGBA")
    tn.band(art, 0, 150 * SS, (0, 0, 0, 155), soft=True)
    tn.punch(art, (pad * SS + 12 * SS, 76 * SS), heading, 96 * SS, tn.YELLOW,
             rotate=-1.5, anchor="lm")
    tn.starburst(art, ((W - 120) * SS, 110 * SS), 96 * SS, fill=(46, 150, 226))
    tn.punch(art, ((W - 120) * SS, 110 * SS), badge, 58 * SS, tn.WHITE, stroke=10)

    tn.band(art, (H - 60) * SS, 60 * SS, (0, 0, 0, 205))
    tn.punch(art, (W * SS // 2, (H - 30) * SS), note, 15 * SS, tn.WHITE, stroke=7,
             font=tn.black_sans(15 * SS))

    finish(art.convert("RGB"), W, H, os.path.join(OUT, filename))


if __name__ == "__main__":
    if not os.path.isdir(rd.DUMP):
        raise SystemExit(f"No dump at {rd.DUMP}. Run esp.dump in the in-game console first.")

    parts_sheet(4, 90.0, "promo-parts-quad.png", "EVERY NEW PART!",
                "GEAR  CROSS  BAR  DIAMOND  DOT  DOME  WEDGE  SAWBLADE  FLOWER  LEAF", "10")
    parts_sheet(6, 60.0, "promo-parts-hex.png", "ALL TEN IN HEX!",
                "ITS OWN MESH - NOT THE QUAD SHAPE STRETCHED", "60\u00b0")
    quests_sheet(4, 90.0, "promo-quests-quad.png", "38 NEW QUESTS!",
                 "TEN CHAINS - EVERY STEP ADDS ONE THING TO THE FACTORY BEFORE IT", "38")
    quests_sheet(6, 60.0, "promo-quests-hex.png", "QUESTS IN HEX TOO!",
                 "REBUILT FOR SIX PARTS - DOWN TO THE VANILLA SHAPES UNDERNEATH", "38")
