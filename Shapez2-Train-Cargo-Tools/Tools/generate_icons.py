#!/usr/bin/env python3
"""Generates the twelve toolbar icons into TrainCargoTools/Resources/*.png.

Run from anywhere:  python Tools/generate_icons.py     (needs Pillow)


WHY THESE ARE FLAT AND NOT RENDERS OF THE MODELS
------------------------------------------------
The obvious idea - render Resources/*.obj from a nice angle and call it an icon - is
wrong, and the shipped art says so. Look at what the samples ship at
shapez2-mod-samples/*/Resources/*.png:

  Foundation_4x4    a flat top-down grid of rounded squares
  Foundation_5x1    a flat row of five rounded squares
  FluidTrash        a flat white spout with a saturated blue splash
  DiagonalCutter    two flat white quarter-discs with two orange crosses

None of them is a 3D render. The house style is a flat schematic: white-to-grey
structure, one saturated accent colour carrying the meaning, a heavy dark outline, a
transparent background, 512x512 with a generous margin. An isometric render would read as
a different mod's icon at a glance, and would be mush at toolbar size.

So these are drawn, not rendered, and they say what the thing *does* rather than what it
looks like.


THE SYSTEM
----------
Nine icons, and the eye has to sort them instantly in a toolbar.
Two independent channels do that, so either one alone is enough:

  colour   shapes are amber, fluids are blue.        Matches the game's own use of blue
                                                     for fluid (FluidTrash) and orange for
                                                     shape operations (DiagonalCutter).
  form     shape cargo is a square crate,            Survives being squinted at, printed
           fluid cargo is a rounded capsule.         in grey, or seen by a colourblind
                                                     player - colour alone would not.

Structure - track, press body, rack frame - is always the white-to-grey gradient and
never accent-coloured, so the accent only ever means "this is the cargo".

Within a family the six are told apart by composition, matching the models in
generate_meshes.py so the icon predicts what lands on the map:

  belt        a run of track with sealed containers sitting on it. One belt, not two - it
              carries either line's cargo, so there is no fluid twin to draw
  left/right  the same run turning up or down
  packager    three loose items -> a right-pointing arrow -> one package
  unpackager  the same two sides swapped, arrow still pointing right
  store       a 3x2 rack: crates for shapes, capsules for fluids

Everything is drawn in a 0..1 square and scaled up, so proportions are resolution
independent. Work happens at 4x and is downsampled at the end, which is what gives clean
edges out of PIL's aliased primitives.
"""

import os
import sys

try:
    from PIL import Image, ImageDraw, ImageFilter
except ImportError:
    sys.exit("Pillow is needed: python -m pip install Pillow")

# 512 is what every shipped sample icon is. The toolbar draws them far smaller, but the
# sprite is also used at larger sizes in the side panel and the wiki.
SIZE = 512

# Drawn at 4x and downsampled. PIL's polygon and ellipse are hard-edged, so without this
# every diagonal is a staircase.
SS = 4
W = SIZE * SS

OUTLINE = (26, 29, 34, 255)

# Vertical gradients, top colour to bottom colour.
STRUCTURE = ((255, 255, 255), (219, 225, 233))
AMBER = ((255, 173, 51), (238, 120, 16))
BLUE = ((77, 175, 250), (14, 118, 224))

# As a fraction of the icon. Thick: these are read at about 40px in the toolbar, where a
# hairline outline disappears and the shape loses its edge against the panel.
OUTLINE_WIDTH = 0.030
MARGIN = 0.072


def px(v):
    return v * W


class Icon:
    """Collects shapes into two masks - structure and accent - and composites at the end.

    Two masks rather than two images because the outline is drawn from their *union*: the
    whole icon gets one continuous silhouette, the way the shipped ones do, instead of
    every piece being separately ringed.
    """

    def __init__(self):
        self.structure = Image.new("L", (W, W), 0)
        self.accent = Image.new("L", (W, W), 0)
        # Painted last, in the outline colour, and never outlined itself. For markings that
        # sit *inside* a shape - a container's lid seam, a strap. They cannot be another
        # accent shape: outlining works off the union of the masks, so a shape drawn wholly
        # inside another contributes nothing to the silhouette and gets no edge, which is
        # why an earlier version had to make the lid stick out and ended up drawing jars.
        self.detail = Image.new("L", (W, W), 0)
        self._s = ImageDraw.Draw(self.structure)
        self._a = ImageDraw.Draw(self.accent)
        self._d = ImageDraw.Draw(self.detail)

    def _draw(self, layer):
        if layer == "accent":
            return self._a
        if layer == "detail":
            return self._d
        return self._s

    def rect(self, x0, y0, x1, y1, radius=0.05, layer="structure"):
        self._draw(layer).rounded_rectangle(
            (px(x0), px(y0), px(x1), px(y1)), radius=px(radius), fill=255)

    def box(self, cx, cy, w, h=None, radius=0.05, layer="structure"):
        """A crate: a rounded rectangle given by its centre."""
        h = w if h is None else h
        self.rect(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2, radius, layer)

    def capsule(self, cx, cy, w, h=None, layer="accent"):
        """A fluid blob: the same footprint as `box`, fully rounded on its short axis."""
        h = w if h is None else h
        self.box(cx, cy, w, h, radius=min(w, h) / 2, layer=layer)

    def circle(self, cx, cy, d, layer="accent"):
        self._draw(layer).ellipse(
            (px(cx - d / 2), px(cy - d / 2), px(cx + d / 2), px(cy + d / 2)), fill=255)

    def polygon(self, points, layer="structure"):
        self._draw(layer).polygon([(px(x), px(y)) for x, y in points], fill=255)

    # -- composition ------------------------------------------------------------------

    def render(self):
        """Paints outline, structure, accent outline, accent - in that order.

        The accent gets outlined *after* the structure is filled, which is what puts a
        dark edge around a crate sitting on white track. Outlining the union once would
        ring the icon's silhouette and leave the crates as flat patches, and comparing
        against Foundation_4x4 - where every square in the grid has its own edge, not
        just the grid - that is not how the shipped icons are built.
        """
        from PIL import ImageChops

        silhouette = ImageChops.lighter(self.structure, self.accent)
        out = Image.new("RGBA", (W, W), (0, 0, 0, 0))
        ink = Image.new("RGBA", (W, W), OUTLINE)

        # A soft dark halo under everything. The shipped icons all sit on one, and
        # without it a white shape on the dark toolbar panel looks pasted on rather than
        # lit. Kept well under half strength - it should not read as a drop shadow.
        glow = silhouette.filter(ImageFilter.GaussianBlur(px(OUTLINE_WIDTH) * 1.8))
        out.paste(ink, mask=glow.point(lambda v: int(v * 0.45)))

        out.paste(ink, mask=self._grow(silhouette))
        out.paste(gradient(STRUCTURE), mask=self.structure)
        out.paste(ink, mask=self._grow(self.accent))
        out.paste(self.accent_image, mask=self.accent)
        out.paste(ink, mask=self.detail)

        return out.resize((SIZE, SIZE), Image.LANCZOS)

    @staticmethod
    def _grow(mask):
        """Dilates a mask by roughly OUTLINE_WIDTH, by blurring and thresholding.

        Not MaxFilter: a true max filter at this radius is O(r^2) per pixel on a 2048px
        canvas and takes minutes, where the Gaussian is separable and takes a moment.
        The threshold puts the hard edge back and the 4x downsample anti-aliases it.
        """
        grown = mask.filter(ImageFilter.GaussianBlur(px(OUTLINE_WIDTH) * 0.62))
        return grown.point(lambda v: 255 if v > 26 else 0)


def gradient(colours):
    """A vertical two-stop gradient the size of the canvas.

    Flat fills look dead next to the shipped icons, which all have a soft top-to-bottom
    falloff. One gradient image is built per fill and masked, rather than shading each
    shape, so every piece of an icon is lit consistently.
    """
    top, bottom = colours
    strip = Image.new("RGB", (1, W))
    pixels = strip.load()
    for y in range(W):
        t = y / (W - 1)
        pixels[0, y] = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
    return strip.resize((W, W)).convert("RGBA")


# ---------------------------------------------------------------------------
# The drawings
# ---------------------------------------------------------------------------

TRACK_TOP, TRACK_BOTTOM = 0.34, 0.66
NEAR, FAR = MARGIN, 1.0 - MARGIN

# The turns are laid out on their own inset square rather than reusing the straight
# belt's extents. An L only ever fills three quadrants, so drawn from the same edges as
# the straight run it ends up hard against the top-left with a quarter of the canvas
# empty - which in a toolbar row reads as a misaligned icon rather than a corner piece.
# These numbers put the L's bounding box back in the middle.
TURN_NEAR, TURN_FAR = 0.12, 0.88
ARM = 0.30


def cargo(icon, cx, cy, size, fluid):
    """One sealed cargo container.

    A plain coloured rectangle reads as "a block", not "a package". What makes it a
    container is one dark line across it - a lid seam on a crate, a band around a drum -
    drawn on the detail layer so it sits inside the silhouette instead of breaking it.

    The crate/capsule split is the non-colour half of the shape-versus-fluid distinction,
    so it lives here and nowhere else.
    """
    if fluid:
        icon.capsule(cx, cy, size, size * 0.92)
        # Near the top, matching the crate's lid rather than crossing the middle. Centred
        # it turned a round container into a "no entry" sign at toolbar size. Narrower than
        # the body so it stays inside the capsule's curve.
        icon.rect(cx - size * 0.38, cy - size * 0.30,
                  cx + size * 0.38, cy - size * 0.20, radius=0.008, layer="detail")
    else:
        icon.box(cx, cy, size, size, radius=0.035, layer="accent")
        # Nearer the top: a lid seam.
        icon.rect(cx - size * 0.5, cy - size * 0.30,
                  cx + size * 0.5, cy - size * 0.20, radius=0.008, layer="detail")


def belt(fluid, turn=0):
    """A run of cargo track. turn is 0 straight, -1 up (North), +1 down (South).

    Turns are two overlapping rounded bars, not one L polygon: the masks merge so the
    join is solid, and both arms keep the rounded cap the straight run has.
    """
    icon = Icon()

    if turn == 0:
        icon.rect(NEAR, TRACK_TOP, FAR, TRACK_BOTTOM, radius=0.055)
        cargo(icon, 0.33, 0.5, 0.20, fluid)
        cargo(icon, 0.67, 0.5, 0.20, fluid)
        return icon

    # The corner arm is always on the right, because every piece runs West to East; only
    # which way it then leaves changes.
    corner_near = TURN_FAR - ARM

    if turn < 0:
        arm_top = TURN_FAR - ARM                       # horizontal arm low, exit upwards
        icon.rect(TURN_NEAR, arm_top, TURN_FAR, TURN_FAR, radius=0.055)
        icon.rect(corner_near, TURN_NEAR, TURN_FAR, TURN_FAR, radius=0.055)
        along, across = arm_top + ARM / 2, TURN_NEAR + ARM / 2
    else:
        arm_top = TURN_NEAR                            # horizontal arm high, exit down
        icon.rect(TURN_NEAR, TURN_NEAR, TURN_FAR, TURN_NEAR + ARM, radius=0.055)
        icon.rect(corner_near, TURN_NEAR, TURN_FAR, TURN_FAR, radius=0.055)
        along, across = TURN_NEAR + ARM / 2, TURN_FAR - ARM / 2

    cargo(icon, TURN_NEAR + ARM / 2, along, 0.20, fluid)
    cargo(icon, corner_near + ARM / 2, across, 0.20, fluid)
    return icon


def loose(icon, cx, fluid, size=0.16):
    """The unpacked side: three small items in a column."""
    for cy in (0.23, 0.5, 0.77):
        if fluid:
            icon.circle(cx, cy, size)
        else:
            icon.box(cx, cy, size, size, radius=0.032, layer="accent")


def arrow(icon, x0, x1, cy=0.5, thickness=0.19, head=0.42):
    """A plain right-pointing arrow: rectangular tail, triangular head.

    Both machines point the same way because both run West to East. It is the only
    structure in these two icons - an earlier version used a converging funnel instead,
    which at toolbar size turned the whole icon into a loudspeaker.
    """
    split = x1 - (x1 - x0) * head
    icon.rect(x0, cy - thickness / 2, split + 0.01, cy + thickness / 2, radius=0.02)
    icon.polygon([(split, cy - thickness), (x1, cy), (split, cy + thickness)])


def packager(fluid, reverse=False):
    """Three loose items on one side, one package on the other, an arrow between.

    Packager and unpackager are the same drawing with the two cargo sides swapped, and
    the arrow stays pointing right in both. So the icon says two things at once: which
    way material flows, and which end of it is packed.
    """
    icon = Icon()
    arrow(icon, 0.36, 0.63)

    small_x, package_x = (0.86, 0.20) if reverse else (0.14, 0.80)
    loose(icon, small_x, fluid)
    cargo(icon, package_x, 0.5, 0.30, fluid)
    return icon


def store(fluid):
    """A buffer: a rack of containers, three shelves deep.

    Three because the simulation keeps three independent per-layer queues, and because the
    model is a three-shelf rack that the renderer fills from live state - so the icon and
    the thing you place say the same number.

    The fluid store used to be drawn as a row of standing tanks. That was wrong: a fluid
    cargo store holds sealed `CargoPackage<FluidId>` containers, not loose fluid, and tanks
    implied it pooled the stuff. Both are racks now, and they differ the way everything
    else in this set does - crate against capsule, amber against blue.
    """
    icon = Icon()
    icon.rect(0.09, 0.12, 0.91, 0.88, radius=0.07)

    for cy in (0.28, 0.5, 0.72):
        for cx in (0.32, 0.68):
            if fluid:
                icon.capsule(cx, cy, 0.24, 0.155)
            else:
                icon.box(cx, cy, 0.24, 0.155, radius=0.026, layer="accent")
    return icon


ICONS = {
    "CargoBelt": lambda: belt(False, 0),
    "CargoBeltLeft": lambda: belt(False, -1),
    "CargoBeltRight": lambda: belt(False, +1),
    "CargoPackager": lambda: packager(False),
    "CargoUnpackager": lambda: packager(False, reverse=True),
    "CargoStore": lambda: store(False),
    "FluidCargoPackager": lambda: packager(True),
    "FluidCargoUnpackager": lambda: packager(True, reverse=True),
    "FluidCargoStore": lambda: store(True),
}


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.normpath(os.path.join(here, os.pardir, "TrainCargoTools", "Resources"))
    if not os.path.isdir(out):
        sys.exit("No Resources folder at %s" % out)

    problems = 0
    for name, build in sorted(ICONS.items()):
        icon = build()
        icon.accent_image = gradient(BLUE if name.startswith("Fluid") else AMBER)
        image = icon.render()
        image.save(os.path.join(out, name + ".png"))

        alpha = image.getchannel("A")
        # Thresholded: getbbox() counts any non-zero pixel, and the halo fades out over
        # tens of pixels, so an unthresholded box always reports the whole canvas and the
        # clipping check never means anything.
        box = alpha.point(lambda v: 255 if v > 40 else 0).getbbox()
        coverage = sum(1 for p in alpha.getdata() if p > 8) / float(SIZE * SIZE)
        print("%-26s bbox=%-22s coverage=%4.1f%%" % (name + ".png", box, coverage * 100))

        # The shipped icons run 25-80% coverage. Far outside that means a drawing that is
        # either a speck or a solid slab, both of which read badly in the toolbar.
        if coverage < 0.12:
            print("   WARN: very sparse, will look lost at toolbar size")
            problems += 1
        if coverage > 0.88:
            print("   WARN: nearly solid, the silhouette carries no information")
            problems += 1
        if box is None or box[0] < 2 or box[1] < 2 or box[2] > SIZE - 2 or box[3] > SIZE - 2:
            print("   WARN: touches the canvas edge; the outline will be clipped")
            problems += 1

    print("\n%d icon(s) with warnings." % problems if problems else
          "\nAll %d drawn, none clipped." % len(ICONS))


if __name__ == "__main__":
    main()
