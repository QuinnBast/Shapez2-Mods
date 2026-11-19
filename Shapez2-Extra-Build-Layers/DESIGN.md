# Extra Build Layers — design

Verified facts, each with the class or method that proves it. Anything marked **untested** has
been compiled but not run. Nothing here has been runtime-tested yet.

The mod is a substrate: it creates layers and fixes what the engine gets wrong about them, and
delegates every question of progression to a replaceable policy. That split is deliberate —
creating a layer has one right answer, earning one does not.

## The two ceilings are counts, not constants

```csharp
// SPZGameAssembly/GameMode.cs:66-72
public short MaxBuildingLayer  => (short)Mechanics.BuildingLayerUnlocks.Count;
public short MinIslandLayer    => 0;
public short MaxIslandLayer    => (short)(MinIslandLayer + Mechanics.IslandLayerUnlocks.Count);
public short TotalIslandLayers => (short)(Mechanics.IslandLayerUnlocks.Count + 1);
```

`ResearchMechanics.BuildingLayerUnlocks` and `.IslandLayerUnlocks` are public
`List<ResearchMechanicId>` fields. Appending raises the corresponding ceiling. A layer exists
because a mechanic exists to unlock it; there is no separate layer object.

How many entries the scenario ships is **authored data and not readable statically**. Both
counts are therefore expressed as "extra on top of what is there".

> `MaxIslandLayer` is the unlock **count**, not the top altitude. A scenario that hands out a
> layer below the start has more unlocks than it has layers above zero. `LayerRegistration.TopSpaceLevel`
> reports the largest entry in `IslandLayersUnlockOrder` instead, which is the number worth
> using for anything that reasons about altitude.

## The player's ceiling is a separate, backwards walk

```csharp
// SPZGameAssembly/BaseMapInteractionMode.cs:94-109
for (int num = unlocks.Count - 1; num >= 0; num--)
    if (Progress.IsUnlocked(unlocks[num]))
        return (short)(num + 1);
return 0;
```

Three consequences the mod is built on:

- **Building layers are positional.** Unlocking entry `i` allows layer `i + 1`. List order *is*
  layer order.
- **An unearned entry is harmless.** The walk stops at the first unlocked entry from the end, so
  appending a mechanic nobody owns leaves the ceiling exactly where it was. That is what makes
  `NoUnlockPolicy` safe rather than broken, and why registering 50 layers changes nothing
  visible until a policy makes them reachable.
- **One purchase can grant many layers.** Because the ceiling is "highest unlocked", a single
  side upgrade rewarding five mechanics raises it by five. `ShopUnlockPolicy` groups on that.

Island layers differ: they carry a parallel `List<int> IslandLayersUnlockOrder`, and
`GetMinimumAllowedIslandLayer` / `GetMaximumAllowedIslandLayer` take `math.min` / `math.max`
over the *orders* of unlocked entries. Vanilla hands out layers above and below the start
interleaved, which is why new levels continue from the **highest order in use** and why
`SpaceCeilingGate` finds the gate by order rather than by position. The two lists are indexed
together, so a length mismatch would be an `IndexOutOfRange` in a per-frame call — the registrar
bails rather than guess.

## Granting a layer with no player action

Not possible by writing into the progress manager:

```csharp
// ResearchUnlockProgressManager.RecomputeUnlocks
_CachedUnlockedMechanics.Clear();
...
if (cachedUnlockedReward is ResearchRewardMechanic m) _CachedUnlockedMechanics.Add(m.MechanicId);
```

The set is rebuilt from the rewards of unlocked upgrades every time anything changes, so an
injected entry survives until the next research event. The supported route is
`TryUnlockInternal(upgrade)` — `internal`, reachable through the publicizer — which adds to
`_ManuallyUnlockedUpgrades` and recomputes. It needs a live session, which the scenario rewirer
does not have, so `GrantedUnlockPolicy` queues the id and `PendingGrantDrainer` (an
`ITickRewirer`, fired from `GameSessionOrchestrator.Tick`) drains it.

`_ManuallyUnlockedUpgrades` is **serialized** (`SerializedData.UnlockedUpgradeIds`), so a
granted upgrade persists into the save. Removing the mod leaves an id the progress manager's
constructor logs a warning for and skips — graceful, but not silent. `TryUnlockInternal` returns
false for an id already present, so re-granting on every load costs nothing.

## The one thing that blocked machine levels

```csharp
// SPZGameAssembly/BuildingDrawDataFactory.cs — FromMeta
int num = 3;
ILODMesh[] array = new ILODMesh[num];
```

```csharp
// SPZGameAssembly/StaticBuildingMeshBuilder.cs:44
.MainMeshPerLayer[tile_G.BuildingLayer()];
```

A building on layer 3 indexes past the end, inside the chunk's static mesh build — so it does
not make one machine invisible, it aborts the build for the **whole chunk**.

`StaticBuildingMeshBuilder.cs:44` is the only place the array is indexed by layer
(`grep -rn "MainMeshPerLayer\["`). `PreviewMesh` is a single mesh despite
`GeneratePreviewMeshPerLayer`'s name — it scans the array for the first non-empty LOD. So
widening the array is the entire fix. `BuildingMeshLayerWidener` rebuilds each definition's
`BuildingDrawData` with a longer array, repeating the topmost authored mesh; for a building
without `IndividualMainMeshPerLayer` — most of them — `ComputeMainMesh` returns the same mesh
for every index anyway. **Untested.**

Three details that are not optional:

- The interface exposes only `ICustomDataReader`; the writable holder is the concrete
  `BuildingDefinition.CustomData` field, so the widener soft-casts.
- `BuildingDrawData` is `IDisposable` and its `Dispose` tears down `CustomDrawData`, which the
  replacement shares by reference. The old instance is **dropped, never disposed**.
- Decorations are in `GameBuildings.AllDecorations` and *not* in `_DefinitionsById` (the ctor
  builds that dictionary from `_VariantsById` only). Both lists are walked.

## Where machine levels stop, and where they only *seem* to

Two different limits, and an earlier version of this document had the wrong one as the headline.

### The hard clamp: 19 layers

A building's layer is not stored. It is recovered from its tile's `z`:

```csharp
// GlobalTileCoordinateExtensions
public static short BuildingLayer(this GlobalTileCoordinate coordinate)
    => (short)FastMath.SafeMod(coordinate.z, 20);
```

because `CoordinateConstants.TilesPerIslandLayer` is 20 and `ScreenUtils` stamps
`z = IslandLayer * 20 + BuildingLayer`. Layer 20 is layer 0 of the island layer above, so a
building there would alias onto another platform's ground floor. `BuildLayers.MaxMachineLevel`
is 19 and the registrar clamps to it.

### The real ceiling on usefulness: ports stop at 3

```csharp
// Game.Content/Game.Core.Map.Transport/SpacePathPortSystem.cs — CreateAllSenderSimulations
for (short num = 0; num < 4; num = (short)(num + 1))        // lane index, 4 notch tiles
    for (short num2 = 0; num2 < 3; num2 = (short)(num2 + 1)) // building layer, hardcoded
        GlobalTilePivot pivot = GetPivotForPlatformConnector(platformOutputPivot, num, num2);
```

`CreateAllSenderSimulations` and `CreateAllReceiverSimulations` build the simulations that
actually move items between platforms, and they only ever look at building layers 0, 1 and 2. A
belt or fluid port above layer 2 gets **no simulation**, so nothing crosses there. This is
runtime transport, not placement assistance.

It cannot be hooked. `SpacePathPortSystem<TInput, TOutput>` is a **generic type** — the trap
that compiles and then kills startup. `SpaceBeltPortSystem` and `SpacePipePortSystem` inherit
these private methods rather than overriding them, so there is no non-generic choke point, and
the prediction variant `PredictionSpacePathPortSystem<TInput, TOutput>` is generic too.

`NotchConnectorsExtender` has the matching placement-side bound:

```csharp
private const int MaxBuildingLayer = 3;
...
for (short num = 0; num <= 3; num = (short)(num + 1))   // AddConnectors
```

also unhookable — `AddConnectors<TMapLayout>` is a generic *method*, and the only factory that
builds the stack (`ConnectorExtendersUtils.CreateDefaultExtenderStackForProcessor<,,>`) is a
generic static iterator consumed only from field initialisers on generic types.

**This is a design constraint, not a defect.** A tall factory has to bring its output back down
to the bottom three floors to export it, which is a reasonable thing for the game to ask. It is
logged once at `Info` so "my belt port on floor 5 does nothing" is not a mystery.

### Everything inside a platform does scale

Verified to follow `MaxBuildingLayer` rather than a constant:

| Consumer | Evidence |
| --- | --- |
| Valid tile `z` range | `IslandLayoutQuery`: `tile_I.z < 0 \|\| tile_I.z > MaxBuildingLayer` |
| Platform floor art | `IslandPlayingFieldLayersDrawer`: `for (int i = 1; i <= MaxLayer; i++)` |
| Hub input slots | `HubSlots.ComputeInputSlots`: `for (num2 = 0; num2 <= maxBuildingLayer; ...)` |
| In-platform lifts | `LayerLiftPlacementTracker` compares against `ViewportLayersController.MaxBuildingLayer` |
| Notch tile for a layer | `NotchDefinition.GetNotchLocationOnChunk_L` just sets `result.z = layer` |
| Notch slot / static building / island notch drawers | all take `Mode.MaxBuildingLayer` |
| Belt path placement | `PlatformIslandsPlacersCreators` passes `MaxBuildingLayer` into the path placer |
| Building-layer HUD strip | same `HUDLayersView` base as the island strip, so `CompactLayerStrip` covers it |

## What scales with space levels, and what does not

Space levels are clamped so the top one is at most `BuildLayers.MaxSpaceLevel` (999), in
`LayerRegistrar.AddSpaceLevels`, logged as an error and never thrown, the same way machine levels
are clamped to 19. Unlike 19 it is not a coordinate limit: a tile's `z` is a `short` holding
`layer * 20 + floor`, so the real ceiling is layer 1637. 999 is where the layer numbers run out of
room - they are drawn for up to three digits. The other thing that grows is the tall space lifts,
one set per span up to the top layer.

It was 99 while the grid numbers cost a 1.4 MB texture slice per layer. They no longer cost
anything per layer - see "Space grid numbers are drawn for the layer on screen" below.

Everything below is driven by the mode and grows on its own:

| Consumer | Source |
| --- | --- |
| Rail lift targets | `LiftVerticalOffsetProvider` enumerates `MinIslandLayer..MaxIslandLayer` |
| Super chunk culling bounds | `SpaceThemeBoundsProvider.GetSuperChunkBounds` expands by `MaxIslandLayer` |
| Placement layer bound | `IslandWithinAvailableLayerBounds` compares against the controller |
| Layer-up/down buttons | `LayerLiftPlacementTracker` |
| Camera height | `ViewportHeightEasingAnimationController.GlobalHeight = building + island * 20f`, unbounded |

Things that get *more expensive*, not wrong:

- `ScreenUtils.TryFindIslandAtCursor` with "show all island layers" on raytraces from the
  current layer plane up to `MaxIslandLayer + 1`. `RaytraceChunkCoordinates` is a proper DDA
  stepping 20 units a chunk, so it emits chunks-crossed rather than a fixed step count — it
  scales linearly with the layer count, not catastrophically.
- `LiftVerticalOffsetProvider` builds and sorts an `Enumerable.Range` over the whole layer span
  per query.

Things that are genuinely wrong at height:

- **The HUD layer strip does not fit.** `HUDLayersView` builds it procedurally and rebuilds on
  research change (so extra layers appear for free), but the layout is linear:
  `anchoredPosition.y = -5 - (24 + (num - 1) * 9)` and
  `UILayerBtnLayoutElement.preferredHeight = 32 + 10 * maxUnlockedLayer`. Three layers is 60px;
  fifty is over 500px and runs off a 1080p screen. `CompactLayerStrip` postfixes
  `UpdateLayersState` and rescales spacing to a budget, doing nothing while the natural layout
  already fits. **Untested, and the most likely thing in this mod to be wrong.**
- **Resource patches are pinned to one height band** — see below. Out of scope here, but it is
  the first thing the altitude-scatter mod will hit.

## For the mod that scatters resources across altitudes

Researched but deliberately **not implemented here**. Recording it so it is not re-derived:

- `GlobalChunkCoordinate`'s `Equals` and `GetHashCode` both include `z`, and
  `GameResourcesMap.GetResourceAt_GC` keys on the full coordinate — so a patch generated at
  `z = 7` exists only at layer 7. `OnlyOnShapeResourcePatchPlacementRule` and
  `IslandOriginOnResourcePlacementRule` look up at the island's own `Transform.Position`, z
  included, so a miner platform at layer 7 validates correctly.
- Vanilla never generates one: `MapPatchGenerator.TryGeneratePatchLayout` writes
  `chunkVector.z = 0` literally, and `SuperChunkCoordinate.ToOrigin_GC()` returns z 0.
  `SuperChunkCoordinate` is 2D and `To_SC()` ignores z, so one super chunk spans all altitudes.
- `DefaultMapGenerator.Generate(SuperChunkCoordinate)` is public, non-generic, on a non-generic
  class and returns an `IEnumerable` — `DetourHelper.CreatePostfixHook<TObject, TArg0, TResult>`
  fits it exactly, and the enumerable can be wrapped. `ShapeResourceClusterData` and
  `FluidResourceSourceData` are plain data with a center plus local offsets, so moving a patch
  is one field.
- **The blocker.** `SpaceThemeBoundsProvider.ComputeResourceSourceBounds` overwrites the Y
  extent with `MapResourceMinHeight = -50f` / `MapResourceMaxHeight = -22f` regardless of the
  patch's real z. `SuperChunksDrawer` frustum-tests against those bounds before drawing, so an
  asteroid at layer 10 is culled unless the camera happens to frame Y −50..−22. The mesh itself
  is built from `source.Origin_GC` and would be in the right place. It is a public non-generic
  instance method, so it is hookable.
- `MapResourceSource.CenterOfMass_GC` also hardcodes `z` to 0. Minor — it is what "go to this
  resource" would aim at.
- Keep the origin super chunk at layer 0. `DefaultMapGenerator.Generate` special-cases
  `SuperChunkCoordinate.Origin` and emits the scenario's `StartingLocation.FixedPatches` there.

## Hook points

| What | Hook |
| --- | --- |
| Register layers, run the policy | `IGameScenarioRewirer` — right after `GameScenario` is built |
| Widen per-layer mesh arrays | `IBuildingsRewirer` — right after `GameModeBuildingsFactory.FromMetadata` |
| Grant policy-granted upgrades | `ITickRewirer` — `GameSessionOrchestrator.Tick` |
| Widen meshes of later mods' buildings | `ISimulationSystemsRewirer` — after every buildings rewirer |
| Tall lifts and pipes, their meshes and previews | `IBuildingsRewirer` (`ExtraLiftBuildings`, `TallBlueprints`) |
| Tall lift simulations, minus vanilla's claim | `ISimulationSystemsRewirer` (`ExtraLiftSystems`) |
| Tall space belt/pipe lifts | Shifter atomic islands (`ExtraSpaceLiftIslands`) |
| Layer colours, floor materials, numbered textures | `IBuildingsRewirer` (`LayerVisualResourcesExtender`, `LayerNumberArt`) |
| Lit previews on floor 9+ | MonoMod hook on `SmartBuildingBlueprintRenderer.DrawBlueprintPart` |
| Layer strip rescale / number readout | MonoMod postfix on `HUDLayersView.UpdateLayersState` |
| Opening a save made without the mod | MonoMod postfix on `SavegameModdingContextDivergenceBarrier.ComputeModDiff` |

`GameMode.From` builds the scenario first and the buildings second, so the machine-level count
is already raised when the widener runs. Neither reads the other — both work from the same
requested counts — so a failure on the research side leaves over-long mesh arrays (harmless)
rather than a layer with no mesh (not harmless).

## Save games

`AffectsSaveGames: true`, because **removing** the mod from a save that used it is destructive:
`IslandLayoutSerializer.DeserializeBuilding` drops unknown definitions (every tall lift), and
vanilla buildings left above floor 2 meet three-slot mesh arrays again. The game enforces the flag
in both directions, so on its own it would also refuse **adding** the mod to an existing save.
`SavegameAdoption` removes only this mod from the "added" list `ComputeModDiff` fills; the save
loads, `Savegame.UpdateData` records the mod on the next save, and removal stays refused by the
unmodified vanilla path. Off with `AllowAddingToExistingSaves: false`.

**Lowering a count is not safe on an existing save.** Going from 30 space levels to 10 leaves
platforms at altitudes `IslandWithinAvailableLayerBounds` now rejects. Raising is fine.

## Hot reload

`mrl.reload` does not help and is not safe. Most of the mod runs while the scenario, buildings and
islands are built, once per session, and islands cannot be unregistered. Restart to test.

## Relationship to Extended Research

`Shapez2-Extended-Research/ExtendedResearch/LayerUnlockExtender.cs` does the same research
plumbing and is where this approach came from. It clamps machine levels to zero
(`MaxSupportedBuildingLayerUnlocks = 2`) because it has no mesh widener.

Both mods appending to the same lists would **stack**, not conflict — ids are namespaced
(`ExtraBuildLayers.*` vs `ExtendedResearch.*`) — but a player running both gets the sum.
Extended Research defaults both its layer counts to `0`, so out of the box there is no overlap.
Its layer code should be removed once this mod ships.

## What the first runs settled, and what is still open

Settled at the defaults: repeated meshes draw on high floors; the floor art stacks to the top;
in-platform lifts reach the top floor and animate items all the way; space belt and pipe lifts
reach the top space level; floors and space layers show their own numbers; previews stay lit.

Still open: items through a tall **Down** lift; tall in-platform **pipes** end to end; a
save/reload with items mid-lift; the `SavegameAdoption` path on a save that never had the mod;
counts far from the defaults (30-50 space levels - the strip, "show all island layers", memory at
~5 MB per numbered floor); a port sender above floor 2 (expected: never moves anything, no
exception); and a second mod driving `BuildLayers`.

## N-layer lifts

The span of a lift is **structural, not a parameter**. `BeltLift2LayerSimulation` declares its
two vertical lanes as fields, and `Lift2LayerSimulationState` mirrors them:

```csharp
public readonly BeltLane VerticalLane0;
public readonly BeltLane VerticalLane1;
```

So there is no number to raise. A taller lift is a new building. Three things had to be true
for a mod to add one, and all three are:

**1. The path placer picks a lift purely by connector geometry.** There is no lift-specific
logic in the building path placer - `BuildingPlacersCreator` mentions lifts nowhere. The
candidate set comes from one place:

```csharp
// DefinitionsFinderUtils - a static, non-generic extension method, so it is hookable
public static IMatchingDefinitionFinder<BuildingDescriptor, GlobalTilePivot>
    BeltPathDefinitionsFinder(this GameBuildings buildings)
    => new MatchingDefinitionFinder<…>(
        buildings.BeltBuildingId.AsEnumerable()
            .Append(buildings.Lift1LayerDefinitionGroupId)
            .Append(buildings.Lift2LayerDefinitionGroupId)
            …);
```

A postfix returning a finder that also contains taller lifts is enough to make the placer
choose them. `MatchingDefinitionFinder<…>` is a generic *type*, but constructing one is fine -
only hooking its methods is forbidden. `PipePathDefinitionFinder` and
`SpaceBeltPathDefinitionFinder` sit beside it with the same shape.

**2. The pieces are constructible.** `BuildingConnectorData` has a public constructor
(`IEnumerable<IBuildingIO>, IEnumerable<TileVector>, LocalTileBounds, LocalVector,
TileDimensions`) and so does `BuildingDefinition(BuildingDefinitionId, IBuildingConnectorData)`.
So a taller lift can be cloned from the vanilla `Lift2Layer` definition with the output
connector's `z` moved up and the tile bounds stretched, reusing its draw data, colliders and
sounds.

**3. The simulation generalises.** `BeltLift2LayerSimulation` is just
`Input → Vertical0 → Vertical1 → Output`, chained through `new BeltLane(speed, state, next)`.
An N-lane version is the same loop.

### The constraint that shapes the design

```csharp
// AtomicStatefulBuildingSimulationSystem
where TSimulationState : class, ISimulationState, new()
…
TSimulationState arg = building.State.New<TSimulationState>();
```

The state is built through a **parameterless constructor**, so it cannot be told its span. And
`[SyncableIdentifier]` is read with `inherit: false`, so a generic `LiftState<N>` cannot carry a
per-span id either.

That pair looks like it forces either a class per span or a fixed maximum. It forces neither,
because the game already has the pattern - `SpaceMergerSimulationState` makes its state
**self-describing**:

```csharp
public BundleState<FastBeltPathLaneState>[] InputLaneBundleStates = Array.Empty<…>();

public void Sync(ISerializationVisitor visitor)
{
    byte s = (byte)InputLaneBundleStates.Length;
    visitor.SyncByte_1(ref s);
    if (visitor.Reading) { EnsureSize(s); }
    foreach (var lane in InputLaneBundleStates) { lane.Sync(visitor); }
}
```

An empty array satisfies `new()`; the length goes into the blob ahead of the elements; on read
`EnsureSize` rebuilds before they are consumed. `ExtraLiftSimulationState` does the same with
`SyncInt_4`, so there is no cap at all and nothing to add per height.

`EnsureSize` has to be idempotent, because two different paths call it: `Sync` for a loaded
lift, and the factory for a freshly placed one. Whichever runs first, the second must not swap
the lane state objects out from under the `BeltLane`s wrapping them.

The system is registered per building definition, so one factory per span sharing that single
state type works - the span lives in the factory, which is the only participant that knows it:
`AtomicStatefulBuildingSimulationSystem<ExtraLiftSimulation, ExtraLiftSimulationState>(factoryForSpanN, definitionIdForSpanN, logger)`.

### Registering a path building: the group, not the finder

**Verified working in game.** In-platform belt lifts carry items to the top floor at spans well
past vanilla's two.

What made it work was registering the definitions as internal variants of the stock lift's
group:

```csharp
if (source is BuildingDefinitionGroup concreteGroup)
{
    concreteGroup.AddInternalVariant(stretched);
}
```

What did **not** work, across three attempts, was intercepting the thing that appears to own the
candidate list. `DefinitionsFinderUtils.BeltPathDefinitionsFinder` is a static, non-generic
extension method, it hooks cleanly, and a postfix returning a finder that also held the tall
lifts had no effect whatsoever. A trace decorator on the returned finder proved why: in a full
session the hook fired **hundreds of times** and **not one** `TryMatchingEntityFromInputsAndOutputs`
call ever arrived at any finder it returned. Whatever the running placer holds is built
somewhere else.

So for a building the path placer must be able to choose - a belt, a lift, a merger - put it in
the definition group. Every consumer enumerates the group: the path definition finder wherever
it is really constructed, the neighbour upgrade and downgrade processors, replacement rules, the
pipette map. One registration, no hooks, and no way for two consumers to disagree about what
exists.

Two traps in doing it:

- **Snapshot the group's definitions before looping over them.** `AddInternalVariant` appends to
  the very list `source.Definitions` exposes, so using `source.Definitions.Count` as the loop
  bound grows the bound every iteration and hangs on load.
- Being an internal variant is **not** a toolbar entry. The stock lift already ships eight the
  player never cycles, because the placer picks among them by geometry; these join that set.

### The geometry, dumped rather than guessed

Read out of the running game with `extrabuildlayers.dump-lifts`, because it is authored
ScriptableObject data:

```
Lift2UpForwardInternalVariant     in (0,0,0) West   out (0,0, 2) East    bounds 0..2
Lift2DownForwardInternalVariant   in (0,0,0) West   out (0,0,-2) East    bounds -2..0
```

Eight variants per height: Up/Down × Backward/Forward/Left/LeftMirrored, and the **Down ones come
first** in the list. The input sits at z 0 in every one; only the output moves, and the sign of
its z is what makes the lift climb or descend.

So a taller lift moves the connector **by role**, never by height. An earlier version moved
whichever connector sat highest - correct for an Up lift, and on a Down lift it moved the *input*
to +span while leaving the output at -2, producing four nonsense definitions per span:

```
ExtraBuildLayers.Lift3Layer_1     in (0,0,3) West   out (0,0,-2) East   bounds 0..3
```

### What is built

In-platform belt lifts and pipes, spanning 3 layers up to the layer ceiling.

| | |
| --- | --- |
| `ExtraLiftSimulationState` | self-describing, any span, one `SyncableIdentifier` |
| `ExtraLiftSimulation` | the unrolled vanilla chain rolled back into a loop, plus the per-span factory |
| `ExtraLiftBuildings` | restretches the vanilla two-layer definitions once per span, as internal variants of the stock group |
| `ExtraLiftSystems` | one simulation system per definition, after removing vanilla's claim on them |
| `TallBlueprints` | full-height static meshes and placement previews |

The definitions carry the vanilla lift's custom data - draw data, colliders, sounds,
configuration factory - including its `BuildingDefinitionGroup`, because plenty of lookups do
`CustomData.Get<BuildingDefinitionGroup>()` and would throw without one. They **are** internal
variants of that group: that is how the path placer reaches them (see below), and it is also why
vanilla's own simulation claimed them until `ExtraLiftSystems` removed it.

The height is vanilla's own bodies stacked - two-layer ones, capped by a one-layer one for an odd
span - laid out in one place, `TallShaft`, and drawn twice:

- **Placed:** baked into every slot of the definition's `MainMeshPerLayer` by `TallBlueprints`, so
  the chunk drawer draws it statically at every zoom (`TallShaft.Draw` remains as the per-frame
  fallback for a definition whose stack failed).
- **Being placed:** a stacked `IsolatedBlueprintMesh` baked at load by `TallBlueprints`. The preview
  is a different mesh from a different field - `SmartBuildingBlueprintRenderer.Draw` reads
  `IsolatedBlueprintMesh` and nothing else, and `GenerateIsolatedBlueprintMesh` builds it from
  `mainMeshPerLayer[0]` alone - so without this a twelve-layer lift *previews* as a two-layer one
  and then draws full height once placed. `IBuildingDrawData.PreviewMesh` is the obvious-looking
  field and is dead: nothing in the game reads it.

A Down piece's shaft runs downwards (`TallShaft.Direction`, off the tile bounds `Stretch` wrote).
Before 2026-11-18 the placed shaft always stacked upwards, which put a tall Down lift's extra
bodies on the floors above it.

## Pipes: the same trick, and no simulation needed

In-platform pipes are belts with two differences, both handled in `ExtraLiftBuildings`:

- **Both ends are the same type.** `PipePathDefinitionFinder` is
  `MatchingDefinitionFinder<…, BuildingFluidJunction, BuildingFluidJunction>` - there is no
  output to single out. So the connector that moves is identified by **height**: the end that is
  not on the ground floor. That rule covers belts too, whose output is the only connector at
  z != 0, so one code path serves both.
- **The simulation is free.** `FluidNetworkSystem` claims a building by asking
  `building.Definition.ConnectorData.BuildingConnectorsOfType<BuildingFluidJunction>().Count > 0`
  - by connector type, never by definition id - so a stretched pipe joins the fluid network the
  moment it exists. Only belts need an `AtomicStatefulBuildingSimulationSystem` registered.

Connectors are cloned with `MemberwiseClone` through reflection rather than `new` on a known
subtype: `BuildingFluidJunction` carries its own `_IOType`, and a hardcoded list of types to
construct would silently drop whatever a future one adds.

## Space belts are not the same problem

Built in `ExtraSpaceLiftIslands` and working to the top space level. A space belt lift is an **island**, and an island
cannot be cloned-and-appended the way a building can, because of one gate:

```csharp
// GameIslands
public bool TryGetDefinition(IslandDefinitionId id, out IIslandDefinition definition)
{
    if (!CatalogPair.GameSessionCatalog.HasIsland(id)) { definition = null; return false; }
    return DefinitionsById.TryGetValue(id, out definition);
}
```

`AllDefinitions` is likewise built by walking `CatalogPair.GameSessionCatalog.IslandIds` and
warning about any id the catalog has but the definitions do not. So an island definition that is
not in the **catalog** is invisible however it is registered - and the catalog is built inside
`GameMode.From` from the authored manifest:

```csharp
IslandCatalogPair islandCatalogPair = CreateCatalogPair(islandDefinitionManifestProvider.Ids, entityContent);
```

`GameIslands.SpaceBelts` is also a `readonly` array, and the constructor derives `DefinitionsById`,
`AllDefinitions` and `AllDefinitionGroups` from it, so writing the field afterwards leaves four
views disagreeing.

The supported route is therefore Shifter's `AtomicIslands` island registration - layout, per-chunk
colliders, meshes, unlock, simulation - with the traps the workspace already records: `Build`
returns no handle so it cannot be unregistered, its chain does not re-arm for a player with
prediction off, and a second placer over the same definitions crashes on the pipette map's
`Dictionary.Add`. That is a piece of work in its own right, not a follow-on to the pipe change.

## Layer numbers are pixels, so they are painted

The floor's "01"/"02" and the space grid's "0"/"1"/"2" are not text and not a uniform. Dumped
(`extrabuildlayers.dump-layer-visuals`), they are authored, **red-only** masks:

- **Floor:** `_ChunkTexture` on each `PlayingfieldCurrentLayerPlaneMaterialPerLayer` entry -
  `PlayingfieldChunkIndicatorLayer1`/`2`, 2048². Vanilla authors two (floor 0 draws no plane).
  The three dots under the number count the floor within vanilla's stack (●●○, ●●●), and every
  grid line in the "02" texture is exactly 60 brighter than in "01".
- **Space grid:** slices of a `Texture2DArray`, `IslandGridLayerIndicator`, 1024², bound on the
  four `IslandGridHelperMaterial*`. Three slices; a GPU clamps an array index, so every layer
  past 2 shows slice 2. That is the whole "2 forever" bug.

### Space grid numbers are drawn for the layer on screen

The first fix generated one slice per layer, which made the layer count a memory cost (1024² R8
with mips is about 1.4 MB a slice). It does not need to: the grid is only ever drawn for **one**
layer. `HUDIslandGridVisualization.Draw` sets every chunk it draws to `Viewport.IslandLayer`, and
`IslandsPreviewDrawer` draws at that layer too. So the array stays three slices, and the clamp
that caused the bug becomes the mechanism - every layer from 2 up reads the last slice, so
`LiveGridNumber` redraws that one slice whenever the viewed layer changes (postfix on
`HUDLayersView.UpdateLayersState`, which `HUDIslandLayersView` registers on
`OnIslandLayerChanged` and `OnNewViewportLoaded`).

Drawing a number inline was a visible hitch on every layer change: the stroke font measures every
pixel of each digit's box against every segment of the glyph, and `Apply(updateMipmaps: true)`
rebuilt mips for all three slices on the main thread. So the draw and its mip chain run on the
thread pool, the upload comes back through the main thread's `SynchronizationContext`, the layers
two either side of the one on screen are drawn ahead, and a jump to an undrawn layer shows the
backdrop with no number until it lands.

Floors cannot do the same. `IslandPlayingFieldLayersDrawer.Draw` draws **every** floor plane from
1 up to the current one, at once, each with its own material, so each floor does need its own
texture. That is bounded by the 19-floor cap: 17 at 2048² R8 with mips, about 95 MB.

`LayerNumberArt` makes the missing ones at load from the vanilla art: read back through a blit
into a linear RenderTexture (they are not CPU-readable), the digits rubbed out, new ones stroked
in by a ten-glyph centre-line font sized from the measured vanilla glyph, uploaded as R8.

- Floor digits are found by diffing "01" against "02" above that 60 (anything lower turns a grid
  line into a thousand-row "digit"), and sit on black, so erasing is zeroing. New floors use the
  "02" art, dots all filled - they cannot count higher.
- The grid digit sits on a glow, which is filled by relaxation (row interpolation alone leaves a
  box at the top and bottom of the "0"). Two-digit numbers draw at 0.8 scale and push the "SPZ2"
  label left, re-added as brightness over the backdrop so it does not drag a patch of glow along.
- Existing grid slices are copied through unchanged (`CopyTexture` into a same-format texture,
  then read back), and every loaded material holding the vanilla array is retargeted.

Prototyped in Python on the dumped PNGs and the output inspected; the C# is **compiled, untested**.
Unverified: that the grid shader indexes the array by island layer directly. If layers above 2 now
show the wrong number rather than "2", that assumption is what broke.

## Previews go black from floor 10: a constant in the preview shader

`Shader Graphs/UXBuildingBlueprintShader` (every building placement preview) computes, from the
pixel's world height - disassembled out of `resources.assets` with `D3DDisassemble`, since shader
code is not in the decompiled assemblies:

```
m = (worldY - 10) mod 20    // 20 = one island layer
t = saturate(m - 9)         // 1 below height 10, 0 from 10 to 19
emission *= t; alpha term *= t
```

So a preview between heights 10 and 19 of an island layer draws unlit: black. The literals are in
all 68 compiled programs; no material property moves them. Placed buildings use the uber
building shader, which has no band. `HighFloorBlueprints` hooks the innermost
`SmartBuildingBlueprintRenderer.DrawBlueprintPart` and draws any building reaching floor 9+ with
the island blueprint material, which has no band and takes the same allowability colour block.
**Compiled, untested** - the look of that material on a building is the open question.

## Lifts from floor 1 or 2 had no body

A tall lift's `MainMeshPerLayer` was the stock lift's, widened. The stock two-layer lift only
starts on floor 0, so slots 1 and 2 are `LODEmptyMesh` (`ComputeMainMesh`) and the widener only
fills slots it adds. `StaticBuildingMeshBuilder` draws `MainMeshPerLayer[layer]`, so a tall lift
placed from floor 1 or 2 drew nothing, and the per-frame shaft read the same empty slot. The
preview never showed it because the blueprint mesh is built from slot 0.

The shaft is now baked into every slot (`TallBlueprints.StackMainMeshes`, empty slots taking the
first real body), so the chunk drawer draws the whole lift statically at every zoom. The
per-frame `TallShaft.Draw` is kept only as the fallback for a definition whose stack failed - it
also stopped at a distance, since simulation renderers only draw while
`ShouldDrawDynamicBuildingSimulations`.

## Floor digits: measure on what was tested

The first in-game run of `LayerNumberArt` found no floor digits although the same search found
them on every dumped PNG. The dump reads through an **sRGB** RenderTexture; the generator read
through a **linear** one, where an sRGB texture's values are decoded - darker, and closer
together, which breaks thresholds set against the dump. It now measures on an sRGB read and keeps
pixels from a linear one, and logs which step failed if it fails again. (Inferred, not proven: the
linear-read numbers were never seen.)

## Tall lifts were simulated as two-layer ones

Items climbed two floors and then appeared at the top. A one-shot diagnostic (`LiftDiagnostics`)
showed the renderer constructed and **no** `ExtraLiftSimulation` ever produced: the building was
claimed by vanilla's `BeltLift2LayerSimulation`. `BuiltinSimulationSystems.CreateBeltLiftSystems`
walks the Lift2 group's `Definitions`, which include the tall lifts since they were made internal
variants of it, so vanilla registered a system for each, ahead of ours. `ExtraLiftSystems` now
removes every system specialised on a tall lift id before adding its own. A lift saved under the
old claimant loads empty (`PolymorphicSerializer` skips a mismatched state blob) and works.

## Tall lifts must carry tall colliders

Selecting a building is `ScreenUtils.TryFindBuildingAtCursor`: it walks the tiles under the cursor,
finds the building occupying one, and accepts it only if the ray hits one of
`IBuildingDrawData.Colliders`. A stretched lift occupies its whole shaft, so the first step finds it
on every floor, but colliders copied from the two-layer lift made the second step fail above floor
1 - a tall lift could only be clicked on its bottom two floors. `TallBlueprints.WithTallColliders`
grows every box by `span - 2` layers (one local unit each) in the lift's direction. Space lifts
had the same copy (`IslandCollisionData`, checked by `TryFindIslandAtCursor`) and are grown by 20
units per extra level in `ExtraSpaceLiftIslands.TallColliders`.

## Stands are theme arrays indexed by height

The supports under belts, pipes and wires are not in the building's mesh. `StaticBuildingMeshBuilder`
adds one per connector from `VisualThemeBaseResources` arrays indexed by building layer (plus one
over the void): `BeltCapStandsNormal`, `PipeStandsBetweenPipes`, `WireStandsBetweenWires`,
`Pipe/WireStandsAndEndCap`, `Pipe/WireBuildingStandsAndEndCap`. They are authored for three
layers, and `GetSafe` clamps, so above floor 2 every stand is the two-floor one hanging in the air.

`TallStands` extends every `LODMeshAsset[]` on the theme whose entries reach steadily lower (a
stand ladder - the step is measured from the last two, not assumed) to layer 19. Each new entry
is the tallest authored one cloned through `Object.Instantiate`, with every vertex below the middle
of its bottom layer moved down by the extra height: the column's faces stretch, the foot moves
intact. The building shader colours from a UV0 palette lookup, so stretched faces keep their
colour. **Untested in game**: the visual result, and whether the foot is shorter than half a layer
(if not, the foot stretches too).

## Plan: space lift stations instead of per-height space lift definitions (in progress)

Generating a tall space lift definition for every height and variant (`ExtraSpaceLiftIslands`) is
quadratic: at 999 levels it made ~32,000 definitions holding ~16M tiles, and loading, saving and
placing all stalled. A definition's connectors, layout and simulation are fixed per definition and
read by generic code that cannot be hooked, so one definition cannot have a per-instance height.

What the drag tool actually does with a climb (`PathAnchorsAndFillingProcessor`): one matcher call
per anchor pair - input at the bottom of the climb, output at the top - places the single island
returned, and `FillBetweenAnchors` skips vertical stretches entirely. `MatchesRequirements` is a
strict connector-position match.

Replacement:

1. **Two stations per path kind** (belt, pipe): a lift base and a lift top, fixed one-chunk islands,
   registered like Crossover Platforms registers its crossings.
2. **A placement processor** in the space belt and space pipe placers (inserted the way
   `CrossoverPlacementRewirer` does, via `IPlatformIslandPlacementRewirers`), after anchors are
   filled: for a climb no definition matched, add a base at the bottom and a top at the target
   level to the placement, so preview and occupancy checks are live. Vanilla's one-level lift is
   untouched.
3. **Simulation by distance**: the shaft is one long lane between the stations, ideally connected
   the way vanilla connects space belts between platforms, so belt speed upgrades and prediction
   apply. Fallback if a non-adjacent connection is refused: a station simulation that hands items
   from base to top after a delay.
4. **Shaft rendering** between each pair from the stock lift art.
5. **Remove** the generated tall space lifts, their colliders and layout work. In-platform tall
   lifts (buildings, max 19 floors) stay.

Saves holding generated tall space lifts lose them (unknown ids are dropped on load).
