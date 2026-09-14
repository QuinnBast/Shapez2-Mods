# Crossover platforms - design notes

Idea stage. What was read out of the decompiled assemblies rather than assumed, so none of it
has to be re-derived.

## The idea

A space platform piece where two transport paths cross without interacting: space belt over
space pipe, belt over belt, or pipe over pipe. Items pass straight through each other. Saves the
detour platforms players currently build to route around a crossing, while keeping full space
path throughput.

## Status: playable, 2026-09-10

Placed and tested in game. Both paths carry items independently; the crossings work.

Three things came out of that first session:

1. **`translations.json` had the wrong shape** and took the whole game's mod load down with it.
   The file is deserialized as `Dictionary<string, Dictionary<string, string>>`, so the top level
   must be language codes (`"en-US"`), not keys. A flat key/string file throws in
   `LoadModTranslationAfterResolve`, before any mod code runs.
2. **The toolbar path lands them in the trains' Locomotive Depot collection.** Still using the
   SandboxIslands path; `ToolbarSlot()` is where to change it once `pbx.toolbar` has been read.
3. **A crossing showed a flashing "Empty" for the shape or fluid** even with items flowing. See
   below - it was the missing prediction.

Since then: F flips a crossing, and dragging a path across another one now offers a crossing
rather than a lift detour. The placement change is the one part of this not yet confirmed in
game - the mechanism is vanilla's own and the ordering is checked, but whether a crossing is
allowed to *replace* the existing segment depends on replacement IO checks that only run live.

## Status: first build, 2026-09-10

Builds and stages (`dotnet build -p:Dev=true`). Three islands registered: `Crossover_BeltBelt`,
`Crossover_BeltPipe`, `Crossover_PipePipe`, all straight-across only.

**The belt-crossing-belt problem solved itself.** The open question above worried that both paths
use `SpaceBeltInputConnector`, so nothing distinguishes West-to-East from North-to-South.
`ConnectableIslandSimulation` settles it: it filters the island's connector array by
`ISpacePathInputConnector` / `ISpacePathOutputConnector` and pairs the n-th of each with bundle n,
in declaration order. So declaring West-in, East-out, North-in, South-out is the whole mechanism -
no new connector subtype, no `CustomData`, no touching `NotchConnectorsExtender`.

A crossing is therefore two vanilla `SpaceConveyorSimulation`s sharing one island: two
`ItemLaneBundle<FastBeltPathLane>`s, each handed out as both receiver and provider at its own
index. Speeds come from the vanilla space belt and space pipe configurations at runtime, so
research upgrades apply.

### What to check first

1. **Toolbar placement.** `ToolbarSlot()` uses the path from the SandboxIslands sample, which
   resolves but puts these somewhere arbitrary. A wrong path throws `ToolbarQueryException` at
   load, so if the mod fails to load, this is why.
2. **That the two paths really are independent** - feed distinguishable shapes West and North,
   confirm nothing appears on the wrong output.
3. **Belt-pipe orientation.** Path A (West-to-East) is the belt, path B (North-to-South) the pipe.
   If that reads backwards in play, swap `pipeA`/`pipeB` in `CrossoverConnectors`.
4. **Throughput.** Each path should carry the full 12 lanes. State uses 16 slots per lane to match
   vanilla, so inserting a crossing should not change a line's rate.

### Prediction is a separate graph, and silence in it looks like a fault

The game runs a second simulation beside the real one purely to answer "what will arrive here",
and that is what fills the shape and fluid readouts on ports and in the build overlay. It is
registered per island definition id, exactly like the real simulation, and the two share nothing.
An island that simulates perfectly but registers no prediction is a hole in that graph, so
everything downstream of it reads as empty while real items keep flowing past - which is precisely
what a crossing looked like.

Vanilla's `SpacePathPredictionSimulation` is one `ItemPredictionBundle<ItemPredictionConverter>`
handed out as both receiver and provider, which makes a straight segment forward whatever it is
told. `CrossoverPredictionSimulation` is two of those, indexed the way `CrossoverSimulation`
indexes its lanes - `ConnectableIslandPredictionSimulation` pairs prediction bundles to connectors
by the same declaration order `ConnectableIslandSimulation` uses for the real ones, so one
connector order serves both.

### Flipping with F is group membership, not a flag

`IslandPlacersCreator` decides a group is flippable by *counting*: two definitions in the group
gives a `FlippableSinglePlacer` (or `FlippableAreaPlacer`), one gives the plain placer, and three
or more throws. So F needs a second, mirrored definition in the same group - `MirroredIslandPair`
builds both from one `IIslandBuilder` so the extender chain still sees one island and the toolbar
still gets one entry.

The engine mirrors over `ChunkAxis.YAxis`, and `ChunkDirection.Mirror` swaps a direction only when
that direction's own axis matches, so the mirror reverses the North/South path and leaves the
East/West one alone. Only **belt-over-pipe** actually needs it: for the symmetric kinds the four
rotations already reach every combination of the two path directions, and the mirror is just a
shortcut. All three are flippable so F behaves consistently.

Two costs to this. The mirror never travels the extender chain, so its simulation is registered by
hand (`ReArmedRegistrations`), including the re-arm that `AtomicIslandExtender` does for its own
chain - a hand-registered rewirer that does not re-arm silently stops working on the second
scenario load. And placement is reached only by casting to `IDefinedSimulatableIslandExtender`:
nothing in the fluent chain returns that interface, though the same `AtomicIslandExtender` instance
implements it.

### Prediction is registered by hand for both variants, not just the mirror

The straight variant's prediction used to go on the chain, through `.WithPrediction(...)`. A
player's `Player.log` showed why it cannot.

`AtomicIslandExtender.Build` re-arms itself only when `WaitAllRewirers` sees every branch clear its
link - modules, placement + toolbar, simulation **and** prediction. Prediction fires from exactly
one place, `PredictionSystemsInterceptor`, a MonoMod postfix on
`BuiltinPredictionSimulationSystems.CreateSimulationSystems`. That method in turn has exactly one
caller, `GameSessionOrchestrator.SetupPredictions` - and `StartPredictionUpdate` skips it outright:

```csharp
if (!SimulationSettings.Predict) { if (PredictionSimulator != null) ShutDownPredictions(); return; }
if (PredictionSimulator == null) SetupPredictions(...);
```

`SimulationSettings.Predict` is `BoolGameSetting("prediction", ..., defaultValue: true)`, stored as
`setting.simulation-settings.prediction`. **The player had predictions turned off.** No prediction
system is ever created, so `IslandPredictionExtender` was added six times - three from the chain,
three from the mirrors - and removed none across four sessions. The link never cleared and the
chain never re-armed.

Everything else was a red herring: their mod set, load order, the Workshop packaging and the icon
paths all reproduce clean on a machine with the setting left on, and all eleven of their mods were
installed at matching versions to prove it.

The chain is therefore consumed by the first scenario of the process - which is the **main menu's
background game** (`Initializing Main Menu`, then `Init existing savegame memory`), not the
player's save. `New islands: 170 + 163` in that menu session, `164 + 163` in every session after
it: the six crossings registered once, into a map nobody plays. The real save had no definitions,
no toolbar group and no research unlock, and the mod logged not one error. The reported symptoms
were exactly that - no hotbar icons, and `CrossoverPlacementRewirer` falling through to vanilla
lift bridges because it could find no crossing definition to substitute.

So prediction goes through `ReArmingRewirer` for the straight variant too. The chain then waits
only on branches that do fire, and prediction still attaches whenever `CreateSimulationSystems`
runs. The cost is a second cast: what `InToolbar` returns offers only `WithPrediction` and
`WithoutPrediction`, and `WithoutPrediction` is `throw new NotImplementedException()`
(`AtomicIslandExtender:307`), so there is no fluent route onward that skips prediction.

### The look, without authoring a mesh

A crossing draws as two space path segments at right angles. Nothing was modelled for it.

**The first attempt was wrong, twice.** It read the segment's meshes off the island definition's
`IslandMeshDrawer.Data` and fed them to `ModularIslandMeshDrawer`. A space path definition carries
no `IslandMeshDrawer.Data` at all - the log said so plainly: *"Vanilla space path draws no main
mesh"*. Island visuals are not all held the same way:

- Most islands keep their meshes as CustomData on the definition, via `IslandMeshDrawer` (one list,
  island transform) or `ModularIslandMeshDrawer` (each mesh with its own `LocalChunkTransform`).
- **Space paths keep theirs on the visual theme**, in `SpaceBeltResources` / `SpacePipeResources`,
  fetched by `ISpacePathResources.GetMeshMaterials(PathNodeClassification)`. Nothing about the look
  of a belt is on its definition.

And even with the right meshes, `ModularIslandMeshDrawer` would still have been wrong:
`SpacePathPlatformDrawer` drops a segment by **2.07314 world units** and submits it to
`Renderers.SpacePaths`; the modular drawer does neither, and `LocalChunkTransform.Position` is an
integer `ChunkVector`, so that sub-chunk drop cannot be expressed there at all.

**What works.** `CrossoverPlatformDrawer` is `SpacePathPlatformDrawer` with one addition: it draws
its mesh list twice, the second time turned a quarter turn - the same quarter turn the connectors
and the placement processor use, reversed for the mirrored variant so its arrows point where its
items travel. Blueprint ghosts, the non-instanced path and the overview mesh all get the same
treatment, or a crossing would look solid as a ghost and vanish on the zoomed-out map.

**Getting it registered is the awkward part.** Platform drawers are not CustomData - they live in a
session-wide `Dictionary<IslandDefinitionId, IIslandPlatformDrawer>` that
`GameSessionOrchestrator.CreateIslandPlatformDrawers` fills by walking `GameIslands.SpaceBelts` and
`SpacePipes`. A modded island is in neither list, and Shifter has no rewirer for drawers - it has
them for islands, placers, toolbars, simulation and prediction, but not this. So `CrossoverAppearance`
postfix-hooks that one method with Shifter's own `DetourHelper` and adds six entries to the
dictionary vanilla just built. `MonoMod.RuntimeDetour` is a compile-time-only `PackageReference`
(`ExcludeAssets: runtime`), the arrangement Mod Reloader already uses; the game loads it anyway.

`IIslandPlatformDrawer` is `[Obsolete]` in favour of `ModularIslandMeshDrawer` - the direction the
game is moving. Until space paths move with it, this is how their track gets drawn.

**The black slab had to be taken off separately.** It was never a default: it came from the mod's own
`WithRenderingOptions(new HomogeneousChunkDrawing(ChunkPlatformDrawingContext.DrawAll()),
drawPlayingField: true)`, which asks for all five frame layers and a playing field. Track drawn on
top of that is still a box with track on it. `CrossoverAppearance.StripPlatformSlab` detaches the
`IslandFrameDrawData` and removes the `DrawPlayingFieldFlag` at registration time - registration
rather than in the hook, because only the concrete `IslandDefinition` exposes a writable CustomData
holder, `IIslandDefinition` being read-only.

**What this still is not.** Two straight segments overlapping at the centre. No junction detail where
they meet, and the decks are coplanar rather than one passing over the other. A purpose-built X mesh
means modelling it and loading it at runtime - `AssimpNet` is on the workshop for that - and that is
model work, not code work.

### `ModDirectoryLocator` and hot reload cannot both work

`ModDirectoryLocator` resolves a mod's folder as
`Directory.GetParent(typeof(TMod).Assembly.Location)`. Mod Reloader has to load with
`Assembly.Load(byte[])` - `LoadFrom` binds by assembly identity and would hand back the copy
already loaded, which is precisely why reloading needs the byte[] overload - and an assembly with
no file backing it has `Location == ""`. `GetParent("")` throws
`ArgumentException: Path cannot be the empty string`, inside the mod's constructor, so the reload
reports the mod as dead.

This is not specific to crossings. It breaks hot reload for **every mod that loads an icon off
disk**, which is every mod here with a toolbar entry, plus `DiagonalCutter`, `BiggerPlatforms` and
`SandboxIslands`.

`ModResources.Locate` works around it mod-side: use the locator when there is an assembly location,
otherwise fall back to `<persistent>/mods-dev/<name>` then `<persistent>/mods/<name>`, preferring
mods-dev the way the reloader does. The general fix belongs in the reloader instead - it already
knows the source directory it staged from, and could make that available to whatever asks.

### Travelling items, and the panel figures

**Renderers need no registration at all.** `GameSessionOrchestrator.CreateSimulationRenderers`
reflects over `AppDomain.CurrentDomain.GetAssemblies()` for every `IIslandSimulationRenderer` and
builds each through a dependency container - which reaches a mod assembly as readily as the game's
own, and is why vanilla's renderers all carry `[UsedImplicitly]`. `CrossoverSimulationRenderer` is
picked up on its own. Renderers key off the *simulation type*, so one covers the straight and
mirrored variants of all three crossings.

It deliberately does **not** derive from `SpacePathSimulationRenderer`, which cannot express a belt
crossing a pipe: that class sorts connectors into per-item-type lists, then runs the whole item
list once against the shape lists and once against the fluid lists, casting each item
unconditionally. Fine where every connector on a node carries the same item type; an
`InvalidCastException` the first time a `FluidPackageItem` meets the shape pass. So a crossing pairs
its own connectors instead - `ConnectableIslandSimulation` adds every input in bundle order then
every output in bundle order, so path *n* is connector *n* with connector *(bundles + n)*, and the
connector's own generic type says whether that path is belt or pipe. The position maths is vanilla's
straight-segment case; the curved branch that handles a turn is not needed.

**The side panel** reuses vanilla's `StructureStatProcessingTime` and
`StructureStatFluidThroughput`, built exactly as `SpaceBeltSidePanelModuleDataProvider` and
`SpacePipeSidePanelModuleDataProvider` build them, so the numbers agree with the belt either side
and track speed research. A crossing yields two, one per path.

Three wrinkles. `IslandsModulesLookup.AddModuleProvider` is a plain `Dictionary.Add`, and the
extender chain **always** registers something for the definition it carries - `WithoutModules` is
not "no registration", it registers a `NoModulesProvider`. So the straight variants take their
provider through `WithCustomModules` and the rewirer claims only the mirrored ids; registering a
straight id in both places throws `An item with the same key has already been added` during
`InjectIslandsModuleProviders`, which takes the main menu down. `IIslandModulesRewirer.AddModules`
is handed nothing but the lookup, and providers are
constructed when the mod is - neither point has a `GameMode` to read belt speed, pipe speed or the
fluid package size from. `CrossoverSimulationFactory` has one and runs once per scenario load before
any panel can open, so it publishes them to `CrossoverScenario`. The mirror never travels that chain at all, which is why it needs the rewirer.

`Game.Content` had to be referenced for `IFluidPortSenderConfiguration`, which is where the fluid
package size lives.

### Two traps in island CustomData

**`IslandFrameDrawData` must be attached, even when nothing should be drawn.**
`CommonIslandDefinitionFactory` attaches it to *every* island unconditionally - space belts
included, just with an all-false `ChunkPlatformDrawingContext`. Detaching it to remove the black
slab looked right and was not: `IslandChunkPlatformFramesCache.RegisterIsland` early-returns for an
island that lacks it, so that island's chunks never enter the cache, while `IslandFramesDrawer.Draw`
calls `GetEntry` for every culled chunk with **no guard at all** - a `KeyNotFoundException` once per
frame, forever. The way to draw no frame is a zeroed context
(`new HomogeneousChunkDrawing(default)`, `drawPlayingField: false`), not a missing one.

**Anything mutating a definition inside `BuildAndRegister` must be idempotent.** The island builders
run their fluent chain once, when the mod is constructed; `BuildAndRegister` runs again for every
scenario load against those same `IslandDefinition` objects. `Detach<T>` resolves with `Get<T>`
first and throws `NoDataFitDataTypeQueryException` when there is nothing there, and `RemoveFlag<T>`
is only `Detach<T>` renamed. `Attach` is worse than that: it is a plain add, so a second
`FlippableDefinition` sits *beside* the first, and `CustomDataHolder` reports a multiple match as
found-nothing - F-flipping would have worked on a fresh save and silently stopped on a loaded one.
Use `AttachOrReplace`, or guard with `Has<T>`.

### ShapezShifter's `WithBoundingCollider` is unusable

It sizes the collision box as `(max - min) * 20` over the chunk positions. That is one chunk short
on every axis, and for a single-chunk island it collapses to **zero**. Vanilla's
`GenerateCollisionBoxes` uses `(count) * 20` - a 1x1 island gets 20x20x20.

A zero-size collider is why a crossing could not be hovered, selected or deleted: the cursor had
nothing to hit. `WithPerChunkColliders()` is correct (centre `chunk * 20`, dimensions `20,20,20`) and
is what this mod now uses.

**This affects every island mod in this folder** - Platform Blackbox and Train Cargo Tools both call
`WithBoundingCollider()`, and so do the official `BiggerPlatforms` and `SandboxIslands` samples. Same
one-line fix in each.

### Crossing beats lifting, by ordering

Dragging a space belt across an existing one leaves an invalid node where they meet, and
`PathLiftingProcessor` is what currently resolves it - walking the run up a layer, across, and
back down. That is the four-lift detour.

The game already does the better thing for **wires**: `PathCrossProcessor` turns a `WireForward`
laid across a `WireForward` at a right angle into a wire bridge, and it is inserted at
`ProcessorIndex<IPathUpgradeProcessor>()` while lifting goes in at that index **+ 1**. Both
processors start from `GetAllInvalidEntities`, so whichever runs first wins: once crossing has
replaced the node with a valid placement, lifting never sees it. Ordering is the entire mechanism.

`CrossoverPathProcessor` is that idea with the two things vanilla's cannot express:

- **Mixed path types.** `PathCrossProcessor` takes one `ForwardDefinitionId` and requires both the
  existing and the placed node to match it, so it cannot describe a belt crossing a pipe. Ours
  tests the two independently and picks the kind from the pair.
- **Which way the second path runs.** The bridge is built at the *existing* entity's transform, so
  path A follows the existing run and path B is perpendicular - but perpendicular in one of two
  directions. A rotation maps local East to `rotation.ToChunkDirection()` and local South is East
  turned clockwise, so an island at `pathA` runs its second path towards `pathA + RotateCW`; when
  the other path runs the other way, the mirrored variant is the one that fits. This is the second
  thing the mirrored pair pays for, beyond F.
  For a mixed crossing the belt has to land on path A, so the orientation is taken from whichever
  of the two paths is the belt rather than from whichever was there first.

Getting at the placers: `PlatformIslandsPlacersCreators` builds them and never exposes them, but
Shifter postfix-hooks `RegisterPlacers`, and by then both are in the registry under
`"SpaceBeltPlacementInitiator"` and `"SpacePipePlacementInitiator"` - names from `Enum.GetName`
over a private enum, so there is nothing to reference and they are string constants. Resolve the
initiator, reach its `ModularEntityPlacer` through `GamePlacementInitiator.Placer`, and its
processor list is still mutable.

Deliberately left to the lift: two segments head-on on the same axis, and anything that is not a
plain `Forward` - a turn, a splitter, or an existing crossing.

### `IslandGroupBuilder` options that do nothing

`IslandGroupBuilder.BuildAndRegister` attaches only `GroupPresentationData`. Everything else the
group builder accepts - `AsNonTransportableIsland`, `WithPreferredPlacement`, `Removable`,
`AutoConnected`, `AllowedOnNotches` - is stored on the builder and never read. So the declared
`DefaultPreferredPlacementMode.Area` never reaches the island definition, `CreateDefaultPlacer`
finds no mode on it and falls through to `CreateSinglePlacer`, and crossings place one at a time.
Attaching the mode to the definition after `BuildAndRegister` would make it take effect; left
alone for now because single placement is what has been played and liked.

### Known rough edges

- **Art is borrowed, not authored.** Two vanilla segment meshes crossed at the centre. Reads as
  track rather than as a black platform, but has no junction detail and no over/under.
- Travelling items are drawn, and the side panel reports both paths' throughput.
- Unlocked at the *first* milestone. **Decided, not a TODO**: someone installing a crossing mod
  is not a first-time player, and is installing it precisely to stop building lift detours.
  Gating that behind late research would defeat the point.
- `AffectsSaveGames: true`, so it cannot be added to or removed from an existing save. Test on a
  throwaway one.

## Disposal

Everything the mod adds is taken back out on `Dispose`, because a loader that disposes mods leaves
the game running and anything still registered keeps firing against a dead instance:

| Registered | Undone by |
| --- | --- |
| `CrossoverPlacementRewirer` | `RemoveRewirer(PlacementHandle)` |
| `CrossoverModulesRewirer` | `RemoveRewirer(ModulesHandle)` |
| `CreateIslandPlatformDrawers` detour | `CrossoverAppearance.Uninstall()` |
| Nine prediction/simulation registrations (three straight predictions, three mirrored pairs) | `ReArmingRewirer.Dispose()` |
| `CrossoverScenario`, `ToolbarKit.Log` statics | cleared explicitly |

The re-arming ones are why `RewirerChain.BeginRewiringWith` could not be used: it arms the rewirer
but keeps the `RewirerHandle` to itself, so the loop would outlive the mod and re-register on every
later scenario load forever. `ReArmingRewirer` owns the handle instead, and releases exactly once
per registration - `GameRewirers.RemoveRewirer` logs an error for a handle it has already dropped,
so a double release would be noise in every user's log.

**One thing cannot be undone.** `AtomicIslands.Extend()...Build()` re-arms its own chain internally
(`OnApplyAllExtenders` calls `BuildExtenders` again) and exposes no handle, so the island
definitions themselves stay registered after disposal. That is a ShapezShifter limitation shared by
every mod that adds content through the atomic extender, the official samples included; there is
nothing to do about it from here.

## Verified facts

| Question | Answer | Where |
|---|---|---|
| Does a crossing piece exist in vanilla? | **No.** The generator emits Forward, LeftTurn, RightTurn, LeftFwdSplitter, RightFwdSplitter, YSplitter, TripleSplitter, LeftFwdMerger, RightFwdMerger, YMerger, TripleMerger and the lifts. Nothing with two independent paths | `SpacePathIslandDefinitionFactory.Create<TInput, TOutput>` |
| Are belts and pipes the same machinery? | **Yes.** Both come from one generic call, differing only in the connector type pair: `SpacePathFactory.Create<SpaceBeltInputConnector, SpaceBeltOutputConnector>(spaceBeltIsland, "SpaceBelt")` and the `SpacePipe*` equivalent on the next line | `IslandDefinitionFactory.cs:64-65` |
| And the port systems? | Both derive from `SpacePathPortSystem<TInput, TOutput>`; `SpaceBeltPortSystem` and `SpacePipePortSystem` are thin subclasses | `Game.Core.Map.Transport/SpacePathPortSystem.cs` |
| Can one chunk carry four connectors? | **Yes.** `TripleSplitter` declares West in plus North/East/South out on `ChunkVector.Zero`; `TripleMerger` is the mirror | `SpacePathIslandDefinitionFactory` |
| Can a belt path and a pipe path be confused? | **No.** `SpaceBeltInputConnector.IsCompatibleConnection(other)` returns `other is SpaceBeltOutputConnector`, and the pipe connectors mirror that. Cross-type connection is structurally impossible | `SpaceBeltInputConnector`, `SpacePipeInputConnector` |
| How wide is one path? | 12 lanes - `NumLanes = 4`, `NumLayers = 3`, `TotalEntriesPerBundle = 12`. A crossing must hold two independent 12-entry bundles | `SpacePathConstants` |
| Is bundle enumeration already written down? | Yes, in the samples fork: `ItemReceiverBundle.Create<TLane>()` loops `NumLanes x NumLayers` and indexes via `Bundle.ToArrayIndex(laneIndex, layerIndex)` | `shapez2-mod-samples/SandboxIslands/ItemReceiverBundle.cs` |
| How is a definition declared? | `CreateDefinitionFromMeta(prefix + "_Name", meta, group, chunks, EntityIO<LocalChunkPivot, IIslandConnector>[], meshModules, ..., renderConnectors: false)` | `SpacePathIslandDefinitionFactory` |

## The easy case and the hard case

**Belt x pipe is nearly free.** Because the two connector types refuse to connect to each other,
declaring one island with `West: SpaceBeltInput`, `East: SpaceBeltOutput`,
`North: SpacePipeInput`, `South: SpacePipeOutput` gives two paths that cannot cross-talk. The
existing belt and pipe port systems each pick up their own connectors. This is the version to
build first.

**Belt x belt is the real design problem.** Both paths use `SpaceBeltInputConnector`, so nothing
in the connector data distinguishes "the West input feeds the East output" from "the West input
feeds the South output". The splitter and merger pieces get away with this because they genuinely
do merge. A crossing has to carry routing intent that the current connector model does not
express. Options to work through, none verified yet:

- A distinct connector subtype pair used only by the crossing, so pairing is unambiguous by type.
  Cheapest, but check `NotchConnectorsExtender` and `NotchConnectors`, which build explicit
  `compatibleTypes` lists naming `SpaceBeltInputConnector` - a new subtype may need registering
  there to be placeable.
- Routing held in the island's `CustomData` and read by a custom path system.
- Two stacked single-path islands on different layers, if the layout system allows overlap -
  probably not, given the layer cap noted in the research-HUD memory.

## Open questions

- ~~**Art is the likely blocker.**~~ Answered: the Forward mesh *can* be instanced twice at 90
  degrees, through `ModularIslandMeshDrawer`. See "The look, without authoring a mesh". A
  purpose-built X mesh via `AssimpNet` is still the ceiling, but is no longer the floor.
- Does the path network model tolerate a node with two disjoint paths? `SpacePathNetworkNode`,
  `SpacePathConnectionsData` and `SpacePathNodeMaterializer` are where to look - if the network
  is built by flood fill over connectors, a crossing might merge two networks that must stay
  separate.
- Throughput: confirm a crossing does not become a chokepoint. Each path should stay at the full
  12 lanes; nothing should be shared between them.
- Placement and rotation: the factory calls `LinkFlipped(a, b)` to pair mirrored variants. A
  symmetric crossing may need no flip partner, but check what `LinkFlipped` being absent implies.
