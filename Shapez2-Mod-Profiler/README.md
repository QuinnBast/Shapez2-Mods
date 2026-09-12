# Mod Profiler

A development tool: where a mod's frames and memory actually go, measured from inside the
running game.

- **Flame graph** — a call tree for one mod's assembly, woven in on demand and removed again.
- **Managed object census** — every C# object a loaded mod is holding, grouped by the assembly
  that declares its type. This is the one that says how much of the heap is *yours*.
- **Unity object census** — every live `UnityEngine.Object` with its real size.
- **Counters and frame graph** — everything a release player feeds for free.

Open it from the tinted button beside Statistics, or drive it from the debug console (**F1**).

## Why the collector had to be ours

No off-the-shelf profiler can attach to this process. Not for want of effort: every agent
worth having attaches through an interface this player does not expose.

| Tool | Attaches via | Why not here |
| --- | --- | --- |
| Pyroscope, Datadog, dotTrace (attach) | CLR Profiling API (`ICorProfilerCallback`) | Mono does not implement it |
| `dotnet-trace`, `dotnet-counters` | EventPipe diagnostics server | Unity's Mono fork does not ship it |
| Mono log profiler (`--profile=log:`) | native profiler module | `MonoBleedingEdge/EmbedRuntime/` carries only `mono-2.0-bdwgc.dll` and `MonoPosixHelper.dll` |
| Unity Profiler | player connection | `boot.config` has no debug markers — this is a release build, and the profiler is compiled out of one |

So the collector lives in a mod. What it can collect is decided entirely by which APIs the
runtime actually implements — and metadata cannot answer that. A method can be present and
throw; a counter can exist and return a constant zero. Hence `prof.probe`: call them and look.

## Using it

| | |
|---|---|
| `prof.probe` | which profiling APIs this runtime implements |
| `prof.counters` | every `ProfilerRecorder` counter the player exposes, by category |
| `prof.native` | which Mono runtime symbols resolve — resolves only, calls nothing |
| `prof.managed` | what the loaded mods are holding, by assembly and by type |
| `prof.record <mod>` | weave enter/exit hooks into one mod's assembly and start recording |
| `prof.stop` | stop, and remove the weave |
| `prof.watch` / `prof.live` | open the live counters, then read them |
| `prof.copy` | put the last report on the clipboard |

`prof.counters` runs to hundreds of lines, which no in-game console is readable at — that is
what `prof.copy` is for. Everything is mirrored into `Player.log` as well.

## The two halves of the heap

**`UnityEngine.Object`s** come from `Resources.FindObjectsOfTypeAll` plus
`Profiler.GetRuntimeMemorySizeLong`. Complete, and almost entirely the game's.

**Everything else** — your classes, their lists and dictionaries, strings, boxed structs — is
invisible to every managed API. `GC.GetAllocatedBytesForCurrentThread` is frozen at zero here,
and `mono_gc_walk_heap` is a stub on Boehm that walks nothing.

The runtime *does* export the heap walk Unity wrote for its own memory profiler, and it cannot
be called from a mod. That was tried, and it hung the game:
`mono_unity_liveness_calculation_from_statics` reports objects through a callback, a callback
into a mod is managed code, and re-entering managed code is exactly what a stopped GC world
forbids. Unity gets away with it because their callback is C++. There is no version of that
design that works from here, so it is gone rather than guarded — the symbols are still listed
by `prof.native`, which resolves and calls nothing.

What the census does instead is walk the graph with reflection — rooted at every loaded
assembly's static fields *and* at every live `UnityEngine.Object`, because a mod's objects are
mostly held by somebody else: Shifter holds the `IMod`, the simulation holds what the mod
registered, and Unity holds MonoBehaviours natively. Attribution is by the assembly that
declares the type, not by where the walk started, and mod assemblies sort to the top of the
table.

It runs **six milliseconds a frame** from the panel's Scan button, with progress and a Cancel,
so the game keeps running through it; `prof.managed` runs the same walk to completion with a
four second ceiling, because the console has no frames to spread it over. Counts are exact;
**sizes are estimated from field layout**, which is the price of not stopping the world to
ask.

## What the flame graph cannot see

- **Methods on generic types, and generic methods.** `ILHook` refuses them, and the check is on
  the declaring type, so `Foo<ShapeId>.Bar()` is as unhookable as `Foo<T>.Bar()`. The count of
  skipped methods is reported rather than hidden.
- **Inlined calls.** Mono's JIT inlines small methods, and a body rewritten after a caller has
  inlined it does not affect that caller's copy. Tiny hot helpers under-report.
- **The game.** Only the named assembly is woven. Sampling the game's own stacks needs the Mono
  profiler API — `mono_profiler_enable_sampling` is exported, which is the shape of one, and
  nobody has called it yet.

Every thread *is* recorded, which matters more than it sounds: `Simulator.StartAsynchronousUpdate`
returns a `Task`, so a mod's simulations run on pool threads. Recording only the main thread
turned a 200 second capture into six methods and 0.1 ms. The per-thread split is in the stop
message and the export.

## What this build is known to be

Read off the install before any of it runs:

- **Unity 6000.3.19f1, Mono, not IL2CPP.** No `GameAssembly.dll`;
  `MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll` is the runtime. Runtime detours,
  reflection over method bodies and the whole embedding API work, which is the only reason a
  mod-side profiler is possible at all.
- **A release player.** `Profiler.BeginSample` and `EndSample` are
  `[Conditional("ENABLE_PROFILER")]`, so they compile to nothing here. `ProfilerRecorder` is
  the documented exception and does work.
- **Boehm GC** (`bdwgc`), non-generational and non-moving. Generation-by-generation collection
  counts are exposed but mean little here.

## The page

Three tabs: **Overview** (frame graph and the player's own counters), **Memory** (GC, the
managed census, the Unity census), **CPU** (pick a mod, Record, Stop, then the flame graph and
a list of the hottest methods by self time). Recording no longer needs the console — the
picker lists the mods actually loaded.

## Export

**Export** in the header writes `modprofiler-<stamp>.txt` beside `Player.log` and copies the
folder path to the clipboard. It holds everything on the page: counters with min, mean and max,
the collector, both censuses, the per-thread split and the whole call tree, indented. Readable
as it stands and diffable between builds.

## The panel is modal

It stops the game beneath it: input is consumed after the HUD parts run, so the console still
works but the camera and placement get an idle frame, and a transparent full-screen graphic
swallows clicks aimed at the toolbar. Escape closes it. The mechanism is
`context.ConsumeAll()`, which is what the game's own screens do — see
[Notifications and HUD screens](../shapez2-modding-docs/docs/howto/notifications-and-hud-screens.md#make-your-own-overlay-modal).

The page itself is IMGUI rather than a `HUDPart`. It will not look like the game's UI, and
that is the trade: none of the prefab and dependency-injection machinery applies to it, which
is where the hours went on the last two HUD additions. For a page whose job is dense numbers,
that machinery buys nothing back.

## Building

Needs `SPZ2_PATH`, `SPZ2_PERSISTENT` and `SPZ2_SHIFTER`, like the others.

```bash
dotnet build                 # installs into <persistent>/mods/ModProfiler   (game must be CLOSED)
dotnet build -p:Dev=true     # stages into <persistent>/mods-dev/ModProfiler (safe while running)
```

`mrl.reload modprofiler` picks up a staged build, but **the panel needs a restart**: Unity will
not add a MonoBehaviour whose type came from a byte-loaded assembly, so a reloaded generation
has commands and no page.
