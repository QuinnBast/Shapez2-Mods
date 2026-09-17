"""Pull the block textures this mod needs out of an installed Minecraft client jar.

    python tools_import_minecraft.py [path-to-.minecraft]

Writes 16x16 PNGs into DecorationBlocks/Resources/Textures, replacing whatever is there.

Two things are not a straight copy:

* **Grass is greyscale in the jar.** Minecraft tints it per biome at draw time, so
  `grass_block_top.png` extracted as-is is a pale grey square, and the green fringe on
  `grass_block_side.png` lives in a separate greyscale-plus-alpha overlay. Both are
  multiplied by the plains tint here and the overlay is composited down, because shapez has
  no biomes to ask.
* **Not every greyscale texture wants tinting.** `stone.png` is greyscale too, and it is
  meant to be. So the tint list is explicit rather than detected.

The output of this script is Mojang's artwork. It is fine in your own game folder; it is not
fine in a Workshop upload. See DESIGN.md.
"""
import io
import os
import re
import sys
import zipfile

from PIL import Image

# Where each file goes, and what to do to it on the way. `tint` multiplies the greyscale
# source by a colour; `overlay` composites another (tinted) texture on top.
PLAINS_GRASS = (0x79, 0xC0, 0x5A)

PLAIN = [
    "stone", "cobblestone", "stone_bricks", "bricks", "oak_planks", "oak_log", "oak_log_top",
    "dirt", "sand", "gravel", "snow", "gold_block", "iron_block", "diamond_block",
    "emerald_block", "lapis_block", "redstone_block", "obsidian", "netherrack", "bookshelf",
    "glass",

    # Redstone components. The torch and lever textures are cutouts, which is why the
    # components render on the alpha-clipped material.
    "redstone_torch", "redstone_torch_off", "repeater", "repeater_on", "lever", "smooth_stone",
    "redstone_lamp", "redstone_lamp_on", "comparator", "comparator_on",
    "observer_front", "observer_back", "observer_back_on", "observer_side", "observer_top",
]

TEXTURES = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "DecorationBlocks", "Resources", "Textures")


def find_jar(minecraft_root):
    """Newest version jar, by the version folder name sorted naturally.

    Release candidates and snapshots sit beside releases in the same folder and only some of
    them have a jar - the launcher writes the .json first - so folders without one are
    skipped rather than being an error.
    """
    versions = os.path.join(minecraft_root, "versions")
    if not os.path.isdir(versions):
        sys.exit("No versions folder under " + minecraft_root)

    candidates = []
    for name in os.listdir(versions):
        jar = os.path.join(versions, name, name + ".jar")
        if os.path.isfile(jar):
            key = [int(part) if part.isdigit() else part for part in re.split(r"(\d+)", name)]
            candidates.append((key, jar))

    if not candidates:
        sys.exit("No version jar under " + versions + " - run the game once to download one.")

    candidates.sort()
    return candidates[-1][1]


def read(zf, name):
    path = "assets/minecraft/textures/block/" + name + ".png"
    return Image.open(io.BytesIO(zf.read(path))).convert("RGBA")


def tint(image, colour):
    result = image.copy()
    pixels = result.load()
    for y in range(result.height):
        for x in range(result.width):
            r, g, b, a = pixels[x, y]
            pixels[x, y] = (r * colour[0] // 255, g * colour[1] // 255, b * colour[2] // 255, a)
    return result


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.environ.get("APPDATA", ""), ".minecraft")

    jar = find_jar(root)
    print("reading", jar)

    os.makedirs(TEXTURES, exist_ok=True)
    written = 0

    with zipfile.ZipFile(jar) as zf:
        for name in PLAIN:
            read(zf, name).save(os.path.join(TEXTURES, name + ".png"))
            written += 1

        top = tint(read(zf, "grass_block_top"), PLAINS_GRASS)
        top.save(os.path.join(TEXTURES, "grass_block_top.png"))
        written += 1

        side = read(zf, "grass_block_side")
        overlay = tint(read(zf, "grass_block_side_overlay"), PLAINS_GRASS)
        side.alpha_composite(overlay)
        side.save(os.path.join(TEXTURES, "grass_block_side.png"))
        written += 1

        written += write_dust_masks(zf)
        written += write_solid()
        written += write_icons(zf)

    print("wrote", written, "textures to", TEXTURES)


def write_icons(zf):
    """Toolbar icons for the three components whose own texture makes a poor one.

    A toolbar icon is cut straight out of the atlas, which is normally exactly right - a block's
    entry shows the block's texture. Two components are not normally right:

    * **The button** has no texture of its own; in Minecraft it is a small plate wearing the
      stone it is cut from. Using `smooth_stone` whole gives a toolbar entry that is a full
      square of stone, indistinguishable from the stone block and far larger than the thing it
      places. So the plate is masked out of the stone here, with the rest transparent.
    * **Redstone dust** is stored greyscale and tinted by signal strength at draw time, so its
      own texture makes a grey icon for the one component everybody recognises by its colour.
      This tints the fully-connected shape with Minecraft's own strength-15 colour.
    * **The converter** has no Minecraft counterpart at all, so there is no texture to cut. It
      is drawn here instead.
    """
    stone = read(zf, "smooth_stone")
    size = stone.width

    # A button plate: a touch over half the tile wide, a third of it deep, with its border
    # darkened so it reads as a raised object rather than a crop of the stone behind it.
    button = Image.new("RGBA", (size, size))
    left, right = size * 3 // 16, size * 13 // 16
    top, bottom = size * 5 // 16, size * 11 // 16
    pixels = button.load()
    source = stone.load()

    for y in range(top, bottom):
        for x in range(left, right):
            r, g, b, a = source[x, y]
            edge = x in (left, right - 1) or y in (top, bottom - 1)
            shade = 0.55 if edge else 1.0
            pixels[x, y] = (int(r * shade), int(g * shade), int(b * shade), 255)

    button.save(os.path.join(TEXTURES, "button_icon.png"))

    # Dust at full strength, which is the colour the item is recognised by. The ramp is
    # Minecraft's own from RedStoneWireBlock, the same one RedstoneCatalog.DustColour applies at
    # draw time - kept in step by hand, which is acceptable for one end of it.
    dust = Image.open(os.path.join(TEXTURES, "redstone_dust_15.png")).convert("RGBA")
    dust = tint(dust, (255, 51, 0))
    dust.save(os.path.join(TEXTURES, "redstone_dust_icon.png"))

    # The converter, drawn rather than borrowed: the tile split on the diagonal into redstone red
    # and wire blue, with a double-headed arrow across it. Double-headed because the building
    # really does work both ways at once, and one arrowhead would be a lie about which way it goes.
    converter = Image.new("RGBA", (size, size))
    pixels = converter.load()
    red, blue = (200, 30, 12, 255), (38, 92, 190, 255)
    white, dark = (245, 245, 245, 255), (25, 25, 30, 255)

    for y in range(size):
        for x in range(size):
            pixels[x, y] = red if x + y < size - 1 else blue

    mid = size // 2
    for x in range(3, size - 3):
        for y in (mid - 1, mid):
            pixels[x, y] = white

    # The two heads. Each step out from the tip widens the head by a row either side of the shaft.
    for i in range(3):
        for y in range(mid - 1 - i, mid + 1 + i):
            pixels[2 + i, y] = white
            pixels[size - 3 - i, y] = white

    # A dark border, so the shape survives whatever the toolbar puts behind it.
    for i in range(size):
        pixels[i, 0] = dark
        pixels[i, size - 1] = dark
        pixels[0, i] = dark
        pixels[size - 1, i] = dark

    converter.save(os.path.join(TEXTURES, "converter_icon.png"))

    return 3


def write_solid():
    """One fully opaque white tile.

    Anything drawn as flat colour - redstone dust, which is a coloured line and nothing else -
    maps its geometry here and takes its colour from a material property block instead. That
    removes the dependency on the shader clipping transparent texels, which is not something a
    mod can rely on: URP strips shader variants it thinks nothing uses, and a stripped
    `_ALPHATEST_ON` silently falls back to a variant that renders the transparent parts as
    black rather than discarding them.
    """
    Image.new("RGBA", (16, 16), (255, 255, 255, 255)).save(
        os.path.join(TEXTURES, "solid_white.png"))
    return 1


def write_dust_masks(zf):
    """One tile per connection shape: redstone_dust_0 .. redstone_dust_15.

    Minecraft does not ship a texture per shape. It ships one strand
    (`redstone_dust_line0`, a vertical run) and a centre blob (`redstone_dust_dot`), and
    assembles a dust by drawing the strand twice - once rotated - and clipping each half to
    whichever neighbours the dust is connected to. This does the same thing once, at import,
    so the runtime picks a tile by connection mask instead of composing anything.

    Bit order is N=1, E=2, S=4, W=8, matching RedstoneWorld's mask.

    The result stays **greyscale**. Signal strength is a tint applied per draw - sixteen
    strengths times sixteen shapes would be 256 tiles otherwise - so the colour comes from a
    material property block and this only carries the shape.
    """
    strand = read(zf, "redstone_dust_line0")
    dot = read(zf, "redstone_dust_dot")
    size = strand.width

    # The strand runs top to bottom, so the vertical halves cut straight out of it and the
    # horizontal ones come from the same image rotated a quarter turn.
    horizontal = strand.rotate(90)
    half = size // 2

    written = 0
    for mask in range(16):
        tile = Image.new("RGBA", (size, size))

        if mask & 1:                                   # north: top half of the vertical strand
            tile.alpha_composite(strand.crop((0, 0, size, half)), (0, 0))
        if mask & 4:                                   # south: bottom half
            tile.alpha_composite(strand.crop((0, half, size, size)), (0, half))
        if mask & 2:                                   # east: right half of the horizontal one
            tile.alpha_composite(horizontal.crop((half, 0, size, size)), (half, 0))
        if mask & 8:                                   # west: left half
            tile.alpha_composite(horizontal.crop((0, 0, half, size)), (0, 0))

        # The blob goes on last and always. An unconnected dust is a blob and nothing else,
        # and a connected one needs it to cover the seam where the halves meet.
        tile.alpha_composite(dot)
        tile.save(os.path.join(TEXTURES, "redstone_dust_%d.png" % mask))
        written += 1

    return written


if __name__ == "__main__":
    main()
