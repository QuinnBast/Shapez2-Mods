# How do I…?

Task-first recipes. Each is a problem, the code, and the gotcha that will bite you.

Where a page is derived from the game's own implementations rather than from a working
sample, it says so in a **Note** — those are the ones to verify against your game
version before building on them.

## Adding content

| Task | Recipe |
| --- | --- |
| Add a new machine | [Add a building](add-a-building.md) |
| Add a new platform or island | [Add an island or platform](add-an-island.md) |
| Add a new shape quadrant type | [Add a shape part](add-a-shape-part.md) |
| Put it on the toolbar so it can be selected | [Add to the toolbar](add-to-toolbar.md) |
| Make it unlockable through research | [Add a research unlock](add-research-unlock.md) |
| Give it a page in the knowledge panel | [Add a wiki entry](add-a-wiki-entry.md) |
| Give it a name that is not a translation key | [Add translations](add-translations.md) |
| Load a `.fbx` model, an icon, or an asset bundle | [Load models and icons](load-models-and-icons.md) |
| Give it sound | [Add sounds](add-sounds.md) |
| Make it scale with the player's research | [Speed upgrades and buffs](speed-upgrades-and-buffs.md) |
| Show what it *would* produce during placement | [Placement previews](placement-predictions.md) |
| Add behaviour spanning many entities | [Add a simulation system](add-simulation-system.md) |
| Change the rules of the game | [Custom scenarios](custom-scenarios.md) |

New content needs **all four** of definition, toolbar entry, research unlock, and
translations. Miss the unlock and the toolbar entry stays hidden; miss the translation
and the player sees a raw key. [Add a building](add-a-building.md) is the spine — start
there and follow the links.

## Reading a running game

| Task | Recipe |
| --- | --- |
| Run something once a save is actually open | [Run code when a game loads](run-code-when-game-loads.md) |
| Do work every frame without tanking the framerate | [Run code when a game loads](run-code-when-game-loads.md#do-not-do-heavy-work-every-tick) |
| Find every building of a given type | [Find buildings](find-buildings.md) |
| Find the building at a tile | [Find buildings](find-buildings.md#the-building-at-a-tile) |
| Find what the player has selected | [React to the player's selection](react-to-selection.md) |
| Tell whether a machine is running, starved, or backed up | [Read machine state](read-machine-state.md) |
| Measure a machine's real throughput | [Read machine state](read-machine-state.md#measuring-actual-throughput) |
| Gate a feature on the player's progress | [Read research progress](read-research-progress.md) |
| Get production rates over time | [Read production statistics](read-statistics.md) |
| Measure what a belt or machine is actually moving | [Measure throughput](measure-throughput.md) |
| Tell a bottleneck from a starved machine | [Measure throughput](measure-throughput.md#throughput-alone-cannot-find-a-bottleneck) |
| Total up what a platform ships | [Measure throughput](measure-throughput.md#there-is-more-than-one-way-off-a-platform) |
| Know which shape will arrive somewhere, before it does | [Read shape predictions](read-shape-predictions.md) |
| Tell whether a region of factory is simple enough to reason about | [Read shape predictions](read-shape-predictions.md#degenerated-is-the-games-own-give-up) |
| Work out what crosses the boundary of a set of platforms | [Ports and Notches](../ports-and-notches.md) |
| Measure a factory's ceiling rather than its current rate | [Run a detached simulation](run-a-detached-simulation.md) |

## Game systems

| Task | Recipe |
| --- | --- |
| Read or transform a shape | [Work with shapes](work-with-shapes.md) |
| Read tank levels and pipe networks | [Work with fluids](work-with-fluids.md) |
| Read a wire, or react to a signal | [Work with signals and wires](work-with-signals.md) |
| Read where trains are and what they carry | [Work with trains](work-with-trains.md) |
| Read or add to the blueprint library | [Work with blueprints](work-with-blueprints.md) |
| Read what is inside a blueprint, or expand one myself | [Work with blueprints](work-with-blueprints.md#reading-what-is-inside-a-blueprint) |

## Showing things to the player

| Task | Recipe |
| --- | --- |
| Add a button to the bottom-right visualization bar | [Add a visualization toggle](add-a-visualization-toggle.md) |
| Draw a coloured marker over a building or platform | [Draw in the world](draw-in-world.md) |
| Draw text in the world without a font | [Rendering](../rendering.md#world-space-text-without-a-font) |
| Stop an overlay showing through the platform above it | [Rendering](../rendering.md#depth-layers-and-overlays) |
| Draw only when zoomed out to the platform view | [Draw in the world](draw-in-world.md#only-in-overview-mode) |
| Show a message, or open the research/statistics screen | [Notifications and HUD screens](notifications-and-hud-screens.md) |
| Add a section to the selected building's panel | [Building side panel](building-side-panel.md) |
| Add a section to the selected platform's panel | [Platform side panel](island-side-panel.md) |
| Show a live rate in a panel | [Platform side panel](island-side-panel.md#reuse-the-efficiency-gauge) |

## Tooling and shipping

| Task | Recipe |
| --- | --- |
| Toggle my feature with a key | [Bind a key](keybind-toggle.md) |
| Add a dev console command | [Add a console command](console-command.md) |
| Store my own data in the player's save | [Store data in the save](save-data.md) |
| Work out why nothing is happening | [Debug a mod](debugging.md) |
| Find where a mod's frames and memory go | [Profile a mod](profile-a-mod.md) |
| Walk the managed heap from inside the game | [Profile a mod](profile-a-mod.md#walking-the-managed-heap) |
| Get my mod to players | [Publish to the Steam Workshop](publish-to-workshop.md) |

## Working out the rest yourself

When there is no recipe, the fastest route is to find where the game does the same thing
and copy it — see [Exploring the Assemblies](../exploring-assemblies.md). The
[reference pages](../map-model.md) explain the types those answers are made of.

Two shortcuts worth internalising:

- **`F1` → `debug.export-game-data`** answers most "what is this thing called?"
  questions — definition ids, milestone ids, scenario data.
- **`grep` the decompiled tree** for a method name to find every vanilla caller, which
  shows you the intended usage far faster than reading signatures.
