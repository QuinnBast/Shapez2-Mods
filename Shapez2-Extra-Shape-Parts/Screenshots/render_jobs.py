"""Draws the side quest draft: each group as a chain ending in one of the showcase patterns."""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from render import (  # noqa: E402
    SS, BG, CARD, RULE, INK, MUTED, ACCENT, canvas, card, draw_shape, finish, font, text)

OUT = r"C:\Users\Quinn\Documents\Coding\shapez2-mods\Shapez2-Extra-Shape-Parts\Screenshots"

# (group title, subtitle, [(step label, code, amount, what this step adds)])
GROUPS = [
    ("Rainbow Vortex", "dome - one colour per layer", [
        ("Red dome", "MrMrMrMr", "250", "a dome line, red"),
        ("Sunrise", "MrMrMrMr:MyMyMyMy", "1,000", "+ a yellow line, stacker"),
        ("Three deep", "MrMrMrMr:MyMyMyMy:MgMgMgMg", "4,000", "+ a green line"),
        ("Vortex", "MrMrMrMr:MyMyMyMy:MgMgMgMg:MbMbMbMb", "8,000", "+ a blue line"),
    ]),
    ("Turn the Wheel", "gear - stack, then pin", [
        ("Gear", "EuEuEuEu", "250", "a gear line"),
        ("Two deep", "EuEuEuEu:EyEyEyEy", "1,000", "+ painter, stacker"),
        ("Gear tower", "EuEuEuEu:EyEyEyEy:ErErErEr", "4,000", "+ a third line"),
        ("Pinned tower", "P-P-P-P-:EuEuEuEu:EyEyEyEy:ErErErEr", "8,000", "+ pin pusher"),
    ]),
    ("Both Ways", "dome + wedge - handedness", [
        ("Red wedge", "TrTrTrTr", "250", "a wedge line, red"),
        ("Wedge on dome", "TrTrTrTr:MwMwMwMw", "1,000", "+ a dome line, white"),
        ("Three deep", "TrTrTrTr:MwMwMwMw:TbTbTbTb", "4,000", "+ blue wedge"),
        ("Contra", "TrTrTrTr:MwMwMwMw:TbTbTbTb:MwMwMwMw", "8,000", "+ a fourth stack"),
    ]),
    ("In Bloom", "flower - into crystal", [
        ("Flower", "BmBmBmBm", "250", "a flower line, magenta"),
        ("Half a rose", "BmBmBmBm:Br--Br--", "1,000", "+ half-cut red flower"),
        ("Crystal rose", "BmBmBmBm:BrcrBrcr", "4,000", "+ crystal generator"),
        ("Crowned rose", "BmBmBmBm:BrcrBrcr:ByByByBy", "8,000", "+ yellow flower"),
    ]),
    ("Sharpen", "sawblade - and one vanilla line", [
        ("Sawblade", "ZuZuZuZu", "250", "a sawblade line"),
        ("Buzzsaw", "ZuZuZuZu:OrOrOrOr", "1,000", "+ a dot line, red"),
        ("Twin saw", "ZuZuZuZu:OrOrOrOr:ZwZwZwZw", "4,000", "+ white sawblade"),
    ]),
    ("Fine Detail", "bar, cross, dot - then interleave", [
        ("Bar", "IcIcIcIc", "250", "a bar line, cyan"),
        ("Circuitry", "IcIcIcIc:KmKmKmKm", "1,000", "+ a cross line, magenta"),
        ("Three deep", "IcIcIcIc:KmKmKmKm:OyOyOyOy", "4,000", "+ a dot line, yellow"),
        ("Interleave", "IcIcIcIc:KmKmKmKm:OyIyOyIy", "8,000", "+ half-cut and recombine"),
    ]),
    ("Foundations", "vanilla underneath, new on top", [
        ("Inlay", "RuDuRuDu", "500", "cut and recombine square + diamond"),
        ("Porthole", "RuDuRuDu:CwCwCwCw", "2,000", "+ a circle line, white"),
        ("Cog plate", "RuDuRuDu:CwCwCwCw:EyEyEyEy", "6,000", "+ a gear line, yellow"),
    ]),
]


def make_jobs():
    step_w = 268
    row_h = 266
    left = 300
    pad = 34
    cols = max(len(steps) for _, _, steps in GROUPS)

    W = left + cols * step_w + pad
    H = 158 + len(GROUPS) * row_h + 150

    image, draw = canvas(W, H)
    title = font("arialbd.ttf", 44)
    sub = font("arial.ttf", 21)
    group_font = font("arialbd.ttf", 26)
    group_sub = font("arial.ttf", 18)
    step_font = font("arialbd.ttf", 17)
    mono = font("consola.ttf", 13)
    amount = font("arialbd.ttf", 16)
    arrow = font("arial.ttf", 30)
    adds_font = font("arial.ttf", 14)

    text(draw, (pad, 46), "Side quest draft", title, INK)
    text(draw, (pad, 100),
         "Seven chains. Every step adds one thing to the factory that made the step before it - "
         "a painter, a stacker, a pin pusher - so nothing gets rebuilt.", sub, MUTED)

    for row, (name, flavour, steps) in enumerate(GROUPS):
        y = 158 + row * row_h
        draw.line([pad * SS, (y - 10) * SS, (W - pad) * SS, (y - 10) * SS], fill=RULE, width=SS)

        text(draw, (pad, y + 62), name, group_font, INK)
        text(draw, (pad, y + 96), flavour, group_sub, MUTED)

        for i, (label, code, count, adds) in enumerate(steps):
            x = left + i * step_w
            card(draw, x, y + 8, step_w - 26, row_h - 44)

            cx = x + (step_w - 26) / 2
            draw_shape(draw, code, cx * SS, (y + 72) * SS, 50 * SS)
            text(draw, (cx, y + 124), label, step_font, INK, anchor="ma")
            text(draw, (cx, y + 146), adds, adds_font, ACCENT, anchor="ma")
            text(draw, (cx, y + 166), count + " x", amount, INK, anchor="ma")

            # Four layers is the deepest chain here; the card is sized to hold them.
            for j, layer in enumerate(code.split(":")):
                text(draw, (cx, y + 188 + j * 13), layer, mono, MUTED, anchor="ma")

            if i < len(steps) - 1:
                text(draw, (x + step_w - 20, y + 70), ">", arrow, RULE, anchor="ma")

    text(draw, (pad, H - 104),
         "Each chain is one ResearchSideQuestGroup. Each step is a ResearchSideQuest whose cost is",
         sub, MUTED)
    text(draw, (pad, H - 78),
         "a SerializedResearchCostShapes - deliver N of that shape. Steps unlock in order. "
         "Amounts are placeholders.", sub, MUTED)
    text(draw, (pad, H - 46),
         "Pin, crystal, windmill and star draw as plain grey quarters: their outlines are authored "
         "meshes, so this is not a claim about how they look.", sub, MUTED)

    finish(image, W, H, os.path.join(OUT, "jobs-draft.png"))


make_jobs()
