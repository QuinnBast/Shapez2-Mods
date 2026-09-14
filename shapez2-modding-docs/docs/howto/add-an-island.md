# Add an island or platform

**Problem.** You want a new platform shape, or an island with its own behaviour.

**Solution.** `AtomicIslands.Extend()` — the same state-machine chain as
[buildings](add-a-building.md), with island-shaped stages.

```csharp
AtomicIslands.Extend()
   .AllScenarios()
   .WithIsland(islandBuilder, islandGroupBuilder)
   .UnlockedAtMilestone(new ByIndexMilestoneSelector(^1))
   .WithDefaultPlacement()
   .InToolbar(ToolbarElementLocator.Root().ChildAt(5).ChildAt(4).ChildAt(^1).InsertAfter())
   .WithSimulation(new MyIslandFactoryBuilder())
   .WithoutModules()          // or WithCustomModules(...)
   .Build();
```

Two differences from the building chain worth noting: island `WithSimulation` takes no
logger, and the modules stage has an explicit `WithoutModules()` — a platform with no
HUD panel is normal, so you say so rather than skipping the stage.

## The group

```csharp
IslandDefinitionGroupId groupId = new("MyIslandGroup");

ModFolderLocator resources = ModDirectoryLocator.CreateLocator<MyMod>().SubLocator("Resources");

IIslandGroupBuilder group = IslandGroup.Create(groupId)
   .WithTitle("my-mod.island.title".T())
   .WithDescription("my-mod.island.description".T())
   .WithIcon(FileTextureLoader.LoadTextureAsSprite(resources.SubPath("Island.png"), out _))
   .AsNonTransportableIsland()
   .WithPreferredPlacement(DefaultPreferredPlacementMode.Area);
```

`DefaultPreferredPlacementMode.Area` is the drag-a-rectangle behaviour you want for
platforms; buildings usually want `LinePerpendicular`.

## The island

```csharp
ChunkLayoutLookup<ChunkVector, IslandChunkData> layout = FoundationLayout();

IIslandBuilder island = Island.Create(new IslandDefinitionId("MyIsland"))
   .WithLayout(layout)
   .WithPerChunkColliders()          // never WithBoundingCollider - see below
   .WithConnectorData(FoundationConnectors(layout))
   .WithInteraction(flippable: false, canHoldBuildings: false)
   .WithDefaultChunkCost()
   .WithRenderingOptions(ChunkDrawingOptions(), drawPlayingField: true);
```

`canHoldBuildings: false` makes it a fixed-function island (the trash island in
`SandboxIslands`); `true` makes it a build surface (the foundations in
`BiggerPlatforms`).

## The layout is the hard part

An island's shape is a `ChunkLayoutLookup` mapping `ChunkVector` → `IslandChunkData`,
and there is **no fluent builder for it yet** — the sample carries a
`// TODO: Create fluent API for this` comment. You construct chunk data directly:

```csharp
IslandChunkData chunkData = IslandLayoutFactory.CreateIslandChunkData(
    chunkTile: origin,
    notchDirections: Array.Empty<ChunkDirection>(),
    neighborChunks: origin.AsEnumerable(),
    isBuildable: true,
    flipped: false,
    out _);
```

The parameters that matter:

- **`neighborChunks`** — every chunk in the island, so each chunk knows what it borders.
  Get this wrong and edges render as if the platform ends mid-tile.
- **`notchDirections`** — where the connector notches sit, i.e. how the platform links
  to its neighbours.
- **`isBuildable`** — whether players can place buildings on this chunk.
- **`TileVoidFlags_L`** — per-tile void flags on the returned data, for punching holes
  in a chunk.

For anything beyond a single chunk, copy `BiggerPlatforms` — it exists specifically to
demonstrate multi-chunk foundation layouts (4×4, 5×5, 6×6, 5×1, 6×1) and is much
faster to adapt than deriving the data by hand.

## Rendering

```csharp
private IChunkDrawingContextProvider ChunkDrawingOptions()
{
    return new HomogeneousChunkDrawing(ChunkPlatformDrawingContext.DrawAll());
}
```

`HomogeneousChunkDrawing` draws every chunk the same way, which is what you want for a
uniform platform. `drawPlayingField: true` draws the build grid on top.

For something that is *not* a platform — a piece of track, say — you want no frame at all.
Pass a zeroed context rather than removing the data:

```csharp
.WithRenderingOptions(new HomogeneousChunkDrawing(default), drawPlayingField: false)
```

`ChunkPlatformDrawingContext` is a struct of five bools, so `default` is "draw nothing".
Detaching `IslandFrameDrawData` instead looks equivalent and is not — see the gotcha below.

## Flipping with F is group membership, not a flag

`WithInteraction(flippable: true, …)` on its own does nothing. `IslandPlacersCreator`
decides by **counting** the group's `IslandGroupCollection`:

| Definitions in the group | Placer |
| --- | --- |
| 1 | `SinglePlacer` / `AreaPlacer` — F does nothing |
| 2 | `FlippableSinglePlacer` / `FlippableAreaPlacer` — F swaps between them |
| 3+ | throws |

So F needs a second, mirrored definition **in the same group**. The extender registers one
island per chain and a second chain would need its own group id — so build both from a
single `IIslandBuilder` that quietly registers the pair and returns the original. Attach
`FlippableDefinition` both ways as well: the placer works off group membership, but
`IslandBlueprintProcessor` reads that CustomData, and without it a flipped island comes
back unflipped out of a blueprint.

The engine mirrors over `ChunkAxis.YAxis`, and `ChunkDirection.Mirror` swaps a direction
only when that direction's own axis matches — North and South are the `YAxis` pair, East
and West the `XAxis` one. So a mirrored variant reverses its North/South connectors and
leaves East/West alone.

The catch: **the mirror never travels the extender chain**. Its simulation, prediction and
side-panel provider are all keyed by definition id, so each needs registering separately,
re-arming per scenario load the way `AtomicIslandExtender.Build` re-arms its own chain.

## The extender chain is one-shot, and `WithPrediction` can strand it

`AtomicIslands.Extend()…Build()` does not register your island once and leave it there.
Every link in `RewirerChain` **unregisters itself the moment it has applied** —
`RewirerChainLink` calls `OnPropagate.Unregister` and then `GameRewirers.RemoveRewirer` on
its own handle — so one pass through the chain consumes it. What makes the island come
back on the next scenario load is the last few lines of `AtomicIslandExtender.Build`:

```csharp
allRewirersToWait.AfterHijack.Register(OnApplyAllExtenders);
void OnApplyAllExtenders()
{
    allRewirersToWait.AfterHijack.Unregister(OnApplyAllExtenders);
    BuildExtenders();          // the whole chain, built again
}
```

`allRewirersToWait` is a `WaitAllRewirers`, and it fires **only when every branch has
cleared its link** — modules, placement + toolbar, simulation, and prediction. One branch
that never fires means no re-arm, ever.

Prediction is the branch that fails to fire, and it does so for an ordinary reason: **the
player turned predictions off in the settings.**

ShapezShifter invokes prediction rewirers from exactly one place,
`PredictionSystemsInterceptor` — a MonoMod postfix on
`BuiltinPredictionSimulationSystems.CreateSimulationSystems`. That method has exactly one
caller, `GameSessionOrchestrator.SetupPredictions`, and `StartPredictionUpdate` skips it:

```csharp
if (!SimulationSettings.Predict)
{
    if (PredictionSimulator != null) ShutDownPredictions();
    return;                                  // ← never reaches SetupPredictions
}
if (PredictionSimulator == null) SetupPredictions(...);
```

`SimulationSettings.Predict` is `BoolGameSetting("prediction", …, defaultValue: true)`,
persisted as `setting.simulation-settings.prediction`. Default on, so most players and
every developer testing their own mod never see this — and the key is absent from
`settings.json` entirely until someone changes it. Turn it off and no prediction system is
ever created for the whole process: `ModifyPredictionSystems` is never called,
`AfterHijack` never fires, the link never clears, and `Build` never re-arms.

The consequence is nastier than it sounds, because **the first scenario of the process is
the main menu's background game**, not the player's save. Look for this pair in
`Player.log`:

```
Initializing Main Menu
Core:: Stage 4 - Init existing savegame memory with mode RegularGameMode
```

So the one shot is spent before the player has loaded anything. Their actual save gets no
definitions, no toolbar entry and no research unlock — and the mod reports **no error at
all**, because nothing threw. It simply is not there.

Diagnosing it from a log takes one count. Every rewirer logs on the way in and on the way
out, and the removal is what proves `AfterHijack` fired:

```bash
grep -c "Adding rewirer IslandPredictionExtender"           Player.log
grep -c "Removing rewirer with handle IslandPredictionExtender" Player.log
```

Adds with no matching removals is the signature — and with the setting off the add count
never rises at all, because nothing re-arms to add more. Confirm it with the island counts:
`New islands: 170 + 163` in the menu session and `164 + 163` in every session after it
means seven islands registered once and one of them (someone else's, from a chain with no
prediction branch) re-armed.

To reproduce it yourself, turn that one setting off. No third-party mod is involved — chasing
the reporter's mod list, load order and Workshop packaging is a dead end, because all three
reproduce clean with the setting left on.

**So keep prediction off the chain.** Register it by hand instead, re-arming it yourself:

```csharp
// Not .WithPrediction(...) on the builder.
registrations.Add(new ReArmingRewirer(() =>
    new IslandPredictionExtender<MyPredictionSimulation>(
        definitionId, new MyPredictionFactory(), logger)));
```

where `ReArmingRewirer` re-registers on `AfterHijack` and owns its `RewirerHandle` so
`Dispose` can stop it (`RewirerChain.BeginRewiringWith` keeps the handle to itself, so a
mod that used it could never unregister). The chain then waits only on branches that do
fire, and prediction still attaches the moment `CreateSimulationSystems` runs.

There is no fluent way to do this: `WithPrediction` and `WithoutPrediction` are the only
two exits from `IDefinedAccessibleSimulatablePlaceableIslandExtender`, and
`WithoutPrediction` is `throw new NotImplementedException()`. Cast back to
`IAtomicIslandExtender` — every one of these interfaces is the same `AtomicIslandExtender`
instance.

The same reasoning covers anything else registered outside the chain: a mirrored
definition's simulation, a hand-built modules provider. If it is keyed by definition id and
did not travel the chain, it has to re-arm per scenario load or it works exactly once.

## Use `WithPerChunkColliders()`, not `WithBoundingCollider()`

`WithBoundingCollider()` sizes one box as `(max - min) * 20` over the chunk positions.
That is **one chunk short on every axis**, and since every island is a single layer deep,
`min.z == max.z` — so the box is always **zero height**, whatever the island's footprint.
A single-chunk island gets a box of zero size in all three axes.

The symptom is that the cursor passes straight through: the island cannot be hovered,
selected, pipetted or deleted, while everything vanilla around it works normally.

```csharp
.WithPerChunkColliders()   // 20×20×20 per chunk, centred at chunk * 20
```

That matches what vanilla's `CommonIslandDefinitionFactory.GenerateCollisionBoxes`
produces — `(count) * 20` per axis, greedy-meshed. The official `BiggerPlatforms` and
`SandboxIslands` samples both call `WithBoundingCollider()`, so do not take them as
evidence it works.

## Two connectors at one pivot, and the red cross that follows

`IslandConnectorData` keys connectors by pivot in a `MultiValueDictionary` and rejects only two
of the *same type* at one pivot. So an island can declare a belt-tagged connector **and** a
pipe-tagged one at the same place, and it will then snap to a shape line or a fluid line without
needing two island families:

```csharp
connectors.Add(Connector(ChunkDirection.West, new SpaceBeltInputConnector()));
connectors.Add(Connector(ChunkDirection.West, new SpacePipeInputConnector()));
```

Two things have to follow, and both bite.

**The simulation must claim a bundle per connector.** `ConnectableIslandSimulation` bounds its
loop by `min(NumItemReceiverBundles, connectors.Count)`, so the second connector at a pivot is
silently dropped unless the simulation says `NumItemReceiverBundles => 2`. Hand the same bundle
back for both indices. `ConnectableIslandPredictionSimulation` is bounded the same way — see
[Placement previews](placement-predictions.md).

**The placement preview will paint a red cross over a perfectly good placement.**
`PlacementConnectorDrawer` walks connectors one at a time and knows nothing about the pairing,
and `IslandInstanceModel.CreateConnection` marks a connector conflicting whenever there is an
island on the far side with no matching connector at that pivot:

```csharp
if (island2.TryGetConnector(pivot, out var found) && from.ConnectsTo(found))
    return new IslandConnection(from, found);
return new IslandConnection(from, isConflicting: true);   // <- the cross
```

Dragging such an island onto a shape belt connects the belt-tagged connector and conflicts the
pipe-tagged one, at the same pivot. Both are drawn, and the cross is what the player sees.

**And a dragged path will not snap to the tag the placer is not typed on.** A path placer asks
`EntityConnectionWorldIOQuery.TryGetUniqueOutput` which way the thing it is starting from faces,
and that query looks for connectors that are exactly its `TOutput`. Type the placer on the belt
pair and it cannot see a pipe-tagged output, so the run does not align even though it connects
once placed. `TInput`/`TOutput` are constrained `class, IEntityConnector, new()`, so
`ISpacePathOutputConnector` cannot be used to cover both — an interface has no `new()`. Wrap two
vanilla queries instead and OR them:

```csharp
internal sealed class EitherTagIOQuery : IWorldIOQuery<GlobalChunkCoordinate, ChunkDirection>
{
    public bool TryGetUniqueOutput(IReadOnlyMapLayoutModel map, GlobalChunkCoordinate position,
        out ChunkDirection direction)
    {
        return Belts.TryGetUniqueOutput(map, position, out direction)
            || Pipes.TryGetUniqueOutput(map, position, out direction);
    }
    // ...TryGetUniqueInput, HasInput, HasOutput the same way
}
```

Getting it in costs you `PlatformIslandsPlacersCreators.CreateSpacePathPlacementInitiator`, which
builds the query internally. Rebuild that method instead — every part of it is a game class with
a public constructor, so it is vanilla's sequence with one object substituted, not a
reimplementation of placement. Two things to know while you are in there: the *island* overload of
`CreatePathPlacer` ignores the `portSender`/`portReceiver` it is passed (only the building overload
uses them), and skipping vanilla's `PipetteMap.Add` loop is a feature — `DefaultIslandPlacementExtender`
has already registered your definitions and `Dictionary.Add` throws on the duplicate.

The game's own opt-out is `SkipConflictingConnectorsDrawingFlag`, read by
`PlacementConnectorDrawer.DrawEntityConnectors`. Vanilla sets it from a `MetaIslandDefinition`
field (`RenderConflictIndicatorVisualization`) that a mod-built definition never passes through,
so attach it yourself. Connected and not-connected markers are unaffected, so the island still
shows a green arrow where it really joins something; what you give up is the cross in the cases
where it *would* be deserved.

ShapezShifter's `IslandBuilder` keeps its definition private, but `BuildAndRegister` hands back
the definition it registered and the flag is only read at draw time — so decorate the builder:

```csharp
internal sealed class SkipConflictMarkers : IIslandBuilder
{
    private readonly IIslandBuilder Inner;

    public SkipConflictMarkers(IIslandBuilder inner) => Inner = inner;

    public IslandDefinition BuildAndRegister(IslandDefinitionGroup group, GameIslands gameIslands)
    {
        IslandDefinition definition = Inner.BuildAndRegister(group, gameIslands);

        // Guarded: see the first gotcha below. AddFlag is a plain Attach.
        if (!definition.CustomData.Has<SkipConflictingConnectorsDrawingFlag>())
        {
            definition.CustomData.AddFlag<SkipConflictingConnectorsDrawingFlag>();
        }

        return definition;
    }
}
```

`AtomicIslandExtender.WithIsland` takes an `IIslandBuilder` and `IslandsExtender` calls it
through the interface, so a decorator drops in where the builder went.

## Gotchas

- **Anything you attach in `BuildAndRegister` must be idempotent.** The builder runs its
  fluent chain once, when your mod is constructed, but `BuildAndRegister` is called again
  for *every scenario load* against those same `IslandDefinition` objects. `Detach<T>()`
  resolves with `Get<T>()` first and throws `NoDataFitDataTypeQueryException` when there
  is nothing there, and `RemoveFlag<T>()` is only `Detach<T>()` renamed. `Attach` is
  quieter and worse: it is a plain add, so a second copy sits *beside* the first and
  `CustomDataHolder` does not quietly pick one: `TryGet` **throws**
  `MultipleDataFitDataTypeQueryException` on `DataMatch.Multiple`, and `Has<T>` is `TryGet`, so
  every later reader of that type throws too — on the second session of a run and never the
  first. `AddFlag<T>()` is `Attach(new T())` and carries exactly this hazard. Use
  `AttachOrReplace`, or guard with `Has<T>`.
- **Never detach `IslandFrameDrawData`.** Vanilla attaches it to *every* island
  unconditionally, space belts included, just with an all-false context.
  `IslandChunkPlatformFramesCache.RegisterIsland` early-returns for an island that lacks
  it, so that island's chunks never enter the cache — while `IslandFramesDrawer.Draw`
  calls `GetEntry` for every culled chunk **with no guard at all**. The result is a
  `KeyNotFoundException` once per frame, forever, which also aborts `MapDrawer.Draw`
  partway through and silently kills whatever draws after it.
- **Layout is the time sink**, not the chain. Budget accordingly, and start from the
  closest sample rather than from zero.
- **The pipette map is a `Dictionary.Add`, and `.WithDefaultPlacement()` already claimed
  your island.** If you also register a second placer covering the same definitions — a
  path placer over a family of forwards and turns, say — the game's
  `CreateSpacePathPlacementInitiator` registers every member for pipetting too, and the
  second `Add` throws `An item with the same key has already been added` during
  `PlayerInteractionOrchestrator`'s constructor, before the main menu appears. The
  builder chain offers no way to skip `WithDefaultPlacement()`, so hand your own placer a
  throwaway `Dictionary<IEntityDefinition, PipettePlacementRequest>` instead of the real
  `IslandInitiatorsParams.PipetteMap`. Pipetting then resolves to the default placer,
  which is a fair trade for starting up. Which of the two rewirers runs first is not
  something to rely on — fix it so either order works.
- Island toolbar categories are different from building ones — `SandboxIslands` anchors
  at `ChildAt(5).ChildAt(4)`, nowhere near the building indices. See
  [Add to the toolbar](add-to-toolbar.md).
- `IslandDefinitionId` is written into saves. Renaming breaks existing saves.
- A platform that unlocks in the standard scenario but not the converter scenario is
  the classic milestone-id bug — see
  [per-scenario selection](add-research-unlock.md#unlock-at-a-milestone).
- Most `IslandGroup.Create(...)` options are stored and never read. ShapezShifter's
  `IslandGroupBuilder.BuildAndRegister` attaches only `GroupPresentationData`, so
  `AsNonTransportableIsland`, `WithPreferredPlacement`, `Removable`, `AutoConnected` and
  `AllowedOnNotches` have no effect. A declared `DefaultPreferredPlacementMode` never
  reaches the definition, and `CreateDefaultPlacer` falls through to single placement.
  Attach it to the definition yourself if you need it.
- For how the island is actually drawn — and why a space-path-like island needs an
  `IIslandPlatformDrawer` rather than mesh CustomData — see
  [Rendering](../rendering.md#how-an-island-gets-drawn).
