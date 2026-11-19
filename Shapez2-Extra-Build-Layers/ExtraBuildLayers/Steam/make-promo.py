#!/usr/bin/env python3
"""
Builds the Workshop preview image.

    python Steam/make-promo.py

Steam shows the grid thumbnail at well under 150 pixels, where a screenshot of a factory is grey
mush. So this is the mod's idea drawn plainly instead: a stack of platform floors, far taller than
the game's three, each one numbered, with a single lift running through all of them. The stack
says "more layers", the numbers say "and they are labelled", the lift says "and things climb it" -
the three things the mod adds that a player would notice.

Same conventions as the other mods' previews: 640 square, drawn at 4x and downsampled because PIL
antialiases nothing, Segoe UI from the Windows fonts folder with a fallback rather than a failure.
"""

import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))

SIZE = 640
SS = 4

SPACE_TOP = (10, 13, 24)
SPACE_BOTTOM = (26, 36, 60)
GRID = (96, 170, 235)
SLAB = (40, 88, 140)
AMBER = (255, 176, 64)
AMBER_DARK = (196, 116, 30)
INK = (238, 244, 252)
MUTED = (150, 168, 196)

# The game's own quadrant colours, for the shapes riding the lift.
SHAPES = [(238, 51, 51), (66, 165, 245), (102, 187, 106), (232, 238, 246)]

BLACK_FONT = r"C:\Windows\Fonts\seguibl.ttf"   # Segoe UI Black
SEMI_FONT = r"C:\Windows\Fonts\seguisb.ttf"    # Segoe UI Semibold

FLOORS = 13            # 0..12: the default +10 on vanilla's three
CENTRE_X = 0.50
BOTTOM_Y = 0.635       # centre of the lowest slab
TOP_Y = 0.165          # centre of the highest slab
HALF_W = 0.24          # half the slab's width
LIFT_X = 0.545         # where the lift pierces the stack


def font(path, size):
    """Falls back rather than failing: a preview with plain type beats no preview."""
    try:
        return ImageFont.truetype(path, size)
    except OSError:
        return ImageFont.load_default()


def tracked(draw, xy, text, face, fill, tracking):
    x, y = xy
    for char in text:
        draw.text((x, y), char, font=face, fill=fill)
        x += draw.textlength(char, font=face) + tracking


def tracked_width(draw, text, face, tracking):
    return sum(draw.textlength(c, font=face) + tracking for c in text) - tracking


def lerp(a, b, t):
    return tuple(int(round(x + (y - x) * t)) for x, y in zip(a, b))


def main():
    n = SIZE * SS
    image = Image.new("RGBA", (n, n))
    draw = ImageDraw.Draw(image, "RGBA")

    for y in range(n):
        draw.line([(0, y), (n, y)], fill=lerp(SPACE_TOP, SPACE_BOTTOM, y / n) + (255,))

    cx = CENTRE_X * n
    hw = HALF_W * n
    hh = hw * 0.5      # isometric: a diamond twice as wide as it is tall
    lift_x = LIFT_X * n
    lift_half = 0.022 * n
    step = (BOTTOM_Y - TOP_Y) * n / (FLOORS - 1)

    def slab_y(floor):
        return BOTTOM_Y * n - floor * step

    label = font(SEMI_FONT, int(0.027 * n))

    def diamond(floor):
        y = slab_y(floor)
        return [(cx, y - hh), (cx + hw, y), (cx, y + hh), (cx - hw, y)]

    def fade(floor):
        # Lower floors sit further into the dark, so the eye goes up the stack.
        return 0.35 + 0.65 * (floor / (FLOORS - 1))

    # Three passes. Fills first, faint, so they tint what lies behind without hiding it. Then the
    # lift over the fills. Then every slab's edges over the lift, so each floor's outline crosses
    # it and it reads as running *through* the floors rather than standing in front of them.
    for floor in range(FLOORS):
        alpha = 150 if floor == FLOORS - 1 else int(55 * fade(floor))
        draw.polygon(diamond(floor), fill=SLAB + (alpha,))

    top = slab_y(FLOORS - 1)
    bottom = slab_y(0) + hh * 0.5
    draw.rectangle([lift_x - lift_half, top, lift_x + lift_half, bottom], fill=AMBER_DARK + (255,))
    draw.rectangle([lift_x - lift_half * 0.4, top, lift_x + lift_half * 0.4, bottom], fill=AMBER + (255,))

    for i, floor in enumerate(range(1, FLOORS - 1, 2)):
        y = slab_y(floor) - step * 0.5
        r = lift_half * 0.95
        draw.rounded_rectangle([lift_x - r, y - r, lift_x + r, y + r], radius=r * 0.35,
                               fill=SHAPES[i % len(SHAPES)] + (255,))

    for floor in range(FLOORS):
        d = diamond(floor)
        y = slab_y(floor)
        f = fade(floor)
        top_floor = floor == FLOORS - 1

        # A few grid lines across the top face, as the game's floor planes have. Only the top
        # floor's are visible; on the rest they would just be noise.
        if top_floor:
            for k in (1, 2, 3):
                t = k / 4
                draw.line([lerp(d[0], d[3], t), lerp(d[1], d[2], t)], fill=GRID + (90,), width=max(1, int(0.0014 * n)))
                draw.line([lerp(d[0], d[1], t), lerp(d[3], d[2], t)], fill=GRID + (90,), width=max(1, int(0.0014 * n)))

        edge = AMBER if top_floor else GRID
        width = int(0.0045 * n) if top_floor else int(0.0026 * n)
        draw.line(d + [d[0]], fill=edge + (int(255 * f),), width=width, joint="curve")

        text = "%02d" % floor
        tw = draw.textlength(text, font=label)
        draw.text((cx - hw - tw - 0.02 * n, y - 0.018 * n), text, font=label,
                  fill=(AMBER if top_floor else INK) + (int(255 * f),))

    out = image.convert("RGB").resize((SIZE, SIZE), Image.LANCZOS)
    draw = ImageDraw.Draw(out)

    title = font(BLACK_FONT, 46)
    sub = font(SEMI_FONT, 24)

    text = "EXTRA BUILD LAYERS"
    tracking = 3
    width = tracked_width(draw, text, title, tracking)
    tracked(draw, ((SIZE - width) / 2, SIZE * 0.80), text, title, INK, tracking)

    text = "taller platforms, a taller sky"
    width = draw.textlength(text, font=sub)
    draw.text(((SIZE - width) / 2, SIZE * 0.905), text, font=sub, fill=MUTED)

    path = os.path.join(HERE, "preview.png")
    out.save(path)
    print("preview.png  %dx%d  %d bytes" % (SIZE, SIZE, os.path.getsize(path)))


if __name__ == "__main__":
    main()
