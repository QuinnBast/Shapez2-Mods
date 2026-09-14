# Debug a mod

**Problem.** Your mod does nothing, and you cannot tell whether it failed to load,
failed to hook, or is running and silently wrong.

**Solution.** Work down the list below in order. Each step rules out a whole class of
failure.

## 1. Read the log

```text
%USERPROFILE%\AppData\LocalLow\tobspr Games\shapez 2\Player.log
```

`Player-prev.log` is the previous run — which is the one you want after a crash, because
the current file has already been overwritten by the restart.

Unity writes mod-load exceptions here. Tail it while the game starts:

```bash
tail -f "$SPZ2_PERSISTENT/Player.log"
```

Your own logging lands here too:

```csharp
Logger.Info?.Log("MyMod: loaded");
Logger.Warn?.Log($"MyMod: unexpected {value}");
Logger.Exception?.LogException(ex);
```

Prefix your messages with the mod name. `Player.log` is busy, and `grep MyMod` is the
difference between finding your line and scrolling.

## 2. Confirm the mod loaded at all

Log one line from the constructor. No line means the game never constructed your class:

- The DLL is not in the mod folder — check
  `%USERPROFILE%\AppData\LocalLow\tobspr Games\shapez 2\mods\<YourMod>\`
- `manifest.json` is missing, malformed, or does not list your assembly in `Assemblies`
- A dependency in `manifest.json` is not installed
- The mod is not enabled in the game's mods menu
- Your class is not `public`, or does not implement `IMod`

## 3. Use the in-game console

Press **`F1`**. Two commands earn their keep immediately:

| Command | Use |
| --- | --- |
| `debug.export-game-data` | dumps game content to JSON — definition ids, milestones, scenario data |
| `<yourmodname>.<command>` | your own commands, auto-prefixed with your assembly name |

Adding a `dump` command that prints the state your mod *believes* it has is the fastest
debug loop available — see [Add a console command](console-command.md). It beats
per-frame logging, which drowns you in `Player.log`.

## 4. Attach a debugger

Unity's mono runtime accepts a managed debugger, which gets you breakpoints and locals
instead of print statements.

- **Rider** — *Run → Attach to Unity Process*, pick the running `shapez 2`
- **Visual Studio** — install the *Visual Studio Tools for Unity* workload, then
  *Debug → Attach Unity Debugger*

Build in `Debug` and make sure the `.pdb` sits next to your `.dll` in the mod folder, or
breakpoints will not bind.

> [!NOTE]
> Attaching to a shipped Unity player is not always permitted depending on how the build
> was made. If breakpoints never bind, fall back to console commands and logging — the
> loop is slower but it always works.

## The two failures that look like nothing

These produce no exception and no log line, which is why they waste whole evenings.

### A hook that never installed

`DetourHelper` throws if it cannot resolve the target method — but only if you let it.
Wrap installation and log the outcome:

```csharp
try
{
    DrawHook = DetourHelper.CreatePostfixHook<MapDrawer, FrameDrawOptionsNoLOD>(
        (drawer, options) => drawer.Draw(options),
        (drawer, options) => Draw(options));

    Logger.Info?.Log("MyMod: draw hook installed");
}
catch (Exception ex)
{
    Logger.Exception?.LogException(ex);   // the method moved or was renamed
}
```

If the hook installed but your code never runs, the method is not being called — you
targeted an overload or a code path the game does not take.

### A publicizer that did not run

Symptom: it compiles, then throws `FieldAccessException` or `MethodAccessException` at
runtime when touching a `private` member.

Check for this at build time:

```text
warning : Assembly is marked for publicization, but no members were publicized
```

That means references are not resolving — usually `SPZ2_PATH` is unset or wrong. See
[The Publicizer](../publicizer.md).

### An island registration failure, reported as something else entirely

Worth knowing before you spend an evening on the wrong mod. If a mod throws while
registering islands, the error the game shows you is **not** that error.

What happens: `GameMode.From` bakes island metadata, and ShapezShifter's
`IslandsInterceptor` postfix runs every mod's `IslandsExtender` inside that bake. A throw
there — say `IslandBuilder.BuildAndRegister` doing `DefinitionsById.Add` on an id that is
already present — propagates out of `SavegameSerializationUtils.Load`. The game catches it,
logs `Failed to load 'memory': …`, and **falls back to starting a new savegame**. That
fallback calls `GameMode.From` again with the *same* `IslandGroupDefinitionRegistry`, which
is already populated, so vanilla's own `ResolveGroups` now throws on the very first group it
tries to create:

```text
System.InvalidOperationException: An island group with id HUB already exists
  at IslandGroupDefinitionRegistry.Create (MetaIslandDefinitionGroup, System.String)
  at IslandDefinitionFactory.ResolveGroups (AuthoringIslands, …)
```

That is the error you get shown, and it names `HUB` — a vanilla group, no mod anywhere in
the stack. It is a cascade, and chasing it leads nowhere.

**So search the log for the *first* failure, not the one in the crash dialog.** Grep for
`Failed to load` and read upward from there; the real stack names the mod and the duplicate
id. And note the two are far apart in the log, with a whole second init pass between them.

The underlying hazard is worth avoiding in your own mod: `IslandBuilder.BuildAndRegister`
uses `Dictionary.Add`, not the indexer, so registering the same `IslandDefinitionId` twice
throws rather than replacing. A builder built once at mod construction and then registered
into more than one bake is the usual way in.

### A mod that works for you and is invisible for one player

The report is "your mod does nothing" from a log with no error in it. The mod loads, its
own log lines are all present, and on your machine the same build works perfectly.

What happens: `AtomicIslandExtender.Build` registers through `RewirerChain`, and every link
in that chain **unregisters itself the moment it has applied**. What brings the content back
on the next scenario load is the re-arm at the end of `Build` — and `WaitAllRewirers` fires
it only once *every* branch has cleared its link: modules, placement + toolbar, simulation
and prediction.

The prediction branch is the one that can never fire. It runs from
`PredictionSystemsInterceptor`, a postfix on
`BuiltinPredictionSimulationSystems.CreateSimulationSystems`, and that method has exactly
one caller — `GameSessionOrchestrator.SetupPredictions`, which `StartPredictionUpdate`
skips outright:

```csharp
if (!SimulationSettings.Predict)
{
    if (PredictionSimulator != null) ShutDownPredictions();
    return;                                   // SetupPredictions never runs
}
```

`SimulationSettings.Predict` is `BoolGameSetting("prediction", …, defaultValue: true)` — an
ordinary game setting. **A player who turns predictions off never creates a prediction
system at all**, so the branch never completes and the chain never re-arms.

That matters because **the first scenario of the process is the main menu's background
game**, not anything the player loaded:

```text
Initializing Main Menu
Core:: Stage 4 - Init existing savegame memory with mode RegularGameMode
```

The one registration is spent there. Every session after it — including the save they
actually play — gets no definitions, no toolbar entry and no research unlock, and nothing
throws.

**Diagnosing it from a user's log takes one count.** Every rewirer logs going in and coming
out, and the *removal* is what proves `AfterHijack` fired:

```bash
grep -c "Adding rewirer IslandPredictionExtender"               Player.log
grep -c "Removing rewirer with handle IslandPredictionExtender" Player.log
```

Adds with **zero** removals, and an add count that never rises across sessions, is the
signature. Confirm against the island counts and your own toolbar line:

```text
New islands: 170 + 163      Toolbar: created group '…'     ← menu background session
New islands: 164 + 163      —                              ← every session after it
```

Do not go hunting through the reporter's mod list. Their mods, load order and Workshop
packaging all reproduce clean on a machine with the setting left on; the only thing that
reproduces it is the setting itself, and it reproduces with no third-party mod installed at
all. See
[the extender chain is one-shot](add-an-island.md#the-extender-chain-is-one-shot-and-withprediction-can-strand-it)
for the fix, which is to keep prediction off the chain and re-arm it by hand.

## Common symptoms

| Symptom | Likely cause |
| --- | --- |
| `NullReferenceException` in the constructor | you touched the map at mod-load time — [run code when a game loads](run-code-when-game-loads.md) |
| New building missing from the toolbar | no research unlock, or wrong toolbar index — [unlock](add-research-unlock.md), [toolbar](add-to-toolbar.md) |
| Name shows as `my-mod.thing.title` | `translations.json` did not copy, or key is misspelled — [translations](add-translations.md) |
| Works in one scenario, not another | hard-coded milestone id — [per-scenario selection](add-research-unlock.md#unlock-at-a-milestone) |
| Machines stop working near your code | you replaced a lane hook instead of chaining it — [read machine state](read-machine-state.md#gotchas) |
| Framerate collapses | per-frame work over every building — [pacing](run-code-when-game-loads.md#do-not-do-heavy-work-every-tick) |
| Worked before a game update | your detour target changed — [staying compatible](../hooking.md#staying-compatible) |
| Works for you, does nothing for one reporter | they turned the `prediction` setting off and your island chain waits on `WithPrediction` — [above](#a-mod-that-works-for-you-and-is-invisible-for-one-player) |

## The crash screen, and getting back from it

When anything in a session load or a tick throws past the game's own handlers,
`GameOrchestrator.HandleFatalException` shows `HUDCrashOverlay` and then awaits
`TryDisposing()` -> `UnloadCurrentState()`. Two consequences worth knowing before you reach
for that screen:

- **The session is gone by the time you read the message.** The overlay is shown *before*
  the teardown, but the teardown does not wait for you. Anything unsaved is already lost, so
  there is nothing to recover and no point hunting for a save button.
- **The game never comes back from it.** `GameView` hides the overlay exactly once, in its
  constructor, which runs at `GameOrchestrator` construction - so once per process.

`HUDCrashOverlay` itself is a plain `MonoBehaviour` with public `Button` fields and a
non-generic `public void Setup(string, string)`, so it hooks like anything else. Two details
matter if you extend it:

- `Setup` guards its whole body with `if (CurrentError == null)` and **never clears
  `CurrentError`**. Vanilla has no reason to - it never returns from a crash - but it means
  a second crash in the same process silently shows nothing at all unless you null it.
- `Setup` wires both buttons with `onClick.AddListener` every time it runs a fresh error. If
  you do clear `CurrentError`, clear those listeners too, or the second crash copies to the
  clipboard twice and opens two Discord tabs.

**By the time you can read it, the screen is usually dead to input.** Every sub-orchestrator
owns its own EventSystem - `GameSessionOrchestrator.MainEventSystem`,
`MainMenuOrchestrator.MainEventSystem`, `IntroOrchestrator.EventSystem` - and releases it when
disposed. `HandleFatalException` shows the screen and *then* awaits `TryDisposing()`, so the
EventSystem goes with the session and no button works, Copy to Clipboard and Report on Discord
included. It is intermittent only because `TryDisposing` swallows what the teardown throws: a
dispose that fails partway leaves input alive. If you add anything clickable to this screen,
add an EventSystem with a `StandaloneInputModule` when `EventSystem.current` is null - and keep
checking, because at the moment the screen goes up the doomed one is still there.

Its buttons are also positioned absolutely, with no layout group. A cloned `RectTransform`
keeps its template's `anchoredPosition`, so a new button lands exactly on top of the one it was
copied from and looks like it replaced it. Measure the step between the two existing buttons
and continue it.

**The way back is `IGameFlowNavigator`.** `GameOrchestrator` implements it directly, so
`LoadMainMenu()` and `LoadSession(options)` are both available after a crash, and
`UnloadCurrentState` opens with a null check - calling it again on an already-unloaded state
is a no-op, not a double teardown. Reaching the orchestrator without a session is the part
that looks impossible and is not: `GameBootstrapper` is a static class holding a private
static `GameOrchestrator`, which [the publicizer](../publicizer.md) makes readable.

That same static is the only reliable route to the mod loader when a session never finished
loading. `GameModdingFramework` is bound in two places - the session container
(`GameSessionOrchestrator.cs:381`) and the game-level `InitializationDependencyContainer`
(`ModLoadingBlindStep.cs:30`). Only the second survives a failed session load, and it holds
the same `ModLoader`.

Mod Reloader puts a **Reload Mods** button on this screen using all of the above.

## Iterating faster

- **Rebuild installs.** `OutputPath` points at the mod folder, so a build *is* an
  install. There is no hot reload — restart the game.
- **Compile-check without closing the game.** While the game is running, the DLL is
  memory-mapped and the copy into the mod folder fails with
  `MSB3021 ... user-mapped section open` — the *compile* succeeded, only the install
  failed. Redirect the output to check your code without quitting:

  ```
  dotnet build MyMod.csproj -p:OutputPath=/tmp/verify/
  ```

  Only the final deploy needs the game closed.
- **Expose tunables as console commands.** Anything you would otherwise change by
  rebuilding — colours, sizes, thresholds — becomes a typed number instead of a restart.
  See [add a console command](console-command.md).
- **Keep a small test save.** Loading a 10,000-building megabase to test a two-line
  change costs more than the change.
- **Log state transitions, not frames.** "overlay enabled", "map loaded, 42 islands" —
  one line per event, not per tick.
