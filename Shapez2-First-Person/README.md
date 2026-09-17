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
| Vortex spawn | works (played) |
| Rebindable keys in the game's settings | works (played) |
| Ctrl+wheel for space scope | compiled, not yet played |
| Research gates, configurable reach | compiled, not yet played |
| Riding belts | works (played) |
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
| `Space` | jump, or rise while flying |
| `LeftCtrl` | sink while flying |
| `Q` / `E` | layer down / up (yours to pick while flying) |
| double-tap `Space` | fly / noclip — needs the flight research |
| `M` | the same, as a fixed key |
| `Tab` (hold) | release the mouse to click the HUD, side panels and menus |
| `F5` | fast travel to your next waypoint |
| click a waypoint or the home icon | travel there on foot, not in space view |
| `F` | board the train you are looking at, or step off (jump works too) — stands aside for mirror while you are holding a building |
| mouse | look |
| wheel | previous / next toolbar (belts, fluids, platforms) |
| `Ctrl` + wheel | step a level deeper into the toolbar, or back out — the plain wheel then cycles at that level |
| stand on a belt | it carries you, at the belt's real speed |
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

First Person is meant to be built on. `FirstPersonControl` is the entire public surface —
plain statics, no events, interfaces or generics.

```csharp
using QuinnBast.Shapez2.FirstPerson;

public class MyMod : IMod
{
    public MyMod(ILogger logger)
    {
        FirstPersonControl.Forced = true;                     // a first-person game
        FirstPersonControl.FlightEnabled = false;             // on foot, permanently
        FirstPersonControl.TravelRequiresResearch = false;    // fast travel from the start
        FirstPersonControl.TrainRidingResearchCostPoints = 20; // shows as "2k"
    }
}
```

### The members

| Member | Type | Default | |
| --- | --- | --- | --- |
| `Forced` | `bool` | `false` | Lock the player into first person for every session, the way the scenario does. |
| `LockedIn` | `bool` (get) | | `Forced`, or the mod's own scenario is running. This is what the camera reads. |
| `ToggleEnabled` | `bool` | `false` | Whether the toggle key can put an ordinary session into first person. Off means the binding is not registered at all. |
| `WalkSpeed` | `float` | `8` | Tiles per second on foot. |
| `FlySpeed` | `float` | `60` | Tiles per second in the air, before sprinting. Vertical movement uses the same number. |
| `SprintMultiplier` | `float` | `3` | What the game's "move faster" key multiplies both by, climbing and diving included. |
| `ResourceRenderRadius` | `float` | `2000` | How far shape and fluid patches stay drawn regardless of where you look, in world units (100 chunks). `0` leaves the game's culling alone. |
| `TrainRideHeight` | `float` | `7.8` | How far from a ridden wagon's origin the rider sits, in tiles — measured along the wagon's own up, so an upside-down rail hangs you underneath. |
| `Active` | `bool` (get) | | Whether the player is in first person right now. |
| `ScenarioEnabled` | `bool` | `true` | Whether the **First Person** scenario and its preset appear in the new-game menu. |
| `FlightEnabled` | `bool` | `true` | Whether flight is offered at all. |
| `FlightRequiresResearch` | `bool` | `true` | Whether flight must be bought. |
| `FlightResearchCostPoints` | `int` | `60` | Cost of the flight node. |
| `FlightUnlocked` | `bool` (get) | | Can the player fly right now, by either route. |
| `TravelEnabled` | `bool` | `true` | Whether waypoint fast travel is offered at all. |
| `TravelRequiresResearch` | `bool` | `true` | Whether fast travel must be bought. |
| `TravelResearchCostPoints` | `int` | `50` | Cost of the travel node. |
| `TravelUnlocked` | `bool` (get) | | Can the player fast travel right now. |
| `TrainRidingEnabled` | `bool` | `true` | Whether riding trains is offered at all. |
| `TrainRidingRequiresResearch` | `bool` | `true` | Whether riding trains must be bought. |
| `TrainRidingResearchCostPoints` | `int` | `48` | Cost of the train node. |
| `TrainRidingUnlocked` | `bool` (get) | | Can the player ride trains right now. |

### Off, free, or bought

The three gated features take the same three options each, so there is one shape to learn:

| | |
| --- | --- |
| `…Enabled = false` | the feature does not exist — **no research node, and no row in the keybindings screen** |
| `…Enabled = true`, `…RequiresResearch = false` | available from the start, still no research node |
| both `true` (default) | a node appears in the research shop's *First Person* category |

"Off" is a deliberately different question from "not yet researched": a mod that switches a
feature off wants it gone, not pending.

### Costs are multiplied by 100 on screen

`…CostPoints` is the **stored** amount, not the displayed one. `Format(this
ResearchPointCurrency)` renders `Amount * 100`, so `30` appears as "3k" and `50` as "5k".
Reading this backwards prices a node a hundredfold out and still looks entirely plausible.

The defaults are priced against the authored ladder in `default-scenario.json`, whose top
end is 42 for train transfer stations, 48 for an extra rail line colour, 50 for a third
factory or space floor, 90 for the large platform pack and 200 for vortex delivery. Points
arrive from side quests in ones, twos and threes, so these are small numbers carrying a lot
of weight.

All three sit in that top band on purpose. Flight is above the third-floor unlock because
it is worth more than one — it does not add somewhere to build, it removes traversal as a
problem for the rest of the save.

### When to set them

Set everything from your mod's constructor. That runs at mod load, before any scenario,
which is early enough for all of it:

| | |
| --- | --- |
| `ScenarioEnabled` | read while game data loads, which is after mods are constructed |
| `ToggleEnabled` | read when the keybindings register, on the first tick |
| movement speeds and the ride height | read live, every frame |
| research options | read when a scenario loads |
| `…Enabled` | also read when the keybindings register, on the first tick |
| `Forced`, and everything else | read live, every frame |

`Forced` can be set mid-session and takes effect on the next frame. The research options
cannot — a node is a definition, so changing them after a scenario has loaded does nothing
until the next one.

### Without a reference

Every member is a plain static, so a mod that would rather not take a hard dependency on
another mod can do the same thing reflectively:

```csharp
Type control = Type.GetType("QuinnBast.Shapez2.FirstPerson.FirstPersonControl, FirstPerson");
control?.GetField("Forced")?.SetValue(null, true);
```

That is the reason the surface is as dull as it is — anything richer would serve a
referencing consumer and shut out this one.

### Behaviour worth knowing

- **Leaving a session exits first person**, because the body's position belongs to a map
  that no longer exists. A forced player is put straight back in on the next session.
- **Locks are re-checked, not latched.** Loading a save where flight is not bought puts the
  player on the floor rather than leaving them airborne, and off a train rather than riding
  one they are not entitled to.
- **A forced player who cannot fly cannot easily find resource islands** — the shape-resource
  overlay is shown while flying. If you lock players in, either turn
  `FlightRequiresResearch` off or expect them to buy it.
- **Nothing here is saved.** Set it every session from your own load path.

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
