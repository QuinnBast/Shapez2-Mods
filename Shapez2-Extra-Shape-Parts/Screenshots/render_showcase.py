"""Gallery sheets built from in-game captures rather than drawn.

Everything else in here draws shapes flat, because that is all a script can do. The game extrudes
them, lights them and sits them on a platform, and no amount of Pillow gets there - so the store
page leads with captures and keeps the drawn sheets as the reference at the end.

The captures come out of the Shape Inspector at slightly different sizes but with the same
background, which is what makes tiling them work: sample that background, use it for the sheet, and
the seams disappear.

    python render_showcase.py
"""

import os

from PIL import Image, ImageDraw

from render import INK, MUTED, SS, finish, font, text

OUT = os.path.dirname(os.path.abspath(__file__))

QUAD = [
    ("GearTowerPreview.PNG", "GEAR TOWER"),
    ("CrystalRosePreview.PNG", "CRYSTAL ROSE"),
    ("RainbowVortexPreview.PNG", "RAINBOW VORTEX"),
    ("BuzzsawPreview.PNG", "BUZZSAW"),
    ("WidowsWebPreview.PNG", "WIDOW'S WEB"),
    ("FourPartsPreview.PNG", "FOUR PARTS, FOUR COLOURS"),
]

HEX = [
    ("GearTowerHexPreview.PNG", "GEAR TOWER"),
    ("BloomHexPreview.PNG", "IN BLOOM"),
    # Labelled by what is in the picture, not by the file name: this capture is four layers of
    # domes in red, yellow, green and blue, which is Rainbow Vortex. Worth checking rather than
    # trusting - a wrong caption on a store page is worse than no caption.
    ("FlowerHexPreview.PNG", "RAINBOW VORTEX"),
]


def background(files):
    """The captures' own backdrop, averaged off their corners.

    Sampled rather than picked, so the tiles sit on the same colour they already contain and the
    joins between them stop being visible.
    """
    total = [0, 0, 0]
    samples = 0
    for name in files:
        image = Image.open(os.path.join(OUT, name)).convert("RGB")
        for point in ((2, 2), (image.width - 3, 2), (2, image.height - 3),
                      (image.width - 3, image.height - 3)):
            pixel = image.getpixel(point)
            total = [t + p for t, p in zip(total, pixel)]
            samples += 1
    # Darkened: the captures carry a blue to warm gradient, so their average corner is a mauve
    # that matches none of them. Two thirds of it reads as a neutral the tiles sit on rather than
    # as a colour trying and failing to be the same.
    return tuple(int(t / samples * 0.62) for t in total)


def tile(name, size):
    """One capture, centre cropped square and scaled - the Inspector frames them slightly
    differently each time and a grid needs them the same."""
    image = Image.open(os.path.join(OUT, name)).convert("RGB")
    side = min(image.width, image.height)
    left = (image.width - side) // 2
    top = (image.height - side) // 2
    return image.crop((left, top, left + side, top + side)).resize((size, size), Image.LANCZOS)


def sheet(entries, cols, filename, heading, note, tile_px=420):
    rows = (len(entries) + cols - 1) // cols
    gap, pad, label = 18, 44, 54
    head = 178

    W = pad * 2 + cols * tile_px + (cols - 1) * gap
    H = head + rows * (tile_px + label) + (rows - 1) * gap + pad

    back = background([name for name, _ in entries])
    image = Image.new("RGB", (W * SS, H * SS), back)
    draw = ImageDraw.Draw(image)

    text(draw, (pad, 40), heading, font("arialbd.ttf", 66), INK)
    text(draw, (pad, 124), note, font("arial.ttf", 21), MUTED)

    for i, (name, caption) in enumerate(entries):
        col, row = i % cols, i // cols
        x = pad + col * (tile_px + gap)
        y = head + row * (tile_px + label + gap)
        image.paste(tile(name, tile_px * SS), (x * SS, y * SS))
        text(draw, (x + tile_px / 2, y + tile_px + 16), caption,
             font("arialbd.ttf", 21), INK, anchor="ma")

    finish(image, W, H, os.path.join(OUT, filename))


if __name__ == "__main__":
    sheet(QUAD, 3, "showcase-quad.png", "10 NEW SHAPES!",
          "Mined from the map. Cut, stacked, painted, pinned and crystallised like any other shape.")
    sheet(HEX, 3, "showcase-hex.png", "WORKS IN HEXAGONAL MODE!",
          "Every part rebuilt at 60 degrees with its own mesh - not the quad shape stretched to fit.")
