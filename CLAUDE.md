# shapez 2 modding workspace

One folder per repo. Work happens in the `Shapez2-*` mod repos; the other three are
references.

| Folder | What it is |
| --- | --- |
| `shapez2-modding-docs/` | **Community modding docs. Read these first, and keep them updated.** |
| `decompiled/` | Decompiled game + ShapezShifter assemblies. The source of truth for behaviour. |
| `shapez2-mod-samples/` | Fork of the official samples. Reference only — don't add files here. |
| `Shapez2-Toolbar-Kit/` | Shared *source* (not an assembly) for name-based toolbar placement. |
| `Shapez2-Train-Cargo-Tools/` | Cargo packagers, belts, stores; detours so stations eat packages. |
| `Shapez2-Crossover-Platforms/` | Belt/pipe crossings. |
| `Shapez2-Platform-Blackbox/` | Collapsing platform selections into blueprints. |
| `Shapez2-Space-Platform-Efficiencies/` | Throughput HUD overlay (shipped). |
| `Shapez2-Extra-Shape-Parts/` | New shape quadrant types; procedural quadrant meshes. |
| `Shapez2-Extended-Research/` | Extra research tiers. Also has layer unlocks, with machine levels clamped off. |
| `Shapez2-Extra-Build-Layers/` | Substrate for many more space levels (default +10) and +1 machine level. Pluggable `ILayerUnlockPolicy`; public `BuildLayers` API for other mods. |
| `Shapez2-Decoration-Blocks/` | Voxel decoration blocks on the machine layer; own texture atlas and material. |
| `Shapez2-First-Person/` | Eye-level camera: walk, gravity, build at a crosshair. |
| `Shapez2-Mod-Reloader/` | Hot reload during development. |
| `Shapez2-Mod-Profiler/` | Flame graph, managed heap census, live counters. Dev tool. |
| `Shapez2-Infinite-Train-Jumps/` | DESIGN.md only, no code. |

## Use the docs, and add to them

`shapez2-modding-docs/` is the accumulated answer to "how does this actually work".
Before investigating a modding question from scratch, **search there first** — several
things that look like fresh discoveries are already written up, sometimes more thoroughly.

```
grep -rn "<thing>" shapez2-modding-docs/docs/
```

Layout: `docs/*.md` are concept pages (architecture, hooking, coordinates, map model);
`docs/howto/*.md` are task pages; `docs/toc.yml` is the nav. `api/` is DocFX-generated —
never hand-edit it.

**When you learn something non-obvious about the game or ShapezShifter, write it down
there.** That is the point of the repo. Prefer extending an existing page over adding
one; add a `toc.yml` entry only if you create a page. Match the house voice: state the
problem, then the mechanism, then the consequence. Say *why*, and name the concrete
class or method that proves it.

If a page's code sample contradicts a warning further down, fix the sample — people copy
samples.

## Finding out how the game works

`decompiled/` holds the decompiled assemblies. `grep -rn` there answers most questions
faster than guessing.

**What is not in there:** authored ScriptableObject data. Island definitions for train
stations, toolbar structure, base processing durations, scenario JSON — all authored, so
you cannot read their values statically. Don't guess a constant and ship it; either
source it at runtime from `GameMode`/config, or say you couldn't and leave it out. A
confidently wrong number in the UI is worse than no number.

## Build and install

Three env vars drive the projects: `SPZ2_PATH` (game install), `SPZ2_PERSISTENT`
(`~/AppData/LocalLow/tobspr Games/shapez 2`), `SPZ2_SHIFTER` (ShapezShifter.dll).

```bash
dotnet build                 # installs into <persistent>/mods/<Mod>      (game must be CLOSED)
dotnet build -p:Dev=true     # stages into <persistent>/mods-dev/<Mod>    (safe while running)
```

**Always check whether the game is running before building**, because the failure mode is
confusing:

```powershell
Get-Process | Where-Object { $_.ProcessName -match 'shapez' }
```

The installed dll is memory-mapped while the game runs, so a plain build fails with
`MSB3027 / user-mapped section open` — note that this is the *copy* failing, not a
compile error.

The game loads from `mods/`. `mods-dev/` only exists for the reloader. It is easy to
"fix" something, stage it, and have the user still running the old build — **verify what
is actually installed**:

```bash
grep -o '"Version": "[^"]*"' "$SPZ2_PERSISTENT/mods/<Mod>/manifest.json"
```

### Rider

`python Tools/make-rider-configs.py` writes a run configuration per mod into
`.idea/.idea.Shapez2-Mods/.idea/runConfigurations/`, grouped into three folders:

| Folder | Runs | Goes to |
| --- | --- | --- |
| 1 Install to mods | `dotnet build` | `<persistent>/mods/<Mod>` — game closed |
| 2 Stage to mods-dev | `dotnet build -p:Dev=true` | `<persistent>/mods-dev/<Mod>` — safe while running |
| 3 Publish to Steam | `dotnet build -t:SteamPublish` | the workshop item in `Steam/base.vdf` |

The working directory is the **project** folder, not the mod repo: Decoration Blocks and Extra
Shape Parts have no solution file at their root, so a bare `dotnet build` there fails with
`MSB1003: Specify a project or solution file`. Publish is only generated for projects that
declare the target. Re-run the script after adding
a mod; it only rewrites the files it owns (`_mod_*.xml`), so anything hand-made beside them
survives. `.idea/` is gitignored, so these are local-only.

**Do not guess a Rider run configuration `type`.** A wrong one is rejected outright with
"Unknown run configuration type", and Rider's .NET config classes are obfuscated - the string
constants in them are fragmented and the plausible-looking ones turn out to be display names.
`DotNetExe` and `DotNetExecutable` were both inferred from `intellij.rider.jar` and both
rejected. These use `ShConfigurationType`, which is IntelliJ platform rather than Rider, has a
public and long-stable id, and is bundled (`com.intellij.sh.run.ShConfigurationType`). It shells
out through Git Bash, so `dotnet` must be on PATH - which it already is, or none of the command
lines above would work either.

### Hot reload

`mrl.reload <modname>` in-game picks up `mods-dev/`. It reloads **code only**:

- Logic changes, detours, HUD providers → reload works.
- New islands, new toolbar entries, collider or definition changes → **restart required.**
  Definitions and the toolbar are built per session, and `AtomicIslands…Build()` returns
  no handle so a mod cannot unregister them in `Dispose`. Reloading then re-entering a
  session double-registers and can crash on a duplicate key.

Log: `$SPZ2_PERSISTENT/Player.log`. Read it before theorising about a crash.

## Conventions in these repos

- **Every type lives under `QuinnBast.Shapez2.<ModName>`.** The game has ~2,800 types in the
  global namespace, so a mod type left there is a collision waiting to happen - `Data`,
  `Entry` and `Configuration` all did collide before this was applied. Files added since use
  a file-scoped `namespace QuinnBast.Shapez2.<Mod>;` (hence `LangVersion 10`); older files
  keep their block form. The namespace is **not** the assembly name: `AssemblyName` is unset
  everywhere, so dll filenames follow the project name and `manifest.json` is unaffected.
- **XML doc comments explain *why*, not what.** Match the surrounding density. Comments
  that record a constraint you discovered ("this is the only option because X throws")
  are the valuable ones.
- Each mod has a `DESIGN.md` recording verified facts with the class/method that proves
  them. Keep it current when behaviour changes.
- `Directory.Build.props` adds the `-p:Dev=true` staging mode; Apache 2.0 `LICENSE` +
  `NOTICE`; `PublicizeAll` with a `DoNotPublicize` list; `NoWarn=CS0436`.
- Krafs.Publicizer **works** despite the build warning "Assembly is marked for
  publicization, but no members were publicized". Private game members are callable.
  Verify with a throwaway probe file rather than trusting the warning.
- Shared code is distributed as **source, not assemblies** — see
  `Shapez2-Toolbar-Kit/ToolbarKit.props`. Two copies of one dll in the same AppDomain, or
  a mod that hard-depends on another mod, are both problems worth avoiding for ~100 lines.

## Other agents edit these repos concurrently

More than one session works here at once. Files change under you.

- Before editing a file you last read a while ago, re-read it.
- If a build fails in a file you never touched, check `ls -la --time-style=+%H:%M` before
  assuming you broke it — and don't "fix" someone's in-flight edit.
- Several fixes have already been applied by another session (the `ModResources` hot-reload
  fix, the per-chunk collider, the docs' collider section). **Check before doing work
  twice.**

## Traps that have already cost a session

Each is written up properly in the docs; this is the index.

| Trap | Page |
| --- | --- |
| `WithBoundingCollider()` makes a **zero-sized** box — island cannot be clicked. Always `WithPerChunkColliders()`. | `howto/add-an-island.md` |
| MonoMod cannot hook a method on a **generic type**, struct instantiation included. Compiles, then kills startup. Relocate to a non-generic choke point. | `hooking.md` |
| Toolbar ids end in `.title`, casing is inconsistent, and separators are skipped resolving the parent but counted for the leaf index. | `howto/add-to-toolbar.md` |
| `Bind("x", n.ToString().T())` renders `?1` — `.T()` means *translation id*. Use `RawText`. A `?` prefix anywhere means a failed lookup. | `howto/add-translations.md` |
| No stock side-panel module renders an arbitrary **live number**; text is set once. `HUDSidePanelModuleRocketProgress` is the generic current/max bar despite the name. | `howto/island-side-panel.md` |
| Pipette map is a `Dictionary.Add`; a second placer over the same definitions crashes startup. | `howto/add-an-island.md` |
| `ModDirectoryLocator` throws on hot reload — a byte-loaded assembly has an empty `Location`. | `howto/load-models-and-icons.md` |
| `SyncableIdentifier` is read with `inherit: false`, so every saved state needs its own concrete type and id. | — |
| `translations.json` is keyed by **language code first**; placeholders are self-closing `<name/>`. Either mistake aborts mod load. | `howto/add-translations.md` |
| `AffectsSaveGames: true` means the mod cannot be added to or removed from an existing save. | — |
| Shape sub-part meshes carry their **material assignment in vertex colours**, and ShapezShifter's model importer drops that channel - so a shape part mesh cannot be loaded from a file. | `howto/add-a-shape-part.md` |
| An IMGUI overlay does **not** block the game — the wheel still zooms behind it. Consume the input context in a `HUD.OnGameUpdate` postfix; uGUI clicks need a raycast blocker as well. | `howto/notifications-and-hud-screens.md` |
| The runtime's managed heap walk **cannot be called from a mod**: `mono_unity_liveness_*` reports through a callback, a callback into a mod is managed code, and a stopped GC world forbids entering managed code. The game hangs with no log line. (`mono_gc_walk_heap` is separately a Boehm stub returning 1.) Walk the graph with reflection instead. | `howto/profile-a-mod.md` |
| **Simulation does not run on the main thread.** `Simulator.StartAsynchronousUpdate` returns a `Task`, so a mod's registered simulations run on pool threads - anything that measures or hooks per-thread state and assumes the main thread sees nothing. | `howto/profile-a-mod.md` |
| Reflecting over an unknown heap: `GetValue` on a **pointer field** boxes a new `System.Reflection.Pointer` whose own field is a pointer, so a walk that follows them never ends; and reading a **thread-static or pointer static** crashes natively inside `GetValueInternal` - a crash window, not an exception. Exclude before the call, never after. | `howto/profile-a-mod.md` |
| `ShapesConfiguration.Parts` is a `List<MetaShapeSubPart>` wearing `IReadOnlyList<IShapeSubPart>` - covariance makes that legal, so `is List<IShapeSubPart>` is **false**. Cast to non-generic `IList`. | `howto/add-a-shape-part.md` |
| A shape part mesh is authored for exactly `360 / PartCount` degrees and nothing rescales it angularly, so a quadrant mesh injected into the 6-part **hexagonal** configuration overlaps its neighbours. Vanilla ships a separate part asset per configuration, and codes `G` `H` `F` are taken there. | `howto/add-a-shape-part.md` |
| Namespaces are free to move *except* for `ModSaveData`: `ModSaveDataExtensions.ResolveId<T>()` is `AssemblyName + "-" + typeof(T).FullName`, so renaming a namespace silently orphans a shipped mod's saved settings. `[SyncableIdentifier]` is an explicit string and is unaffected. | `howto/save-data.md` |
| A wiki page is **two halves in two objects** - the reference in `ResearchProgression.WikiConfiguration`, the entry in `GameData`. A reference whose entry is missing throws out of `WikiDatabase`'s constructor and takes the session. A `MetaWikiEntry`'s id is its `name`, and its title key is fixed at `wiki.<id>.title`. | `howto/add-a-wiki-entry.md` |
| A research cost is **hundreds of displayed points**: `Format(this ResearchPointCurrency)` renders `Amount * 100`, so `48` is the "4.8k" on screen. Getting it backwards prices a node a hundredfold too high and still looks plausible. | `howto/add-research-unlock.md` |
| `UnlockedWithNewSideUpgrade` registers its node **once per island/building group**, and `CustomSideUpgradeSelector.Select` is itself a call to `Build` - so one node shared by ten islands becomes ten shop entries. Use a get-or-create `ISideUpgradeSelector`. A side upgrade's **preview image is not optional** either: `GetImage` throws on the empty id and takes the research screen with it. | `howto/add-research-unlock.md` |
| A wrapper on a bundle or lane the game reads back must return **the object that was assigned**, not itself. `ItemOutputChunkConnector.TryDisconnect` compares `ProviderBundle.NextBundle` to the other side's bundle by reference, so a self-reporting wrapper makes disconnect silently fail - and `TryConnect` then refuses because the old connection is still set. Symptom: one island never works again after being replaced. | — |
| `AtomicIslandExtender.Build` re-arms its chain only when **every** branch has fired, and the prediction branch never fires for a player with the **`prediction` setting off** (`StartPredictionUpdate` skips `SetupPredictions`, the sole caller of `CreateSimulationSystems`). The islands, toolbar entry and unlock are then spent on the **main menu's background game** and their save gets nothing, with no error. Keep prediction off the chain, re-armed by hand. | `howto/add-an-island.md` |
| A throw in a mod constructor is **not** contained by `ModLoader` - it comes out through `ModLoadingStep.LoadMods` and kills the game's whole mod loading step. Static field initialisers running in declaration order are an easy way to cause one. | — |
| `GetCursorPointOnVirtualPlane` returns **`new double2(0)` - the map origin** - when the cursor ray misses the ground plane, and every placement path goes through it. A camera near level does not fail to place a building, it places one at the centre of the map. Use the `Try…` form and check the bool. | `camera-and-viewport.md` |
| `InputDownstreamContext.MouseDelta` is the frame delta of `Input.mousePosition`, so a **locked cursor freezes it** and mouse look through the context sees nothing. Read `Input.GetAxisRaw("Mouse X"/"Mouse Y")`. | `camera-and-viewport.md` |
| The `debug` keybindings layer is **live for ordinary players**: `F6` is `debug.step-speed`, `F7` `debug.slow-speed`, `F8`-`F10` the other speeds. A mod binding one of those silently changes the simulation speed, which reads as "the mod pauses the game" rather than as a key collision. `F5`, `F11` and `F12` are genuinely free, and `M` is the only unbound letter. | — |
| A building **cannot carry its own material**. `IslandChunkStaticBuildingsDrawer` combines a chunk's main meshes and draws them with one shared `Theme.BaseResources.BuildingMaterial`, and `BuildingDrawData` holds no material. For a real texture, leave `MainMeshPerLayer` empty and draw from a `StatelessBuildingSimulationRenderer` - which is already the per-chunk sub-drawer you would otherwise write. | `rendering.md` |
| The building shader is `Shader Graphs/UberBuildingShader` and has **no albedo map** - no `_BaseMap`, no `_MainTex`. Colour is a UV0 lookup into a 256x256 `_MaterialLUT` palette with metal, noise and scratch passes over it, so an atlas fed into the LUT comes out as etched metal. For a real texture use `Universal Render Pipeline/Lit`, which **is** in the shipped build - but dump `Resources.FindObjectsOfTypeAll<Shader>()` to confirm, because a stripped URP shader makes `Shader.Find` return null rather than fail. | `rendering.md` |
| `GetTexturePropertyNames` returns Unity's built-ins (`unity_Lightmaps`, …) **first**, so "fall back to the first texture property" writes your texture into the lightmap slot. Nothing errors; the mesh just draws in the material's own colours and you go looking at your vertices. | `rendering.md` |
| The game's **colours are not in `_MaterialLUT`** - it is a shade ladder whose one hued column is painted placeholder **red**. A face renders in a live colour by landing in that accent column (`u ∈ [0.0625, 0.125)`, slot = row of `v`), which the shader fills from the `_G_AccentColorPalette` global array. Sample the texture and every hue you find is a red while the buildings on screen are orange; the red never renders. `AccentColorMeshCreator` carries the arithmetic. | `rendering.md` |
| `Graphics.RenderMeshInstanced` - what every `InstancedMeshManager` ends in - **draws nothing** for a material without `enableInstancing = true`. A clone of a game material inherits the flag; a `new Material(Shader.Find(...))` does not. | `rendering.md` |
| The building layer a placement targets is stamped once, in `ScreenUtils.TryGetTileCoordinate` (`z = IslandLayer * 20 + BuildingLayer`), and every placement path reaches it through `TryGetTileCoordinateAtCursor` - the one non-generic choke point, since `AreaPlacer`, `SinglePlacer` and `ModularEntityPlacer` are all **generic types** MonoMod cannot hook. Do **not** drive `Viewport.BuildingLayer` instead: its setter eases the **camera** to the new layer. | `rendering.md` |
| A building mesh that implies a **direction** must be authored along **+X**. Rotation is about Unity's Y, rotation zero is identity, and `TileDirection.East` at rotation zero is +X (game East is `LocalVector(1,0,0)`, and the game-to-Unity cast is `(x, z, -y)`). Author along Z and the mesh is drawn ninety degrees from the direction the logic reads and writes - the mesh is right, the logic is right, and nothing looks like an orientation bug. | `howto/load-models-and-icons.md` |
| A **top face's** UV frame is not `Cross(up, normal)` - that yields -X for the +Y face and **mirrors** every texture put on it. Use u=+X, v=+Z, matching what a block's top texture is authored for. Mirroring is invisible on gravel or grass and only shows once an asymmetric texture lands on it, so it survives a long time. Minecraft's repeater and comparator textures are **top views** carrying the component's direction, and want one quarter turn so the texture's up lands on +X. | `howto/load-models-and-icons.md` |
| Building a face's half-spans as `Scale(Abs(axis), extent)` **silently discards half of every rotation**. `extent` is already non-negative, so the `Abs` only strips the axis's sign - and the sign is all that separates a quarter turn from the turn opposite it. Turns 1 and 3 emit identical meshes, as do 0 and 2, so a face has two reachable orientations instead of four. The symptom is a texture that reads a half turn out at *every* setting, which looks exactly like a mirror and is not one. | `howto/load-models-and-icons.md` |
| The game is fine with a building that has **no simulation** (`Simulator.OfferBuilding` returns when no system claims the id), but `AtomicBuildingExtender.Build` dereferences its simulation branch unconditionally while null-checking prediction. No `WithSimulation` means an NRE out of the mod constructor, not a decoration. | `howto/add-a-building.md` |
| `BuildingBuilder.WithEfficiencyData` **discards its argument** and attaches `new BuildingEfficiencyData(2f, 1)` regardless. The docs' "pass the duration your simulation uses" is not achievable through it. | `howto/add-a-building.md` |
| Map resource patches **can** live at any island layer - `GlobalChunkCoordinate` hashes `z` and `GetResourceAt_GC` keys on the whole coordinate - but `SpaceThemeBoundsProvider.ComputeResourceSourceBounds` **overwrites their culling bounds** with the constants `-50f`/`-22f`, so a patch above layer 0 is frustum-culled unless the camera frames that band. The mesh is built from `Origin_GC` and is in the right place, so the symptom is an asteroid that flickers in from odd angles, not one that is misplaced. From an **eye-level** camera that band sits a fixed angle below the horizon, so looking down brings every asteroid in the map into frustum at once and looking up removes them all. `Bounds.Encapsulate` with the patch's own world bounds fixes it without ever shrinking anything. | `map-model.md` |
| Unlocking something with **no player action** cannot be done by writing `_CachedUnlockedMechanics`: `RecomputeUnlocks` clears and rebuilds it from unlocked upgrades' rewards. Use `ResearchUnlockProgressManager.TryUnlockInternal` (internal, needs the publicizer, needs a live session) - and note `_ManuallyUnlockedUpgrades` is **serialized**, so it is save state. | `howto/add-research-unlock.md` |
| A building's `MainMeshPerLayer` is built to a **hardcoded length of 3** (`BuildingDrawDataFactory.FromMeta`) and indexed by the building's layer in `StaticBuildingMeshBuilder` - so a machine above layer 2 throws inside the *chunk's* mesh build and everything in that chunk disappears. It is the only place the array is indexed, so widening it is the whole fix. | `rendering.md` |
| **Ports between platforms exist only on building layers 0-2**, and it cannot be changed. `SpacePathPortSystem.CreateAllSenderSimulations` (and its receiver twin) build the *runtime transport* over a hardcoded `for (num2 = 0; num2 < 3; …)`, and the class is `SpacePathPortSystem<TInput, TOutput>` - a **generic type**. `NotchConnectorsExtender.AddConnectors<TMapLayout>` has the placement-side bound in a generic *method*. A platform can have twenty floors; only the bottom three can export. Separately, a building layer is `SafeMod(tile.z, 20)`, so **19 is the last valid layer** - 20 aliases onto the island layer above. | `howto/add-research-unlock.md` |
| `BoundingBoxHelper.CreateBasicCollider` copies `mesh.bounds.center` (Unity, **Y-up**) straight into a `LocalVector` (game, **Z-up**), so a mesh's height lands on the north-south axis. Small enough to miss on a flat machine; puts the collider beside anything tall, and then the building cannot be clicked. | `howto/load-models-and-icons.md` |
| A mod can ship a **scenario**: `ModdedScenarios` reads `<mod>/scenarios/*.json` and `<mod>/scenario-presets/*.json`. But **do not start from `debug.export-game-data`** - the shipped asset is 1.7 KB of `#include:` references and the export is 144 KB of resolved ones, which does not load back: `ScenarioReader` gates on the literal substring `"FormatVersion": 3,` **with that space** (the export is minified), and the inlined `ToolbarConfig` blob's escaped `\"#include:...\"` lines make the include regex capture a trailing backslash. Copy the asset out of `resources.assets` instead. And an unresolvable `#include:` **stops the game booting** - it throws outside `TryReadingScenario`'s try block - so wrap the call for your own file. | `howto/custom-scenarios.md` |
| **Zoom is a drawing budget.** `IslandPlacementHelperHighlightShapeResources.Draw` - the only placement helper that sweeps the whole map, and so the only one that makes *miners* slow while every other island is fine - guards its cost three ways, all camera-derived: `Zoom > 4000` skips it, `InOverviewMode` (`Zoom > 1500`) cuts ten indicator planes per chunk to one, and `CameraPlanes` culls. A mod reporting a small zoom from a level camera removes all three at once. Swap `CameraPlanes` for a box around the player around the call, rather than reimplementing the helper. | `camera-and-viewport.md` |
| The scenario picker has **no scroll view**: `HUDMenuSelectScenarioState` drops cards straight into a `RectTransform` with a layout group. Seven fit; an eighth, which is what a mod that adds a scenario creates, does not. | `howto/custom-scenarios.md` |
| A research shop **preview image is 1024 x 709**, not square, and `HUDResearchSideUpgradeDisplay` assigns it to an `Image` with `preserveAspect` off - so a square sprite renders 1.44x too wide and looks like a rendering bug. Read the size out of `resources.assets`: a `Sprite`'s `m_Rect` is four floats after its 4-aligned name. | `howto/add-research-unlock.md` |
| To make the path placer able to choose a new belt/lift/merger, **register it into the `BuildingDefinitionGroup`** (`AddInternalVariant`) - not by hooking `DefinitionsFinderUtils.BeltPathDefinitionsFinder`. That extension method hooks cleanly and is a **dead seam**: a trace decorator showed it firing hundreds of times a session with **zero** `TryMatchingEntityFromInputsAndOutputs` calls ever reaching the finder it returned, so the live placer builds its finder elsewhere. Group registration reaches every consumer at once. Snapshot `group.Definitions` before looping - `AddInternalVariant` appends to that same list, so using its `Count` as the bound hangs on load. An internal variant is **not** a toolbar entry; the stock lift already has eight nobody cycles. | — |
| A lift's connectors must be moved **by role, not by height**: every variant has its input at z 0, and the **output**'s sign is what makes it go up or down (`out (0,0,2)` vs `out (0,0,-2)`), with the four Down variants listed *first*. "Move the highest-z connector" silently corrupts every Down variant by moving the input instead. Authored data - dump it at runtime rather than guessing. | — |
| **`CameraController.OnGameUpdate` stops being called when a session ends** - `PlayerInteractionOrchestrator` calls it, and leaving for the main menu takes the player interaction with it. A mod that cleans up inside that hook never cleans up: nothing throws, nothing is logged, and its UI floats over the menu. Put the stand-down on a tick postfixed onto `GameSessionOrchestrator.Tick`, which keeps running for the menu's background game. | `camera-and-viewport.md` |
| A drawn wagon's orientation is **readable off its matrix**, so upside-down rails need no access to navigation state: `TrainsDrawer.CalculateWagonTransform` builds `Quaternion.Euler(roll + lean, yaw, pitch)`, where **`pitch` is the Z euler despite the name** and is 180 on an inverted rail - so column 1 of the matrix `DrawHooks.OnDrawTrain` hands over is the wagon's own up. Normalise it: a lift solver writes `scale` by reference. | — |
| `SuperChunksDrawer.Draw` - which draws **every map resource** - opens by asking `ScreenUtils.TryGetChunkCoordinate` what the *screen centre* is over and **returns outright** if the answer is none. That is the flat-plane intersection, so an eye-level camera aimed above the horizon draws no asteroids at all, anywhere. The coordinate is only a flood-fill seed, so answering with the player's own chunk is both a fix and more correct. Separately, the per-resource bounds it culls against are **cached per resource for the drawer's life** (`GetResourceBounds`), so hooking `ComputeResourceSourceBounds` to depend on camera position freezes the first answer forever. | `camera-and-viewport.md` |
| A simulation renderer is bound to its buildings by **simulation type** and nothing else, so giving one building of a family its own simulation makes it **stop drawing, silently**. `SimulationsDrawer` keys on `(LocalizedSimulationType, SimulationType)` and never dispatches a simulation matching no key - from its side nothing is wrong, so nothing is logged. The tell is that the **placement preview still draws**, since a preview comes off the mesh and never touches the simulation. `DynamicallyRendering`'s `TRenderer` is not the binding and ignoring it is not the bug. | `rendering.md` |
| The red **conflict cross** on a connector cannot be switched off through the builder chain, and two similarly named methods make it look like it can: `NotRenderingConnectorConflictIndicator()` sets a different field, and `NotRenderingConflictingIndicatorVisualization()` sets the right one but is still dead, because **ShapezShifter never calls `BuildingDefinitionFactory`** - the only thing that turns either field into `SkipConflictingConnectorsDrawingFlag`. Attach the flag to the definition `BuildAndRegister` returns, guarded with `Has<>` because it re-runs per scenario load. | `howto/add-a-building.md` |

## Working style the user expects

- Say what is verified versus what is untested. Nothing here can be runtime-tested by the
  agent — the user tests. Be explicit about which parts have only been compiled.
- When a fix is guessed, say so and name the discriminating observation that would confirm it.
- Correct your own earlier wrong claims plainly when evidence contradicts them.
- Prefer finishing one thing properly over starting five.
