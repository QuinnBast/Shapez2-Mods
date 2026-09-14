# Mod Reloader

A development tool. Rebuild a mod, run one console command, and its new code runs — no game
restart.

On a large save that is the difference between a two-minute wait and a second, which is the
whole point: the game itself has no reload, and its loader actively refuses a second load.

> **Not for shipping.** Every reload leaks an assembly, and reloading only works cleanly if
> the mod being reloaded undoes everything in `Dispose`. Keep this in your dev mods folder.

## Using it

In the debug console (**F1**):

| | |
|---|---|
| `mrl.list` | every loaded mod, with every name `mrl.reload` will accept |
| `mrl.reload <name>` | save, run that mod's rebuilt assembly, and rebuild the session |
| `mrl.watch` | reload by itself whenever a staged build changes (toggle) |
| `mrl.run <command>` | run another command, print it, and copy its output to the clipboard |
| `mrl.copy` | copy the last command's output, whatever command it was |
| `mrl.paste` | run whatever is on the clipboard as a console command |
| `mrl.seed` | install any staged build that has no installed copy yet |

`mrl.reload` saves the game, swaps the code, and then rebuilds the game session by
re-entering that save. The rebuild is what makes new *content* appear: buildings and
islands are baked in `GameMode.From`, meshes and animations into the session's own
`MeshCache`, the toolbar in `ToolbarBuilder.BuildToolbar`, panels and commands in the
orchestrator's `Init_` steps - all per session, from the vanilla baseline, with Shifter's
interceptors consulting your mod on the way past. Swapping the assembly alone cannot touch
any of it, because it was already built by the old code.

The save has to come first: the old instance is the only code that can still write its own
save data, and without it everything since the last save would be rolled back by the reload
that follows. If there is no session to rebuild - the main menu's background map, or a save
that would not write - the reload stays a code-only one and says so, and then puts the new
code back in front of the session callbacks that only fire once at init, which no mod can
do for itself. Without that a reloaded mod's commands keep answering from the disposed
instance.

`mrl.watch` runs that loop for you: it watches the staged build folder and reloads whatever
was rebuilt, so a `dotnet build -p:Dev=true` in another window is the whole gesture. A build
is several writes, so a change only counts once the folder has been quiet for a moment; a
solution-wide build that stages every mod is one save and one session rebuild, not one per
mod. The report goes to `Player.log`, and stays on `mrl.copy` - the rebuilt session takes
the console's history with it.

The clipboard commands exist because the in-game console cannot be selected from, so
anything worth reading is trapped there. `mrl.run peo.types` runs that command and leaves
its output on your clipboard. `mrl.copy` does the same afterwards for whatever you ran
last - including the game's own commands and other mods' - so output only worth keeping
once you have seen it does not have to be produced twice. Everything these commands print
is mirrored into `Player.log` as well, so output survives even if the clipboard is
unavailable.

The name matches loosely against the mod's title and folder, and refuses ambiguous matches
so a typo cannot reload the wrong thing. The loop becomes:

## From the pause menu

`mrl.reload` also has a button. **Reload Mods** sits under Save in the pause menu and does
exactly what the command does - save, swap every staged build, re-enter the save - without
opening the console or typing a mod name.

It reloads everything in `mods-dev` rather than asking which mod, the same as `mrl.watch`
does, because a solution-wide build stages them all at the same moment and a batch costs one
save and one session rebuild however many mods it covers. Anything staged that is not a
running mod costs a line in the report.

The report goes to `Player.log` and stays on `mrl.copy`. A notification confirms the reload
started, though it will not usually outlive the session rebuild that follows it - the case
where you *do* see it is the one worth seeing, a reload that declined to rebuild because a
mod was refused or the save failed.

## After a crash

When a mod throws hard enough to reach the game's crash screen, there is no console to type
into - the session that owned it has already been unloaded by the time the screen appears.
So the reload goes on the screen itself, as a third button beside Copy to Clipboard and
Report on Discord:

**Reload Mods** swaps every staged build in `mods-dev` and returns to the main menu. Re-enter
your save from there and the session is built by the new code.

It cannot save anything. `GameOrchestrator.HandleFatalException` shows the screen and then
unloads the session without waiting, so everything since your last save is gone before you
read the message - this is a faster way back to a working game, not a rescue. The main menu
rather than your save is deliberate too: a mod that throws while a session is *loading* would
otherwise be re-entered straight back into the same crash.

The button reloads everything staged rather than asking for a name, because there is nowhere
to type one. Anything in `mods-dev` that is not a running mod costs a line in the report;
Mod Reloader refuses to reload itself, so a staged build of *this* mod still needs a restart.
The report goes to `Player.log` and stays on `mrl.copy`.

## Islands, and why they used to break a reload

Islands are registered from a mod's constructor as `GameScenarioIslandExtender`s. A session
build consumes each one - removing it from Shifter's rewirer list and replacing it with an
`IslandsExtender`, which is what actually calls `IslandBuilder.BuildAndRegister`.

The scenario extenders balance exactly. The islands extenders do not: over one session of
reloading, 94 added against 61 removed. The residue builds up a generation at a time, and the
moment two of them carry the same island id the next session build calls
`gameIslands.DefinitionsById.Add` twice and the savegame load dies:

```
An item with the same key has already been added. Key: CargoStore
  at ShapezShifter.Flow.IslandBuilder.BuildAndRegister (...)
  at ShapezShifter.Flow.Atomic.IslandsExtender.ModifyGameIslands (...)
```

`IslandDeduplication` prefixes `IslandDefinitionFactory.BakeMetadataIntoRuntime` - the method
Shifter's own islands interceptor postfixes - so it runs immediately before the extenders do,
and drops every registration but the newest for each island id. Newest wins because it belongs
to the most recently loaded assembly, which is what reloading a mod asks for.

It is installed whether or not anything is ever reloaded, and that is the point: **the residue
outlives the reload that created it.** Once the list is poisoned, an ordinary load from the
main menu is enough to hit it, with no reload anywhere near. Cleaning up only at reload time
would have left that case broken.

Your save is never at risk either way - the reload writes it before anything is swapped.

## MonoBehaviours cannot be rebuilt by a reload

A rebuilt assembly is loaded from bytes, because a reload has to bind by assembly identity
rather than by path. One consequence is already known - `ModDirectoryLocator` throws on a
reloaded mod, since a byte-loaded assembly has an empty `Location`. Here is another:

**`AddComponent<T>()` returns null for a MonoBehaviour whose type comes from a byte-loaded
assembly.** Not an exception - null. So this works on the first launch and fails on every
reload after it:

```csharp
DebugPanel panel = host.AddComponent<DebugPanel>();
panel.Feed = feed;   // NullReferenceException, second generation onwards
```

Thrown from a mod constructor, that is fatal to the whole mod: the reloader disposes the old
entry point before constructing the new one, so a throw here leaves nothing running and takes
every console command with it.

```
the new entry point threw while constructing - see the log. That mod is now not running.
```

Two rules follow, and Mod Profiler learned both the hard way:

- **Check the result of `AddComponent`** and carry on without whatever it was for.
- **Register console commands before anything that can fail.** They are what you need in order
  to diagnose the failure, and they cost nothing to set up.

The component itself needs a game restart to come back, which puts MonoBehaviours in the same
category as islands and toolbar entries: reloadable code, but not reloadable *content*.

Anything the old instance created and the new one cannot recreate is also worth re-pointing
rather than leaving alone. A cloned button left over from a previous generation still has its
`onClick` wired to that generation's objects, which were destroyed with it - so it sits in the
UI looking perfectly normal and does nothing at all.

## Setting up a mod for reloading

The installed copy of a mod is memory-mapped the moment the game loads it, so a normal
build cannot overwrite it while the game runs - there would be nothing new to reload. So a
mod being developed needs to build somewhere else: **`<persistent>/mods-dev/<ModName>/`**.

Mod discovery only enumerates immediate subdirectories of `mods`, so a staged build there
is never loaded as a second mod, and `mrl.reload` prefers it when it finds one.

The one-time setup, for any mod:

```xml
<PropertyGroup Condition="'$(Dev)' == 'true'">
    <OutputPath>$(SPZ2_PERSISTENT)\mods-dev\$(MSBuildProjectName)\</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
</PropertyGroup>
```

Then the loop is:

```
dotnet build -p:Dev=true    # the game can stay open; nothing locked is touched
mrl.reload mymod            # in game
```

`Directory.Build.props.template` in this repo is the same thing as a drop-in file: rename it
to `Directory.Build.props` beside your solution and MSBuild imports it automatically, with
no csproj edit at all.

## The first build of a new mod

A staged build with no installed copy beside it is invisible: the game never discovers it,
so there is nothing for `mrl.reload` to replace. Mod Reloader copies such a build into the
mods folder at startup, and `mrl.seed` does the same on demand.

A mod already running from somewhere else - a workshop subscription, most likely - is left
alone: a second copy would give the loader two mods with the same id, and someone who
deleted their local copy to test the published one should not find it put back.

The copy cannot take effect in the session that makes it - discovery runs before any mod's
code, this one included - so it loads on the next launch. That is the point of copying
rather than loading the assembly directly: the game then treats it as an ordinary installed
mod, applies the usual manifest, game-version and dependency checks, and reports a failure
in the mod list exactly as it would for anything else. From then on it reloads normally.

The reload report names the source it used and how old the staged build is, because
reloading stale bytes looks exactly like a reload that did nothing. Reloading straight from
the installed folder says so outright rather than appearing to work.

## Why the game cannot do this itself

`ModLoader.LoadMods()` throws on a second call — *"Trying to load mods multiple times. This
is not supported"* — and mods are loaded with `Assembly.LoadFrom`. In Unity's Mono runtime
an assembly loaded that way can never be unloaded, and `LoadFrom` binds by assembly
*identity* — name, version, culture, public key — not by path. A rebuilt mod keeps the same
identity, so `LoadFrom` returns the copy already in memory and never reads the new file, no
matter what path it is given.

So this tool reads the rebuilt DLL into a byte array and loads it with `Assembly.Load(byte[])`.
That overload has no identity/path context: it takes the raw image and genuinely reads the
new bytes as a distinct assembly every time, even when the identity is unchanged.

## What that costs

These are consequences of the approach, not bugs to be fixed:

- **Each reload leaks an assembly.** A few MB across a session's worth of reloads. Fine to
  develop against, never to ship.
- **Old and new types are different types.** Anything the game still holds from the previous
  assembly keeps the old code alive.
- **A mod is only as reloadable as its `Dispose`.** Whatever it registered and did not undo
  — a HUD button, an event subscription, a lane hook — survives and then exists twice. This
  is the usual cause of a reload that "half worked".

That last point is the useful side effect: it makes incomplete teardown visible immediately,
and a mod that cannot clean up after itself also cannot be disabled mid-session.

## State

Early. `mrl.list` reads the loader's own lists; `mrl.reload` performs the dispose,
shadow-copy, load and construct sequence, mirroring what `ModLoader` does
(`DependencyContainer` with `ILogger` bound, then `Create` on the single `IMod`). Reporting
is deliberately verbose so a failure says which step failed and whether the old instance is
still running.

Untested against a real reload so far.

## Building

Set `SPZ2_PATH`, `SPZ2_PERSISTENT` and `SPZ2_SHIFTER`, then `dotnet build`. Output goes to
the mods folder.

Licensed under [Apache 2.0](LICENSE).
