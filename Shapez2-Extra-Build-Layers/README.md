# Extra Build Layers

More floors inside every platform, and a much taller stack of platforms in space.

| | Default | |
|---|---|---|
| **Machine levels** | +17 | building layers inside every platform — floors 0-19, all there is room for |
| **Space levels** | +997 | platform layers above the current top one - up to level 999 |

Both are configurable. Machine levels are hard-capped at floor 19 — a 20th floor would be the
ground floor of the space layer above, because the game encodes both in one coordinate — and only
the bottom three can carry a port onto a space belt or space pipe; see
[The one thing to know about tall platforms](#the-one-thing-to-know-about-tall-platforms).
Space levels are capped at layer 999 (`BuildLayers.MaxSpaceLevel`) — a safety margin rather than
an engine limit: layer numbers are drawn for up to three digits.
Asking for more, from the config or from another mod, is clamped and logged, never fatal.

What a player gets on top of the raw layers:

- **Tall lifts and pipes.** Drag a belt or pipe up or down any number of floors and it places one
  lift for the whole climb, drawn full height, with items animated all the way. Stands under belts,
  pipes and wires reach the floor from any height.
- **Lift stations for space.** Drag a space belt or space pipe up or down any number of space levels
  and it places an entry station at the bottom and an exit at the top, joined by a shaft. Items
  take time to climb in proportion to the height, at full belt throughput. Click anywhere on the
  shaft to select the lift; deleting either station removes both; a station's panel has
  **View Entry** / **View Exit**.
- **Getting around.** Hold the layer keys to keep moving (it speeds up the longer you hold). Zoomed
  all the way out, the overview stays on your layer and each press moves a tenth of the unlocked
  range. A **View** action (magnifying glass, key **Z**) beside Copy/Cut/Delete takes you to the
  selected platform or building.
- **High layers stay visible.** The game culls everything above space layer 2 unless you look
  straight down; the mod fixes that.
- **Numbered floors.** Every new platform floor and space layer is labelled with its own number,
  drawn at load in the game's own style - not "01, 02, 01, 02".
- **A layer readout that scales.** Above 8 layers the HUD strip becomes the current layer's number
  instead of a stack of handles.
- **Previews and hovers that stay lit.** The game's placement preview and hover shaders go dark
  from floor 10 up; the mod swaps in one that does not.
- **Works on an existing save** - see [Save games](#save-games).

## It is a substrate, not a campaign

Creating a layer and deciding how the player earns one are different problems, and only the
first has a single right answer. So this mod does the engine plumbing and hands the progression
question to a replaceable policy:

```csharp
// from your mod's constructor — mods are all constructed before the first scenario is built
BuildLayers.RequireExtraSpaceLevels(50);                // at least 50; never lowers another mod's ask
BuildLayers.RequireExtraMachineLevels(10);
BuildLayers.UnlockPolicy = new GrantedUnlockPolicy();   // all 50 from the first frame
```

### Using it from another mod

1. Reference `ExtraBuildLayers.dll` at compile time only (`<Private>False</Private>`) — never ship
   a copy; two copies of one assembly in the game is its own bug.
2. List it in your manifest's `Dependencies`. That is not a formality: the game's `ModLoader`
   constructs mods in dependency order, which is what puts your constructor **after** this mod's,
   so your settings override its `config.json` instead of being overwritten by it.
3. Set things in your constructor. Values are read once per scenario load; changing them
   mid-session takes effect at the next load. `BuildLayers.Registered` tells you what a session
   actually got.

Prefer `RequireExtra…Levels` over assigning `Extra…Levels` when you need *some* floors: two mods
that assign overwrite each other, two that require both get what they asked for.

To cooperate only when the mod happens to be installed, without depending on it, look it up by
reflection and check `BuildLayers.ApiVersion` (currently `1`) first:

```csharp
Type api = Type.GetType("QuinnBast.Shapez2.ExtraBuildLayers.BuildLayers, ExtraBuildLayers");
if (api != null && (int)api.GetField("ApiVersion").GetValue(null) >= 1)
    api.GetMethod("RequireExtraSpaceLevels").Invoke(null, new object[] { 30 });
```

Your own buildings need nothing: every building definition gets per-floor meshes for the new
floors, including ones registered by mods that load after this one.

Three policies ship:

| Policy | |
|---|---|
| `ShopUnlockPolicy` | Buy them in tiers (`MachineTiers`, `SpaceTiers`), or `LayersPerEntry` at a time. Chained and escalating. The default. |
| `GrantedUnlockPolicy` | All of them, no purchase, no research. For sandbox modes and testing. |
| `NoUnlockPolicy` | Layers exist, nothing unlocks them. For driving the progression yourself. |

Write your own by implementing `ILayerUnlockPolicy` — the `LayerUnlockContext` you get hands you
the mechanic ids, the gate to hang the first one off, a borrowed preview image, and the shop
registration plumbing.

To react to what a session actually got:

```csharp
BuildLayers.Registered += registration =>
{
    int top = registration.TopSpaceLevel;   // the real top altitude
    foreach (AddedLayer layer in registration.SpaceLevels) { /* ... */ }
};
```

Use `TopSpaceLevel`, not `GameMode.MaxIslandLayer` — the latter is the unlock *count* and
over-reports on any scenario that also hands out a layer below the start.

## Why this is a small mod

Neither ceiling is a constant:

```csharp
// GameMode.cs
public short MaxBuildingLayer => (short)Mechanics.BuildingLayerUnlocks.Count;
public short MaxIslandLayer   => (short)(MinIslandLayer + Mechanics.IslandLayerUnlocks.Count);
```

A layer exists because a research mechanic exists to unlock it. Adding fifty is adding fifty
mechanics. The HUD layer strip builds itself from the same numbers and rebuilds when research
changes, so it grows on its own.

Two things are not data, and they are most of the code here:

**The art.** `BuildingDrawDataFactory` builds each building's per-layer mesh array to a
hardcoded length of three, and the chunk mesh builder indexes it by the building's layer — so a
machine above the third floor throws and takes its whole chunk's mesh down. That is why extra
machine levels have generally been considered impossible. It is also the only place that array
is indexed, so widening it is the whole fix.

**The layer strip.** It grows nine pixels a layer. Fifty layers is over 500px and runs off a
1080p screen, so the mod rescales it to fit — and leaves it alone entirely while it already
fits.

Both are written up in [DESIGN.md](DESIGN.md).

## The one thing to know about tall platforms

**Only building layers 0, 1 and 2 can carry a port onto a space belt or space pipe.** Build as
high as you like; to put anything onto a space belt or pipe, bring it back down to the bottom three
floors. Belts between two platforms that touch are not affected - they transfer on every floor.

`SpacePathPortSystem.CreateAllSenderSimulations` and its receiver twin are what hand items between
a platform and the space path at its edge, and they iterate building layers over a hardcoded
`num2 < 3`. A belt or fluid port onto a space path above layer 2 gets no simulation at all. The class is
`SpacePathPortSystem<TInput, TOutput>` — a generic type, whose methods MonoMod cannot hook — and
the placement-side scan in `NotchConnectorsExtender` is a generic method, equally out of reach.
There is no version of this mod that lifts it.

In practice it reads as a constraint rather than a bug: a vertical factory has to route its
output back down to ship it out by space belt. Everything else reaches every floor — machines, belts, in-platform
lifts (`LayerLiftPlacementTracker` follows `MaxBuildingLayer`), the platform floor art, and even
the hub's own input slots.

The other limit is a hard clamp at **19 layers total**. A building's layer is
`SafeMod(tile.z, 20)`, so layer 20 would be layer 0 of the platform stack above it. Ask for more
and the mod clamps and says so.

## Configuration

Copy `config.example.json` to `config.json` beside the assembly:

```
%APPDATA%\..\LocalLow\tobspr Games\shapez 2\mods\ExtraBuildLayers\config.json
```

| Key | Default | |
|---|---|---|
| `ExtraMachineLevels` | `17` | Building layers inside a platform (clamped so the top floor is at most 19) |
| `ExtraSpaceLevels` | `997` | Platform layers in space (clamped so the top layer is at most 999) |
| `UnlockPolicy` | `"Shop"` | `Shop`, `Granted` or `None` |
| `MachineLevelTiers` | `[3, 10, 19]` | The level each machine-level purchase ends at: level 3, then 4-10, then 11-19 |
| `SpaceLevelTiers` | `[25, 250, 999]` | The same for space levels: 3-25, then 26-250, then 251-999 |
| `LayersPerShopEntry` | `100` | Layers per purchase when a tier list is empty; `1` gives one entry per layer |
| `MachineLevelBaseCost` | `2500` | Research points for the first entry — rendered ×100, so this is "250k" |
| `SpaceLevelBaseCost` | `4000` | |
| `CostGrowthFactor` | `1.6` | Per shop entry |
| `LayerKeyRepeatDelay` | `1.0` | Seconds a layer key is held before it repeats, speeding up the longer it is held; `0` turns it off |
| `OverviewLayerStep` | `0` | Space layers per key press in the overview; `0` is a tenth of the unlocked range |
| `JumpToSelectedLayer` | `true` | Selecting a single platform on another layer (zoomed in) moves the view to its layer |
| `ShopCategory` | `"ExtraBuildLayers"` | A custom name needs its own translation key |
| `CompactLayerStrip` | `true` | Rescale the HUD layer strip to fit |
| `LayerStripMaxHeight` | `420` | Pixels |
| `LayerStripNumberAbove` | `8` | Above this many layers, show the layer number instead of handles. `0` never |
| `AllowAddingToExistingSaves` | `true` | Let a save made without the mod be opened with it |
| `ExtendLayerRamps`, `RefreshLayerShaderParams` | `true` | Diagnostic switches for shading on high floors |

A missing or malformed file is not an error. The config only *seeds* `BuildLayers`; a dependent
mod setting those properties wins.

## Save games

**Adding** the mod to an existing save works: open it and the new layers appear in the research
shop. **Removing** it from a save that has been saved with it is refused by the game, as it should
be — tall lifts would vanish and buildings above floor 2 would break their chunks.

That asymmetry is deliberate. The manifest says `AffectsSaveGames: true`, which the game enforces
in both directions; the mod removes only itself from the game's "added mods" check, so the save
loads, and from its next save on it requires the mod. Set `AllowAddingToExistingSaves` to `false`
to get the game's stricter behaviour back.

**Lowering a count is also unsafe on an existing save.** Going from 30 space levels back to 10
leaves platforms at altitudes the placement bounds now reject. Raising is fine.

## Building

```bash
dotnet build                 # installs into <persistent>/mods/ExtraBuildLayers   (game must be CLOSED)
dotnet build -p:Dev=true     # stages into <persistent>/mods-dev/ExtraBuildLayers
```

Hot reload does not help, and is not safe: most of the mod runs while the scenario, buildings
and islands are built, once per session, and islands cannot be registered twice. Restart to test.

Two diagnostic console commands stay in: `extrabuildlayers.dump-layer-visuals` (layer materials,
textures and shader globals to `dump/`) and `extrabuildlayers.dump-lifts` (lift connector geometry).

## Status

Played at 999 space levels: layers unlock from the shop; tall in-platform lifts carry and animate
items the full height in both directions; lift stations for belts and pipes, up and down, carry
items with travel time and keep them through a save and reload; shafts draw and can be clicked;
startup is quick; floors and space layers are numbered; previews stay lit; the overview, View and
the layer keys get around.

Fixed since the last play session and not yet seen in game: stands reaching the floor, the hover
glow on tall buildings, and Z no longer firing on Ctrl+Z.

Not yet exercised: tall in-platform **pipes** end to end, adding the mod to a save that never had it
(the `SavegameAdoption` path), and a second mod using the API.

## Licence

Apache 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
