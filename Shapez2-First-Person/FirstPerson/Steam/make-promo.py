#!/usr/bin/env python3
"""
Builds the Workshop preview image.

    python Steam/make-promo.py

Steam draws the grid thumbnail small - often under 150 pixels wide - so the preview is not a
screenshot. A screenshot of a factory at that size is grey mush. It is the mod's own mark
instead: the crosshair, drawn large, on the horizon it puts you on.

The mark is a **shape seen through the crosshair**, because a crosshair alone says "first
person shooter" and says nothing about which game. The shape is what carries the colour, and
colour is what wins a row of dark thumbnails; the crosshair is what makes it this mod rather
than shapez itself. Neither half works alone.

The crosshair keeps the proportions `FirstPersonTuning` uses in game - arm 7, gap 3,
thickness 2 - so the frame is the thing the player actually looks through, widened only
enough to hold the shape.

Promo screenshots for the item page are captures rather than drawings, and there is nothing
to generate for them: put 1920x1080 grabs in `Shapez2-First-Person/Screenshots/` and link
them from the README, the way the other mods here do.
"""

import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageEnhance

HERE = os.path.dirname(os.path.abspath(__file__))

SIZE = 640
SS = 4  # supersample; PIL antialiases nothing on its own

SPACE = (14, 18, 28)
HORIZON = (30, 42, 66)
GLOW = (46, 68, 104)
AMBER = (255, 209, 102)
INK = (238, 244, 252)
MUTED = (150, 168, 196)

# The game's own quadrant colours. Four different hues and two different quadrant shapes,
# because a shape that is all discs or all squares reads as a logo rather than as shapez.
SHAPE_RED = (238, 51, 51)
SHAPE_BLUE = (66, 165, 245)
SHAPE_GREEN = (102, 187, 106)
SHAPE_WHITE = (232, 238, 246)

BLACK_FONT = r"C:\Windows\Fonts\seguibl.ttf"   # Segoe UI Black
SEMI_FONT = r"C:\Windows\Fonts\seguisb.ttf"    # Segoe UI Semibold


def font(path, size):
    """Falls back rather than failing: a preview with plain type beats no preview."""
    try:
        return ImageFont.truetype(path, size)
    except OSError:
        return ImageFont.load_default()


def tracked(draw, xy, text, face, fill, tracking):
    """Letterspaced text. PIL has no tracking, and a wordmark without it reads as a caption."""
    x, y = xy
    for char in text:
        draw.text((x, y), char, font=face, fill=fill)
        x += draw.textlength(char, font=face) + tracking
    return x


def tracked_width(draw, text, face, tracking):
    return sum(draw.textlength(c, font=face) + tracking for c in text) - tracking


def shape(draw, cx, cy, r):
    """
    A four-quadrant shapez shape: two discs, two squares, alternating, in four colours.

    Drawn as whole quadrants and then cut with a cross of background, which is how the game
    draws them too - the gap between quadrants is as much a part of the silhouette as the
    quadrants are, and without it the thing reads as a pie chart.
    """
    quadrants = [
        (SHAPE_RED, "square", 180, 270),
        (SHAPE_BLUE, "disc", 270, 360),
        (SHAPE_GREEN, "square", 0, 90),
        (SHAPE_WHITE, "disc", 90, 180),
    ]

    for colour, kind, start, end in quadrants:
        if kind == "disc":
            draw.pieslice([cx - r, cy - r, cx + r, cy + r], start, end, fill=colour)
        else:
            x0 = cx if start in (270, 0) else cx - r
            y0 = cy if start in (0, 90) else cy - r
            draw.rectangle([x0, y0, x0 + r, y0 + r], fill=colour)

    cut = r * 0.075
    draw.rectangle([cx - cut, cy - r * 1.05, cx + cut, cy + r * 1.05], fill=SPACE)
    draw.rectangle([cx - r * 1.05, cy - cut, cx + r * 1.05, cy + cut], fill=SPACE)


# A capture to sit the mark on, and the square region of it to use. The crop avoids the HUD
# on every edge - the top resource bar, the bottom toolbar, and both side rails - because a
# sliver of interface in a thumbnail reads as a mistake rather than as context.
# The dense factory rather than the sky or the rails: blurred past legibility, only a shot
# with several colours in it still reads as anything. A sky crop becomes one flat blue
# square, which is worse than no backdrop at all.
BACKDROP = ("Screenshots/2026_09_22_0zh_Kleki.png", 100, 250, 780)


def backdrop(n, blur=0.024, bright=0.58, colour=0.92, sink=225):
    """
    The capture, blurred and darkened until it is atmosphere rather than a picture.

    Steam draws this at about 150 pixels. Any detail left in the background at that size is
    noise competing with the mark, so the treatment is deliberately brutal: blur it past
    legibility, halve the brightness, pull the saturation back, then sink a vignette into the
    middle so the shape has somewhere dark to sit. What survives is the palette - purple sky,
    orange factory - which is the only part that reads small anyway.
    """
    path = os.path.normpath(os.path.join(HERE, "..", BACKDROP[0]))

    if not os.path.exists(path):
        return None

    x, y, size = BACKDROP[1], BACKDROP[2], BACKDROP[3]
    image = Image.open(path).convert("RGB").crop((x, y, x + size, y + size)).resize((n, n),
                                                                                   Image.LANCZOS)
    if blur > 0:
        image = image.filter(ImageFilter.GaussianBlur(n * blur))

    image = ImageEnhance.Color(image).enhance(colour)
    image = ImageEnhance.Brightness(image).enhance(bright)

    # A radial sink towards the centre, drawn as concentric rings - PIL has no gradient fill.
    vignette = Image.new("L", (n, n), 0)
    ring = ImageDraw.Draw(vignette)
    steps = 120

    for i in range(steps):
        t = i / steps
        r = n * 0.78 * (1.0 - t)
        ring.ellipse([n / 2 - r, n / 2 - r, n / 2 + r, n / 2 + r], fill=int(sink * t ** 1.3))

    image = Image.composite(Image.new("RGB", (n, n), SPACE), image, vignette)
    return image


def build(photo=False, name=None, **treatment):
    n = SIZE * SS
    image = backdrop(n, **treatment) if photo else None

    if image is None:
        image = Image.new("RGB", (n, n), SPACE)

    draw = ImageDraw.Draw(image)

    # A horizon rather than a flat field: it is what first person put on the screen, and it
    # gives the crosshair something to sit against at thumbnail size. Skipped over a capture,
    # which brought its own.
    horizon = int(n * 0.63)
    for y in range(horizon, n):
        t = (y - horizon) / (n - horizon)
        draw.line(
            [(0, y), (n, y)],
            fill=tuple(int(HORIZON[i] + (SPACE[i] - HORIZON[i]) * t) for i in range(3)),
        )

    # A soft bloom just above it, so the band does not read as a hard seam.
    for i in range(int(n * 0.06), 0, -1):
        t = i / (n * 0.06)
        draw.line(
            [(0, horizon - i), (n, horizon - i)],
            fill=tuple(int(SPACE[c] + (GLOW[c] - SPACE[c]) * (1 - t) * 0.5) for c in range(3)),
        )
    draw.line([(0, horizon), (n, horizon)], fill=GLOW, width=max(1, SS))

    if photo:
        image = backdrop(n, **treatment)
        draw = ImageDraw.Draw(image)

    # Sized so the lower arm clears the wordmark: it ends at cy + gap + arm, which with
    # these numbers is 0.72n against a title starting at 0.79n.
    cx, cy = n // 2, int(n * 0.38)
    radius = n * 0.165

    shape(draw, cx, cy, radius)

    # The crosshair, in FirstPersonTuning's proportions: arm 7, gap 3, thickness 2. The gap
    # is widened from 3 units to whatever clears the shape - the ratio of arm to thickness is
    # what makes it read as the game's crosshair, not the size of the hole.
    unit = n * 0.021
    gap = radius + unit * 1.5
    arm = 7 * unit
    half = unit

    for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
        if dx:
            x0, x1 = sorted((cx + dx * gap, cx + dx * (gap + arm)))
            draw.rectangle([x0, cy - half, x1, cy + half], fill=INK)
        else:
            y0, y1 = sorted((cy + dy * gap, cy + dy * (gap + arm)))
            draw.rectangle([cx - half, y0, cx + half, y1], fill=INK)

    out = image.resize((SIZE, SIZE), Image.LANCZOS)
    draw = ImageDraw.Draw(out)

    title = font(BLACK_FONT, 62)
    sub = font(SEMI_FONT, 25)

    text = "FIRST PERSON"
    tracking = 5
    width = tracked_width(draw, text, title, tracking)
    tracked(draw, ((SIZE - width) / 2, SIZE * 0.785), text, title, INK, tracking)

    text = "walk your own factory"
    width = draw.textlength(text, font=sub)
    draw.text(((SIZE - width) / 2, SIZE * 0.90), text, font=sub, fill=MUTED)

    name = name or ("preview-photo.png" if photo else "preview-flat.png")

    path = os.path.join(HERE, name)
    out.save(path)
    print(f"{name}  {out.size[0]}x{out.size[1]}")


if __name__ == "__main__":
    # A capture behind the mark, kept **sharp** and sunk rather than blurred.
    #
    # Blurring it was the obvious treatment and the wrong one: this game's palette averages to
    # grey-brown, so a blurred factory is mud and the mark loses its contrast against a
    # mid-tone. Left sharp and dropped to a third brightness, the same crop still reads as
    # machines and belts at thumbnail size - texture rather than noise - and the vignette
    # keeps the middle dark enough for the shape to sit on.
    # 0.45 rather than 0.32: bright enough that the belts and machines are legible, and
    # still dark enough that the wordmark holds its contrast at thumbnail size. Past about
    # 0.58 the backdrop starts competing with the mark instead of sitting behind it.
    build(photo=True, name="preview.png", blur=0.007, bright=0.45, colour=1.10, sink=215)
