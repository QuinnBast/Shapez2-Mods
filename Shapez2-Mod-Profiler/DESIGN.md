# Mod Profiler - design notes

A development tool: where a mod's frames and memory go, measured from inside the running game.
What follows is what was read off the assemblies, the runtime and two crash logs rather than
assumed, so none of it has to be re-derived.

## Status: working, 2026-09-13

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

## The page's look

Rebuilt 2026-09-13. The page is still IMGUI and still not a `HUDPart`, but "it will not look
like the game's UI" is no longer part of the trade. Everything the game's pages are made of is a
texture, and `PanelTheme` generates them on the first draw:

- **Rounded surfaces** are a white mask built from a signed distance field, nine-sliced through a
  `GUIStyle.border`. `GUI.DrawTexture` does not nine-slice; `GUIStyle.Draw` does, honours
  `GUI.color`, and throws outside `EventType.Repaint`. One mask serves every colour.
- **Three corner radii, not one.** A nine-sliced texture drawn smaller than the sum of its own
  borders squashes them, so the 6-pixel scrollbar and the 6-pixel progress meter take a radius-3
  mask and a flame graph frame - which can be one pixel wide - is a plain rectangle.
- **Cards** are `GUILayout.BeginVertical(style)`, the only way IMGUI will put a background behind
  a group whose height is not known until its contents are laid out.
- **The scrollbar is ours.** `GUI.VerticalScrollbar` resolves its thumb by looking up
  `style.name + "thumb"` in `GUI.skin`, so restyling it means replacing a skin the game's own
  debug console shares. `GUIStyle.none` for both scrollbars, a hand-drawn track and thumb with
  their own hot control, and `GUI.EndScrollView` still handles the wheel.
- **The mod picker is split in two.** Immediate mode does input and drawing in one pass, so a
  menu painted last is on top but has already let every click through to what it covers. Its rows
  are hit-tested before the content is laid out and painted after it.

**The palette is sampled, and its polarity was wrong the first time.** Off a screenshot of the
Statistics page: ground `36,55,90`, a stat tile on it `21,35,51`, the chart well inside that tile
`18,29,40`, an unselected control `66,84,113`, a selected one `97,105,125`. Content surfaces are
**recessed** and controls **lift** - the opposite of the usual habit of floating cards a few
percent of white above the background. Building them the wrong way round cost twice: the cards
were only visible with the page dropped to about half a real page's brightness, and every label
on them was then fighting a near-black ground. Surfaces are now black at 34%, the well at 50%,
and the buttons are the white.

**The background is the game's own.** Every full-screen page holds a
`HUDFullscreenDialogBackground` - a vignette, a glow top and bottom, and two faint line layers its
`OnUpdate` rotates at 2 and -1.333 degrees a second. `GameBackdrop` reads those five sprites, their
tint colours and both rotation rates off the live instance (`FindObjectsOfTypeAll`, because each
one is inactive until its page opens) and draws them in IMGUI. The *component* is deliberately not
cloned: it is a `HUDComponent` whose `[Construct]` would not run and whose `OnUpdate` ends in
`ConsumeAll()`, which is a second thing fighting this panel for input it already consumes.

**The sprites do not outlive the session.** They belong to the HUD, so quitting to the main menu
destroys them, and a destroyed `UnityEngine.Object` compares equal to null. `GameBackdrop.Available`
was a bool set once at resolve, which meant the layers all went invalid and drew nothing while the
flag still said yes - so the panel's retry never fired and every later save ran on the bare
gradient. It is now derived from the layer references each time it is asked, which costs five null
checks a frame and cannot go stale.

What could not be taken is the layout. The prefab's rectangles are authored, and the live
instance's transforms are a pose rather than a layout - `Construct` parks the vignette at half
height and the glows at three times width until `Show` animates them in - so the five positions
are matched against a screenshot. `prof.backdrop` says whether the layers were found; if they were
not, the generated three-stop gradient stands in.

Two things about the page cannot be settled from here.

**The typeface.** Every string the game draws goes through TextMeshPro, which uses a
`TMP_FontAsset` - a baked atlas, not a font - and IMGUI needs a `UnityEngine.Font`. The only
bridge is `TMP_FontAsset.sourceFontFile`, which a build keeps only if the asset was imported with
its font data included. `PanelFont` probes for one and takes it if it is there and `dynamic`;
otherwise the page draws in IMGUI's built-in face and looks no worse than it did. `prof.fonts`
says which happened and `prof.font` overrides it, because choosing between candidates needs eyes
on the screen.

**Glyph coverage.** Which characters a face carries is unknowable from a mod, and the built-in
font and the game's do not have to agree. Only Latin-1 is safe: `×` is the close button,
and the dropdown caret is a generated mask rather than `▾`, which would be a box on a face
that lacks it.

The palette was matched by eye against a screenshot of the Statistics page. The game's real
values live in authored prefabs, which cannot be read from an assembly, so it is a likeness
rather than the same numbers.

## The flame graph was drawing the right widths in the wrong places

`DrawFrames` took a width but no origin, and started every recursion at `area.x`. Each row was
therefore packed against the left edge of the graph rather than laid out under the frame that
called it. The widths were correct the whole time, which is why it looked plausible: the
proportions were right and only the positions were wrong, so the picture said nothing at all
about who called whom - the one thing a flame graph is for.

It now carries the parent's left edge down with the span. Two details that go with it:

- The span passed to the recursion is the child's **full** share, not its drawn width.
  Subtracting the two-pixel gap before recursing shrank every level a little more than the last.
- The box is sized by `VisibleDepth`, which applies the same sub-pixel cull the draw does, rather
  than by the tree's real depth. A deep capture is mostly frames too narrow to draw, and
  measuring the true depth left two thirds of the card empty.

## The frame graph is bucketed, not per frame

One slot per frame made the chart a 180-frame window, which is three seconds at 60fps - it
scrolled past faster than a stutter could be looked at. `CounterFeed` now keeps 240 buckets of a
quarter second each, a minute of history in the same width.

The aggregate is the bucket's **worst** frame, not its mean. A mean over a quarter second is
fifteen frames averaged together, which is precisely how a single 80 ms hitch disappears. The
bucket under the cursor is live - written every frame and only sealed when its window runs out -
so the right hand end of the chart is the current frame rather than a quarter second behind it,
and the slot being moved into is cleared on arrival, because otherwise a minute-old reading is
drawn as if it were current.

## Recording has a deadline

`ProfilerSession.LimitSeconds` defaults to 120 and `Tick()`, called from `DebugPanel.Update`,
stops the capture when it expires. The failure this prevents is not hypothetical: Record weaves
the whole assembly, the weave costs two timestamp reads and a dictionary lookup per call, and
nothing about a running recording is visible from outside the CPU tab. Somebody who presses
Record to see what it does and then plays for an hour is paying for it the entire time.

The deadline is held in `ProfilerSession` rather than the panel so that `prof.record` gets it too,
and it is checked from `Update` rather than a timer because stopping unweaves a few thousand
methods. A build with no panel - a hot reload, where `AddComponent` refuses the MonoBehaviour -
has no auto-stop and still has `prof.stop`.

## A zero reading is not a measurement

Every min on the Overview tab read zero, permanently, and the cause is worth recording because
nothing about it looks like a bug in the statistics code.

`ProfilerRecorder.LastValue` returns zero until the recorder has taken a sample, and the render
counters return zero on any frame that drew nothing - a loading screen, the frames either side of
a save being opened. `CounterFeed.Sample` runs from `DebugPanel.Update`, which exists from mod
construction, so the feed walks through a pile of those at the main menu and again on every load
before anybody opens the page. A minimum only ever decreases, so one zero pinned every counter's
min there for the session; max and mean were dragged the same way, less visibly.

`Gauge.Observe` now discards non-positive readings entirely rather than special-casing min, and
`Sample` does the same for a frame time of zero - Unity reports an unscaled delta of zero on the
first frame after a scene load. None of the counters in the verified set can legitimately read
zero while a frame is being drawn; they are byte totals and draw call counts. `Value` is still
the raw reading, because "now" should say what the counter actually said.

What this does **not** fix is `max` carrying the hitch from loading a save, which is a real frame
and belongs in the statistics. `Reset stats` in the Overview bar is the answer to that.

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

- **Overhead calibration.** Time an empty instrumented method at Record time and report
  `calls × cost` as a share of the capture.
- **The Mono sampling profiler.** `mono_profiler_create`, `mono_profiler_enable_sampling` and
  `mono_profiler_set_sample_hit_callback` are all exported, and it would cover the game itself
  and the frames inlining hides. It is very likely the same trap as the liveness walk: the sample
  hit callback runs with the sampled thread suspended, and anything a mod can pass is managed
  code. If it is ever tried, it belongs behind a console command nobody runs by accident.
- `mono_profiler_enable_allocations` is exported but Mono refuses it after runtime startup, so
  per-call allocation attribution is almost certainly off the table from inside a mod.
