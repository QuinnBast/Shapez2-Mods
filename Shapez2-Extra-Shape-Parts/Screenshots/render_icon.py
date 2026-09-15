"""Workshop icon candidates: one shape, full bleed, no text.

Drawn through render_promo, so a candidate may use vanilla parts - the rose has a
crystal in it - and they come from the dumped meshes rather than an approximation.

The store grid shows this at about a hundred pixels, so it has to survive being small. That rules
out everything the other sheets in here do - cards, labels, shape codes, a row of ten parts. One
shape, big, high contrast, and it has to read as *not vanilla* at a glance.

A real capture from the Shape Inspector beats this for anything that wants to look like the game,
because the game extrudes and lights its shapes and this draws flat. An icon is the one place flat
is fine: it is a logo, not a photograph.

    python render_icon.py
"""

import math
import os

from PIL import Image, ImageDraw, ImageFilter

import render_promo as rp
from render import EDGE, R, SS, TINT, canvas, finish, font, text

OUT = os.path.dirname(os.path.abspath(__file__))

# (name, code, part count, sector) - one layer reads biggest, more layers read richer.
CANDIDATES = [
    ("quartet", "ErKgDbMy", 4, 90.0),
    ("gear-tower", "EuEuEuEu:EyEyEyEy:ErErErEr", 4, 90.0),
    ("rose", "BmBmBmBm:BrcrBrcr:ByByByBy", 4, 90.0),
    ("buzzsaw", "ZuZuZuZu:OrOrOrOr", 4, 90.0),
    ("vortex", "MrMrMrMr:MyMyMyMy:MgMgMgMg:MbMbMbMb", 4, 90.0),
    ("hex-quartet", "ErKgDbMyTcZw", 6, 60.0),
]

BACK_OUTER = (10, 14, 18)
BACK_INNER = (32, 44, 56)


def backdrop(size):
    """A soft radial lift behind the shape, so a dark shape still separates from a dark grid."""
    small = 64
    ramp = Image.new("RGB", (small, small), BACK_OUTER)
    pixels = ramp.load()
    for y in range(small):
        for x in range(small):
            d = math.hypot(x - small / 2, y - small / 2) / (small / 2)
            t = max(0.0, 1.0 - d) ** 1.6
            pixels[x, y] = tuple(int(o + (i - o) * t) for o, i in zip(BACK_OUTER, BACK_INNER))
    return ramp.resize((size, size), Image.BICUBIC)


def icon(code, count, sector, size=512, margin=0.08):
    image = backdrop(size * SS)
    draw = ImageDraw.Draw(image)

    # A shadow pass first, offset and blurred, so the shape sits on the backdrop rather than in it.
    #
    # The silhouette is taken by difference rather than drawn as a mask: draw_shape fills with the
    # palette's RGB tuples, which an "L" image will not accept, and the shape's own colours are not
    # known here anyway. Drawing on a colour that cannot occur and asking which pixels moved gives
    # the silhouette whatever the fills were.
    KEY = (255, 0, 255)
    stencil = Image.new("RGB", image.size, KEY)
    rp.draw_shape(ImageDraw.Draw(stencil), code, image.width / 2, image.height / 2,
                  image.width * (0.5 - margin), count, sector)
    mask = Image.eval(Image.merge("L", [
        Image.eval(a, lambda v, k=k: 255 if v != k else 0)
        for a, k in [(stencil.split()[1], 0)]
    ]), lambda v: v)
    shadow = mask.filter(ImageFilter.GaussianBlur(6 * SS))
    shadow = shadow.transform(image.size, Image.AFFINE, (1, 0, 0, 0, 1, -7 * SS))
    image = Image.composite(Image.new("RGB", image.size, (4, 6, 9)), image, shadow)

    draw = ImageDraw.Draw(image)
    rp.draw_shape(draw, code, image.width / 2, image.height / 2,
                  image.width * (0.5 - margin), count, sector)
    return image.resize((size, size), Image.LANCZOS)


def sheet():
    cell, pad = 300, 30
    cols = 3
    rows = (len(CANDIDATES) + cols - 1) // cols
    W = pad * 2 + cols * cell
    H = 110 + rows * (cell + 46) + pad

    image, draw = canvas(W, H)
    text(draw, (pad, 40), "Workshop icon candidates", font("arialbd.ttf", 36), (230, 237, 240))
    text(draw, (pad, 82), "Shown at 300px; the store grid is nearer 100.",
         font("arial.ttf", 17), (135, 154, 164))

    for i, (name, code, count, sector) in enumerate(CANDIDATES):
        col, row = i % cols, i // cols
        x, y = pad + col * cell, 110 + row * (cell + 46)
        art = icon(code, count, sector, size=cell - 20)
        image.paste(art.resize(((cell - 20) * SS, (cell - 20) * SS), Image.LANCZOS),
                    ((x + 10) * SS, y * SS))
        text(draw, ((x + cell / 2), y + cell - 6), name, font("arialbd.ttf", 18),
             (230, 237, 240), anchor="ma")
        # and a thumbnail, at the size that actually decides this
        thumb = art.resize((72 * SS, 72 * SS), Image.LANCZOS)
        image.paste(thumb, ((x + cell - 86) * SS, (y + cell - 96) * SS))

    finish(image, W, H, os.path.join(OUT, "icon-candidates.png"))


def tracked(draw, xy, body, fnt, fill, tracking):
    """PIL has no letter spacing, and a title set solid reads as a word rather than a label."""
    widths = [draw.textlength(ch, font=fnt) for ch in body]
    x = xy[0] - (sum(widths) + tracking * (len(body) - 1)) / 2
    for ch, w in zip(body, widths):
        draw.text((x, xy[1]), ch, font=fnt, fill=fill)
        x += w + tracking


def fit(draw, body, max_width, tracking_em):
    """The largest size at which the line, tracked, still fits the frame.

    Measured rather than chosen: `font()` already multiplies by the supersample, so a size that
    looks sensible written down comes out three times too wide, which is exactly what the first
    version of this did.
    """
    px = 8
    best = font("arialbd.ttf", px)
    while px < 200:
        candidate = font("arialbd.ttf", px + 1)
        track = (px + 1) * tracking_em * SS
        width = sum(draw.textlength(c, font=candidate) for c in body) + track * (len(body) - 1)
        if width > max_width:
            break
        px, best = px + 1, candidate
    return best, px, px * tracking_em * SS


def titled(code, count, sector, lines, size=512, shape_scale=0.60, shape_lift=0.11):
    """The icon with the mod's name on it.

    The shape still has to carry the thumbnail on its own: at store-grid size a seventeen character
    title is a few pixels tall and nobody reads it. The name is for the page, the shape is for the
    grid, so the shape keeps most of the frame.
    """
    big = size * SS
    image = backdrop(big)

    KEY = (255, 0, 255)
    stencil = Image.new("RGB", (big, big), KEY)
    cy = big / 2 - big * shape_lift
    rp.draw_shape(ImageDraw.Draw(stencil), code, big / 2, cy, big * shape_scale / 2, count, sector)
    mask = Image.eval(stencil.split()[1], lambda v: 255 if v != 0 else 0)
    shadow = mask.filter(ImageFilter.GaussianBlur(6 * SS))                  .transform((big, big), Image.AFFINE, (1, 0, 0, 0, 1, -7 * SS))
    image = Image.composite(Image.new("RGB", (big, big), (4, 6, 9)), image, shadow)

    draw = ImageDraw.Draw(image)
    rp.draw_shape(draw, code, big / 2, cy, big * shape_scale / 2, count, sector)

    # A scrim under the title, so it stays legible whatever the shape does behind it.
    scrim = Image.new("L", (big, big), 0)
    top = int(big * 0.68)
    sd = ImageDraw.Draw(scrim)
    for y in range(top, big):
        sd.line([(0, y), (big, y)], fill=int(220 * min(1.0, (y - top) / (big - top) * 2.0)))
    image = Image.composite(Image.new("RGB", (big, big), (9, 12, 16)), image, scrim)

    draw = ImageDraw.Draw(image)
    fitted = [fit(draw, body, big * 0.86, track_em) for body, track_em in lines]
    block = sum(px * SS * 1.18 for _f, px, _t in fitted)
    y = big - big * 0.06 - block

    for (fnt, px, track), (body, _em) in zip(fitted, lines):
        tracked(draw, (big / 2, y), body, fnt, (238, 242, 245), track)
        y += px * SS * 1.18

    return image.resize((size, size), Image.LANCZOS)


# (name, [(line, tracking as a fraction of the size)])
TITLES = [
    ("one-line", [("EXTRA SHAPE PARTS", 0.10)]),
    ("stacked", [("EXTRA", 0.34), ("SHAPE PARTS", 0.06)]),
    ("two-up", [("EXTRA SHAPE", 0.06), ("PARTS", 0.30)]),
]

HERO = ("MrMrMrMr:MyMyMyMy:MgMgMgMg:MbMbMbMb", 4, 90.0)


def title_sheet():
    cell, pad = 300, 30
    W = pad * 2 + len(TITLES) * cell
    H = 110 + cell + 50
    image, draw = canvas(W, H)
    text(draw, (pad, 40), "Titled icon layouts", font("arialbd.ttf", 36), (230, 237, 240))
    text(draw, (pad, 82), "Inset is store-grid size.", font("arial.ttf", 17), (135, 154, 164))
    for i, (name, lines) in enumerate(TITLES):
        x = pad + i * cell
        art = titled(*HERO, lines, size=cell - 20)
        image.paste(art.resize(((cell - 20) * SS, (cell - 20) * SS), Image.LANCZOS),
                    ((x + 10) * SS, 110 * SS))
        # Top right: the title lives along the bottom, so an inset there covers the thing being
        # judged.
        image.paste(art.resize((80 * SS, 80 * SS), Image.LANCZOS),
                    ((x + cell - 96) * SS, (110 + 14) * SS))
        text(draw, (x + cell / 2, 110 + cell - 4), name, font("arialbd.ttf", 18),
             (230, 237, 240), anchor="ma")
    finish(image, W, H, os.path.join(OUT, "icon-title-layouts.png"))


STEAM = os.path.join(OUT, "..", "ExtraShapeParts", "Steam")

# The shipping icon: the vortex, with the name stacked under it. `EXTRA` tracked wide over
# `SHAPE PARTS` set solid reads as a logotype rather than as a caption, and the shape still keeps
# two thirds of the frame - which is the part that has to work at store-grid size.
SHIPPING = [("EXTRA", 0.34), ("SHAPE PARTS", 0.06)]


def preview():
    art = titled(*HERO, SHIPPING, size=1024)
    for path in (os.path.join(OUT, "preview.png"), os.path.join(STEAM, "preview.png")):
        art.save(path, optimize=True)
        print(f"  {os.path.relpath(path, OUT)}  1024x1024  "
              f"{os.path.getsize(path) / 1024:.0f} KB")


if __name__ == "__main__":
    sheet()
    title_sheet()
    preview()
    for name, code, count, sector in CANDIDATES:
        art = icon(code, count, sector, 512)
        art.save(os.path.join(OUT, f"icon-{name}.png"), optimize=True)
        print(f"  icon-{name}.png  512x512")
