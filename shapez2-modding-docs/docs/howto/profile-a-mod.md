# Profile a mod

**Problem.** Your mod is costing frames or leaking memory and you want numbers, not
guesses. Nothing can attach from outside: the player is Unity Mono with no CLR profiling
API, no EventPipe, no profiler module in `MonoBleedingEdge/EmbedRuntime/`, and
`Profiler.BeginSample` is `[Conditional("ENABLE_PROFILER")]` so it compiles to nothing in
a release build.

**Solution.** Collect from inside the process. Three layers are available, and the useful
one is not the one you would expect.

| Layer | Gives you | Cost |
| --- | --- | --- |
| `ProfilerRecorder` counters | frame time, draw calls, memory totals | free, works in a release player |
| IL-rewritten enter/exit hooks | a per-method call tree for one assembly | two timestamps per call |
| The Mono runtime's own C API | collector totals; **not** the heap walk — it hangs | a P/Invoke |
| Reflection over static roots | what one mod retains, by type | a budgeted hitch |

## The managed heap is reachable, and the managed API is a red herring

`Resources.FindObjectsOfTypeAll` plus `Profiler.GetRuntimeMemorySizeLong` gives a complete
census of `UnityEngine.Object`s — textures, meshes, components. That is the engine's half of
the heap, and on a modded game it is almost entirely the game's.

Your own memory is the other half: your classes, the `List`s and `Dictionary`s they hold,
strings, boxed structs. No managed API exposes those. `GC.GetTotalMemory` is one number for
the process, `GC.GetAllocatedBytesForCurrentThread` is frozen at zero in this build, and
`mono_gc_walk_heap` — the documented Mono heap walker — is a stub on Boehm that returns `1`
without walking anything (`mono/metadata/boehm-gc.c`).

The runtime's *other* heap walk exists and is exported, and it still cannot be called from a
mod — see [below](#the-runtimes-heap-walk-cannot-be-called-from-a-mod). What works is walking
the graph yourself.

What *is* there: **the runtime exports its whole embedding API**, 1238 symbols out of
`mono-2.0-bdwgc.dll`. Not all of them are usable from a mod — the heap walk is the cautionary
tale — but the collector's own numbers are, and knowing what a build exports is the start of
every answer here. Read the export table rather than trusting any of this:

```bash
# any PE export dump works; this is the list that matters
mono_unity_liveness_allocate_struct
mono_unity_liveness_calculation_from_statics
mono_unity_liveness_finalize
mono_unity_liveness_free_struct
mono_unity_stop_gc_world
mono_unity_start_gc_world
mono_object_get_class      mono_object_get_size
mono_class_get_name        mono_class_get_image     mono_image_get_name
mono_gc_get_heap_size      mono_gc_get_used_size
mono_profiler_create       mono_profiler_enable_sampling
mono_profiler_set_sample_hit_callback
```

Bind them with `GetModuleHandle` + `GetProcAddress` rather than `DllImport`. The module is
already mapped, so you bind to the runtime that is *running*; and resolving a symbol calls
nothing, so you can report what a build has before you risk calling it:

```csharp
[DllImport("kernel32", EntryPoint = "GetModuleHandleA", ExactSpelling = true, CharSet = CharSet.Ansi)]
private static extern IntPtr GetModuleHandleA(string name);

[DllImport("kernel32", EntryPoint = "GetProcAddress", ExactSpelling = true, CharSet = CharSet.Ansi)]
private static extern IntPtr GetProcAddressA(IntPtr module, string name);

IntPtr mono = GetModuleHandleA("mono-2.0-bdwgc.dll");
IntPtr walk = GetProcAddressA(mono, "mono_unity_liveness_calculation_from_statics");
```

> [!WARNING]
> A wrong native signature corrupts the stack; it does not throw. Read the signatures off
> Unity's fork — github.com/Unity-Technologies/mono, `mono/metadata/unity-liveness.c` and
> `unity-utils.c` — on the branch matching the player's Unity version, which
> `UnityPlayer.dll`'s file version reports. The five-argument
> `mono_unity_liveness_allocate_struct` below is byte-identical on `unity-main` and the
> 6000.3 branches; older forks have a four-argument one with no reallocator, and calling the
> wrong shape is a crash, not an exception.

## The runtime's heap walk cannot be called from a mod

This is the part worth reading before you spend an evening on it, because the API is right
there and it looks like it should work.

Unity's fork adds a liveness walk for its own memory profiler, and the shipped runtime
exports it:

```c
LivenessState *mono_unity_liveness_allocate_struct (MonoClass *filter, guint max_count,
        register_object_callback callback, void *userdata, ReallocateArray realloc);
void mono_unity_liveness_calculation_from_statics (LivenessState *state);
```

It roots at the static fields of every loaded class whose image is not corlib, traverses,
and hands you every object it found — through a **callback**, in batches of 64. And it has
to run with the GC world stopped (`mono_unity_stop_gc_world`: loader lock, domain lock,
`GC_stop_world_external`), because the traversal marks bits in vtables and holds raw pointers
that nothing else roots.

> [!WARNING]
> **A callback into a mod is managed code, and a stopped world is precisely the state in
> which the runtime refuses to let a thread enter managed code.** It does not throw and it
> does not crash: the game hangs, with no log line, and the only way out is to kill it. There
> is no arrangement of pre-JIT-ing, pre-allocation or care that fixes this — the callback is
> the design, and it is the part that cannot run.
>
> Unity gets away with it because their callback is C++ and never re-enters the runtime.
> Nothing a mod can pass is C++.

Calling it also fails for a second, independent reason worth knowing: between
`GC_stop_world_external` and the restart, other threads are suspended wherever they happened
to be — including inside `malloc`. Any allocation on your thread, managed *or* unmanaged, can
deadlock against a suspended thread's lock. The `ReallocateArray` callback exists so the
caller can supply memory without allocating; that is a strong hint about how narrow the path
is even from C++.

So: read the Boehm totals (`mono_gc_get_heap_size`, `mono_gc_get_used_size` — plain reads, no
callback, no suspension) and walk the object graph yourself.

## Walking the heap with reflection

The roots the liveness walk uses are reachable from managed code, and so is everything they
point at. This is slower and its sizes are estimates, but it runs with the world running,
nothing suspended, and it can be stopped halfway.

**Root at everything, attribute by type.** The obvious design — root at the mod's own static
fields — produces a correct and useless answer, because that is not where a mod's objects
live. Shapez Shifter holds every `IMod` in an instance field. The game's simulation holds the
simulation objects a mod registered. Unity holds MonoBehaviours natively, and the managed side
reaches them only through GC handles, which no static walk touches at all. Every path to a
mod's memory starts outside the mod.

So take both root sets:

```csharp
// Unity's side: held natively, unreachable from any managed static.
foreach (UnityEngine.Object item in Resources.FindObjectsOfTypeAll<UnityEngine.Object>()) …

// The managed side: every static field of every type of every loaded assembly.
foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
    foreach (Type type in assembly.GetTypes())
        foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public
                                                   | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) …
```

and answer "whose is it?" per object with `type.Assembly`, not by where the walk started. A
mod assembly is one that declares a concrete `IMod` — the same test the loader uses, and it
needs no blocklist of the game's assemblies to keep current.

**Step it across frames.** That root set reaches most of the heap, which is millions of
objects, which is minutes of reflection — and doing it in one call is the freeze you were
trying to measure. A `Step(milliseconds)` called once a frame from a `MonoBehaviour.Update`,
six milliseconds at a time, finishes in a second or two of wall clock with the game still
running underneath, and can show progress and take a Cancel.

> [!WARNING]
> **Reflection over an unknown heap has two ways of killing the process, and neither throws.**
>
> **Pointer fields generate objects.** `GetValue` on a field of type `void*` boxes the value
> into a `System.Reflection.Pointer` — an object your scan just created, and one whose own
> `_ptr` field is a pointer, so reading *that* boxes another. A walk that follows them never
> ends. The first run that produced a table reported 212,478 `System.Reflection.Pointer`
> objects taking 6.5 MiB, every one of them manufactured by the scan.
>
> **Some static reads crash natively.** A thread-static whose per-thread storage was never
> allocated, or a pointer static, takes the process down inside
> `RuntimeFieldInfo.GetValueInternal` — a Unity crash window, no exception, nothing to catch.
> Unity's own walker guards this by skipping any field at offset -1 ("shortcut check for
> special statics") and skipping corlib entirely.
>
> So exclude before the call, never after it: pointer and by-ref fields, ref structs
> (`IsByRefLikeAttribute`, detected by name — `Type.IsByRefLike` may not exist in this class
> library), fields carrying `[ThreadStatic]`, and the statics of mscorlib, `System.*`,
> `UnityEngine.*` and the Burst/Collections/Jobs assemblies. Nothing is lost by the last one:
> the engine's objects come in through `FindObjectsOfTypeAll` anyway.

A crash there is also hard to diagnose after the fact — the crash log names the line but not
the field. Writing the current type to a file every few dozen types, and deleting the file on a
clean finish, means the next run can say what the last one died in, and skip it.

Three more things decide whether this works:

- **Compare by identity.** `HashSet<object>` with an `IEqualityComparer<object>` built on
  `ReferenceEquals` and `RuntimeHelpers.GetHashCode`. The default comparer calls each object's
  own `Equals`, which folds distinct objects together and runs arbitrary code a million times
  during a memory scan. At this scale an open-addressed `object[]` beats `HashSet` outright —
  one reference per slot against a bucket, a hash and a next field.
- **Hold the visited set only while walking.** It is a strong reference to every object seen,
  so it keeps the heap it measured alive. Drop it the moment the walk ends.
- **Know what it costs you.** Reading a static through reflection runs that type's initializer
  if it has not run yet. There is no "is this initialised" in the managed API, so it cannot be
  avoided, only stated.

Sizes come out of the field layout — 16 bytes of Mono object header, 8 per reference, the
declared width per primitive, `2 * Length + 6` for a string, 32 plus the elements for an
array. Exact for the shapes that dominate a heap and wrong at the margins, so label them
estimates. Counts are exact either way, and counts are usually the answer: 400,000 live
`CargoPackage`s is a finding whatever they each weigh.

## A call tree for one assembly

Timing needs instrumentation, and instrumentation means rewriting method bodies. A `Hook`
wraps one method with a delegate of matching signature, which does not scale to a few
thousand unknown ones; `ILHook` edits the body instead — push an id, call `Enter`, and call
`Exit` before every `ret` — which is signature-agnostic:

```csharp
new ILHook(method, il =>
{
    ILCursor cursor = new ILCursor(il);
    cursor.Goto(0);
    cursor.Emit(OpCodes.Ldc_I4, id);
    cursor.Emit(OpCodes.Call, enter);

    while (cursor.TryGotoNext(MoveType.Before, i => i.MatchRet()))
    {
        cursor.Emit(OpCodes.Ldc_I4, id);
        cursor.Emit(OpCodes.Call, exit);
        cursor.Index += 1;   // stepping further walks off the end of the body
    }
});
```

`MoveType.Before` is what makes a branch to that `ret` run the exit call too — incoming
labels are retargeted onto the emitted instructions.

> [!WARNING]
> **Record every thread, or you will record almost nothing.** The obvious shape for a recorder
> is one stack and one tree, guarded by "only the thread that started the capture", on the
> reasoning that the game and its mods run on the main thread. They do not.
> `GameSessionOrchestrator` calls `Simulator.StartAsynchronousUpdate`, which returns a `Task` —
> belts, machines and **every simulation a mod registers** run on pool threads. A 200 second
> recording of a belt mod came back with six methods and 0.1 ms, because all of its work
> happened on threads that were being discarded.
>
> Give each thread its own pre-sized stack and its own tree in a `[ThreadStatic]`, take a lock
> only when a thread first appears, and merge the trees when the recording stops. A lock on the
> hot path would cost more than the measurement is worth. Keep the per-thread totals as well as
> the merged tree: "main 12 ms, thread 34 4,201 ms" is the line that tells you the recorder is
> looking in the right place.

Two details in the recorder that are easy to get wrong and hard to notice:

- **A depth cap must not corrupt the tree.** When the stack is full, a dropped entry still runs
  its exit — and an exit that goes looking down the stack for its own method id will find the
  *ancestor* that caused the recursion and close a frame it never opened. Count the drops and
  spend them first: exits pair with entries last-in-first-out. Reset the count when the stack
  empties, which is the one moment the books are known to balance after an exception.
- **The names outlive the hooks.** Taking the weave out when recording stops is right; clearing
  the id-to-name table at the same time is not, because the tree is read *after* the stop. Doing
  both rendered every frame in the graph as "(root)".

What this misses, and what a graph built on it must say out loud:

- **Methods on generic types, and generic methods.** Refused outright — and the check is on
  the *declaring* type, so `Foo<ShapeId>.Bar()` is as unhookable as `Foo<T>.Bar()`. See
  [Hooking](../hooking.md).
- **Inlined calls.** Mono's JIT inlines small methods, and rewriting a body does not affect a
  caller that already inlined it. Tiny hot helpers under-report.
- **Anything outside the instrumented assembly.** This profiles mods, not the game.

Keep the recorder allocation-free after warm-up: create each tree node the first time a call
path is seen and reuse it, keep the stack in a pre-sized array, and record only the main
thread — locking per call costs more than the measurement is worth.

## Export what you collected

A capture is worth more in a file than on a screen: an in-game console cannot be scrolled or
selected, a heap census is thousands of rows, and the interesting comparison is usually against
last week's build. Write it next to `Player.log` (`Application.persistentDataPath`) and put the
*path* on the clipboard rather than the data.

Plain indented text is the format that needs no explaining and diffs between builds. Folded
stacks — `frame;frame;frame <microseconds>`, one line per leaf — are the cheapest bridge to
[speedscope](https://www.speedscope.app) or `flamegraph.pl` if you ever want a real viewer, but
they are a file nobody can read without one, so do not ship them as the default.

## What is still open

`mono_profiler_create`, `mono_profiler_enable_sampling` and
`mono_profiler_set_sample_hit_callback` are all exported, which is the shape of a real
sampling profiler: stacks off any thread, game code included, with no instrumentation and no
generic-type gap. `mono_profiler_enable_allocations` is exported too, but Mono refuses it
after runtime startup, so per-allocation attribution is almost certainly off the table from
inside a mod. None of this has been called yet — a sample hit callback runs with the sampled
thread suspended, so the same "must not allocate" rule applies, harder.

## See also

- [Debug a mod](debugging.md) — for when the problem is that nothing happens at all.
- [Hooking](../hooking.md) — what can and cannot be detoured.
