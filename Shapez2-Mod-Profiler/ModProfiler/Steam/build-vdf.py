#!/usr/bin/env python3
"""
Rewrites base.vdf's description from description.bbcode.

    python Steam/build-vdf.py

description.bbcode is the copy people edit; base.vdf is what steamcmd reads, and it carries the
description inline. Keeping them in step by hand is a way to publish last week's text without
noticing, so this does it.

**A double quote cannot appear in the value at all, escaped or not.** A VDF value is a quoted
string. Escaping the quotes as \\" looks right and is not: Valve's KeyValues text parser takes a
bEscapeSequences flag that is off by default, so the backslash stays literal, the first quote
ends the value, and steamcmd dies with

    src\\tier1\\KeyValues.cpp (3176) : Assertion Failed: Error while parsing text KeyValues

before it ever creates the item. Write apostrophes in the description instead. Every base.vdf in
this repo that has published successfully has zero quotes inside the description and is plain
ASCII, and this refuses to build one that is not.

Nothing is rewritten here on purpose. The description is copy somebody wrote, and quietly
publishing something other than what is in the file would be worse than refusing.

Everything else in base.vdf is preserved, publishedfileid included: SteamPublish.sh writes the
id back there after a first publish, and regenerating the whole file would throw it away.
"""

import io
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))

VDF = os.path.join(HERE, "base.vdf")
BBCODE = os.path.join(HERE, "description.bbcode")


def check(text):
    """Refuses to build a vdf the parser will reject, and says which character is the problem."""
    if '"' in text:
        raise SystemExit(
            "description.bbcode contains %d double quote(s), which cannot go inside a VDF "
            "value.\nUse apostrophes: 'Record' rather than \"Record\"." % text.count('"'))

    leftover = sorted({c for c in text if ord(c) > 127})

    if leftover:
        raise SystemExit(
            "description.bbcode contains non-ASCII characters: %s\n"
            "Every base.vdf that has published from this repo is plain ASCII - use - for a "
            "dash and ... for an ellipsis." % [hex(ord(c)) for c in leftover])


def main():
    body = io.open(BBCODE, encoding="utf-8").read().rstrip("\n")
    check(body)

    vdf = io.open(VDF, encoding="utf-8").read()

    # The description is the last key and runs to the closing brace, so it is matched rather
    # than parsed - a real KeyValues parser is a lot of code to change one value.
    pattern = re.compile(r'("description"\s*")(.*)("\s*\n\})', re.S)

    if not pattern.search(vdf):
        raise SystemExit("base.vdf has no description block in the expected shape.")

    updated = pattern.sub(lambda m: m.group(1) + body + m.group(3), vdf)

    io.open(VDF, "w", encoding="utf-8", newline="\n").write(updated)

    kept = re.search(r'"publishedfileid"\s*"(\d+)"', updated)

    print("base.vdf description updated from description.bbcode")
    print("  %d characters, plain ASCII, no quotes" % len(body))
    print("  publishedfileid kept as %s" % (kept.group(1) if kept else "?"))


if __name__ == "__main__":
    main()
