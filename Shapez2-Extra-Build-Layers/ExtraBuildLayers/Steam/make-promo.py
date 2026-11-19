#!/usr/bin/env python3
"""
Builds the Workshop preview image.

    python Steam/make-promo.py

Steam shows the grid thumbnail at well under 150 pixels, where a screenshot of a factory is grey
mush. So this draws the one picture every player already reads as "layers": the game's own layer
selector from the HUD, a diamond for the top layer over a chevron per layer below it, stretched
far past the three it has in vanilla. One chevron is lit, as the selector lights the layer being
viewed, with the number readout the mod puts beside it.

The two sprites are read out of the game's resources.assets at run time rather than committed,
so the repo carries no game art. That needs UnityPy (pip install UnityPy) and the game install,
found from SPZ2_PATH (which points at the Managed folder) or the default Steam path.

Same conventions as the other mods' previews: 640 square, drawn at 4x and downsampled, Segoe UI
from the Windows fonts folder with a fallback rather than a failure.
"""

import os

from PIL import Image, ImageDraw, ImageFont

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

LAYERS = 19              # chevrons below the diamond: 19 is the highest floor a platform can have
SELECTED = 12            # which chevron is lit, counted from the bottom
STACK_X = 0.36           # centre of the selector column
STACK_TOP = 0.05
STACK_BOTTOM = 0.74
SPRITE_W = 0.30          # sprite width as a fraction of the image


def game_data():
    path = os.environ.get("SPZ2_PATH", "")
    if path.lower().rstrip(r"\/").endswith("managed"):
        path = os.path.dirname(path.rstrip(r"\/"))
    if not path:
        path = r"C:\Program Files (x86)\Steam\steamapps\common\shapez 2\shapez 2_Data"
    return path


def load_sprites(*names):
    import UnityPy
    env = UnityPy.load(os.path.join(game_data(), "resources.assets"))
    found = {}
    for obj in env.objects:
        if obj.type.name == "Sprite" and obj.peek_name() in names:
            found[obj.peek_name()] = obj.read().image.convert("RGBA")
    missing = set(names) - set(found)
    if missing:
        raise SystemExit("sprites not found in resources.assets: %s" % ", ".join(sorted(missing)))
    return found


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


def tinted(sprite, colour, alpha):
    """The HUD sprites are white with a dark rim; multiplying keeps the rim and recolours the face."""
    r, g, b, a = sprite.split()
    out = Image.merge("RGBA", (
        r.point(lambda v: v * colour[0] // 255),
        g.point(lambda v: v * colour[1] // 255),
        b.point(lambda v: v * colour[2] // 255),
        a.point(lambda v: v * alpha // 255),
    ))
    return out


def scaled(sprite, width):
    height = round(sprite.height * width / sprite.width)
    return sprite.resize((width, height), Image.LANCZOS)


def main():
    sprites = load_sprites("TopLayer", "SubLayer")
    n = SIZE * SS
    image = Image.new("RGBA", (n, n))
    draw = ImageDraw.Draw(image, "RGBA")

    for y in range(n):
        draw.line([(0, y), (n, y)], fill=lerp(SPACE_TOP, SPACE_BOTTOM, y / n) + (255,))

    width = int(SPRITE_W * n)
    top = scaled(sprites["TopLayer"], width)
    sub = scaled(sprites["SubLayer"], width)
    cx = STACK_X * n

    # Slots from the top: the diamond, then the chevrons down to layer 0.
    slots = LAYERS + 1
    step = (STACK_BOTTOM - STACK_TOP) * n / (slots - 1)
    lit_y = None
    for slot in range(slots):
        layer = LAYERS - slot          # the diamond is the top layer, LAYERS
        y = STACK_TOP * n + slot * step
        sprite = top if slot == 0 else sub
        if layer == SELECTED:
            colour, alpha = ACCENT, 255
            lit_y = y
        else:
            # Fade towards the bottom, so the stack reads as rising rather than as a list.
            colour, alpha = INK, int(255 * (0.35 + 0.65 * (layer / LAYERS)))
        piece = tinted(sprite, colour, alpha)
        image.alpha_composite(piece, (int(cx - piece.width / 2), int(y - piece.height / 2)))

    # The readout the mod shows beside the selector instead of a tower of handles.
    big = font(BLACK_FONT, int(0.20 * n))
    small = font(SEMI_FONT, int(0.065 * n))
    number = "%d" % SELECTED
    nx = cx + width * 0.62
    bbox = draw.textbbox((0, 0), number, font=big)
    draw.text((nx, lit_y - (bbox[1] + bbox[3]) / 2), number, font=big, fill=ACCENT + (255,))
    nw = draw.textlength(number, font=big)
    draw.text((nx + nw + 0.02 * n, lit_y - 0.005 * n), "/ %d" % LAYERS, font=small, fill=MUTED + (255,))

    out = image.convert("RGB").resize((SIZE, SIZE), Image.LANCZOS)
    draw = ImageDraw.Draw(out)

    title = font(BLACK_FONT, 46)
    sub_face = font(SEMI_FONT, 24)

    text = "EXTRA BUILD LAYERS"
    tracking = 3
    w = tracked_width(draw, text, title, tracking)
    tracked(draw, ((SIZE - w) / 2, SIZE * 0.80), text, title, INK, tracking)

    text = "taller platforms, a taller sky"
    w = draw.textlength(text, font=sub_face)
    draw.text(((SIZE - w) / 2, SIZE * 0.905), text, font=sub_face, fill=MUTED)

    path = os.path.join(HERE, "preview.png")
    out.save(path)
    print("preview.png  %dx%d  %d bytes" % (SIZE, SIZE, os.path.getsize(path)))


if __name__ == "__main__":
    main()
