#!/usr/bin/env python3
"""
Captions the Workshop screenshots in the game's own voice.

    python tools/caption-screenshots.py

Reads clean 1920x1080 captures from `FirstPerson/Screenshots/` and writes captioned copies
to `FirstPerson/Screenshots/captioned/`. The sources are never modified, so a caption can be
rewritten without recapturing.

Output format is chosen per shot against **Steam's 2 MB screenshot limit**, not fixed: a
captioned gameplay frame is 2.0 to 2.8 MB as a PNG - over the cap on every shot that is not
a flat UI panel - so those are written as JPEG instead, and the UI panels stay lossless. See
`write` for why the JPEGs are 4:4:4.

The style is taken from the game rather than invented. shapez writes its HUD in **letterspaced
uppercase** - SELECT SCENARIO, DELETE, PIPETTE - in near-white, with amber reserved for the
thing that matters. So the headline is letterspaced uppercase amber and the sub-line is a
plain sentence in near-white, and the pair reads as part of the interface instead of as
something stuck on top of it.

Two things a red outline gets wrong that this avoids: it fights a palette that is orange and
teal on purple, and a stroke turns to mud when Steam scales the image down. Legibility comes
from a **local scrim** instead - a soft dark gradient behind the text only, fading out to the
right, so the picture itself is never dimmed.

Captions live here, in CAPTIONS, keyed by source filename. Keeping the words in the script
rather than in the pixels is the point: when the mod changes, the caption is a line of code to
edit rather than a screenshot to retake.
"""

import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.normpath(os.path.join(HERE, "..", "FirstPerson", "Screenshots"))
OUTPUT = os.path.join(SOURCE, "captioned")

# Steam rejects a Workshop screenshot over 2 MB, and a captioned 1920x1080 frame of a
# detailed scene lands at 2.0 to 2.8 MB as a PNG - just over, on every shot that is actual
# gameplay rather than a flat UI panel. So the budget is enforced here, where it can pick a
# format, rather than discovered one file at a time in the upload dialog.
BUDGET = 2 * 1024 * 1024

# Aimed under the cap rather than at it. The margin costs a quality step nobody can see, and
# spends it on not having to care whether Steam's 2 MB means 2,000,000 or 2,097,152.
TARGET = int(BUDGET * 0.92)

AMBER = (255, 176, 74)
INK = (238, 244, 252)
SCRIM = (8, 10, 16)

BLACK_FONT = r"C:\Windows\Fonts\seguibl.ttf"   # Segoe UI Black
SEMI_FONT = r"C:\Windows\Fonts\seguisb.ttf"    # Segoe UI Semibold

# (source file, output slug, headline, sub-line), in the order they should appear on the
# Workshop page. The slug is numbered because Steam orders screenshots by upload and the
# numbering is the only thing that makes that order reproducible.
#
# The first two earn their place: somebody deciding whether to install this wants to see what
# it looks like to stand in a factory, not a menu. The mod-list shot is last on purpose - it
# is aimed at the handful of people who will read the description anyway.
CAPTIONS = [
    (
        "2026_09_22_0zj_Kleki.png", "1-eye-level",
        "YOUR FACTORY AT EYE LEVEL",
        "Walk the platforms you built, and watch the shapes go past.",
    ),
    (
        "2026_09_22_0zh_Kleki.png", "2-build-at-a-crosshair",
        "CROSSHAIR SUPPORT",
        "Place, delete and pipette whatever you are looking at.",
    ),
    (
        "2026_09_22_0zg_Kleki.png", "3-ride-the-trains",
        "RIDE YOUR OWN TRAINS",
        "Travel faster on trains and experience the lifts, jumps and loops!",
    ),
    (
        "2026_09_22_0zf_Kleki.png", "4-fly",
        "UNLOCK THE ABILITY TO FLY",
        "Jet Pack is just one of three new research unlocks!",
    ),
    (
        "2026_09_22_0ze_Kleki(1).png", "5-a-scenario",
        "PLAY IN A CUSTOM SCENARIO",
        "Start your next game in First Person - doesn't affect existing saves",
    ),
    (
        "2026_09_22_0ze_Kleki.png", "6-built-to-be-built-on",
        "ACCESSIBLE FOR DEVELOPERS",
        "Extensible code lets other modders add additional custom first person experiences!",
    ),
]

# Shots that go up without a caption, because they already say it themselves. The research
# panel is captioned in the game's own words and adding more would be shouting over it.
UNCAPTIONED = [
    ("2026_09_22_0zj_Kleki(1).png", "7-researched"),
]


def font(path, size):
    """Falls back rather than failing: a caption in plain type beats no caption."""
    try:
        return ImageFont.truetype(path, size)
    except OSError:
        return ImageFont.load_default()


def tracked(draw, xy, text, face, fill, tracking):
    """Letterspaced text. PIL has no tracking, and the game's uppercase is letterspaced."""
    x, y = xy
    for char in text:
        draw.text((x, y), char, font=face, fill=fill)
        x += draw.textlength(char, font=face) + tracking


def tracked_width(draw, text, face, tracking):
    return sum(draw.textlength(c, font=face) + tracking for c in text) - tracking


def scrim(image, top, bottom, width):
    """
    A soft dark wash behind the caption only, fading out to the right.

    Dimming the whole lower third would be easier and would dim the subject with it - in half
    these shots the interesting thing *is* in the lower third. Fading horizontally keeps the
    picture and buys the text its contrast from the part of the frame that is already empty.
    """
    band = Image.new("RGBA", (width, bottom - top), SCRIM + (0,))
    pixels = band.load()

    for x in range(width):
        # Full strength for the first third, then out to nothing by the right edge.
        t = max(0.0, min(1.0, (x - width * 0.34) / (width * 0.66)))
        alpha = int(205 * (1.0 - t) ** 1.6)
        for y in range(band.height):
            pixels[x, y] = SCRIM + (alpha,)

    # Feather the top and bottom edges so the band has no visible seam.
    feather = 46
    for y in range(feather):
        fade = y / feather
        for x in range(width):
            r, g, b, a = pixels[x, y]
            pixels[x, y] = (r, g, b, int(a * fade))
            r, g, b, a = pixels[x, band.height - 1 - y]
            pixels[x, band.height - 1 - y] = (r, g, b, int(a * fade))

    image.alpha_composite(band, (0, top))


def caption(path, slug, headline, subline):
    image = Image.open(path).convert("RGBA")
    width, height = image.size

    # Sized from the width rather than the height: one of these is a wide crop of a panel
    # rather than a full frame, and height-relative type comes out tiny on it.
    head = font(BLACK_FONT, int(width * 0.029))
    sub = font(SEMI_FONT, int(width * 0.0152))

    left = int(width * 0.042)
    # Above the game's own action-hint row (SELECT AREA / DELETE AREA / PIPETTE), which sits
    # at about 0.83 of the height and which the sub-line collided with one notch lower.
    head_y = int(height * 0.655)
    sub_y = head_y + int(height * 0.075)

    scrim(image, head_y - int(height * 0.05), sub_y + int(height * 0.075), int(width * 0.78))

    draw = ImageDraw.Draw(image)
    tracking = head.size * 0.06

    tracked(draw, (left, head_y), headline, head, AMBER, tracking)
    draw.text((left, sub_y), subline, font=sub, fill=INK)

    write(image, slug, width, height)


def discard(path):
    """Removes the other format's file, so a shot that changes format leaves nothing behind.

    Without this, a frame that used to fit as a PNG keeps its stale oversized copy next to
    the new JPEG, and the upload dialog offers both - with the wrong one sorting first.
    """
    if os.path.exists(path):
        os.remove(path)


def write(image, slug, width, height):
    """
    Writes the smallest faithful file that fits Steam's 2 MB screenshot limit.

    PNG is tried first and kept when it fits, because it is lossless and the two UI-panel
    shots are flat enough to compress to a few hundred KB. A full 3D frame is not, and falls
    back to JPEG.

    That JPEG is written at **4:4:4 chroma** (`subsampling=0`), which matters more here than
    the quality number does. The headline is saturated amber on near-black, and 4:2:0 halves
    the colour resolution across exactly that edge - so the letterspaced caps fringe and
    smear while the photographic three-quarters of the frame still looks perfect, which is a
    hard thing to notice in a thumbnail and an obvious one at full size.
    """
    os.makedirs(OUTPUT, exist_ok=True)
    flat = image.convert("RGB")

    png = os.path.join(OUTPUT, slug + ".png")
    jpg = os.path.join(OUTPUT, slug + ".jpg")

    flat.save(png, optimize=True)

    if os.path.getsize(png) <= TARGET:
        discard(jpg)
        report(png, width, height, "png lossless")
        return

    # The highest quality that fits, rather than one fixed number for every shot. These
    # frames differ by nearly a factor of two in how well they compress, so a single setting
    # either leaves most of the budget unspent or misses it on the busiest frame.
    for quality in (95, 93, 91, 89, 86, 83, 80, 76, 72):
        flat.save(jpg, quality=quality, subsampling=0, optimize=True, progressive=True)

        if os.path.getsize(jpg) <= TARGET:
            discard(png)
            report(jpg, width, height, f"jpeg q{quality}")
            return

    discard(png)
    report(jpg, width, height, "jpeg q72 - STILL OVER BUDGET")


def report(path, width, height, how):
    size = os.path.getsize(path)
    flag = "  <-- OVER 2 MB" if size > BUDGET else ""
    print(f"{os.path.basename(path):28} {width}x{height}  "
          f"{size / 1048576:5.2f} MB  {how}{flag}")


if __name__ == "__main__":
    for name, slug, headline, subline in CAPTIONS:
        source = os.path.join(SOURCE, name)

        if not os.path.exists(source):
            print(f"missing: {name}")
            continue

        caption(source, slug, headline, subline)

    for name, slug in UNCAPTIONED:
        source = os.path.join(SOURCE, name)

        if not os.path.exists(source):
            print(f"missing: {name}")
            continue

        image = Image.open(source).convert("RGBA")
        write(image, slug, image.width, image.height)
