using UnityEngine;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// Everything a spike wants to change between two launches, in one place.
///
/// The distance units are the game's world units, and one of those is exactly one
/// building tile: <c>ScreenUtils.RaytraceTileCoordinates</c> steps the tile DDA by
/// <c>1f</c>, while the chunk walk beside it steps by <c>20f</c>. So a chunk and an
/// island layer are both twenty of these, and a belt is one wide - which is what makes
/// a human eye height of about 1.7 the right number rather than a guess.
/// </summary>
public static class FirstPersonTuning
{
    // ---- Keys -------------------------------------------------------------------

    // These are the *defaults* for the bindings FirstPersonKeybindings registers. The
    // player rebinds them in the game's own settings screen, and the mod reads them through
    // the input context rather than through Input.GetKey - so a value here is a starting
    // point, not the key the mod actually uses.

    /// <summary>
    /// The default for the toggle binding - which is **not registered unless a mod sets**
    /// <see cref="FirstPersonControl.ToggleEnabled"/>. First person is normally entered by
    /// playing the scenario, so this key is dead in an ordinary game.
    /// </summary>
    public const KeyCode ToggleKey = KeyCode.F6;

    /// <summary>
    /// Noclip. The escape hatch for getting stuck, and the old floating camera.
    ///
    /// `M` because it is the only letter `DefaultKeybindings` leaves unbound. This was `F`,
    /// which is `building-placement.mirror` - holding it to fly was also mirroring whatever
    /// was on the cursor.
    /// </summary>
    public const KeyCode FlyKey = KeyCode.M;

    /// <summary>
    /// Held to release the mouse so the HUD, the side panels and the menus can be clicked.
    ///
    /// This was Tab, which is `toolbar.next-variant` - and sharing a key here is not free.
    /// Reading our binding consumes it, and the input system marks every other active
    /// binding on the same key consumed too, so first person silently took variant cycling
    /// away from the player. Variants are picked constantly while building; freeing the
    /// mouse is occasional. The occasional one moves.
    ///
    /// `LeftAlt` is as close to free as the keyboard gets. `DefaultKeybindings` binds it
    /// once, to `mass-selection.deselect-area-modifier` - a drag modifier, which is a thing
    /// you do with a map camera rather than from the factory floor - and every other
    /// candidate is worse: `M` is flight, `F5` is travel, and the function keys are a reach
    /// from the movement keys for something that has to be held.
    /// </summary>
    public const KeyCode CursorKey = KeyCode.LeftAlt;

    /// <summary>
    /// Held while pressing <see cref="ToggleKey"/> to enter where the camera is looking
    /// instead of at the vortex.
    /// </summary>
    public const KeyCode SpawnHereModifier = KeyCode.LeftShift;

    /// <summary>
    /// Steps to the next of the player's own waypoints and travels there. `F5` because the
    /// only unbound letter is already flight, and `DefaultKeybindings` uses F1-F4 and F6-F10
    /// but not F5.
    /// </summary>
    public const KeyCode TravelKey = KeyCode.F5;

    /// <summary>
    /// Boards the train the crosshair is on, or steps off the one you are riding. Jumping
    /// gets off too.
    ///
    /// `F` is `building-placement.mirror`, and that clash is resolved by *when* the binding
    /// is read rather than by picking a different key: it is skipped entirely while the
    /// player is holding something, so mirror keeps working where it means anything and `F`
    /// boards a train the rest of the time. Reading it at all would consume it, because the
    /// input system marks every other active binding on the same key consumed too.
    ///
    /// It was `F7`, which is `debug.slow-speed` - that one read as "riding a train is
    /// expensive" rather than as a key collision.
    /// </summary>
    public const KeyCode BoardKey = KeyCode.F;

    /// <summary>
    /// Two taps of the jump key inside this many seconds toggles flight, the way a creative
    /// mode does. Short enough not to fire on a deliberate double jump, long enough not to
    /// need a drum roll.
    /// </summary>
    public const float FlyDoubleTapSeconds = 0.3f;

    /// <summary>
    /// Held with the wheel to cycle a building's variants instead of walking the toolbar.
    ///
    /// Shift rather than Ctrl, which is what the old depth modifier used: Ctrl is
    /// <see cref="SinkKey"/>, so a player descending while flying was also driving the
    /// toolbar every time they touched the wheel.
    /// </summary>
    public const KeyCode ToolbarVariantModifier = KeyCode.LeftShift;

    public const KeyCode ToolbarVariantModifierAlt = KeyCode.RightShift;

    public const KeyCode JumpKey = KeyCode.Space;
    public const KeyCode SinkKey = KeyCode.LeftControl;

    // ---- Looking ----------------------------------------------------------------

    /// <summary>
    /// Degrees of view rotation per unit of raw mouse axis, before the player's own
    /// sensitivity is applied.
    ///
    /// This is multiplied by the game's existing <c>MouseCameraDragSensitivityX/Y</c>
    /// settings, which are already sliders in the options menu with a 0-5 range and a
    /// default of 1. That is the tuning knob, at least until first person earns a settings
    /// page of its own - there is no point shipping a second slider for the same quantity.
    /// </summary>
    public const float LookSensitivity = 0.55f;

    /// <summary>
    /// Pitch is measured the way <c>Viewport.Angle</c> measures it: degrees below the
    /// horizon, so 90 is the stock top-down view and 0 is level. Vanilla clamps this to
    /// [30, 90] and never goes negative; we need negative to look up at a tall platform.
    /// Stopping just short of straight up and straight down avoids the gimbal flip that
    /// <c>Quaternion.Euler(±90, 0, 0)</c> would produce.
    /// </summary>
    public const float MinPitch = -85f;

    public const float MaxPitch = 85f;

    // ---- The body ---------------------------------------------------------------

    /// Eye height above the surface being stood on, in tiles.
    public const float EyeHeight = 1.7f;

    /// <summary>
    /// How tall the body is for the purpose of bumping into things. Buildings occupy one
    /// full tile of height each, so anything above 1 means a single belt is a wall unless
    /// <see cref="StepHeight"/> lets you climb it - which it does.
    /// </summary>
    public const float BodyHeight = 1.8f;

    public const float BodyRadius = 0.35f;

    /// <summary>
    /// How high a surface can be and still be walked onto rather than into. One tile plus
    /// a margin, so belts and machines are steps rather than walls. Without this a factory
    /// floor is a maze of one-tile-deep dead ends, because in shapez the platform is
    /// usually covered.
    /// </summary>
    public const float StepHeight = 1.15f;

    /// Tiles per second, and the default for <see cref="FirstPersonControl.WalkSpeed"/>. A
    /// belt moves items at a few tiles per second, so this is deliberately close to belt
    /// speed - walking beside a shape is the whole point.
    public const float WalkSpeed = 8f;

    /// The default for <see cref="FirstPersonControl.SprintMultiplier"/>.
    public const float SprintMultiplier = 3f;

    /// <summary>
    /// Tiles per second, in noclip, before sprinting. The map is measured in chunks of
    /// twenty tiles and spirals outward, so walking pace would put the first spiral edge an
    /// hour away; this is three chunks a second, and the sprint key triples it.
    ///
    /// This was 180 - what the sprint speed is now. At that pace flight outran a train,
    /// which made riding one a novelty rather than a way of getting anywhere. Trains are
    /// faster than unhurried flight now, and sprinting still beats them, so crossing the map
    /// in the air is a choice rather than the default.
    ///
    /// The default for <see cref="FirstPersonControl.FlySpeed"/>, which is what the mod
    /// actually reads.
    /// </summary>
    public const float FlySpeed = 60f;

    /// Tiles per second squared. Earth gravity in these units would be 9.81, which at this
    /// scale reads as floaty, because the "metre" here is a belt width.
    public const float Gravity = 26f;

    /// <summary>
    /// Tiles per second of initial upward speed, and the default for
    /// <see cref="FirstPersonControl.JumpSpeed"/>.
    ///
    /// Sized to clear **three building layers**, which is what a blueprint that fills all
    /// three of them needs - and those are common enough that the old jump could leave a
    /// player unable to get into their own platform at all.
    ///
    /// The arithmetic is not simply "three tiles", because <see cref="StepHeight"/> is free
    /// clearance: `Blocked` scans the column from `floor(feet + StepHeight)`, so moving
    /// horizontally over an N-layer stack needs the feet at `N - StepHeight`, and the apex
    /// of a jump is `v^2 / 2g`.
    ///
    /// | Stack | Feet needed | Speed |
    /// | --- | --- | --- |
    /// | 2 layers | 0.85 | 6.65 |
    /// | 3 layers | 1.85 | 9.81 |
    /// | 4 layers | 2.85 | 12.17 |
    ///
    /// 11 puts the apex at 2.33 - comfortably over the 1.85 that three layers wants, and
    /// comfortably under the 2.85 that would start clearing four. Sitting in the middle of
    /// that band is deliberate: the integration is Euler and the real apex is a little under
    /// the analytic one, so a value chosen to just barely clear three layers would fail at a
    /// low frame rate.
    ///
    /// This was 8.5, an apex of 1.39 - over two layers and under three, which is exactly
    /// where a three-layer blueprint becomes a wall.
    /// </summary>
    public const float JumpSpeed = 11f;

    /// Terminal velocity, so a fall off the edge does not run away to infinity and lose
    /// float precision on the way.
    public const float MaxFallSpeed = 60f;

    /// <summary>
    /// How far below the last solid ground a fall is allowed to get before the player is
    /// put back. Falling off a space platform is the correct thing to happen; staying
    /// fallen, with no floor anywhere below, is a soft lock.
    /// </summary>
    public const float FallRescueDepth = 50f;

    // ---- Trains -----------------------------------------------------------------

    /// <summary>
    /// How high above a wagon's own origin the rider stands, in tiles.
    ///
    /// Unlike the belt speed, this one **is** a preference rather than a game value. A
    /// wagon's dimensions are not readable from anything the mod can reach - the renderer
    /// is handed finished matrices, not a size - so where "on top of the train" is has to
    /// be chosen by eye. Adjust until it looks right rather than trying to derive it.
    ///
    /// Raised four times - 1.6, 2.6, 4.4, 7 - and the fourth finally put the rider on the
    /// roof rather than inside the wagon, but close enough that the near clip plane was
    /// cutting into it. This last step is for the clip plane rather than for the wagon.
    ///
    /// There is nothing to derive it from - a wagon's dimensions are not readable from
    /// anything a mod can reach - which is why
    /// <see cref="FirstPersonControl.TrainRideHeight"/> exposes it.
    /// </summary>
    public const float TrainRideOffset = 7.8f;

    /// <summary>
    /// How far down the crosshair a wagon can be and still be boardable, in tiles, and the
    /// default for <see cref="FirstPersonControl.BoardReach"/>.
    ///
    /// Two chunks. Raised from 25, which was about the length of one platform: a train you
    /// could see coming was routinely out of range, and the key had to be held until it had
    /// almost arrived. <see cref="BoardRadius"/> is unchanged, so the angular tolerance
    /// narrows with distance - at 40 tiles a radius of 4 is about six degrees, which is
    /// still a comfortable aim and keeps the far end of the reach from grabbing a train you
    /// were only looking past.
    /// </summary>
    public const float BoardReach = 40f;

    /// <summary>
    /// How far off the line of sight a wagon can be and still count as the one you meant.
    /// Distance is measured to the ray rather than to a box because a wagon has no readable
    /// dimensions; a tolerance around the crosshair is the right test for "look at it and
    /// press the key" anyway.
    /// </summary>
    public const float BoardRadius = 4f;

    /// <summary>
    /// How many frames the ridden wagon may be absent from the draw before the rider is put
    /// down. Not one: the renderer culls, so looking away from the train you are standing
    /// on would otherwise throw you off it.
    /// </summary>
    public const int TrainLostFrames = 30;

    /// <summary>
    /// How many frames the camera hook may go without being called before the mod assumes
    /// the session has gone and stands down. See <c>FirstPersonCamera.Watchdog</c>.
    ///
    /// Half a second at 60fps. Generous on purpose: a load pauses the camera update for a
    /// few frames quite legitimately, and being ejected from first person for it would be a
    /// worse bug than the one this guards against.
    /// </summary>
    public const int CameraLostFrames = 30;

    /// <summary>
    /// How far from the player a shape or fluid patch stays visible regardless of where the
    /// player is looking, in world units. 2000 is a hundred chunks.
    ///
    /// The default for <see cref="FirstPersonControl.ResourceRenderRadius"/>. See
    /// <c>FirstPersonMod.OnResourceBounds</c> for what it does and why a patch needs it.
    /// </summary>
    public const float ResourceRenderRadius = 2000f;

    // ---- Reaching ---------------------------------------------------------------

    /// <summary>
    /// How far the crosshair can place a building, in tiles, measured along the ray rather
    /// than along the ground.
    ///
    /// The ray has to descend to meet the build plane, so pitch and reach trade off: a
    /// shallower look-down meets the plane further away, and anything too shallow is refused
    /// rather than silently snapped somewhere.
    ///
    /// This was 10, which is about arm's length and made laying anything out a walk. Twenty
    /// is still clearly a reach rather than a free hand.
    /// </summary>
    public const float Reach = 20f;

    /// <summary>
    /// The same, for platforms and other chunk-placed things. Chunks are twenty tiles, so
    /// this is twenty chunks - platforms are placed at a distance by nature, and you cannot
    /// stand on one that does not exist yet.
    /// </summary>
    public const float ChunkReach = 400f;

    /// <summary>
    /// Both reaches are multiplied by this while flying.
    ///
    /// Altitude spends reach: the distance along the ray from an eye fifty units up, looking
    /// straight down, is fifty before it even touches the plane. Without this, climbing high
    /// enough to see where a platform should go puts the ground out of range - and getting
    /// close enough to a shape or fluid island to place an extractor meant landing on it.
    /// </summary>
    public const float FlyingReachMultiplier = 3f;

    /// <summary>
    /// How much further than <see cref="ChunkReach"/> the miner-placement resource highlight
    /// is allowed to reach, as a multiple.
    ///
    /// Bounding it to exactly the reach would make patches appear at the instant they became
    /// placeable, which reads as pop-in; a little beyond means you can see where to walk to.
    /// See <see cref="FirstPersonPlacementHighlight"/> for why it is bounded at all.
    /// </summary>
    public const float PlacementHighlightMargin = 1.5f;

    // ---- Presentation -----------------------------------------------------------

    /// <summary>
    /// The stock cameras are set to 45 by <c>GameSessionOrchestrator.SetupCameras</c>,
    /// which is a sensible framing for an RTS camera a long way up and claustrophobic at
    /// head height. Applied to the transparent overlay camera too - that one is parented
    /// to the main camera at identity and given the same FOV in the same loop, so
    /// changing only one of the pair makes the overlay disagree with the world.
    /// </summary>
    public const float FieldOfView = 75f;

    /// <summary>
    /// What we report to <c>Viewport.Zoom</c> while first person is active.
    ///
    /// The camera does not use it - we write the transform ourselves - but culling
    /// thresholds, the HUD, the shader global and, crucially, the *interaction scope* all
    /// read it.
    ///
    /// This has to sit inside <c>HUDInteractionZoomManager</c>'s overlap band,
    /// <c>[IslandsMinZoom, BuildingsMaxZoom]</c> = [50, 110]. Outside it the manager drags
    /// the scope back every frame:
    ///
    /// <code>
    /// // UpdateScope_Islands
    /// if (TargetZoom &lt; IslandsMinZoom) { … StateManager.TryMoveIntoBaseState(Buildings); }
    /// </code>
    ///
    /// This was 4 - <c>CameraController.MinZoom</c> - which put us permanently below that
    /// floor, so switching to the platform scope was undone on the next frame and the
    /// rocket and space-platform toolbars could not be reached at all. Inside the band
    /// neither <c>UpdateScope_Buildings</c> nor <c>UpdateScope_Islands</c> forces anything,
    /// so the scope stays where it is put. 80 is also an ordinary working zoom rather than
    /// an extreme, so zoom-driven HUD scaling lands somewhere sensible.
    /// </summary>
    public const float ReportedZoom = 80f;


    /// Half-length of a crosshair arm, in pixels, and the gap left in the middle.
    public const float CrosshairArm = 7f;

    public const float CrosshairGap = 3f;

    public const float CrosshairThickness = 2f;
}
