"""Gallery thumbnails built from in-game captures.

Captures rather than drawings, because the game extrudes, lights and plates its shapes and no
amount of Pillow gets there. Thumbnail furniture rather than a heading and a caption, because a
store grid is scanned, not read - see thumbnail.py for the rules and where they come from.

    python render_showcase.py
"""

import os

from PIL import Image

import thumbnail as tn

OUT = os.path.dirname(os.path.abspath(__file__))

QUAD = ["GearTowerPreview.PNG", "CrystalRosePreview.PNG", "RainbowVortexPreview.PNG",
        "BuzzsawPreview.PNG", "WidowsWebPreview.PNG", "FourPartsPreview.PNG"]

# FlowerHexPreview is four layers of domes, which is Rainbow Vortex - the file name does not match
# what is in it, and nothing here captions from a file name.
HEX = ["GearTowerHexPreview.PNG", "BloomHexPreview.PNG", "FlowerHexPreview.PNG"]

W, H = 1280, 720


def mosaic(files, cols, rows):
    sheet = Image.new("RGBA", (W, H))
    tile_w, tile_h = W // cols, H // rows
    for i, name in enumerate(files):
        col, row = i % cols, i // cols
        sheet.paste(tn.fill_tile(os.path.join(OUT, name), tile_w, tile_h),
                    (col * tile_w, row * tile_h))
    return sheet


def shapes_thumbnail():
    art = mosaic(QUAD, 3, 2)

    # Darken a strip through the middle so the headline has something to sit on without hiding the
    # shapes it is selling.
    tn.band(art, H // 2 - 170, 340, (0, 0, 0, 135), soft=True)

    tn.punch(art, (W // 2, H // 2 - 58), "10 NEW", 190, tn.YELLOW, rotate=-3)
    tn.punch(art, (W // 2, H // 2 + 96), "SHAPES!", 190, tn.WHITE, rotate=-3)

    tn.starburst(art, (1128, 128), 118)
    tn.punch(art, (1128, 106), "IN EVERY", 34, tn.WHITE, stroke=4)
    tn.punch(art, (1128, 154), "MODE", 60, tn.WHITE, stroke=6)

    tn.ring(art, (213, 180), 150, squash=0.96)
    tn.arrow(art, (330, 632), (212, 356))

    tn.band(art, H - 74, 74, (0, 0, 0, 195))
    tn.punch(art, (W // 2, H - 37), "MINE THEM  •  CUT THEM  •  STACK THEM  •  PAINT THEM",
             34, tn.WHITE, stroke=4, font=tn.black_sans(34))

    art.convert("RGB").save(os.path.join(OUT, "showcase-quad.png"), optimize=True)
    print(f"  showcase-quad.png  {W}x{H}")


def hex_thumbnail():
    art = mosaic(HEX, 3, 1)

    tn.band(art, H // 2 - 185, 370, (0, 0, 0, 135), soft=True)

    tn.punch(art, (W // 2, H // 2 - 70), "HEX MODE", 168, tn.WHITE, rotate=-2)
    tn.punch(art, (W // 2, H // 2 + 86), "TOO!!", 205, tn.YELLOW, rotate=-2)

    tn.starburst(art, (152, 130), 124, fill=(46, 150, 226))
    tn.punch(art, (152, 106), "ALL", 50, tn.WHITE, stroke=5)
    tn.punch(art, (152, 160), "TEN", 66, tn.WHITE, stroke=6)

    tn.arrow(art, (1010, 636), (1120, 430))

    tn.band(art, H - 74, 74, (0, 0, 0, 195))
    tn.punch(art, (W // 2, H - 37), "ITS OWN MESH - NOT THE QUAD SHAPE STRETCHED",
             34, tn.YELLOW, stroke=4, font=tn.black_sans(34))

    art.convert("RGB").save(os.path.join(OUT, "showcase-hex.png"), optimize=True)
    print(f"  showcase-hex.png  {W}x{H}")


if __name__ == "__main__":
    shapes_thumbnail()
    hex_thumbnail()
