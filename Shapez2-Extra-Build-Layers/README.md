# Extra Build Layers

More floors inside every platform, and a much taller stack of platforms in space.

| | Default | |
|---|---|---|
| **Machine levels** | +17 | building layers inside every platform — floors 0-19, all there is room for |
| **Space levels** | +20 | platform layers above the current top one |

Both are configurable. Machine levels are hard-capped at floor 19 — a 20th floor would be the
ground floor of the space layer above, because the game encodes both in one coordinate — and only
the bottom three can carry a port to another platform; see
[The one thing to know about tall platforms](#the-one-thing-to-know-about-tall-platforms).
Space levels are capped at layer 999 (`BuildLayers.MaxSpaceLevel`) — a safety margin rather than
an engine limit: layer numbers are drawn for up to three digits.
Asking for more, from the config or from another mod, is clamped and logged, never fatal.

What a player gets on top of the raw layers:

- **Tall lifts and pipes.** Drag a belt or pipe up or down any number of floors and it places one
  lift for the whole climb, drawn full height, with items animated all the way. Space belt and
  space pipe lifts reach every space level too.
- **Numbered floors.** Every new platform floor and space layer is labelled with its own number,
  drawn at load in the game's own style - not "01, 02, 01, 02".
- **A layer readout that scales.** Above 8 layers the HUD strip becomes the current layer's number
  instead of a stack of handles.
- **Previews that stay lit.** The game's placement preview shader goes dark from floor 10 up; the
  mod swaps in one that does not.
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
| `ShopUnlockPolicy` | Buy them, `LayersPerEntry` at a time (100 by default). Chained and escalating. The default. |
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

**Only building layers 0, 1 and 2 can carry a port to another platform.** Build as high as you
like; to get anything *off* the platform, bring it back down to the bottom three floors.

`SpacePathPortSystem.CreateAllSenderSimulations` and its receiver twin are what actually move
items between platforms, and they iterate building layers over a hardcoded `num2 < 3`. A belt or
fluid port above layer 2 gets no simulation at all. The class is
`SpacePathPortSystem<TInput, TOutput>` — a generic type, whose methods MonoMod cannot hook — and
the placement-side scan in `NotchConnectorsExtender` is a generic method, equally out of reach.
There is no version of this mod that lifts it.

In practice it reads as a constraint rather than a bug: a vertical factory has to route its
output back down to export it. Everything else reaches every floor — machines, belts, in-platform
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
| `ExtraSpaceLevels` | `20` | Platform layers in space (clamped so the top layer is at most 999) |
| `UnlockPolicy` | `"Shop"` | `Shop`, `Granted` or `None` |
| `LayersPerShopEntry` | `100` | Layers per purchase; `1` gives one chained entry per layer |
| `MachineLevelBaseCost` | `2500` | Research points for the first entry — rendered ×100, so this is "250k" |
| `SpaceLevelBaseCost` | `4000` | |
| `CostGrowthFactor` | `1.6` | Per shop entry |
| `LayerKeyRepeatDelay` | `1.0` | Seconds a layer key is held before it repeats, speeding up the longer it is held; `0` turns it off |
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

Played at the earlier defaults (+10 / +10): layers unlock from the shop, tall belt
lifts carry and animate items the full height in both directions, space belt and pipe lifts reach the top, floors and
space layers are numbered, previews stay lit on floors 10-12, and the HUD shows the layer number.

Not yet exercised in game: the current defaults (+17 / +20), tall in-platform **pipes** end to
end, a save/reload with items mid-lift, adding the mod to a save that never had it (the
`SavegameAdoption` path), counts far from the defaults, and a second mod using the API.

## Licence

Apache 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
