# Rendering

> **You need this when** you are putting something on screen and it has to stay fast.
> For the minimum viable version, start with
> [Draw in the world](howto/draw-in-world.md) and come back here when you need batching
> or culling.


Nothing in shapez 2 is a `GameObject` per building — at this scale it could not be. The
world is drawn every frame from the model by a pipeline of drawers, using instanced and
combined meshes. To draw something of your own you join that pipeline.

## The pipeline

```text
DrawManager                     owns the frame
├── DrawOptions : FrameDrawOptionsNoLOD
├── Hooks       : DrawHooks     [Obsolete] but simple
└── MapDrawer
    └── Draw(FrameDrawOptionsNoLOD options)
        ├── Culler.Cull(...)          → MapCullResult
        ├── options.Hooks.OnDrawMap(options, map, cullResult)
        ├── foreach (IMapSubDrawer d in SubDrawers)   ← the intended extension point
        │       d.Draw(options, cullResult)
        └── options.Hooks.OnDrawFinal(options)
```

`MapDrawer` catches and logs exceptions from each sub-drawer, so a throwing drawer
degrades rather than killing the frame. It also registers on the map's
island/building add and remove events to invalidate its caches.

## `FrameDrawOptionsNoLOD`

The per-frame context handed to every drawer. Most of what you need is on it:

```csharp
public readonly RenderersCollection Renderers;   // where you submit draws
public readonly Player Player;                   // → Player.CurrentMap
public readonly Viewport Viewport;
public readonly IShapeColorScheme ColorScheme;
public readonly IColorVisualizationManager ColorVisualization;
public readonly AccentColorPalette AccentColorPalette;
public readonly VisualTheme Theme;               // [Obsolete] "should be injected"
public readonly DrawHooks Hooks;                 // [Obsolete]

public float3 CameraPosition_W { get; }
public Plane[] CameraPlanes { get; }
public double SimulationTime_G { get; }
public float DeltaTime { get; }
public int MaxBuildingIslandLayer { get; }
public FrameBudgetManager FrameBudget { get; }
public LODComputationParameters LODComputation { get; }

public const float OverviewModeZoom = 1500f;
public bool InOverviewMode => Viewport.Zoom > 1500f;
```

`InOverviewMode` is how you make an overlay behave differently zoomed out — the game
uses the same flag to swap detailed island meshes for flat overview planes.

`FrameDrawOptions` is the same thing with LOD resolved; drawers that need level of
detail take that instead.

## Submitting a draw

`RenderersCollection` is a bundle of renderers, each batching a different category:

| Field | Use |
| --- | --- |
| `RegularNonInstanced` | arbitrary one-off meshes — the general-purpose choice |
| `UINonInstanced` | the same, on the UI layer |
| `Misc`, `Buildings`, `Islands`, `Playingfield`, `Shapes`, `UI`, … | `InstancedMeshManager`s, for many copies of one mesh |
| `BeltItems` | the item renderer, with shape drawing helpers |
| `LazyMeshCombinationManager` | combined-mesh caching |

The general-purpose call:

```csharp
void DrawMesh(
    IMeshReference mesh,
    IMaterialReference material,
    Matrix4x4 matrix,
    RenderCategory category,
    MaterialPropertyBlock properties = null,
    ShadowToken castShadows = default,
    ShadowToken receiveShadows = default);
```

```csharp
float3 position = tile_G.ToCenter_W(2.0f);

options.Renderers.RegularNonInstanced.DrawMesh(
    mesh,
    material,
    Matrix4x4.TRS(position, Quaternion.identity, new Vector3(20f, 20f, 20f)),
    RenderCategory.Misc,
    MaterialPropertyHelpers.CreateAlphaBlock(0.6f));
```

### Property blocks — read this before using them

```csharp
public static class MaterialPropertyHelpers
{
    public static int SHADER_ID_Alpha     = Shader.PropertyToID("_Alpha");
    public static int SHADER_ID_BaseColor = Shader.PropertyToID("_BaseColor");

    public static MaterialPropertyBlock CreateAlphaBlock(float alpha);
    public static MaterialPropertyBlock CreateBaseColorBlock(Color baseColor);
}
```

Both helpers mutate and return a **shared static** `MaterialPropertyBlock`. That is
fine in the game's usage — set it, submit the draw immediately, never hold it — but it
means you cannot build up several blocks and submit them later, and you cannot combine
alpha and colour by calling both. If you need more than one property at once, keep your
own `MaterialPropertyBlock` instance.

## How an island gets drawn

There is no single mechanism, and picking the wrong one costs an evening. An island's
visual can come from any of three places:

| Source | Where it lives | Transform |
| --- | --- | --- |
| `IslandMeshDrawer.Data` | CustomData on the definition | the island's own, one copy |
| `ModularIslandMeshDrawer.Data` | CustomData on the definition | a `LocalChunkTransform` per mesh, drawn at `module.Transform * island.Transform` |
| `IIslandPlatformDrawer` | a **session dictionary**, not CustomData | whatever the drawer does |

The modular drawer is the one the game is migrating toward — `IIslandPlatformDrawer` is
marked `[Obsolete("Replace this drawer with the more generic ModularIslandMeshDrawer")]`.

**Space paths are the trap.** A space belt or pipe definition carries *no*
`IslandMeshDrawer.Data` at all. Its meshes live on the **visual theme** —
`SpaceBeltResources` / `SpacePipeResources`, reached through
`ISpacePathResources.GetMeshMaterials(PathNodeClassification)` — and are drawn by a
`SpacePathPlatformDrawer`. So borrowing a belt's look by reading its definition silently
yields nothing.

Two details that bite if you try to re-stage those meshes through the modular drawer
anyway: `SpacePathPlatformDrawer` drops its mesh **2.07314 world units** and submits to
`Renderers.SpacePaths`, while `ModularIslandMeshDrawer` does neither — and
`LocalChunkTransform.Position` is an integer `ChunkVector`, so that sub-chunk drop cannot
be expressed there at all. For a modded space-path-like island, use an
`IIslandPlatformDrawer` instead.

**Usually without writing one.** `SpacePathPlatformDrawer` is public and constructible, so
an island that is a straight belt or a single turn just takes vanilla's drawer over
vanilla's track and comes out identical to the segment beside it. You only need your own
drawer for a shape the enum has no entry for — a crossing, say. And you do not have to
hardcode which entry: `PlatformPathDrawingClassifier.TryClassifySpacePathNode` derives the
`PathNodeClassification` from the island's own `IIslandConnectorData`, so West-in-East-out
classifies as `Forward` and the turns follow, and it keeps up if a connector ever moves.

```csharp
definition.CustomData.TryGet(out IIslandConnectorData connectors);
PlatformPathDrawingClassifier.TryClassifySpacePathNode(connectors, out var classification);
drawers[id] = new SpacePathPlatformDrawer(
    orchestrator.Theme.BaseResources.SpaceBelts, classification);
```

The connectors have to be `ISpacePathInputConnector` / `ISpacePathOutputConnector` for the
classifier to see them, which all four of `SpaceBeltInput/OutputConnector` and
`SpacePipeInput/OutputConnector` are.

> [!WARNING]
> **Turn the platform frame off as well, or you get both.** `CustomPlatformsDrawer` is
> *additive* — it does not replace the standard deck, it draws alongside it. An island
> built with `WithRenderingOptions(new HomogeneousChunkDrawing(
> ChunkPlatformDrawingContext.DrawAll()), drawPlayingField: true)` and then given a path
> drawer renders a full platform deck floating above its own track, which no vanilla space
> belt has. Path-like islands want `ChunkPlatformDrawingContext.DrawNothing()` and
> `drawPlayingField: false`.

### Registering a platform drawer

The dictionary is built by `GameSessionOrchestrator.CreateIslandPlatformDrawers`, which
walks `GameIslands.SpaceBelts`, `SpacePipes` and the rail lists. A modded island is in
none of them, and ShapezShifter has no rewirer for drawers — it has them for islands,
placers, toolbars, simulation and prediction, but not this. Postfix-hook that one method
and add your entries to the dictionary it just returned:

```csharp
DetourHelper.CreatePostfixHook(
    (GameSessionOrchestrator orchestrator, GameIslands islands) =>
        orchestrator.CreateIslandPlatformDrawers(islands),
    (orchestrator, islands, drawers) =>
    {
        drawers[myDefinitionId] = new MyPlatformDrawer(
            orchestrator.Theme.BaseResources.SpaceBelts);
        return drawers;
    });
```

Use the indexer, not `Add` — the vanilla loops use `Add`, and a duplicate key would take
the whole session's drawer setup down. Implement `DrawOverview` too, or your island
disappears on the zoomed-out map while everything around it stays.

This hook is also, in practice, the earliest point a mod holds both a `GameIslands` and a
`Theme`. If you need either later — to read a material, or to dump one for inspection —
keep the reference as it goes past. `IGameSessionManagers`, what `GameHelper.Core` returns,
exposes the player, mode, registries and viewport but neither the session nor the theme,
so there is no second way to ask.

### Putting your own mesh on an ordinary island

`ModularIslandMeshDrawer` is registered unconditionally in `CreateMapSubDrawers` and draws
whatever definitions carry its `Data`, so for a normal island there is no hook at all —
attach and you are done. Two things are not obvious:

- **`Data` must be attached to the concrete `IslandDefinition`.** `IEntityDefinition`
  exposes `CustomData` only as `ICustomDataReader`, so `GameIslands.TryGetDefinition` hands
  back something you cannot attach to; cast. Use `AttachOrReplace` rather than `Attach` —
  definitions and this hook both run per session, and `Attach` on an already-attached type
  is how re-entering a session becomes a crash.
- **`ILODMeshMaterial` has no runtime-constructible implementation.** `Module` wants one,
  and the only one the game ships is `LODMeshMaterialAsset`, a `ScriptableObject` authored
  in the editor. The interface is public and has three members, so implement it yourself,
  pairing your `LOD6Mesh` with `Theme.BaseResources.IslandMaterial` — taking the material
  from the theme rather than shipping one means your island follows whatever theme the
  player is using.

`LocalChunkTransform.Identity` is the right transform for a single-chunk island whose mesh
is authored centred on its chunk: `Module.Transform` is multiplied by the island transform,
so identity means one mesh serves all four rotations.

> [!WARNING]
> **`ModularIslandMeshDrawer.Data` cannot vary per instance.** It is CustomData on the
> *definition*, so every island of that type on the map shares one copy. If you want a
> machine to show what it is holding — a buffer's fill level, a hopper's contents — that
> has to come from a simulation renderer instead, which is handed the entity, and so the
> state, every frame.

> [!WARNING]
> **Per-instance data on a material that does not declare it makes the mesh disappear.**
> It is not ignored. `AddWithPerInstanceData` builds a separate
> `InstancedMeshRendererWithPerInstanceData<T>` batch, and a shader with no matching buffer
> draws nothing at all — so a speculative tint does not degrade to "no visible change", it
> deletes the geometry.
>
> Materials differ *within one object*. Feeding `TextureIndexPerInstanceData` to a space
> path's meshes recolours the trim and the direction arrows, which sample the accent
> palette, and erases the deck plane, which does not — leaving a belt as a pair of floating
> edges. `ISpacePathResources.StandardPlaneMaterial` is the discriminator, and it is the
> same one vanilla uses to swap in the blueprint material, which is a hint that the plane is
> a different shader.
>
> So: test per-instance data one material at a time, and keep the ones that fail on the
> plain `Add` path.

### Getting a valid colour onto a mod's own mesh

Colour is an atlas lookup through UV0 (see
[Load models and icons](howto/load-models-and-icons.md)), and the atlas cannot be read
statically — so a hand-picked UV is a guess, and a wrong guess is usually a lurid one.

There is a way to stop guessing: **take the coordinate off a vanilla mesh the game draws
with the same material.** `CargoExchangerDrawer` draws
`Theme.BaseResources.Trains.Cargo.ShapeCargoPackage` with
`Theme.BaseResources.IslandMaterial`, so whatever UV that mesh carries is by construction a
coordinate that lands on a sensible island colour.

```csharp
asset.TryGet(0, out IMeshReference reference);
Mesh mesh = reference.GetMeshInternal();
if (mesh != null && mesh.isReadable)
{
    Vector2[] uvs = mesh.uv;   // rank by how many vertices share a value
}
```

Author your own mesh with a per-role sentinel UV, then rewrite the sentinels to sampled
values at load. Rank candidates by how many vertices share a coordinate — a decent proxy for
"the main colour of this object" — and take a mesh's *second* most common coordinate when a
part needs to differ from its neighbour, so the two shades still belong together.

`mesh.isReadable` is the catch: a mesh shipped in a build usually has no CPU-side copy, so
`mesh.uv` is unavailable and this returns nothing. `mesh.bounds` is metadata and works
regardless, which is enough to scale a borrowed mesh to fit.

## Simulation renderers register themselves

Anything travelling *through* a machine — items on a belt, fluid in a pipe — is drawn by
an `ISimulationRenderer`, and those need **no registration at all**:

```csharp
Type[] second = (from type in GetAllLoadableTypes()
    where typeof(IIslandSimulationRenderer).IsAssignableFrom(type)
          && !type.IsAbstract && !type.IsInterface
    select type).ToArray();
```

### Drawing train cargo: use the game's drawers, not its meshes

If you draw a cargo package, do **not** draw `Trains.Cargo.ShapeCargoPackage` or
`FluidCargoPackage` as a mesh. A fluid package is three meshes, and that one is the empty
shell — no colour, no lid, see-through. `FluidCargoContainerDrawer` also draws
`FluidInsideCargoCrate` through `Renderers.FluidsContainer` (which is what applies the
fluid's own colour, from `IFluidRegistry.GetFluidReference`) and
`FluidCargoContainerGlassLid` with `BuildingsGlassMaterial`. `ShapeCargoContainerDrawer`
likewise draws the contained shape above the crate.

Both are constructible, and `ICargoContainerDrawer<TItem>.Draw` takes a `Matrix4x4` — so a
mod can place a complete, correctly-coloured package anywhere:

```csharp
ICargoContainerDrawer<ShapeId> drawer = new ShapeCargoContainerDrawer(shapeRegistry);
drawer.Draw(options, in package, Matrix4x4.TRS(pos, rot, Vector3.one * scale), flipped: false);
```

Scale it down from `mesh.bounds` if it is not going on a train — the packages are authored
wagon-sized, roughly a chunk wide.

> [!WARNING]
> `CreateSimulationRenderers` binds `IShapeRegistry` into its container but **not**
> `IFluidRegistry`. Asking for the fluid registry in a renderer's constructor fails to
> construct it — and because every renderer is built in one pass, that takes out every
> other mod's renderers too. Resolve it lazily from `GameHelper.Core` instead.

Two consequences worth knowing before you write one. Renderers are keyed by **simulation
type**, so one renderer covers every island sharing that simulation — and an `abstract` or
open generic renderer is skipped, so a generic base needs a concrete subclass per closed
type. And the publicizer turns `ShouldDraw` and `OnDrawDynamic` public, so they must be
overridden as `public override`, not `protected override`, despite what the decompiled
source shows.

`GetAllLoadableTypes()` is `AppDomain.CurrentDomain.GetAssemblies()`, so it reaches a mod
assembly as readily as the game's own, and each type is built through a dependency
container (`IMapModel`, `GameMode`, `ISimulator` and friends are bound). That is why every
vanilla renderer carries `[UsedImplicitly]` — nothing references them by name.

Renderers are keyed by **simulation type**, not definition id, so one class covers every
definition sharing that simulation. `SimulationsDrawer` logs an error and drops yours if
another renderer already claims the same simulation type.

One caveat if you subclass `SpacePathSimulationRenderer`: it sorts connectors into
per-item-type lists, then runs the whole item list once against the shape lists and once
against the fluid lists, casting each item unconditionally. Fine where every connector on
a node carries the same item type — an `InvalidCastException` the first time a
`FluidPackageItem` meets the shape pass. An island mixing belts and pipes has to pair its
own connectors instead.

## Meshes

| Type | Lifetime |
| --- | --- |
| `IMeshReference` | the interface everything takes (`InstanceId`, `IsEmpty`, `GetMeshInternal()`) |
| `TemporaryMeshReference` | a mesh you own and **must dispose** |
| `LazyCombinedMesh` | a combined mesh built on demand |
| `ExpiringDisposableObject<T>` | a cache slot that releases after disuse, driven by `IResourceLifetime` |

Ready-made geometry lives on `GeometryHelpers`:

```csharp
public static IMeshReference PlaneMesh;      // a unit plane
public static IMeshReference BillboardMesh;
public static TemporaryMeshReference GeneratePlaneMeshUVColoredUncached(Color color);
public static Mesh GenerateTransformedMeshUncached(Mesh baseMesh, Matrix4x4 trs);
```

Note how the game colours flat overlays: it does **not** set a colour per draw, it bakes
the colour into a plane mesh and caches one mesh per colour
(`HUDOverviewModeMapResourcesRenderer` keeps a `DisposableDictionary<Color,
TemporaryMeshReference>`). For an overlay with a small palette — say eleven buckets from
red to green — that is the pattern to copy.

### Combining

Thousands of individual `DrawMesh` calls will cost you. `MeshBuilder` batches them into
one mesh:

```csharp
using MeshBuilder builder = new MeshBuilder("MyOverlay(chunk)", lod: 0);

foreach (GlobalChunkCoordinate chunk in island.Chunks)
{
    builder.AddTranslateScale(planeMesh, chunk.ToCenter_W(-20f), new float3(20f, 20f, 20f));
}

if (builder.Empty) return null;
TemporaryMeshReference combined = builder.GenerateSingleMeshMax65KVertices();
```

Other adds: `AddTranslate`, `AddByTransform(mesh, in GlobalTileTransform)`,
`AddTranslateRotate(...)`, and LOD-mesh overloads. `GenerateCombined()` and
`GenerateLazy(bool)` are the alternatives when you want a multi-mesh or deferred result.

Build these **once and cache**, keyed by whatever makes them invalid — the game caches
per `SuperChunkCoordinate` and rebuilds when an island or building changes. Rebuilding
a combined mesh every frame is worse than not combining at all.

## Getting your code into the frame

### Option A — postfix `MapDrawer.Draw`

The shortest route, and it hands you the full context:

```csharp
Hook DrawHook = DetourHelper.CreatePostfixHook<MapDrawer, FrameDrawOptionsNoLOD>(
    (drawer, options) => drawer.Draw(options),
    (drawer, options) => Overlay.Draw(options));
```

Your code runs after all the game's drawers, which is what you want for an overlay that
sits on top. `options.Player.CurrentMap` gives you the map without any global lookup.

### Option B — `DrawHooks`

`FrameDrawOptionsNoLOD.Hooks` carries multicast delegates — `OnDrawMap`,
`OnDrawSuperChunk`, `OnDrawShapeResourceSource`, `OnDrawFluidResourceSource`,
`OnDrawIslandNotch`, `OnDrawTrain`, `OnDrawFinal`. `HUDOverviewMode` uses
`OnDrawSuperChunk` this way:

```csharp
DrawHooks hooks = DrawManager.Hooks;
hooks.OnDrawSuperChunk = (DrawHooks.DrawSuperChunkDelegate)Delegate.Combine(
    hooks.OnDrawSuperChunk, new DrawHooks.DrawSuperChunkDelegate(DrawSuperChunk));
```

Always `Delegate.Combine` / `Delegate.Remove`, never assign. The whole class is marked
`[Obsolete("Instead use/introduce concepts like the IMapSubDrawer.")]` — it works, but
it is on the way out.

### Option C — `IMapSubDrawer`

The intended extension point. `DrawManager` takes `IEnumerable<IMapSubDrawer>` at
construction and `MapDrawer` copies it into a list, so registering one after the fact
means reaching that list — which the [publicizer](publicizer.md) makes possible. More
work than a hook, and more idiomatic; worth it for a drawer you intend to maintain.

Sub-drawers can also implement `IMapSubDrawerIslandEventListener` to be told when
islands are registered or unregistered, which is how `IslandOverviewDrawer` keeps its
per-super-chunk cache honest.

## Depth, layers and overlays

Two facts that decide whether an overlay looks right, and neither is obvious until it looks
wrong.

**The instanced UI renderer ignores depth.** `options.Renderers.UI` draws over everything,
which is what the super chunk coordinate labels want — nothing occludes them from out in
space. It is not what a label sitting on a machine wants: it shows through the platform
above it. There is no easy fix from a mod, because the depth state belongs to the material
and the materials come from the game's asset bundles. The practical answers are to scope
what you draw to the layer being viewed, or to not draw world-space text at all and put the
information in a side panel.

**Layer visibility is a predicate the game already exposes:**

```csharp
if (!options.ShouldRenderPlatformContentsAtLayer(chunk.z))
{
    continue;   // the player is looking at a lower layer
}
```

That is `chunkPlatformLayer <= MaxBuildingIslandLayer` internally.

> [!WARNING]
> `MaxBuildingIslandLayer` **defaults to 999**, meaning unrestricted. Code shaped like
> `if (chunk.z == options.MaxBuildingIslandLayer)` — "only annotate the current layer" —
> therefore matches nothing during ordinary play and silently does nothing. Test against
> the predicate, and treat any value near 999 as "no layer restriction".

**Translucent draws blend with each other.** An overlay quad drawn on top of another
overlay quad mixes colours: a green marker at the wash's 55% alpha over a red tile reads
as *yellow*, not green. If a second layer of drawing is meant to have its own colour, give
it its own property block at a high alpha rather than reusing the one underneath.

## World-space text without a font

There is no world-space text API, but there is a character atlas — the one behind the
super chunk labels. `UXSuperChunkCoordinatesRendererMaterial` maps a 7x7 grid:

| Index | Character |
|---|---|
| 0–9 | digits `0`–`9` |
| 10–35 | `A`–`Z` |
| 36 | `/` |
| 37 | `-` |

Build one quad per character with UVs into the cell, then draw them side by side through
`options.Renderers.UI`. `HUDSuperChunkCoordinatesVisualization` is the working example,
including the slightly surprising UV origin it uses.

Size them by the width the label is allowed to occupy rather than by a fixed world size.
`Viewport.Zoom` is the camera's **distance** (4 near, 20000 far), so a label of world size
`s` covers roughly `s / zoom` of the screen — multiply zoom by a constant for a stable
on-screen size, and cap it so a four-digit number shrinks instead of sprawling across its
neighbours.

## Performance notes

- Cull first. `MapCullResult` from `MapDrawer` tells you which super chunks are visible;
  drawing for the whole map when the camera sees a corner of it is the most common
  mistake.
- Cache combined meshes; invalidate on the map's add/remove events.
- Dispose every `TemporaryMeshReference` — `TemporaryMeshReference.WarnAboutNonDisposedInstances()`
  exists because leaking them is a known trap.
- Respect `options.FrameBudget` if you generate meshes lazily.
