# Debug a shapez 2 mod

Work down this ladder in order. Each rung eliminates a whole class of failure, and they
are ordered by how often they are the answer against how cheap they are to check.

**Do not start by reading the mod's source.** Most failures on this list are invisible
there: the source is correct and something else — the wrong build, a hook that refused to
install, a cascade in the log — is the actual fault.

## 0. Confirm which build is running

The most common wasted hour is fixing something, building it, and then testing the
previous build. A build *is* an install (`OutputPath` points at the mod folder), so the
two are easy to confuse.

```bash
grep -o '"Version": "[^"]*"' "$SPZ2_PERSISTENT/mods/<Mod>/manifest.json"
ls -la --time-style=+%H:%M "$SPZ2_PERSISTENT/mods/<Mod>/"
```

If the timestamp predates the last build, the install did not happen. The usual reason:

> `MSB3021` / `MSB3027` … **user-mapped section open**

That is the *copy* failing because the game holds the DLL memory-mapped. The compile
succeeded. Close the game and build again — and check before building:

```powershell
Get-Process | Where-Object { $_.ProcessName -match 'shapez' }
```

To compile-check without closing the game, redirect the output:

```bash
dotnet build MyMod.csproj -p:OutputPath=/tmp/verify/
```

## 1. Read the log — and find the *first* failure

```
%USERPROFILE%\AppData\LocalLow\tobspr Games\shapez 2\Player.log
```

After a crash you want **`Player-prev.log`**, not `Player.log` — restarting has already
overwritten the current one.

**The error in the crash dialog is often not the error.** A mod that throws while
registering islands produces a cascade: the throw propagates out of
`SavegameSerializationUtils.Load`, the game logs `Failed to load 'memory': …` and falls
back to starting a new savegame, that fallback re-runs `GameMode.From` against an
already-populated registry, and vanilla's own `ResolveGroups` throws on the first group
it touches:

```
System.InvalidOperationException: An island group with id HUB already exists
  at IslandGroupDefinitionRegistry.Create (MetaIslandDefinitionGroup, System.String)
```

`HUB` is vanilla. No mod appears anywhere in that stack. Chasing it leads nowhere.

**Grep for `Failed to load` and read upward from there.** The real stack names the mod
and the duplicate id, and it sits far above the visible error with a whole second init
pass in between.

## 2. Did the mod load at all?

Log one line from the `IMod` constructor. No line means the game never constructed the
class:

- the DLL is not in `<persistent>/mods/<YourMod>/`
- `manifest.json` is missing, malformed, or does not list the assembly in `Assemblies`
- a `Dependencies` entry is not installed
- the mod is not enabled in the mods menu
- the class is not `public`, or does not implement `IMod`

Prefix every log line with the mod name. `Player.log` is busy, and `grep MyMod` is the
difference between finding the line and scrolling.

## 3. Did the hooks install?

A hook that fails to install produces **no exception and no log line** when installation
is wrapped in `try`/`catch` — which it should be. So count them and say so:

```csharp
Logger.Info?.Log($"MyMod: {Hooks.Count} of 3 hooks installed");
```

Two constructions fail at install time while compiling perfectly:

| Log message | Cause |
| --- | --- |
| `Source method is generic, generic hooks are not supported` | the declaring type is generic — relocate to a non-generic choke point |
| `Target method is not compatible with source method` | `CreatePrefixHook` pointed at a method that returns non-void |

Both are in `docs/hooking.md`. If a hook *installed* but the code
never runs, the method is not being called — you targeted an overload or a code path the
game does not take.

## 4. Reproduce from the console

Press **`F1`**.

| Command | Use |
| --- | --- |
| `debug.export-game-data` | dumps definition ids, milestone ids, scenario data to JSON |
| `<yourmodname>.<command>` | your own commands, auto-prefixed with the assembly name |

A `dump` command printing the state the mod *believes* it has is the fastest debug loop
available, and beats per-frame logging — which drowns you in `Player.log`. See
`docs/howto/console-command.md`.

## Symptom table

| Symptom | Likely cause |
| --- | --- |
| `NullReferenceException` in the constructor | touched the map at mod-load time — there is no map at the main menu. `docs/howto/run-code-when-game-loads.md` |
| New building missing from the toolbar | no research unlock, or a wrong toolbar index — unlock and toolbar are **separate** requirements |
| Name renders as `my-mod.thing.title` | `translations.json` did not copy, or the key is misspelled |
| A stray `?` before a value (`Layer ?1`) | something was treated as a translation id and not found — usually `.T()` where `RawText` was meant |
| `An item with the same key has already been added`, before the main menu | the pipette map is a `Dictionary.Add` — a second placer covering the same definitions |
| Island cannot be hovered, selected, pipetted or deleted | `WithBoundingCollider()` builds a zero-sized box. Use `WithPerChunkColliders()` |
| `KeyNotFoundException` once per frame, and things stop drawing | an island is missing `IslandFrameDrawData`; it also aborts `MapDrawer.Draw` partway through |
| `FieldAccessException` / `MethodAccessException` at runtime | publicizer did not apply at runtime — see below |
| Machines near your code stop working | a lane hook was replaced instead of chained |
| Framerate collapses | per-frame work over every building |
| Works on a new save, silently stops on a loaded one | `Attach` used where `AttachOrReplace` was needed — the second copy sits beside the first, and `CustomDataHolder` reports a multiple match as found-nothing |
| Worked before a game update | a detour target changed |
| Works in one scenario, not another | a hard-coded milestone id |

## The publicizer warning is not a reliable signal

```
warning : Assembly is marked for publicization, but no members were publicized
```

This warning appears on builds where publicization **did** work and private game members
are callable. Do not treat it as a diagnosis on its own.

The discriminating test is a throwaway probe that touches one private member and then
gets deleted. If it compiles, publicization applied at compile time.

A `FieldAccessException` or `MethodAccessException` at *runtime* having compiled fine is
the genuine failure: compile-time visibility is resolving but
`IgnoresAccessChecksToAttribute` is not reaching the runtime. Check `SPZ2_PATH`, and that
every game `<Reference>` is `<Private>False</Private>` — a publicized game assembly
copied into the mod folder shadows the real one.

## Hot reload does not reload everything

Where Mod Reloader is in use, `mrl.reload <modname>` picks up `mods-dev/` and reloads
**code only**:

- logic, detours, HUD providers → reload works
- new islands, new toolbar entries, collider or definition changes → **restart required**

Definitions and the toolbar are built per session, and `AtomicIslands…Build()` returns no
handle, so a mod cannot unregister them in `Dispose`. Reloading and then re-entering a
session double-registers and crashes on a duplicate key — which presents as the `HUB`
cascade from step 1.

## The crash screen is already dead

When something throws past the game's own handlers,
`GameOrchestrator.HandleFatalException` shows `HUDCrashOverlay` and *then* awaits
teardown without waiting for you.

- The session is gone by the time you read it. Nothing is recoverable, and there is no
  save button to hunt for.
- Its buttons usually do not work. Every sub-orchestrator owns its own EventSystem and
  releases it on dispose, so Copy to Clipboard and Report on Discord are both inert.
- The game never returns from it — `GameView` hides the overlay exactly once per process.

Read `Player-prev.log` instead of trying to use the screen.

## Deeper reading

Long-form pages in the community docs repo (`shapez2-modding-docs`):


`docs/howto/debugging.md` ·
`docs/hooking.md` ·
`docs/publicizer.md` ·
`docs/howto/run-code-when-game-loads.md`
