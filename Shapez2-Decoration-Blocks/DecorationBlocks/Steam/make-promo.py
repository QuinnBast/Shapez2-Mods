#!/usr/bin/env python3
"""Builds the Workshop preview and promo images from the in-game captures.

    python Steam/make-promo.py

Sources are the screenshots in the repo's Screenshots folder. Re-run this whenever they are
replaced: the copy lives here, the pictures do not.

Same two jobs and two designs as the cargo mod's version, which this is adapted from.
**preview.png** is the grid thumbnail, which Steam often draws under 150 pixels wide, so it
wants the one capture that still reads as *blocks* at that size - which is the colour-banded
wall, not the prettiest picture. **promo-*.png** are the item page images, where there is room
to show a built room and a working circuit.

Three of the captures are nearly square or far wider than 16:9, and cropping those to the frame
throws away the subject - the gates sheet loses its top and bottom row, the ENJOY platform loses
both ends. Those go through `build_wide`, which sets the capture into the frame at its own
aspect instead of cropping it to fit.
"""

import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))          # the repo folder
SHOTS = os.path.join(ROOT, "Screenshots")

WIDTH, HEIGHT = 1280, 720
PREVIEW = 640

# Sparkstone red rather than the cargo mod's amber. These images sit beside the other mods on
# one author's Workshop page, so the layout is deliberately the same and the accent is not.
SPARK = (214, 58, 40)
INK = (245, 247, 252)

BLACK_FONT = r"C:\Windows\Fonts\seguibl.ttf"           # Segoe UI Black
SEMI_FONT = r"C:\Windows\Fonts\seguisb.ttf"            # Segoe UI Semibold

WORDMARK = "BLOCKWORKS"


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

    The captures are whatever shape the window was - 723x387 through 1630x554 - so each needs
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


def build_preview(source="DecorExample.png", centre=0.45, out="preview.png"):
    """The Workshop icon: the colour-banded wall, over a strip carrying the name.

    This capture wins the thumbnail on legibility rather than looks. `ExampleScene.png` is the
    better picture and would be the obvious pick, but a built room at 96 pixels is a brown
    rectangle, whereas eighteen materials stacked in bands still read as *a set of blocks* when
    the icon is smaller than the text under it. That is the only job this image has.
    """
    image = frame(source, 1.0, centre).resize((PREVIEW, PREVIEW), Image.LANCZOS)

    strip = 92
    draw = ImageDraw.Draw(image)
    draw.rectangle([0, PREVIEW - strip, PREVIEW, PREVIEW], fill=(12, 14, 26))
    draw.rectangle([0, PREVIEW - strip, PREVIEW, PREVIEW - strip + 3], fill=SPARK)

    mark = font(BLACK_FONT, 44)
    width = tracked_width(draw, WORDMARK, mark, 2.2)
    tracked(draw, ((PREVIEW - width) / 2, PREVIEW - strip + 24), WORDMARK, mark, INK, 2.2)

    image.save(os.path.join(HERE, out), optimize=True)
    print("  wrote   %-26s %.0f KB" % (out, os.path.getsize(os.path.join(HERE, out)) / 1024))

    # Answer "does it survive being small" here rather than after uploading.
    image.resize((96, 96), Image.LANCZOS).save(os.path.join(HERE, "preview-96.png"))


# --------------------------------------------------------------------------- the promos


def build(source, headline, out, centre=0.5, zoom=1.0, place="bottom"):
    image = frame(source, WIDTH / float(HEIGHT), centre, zoom).resize(
        (WIDTH, HEIGHT), Image.LANCZOS)

    head = font(BLACK_FONT, 54)
    brow = font(SEMI_FONT, 19)

    lines = headline.split("\n")
    LINE, MARGIN, LEFT = 62, 52, 60
    block = 48 + len(lines) * LINE + 8

    if place == "top":
        top = MARGIN
        scrim(image, top + block + 10, top + block + 230)
    else:
        top = HEIGHT - MARGIN - block
        scrim(image, top - 14, top - 234)

    draw = ImageDraw.Draw(image)

    tracked(draw, (LEFT, top), WORDMARK, brow, SPARK, 3.4)
    draw.rectangle([LEFT, top + 32, LEFT + 88, top + 35], fill=SPARK)

    y = top + 48
    for line in lines:
        draw.text((LEFT + 2, y + 3), line, font=head, fill=(0, 0, 0))
        draw.text((LEFT, y), line, font=head, fill=INK)
        y += LINE

    image.save(os.path.join(HERE, out), optimize=True)
    print("  wrote   %-26s %.0f KB" % (out, os.path.getsize(os.path.join(HERE, out)) / 1024))


def build_wide(source, headline, out, panel_width=1160):
    """A capture that does not fit 16:9, set into the frame rather than cropped to it.

    The gates sheet is 759x763 - square - and the ENJOY platform is 1630x554, nearly three to
    one. Cropping either to 16:9 throws away the part being captioned: two of six gates, or
    both ends of the word. So each is placed at its own aspect and the frame is built around it.

    The ground is the capture itself, blown up, blurred and darkened. It is the one backdrop
    guaranteed to be in the right colours, because it *is* the picture - anything invented here
    would be a guess at the scene's palette sitting directly beside the real thing.
    """
    shot = Image.open(os.path.join(SHOTS, source)).convert("RGB")

    cover = max(WIDTH / shot.width, HEIGHT / shot.height) * 1.6
    ground = shot.resize((int(shot.width * cover), int(shot.height * cover)), Image.LANCZOS)
    left = (ground.width - WIDTH) // 2
    top = (ground.height - HEIGHT) // 2
    ground = ground.crop((left, top, left + WIDTH, top + HEIGHT))
    ground = ground.filter(ImageFilter.GaussianBlur(38))
    ground = Image.blend(ground, Image.new("RGB", (WIDTH, HEIGHT), (10, 12, 26)), 0.62)

    caption_bottom = 52 + 48 + 62 + 8

    # Fit the panel to whatever room is left under the caption, rather than trusting the caller's
    # width. A square capture at panel_width 1160 would be 1160 tall in a 720 frame.
    room = HEIGHT - caption_bottom - 40
    panel_height = int(shot.height * panel_width / shot.width)

    if panel_height > room:
        panel_height = room
        panel_width = int(shot.width * panel_height / shot.height)

    panel = shot.resize((panel_width, panel_height), Image.LANCZOS)

    py = caption_bottom + (HEIGHT - caption_bottom - panel_height) // 2
    px = (WIDTH - panel_width) // 2

    # Scrim first, then the panel on top of it. The cargo mod's version scrims last, which was
    # fine there because its capture was a short strip that sat well below the dark band - here
    # a tall panel reaches up into it and the top row of the picture comes out dimmed, which
    # reads as a badly exposed screenshot rather than as a caption treatment.
    scrim(ground, caption_bottom + 10, caption_bottom + 230)

    shadow = Image.new("L", (WIDTH, HEIGHT), 0)
    ImageDraw.Draw(shadow).rectangle(
        [px + 6, py + 10, px + panel_width - 6, py + panel_height + 10], fill=150)
    ground.paste(Image.new("RGB", (WIDTH, HEIGHT), (4, 5, 12)), (0, 0),
                 shadow.filter(ImageFilter.GaussianBlur(18)))

    ground.paste(panel, (px, py))

    head = font(BLACK_FONT, 54)
    brow = font(SEMI_FONT, 19)

    draw = ImageDraw.Draw(ground)

    tracked(draw, (60, 52), WORDMARK, brow, SPARK, 3.4)
    draw.rectangle([60, 84, 148, 87], fill=SPARK)

    draw.text((62, 103), headline, font=head, fill=(0, 0, 0))
    draw.text((60, 100), headline, font=head, fill=INK)

    ground.save(os.path.join(HERE, out), optimize=True)
    print("  wrote   %-26s %.0f KB" % (out, os.path.getsize(os.path.join(HERE, out)) / 1024))


# Bottom placement throughout, for the same reason as the cargo mod's: in every one of these
# captures the subject sits in the upper half, and a scrim across the top covers the thing being
# captioned. `centre` then lifts each subject into the clear band above the text.
IMAGES = [
    ("ExampleScene.png", "Build somewhere to be",
     "promo-decor.png", 0.42, 1.0, "bottom"),

    ("DecorExample.png", "Twenty-one materials",
     "promo-blocks.png", 0.40, 1.0, "bottom"),

    ("SparkstoneSample.png", "Circuits that really run",
     "promo-sparkstone.png", 0.42, 1.0, "bottom"),
]


if __name__ == "__main__":
    print("building promo images into %s" % HERE)

    build_preview()

    for source, headline, out, centre, zoom, place in IMAGES:
        build(source, headline, out, centre, zoom, place)

    build_wide("SparkstoneGates.png", "Every gate you need", "promo-gates.png")
    build_wide("WireConverter.png", "Bridged to shapez wires", "promo-converter.png")
    build_wide("Enjoy.png", "Make it yours", "promo-enjoy.png")
