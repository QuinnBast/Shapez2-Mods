"""Placeholder 16x16 block textures, so the mod is runnable before real art is dropped in.

Each is a flat base colour with per-pixel value jitter and a slightly darker one-pixel border,
which is enough to tell at a glance whether the atlas packed correctly, whether point filtering
survived, and which face of a cube is which. They are not meant to ship.
"""
import os, random
from PIL import Image

OUT = os.path.join("DecorationBlocks", "Resources", "Textures")
os.makedirs(OUT, exist_ok=True)

# name -> (base rgb, jitter amount, alpha)
SPEC = {
    "stone":            ((125, 125, 125), 14, 255),
    "cobblestone":      ((115, 115, 115), 34, 255),
    "stone_bricks":     ((122, 122, 122), 10, 255),
    "bricks":           ((150,  97,  83), 18, 255),
    "oak_planks":       ((162, 130,  78), 14, 255),
    "oak_log":          ((102,  81,  50), 16, 255),
    "oak_log_top":      ((155, 125,  76), 12, 255),
    "dirt":             ((134, 96,   67), 18, 255),
    "grass_block_side": ((124, 104,  70), 18, 255),
    "grass_block_top":  (( 91, 153,  75), 16, 255),
    "sand":             ((219, 207, 163), 12, 255),
    "gravel":           ((131, 127, 126), 30, 255),
    "snow":             ((248, 252, 252),  6, 255),
    "gold_block":       ((249, 236,  78), 12, 255),
    "iron_block":       ((220, 220, 220), 10, 255),
    "diamond_block":    (( 98, 219, 214), 12, 255),
    "emerald_block":    (( 42, 203,  87), 14, 255),
    "lapis_block":      (( 30,  67, 140), 18, 255),
    "redstone_block":   ((175,  24,   5), 16, 255),
    "obsidian":         (( 21,  17,  32), 10, 255),
    "netherrack":       ((111,  54,  52), 20, 255),
    "bookshelf":        ((118,  88,  50), 26, 255),
    "glass":            ((205, 232, 240), 10, 110),
}

random.seed(20260914)

for name, (base, jitter, alpha) in SPEC.items():
    image = Image.new("RGBA", (16, 16))
    pixels = image.load()
    for y in range(16):
        for x in range(16):
            edge = x == 0 or y == 0 or x == 15 or y == 15
            shade = random.randint(-jitter, jitter) + (-18 if edge else 0)
            pixels[x, y] = tuple(max(0, min(255, c + shade)) for c in base) + (alpha,)
    image.save(os.path.join(OUT, name + ".png"))

print("wrote", len(SPEC), "placeholder textures to", OUT)
