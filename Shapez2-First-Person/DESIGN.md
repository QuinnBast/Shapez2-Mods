# First Person — design

Stand on the factory floor: walk, fall, and build at a crosshair.

Everything below was read out of `decompiled/`. The camera half has been run; the physics
and crosshair halves are compiled but not yet played — the open questions at the bottom say
which is which.

## The camera is an orbit rig, and that is the whole trick

`CameraController.ApplyCameraTransform` (`SPZGameAssembly/CameraController.cs:493`) is the
entire camera:

```csharp
float y = math.sin(math.radians(angle)) * zoom;
transform.localPosition = new Vector3(0f, y, (0f - math.cos(math.radians(angle))) * zoom);
transform.localRotation = Quaternion.Euler(angle, 0f, 0f);
```

The parent is placed by `ApplyDirtyPosition` (`:225`) at
`(Viewport.Position.x, Viewport.Height, Viewport.Position.y)` and yawed by
`Viewport.RotationDegrees` in `Update_ApplyDirtyChangesToCamera` (`:440`).

So `angle` is pitch measured **downward from the horizon** — 90 is top-down — and `zoom`
is the orbit radius. First person is the same rig with the radius collapsed: the camera
lands on the pivot and pitches freely. No second camera, no re-parenting.

The clamps that stop it in vanilla are `MinAngle = 30` and `MinZoom = 4`
(`:72`, `:77`), both `private set` on a static. The game loosens exactly those two itself
in the `camera.disable-restrictions-danger` debug command (`:522`), which sets them to 5
and 0.1 — proof the rest of the engine tolerates it.

### Scale

One world unit is one building tile: `ScreenUtils.RaytraceTileCoordinates` steps its DDA
by `1f`, and the chunk walk beside it steps by `20f`. A chunk is 20 tiles and an island
layer is 20 units of height (`ScreenUtils` indexes planes at `layer * 20`). A belt is one
tile wide, so a human eye height of ~1.7 is a real number rather than a guess.

Near clip is already 0.4 and FOV 45, set in `GameSessionOrchestrator.SetupCameras`
(`Game.Orchestration/GameSessionOrchestrator.cs:753-755`). The near plane is fine for
standing on a belt. The FOV is not — 45 is claustrophobic at head height, so the mod
raises it to 75.

**The transparent overlay camera must move with it.** `SetupCameras` parents
`OverlayTransparentCamera` to `MainCamera` at identity and sets both FOVs in the same
loop. Changing only `MainCamera.fieldOfView` makes the overlay disagree with the world.

### `WorldCoordinate` is not Unity's coordinate system

This one is a trap worth stating on its own, because it is silent:

```csharp
public static implicit operator float3(WorldCoordinate v) => new float3(v.x, v.z, 0f - v.y);
public static implicit operator WorldCoordinate(float3 v) => new WorldCoordinate(v.x, 0f - v.z, v.y);
```

`WorldCoordinate` is **Z-up, and its Y axis is the negation of Unity's Z**. A hand-written
axis swap that gets the sign wrong produces a world mirrored about one axis, which looks
almost right until you walk into something that is not where it is drawn. Every tile lookup
in this mod goes through the game's own operator for that reason.

`GlobalTileCoordinate.z` is height in tiles, and `WorldCoordinate.ToGlobalTileCoordinate`
*rounds* x and y but *floors* z — so tiles are centred on integers horizontally and span
`[z, z+1)` vertically. That asymmetry is what makes "the top of a building at layer B is at
`B + 1`" correct.

## Why the mod replaces the controller instead of unclamping it

Publicising `MinAngle`/`MinZoom` and letting the stock controller run *almost* works, and
it is the smaller change. Three behaviours make it the wrong one:

| Behaviour | Where | At zero radius |
| --- | --- | --- |
| Pan speed is multiplied by `Viewport.Zoom` | `ApplyMovementVector:283` | You cannot move. |
| Zoom-to-cursor correction differences two cursor→ground intersections and adds the result to `Viewport.Position` | `UpdateCameraOnZoomOrAngleChange:476` | Both terms diverge near the horizon; the camera teleports. Only affects players who enabled the setting, and suppressing it means writing a saved preference. |
| Angle and zoom re-clamped every frame from statics other code reads | `:410`, `:472` | `HUDCompass` and `HUDInteractionZoomManager` read the same statics. |

So the hook does not call the original while first person is active, and
`FirstPersonCamera.Apply` writes the transform directly.

It still keeps `Viewport` truthful, because **the viewport is what everything else reads** —
culling, LOD, the HUD, every placement raycast. A viewport that disagrees with the camera
desynchronises all of them.

Skipping the original also means taking over the three shader globals it owns:
`GlobalShaderInputs.Zoom`, `CameraAngle` (`:489`) and `CursorWorldPos`
(`Update_MouseShaderParams:462`). `Apply` re-publishes all three.

### The token is the supported way to silence it

`OnGameUpdate:127` reads `context.ConsumeToken("CameraController::disable-player-interaction")`
and gates its whole input block on the result. `ConsumeToken` is `HashSet.Add`, so it
returns true only for whoever gets there first. `HUDCinematicIntro:99` and
`HUDInteractionZoomManager:334` both use this.

### Reported zoom is a deliberate lie

`Apply` writes `Viewport.Zoom = 80`. The camera does not use it, but culling thresholds, the
HUD, a shader global and the **interaction scope** do — and that last one is why the number
is 80 rather than something small. See [the scope section](#the-interaction-scope-and-why-rockets-were-unreachable):
it has to sit inside `HUDInteractionZoomManager`'s overlap band or the game drags the scope
back every frame.

`Viewport.Angle` is *not* lied about — it gets the real pitch, including negative. That one
is genuinely unreached in vanilla.

## Looking

`InputDownstreamContext.MouseDelta` is computed in `GameInputManager.OnGameUpdate:44-47` as
the frame-to-frame change in `Input.mousePosition`. `CursorLockMode.Locked` freezes
`Input.mousePosition`, so the context reports no movement at all. Mouse look reads
`Input.GetAxisRaw("Mouse X"/"Mouse Y")`, which is unaffected by the lock. Those axes are
Unity defaults but live in project settings we cannot read, so `FirstPersonCamera.Look`
catches a missing one once, unlocks the cursor and says so in the log.

Sensitivity is `LookSensitivity` multiplied by the player's existing
`MouseCameraDragSensitivityX/Y` — sliders already in the options menu, 0–5, default 1, with
the same meaning they have for dragging the orbit camera. `InvertVerticalAxis` is honoured
too. **That is the settings page, for now**: a second slider for the same quantity would
just be a thing to keep in sync.

### The cursor lock has to be re-asserted every frame

`GameCursorManager.Update` runs after us, from `InputManager.PostInputsUpdate`:

```csharp
bool flag = (bool)CameraSettings.ConfineMouseCursor && !fullScreenOverlayOpen && !Headless;
if (flag != CursorConfined) { Cursor.lockState = flag ? CursorLockMode.Confined : CursorLockMode.None; … }
if (!Cursor.visible) { Cursor.visible = true; }
```

It writes `lockState` only when its own flag *changes* — which happens on every dialog open
and close. Set once on entry, our lock would silently vanish the first time the player
opened a menu. So `UpdateCursorLock` runs every frame.

`Cursor.visible` is not worth contesting: the same method forces it true every frame, and
`CursorLockMode.Locked` hides the pointer regardless.

That same mechanism gives the menus back. `HUDDialog:149` consumes
`HUDPart$confine_cursor`, which `GameInputManager:148` reads as "a fullscreen overlay is
open", and HUD parts run before `PlayerInteractionOrchestrator` — so by the time our hook
runs the flag is already set for this frame. When it is set we release the cursor, stop
steering, and freeze the body, so the pause screen is clickable and gravity does not drop
the player off the edge while they read the settings.

## Physics

Not a rigidbody. shapez has no physics world to join — buildings are rows in a model, and
the only `Collider` components in a session are the per-chunk boxes islands carry so they
can be clicked. `FirstPersonBody` queries the map model instead, which on a one-tile grid
is cheaper and far more predictable than a swept capsule.

The model is Minecraft's:

- A tile is solid iff `IMapModel.TryGetBuilding` finds something in it.
- The surface you stand on is the top of the highest building in your column, or the island
  floor (`islandLayer * 20`) if the column is empty.
- A surface within `StepHeight` (1.15) of your feet is a step, not a wall. **This is load
  bearing**: in shapez the platform is usually covered in machines, so without step-up a
  factory floor is a maze of one-tile dead ends.
- No island in the column means no floor, and you fall. On a space platform that is the
  correct outcome rather than a bug.

Walls are tested per axis, so a blocked X still allows Z and the player slides along a
machine rather than sticking to it. The leading edge is probed as well as the centre,
otherwise the body is a point and you can stand inside the face of a building.

A fall that gets `FallRescueDepth` (50) below the last solid ground is put back where it
started. Falling off is the point; staying fallen with no floor anywhere below is a soft
lock, and the game has no respawn to borrow.

`Viewport.IslandLayer` follows the body's feet, so climbing to an upper platform shows that
platform — **except while flying**, where altitude is not a choice of layer. Hovering three
layers up to see what you are doing would otherwise drag the build layer with you and make
it impossible to put a space belt on the floor you are looking down at. With the sync off,
`Q` and `E` (`camera.select-layer-down` / `-up`) do what they always do and the layer is the
player's to pick while their body is somewhere else entirely; landing resumes the sync.

Reach does not get in the way of that: platform placement goes through
`TryGetChunkCoordinateAtCursor`, whose limit is measured in chunks, so a floor sixty units
below is still in range. Tile placement keeps its much shorter reach, which is correct — you
should have to come down to lay a belt.

`BuildingLayer` is deliberately left alone — that is the player's choice of what
to build on, and standing on a belt while building on the floor beside it is normal.

## The crosshair, and building at it

Two problems, one of which was already solved for us.

**Hovering, selecting and deleting already worked.** `ScreenUtils.TryFindBuildingAtCursor`
and `TryFindIslandAtCursor` are true 3D DDA walks against each entity's `CollisionBox`
array — no ground plane anywhere in them, so they are correct at any camera angle. They
only needed to be told where the cursor is.

**Placement did not.** Every placer — `EntityPlacementRunner:357`,
`ModularEntityPlacer:250`, `PipetteController:139`,
`AccessibleMousePlacementTracker:36`, `HUDBuildingMassSelection:172` — routes through
`ScreenUtils.TryGetTileCoordinateAtCursor`, which intersects the cursor ray with a flat
horizontal plane at `Viewport.Height`. At head height that ray is nearly parallel to the
plane, `RaycastPlane` rejects it, and the wrapper answers `new double2(0)` — **the map
origin, not a failure**.

Three hooks fix both:

| Hook | Why |
| --- | --- |
| `Viewport.CursorScreenPosition` → screen centre | The cursor every query starts from. A locked cursor freezes the `Input.mousePosition` behind it, so without this the game keeps asking about wherever the pointer was on entry. Moving this one property redirects hovering, selecting, deleting and the placement helpers at once. |
| `ScreenUtils.TryGetTileCoordinateAtCursor` | The same plane intersection, plus the two guards it is missing: refuse a ray that never meets the plane, refuse one that meets it beyond `Reach`. |
| `ScreenUtils.TryGetChunkCoordinateAtCursor` | Same, for island placement. Placing a platform from inside one is strange, but the unguarded version would drop it on the origin. |

Reach is measured along the ray, not along the ground, so looking down steeply gives a
short reach in front of your feet — which is the behaviour you want. From eye height 1.7 a
10° look-down meets the build plane about 10 tiles out, and anything shallower is refused
rather than silently snapped somewhere.

All three hooks pass straight through to the original when first person is off.

## Layer planes, the cursor key, and the wheel

### The blue planes, not the layers

The game floats a translucent plane at each build layer so you can see which one you are
on from above. `IslandPlayingFieldLayersDrawer.Draw` fades them in with
`saturate(InterpolatedBuildingLayer - i + 1)` and breaks out of the loop below 1, so they
only exist above layer 1 at all. From above they are a helpful overlay. At eye height the
nearest one fills the screen.

The first attempt at this pinned `Viewport.BuildingLayer` to 0 - freezing
`LayerController.TemporarilyDisableChanges` every frame and forcing the viewport setter -
which did remove the planes, and also cost the player every upper layer. **The planes were
the problem, not the layers.** Both pins are gone; a single hook on the drawer returns
early while first person is on, and layer switching works exactly as it does above ground.

### The cursor key

Holding `Tab` hands the mouse back: the cursor unlocks, look and movement stop, the
crosshair hides, and `Viewport.CursorScreenPosition` goes back to reporting the real
pointer so the HUD, the side panels and the menus can be clicked.

The two `ScreenUtils` targeting hooks do **not** simply pass through while it is held.
Passing through means the flat-plane intersection, which from head height answers with the
map origin rather than a miss - so they refuse instead. Nothing gets placed while the
player is aiming at a panel, which is the right answer anyway.

Gravity keeps running, so reaching for the HUD cannot be used to hover over a gap.

`Tab` is `toolbar.next-variant`, so first person consumes that binding for as long as it
is on - otherwise every reach for a panel cycles the variant of whatever is on the cursor.
Variant cycling needs another key while down here.

### The interaction scope, and why rockets were unreachable

Rockets and space platforms are not on a toolbar you can reach by cycling toolbars: they
live in the *island* interaction scope, and the scope follows the camera's zoom.
`HUDInteractionZoomManager` drags it back every frame when the zoom is outside the band
the two scopes overlap in:

```csharp
// UpdateScope_Islands
if (TargetZoom < IslandsMinZoom) { ... StateManager.TryMoveIntoBaseState(Buildings); }
```

We were reporting `Viewport.Zoom = 4`, permanently below `IslandsMinZoom` (50), so any
move into the island scope was undone on the next frame. That is the whole reason the
rocket toolbar could not be opened, and why the space-platform tabs behaved oddly.

The fix is one constant: report a zoom **inside** the overlap band, `[50, 110]`. There
neither `UpdateScope_Buildings` nor `UpdateScope_Islands` forces anything and the scope
stays where it is put. 80 is also an ordinary working zoom rather than an extreme, so the
zoom-driven HUD scaling lands somewhere sensible.

Scope is then switched with `IPlayerInteractionStateManager.TryMoveIntoBaseState`, which
is a public API, on ctrl+wheel: up for space, down for the factory. That direction matches
the vanilla mental model, where zooming out *is* how you get to space.

`main.scope-change` — the game's own scope toggle — is bound to **Space**, which is the
jump key down here, so first person consumes it. Without that, every jump asked the game
to flip scope.

### The wheel moves between toolbars

Belts, fluids, space platforms - the row you cannot otherwise reach from first person.
Within an open category the number keys already work and need nothing.

The toolbar is a **tree of arbitrary depth** — categories, slots, variants, and whatever a
mod adds under those — and `HUDToolbarView` is already parameterised by it:

```csharp
private bool TryCycleChildSlots(int amount, int depth, IToolbarElement activeElement, out IToolbarElement nextSlot)
```

It walks the selection up to `depth` and cycles the unlocked siblings there. The game's own
hotkeys are three fixed calls into it — `next-toolbar` at depth 1, `next-toolbar-slot` at
depth 2, `next-variant` at the selection's own depth.

An earlier version of this mod *synthesised those keybindings*, reasoning that the runtime
toolbar lives in `Toolbar.dll` and `Game.Hud.View.dll` and neither is in `decompiled/`. The
premise was true and the conclusion was still wrong: "no API" meant "not decompiled".
Decompiling the two assemblies took a minute and replaced the guesswork with the real
helper — which matters, because synthesised keybindings can only ever reach the three
depths the game has hotkeys for, and a mod adding a fourth level would have been
unreachable.

So the wheel calls `TryCycleChildSlots` directly, with a depth cursor of its own: ctrl-wheel
moves between levels, the plain wheel cycles at whichever level you are on. The cursor is
clamped against the selection's own `TreeDepth()` every frame, because the tree is not
uniform — a level that exists under one category may not exist under the next.

**Stepping deeper has to select something.** `TryCycleChildSlots` refuses outright when the
selection is shallower than the depth asked for, and opening a category does *not* select
anything inside it — so a cursor that only counted up could never leave level one, and the
wheel appeared dead until something in a submenu had been clicked by hand. Stepping in now
selects the first unlocked child, which is what makes the level exist to move onto. Stepping
back out needs no such thing: the cycle helper walks up from wherever the selection is.

Interaction scope briefly lived on alt+wheel here, as a guess at why the rocket toolbar was
unreachable. The real answer was the tree traversal above, so it is gone.

### Spawning at the vortex

Entering drops the player at the vortex, because arriving somewhere recognisable beats
arriving under wherever the map camera was pointing. Holding shift while pressing the
toggle enters where the camera is looking instead.

There is no "where is the hub" accessor. `HubObserver` knows, but it belongs to
`GameSessionOrchestrator` and reaching it from a camera hook means plumbing a session hook
for one coordinate. `IMapModel.Islands` is enumerable and the hub is the island whose
definition carries an `IHubIslandConfiguration` - the same test
`IslandNotObliteratedByVortexDeathRay.Check` uses. Two definitions answer to it, the
vortex and the space converter hub, so prefer `HubDefinitionMetadata.Configuration` and
settle for the other only if there is no vortex.

`GlobalChunkCoordinate.ToCenter_W()` puts the height at exactly `z * 20`, the island
floor, so the spawn is a standing position and gravity only has to settle the player onto
whatever is built on it.

## Riding belts

The surface you stand on is the top of a building, so the belt is the tile *below* your
feet. If that building's definition carries an `IConveyorConfiguration`, it carries you.

Testing for the configuration rather than a definition id is what makes this cover every
belt variant at once — carrying items is exactly what having that configuration means.

**The speed is not a constant of ours.** Each belt's own configuration holds the same
`BeltSpeed` the simulation is built from: `BuiltinSimulationSystems.CreateSimulationSystems`
reads it from `Mode.Buildings.ForwardBelt.ConfigAs<IConveyorConfiguration>().ConveyorSpeed`
and hands it to every belt system. The unit algebra converts it exactly, with nothing
guessed:

```csharp
Steps  = StepRate * Ticks            // operator on StepRate
tiles  = Steps.FloatWorldUnits       // one world unit is one tile
```

Asking the *building* rather than a global has two side effects worth having: a space belt
reports its own speed, and a research speed buff is already folded in, because the
configuration holds a `BuffableBeltSpeed`.

The drift goes through `TryMove` rather than straight onto `Horizontal`, so a belt running
into a wall presses you against it instead of through it, and walking against the belt does
what it looks like it should.

**Curves are approximate.** A curved belt's rotation is its *output* facing, so a corner
carries you out the way the items leave rather than around the bend. It still takes you
where it is going.

## Riding trains

The hard part was meant to be knowing where a wagon is. Wagons are not in the tile map, so
the ground model that carries the player along belts cannot see them, and their position is
interpolated from chunk pivots and a jump progress by one of eight `ITrainTransformSolver`
implementations — a quadratic bezier along the rail in the ordinary case, an animation
curve on a lift, something else again mid-jump. Re-deriving that means eight hooks and a
real chance of an invisible train carrying the player somewhere the visible one is not.

None of it is necessary. `DrawHooks.OnDrawTrain` is a plain multicast delegate the renderer
fires per train per frame:

```csharp
delegate void DrawTrainDelegate(FrameDrawOptionsNoLOD options, TrainId trainId,
                                TrainData trainData, IDictionary<int, Matrix4x4> wagonsMatricesMap);
```

It hands over **the matrices the wagons are actually drawn with**, keyed by wagon index,
alongside a `TrainId` that is stable between frames. So the position is not computed, it is
observed, and it cannot disagree with what is on screen. `HUDTrainCargoVisualization`
subscribes to the same event, so this is the game's own way in rather than a patch.
`DrawHooks` is `[Obsolete]`, which is the one reason to expect it to move.

Three consequences worth stating:

- **Riding is absolute, not incremental.** The player *is* at the wagon's position each
  frame, so nothing accumulates and nothing drifts — including through the jumps between
  platforms, where the train leaves the rails entirely and takes the rider with it.
- **The dictionary is cleared from the camera update, which runs before the draw that
  refills it.** A wagon read during an update is therefore one frame old, which is
  invisible, and anything that stopped being drawn retires itself without the mod needing
  to be told it was destroyed.
- **Losing the wagon is tolerated for a while.** The renderer culls, so a one-frame
  absence is not proof the train is gone; looking away from the train you are standing on
  would otherwise throw you off it.

Boarding measures distance to the crosshair *ray* rather than to a box, because a wagon's
dimensions are not readable from anything the mod can reach — the renderer is handed
finished matrices, not a size. `TrainRideOffset` is for the same reason the one number in
the mod that is honestly a preference rather than a game value: where "on top of the train"
is has to be chosen by eye.

## Fast travel, on waypoints that already exist

No new building, no new saved state and no picker UI, because the game already has all
three. `IPlayerWaypoints.Waypoints` — reached as `Player.HUDData.Waypoints` — is a saved
list the player fills with the checkpoint key, and every entry carries position, rotation,
angle, zoom, building layer and island layer.

A dedicated teleporter building would need a mesh, a material (a building cannot carry its
own), an icon, a toolbar entry, a research unlock, its own saved network state and a
destination picker, and would arrive at the same place this does. The one thing it would
have that this does not is being diegetic — something you build and pay for — which is a
real design difference rather than only a cost.

**Clicking a waypoint travels too.** In first person, clicking one in the list — or the home
icon — walks you there instead of pulling the map camera out to space view and leaving your
body behind. That is one hook, because both go the same way:
`HUDWaypoints.JumpToWaypointInternal` fires `HUDEvents.RequestMoveToViewport`, and
`GameSessionOrchestrator` registers that event straight onto
`CameraController.RequestMoveToViewport`. The home icon is not a special case either —
`JumpToHubInternal` builds a `PlayerWaypoint` at the scenario's starting location and sends
it through the same call.

A waypoint's own `Zoom` and `Angle` are deliberately dropped on the way: they describe where
the *map* camera was, above the factory looking down, not where a person would stand. Only
the ground position, the layer and the facing survive.

The travel key steps to the next waypoint and goes. The height used is the waypoint's
**island floor**, not anything it recorded about the camera: a waypoint stores where the
*map* camera was, which is above the factory rather than standing in it. Gravity settles
the rest, exactly as entering at the vortex does. Yaw comes from the waypoint so you arrive
facing the way it was saved.

Past a dozen waypoints this wants a picker rather than a cycle, and the freed cursor
already gives us somewhere to put one.

## Telling the player anything

`HUDEvents` is a bag of public multicast events the HUD listens to, and firing one is the
cheapest player-facing output there is. There is no static accessor, so the instance is
captured as the HUD builds itself — `HUDPart.Construct` is public and receives it, and
fires once per part with the same object.

This exists because **a key that silently does nothing is the worst failure mode a mod
has**. Pressing the fly key without the research used to write a line to `Player.log` and
nothing else.

## Flight is bought, walking is free

One rewardless side upgrade grants a research mechanic; the fly key reads that mechanic
back through `ResearchUnlockProgressManager.IsUnlocked(ResearchMechanicId)` and does
nothing until it is unlocked. The node costs 2 — which the research screen renders as
**200**, because `Format(this ResearchPointCurrency)` multiplies by 100 before formatting.

It has no prerequisites on purpose. Every other node in the tree gates content that needs
a factory behind it; this one gates a camera, it is meant to be affordable early, and
there is no vanilla node it belongs beside.

Two things that would break the research screen rather than the mod, both borrowed from
existing entries rather than invented: the shop entry's **preview image** — `GetImage`
throws on an id it cannot resolve, `GameImageId.Empty` included, and that throw comes out
of the whole `HUDResearchTree` construction — and the mechanic's icon. If no image can be
borrowed the node is skipped entirely, because no node beats a research screen that cannot
be closed.

The gate **fails closed**: if the node never registered, flight stays locked. That cannot
strand anyone, because flight is a convenience rather than the only way out — the toggle
key leaves first person from anywhere, and a fall past every floor already puts the player
back on the last solid ground.

This is a definition, not logic, so it is built once per scenario load: **a hot reload
will not pick it up and the game has to be restarted after installing.**

## The reach raycast is solved once a frame

Placing extractors from the air was measurably laggy, and the mod's own profile put
`FirstPersonTargeting.TryReach` at the top by call count.

The call itself is not obviously expensive, but everything it reaches is.
`RaycastHelpers.CustomScreenPointToRayDouble` does two 4x4 matrix inversions and two native
camera property reads per call:

```csharp
double4x4 a  = math.inverse((double4x4)(float4x4)camera.projectionMatrix);
double4x4 a2 = (float4x4)camera.worldToCameraMatrix.inverse;
```

And placement asks "what is under the cursor" many times in one frame — per candidate chunk
while an island is being positioned, plus the trackers and the preview — so that work was
being paid for dozens of times to produce one number.

Within a frame the answer cannot change: the camera has already been written for this frame
by the time anything asks, the cursor is pinned to the screen centre, and the plane height
does not move. So the ray is solved at most once per frame and the result reused.

The cache is keyed on `Viewport.Height` as well as the frame, because the tile and chunk
callers could in principle be handed different planes, and because `Viewport.Height` is
*animated* when the layer changes — a stale hit there would place a building a layer out,
which is a far worse bug than the one being fixed.

## Rendering needs nothing

This was the expected hard part and it is free. `MapCuller` culls on
`math.distancesq(cameraPosition_W, …LODCenter)` (`Game.Core.Rendering.Culling/MapCuller.cs:278`,
`:300`) plus frustum planes, and `LODComputationParameters` is pure distance tables. Walk up
to a cutter and it selects LOD0 on its own. `InOverviewMode` is `Zoom > 1500f`, nowhere near.

The crosshair is uGUI, not IMGUI. IMGUI needs an `OnGUI`, which needs a MonoBehaviour
defined in this assembly — and Mod Reloader byte-loads a rebuilt assembly, after which
Unity refuses to add a component whose type came from it and `AddComponent` returns null
(the same property behind the `ModDirectoryLocator` trap). `Canvas` and `Image` are Unity's
own types, so the crosshair survives a hot reload and needs no fallback.

## Open questions for the next run

**Verified by playing:** the camera, mouse look, walking, leaving cleanly, and building
and deleting belts at the crosshair.

That last one settles the risk this design was most exposed to. `Viewport.CursorScreenPosition`
is a trivial expression-bodied property, and a MonoMod hook on one of those is exactly the
case the JIT can inline past — if it had, every "at cursor" query would have kept using the
frozen mouse position and targeted something across the platform. It did not. The hook
holds, and the three-hook targeting design stands.

**Compiled only:**

1. **Does step-up feel right at 1.15?** Too low and every belt is a wall; too high and you
   float over machines. This is the number most likely to be wrong.
2. **Is the ground query finding islands correctly?** `TryGetIsland(GlobalTileCoordinate)`
   is assumed to respect the island's real footprint rather than its bounding chunks. If it
   does not, you will walk on air off the edge of an L-shaped platform.
3. **Does reach at 10 tiles feel cramped?** The pitch/reach trade-off is real and only
   playing will say whether it reads as natural or as a bug.
4. **Does the wheel land on the right slots?** It presses `toolbar.select-slot-N` for an
   index we track ourselves, so an empty slot is a step where nothing appears to happen,
   and clicking a slot with the mouse drifts our index away from the game's. Ten slots is
   assumed because only the first ten have default keys.
5. **Does anything key off `Viewport.Angle` below 30 or negative?** Still unanswered —
   watch ground and shadow materials while looking up.
6. **Gravity at 26 tiles/s², jump at 8.5.** Earth gravity would be 9.81 but the "metre"
   here is a belt width, so it reads as floaty. The jump clears a little over one tile.

**Answered by playing, kept for the record:** the build plane moving during placement was
the layer following the placer. It is pinned now; see above.

## Finding resource islands from down here

`HUDShapeResourcesVisualization.IsAvailable` is

```csharp
if (Player.InteractionState.BaseState == PlayerInteractionBaseState.Islands)
    return Viewport.Zoom >= MinimumZoomLevel;   // 450
return false;
```

and first person reports a zoom of 80, so the overlay is never offered. One hook on that
getter, answering true while flying, is the whole fix — the overlay is `defaultActive: true`
and its markers are sized by `85f + zoom * 0.05f`, which is dominated by the constant and so
lands close to what it would be at the zoom the game intends.

**Only while flying.** Searching for a resource island is something you do in the air; on the
factory floor the markers would be clutter in front of your face.

A separate "map mode" was considered and rejected. The game already has a map — it is the
normal view, and the toggle key returns to it. A second camera mode with its own rules would
be a large thing to maintain for a case the overlay answers directly, and it would still need
building for players a downstream mod has locked into first person, which the overlay covers
for free.

## The keys are real keybindings

`KeybindingsLayer`'s constructor is public and calls `AssignFullIdAndLoad` on every binding,
so building one loads whatever the player rebound it to — persistence is free. Two consumers
then walk `Keybindings.Layers`:

- `HUDKeybindingsRenderer` — the mod's keys get a rebindable section in the game's own
  settings screen, titled from `keybindings.first-person`.
- `GameInputManager` — the bindings flow into `InputDownstreamContext`, so the mod reads
  them with `ConsumeWasActivated` rather than `Input.GetKey`.

The second is the one that matters. `FirstPersonInput.Read` runs in the prefix on
`HUD.OnGameUpdate`, ahead of every HUD part, and `TryConsume` does the rest:

```csharp
if (keySet.Code != 0 && activeBinding.Value.Code == keySet.Code) ConsumedBindings.Add(activeBinding.Key);
```

Consuming a binding marks every *other active* binding on the same key as consumed. So
sharing Space with `main.scope-change`, Tab with `toolbar.next-variant` and F6 with
`debug.step-speed` is now resolved by the input system, in the player's favour, and
**three hand-written suppression calls were deleted**. A key read outside the input system
cannot participate in that, which is the whole reason the debug collisions went unnoticed
for so long.

Only the toggle is read when first person is off. Consuming the rest would take Space and
Tab away from someone who is not even in first person.

Registration happens from a tick, not a hook: `Keybindings` is built inside
`GlobalsInitialization`, quite possibly before mods load at all, so a constructor hook could
simply never fire. Ticking until `Globals` has data is immune to load order. The two
*modifiers* — shift-on-entry and ctrl-with-the-wheel — stay raw, because a held modifier is
not an action and the settings screen has nowhere sensible to put one.

## The debug keybinding layer is live

`DefaultKeybindings` has a layer called `debug`, and it is **not** developer-only:

| | |
| --- | --- |
| `F6` | `debug.step-speed` |
| `F7` | `debug.slow-speed` |
| `F8`-`F10` | normal / fast / ultra speed |

So entering first person on `F6` also stepped the simulation down to a crawl, and boarding
a train on `F7` slowed it. Both were present from the first build and neither looked like a
keybinding collision — one reads as "the mod pauses the game", the other as "riding a train
is expensive". The mod had even reasoned *in a comment* that the layer must be inactive
because `F6` had never appeared to collide.

Both are consumed now, before the debug HUD part looks. `F8`-`F10` still work.

The suppression is deliberately **not** gated on `Active`: HUD parts run before
`PlayerInteractionOrchestrator`, so on the frame that enters first person the camera has not
flipped `Active` yet, and `debug.step-speed` would get through exactly once — which is
precisely the symptom, a pause on entry.

## What downstream mods can use

`FirstPersonControl` is the whole public surface. Reference the assembly and it is what it
looks like:

```csharp
FirstPersonControl.Forced = true;                    // a first-person game, not a view
FirstPersonControl.FlightEnabled = false;            // no flight at all, and no node
FirstPersonControl.TravelRequiresResearch = false;   // fast travel from the start
```

| | |
| --- | --- |
| `Forced` | lock the player in: they enter on the first frame of a session and the toggle stops working |
| `Active` | whether the player is in first person right now |
| `FlightEnabled` / `TravelEnabled` / `TrainRidingEnabled` | whether the feature is *offered* at all |
| `FlightRequiresResearch` / `TravelRequiresResearch` / `TrainRidingRequiresResearch` | whether it has to be *bought* |
| `FlightResearchCostPoints` / `TravelResearchCostPoints` / `TrainRidingResearchCostPoints` | what the node costs |
| `FlightUnlocked` / `TravelUnlocked` / `TrainRidingUnlocked` | whether the player can do it right now, by either route |

The three gated features - flight, waypoint travel and riding trains - each take the same
three options, so there is one shape to learn rather than three. Each is off, free, or
bought, and "off" is a different question from "not yet researched": a mod that turns a
feature off wants it gone, not pending. Off also means no node in the research tree **and no
row in the keybindings screen**, so the feature leaves no trace.

The nodes are held as data rather than three copies of the same fifty lines, which is what
stops them drifting apart as options are added. The input reader has to stay in step: a
binding that was never registered is a `KeyNotFoundException` out of a frame hook, not a
false, so each optional read is guarded by the same flag that decides whether to register
it.

**`TravelRequiresResearch` defaults to true**, which is a change from fast travel simply
working. `FlightResearchCostPoints` and its twin are **the stored amount, not the displayed
one**: the research screen renders `Amount * 100`, so the default 2 appears as "200". That
trap is worth restating in the one place a consumer will read.

The surface is deliberately dumb - plain statics, no events, no interfaces, no generics -
for a reason beyond taste. Direct reference is the normal path and needs nothing clever, but
a mod that would rather not take a hard reference on another mod can drive the same members
reflectively. Anything richer would serve the first kind of consumer and shut out the second.

Everything is read live; the research settings are read when a scenario loads and the
keybindings when they register, so a mod's constructor is early enough for all of it.

Leaving a session still exits, because the body's position belongs to a map that is gone;
the next session puts a forced player straight back in.

## Reach

Placement reach was 10 tiles - about arm's length, which made laying anything out a walk.
It is 20 now, with a separate 400 for chunk-placed things like platforms, because platforms
are placed at a distance by nature and you cannot stand on one that does not exist yet.

**Flying triples both.** Altitude spends reach: the distance along the ray from an eye fifty
units up looking straight down is fifty before it even touches the plane. Without the
multiplier, climbing high enough to see where a platform should go puts the ground out of
range, and getting close enough to a shape or fluid island to place an extractor means
landing on it.

The multiplier is published to the targeting hooks by the camera each frame rather than
queried, because those hooks are called from the game's own placement code and have no way
back to the body.

## Controls

| | |
| --- | --- |
| `F6` | toggle first person — enters at the vortex |
| `Shift` + `F6` | enter where the camera is looking instead |
| movement keys | walk — the game's own `camera.move-*` bindings, not hardcoded WASD |
| `camera.move-faster` binding | sprint (3×) |
| `Space` | jump, or rise while flying |
| `LeftCtrl` | sink while flying |
| `Q` / `E` | layer down / up — yours to choose while flying, follows your feet on the ground |
| double-tap `Space` | toggle flight — needs the research. `M` does the same |
| `Tab` (hold) | release the mouse for the HUD, side panels and menus |
| `F5` | travel to your next waypoint |
| `F7` | board the train at the crosshair, or step off |
| wheel | previous / next toolbar — free, because nothing zooms in first person |
| `Ctrl` + wheel | step a level deeper into the toolbar, or back out |
| mouse | look |

The mod's own keys are raw `Input.GetKey`, not game keybindings: registering one needs
session-scoped infrastructure a spike should not own yet. `F6` is read outside the
input-token gate so first person can always be left, even with a dialog open.

Both letters were chosen against `DefaultKeybindings` rather than at random. `M` is the
**only** unbound letter in the game — fly was `F`, which is `building-placement.mirror`, so
holding it to fly was also mirroring whatever was on the cursor. `Tab` is
`toolbar.next-variant` and is taken deliberately; first person consumes that binding while
it is on, so variant cycling needs another key down here.
