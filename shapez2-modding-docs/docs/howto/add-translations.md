# Add translations

**Problem.** Your building's name shows up as `my-mod.cutter.title` in-game.

**Solution.** Everything player-facing is a translation key, not a string. Ship a
`translations.json` next to your DLL and resolve keys with `.T()`.

## The file

```json
{
  "en-US":
  {
    "building-variant.cutter-diagonal.title": "Diagonal Destroyer",
    "building-variant.cutter-diagonal.description": "<gl>Destroys</gl> the <gl>Even Parts</gl> of a shape."
  },

  "pt-BR":
  {
    "building-variant.cutter-diagonal.title": "Eliminadora de diagonais"
  }
}
```

Top level is language code, then flat key → text. Languages may be partial — the
`pt-BR` block above translates only the title, and the description falls back to
`en-US`. Always provide a complete `en-US` block as the fallback.

Copy it to the output on every build:

```xml
<None Update="translations.json">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

## Using a key

```csharp
using Core.Localization;

IText title = "my-mod.cutter.title".T();
```

`.T()` turns a key into an `IText`, which is what every builder wants:

```csharp
BuildingGroup.Create(groupId)
   .WithTitle("my-mod.cutter.title".T())
   .WithDescription("my-mod.cutter.description".T())
```

`IText` is resolved lazily at display time, so it follows the player's language setting
and updates if they change it. Never pass a literal string where an `IText` is
expected — you would hard-code English.

## Language codes

The codes the game recognises, from `BuiltinLanguagesExtensions.Code()`:

| Language | Code | Language | Code |
| --- | --- | --- | --- |
| English | `en-US` | Polish | `pl` |
| German | `de-DE` | Brazilian Portuguese | `pt-BR` |
| Spanish | `es-ES` | Russian | `ru` |
| French | `fr-FR` | Thai | `th` |
| Japanese | `ja` | Turkish | `tr` |
| Korean | `ko` | Traditional Chinese | `zh-Hant` |
| | | Simplified Chinese | `zh-Hans` |

Note the inconsistency — some are language-region (`en-US`, `pt-BR`), some are bare
(`ja`, `ru`). Use exactly what the table says.

## Naming keys

Vanilla uses dotted, kebab-cased paths:
`building-variant.cutter-diagonal.title`. Two workable conventions for a mod:

- **Mimic vanilla** (`building-variant.my-cutter.title`) — fits in, but risks colliding
  with a future vanilla key.
- **Namespace with your mod** (`my-mod.cutter.title`) — collision-free, and obvious in a
  translator's diff which keys are yours. Preferred.

Some keys are **not** yours to name. Keybinding titles are looked up as
`keybinding.<id>` and `keybinding.<id>.description` from the binding's own id, so the
key follows whatever id you registered.

## Markup

Translation text is parsed as extended XML by `TranslationExtendedXMLParser`, and the
tag table is `TagMatch` in `Core.Localization`. These are the tags it accepts:

| Tag | Renders as | Wraps text? |
| --- | --- | --- |
| `<gl>…</gl>` | a **glossary term** — bold, orange `#ff9e16`, on a dark chip; not clickable | yes |
| `<gll:EntryId>…</gll>` | a **glossary link** — the same orange, underlined, navigating to that wiki entry | yes |
| `<b>…</b>` | bold | yes |
| `<info>…</info>` | secondary text — italic, `#ffffff55` | yes |
| `<unit>…</unit>` | unit styling — 65% size, dimmed, letter-spaced | yes |
| `<link:Id>…</link>` | a blue underlined link, resolved against a `MetaWikiEntryContentTextWithLinksData`'s `Links`, else treated as a glossary link | yes |
| `<hotkey:Action/>` | a key chip | no |
| `<icon:IconId/>` | an inline icon | no |
| `<copy-from:other.key/>` | the text of another translation entry, inlined | no |
| `<wip-warning/>` | the work-in-progress warning | no |

**The separator is a colon, not an equals sign.**
`TranslationExtendedXMLParser.ParseTagData` is `inTag.Split(':')`, so it is
`<gll:MyMod_Cutter>`, never `<gll="MyMod_Cutter">`. This is the single easiest thing to
get wrong, because every other markup dialect uses `=`.

```json
"my-mod.wiki.cutter.what":
  "Cuts a shape in half. Feed the halves to a <gll:MyMod_Stacker>Stacker</gll> to make a <gl>compound shape</gl>."
```

A `<gll:…>` target is a **wiki entry id**. `HUDWikiContentRenderer.OnLinkClicked` prefixes
it with `glossary.` and navigates there; an id that does not exist plays an error sound
rather than doing nothing visible. Plain `MetaWikiEntryContentTextData` blocks handle
these links too, so you only need the with-links variant for external URLs.

`<copy-from:…/>` is worth knowing before you duplicate a string: it inlines another
entry's text, so a shared phrase lives in one place. The game leans on it heavily — 336
uses, against 1,700 for `<gl>` and 882 for `<b>`. It resolves against the raw entries and
throws `Copy-from tag key not found` on a key that does not exist.

Anything the table does not list is treated as a **placeholder** and must self-close —
that is what makes `<layer/>` work, and why a mistyped `<layer>` is an unclosed tag
rather than an unknown one.

### Check the markup before shipping

A malformed tag is not cosmetic. `TranslationExtendedXMLParser` throws
`XML Tag not properly closed`, the file fails to load, and the mod is aborted. Nothing in
the build catches it, and the game will not tell you which string was at fault.
`Shapez2-Train-Cargo-Tools/Tools/check_translations.py` is a standalone checker that
walks the file with the same rules — tag names, whether each expects `:data`, whether it
wraps children, and whether a `<gll:…>` target is an entry the mod actually defines.

## Placeholders: bind values with `RawText`, never `.T()`

A key with a self-closing placeholder is filled in with `Bind`:

```json
"my-mod.store.layer": "Layer <layer/>"
```

```csharp
"my-mod.store.layer".T().Bind("layer", new RawText(n.ToString()))
```

`Bind` takes an `IText`, and the obvious-looking way to make one is `.T()` — which is
wrong. `.T()` builds a `LazyLocalizedText`: it treats the string as a **translation id**.
So `n.ToString().T()` asks the resolver for a key named `"1"`, the lookup fails, and
`LazyLocalizedText.Build` emits its miss marker:

```csharp
sb.Append("?");
sb.Append(Id.Id);      // renders "?1"
```

The symptom is a stray `?` in front of the value — `Layer ?1`. Use `RawText`, the
plain-text `IText`, for anything that is already literal: numbers, names, values read
from simulation state.

A `?` prefix anywhere in the UI means the same thing — something was treated as a
translation id and not found.

## Gotchas

- A missing key renders as the raw key. That is your signal the file did not copy, the
  language block is missing, or the key is misspelled — check the output folder first.
- Keys are flat strings, not nested objects. Dots are part of the name, not structure.
- `translations.json` is loaded by the game's mod loader, not by ShapezShifter, so the
  filename and location are fixed by convention: next to your DLL.
