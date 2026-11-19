#!/usr/bin/env python3
"""
Builds the Workshop preview image.

    python Steam/make-promo.py

Steam shows the grid thumbnail at well under 150 pixels, where a screenshot of a factory is grey
mush. So this draws the one picture every player already reads as "layers": the game's own layer
selector from the HUD, a diamond for the top layer over a chevron per layer below it, stretched
far past the three it has in vanilla, filling the frame, with the title set big across it. One
chevron is lit, as the selector lights the layer being viewed.

The shapes are drawn as vectors in the style of the HUD's sprites rather than taken from them: the
sprites are 64 pixels wide and blur at preview size. Nothing is read from the game.

Same conventions as the other mods' previews: 640 square, drawn at 4x and downsampled, Segoe UI
from the Windows fonts folder with a fallback rather than a failure.
"""

import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))

SIZE = 640
SS = 4

SPACE_TOP = (12, 16, 30)
SPACE_BOTTOM = (30, 40, 66)
INK = (238, 244, 252)
MUTED = (150, 168, 196)
ACCENT = (255, 160, 40)      # the game's orange, as on its layer unlock icon

BLACK_FONT = r"C:\Windows\Fonts\seguibl.ttf"   # Segoe UI Black
SEMI_FONT = r"C:\Windows\Fonts\seguisb.ttf"    # Segoe UI Semibold

LAYERS = 11              # chevrons below the diamond - few enough that each one reads
SELECTED = 2             # which chevron is lit, counted from the bottom
STACK_X = 0.50           # centre of the selector column
STACK_TOP = 0.07
STACK_BOTTOM = 0.93
SPRITE_W = 0.62          # sprite width as a fraction of the image

TITLE_LINES = ("EXTRA", "LAYERS!")


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


def chevron(draw, cx, y, half_w, drop, thickness, colour, outline):
    """The HUD's layer chevron, drawn as a vector: a thick rounded V with a dark rim."""
    pts = [(cx - half_w, y - drop / 2), (cx, y + drop / 2), (cx + half_w, y - drop / 2)]
    for width, fill in ((thickness + outline * 2, (10, 12, 24, 255)), (thickness, colour)):
        draw.line(pts, fill=fill, width=int(width), joint="curve")
        for px, py in (pts[0], pts[2]):
            r = width / 2
            draw.ellipse([px - r, py - r, px + r, py + r], fill=fill)


def diamond(draw, cx, y, half_w, half_h, colour, outline):
    """The HUD's top-layer diamond, likewise."""
    for grow, fill in ((outline, (10, 12, 24, 255)), (0, colour)):
        draw.polygon([(cx, y - half_h - grow), (cx + half_w + grow * 2, y), (cx, y + half_h + grow),
                      (cx - half_w - grow * 2, y)], fill=fill)


def main():
    n = SIZE * SS
    image = Image.new("RGBA", (n, n))
    draw = ImageDraw.Draw(image, "RGBA")

    for y in range(n):
        draw.line([(0, y), (n, y)], fill=lerp(SPACE_TOP, SPACE_BOTTOM, y / n) + (255,))

    # Drawn rather than taken from the HUD sprites: those are 64 pixels wide and go to mush at the
    # size a preview needs. Same shapes - a diamond for the top layer, a chevron per layer below.
    cx = STACK_X * n
    half_w = SPRITE_W * n / 2
    slots = LAYERS + 1
    step = (STACK_BOTTOM - STACK_TOP) * n / (slots - 1)
    thickness = step * 0.42
    outline = thickness * 0.18
    drop = half_w * 0.36

    for slot in range(slots):
        layer = LAYERS - slot
        y = STACK_TOP * n + slot * step
        if layer == SELECTED:
            colour = ACCENT + (255,)
        else:
            # Fade towards the bottom, so the stack reads as rising rather than as a list.
            colour = lerp((70, 82, 110), INK, 0.35 + 0.65 * (layer / LAYERS)) + (255,)
        if slot == 0:
            diamond(draw, cx, y, half_w, drop * 0.9, colour, outline)
        else:
            chevron(draw, cx, y, half_w, drop, thickness, colour, outline)

    # A soft dark glow behind the title so it reads over the chevrons, without a hard band.
    glow = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse([int(0.02 * n), int(0.30 * n), int(0.98 * n), int(0.70 * n)], fill=(6, 8, 18, 200))
    glow = glow.filter(ImageFilter.GaussianBlur(radius=0.06 * n))
    image.alpha_composite(glow)

    out = image.convert("RGB").resize((SIZE, SIZE), Image.LANCZOS).convert("RGBA")
    draw = ImageDraw.Draw(out)

    # Two lines, as wide as the frame allows, with a dark outline so the letters hold up at
    # thumbnail size over the chevrons.
    sizes = [118, 118]
    faces = [font(BLACK_FONT, size) for size in sizes]
    heights = []
    for line, face in zip(TITLE_LINES, faces):
        box = draw.textbbox((0, 0), line, font=face)
        heights.append(box[3] - box[1])
    gap = 6
    y = (SIZE - (sum(heights) + gap)) / 2
    for i, (line, face) in enumerate(zip(TITLE_LINES, faces)):
        box = draw.textbbox((0, 0), line, font=face)
        x = (SIZE - (box[2] - box[0])) / 2 - box[0]
        fill = ACCENT if i == 1 else INK
        draw.text((x, y - box[1]), line, font=face, fill=fill + (255,), stroke_width=7, stroke_fill=(10, 12, 24, 255))
        y += heights[i] + gap

    out = out.convert("RGB")

    path = os.path.join(HERE, "preview.png")
    out.save(path)
    print("preview.png  %dx%d  %d bytes" % (SIZE, SIZE, os.path.getsize(path)))


if __name__ == "__main__":
    main()
