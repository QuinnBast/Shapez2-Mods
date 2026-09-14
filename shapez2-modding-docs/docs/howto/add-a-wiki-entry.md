# Add a wiki entry

**Problem.** Almost every vanilla machine has a page in the knowledge panel. A modded
one has none, and there is no ShapezShifter API for it — `grep -rli wiki` over
ShapezShifter finds only incidental matches in the island and building builders.

**Solution.** The game keeps a wiki page in two halves, in two different objects. Both
have to land, and landing only one is worse than landing neither.

| Half | Lives in | How a mod reaches it |
| --- | --- | --- |
| The **reference** — which entry ids exist, the category each sits in, the research each waits for | `ResearchProgression.WikiConfiguration.WikiReferences` | already handed to every `IIslandResearchProgressionExtender` |
| The **entry** — a `MetaWikiEntry` per id, holding the content blocks | `GameData._WikiEntries`, read through `IGameData.GetWikiEntry` | a detour on `GameData.GetWikiEntry` |

`WikiDatabase`'s constructor walks every reference and calls `data.GetWikiEntry(...)` for
each. A reference whose entry cannot be found throws out of that constructor and takes
the session with it — so **register no references unless the entry half is in place.**

## Serving the entries

```csharp
private delegate MetaWikiEntry GetEntryOrig(GameData self, WikiEntryId id);
private delegate MetaWikiEntry GetEntryHook(GetEntryOrig orig, GameData self, WikiEntryId id);

new Hook(typeof(GameData).GetMethod(nameof(GameData.GetWikiEntry)),
    new GetEntryHook((orig, self, id) =>
        Entries.TryGetValue(id, out MetaWikiEntry mine) ? mine : orig(self, id)));
```

`_WikiEntries` is publicized and could simply be added to. Don't: `GameData` outlives a
session while a `ResearchProgression` does not, so an insert runs again on the next
scenario load and `Dictionary.Add` throws on the duplicate key — the same trap the
pipette map sets. A detour is indifferent to how many sessions have been loaded.

## Building an entry

`MetaWikiEntry` is a `ScriptableObject` whose **id is its `name`** — `Id => new
WikiEntryId(base.name)`, with no id field to set:

```csharp
MetaWikiEntry entry = ScriptableObject.CreateInstance<MetaWikiEntry>();
entry.name = "MyMod_Cutter";              // this is the id
entry.ShowInKnowledgePanel = true;
entry.RelatedEntries = new[] { new WikiEntryId("MyMod_Overview") };
entry.Contents = new IWikiEntryContentData[] { heading, text };
```

Content blocks are plain `[Serializable]` classes, so `new` works once their fields are
publicized. The useful ones are `MetaWikiEntryContentHeadingData`,
`…TextData`, `…TextWithLinksData`, `…ImageData` and `…VideoData`.

```csharp
static SerializedTranslationId Translation(string key)
{
    SerializedTranslationId id = default;
    id.Id = new TranslationId(key);          // public setter; no need for the private field
    return id;
}

var text = new MetaWikiEntryContentTextData { Text = Translation("my-mod.wiki.cutter.what") };
```

`SerializedTranslationId.T()` returns **null** — not an empty text — for an unset or
`<none>` id, and the content renderers do not expect a null. Give every block a real key.

## Registering the reference

```csharp
wiki.WikiReferences.Add(new UnlockableWikiReference(
    new SerializedUnlockableWikiReference
    {
        EntryId = "MyMod_Cutter",
        CategoryId = category.Id,
        RequiredUpgradeIds = new[] { "MyMod_CutterUnlock" },
        RequiredMechanicIds = Array.Empty<string>(),
        UnavailableIfLocked = false,
    },
    Array.Empty<ResearchMechanicId>(),
    Array.Empty<ResearchUpgradeId>()));
```

Gate the entry on whatever unlocks the thing it describes, which is what vanilla does and
why `UnlockableWikiReference` carries requirements at all. `UnavailableIfLocked = false`
greys the entry while locked rather than hiding it, so the wiki agrees with the research
tree, which already lists the node.

Guard the whole registration — it is normally driven from the research extenders, which
run **once per island or building group**, and adding the same reference five times lists
the entry five times:

```csharp
if (wiki.WikiReferences.Any(r => r.EntryId.Id == MyFirstEntryId)) { return; }
```

## Read the vanilla entries first

They are authored ScriptableObjects, so they cannot be read out of `decompiled/` — but
`debug.export-game-data` in the `F1` console writes them to
`<persistent>/basedata-v<N>/`, and two files there are what you want:

| File | What it gives you |
| --- | --- |
| `translations-en-US.json` | every vanilla entry's text under `wiki.*`, in the `Entries` object |
| `scenarios/<id>.json` | `Progression.WikiConfiguration` — every entry id, its category, and the category list with icon ids |

Do this before writing a line. Four conventions are obvious from the data and guessable
from none of it:

- **Ids are `WK<Category>_<Thing>`** — `WKTrains_TrainStations`, `WKIslands_SpaceBelts`.
- **Text keys are `wiki.<id>.text-intro-1`**, then `-2`, `-3` for blocks after an image,
  and `wiki.<id>.text-robot`.
- **No vanilla entry uses a heading.** `MetaWikiEntryContentHeadingData` exists and goes
  unused across all 149 of them; pages are broken up with `

` paragraphs and images.
  A page built from headings looks immediately foreign.
- **Most entries close on `text-robot`** — 60 of 149 — a dry one-liner in the game's
  narrator voice, rendered by `MetaWikiEntryContentNarrativeTextData` in its own style.
  *"Belts don't judge. They'll happily carry the wrong shape to the wrong place at maximum
  speed."*

The prose itself is short, second person, and links nearly every noun that has an entry
of its own.

## Writing the text like a vanilla entry

Vanilla entries do two things a first attempt usually misses, and both live in the
translation string rather than in the content blocks:

- **They highlight their own concepts** with `<gl>…</gl>`, which renders bold orange on a
  dark chip. Use it on a term the entry is teaching, at its first mention — not on whole
  sentences.
- **They link to related content** with `<gll:EntryId>…</gll>`, an orange underlined link
  that navigates to that wiki entry. Plain `MetaWikiEntryContentTextData` handles these;
  `…TextWithLinksData` is only needed for external URLs.

```json
"my-mod.wiki.cutter.use":
  "Feed one shape type per floor. The halves can go straight to a <gll:MyMod_Stacker>Stacker</gll>, or be packed into a <gl>cargo container</gl> first."
```

The separator is a colon — `<gll:MyMod_Stacker>`, never `=`. See
[Add translations](add-translations.md) for the full tag table and a checker script;
a malformed tag aborts the mod rather than rendering oddly.

`RelatedEntries` on the `MetaWikiEntry` is a separate mechanism: it renders a
"Related entries" strip at the foot of the page. Use both — the strip for neighbours, the
inline links for concepts named in the prose.

## Pictures

`MetaWikiEntryContentImageData` holds a **`Sprite` directly**, not a `GameImageId`, so a
wiki picture needs no registration at all — load a PNG from your mod folder and assign it:

```csharp
new MetaWikiEntryContentImageData { Image = FileTextureLoader.LoadTextureAsSprite(path, out _) }
```

That is unlike a research node's preview image, which is looked up by id and has to be
served (see [Add a research unlock](add-research-unlock.md)). Drop the block entirely if
the sprite fails to load; `WikiEntryContentImage` holds it directly and the renderer does
not expect a null.

`MetaWikiEntryContentVideoData` takes a `GameVideoId` and is authored-asset territory, so
a short looping clip is not available to a mod the way a still is.

## Categories

A category is a tab. `WikiConfiguration.Categories` is a mutable `List<WikiCategory>`, so
a mod can add its own rather than guess at a vanilla one — category ids are authored per
scenario, exactly like research categories.

```csharp
wiki.Categories.Add(new WikiCategory(new SerializedWikiCategory
{
    Id = "MyMod_Category",
    IconId = borrowedIconId,                  // must resolve — see below
    TitleId = "my-mod.wiki.category.title",
    RequiredUpgradeIds = Array.Empty<string>(),
    RequiredMechanicIds = Array.Empty<string>(),
}));
```

The icon is not optional, for the same reason a research node's preview image is not: icon
ids live in Unity assets, so the only one certain to resolve is one the game is already
drawing. Borrow the first non-empty `IconId` off the existing categories.

Note that `WikiConfiguration`'s constructor validates that every reference's category
exists and throws `Configuration Error: Category '…' not found` if not — but that runs at
construction, and a mod adds its references afterwards, so nothing will check yours. Add
the category first.

## Ordering

`Register` runs from a research extender; ShapezShifter drives those from
`GameScenarioInterceptor`, an `ILHook` on `GameMode.From` that fires **immediately after
`new GameScenario(...)`**. `GameMode.From` constructs `new WikiDatabase(...)` thirteen
lines later off the same `gameScenario.Progression`, so the references are always in place
before anything reads them.

Nothing enforces that. If the hook ever moved later, entries would silently stop appearing
rather than fail loudly — worth knowing when one mysteriously does not show up.

## Gotchas

- **The title key is fixed.** `WikiDatabase.Convert` builds it as
  `("wiki." + entry.Id.Id + ".title").T()`. An entry called `MyMod_Cutter` needs
  `wiki.MyMod_Cutter.title` in `translations.json` and nothing else will do. The content
  keys are yours to name; this one is not.
- **`MetaWikiEntryContentIslandPanelData` is out of reach for modded content.** It holds a
  `MetaIslandDefinitionId` — a reference to an authored asset — so a modded island cannot
  supply one. Heading, text and image blocks work.
- **Fail soft.** A throw in a mod constructor is not contained by `ModLoader`; it comes out
  through `ModLoadingStep.LoadMods` and kills the game's whole mod loading step. A wiki page
  is documentation — log and skip if the detour target has moved, rather than throwing.
- Entries also need `RelatedEntries` to link to each other; ids that do not exist are
  simply not rendered.
