#!/usr/bin/env python3
"""
Builds the Workshop preview image.

    python Steam/make-promo.py

Steam draws the grid thumbnail small - often under 150 pixels wide - so the preview is not a
screenshot. A screenshot of a factory at that size is grey mush. It is the mod's own mark
instead: the crosshair, drawn large, on the horizon it puts you on.

The crosshair is built from the same proportions `FirstPersonTuning` uses in game - arm 7,
gap 3, thickness 2 - scaled up, so the mark is the thing the player actually looks through
rather than a logo invented for the store.

Promo screenshots for the item page are captures rather than drawings, and there is nothing
to generate for them: put 1920x1080 grabs in `Shapez2-First-Person/Screenshots/` and link
them from the README, the way the other mods here do.
"""

import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))

SIZE = 640
SS = 4  # supersample; PIL antialiases nothing on its own

SPACE = (14, 18, 28)
HORIZON = (30, 42, 66)
GLOW = (46, 68, 104)
AMBER = (255, 209, 102)
INK = (238, 244, 252)
MUTED = (150, 168, 196)

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


def build():
    n = SIZE * SS
    image = Image.new("RGB", (n, n), SPACE)
    draw = ImageDraw.Draw(image)

    # A horizon rather than a flat field: it is what first person put on the screen, and it
    # gives the crosshair something to sit against at thumbnail size.
    horizon = int(n * 0.60)
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

    # The crosshair, in FirstPersonTuning's proportions: arm 7, gap 3, thickness 2.
    # Sat high enough that the lower arm clears the wordmark: it ends at
    # cy + (gap + arm) * unit, which with these numbers is 0.65n against a title at 0.70n.
    cx, cy = n // 2, int(n * 0.38)
    unit = n * 0.027
    arm = 7 * unit
    gap = 3 * unit
    half = unit  # thickness 2, so half of it either side of the axis

    for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
        near = gap
        far = gap + arm
        if dx:
            x0, x1 = sorted((cx + dx * near, cx + dx * far))
            draw.rectangle([x0, cy - half, x1, cy + half], fill=INK)
        else:
            y0, y1 = sorted((cy + dy * near, cy + dy * far))
            draw.rectangle([cx - half, y0, cx + half, y1], fill=INK)

    dot = unit * 0.9
    draw.ellipse([cx - dot, cy - dot, cx + dot, cy + dot], fill=AMBER)

    out = image.resize((SIZE, SIZE), Image.LANCZOS)
    draw = ImageDraw.Draw(out)

    title = font(BLACK_FONT, 62)
    sub = font(SEMI_FONT, 25)

    text = "FIRST PERSON"
    tracking = 5
    width = tracked_width(draw, text, title, tracking)
    tracked(draw, ((SIZE - width) / 2, SIZE * 0.70), text, title, INK, tracking)

    text = "walk your own factory"
    width = draw.textlength(text, font=sub)
    draw.text(((SIZE - width) / 2, SIZE * 0.82), text, font=sub, fill=MUTED)

    path = os.path.join(HERE, "preview.png")
    out.save(path)
    print(f"{path}  {out.size[0]}x{out.size[1]}")


if __name__ == "__main__":
    build()
