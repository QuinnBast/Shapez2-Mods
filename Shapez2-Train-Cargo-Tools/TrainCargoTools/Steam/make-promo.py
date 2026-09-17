#!/usr/bin/env python3
"""Builds the Workshop preview and promo images from the in-game captures.

    python Steam/make-promo.py

Sources are the screenshots in the repo's Screenshots folder. Re-run this whenever they are
replaced: the copy lives here, the pictures do not.

**These are real captures, and that is the point.** An earlier version rendered the shipped
.obj files offline - Tools/render_meshes.py, still there and still the quickest way to check a
mesh change by eye - because the captures at the time predated the art work. Rendered art is a
likeness: no metal, noise or scratch passes, and the accent colours only resolve against a live
palette at runtime, so it reads as exactly what it is next to the real thing. A screenshot wins
the moment one exists.

Two jobs, two designs. **preview.png** is the grid thumbnail, which Steam often draws under
150 pixels wide: a tight square crop of the freight itself, where the shapes stay legible at
any size, with the wordmark on a narrow strip rather than washed across the art. **promo-*.png**
are the item page images, where there is room to say what each part of the mod does.
"""

import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))          # the repo folder
SHOTS = os.path.join(ROOT, "Screenshots")

WIDTH, HEIGHT = 1280, 720
PREVIEW = 640

AMBER = (244, 158, 36)
INK = (245, 247, 252)
MUTED = (196, 201, 214)

BLACK_FONT = r"C:\Windows\Fonts\seguibl.ttf"           # Segoe UI Black
SEMI_FONT = r"C:\Windows\Fonts\seguisb.ttf"            # Segoe UI Semibold


def font(path, size):
    return ImageFont.truetype(path, size)


def tracked(draw, xy, text, face, fill, tracking):
    """Letterspaced text. PIL has no tracking, and a wordmark without it reads as a caption."""
    x, y = xy
    for char in text:
        draw.text((x, y), char, font=face, fill=fill)
        x += draw.textlength(char, font=face) + tracking
    return x


def tracked_width(draw, text, face, tracking):
    return sum(draw.textlength(c, font=face) + tracking for c in text) - tracking


def scrim(image, solid_at, clear_at):
    """
    Darkens the band the text sits in: fully dark at `solid_at` and everything beyond it,
    fading to nothing by `clear_at`. Either edge may be the higher one, so the same function
    serves text at the top and text at the bottom.
    """
    width, height = image.size
    layer = Image.new("L", (1, height), 0)
    run = float(clear_at - solid_at)

    for y in range(height):
        t = min(1.0, max(0.0, (y - solid_at) / run))
        layer.putpixel((0, y), int(255 * 0.93 * ((1.0 - t) ** 0.85)))

    image.paste(Image.new("RGB", (width, height), (8, 10, 22)), (0, 0),
                layer.resize((width, height)))


def frame(source, aspect, centre=0.5, zoom=1.0):
    """
    Crops a capture to an aspect ratio, keeping as much of it as possible.

    The captures are whatever shape the window was - 938x690 through 1414x776 - so each needs
    its own crop to reach 16:9, and `centre` says which band of the picture to keep rather than
    defaulting to the middle and cutting the subject in half.
    """
    image = Image.open(os.path.join(SHOTS, source)).convert("RGB")
    w, h = image.size

    cw = int(min(w, min(w, int(h * aspect)) / zoom))
    ch = int(min(h, min(h, int(w / aspect)) / zoom))

    x = max(0, min(w - cw, int(w * 0.5 - cw / 2)))
    y = max(0, min(h - ch, int(h * centre - ch / 2)))

    return image.crop((x, y, x + cw, y + ch))


# --------------------------------------------------------------------------- the preview


def build_preview(source="cargo-line-alt.png", centre=0.5, out="preview.png"):
    """The workshop icon: containers, close, over a strip carrying the name."""
    image = frame(source, 1.0, centre).resize((PREVIEW, PREVIEW), Image.LANCZOS)

    # A strip, not a wash. This capture is tight enough that the freight fills the frame, and a
    # scrim deep enough to read a two-line wordmark against would cover the half of it that
    # makes the icon work at 96 pixels.
    strip = 92
    draw = ImageDraw.Draw(image)
    draw.rectangle([0, PREVIEW - strip, PREVIEW, PREVIEW], fill=(12, 14, 26))
    draw.rectangle([0, PREVIEW - strip, PREVIEW, PREVIEW - strip + 3], fill=AMBER)

    mark = font(BLACK_FONT, 40)
    width = tracked_width(draw, "TRAIN CARGO TOOLS", mark, 1.6)
    tracked(draw, ((PREVIEW - width) / 2, PREVIEW - strip + 26),
            "TRAIN CARGO TOOLS", mark, INK, 1.6)

    image.save(os.path.join(HERE, out), optimize=True)
    print("  wrote   %-26s %.0f KB" % (out, os.path.getsize(os.path.join(HERE, out)) / 1024))

    # Answer "does it survive being small" here rather than after uploading.
    image.resize((96, 96), Image.LANCZOS).save(os.path.join(HERE, "preview-96.png"))


# --------------------------------------------------------------------------- the promos


def build(source, headline, subline, out, centre=0.5, zoom=1.0, place="bottom"):
    image = frame(source, WIDTH / float(HEIGHT), centre, zoom).resize(
        (WIDTH, HEIGHT), Image.LANCZOS)

    head = font(BLACK_FONT, 54)
    sub = font(SEMI_FONT, 24)
    brow = font(SEMI_FONT, 19)

    lines = headline.split("\n")
    LINE, GAP, MARGIN, LEFT = 62, 18, 52, 60
    block = 48 + len(lines) * LINE + GAP + 30

    if place == "top":
        top = MARGIN
        scrim(image, top + block + 10, top + block + 230)
    else:
        top = HEIGHT - MARGIN - block
        scrim(image, top - 14, top - 234)

    draw = ImageDraw.Draw(image)

    tracked(draw, (LEFT, top), "TRAIN CARGO TOOLS", brow, AMBER, 3.4)
    draw.rectangle([LEFT, top + 32, LEFT + 88, top + 35], fill=AMBER)

    y = top + 48
    for line in lines:
        draw.text((LEFT + 2, y + 3), line, font=head, fill=(0, 0, 0))
        draw.text((LEFT, y), line, font=head, fill=INK)
        y += LINE

    draw.text((LEFT, y + GAP), subline, font=sub, fill=MUTED)

    image.save(os.path.join(HERE, out), optimize=True)
    print("  wrote   %-26s %.0f KB" % (out, os.path.getsize(os.path.join(HERE, out)) / 1024))


# The headlines are Quinn's. Each subline carries a figure this repo can show its working for -
# see the throughput table in DESIGN.md - so the page says something as well as sells.
#
# Every caption sits at the bottom, which was not the plan - alternating top and bottom reads
# less like a template - but in three of the four captures the machines are in the upper half
# of the shot, and a scrim across the top covered the very thing being captioned. `centre`
# then lifts each subject into the clear band above the text.
IMAGES = [
    ("CargoPackagers.png",
     "Create Cargo yourself!",
     "360 shapes into one container, or 60 fluid. No train required.",
     "promo-packagers.png", 0.38, 1.0, "bottom"),

    ("TransportCargo.png",
     "Move Cargo Anywhere",
     "About 18x a space belt for shapes. Corners, junctions and lifts place themselves.",
     "promo-transport.png", 0.42, 1.0, "bottom"),

    ("CargoStorage.png",
     "Store Excess Cargo!",
     "75 containers per store - 25 on each floor, shapes and fluid in the same rack.",
     "promo-storage.png", 0.40, 1.0, "bottom"),

    ("CargoUnloaders.png",
     "Unpackage on Demand!",
     "Back onto a space belt or pipe, wherever you needed it.",
     "promo-unloaders.png", 0.40, 1.0, "bottom"),
]


if __name__ == "__main__":
    print("building promo images into %s" % HERE)

    build_preview()

    for source, headline, subline, out, centre, zoom, place in IMAGES:
        build(source, headline, subline, out, centre, zoom, place)
