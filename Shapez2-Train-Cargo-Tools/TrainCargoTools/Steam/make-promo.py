#!/usr/bin/env python3
"""
Builds the Workshop preview and promo images.

    python Steam/make-promo.py

Sources are the captures in the repo's Screenshots folder. Re-run this whenever they are
replaced: the copy lives here, the pictures do not.

Two different jobs, so two different designs.

**preview.png** is the grid thumbnail, and Steam draws it small - often under 150 pixels wide.
Unlike the Mod Profiler's, which is a drawn mark because a screenshot of a text panel turns to
mush, this mod's subject is a chunk-wide crate on a belt. That survives being shrunk, so the
preview is a square crop of the real thing with the wordmark laid on it rather than a caption
placed above it.

**promo-*.png** are the screenshots on the item page, where there is room to read. Each one
leads with an in-game capture and says what you can do with it.

Two of the four buildings have no capture yet - a junction and a lift - so there is no promo
for either. `build` skips a missing source rather than failing, so adding the file is all it
takes to get the image.
"""

import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))          # the repo folder
SHOTS = os.path.join(ROOT, "Screenshots")

WIDTH, HEIGHT = 1280, 720
PREVIEW = 640

# The orange the game puts on a platform edge and a hotbar selection, which is also what the
# cargo machines are trimmed with.
AMBER = (247, 162, 27)
INK = (240, 245, 252)
MUTED = (182, 192, 208)

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
    Darkens the band the text sits in: fully dark at `solid_at` and everything beyond it, fading
    to nothing by `clear_at`. Either edge may be the higher one, so the same function serves
    text at the top and text at the bottom.
    """
    width, height = image.size
    layer = Image.new("L", (1, height), 0)
    run = float(clear_at - solid_at)

    for y in range(height):
        t = min(1.0, max(0.0, (y - solid_at) / run))
        layer.putpixel((0, y), int(255 * 0.965 * ((1.0 - t) ** 0.85)))

    image.paste(Image.new("RGB", (width, height), (6, 9, 18)), (0, 0),
                layer.resize((width, height)))


def focus(image, zoom, centre):
    """
    Crops into the part of the shot being talked about, keeping the aspect it came with.

    The zoom has a low ceiling on purpose: these captures are already framed on their subject,
    and cropping hard enough to lose the belt either side of a machine loses the thing that
    makes the picture legible - you can no longer tell what is feeding what.
    """
    zoom = min(zoom, 1.5)

    w, h = image.size
    cw, ch = int(w / zoom), int(h / zoom)

    x = (w - cw) // 2
    y = max(0, min(h - ch, int(centre * h - ch / 2)))

    return image.crop((x, y, x + cw, y + ch))


# --------------------------------------------------------------------------- the preview


def build_preview(source="cargo-line.png", centre=0.52, out="preview.png"):
    path = os.path.join(SHOTS, source)

    if not os.path.exists(path):
        print("  skipped %-26s (no Screenshots/%s)" % (out, source))
        return

    image = Image.open(path).convert("RGB")

    # A square crop, taken about the tallest thing worth keeping rather than the middle: these
    # are landscape captures, so a centred square is mostly empty space either side of the line.
    w, h = image.size
    side = min(w, h)
    left = max(0, min(w - side, int(w * 0.46 - side / 2)))
    top = max(0, min(h - side, int(centre * h - side / 2)))

    image = image.crop((left, top, left + side, top + side)).resize(
        (PREVIEW, PREVIEW), Image.LANCZOS)

    scrim(image, PREVIEW - 152, PREVIEW - 330)

    draw = ImageDraw.Draw(image)

    mark = font(BLACK_FONT, 52)
    lines = ["TRAIN CARGO", "TOOLS"]
    y = PREVIEW - 176

    for line in lines:
        width = tracked_width(draw, line, mark, 2.4)
        tracked(draw, ((PREVIEW - width) / 2, y), line, mark, INK, 2.4)
        y += 58

    tag = font(SEMI_FONT, 20)
    width = tracked_width(draw, "PACK   BELT   STORE", tag, 3.0)
    tracked(draw, ((PREVIEW - width) / 2, y + 14), "PACK   BELT   STORE", tag, AMBER, 3.0)

    image.save(os.path.join(HERE, out), optimize=True)
    print("  wrote   %-26s %.0f KB" % (out, os.path.getsize(os.path.join(HERE, out)) / 1024))

    # A thumbnail beside it, so the "does this survive being small" question is answered here
    # rather than after uploading.
    image.resize((96, 96), Image.LANCZOS).save(os.path.join(HERE, "preview-96.png"))


# --------------------------------------------------------------------------- the promos


def build(source, headline, subline, out, zoom=1.0, centre=0.5, place="bottom"):
    path = os.path.join(SHOTS, source)

    if not os.path.exists(path):
        print("  skipped %-26s (no Screenshots/%s)" % (out, source))
        return False

    image = focus(Image.open(path).convert("RGB"), zoom, centre)
    image = image.resize((WIDTH, HEIGHT), Image.LANCZOS)

    head = font(BLACK_FONT, 52)
    sub = font(SEMI_FONT, 24)
    brow = font(SEMI_FONT, 19)

    lines = headline.split("\n")

    LINE, GAP, MARGIN, LEFT = 60, 18, 52, 60
    block = 48 + len(lines) * LINE + GAP + 30

    if place == "top":
        top = MARGIN
        scrim(image, top + block + 10, top + block + 210)
    else:
        top = HEIGHT - MARGIN - block
        scrim(image, top - 14, top - 214)

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
    return True


# The copy leads with what a player can do, and every figure in it is one this repo can show
# its working for - see the throughput table in DESIGN.md.
IMAGES = [
    ("cargo-line.png",
     "Move cargo without\nputting it on a train",
     "Pack 360 shapes into one container, belt it anywhere, unpack it there.",
     "promo-line.png", 1.0, 0.5, "bottom"),

    ("cargo-stores.png",
     "Buffer 75 containers\nnext to the station",
     "25 on each floor, shapes and fluid in the same store.",
     "promo-stores.png", 1.0, 0.5, "top"),

    ("cargo-belt-corners.png",
     "Drag it like a\nspace belt",
     "About 18x a space belt for shapes. Corners, junctions and lifts place themselves.",
     "promo-belts.png", 1.15, 0.42, "top"),
]


if __name__ == "__main__":
    print("building promo images into %s" % HERE)

    build_preview()

    for source, headline, subline, out, zoom, centre, place in IMAGES:
        build(source, headline, subline, out, zoom, centre, place)
