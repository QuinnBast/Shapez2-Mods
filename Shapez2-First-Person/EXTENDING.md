# Extending First Person

First Person is meant to be built on. Another mod can lock players into it permanently, turn
the research gates off, reprice them, change how fast the player walks, reshape the map the
scenario generates, or react to the player standing up and sitting down.

All of it is on one static class, `FirstPersonControl`, plus one plain object hanging off it
for the map. There are no interfaces to implement, no types to derive from, and no
registration call.

```csharp
using QuinnBast.Shapez2.FirstPerson;

public class MyMod : IMod
{
    public MyMod(ILogger logger)
    {
        FirstPersonControl.Forced = true;                      // every session, not a view
        FirstPersonControl.FlightEnabled = false;              // on foot, permanently
        FirstPersonControl.TravelRequiresResearch = false;     // fast travel from the start
        FirstPersonControl.TrainRidingResearchCostPoints = 20; // shows as "2k" - see below
    }
}
```

A mod constructor is early enough for everything here. See [when to set what](#when-to-set-what)
for the one case where it is not.

## Two ways in

**Reference the assembly** and the members are what they look like. This is the normal path.

**Or reach it reflectively**, if you would rather not take a hard dependency on another mod
being installed:

```csharp
Type control = Type.GetType("QuinnBast.Shapez2.FirstPerson.FirstPersonControl, FirstPerson");
control?.GetField("Forced")?.SetValue(null, true);
```

`?.` throughout, so a missing First Person is a no-op rather than a `TypeLoadException`.

That second consumer is the reason this surface is as dull as it is. Every member is a
**public static field** or a get-only property - no `event`s, no interfaces, no generics -
because anything richer serves the mod holding a reference and shuts out the one that is not.
Where that costs something, it is called out below.

## Locking the player in

| Member | Type | Default | |
| --- | --- | --- | --- |
| `Forced` | `bool` | `false` | Lock the player into first person for every session, the way the scenario does. |
| `LockedIn` | `bool` (get) | | `Forced`, or the mod's own scenario is running. This is what the camera actually reads. |
| `ToggleEnabled` | `bool` | `false` | Whether the toggle key can put an *ordinary* session into first person. Off means the binding is never registered at all. |
| `ScenarioEnabled` | `bool` | `true` | Whether the **First Person** scenario and its preset appear in the new-game menu. |
| `Active` | `bool` (get) | | Whether the player is in first person right now. |

`Forced` can be set mid-session and takes effect on the next frame.

Setting `Forced` does **not** require the scenario. Turning `ScenarioEnabled` off and `Forced`
on gives a mod first person in every save with no entry in the new-game menu at all, which is
the right shape if first person is a detail of your own scenario rather than the point of it.

## Movement and reach

Read live, every frame, so these can be changed mid-session and even per-frame.

| Member | Type | Default | |
| --- | --- | --- | --- |
| `WalkSpeed` | `float` | `8` | Tiles per second on foot. |
| `FlySpeed` | `float` | `60` | Tiles per second in the air, before sprinting. Vertical movement uses the same number. |
| `SprintMultiplier` | `float` | `3` | What the game's "move faster" key multiplies both by, climbing and diving included. |
| `JumpSpeed` | `float` | `11` | Tiles per second off the ground. The apex is `v^2/2g` with `g = 26`; the default clears three building layers and not four. |
| `BoardReach` | `float` | `40` | How far along the crosshair a train can be and still be boardable, in tiles. |
| `TrainRideHeight` | `float` | `7.8` | How far from a ridden wagon's origin the rider sits, in tiles - measured along the wagon's own up, so an upside-down rail hangs you underneath rather than burying you in it. |
| `BeltsCarryPlayer` | `bool` | `false` | Whether standing on a belt carries you along it. Off by default because it moves you away from whatever you were building. |
| `ResourceRenderRadius` | `float` | `2000` | How far shape and fluid patches stay drawn regardless of where you look, in world units (100 chunks). `0` leaves the game's own culling alone. |

**`JumpSpeed` is the one to be careful with.** Three building layers is not a round number that
fell out of taste - blueprints routinely occupy all three, and a player who cannot clear them
can be shut out of their own platform entirely. Raise it and the player clears four and starts
overshooting; lower it and you should check they can still get onto a full-height build. The
body scans for blockage from `floor(feet + StepHeight)` with `StepHeight = 1.15`, so clearing
N layers needs the feet above `N - 1.15`.

## The three gated features

Flight, waypoint fast travel and riding trains each take the same three settings, so there is
one shape to learn rather than three.

| Feature | `...Enabled` | `...RequiresResearch` | `...ResearchCostPoints` | `...Unlocked` (get) |
| --- | --- | --- | --- | --- |
| Flight | `FlightEnabled` | `FlightRequiresResearch` | `FlightResearchCostPoints` = `60` | `FlightUnlocked` |
| Fast travel | `TravelEnabled` | `TravelRequiresResearch` | `TravelResearchCostPoints` = `50` | `TravelUnlocked` |
| Riding trains | `TrainRidingEnabled` | `TrainRidingRequiresResearch` | `TrainRidingResearchCostPoints` = `48` | `TrainRidingUnlocked` |

Both `bool`s default to `true`, so all three ship as bought features.

| | |
| --- | --- |
| `...Enabled = false` | the feature does not exist - **no research node, and no row in the keybindings screen** |
| `...Enabled = true`, `...RequiresResearch = false` | available from the first frame, still no research node |
| both `true` (default) | a node appears in the research shop's *First Person* category |

"Off" is a deliberately different question from "not yet researched": a mod that switches a
feature off wants it gone, not pending, so it leaves no trace anywhere in the UI.

### Costs are multiplied by 100 on screen

`...ResearchCostPoints` is the **stored** amount, not the displayed one.
`Format(this ResearchPointCurrency)` renders `Amount * 100`, so `60` appears as "6k" and `48`
as "4.8k". Reading this backwards prices a node a hundredfold out and still looks entirely
plausible on screen, which is why it is worth restating here rather than only in the game's
own docs.

For scale, the authored ladder in `default-scenario.json` tops out at 42 for train transfer
stations, 48 for an extra rail line colour, 50 for a third factory or space floor, 90 for the
large platform pack and 200 for vortex delivery. Points arrive from side quests in ones and
twos, so these are small numbers carrying a lot of weight. The three defaults sit in that top
band on purpose - flight in particular is priced above the third-floor unlock because it does
not add somewhere to build, it removes traversal as a problem for the rest of the save.

## The scenario, and the map it generates

`FirstPersonControl.ScenarioMapGeneration` is a `FirstPersonMapGeneration` - a plain object
with plain public fields, for the same reason everything else here is plain.

```csharp
FirstPersonControl.ScenarioMapGeneration.ShapePatchLikelinessPercent = 300;
FirstPersonControl.ScenarioMapGeneration.SpiralGeneration = true;
```

| Field | Type | Default | |
| --- | --- | --- | --- |
| `SpiralGeneration` | `bool` | `false` | Cut the map into a spiral. Off here because a walking player meets an edge rather than a horizon. |
| `FluidPatchLikelinessPercent` | `int` | `250` | Two fluid patches per super chunk - see the arithmetic below. |
| `FluidPatchBaseSize` | `int` | `3` | |
| `FluidPatchSizeGrowPercentPerChunk` | `int` | `60` | |
| `FluidPatchMaxSize` | `int` | `8` | |
| `ShapePatchLikelinessPercent` | `int` | `200` | Two shape patches per super chunk. |
| `ShapePatchBaseSize` | `int` | `4` | |
| `ShapePatchSizeGrowPercentPerChunk` | `int` | `70` | |
| `ShapePatchMaxSize` | `int` | `8` | |
| `ShapePatchRareShapeLikelinessPercent` | `int` | `45` | |
| `ShapePatchVeryRareShapeLikelinessPercent` | `int` | `33` | |

The defaults are fewer and larger patches than the game's own, which is the shape a walking
player wants: you arrive at an asteroid on foot, so there should be something there when you
do.

### Percentages above 100 loop, they do not saturate

`DefaultMapGenerator` runs

```csharp
do { if (rng.TestPercentage(k)) ...; k -= 100; } while (k > 100);
```

so the count is `ceil(k / 100) - 1` patches per super chunk, each placed outright because `k`
is still over 100 when it is tested, and the trailing remainder is dropped rather than rolled.
**250 and 200 are both exactly two, and 199 is one.** A super chunk is 64x64 chunks, and the
game's own defaults are 15 and 30 - well under one patch each.

### What is deliberately not exposed

`ShapePatchGenerationLikeliness`, the table of which shape types appear at which distance from
the origin, is left exactly as the scenario's `#include` delivered it. It is authored
ScriptableObject data with no readable source, so there is nothing to offer you but a
fabrication - and a wrong distribution would be invisible until somebody walked far enough to
notice the wrong shapes.

## Events

Five of them, as plain `Action` **fields** rather than `event`s. An `event` exposes only its
add and remove accessors, and a mod driving this reflectively has to read the delegate,
combine, and write it back - which a field allows and an `event` does not.

**Use `+=`.** Assigning with `=` clobbers every other subscriber.

```csharp
FirstPersonControl.OnEntered += () => Log("standing up");
FirstPersonControl.OnRidingChanged += () => Log(FirstPersonControl.Riding ? "aboard" : "off");
```

| | |
| --- | --- |
| `OnEntered` | the player arrived in first person, automatic scenario entry included |
| `OnLeft` | they left - session over, `Forced` cleared, or the camera stood down |
| `OnFlyingChanged` | read `Flying` for which way |
| `OnRidingChanged` | read `Riding`. Covers stepping off, jumping off, losing the research, and the train being delivered into the hub underneath them |
| `OnTravelled` | fast travel finished, after the body has moved |

Plus the two get-only companions to that state: `Flying` and `Riding`.

All five are parameterless on purpose. Game types in the signatures would serve a referencing
consumer and shut out the reflective one, which is the trade the rest of this surface refuses.
Read the state properties instead.

They are raised from `ReportActive` / `ReportFlying` / `ReportRiding`, the single choke point
for each flag, and each compares before it assigns. That matters more than it looks:
`Body.Flying` and `Body.Riding` are written from about a dozen places - the toggle, the
per-frame research re-check, travel, losing a train, leaving the session - and an event raised
at each of them would fire repeatedly for one change.

**A subscriber that throws is caught and written to the log**, not allowed to escape. These are
raised from inside the camera update, so an escaping exception would freeze the view rather
than show anyone the bug.

## Diagnostics

| Member | Type | Default | |
| --- | --- | --- | --- |
| `LogToolbar` | `bool` | `false` | Log every hotbar selection the flat wheel-walk makes, with the element it landed on and why. |

Left in place rather than deleted because it earned that: it is what found the interaction
scope being clobbered on selection, and the `values[0]` fallback in
`PrioritizeToolbarElementFromCurrentCategorySelector`. Neither was visible from reading the
code, and neither was guessable from watching it happen.

## When to set what

Set everything from your mod's constructor. That runs at mod load, before any scenario, which
is early enough for all of it.

| | Read |
| --- | --- |
| `ScenarioEnabled` | while game data loads - after mods are constructed |
| `ScenarioMapGeneration` | when the new-game menu asks for the presets |
| `ToggleEnabled`, the three `...Enabled` | when the keybindings register, on the first tick |
| research options | when a scenario loads |
| `Forced`, movement speeds, reach, ride height | live, every frame |

**The research options are the exception to "change it whenever you like".** A node is a
definition, and definitions are built once per scenario load, so repricing or disabling one
after a scenario has loaded does nothing until the next one. Everything else can be changed
mid-session and takes effect on the next frame.

## Recipes

**A walking-only survival game.** No flight, no fast travel, trains the only way to cover
distance - and free, so the player is not stranded before they can afford one.

```csharp
FirstPersonControl.Forced = true;
FirstPersonControl.FlightEnabled = false;
FirstPersonControl.TravelEnabled = false;
FirstPersonControl.TrainRidingRequiresResearch = false;
```

**First person as a free camera mode.** No scenario in the menu, nothing gated, the player
toggles in and out of any save.

```csharp
FirstPersonControl.ScenarioEnabled = false;
FirstPersonControl.ToggleEnabled = true;
FirstPersonControl.FlightRequiresResearch = false;
FirstPersonControl.TravelRequiresResearch = false;
FirstPersonControl.TrainRidingRequiresResearch = false;
```

**A denser map to walk.** Four shape patches per super chunk rather than two, and rare shapes
closer to hand.

```csharp
var map = FirstPersonControl.ScenarioMapGeneration;
map.ShapePatchLikelinessPercent = 500;   // ceil(500/100) - 1 = 4
map.ShapePatchRareShapeLikelinessPercent = 70;
```

**React to the player taking off.**

```csharp
FirstPersonControl.OnFlyingChanged += () =>
{
    if (FirstPersonControl.Flying) MyHud.Hide();
    else MyHud.Show();
};
```

## What you cannot change from here

Worth knowing before you go looking for a setting that is not there.

- **The keybindings themselves.** They are real game keybindings under *First Person* in the
  settings screen, so the player rebinds them and a mod does not. What a mod controls is
  whether a binding is registered at all - that is what `...Enabled` does.
- **The research nodes' identity** - their category, icons and position. Only whether they
  exist and what they cost.
- **The shape-type distribution**, for the reason above.
- **Anything about research after a scenario has loaded.** See the timing table.

## Behaviour worth knowing

- **Leaving a session exits first person**, because the body's position belongs to a map that
  no longer exists. A forced player is put straight back in on the next session.
- **Locks are re-checked every frame, not latched.** Loading a save where flight is not bought
  puts the player on the floor rather than leaving them airborne, and off a train rather than
  riding one they are not entitled to. A mod that sets `FlightEnabled = false` mid-flight
  lands the player rather than leaving them stuck.
- **A forced player who cannot fly cannot easily find resource islands** - the shape-resource
  overlay is shown while flying. If you lock players in, either turn `FlightRequiresResearch`
  off or expect them to buy it early.
- **None of these settings are saved.** Set them every session from your own load path. What
  *is* saved is the player's own position, so they resume where they left off; that is the
  mod's own save data and not something to drive from here.
- **A First Person save needs this mod to open**, because the save records its scenario id.
  Ordinary saves are unaffected either way - the manifest declares `AffectsSaveGames: false`
  deliberately.

## Where the reasoning lives

[DESIGN.md](DESIGN.md) records why the mod is built the way it is, what has been verified at
runtime against which class, and what the next play session is meant to find out. If something
here surprises you, the answer is usually there.
