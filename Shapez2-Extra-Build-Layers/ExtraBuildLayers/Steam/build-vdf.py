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

before it ever creates the item. Write apostrophes in the description instead.

**Non-ASCII text is fine, and the file is written as UTF-8 without a BOM.** This used to refuse
it, on the evidence that every vdf that had published from here was plain ASCII - which was
true, and was not a rule. Valve's KeyValues text parser is byte-oriented: it scans for the
closing quote and copies the bytes between, so a UTF-8 sequence is opaque to it. A BOM is not,
being three bytes ahead of the first key, which is why nothing here writes one.

Descriptions in this repo carry Japanese, Simplified Chinese and French sections in the one
value, because `workshop_build_item` cannot publish a per-language description at all - the
reasoning is in SteamPublish.sh.

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


# k_cchPublishedDocumentDescriptionMax in the Steamworks SDK, and it is a count of **bytes**,
# not of characters: `cch` is Valve's notation for a count of `char`, and the field is a UTF-8
# `char*` buffer. So a CJK section costs three bytes per character against it, and a page that
# measures 6,251 characters can be 8,182 bytes and over the limit.
#
# This is not the truncation the comment here used to claim. Going over **fails the publish**,
# after steamcmd has uploaded the content and created the item:
#
#     Committing update...ERROR! Failed to update workshop item (Invalid Parameter).
#
# which names no field and leaves an item created but uncommitted, so it reads as a broken
# script rather than as a description one paragraph too long.
DESCRIPTION_MAX = 8000

CURLY = "“”‘’"


def check(text):
    """Refuses to build a vdf the parser will reject, and says which character is the problem."""
    if '"' in text:
        raise SystemExit(
            "description.bbcode contains %d double quote(s), which cannot go inside a VDF "
            "value.\nUse apostrophes: 'Record' rather than \"Record\"." % text.count('"'))

    # A curly quote is not a parser problem - KeyValues only looks for U+0022 - but it is a
    # copy problem, and it arrives exactly where translated text does: pasted out of a
    # browser, or typed into an editor that autocorrects. Straighten it in the file rather
    # than here, because silently rewriting somebody's copy is the thing this script is
    # careful not to do.
    curly = sorted({c for c in text if c in CURLY})

    if curly:
        raise SystemExit(
            "description.bbcode contains curly quotes: %s\n"
            "They are safe for the parser but inconsistent on the page - use ' throughout."
            % [hex(ord(c)) for c in curly])

    # Measured in bytes, which is the whole point of this check - counting characters is what
    # let an over-length page through to a publish that failed at the commit step.
    size = len(text.encode("utf-8"))

    if size > DESCRIPTION_MAX:
        raise SystemExit(
            "description.bbcode is %d bytes of UTF-8 (%d characters), over Steam's limit of "
            "%d bytes. The publish would fail at 'Committing update' with Invalid Parameter - "
            "shorten a section. Trim the CJK sections first: they cost 3 bytes per character."
            % (size, len(text), DESCRIPTION_MAX))


def main():
    body = io.open(BBCODE, encoding="utf-8").read().rstrip("\n")
    check(body)

    vdf = io.open(VDF, encoding="utf-8").read()

    # Matched rather than parsed - a real KeyValues parser is a lot of code to change one
    # value - but matched on the value itself, not on "everything up to the closing brace".
    #
    # The earlier pattern was `("description"\s*")(.*)("\s*\n\})` with DOTALL, on the assumption
    # that description is the last key. It is not: `changenote` follows it, and the greedy `.*`
    # swallowed the whole of it, so every run of this script **silently deleted the changenote**.
    #
    # A VDF value cannot contain a double quote at all - `check` above refuses one, because
    # Valve's parser has escape sequences off - so `[^"]*` is not a heuristic here. It is
    # exactly the value and nothing after it.
    pattern = re.compile(r'("description"\s*")([^"]*)(")')

    if not pattern.search(vdf):
        raise SystemExit("base.vdf has no description block in the expected shape.")

    updated = pattern.sub(lambda m: m.group(1) + body + m.group(3), vdf)

    io.open(VDF, "w", encoding="utf-8", newline="\n").write(updated)

    kept = re.search(r'"publishedfileid"\s*"(\d+)"', updated)

    print("base.vdf description updated from description.bbcode")
    size = len(body.encode("utf-8"))
    print("  %d of Steam's %d bytes (%d characters), %d to spare, no quotes"
          % (size, DESCRIPTION_MAX, len(body), DESCRIPTION_MAX - size))
    print("  publishedfileid kept as %s" % (kept.group(1) if kept else "?"))


if __name__ == "__main__":
    main()
