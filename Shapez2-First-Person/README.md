# First Person

Stand on your own factory floor and watch the shapes go past at eye level.

Walk around your platforms, fall off them, and build at a crosshair.

## Status

| | |
| --- | --- |
| Camera, mouse look, walking | works (played) |
| Crosshair, placing and deleting belts at it | works (played) |
| Gravity, floors, walls, falling off the edge | compiled, not yet played |
| Wheel between toolbars, Tab for the cursor | works (played) |
| Vortex spawn, and resuming where you saved | spawn works (played); resume compiled, not yet played |
| Rebindable keys in the game's settings | works (played) |
| Flat wheel through the toolbar, Shift+wheel for variants | compiled, not yet played |
| Research gates, configurable reach | compiled, not yet played |
| Riding belts | works, off by default — it carried you away from what you were building |
| Waypoint fast travel | works (played) |
| Riding trains | works (played) — upside-down rails included |
| The First Person scenario | works (played) |
| Shop icons and research nodes | works (played) |
| Settings page | not yet — see below |

Test in a save you do not mind breaking until the bottom rows move up.

## Controls

All of these are **rebindable in the game's own settings**, under *First Person*. The
defaults are below.

There is no key to turn first person on. You are in it because you are playing the **First
Person** scenario, and you stay in it for that session — see below.

| | |
| --- | --- |
| your movement keys | walk (uses the game's own bindings, not hardcoded WASD) |
| your "move faster" key | sprint — three times walking, and three times flight |
| `Space` | jump — high enough to get onto a three-layer blueprint — or rise while flying |
| `LeftCtrl` | sink while flying |
| `Q` / `E` | layer down / up (yours to pick while flying) |
| double-tap `Space` | fly / noclip — needs the flight research |
| `M` | the same, as a fixed key |
| `LeftAlt` (hold) | release the mouse to click the HUD, side panels and menus |
| `Tab` | cycle variants — the game's own binding, left alone |
| `F5` | fast travel to your next waypoint |
| click a waypoint or the home icon | travel there on foot, not in space view |
| `F` | board the train you are looking at, or step off (jump works too) — stands aside for mirror while you are holding a building |
| mouse | look |
| wheel | the next item, anywhere in the toolbar — and it switches between the machine and space views by itself when it crosses into one |
| `Shift` + wheel | cycle the variants of whatever you are holding |
| stand on a belt | nothing — it is floor. Set `BeltsCarryPlayer` if you want to be carried |
| left click | place, delete, pipette — whatever the toolbar is holding, at the crosshair |

The cursor unlocks by itself whenever a dialog opens, so the pause menu and the settings
screen still work.

## Layers

Layer switching works normally, and **while flying the layer is yours** — `Q` and `E` pick
it rather than it following your altitude, so you can hover high and still place space belts
on the floor below. On the ground it follows your feet again, so climbing to an upper
platform shows that platform. What first person hides is the translucent **blue layer
plane** the game floats at the current build layer — helpful from above, but at eye height
the nearest one fills the screen.

(An earlier build pinned you to layer 1 to get rid of those planes. That was the wrong
fix: the planes were the problem, not the layers.)

## The First Person scenario

New game → Regular → **First Person**. That is the only way in: there is no toggle key, and
in any other save the mod does nothing. It is the ordinary scenario with two differences:

- **You are locked in.** First person starts on the first frame and there is nothing to
  press to leave, the same way `FirstPersonControl.Forced` behaves for a downstream mod.
- **The map is denser.** No spiral, bigger patches, and enough of them that resources are
  a walk rather than an expedition — which matters when you are walking.

| | Vanilla | Here |
| --- | --- | --- |
| Spiral generation | off | off |
| Fluid patch likeliness | 15% | 250% |
| Fluid patch base size / growth / max | 2 / 70% / 4 | 3 / 60% / 8 |
| Shape patch likeliness | 30% | 200% |
| Shape patch base size / growth / max | 2 / 70% / 5 | 4 / 70% / 8 |
| Rare shapes | 30% | 45% |
| Very rare shapes | 10% | 33% |

Above 100% these do not saturate, they **loop**: `DefaultMapGenerator` runs
`do { … k -= 100; } while (k > 100)`, so the count is `ceil(k / 100) - 1` patches per super
chunk, each one placed outright — 250% and 200% are both two patches.
A super chunk is 64×64 chunks, and the vanilla figures above are less than one patch each.

(The vanilla column is the class default in `MapGenerationParameters.SerializedData`. The
scenario's own base file is a Unity `TextAsset` behind a `#include:`, which nothing dumps,
so the real vanilla numbers could differ — what the table promises is the right-hand
column, which is written directly onto the resolved parameters.)

These are *starting* values: the scenario config dialog still lets you change them before
you press play, and the shape-type distribution table is left exactly as vanilla has it.

Set `FirstPersonControl.ScenarioEnabled = false` and the scenario and its preset both
disappear from the menu.

**A First Person save needs this mod to open.** The save records its scenario id and the game
resolves it on load, so removing the mod makes that particular save fail.

The manifest still declares `AffectsSaveGames: false`, deliberately. Setting it true would
turn that failure into a polite refusal, but the same barrier also blocks mods that were
*added* — so every pre-existing save would stop loading the moment this mod was installed.
The mod does nothing to an ordinary save, so that trade buys nothing and costs the player
their library. Ordinary saves load normally, with or without it.

## Three things are researched

Walking and building are free. Flight needs **Jet Pack** (6k), fast travel needs **Waypoint
Travel** (5k), and riding trains needs **Train Riding** (4.8k) — three nodes in the research
shop's *First Person* category, each with its own icon. They are priced against the top of
the vanilla ladder, where 5k buys a third factory floor. Until they are bought, those keys
say so rather than doing nothing.

You cannot get stranded by that: falling past every floor puts you back on the last solid
ground by itself, and leaving the session leaves first person.

The nodes are definitions, so **restart the game** after installing — a hot reload will not
add them.

## Sensitivity

There is no settings page yet. Look sensitivity multiplies the game's own **camera drag
sensitivity** sliders in Settings → Camera, so those already work, and "invert vertical
axis" is honoured. Adjust them there; a mod-specific slider for the same quantity would
only be a second thing to keep in sync.

If it is still too fast or slow at the extremes, `LookSensitivity` in
`FirstPerson/FirstPersonTuning.cs` is the base value and everything else in that file is
tunable in the same way.

## For other mods

First Person is meant to be built on, and the whole public surface is one static class:

```csharp
using QuinnBast.Shapez2.FirstPerson;

FirstPersonControl.Forced = true;                   // first person in every save
FirstPersonControl.FlightEnabled = false;           // on foot, permanently
FirstPersonControl.TravelRequiresResearch = false;  // fast travel from the start
FirstPersonControl.WalkSpeed = 12f;
```

Thirty-odd plain statics, five `Action` fields for events, and one object holding the eleven
numbers the scenario's map generator uses. No interfaces, no generics, nothing to register -
so a mod that would rather not take a hard dependency can drive all of it reflectively.

What you can change: whether the player is locked in, whether the toggle and the scenario
exist at all, walk / fly / sprint / jump speeds, reach and ride height, whether belts carry
you, and for each of flight, fast travel and riding trains whether it is **off, free, or
bought** and what it costs.

**[EXTENDING.md](EXTENDING.md) is the full guide** - every member with its type and default,
the events and when they fire, the map-generation fields, worked recipes, when each setting is
read, and what is deliberately not exposed.

Two things that bite if you skip it: a research cost is the **stored** amount and the screen
shows it multiplied by a hundred, so `60` reads as "6k"; and the research settings are read
**when a scenario loads**, so they are the one group a constructor must set rather than
change later.

## Build

Needs `SPZ2_PATH`, `SPZ2_PERSISTENT` and `SPZ2_SHIFTER`.

```bash
dotnet build                 # installs to <persistent>/mods/FirstPerson   (game must be CLOSED)
dotnet build -p:Dev=true     # stages to <persistent>/mods-dev/FirstPerson (safe while running)
```

Hot reload picks up code changes — `mrl.reload firstperson` — but **not the research
nodes**, which are definitions built once per scenario load. Restart the game after changing
anything about them. The keybindings layer survives a reload: it detects that a previous
load already registered it rather than adding a duplicate section.

## How it works

The game's camera is already an orbit rig: a pivot on the island-layer plane, with the
camera hung off it at a distance (`zoom`) and a downward angle. First person is that rig
with the distance collapsed to zero, so the camera sits on the pivot and pitches freely.

Rather than loosen the game's clamps and let its controller run, the mod takes the camera
over for the frames it is active, and keeps `Viewport` in sync so culling, LOD and the HUD
all still agree with where the camera actually is.

Physics is not a rigidbody — there is no physics world in shapez to join. The body asks the
map model what is in each tile, which on a one-tile grid is both cheaper and more
predictable.

[DESIGN.md](DESIGN.md) has the details, the reasoning, and what the next run is meant to
find out.
