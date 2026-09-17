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
- ~~Unlocked at the *first* milestone so they can be tested without an endgame save.~~ Resolved:
  two research nodes, see "Two research nodes, one selector" below.
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

Live tuning earns its keep because a mod DLL is memory-mapped once loaded, so new code needs
a restart - and a colour is the definition of something that has to be judged by eye.

### The palette can be read - and it is a shade ladder, not a colour palette

Sampling vanilla meshes works and is the wrong ceiling. It yields a handful of coordinates, so
the five roles were most of what the machines had, and **four flat colours across a whole
chunk-sized machine is the single thing that made them read as unfinished** next to shipped
art. Measured, rather than asserted:

| | verts | tris | distinct palette entries |
|---|---|---|---|
| `DiagonalCutter.fbx`, the shipped sample - **one tile** | 1,061 | 1,129 | **319** |
| `FluidCargoStore.obj` before this change - **a whole chunk** | 834 | 1,492 | **4** |

The polygon budget was never the problem. The colour budget was.

**And the gap is not texture.** `UberBuildingShader` has no `_BaseMap` and no `_MainTex` at
all. What it has is `_MaterialLUT`, a 256x256 *material palette*, plus procedural metal, noise
and scratch passes layered over whatever UV0 samples - passes these meshes already get. There
is no albedo map missing here.

A palette can be read. `Graphics.Blit` to a `RenderTexture` and `ReadPixels` back works on a
texture with `Read/Write Enabled` off, which every shipped texture has - the same trick
`cargotools.dumpatlas` uses, moved to load time.

**What is actually in it, which changes the plan.** Dumped from the shipped build, the LUT is
**75.3% unassigned** - the opaque magenta filler, the classic "no texture" pink. What is left
is **27 visibly distinct cells, 24 of them neutral**: a ladder from white to black. The only
real colours are pure red, one teal `(118, 231, 202)` and one periwinkle `(105, 115, 182)`.
There is no orange, no yellow, nothing warm. *The orange on a vanilla platform edge does not
come from this LUT*, so no amount of searching will find it.

Two ways of counting that got it wrong first, both caught by looking at the swatches rather
than at the code:

- **Relative saturation files near-blacks as hues.** `(max - min) / max` divides by almost
  nothing on a dark cell, so `(19, 19, 30)` scores 0.37 and reads as coloured. The palette has
  three such blue-blacks; calling them hues took the ladder's darkest rungs away from the roles
  that wanted them and left them where a role asking for a colour could have claimed one.
  `(max - min) / 255` asks how far apart the channels are, which is what "is this grey" means.
  On this palette the three real colours score 0.30 to 1.00 and everything else 0.06 or less,
  so the threshold sits in a gap rather than on a judgement call.
- **Exact-RGB deduplication over-counts.** 36 cells by that measure, 27 once colours within six
  levels of each other are merged: the red block carries four near-identical neighbours a
  channel or two apart, and several rungs repeat within three levels. Each of those is a cell a
  role can claim while believing it took a different colour - the exact failure this pass
  exists to prevent, surviving inside the fix for it.

So the separation available is **light against dark**, which on inspection is how the shipped
buildings read anyway. Roles ask for a rung on that ladder; the three that genuinely want a hue
- `warn`, `glass`, `light` - ask for one of the three that exist. The two roles that had been
named for a colour the palette does not hold, `copper` and `wear`, are now `collar` and
`scuff`: a name describing the ask would be a promise the code cannot keep.

Three filters make the search usable rather than lucky, each for a failure that looks
deliberate rather than broken:

- **The filler is rejected by colour, not by alpha.** It is fully opaque, so an alpha test
  keeps it and a role asking for something absent is answered with a hole in the atlas.
- **Only cells whose four neighbours match**, sampled at the texel centre. A palette is blocks
  of flat colour and the sampler filters bilinearly, so a seam coordinate renders as a blend of
  two swatches - a colour that appears nowhere in the game. It also discards the antialiased
  fringe around the filler, a few hundred near-pink cells a warm role would leap at.
- **Roles claim cells greedily, against what is already taken.** This is the one that matters
  most and was missing from the first attempt: asking eighteen roles independently for their
  nearest cell returned **ten** distinct answers, with four sharing one mid grey and every
  role that wanted a hue landing on a grey. Simulated against the real dump, claiming gives
  **18 of 18**, spanning luma 0.14 to 1.00 plus the three hues, with no two within six levels
  of each other.

Sampling vanilla meshes is kept as the fallback for when the material or the LUT cannot be
reached, with the extra roles aliased onto the five it can find. `cargotools.palette` reports
which path ran and how many distinct coordinates came out of it, because "the machine looks
flat" and "the fallback ran" are the same symptom.

`block` and `tube` take the chamfer in a **lighter** role than the body (see `LIGHTER` in the
generator). That is free detail: the geometry was already two lofts, they simply shared a role.
A shoulder that catches the light is most of what makes a box read as machined.

The result on disk: the packagers carry 12 roles each and the stores 8 to 9, against 4 before.

### The colours were never in the LUT

Everything above is about shades, and it had to be, because the LUT has nothing else. That was
the wrong conclusion to stop at: **most buildings in this game are orange**, and none of that
orange is in `_MaterialLUT`.

The tell was in the shipped sample. Decoding `DiagonalCutter.fbx`'s own UV0 and looking each
coordinate up in the dumped palette, 497 of its 1,598 vertices land on something with a hue -
and every one of those hues is a **red**, from `(255, 0, 0)` through `(255, 87, 87)` to
`(255, 219, 219)`. A cutter is not red. So the red is not what renders.

`AccentColorPalette` is the answer:

```csharp
private static readonly int GlobalAccentColorPaletteShaderPropId =
    Shader.PropertyToID("_G_AccentColorPalette");
...
Shader.SetGlobalVectorArray(GlobalAccentColorPaletteShaderPropId, AccentColorVectorArray);
```

Up to **15 live colours** in a global shader array, and a face is painted from it by where its
UV sits. The palette texture is a 16x16 grid and one column of it is the accent column;
`AccentColorMeshCreator.TryGenerateUniqueAccentColoredMeshRef` recolours a mesh by sliding UVs
down that column, which makes its arithmetic the specification:

```csharp
if ((int)math.floor((vector.x + -0.0625f) * 16f) != 0) { /* not an accent vertex */ }
int id = (int)((0.9375 - (double)vector.y) * 16.0);
```

The column test is on `u` alone - `u` in `[0.0625, 0.125)` - and the slot is a row of `v`. Read
backwards, slot *n* sits at `(0.09375, 0.90625 - n * 0.0625)`; fifteen are addressable, since
slot 15 would be at `v = -0.03125`, off the texture. Three of the reds this repo found in the
LUT were at `u = 0.0684`, `0.0840` and `0.1074` - all inside that column. **The LUT paints the
accent column red as a placeholder.**

So `accent`, `warn`, `glass` and `light` now ask for accent slots 0 to 3 rather than for a cell
of the LUT, and they need no search at all: the slot names its own coordinate. The other
fourteen roles stay on the shade ladder, which is still the right answer for a hull.

Two consequences worth keeping:

- **A mod that uses slots follows the game's palette.** Where the game's own colours change -
  by theme, or by a future update - the machines change with everything around them instead of
  drifting away from it.
- **The entries cannot be read statically.** `MetaAccentColorPalette` is authored
  ScriptableObject data. The global array does read back, so `cargotools.accents` prints what
  each slot currently holds, and `cargotools.accent.<role> <slot>` repaints live.

### Which slot each role takes

Chosen by eye against the live palette, because nothing makes them derivable:
`MetaAccentColorPalette` is authored data, so which slot is which colour is a thing somebody
has to look at.

| Role | Paint | Triangles |
|---|---|---|
| `accent` | slot 13 | 4,972 |
| `frame` | slot 10 | 2,960 |
| `deck` | slot 10 | 2,420 |
| `metal` | slot 11 | 1,252 |
| `rail` | slot 0 | 740 |
| `collar` | slot 10 | 508 |
| `warn` | slot 4 | 420 |
| `fluid` | slot 7 | 418 |
| `glass` | slot 2 | 310 |
| `shadow` | slot 13 | 224 |
| `hull` | slot 3 | 154 |
| `cargo`, `trim`, `pale` | slots 10, 0, 10 | 88 each |
| **`hullDark`** | **the shade ladder** | **6,032** |

`hullDark` stays neutral deliberately. It is the largest surface in the set by a distance -
more triangles than any other role, in all 33 meshes - so it is what everything else is read
against, and accents stop reading as accents when the ground they sit on is one of them.

**`rubber`, `light` and `scuff` are gone.** Counting triangles by role turned up three that
painted **nothing at all** - they had been named for jobs the models do not have. A role that
colours nothing is worse than no role: it is a console command that appears to do nothing,
which is exactly how it was found. Add one back at the *end* of the list when there is geometry
for it; inserting shifts every later role's sentinel.

That count is worth repeating whenever roles change. The generator writes a sentinel UV per
vertex, so the .obj files can be read back directly - there is no need to guess whether a role
is doing any work.

| Command | What it does |
|---|---|
| `cargotools.accents` | the 15 live accent colours, with the uv for each |
| `cargotools.accent.<role> <slot>` | paints a role from that list, no restart |
| `cargotools.palette` | the resolved uv per role, and which path resolved it |
| `cargotools.uv.<role> <u> <v>` | the raw form, for a coordinate off the LUT itself |

### A repaint has to work from a copy

`ApplyPalette` used to rewrite the live mesh's UVs in place and match on sentinels the next
time round. That only worked while no resolved coordinate could look like a sentinel - the
five sampled coordinates never landed in the bottom-left corner of UV space. Resolving against
the material palette removes the guarantee, because the search window is wherever the art is.

`CargoMeshes` now keeps each mesh's UVs exactly as the file held them and rebuilds from that
copy every time, so the second application - which `cargotools.uv` makes on every change - is
identical to the first.

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

Containers are drawn one file per layer, the three files abreast across the deck (see "Three
layers, one deck"), scaled so their long axis fills their slot along the run, and turned a quarter
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

Prediction is **not** registered through the chain's own `WithPrediction`, which would be the
obvious call. `AtomicIslandExtender.Build` re-arms itself only when `WaitAllRewirers` sees every
branch clear its link, and the prediction branch fires from a single postfix -
`PredictionSystemsInterceptor` on `BuiltinPredictionSimulationSystems.CreateSimulationSystems`.
That method's one caller is skipped outright when the game setting
`setting.simulation-settings.prediction` is off, so **any player who turns predictions off** never
completes the branch. The chain then never re-arms and every island is spent on the first scenario
of the process, which is the main menu's background game rather than the player's save: no
definitions, no toolbar entries, no unlocks, and no error anywhere. A Crossover Platforms player's
log is where this was caught; see that mod's DESIGN.md for the log signature.

So each island's prediction goes on by hand through `ReArmingRewirer`, re-armed per scenario load
and stopped in `Dispose`. The chain then waits only on branches that do fire. It also loses its one
cast: taking placement and the toolbar *before* the simulation
(`WithDefaultPlacement().InToolbar(..).WithSimulation(..)`) lands back on `IAtomicIslandExtender`
under its own power, and only `WithPrediction` ever needed reaching for. Crossover Platforms still
casts, because it declares its simulation first and has no such route.

Prediction simulations hold no `ISimulationState`, so none of this touches the save blob.
Registering one is a definition-time change, though, so it needs a restart rather than a reload.

## Three layers, one deck - resolved twice

A loaded cargo belt first appeared to be carrying a single file of containers, even though all
three layers were full and all three drained into a store correctly.

All three *were* being drawn. The renderer put each layer at
`SpacePathItemRenderingConfig.LayerOffset * layer + Height`, the theme's own spacing, correct for
what it was authored for: a shape is a fraction of a world unit across, so a fraction of a unit
between layers separates them cleanly. A cargo container is scaled to fill a whole belt slot -
about 2.3 units tall - so at that spacing the three layers interpenetrated almost entirely and
read as one object.

**The first fix was to stack them further apart, and it was the wrong fix.** A cargo belt set its
own `LayerSpacing_W` of 3.0 units and the track drawer stacked a tier of track at each height.
That made three distinct layers, and it made a six-unit tower: from the game's angled camera the
top deck stands in front of the two below it, so a player still could not see what a belt was
carrying at a glance. Three tiers of track advertised the problem rather than solving it.

**Vanilla does not stack.** `SpacePathSimulationRenderer.DrawItems` computes

```
num3 = (layerIndex - 1) * TracksSpacing + (columnIndex - 1.5) * TrackItemsSpacing
```

and applies `num3` *perpendicular to travel*, alongside a vertical `LayerOffset * layer + Height`.
A space belt fans its layers **across** the track as well as through it - which is the whole
reason all three floors of a vanilla belt are legible at once, and it is not something the
vertical offset alone would ever have achieved.

So a cargo belt now does the same with the lane term dropped, there being one lane per layer:
**three files abreast on one deck**, at `CargoLanes.Across_W(layer) = (layer - 1) * 2.4`. Nothing
is behind anything from any angle, and the track drawer is back to a single tier.

2.4 is the mod's own number for the same reason the old vertical gap was: `TracksSpacing` is
authored for shapes and freight this size would overlap at it. It is sized to the deck instead -
`cargo_track` sweeps to +-3.7, so the outer two files sit at +-2.4 and the deck edge stays clear.

### Three constraints on a container, not two

`CargoPackageMeshes` scales the crate uniformly to fill its slot along the run, and that says
nothing about how wide it then is. With three files abreast the width is what binds, so the
constructor now takes `maxAcross` as well: the *short* horizontal axis is capped at
`LayerAcross_W * 0.92`, and the smallest of the three constraints wins.

The height cap went the other way and is now gone for belts. It existed to stop a roughly cubic
package growing into a tower that reached the tier above; there is no tier above any more, and a
cubic package is caught by the across cap first.

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

## Two research nodes, one selector

Everything used to unlock at `ByIndexMilestoneSelector(0)`, which was a testing convenience. It is
now two purchasable nodes: **Cargo Machines** (belts, corners, both packagers, both unpackagers)
and **Cargo Stores** behind it. The legacy hidden definitions ride with their modern equivalents,
so a save holding one keeps working.

### `UnlockedWithNewSideUpgrade` cannot be shared between islands

The obvious call registers the node once *per island group*, not once.
`UnlockIslandWithNewSideUpgradeResearchProgressionExtender.ExtendResearch` is invoked for each
group and its body is `SideUpgradeBuilder.Build(...)`, and `Build` appends to `_SideUpgrades`,
`_ShopItems`, `_AllUpgrades` and `_UpgradesById` every time it is called. Ten islands sharing one
builder therefore puts ten identical nodes in the shop.

`CustomSideUpgradeSelector` looks like the escape and is not: its `Select` is literally
`SideUpgrade.Build(scenarioId, progression)`, so passing it to `UnlockedWithExistingSideUpgrade`
duplicates in exactly the same way.

**So `CargoUnlock` is a get-or-create `ISideUpgradeSelector`.** It answers `Select` from
`progression.TryGetUpgrade`, and builds only when the lookup misses. The first island extended in
a scenario creates the node; every island after it gets the built one, and
`UnlockIslandWithExistingSideUpgradeResearchProgressionExtender` appends its group to that node's
`Rewards`. Registration order stops mattering.

The lookup is keyed on the `ResearchProgression` passed in rather than cached in a field, because
`ExtendResearch` runs once per scenario load with a fresh progression each time. A cached upgrade
object would be appended to a progression that never contained it.

`TryGetUpgrade` returns `IResearchUpgrade`, and `_UpgradesById` holds levels and side quests
alongside side upgrades, so the result is tested with `is ResearchSideUpgrade` rather than cast.

### The category is copied from the train station node, not named

A side upgrade's `Category` is authored per scenario - `ResearchProgression` reads it from
`serialized.ScenarioContent.UpgradeCategories` - so no string can be hardcoded and be right in
every scenario. `ResearchProgression`'s constructor logs `Unknown/non-configured upgrade category`
for a shop item whose category is not in that list, and the node then has nowhere to render.

So the nodes find the vanilla upgrade that **rewards a train station island group** and copy its
`Category`, its `ImageId` and its `Id` (as the prerequisite). A reward is structural where a name
is not: whatever a scenario calls its nodes, the one handing out the station group is the one a
player reaches before cargo tools are worth anything.

The search runs over `AllUpgrades`, not `SideUpgrades`, because `ResearchLevel` is an
`IResearchUpgrade` too and hands out island groups the same way - whether stations come from a
milestone or from the shop is an authoring decision the mod does not need to know. A side upgrade
is preferred when both match, since only a side upgrade carries a category.

**Nothing vanilla is called "TrainStation" except the spacers.** The first version matched that
substring and anchored both nodes to `CBTrains_StationSpacer` - cosmetic filler - because
`AuthoringIslands` names the real stations `TrainShapeLoadersGroup`, `TrainFluidUnloadersGroup`
and so on, and only `TrainStationStraightSpacerShapeGroup` and its three siblings carry the word
"station". The marker is now "Train" plus "Loader", which covers unloaders as a substring and is
the right anchor anyway: a loader is what a cargo belt exists to feed. Spacers stay as a
last-resort fallback.

That mistake did prove one thing worth keeping: the run matched a real reward group, so island
group **ids do mirror the `AuthoringIslands` field names**. Those fields are the only evidence
available - a field name is not an id - and this is the only confirmation that reading them is
sound.

### A preview image is not optional

`HUDResearchSideUpgradeDisplay.RebuildView` calls `GameData.GetImage(upgrade.ImageId)`
unconditionally and `GetImage` throws on an id it cannot resolve, the empty one included. The
throw comes out through the whole `HUDResearchTree` construction and leaves the research screen
half-built and unclosable. Borrowing an id off a node the game already renders is the only way to
be sure it resolves, since image ids live in Unity assets. Extended Research hit this first; the
anchor gives one for free, and `BorrowImage` is the fallback.

### Still to settle

- **Cost.** Both nodes are 4.8k as the research screen shows it, set by hand: cargo tools are a
  late convenience, so they are priced with the endgame train nodes rather than the cheap early
  ones.

  **A `ResearchPointCurrency` is not the number the player sees.**
  `StringFormattingExtensions.Format(this ResearchPointCurrency)` is
  `FormatIntegerMax4Digits(amount.Amount * 100)`, so the constant is in *hundreds* of displayed
  points: `48` renders as "4.8k", and a literal 4800 would have priced these at 480k. Confirmed
  in game - an earlier 100 displayed as 10.0k.
- **Existing saves.** The gate is new, so a save that is past the first milestone loses the
  toolbar entries until the nodes are bought. Already-placed islands are unaffected - unlocking
  gates placement, not simulation.

## Wiki entries, assembled from two halves

Every vanilla machine has a knowledge-panel entry, so the cargo machines have five: an overview,
and one each for the belt, the packagers, the unpackagers and the store.

**Shifter has no API for this.** `grep -rli wiki` over ShapezShifter finds only incidental
matches in the island and building builders. The game keeps the pieces in two places and both
have to be reached:

| Half | Where it lives | How the mod reaches it |
| --- | --- | --- |
| The *references* - which ids exist, their category, the research each waits for | `ResearchProgression.WikiConfiguration.WikiReferences` | already handed to every `IIslandResearchProgressionExtender` |
| The *entries* - a `MetaWikiEntry` per id | `GameData._WikiEntries`, read via `IGameData.GetWikiEntry` | a detour on `GameData.GetWikiEntry` |

A reference whose entry cannot be found throws out of `WikiDatabase`'s constructor, so a half
landing is worse than neither: it takes the session. `CargoWiki` therefore refuses to register
any reference unless its lookup detour installed.

### Why a detour and not a dictionary insert

`_WikiEntries` is publicized and could simply be added to. It should not be: `GameData` outlives
a session and a `ResearchProgression` does not, so an insert would run again on the next scenario
load and `Dictionary.Add` throws on a duplicate key - the same trap the pipette map sets. A
detour is stateless with respect to session count.

### The ordering is not luck

`Register` is driven from the research extenders, and Shifter runs those from
`GameScenarioInterceptor`, an `ILHook` on `GameMode.From` that fires **immediately after
`new GameScenario(...)`**. `GameMode.From` constructs `new WikiDatabase(...)` thirteen lines
later, off the same `gameScenario.Progression`. So the references are always in place before
anything reads them. Worth writing down because nothing enforces it: if Shifter ever moved that
hook later, the entries would silently stop appearing rather than fail.

### Things that are not ours to name

- **The title key is fixed.** `WikiDatabase.Convert` builds it as
  `("wiki." + entry.Id.Id + ".title").T()`, so an entry called `CargoTools_CargoBelt` must have
  `wiki.CargoTools_CargoBelt.title` and nothing else will do.
- **A `MetaWikiEntry`'s id is its `name`.** It is a `ScriptableObject` with `Id => new
  WikiEntryId(base.name)` and no id field, so `CreateInstance` then assign `.name`.
- **`SerializedTranslationId.T()` returns null**, not an empty text, for an unset id. Every
  heading and text block therefore gets a real key.
- **A category needs an icon that resolves**, for the same reason a research node needs a preview
  image, so the cargo category borrows the first icon id already in use.

### What the exported base data settled

The first two versions of these pages were written without ever having read a vanilla entry -
they are authored ScriptableObjects and cannot be read out of `decompiled/`. That was avoidable:
`debug.export-game-data` writes `basedata-v<N>/translations-en-US.json`, which holds all 499
`wiki.*` strings, and `basedata-v<N>/scenarios/*.json`, which holds `WikiConfiguration` with
every entry id and category.

Four things were wrong until that was read, none of them guessable:

| | Vanilla | What this mod had |
| --- | --- | --- |
| Entry ids | `WK<Category>_<Thing>` | `CargoTools_CargoBelt` |
| Text keys | `wiki.<id>.text-intro-1`, `-2`, `text-robot` | invented `cargo-tools.wiki.*` names |
| Headings | **none** - not one of the 149 entries uses `MetaWikiEntryContentHeadingData` | four headings per page |
| Closing line | `text-robot`, a dry aside, on 60 of 149 entries | absent |

The pages now carry vanilla ids, no headings, `

` paragraphs, links out to
`WKTrains_TrainStations`, `WKIslands_SpaceBelts`, `WKIslands_Floors`, `WKFluids_Pipes` and
`WKProcessing_Intro`, and a robot line each.

`Tools/check_translations.py` knows those five vanilla ids as well as the mod's own, so a typo
in a `<gll:>` target is caught at build time rather than becoming an orange link that plays an
error sound.

### Writing them like vanilla writes them

The first version of these pages was plain prose, and it read as a mod's README rather than a
wiki entry, because it used none of the markup vanilla uses. Translation text is parsed as
extended XML by `TranslationExtendedXMLParser`, and `TagMatch` in `Core.Localization` is the
whole vocabulary:

| Tag | `DefaultTextStyleProvider` emits |
| --- | --- |
| `<gl>` | `<color=#ff9e16><b>` on a dark chip - **the bold orange a concept is written in** |
| `<gll:EntryId>` | the same orange, underlined, wrapped in a TMP `<link>` |
| `<b>` `<info>` `<unit>` | bold; italic `#ffffff55`; 65% dimmed and letter-spaced |
| `<link:Id>` | `<color=#00d2ff><b><u>` - the blue used for external links |
| `<hotkey:X/>` `<icon:X/>` `<wip-warning/>` | self-closing chips |

**The separator is a colon.** `ParseTagData` is `inTag.Split(':')`, so `<gll:CargoTools_CargoBelt>`
and never `=`. Anything not in the table is treated as a placeholder and must self-close, which
is what makes `<layer/>` work.

A `<gll:...>` target is a wiki entry id: `HUDWikiContentRenderer.OnLinkClicked` prefixes it with
`glossary.` and navigates. Ordinary `MetaWikiEntryContentTextData` blocks register the same
handler, so `…TextWithLinksData` is needed only for an external URL.

A malformed tag throws `XML Tag not properly closed`, the translation file fails to load and the
mod aborts - with no clue which string was at fault. `Tools/check_translations.py` walks the file
with the same rules and also checks that every `<gll:>` target is an entry the mod defines.

### Pictures, and where they have to come from

A wiki image block holds a **`Sprite` directly** - `MetaWikiEntryContentImageData.Image` - so it
needs no registration. A research node's preview is the opposite: it is a `GameImageId` resolved
through `GetImage`, which throws on an id it does not know.

`CargoImages` therefore detours `GetImage` the same way `CargoWiki` detours `GetWikiEntry`, and
for the same reason - `GameData` outlives a session, so inserting into `_Images` would need
guarding against a second load. Sprites load on first request, because nothing asks for one until
the research or wiki screen is opened.

The pictures are screenshots. They have to be: the machines' appearance is generated at runtime
from `Tools/generate_meshes.py`, so there is no authored art to point at, and a picture drawn
from outside the game would show something that is not what the player will see.

`MetaWikiEntryContentVideoData` wants a `GameVideoId`, which is authored-asset territory, so the
clips vanilla shows are not available - stills are as far as this goes.

### What could not be used

`MetaWikiEntryContentIslandPanelData` would have rendered the island's own panel inside the entry,
which is what several vanilla entries do. It holds a `MetaIslandDefinitionId` - a reference to an
authored asset - and a modded island has none, so the entries are heading, text and (later) image
blocks only.

## A wagon unloader hands over a whole package - resolved

A cargo belt used to accept loose shapes and pack them itself. That was the only way anything
could put cargo on a belt without a packager, and it cost two things:

- **Any space belt or pipe could feed a cargo belt.** The belt cannot tell one sender from
  another: `PreAcceptHook` is `Func<IBeltItem, bool>` and sees only the item, and a wagon
  unloader's output connector is the same `SpaceBeltOutputConnector` class an ordinary belt
  presents. There is no type-level rule that admits one and refuses the other.
- **A wagon emptied instantly.** `TrainCargoToBeltFillingContainer.Update` is
  `while (Peek(...) && NextLane.CanAcceptItem(...)) HandOverItem(...)`, which drains the entire
  package inside a single update for as long as the receiver keeps saying yes - and a belt
  packing into a filling container, rather than occupying belt slots, said yes to all of it.

Both go away if the **unloader** offers the package instead. The belt then takes packages only,
and one package moves per hand-over like any other belt item.

### The seam DESIGN.md previously said did not exist

This was recorded as impossible, on the grounds that `TrainCargoUnloaderSimulation<T>`,
`TrainCargoToBeltFillingContainer<T>` and `ItemLaneBundle<TLane>` are all generic and MonoMod
will not hook a method on a generic type. That is true and still true. The mistake was looking
only at the classes that do the work.

One level out, the classes that *build* them are not generic:

```
ShapeCargoStationSimulationFactory.Produce(IslandInstance, out TrainCargoUnloaderSimulation<ShapeId>, out ConnectableIslandSimulation)
FluidCargoStationSimulationCreator .Produce(IslandInstance, out TrainCargoUnloaderSimulation<FluidId>, out ConnectableIslandSimulation)
```

A non-generic method on a non-generic type, handing back the unloader it just made. Detouring it
lets the mod construct the unloader with a converter of its own -
`CargoUnloadConverter<TItem> : ICargoToBeltItemConverter<TItem>`.

`GetMethod("Produce")` is ambiguous - there are three overloads, loader, unloader and
transferrer, differing only in the `out` parameter - so the lookup matches on that parameter
type.

### Why a converter and not a per-tick pump

The alternative was a pass over every unloader each tick, taking packages out of
`_FillingContainers` and pushing them downstream. It would have been simpler to write and is
wrong: **simulation does not run on the main thread**, so that pass would be writing another
simulation's state from whichever pool thread happened to run it, racing the unloader's own
`Update` over the same package.

Replacing the converter puts every decision inside the unloader's own `Update`, on the
unloader's own thread. There is no shared state and nothing to synchronise.

### All outputs, not any

`Peek` is called once per sender and is handed only the package - never the sender - so a
station with a cargo belt on one output and an ordinary belt on another has to give both the
same answer. It offers a package only when **every** connected output would accept one, and
falls back to loose otherwise. An ordinary belt handed a package would carry it to something
that cannot read it, which is the failure `CargoHandover` exists to prevent in the other
direction.

The test is `CargoHandover.Allows`, so the belt, the store, the unpackager and a station's
loader all count as package-takers, and the answer is cached against the observed `NextLane`
references because `Peek` runs inside that `while` loop.

### The intake stays, unused

`CargoIntake` is still built, still updated and still part of `CargoBeltSimulationState`.
Nothing fills it any more, and it is kept for two reasons: its states are fields of the saved
blob, and changing that blob's shape has broken saves before; and a save written by an earlier
version can hold a part-filled intake, which the drain still finishes onto the belt.

## Junctions: one in, up to three out

A cargo line can now branch. Four pieces, matching vanilla's own splitter family:

| Definition | Outputs | Classification it is drawn as |
| --- | --- | --- |
| `CargoBelt_LeftFwdSplitter` | North, East | `LeftForwardSplitter` |
| `CargoBelt_RightFwdSplitter` | South, East | `RightForwardSplitter` |
| `CargoBelt_YSplitter` | North, South | `LeftRightSplitter` |
| `CargoBelt_TripleSplitter` | North, East, South | `TripleSplitter` |

Three is the ceiling, because the fourth side is the input.

### Almost none of it is new

`SpaceSplitterSimulation` is the game's own space belt splitter: non-generic, public
constructor, and it already distributes with `RoundRobinDistributionBehaviour` - which is exactly
"round-robin the content on each level between the downstream routes". `CargoSplitterSimulation`
subclasses it, the way the packagers re-host `TrainBeltToCargoFillingContainer`, and changes two
things, neither of them about splitting:

- **The outputs refuse loose items.** A vanilla splitter's lanes take anything, and
  `BeltPathLane.PreAcceptHook` is public, so each output lane gets the belt's own
  `IsCargoPackage`. `SplittingItemDistributor.CanAcceptItem` answers by asking the behaviour to
  find a lane that will take the item, so refusing on the lanes refuses at the junction.
- **The outputs are guarded.** Vanilla's provider bundle has no `CargoHandover` around it, so a
  splitter output pointed at an ordinary belt would hand it a package. Each output bundle is
  wrapped, and `CargoHandover.Allows` gained `SplittingItemDistributor` so a cargo belt will feed
  a junction in the first place.

`IItemBundleSimulation` is restated in the base list purely so the guarded
`GetItemProviderBundle` can be an explicit implementation - C# only allows one for an interface
the type itself names.

### The state is vanilla's, deliberately

`SpaceSplitterSimulationState` is reused unchanged, under vanilla's own
`SyncableIdentifier("SpaceSplitterState")`. `PolymorphicSerializer` registers from a *set* of
types, so one type is registered once however many islands use it, and reusing it means reusing
a blob format that already handles its own version migrations rather than inventing one.

### Speed, and why `CargoBeltSpeed` changed shape

`SpaceSplitterConfiguration` takes the concrete `BeltSpeed` class, not `IBeltSpeed`. Handing it a
plain `BeltSpeed` would have frozen every splitter at whatever the speed was when it was built,
silently cutting junctions off from belt-speed research while the belts either side of them sped
up. `BeltSpeed` implements `IBeltSpeed` **explicitly**, so `CargoBeltSpeed` now derives from it
and re-implements the interface: everything that reads a speed holds an `IBeltSpeed` and reaches
the override.

### Belt-tagged only

Unlike the belts, a junction carries one connector type. It only ever meets a cargo belt or a
cargo machine, and all of those carry a belt connector, so the second tag would buy nothing -
and it would cost the two-receiver-bundle arrangement `CargoBeltSimulation` needs to make dual
tags work at all.

### Geometry

`PathNodeClassification` already names all four, and `PlatformPathDrawingClassifier` derives the
classification from the connectors, so a junction needs no classification logic - only a mesh for
the one it is given.

`cargo_junction` in Tools/generate_meshes.py builds each from straight arms meeting at the chunk
centre: the inbound arm from the West edge, then one arm out to each named edge. Not swept arcs -
a junction is where a line stops being a smooth run, vanilla's own splitters read as a boxy node,
and arcs radiating from one point would intersect rather than blend. The corner pieces get away
with arcs only because there are two of them sharing a tangent.

The arms overlap in one channel-width cube at the middle, which reads as the junction box. Each
arm is its own closed volume, so the union stays closed and outward-wound - which is what the
generator's own checks confirm.

### Mergers are the other half, and leaving them out broke the splitters

The first version shipped splitters alone and junctions did not work: they placed, but drew
nothing and passed nothing, and the line feeding them backed up.

Two separate causes, and the second is the interesting one.

**The model.** `CargoAppearance.BeltIds` is the list the track drawer walks, and the junctions
were not in it. Registering the island and generating its mesh is not enough - a `pathTrack`
island has `ChunkPlatformDrawingContext.DrawNothing()` and no platform frame to fall back on, so
one left out of that list places, connects and simulates while drawing nothing at all. Worth
remembering because it fails silently: the two `Log.Warning` paths in `AddTrack` never run,
because the id never reaches the loop.

**The flow.** Pulling a new run *out of* an existing one is a split; dragging a new run *into*
one is a **merge**. With only splitters registered, `MatchingDefinitionFinder` has no family
member whose connectors fit a merge, so it settles on a splitter - whose outputs sit exactly
where the inputs are needed. Nothing connects, and the upstream line backs up at the node it
just made.

So the family is eight pieces, not four. `SpaceMergerSimulation` is vanilla's own and is easier
to make cargo-only than the splitter was: its inputs are real `FastBeltPathLane`s, so they take
a `PreAcceptHook` directly rather than through a distributor. Its single output bundle is
wrapped in the same guard.

### One mesh per set of arms, not per piece

Flow direction is not geometry. A left-forward splitter (in West, out North and East) and a
left-forward merger (in West and North, out East) occupy the same three arms, so they share a
mesh. Only the Y pair differs - a Y splitter reaches West, North, South; a Y merger reaches
North, South, East - so there are five junction meshes for eight pieces.

### Placement

All eight are appended to the family handed to `MatchingDefinitionFinder`, alongside forward and
the two corners. Nothing in the mod has to know what a branch is: vanilla's placer picks whichever
family member matches the connections a node ends up with, which is the same reason a turning drag
picks a corner. A missing junction definition is a warning rather than a failure - the run still
lays and turns, it just will not branch.

## A wrapped bundle must still answer as itself - resolved

Junctions looked as though they refused cargo. They did not. **No cargo belt could ever be
disconnected**, and the symptom only became obvious once junctions existed, because a junction is
placed over belt that is already there.

`ItemOutputChunkConnector` identifies a connection by object:

```csharp
public bool TryDisconnect(ISimulationConnector other)
{
    if (ProviderBundle.NextBundle != other.ReceiverBundle) { return false; }
    ProviderBundle.NextBundle = null;
    return true;
}
```

`CargoHandover.GuardedProviderBundle` wraps whatever is assigned so every hand-over is checked -
that is what stops cargo being given to an ordinary belt. Its getter returned the **wrapper**, so
that comparison never matched, `TryDisconnect` always returned false, and `NextBundle` stayed set
to a guard around a bundle whose island no longer existed.

`TryConnect` opens with `if (ProviderBundle.NextBundle != null) { return false; }`, so the
replacement could never connect either. Delete a cargo belt and put another in its place, and
nothing was handed to it again for the rest of the session.

The fix is to remember what was assigned and return that, while still handing the wrapper to the
lanes. The guard stays invisible to the simulation and visible to nothing else.

**The general rule this is an instance of:** a wrapper placed on a property the game reads back
has to be transparent to *identity*, not just to behaviour. Anything the game compares by
reference - and the connector layer compares bundles by reference throughout - will silently stop
matching. It fails as "this one thing never works again", which is far harder to read than a
throw.

It also explains the earlier confusion. Splitters appeared to work occasionally: those were the
ones placed where no cargo belt had been, so there was no stale connection to block them.

## A junction holds what a belt holds

Vanilla sizes its path junctions for loose shapes, and those sizes are much larger than a cargo
belt's four slots:

| Lane | Vanilla slots | Named in |
| --- | --- | --- |
| Splitter output | 16 | `PathSplitterSimulation.NumItemsPerLane` |
| Merger input | 12 | `SpaceMergerSimulationState.NumItemsPerInputLane` |
| Cargo belt lane | 4 | `CargoLanes.SlotsPerLane` |

Left alone a junction quietly buffers several belts' worth of freight. That is a balance hole,
and it reads badly too - cargo appears to vanish into a junction and trickle out of it.

Both counts are baked into vanilla's own state constructors, so there is nothing to configure.
`JunctionCapacity.Shrink` resizes the states after the base constructor has built them, which
works because **both lanes read their length from the state every time rather than caching it**:
`BeltPathLane.Length_S` is `SlotLength_S * State.Slots.Count`, and `FastBeltPathLaneState.Length_S`
is `ItemCapacity * ItemSpacing`. The two states differ in shape - one holds a list of slots, the
other a capacity field - so there are two overloads.

Each also needs its "the state moved under you" flag set, or the lane measures its first item
against the length it was built with: `HasBeenModifiedExternally` for the splitter's, and `Clear()`
for the merger's, which re-derives `FirstItemDistance_S` from the new capacity.

**A junction already in a save keeps the size it was built with.** Both states write their own
capacity into the blob and rebuild from it on load - `BeltPathLaneState.Deserialize` clears
`Slots` and reads the count back - so deserialisation overwrites this. Only junctions placed
after the change are four. That is also what makes the change save-safe rather than a blob
format break.

## What a cargo belt is actually worth

The wiki said "4x faster than a Space Belt". That was the slots-per-lane number wearing a
throughput hat. The real figure, derived rather than guessed:

| | Space belt | Cargo belt |
| --- | --- | --- |
| Carrying lanes per chunk | 4 lanes x 3 layers = **12** | 1 lane x 3 layers = **3** (`CargoLanes.Travel`) |
| Speed | R | R / 5 (`CargoBeltSpeed.Divisor`) |
| Payload per slot | 1 shape | 360 (`MetaTrainSimulationConfiguration.ShapePackageSize`) |
| Shapes past a point | 12R | 3 x (R/5) x 360 = **216R** |

So **about 18x** for shapes. Throughput is `speed / LaneConstants.ItemSpacing` on both, and
`SlotsPerLane` does not enter it - the shorter lane changes buffering and latency only.

Buffering per chunk is a different ratio: 12 x 16 = 192 shapes against 3 x 4 x 360 = 4,320, so
**22.5x**.

**Fluid is not the same number.** `FluidPackageSize` is 60, not 360, so a fluid cargo belt is
3 x (R/5) x 60 = 36R against a space pipe's 12R - about **3x**. Worth knowing before anyone
repeats the shape figure for fluid.

### Wagon-capacity research does not change any of this

Several comments in this repo claimed a package grows with wagon-capacity research. They were
wrong, and are corrected. The buff is declared on the *container*:

```csharp
[BuffInteger("_MaxPackagesPerContainer")]
private ResearchSpeedId TrainWagonCapacityResearch;
```

and fullness is `package.Amount == capacityProvider.PackageSize` - `ShapePackageSize`, which
nothing buffs. Research changes how many packages a wagon's container holds, not how many shapes
a package holds. The multiplier above is therefore constant for the whole game.

Reading `PackageSize` from the provider rather than hardcoding 360 is still right - it keeps a
packager agreeing with whatever a station is making - but it is a constant in practice, and the
packager's gauge maximum never moves.

## Cargo climbing a lift

Two separate faults, and only the first is obvious.

**Nothing was drawn on a lift at all.** `CargoBeltSimulationRenderer` found its output connector
by index:

```csharp
int outputIndex = entity.Simulation.NumItemReceiverBundles;   // 2 for a cargo belt
```

which holds only while an island declares as many input connectors as the simulation claims
bundles. `CargoBeltSimulation` claims two - a belt tag and a pipe tag at one pivot - but a lift
declares a single belt input, so `ConnectableIslandSimulation` adds one input and the output
lands at index 1 while the renderer asked for 2. The lookup failed and it returned before
drawing. Cargo crossed the lift correctly and simply appeared on the far side, which reads as
teleporting rather than as a missing renderer.

Both ends are now found by asking each connector whether it is an `IItemInputChunkConnector` or
an `IItemOutputChunkConnector`, which cannot drift as pieces are added.

**Then the path itself.** `CargoPathArm` had the climb where it could not survive a corner:

- A **Forward** lift was already right. It is not a turn, so the position is a straight lerp
  between two pivots that differ in height, and the lerp climbs.
- A **Left or Right** lift was not. `OnCurve` adds two horizontal tile vectors to a fixed pivot,
  so every item on the arc takes that pivot's height - the midpoint of the two neighbours. A
  container jumped to half way up, slid round flat, and jumped again.
- A **Backward** lift could not be drawn by that method at all. Its input and output face the
  same way, so the arc's two terms collapse onto one line and the container would slide in and
  back out through the wall it entered by.

So horizontal shape and vertical climb are now computed separately: the curve runs flat at the
entry height and the climb is applied afterwards in proportion to progress. Flat track has a
climb of zero and is untouched.

Backward gets its own hairpin, traced with the same `HairpinRadius` and `HairpinLeg` that
`lift_path` uses in Tools/generate_meshes.py so the freight follows the deck it is riding on.
**Those two numbers now exist in two languages**, which is a seam: change the mesh hairpin and
the item path has to move with it. They are named constants on both sides rather than bare
figures for that reason.

## The climb was half what the ramp was - resolved

Cargo on a lift rose at the wrong angle and appeared to leave the deck. The climb was being
measured from the wrong pair of points:

```csharp
Exit = WorldCoordinate.Lerp(centre, afterOut, 0.5f);   // centre is the *input* chunk
```

`centre` is the input pivot's chunk, which is at the input's layer, so `Exit` sits half way up.
The freight climbed ten units while its deck climbed twenty, and the gap widened the further
along the ramp it got.

The climb is now taken between the two pivots' own chunks - `outPivot.Position.ToCenter_W().z`
less the input's - so it is the full layer, and a flat piece measures zero and is untouched.

## Containers lie along the ramp

A crate drawn upright on a forty-five degree ramp reads as hovering, so the yaw is followed by a
pitch about the lateral axis.

The angle is **measured off the path** rather than worked out per shape: sample a short step
either side of the container and take the angle between the horizontal distance covered and the
height gained. That one calculation is right for a straight ramp, a turning one and the hairpin
alike - a quarter circle covers about 15.7 units while climbing 20, so it is steeper than the
straight ramp's 45 degrees, and neither figure has to be written down.

It also cannot drift from where the container actually is, because the same `Point` that draws
it is the one being sampled.

The rotation is applied in world space rather than in the container's own frame, which would
depend on `CargoPackageMeshes.LongAxisIsX` - whether the crate mesh was authored long-ways along
X or Z.

The axis is **the sampled heading crossed with world up**, not the arm's lateral vector. Both
name the same line; only the cross product fixes which way along it points, and the sign is the
whole difference between nose-up and nose-down. The lateral vector is a tile direction rotated
clockwise in the *game's* frame, and `WorldVector`'s cast to Unity is `(x, z, -y)`, so East
rotated clockwise is `South = (0, 1, 0)`, which reaches Unity as `-Z` - the opposite of the `+Z`
that tilts an East-bound crate nose-up. Deriving the axis from the two points already sampled
for the slope cannot disagree with them.

## Both lift fixes reached only junctions - resolved

The two sections above were written, shipped, and changed nothing on a lift, because
`CargoBeltSimulationRenderer` still held **its own copy** of the placement maths. `CargoPathArm`
was extracted for junctions and only the junction renderers were moved onto it; a lift is a
`CargoBeltSimulation`, so it kept drawing through the copy, at half the deck's gradient and with
no pitch at all.

The renderer now builds one `CargoPathArm` and calls `At`. There is a single copy of the maths
again, which is what makes the two fixes above true of lifts as well.

The deck mesh was right the whole time: `lift_path` carries the full `layers * LAYER_RISE` across
a twenty-unit run, so a one-layer lift is exactly forty-five degrees and a two-layer one about
sixty-three. The item path now matches it rather than being measured separately.

## See-through parts: two winding bugs the whole-mesh check could not see

Reported as "the hopper does not render in game - I can kind of see it clipping a bit, but
it's mostly see-through". That is the exact signature of backface culling, and there were two
separate causes.

**The generator's check was whole-mesh.** `signed_volume` is a sum over every triangle, so one
part wound inside out inside a mesh that is positive overall passes silently. It now runs per
*connected component* - triangles grouped by shared vertex position, which keeps merely
overlapping parts separate - and that is the only reason either bug was found. Running it
against the shipped meshes turned up **36 inverted parts across 28 of the 33 files**.

**`sweep_box` inverted on every mirrored call.** `_ring` walks its corners a0-low, a1-low,
a1-high, a0-high; with `a0` above `a1` that circuit runs the other way and every quad in the
sweep is wound inward. `channel` builds its walls and rails with `for side in (-1, 1)`, so
**one side of every cargo belt, corner, junction and lift was see-through** - and the two
sides summed to a positive volume, which is why nothing complained. Swapping the bounds fixes
the mirrored calls.

That was still not enough. A ramp's sleepers climb 2.5 units across a box 0.26 tall, so the
cross-section is sheared far past its own height and the circuit reverses again. Chasing cases
is how the first two were missed, so `sweep_box` now asks the finished solid which way it is
facing and flips itself - a closed surface's signed volume is origin-independent, so that is
exact rather than a heuristic, and it is the same flip `cargo_track` and `cargo_junction`
already applied to themselves.

**The hopper was a single-skinned cone.** Open at the top by design, which is what a funnel
looks like on paper and is invisible here: the camera looks down into the mouth, the inside of
a one-sided cone is backfaces, and you see straight through the machine to the platform behind
it - with a sliver of the outer skin still catching the light at the edges, which is the
"clipping a bit". It is now a solid with a hollow in it: outer skin, a rim across the top, and
an inner skin facing back up out of the cavity. `expected_open` is empty as a result - nothing
in the set is an open shell any more.

Cost: 38 triangles on each packager. The whole set is still 33 closed, outward-wound meshes.

## Which end is the packed one, on a machine with pipes at both ends

The fluid pair read as a length of plumbing: a pipe at each end, no way to tell input from
output at a glance. The first version leaned on the tank to carry it - lying across the flow on
the packager, standing upright on the unpackager - and that fails in practice, because a tank
is not something you compare across two buildings twenty chunks apart.

**A pipe at each end is what the connectors are, not what the machine does.** Both fluid ends
are declared `inputIsPipe: true, outputIsPipe: true` because the fluid line is pipe-tagged from
end to end, but what *travels* on the two sides still differs: loose fluid one way, sealed
containers the other. The shape pair already says that with geometry - an open hopper for loose
stuff, a sealed `duct` for packages.

So the fluid machines use the same duct. The packager takes a pipe in from the West and a duct
out to the East; the unpackager is the mirror. Which end is the packed one is then the same
question on all four machines - **the duct is the packed side** - rather than a different tell
per pair. The pipe end is now a statement rather than a default.

## Store art is rendered, not captured

Screenshots rot. The Steam preview and every promo image showed grey machines with a
see-through hopper for two days after both were fixed, because the captures predated the art
work and nobody retakes five images per change.

`Tools/render_meshes.py` reads the same `.obj` files the game loads and colours them **by the
role sentinels the generator writes** - every vertex already carries a per-role marker UV, so
the renderer knows which triangles are `hull`, which are `frame`, which are `warn`, and paints
them the way `CargoPalette` does at runtime. Regenerate the meshes, re-run
`Steam/make-promo.py`, and the art is current by construction.

The figures in `ROLE_COLOURS` are **sampled from the real captures** rather than picked: the
platform-edge orange is `rgb(189, 111, 1)` out of `cargo-line.png` and the machine body grey is
`rgb(112, 100, 97)` out of `cargo-stores.png`. So is the lighting - a face turned away from the
light sits at luma 0.39 against 0.53 for one facing it, a ratio near 1.35, so the model is
heavy ambient and a light key. The first pass used a hard key at 0.30 ambient and rendered the
machines nearly black: accurate to the palette, nothing like the game.

It is a likeness, not a frame grab. No metal, noise or scratch passes, and the accent slots
resolve against a live palette that only exists at runtime. **A fresh in-game capture is still
the better store image** - this is what to ship until somebody takes one.

Two framing rules learned by looking: one subject per image, because the auto-fit scales a row
of three down until a junction is a ribbon forty pixels tall; and a `bias` that lifts the
subject clear of the caption block, because a scrim across the bottom third otherwise lands on
the thing being captioned.

## Open questions

- **Do buildings accept containers?** Deliberately dodged: an unpackager sits in front of
  ordinary machinery. Train stations were the case worth solving properly, and they are solved.
- **Balance.** Settled. A cargo belt carries `PackageSize` shapes per slot against a shape
  belt's one, which works out at 18x the throughput for shapes and 3x for fluid; both lines are
  gated behind the Cargo Machines and Cargo Stores research nodes at 4.8k each.
- **Publishing.** The only mod repo here with neither a `README.md` nor a screenshots folder,
  and it is the largest. Nothing blocks a release but nothing is prepared for one either.
