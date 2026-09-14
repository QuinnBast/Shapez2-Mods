---
name: shapez2-modding
description: Build, debug and publish mods for shapez 2 using the ShapezShifter API — new buildings, islands, machines, shape parts and toolbar entries; detours and interceptors over existing game behaviour; and diagnosing a mod that will not load, crashes at startup, or whose content never appears in game. Use for any shapez 2 / ShapezShifter modding task, including setting up a mod project from scratch.
---

# Modding shapez 2

shapez 2 mods are .NET assemblies loaded by **ShapezShifter**, which wraps the game's own
`IMod` interface with fluent builder APIs for content and a set of interception points for
behaviour. Everything below assumes ShapezShifter; raw `IMod` alone is not worth using.

## Route by task

| Task | Read | Then |
| --- | --- | --- |
| Set up a project, or the build is misbehaving | `references/setup.md` | `scripts/new-mod.sh`, `scripts/check-env.sh` |
| Add a building, island, machine, toolbar entry, translation | `references/add-content.md` | — |
| Change how existing behaviour works: detour, interceptor, lane hook, draw over the world | `references/hooking.md` | — |
| It does not work and you do not know why | `references/debugging.md` | `scripts/scan-log.sh` |
| Quick check before shipping | `references/traps.md` | — |

Read `references/traps.md` before finishing any task. It is the index of failures that
compile cleanly and then cost a session — most are invisible in a code review.

## Five rules that apply to every task

**1. Do not guess authored values.** Island definitions for train stations, toolbar
structure, base processing durations and scenario JSON are authored ScriptableObject data.
They are *not* in the decompiled assemblies and cannot be read statically. Source them at
runtime from `GameMode`/config, or get the real ids from `debug.export-game-data` in the
`F1` console. If you can do neither, leave the value out and say so — a confidently wrong
constant in the UI is worse than no number.

**2. A build is an install, and the game must be closed.** `OutputPath` points straight at
`<persistent>/mods/<Mod>/`. While the game runs the DLL is memory-mapped and the copy
fails with `MSB3021` / `MSB3027 … user-mapped section open` — which is the *copy* failing,
not a compile error. Run `scripts/check-env.sh` before building, and again afterwards to
confirm what is actually installed. Testing the previous build is the single most common
wasted hour.

**3. Never let a mod constructor throw.** `ModLoader` does not contain it: the exception
comes out through `ModLoadingStep.LoadMods` and kills the game's entire mod loading step,
taking every other mod with it. Wrap hook installation individually, log what failed, and
carry on degraded. Static field initialisers running in declaration order are an easy way
to cause one.

**4. Ids are a save-file contract.** `BuildingDefinitionId`, `IslandDefinitionId` and
`[SyncableIdentifier]` strings are written into save files; renaming one breaks every save
that placed the content. `ModSaveData` is worse — `ModSaveDataExtensions.ResolveId<T>()` is
`AssemblyName + "-" + typeof(T).FullName`, so moving a *namespace* silently orphans a
shipped mod's settings. Choose all of these as if they ship, because they do.

**5. Put every type in your own namespace.** The game has ~2,800 types in the global
namespace. `Data`, `Entry` and `Configuration` are all real collisions that have happened.
Use `<Author>.Shapez2.<ModName>`.

## Scripts

All are bash (Git Bash on Windows) and read the three standard environment variables
`SPZ2_PATH`, `SPZ2_PERSISTENT` and `SPZ2_SHIFTER`. On Windows the game sets them for you:
`"shapez 2.exe" --set-modding-env-vars`.

| Script | Does |
| --- | --- |
| `scripts/check-env.sh` | Verifies the env vars, reports whether the game is running, and lists every installed mod with its version and DLL timestamp. Run before and after a build. |
| `scripts/scan-log.sh` | Triages `Player.log` — finds the *first* real failure rather than the visible one, and names the known signatures. `--prev` reads `Player-prev.log`, which is what you want after a crash. |
| `scripts/new-mod.sh` | Scaffolds a complete buildable project: csproj with the right references and publicizer setup, `manifest.json`, `translations.json`, `Directory.Build.props` for dev staging, and an `IMod` entry point that logs on load. |

Each takes `--help`.

## Verifying your work

Nothing here can be tested by reading code. Be explicit about which parts have only been
compiled, and say when a fix is a guess — naming the observation that would confirm it.

- `dotnet build` compiles **and installs**; it does not tell you the mod loads.
- Log one line from the `IMod` constructor, prefixed with the mod name. No line in
  `Player.log` means the class was never constructed, which is a different problem from
  anything in your code.
- Count your hooks: `Logger.Info?.Log($"MyMod: {Hooks.Count} of 3 hooks installed")`. A
  hook that fails to install throws at construction and is otherwise silent.

## Deeper reading

These references are distilled for working, not for learning. The long-form pages behind
them — architecture, coordinates, the map model, rendering, per-task how-tos, and a DocFX
API reference over ~4,700 game types — are in the community docs repo at
`shapez2-modding-docs` (`docs/*.md` and `docs/howto/*.md`). Each reference page names the
specific page to open.

The official ShapezShifter samples are the fastest starting point for new content:
`DiagonalCutter` is the only complete worked example of the full building stack,
`BiggerPlatforms` covers multi-chunk island layouts, `SandboxIslands` is an island with a
simulation. Adapt one rather than writing from blank — but read `references/traps.md`
first, because two of the samples contain a call that does not work.
