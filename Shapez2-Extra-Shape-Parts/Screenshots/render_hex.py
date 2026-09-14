"""Mocks up the ten Extra Shape Parts in hexagonal mode.

Answers one question: hexagonal mode is `PartCount = 6`, and every outline in
ExtraShapePartCatalog is authored over 0-90 degrees. ShapeItemRenderer places part `j` with a
Y rotation of `j / PartCount * 360` and a *uniform* horizontal scale - there is no angular
squash anywhere in that matrix - so a quadrant mesh in a six-part shape is placed every 60
degrees and overlaps both neighbours by 30.

Three columns per part:

  quad       4 parts, outline over 90 degrees
  one mesh   what the hexagonal configuration drew before this was fixed: the same 90-degree
             outline placed every 60
  own mesh   the part re-authored over 60 degrees, which is what ships now

The third column is the shipping geometry. ExtraShapePartCatalog builds every outline as a
function of its sector angle and ShapePartFactory builds one MetaShapeSubPart per distinct
PartCount, so this file and the mod now describe the same shapes twice - keep them in step.

The re-authoring is not a uniform squash. Radial parts (gear, sawblade, flower, dome) take the
sector angle as a parameter; cell-authored parts (cross, bar, diamond, wedge) are mapped through
the sector's own oblique basis so their straight edges stay straight; the two placed parts (dot,
leaf) keep their shape and move to the new bisector, with clearance scaled by sin(sector/2).

Not screenshots: flat fills, approximated palette. The geometry is the real geometry.
"""

import math
import os

from render import (ACCENT, EDGE, INK, MUTED, SS, TINT, GAP, OUTLINE, R, LAYER, arc, canvas,
                    card, circle, circle_arc, clockwise, finish, font, outset, lens, polar, text)

OUT = os.path.dirname(os.path.abspath(__file__))

# --- the ten outlines, parametric in the sector angle ---------------------------------------------


def cell(sector, u, v):
    """The sector's own oblique basis: `v` along the edge it starts on, `u` along the edge it ends
    on. At sector 90 this is the identity - `cell(90, u, v) == (u * R, v * R)` - which is why the
    quad column below is the shipped geometry and not a re-derivation of it.

    The basis is normalised so the cell's far corner keeps the radius it has in a quadrant. Two
    unit edges 60 degrees apart span a rhombus whose long diagonal is 1.73R, against the quad
    cell's 1.41R, so the raw basis makes every cell-authored part a third too big and pushes it
    into its neighbours - which the first draft of this sheet drew, and it looked like a bug in
    the parts rather than in the map."""
    ex, ez = polar(sector, R)
    fit = math.hypot(R, R) / math.hypot(ex, R + ez)
    return (u * ex * fit, (v * R + u * ez) * fit)


def compress(points, sector):
    """Remap polar angle from 0-90 onto 0-sector, keeping radius. For the parts whose whole point
    is that they exactly fill their sector."""
    out = []
    for x, z in points:
        out.append(polar(math.degrees(math.atan2(x, z)) * sector / 90.0, math.hypot(x, z)))
    return out


def clearance(sector):
    """How much narrower the sector is at a given radius, relative to a quadrant."""
    return math.sin(math.radians(sector / 2)) / math.sin(math.radians(45))


def gear(s):
    root = R * 0.74
    return (arc(0, 0.125 * s, root, 2) + arc(0.125 * s, 0.375 * s, R, 3)
            + arc(0.375 * s, 0.625 * s, root, 3) + arc(0.625 * s, 0.875 * s, R, 3)
            + arc(0.875 * s, s, root, 2) + [(0.0, 0.0)])


def cross(s):
    # The arm holds its angular width rather than its fraction of the cell: six rhombi each missing
    # their outer point still read as six rhombi, which is what vanilla's hexagonal RectHex is.
    a = 0.42 * s / 90.0
    return [cell(s, 0, 1), cell(s, a, 1), cell(s, a, a), cell(s, 1, a), cell(s, 1, 0), (0.0, 0.0)]


def bar(s):
    return [cell(s, 0.16, 0), cell(s, 0.16, 1.06), cell(s, 0.50, 1.06), cell(s, 0.50, 0)]


def diamond(s):
    return [cell(s, 1, 0.5), cell(s, 0.5, 1), cell(s, 0, 0.5), cell(s, 0.5, 0)]


def dot(s):
    return circle(polar(s / 2, R * 0.72), R * 0.34 * clearance(s), 22)


def dome(s):
    span = R * 0.52
    return compress(circle_arc((0.0, span), span, 90, -90, 20), s)


def wedge(s):
    return [cell(s, 0, 0.46), cell(s, 1.10, 0), (0.0, 0.0)]


def sawblade(s):
    root = R * 0.70
    teeth = [(0, root), (28, R), (30, root), (58, R), (60, root), (88, R), (90, root)]
    return [polar(d * s / 90.0, r) for d, r in teeth] + [(0.0, 0.0)]


def flower(s):
    # Below 90 degrees each petal gets two notches, leaving three teeth, to keep it clear of
    # vanilla's FlowerHex - which is this same "one round lobe per sector" idea and cannot be
    # separated from it by tuning the lobe. Scales in from nothing at 90, so quad is untouched.
    tooth = 0.16 * max(0.0, min(1.0, (90.0 - s) / 30.0))
    steps = 64 if tooth > 0 else 28

    def radius(t):
        lobe = 0.58 + 0.42 * math.sin(math.pi * t)
        notch = tooth * (math.exp(-(((t - 0.34) / 0.07) ** 2))
                         + math.exp(-(((t - 0.66) / 0.07) ** 2)))
        return R * (lobe - notch)

    return [polar(s * i / steps, radius(i / steps)) for i in range(steps + 1)] + [(0.0, 0.0)]


def leaf(s):
    return lens((0.0, 0.0), polar(s / 2, R * 1.26), R * 0.30 * clearance(s), 12)


BUILDERS = {"E": gear, "K": cross, "I": bar, "D": diamond, "O": dot,
            "M": dome, "T": wedge, "Z": sawblade, "B": flower, "L": leaf,
            "C": lambda s: arc(0, s, R, 24) + [(0.0, 0.0)]}

NAMES = {"E": "Gear", "K": "Cross", "I": "Bar", "D": "Diamond", "O": "Dot",
         "M": "Dome", "T": "Wedge", "Z": "Sawblade", "B": "Flower", "L": "Leaf"}
ORDER = ["E", "K", "I", "D", "O", "M", "T", "Z", "B", "L"]

PREPARED = {}
for _code, _build in BUILDERS.items():
    for _sector in (90, 60):
        _loop = clockwise(_build(_sector))
        PREPARED[(_code, _sector)] = (outset(_loop, OUTLINE), _loop)


# --- drawing, generalised over PartCount ----------------------------------------------------------


def draw_shape(draw, code, cx, cy, radius_px, count, sector):
    """`code` is the ordinary shapez hash, but with `count` parts per layer instead of four.

    The placement is ShapeItemRenderer.GenerateShapeMesh verbatim: rotate part j by
    j / count * 360, push it along the bisector at (j + 0.5) / count * 360 by ShapeInnerGap,
    scale layer i by 0.76^i. Nothing here depends on the outline's own angular width, which is
    the whole problem being drawn."""
    half = 0.5 * 360.0 / count
    ox, oz = polar(half, GAP)

    for i, layer in enumerate(code.split(":")):
        layer_scale = LAYER ** i
        for j in range(count):
            part, colour = layer[j * 2], layer[j * 2 + 1]
            if part == "-":
                continue
            loop, border = PREPARED[(part, sector)]
            a = math.radians(j * 360.0 / count)
            ca, sa = math.cos(a), math.sin(a)

            def project(points, ca=ca, sa=sa, layer_scale=layer_scale):
                out = []
                for px, pz in points:
                    x, z = px * layer_scale + ox, pz * layer_scale + oz
                    out.append((cx + (x * ca + z * sa) * radius_px / R,
                                cy - (-x * sa + z * ca) * radius_px / R))
                return out

            draw.polygon(project(loop), fill=EDGE)
            draw.polygon(project(border), fill=TINT.get(colour, TINT["u"]))


# --- the sheet ------------------------------------------------------------------------------------

COLUMNS = [("quad", 4, 90), ("one mesh", 6, 90), ("own mesh", 6, 60)]
WARN = (226, 132, 74)


def make_sheet():
    cols, rows = 5, 2
    cell_w, cell_h = 330, 246
    pad = 34
    W = pad * 2 + cols * cell_w
    head = 196
    H = head + rows * cell_h + pad

    image, draw = canvas(W, H)
    title = font("arialbd.ttf", 44)
    sub = font("arial.ttf", 21)
    label = font("arialbd.ttf", 23)
    mono = font("consola.ttf", 15)
    tiny = font("consola.ttf", 14)

    text(draw, (pad, 44), "Hexagonal mode", title, INK)
    text(draw, (pad, 98), "Hexagonal mode is PartCount = 6, and ShapeItemRenderer scales a part "
                          "uniformly - it never squashes one angularly. So one outline cannot "
                          "serve both:", sub, MUTED)
    text(draw, (pad, 126), "a 90 degree quadrant placed every 60 degrees overlaps both its "
                           "neighbours by 30. Each part is now built once per part count, and the "
                           "third column is what ships.", sub, MUTED)
    text(draw, (pad, 162), "quad  4 parts, 90 deg outline      "
                           "one mesh  6 parts, 90 deg outline - the bug this replaced      "
                           "own mesh  6 parts, 60 deg outline - shipping", mono, ACCENT)

    for i, part in enumerate(ORDER):
        col, row = i % cols, i // cols
        x = pad + col * cell_w
        y = head + row * cell_h
        card(draw, x + 8, y + 8, cell_w - 16, cell_h - 20)

        for k, (name, count, sector) in enumerate(COLUMNS):
            sx = x + 62 + k * 103
            draw_shape(draw, (part + "u") * count, sx * SS, (y + 92) * SS, 44 * SS, count, sector)
            text(draw, (sx, y + 146), name, tiny, WARN if k == 1 else MUTED, anchor="ma")

        cx = x + cell_w / 2
        text(draw, (cx, y + 176), NAMES[part], label, INK, anchor="ma")
        text(draw, (cx, y + 208), part + "u x 6", mono, ACCENT, anchor="ma")

    finish(image, W, H, os.path.join(OUT, "hexagonal.png"))


HEX_PATTERNS = [
    ("Vortex", "MrMrMrMrMrMr:MyMyMyMyMyMy:MgMgMgMgMgMg"),
    ("Contra", "TrTrTrTrTrTr:MwMwMwMwMwMw:TbTbTbTbTbTb"),
    ("Rose", "BmBmBmBmBmBm:BrBrBrBrBrBr:ByByByByByBy"),
    ("Gear tower", "EuEuEuEuEuEu:EyEyEyEyEyEy:ErErErErErEr"),
    ("Orbit", "OcOcOcOcOcOc:OmOmOmOmOmOm"),
    ("Turbine", "IuIuIuIuIuIu:IwIwIwIwIwIw"),
    ("Snowflake", "LwLwLwLwLwLw:KcKcKcKcKcKc:OwOwOwOwOwOw"),
    ("Buzzsaw", "ZuZuZuZuZuZu:OrOrOrOrOrOr"),
    ("Harlequin", "DrDwDrDwDrDw"),
    ("Prism", "ErKgDbMyTmZc"),
]


def make_patterns():
    cols, rows = 5, 2
    cell_w, cell_h = 264, 300
    pad = 34
    W = pad * 2 + cols * cell_w
    H = 150 + rows * cell_h + pad

    image, draw = canvas(W, H)
    title = font("arialbd.ttf", 44)
    sub = font("arial.ttf", 21)
    label = font("arialbd.ttf", 19)
    mono = font("consola.ttf", 13)

    text(draw, (pad, 46), "Hexagonal mode", title, INK)
    text(draw, (pad, 100), "Six parts per layer, each outline built over 60 degrees. Every code "
                           "below is buildable in a hexagonal save.", sub, MUTED)

    for i, (name, code) in enumerate(HEX_PATTERNS):
        col, row = i % cols, i // cols
        x = pad + col * cell_w
        y = 150 + row * cell_h
        card(draw, x + 6, y + 6, cell_w - 12, cell_h - 18)

        cx = x + cell_w / 2
        draw_shape(draw, code, cx * SS, (y + 118) * SS, 92 * SS, 6, 60)
        text(draw, (cx, y + 214), name, label, INK, anchor="ma")
        for j, layer in enumerate(code.split(":")):
            text(draw, (cx, y + 242 + j * 16), layer, mono, MUTED, anchor="ma")

    finish(image, W, H, os.path.join(OUT, "hexagonal-patterns.png"))


if __name__ == "__main__":
    make_sheet()
    make_patterns()
