# The Camera and Viewport

> **You need this when** you want to move the player's view, follow something with it, or
> turn a screen position into a place in the world — and when you need to know why the
> second one sometimes answers with the map origin.


There is one camera, and it is an orbit rig. `Viewport` holds where it is pointed;
`CameraController` turns that into a transform once a frame.

## `Viewport` is the authority, not the camera

Culling, LOD, the HUD and every placement raycast read `Viewport`. The `Transform` is
downstream of it. **If you move the camera by writing the transform, everything else keeps
reasoning about where the camera used to be** — the culler will cull for the old position
and buildings will pop in and out around you.

Write `Viewport`. It raises a change event per property, which is what marks the controller
dirty:

```csharp
viewport.Position;          // double2, world XZ of the pivot
viewport.Height;            // float, Y of the pivot - eased, derived from the layer
viewport.Zoom;              // float, orbit radius
viewport.Angle;             // float, degrees BELOW the horizon: 90 is top-down
viewport.RotationDegrees;   // float, yaw
viewport.IslandLayer;       // short - setting it animates Height
```

`Viewport.MainCamera`, `TransparentCamera` and `UICamera` are public readonly fields on it.

## The rig

`CameraController.ApplyCameraTransform` is the whole camera:

```csharp
float y = math.sin(math.radians(angle)) * zoom;
transform.localPosition = new Vector3(0f, y, (0f - math.cos(math.radians(angle))) * zoom);
transform.localRotation = Quaternion.Euler(angle, 0f, 0f);
```

and the parent it hangs off is placed by `ApplyDirtyPosition`:

```csharp
Parent.position = new float3((float)Viewport.Position.x, Viewport.Height, (float)Viewport.Position.y);
Parent.localRotation = Quaternion.Euler(0f, Viewport.RotationDegrees, 0f);
```

So `Angle` is pitch downward from the horizon and `Zoom` is the orbit radius — the camera
is always looking *at* the pivot from `Zoom` away. Collapse the radius and the camera lands
on the pivot; that is all a first-person view is.

The limits are `static` properties with a `private set`:

```csharp
public static float MinAngle { get; private set; } = 30f;   // 30°, never level
public static float MaxAngle => 90f;
public static float MinZoom  { get; private set; } = 4f;
public static float MaxZoom  => 20000f;
```

They are publicizable, and the game loosens them itself — `camera.disable-restrictions-danger`
in the debug console sets `MinAngle = 5; MinZoom = 0.1`. `HUDCompass` and
`HUDInteractionZoomManager` read the same statics, so changing them changes the compass
readout too.

`ComputeZoomAdjustedMinAngle` adds up to 40° to `MinAngle` as zoom passes 1000, to stop the
map going edge-on when zoomed out. Below zoom 1000 it is `MinAngle` unchanged.

### Scale

One world unit is one building tile — `ScreenUtils.RaytraceTileCoordinates` steps its DDA by
`1f` where the chunk walk beside it steps by `20f`. A chunk is 20 tiles, and an island layer
is 20 units of height (`ScreenUtils` indexes layer planes at `layer * 20`).
`GameSessionOrchestrator.SetupCameras` sets near clip 0.4, far clip 1,000,000 and FOV 45.

**`OverlayTransparentCamera` is parented to `MainCamera` at identity and given the same
FOV in the same loop.** Change one field of view and not the other and the overlay stops
lining up with the world.

## Taking the camera over

`CameraController.OnGameUpdate` gates its entire input block on a token:

```csharp
bool handleInput = context.ConsumeToken("CameraController::disable-player-interaction");
if (handleInput) { /* keys, mouse, zoom, screen panning */ }
else             { /* re-sync targets from the viewport */ }
```

`ConsumeToken` is `HashSet.Add`, so it returns true only for whoever gets there first.
Consume it upstream and the stock camera goes quiet while still applying whatever you write
to `Viewport`. `HUDCinematicIntro` and `HUDInteractionZoomManager` both do exactly this;
see [Notifications and HUD screens](howto/notifications-and-hud-screens.md) for where in the
frame the input context is walked.

There are two more tokens: `CameraController::disable-interpolation` skips the smoothing,
and `CameraController::copy-remaining-momentum` preserves in-flight movement across the
handover.

For a one-off move, `RequestMoveToViewport(IPlayerWaypoint)` sets a target the controller
eases toward, and is already wired to `HUD.Events.RequestMoveToViewport`.

### If you replace the update entirely

Hooking `OnGameUpdate` and *not* calling the original means taking over three shader
globals it owns, or effects that read them freeze at their last value:

```csharp
Shader.SetGlobalFloat(GlobalShaderInputs.Zoom, viewport.Zoom);
Shader.SetGlobalFloat(GlobalShaderInputs.CameraAngle, viewport.Angle);
Shader.SetGlobalVector(GlobalShaderInputs.CursorWorldPos, cursorOnGround);  // 1e20 for "not on the ground"
```

And on the way back, the controller keeps its own copies of where the camera is heading —
`CurrentPosition`, `TargetAngle`, `TargetRotationDegrees`, `TargetPosition`. They are still
whatever they were when you took over, so unless you write them the first frame after you
stop drags the view back to where the player started.

## Screen to world: two families, and only one of them is safe

### Plane intersection — cheap, and degenerate near the horizon

`ScreenUtils.TryGetWorldCoordinate` / `TryGetTileCoordinateAtCursor` /
`TryGetChunkCoordinateAtCursor` all intersect the cursor ray with a **flat horizontal plane**
at a given height, via `RaycastHelpers.TryGetCursorPointOnVirtualPlane`.

This is what every placement path uses — `EntityPlacementRunner`, `ModularEntityPlacer`,
`PipetteController`, `AccessibleMousePlacementTracker`, `HUDBuildingMassSelection`.

It assumes the camera is above the plane looking down, which vanilla guarantees by clamping
`MinAngle` to 30°. Take that clamp off and the assumption goes with it:

```csharp
double num = math.dot(planeNormal, rayDirection);
if (math.abs(num) < 1E-06) { intersectionPoint = default; return false; }   // parallel
…
if (num2 < 0.0)            { intersectionPoint = default; return false; }   // behind the camera
```

`RaycastPlane` reports the miss honestly — but the convenience wrapper does not:

```csharp
public static double2 GetCursorPointOnVirtualPlane(double2 pos, double height, Camera cam)
{
    …
    if (!RaycastPlane(…, out var intersectionPoint, out _)) return new double2(0);
    return intersectionPoint.xz;
}
```

**`new double2(0)` is the map origin, not a sentinel.** A near-level camera does not fail to
place a building; it places it at the centre of the map. Use
`TryGetCursorPointOnVirtualPlane` and check the bool, and treat a shallow angle as out of
range rather than trusting the number.

The same shape appears in `UpdateCameraOnZoomOrAngleChange`, where the zoom-to-cursor
correction differences two of these intersections and adds the result to `Viewport.Position`.
Near the horizon both terms diverge, so the difference is noise and the camera teleports. It
only affects players who enabled the setting (it defaults off).

### 3D DDA against colliders — works from anywhere

`ScreenUtils.TryFindBuildingAtCursor` and `TryFindIslandAtCursor` walk the cursor ray tile by
tile (or chunk by chunk) and test each entity's `CollisionBox` array. No ground plane is
involved, so they are correct at any camera angle, including level and looking up. If you
need "what is under the cursor" rather than "which tile does the cursor sit over", prefer
these.

## Zoom is a performance budget, not just a framing

A lot of the game's drawing decides how much work to do from `Viewport.Zoom` and from the
camera's own position and frustum. That is sound for an overhead camera, where being zoomed
out means being far away and seeing a bounded patch of ground. Move the camera to eye level
and report a small zoom and every one of those guards comes off at once.

`IslandPlacementHelperHighlightShapeResources.Draw` - the helper that marks minable patches
while a shape miner is on the cursor - is the clearest example. It has three separate cost
guards and they are all camera-derived:

```csharp
if (options.Viewport.Zoom > 4000f) return;                   // skip entirely when zoomed out
…
foreach (MapSuperChunk superChunk in map.SuperChunks)
    if (!GeometryUtility.TestPlanesAABB(options.CameraPlanes, superChunkBounds)) continue;
        …
        float num2 = math.distancesq(options.CameraPosition_W, bounds.center);
        int num3 = ((!(num2 < 2250000f) || options.InOverviewMode) ? 1 : 10);  // 10 planes per chunk
```

`InOverviewMode` is just `Viewport.Zoom > 1500f`. So a mod reporting a working zoom below
1500 gets ten indicator planes per resource chunk instead of one, a full-detail shape mesh
per chunk within 2500 units, and no early-out - while its level frustum admits far more of
the map than the overhead one ever did. The resource bounds make that worse rather than
better: `SpaceThemeBoundsProvider.ComputeResourceSourceBounds` overwrites their height with
the constants `-50f`/`-22f`, so they are a flat band that a near-horizontal frustum slices
through for a very long way.

The symptom is specific and misleading - *one* thing to place is slow and everything else is
fine - because this is the only placement helper that looks at the whole map rather than at
the entity being placed.

The cheap fix is not to reimplement the helper but to narrow what it is culled against.
`FrameDrawOptionsNoLOD.CameraPlanes` is a plain `Plane[6]`, so a hook can swap in six
inward-facing planes forming a box around the player, call the original, and put the real
ones back in a `finally`. `GeometryUtility.TestPlanesAABB` then does the culling itself and
nothing about the drawing changes.

> [!TIP]
> Bound it to something the player understands. Placement reach is the natural choice: a
> patch further away than you can place on is scenery, not a hint.

## The camera hook stops without telling anyone

`CameraController.OnGameUpdate` is called once a frame from
`PlayerInteractionOrchestrator.OnGameUpdate`. That is the whole reason it is the right place
to take the camera over - and the reason it is the wrong place to give it back.

Leaving a session for the main menu takes the player interaction with it, so the hook simply
stops firing. Nothing throws, nothing is logged, and a mod holding state behind
`if (active)` holds it forever: a locked cursor, an overridden field of view, an overlay
canvas floating over the menu.

Put the stand-down somewhere that outlives a session. A tick postfixed onto
`GameSessionOrchestrator.Tick` - which is what ShapezShifter's `IMod.OnTick` is - keeps
running for the **main menu's background game**, so it can notice that the camera hook has
gone quiet:

```csharp
private void OnTick(float deltaTime)
{
    if (!Active || Time.frameCount - LastUpdateFrame <= 30) { return; }
    Restore();                       // hide the overlay, unlock the cursor, restore the FOV
}
```

Be generous with the threshold. A load legitimately pauses the camera update for a frame or
two, and ejecting the player for it is a worse bug than the one being guarded against.

## Mouse input

`InputDownstreamContext.MouseDelta` is not a device delta. `GameInputManager.OnGameUpdate`
computes it as the frame-to-frame change in `Input.mousePosition`:

```csharp
Vector3 mousePosition = Input.mousePosition;
float2 current = new float2(mousePosition.x, mousePosition.y);
float2 delta = current - LastMousePos;
```

So **`CursorLockMode.Locked` freezes it**, and a mod doing mouse look with a locked cursor
sees no movement at all through the context. Read `Input.GetAxisRaw("Mouse X")` and
`"Mouse Y"` instead, which the lock does not affect. A locked cursor also freezes
`GameInputManager.RaytraceUIHoverState`, so the HUD keeps believing the pointer is wherever
it was when you locked it.

`Viewport.CursorScreenPosition` is `Input.mousePosition` clamped to the screen, and
`Viewport.CursorRay` is `MainCamera.ScreenPointToRay` of it. Note that `RaycastHelpers`
does *not* use `CursorRay` for its own maths — it rebuilds the ray in `double` precision in
`CustomScreenPointToRayDouble`, because at map scale a `float` ray is not accurate enough.
