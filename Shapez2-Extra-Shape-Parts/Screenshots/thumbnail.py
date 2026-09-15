"""Thumbnail furniture: heavy text, badges and arrows, drawn over the art rather than above it.

Written against what actually works on a thumbnail rather than what looks tidy in a document:
ultra bold condensed type, three to five words, a black stroke around eight to twelve pixels so the
text survives any background, one high contrast accent colour, and arrows or rings to point at the
thing being sold. Text sits *on* the picture; a caption under a picture is a caption, and nobody
reads captions at gallery size.

Sources for the rules, not the taste:
  hellothematic.com/youtube-thumbnails-playbook
  vidiq.com/blog/post/youtube-thumbnail-design-tips
"""

import math
import os

from PIL import Image, ImageDraw, ImageFont

FONTS = r"C:\Windows\Fonts"

YELLOW = (255, 214, 10)
WHITE = (255, 255, 255)
RED = (233, 58, 48)
BLACK = (0, 0, 0)


def impact(size):
    return ImageFont.truetype(os.path.join(FONTS, "impact.ttf"), size)


def black_sans(size):
    return ImageFont.truetype(os.path.join(FONTS, "ariblk.ttf"), size)


def punch(image, xy, body, size, fill=WHITE, stroke=None, rotate=0.0, anchor="mm",
          font=None, shadow=True):
    """One slab of thumbnail text, stroked and optionally rotated.

    Drawn onto its own transparent layer so it can be rotated without chewing the art underneath,
    and so the stroke composites cleanly instead of being drawn twice.
    """
    fnt = font or impact(size)
    stroke = max(6, size // 9) if stroke is None else stroke

    pad = stroke * 3 + size
    scratch = Image.new("RGBA", (image.width + pad * 2, image.height + pad * 2), (0, 0, 0, 0))
    draw = ImageDraw.Draw(scratch)

    x, y = xy[0] + pad, xy[1] + pad
    if shadow:
        draw.text((x + stroke * 0.7, y + stroke * 0.9), body, font=fnt, fill=(0, 0, 0, 150),
                  anchor=anchor, stroke_width=stroke, stroke_fill=(0, 0, 0, 150))
    draw.text((x, y), body, font=fnt, fill=fill, anchor=anchor,
              stroke_width=stroke, stroke_fill=BLACK)

    if rotate:
        scratch = scratch.rotate(rotate, resample=Image.BICUBIC, center=(x, y))

    image.alpha_composite(scratch, (-pad, -pad))
    return image


def starburst(image, centre, radius, points=12, fill=RED, outline=BLACK, width=None):
    """The spiky disc a 'NEW!' sits in. Cheap, loud, and reads at any size."""
    width = width or max(3, radius // 12)
    poly = []
    for i in range(points * 2):
        angle = math.pi * i / points - math.pi / 2
        r = radius if i % 2 == 0 else radius * 0.72
        poly.append((centre[0] + math.cos(angle) * r, centre[1] + math.sin(angle) * r))

    layer = Image.new("RGBA", image.size, (0, 0, 0, 0))
    ImageDraw.Draw(layer).polygon(poly, fill=fill, outline=outline, width=width)
    image.alpha_composite(layer)
    return image


def ring(image, centre, radius, colour=YELLOW, width=None, squash=1.0):
    """A hand-drawn-looking circle round the thing worth looking at."""
    width = width or max(5, radius // 9)
    layer = Image.new("RGBA", image.size, (0, 0, 0, 0))
    box = [centre[0] - radius, centre[1] - radius * squash,
           centre[0] + radius, centre[1] + radius * squash]
    ImageDraw.Draw(layer).ellipse(box, outline=colour, width=width)
    image.alpha_composite(layer)
    return image


def arrow(image, start, end, colour=YELLOW, width=None, head=None):
    """A fat arrow, black-edged so it survives a busy background."""
    width = width or 18
    head = head or width * 2.6
    layer = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)

    angle = math.atan2(end[1] - start[1], end[0] - start[0])
    back = (end[0] - math.cos(angle) * head, end[1] - math.sin(angle) * head)

    for colour_pass, extra in ((BLACK, width // 2 + 4), (colour, 0)):
        draw.line([start, back], fill=colour_pass, width=width + extra * 2)
        tip = [end,
               (back[0] + math.cos(angle + math.pi / 2) * (head * 0.55 + extra),
                back[1] + math.sin(angle + math.pi / 2) * (head * 0.55 + extra)),
               (back[0] + math.cos(angle - math.pi / 2) * (head * 0.55 + extra),
                back[1] + math.sin(angle - math.pi / 2) * (head * 0.55 + extra))]
        draw.polygon(tip, fill=colour_pass)

    image.alpha_composite(layer)
    return image


def band(image, top, height, fill=(0, 0, 0, 170), soft=False):
    """A dark strip to drop a line of text onto when the art behind it is too busy.

    `soft` feathers the top and bottom edges. A hard bar reads as deliberate furniture along an
    edge of the frame, and reads as a mistake through the middle of one.
    """
    layer = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)

    if not soft:
        draw.rectangle([0, top, image.width, top + height], fill=fill)
    else:
        feather = height // 3
        for y in range(height):
            t = min(y, height - y, feather) / feather
            draw.line([(0, top + y), (image.width, top + y)],
                      fill=fill[:3] + (int(fill[3] * min(1.0, t)),))

    image.alpha_composite(layer)
    return image


def fill_tile(path, width, height):
    """A capture cropped to fill a tile completely - thumbnails have no room for letterboxing."""
    image = Image.open(path).convert("RGBA")
    scale = max(width / image.width, height / image.height)
    resized = image.resize((max(1, int(image.width * scale)), max(1, int(image.height * scale))),
                           Image.LANCZOS)
    left = (resized.width - width) // 2
    top = (resized.height - height) // 2
    return resized.crop((left, top, left + width, top + height))
