using Game.Core.Coordinates;
using Game.HUD.CameraManager;
using Unity.Mathematics;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// Drives the game camera from head height instead of orbit.
///
/// The stock camera is an orbit rig: a parent transform pinned to
/// <c>(Viewport.Position.x, Viewport.Height, Viewport.Position.y)</c> and yawed by
/// <c>Viewport.RotationDegrees</c>, with the camera itself hung off it at
/// <c>(0, sin(angle) * zoom, -cos(angle) * zoom)</c> looking back at the pivot. First
/// person is that rig with the orbit radius collapsed to nothing, so the camera sits on
/// the pivot and pitches freely.
///
/// We could get most of the way there by publicising <c>CameraController.MinAngle</c> and
/// <c>MinZoom</c> - the game's own <c>camera.disable-restrictions-danger</c> console
/// command loosens exactly those two - and letting the stock controller run. We do not,
/// because three of its behaviours are actively hostile at zero radius:
///
/// <list type="bullet">
/// <item>Panning speed is multiplied by <c>Viewport.Zoom</c>, so at the zoom that puts you
/// on the floor you cannot move at all.</item>
/// <item><c>UpdateCameraOnZoomOrAngleChange</c> runs the zoom-to-cursor correction, which
/// is a difference of two cursor-to-ground-plane intersections. Near the horizon both
/// terms diverge, and their difference lands in <c>Viewport.Position</c> - the camera
/// teleports. It only bites players who turned the setting on, and suppressing it would
/// mean writing to a saved preference.</item>
/// <item>Angle and zoom are re-clamped every frame from static fields that other code -
/// <c>HUDCompass</c>, <c>HUDInteractionZoomManager</c> - also reads.</item>
/// </list>
///
/// So while first person is active the hook does not call the original at all and this
/// class writes the transform itself. It still keeps <c>Viewport</c> truthful, because
/// culling, LOD, the HUD and every placement raycast read the viewport rather than the
/// camera, and a viewport that disagrees with the camera desynchronises all of them.
/// </summary>
public sealed class FirstPersonCamera
{
    private readonly ILogger Logger;
    private readonly Crosshair Crosshair = new Crosshair();
    private readonly FirstPersonTravel Travel = new FirstPersonTravel();
    private readonly FirstPersonTrains Trains = new FirstPersonTrains();

    public FirstPersonNotifier Notifier { get; } = new FirstPersonNotifier();

    public FirstPersonTargeting Targeting { get; } = new FirstPersonTargeting();

    public bool Active { get; private set; }

    /// <summary>
    /// Where the player's eye is, in Unity world space - the same vector written to the
    /// camera rig each frame. Read by the resource-bounds hook, which is called from the
    /// culler and has no other way back to the body.
    /// </summary>
    public Vector3 EyePosition => new Vector3(
        (float)Body.Horizontal.x,
        Body.Height + FirstPersonTuning.EyeHeight,
        (float)Body.Horizontal.y);

    /// <summary>
    /// The chunk the player is standing in, for callers that asked "what is the camera
    /// looking at" and got no answer because the ray missed the build plane.
    /// </summary>
    public GlobalChunkCoordinate PlayerChunk(Viewport viewport)
    {
        GlobalChunkCoordinate chunk_GC =
            ((WorldCoordinate)(float3)EyePosition).ToGlobalChunkCoordinate();
        chunk_GC.z = viewport.IslandLayer;
        return chunk_GC;
    }

    /// <summary>
    /// Whether a dialog owned input on the frame we last ran. Read by the hotbar hook,
    /// which runs earlier in the frame than we do - `HUD.OnGameUpdate` is where the dialogs
    /// consume the token in the first place, so at that point this frame's answer does not
    /// exist yet and last frame's is the best available. Being one frame late only matters
    /// for the single scroll that opens or closes a menu.
    /// </summary>
    public bool OverlayOpen { get; private set; }

    /// <summary>
    /// The player is holding the cursor key, so the mouse is theirs: the crosshair is gone,
    /// look and movement are suspended, and every "at cursor" query goes back to the real
    /// pointer so the HUD, the side panels and the menus can be clicked.
    /// </summary>
    public bool CursorFreed { get; private set; }

    /// <summary>
    /// Read by the hook that keeps the shape-resource overlay available: searching for a
    /// resource island is a thing you do in the air, and on the ground the markers would
    /// only be clutter.
    /// </summary>
    public bool Flying => Active && Body.Flying;

    /// Degrees, matching <c>Viewport.RotationDegrees</c>.
    private float Yaw;

    /// Degrees below the horizon, matching <c>Viewport.Angle</c>. See FirstPersonTuning.
    private float Pitch;

    private readonly FirstPersonBody Body = new FirstPersonBody();

    /// Captured each frame so the travel key can reach the player's waypoint list without
    /// threading it through every movement call.
    private Player CurrentPlayer;

    /// When the jump key was last pressed, for the double-tap that toggles flight.
    private float LastJumpTap;

    /// <summary>
    /// This frame's reading of the mod's own bindings, taken in the HUD prefix before any
    /// HUD part gets a chance at the same keys. See <see cref="FirstPersonInput"/>.
    /// </summary>
    private FirstPersonInput Keys;

    /// <summary>
    /// Set once if <c>Input.GetAxisRaw</c> throws, which it does when the legacy Input
    /// Manager has no "Mouse X"/"Mouse Y" axis defined. They are Unity defaults and should
    /// be there, but a missing axis would otherwise present as "the view will not turn"
    /// with nothing in the log, so unlock the cursor again and say so.
    /// </summary>
    private bool RawAxesUnavailable;

    /// State to hand back to the stock controller on the way out.
    private float RestoreZoom;
    private float RestoreAngle;
    private float RestoreFieldOfView;
    private CursorLockMode RestoreCursorLock;
    private bool RestoreCursorVisible;

    /// <summary>
    /// The viewport we entered on. A session change replaces it, and our cached position
    /// then refers to a map that is gone, so we drop out rather than teleport.
    /// </summary>
    private Viewport EnteredOn;

    private CameraController EnteredWith;

    /// <summary>
    /// The last frame <see cref="Update"/> ran while active. See <see cref="Watchdog"/>.
    /// </summary>
    private int LastUpdateFrame = -1;

    public FirstPersonCamera(ILogger logger)
    {
        Logger = logger;
    }

    /// <summary>
    /// Handed this frame's bindings by the HUD prefix, which runs earlier than we do.
    /// </summary>
    public void SetInput(FirstPersonInput input)
    {
        Keys = input;
    }

    /// <summary>
    /// Replaces <see cref="CameraController.OnGameUpdate"/> while active. Returns true if
    /// the caller should skip the original.
    /// </summary>
    public bool Update(CameraController controller, InputDownstreamContext context, FrameDrawOptions options)
    {
        Viewport viewport = options.Viewport;

        // Either a downstream mod set the flag, or this is the mod's own scenario, which
        // is a first-person game by definition.
        bool lockedIn = FirstPersonControl.LockedIn;

        if (lockedIn && !Active)
        {
            // Entering here rather than on a session hook means it also takes effect the
            // moment the flag is set, and covers a scenario the player loaded into.
            Enter(controller, viewport, options.Player?.CurrentMap);
        }
        else if (Keys.Toggle)
        {
            if (lockedIn)
            {
                Notifier.Show("First person cannot be left in this game.", HUDNotificationType.Warning);
            }
            else
            {
                Toggle(controller, viewport, options.Player?.CurrentMap);
            }
        }

        if (!Active)
        {
            return false;
        }

        if (!ReferenceEquals(viewport, EnteredOn))
        {
            // New session. Our position is a coordinate in the map that just went away.
            Logger.Info?.Log("First person: viewport replaced, dropping back to the orbit camera.");
            Restore();
            return false;
        }

        LastUpdateFrame = Time.frameCount;

        float deltaTime = math.min(0.2f, Time.unscaledDeltaTime);
        CurrentPlayer = options.Player;
        IMapModel map = CurrentPlayer?.CurrentMap;

        // The game's own "a fullscreen overlay is open" signal - GameInputManager reads
        // exactly this to decide whether to free the cursor, and every HUDDialog consumes
        // it. HUD parts run before PlayerInteractionOrchestrator, so by the time we get
        // here it is already set for this frame.
        bool overlayOpen = !context.IsTokenAvailable("HUDPart$confine_cursor");
        OverlayOpen = overlayOpen;

        // Taking the token is how the stock controller is told to keep its hands off, and
        // it is what HUDCinematicIntro and HUDInteractionZoomManager already do. Failing
        // to take it means something upstream - a cinematic, say - owns input this frame.
        bool steering = !overlayOpen && context.ConsumeToken(CameraController.TokenEnableCameraControls);

        // Holding the cursor key hands the mouse back. Movement stops with it - walking
        // blind while aiming at a side panel is not something anyone wants - but gravity
        // does not, so you cannot hover over a gap by reaching for the HUD.
        CursorFreed = Active && Keys.FreeCursor;
        bool piloting = steering && !CursorFreed;

        UpdateCursorLock(piloting);

        if (piloting)
        {
            Look(controller);
        }

        // A new session brings a new DrawHooks, so this is re-checked rather than done once.
        Trains.Attach(options.Hooks);
        Trains.Collecting = true;

        if (piloting)
        {
            // Riding is left on a fresh press; boarding happens for as long as the key is
            // held, so a train only has to pass through the crosshair rather than be caught
            // on exactly the right frame. The press that boards you cannot also drop you,
            // because you were not riding when it was read.
            if (Body.Riding)
            {
                if (Keys.Board)
                {
                    LeaveTrain("Stepped off the train.");
                }
            }
            else if (Keys.BoardHeld)
            {
                TryBoardTrain(viewport, announceFailure: Keys.Board);
            }
        }

        if (!overlayOpen)
        {
            // Frozen while a menu is up. Gravity that keeps running behind the pause screen
            // would drop the player off the edge while they were reading the settings.
            Walk(controller, context, map, piloting, deltaTime);
        }

        RideTrain();
        Apply(viewport, map);

        // Retires last frame's wagons. The draw that refills them runs after this update,
        // so a wagon read during an update is one frame old - and anything that stopped
        // being drawn drops out without us having to be told it was destroyed.
        Trains.EndFrame();
        return true;
    }

    public void Toggle(CameraController controller, Viewport viewport, IMapModel map)
    {
        if (Active)
        {
            Restore();
        }
        else
        {
            Enter(controller, viewport, map);
        }
    }

    private void Enter(CameraController controller, Viewport viewport, IMapModel map)
    {
        EnteredOn = viewport;
        EnteredWith = controller;

        RestoreZoom = viewport.Zoom;
        RestoreAngle = viewport.Angle;
        RestoreFieldOfView = viewport.MainCamera.fieldOfView;
        RestoreCursorLock = Cursor.lockState;
        RestoreCursorVisible = Cursor.visible;

        Yaw = viewport.RotationDegrees;
        // Not the viewport's angle: entering from the default 60 degrees would start the
        // player staring at their own feet, which reads as a bug rather than as a viewpoint.
        Pitch = 0f;

        // At the vortex by default, because arriving somewhere you recognise beats arriving
        // under wherever the map camera was pointing. Holding the modifier drops you where
        // you are looking instead, which is what you want when you have just flown the
        // camera out to a remote platform to inspect it.
        //
        // Either way the height is only a starting point: gravity settles the player onto
        // whatever is actually underneath a frame later, so there is nothing to guess here.
        double2 start = viewport.Position;
        float startHeight = viewport.Height;

        bool spawnHere = Keys.SpawnHere;
        if (!spawnHere && FirstPersonSpawn.TryFindVortex(map, out double2 vortex, out float vortexHeight))
        {
            start = vortex;
            startHeight = vortexHeight;
        }

        Body.Reset(start, startHeight);

        viewport.MainCamera.fieldOfView = FirstPersonTuning.FieldOfView;
        viewport.TransparentCamera.fieldOfView = FirstPersonTuning.FieldOfView;

        UpdateCursorLock(true);
        Crosshair.Show();
        FirstPersonHotbar.Reset();

        Active = true;
        FirstPersonControl.ReportActive(true);
        Logger.Info?.Log("First person: on. "
                         + (FirstPersonControl.ToggleEnabled
                             ? FirstPersonTuning.ToggleKey + " to leave, "
                             : "no way out - this session is first person, ")
                         + FirstPersonTuning.FlyKey + " to fly.");
    }

    /// <summary>
    /// Hands the camera back. The stock controller keeps its own copies of where the
    /// camera is heading - <c>CurrentPosition</c>, <c>TargetAngle</c>,
    /// <c>TargetRotationDegrees</c> - and they are whatever they were before we took over,
    /// so without writing them the first frame after exit smoothly drags the player back
    /// to where they started. The dirty flags are the same ones the controller's own
    /// disabled-input branch sets, and they are what makes it rebuild the transform.
    /// </summary>
    private void Restore()
    {
        Active = false;
        CursorFreed = false;
        FirstPersonControl.ReportActive(false);

        // Update returns early once Active is false, so nothing here would ever be cleared
        // by the frame loop: the draw hook would keep filling a dictionary that nothing
        // empties for the rest of the session.
        Body.Riding = false;
        Trains.Collecting = false;
        Trains.Detach();

        Cursor.lockState = RestoreCursorLock;
        Cursor.visible = RestoreCursorVisible;
        Crosshair.Hide();

        // Viewport is a plain class, so `!= null` on it is an ordinary null check - but its
        // cameras are UnityEngine.Objects, and the path that ends a session gets here with
        // the rig already destroyed. Unity's overloaded == is what tells the two apart;
        // without it, writing fieldOfView throws MissingReferenceException. Nothing is lost
        // by skipping it: the next session builds a fresh rig from the prefab.
        if (EnteredOn != null && EnteredOn.MainCamera != null)
        {
            EnteredOn.MainCamera.fieldOfView = RestoreFieldOfView;
            EnteredOn.TransparentCamera.fieldOfView = RestoreFieldOfView;

            EnteredOn.RotationDegrees = Yaw;
            EnteredOn.Angle = RestoreAngle;
            EnteredOn.Zoom = RestoreZoom;
            EnteredOn.TargetZoom = RestoreZoom;
        }

        if (EnteredWith != null)
        {
            EnteredWith.TargetPosition = null;
            EnteredWith.CurrentPosition = Body.Horizontal;
            EnteredWith.TargetAngle = RestoreAngle;
            EnteredWith.TargetRotationDegrees = Yaw;

            EnteredWith.ZoomDirty = true;
            EnteredWith.AngleDirty = true;
            EnteredWith.RotationDirty = true;
            EnteredWith.PositionDirty = true;
        }

        EnteredOn = null;
        EnteredWith = null;
        Logger.Info?.Log("First person: off.");
    }

    /// <summary>
    /// Holds the cursor locked while the player is steering, and releases it the moment a
    /// dialog opens so the pause menu and the settings screen can still be clicked.
    ///
    /// Re-asserted every frame rather than set once on entry, because
    /// <c>GameCursorManager.Update</c> runs after us in <c>InputManager.PostInputsUpdate</c>
    /// and writes <c>Cursor.lockState</c> whenever its own confine flag changes - which it
    /// does on every dialog open and close. Set once, the lock would silently disappear
    /// the first time the player opened a menu.
    ///
    /// <c>Cursor.visible</c> is not worth fighting over: the same method forces it back to
    /// true every frame, and <c>CursorLockMode.Locked</c> hides the pointer regardless.
    /// </summary>
    private void UpdateCursorLock(bool steering)
    {
        if (RawAxesUnavailable)
        {
            // Deliberately left free - without raw axes the frozen-cursor path is all we
            // have, and locking it would take mouse look away entirely.
            return;
        }

        CursorLockMode wanted = steering ? CursorLockMode.Locked : CursorLockMode.None;
        if (Cursor.lockState != wanted)
        {
            Cursor.lockState = wanted;
        }
    }

    /// <summary>
    /// Mouse look. Not from <c>InputDownstreamContext.MouseDelta</c>: that is computed in
    /// <c>GameInputManager.OnGameUpdate</c> as the frame-to-frame change in
    /// <c>Input.mousePosition</c>, and a locked cursor freezes exactly that, so the
    /// context reports no movement at all. The raw device axes are unaffected by the lock.
    ///
    /// Sensitivity is the player's own <c>MouseCameraDragSensitivityX/Y</c> - the sliders
    /// already in the options menu, with the same meaning they have for dragging the orbit
    /// camera - rather than a second set of numbers only this mod knows about.
    /// </summary>
    private void Look(CameraController controller)
    {
        if (RawAxesUnavailable)
        {
            return;
        }

        float x;
        float y;
        try
        {
            x = Input.GetAxisRaw("Mouse X");
            y = Input.GetAxisRaw("Mouse Y");
        }
        catch (System.Exception exception)
        {
            RawAxesUnavailable = true;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Logger.Exception?.LogException(exception);
            Logger.Info?.Log("First person: no raw mouse axes, the view will not turn. Walking still works.");
            return;
        }

        CameraGameSettings settings = controller.Settings;
        if (settings.InvertVerticalAxis.Value)
        {
            y *= -1f;
        }

        Yaw = FastMath.NormalizeAngleDegrees(
            Yaw + x * FirstPersonTuning.LookSensitivity * settings.MouseCameraDragSensitivityX.Value);

        Pitch = math.clamp(
            Pitch - y * FirstPersonTuning.LookSensitivity * settings.MouseCameraDragSensitivityY.Value,
            FirstPersonTuning.MinPitch,
            FirstPersonTuning.MaxPitch);
    }

    /// <summary>
    /// Turns the movement keys into a wish vector and hands it to the body, which owns
    /// gravity and walls. The keys are the player's own <c>camera.move-*</c> bindings, the
    /// four axes <c>CameraController.Update_KeyBasedMovement</c> reads, so a rebound key
    /// keeps working.
    ///
    /// Runs even when we are not steering, so that letting go of the controls over a gap
    /// still drops you.
    /// </summary>
    private void Walk(
        CameraController controller, InputDownstreamContext context, IMapModel map, bool steering, float deltaTime)
    {
        double2 wish = double2.zero;
        float vertical = 0f;
        bool jump = false;

        if (steering)
        {
            // Re-checked rather than latched, so loading a save where these have not been
            // bought puts the player back on the floor instead of leaving them airborne, and
            // off a train rather than riding one they are not entitled to.
            if (Body.Flying && !FirstPersonResearch.FlightUnlocked)
            {
                Body.Flying = false;
            }

            if (Body.Riding && !FirstPersonResearch.TrainRidingUnlocked)
            {
                Body.Riding = false;
                Trains.Disembark();
            }

            // Double-tap jump, the way a creative mode does it. The first tap is still a
            // jump, which is what makes the gesture feel like one rather than a hotkey.
            // Skipped entirely when flight is off, so a double jump is just two jumps rather
            // than a refusal message.
            if (Keys.JumpPressed && FirstPersonControl.FlightEnabled)
            {
                float now = Time.unscaledTime;

                if (now - LastJumpTap <= FirstPersonTuning.FlyDoubleTapSeconds)
                {
                    LastJumpTap = 0f;
                    ToggleFlight();
                }
                else
                {
                    LastJumpTap = now;
                }
            }

            // Kept as well as the double tap: a fixed key is worth having when the gesture
            // is awkward, and it costs nothing - M is the only letter the game leaves free.
            if (Keys.Fly)
            {
                ToggleFlight();
            }

            if (Keys.Travel)
            {
                TravelToNextWaypoint();
            }

            float right = context.ConsumeAsAxis("camera.move-right") - context.ConsumeAsAxis("camera.move-left");
            float forward = context.ConsumeAsAxis("camera.move-up") - context.ConsumeAsAxis("camera.move-down");

            float speed = Body.Flying ? FirstPersonControl.FlySpeed : FirstPersonControl.WalkSpeed;
            float sprint = 1f;
            if (controller.Keybindings.TryGet("camera.move-faster", out Keybinding faster) && faster.CurrentlyActive)
            {
                sprint = FirstPersonControl.SprintMultiplier;
                speed *= sprint;
            }

            // The rig is yawed about Y and the camera looks down its local +Z, so a yaw of
            // theta sends forward to (sin, cos) and right to (cos, -sin) in the world XZ
            // plane - which is what Viewport.Position is indexed by.
            float radians = math.radians(Yaw);
            double2 forwardDirection = new double2(math.sin(radians), math.cos(radians));
            double2 rightDirection = new double2(math.cos(radians), -math.sin(radians));

            double2 direction = forwardDirection * forward + rightDirection * right;
            if (math.length(direction) > 0.001)
            {
                wish = math.normalize(direction) * speed * deltaTime;
            }

            jump = Keys.JumpHeld;

            if (Body.Riding && jump)
            {
                // Jumping off is the reflex, so it works as well as the board key does.
                LeaveTrain("Jumped off the train.");
            }

            if (Body.Flying)
            {
                if (Keys.JumpHeld)
                {
                    vertical += 1f;
                }

                if (Keys.SinkHeld)
                {
                    vertical -= 1f;
                }

                // Sprint applies to climbing and diving too. The body scales this by
                // FlySpeed, so without it going up stayed slow while going forward got fast.
                vertical *= sprint;
            }
        }

        Body.Step(map, wish, vertical, jump, deltaTime);
    }

    private void ToggleFlight()
    {
        if (!FirstPersonControl.FlightEnabled)
        {
            Notifier.Show("Flight is not available in this game.", HUDNotificationType.Warning);
            return;
        }

        if (!FirstPersonResearch.FlightUnlocked)
        {
            Notifier.Show("Flight needs the Jet Pack research.", HUDNotificationType.Warning);
            return;
        }

        Body.Flying = !Body.Flying;
        Notifier.Show(Body.Flying ? "Flying." : "Walking.");
    }

    /// <summary>
    /// Boards the train under the crosshair.
    ///
    /// <paramref name="announceFailure"/> is only set on the frame the key went down: while
    /// it is held this runs every frame, and saying "no train in reach" sixty times a second
    /// is worse than saying nothing.
    /// </summary>
    private void TryBoardTrain(Viewport viewport, bool announceFailure)
    {
        if (!FirstPersonResearch.TrainRidingUnlocked)
        {
            if (announceFailure)
            {
                Notifier.Show(FirstPersonControl.TrainRidingEnabled
                        ? "Riding trains needs the Train Riding research."
                        : "Riding trains is not available in this game.",
                    HUDNotificationType.Warning);
            }

            return;
        }

        Transform camera = viewport.MainCamera.transform;

        if (Trains.TryBoard(camera.position, camera.forward))
        {
            Body.Riding = true;
            Notifier.Show("Riding the train. Jump to get off.");
        }
        else if (announceFailure)
        {
            Notifier.Show("No train in reach of the crosshair.", HUDNotificationType.Warning);
        }
    }

    private void LeaveTrain(string message)
    {
        Body.Riding = false;
        Trains.Disembark();
        Notifier.Show(message);
    }

    /// <summary>
    /// Places the rider on the wagon. Absolute rather than incremental - the player *is* at
    /// the wagon's position each frame - so nothing accumulates and nothing drifts, however
    /// the wagon is moving. That includes the jumps between platforms, where the train
    /// leaves the rails entirely and takes the player with it.
    /// </summary>
    private void RideTrain()
    {
        if (!Body.Riding)
        {
            return;
        }

        if (Trains.TryGetRidingPosition(out Vector3 wagon, out Vector3 up))
        {
            // Placed by the **eye**, not by the feet.
            //
            // The offset follows the wagon's own up - so an upside-down rail hangs the rider
            // under the track rather than burying them in it - but the body's eye height is
            // always along *world* up, because the player is never rolled over. Hanging
            // below, that 1.7 pushes the camera back towards the wagon, which is why the
            // first attempt clipped into the train underneath while being perfect on top.
            //
            // Solving for the eye and subtracting the eye height afterwards mirrors the two
            // cases properly, and leaves the right-way-up case arithmetically identical to
            // what it was.
            //
            // The whole vector is used rather than its sign, so a flip
            // (`FlippingTrainTransformSolver` lerps pitch through 180) or a rail lift carries
            // the rider round with the wagon instead of teleporting them the moment it
            // passes horizontal.
            Vector3 eye = wagon
                          + up * (FirstPersonControl.TrainRideHeight + FirstPersonTuning.EyeHeight);

            Body.Horizontal = new double2(eye.x, eye.z);
            Body.Height = eye.y - FirstPersonTuning.EyeHeight;
            return;
        }

        Body.Riding = false;
        Trains.Disembark();
        Notifier.Show("The train is gone.", HUDNotificationType.Warning);
    }

    /// <summary>
    /// Steps to the next of the player's own waypoints and drops the body there.
    ///
    /// The height is the waypoint's island floor rather than anything it recorded about the
    /// camera - a waypoint stores where the *map* camera was, which is above the factory,
    /// not standing in it. Gravity settles the rest a frame later, the same way entering at
    /// the vortex does.
    /// </summary>
    private void TravelToNextWaypoint()
    {
        if (!RefuseIfTravelLocked() )
        {
            return;
        }

        if (!Travel.TryNext(CurrentPlayer, out IPlayerWaypoint waypoint, out int position, out int count))
        {
            Notifier.Show("No waypoints yet - place one first.", HUDNotificationType.Warning);
            return;
        }

        string name = string.IsNullOrEmpty(waypoint.Name) ? "waypoint " + position : waypoint.Name;
        TravelTo(waypoint, name + "  (" + position + " of " + count + ")");
    }

    /// <summary>
    /// False when travel is unavailable, having said so. Shared by the key and the click.
    /// </summary>
    private bool RefuseIfTravelLocked()
    {
        if (FirstPersonResearch.TravelUnlocked)
        {
            return true;
        }

        Notifier.Show(FirstPersonControl.TravelEnabled
                ? "Fast travel needs the Waypoint Travel research."
                : "Fast travel is not available in this game.",
            HUDNotificationType.Warning);

        return false;
    }

    /// <summary>
    /// Puts the body at a waypoint. Shared by the travel key and by every click the game
    /// itself routes through <c>HUDEvents.RequestMoveToViewport</c> - the waypoint list and
    /// the home icon both end up here, so in first person they walk you there instead of
    /// pulling the map camera out to space view and leaving your body behind.
    ///
    /// Returns false when there is nothing to travel to, so the caller can fall back to the
    /// stock behaviour rather than silently swallowing the click.
    /// </summary>
    public bool TravelTo(IPlayerWaypoint waypoint, string announcement = null)
    {
        if (waypoint == null)
        {
            return false;
        }

        // Returns true - "handled" - rather than falling through. Falling through would let
        // the stock camera move run, which flies the map view out to space while the player
        // is standing in their factory.
        if (!RefuseIfTravelLocked())
        {
            return true;
        }

        // The waypoint's own Zoom and Angle are deliberately ignored: they describe where
        // the *map* camera was, which is above the factory looking down, not standing in
        // it. Only the ground position, the layer and the facing survive the trip.
        Body.Reset(new double2(waypoint.PositionX, waypoint.PositionY), waypoint.IslandLayer * 20);
        Body.Riding = false;
        Trains.Disembark();

        // Arrive on your feet. Staying airborne after a teleport means dropping out of the
        // sky somewhere you have not seen yet, and the layer sync is off while flying - so
        // you would also arrive on whatever layer you left from.
        Body.Flying = false;

        Yaw = FastMath.NormalizeAngleDegrees(waypoint.RotationDegrees);
        Pitch = 0f;

        Notifier.Show(announcement ?? (string.IsNullOrEmpty(waypoint.Name) ? "Travelled." : waypoint.Name));
        return true;
    }

    /// <summary>
    /// Writes the viewport first and the transform second, in the same shape the stock
    /// controller would have produced, then re-publishes the three shader globals it owns.
    /// Nothing else sets those, so skipping the original means skipping them too, and the
    /// cursor highlight would freeze wherever it last was.
    /// </summary>
    private void Apply(Viewport viewport, IMapModel map)
    {
        viewport.Position = Body.Horizontal;
        viewport.RotationDegrees = Yaw;
        viewport.Angle = Pitch;
        viewport.Zoom = FirstPersonTuning.ReportedZoom;
        viewport.TargetZoom = FirstPersonTuning.ReportedZoom;

        // Let the game's own notion of "which layer am I on" follow the player's feet, so
        // climbing to an upper platform shows that platform's buildings. The building layer
        // within it is left alone - that is the player's choice of what to build on, and
        // standing on a belt while building on the floor beside it is normal.
        //
        // **Except while flying.** Altitude is not a choice of layer: hovering three layers
        // up to see what you are doing would otherwise drag the build layer up with you and
        // make it impossible to put a space belt on the floor you are looking down at. With
        // the sync off, Q and E - `camera.select-layer-down` and `-up` - do what they always
        // do, and the layer is the player's to pick while their body is somewhere else
        // entirely. Landing resumes the sync and snaps the layer back to where they stand.
        //
        // Reach is not the limiting factor here either: platform placement goes through
        // `TryGetChunkCoordinateAtCursor`, whose reach is measured in chunks, so a floor
        // sixty units below is still comfortably in range.
        if (map != null && !Body.Flying)
        {
            viewport.IslandLayer = Body.IslandLayer(map);
        }

        Transform camera = viewport.MainCamera.transform;
        Transform rig = camera.parent;

        rig.position = new Vector3(
            (float)Body.Horizontal.x,
            Body.Height + FirstPersonTuning.EyeHeight,
            (float)Body.Horizontal.y);
        rig.localRotation = Quaternion.Euler(0f, Yaw, 0f);
        camera.localPosition = Vector3.zero;
        camera.localRotation = Quaternion.Euler(Pitch, 0f, 0f);

        Shader.SetGlobalFloat(GlobalShaderInputs.Zoom, viewport.Zoom);
        Shader.SetGlobalFloat(GlobalShaderInputs.CameraAngle, viewport.Angle);

        // Straight out of Update_MouseShaderParams, except that the cursor is the middle of
        // the screen. The sentinel is what the game uses to mean "not on the ground", which
        // at head height is most of the time.
        float3 cursor = new float3(1E+20f);
        if (RaycastHelpers.TryGetCursorPointOnVirtualPlane(
                FirstPersonTargeting.ScreenCentre, viewport.Height, viewport.MainCamera,
                out double3 hit, out double _))
        {
            cursor = (float3)hit;
        }

        Shader.SetGlobalVector(GlobalShaderInputs.CursorWorldPos, (Vector3)cursor);

        // Published rather than queried, because the targeting hooks are called from the
        // game's own placement code and have no way back to the body.
        Targeting.ReachMultiplier = Body.Flying ? FirstPersonTuning.FlyingReachMultiplier : 1f;

        if (CursorFreed || OverlayOpen)
        {
            // The real pointer is back - either because the cursor key is held, or because a
            // dialog took it. A crosshair beside it is just a second cursor, and over the
            // research shop or the pause menu it is a crosshair aiming at a menu.
            Crosshair.Hide();
        }
        else
        {
            Crosshair.Show();

            // Recomputed here rather than read back from the placement hook, because that
            // hook only runs while a placer is active and the crosshair should mean
            // something the rest of the time too.
            Crosshair.SetTargeted(Targeting.TryGetTile(viewport, out GlobalTileCoordinate _));
        }
    }

    /// <summary>
    /// Stands the mod down when the camera hook stops being called.
    ///
    /// Everything this class does on the way out - hiding the crosshair, giving the cursor
    /// back, restoring the field of view - happens inside <see cref="Update"/>, and
    /// <see cref="Update"/> only runs because <c>PlayerInteractionOrchestrator</c> calls
    /// <c>CameraController.OnGameUpdate</c>. Leaving a session for the main menu takes the
    /// player interaction with it, so the hook simply stops firing: nothing throws, nothing
    /// is logged, and `Active` stays true forever with a crosshair floating over the menu.
    ///
    /// This runs from the mod's per-frame tick instead, which is a postfix on
    /// <c>GameSessionOrchestrator.Tick</c> and so keeps running for the menu's background
    /// game. A generous threshold, because a frame or two with no camera update during a
    /// load is normal and being thrown out of first person for it would be worse than the
    /// symptom.
    /// </summary>
    public void Watchdog()
    {
        if (!Active || Time.frameCount - LastUpdateFrame <= FirstPersonTuning.CameraLostFrames)
        {
            return;
        }

        Logger.Info?.Log("First person: the camera update stopped, standing down.");
        Restore();
    }

    /// <summary>
    /// Leaves no trace on unload: a locked cursor, a 75 degree field of view or a stray
    /// canvas surviving the mod would be invisible in the log and blamed on the game.
    /// </summary>
    public void Dispose()
    {
        if (Active)
        {
            Restore();
        }

        Crosshair.Dispose();

        // The captured HUDEvents belongs to a session that outlives the mod on a hot reload,
        // and a stale one would fire notifications into a HUD that no longer exists.
        Notifier.Release();
        Travel.Forget();
        Trains.Collecting = false;
        Trains.Detach();
    }
}
