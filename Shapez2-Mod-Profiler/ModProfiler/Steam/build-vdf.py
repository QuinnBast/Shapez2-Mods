#!/usr/bin/env python3
"""
Rewrites base.vdf's description from description.bbcode.

    python Steam/build-vdf.py

description.bbcode is the copy people edit; base.vdf is what steamcmd reads, and it carries the
description inline. Keeping them in step by hand is a way to publish last week's text without
noticing, so this does it.

**Quotes have to be escaped.** A VDF value is a quoted string, and the description talks about
the "Record" button and the "Export" button - ten double quotes in the current copy. Pasted in
raw they close the value early, and steamcmd either rejects the file or silently publishes the
description up to the first one.

Everything else in base.vdf is preserved, publishedfileid included: SteamPublish.sh writes the
id back there after a first publish, and regenerating the whole file would throw it away.
"""

import io
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))

VDF = os.path.join(HERE, "base.vdf")
BBCODE = os.path.join(HERE, "description.bbcode")


def escape(text):
    """VDF quoted-string escaping. Backslashes first, or the quote escapes get mangled."""
    return text.replace("\\", "\\\\").replace('"', '\\"')


def main():
    body = io.open(BBCODE, encoding="utf-8").read().rstrip("\n")
    vdf = io.open(VDF, encoding="utf-8").read()

    # The description is the last key and runs to the closing brace, so it is matched rather
    # than parsed - a real KeyValues parser is a lot of code to change one value.
    pattern = re.compile(r'("description"\s*")(.*)("\s*\n\})', re.S)

    if not pattern.search(vdf):
        raise SystemExit("base.vdf has no description block in the expected shape.")

    updated = pattern.sub(lambda m: m.group(1) + escape(body) + m.group(3), vdf)

    io.open(VDF, "w", encoding="utf-8", newline="\n").write(updated)

    kept = re.search(r'"publishedfileid"\s*"(\d+)"', updated)

    print("base.vdf description updated from description.bbcode")
    print("  %d characters, %d quote(s) escaped" % (len(body), body.count('"')))
    print("  publishedfileid kept as %s" % (kept.group(1) if kept else "?"))


if __name__ == "__main__":
    main()
