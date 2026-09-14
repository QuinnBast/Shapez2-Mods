# Add content to shapez 2

## Content needs four things, not one

A new building or island is not done when it has a definition. It needs **all four** of:

| Part | Without it |
| --- | --- |
| Definition (`Building.Create` / `Island.Create`) | nothing exists |
| Research unlock (`Unlocked…`) | the toolbar entry is silently hidden |
| Toolbar entry (`.InToolbar`) | the player cannot select it |
| Translations (`translations.json`) | the player sees a raw key like `my-mod.cutter.title` |

Missing the unlock and missing the toolbar index look identical in-game — content that
is simply not there. Wire all four before testing any of them, or the first failure
hides the rest.

**Start by copying the closest official sample rather than writing from blank.**
`DiagonalCutter` is the only complete worked example of the full building stack;
`BiggerPlatforms` is the reference for multi-chunk island layouts; `SandboxIslands` is an
island with a simulation. Adapting one of those is much faster than deriving the data.

## The chain is a state machine

Every stage returns a *different interface*, so the compiler enforces the order.

```csharp
AtomicBuildings.Extend()
   .AllScenarios()
   .WithBuilding(buildingBuilder, buildingGroupBuilder)
   .UnlockedWithNewSideUpgrade(sideUpgradeBuilder)
   .WithDefaultPlacement()
   .InToolbar(toolbarLocation)
   .WithSimulation(new MyFactoryBuilder(), logger)
   .WithAtomicShapeProcessingModules(BuiltinResearchSpeed.CutterSpeed, 2.0f)
   .WithPrediction(new MyPredictionFactoryBuilder(), logger)
   .Build();
```

**If a method you expect is missing, you are at the wrong stage — not missing a
`using`.** That is the single most useful thing to know about this API, and the mistake
costs more time than anything else in the chain. `InToolbar` in particular exists only
after placement and before simulation.

Nothing is registered until `.Build()`.

Islands use `AtomicIslands.Extend()` with the same shape, two differences: island
`WithSimulation` takes no logger, and the modules stage is an explicit
`WithoutModules()` — a platform with no HUD panel is normal, so you say so rather than
skipping the stage.

## Build the chain in the `IMod` constructor

Content registration happens at mod load, not per session. But note the asymmetry that
causes real bugs: the fluent chain runs **once**, while `BuildAndRegister` is called
again **for every scenario load** against those same definition objects.

So anything attached during registration must be idempotent:

- Use `AttachOrReplace`, or guard with `Has<T>`. Plain `Attach` is a add, so a second
  copy sits *beside* the first — and `CustomDataHolder` reports a multiple match as
  found-nothing. The symptom is content that works on a new save and silently stops
  working on a loaded one.
- `Detach<T>()` resolves with `Get<T>()` first and throws
  `NoDataFitDataTypeQueryException` when there is nothing there. `RemoveFlag<T>()` is
  `Detach<T>()` renamed.

## Ids are permanent

`BuildingDefinitionId` and `IslandDefinitionId` are written into save files. Renaming one
breaks every save that placed the content. Choose ids as if they ship.

## Research unlock — prefer ids over indices

| Call | Use when |
| --- | --- |
| `UnlockedAtMilestone(selector)` | simplest — available from some tier onward |
| `UnlockedWithExistingSideUpgrade(selector)` | it belongs with a vanilla upgrade |
| `UnlockedWithNewSideUpgrade(builder)` | it wants its own research node, cost and description |

```csharp
.UnlockedAtMilestone(new ByIdMilestoneSelector(new ResearchUpgradeId("Milestone_Initial")))
```

Milestone **indices** shift between game versions; milestone **ids** are stabler. Use
`^1` only when you genuinely mean "the final milestone".

**Scenarios use different milestone ids.** A hard-coded id that works in the standard
scenario can be wrong in the converter scenario, and the symptom is content that never
unlocks in one scenario only. Use `ByIdPerScenarioMilestoneSelector` when that matters.

Get the real ids from `debug.export-game-data` in the `F1` console. Do not guess them —
authored scenario data is not in the decompiled assemblies, so a guessed id cannot be
verified statically.

For a new side upgrade, the `ResearchUpgradeId` in `SideUpgradePresentationData` is the
*parent* node it attaches to, and the category string (e.g. `"Buildings"`) must match an
existing research category or the node has nowhere to render. `CopyingRequirements(...)`
clones an existing upgrade's prerequisites instead of enumerating them by hand.

## Toolbar — positional, and the indexing is asymmetric

```csharp
.InToolbar(ToolbarElementLocator.Root().ChildAt(0).ChildAt(2).ChildAt(^1).InsertAfter())
```

Read right to left: the last child of the third child of the first child of root, insert
after it.

**Prefer `ChildAt(^1).InsertAfter()` for the leaf.** Two reasons:

1. It survives game updates — new vanilla entries also land at the end of a category.
2. It sidesteps a real asymmetry. `FindElementParent` filters `ToolbarSlotSeparator` out
   before indexing, but `ToolbarEntryLocation`'s `Before`/`After` compute the final index
   from `parent.Children.Count()`, which **counts** separators:

   ```csharp
   int num = index.IsFromEnd ? (parent.Children.Count() - index.Value) : index.Value;
   ```

   So a numeric leaf index lands one slot later for every separator ahead of it, while
   `^1` appends correctly either way. Count with separators excluded for every hop
   *except* the last.

Nothing looks broken when this is wrong — the entry appears, in the wrong category.

Island categories are nowhere near building ones: `SandboxIslands` anchors at
`ChildAt(5).ChildAt(4)`.

To insert by name instead of index, implement `IToolbarEntryInsertLocation` yourself — it
receives the whole `ToolbarData`. Match on the title's `TranslationId`, not displayed
text, and note two traps in those ids: **they end in `.title`**
(`island-toolbar.category-Rail.title`, not `island-toolbar.category-Rail`), and casing is
inconsistent (`category-RegularPlatform` but `category-converters`). Make it idempotent —
`AddEntry` runs once per registered entry, so a mod adding five islands to one new group
calls it five times and must get the same group each time.

A no-op `AddEntry` is also the only way to register a definition that should exist
*without* being individually placeable, since `.InToolbar(...)` is mandatory.

## Translations

`translations.json` sits next to the DLL, loaded by the game's mod loader.

```json
{
  "en-US": {
    "my-mod.cutter.title": "Diagonal Destroyer",
    "my-mod.cutter.description": "<gl>Destroys</gl> the <gl>Even Parts</gl> of a shape."
  }
}
```

**Top level is the language code, then flat key → text.** Getting that nesting backwards
aborts mod load. So do placeholders written any way other than self-closing (`<layer/>`).

Always ship a complete `en-US` block — other languages may be partial and fall back to it.
Language codes are inconsistent by design: `en-US`, `pt-BR`, but bare `ja`, `ru`, `pl`,
`tr`. Use exactly what `docs/howto/add-translations.md`
lists.

Namespace keys with the mod name (`my-mod.cutter.title`) rather than mimicking vanilla —
collision-free, and obvious in a translator's diff.

### Bind values with `RawText`, never `.T()`

```csharp
"my-mod.store.layer".T().Bind("layer", new RawText(n.ToString()))
```

`.T()` means *translation id*. `n.ToString().T()` asks the resolver for a key named
`"1"`, the lookup fails, and it renders `Layer ?1`. **A `?` prefix anywhere in the UI
means something was treated as a translation id and not found.**

Copy both `translations.json` and `manifest.json` to output on every build:

```xml
<None Update="translations.json">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

## Island-specific traps

### Always `WithPerChunkColliders()`

`WithBoundingCollider()` sizes one box as `(max - min) * 20`, which is one chunk short on
every axis — and since every island is a single layer deep, `min.z == max.z`, so the box
is **always zero height**. A single-chunk island gets a box of zero size in all three
axes.

The symptom is that the cursor passes straight through: the island cannot be hovered,
selected, pipetted or deleted, while everything vanilla around it behaves normally.

**The official `BiggerPlatforms` and `SandboxIslands` samples both call
`WithBoundingCollider()`.** Do not take them as evidence it works.

### Never detach `IslandFrameDrawData`

Vanilla attaches it to *every* island unconditionally, space belts included, just with an
all-false context. `IslandChunkPlatformFramesCache.RegisterIsland` early-returns for an
island that lacks it, so its chunks never enter the cache — while `IslandFramesDrawer.Draw`
calls `GetEntry` for every culled chunk with no guard. The result is a
`KeyNotFoundException` every frame forever, which also aborts `MapDrawer.Draw` partway and
silently kills whatever draws after it.

For an island that should have no frame, pass a zeroed drawing context instead:

```csharp
.WithRenderingOptions(new HomogeneousChunkDrawing(default), drawPlayingField: false)
```

### The pipette map is a `Dictionary.Add`

`.WithDefaultPlacement()` already claimed the island. Registering a second placer over the
same definitions — a path placer over a family of forwards and turns, say — makes the
second `Add` throw `An item with the same key has already been added` during
`PlayerInteractionOrchestrator`'s constructor, **before the main menu appears**.

The chain offers no way to skip `WithDefaultPlacement()`, so hand your own placer a
throwaway `Dictionary<IEntityDefinition, PipettePlacementRequest>` instead of the real
`IslandInitiatorsParams.PipetteMap`. Do not rely on which rewirer runs first — fix it so
either order works.

### Flipping with F is group membership, not a flag

`WithInteraction(flippable: true, …)` alone does nothing. `IslandPlacersCreator` decides by
**counting** the group's `IslandGroupCollection`: one definition gets a non-flippable
placer, two get the flippable pair, three or more throws. So F needs a second mirrored
definition in the same group, registered from a single `IIslandBuilder`.

Attach `FlippableDefinition` both ways — the placer works off group membership, but
`IslandBlueprintProcessor` reads that CustomData, and without it a flipped island comes back
unflipped out of a blueprint. And note the mirror never travels the extender chain: its
simulation, prediction and side-panel provider are all keyed by definition id and each needs
registering separately.

### Most `IslandGroup.Create(...)` options are never read

`IslandGroupBuilder.BuildAndRegister` attaches only `GroupPresentationData`. So
`AsNonTransportableIsland`, `WithPreferredPlacement`, `Removable`, `AutoConnected` and
`AllowedOnNotches` have no effect — a declared `DefaultPreferredPlacementMode` never
reaches the definition and `CreateDefaultPlacer` falls through to single placement. Attach
it to the definition yourself if you need it.

## Do not guess authored values

Island definitions for train stations, toolbar structure, base processing durations and
scenario JSON are all authored ScriptableObject data — **not** in the decompiled
assemblies. You cannot read them statically.

Source them at runtime from `GameMode`/config, or leave them out and say so. A
confidently wrong constant in the UI is worse than no number.

Related: `WithEfficiencyData(new BuildingEfficiencyData(duration, laneCount))` must pass
the same duration the simulation actually uses, or the vanilla efficiency panel lies.

## Changing content requires a restart

New islands, new toolbar entries, and collider or definition changes are built per session
and cannot be hot reloaded. Logic and detours can. Reloading and re-entering a session
double-registers definitions and crashes on a duplicate key.

## Deeper reading

Long-form pages in the community docs repo (`shapez2-modding-docs`):


`docs/howto/add-a-building.md` ·
`docs/howto/add-an-island.md` ·
`docs/howto/add-to-toolbar.md` ·
`docs/howto/add-research-unlock.md` ·
`docs/howto/add-translations.md` ·
`docs/howto/load-models-and-icons.md` ·
`docs/simulations-and-lanes.md`
