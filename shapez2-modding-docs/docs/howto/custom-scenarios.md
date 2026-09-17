# Custom scenarios and game modes

**Problem.** You want to change the rules — starting content, progression, limits — not
add a building.

There are **two** routes, and they are for different things.

## Route 1: JSON, no code

The game exports its own content and reads modified copies back:

```text
F1
debug.export-game-data
```

Edit the exported JSON, then place your scenario in the `custom-scenarios` folder:

```text
%USERPROFILE%\AppData\LocalLow\tobspr Games\shapez 2\custom-scenarios\
```

There is also `custom-scenario-parameter-presets\` alongside it for parameter presets.

This is the documented, supported path, and the
[wiki's Custom Game Modes page](https://shapez2.wiki.gg/wiki/Custom_Game_Modes) covers
it properly — start there. No compilation, no API, and it survives game updates far
better than code does.

**Use JSON when** you are changing values, starting conditions, progression order, or
available content.

## Route 1b: the same JSON, shipped inside your mod

A scenario does **not** have to live in the persistent folder. `ModdedScenarios` walks
every resolved mod and reads two folders beside its `manifest.json`:

```text
<your mod>/scenarios/*.json
<your mod>/scenario-presets/*.json
```

Everything found is concatenated with the built-in scenarios in `LoadGameDataBlindStep`
and goes through the same `ScenarioReader` and `ScenarioPresetCollection`, so a scenario
you ship is indistinguishable from one the game ships. Copy the files into the output
directory and you are done:

```xml
<None Update="scenarios\*"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
<None Update="scenario-presets\*"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
```

### Do not start from the export

`debug.export-game-data` is the obvious starting point and it is the wrong one. The
exported `default-scenario.json` is 144 KB; the asset the game actually ships is **1.7 KB**,
because a real scenario is almost nothing but references:

```json
{
  "FormatVersion": 3,
  "UniqueId": "default-scenario",
  "Progression": {
    "Levels": "#include:Scenarios/Classic/Regular/MilestoneReferences",
    …
  },
  "StartingLocation": "#include:Scenarios/Classic/DefaultData/StartingLocation",
  "ToolbarConfig": "#include_raw:Scenarios/Classic/DefaultData/Toolbar/ToolbarConfigWithConverters"
}
```

The export is that file with every reference **resolved and inlined**, and it does not load
back. Two independent reasons:

- `ScenarioReader` gates on the literal substring `"FormatVersion": 3,` **with that exact
  space**, while the export is minified to `{"FormatVersion":3,`. The game's own export
  fails the game's own format check.
- `ToolbarConfig` is a `#include_raw:` - `ScenarioRawIncludeJsonConverter` loads the
  referenced asset's text **verbatim into a string field** and declares `CanWrite => false`,
  so serialising writes `{"ToolbarDataJson": "…escaped JSON…"}` instead. That escaped blob
  still contains `\"#include:…\"` lines, and the pre-processor's pattern is
  `"\"#include:(?<path>[^\0\"]+)\""` - a plain quote, which matches the quote *inside*
  `\"`. The captured path picks up a trailing backslash and the load dies with
  `Could not resolve path Scenarios/…/ShapeBuildings\`.

So **copy the shipped asset, not the export**. It lives in `resources.assets` next to the
managed folder and is plain UTF-8 - find `"FormatVersion": 3,`, walk back to the `{` and
brace-match forward. Change the `UniqueId`, `Title` and `Description` and leave every
`#include:` alone: they resolve through `Resources.Load`, which works exactly the same for
a scenario shipped in a mod folder, and the file then tracks the game's data across
updates instead of freezing a copy of it.

> [!WARNING]
> **An unresolvable `#include:` does not skip your scenario, it stops the game booting.**
> `TryReadingScenario` resolves includes *before* its `try` block, so the
> `Could not resolve path …` exception comes out through `GameData`'s constructor and
> `LoadGameDataBlindStep`. Since your file names the game's own resource paths, a game
> update that moves one bricks the install. Hook `ScenarioReader.TryReadingScenario`,
> wrap the call for *your* file only, and return false on a throw.

Three more things will bite:

- **A scenario needs a matching preset.** `HUDMenuSelectScenarioState.StartScenarioConfig`
  finds presets with `p.Parameters.ScenarioId == scenario.UniqueId` and calls `First()` on
  the result. A scenario with no preset does not appear disabled - clicking it throws.
- **`#include:` substitutes a whole value, and there is no merge.**
  `"MapGenerationParameters": "#include:Scenarios/SharedData/BaseMapGenerationParameters"`
  is all-or-nothing: to change one number you must inline every number, including
  `ShapePatchGenerationLikeliness`, whose contents are in a Unity `TextAsset` that
  `debug.export-game-data` writes out as the unresolved `#include:` line. If you only want
  to change scalars, keep the include and overwrite the fields at runtime instead - the
  resolved `MapGenerationParameters` hangs off every `GameScenarioParametersPreset`, and
  the menu copies it by value (`ScenarioParameters.AssignFrom(preset.Parameters)`), so a
  stamp applied before the copy becomes the starting values the player then sees in the
  scenario config dialog.
- **One bad preset drops every preset after it.** `ScenarioPresetCollection`'s loop
  `break`s on a deserialisation failure rather than continuing, and mod presets are
  concatenated after the built-in ones - so your broken file costs you yours and any
  loaded later, with a single `Failed ot load scenario parameter preset` line in the log.

Translation ids work as usual: `GameDataUtils.ResolveUserTranslationId` treats a leading
`@` as a translation key, so `"Title": "@scenario.mine.title"` resolves against your
`translations.json`.

### The picker does not scroll

`HUDMenuSelectScenarioState` places its cards straight into a `RectTransform` with a layout
group and no scroll view. The seven the game ships fit; the eighth does not, and there is
nothing to reach it with. Adding a scenario means adding the scroll view too - wrap
`UIScenariosParent` in a `ScrollRect` with a `RectMask2D` and a `ContentSizeFitter`, and
check `GetComponentInParent<ScrollRect>()` first so two mods do not both do it. The wheel
then works without further effort: `GameInputManager.RaytraceUIHoverState` already looks for
a `ScrollRect` under the pointer.


## Route 2: code, via the scenario interceptor

```csharp
using ShapezShifter.Hijack;

public class MyScenarioRewirer : IGameScenarioRewirer
{
    public GameScenario ModifyGameScenario(GameScenario gameScenario)
    {
        // inspect and return a modified scenario
        return gameScenario;
    }
}

RewirerHandle handle = GameRewirers.AddRewirer(new MyScenarioRewirer());
```

Note the signature **returns** a `GameScenario` — so this rewirer can transform or wholly
replace the scenario the game is about to use, not merely append to it.

**Use code when** the rule you want cannot be expressed as data: behaviour that depends
on runtime state, or a mechanic that needs new logic.

This is also the hook Flow uses under the hood — `GameScenarioBuildingExtender` and
`GameScenarioIslandExtender` are how `AtomicBuildings.Extend().AllScenarios()` gets your
building into scenario data. So if all you want is to add content to every scenario, use
Flow and let it do this for you.

## Scenario ids and per-scenario behaviour

Scenarios are identified by `ScenarioId`, and content can target them selectively.
`.AllScenarios()` is the blanket option; the per-scenario selectors exist because
scenarios genuinely differ:

```csharp
.UnlockedAtMilestone(new ByIdPerScenarioMilestoneSelector(scenarioId =>
    new ResearchUpgradeId(scenarioId == converterScenario
        ? "ConverterMilestoneTier_Initial"
        : "Milestone_Initial")))
```

Milestone ids are **not** shared between scenarios. This is the single most common
scenario-related bug in mods — see
[add a research unlock](add-research-unlock.md#unlock-at-a-milestone).

## Which route to pick

| Want | Route |
| --- | --- |
| A scenario that ships with your mod | JSON in `scenarios/` |
| Different starting shapes, costs, limits | JSON |
| A different progression order | JSON |
| Content added to all scenarios | Flow (`.AllScenarios()`) |
| A rule that depends on runtime state | `IGameScenarioRewirer` |
| To replace a scenario entirely | `IGameScenarioRewirer` |

Prefer JSON. It needs no build step, no API compatibility, and players can inspect and
share it.

## Gotchas

- **A code-modified scenario affects saves.** Set `AffectsSaveGames: true` in
  `manifest.json` and think about what happens to a save if the player disables your
  mod.
- Exported game data is a snapshot of *your* game version. Re-export after an update
  rather than carrying an old file forward.
- Scenario changes and content additions interact: content added via Flow lands in
  scenario data, so a rewirer that replaces the scenario can drop other mods' content.
  Transform, do not replace, if you want to coexist.
- A custom scenario is not shipped *inside* your DLL, but it can be shipped **beside**
  it - see [Route 1b](#route-1b-the-same-json-shipped-inside-your-mod). The persistent
  `custom-scenarios\` folder is the route for a scenario with no mod attached.

> [!NOTE]
> The `GameScenario` type is large and no sample mod modifies it directly. This page
> covers the entry point honestly; the shape of what you can change inside a
> `GameScenario` is unexplored — inspect <xref:Root.GameScenario> in the API
> reference before committing to this route.
