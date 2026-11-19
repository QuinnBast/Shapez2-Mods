#!/usr/bin/env python3
"""Builds the Workshop item page images from the in-game captures.

    python Steam/make-shots.py

Sources are ExtraBuildLayers/Screenshots/*.png. Re-run whenever they are replaced: the captions
and crops live here, the pictures do not.

Same layout as the other mods' promo images (adapted from Decoration Blocks' make-promo.py), so
they sit together on one Workshop page: 1280x720, a small letterspaced brow over a large
headline, a dark scrim behind the text. The accent is the orange of this mod's preview image.

Each capture is cropped by an explicit box rather than a centre: the useful part is never in the
middle, and the game's toolbar and key hints along the bottom have to go. A capture whose useful
part is far from 16:9 - the two tall shots, and the long belt once its toolbar is cut away - is
set into the frame at its own aspect on a blurred copy of itself instead of being cropped to fit.
"""

import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
SHOTS = os.path.join(os.path.dirname(HERE), "Screenshots")

WIDTH, HEIGHT = 1280, 720
ACCENT = (255, 160, 40)
INK = (245, 247, 252)
BROW = "EXTRA BUILD LAYERS"

BLACK_FONT = r"C:\Windows\Fonts\seguibl.ttf"   # Segoe UI Black
SEMI_FONT = r"C:\Windows\Fonts\seguisb.ttf"    # Segoe UI Semibold


def font(path, size):
    try:
        return ImageFont.truetype(path, size)
    except OSError:
        return ImageFont.load_default()


def tracked(draw, xy, text, face, fill, tracking):
    x, y = xy
    for char in text:
        draw.text((x, y), char, font=face, fill=fill)
        x += draw.textlength(char, font=face) + tracking


def scrim(image, solid_at, clear_at):
    """Dark at `solid_at` and beyond, fading out by `clear_at`; either edge may be the higher."""
    width, height = image.size
    layer = Image.new("L", (1, height), 0)
    run = float(clear_at - solid_at)
    for y in range(height):
        t = min(1.0, max(0.0, (y - solid_at) / run))
        layer.putpixel((0, y), int(255 * 0.93 * ((1.0 - t) ** 0.85)))
    image.paste(Image.new("RGB", (width, height), (8, 10, 22)), (0, 0), layer.resize((width, height)))


def caption(image, headline, top):
    draw = ImageDraw.Draw(image)
    tracked(draw, (60, top), BROW, font(SEMI_FONT, 19), ACCENT, 3.4)
    draw.rectangle([60, top + 32, 148, top + 35], fill=ACCENT)
    head = font(BLACK_FONT, 54)
    y = top + 48
    for line in headline.split("\n"):
        draw.text((62, y + 3), line, font=head, fill=(0, 0, 0))
        draw.text((60, y), line, font=head, fill=INK)
        y += 62


def save(image, out):
    path = os.path.join(HERE, out)
    image.save(path, optimize=True)
    print("  wrote   %-34s %.0f KB" % (out, os.path.getsize(path) / 1024))


def build(source, box, headline, out, place="bottom"):
    """A capture cropped to `box` (which should be close to 16:9) and filled to the frame."""
    shot = Image.open(os.path.join(SHOTS, source)).convert("RGB").crop(box)
    image = shot.resize((WIDTH, HEIGHT), Image.LANCZOS)

    lines = headline.count("\n") + 1
    block = 48 + lines * 62 + 8
    if place == "top":
        scrim(image, 52 + block + 10, 52 + block + 230)
        caption(image, headline, 52)
    else:
        top = HEIGHT - 52 - block
        scrim(image, top - 14, top - 234)
        caption(image, headline, top)

    save(image, out)


def build_panel(source, box, headline, out):
    """A capture cropped to `box` and set into the frame at its own aspect, under the caption."""
    shot = Image.open(os.path.join(SHOTS, source)).convert("RGB").crop(box)

    cover = max(WIDTH / shot.width, HEIGHT / shot.height) * 1.6
    ground = shot.resize((int(shot.width * cover), int(shot.height * cover)), Image.LANCZOS)
    left = (ground.width - WIDTH) // 2
    top = (ground.height - HEIGHT) // 2
    ground = ground.crop((left, top, left + WIDTH, top + HEIGHT)).filter(ImageFilter.GaussianBlur(38))
    ground = Image.blend(ground, Image.new("RGB", (WIDTH, HEIGHT), (10, 12, 26)), 0.62)

    caption_bottom = 52 + 48 + 62 + 8
    room_h = HEIGHT - caption_bottom - 36
    room_w = WIDTH - 120
    scale = min(room_w / shot.width, room_h / shot.height)
    pw, ph = int(shot.width * scale), int(shot.height * scale)
    panel = shot.resize((pw, ph), Image.LANCZOS)
    px = (WIDTH - pw) // 2
    py = caption_bottom + (HEIGHT - caption_bottom - ph) // 2

    scrim(ground, caption_bottom + 10, caption_bottom + 230)
    shadow = Image.new("L", (WIDTH, HEIGHT), 0)
    ImageDraw.Draw(shadow).rectangle([px + 6, py + 10, px + pw - 6, py + ph + 10], fill=150)
    ground.paste(Image.new("RGB", (WIDTH, HEIGHT), (4, 5, 12)), (0, 0), shadow.filter(ImageFilter.GaussianBlur(18)))
    ground.paste(panel, (px, py))

    caption(ground, headline, 52)
    save(ground, out)


def main():
    # Boxes are (left, top, right, bottom) in the capture's own pixels, chosen to drop the
    # toolbar, the key hints and the version stamp.
    build("UnfathomableMachines.png", (180, 80, 1602, 880),
          "Up to 20 machine layers", "promo-machine-layers.png")
    build("19MachineLayers.png", (253, 0, 1480, 690),
          "Build all the way\nto floor 19", "promo-floor-19.png", place="top")
    build_panel("1000SpaceLayers.png", (20, 60, 1530, 630),
                "Up to 1000 space layers", "promo-space-layers.png")
    build("OptimizedSpaceElevators.png", (0, 0, 1258, 706),
          "Lifts to any height", "promo-lifts.png", place="top")
    build_panel("MassSpaceProduction.png", (0, 12, 900, 820),
                "Stack your factory to the sky", "promo-mega-platform.png")
    build_panel("UnreachableHeights.png", (0, 40, 828, 880),
                "Unreachable heights", "promo-heights.png")


if __name__ == "__main__":
    main()
