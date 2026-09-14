"""Renders shapez 2 shape codes to PNG, using the same outline maths ExtraShapeParts extrudes.

Not screenshots: the game shades an extruded mesh and these are flat fills. The geometry, the
quadrant placement, the inner gap and the per-layer scale are all the real ones, read out of
ShapeItemRenderer. The colours are approximations - the palette is authored Unity data.
"""

import math
import os
from PIL import Image, ImageDraw, ImageFont

# --- the authoring space, from ShapeItemRenderer -------------------------------------------------

R = 0.37          # reference radius: the divisor the renderer applies to every sub-part mesh
GAP = 0.022       # ShapeInnerGap, and it is NOT scaled per layer
OUTLINE = 0.043   # border width, measured off the vanilla meshes that esp.dump writes
LAYER = 0.76      # 1 - ShapeLayerScaleReduction

# --- palette -------------------------------------------------------------------------------------

BG = (18, 24, 29)
CARD = (26, 34, 40)
RULE = (44, 57, 66)
INK = (230, 237, 240)
MUTED = (135, 154, 164)
ACCENT = (79, 197, 209)
EDGE = (10, 15, 18)

TINT = {
    "u": (170, 182, 189), "r": (232, 80, 58), "g": (91, 181, 74), "b": (59, 143, 224),
    "c": (46, 196, 196), "m": (196, 85, 196), "y": (232, 207, 58), "w": (242, 244, 245),
}

# --- outline helpers -----------------------------------------------------------------------------


def polar(deg, radius):
    a = math.radians(deg)
    return (math.sin(a) * radius, math.cos(a) * radius)


def arc(a0, a1, radius, steps):
    return [polar(a0 + (a1 - a0) * i / steps, radius) for i in range(steps + 1)]


def radial(fn, steps):
    return [polar(90 * i / steps, fn(90 * i / steps)) for i in range(steps + 1)]


def circle_arc(centre, radius, a0, a1, steps):
    out = []
    for i in range(steps + 1):
        a = math.radians(a0 + (a1 - a0) * i / steps)
        out.append((centre[0] + math.cos(a) * radius, centre[1] + math.sin(a) * radius))
    return out


def circle(centre, radius, steps):
    return [(centre[0] + math.cos(2 * math.pi * i / steps) * radius,
             centre[1] + math.sin(2 * math.pi * i / steps) * radius) for i in range(steps)]


def lens(a, b, half_width, steps):
    along = (b[0] - a[0], b[1] - a[1])
    length = math.hypot(*along)
    across = (along[1] / length, -along[0] / length)

    def side(t, width):
        bulge = width * math.sin(t * math.pi) ** 0.75
        return (a[0] + along[0] * t + across[0] * bulge,
                a[1] + along[1] * t + across[1] * bulge)

    points = [a]
    points += [side(i / steps, half_width) for i in range(1, steps)]
    points += [b]
    points += [side(i / steps, -half_width) for i in range(steps - 1, 0, -1)]
    return points


# --- the parts, transcribed from ExtraShapePartCatalog -------------------------------------------

GEAR = (arc(0, 11.25, R * 0.74, 2) + arc(11.25, 33.75, R, 3) + arc(33.75, 56.25, R * 0.74, 3)
        + arc(56.25, 78.75, R, 3) + arc(78.75, 90, R * 0.74, 2) + [(0.0, 0.0)])

PROFILES = {
    "C": arc(0, 90, R, 24) + [(0.0, 0.0)],
    "R": [(0.0, R), (R, R), (R, 0.0), (0.0, 0.0)],
    "E": GEAR,
    "K": [(0.0, R), (R * 0.42, R), (R * 0.42, R * 0.42), (R, R * 0.42), (R, 0.0), (0.0, 0.0)],
    "I": [(R * 0.16, 0.0), (R * 0.16, R * 1.06), (R * 0.50, R * 1.06), (R * 0.50, 0.0)],
    "D": [(R, R * 0.5), (R * 0.5, R), (0.0, R * 0.5), (R * 0.5, 0.0)],
    "O": circle(polar(45, R * 0.72), R * 0.34, 22),
    "M": circle_arc((0.0, R * 0.52), R * 0.52, 90, -90, 20),
    "T": [(0.0, R * 0.46), (R * 1.10, 0.0), (0.0, 0.0)],
    "Z": [polar(0, R * 0.70), polar(28, R), polar(30, R * 0.70), polar(58, R),
          polar(60, R * 0.70), polar(88, R), polar(90, R * 0.70), (0.0, 0.0)],
    "B": radial(lambda d: R * (0.58 + 0.42 * math.sin(math.radians(2 * d))), 28) + [(0.0, 0.0)],
    "L": lens((0.0, 0.0), polar(45, R * 1.26), R * 0.30, 12),
}

NAMES = {
    "E": "Gear", "K": "Cross", "I": "Bar", "D": "Diamond", "O": "Dot",
    "M": "Dome", "T": "Wedge", "Z": "Sawblade", "B": "Flower", "L": "Leaf",
}
ORDER = ["E", "K", "I", "D", "O", "M", "T", "Z", "B", "L"]

# --- outline -> drawable -------------------------------------------------------------------------


def signed_area(loop):
    total = 0.0
    for i in range(len(loop)):
        x1, z1 = loop[i]
        x2, z2 = loop[(i + 1) % len(loop)]
        total += x1 * z2 - x2 * z1
    return total / 2


def clockwise(loop):
    return loop if signed_area(loop) <= 0 else loop[::-1]


def unit(x, z):
    length = math.hypot(x, z)
    return (x / length, z / length) if length > 1e-12 else (0.0, 0.0)


def outset(loop, width):
    """The designed outline grown outwards, with rounded corners - which is what the game does.

    The coloured face is the shape as designed and the black is added outside it, so two
    neighbouring parts each grow across the gap between them and their borders merge into one
    thick line. Corners are rounded because vanilla's are: its square corner grows by 0.042
    against a border width of 0.043, where a mitre would give 0.061.
    """
    n = len(loop)
    points = []
    for k in range(n):
        prev, cur, nxt = loop[(k - 1) % n], loop[k], loop[(k + 1) % n]
        e0 = unit(cur[0] - prev[0], cur[1] - prev[1])
        e1 = unit(nxt[0] - cur[0], nxt[1] - cur[1])
        n0, n1 = (-e0[1], e0[0]), (-e1[1], e1[0])          # outward normals
        turn = e0[0] * e1[1] - e0[1] * e1[0]               # negative is a right turn, i.e. convex

        if turn < -1e-9:
            angle = math.atan2(n0[0] * n1[1] - n0[1] * n1[0], n0[0] * n1[0] + n0[1] * n1[1])
            steps = max(1, min(16, int(math.ceil(abs(math.degrees(angle)) / 18.0))))
            for s in range(steps + 1):
                a = angle * s / steps
                c, si = math.cos(a), math.sin(a)
                d = (n0[0] * c - n0[1] * si, n0[0] * si + n0[1] * c)
                points.append((cur[0] + d[0] * width, cur[1] + d[1] * width))
            continue

        bis = (n0[0] + n1[0], n0[1] + n1[1])
        if bis[0] ** 2 + bis[1] ** 2 < 1e-8:
            points.append((cur[0] + n1[0] * width, cur[1] + n1[1] * width))
            continue
        bis = unit(*bis)
        projection = max(bis[0] * n1[0] + bis[1] * n1[1], 0.34)
        points.append((cur[0] + bis[0] * width / projection, cur[1] + bis[1] * width / projection))

    # Drop points that folded over another part of the outline, which is what happens where a
    # notch is narrower than twice the border. Gear and Flower both do it at 60 degrees.
    return [p for p in points if _distance_to_loop(p, loop) >= width * 0.97]


def _distance_to_loop(p, loop):
    n = len(loop)
    return min(_distance_to_segment(p, loop[i], loop[(i + 1) % n]) for i in range(n))


def _distance_to_segment(p, a, b):
    vx, vz = b[0] - a[0], b[1] - a[1]
    length = vx * vx + vz * vz
    t = 0.0 if length < 1e-18 else max(0.0, min(1.0, ((p[0] - a[0]) * vx + (p[1] - a[1]) * vz) / length))
    return math.hypot(p[0] - (a[0] + vx * t), p[1] - (a[1] + vz * t))


def inset(loop, width):
    """Mitre the loop inwards. Inward normal of an edge (ex, ez) is (ez, -ex) when clockwise."""
    n = len(loop)
    out = []
    for k in range(n):
        prev, cur, nxt = loop[(k - 1) % n], loop[k], loop[(k + 1) % n]
        e0 = unit(cur[0] - prev[0], cur[1] - prev[1])
        e1 = unit(nxt[0] - cur[0], nxt[1] - cur[1])
        n0, n1 = (e0[1], -e0[0]), (e1[1], -e1[0])
        bis = (n0[0] + n1[0], n0[1] + n1[1])
        if bis[0] ** 2 + bis[1] ** 2 < 1e-8:
            out.append((cur[0] + n1[0] * width, cur[1] + n1[1] * width))
            continue
        bis = unit(*bis)
        projection = max(bis[0] * n1[0] + bis[1] * n1[1], 0.34)
        out.append((cur[0] + bis[0] * width / projection, cur[1] + bis[1] * width / projection))
    return out


# Pin, crystal, windmill and star are authored Unity meshes - their outlines are not in the
# assemblies and not knowable from here. Rather than invent geometry for them, they draw as a neutral quarter disc
# so the composition is structurally right and visibly not a claim about their shape.
PLACEHOLDER = "?"
PROFILES[PLACEHOLDER] = arc(0, 90, R * 0.74, 16) + [(0.0, 0.0)]
UNKNOWN = {"P", "c", "W", "S"}
PLACEHOLDER_TINT = (96, 110, 119)

PREPARED = {}
for code, outline in PROFILES.items():
    loop = clockwise(outline)
    PREPARED[code] = (outset(loop, OUTLINE), loop)


def rot(point, quarters):
    """rot90(x, z) = (z, -x) - FastMatrix.QuaternionByRotation[1] is a positive turn about +Y."""
    x, z = point
    for _ in range(quarters):
        x, z = z, -x
    return (x, z)


def draw_shape(draw, code, cx, cy, radius_px):
    """Draws one shape code centred on (cx, cy), sized so radius R lands at radius_px."""
    layers = code.split(":")
    for i, layer in enumerate(layers):
        layer_scale = LAYER ** i
        for q in range(4):
            part, colour = layer[q * 2], layer[q * 2 + 1]
            if part == "-":
                continue
            unknown = part in UNKNOWN or part not in PREPARED
            loop, border = PREPARED[PLACEHOLDER if unknown else part]
            offset = (math.sin(math.radians(45)) * GAP, math.cos(math.radians(45)) * GAP)

            def project(points):
                out = []
                for px, pz in points:
                    x = px * layer_scale + offset[0]
                    z = pz * layer_scale + offset[1]
                    rx, rz = rot((x, z), q)
                    out.append((cx + rx * radius_px / R, cy - rz * radius_px / R))
                return out

            draw.polygon(project(loop), fill=EDGE)
            draw.polygon(project(border),
                         fill=PLACEHOLDER_TINT if unknown else TINT.get(colour, TINT["u"]))


# --- page furniture ------------------------------------------------------------------------------

SS = 3  # supersample: draw big, downsample once with LANCZOS


def font(name, size):
    return ImageFont.truetype(rf"C:\Windows\Fonts\{name}", size * SS)


def text(draw, xy, body, fnt, fill, anchor="la"):
    draw.text((xy[0] * SS, xy[1] * SS), body, font=fnt, fill=fill, anchor=anchor)


def canvas(width, height):
    image = Image.new("RGB", (width * SS, height * SS), BG)
    return image, ImageDraw.Draw(image)


def finish(image, width, height, path):
    image = image.resize((width, height), Image.LANCZOS)
    image.save(path, optimize=True)
    print(f"{os.path.basename(path):24s} {width}x{height}  {os.path.getsize(path) / 1024:.0f} KB")


def card(draw, x, y, w, h):
    draw.rounded_rectangle([x * SS, y * SS, (x + w) * SS, (y + h) * SS],
                           radius=6 * SS, fill=CARD, outline=RULE, width=SS)


# --- the three images ----------------------------------------------------------------------------

OUT = r"C:\Users\Quinn\Documents\Coding\shapez2-mods\Shapez2-Extra-Shape-Parts\Screenshots"
STEAM = r"C:\Users\Quinn\Documents\Coding\shapez2-mods\Shapez2-Extra-Shape-Parts\ExtraShapeParts\Steam"

FEATURED = [
    ("Vortex", "MrMrMrMr:MyMyMyMy:MgMgMgMg:MbMbMbMb"),
    ("Contra", "TrTrTrTr:MwMwMwMw:TbTbTbTb:MwMwMwMw"),
    ("Rose", "BmBmBmBm:BrBrBrBr:ByByByBy"),
    ("Bouquet", "LgLgLgLg:LyLyLyLy:LrLrLrLr"),
]

PATTERNS = [
    ("Vortex", "MrMrMrMr:MyMyMyMy:MgMgMgMg:MbMbMbMb"),
    ("Contra", "TrTrTrTr:MwMwMwMw:TbTbTbTb:MwMwMwMw"),
    ("Bullseye", "CrCrCrCr:CwCwCwCw:CrCrCrCr:OwOwOwOw"),
    ("Gear tower", "EuEuEuEu:EyEyEyEy:ErErErEr"),
    ("Clockwork", "EuEuEuEu:ZrZrZrZr:EwEwEwEw"),
    ("Rose", "BmBmBmBm:BrBrBrBr:ByByByBy"),
    ("Bouquet", "LgLgLgLg:LyLyLyLy:LrLrLrLr"),
    ("Snowflake", "LwLwLwLw:KcKcKcKc:OwOwOwOw"),
    ("Orbit", "OcOcOcOc:OmOmOmOm:OyOyOyOy"),
    ("Turbine", "IuIuIuIu:IwIwIwIw:IuIuIuIu"),
    ("Counter-rotation", "MbMbMbMb:TyTyTyTy"),
    ("Buzzsaw", "ZuZuZuZu:OrOrOrOr"),
    ("Crown", "ZuZuZuZu:IyIyIyIy"),
    ("Compass", "LgLgLgLg:KwKwKwKw"),
    ("Circuitry", "IcIcIcIc:KmKmKmKm"),
    ("Eclipse", "CwCwCwCw:OmOmOmOm"),
    ("Facet", "DwDwDwDw:OmOmOmOm"),
    ("Harlequin", "DrDwDrDw"),
    ("Half moon", "MwMbMwMb"),
    ("Prism", "ErKgDbMy"),
    ("Quartet", "EuKuDuMu"),
    ("Fan", "MuMu----"),
    ("All ten", "EuKuDuMu:TuZuBuLu"),
    ("Old and new", "CuRuCuRu:EuDuMuLu"),
]


def make_preview():
    W = H = 1024
    image, draw = canvas(W, H)

    title = font("arialbd.ttf", 54)
    sub = font("arial.ttf", 23)
    label = font("arialbd.ttf", 21)

    text(draw, (W / 2, 66), "EXTRA SHAPE PARTS", title, INK, anchor="ma")
    text(draw, (W / 2, 136), "Ten new quadrant types for shapez 2", sub, ACCENT, anchor="ma")

    for i, (name, code) in enumerate(FEATURED):
        col, row = i % 2, i // 2
        cx = 268 + col * 488
        cy = 330 + row * 350
        draw_shape(draw, code, cx * SS, cy * SS, 148 * SS)
        text(draw, (cx, cy + 178), name.upper(), label, MUTED, anchor="ma")

    text(draw, (W / 2, H - 54), "circle  square  windmill  star  +  gear cross bar diamond dot "
                                "dome wedge sawblade flower leaf", sub, MUTED, anchor="ma")

    finish(image, W, H, os.path.join(STEAM, "preview.png"))
    finish(image, W, H, os.path.join(OUT, "preview.png"))


def make_patterns():
    cols, rows = 6, 4
    cell_w, cell_h = 264, 300
    pad = 34
    W = pad * 2 + cols * cell_w
    H = 150 + rows * cell_h + pad

    image, draw = canvas(W, H)
    title = font("arialbd.ttf", 44)
    sub = font("arial.ttf", 21)
    label = font("arialbd.ttf", 19)
    mono = font("consola.ttf", 15)

    text(draw, (pad, 46), "Patterns", title, INK)
    text(draw, (pad, 100), "Every code below is buildable. Layers stack at 76% of the one beneath, "
                           "so the detail steps inwards.", sub, MUTED)

    for i, (name, code) in enumerate(PATTERNS):
        col, row = i % cols, i // cols
        x = pad + col * cell_w
        y = 150 + row * cell_h
        card(draw, x + 6, y + 6, cell_w - 12, cell_h - 18)

        cx = x + cell_w / 2
        draw_shape(draw, code, cx * SS, (y + 118) * SS, 92 * SS)
        text(draw, (cx, y + 214), name, label, INK, anchor="ma")

        for j, layer in enumerate(code.split(":")):
            text(draw, (cx, y + 242 + j * 17), layer, mono, MUTED, anchor="ma")

    finish(image, W, H, os.path.join(OUT, "patterns.png"))


def make_parts():
    cols, rows = 5, 2
    cell_w, cell_h = 300, 330
    pad = 34
    W = pad * 2 + cols * cell_w
    H = 150 + rows * cell_h + pad

    image, draw = canvas(W, H)
    title = font("arialbd.ttf", 44)
    sub = font("arial.ttf", 21)
    label = font("arialbd.ttf", 24)
    mono = font("consola.ttf", 17)

    text(draw, (pad, 46), "The ten new parts", title, INK)
    text(draw, (pad, 100), "Each shown as a whole shape of one part. They cut, stack, paint and "
                           "crystallise exactly like the vanilla four.", sub, MUTED)

    for i, part in enumerate(ORDER):
        col, row = i % cols, i // cols
        x = pad + col * cell_w
        y = 150 + row * cell_h
        card(draw, x + 8, y + 8, cell_w - 16, cell_h - 22)

        cx = x + cell_w / 2
        draw_shape(draw, (part + "u") * 4, cx * SS, (y + 132) * SS, 104 * SS)
        text(draw, (cx, y + 240), NAMES[part], label, INK, anchor="ma")
        text(draw, (cx, y + 275), f"{part}u{part}u{part}u{part}u", mono, ACCENT, anchor="ma")

    finish(image, W, H, os.path.join(OUT, "parts.png"))


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    make_preview()
    make_patterns()
    make_parts()
