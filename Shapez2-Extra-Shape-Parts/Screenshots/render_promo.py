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

def parts_sheet(part_count, sector, filename, heading, note):
    cols, rows = 5, 2
    cell_w, cell_h, pad = 300, 330, 34
    W = pad * 2 + cols * cell_w
    H = 160 + rows * cell_h + pad

    image, draw = canvas(W, H)
    text(draw, (pad, 48), heading, font("arialbd.ttf", 46), INK)
    text(draw, (pad, 104), note, font("arial.ttf", 21), MUTED)

    for i, code in enumerate(ORDER):
        col, row = i % cols, i // cols
        x, y = pad + col * cell_w, 160 + row * cell_h
        card(draw, x + 8, y + 8, cell_w - 16, cell_h - 22)

        cx = x + cell_w / 2
        colour = PART_COLOUR[code]
        draw_shape(draw, (code + colour) * part_count, cx * SS, (y + 132) * SS, 104 * SS,
                   part_count, sector)
        text(draw, (cx, y + 242), NAMES[code], font("arialbd.ttf", 26), INK, anchor="ma")
        text(draw, (cx, y + 278), (code + colour) * part_count, font("consola.ttf", 15),
             ACCENT, anchor="ma")

    finish(image, W, H, os.path.join(OUT, filename))


def quests_sheet(part_count, sector, filename, heading, note):
    data = chains()
    widest = max(len(steps) for _title, steps in data)

    # Room for four layers of shape code under the tallest shapes without clipping the card.
    label_w, cell_w, row_h, pad = 210, 258, 292, 34
    W = pad * 2 + label_w + widest * cell_w
    H = 158 + len(data) * row_h + pad

    image, draw = canvas(W, H)
    text(draw, (pad, 48), heading, font("arialbd.ttf", 46), INK)
    text(draw, (pad, 104), note, font("arial.ttf", 21), MUTED)

    title_font = font("arialbd.ttf", 24)
    step_font = font("arialbd.ttf", 17)
    mono = font("consola.ttf", 12)

    for row, (title, steps) in enumerate(data):
        y = 158 + row * row_h
        card(draw, pad, y, W - pad * 2, row_h - 16)
        text(draw, (pad + 22, y + row_h / 2 - 22), title, title_font, ACCENT)
        text(draw, (pad + 22, y + row_h / 2 + 8), f"{len(steps)} goals", mono, MUTED)

        for column, (name, layers) in enumerate(steps):
            code = expand(layers, part_count)
            cx = pad + label_w + column * cell_w + cell_w / 2
            draw_shape(draw, code, cx * SS, (y + 96) * SS, 66 * SS, part_count, sector)
            text(draw, (cx, y + 176), name, step_font, INK, anchor="ma")

            for line, layer in enumerate(code.split(":")):
                text(draw, (cx, y + 202 + line * 14), layer, mono, MUTED, anchor="ma")

    finish(image, W, H, os.path.join(OUT, filename))


if __name__ == "__main__":
    if not os.path.isdir(rd.DUMP):
        raise SystemExit(f"No dump at {rd.DUMP}. Run esp.dump in the in-game console first.")

    parts_sheet(4, 90.0, "promo-parts-quad.png", "Ten new shape parts",
                "Circle, square, windmill and star, plus these. They cut, stack, paint and "
                "crystallise like any other shape.")
    parts_sheet(6, 60.0, "promo-parts-hex.png", "Ten new shape parts, hexagonal",
                "Every part is built again at 60 degrees for hexagonal mode - its own mesh, not "
                "the quad one stretched.")
    quests_sheet(4, 90.0, "promo-quests-quad.png", "Seven side quest chains",
                 "Twenty-six goals in the research screen. Every step adds one thing to the factory "
                 "that built the step before it.")
    quests_sheet(6, 60.0, "promo-quests-hex.png", "Seven side quest chains, hexagonal",
                 "The same chains, rebuilt for six parts - including the vanilla shapes underneath, "
                 "which differ per configuration.")
