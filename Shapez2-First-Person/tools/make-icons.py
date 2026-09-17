"""Draws the three research-shop icons.

Run from anywhere:  python tools/make-icons.py

The size is not a preference. Every shop preview image the game ships is a sprite of
exactly 1024 x 709 - read out of `resources.assets`, where a Sprite's `m_Rect` is stored
as four floats right after its name, and CBBelts_Core, CBShapes_Extraction,
CBPlatformPack_Large and nine others all answer (0, 0, 1024, 709).

`HUDResearchSideUpgradeDisplay` assigns the sprite to a plain `Image` whose
`preserveAspect` is off, so the sprite is stretched to whatever box the prefab gives it.
A square source therefore comes out 1.44x too wide. Matching the authored size is the
whole fix; there is nothing to set in code.

Drawn at 4x and downsampled, because PIL has no antialiasing of its own.
"""

import os
from PIL import Image, ImageDraw

WIDTH, HEIGHT = 1024, 709
SS = 4

LIGHT = (231, 237, 246, 255)
MID = (141, 156, 177, 255)
DARK = (26, 32, 41, 255)
ORANGE = (255, 175, 69, 255)
FLAME = (255, 138, 61, 255)

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   "..", "FirstPerson", "Resources")


def canvas():
    image = Image.new("RGBA", (WIDTH * SS, HEIGHT * SS), (0, 0, 0, 0))
    return image, ImageDraw.Draw(image)


def s(*values):
    """Scales a coordinate tuple into supersampled space."""
    return tuple(int(round(v * SS)) for v in values)


def finish(image, name):
    out = image.resize((WIDTH, HEIGHT), Image.LANCZOS)
    path = os.path.normpath(os.path.join(OUT, name))
    out.save(path)
    print(f"{path}  {out.size[0]}x{out.size[1]}")


def jet_pack():
    """Two tanks, a harness and angled thrust.

    Spread wide on purpose: the frame is 1.44:1, and a tall narrow subject centred in it
    reads as a small subject. The flames drift outwards so the silhouette widens towards
    the bottom rather than hanging in the middle.
    """
    image, draw = canvas()
    cx = 512

    # One wide backplate the tanks overlap, rather than two bars meeting a small centre
    # body - that version came out reading as a dumbbell.
    draw.rounded_rectangle(s(cx - 142, 196, cx + 142, 358), radius=48 * SS, fill=MID)

    for side in (-1, 1):
        x = cx + side * 186
        draw.rounded_rectangle(s(x - 68, 146, x + 68, 392), radius=68 * SS, fill=LIGHT)
        draw.polygon([s(x - 68, 392), s(x + 68, 392), s(x + 44, 444), s(x - 44, 444)], fill=MID)
        draw.polygon([s(x - 44, 448), s(x + 44, 448), s(x + side * 34, 606)], fill=FLAME)
        draw.polygon([s(x - 21, 448), s(x + 21, 448), s(x + side * 16, 548)], fill=ORANGE)

    finish(image, "Icon_JetPack.png")


def waypoint_travel():
    image, draw = canvas()
    cx, cy, r = 620, 288, 150

    draw.ellipse(s(cx - r, cy - r, cx + r, cy + r), fill=LIGHT)
    draw.polygon([s(cx - 95, cy + 76), s(cx + 95, cy + 76), s(cx, cy + 266)], fill=LIGHT)
    draw.ellipse(s(cx - 77, cy - 77, cx + 77, cy + 77), fill=ORANGE)

    # A trail running back from the pin's tip, along the same ground line. The dashes
    # shorten with distance, which reads as "travelled from over there" without an
    # arrowhead to argue with the pin for attention.
    right = 480
    for width in (92, 62, 36):
        draw.rounded_rectangle(s(right - width, 536, right, 560), radius=12 * SS, fill=MID)
        right -= width + 42

    finish(image, "Icon_WaypointTravel.png")


def train_riding():
    image, draw = canvas()

    draw.rectangle(s(150, 520, 890, 544), fill=MID)
    for x in range(172, 880, 64):
        draw.rectangle(s(x, 552, x + 40, 578), fill=MID)

    draw.rounded_rectangle(s(250, 148, 790, 462), radius=56 * SS, fill=LIGHT)
    draw.rounded_rectangle(s(300, 208, 740, 352), radius=34 * SS, fill=DARK)

    # The wheels rest on the rail rather than through it: bottom = centre + r = 520, which
    # is the top of the rail bar.
    for x in (392, 610):
        draw.ellipse(s(x - 52, 416, x + 52, 520), fill=MID)

    draw.polygon([s(820, 292), s(820, 412), s(930, 352)], fill=ORANGE)
    finish(image, "Icon_TrainRiding.png")


if __name__ == "__main__":
    jet_pack()
    waypoint_travel()
    train_riding()
