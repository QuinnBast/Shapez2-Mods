#!/usr/bin/env python3
"""Check translations.json against the game's extended-XML translation parser.

A malformed tag is not a cosmetic problem: `TranslationExtendedXMLParser` throws
"XML Tag not properly closed", the translation file fails to load, and ShapezShifter
aborts the whole mod. Nothing in the build catches it, so this does.

The tag table mirrors `TagMatch` in Core.Localization, and the separator is ':' rather
than '=' - `TranslationExtendedXMLParser.ParseTagData` is `inTag.Split(':')`.

    python Tools/check_translations.py
"""
import json
import os
import re
import sys

# tag -> (expects ':data', wraps children)
KNOWN = {
    "gl": (False, True),           # glossary highlight: bold orange, not clickable
    "b": (False, True),            # bold
    "unit": (False, True),         # small dim unit styling
    "info": (False, True),         # faded italic secondary text
    "link": (True, True),          # blue link; data is matched against a TextWithLinks Link
    "gll": (True, True),           # orange link to a wiki entry: data is the entry id
    "hotkey": (True, False),       # key chip
    "wip-warning": (False, False),
    "icon": (True, False),
    "copy-from": (True, False),   # inlines another entry's text
}

# Wiki entry ids this mod defines, so a <gll:...> typo is caught rather than silently
# rendering an orange link that plays an error sound. Keep in step with CargoWiki.
ENTRIES = {
    # This mod's own, named the way vanilla names its entries.
    "WKCargo_Intro", "WKCargo_Belt", "WKCargo_Packager",
    "WKCargo_Unpackager", "WKCargo_Store",
    # Vanilla entries linked to from the cargo pages. Confirmed present in
    # basedata-v1138/translations-en-US.json; a typo here renders an orange link that
    # plays an error sound rather than navigating.
    "WKTrains_TrainStations", "WKIslands_SpaceBelts", "WKIslands_Floors",
    "WKFluids_Pipes", "WKFluids_SpacePipes", "WKProcessing_Intro",
}


def check(text, key, problems):
    stack = []
    for match in re.finditer(r"<([^<>]*)>", text):
        body = match.group(1)
        selfclose = body.endswith("/")
        if selfclose:
            body = body[:-1]
        closing = body.startswith("/")
        if closing:
            body = body[1:]
        tag, _, data = body.partition(":")

        if closing:
            if not stack or stack[-1] != tag:
                got = stack[-1] if stack else "nothing"
                problems.append(f"{key}: </{tag}> does not close {got}")
            elif stack:
                stack.pop()
            continue

        if tag not in KNOWN:
            # An unknown self-closing tag is a placeholder, which is how <layer/> works.
            # An unknown tag with children is a typo.
            if not selfclose:
                problems.append(f"{key}: unknown tag <{tag}> (placeholders must self-close)")
            continue

        wants_data, wants_children = KNOWN[tag]
        if bool(data) != wants_data:
            problems.append(
                f"{key}: <{tag}> data mismatch (has={bool(data)}, wants={wants_data})")
        if wants_children and selfclose:
            problems.append(f"{key}: <{tag}/> self-closed but wraps children")
        if tag == "gll" and data not in ENTRIES:
            problems.append(f"{key}: <gll:{data}> is not a wiki entry this mod defines")
        if '"' in data:
            problems.append(f"{key}: quotation mark in link data, which the parser rejects")
        if not selfclose and wants_children:
            stack.append(tag)

    if stack:
        problems.append(f"{key}: unclosed {stack}")


def main():
    path = os.path.join(os.path.dirname(__file__), "..",
                        "TrainCargoTools", "translations.json")
    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)

    problems = []
    total = 0
    for language, strings in data.items():
        if not isinstance(strings, dict):
            problems.append(f"{language}: expected an object of keys, "
                            "translations.json is keyed by language code first")
            continue
        for key, text in strings.items():
            total += 1
            check(text, f"{language}/{key}", problems)

    for problem in problems:
        print("PROBLEM:", problem)
    print(f"checked {total} strings, {len(problems)} problems")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
