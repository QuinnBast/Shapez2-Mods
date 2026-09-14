#!/usr/bin/env python3
"""
Builds the Workshop preview and promo images.

    python Steam/make-promo.py

Screenshots live in the mod's root folder, captured at 1920x1080 with the panel open. Re-run
this whenever the panel's look changes: the copy lives here, the pictures do not.

Two different jobs, so two different designs.

**preview.png** is the grid thumbnail, and Steam draws it small - often under 150 pixels wide.
A screenshot scaled to that size is grey mush, so the preview is not a screenshot at all: it is
the mod's own flame-graph mark, drawn large, square, and readable at any size.

**promo-*.png** are the screenshots on the item page, where there is room to read. Each one
crops into a different part of the tool and says what you can do with it. The copy is the point
here: this is for somebody debugging their own mod, so the headline offers them a look at
something, rather than telling them what is wrong with their work.
"""

import colorsys
import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))          # the mod folder

WIDTH, HEIGHT = 1280, 720
PREVIEW = 640

AMBER = (255, 154, 60)
INK = (240, 245, 252)
MUTED = (176, 190, 210)

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


# --------------------------------------------------------------------------- the preview


def frame_colour(index):
    """
    The same golden-angle hue walk the panel uses for flame frames, so the mark is built out of
    the colours somebody will actually see on the page.
    """
    hue = (index * 0.618034) % 1.0
    r, g, b = colorsys.hsv_to_rgb(hue, 0.55, 0.95)
    return int(r * 255), int(g * 255), int(b * 255)


# A small call tree, as fractions of the mark's width. Each row nests inside the one above it,
# because that nesting is the whole idea the icon has to convey at 80 pixels wide.
TREE = [
    [(0.00, 1.00)],
    [(0.00, 0.54), (0.56, 0.87), (0.89, 1.00)],
    [(0.02, 0.30), (0.32, 0.52), (0.58, 0.80)],
    [(0.04, 0.21), (0.34, 0.49), (0.60, 0.72)],
    [(0.06, 0.16), (0.62, 0.70)],
]


def build_preview(out="preview.png"):
    image = Image.new("RGB", (PREVIEW, PREVIEW), (14, 22, 40))
    draw = ImageDraw.Draw(image)

    # The panel's ground: cool at the top, a trace of warmth at the bottom.
    for y in range(PREVIEW):
        t = y / float(PREVIEW - 1)
        draw.line([(0, y), (PREVIEW, y)],
                  fill=(int(14 + 34 * t), int(22 + 14 * t), int(40 + 8 * t)))

    # The warm bloom the game puts under a page's tab row, flattened into a corner glow.
    glow = Image.new("RGB", (PREVIEW, PREVIEW), (0, 0, 0))
    mask = Image.new("L", (PREVIEW, PREVIEW), 0)
    md = ImageDraw.Draw(mask)
    for i in range(26):
        a = int(6 + i * 1.5)
        r = PREVIEW * (0.62 - i * 0.018)
        md.ellipse([PREVIEW * 0.5 - r, PREVIEW * 0.92 - r * 0.5,
                    PREVIEW * 0.5 + r, PREVIEW * 0.92 + r * 0.5], fill=a)
    glow.paste(Image.new("RGB", (PREVIEW, PREVIEW), AMBER), (0, 0))
    image.paste(Image.blend(image, glow, 0.30), (0, 0), mask)

    draw = ImageDraw.Draw(image)

    left, right = 62, PREVIEW - 62
    span = right - left
    row, gap = 58, 9
    top = 112

    for depth, frames in enumerate(TREE):
        y = top + depth * (row + gap)
        for index, (a, b) in enumerate(frames):
            x0 = left + span * a
            x1 = left + span * b
            draw.rounded_rectangle([x0, y, x1 - 5, y + row], radius=9,
                                   fill=frame_colour(depth * 3 + index))

    mark = font(BLACK_FONT, 46)
    width = tracked_width(draw, "MOD PROFILER", mark, 3.0)
    tracked(draw, ((PREVIEW - width) / 2, PREVIEW - 116), "MOD PROFILER", mark, INK, 3.0)

    tag = font(SEMI_FONT, 19)
    width = tracked_width(draw, "FRAMES  MEMORY  FLAME GRAPHS", tag, 2.4)
    tracked(draw, ((PREVIEW - width) / 2, PREVIEW - 56),
            "FRAMES  MEMORY  FLAME GRAPHS", tag, AMBER, 2.4)

    image.save(os.path.join(HERE, out), optimize=True)
    print("  wrote   %-26s %.0f KB" % (out, os.path.getsize(os.path.join(HERE, out)) / 1024))

    # A thumbnail beside it, so the "does this survive being small" question is answered here
    # rather than after uploading.
    image.resize((96, 96), Image.LANCZOS).save(os.path.join(HERE, "preview-96.png"))


# --------------------------------------------------------------------------- the promos


def scrim(image, solid_at, clear_at):
    """
    Darkens the band the text sits in: fully dark at <paramref name="solid_at"/> and everything
    beyond it, fading to nothing by <paramref name="clear_at"/>. Either edge may be the higher
    one, so the same function serves text at the top and text at the bottom.

    Stating both edges rather than an offset and a direction is the second attempt. The first
    took a direction flag and got it backwards for top-placed text, which put the darkest part
    of the ramp at the top of the image and left the headline itself sitting at about 40%.
    """
    layer = Image.new("L", (1, HEIGHT), 0)
    run = float(clear_at - solid_at)

    for y in range(HEIGHT):
        t = min(1.0, max(0.0, (y - solid_at) / run))
        layer.putpixel((0, y), int(255 * 0.965 * ((1.0 - t) ** 0.85)))

    image.paste(Image.new("RGB", (WIDTH, HEIGHT), (6, 9, 18)), (0, 0),
                layer.resize((WIDTH, HEIGHT)))


def focus(image, zoom, centre):
    """
    Crops into the part of the page being talked about. A whole 1920x1080 page scaled to 1280
    makes every number too small to read, which defeats a screenshot whose job is to show that
    the numbers are real.

    **The zoom has a ceiling, and it is low.** The page is already 16:9, so any zoom crops
    horizontally as well - and the panel's content runs nearly the full width, so a 1.4x crop
    sliced the first characters off every row label and the last digits off every number. A
    table with its own labels cut off reads as broken, not as detailed. The page carries about
    180 pixels of dead margin a side, and 1.2 spends all of it.
    """
    zoom = min(zoom, 1.2)

    w, h = image.size
    cw, ch = int(w / zoom), int(h / zoom)

    x = (w - cw) // 2
    y = max(0, min(h - ch, int(centre * h - ch / 2)))

    return image.crop((x, y, x + cw, y + ch))


def build(source, headline, subline, out, zoom=1.0, centre=0.5, place="bottom"):
    path = os.path.join(ROOT, source)

    if not os.path.exists(path):
        print("  skipped %-26s (no %s)" % (out, source))
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

    tracked(draw, (LEFT, top), "MOD PROFILER", brow, AMBER, 3.4)
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


# Four different parts of the tool, four different things you can do with it. The copy invites
# a look; it does not diagnose anybody's mod for them.
IMAGES = [
    ("FlameGraph.PNG",
     "Find un-optimized\nmethod calls",
     "Every call your mod makes, timed and nested under the one that made it.",
     "promo-flame.png", 1.2, 0.24, "bottom"),

    ("HeapPanelWithClasses.PNG",
     "Find out what your\nmod is holding on to",
     "Every object on the managed heap, grouped by the assembly that declares it.",
     "promo-heap.png", 1.2, 0.46, "top"),

    ("Overview.PNG",
     "Keep a minute of\nframe time in view",
     "Worst frame per bucket, plus every counter a release build still feeds.",
     "promo-overview.png", 1.2, 0.22, "bottom"),
]


if __name__ == "__main__":
    print("building promo images into %s" % HERE)

    build_preview()

    for source, headline, subline, out, zoom, centre, place in IMAGES:
        build(source, headline, subline, out, zoom, centre, place)
