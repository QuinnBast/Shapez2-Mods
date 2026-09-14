# Traps that compile cleanly

Every entry here is a real failure that got through a code review, cost a working session,
and was then traced to a specific class or method. None of them produce a compiler error.

Scan the section matching what you just wrote before calling the work done.

## Content and definitions

**`WithBoundingCollider()` builds a zero-sized box.** It sizes one box as `(max - min) * 20`,
one chunk short on every axis — and since every island is a single layer deep,
`min.z == max.z`, so the height is always zero. The island cannot be hovered, selected,
pipetted or deleted, while everything vanilla around it behaves normally. Always use
`WithPerChunkColliders()`. **The official `BiggerPlatforms` and `SandboxIslands` samples
both call `WithBoundingCollider()`** — do not read that as evidence it works.
→ `docs/howto/add-an-island.md`

**Never detach `IslandFrameDrawData`.** Vanilla attaches it to every island unconditionally,
space belts included, just with an all-false context.
`IslandChunkPlatformFramesCache.RegisterIsland` early-returns for an island that lacks it,
so its chunks never enter the cache — while `IslandFramesDrawer.Draw` calls `GetEntry` for
every culled chunk with no guard. The result is a `KeyNotFoundException` every frame
forever, which also aborts `MapDrawer.Draw` partway and silently kills whatever draws after
it. Pass a zeroed drawing context instead.
→ `docs/howto/add-an-island.md`

**The pipette map is a `Dictionary.Add`.** `.WithDefaultPlacement()` already claimed the
island, so registering a second placer over the same definitions throws
`An item with the same key has already been added` inside
`PlayerInteractionOrchestrator`'s constructor — **before the main menu appears**. Hand your
own placer a throwaway dictionary rather than the real `IslandInitiatorsParams.PipetteMap`.
→ `docs/howto/add-an-island.md`

**Flipping with F is group membership, not a flag.** `WithInteraction(flippable: true, …)`
alone does nothing. `IslandPlacersCreator` decides by *counting* the group's
`IslandGroupCollection`: one definition gets a non-flippable placer, two get the flippable
pair, three or more throws.
→ `docs/howto/add-an-island.md`

**Most `IslandGroup.Create(...)` options are never read.**
`IslandGroupBuilder.BuildAndRegister` attaches only `GroupPresentationData`, so
`AsNonTransportableIsland`, `WithPreferredPlacement`, `Removable`, `AutoConnected` and
`AllowedOnNotches` have no effect. Attach what you need to the definition yourself.

**`Attach` where `AttachOrReplace` was meant.** The fluent chain runs once, but
`BuildAndRegister` runs again for every scenario load against the same definition objects.
Plain `Attach` adds, so a second copy sits beside the first — and `CustomDataHolder` reports
a multiple match as found-nothing. The symptom is content that works on a new save and
silently stops working on a loaded one.

**`ShapesConfiguration.Parts` is a `List<MetaShapeSubPart>` wearing
`IReadOnlyList<IShapeSubPart>`.** Covariance makes that legal, so `is List<IShapeSubPart>`
is **false**. Cast to the non-generic `IList`.
→ `docs/howto/add-a-shape-part.md`

**Shape sub-part meshes carry their material assignment in vertex colours**, and
ShapezShifter's model importer drops that channel — so a shape part mesh cannot be loaded
from a file. A part mesh is also authored for exactly `360 / PartCount` degrees with
nothing rescaling it angularly, so a quadrant mesh injected into the 6-part hexagonal
configuration overlaps its neighbours. Vanilla ships a separate asset per configuration.
→ `docs/howto/add-a-shape-part.md`

## Toolbar, research and UI

**Toolbar indexing is asymmetric.** `FindElementParent` filters `ToolbarSlotSeparator` out
before indexing, but `ToolbarEntryLocation`'s `Before`/`After` compute the final index from
`parent.Children.Count()`, which **counts** separators. A numeric leaf index lands one slot
later for every separator ahead of it. Count with separators excluded for every hop *except*
the last, and prefer `ChildAt(^1).InsertAfter()` for the leaf — it appends correctly either
way and survives game updates. Nothing looks broken when this is wrong; the entry appears,
in the wrong category.
→ `docs/howto/add-to-toolbar.md`

**Toolbar ids end in `.title`** (`island-toolbar.category-Rail.title`, not
`…category-Rail`), and the casing is inconsistent — `category-RegularPlatform` but
`category-converters`.
→ `docs/howto/add-to-toolbar.md`

**A research cost is hundreds of displayed points.** `Format(this ResearchPointCurrency)`
renders `Amount * 100`, so `48` is the "4.8k" on screen. Getting it backwards prices a node
a hundredfold too high and still looks plausible.
→ `docs/howto/add-research-unlock.md`

**`UnlockedWithNewSideUpgrade` registers its node once per island/building group**, and
`CustomSideUpgradeSelector.Select` is itself a call to `Build` — so one node shared by ten
islands becomes ten shop entries. Use a get-or-create `ISideUpgradeSelector`. A side
upgrade's **preview image is not optional** either: `GetImage` throws on the empty id and
takes the research screen with it.
→ `docs/howto/add-research-unlock.md`

**A wiki page is two halves in two objects** — the reference in
`ResearchProgression.WikiConfiguration`, the entry in `GameData`. A reference whose entry is
missing throws out of `WikiDatabase`'s constructor and takes the session. A
`MetaWikiEntry`'s id is its `name`, and its title key is fixed at `wiki.<id>.title`.
→ `docs/howto/add-a-wiki-entry.md`

**No stock side-panel module renders an arbitrary live number** — text is set once.
`HUDSidePanelModuleRocketProgress` is the generic current/max bar, despite the name.
→ `docs/howto/island-side-panel.md`

**An IMGUI overlay does not block the game.** The wheel still zooms behind it. Consume the
input context in a `HUD.OnGameUpdate` postfix; uGUI clicks need a raycast blocker as well.
→ `docs/howto/notifications-and-hud-screens.md`

## Translations

**`translations.json` is keyed by language code first**, then flat key → text. Getting that
nesting backwards aborts mod load. So do placeholders written any way other than
self-closing (`<layer/>`).
→ `docs/howto/add-translations.md`

**`Bind("x", n.ToString().T())` renders `?1`.** `.T()` means *translation id*, so this asks
the resolver for a key named `"1"`. Use `RawText`. **A `?` prefix anywhere in the UI means a
failed lookup.**
→ `docs/howto/add-translations.md`

## Hooking

**MonoMod cannot hook a method on a generic type** — struct instantiations included.
`Foo<ShapeId>.Bar()` is rejected exactly like `Foo<T>.Bar()`, because
`Hook.CheckSupported()` looks at the declaring type, not the instantiation. It compiles,
then throws at construction and kills startup. Relocate to a non-generic choke point the
calls already pass through.
→ `docs/hooking.md`

**`CreatePrefixHook` only works on void-returning methods.** Every overload builds an
`Action` internally, and C# will silently convert a lambda whose body *calls* a non-void
method into an `Action`, discarding the return value. Use a raw `Hook` with hand-written
delegate types.
→ `docs/hooking.md`

**Chain delegates, never replace them.** Whenever you take over a delegate the game owns — a
lane's `AcceptHook`, a `DrawHooks` delegate, an event — save the previous value and call it.
Replacing outright is the most common way to break a machine while your own code looks
correct.

## Lifecycle and state

**A throw in a mod constructor is not contained by `ModLoader`.** It comes out through
`ModLoadingStep.LoadMods` and kills the game's entire mod loading step. Static field
initialisers running in declaration order are an easy way to cause one.

**`SyncableIdentifier` is read with `inherit: false`**, so every saved state needs its own
concrete type and id.

**Renaming a namespace orphans shipped save data.**
`ModSaveDataExtensions.ResolveId<T>()` is `AssemblyName + "-" + typeof(T).FullName`.
`[SyncableIdentifier]` is an explicit string and is unaffected.
→ `docs/howto/save-data.md`

**`ModDirectoryLocator` throws on hot reload** — a byte-loaded assembly has an empty
`Location`.
→ `docs/howto/load-models-and-icons.md`

**`AffectsSaveGames: true` means the mod cannot be added to or removed from an existing
save.**

**`.WithPrediction(...)` on an island chain deletes your mod for anyone with predictions
switched off.** `AtomicIslandExtender.Build` re-arms its chain only once `WaitAllRewirers`
sees *every* branch clear its link, and the prediction branch runs from a postfix on
`BuiltinPredictionSimulationSystems.CreateSimulationSystems` — whose sole caller,
`GameSessionOrchestrator.SetupPredictions`, is skipped when the game setting
`SimulationSettings.Predict` (`"prediction"`, default on) is false. The chain is then spent
on the first scenario of the process, which is the **main menu's background game**, and the
player's own save gets no definitions, no toolbar entry and no unlock — with no error
anywhere. Register prediction by hand instead, re-armed per scenario load. Signature in a
user's log: `IslandPredictionExtender` added and never removed.
→ `docs/howto/add-an-island.md`

## Profiling and reflection

**Simulation does not run on the main thread.** `Simulator.StartAsynchronousUpdate` returns
a `Task`, so a mod's registered simulations run on pool threads. Anything measuring or
hooking per-thread state will see nothing on the main thread.
→ `docs/howto/profile-a-mod.md`

**The runtime's managed heap walk cannot be called from a mod.** `mono_unity_liveness_*`
reports through a callback, a callback into a mod is managed code, and a stopped GC world
forbids entering managed code — the game hangs with no log line. (`mono_gc_walk_heap` is
separately a Boehm stub returning 1.) Walk the graph with reflection instead.
→ `docs/howto/profile-a-mod.md`

**Reflecting over an unknown heap has two crash windows.** `GetValue` on a **pointer field**
boxes a new `System.Reflection.Pointer` whose own field is a pointer, so a walk that follows
them never ends. Reading a **thread-static or pointer static** crashes natively inside
`GetValueInternal` — a crash, not an exception. Exclude both *before* the call, never after.
→ `docs/howto/profile-a-mod.md`

## Build and install

**`MSB3021` / `MSB3027 … user-mapped section open` is the copy failing, not the compile.**
The running game holds the installed DLL memory-mapped. Close it, or redirect `OutputPath`.

**The publicizer warning `no members were publicized` appears on builds where it worked.**
Not a diagnosis. `FieldAccessException` at *runtime* after a clean compile is the real
failure — usually a game `<Reference>` missing `<Private>False</Private>`, shadowing the
real assembly with a publicized copy.
