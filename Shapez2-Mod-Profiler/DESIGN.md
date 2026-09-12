# Mod Profiler - design notes

A development tool: where a mod's frames and memory go, measured from inside the running game.
What follows is what was read off the assemblies, the runtime and two crash logs rather than
assumed, so none of it has to be re-derived.

## Status: working, 2026-09-11

Three tabs, modal, driven from the panel rather than the console:

- **Overview** - the player's own counters, with now/min/mean/max, and a frame graph.
- **Memory** - the collector's numbers, the managed object census, the Unity object census.
- **CPU** - pick a mod, Record, Stop; flame graph with a hot-methods table under it.

Export writes one text report beside `Player.log` and copies the folder to the clipboard.

Three things had to be rebuilt along the way, each because the first design was wrong in a way
that only the running game could show: the heap walk (hung the game), the heap walk again
(crashed it), and the call recorder (recorded almost nothing). All three are written up below.

## What this build is

Read off the install before any of it runs:

- **Unity 6000.3.19f1, Mono, not IL2CPP.** No `GameAssembly.dll`;
  `MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll` is the runtime. Detours, IL rewriting and
  the whole Mono embedding API are therefore available, which is the only reason a mod-side
  profiler is possible.
- **A release player.** `Profiler.BeginSample` is `[Conditional("ENABLE_PROFILER")]` and
  compiles to nothing. `boot.config` carries no player-connection markers, so Unity's own
  profiler cannot attach either.
- **Boehm GC** (`bdwgc`): non-generational, and **non-moving**, which is why raw object
  pointers stay valid across a walk.

No off-the-shelf profiler attaches to this process: Pyroscope/Datadog/dotTrace want the CLR
profiling API, which Mono does not implement; `dotnet-trace` wants EventPipe, which Unity's fork
does not ship; Mono's log profiler is a native module this player does not carry. The collector
had to be ours. The **UI** did not have to be, and the export exists for that reason.

## Verified facts

### Simulation does not run on the main thread

`GameSessionOrchestrator.StartLogicUpdate` calls `Simulator.StartAsynchronousUpdate`, which
returns a `Task`. Belts, machines and **every simulation a mod registers** run on pool threads.

The first recorder kept one stack and one tree, guarded by "only the thread that started the
capture". A 200 second recording of Train Cargo Tools came back with six methods and 0.1 ms,
because all of its work happened on threads that were being thrown away.

`CallRecorder` now keeps a `[ThreadStatic]` stack and tree per thread, takes a lock only when a
thread first appears, and merges the trees in `Stop()`. `CallRecorder.Threads` keeps the
per-thread split, which is printed on stop - it is the line that says whether the recorder was
looking in the right place.

### The runtime's own heap walk cannot be called from a mod

`mono-2.0-bdwgc.dll` exports its whole embedding API - 1238 symbols - including
`mono_unity_liveness_calculation_from_statics`, the walk Unity's memory profiler is built on.
The signatures are in Unity's fork (`mono/metadata/unity-liveness.c`, `unity-utils.c`),
byte-identical on `unity-main` and the 6000.3 branches.

It still cannot be used. The walk reports objects **through a callback**, and it has to run with
the GC world stopped (`mono_unity_stop_gc_world`: loader lock, domain lock,
`GC_stop_world_external`). A callback into a mod is managed code, and a stopped world is exactly
the state in which the runtime refuses to let a thread enter managed code. It does not throw:
**the game hangs**, with no log line, and has to be killed. Unity gets away with it because
their callback is C++ and never re-enters the runtime.

There is a second, independent reason: between stop and restart, other threads are suspended
wherever they were, including inside `malloc`. Any allocation on the walking thread - managed or
unmanaged - can deadlock against a suspended lock. The `ReallocateArray` callback exists so the
caller can supply memory without allocating, which says how narrow the path is even from C++.

`mono_gc_walk_heap`, the documented Mono heap walker, is separately a **stub on Boehm**: it
returns 1 and walks nothing (`mono/metadata/boehm-gc.c`).

What survives from all this is `MonoRuntime`: bind by `GetModuleHandle` + `GetProcAddress` (the
module is already mapped, and resolving a symbol calls nothing, so `prof.native` can report what
a build has without risking it), and read `mono_gc_get_heap_size` / `mono_gc_get_used_size`.

### Reflection over an unknown heap has two ways to kill the process

Both found the hard way, both invisible to `try`/`catch`.

**Pointer fields generate objects.** `FieldInfo.GetValue` on a `void*` field boxes the value into
a `System.Reflection.Pointer` - an object the scan just created - whose own `_ptr` field is a
pointer, so reading that boxes another. A walk that follows them never ends. The first census
that produced a table reported **212,478 `System.Reflection.Pointer`, 6.5 MiB**, every one of
them manufactured by the scan.

**Some static reads crash natively.** A thread-static whose per-thread storage was never
allocated, or a pointer static, takes the process down inside
`RuntimeFieldInfo.GetValueInternal` - a Unity crash window, no exception. The crash log names
the line and not the field, which is why `ManagedCensus` writes a breadcrumb file during the
static phase and deletes it on a clean finish: the next scan skips the type the last one died in
and says so.

Unity's own walker makes the same judgements from C: it skips corlib entirely and skips any
field at offset -1, its "shortcut check for special statics".

So `ManagedCensus.Readable` excludes **before** the call: pointer and by-ref fields, ref structs
(`IsByRefLikeAttribute` by name, because `Type.IsByRefLike` may not exist in this class library),
`[ThreadStatic]` fields, and the statics of mscorlib, `System.*`, `UnityEngine.*` and
Burst/Collections/Jobs.

### A mod's objects are held by everybody except the mod

Rooting the census at the mod assemblies' own statics was correct and useless: it found a few
mscorlib arrays and nothing else. Shapez Shifter holds every `IMod` in an instance field; the
game's simulation holds the simulation objects a mod registered; Unity holds MonoBehaviours
natively, the managed side reaching them only through GC handles.

So the walk roots at **every loaded assembly's statics and every live `UnityEngine.Object`**
(`Resources.FindObjectsOfTypeAll`, the only way to reach what Unity owns) and attributes what it
finds by `type.Assembly` rather than by where the path started. A mod assembly is one that
declares a concrete `IMod` - the loader's own test, and no blocklist to keep current.

That reaches most of the heap, which is millions of objects, which is minutes of reflection - so
`Step(milliseconds)` runs a slice per frame from `DebugPanel.Update`, with progress and a
Cancel, and the visited set is dropped the moment the walk ends because it is a strong reference
to everything it saw.

### The counters a release player still feeds

`ProfilerRecorder` is the documented exception to everything else being compiled out, and it
works. Of 231 counters, **33 carry a value**; the rest are almost all `Scripts /` markers, which
need `ENABLE_PROFILER`. Between them the live ones cover frame timing, memory and rendering.

What they cannot do is attribute. They say the main thread took 7 ms; nothing in them says which
mod spent it.

**There is no allocation counter.** `GC.GetAllocatedBytesForCurrentThread` is frozen at zero,
and neither `GC Allocated In Frame` nor `Scripts / GC.Alloc` is fed. Heap growth is therefore
measured from the sawtooth between collections, and is process-wide.

### An IMGUI overlay does not block the game

Drawing over the game changes nothing about its input: `GameSessionOrchestrator.Update` fills one
`InputDownstreamContext` per frame and walks it through `DialogStack`, `HUD.OnGameUpdate`,
`PlayerInteractionOrchestrator` (which drives `CameraController`), `SystemButtons`.

The game's own screens block by consuming - `HUDStatistics.OnGameUpdate` ends with
`ConsumeToken("HUDPart$confine_cursor")` and `ConsumeAll()`, which clears the active bindings,
the mouse delta and the wheel delta. `PanelInput` does the same from a **postfix** on
`HUD.OnGameUpdate`: after the HUD parts, so the debug console still works, and before the
interaction orchestrator, so camera and placement get an idle frame.

Escape has to be a **prefix**. The pause menu is a HUD part, so a cancel still unconsumed when
the parts run opens the pause menu behind the panel.

uGUI is a third system again: the toolbar and top bar are routed by an EventSystem raycast that
knows nothing about either the input context or IMGUI. `DebugPanel.SyncBlocker` raises a
transparent full-screen `Image` on a canvas at sorting order 30000 to swallow those clicks.

### The panel cannot survive a hot reload

`DebugPanel` is a MonoBehaviour, and Unity will not `AddComponent` a type from a byte-loaded
assembly - `AddComponent` returns null. So `Attach` is allowed to return null, the commands
register first and unconditionally, and a reloaded generation runs with commands and no page.
Anything that changes the panel needs a restart, not `mrl.reload`.

## The call recorder

`Instrumenter` rewrites method bodies with `ILHook` rather than wrapping them with `Hook`: an
`ILHook` edit is signature-agnostic, so it works on a few thousand unknown methods.

Four things about it are load-bearing:

- **`MoveType.Before` on each `ret`** retargets incoming labels onto the emitted instructions, so
  a branch to the return still closes the frame. `cursor.Index += 1` afterwards, and no further -
  almost every method's last instruction *is* a `ret`, and stepping past it throws.
- **Methods on generic types are refused**, and the check is on the declaring type, so
  `Foo<ShapeId>.Bar()` is as unhookable as `Foo<T>.Bar()`. The counts are reported, not hidden.
- **The names outlive the hooks.** Removing the weave on stop is right; clearing the id-to-name
  table at the same time is not, because the tree is read *after* the stop. Doing both rendered
  every frame in the graph as "(root)".
- **The depth cap must not corrupt the tree.** A dropped entry still runs its exit, and an exit
  that goes looking down the stack for its own method id finds the *ancestor* that caused the
  recursion and closes a frame it never opened. Drops are counted and spent first, and the count
  resets when the stack empties - the one moment the books are known to balance after an
  exception. The cap is 1024; at 256 a 37 second capture overflowed 158,772 times.

Inlined calls under-report: Mono's JIT inlines small methods, and a body rewritten after a caller
inlined it does not affect that caller's copy.

## Known rough edges

- Merging the per-thread trees at `Stop` can race a thread still inside an instrumented call. The
  window is microseconds and a straggler costs that thread's data rather than the capture, but it
  is not airtight.
- Census sizes are **estimates** from field layout (16-byte header, 8 per reference, real array
  and string lengths). Counts are exact.
- Reading a static through reflection runs that type's initializer if it has not run. There is no
  "is this initialised" in the managed API.
- The weave's own cost - two timestamp reads and a dictionary lookup per call - is not measured,
  so a very hot method's reported time includes an unknown amount of profiler.

## Open questions

- **Diffing two heap scans.** Scan, play, scan, sort by delta. The single most useful thing not
  yet built, and everything needed is already in `ManagedCensus`.
- **Overhead calibration.** Time an empty instrumented method at Record time and report
  `calls × cost` as a share of the capture.
- **The Mono sampling profiler.** `mono_profiler_create`, `mono_profiler_enable_sampling` and
  `mono_profiler_set_sample_hit_callback` are all exported, and it would cover the game itself
  and the frames inlining hides. It is very likely the same trap as the liveness walk: the sample
  hit callback runs with the sampled thread suspended, and anything a mod can pass is managed
  code. If it is ever tried, it belongs behind a console command nobody runs by accident.
- `mono_profiler_enable_allocations` is exported but Mono refuses it after runtime startup, so
  per-call allocation attribution is almost certainly off the table from inside a mod.
