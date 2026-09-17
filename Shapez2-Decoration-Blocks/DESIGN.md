# Decoration blocks - design notes

What was read out of the decompiled assemblies rather than assumed, so none of it has to be
re-derived. Nothing here has been run in game yet; see **Status**.

## The idea

21 voxel blocks placeable on the machine layer. They connect to nothing, process nothing
and appear in no statistic. They exist so the empty half of a platform can be made to look like
something - a wall, a path, a pattern in coloured blocks - and they are textured with real
16x16 pixel art rather than tinted with the game's palette.

## Status: loads, registers, draws the wrong colour, 2026-09-14

First run in game. What works: the mod loads, all 21 blocks register, the research node appears
once (not 21 times), the toolbar category is created, the atlas packs correctly, and the toolbar
icons are right - which between them prove the atlas, the UV maths, point filtering and the
icon-from-atlas trick.

**Placed blocks draw in the wrong colour**, and the material dump says why. See below; the fix
is in but untested.

`Resources/Textures` holds the real 16x16 textures, imported from an installed Minecraft
client jar by `tools_import_minecraft.py`. `tools_make_placeholders.py` writes generated flat
colours instead, for testing without a jar. Neither output is shippable as-is - see **Naming**.

**Grass is not a straight copy.** `grass_block_top.png` is stored **greyscale** in the jar and
tinted per biome at draw time, and the green fringe on the side face lives in a separate
greyscale-plus-alpha `grass_block_side_overlay.png`. Extracted as-is, the top of a grass block
is a pale grey square. The importer multiplies both by the plains tint (`#79C05A`) and
composites the overlay onto the side. The tint list is explicit rather than detected, because
`stone.png` is greyscale too and is meant to be.

### The building shader has no albedo map

This is the finding of the first run, and it was not guessable from the assemblies.

`db.material` reports the building material's shader as **`Shader Graphs/UberBuildingShader`**,
and its writable texture properties are:

```
_MaterialLUT                 MaterialLUT 256x256
_PackedTex                   UberShaderPacked 2048x2048
_MetalBrushedMainTex         UberShaderMetalBrushedCombinedRoughness 2048x2048
_MetalCoatingMainTex         SmoothNoise 2048x2048
_MetalNoiseAlbedoMaskTex / _MetalNoiseNormalTex / _PlayingfieldScratchesTex
_SampleTexture2D_<guid>_Texture_1_Texture2D   x11, all noise, normal and scratch maps
```

There is **no `_BaseMap` and no `_MainTex`**. Colour is a lookup: UV0 points at a texel of the
256x256 `_MaterialLUT`, and everything else in the shader is procedural surface treatment
layered on top. That is the same mechanism `load-models-and-icons.md` describes as "colour comes
from a texture atlas, not from your material" - the atlas *is* the LUT, and it is a material
palette rather than an albedo texture.

Feeding the atlas into `_MaterialLUT` does map the right pixels onto the right faces - but the
metal, noise and scratch passes then run over the result, and pixel art does not survive that.
`_PackedTex` is no better. Both were tried in game.

### So the blocks do not use the game's shader at all

`db.shaders` dumps every shader the build actually loaded, and **`Universal Render Pipeline/Lit`
is in it**. That was the one thing that could not be assumed: an unreferenced URP shader is
stripped from a player build, and `Shader.Find` then returns null rather than failing loudly, so
"just use URP Lit" is a coin flip until something has looked.

A fresh URP Lit material takes the atlas in `_BaseMap` and renders it as a texture, lit and
shadowed, with nothing layered on top. Smoothness and metallic are forced to 0 - URP Lit
defaults to 0.5 smoothness, which puts a wet sheen on cobblestone.

> **`Graphics.RenderMeshInstanced` refuses a material that has not enabled instancing, and
> refuses it by drawing nothing.** `InstancedMeshRenderer.Flush` is `Graphics.RenderMeshInstanced`,
> so every material submitted through `Renderers.Buildings` needs `material.enableInstancing = true`.
> A material *cloned* from one of the game's inherits the flag, which is why the earlier
> clone-the-theme approach drew at all; one built from a bare `Shader.Find` does not, and would
> have looked exactly like the mesh failing to load.

Glass uses **alpha clip**, not alpha blend. Blending in URP means setting `_Surface`, `_Blend`,
`_SrcBlend`, `_DstBlend`, `_ZWrite`, the render queue and two keywords in agreement, and then
getting sort order right; Minecraft's own glass is a cutout - fully transparent holes in an
opaque frame - so clipping is both simpler and more faithful. URP reads the **keyword**, so
`_AlphaClip` alone does nothing: `EnableKeyword("_ALPHATEST_ON")` and the AlphaTest render queue
are what actually switch it on.

Cloning the theme material is kept as the fallback for a build where URP Lit has been stripped.
It looks wrong, but it draws.

**What went wrong the first time is worth keeping.** The heuristic fell through every name it
knew and took its fallback - "the first texture property" - which was **`unity_Lightmaps`**.
`GetTexturePropertyNames` returns Unity's own per-renderer built-ins (`unity_Lightmaps`,
`unity_LightmapsInd`, `unity_ShadowMasks`) at the head of the list, ahead of anything the shader
author declared. So the atlas went into the lightmap slot, the LUT was left as vanilla's, and
the blocks drew in whatever colour vanilla's palette happened to hold at their UVs. `Writable()`
now strips `unity_*`, and the fallback is gone entirely: no recognised property means no write
and an error in the log, because a wrong slot is worse than no texture.

### What to check next

1. **Whether URP Lit's lighting suits them.** `db.set smoothness <0..1>`, `db.set metallic` and
   `db.set cutoff` retune and rebuild live. Shapez's own lighting is fairly flat, so blocks may
   want a little smoothness rather than none.
2. **Glass.** `db.set cutoff` moves the alpha threshold if the frame comes out too thin or too
   fat.
3. **Whether URP Lit looks out of place beside vanilla machines.** It will not share the Uber
   shader's surface treatment, which is the point, but it may read as flatter than its
   neighbours.

Two smaller ones: that a block is **clickable** (the collider is asserted below, not observed),
and that **Area placement** drags out a rectangle the way foundations do.

## The blocks are ordinary buildings, and the game already has the concept

`BuildingDefinitionGroupFactory.CreateFromMetadata(..., MetaDecorationBuildingDefinitions)`
builds vanilla's own decorations, and `DecorationMetaBuildingDefinition` is the shape of one:
every connector list is `Array.Empty`, and the group is constructed with `autoConnect: false`,
`autoRotateToFitStructures: false`, `allowNonForcingReplacementByOtherBuildings: true`,
`shouldSkipReplacementIOChecks: true` and a null structure overview.
`DecorationBuildingModuleDataProvider` returns an empty side panel.

`DecorationRegistrar` sets those same flags. They are copied, not chosen.

### A building with no simulation is legal

`Simulator.OfferBuilding` ends:

```csharp
if (!SpecializedBuildingTenantSystemsByType.TryGetValue(id, out var value))
{
    return;
}
```

So a definition no system claims is ignored - no error, no cost. That is exactly what vanilla
decorations are: `DecorationBuildingsPlacersCreator` registers placers for
`Buildings.AllDecorations` and nothing registers a simulation for them.

**Shifter is the part that insists.** `AtomicBuildingExtender.Build` does

```csharp
RewirerChainLink rewirer = LazySimulationExtender.ContinueAfter(rewirerChainLink);
RewirerChainLink rewirerChainLink2 = LazyPredictionExtender?.ContinueAfter(rewirerChainLink);
```

- the prediction branch is null-checked and the simulation branch is not. Omitting
`WithSimulation` therefore does not give a decoration, it gives a `NullReferenceException` out
of the mod constructor, which `ModLoader` does not contain.

`InertSimulation` is the answer: a class implementing `ISimulation` and nothing else.
`ConnectableBuildingSimulation`'s constructor finds no `IItemSimulation`, no fluid simulation
and no signal simulation to bind and builds a connector list of length zero, and
`AtomicBuildingSimulationSystem` is not an `IUpdateableSimulationSystem`, so nothing ticks. One
shared instance serves every block on the map - it has no fields, and the system only disposes
a simulation that is `IDisposable`.

The **stateless** `WithSimulation<TSimulation>` overload is used, not the stateful one the
chain's interfaces expose. The stateful one drags in a serialised `ISimulationState` class and
a `BuffablesExtender` for a configuration, neither of which a cube has any use for. It is
`public` on the concrete `AtomicBuildingExtender` but no interface in the chain returns it, so
reaching it means casting the chain back to the concrete class - which is legal because every
stage returns the same object.

### One group per block is forced

`BuildingGroupBuilder.BuildAndRegister` ends in

```csharp
gameBuildings._All.Add(buildingDefinitionGroup);
gameBuildings._VariantsById.Add(buildingDefinitionGroup.Id, buildingDefinitionGroup);
```

`Dictionary.Add`, not an indexer. Running the chain twice against one group id kills the
session on a duplicate key, and the chain calls `BuildAndRegister` once per building. So 21
blocks are 21 groups and 21 toolbar entries. Vanilla does the same - one group per
`MetaDecorationBuildingDefinition`, each with a single internal variant - so this is not a
workaround.

Sharing one group *is* reachable, by passing a get-or-create `IBuildingGroupBuilder` that
returns the already-registered group on later calls. It was not done, because the toolbar entry
is added per building too, so the 21 entries would remain and only the variant cycling
would change. The clutter is solved with a category instead.

### One research node for all 21

`UnlockedWithNewSideUpgrade` registers its node **once per building group**, and
`SideUpgradeBuilder.Build` appends to `_SideUpgrades`, `_ShopItems`, `_AllUpgrades` and
`_UpgradesById` every call, so 21 blocks sharing one builder would be 21 identical shop
entries. `CustomSideUpgradeSelector` is not the escape either - its `Select` is a call to
`Build`.

`DecorationUnlock` is a get-or-create `ISideUpgradeSelector`, the same pattern as
`CargoUnlock` in Train Cargo Tools. The first block extended in a scenario builds the node; the
other twenty find it by id and
`UnlockBuildingWithExistingSideUpgradeResearchProgressionExtender` appends their group to its
`Rewards`. The lookup keys on the `ResearchProgression` it is handed, never on a cached field:
`ExtendResearch` runs once per scenario load with a fresh progression.

Cost is `new ResearchPointCurrency(48)`, which the research screen renders as **4.8k** -
`Format(this ResearchPointCurrency)` is `FormatIntegerMax4Digits(Amount * 100)`.

The preview image is borrowed off whatever side upgrade the scenario already has, because
`HUDResearchSideUpgradeDisplay.RebuildView` calls `GameData.GetImage(upgrade.ImageId)`
unconditionally and `GetImage` throws on `GameImageId.Empty`, taking the research screen with
it.

## Why the blocks are drawn by a renderer and not by a mesh

This is the part that decided the architecture.

A building's world visual comes from `BuildingDrawData.MainMeshPerLayer`.
`IslandChunkStaticBuildingsDrawer` combines every building in a chunk into one mesh and submits
it with **one shared material**:

```csharp
IMaterialReference material = options.Theme.BaseResources.BuildingMaterial[lod.BuildingMaterialLOD];
result.Draw(options, material, RenderCategory.BuildingsStatic, culling, lod.Shadows, lod.Shadows);
```

There is no per-definition material anywhere on that path, and colour on the shared one is a
UV0 lookup into an atlas that is authored Unity data - so a building **cannot carry its own
texture through the static pipeline at all**. The best it can do is point its UVs at a spot in
the game's atlas and come out one flat shapez colour per face.

So `MainMeshPerLayer` is three `LODEmptyMesh`es and the cube is drawn separately.

### `StatelessBuildingSimulationRenderer` already is the sub-drawer

The obvious way to draw it yourself is an `IMapSubDrawer` with a per-chunk cache. That was
written and then deleted, because `StatelessBuildingSimulationRenderer<TSimulation, TDrawData>`
already does all of it:

- caches entities in a `SimulationDataCache` keyed by `GlobalChunkCoordinate`;
- registers and unregisters them with the simulation, so there is no map event to subscribe to;
- walks only `cullResult.Chunks`, applies `ShouldRenderPlatformContentsAtLayer`, and
  frustum-tests each entity's `Bounds` before calling `OnDrawDynamic`;
- hands the callback `entity.Transform`, `entity.Definition` and `entity.DrawData`.

And it needs **no registration**: `CreateSimulationRenderers` builds every concrete
`IBuildingSimulationRenderer` found by `GetAllLoadableTypes()`, which reaches a mod assembly as
readily as the game's own, through a container with `IMapModel` bound. There is no hook to
install and nothing to unwind.

Renderers are keyed by **simulation type**, so one `BlockRenderer` serves all 21 blocks -
they all share `InertSimulation`. Each block's own mesh arrives as `entity.DrawData`, which is
the `IBuildingCustomDrawData` attached to its definition.

Two overrides matter:

- `ShouldDraw(LODRenderConfig)` defaults to `lod.ShouldDrawDynamicBuildingSimulations`, which is
  right for items moving through a machine - they stop being worth drawing long before the
  machine does. A block *is* the building, so it is overridden to `lod.ShouldRenderBuildings`.
  Without that, a decorated platform dissolves at a distance where the factory around it does
  not.
- `ShouldDraw` and `OnDrawDynamic` are `protected` in the shipped source and **public after the
  publicizer**, so they must be overridden as `public override`.

### What the other draw-data slots are still used for

Only the main mesh is empty. Everything else is the real cube, and each is read by something
different:

| Slot | Read by | Effect if empty |
| --- | --- | --- |
| `IsolatedBlueprintMesh` | `SmartBuildingBlueprintRenderer`, `BuildingPlacementAnimationPlayer` | blueprints and the placement animation show nothing |
| `CombinedBlueprintMesh` | `HUDBuildingMassSelection` | mass selection highlights nothing |
| `Colliders` | `ScreenUtils`, `DebugViewColliders` | the block cannot be clicked or removed |
| `HasCustomOverviewMesh` | `IslandChunkStaticBuildingsDrawer` | falls back to `OverviewModeFallbackPlaneMesh` per tile, which is what every other building does - so `false` is correct |
| `PreviewMesh` | nothing in the decompiled game reads it | - |

`SimulationRendererDrawsMainMesh: true` is set for honesty. With an empty main mesh it changes
nothing observable; `StaticBuildingMeshBuilder.BuildMainMesh` uses it to skip the base mesh at
LOD <= 2.

## Redstone

Five components in a toolbar tab of their own: torch, lever, button, dust, repeater. The block
of redstone was already in the decoration catalog and is now also a permanent power source, so it
cost nothing new.

### It is not a shapez simulation, and that is the point

Shapez simulations are lanes and connectors - a machine takes items in at one pivot and hands
them out at another. Redstone is a **field over a grid**, recomputed from its sources, where a
component's output depends on neighbours it has no connector to. So the components are registered
as ordinary buildings with an inert simulation, exactly like the blocks, and `RedstoneWorld` owns
the behaviour.

That also settles the threading question. `Simulator.StartAsynchronousUpdate` returns a `Task`,
so anything registered as a simulation runs on **pool threads**. This is driven from
`ITickRewirer` instead, which Shifter postfixes onto `GameSessionOrchestrator.Tick` - the main
thread, where reading the map and touching Unity objects is safe.

One redstone tick is 0.1s, accumulated from frame delta rather than counted in frames, and at
most four ticks are caught up in one frame so a hitch does not become a burst of clock edges.

### Recomputed, not propagated

Each tick solves the whole field breadth-first from the sources rather than propagating changes
from whatever moved. Minecraft propagates, and its famous quirks - quasi-connectivity, zero-tick
pulses, update-order dependence, BUD switches - are artefacts of *how* it propagates rather than
rules anybody designed.

So: **circuits a player would call intended behave the same; circuits that exploit the artefacts
do not.** That is a deliberate trade. Recomputing is cheap at the scale a decorated platform
reaches, cannot desynchronise, and has no ordering to get subtly wrong.

Delay is modelled explicitly instead of falling out of propagation order, because delay is the
part players actually build with:

* a **torch** reads the block power computed on the *previous* tick, which is its one-tick delay,
  and with it every clock built from an odd loop of torches;
* a **repeater** shifts its input through a four-slot queue. A queue shifted by one rather than a
  ring, because the delay is adjustable while values are in flight - lengthening it must not lose
  what is already queued, and shortening it must not skip ahead, which is how Minecraft's own
  repeater behaves.

### Where it knowingly differs from Minecraft

* **A free-standing torch is a source.** In Minecraft every torch is mounted on something and
  inverts it, so a floor torch inverts the floor. Here a torch with a block behind it inverts
  that block, and a torch with nothing behind it is a battery. This is the rule that was asked
  for, and it is the one that makes a torch usable without a lever.
* ~~**Block power is not split into strong and weak.**~~ **It is, and it has to be.** The first
  version collapsed the two, on the theory that the distinction only mattered in compact circuits.
  That was wrong, and wrong in the most basic case there is: dust weakly powers the block beside
  it, a collapsed model lets that block power the dust back, and the pair latches on with no
  source in it. The symptom was a circuit that lit once and then ignored the lever being switched
  off, the torch being taken away, and the dust's last real source being deleted.

  So `RedstoneWorld` now keeps `PoweredBlocks` (weak or strong) and `StronglyPowered` separately.
  **Dust reads only strong power**; a torch is held off by either. That is Minecraft's own rule
  and it exists precisely to break this loop - which is worth remembering the next time a rule
  looks like it is there for an edge case.
* **Nothing persists across a save and reload.** State lives in `RedstoneWorld`, not in an
  `ISimulationState`. Most of it is derived and comes back within a tick of loading - dust
  strength, torch state, block power - but three things are genuinely forgotten: a lever's
  position, a repeater's delay setting, and a button mid-press. Persisting them means a real
  `ISimulationState` per component, which is the obvious next increment.
* **No pistons.** Skipped for this pass by agreement. In Minecraft a piston *moves blocks*, and in
  shapez a block is a building, so moving one means `DeleteBuilding` plus `CreateBuilding` on a
  timer - a save mutation every tick, snapping rather than sliding, with a 12-block push chain
  being a burst of that.

### Everything directional is built along X

Rotation is about Unity's Y in 90 degree steps (`FastMatrix.ROTATION_MATRICES_3D_PRECACHED`),
rotation zero is identity, and `TileDirection.East` at rotation zero is **+X** - game space East is
`LocalVector(1, 0, 0)`, and the game-to-Unity cast is `(x, z, -y)`.

So `RedstoneWorld.Facing` is +X and `Behind` is -X, and **any mesh whose shape implies a direction
has to be authored along X too**.

The first version authored the repeater along Z. It was therefore drawn ninety degrees from the
direction it actually read and powered: dust lined up with the repeater's visible direction fed
its *side*, which is the lock input, so the repeater never turned on. The symptom was a repeater
that refused to repeat, and nothing about it looked like an orientation problem - the mesh was
right, the logic was right, and they disagreed.

The lever's handle now leans along the same axis, so its rotation means something visible. The
torch is deliberately symmetric: its rotation only matters when there is a block behind it, and
then the block is the hint.

A repeater also gets a mesh per delay setting, because the delay is read off the gap between its
two torches - the fixed one near the output, the other sliding toward the back one notch per tick.
Clicking to see the number in the side panel is not how anybody reads a repeater.

### Transparency is not relied on anywhere

Alpha clipping on a runtime-built URP Lit material **did not work in the shipped build**, and the
symptom was dust drawn as an opaque near-black square covering the block it sat on: the tint for
strength zero is nearly black by design, and without clipping the whole quad was drawn.

The likely cause is shader variant stripping - URP strips variants it believes unused, `_ALPHATEST_ON`
among them, and a stripped keyword falls back silently to a variant that renders transparent texels
rather than discarding them. Rather than chase that, the components stopped needing it:

* **Dust is geometry.** A centre pad and one arm per connection, on a flat white tile, coloured by
  the power tint. No transparent texels at all, and the shape is the same one the generated mask
  textures were drawing.
* **Boxes map to the solid part of their texture.** A torch texture is a torch on a transparent
  field; mapping a box to the whole tile puts that field on the box's faces. `BoxMesh.Box` takes a
  UV window, and the torch samples the stick's pixels and the head's pixels separately - which is
  what Minecraft's own torch model does, for the same reason.
* The repeater, button and lever base were already on fully opaque textures (`repeater`,
  `smooth_stone`) and needed nothing.

**Glass rendered solid too**, which confirmed the diagnosis from a second, independent direction.
It is now a **frame**: four uprights and eight rails, sampling the top row of its own texture.

That is not a compromise. Minecraft's glass texture is a one pixel opaque border around a
transparent middle, so a frame of twelve edge bars is precisely what the texture depicts - you see
the frame and straight through the middle, exactly as in Minecraft.

With that, **nothing in the mod relies on transparency any more**, and both renderers use the
opaque material. The translucent one is still built, and `db.material` still reports it, but only
so the diagnosis is visible.

### A wall torch is bracketed on, not standing in its tile

A mounted torch **does not power the block above it, and reaches only dust on its own layer**. That
is not a simplification - it is what stops it oscillating.

In Minecraft a wall torch shares a block space with the block it is attached to. Here it occupies a
whole tile of its own, so "the block above the torch" is a tile Minecraft has no equivalent of.
Powering it let a wall torch light the block above, that block light dust, that dust run back down a
layer - dust connects a layer up and down - and weakly power the very block the torch is bolted to.
Which switched the torch off, which unpowered the chain, which switched it back on: a flicker with
no clock in it and nothing on screen to explain where the loop was.

The same argument covers reach. A bracketed torch reaching a layer up or down would be reaching
through the block it is bolted to, so a mounted torch feeds its four same-layer neighbours and
nothing else.

A **free-standing** torch keeps both - it powers the block above it and reaches up and down - and
cannot oscillate, because with no block behind it there is nothing that can ever hold it off.

Minecraft's own answer to loops like this is torch **burnout**: a torch that toggles too fast stops
for eight ticks. That is deliberately not implemented, because it would also burn out the torch
clocks people build on purpose, and because a rule that hides a geometry mistake is worse than
fixing the geometry.

Mounting is settled at the top of the tick rather than the end of it, since both the block solve and
the dust reach branch on it and it depends only on whether a block is there.

### Mounted torches look mounted

A torch inverts the block **behind** it (`Behind`, which is -X, the opposite of `Facing`) and is a
source when there is nothing there. Rotation is what picks the block - shapez has no way to place
against a face - so rotation had to become visible: a mounted torch is drawn shoved against the
block and jutting out, a free-standing one stands upright in the middle of its tile. The renderer
picks between them from `RedstoneComponent.Mounted`, which the solve sets.

Without that the component was unusable in practice. The logic was right and there was no way to
see it.

### The four components added after the first pass

* **Redstone lamp.** The output the toolkit was missing - before it, a circuit's only visible
  effect was the colour of its own dust. A full cube that lights when anything adjacent drives it,
  and a dead end: its brightness is kept in `Lit` rather than `Power` so that nothing reads it as a
  source.

  Keeping the two apart was right and it shipped broken anyway, because
  `RedstoneComponent.IsActive` - what the renderer draws from - still answered `Power > 0`. A
  lamp's power is **always zero** by design, so a correctly solved lamp drew dark for ever. The
  lesson is narrow and worth keeping: a field deliberately excluded from the general path needs
  every reader of that path taught about it, and `IsActive` was a reader.
* **Comparator.** The other logic primitive, and the only **strength preserving** component -
  compare mode passes the back input through unless a side beats it, subtract mode takes the
  strongest side off the back. Its output is the computed number rather than a flat fifteen, which
  is the whole point of it. Sampled at the end of the tick and released at the start of the next,
  like a repeater, which is where its one-tick delay comes from. The back torch rises when it is
  subtracting, because that is how Minecraft shows the mode and the only way to read it without
  clicking.
* **Observer.** Pulses out of its back for one tick whenever the tile it faces changes. "Changes"
  is a **signature** recomputed each tick rather than an event, because the interesting changes are
  not all map events: a block placed or broken is, but dust changing strength and a lamp lighting
  are not. Hashing what is there catches all of them under one rule.

  Its pulse is one redstone tick - 0.1s, matching Minecraft - which is long enough to drive a
  circuit and far too short to *see* on dust. That mattered more than it should have, because the
  one thing that would have made it visible was the lamp, and the lamp was broken. The observer is
  also the only component whose input side cannot be deduced from its own behaviour while it is
  quiet: a player who has pointed it the wrong way sees exactly what a broken observer looks like.
  So the side panel now names the tile it is watching, and `db.redstone` prints that, its stored
  signature, its remaining pulse ticks and where its output goes.
* **The shapez wire bridge** is still outstanding, and is the one of the four that is a different
  kind of work. See below.

### The wire bridge: one building, both directions

`RedstoneConverter` is a single building carrying **a wire input at the back and a wire output at
the front**, plus redstone on all four sides like any other component. A wire signal that is not
Off broadcasts redstone 15; redstone power reaching its tile puts a true on the wire. Both run at
once.

One shared wire port could not have done it. The building would be a provider *and* a receiver on
the same network, so it would read back its own contribution - two connectors on two networks is
what makes one building able to bridge both ways.

`LogicGateNotSimulation` is the template: the smallest thing in the game that both reads and
writes a wire, with a `SignalConductorInput` handed back from `GetSignalReceiver` and a
`SignalConductorOutput` from `GetSignalProvider`, popping one signal per signal-tick in `Update`
and pushing one back. `ConnectableBuildingSimulation` binds those to the definition's
`BuildingSignalInput` and `BuildingSignalOutput` connectors automatically.

Two things had to be solved rather than copied:

* **A simulation is not told where it is.** `IFactory<T>.Produce()` takes no position, so the
  converter cannot find its own tile in `RedstoneWorld`. The traffic therefore runs the other way:
  `RedstoneWorld.ExchangeWithConverters` walks its own converters on the main thread and calls
  `map.Simulator.TryFindTileSimulation(tile, …)`, which resolves a tile *to* the simulation sitting
  on it.
* **The two halves are on different threads.** `Simulator.StartAsynchronousUpdate` returns a
  `Task`, so `Update` runs on a pool thread while the redstone tick is postfixed onto
  `GameSessionOrchestrator.Tick` on the main one. Exactly two `volatile bool`s cross between them,
  each written by one side and read by the other. Nothing else passes, and a single-word write
  needs no lock.

Its conductor state is built in the constructor rather than coming from a serialised
`ISimulationState`, which keeps it on the stateless branch of the builder chain. The only
consequence is that a signal in flight is not saved, and the network recomputes its value every
tick anyway.

### Its own simulation cost it its renderer

Giving the converter a real simulation - it has to have one, it implements `ISignalSimulation`
while `RedstoneSimulation` is a sealed inert marker - took it out of `RedstoneRenderer`'s reach,
and the building stopped being drawn once placed. Nothing was logged and nothing threw.

`SimulationsDrawer` keys every renderer on `(LocalizedSimulationType, SimulationType)` and
dispatches each simulation to the matching key. A simulation matching no key is not an error from
the drawer's side, it is simply not dispatched. The **placement preview kept working throughout**,
which is the diagnostic: `PlacementPreview` draws `RedstoneMeshes.Build` directly and never goes
near a simulation, so a mesh that ghosts correctly and then vanishes on release is a renderer
binding problem, not a mesh problem.

`RedstoneRenderer` is now `RedstoneRendererBase<TSimulation>`, abstract, with two sealed
subclasses - one per simulation type. Both filters in
`ReflectionUtils.CreateInstancesForInterfaceImplementations` (`IsAbstract`,
`ContainsGenericParameters`) skip the base, so only the two concrete ones register, and they hold
different keys so neither displaces the other.

The dust palette moved out to `RedstoneDustPalette` in the same change, because **a static field
in a generic type exists once per instantiation** - left on the base it would have built sixteen
property blocks a second time for the converter renderer, which never draws dust.

### The conflict cross, and two builder methods that look like the fix

Placing the converter beside dust drew a red cross over the connector facing it, which reads as
"this placement is refused" in exactly the position the building exists for.
`PlacementConnectorDrawer.DrawEntityConnectors` crosses any connector with an entity on the far
side carrying no matching connector at that pivot, and every other redstone component is such an
entity - they are `BuildingConnectors.SingleTile()` with no wire connector at all. So the cross is
correct by the drawer's rule and wrong about the world.

Two methods on `IBuildingGroupBuilder` read like the opt-out. This mod was calling
`NotRenderingConnectorConflictIndicator()`, which sets `RenderConflictingConnectorIndicators` - a
different field. The one that matters is `RenderConflictIndicatorVisualization`, set by
`NotRenderingConflictingIndicatorVisualization()`. **Switching to it would not have worked
either**: `BuildingDefinitionFactory` is the only code that turns that field into
`SkipConflictingConnectorsDrawingFlag`, and ShapezShifter never calls it -
`BuildingBuilder.WithConnectorData` news up a `BuildingDefinition` directly. Both fields are
carried to `BuildingDefinitionGroup` and never read.

`SkipConflictMarkers` attaches the flag to the definition `BuildAndRegister` hands back, guarded
with `Has<>` because it re-runs per scenario load and a doubled flag makes `TryGet` throw
`MultipleDataFitDataTypeQueryException` on a placement worker thread. It is the building twin of
the cargo mod's island file of the same name. The cost is that the converter never shows a cross,
including where one is deserved - the right trade for a building whose purpose is to sit between
two things that do not otherwise connect.

### A torch's ghost has to predict its own mount

A torch stands upright in open air and mounts against a block behind it, and which one it will be
is decided by `SolveMounts` after placement. Rotating during placement therefore changed nothing
on screen and the player had to guess which wall the torch would end up on.

The preview now predicts it. `PlacementPreview` holds a `Func<GlobalTileTransform, IMeshReference>`
per definition rather than a mesh, and the torch's resolver asks
`RedstoneWorld.HasBlockAt(BehindOf(position, rotation))` - deliberately the same arithmetic
`SolveMounts` uses, which is why `Behind` was split into a rotation-taking `BehindOf` rather than
copied. A copy that drifted would put the ghost on one wall and the torch on another, which is
worse than no preview at all. Everything else registers a constant resolver and is unaffected.

There is no separate "floor" rotation to select. A torch mounts when something is behind it and
stands when nothing is, so the five states the player sees are four walls plus open air, and the
ghost does not choose between them - it predicts.

**The blue outline could not follow.** `IsolatedBlueprintMesh` is one mesh per definition, baked
at registration, so it cannot vary with rotation the way the mesh drawn in the postfix can. Built
from the standing torch's bounds it was a narrow cage in the middle of the tile, visibly
disagreeing with a textured torch shoved against a wall. The torch's ghost is now the whole tile
framed to torch height: every one of the five states fits inside it, so the outline answers "which
tile" and the mesh inside answers "which wall", and neither contradicts the other at any rotation.

### The converter is the one thing here drawn as a machine

Every other component is a Minecraft object and is built to look like one. The converter has no
Minecraft counterpart, so there is nothing to copy and no texture to cut an icon from, and the
first pass - a plain iron cube with an arrow on it - read as an unfinished decoration block rather
than as a machine.

It is now a pad, an inset body, a core band standing proud of the body, and a cap. That stack is
the shape language the cargo mod's buildings use, and it is what makes a modded building sit next
to a vanilla machine without looking hand-made. The core band is the state: `obsidian` when idle,
`redstone_block` when either side is carrying, which makes the thing readable at a distance
without a HUD. The two `lapis_block` nubs mark the **wire** axis only - input at the back, output
at the front - because the redstone half has no direction, it reaches all four sides like a lever,
so marking those would be wrong.

Its icon is the one in the mod that is **drawn rather than cut from the atlas**, in
`write_icons`: the tile split on the diagonal into redstone red and wire blue with a
double-headed arrow across it. The rule further down - that an entry showing anything other than
the texture it places would be lying about itself - does not bite here, because there is no
texture it places. Double-headed rather than single because the building genuinely runs both ways
at once.
### Half of every rotation was being discarded

The observer's top texture took five attempts. The cause was one call to `Abs`, and the reason it
survived four rounds of reasoning is worth recording.

`BoxMesh.AddFace` built its half-spans as `Scale(Abs(right), extent)`. `extent` is a half-size and
is already non-negative, so the absolute value did nothing at all *except* throw away the axis's
sign - and the sign is the only thing that distinguishes one quarter turn from the turn opposite
it. Turns 1 and 3 therefore produced byte-identical meshes, as did 0 and 2. A top face had **two**
reachable orientations rather than four, and half of every rotation asked for was silently ignored.

Two wrong conclusions came out of that, both recorded here because the evidence genuinely did point
at them:

* Setting the turn to 3 and being told the result was a half turn out, then setting it to 1 and
  being told the same thing, reads as "no rotation fixes this, so it must be a **mirror**". That
  inference is sound and the conclusion was wrong - 1 and 3 were the same mesh, so the second
  report was not new information.
* The mirror added on the back of it did nothing either, for the same reason: it negated `right`,
  which `Abs` then undid on the next line.

With the sign kept, all four turns are distinct and the flip means something. The repeater and
comparator slabs now carry three turns **and** the flip, which together reproduce exactly the
orientation that was on screen while the bug was collapsing 1 and 3 - so the fix leaves the two
confirmed-correct textures untouched. The observer takes the same three turns without the flip,
which is the half turn it has needed all along and could not be given.

The general lesson is narrower than "a mirror is not a rotation", which was the wrong one: **when
two settings that should differ produce identical output, suspect the code that consumes them
before believing what the difference between them implies.**

### Diagnosing it

`db.redstone` prints the world's size and everything about the tile under the cursor - what
component is there, its power, a dust's connection mask, a torch's mount tile and lit state, a
repeater's delay and output - plus what each neighbour is and whether a neighbouring block is
strongly powered, weakly powered or off.

It exists because the latch above took far longer to find than it should have: from outside the
game there was no way to see whether a lit dust thought it had a source, let alone which one.

### Interaction

Shapez has no notion of clicking a building in the world - selecting one opens the side panel -
so the side panel is where the switch goes. `HUDSidePanelModuleGenericButton.Data` is
`(IText text, Action action)`, which is exactly a lever. The action closes over the **tile**
rather than over the component, so it keeps working if the component object is rebuilt.

### Rendering

Dust is the awkward one: its shape depends on its four neighbours (sixteen connection masks) and
its colour on its signal strength (sixteen levels), and 256 meshes would be silly. So **shape is a
mesh and strength is a tint**: sixteen flat quads, one per mask, drawn through
`AddWithProperties` with one `MaterialPropertyBlock` per strength.

Two details that would otherwise be wrong:

* `MaterialPropertyHelpers.CreateBaseColorBlock` mutates and returns a **shared static** block.
  Fine for set-then-draw-immediately, useless when sixteen have to coexist across a frame, so the
  renderer keeps its own.
* The property block hash is what the instanced renderer batches on, so it must be stable per
  level and distinct between them. `InstancingIdManager.AcquirePropertyBlockHash(string)` caches
  by key, which is that guarantee; a fresh hash per frame would put every dust in its own batch.

Minecraft does not ship a texture per dust shape - it ships one strand and a centre blob and
clips them - so `tools_import_minecraft.py` does the same once, at import, writing
`redstone_dust_0` through `redstone_dust_15`. They stay greyscale, because the colour is the tint.
The ramp is Minecraft's own from `RedStoneWireBlock`, worth copying exactly: a player reads
strength off the colour without counting tiles, and they already know that gradient.

### Still to do

The **shapez wire bridge**, which was asked for and is not in this pass. Reading a wire is cheap -
`map.Simulator.TryFindTileSimulation` to a `SignalNetworkSimulation` and its `LastOutput` - but
driving one means being an `ISignalProvider` in the network, which is real simulation work rather
than an afternoon. It is additive and changes nothing above.

## Placement is the game's own

An earlier version detoured `ScreenUtils.TryGetTileCoordinateAtCursor` so that pointing a block at
a block placed the new one on top, Minecraft style, instead of replacing it. It worked, and it was
**removed** on request: classic placement rules are what a shapez player already has in their
hands, and the layer keys already build upward.

The finding it produced is worth keeping even though the code is gone, and it is in the docs: the
building layer a placement targets is stamped once, in `ScreenUtils.TryGetTileCoordinate`
(`z = IslandLayer * 20 + BuildingLayer`), and every placement path reaches it through
`TryGetTileCoordinateAtCursor` - the only non-generic choke point in a pipeline whose placers are
all generic types MonoMod cannot hook. And do not drive `Viewport.BuildingLayer` instead: its
setter eases the **camera** to the new layer.

## The cube

Built in code, not imported. Three reasons, and the third is decisive:

1. `FileMeshLoader.LoadSingleMeshFromFile` ends in `.Meshes().Single()`, so 21 blocks would
   be 21 files.
2. `AssimpToUnityMeshConverter` writes every vertex as `float3(-x, y, z)` and emits indices
   `2, 1, 0`, so an authored cube arrives mirrored along X.
3. The UVs are the whole problem. Each face points at one cell of an atlas that does not exist
   until the mod has read the player's texture folder, so they cannot be baked into a file.

**Scale is not a choice.** `GlobalTileCoordinate.ToCenter_W` is `(x, y, z + heightOffset)` with
`z` the building layer, so one building layer is exactly **one world unit** and a unit cube
fills the gap to the layer above. Blocks stack the way a player expects, three layers to a
platform. (A chunk's `z` is the *platform* layer and is 20 units;
`GlobalTileCoordinate.ToChunkCoordinate` is `floor(z / 20f)`, so building layers 0-2 all live in
platform layer 0.)

Winding is derived rather than asserted: for each face the first triangle's cross product is
tested against the outward normal and the indices reversed if it points inward. Getting cube
winding wrong by hand produces a cube that is invisible from outside and solid from inside,
which reads as "the mesh did not load" and sends you looking in the wrong place.

### The collider is hand-built, because the helper is wrong here

`BoundingBoxHelper.CreateBasicCollider(mesh)` is the obvious call:

```csharp
Center_L = new LocalVector(mesh.bounds.center)
```

Those are different spaces. Unity's is Y-up; `LocalVector` is Z-up (`LocalVector.Up` is
`(0, 0, 1)`), and `new LocalVector(float3)` is a raw component copy with no swizzle. A cube
whose Unity bounds centre is `(0, 0.5, 0)` becomes a collider offset half a tile sideways,
sitting at deck level.

The default `SerializedCollisionBox` is already exactly right for a unit cube -
`Center_L (0, 0, 0.5)`, `Dimensions_L (1, 1, 1)` - so the block uses that.

## The atlas

Packed at runtime from individual PNGs in `Resources/Textures`, named by the catalog. That is
what lets a player swap in a texture pack by copying files: nothing in the code knows atlas
coordinates.

**Every cell is the tile repeated three by three, and the mesh points at the middle copy.** Mip
levels average across cell boundaries, so a tightly packed atlas bleeds a neighbouring tile's
colour into a block's edges as the camera pulls back, and bilinear sampling does the same at
the seam even at mip 0. Surrounding each tile with copies of itself means whatever bleeds in is
that same tile, at every mip level. It costs nine times the memory for an atlas a few hundred
pixels square.

Two details that would otherwise be wrong:

- `filterMode = FilterMode.Point`. A texture built in code defaults to bilinear, and bilinear
  on a 16-pixel texture is a smear. This is the single most important line in the mod.
- Resampling between resolutions is nearest neighbour, deliberately: a pack at a different
  resolution should stay crisp and the wrong size rather than blurry and the right size, and at
  integer ratios nearest neighbour is exact.

An animated texture in a resource pack is a vertical strip of square frames; the loader takes
the first frame rather than squashing the film onto a face.

### Toolbar icons are cut out of the atlas

`Sprite.Create(atlas.Texture, atlas.PixelRectFor(block.IconTexture), ...)`. No icon authoring at
all, and a texture pack's blocks show the pack's own art. The atlas is already point-filtered,
so a 16-pixel sprite blown up to toolbar size stays crisp.

This departs from the house icon style (512x512, flat, schematic, heavily outlined) on purpose:
an entry in a texture-pack mod that showed anything other than the texture it places would be
lying about itself.

## Known Shifter bugs met on the way

- **`BuildingBuilder.WithEfficiencyData` discards its argument.** It attaches
  `new BuildingEfficiencyData(2f, 1)` regardless of what is passed. `howto/add-a-building.md`
  tells you to pass the duration your simulation uses "or the readout lies" - the readout lies
  either way. The blocks use `WithoutEfficiencyData`.
- **`DynamicallyRendering<TRenderer, TSimulation, TDrawData>` never reads `TRenderer`.** The
  method body is `BuildingDefinition.CustomData.Attach(drawData); return this;`. The type
  parameter exists only as a compile-time constraint, and the renderer is found by reflection
  somewhere else entirely. It is still the only stage of the chain that attaches an
  `IBuildingCustomDrawData`, and `BuildingDrawDataFactory` throws without one, so the constraint
  has to be satisfied regardless.
- **`AtomicBuildingExtender.Build` null-checks prediction but not simulation**, above.

## Naming

The repo, the mod title and the Workshop entry avoid the Minecraft name. The blocks are named
generically - "Oak Planks", "Block of Lapis Lazuli" - because those are descriptions, not marks.
Texture files are named after the vanilla resource-pack file names so that a player can drop a
pack in unchanged; the mod ships placeholders of its own.

## Open questions

- **Rotation.** A cube is rotationally symmetric, so `NotAutoRotated` costs nothing today. A
  block with a directional side texture - a furnace, a jukebox - would need the rotation to be
  meaningful, and the mesh already honours `entity.Transform.Rotation`, so it would work; it has
  just never been exercised.
- **Stairs and slabs.** Both are plausible with the same machinery: a slab is a half-height cube
  and stairs are eight faces instead of six. Both need a real rotation story first.
- **Whether `AffectsSaveGames` has to be true.** It is set true, which means the mod cannot be
  added to or removed from an existing save. A placed block is a `BuildingDefinitionId` in the
  save, so removing the mod would orphan it - but it is worth checking whether the game drops
  unknown buildings gracefully, because a purely decorative mod that can be uninstalled is
  much friendlier than one that cannot.
