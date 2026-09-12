# Train cargo tools - design notes

What was read out of the decompiled assemblies rather than assumed, so none of it has to be
re-derived.

## The idea

Train cargo currently only exists inside a train station. The mod takes it outside:

1. **Cargo belts** - a belt that carries train cargo packages, so cargo can be buffered on belts
   instead of being stuck in a station's small container buffer.
2. **A packager / unpackager pair** - space buildings that turn loose shapes into cargo packages
   and back.

The motivation is buffering: a station holds only so many containers, so a stalled train stalls
the whole line. Cargo on belts is buffer capacity you can build more of, and one belt slot holds
a whole package rather than one shape.

## Status: shape and fluid packagers, package-eating stations, 2026-09-10

Builds clean. Five islands, each one chunk, input West and output East:

| Island | West in | East out |
|---|---|---|
| `CargoBelt` | belt (cargo) | belt (cargo) |
| `CargoPackager` | belt (shapes) | belt (cargo) |
| `CargoUnpackager` | belt (cargo) | belt (shapes) |
| `FluidCargoPackager` | **pipe** (fluid) | belt (cargo) |
| `FluidCargoUnpackager` | belt (cargo) | **pipe** (fluid) |

`CargoBelt` is a straight space path whose lanes accept only `PackageOnTrack<CargoPackage<...>>`,
enforced by a `PreAcceptHook`. 32 slots per lane rather than the vanilla 16, since buffering is
the point.

**One cargo belt carries both kinds.** Its accept hook admits shape and fluid packages alike, so
there is no fluid cargo belt - only fluid *ends*. Cargo always travels the belt side even on the
fluid pieces, because the cargo belt reuses belt connectors.

The packagers and unpackagers are one generic implementation each, instantiated at `ShapeId` and
`FluidId`, which is how the game itself keeps the two apart. The only per-type differences are
the converter and the capacity provider (`ShapePackageSize` vs `FluidPackageSize`).

Plus five detours (`PackagedCargoStations`) so **every train station, shape and fluid, accepts
packages directly** - see below. The end-to-end shape is therefore
`items -> packager -> cargo belt -> train station`, with an unpackager needed only where cargo
has to go back to ordinary machinery.

**The cargo belt is now testable.** The previous build shipped the belt alone and nothing in the
game could put a package on it; a packager can.

### How the two new pieces are built

Almost none of the packing logic is new. `TrainBeltToCargoFillingContainer<ShapeId>` and
`TrainCargoToBeltFillingContainer<ShapeId>` are the game's own classes - the ones every shape
station already uses - re-hosted in an island simulation with the train removed:

| | Station | This mod |
|---|---|---|
| Pack | `TrainBeltToCargoFillingContainer` fills a package, loader hands it to a `CargoPackageTrack` for a train | same filling container, packager wraps it in `PackageOnTrack` and hands it to the output belt |
| Unpack | wagon loads a package into `TrainCargoToBeltFillingContainer`, which drains it onto belts | `CargoPackageReceiver` takes the package off a belt into the same state, same filling container drains it |

`CargoPackageReceiver` is the one genuinely new class, and only because vanilla never needs it:
unloading always starts from a train, so `TrainCargoToBeltFillingContainer` has a `LoadPackage`
method and no receiver side. It accepts a package only while its state is empty, which is what
gives an unpackager correct back-pressure.

Package size is not a mod constant. Both factories read
`GameMode.TrainCargoExchangeConfiguration`, the same `ITrainCargoExchangeSimulationConfig` the
stations are built from, so a package off a packager is byte-identical to one a station would
have made and wagon-capacity research applies.

### What to check first

1. **That a package survives the round trip.** Packager -> cargo belt -> unpackager, one shape
   type, and count the output rate. It should match the input rate.
2. **That a station eats one.** Packager -> cargo belt -> train loader. Watch for the throw the
   third detour exists to prevent, and try deliberately merging a loose-shape belt and a packed
   belt into one station layer, which is the case that would have thrown.
3. **Rendering.** All of it is implemented now - see "Appearance" below. Check the belts draw
   as real belt and pipe track, that the machines show their meshes the right way round (a
   packager's hopper should face the incoming loose belt), that packages are visible moving
   along a cargo belt, and that a store's shelves fill and empty as it buffers.
4. **Mixed shapes into one layer.** All four lanes of a layer pack into one package, exactly as
   at a station, so two shape types on one layer will fight through the filling container's
   subtractive penalty. Expected, but confirm it degrades rather than throws.
5. **Saving.** Should work (see below) but has never actually been exercised.

### Known rough edges

- **The machine meshes are one flat colour each.** Their UVs are placeholders - see below.
- Unlocked at the *first* milestone so they can be tested without an endgame save. Wrong for
  balance, deliberate for now.
- `AffectsSaveGames: true`, so the mod cannot be added to or removed from an existing save.
- **The fluid blob size is vanilla's 60 litres**, hardcoded, copied from
  `FluidCargoStationSimulationCreator`. An unpackager has to emit the same blob size a station
  does or the two would disagree about what one unit of fluid cargo is worth, so this is
  correct rather than lazy - but it is a literal, and if the game ever makes it configurable
  this is the place that will silently go stale.
- **Fluid cargo counts blobs, not litres.** `FluidBeltItemToCargoConverter` counts one
  `FluidPackageItem` as one unit of cargo regardless of its volume. That is vanilla's own
  accounting at a fluid station, inherited deliberately so a packager and a station produce
  interchangeable cargo - but it means packaging a non-60L blob loses or gains fluid, exactly as
  it does at a station today.

## Stations eat packages - resolved, global

`PackagedCargoStations` detours five methods so every train station accepts a
`PackageOnTrack<CargoPackage<ShapeId>>` as readily as a `ShapeItem`, and a
`PackageOnTrack<CargoPackage<FluidId>>` as readily as a `FluidPackageItem`. **A packed line runs
straight into a station; no unpackager in front.** The unpackager is still there for feeding
ordinary machinery, but it is no longer on the critical path.

Why three, and why split the way they are:

| Detour | Why |
|---|---|
| `ShapeBeltItemToCargoConverter.BeltItemTypeMatchesCargoItemType` | opens the gate - `item is ShapeItem` becomes "or a package" |
| `ShapeBeltItemToCargoConverter.TryConvertBeltItemToCargoItem` | unwraps rather than converts, reporting the package's real `Amount` so `TryGive` absorbs the whole thing in one hand-over |
| `DummyLane.CanAcceptItem` | closes the throw described below - one hook covers both item types |

with the first two repeated for `FluidBeltItemToCargoConverter`.

Keeping the first two on the *converter* rather than replacing `HandOverItem` wholesale is
deliberate: the station's own hand-over still runs, so it still sets
`LastTimeMatchingItemWasReceived`, which `IsLayerActive` reads to tell the train scheduler a
layer is alive. Reimplementing the hand-over would have silently broken train scheduling.

The converter pair is spelled out longhand for each item type rather than made generic. These
are the parts of the mod the compiler cannot check, so being able to read exactly what is
hooked is worth the duplication.

### MonoMod will not hook a method on a generic type. At all.

This cost a failed launch and is the single most useful thing learned here.

The guard belongs on `TrainBeltToCargoFillingContainer<T>.CanAcceptItem`, and that is where it
was first written - one hook at `<ShapeId>`, one at `<FluidId>`, on the reasoning that a struct
instantiation has its own native code and is therefore an ordinary method. That reasoning is
wrong. `Hook.CheckSupported` rejects the method outright:

```
System.ArgumentException: Source method is generic, generic hooks are not supported
  at MonoMod.RuntimeDetour.Hook.CheckSupported()
```

It compiles cleanly and throws at mod load, taking the whole game's startup with it. Struct vs
reference instantiation makes no difference; the check is on the declaring type.

**The fix is to find a non-generic choke point the calls already pass through.** Here that is
`DummyLane.CanAcceptItem`: a station's input bundle is a bundle of `DummyLane`s whose `NextLane`
is the layer's filling container, so an upstream belt asks the lane, not the container, first.
One hook there covers shape and fluid stations both - and covers this mod's own packagers,
which are wired the same way and could otherwise have thrown the same throw.

The cost is that a hot, widely used vanilla method now carries a type test. It is ordered so the
common case - a loose item, not a package - fails two `isinst` checks and falls straight through
to the original.

`PackagedCargoStations` also unwinds on failure now. The converter detours applied *without* the
guard is the one combination worse than doing nothing, since stations would accept packages and
then throw on a half-filled layer, so a partial application disposes what it applied.

**Consequence to watch in play:** a station fed a packed line ingests at `ShapePackageSize` times
the loose-shape rate. That is what packing is *for*, but it is a real balance change and it
applies to every station in the game, with no per-building opt-out.

### The throw that made the third detour necessary

Swapping only the converter is not enough. `HandOverItem` calls `TryGive`, which clamps `actual`
to `PackageSize - Package.Amount` and then throws
`Expected to give all when trying to give {amount}` if `actual != amount`. `CanAcceptItem` only
checks *not full*, never *has room for `amount`*, and a converter does not get to override
`CanAcceptItem`. Vanilla never trips this because a loose shape is always amount 1. A layer
holding four loose shapes that then received a ten-package would throw.

The guard is `Package.Amount == 0` - necessary and sufficient, because a package is full by
construction: both a packager and a station only ever emit at `PackageSize`.

## Background: why the station refused in the first place

**A vanilla train loader will not accept a package.** This is the central constraint and it is
worth stating precisely:

- `TrainCargoLoaderSimulation` wires every input lane's `NextLane` to a
  `TrainBeltToCargoFillingContainer<TItem>`.
- `TrainBeltToCargoFillingContainer.CanAcceptItem` is
  `!Package.IsFull(cap) && CargoConverter.BeltItemTypeMatchesCargoItemType(item)`.
- `ShapeBeltItemToCargoConverter.BeltItemTypeMatchesCargoItemType` is `item is ShapeItem`.

A `PackageOnTrack<CargoPackage<ShapeId>>` is not a `ShapeItem`, so the station refuses it and the
cargo belt backs up at the door. There is no other way in - the filling container is the only
entry point for cargo at a station.

That is what `PackagedCargoStations` above undoes.

`DetourHelper`'s prefix/postfix helpers were no use here - they cannot change a return value and
cannot express `out` parameters - so the hooks are raw `MonoMod.RuntimeDetour.Hook`s with
hand-written delegate types. `MonoMod.RuntimeDetour` is a new package reference on this project,
matching the version the other mods in this repo use.

### Still open: the unload direction

Applies to both item types equally. `TrainCargoUnloaderSimulation` takes an
`ICargoToBeltItemConverter<TItem>`, and
`TrainCargoToBeltFillingContainer` only peeks and pops against whatever the downstream lane will
accept - no capacity assertion, no throw. A converter whose `PopCargoIntoBeltItem` emits a
`PackageOnTrack` would give a station that unloads *straight onto a cargo belt*, with no other
change and none of the difficulty the load direction had. Not implemented; it is the obvious
next symmetry.

`TrainCargoUnloaderSimulation` takes an `ICargoToBeltItemConverter<TItem>`, and
`TrainCargoToBeltFillingContainer` only peeks and pops against whatever the downstream lane will
accept - no capacity assertion, no throw. A converter whose `PopCargoIntoBeltItem` emits a
`PackageOnTrack` gives a station that unloads straight onto a cargo belt, with no other change.

### Per-island or global? - decided global

`CargoExchangingController` is keyed on the concrete station simulation type and every shape
station is produced by the one `ShapeCargoStationSimulationFactory`, which has no per-island
branch. A separate "Cargo Train Loader" building would therefore have needed the new island
registered into `TrainIslandCollection.Exchange.ShapeLoaders` *and* a detour on
`ShapeCargoStationSimulationFactory.Produce` branching on `island.Definition.Id` - strictly more
work, and the registration half was never verified.

Global was chosen instead: stations simply understand packages, the way they arguably should
have. The cost is that the balance change is not opt-out.

## Verified facts

| Question | Answer | Where |
|---|---|---|
| Is a cargo container already a belt item? | **Yes.** `public class PackageOnTrack<TContainer> : IBeltItem, IItem, IPoolable where TContainer : struct` | `PackageOnTrack.cs` |
| Does cargo already travel on belt lanes? | **Yes.** `CargoPackageTrack<TPackage>` owns a real `BeltPathLane`, hands it `PackageOnTrack` items, and reads them back out of `BeltSlotState.Item` | `CargoPackageTrack` |
| **Does cargo on a belt lane save?** | **Yes - this is settled.** `BeltItemSerializer` already has tag 4 for `PackageOnTrack<CargoPackage<ShapeId>>` and tag 3 for the fluid one, and `BeltSlotState.Sync` goes through `visitor.Serialize(Item)` / `Deserialize<IBeltItem>()` generically. Nothing about it is station-specific, so a package on a modded lane round-trips | `BeltItemSerializer.cs`, `BeltSlotState.cs` |
| What travels - a container or a package? | A **package**. `CargoPackageTrack<CargoPackage<TItem>>` puts `PackageOnTrack<CargoPackage<TItem>>` on the lane. `CargoContainer` (a bounded list of packages) only ever exists inside a wagon | `TrainCargoLoaderSimulation` |
| What is a cargo package? | `struct CargoPackage<TItem> { short Amount; TItem Item; }` - one item type plus a count, capped at `PackageSize` | `CargoPackage.cs` |
| Where do the cargo numbers come from? | `GameMode.TrainCargoExchangeConfiguration`, a public field, implementing `ITrainCargoExchangeSimulationConfig` (`ShapePackageSize`, `MaxPackagesPerContainer`, ...) | `GameMode.cs:36` |
| Do bundling and unbundling interfaces exist? | **Yes, both**, and both are constructor arguments of the station simulations | `IBeltToCargoItemConverter`, `ICargoToBeltItemConverter` |
| Who implements them today? | `ShapeBeltItemToCargoConverter` / `ShapeCargoToBeltItemConverter`, wired in by `ShapeCargoStationSimulationFactory` | `Game.Content/` |
| Can a vanilla loader eat a package? | **No.** `CanAcceptItem` -> `BeltItemTypeMatchesCargoItemType` -> `item is ShapeItem` | `TrainBeltToCargoFillingContain.cs` |
| Is a converter swap enough to fix that? | **No.** `HandOverItem` throws unless the filling container can absorb the whole amount, and `CanAcceptItem` does not check that | `TrainBeltToCargoFillingContain.cs` |
| How is an island's input wired to a non-lane receiver? | A bundle of `DummyLane`s; `DummyLane` forwards `CanAcceptItem` / `HandOverItem` straight to `NextLane`. This is how the vanilla loader feeds its filling containers | `DummyLane.cs`, `TrainCargoLoaderSimulation` |
| How many lanes in a bundle? | 12 - `NumLanes = 4` x `NumLayers = 3` | `SpacePathConstants` |
| Can one simulation have separate in and out bundles? | **Yes.** `ConnectableIslandSimulation` pairs receiver bundle *i* with the *i*-th `ISpacePathInputConnector` and provider bundle *j* with the *j*-th output, independently | `ConnectableIslandSimulation` |
| Are `SyncableIdentifier`s inheritable? | **No** - `GetCustomAttributes(inherit: false)`, and the table is keyed by exact runtime type. Every saved state class needs its own concrete type and its own id | `PolymorphicSerializer.cs:34` |
| Are there existing cargo item renderers? | Yes for space paths: `ShapeSpacePathBeltItemRenderer`, `FluidSpacePathBeltItemRenderer` | `SPZGameAssembly/` |

## Two constraints found while building the belt

- **Only two connector families exist.** `ConnectableIslandSimulation` switches on the connector
  class to pick the chunk connector's item type (`SpaceBeltInputConnector` to `ShapeItem`,
  `SpacePipeInputConnector` to `FluidPackageItem`) and throws `NotImplementedException` for
  anything else. A dedicated cargo connector would need that class replaced, so all three pieces
  reuse the ordinary belt connectors.
- **But the item type there is only a compatibility tag.**
  `ItemInputChunkConnector<TItem>.CanConnect` tests `other is IItemOutputChunkConnector<TItem>`,
  which decides which paths may *join*; what flows is whatever the lanes accept. That is why the
  accept hook works, and why a packager can emit packages through a shape-tagged connector. The
  visible cost: a cargo belt will connect to an ordinary space belt and then silently refuse its
  shapes. Needs solving either by replacing `ConnectableIslandSimulation` or by making the
  refusal legible in the UI.

## Appearance - resolved

Every one of the twelve islands used to draw as the same bare platform deck. `AddIsland` asked
for `HomogeneousChunkDrawing` and nothing else, and a definition with no mesh data is one the
renderers walk straight past, so a belt, a packager and a store were indistinguishable.

`CargoAppearance` postfix-hooks `GameSessionOrchestrator.CreateIslandPlatformDrawers` and fixes
both halves. It is the same hook Crossover Platforms needed, for the same reason: platform
drawers live in a session dictionary built by walking `GameIslands.SpaceBelts` and `SpacePipes`,
and a modded island is in neither list, so there is no extension point. It is also the earliest
place this mod is handed a `Theme`, which is why the machine meshes are attached there too -
`IGameSessionManagers` has no theme on it, so there is no later chance to ask.

**The six belts get no mesh of their own, deliberately.** They are handed vanilla's
`SpacePathPlatformDrawer` over vanilla's own track, so a cargo belt is pixel-identical to the
space belt beside it. That is not a shortcut - it is the correct answer, because the thing has
to read as a belt. The `PathNodeClassification` is derived by
`PlatformPathDrawingClassifier.TryClassifySpacePathNode` from each island's own connector data
rather than hardcoded, so West-in-East-out classifies `Forward` and the two turns follow.

They also had to stop drawing a platform: `CustomPlatformsDrawer` is *additive*, so a belt
keeping `DrawAll()` would render a full deck floating over its own track. Hence the `pathTrack`
flag on `AddIsland`, which switches those six to `DrawNothing()` and `drawPlayingField: false`.

**The six machines carry generated meshes**, attached as `ModularIslandMeshDrawer.Data` and
paired with the theme's `IslandMaterial` through `CargoMeshes.ThemeMeshMaterial` - the game
ships no runtime-constructible `ILODMeshMaterial`, only the `LODMeshMaterialAsset`
ScriptableObject, so the mod implements the interface itself.

The geometry comes from `Tools/generate_meshes.py`, which writes `Resources/*.obj`. The script
verifies what it writes: signed volume must be positive and boundary edges zero, which caught
three separately inside-out solids that looked perfectly fine in a viewer without backface
culling and would have rendered hollow in game. Authoring frame is the renderer's - Y up, deck
at `Y = 0`, chunk 20x20 centred on the origin, flow along X. The script negates X *and* reverses
winding on export, cancelling `AssimpToUnityMeshConverter`'s own flip; negating X alone writes a
file that is inside-out as geometry.

Nothing depends on the script. The loader goes by filename, so any of the six can be replaced
out of Blender with no code change.

### Icons

`Tools/generate_icons.py` draws the twelve toolbar PNGs. They are **flat schematics, not
renders of the models**, because that is what the game ships: `Foundation_4x4`,
`FluidTrash` and `DiagonalCutter_Icon` are all flat, 512x512, transparent, white-to-grey
structure with one saturated accent, heavily outlined - and the outline is per shape, not
per silhouette, which is what stops `Foundation_4x4`'s grid reading as one blob. A 3/4
render of the `.obj` was the first idea and would have looked like another mod's art.

Two independent channels separate the families, so either alone is enough - amber versus
blue, and a square crate versus a rounded capsule. The second one is what keeps them
apart in greyscale or for a colourblind player.

Composition matches the models, so the icon predicts what gets placed: a run of track with
cargo on it, the same turning up or down, loose items and an arrow and a package, and a 3x2
rack for both stores now that both models are racks. Packager and unpackager are the same
drawing with the cargo sides swapped and the arrow still pointing right, which says both which
way material flows and which end of it is packed.

A container is a body plus one dark line - a lid seam on a crate, a lid on a canister - drawn
on a third `detail` mask painted last. It cannot be another accent shape: outlining works off
the union of the masks, so a shape drawn wholly inside another adds nothing to the silhouette
and gets no edge. An earlier attempt made the lid protrude instead, to force a seam, and drew
twelve little jars.

**Still open:** the game already has package icons on the train loader and unloader, and these
should be matched to them rather than invented. They could not be: sprites live in
`resources.assets` / `.resS` as BC-compressed data, so the atlas is not readable statically.
`cargotools.dumpicons` writes every island icon out at runtime, which is the way to settle it.

### Colour: why they came out bright red, and what fixed it

Colour on an island mesh is not a material property. It is a lookup into a shared texture
atlas through UV0 - `DiagonalCutter.fbx` carries a `base_color_texture` and packs 319 distinct
UVs into `U[0.095,0.476] V[0.587,0.919]`. The first version of the generator gave every vertex
a placeholder `(0.25, 0.75)`, because the atlas is authored Unity data that cannot be read out
of the decompiled assemblies, and whatever sits at that coordinate is **bright red**. Every
machine shipped red.

The fix is not a better guess. Each vertex is now authored with a per-role *sentinel* UV
(`CargoPalette.Sentinel`, mirrored by `PALETTE` in the generator), and at session start
`CargoPalette` samples real coordinates off **vanilla meshes the game draws with the very same
`IslandMaterial`** - `Trains.Cargo.ShapeCargoPackage`, `FluidCargoPackage` and
`SpaceBeltForwardStructureMesh` - then rewrites every sentinel to the sampled value. What
those meshes point at is by construction a sensible island colour, so the question stops being
a guess and becomes a measurement. `CargoExchangerDrawer` is the proof that the pairing is
right: it draws cargo packages with `Theme.BaseResources.IslandMaterial`, exactly as here.

Ranking is by how many vertices share a coordinate, as a proxy for "the main colour of this
object"; a role that has to differ from its neighbour takes that mesh's *second* most common
coordinate rather than one from somewhere unrelated, so the two shades still belong together.

Two things can still go wrong, and both fail visibly rather than silently. A shipped mesh
usually has no CPU-side copy (`Read/Write Enabled` off), in which case `mesh.uv` is
unavailable and sampling is skipped - the machines keep their sentinels and land in one corner
of the atlas. And sampled colours are valid, not art-directed. Either way:

| Command | What it does |
|---|---|
| `cargotools.palette` | prints the resolved UV per role |
| `cargotools.uv.<role> <u> <v>` | moves one role and repaints immediately, no rebuild |
| `cargotools.dumpatlas` | writes the island material's textures to `<persistent>/cargo-tools-atlas/` |
| `cargotools.dumpicons` | writes every island's toolbar icon to `<persistent>/cargo-tools-icons/` |

Roles are `hull`, `accent`, `metal`, `fluid`, `cargo`. Live tuning earns its keep because a
mod DLL is memory-mapped once loaded, so new code needs a restart - and a colour is the
definition of something that has to be judged by eye.

## Cargo in flight, and cargo in store

Both are drawn by **the game's own package drawers**, not by this mod placing meshes.
`ICargoContainerDrawer<TItem>.Draw` takes a `Matrix4x4`, which is the whole reason it works:
vanilla calls it with positions along a train, and nothing stops a mod calling it with a
position on a belt or a shelf.

The first version did place meshes by hand - `Trains.Cargo.ShapeCargoPackage` and
`FluidCargoPackage` with the island material - and that was wrong, obviously so for fluids. A
fluid package is **three** meshes and the one being drawn was the empty shell.
`FluidCargoContainerDrawer` draws:

| Mesh | Renderer / material | What it is |
|---|---|---|
| `FluidCargoPackage` | `Renderers.Trains`, `IslandMaterial` | the crate |
| `FluidInsideCargoCrate` | `Renderers.FluidsContainer` with `IFluidRegistry.GetFluidReference(item)` | **the fluid, in its own colour** |
| `FluidCargoContainerGlassLid` | `Renderers.Trains`, `BuildingsGlassMaterial` | the glass lid |

Drawing only the first leaves a colourless, lidless, see-through box, which is exactly how it
looked. Shapes were less obviously wrong but wrong too: `ShapeCargoContainerDrawer` also draws
the contained shape above the crate, so a package now says *what* it is carrying.

`IShapeRegistry` is bound into the renderer dependency container and is taken by constructor
injection. **`IFluidRegistry` is not bound**, so it is resolved lazily from `GameHelper.Core`
instead - asking for it in a constructor would fail to construct the renderer, and since every
renderer is built in one pass that would take out every other mod's renderers too.

### On the belts

`CargoBeltSimulationRenderer`. Without it a working cargo line looks like an empty one. Vanilla
cannot cover it: `SpacePathSimulationRenderer` sorts connectors into `ShapeItem` and
`FluidPackageItem` lists and casts every item unconditionally, and a
`PackageOnTrack<CargoPackage<ShapeId>>` is neither, so inheriting from it throws the first time
a package moves. The placement maths is ported from it rather than invented, curve included, so
a package sits exactly where a shape would on the same track.

Packages are scaled down from the mesh's own bounds to fit `TrackItemsSpacing`; they are
authored wagon-sized and a space path carries twelve items abreast.

### In the stores

`CargoStoreSimulationRenderer` draws **one container per package, twenty-five per shelf** - a
5x5 grid on each of three shelves, so a full store shows seventy-five real packages. An earlier
version drew five per shelf and let each stand for five, which was a deliberate choice and the
wrong one: a buffer's whole job is how full it is, and a display that moves in steps of five
cannot show a store filling.

This cannot be done with `ModularIslandMeshDrawer.Data` like the static machine meshes: that is
CustomData on the **definition**, so every store on the map shares one copy and none can
differ. Per-instance geometry has to come from a simulation renderer, which is handed the
entity, and so the state, every frame. `CargoStoreState.PackageAt` exists for it - the real
package is drawn, so a stored shape shows its shape and a stored fluid its colour.

One shelf per layer, matching the three independent per-layer queues, so a glance says not just
how full a store is but *which* layer is backed up. Slots fill along a shelf and then back a
row, so a part-full shelf reads as a queue with a front and a back.

**The rack is open and its shelves are runners, not plates.** Both are about being able to see
the cargo. The roof went so the top shelf is exposed; the shelves are five runners each because
a solid shelf is a roof for the shelf below it.

> That last one is a trade, not a fix. Stacked storage occludes itself from a top-down camera,
> and there is no arrangement of twenty-five packages per layer on a single 20x20 chunk that
> avoids stacking - a 5x5 grid at the package's own scale already spans 12.4 of it. So the top
> shelf reads fully, and the lower two read at the edges, through the runner gaps, and at
> shallower camera angles. Spreading the three layers across one deck would need packages about
> half the size to fit.

Neither renderer is registered anywhere. `CreateSimulationRenderers` reflects over
`AppDomain.CurrentDomain.GetAssemblies()` for `IIslandSimulationRenderer` implementations and
builds each through a dependency container, which reaches a mod assembly as readily as the
game's own - hence `[UsedImplicitly]` on both, as on every vanilla renderer. They are keyed by
simulation type, so one belt renderer covers all six belt variants, and the store needs a
concrete subclass per store type because an open generic cannot be constructed.

Also note the publicizer: `ShouldDraw` and `OnDrawDynamic` must be overridden as
`public override`, not `protected override` as the decompiled source shows.

> **A seam the compiler cannot check.** `SHELF_HEIGHTS`, `RACK_SLOTS` and `RACK_INNER` in
> `Tools/generate_meshes.py` are duplicated as `ShelfHeights`, `SlotsPerSide` and `RackInner`
> in `CargoStoreSimulationRenderer`. Nothing can read an `.obj`'s shelf heights back out, so
> moving the rack in the generator without moving these floats the cargo or sinks it into a
> shelf. The generator prints the numbers it used; the renderer's constants have to match.

The fluid store is a rack rather than the row of standing tanks it started as, which was a
correctness fix rather than a style one: it holds sealed `CargoPackage<FluidId>` containers,
not loose fluid, and tanks implied it pooled the stuff.

## One lane per layer, and why the cargo got big

A space belt carries four lanes abreast on three layers because a shape is small. A cargo
package is a shipping container, authored about as wide as a chunk, and four of those abreast
cannot be drawn anywhere near true size - drawn small they looked like crumbs.

So a cargo belt now uses **one lane per layer** (`CargoLanes.Travel`) and the other three refuse
everything, via a `PreAcceptHook` that always returns false. The picture and the simulation are
then the same thing: one file of full-width containers per layer, three files per belt, nothing
hidden. Before, three quarters of a backed-up belt's contents had nowhere to be drawn - and a
backed-up belt is the normal state of a buffer, so this was not an edge case.

The cost is buffer capacity: twelve lanes to three, a quarter of what a chunk held. Each slot is
still a whole package and a lane still holds several along a chunk, so a cargo belt is still far
denser than a shape belt, but it is a real reduction and it was a deliberate trade.

Only the belt is restricted. The packager, unpackager and store still accept a package arriving
on any lane, which costs nothing and keeps a line fed from somewhere unexpected working.

The lane index has to be **counted** in `CargoBeltSimulation`, because `Bundle.CreateLanes` hands
its factory a lane state and nothing else. It walks `for (lane) for (layer)`, so the nth call is
lane `n / NumLayers` - deterministic, but an ordering assumption about game code, which is why
it is named in `CargoLanes.LaneOfFactoryCall` rather than written inline as a division.

Containers are drawn centred across the belt, scaled so their long axis fills about 3.4
`TrackItemsSpacing` - taken from the theme rather than a world constant - and turned a quarter
turn when the mesh's long axis is its local X, so they ride across the belt like freight on a
flatbed rather than nose-first. Which axis is long is read from `mesh.bounds`, because this is
vanilla art and nothing says which way round it was authored. Height is capped at just over half
the width, since the fit is driven by the horizontal extent and a roughly cubic package would
otherwise come out as a tower.

## Telling a cargo belt from an ordinary one

A cargo belt draws with the game's own belt and pipe track, which is correct - it is a belt - and
also a problem, because it looks exactly like track that will refuse its cargo.

**Tinting the track does not work.** Per-instance colour is `BaseColorPerInstanceData` through
`InstancedMeshManager.AddWithPerInstanceData`, and it only reaches shaders written to read that
buffer. Every vanilla use is a dedicated indicator material - `RailSplitIndicatorMaterial`,
the train producers' `ColoredMaterial`, `VisualizationUnderlayMaterial` - not the island material
the track is drawn with. Feeding it a tint would compile, run, and change nothing.

So the distinction is geometry: `CargoBeltMarker.obj`, four short corner posts, attached to all
six belt variants as a `ModularIslandMeshDrawer` module on top of vanilla's drawer. Three
properties matter more than looks:

- **Rotation invariant**, so one mesh serves the straight run and both turns and there is no
  per-variant marker to keep in step with the placer's corner choice.
- **Outboard of the cargo**, because containers ride the middle of the belt at close to full
  width and anything central would vanish the moment the belt loaded.
- **Additive**, so the track underneath is untouched.

It is the one mesh authored *below* the deck, at `TRACK_Y = -2.07314`, because it has to meet the
space path track and `ModularIslandMeshDrawer.Module`'s offset is an integer `ChunkVector` - a
sub-chunk drop is not expressible there and has to be baked into the vertices. The generator's
"dips below the deck" check exempts it by name.

## Unloading a train straight into cargo - resolved, on the receiving side

A train *loader* already ate packages. The *unloader* could not emit them, so a cargo belt behind
an unloader sat empty and the chain only worked with a packager wedged in between.

**Why the two were not symmetric**, which is the useful part: loading only needed a *receiver*
made more permissive - `PackagedCargoStations` hooks the station's accept gate so it takes a
package as readily as a shape. A permissive receiver is safe, because it cannot break anything
that was not already offering. Unloading needs an adaptive *sender*: the station has to choose
what to offer, which requires knowing what is downstream.

The station does ask. It is right there in `TrainCargoToBeltFillingContainer<T>.Update`:

```csharp
while (CargoConverter.PeekCargoAsBeltItem(in State.Package, out beltItem)   // what to offer
       && itemProvider.NextLane.CanAcceptItem(beltItem))                    // asks the platform
```

But it asks one frame too late. `ICargoToBeltItemConverter` is handed only the package, never the
lane, so it cannot offer a package to a cargo belt and loose shapes to an ordinary one. And every
frame in between is on a generic type, which MonoMod refuses outright:

| Candidate | Why not |
|---|---|
| `TrainCargoToBeltFillingContainer<T>.Update` | generic type |
| `TrainCargoUnloaderSimulation<T>` | generic type |
| `ItemLaneBundle<TLane>.NextBundle`, where an adapter would go | generic type |
| the two converters, which *are* non-generic | handed only a package - no idea which lane is being offered to |

So the fix went where loading's did: **the receiving side**. A cargo belt's carrying lane now
accepts loose items and packs them itself, via `CargoIntake`. The unloader is untouched - it goes
on offering loose shapes exactly as it always did, and the belt assembles them.

Two mechanisms make it cheap:

- **`FastBeltPathLane.AcceptHook`.** `HandOverItem` calls it as
  `AcceptHook(this, ref receivedItem, ref remainingTicks)` and then **returns early if
  `receivedItem` is null**, so a hook can take an item off the belt entirely. That is the game's
  own API, and it is what lets a loose shape be swallowed into a filling container instead of
  riding the belt as a loose shape. An earlier attempt subclassed `FastBeltPathLane` and
  re-implemented `IItemReceiver`, because its accept methods are public but not virtual; that
  worked, but leant on interface-map rules for re-implemented interfaces and would have broken
  silently had anything dispatched through `IHookableItemReceiver`.
- **`TrainBeltToCargoFillingContainer`** does the accumulating. It is the game's own class, it is
  already an `IItemReceiver`, and it is exactly what the packager uses - so vanilla's
  serialization and vanilla's rule that an emptied package remembers its item until reset both
  come for free.

One shape container and one fluid container sit side by side per layer rather than making the
belt generic over its item type. A given belt only ever sees one kind, since its connector tags
decide that, so one of the two is always idle. That wastes a little state per segment and buys
not having to split `CargoBeltSimulation`, its state identifier, its factory and its renderer in
two.

The finished package is only cleared from the intake once it is actually on the belt, so a full
belt makes cargo queue in the intake rather than vanishing. And `PreAcceptHook` is consulted only
after the lane has checked it has room, so a full belt refuses loose items too - the back-pressure
reaches the station.

### Two consequences

- **It also fixes the old "silently refuses shapes" wart.** A cargo belt used to connect to an
  ordinary space belt and then quietly refuse everything it offered, which was listed here as
  needing either a replaced `ConnectableIslandSimulation` or a UI warning. It now packs them.
- **The packager is no longer required.** Anything that can hand a loose shape to a cargo belt
  gets it packed, so the packager is a convenience rather than a necessity. That is a design
  question rather than a bug: if the packager should stay mandatory, the intake needs gating to
  station outputs only, and there is no clean way to tell a station's hand-over from a belt's -
  it would mean a flag set from a hook on the non-generic converters and read from a hook on
  `FastBeltPathLane`, which is a hot method and global state.

## One cargo belt, not two - resolved

There used to be two belt families: three belt-tagged variants for shapes and three pipe-tagged
twins for fluids. They existed for one reason, recorded here from the start - a fluid train
station's input is a `SpacePipeInputConnector`, and a belt-tagged output will not snap to it.

There is now **one family of three**, carrying both connector types. Two facts make that work,
and neither is obvious:

- **`IslandConnectorData` allows several connectors at one pivot.** It keys them in a
  `MultiValueDictionary<LocalChunkPivot, IIslandConnector>` and rejects only two of the *same
  type* at one pivot: `if (values.Count != values.Select(x => x.GetType()).Distinct().Count())
  throw`. So West can hold a `SpaceBeltInputConnector` *and* a `SpacePipeInputConnector`.
- **The simulation has to claim a bundle per connector.** `ConnectableIslandSimulation`'s loop is
  `for (i = 0; i < NumItemReceiverBundles && i < connectors.Count; i++)`, so a second connector at
  a pivot is silently ignored unless the simulation says it has a second bundle. `CargoBeltSimulation`
  therefore reports **two** receiver and two provider bundles and hands back the same `PathBundle`
  for both indices. The island ends up with a `ShapeItem` chunk connector and a `FluidPackageItem`
  one at each pivot, feeding the same lanes, and
  `ItemInputChunkConnector<TItem>.CanConnect` matches whichever the neighbour has.

One pivot faces one neighbour, so only one of the two ever finds a partner - there is no
ambiguity about which connection wins.

Two things had to change to suit it:

- **The track classifier could not be used as-is.**
  `PlatformPathDrawingClassifier.TryClassifySpacePathNode` pairs every input with every output, so
  two inputs and two outputs at one pivot produce **four** identical West-to-East connections.
  `FixedList8Bytes.Add` does not dedupe, `TryClassifyNode` handles counts of 1 to 3 only, and the
  whole call returns false - which would leave every belt drawing *nothing*, since a path-track
  island has no platform frame to fall back on. `CargoAppearance.TryClassify` dedupes the
  directions and calls the game's own public `TryClassifyNode`, so the classification logic stays
  vanilla's and only the double counting goes.
- **The renderer's output connector is no longer index 1.** Inputs are added before outputs, so
  output n is connector `NumItemReceiverBundles + n` - which is 2 here, where connector 1 is the
  *second input*. Both connectors at a pivot share the pivot, so which tag is picked up does not
  matter.

Every cargo belt draws belt track, not pipe track, and takes the belt item spacing. It carries
discrete containers whichever line it is on, and the corner posts are what say it is a cargo belt.

The toolbar went from two folders to one for the same reason: the split existed because a player
laying one line had no use for the other's six pieces, and with a shared belt only the three fluid
machines are line-specific.

### Deleting an island definition breaks every save that contains one

The three `FluidCargoBelt*` ids are therefore **still registered**, hidden, as the unified belt.
Removing them cost a real save: a placed island is stored by definition id, and
`IslandLayoutSerializer.DeserializeIslandBlob` throws the moment it meets an id the session does
not have -

```text
Failed to deserialize island FluidCargoBelt (original: FluidCargoBelt)
  ---> Island definition was not migrated correctly. Perhaps a migrator is missing?
```

The game does have the interface that message is asking for: `IIslandAdditionalDataMigrator`, with
`CanMigrate(version, definitionId)` and a `Migrate` that can rewrite a `MigratableIsland`'s
`DefinitionId` - which is exactly a rename. Two reasons it was not used. Its `CanMigrate` is gated
on the **game's** `GameVersion`, which says nothing about a mod's own history; and the migrator
list is built inside the session with no Shifter rewirer over it, so registering one means another
detour. Three hidden definitions cost less and cannot fail.

They share the unified slugs, so they need no translation keys of their own, and they are outside
the placer's family, so nothing can build one. An old fluid cargo belt simply keeps working.

**The rule, since this has now bitten twice in one sitting:** an island id is part of the save
format just as much as a state layout is. Renaming or removing one needs the old id kept alive, or
a migrator, before the change ships.

## What a changed save state costs - a lesson paid for

Adding the intake containers to `CargoBeltSimulationState.Sync` broke every existing save, with an
error that points nowhere near the cause:

```text
Failed to deserialize map ---> Bad string LUT index: -1540349564, have: 119
  at ShapeItemSerializer.Deserialize
  at CargoPackageSerializer`1[TItem].Deserialize
  at TrainCargoFillingContainerState`1[TItem].Sync
  at TrainCargoTools.CargoBeltSimulationState.Sync
```

Each simulation state is written inside a length-delimited `ReadBlob`. A `Sync` that reads *more*
than was written walks off the end of its own blob into the next one, and the failure surfaces
wherever the garbage first has to mean something - here a shape's name, several frames away in
vanilla code.

The intake state is therefore **not serialized**. It costs at most `PackageSize - 1` items per
layer on a belt that is mid-pack, and only on the few segments actually fed loose items;
everything already packed sits on the lane bundle, which is saved. That is a bounded, nearly
invisible loss against losing every save.

Worth knowing for next time: `IPrimitiveSerializationVisitor.Version` is the **game's** version,
not the mod's, so it cannot tell one version of this mod from another. Persisting anything new
needs a mod-level version written *before* the first field, which is a thing to add once, on
purpose, rather than discover.

A note on a wrong turn: the vanilla `CargoPackageSerializer` looked asymmetric at first glance -
it writes the item under `TryGetItem` and reads it under `Amount != 0`. Those agree:
`TryGetItem` returns false exactly when `Amount == 0`. The serializer is fine; the blob length was
the whole story.

## Four containers to a lane, lying lengthways

A cargo belt held **32** items per lane, double a vanilla space belt's 16. That was deliberate -
buffering was the point - and it was wrong twice over: a belt held nearly as much as a cargo
store, which left the store pointless, and at minimum spacing the containers were half a world
unit apart, so they had to be drawn tiny or overlap.

`CargoLanes.SlotsPerLane` is now **4**, and one constant fixes all of it, because of a fact worth
knowing:

```csharp
FastBeltPathLaneState.Length_S => ItemCapacity * LaneConstants.ItemSpacing;
```

A lane's *physical length* is derived from its capacity, and the renderer normalises an item's
progress to 0..1 over that length before mapping it across the chunk. So a lane whose capacity is
four is exactly four minimum gaps long, and four containers - even jammed nose to tail against a
blockage - render at 0, 1/4, 1/2 and 3/4 of the chunk. **Even spacing falls out for free**; there
is no saturated-belt special case to write.

It also sets the size: one chunk divided by the slot count is how much belt each container gets,
which is what they are scaled to fill. They now lie **lengthways** along the run, nose to tail,
rather than across it - the quarter turn is applied only when the mesh's long axis is not already
the flow axis.

Two consequences:

- **Throughput is unchanged.** The rate past any point is `speed / ItemSpacing`, which does not
  depend on lane length. Only latency and buffering shrink - which was the point. Cargo does cross
  a chunk four times faster than a vanilla belt moves shapes, since the lane is a quarter the
  length at the same speed.
- **It is save-safe**, unlike adding a field. `FastBeltPathLaneState.Sync` reads the stored
  capacity and calls `Clear()` when it differs, so belts from an older save come back empty
  rather than corrupt.

## One cargo store, either kind

Same reasoning as the belt: a package is a package, and choosing the right store before knowing
what a line will carry is a choice with no interesting answer. `CargoStoreAny` replaces the two.

It holds a shape half and a fluid half side by side rather than one queue of discriminated
packages - the queues never interact, `CargoStoreState<TItem>` already does everything needed, and
a union would have meant a new serializer for no behavioural gain. **Each layer takes whichever
kind reaches it first** and refuses the other until it drains, which keeps capacity at 25 per
layer rather than 25 of each and keeps the rack honest: 25 slots are drawn per shelf, so a layer
able to hold 50 would under-report.

It is not built on `CargoStoreSimulation<TItem, TState>`, because that is a `Simulation<TState>`
and can own exactly one state. The two per-kind stores keep using it, untouched, and stay
registered but hidden - the same reason the old fluid belt ids are still here. An existing store
carries on working and simply cannot be built any more.

Shelf geometry moved to `CargoRack` so the two store renderers cannot drift apart.

## Telling a cargo belt apart: four decorations, then its own track

Five attempts. The first four all shared one mistake - they decorated something that still looked
exactly like a space belt - and they are worth listing because each failed differently:

1. **Four posts at the chunk corners.** Rotation invariant and clear of the cargo, and they read
   as *damage*: pillars at the edge of a belt look like something snapped off, which is worse
   than no marker because it implies the belt is broken.
2. **Low guard rails along the run.** Worse on corners - a mesh authored straight cannot follow a
   bend, so they jutted out of the outside of every curve like loose sticks.
3. **An accent-palette tint** through `TextureIndexPerInstanceData`. It reaches the trim and the
   direction arrows, which sample the accent palette, and *deletes* the deck plane, which has no
   such buffer - per-instance data on a material that does not declare it draws nothing rather
   than ignoring the data. A belt became a pair of floating edges. With the plane excluded it
   worked and was far too quiet, because the trim is a sliver of what you look at.
4. **A coloured wash under the deck.** Also too quiet to notice.

The track is now **the mod's own geometry**: a channel section with walls, capping rails and
sleepers, swept along a real quarter arc for the corners. It differs at every zoom and from every
angle, and there is nothing bolted on to be misread. The tint and wash console commands went with
it - knobs for tuning a colour nobody could see are dead weight.

`Tools/generate_meshes.py` grew `sweep_box`, which lofts a rectangular cross-section along a path
of `(x, z, heading)` frames. Two bugs in it were caught by the generator's own checks rather than
by eye, which is the entire argument for having them:

- The cross-section was swept *along* the path instead of across it, because the frame normal was
  written as `(cos, sin)` of the heading - the heading itself - rather than `(-sin, cos)`. The Z
  extent read `0.0 to 0.0`.
- Every box's end caps were wound with the side walls rather than against them: 48 unmatched
  boundary edges on the straight piece alone.

A sweep's handedness follows the direction it curves, so the two corner pieces come out mirrored
and one is inside out. Rather than special-casing ring order per turn - the sort of sign that gets
fixed in one place and forgotten in another - `cargo_track` measures its own signed volume and
flips if it is negative.

**Unverified:** which corner is which. A chunk's North is +y in game space and Unity's Z is -y, so
`corner_path(-1)` is taken to be the left turn. If the two corner pieces are swapped in game, that
sign is the only thing to change.

The overview map still uses vanilla's reduced mesh: a map blip wants the ordinary silhouette, and
`MeshBuilder.AddTranslateRotate` wants a `UnityMeshReference`, which a runtime-loaded mesh is not.

## Two bugs from the store and LOD

**The buildable store had no mesh.** Meshes were keyed by island id, and the combined store's id is
`CargoStoreAny` while its mesh file is `CargoStore.obj` - so the one store anybody can place drew
as a bare black platform, while the two hidden legacy stores that nothing can build got the art.
`MachineMeshes` is now an explicit island-to-mesh map rather than one list doing both jobs.

**Cargo vanished from belts at distance.** Two causes, both fixed: `ShouldDraw` used vanilla's
space-path cutoff of `BuildingLOD <= 3`, which is tuned for shapes that really are a few pixels by
then, and a container is a chunk-wide crate; and the package drawers ask their `LOD6Mesh` for
`options.LOD.IslandLOD`, which returns nothing above the levels it was given - one mesh supplied
means anything past its `Count` draws blank. The LOD handed to the drawers is now clamped.

## Cargo has to turn through a corner

Containers were drawn with the entry direction for the whole segment. That is fine for a shape -
vanilla draws shapes axis-aligned and never rotates them - and badly wrong for a long box: halfway
round a bend it still pointed the way it came in, so it lay across the track and hung off the
outside of the curve.

They now `Quaternion.Slerp` between the entry and exit facings by the item's progress. Slerp
rather than working out which way the corner goes: the two ends are at most a quarter turn apart,
so the shortest path is the way the belt actually bends, and a straight run has both ends the same
so the lerp is a no-op.

## Cargo belt speed

Shortening the lane to four slots made it a quarter the length of a vanilla space belt's, and at
the same steps-per-tick that meant crossing a chunk four times faster - cargo flew.

`CargoBeltSpeed` divides `SpaceConveyorSpeed` by five. Dividing rather than picking a number keeps
the reason for reading the space belt's speed in the first place: `SpaceConveyorSpeed` is a
`BuffableBeltSpeed` whose `StepsPerTick` is rewritten when a belt-speed research completes, and
the wrapper delegates every time it is asked, so those upgrades still land. A fixed `BeltSpeed`
would have quietly cut cargo belts off from research.

Unlike the lane-length change, this *does* change throughput: the rate past a point is
`speed / LaneConstants.ItemSpacing`. Five rather than four - which would exactly undo the lane
shortening - so freight reads a little heavier than the belt beside it.

## Detail on the machines, and the animation that is not there

The machines grew a layer of greeble: control cabins, ribbed flanks, exhaust stacks, collars where
a pipe meets a hull, handwheels on the fluid pieces, and pads where each machine meets the deck.
None of it means anything - it exists so a machine does not read as a featureless block at the
distance the game is actually played at. It is all placed *on* the hull rather than past the
footprint, which is the lesson the belt markers taught: anything sticking out gets read as a
connector or as damage.

**The first pass of it was invisible in game**, and the reason generalises: detail that changes
only the *surface* does not read at the distance a factory is looked at. Ribs a third of a unit
proud of a hull, a collar a hair wider than its pipe, a handwheel of radius 0.95 on a fifteen-unit
machine - all of it disappears. What reads is **silhouette**: things that break the outline, at a
size comparable to the machine rather than to its panels. Everything was roughly doubled and put
where it stands clear.

One bug came out of that pass and is worth naming because the class of it will recur: a stack was
placed *beside* a gantry leg rather than on top of one, so it hung in mid-air - "a weird floating
stem". Anything vertical needs its base put on something solid, and the generator cannot check
that for you; it only knows the bounding box.

The generator's own checks earned their keep again here - a pipe collar on the fluid store is
wider than its pipe, and at the old pipe height its underside sat below `Y = 0`. That is invisible
in a preview and would have been a collar buried in the platform.

**Animation was investigated and not built.** The game has a system for it and it is close to
usable:

- `IslandSimpleAnimationDrawer.Data` attaches to a definition's CustomData exactly like
  `ModularIslandMeshDrawer.Data`, and `RegisterIsland` picks it up when an island is added, so a
  modded island would be animated with no hook at all.
- The contract is fully readable from the drawer: `ISimpleAnimationDefinition` needs `MaxLOD`,
  `PlaybackOffset`, `PlaybackSpeed`, `Duration`, `PlaybackInterval` and `Elements`; each element
  needs `PositionCurve`, `RotationEulerCurve`, `ScaleCurve` - each `.Evaluate(float) -> Vector3` -
  and a list of `ILODMeshMaterial`.
- `RuntimeSimpleAnimationElementDefinition` exists with a public constructor taking exactly those.

The blocker is reach, not difficulty: that type lives in an assembly the project does not
reference and that is not in `decompiled/`, so the curve type cannot be read and building one
would be a compile-and-see loop against types that cannot be inspected. A probe confirmed it -
`error CS0246: RuntimeSimpleAnimationElementDefinition could not be found`.

Adding the reference and decompiling that assembly is the first step if this is picked up. A
packager's ram rising and falling is the obvious first animation, since the mesh already has a ram
as a separate block.

## The "empty" bubbles belonged to the belt, not the packager - resolved

The report was that a cargo packager showed a shape/fluid preview bubble on its **entrance**,
flashing empty, while the line through it worked perfectly. The bubble is not drawn by the
packager and never was.

`IslandPredictionRenderer.OnDrawDynamic` walks each island's *provider* bundles and draws at the
pivot of any whose `NextBundle` is null. An output pivot sits flush against the entrance of
whatever it feeds, so an end-of-line bubble on the belt in front of a machine renders on top of
that machine's doorway. None of these islands registered a prediction simulation, so they exposed
no receiver bundle, `ItemPredictionOutputChunkConnector.TryConnect` found nothing to link to, and
every belt feeding a cargo machine stayed a dead end as far as the prediction graph was concerned.

Prediction is a wholly separate simulation graph from the real one - a different system
collection, a different set of dependencies, its own update strategy - and `WithSimulation` does
nothing for it. `CargoPrediction.cs` supplies it for every island in the mod.

**Everything here predicts by forwarding**, which is not a shortcut. A cargo package is not an
`IItem`, so there is nothing to predict *as* a package; and the useful readout at the end of a
cargo line is which shape or fluid is inside the packages anyway. So a packager's output predicts
its input shape, a cargo belt carries that along, a store passes it through, and an unpackager -
which genuinely does emit that shape - comes out right by the same rule.

The dual-connector islands (the belt and the store) claim **two** receiver and two provider
bundles and hand back the same bundle object for both, for the same reason
`CargoBeltSimulation` does: `ConnectableIslandPredictionSimulation` bounds its loop by
`min(NumItemReceiverBundles, connectors.Count)`, so the pipe-tagged connector sharing a pivot with
the belt-tagged one is dropped unless a second bundle is claimed. Sharing one object rather than
making two matters - `NextBundle` is a single field, so whichever connector links first satisfies
both indices, where two separate bundles would leave the unused one dangling and drawing.

Reaching `WithPrediction` needs a cast to `IDefinedAccessibleSimulatablePlaceableIslandExtender`,
which nothing in this chain returns. Safe for the same reason it is in Crossover Platforms: one
`AtomicIslandExtender` implements every interface in the fork, and `WithPrediction` only records
a builder, so the order the simulation and prediction branches are declared in does not matter to
`Build()`.

Prediction simulations hold no `ISimulationState`, so none of this touches the save blob.
Registering one is a definition-time change, though, so it needs a restart rather than a reload.

## Three layers that looked like one

A loaded cargo belt appeared to be carrying a single file of containers, even though all three
layers were full and all three drained into a store correctly.

All three *were* being drawn. The renderer put each layer at
`SpacePathItemRenderingConfig.LayerOffset * layer + Height`, which is the theme's own spacing and
correct for what it was authored for: a shape is a fraction of a world unit across, so a fraction
of a unit between layers separates them cleanly. A cargo container is scaled to fill a whole belt
slot - about 2.3 units tall - so at that spacing the three layers interpenetrate almost entirely
and read as one object.

So a cargo belt sets its own layer spacing. `CargoLanes.LayerSpacing_W` is 3.0 units, sized from
the freight rather than from the theme, and `CargoLanes.LayerHeight_W` is the single place both
drawers ask where a layer sits - the track drawer stacks a tier there, the belt renderer puts that
layer's containers on top of it. Two copies of that arithmetic is exactly how the track and its
freight drift apart.

The stack is centred on the theme's *middle* layer (`Height + LayerOffset`) rather than built
upward from layer 0, so a cargo belt still sits roughly where a vanilla space belt does instead of
floating six units above the station it joins. `LayerOffset` is used only to find that centre.

Two knock-on fixes came out of the same measurement. The container height cap is now
`LayerSpacing_W * 0.78` rather than a fraction of its own length, so a container can never reach
the deck above it; and the belt renderer lifts each container by `-Packages.Bottom`, because the
package mesh is authored centred on its origin and was therefore riding half-buried in its deck -
the store had already found this and the belt had not.

## Unloading straight into a store or an unpackager - resolved

A train unloader could only feed a cargo belt. Reaching a store or an unpackager meant a segment
of belt in between, which is a chunk spent on nothing.

The cause is the one recorded under *Unloading a train straight into cargo*: an unloader offers
**loose** items and cannot be told to offer anything else, because every class on the sending side
is a generic type and MonoMod will not hook one. Every accommodation therefore has to be made on
the receiving side. The belt already did that through `CargoIntake`; the store and the unpackager
did not, so both simply refused.

**The store packs them itself.** It now holds one `CargoIntake` per layer - the same class the
belt uses, so the accumulation, the vanilla serialization and the rule that an emptied package
still remembers its item all come for free. `Receiver.CanAcceptItem` tests room for the *finished*
package rather than for the loose item, so back-pressure reaches the unloader at the moment it
asks rather than one package later. `Shelve` moves a completed package into the layer's queue and
only then clears the intake, so a package with nowhere to go stays in the container instead of
being dropped.

Its state is **not serialized**, for the reason `CargoBeltSimulationState` records at length: a
new field in `Sync` makes every older save walk off the end of its own length-delimited blob. The
loss is at most `PackageSize - 1` items per layer on a store that was mid-pack when saved.

**The unpackager passes them through.** `CargoPackageReceiver` now holds its layer's output
senders and hands a loose item straight to the first one that will take it, accepting only while
no package is being drained so the two sources cannot interleave. Reusing `CargoIntake` here
would have meant packing and then immediately unpacking - an unpackager fed loose items has
nothing to unpack, and routing them through a package first would hold a partial load hostage
until the train happened to complete it.

## A gauge on the packager

A packager is the only machine in the mod whose work is invisible. A belt shows its freight and a
store shows its shelves; a packager on a slow input line and a packager that is jammed look
identical, because neither emits anything.

`CargoPackagerModules` puts one fill bar per layer on the side panel, through
`ICargoPackagerView` - a two-member interface, non-generic so `GetModules` can type-test it with
only an `ISimulation` in hand, exactly as `ICargoStoreView` does for the store.

The maximum is read from the capacity provider every frame rather than captured, because
wagon-capacity research raises `PackageSize` mid-game and a gauge with a baked maximum would start
lying the moment that unlocks.

The bar is `HUDSidePanelModuleRocketProgress`, which is the generic current/max bar despite its
name, and the layer colours are the store's so the same layer reads the same on both panels.

## The red cross on a belt that works - resolved

Dragging a cargo belt against anything that feeds it drew a red conflict cross, even where the
belt then worked. New players read that as "this placement is wrong", which is the worst kind of
false signal: it is wrong about the thing it is most trusted for.

It is the dual connectors, as suspected. `PlacementConnectorDrawer` walks an island's connectors
one at a time and knows nothing about two of them sharing a pivot, and
`IslandInstanceModel.CreateConnection` marks a connector conflicting whenever the island on the
far side has no matching connector *there*. A cargo belt against a shape line therefore connects
its belt-tagged connector and conflicts its pipe-tagged one, at the same pivot, and both get
drawn. The cross wins.

`SkipConflictingConnectorsDrawingFlag` is the game's own opt-out. Vanilla sets it from a
`MetaIslandDefinition` field that a mod-built definition never passes through, so
`SkipConflictMarkers` decorates the island builder and attaches it to the definition
`BuildAndRegister` hands back - the flag is read at draw time, so after registration is early
enough, and decorating avoids reaching into ShapezShifter's private `IslandBuilder` field.

Applied to the dual-connector islands only, since they are the only ones that can conflict with
themselves. Connected and not-connected markers are untouched, so a cargo belt still shows a
green arrow where it joins something. The cost is that these islands never show a cross at all,
including where one would be earned - a cargo belt aimed at a blank platform edge. Acceptable
only because they accept nearly everything, so the deserved cross is rare and the false one was
constant.

The attach is guarded with `Has<T>`, and that guard is not decoration. `BuildAndRegister` runs
again on every scenario load against the same definition objects, `CustomDataHolder.AddFlag` is a
plain `Attach`, and `TryGet` throws `MultipleDataFitDataTypeQueryException` rather than picking
one when a type matches twice. Without the guard the second session of a run would throw out of
`Has<SkipConflictingConnectorsDrawingFlag>` on a placement worker thread every frame.

## Snapping to a pipe-tagged output - resolved

A dragged cargo belt snapped to a shape packager's output and not to a fluid packager's, even
though it connects to both once placed.

A path placer decides which way to face by asking one question of what is already on the map:
`EntityConnectionWorldIOQuery.TryGetUniqueOutput`, which looks for connectors that are exactly its
`TOutput`. The cargo placer is typed on the belt pair - a cargo belt carries both tags, so
something had to be picked - and a fluid packager's only output is a `SpacePipeOutputConnector`.
Invisible to the query, so no direction to snap to.

There is no way to type the query on both. `TInput`/`TOutput` are constrained
`class, IEntityConnector, new()`, so the interface the two connectors do share,
`ISpacePathOutputConnector`, cannot be used - an interface has no `new()`.

So `EitherTagIOQuery` asks twice: two vanilla queries, one per tag, and a connector of either kind
counts. Belt first, so a cargo belt meeting another cargo belt resolves as it always did. Every
rule about what *is* a connector stays inside the game's class, which matters more than it looks -
`ComputeConnectors` runs the whole extender stack, notches and foundations and universal
connectors included, and none of that is worth reimplementing to add an `or`.

Getting the query in meant giving up calling
`PlatformIslandsPlacersCreators.CreateSpacePathPlacementInitiator`, since that builds the query
internally. `BuildInitiator` is now vanilla's own sequence, assembled here, with that one object
substituted - every part is a game class with a public constructor, so nothing about placement is
reimplemented. Two deliberate differences from vanilla fell out of it:

- **No pipette registration**, which retires a workaround rather than adding one. Vanilla adds
  every family member to the pipette map and `DefaultIslandPlacementExtender` has already added
  each of them; `Dictionary.Add` throws on the duplicate. That used to need a scratch dictionary
  passed into the creator, and is now simply a call not made.
- **No port buildings.** Vanilla threads `portSender`/`portReceiver` down to `CreatePathPlacer`,
  and the *island* overload ignores both - only the building overload uses them, for
  `PathAtNotchesUpgradeToPortsProcessor`. The `fluidPorts` flag this mod carried for them was
  therefore doing nothing, and is gone.

## Three layers, one of them fed

Reported as "the belt still only shows one layer". It is not a rendering bug this time.

A space path's three layers are the **platform's three build floors**:
`NotchDefinition.GetNotchLocationOnChunk_L` ends with `result.z = layer`, and each belt port
building feeds the one layer its own floor sits on. A packager fed from a single floor therefore
packs on one layer, emits on one layer, and the cargo belt carries one file. The other two decks
are real and usable - they need a feed on those floors.

Worth recording because the three-tier track made this visible for the first time. A single-tier
model hid an empty layer; three tiers advertise it, so a single-floor build now reads as
two-thirds broken when it is merely two-thirds unused. That is the honest picture and the tiering
stays, but it is the kind of change that turns a non-problem into a support question.

## Cargo handed to an ordinary belt was destroyed - resolved

A cargo belt, store or packager could be pointed at an ordinary space belt, a space pipe, or a
platform's belt port. The connection formed, the package was handed over, and it was then
destroyed somewhere downstream that expected a `ShapeItem`. Silently losing a player's cargo is
the worst outcome this mod had.

**The connection cannot be refused.** `ItemOutputChunkConnector<TItem>.CanConnect` matches on the
item type its connector maps to, and `ConnectableIslandSimulation` derives that from the connector
class with a hard `is` test - `SpaceBeltOutputConnector` to `ShapeItem`, `SpacePipeOutputConnector`
to `FluidPackageItem`, `NotImplementedException` otherwise. A shape train station's input is a
plain `SpaceBeltInputConnector`, the same class an ordinary space belt presents, so no type-level
rule can accept the station and refuse the belt. Subclassing does not help either: the `is` test
would still land on `ShapeItem`, and `IsCompatibleConnector` on both sides is `other is
SpaceBeltInputConnector`.

**So refuse the hand-over.** `FastBeltPathLane` asks `NextLane.CanAcceptItem` before passing
anything on, so a receiver that answers no leaves the package where it is and the line backs up -
the game's own signal for "this does not work", and nothing is destroyed.

Two pieces make that possible:

- `CargoBeltLane`, a subclass of `FastBeltPathLane` that overrides nothing. A cargo belt and an
  ordinary space belt are otherwise the same class, and a vanilla lane's `CanAcceptItem` says yes
  to anything (it consults `PreAcceptHook`, and vanilla space paths set none), so there was no
  question a sender could ask. Now there is a type.
- `CargoHandover.GuardedProviderBundle`, returned from `GetItemProviderBundle`, which wraps
  whatever `ItemOutputChunkConnector.TryConnect` assigns to `NextBundle`. That one setter is the
  only way an island acquires a downstream, so wrapping it catches every case.

**The allow-list has to look past the DummyLane.** The first version allowed `DummyLane`, on the
grounds that stations and this mod's own machines put one in front of their real receiver. That
was wrong in exactly the case that started this: `NotchInputAdapterSimulation` - the platform port
- is twelve DummyLanes. A DummyLane holds nothing and forwards, so the guard follows `NextLane`
and decides on what actually accepts:

| Terminal receiver | Verdict |
| --- | --- |
| `CargoBeltLane` | another cargo belt |
| `ICargoPackageSink` | this mod's store and unpackager receivers |
| `TrainBeltToCargoFillingContainer<ShapeId/FluidId>` | a train station's loader, and this mod's packager, which re-hosts the same class |
| anything else | refused - a vanilla path lane, a platform port's buffer |

The filling container is on the list only because `PackagedCargoStations` detours the converter it
asks; without that detour a station would refuse a package by itself and the entry would be wrong.

The store and the packager push through `GetSender(...).NextLane` directly rather than through a
lane, so they call `CargoHandover.Allows` in their own `Drain`/`TryEmit` instead of going through
the wrapper. A blocked packager stalls with a full filling container; a blocked store keeps its
backlog. Neither loses anything.

## Open questions

- **Do buildings accept containers?** Deliberately dodged: an unpackager sits in front of
  ordinary machinery. Train stations were the case worth solving properly, and they are solved.
- **Balance.** A cargo belt moves `PackageSize` times more per slot than a shape belt. That is
  the point, but it needs a cost and a research gate.
