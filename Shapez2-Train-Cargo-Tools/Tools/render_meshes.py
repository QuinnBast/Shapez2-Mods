#!/usr/bin/env python3
"""Renders the generated .obj files offline, for store art and for checking a change by eye.

    python Tools/render_meshes.py CargoPackager CargoTrack        # writes Tools/out/*.png

Why render rather than screenshot: the meshes change faster than anybody wants to take
captures, and a stale screenshot is worse than none - the Steam page showed grey machines with
a see-through hopper for two days after both were fixed. This reads the same .obj files the
game loads, so it cannot drift from what ships.

**Colour comes from the role sentinels the generator writes.** Every vertex carries a
per-role marker UV (see PALETTE in generate_meshes.py), so the renderer knows which triangles
are hull, which are frame, which are warn, and colours them the way CargoPalette does at
runtime. The figures in ROLE_COLOURS are sampled from real in-game captures in Screenshots/ -
the platform-edge orange is rgb(189, 111, 1) out of cargo-line.png, the machine body grey is
rgb(112, 100, 97) out of cargo-stores.png - rather than picked to look nice here.

It is a likeness, not the game's renderer: no metal, noise or scratch passes, and the accent
slots resolve to live palette entries that only exist at runtime. Close enough to judge
silhouette, colour separation and whether a part is inside out.
"""

import math
import os
import sys

from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
MESHES = os.path.join(ROOT, "TrainCargoTools", "Resources")

# Matches ROLES in generate_meshes.py. Sentinel u is (index + 1) / 100 at v = 0.01.
ROLES = [
    "hull", "accent", "metal", "fluid", "cargo",
    "hullDark", "deck", "frame", "rail", "trim",
    "warn", "glass", "collar", "shadow", "pale",
]

# Sampled from Screenshots/, then assigned per role the way the slot table does.
ROLE_COLOURS = {
    "hull": (168, 160, 152),
    "accent": (226, 142, 30),      # the platform-edge orange, lifted out of shadow
    "metal": (146, 144, 146),
    "fluid": (128, 148, 170),
    "cargo": (178, 168, 148),
    "hullDark": (128, 118, 114),   # the machine body grey the captures sit at
    "deck": (176, 168, 160),
    "frame": (96, 92, 96),
    "rail": (206, 202, 198),
    "trim": (184, 158, 116),
    "warn": (238, 186, 52),
    "glass": (126, 202, 212),
    "collar": (132, 126, 122),
    "shadow": (66, 62, 66),
    "pale": (232, 228, 222),
}

MISSING = (200, 60, 200)           # loud on purpose: a role that did not resolve

SS = 3                             # supersample factor

# The game looks down at its platforms from a high three-quarter angle.
YAW = math.radians(38)
PITCH = math.radians(31)

KEY = (0.42, 0.80, -0.43)          # sun, up and behind the camera's left
FILL = (-0.55, 0.30, 0.45)


def role_of(u, v):
    if abs(v - 0.01) > 0.004:
        return None
    index = int(round(u * 100)) - 1
    return ROLES[index] if 0 <= index < len(ROLES) else None


def load(name):
    """Triangles as (a, b, c, role), in mesh space."""
    path = name if name.endswith(".obj") else os.path.join(MESHES, name + ".obj")
    verts, uvs, tris = [], [], []

    for line in open(path):
        if line.startswith("v "):
            verts.append(tuple(float(t) for t in line.split()[1:4]))
        elif line.startswith("vt "):
            parts = line.split()
            uvs.append(role_of(float(parts[1]), float(parts[2])))
        elif line.startswith("f "):
            corner = [t.split("/") for t in line.split()[1:4]]
            role = None
            for c in corner:
                if len(c) > 1 and c[1]:
                    role = uvs[int(c[1]) - 1]
                    break
            tris.append(tuple(verts[int(c[0]) - 1] for c in corner) + (role,))

    return tris


def place(tris, dx=0.0, dz=0.0, dy=0.0):
    """Moves a mesh, so several can share one scene."""
    return [tuple((v[0] + dx, v[1] + dy, v[2] + dz) for v in t[:3]) + (t[3],) for t in tris]


def _normal(t):
    a, b, c = t[:3]
    u = [b[i] - a[i] for i in range(3)]
    v = [c[i] - a[i] for i in range(3)]
    n = [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]]
    m = math.sqrt(sum(k * k for k in n)) or 1.0
    return [k / m for k in n]


def _dot(a, b):
    return sum(a[i] * b[i] for i in range(3))


def _shade(colour, n):
    """Flat per face, which is what the game's hard-surface art looks like anyway."""
    key = max(0.0, _dot(n, KEY))
    fill = max(0.0, _dot(n, FILL))
    up = max(0.0, n[1])

    # Heavy ambient, light key. The game lights its platforms with far more bounce than a
    # single sun: sampled off the captures, a face turned away from the light still sits at
    # luma 0.39 against 0.53 for one facing it, which is a ratio near 1.35 rather than the 3:1
    # a hard key gives. The first pass used 0.30 ambient and rendered the machines nearly
    # black - accurate to the palette, nothing like the game.
    light = 0.62 + 0.46 * key + 0.12 * fill

    # A cool sky bounce on upward faces and a warm one off the deck, so that large flat areas
    # are not one dead grey - the single thing that separates a render from a diagram.
    r = colour[0] * light + 12 * up + 6 * fill
    g = colour[1] * light + 14 * up + 5 * fill
    b = colour[2] * light + 24 * up + 16 * fill

    return tuple(int(max(0, min(255, v))) for v in (r, g, b))


def render(tris, width=900, height=700, scale=None, margin=0.86, ground=True,
           bias=0.0, sky=((66, 52, 98), (30, 24, 50))):
    """One scene, supersampled, on the game's violet backdrop."""
    w, h = width * SS, height * SS
    img = Image.new("RGB", (w, h), sky[1])
    d = ImageDraw.Draw(img)

    top, bottom = sky
    for y in range(h):
        t = y / float(h - 1)
        d.line([(0, y), (w, y)],
               fill=tuple(int(top[i] + (bottom[i] - top[i]) * (t ** 0.75)) for i in range(3)))

    cy, sy = math.cos(YAW), math.sin(YAW)
    cp, sp = math.cos(PITCH), math.sin(PITCH)

    def camera(p):
        x, y, z = p
        x, z = x * cy - z * sy, x * sy + z * cy
        y, z = y * cp - z * sp, y * sp + z * cp
        return x, y, z

    seen = [tuple(camera(v) for v in t[:3]) + (t[3],) for t in tris]

    xs = [v[0] for t in seen for v in t[:3]]
    ys = [v[1] for t in seen for v in t[:3]]
    if scale is None:
        scale = margin * min(w / (max(xs) - min(xs) + 1e-6), h / (max(ys) - min(ys) + 1e-6))

    # `bias` moves the subject up or down the frame as a fraction of its height, so a caption
    # block along one edge does not land on top of the thing it is captioning.
    ox = w / 2 - (max(xs) + min(xs)) / 2 * scale
    oy = h / 2 + (max(ys) + min(ys)) / 2 * scale + bias * h

    def flat(v):
        return (ox + v[0] * scale, oy - v[1] * scale)

    # A soft contact shadow, drawn from the silhouette flattened onto the deck. Without it
    # everything floats.
    if ground:
        shadow = Image.new("L", (w, h), 0)
        sd = ImageDraw.Draw(shadow)
        for t in tris:
            pts = [flat(camera((v[0], 0.0, v[2]))) for v in t[:3]]
            sd.polygon(pts, fill=70)
        shadow = shadow.filter(ImageFilter.GaussianBlur(14 * SS))
        img.paste(Image.new("RGB", (w, h), (16, 12, 28)), (0, 0), shadow)
        d = ImageDraw.Draw(img)

    order = []
    for t, cam in zip(tris, seen):
        n = _normal(t)
        depth = sum(v[2] for v in cam[:3]) / 3.0
        order.append((depth, cam, n, t[3]))
    order.sort(key=lambda o: o[0])            # painter's algorithm, far first

    view = (0.0, 0.0, -1.0)
    for _, cam, n, role in order:
        cn = _normal(cam)
        if _dot(cn, view) <= 0:               # backface, exactly as the game culls
            continue
        colour = ROLE_COLOURS.get(role, MISSING)
        d.polygon([flat(v) for v in cam[:3]], fill=_shade(colour, n))

    return img.resize((width, height), Image.LANCZOS)


if __name__ == "__main__":
    out = os.path.join(HERE, "out")
    os.makedirs(out, exist_ok=True)

    names = sys.argv[1:] or ["CargoPackager"]
    for name in names:
        path = os.path.join(out, name + ".png")
        render(load(name)).save(path)
        print("wrote", path)
