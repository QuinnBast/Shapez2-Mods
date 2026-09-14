# Publish to the Steam Workshop

**Problem.** Your mod works locally. Now it has to reach players.

**Solution.** `steamcmd` plus a `.vdf` manifest. The sample mods ship a working script;
this page is that script explained, including the one line you **must** change.

> [!WARNING]
> Copying a sample's `Steam/` folder brings **three** things you must change, not one.
> `SteamPublish.sh` contains `steamcmd +login lorenzo_tobspr` — a tobspr developer
> account — and `base.vdf` still carries that sample's real `publishedfileid`, title and
> description. Publishing with an inherited id aims your upload at **someone else's
> workshop item**; it will be refused, but check `base.vdf` before your first run rather
> than after. Set `publishedfileid` to `0`, fix the title and description, and replace the
> login with
> your own Steam account name before running anything.

## What you need

- **`steamcmd`** on your `PATH` ([Valve's download](https://developer.valvesoftware.com/wiki/SteamCMD))
- A Steam account that has **accepted the Workshop legal agreement** — do this once in a
  browser, or your first upload is rejected
- **bash with `envsubst` and `cygpath`** on Windows — Git Bash provides both
- A **`preview.png`** — the Workshop requires a preview image

## The layout

```text
MyMod/
├── MyMod.csproj
└── Steam/
    ├── base.vdf            the item manifest
    ├── preview.png         Workshop thumbnail
    └── SteamPublish.sh     the upload script
```

## base.vdf

```text
"workshopitem"
{
    "appid" "2162800"
    "publishedfileid" "0"
    "contentfolder" "${CONTENT_PATH}"
    "previewfile" "${PREVIEW_IMG}"
    "visibility" "2"
    "title" "My Mod"
    "description" "What the mod does"
    "changenote" "${CHANGE_NOTE}"
}
```

| Key | Meaning |
| --- | --- |
| `appid` | always `2162800` — shapez 2 |
| `publishedfileid` | `0` creates a **new** item; a real id **updates** that item |
| `contentfolder` | absolute path to your built mod folder — filled in by the script |
| `previewfile` | absolute path to the thumbnail — filled in by the script |
| `visibility` | `0` public, `1` friends only, `2` private, `3` unlisted |
| `changenote` | optional; the line shown in the item's change history |

Start with `visibility "2"` while testing. A half-broken public item collects
one-star ratings faster than you can fix it.

Substitute `changenote` rather than hard-coding it. `base.vdf` is a checked-in
template that every publish reuses, so a literal note would claim the same change
on every future update — export `CHANGE_NOTE` beside `CONTENT_PATH` and let
`envsubst` fill it, or leave the key out entirely.

### The manifest carries exactly one image

`previewfile` is the thumbnail, and it is the only image `workshop_build_item`
understands. Additional screenshots and videos cannot be uploaded from the command
line at all — they are added in the item's web interface after it exists. The
Steamworks API does expose `ISteamUGC::AddItemPreviewFile` for extra previews, but
steamcmd does not surface it, so there is nothing to put in the manifest.

Plan for that: the first publish creates an item with one image, and the rest of the
store page is filled in on the website.

### Nor the category

The category checkboxes on an item's page — `UI, QoL & Utility`, `Buildings`, and the
rest — are Workshop *tags*, and `workshop_build_item` has no key for them. The
manifest keys it reads are fixed, and they are these:

```text
appid  publishedfileid  filetype  title  description
visibility  previewfile  contentfolder  kvtags  changenote
```

That list is not guesswork: those literals sit together in `steamconsole64.dll`,
bracketed by the command's own `ERROR! Failed to parse build config file` and
`workshop_build_item <build config filename>` strings. A `"tags"` block, which is
what most copied-around sample manifests contain, is simply an unknown key — Valve's
KeyValues parser drops it without complaint, so it looks like it worked.

`kvtags` is a near-miss, not the answer. It is `ISteamUGC::AddItemKeyValueTag` —
arbitrary key/value metadata for API queries, invisible on the store page. The
categories come from `SetItemTags`, which steamcmd never calls.

So set the category once on the item's web page. It is a property of the item rather
than of an upload, and because `workshop_build_item` never touches tags, later
publishes leave it alone — you will not have to set it again.

## How the script works

```bash
CONTENT_PATH=$1                          # passed in by the csproj target
CURRENT_DIR=$(cygpath -w "$PWD")         # POSIX path → Windows path
PREVIEW_IMG=$CURRENT_DIR\\Steam\\preview.png

export CONTENT_PATH
export PREVIEW_IMG
envsubst < Steam\\base.vdf > Steam\\base.tmp.vdf   # substitute the two paths

steamcmd +login YOUR_ACCOUNT +workshop_build_item "$TMP_VDF" +quit

# read the id Steam assigned and write it back into base.vdf
FILE_ID=$(grep '"publishedfileid"' Steam\\base.tmp.vdf | sed 's/.*"\([0-9]\+\)".*/\1/')
sed -i 's/\("publishedfileid"[ \t]*"\)[0-9]\+"/\1'"$FILE_ID"'"/' Steam\\base.vdf
```

The clever part is the last step: after a successful first upload, Steam writes the new
`publishedfileid` into the temp manifest, and the script copies it back into
`base.vdf`. Every later run therefore **updates** the same Workshop item instead of
creating duplicates.

**Commit `base.vdf` after your first publish.** Losing that id means losing the ability
to update your own item, and there is no way to reclaim it other than publishing a new
one and asking players to re-subscribe.

## Wiring it to the build

```xml
<Target Name="SteamPublish">
  <Exec Command='sh .\Steam\SteamPublish.sh "$(OutputPath)"' />
</Target>
```

```bash
dotnet build MyMod.csproj -t:SteamPublish
```

`$(OutputPath)` is your mod folder, so the upload always ships exactly what the game
loads. Note the closing quote after `$(OutputPath)` — the version in the
`PlatformEfficiencyOverlay` sample is missing it.

## What gets uploaded

Everything in `contentfolder`. That should be:

```text
MyMod.dll
manifest.json
translations.json
Resources/…
```

It should **not** be game assemblies (that is what `<Private>False</Private>` prevents),
`.pdb` files unless you want them shipped, or your `Steam/` folder. Look in the output
folder before your first upload — whatever is there is what the world gets.

## Before you go public

- `manifest.json` `Version` bumped, and `GameVersionSupportRange` honest about what you
  have tested
- `Dependencies` lists ShapezShifter (`steam:3542611357`) with a version range
- `AffectsSaveGames` correct — `true` if you write save data, `false` for a read-only
  overlay
- Tested from a **subscribed copy**, not your build output. Subscribing installs to the
  Workshop content folder, which is a different path from `SPZ2_PERSISTENT\mods` and
  catches "works on my machine" path bugs

## After publishing, unsubscribe before you keep developing

Once you subscribe to your own item, **your local build stops being what runs** — and the
game gives you no hint of it.

The game enumerates both `mods/<Mod>` and the subscribed
`steamapps/workshop/content/2162800/<id>/`, and loads both. It does not notice they are the
same mod. In the log that reads:

```text
Loading M:\SteamLibrary\steamapps\workshop\content\2162800\<id>\MyMod.dll
...
Loading C:\…\mods\MyMod\MyMod.dll
```

Two things then go wrong at once.

**Your local DLL never actually runs.** Both files carry the same assembly identity — same
name, same version — and `Assembly.LoadFrom` binds by identity, so the second call hands
back the assembly already loaded from the Workshop folder. The tell is in the resource
paths: the *local* mod's `ModDirectoryLocator` resolves
`typeof(TMod).Assembly.Location` to the **Workshop** directory, so it loads Workshop icons
while claiming to be the local copy. (This is the same `LoadFrom` behaviour that forces a
hot reloader onto `Assembly.Load(byte[])`.)

**And the mod initialises twice.** The game constructs an instance per discovered folder,
so every constructor side effect happens twice — two sets of rewirers, two island
registrations. The second `IslandBuilder.BuildAndRegister` then throws
`An item with the same key has already been added`, which cascades into a completely
unrelated-looking `An island group with id HUB already exists`. See
[Debugging](debugging.md).

So while developing: **unsubscribe from your own Workshop item**, or delete `mods/<Mod>`
and test the subscribed copy deliberately. Keeping both is the worst of the two, because
you will be reading results from code you did not build. Counting rewirer handles in the
log is the quick check — a doubled mod registers the same sequence twice.

## Gotchas

- **First upload with `publishedfileid "0"`, then never again.** Leaving it at `0`
  creates a fresh item every run.
- The `cygpath`/`envsubst`/double-backslash dance in the script exists because
  `steamcmd` wants Windows paths while the script runs in bash. On Linux or macOS you
  will need to simplify those lines.
- `steamcmd` may prompt for Steam Guard on first login. Run it once interactively before
  wiring it into a build.
- Workshop descriptions take BBCode, not Markdown.
