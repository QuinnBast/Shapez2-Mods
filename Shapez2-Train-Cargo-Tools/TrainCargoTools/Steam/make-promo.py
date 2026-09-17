#!/usr/bin/env python3
"""Builds the Workshop preview and promo images.

    python Steam/make-promo.py

**Rendered from the shipped .obj files, not from screenshots.** Screenshots were the first
approach and they rot: the store page showed grey machines with a see-through hopper for two
days after both were fixed, because the captures were taken before the art work and nobody
retakes five images per change. Tools/render_meshes.py reads the same meshes the game loads
and colours them by the role sentinels the generator writes, so this cannot drift from what
ships - regenerate the meshes, re-run this, and the art is current.

What it is not: the game's own renderer. There are no metal, noise or scratch passes here, and
the accent slots resolve against a live palette that only exists at runtime, so the colours are
a likeness taken off real captures rather than a frame grab. In-game captures are still the
better store images the moment somebody takes fresh ones - drop them in Screenshots/ and point
`CAPTURES` at them.

Two jobs, two designs. **preview.png** is the grid thumbnail, which Steam often draws under
150 pixels wide: one machine, big, square, with the wordmark on the art. **promo-*.png** are
the item page images, where there is room to say what each part of the mod does.
"""

import os
import sys

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))          # the repo folder
sys.path.insert(0, os.path.join(ROOT, "Tools"))

import render_meshes as rm                              # noqa: E402

WIDTH, HEIGHT = 1280, 720
PREVIEW = 640

CHUNK = 20.0                                            # meshes span +-10, so scenes step by 20

AMBER = (232, 150, 40)
INK = (242, 244, 250)
MUTED = (186, 192, 206)

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
    """Darkens the band the text sits in, fading to nothing by `clear_at`."""
    width, height = image.size
    layer = Image.new("L", (1, height), 0)
    run = float(clear_at - solid_at)

    for y in range(height):
        t = min(1.0, max(0.0, (y - solid_at) / run))
        layer.putpixel((0, y), int(255 * 0.94 * ((1.0 - t) ** 0.85)))

    image.paste(Image.new("RGB", (width, height), (10, 8, 20)), (0, 0),
                layer.resize((width, height)))


def row(*names):
    """Several meshes laid out west to east, one chunk apart, as one scene."""
    scene = []
    span = (len(names) - 1) * CHUNK
    for i, name in enumerate(names):
        scene += rm.place(rm.load(name), dx=i * CHUNK - span / 2)
    return scene


# --------------------------------------------------------------------------- the preview


def build_preview(out="preview.png"):
    # One machine, as large as the frame allows. Steam draws this under 150 pixels wide in a
    # grid, so a belt stub either side just costs the subject the room it needs to survive
    # being shrunk.
    image = rm.render(row("CargoPackager"), PREVIEW, PREVIEW, margin=0.96, bias=-0.07)

    scrim(image, PREVIEW - 150, PREVIEW - 330)
    draw = ImageDraw.Draw(image)

    mark = font(BLACK_FONT, 52)
    y = PREVIEW - 176
    for line in ("TRAIN CARGO", "TOOLS"):
        width = tracked_width(draw, line, mark, 2.4)
        tracked(draw, ((PREVIEW - width) / 2, y), line, mark, INK, 2.4)
        y += 58

    tag = font(SEMI_FONT, 20)
    width = tracked_width(draw, "PACK   BELT   STORE", tag, 3.0)
    tracked(draw, ((PREVIEW - width) / 2, y + 14), "PACK   BELT   STORE", tag, AMBER, 3.0)

    image.save(os.path.join(HERE, out), optimize=True)
    print("  wrote   %-26s %.0f KB" % (out, os.path.getsize(os.path.join(HERE, out)) / 1024))

    # Answer "does it survive being small" here rather than after uploading.
    image.resize((96, 96), Image.LANCZOS).save(os.path.join(HERE, "preview-96.png"))


# --------------------------------------------------------------------------- the promos


def build(scene, headline, subline, out, margin=0.80, place="bottom", bias=0.0):
    image = rm.render(scene, WIDTH, HEIGHT, margin=margin, bias=bias)

    head = font(BLACK_FONT, 52)
    sub = font(SEMI_FONT, 24)
    brow = font(SEMI_FONT, 19)

    lines = headline.split("\n")
    LINE, GAP, MARGIN, LEFT = 60, 18, 52, 60
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


# Each image is one part of the mod, and every figure in the copy is one this repo can show
# its working for - see the throughput table in DESIGN.md.
IMAGES = [
    # One subject per image, framed big. Laying three pieces side by side was the first try and
    # the auto-fit then scales the whole row down to fit the width - a junction ends up a ribbon
    # forty pixels tall, which says nothing.
    (lambda: row("CargoPackager", "CargoUnpackager"),
     "Pack it, belt it,\nunpack it there",
     "360 shapes into one container, or 60 fluid. The duct end is always the packed side.",
     "promo-machines.png", 0.78, "bottom", -0.02),

    (lambda: row("CargoTrackSplitTriple"),
     "Drag it like a\nspace belt",
     "About 18x a space belt for shapes. Corners and junctions place themselves.",
     "promo-belts.png", 0.90, "top", 0.10),

    # A flat piece feeding the ramp, so the ramp reads as a climb rather than as a plank. A
    # Lift2 alone is forty units tall against twenty wide and the auto-fit shrinks it to
    # nothing; the one-layer piece is roughly square in projection and fills the frame.
    (lambda: row("CargoTrack", "CargoTrackLift1UpForward"),
     "Climb over what\nis in the way",
     "Lifts of one or two layers, in any direction, chosen for you as you drag.",
     "promo-lifts.png", 0.84, "top", 0.06),

    (lambda: row("CargoStore", "FluidCargoStore"),
     "Buffer 75 containers\nnext to the station",
     "25 on each floor, shapes and fluid in the same store.",
     "promo-stores.png", 0.80, "bottom", -0.02),
]


# The same scenes with no copy on them, for the README. Kept separate from the in-game
# captures rather than replacing them: a real screenshot is the better image the moment
# somebody takes a fresh one, and overwriting them would throw that away.
def build_plain():
    shots = os.path.join(ROOT, "Screenshots")
    os.makedirs(shots, exist_ok=True)

    for scene, _, _, out, margin, _, bias in IMAGES:
        path = os.path.join(shots, out.replace("promo-", "render-"))
        rm.render(scene(), WIDTH, HEIGHT, margin=margin * 0.92, bias=0.0).save(
            path, optimize=True)
        print("  wrote   %-26s %.0f KB"
              % (os.path.relpath(path, ROOT), os.path.getsize(path) / 1024))


if __name__ == "__main__":
    print("rendering promo images into %s" % HERE)

    build_preview()

    for scene, headline, subline, out, margin, place, bias in IMAGES:
        build(scene(), headline, subline, out, margin, place, bias)

    build_plain()
