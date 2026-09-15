using System;
using System.Collections.Generic;
using Game.Core.Coordinates;
using JetBrains.Annotations;
using MonoMod.RuntimeDetour;
using ShapezShifter.Flow;
using ShapezShifter.Hijack;
using ShapezShifter.SharpDetour;
using UnityEngine;
using Unity.Mathematics;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// First Person.
///
/// Four hooks, in two groups.
///
/// The camera: <see cref="CameraController.OnGameUpdate"/> is the single place the camera
/// transform is written, called once per frame from
/// <c>PlayerInteractionOrchestrator.OnGameUpdate</c>. <see cref="FirstPersonCamera"/>
/// decides per frame whether to let the original run or to drive the camera itself.
///
/// The crosshair: three hooks that together move "under the cursor" to the middle of the
/// screen. <c>Viewport.CursorScreenPosition</c> is the cursor every query starts from, and
/// a locked cursor freezes the <c>Input.mousePosition</c> behind it, so without this the
/// game keeps asking about wherever the pointer was when first person started. The two
/// <c>ScreenUtils</c> hooks then fix the flat-plane intersection that placement uses -
/// see <see cref="FirstPersonTargeting"/> for why it needs fixing.
///
/// Every hook is on a non-generic type, which is the constraint that matters: MonoMod
/// refuses a method whose declaring type is generic, and fails at construction rather than
/// at the call.
/// </summary>
[UsedImplicitly]
public class FirstPersonMod : IMod
{
    /// <summary>
    /// A postfix cannot do the camera's job: the token that silences the stock camera has
    /// to be taken before it reads input, and when we are driving we want the original not
    /// to run at all. A prefix cannot either - it only rewrites arguments. So: raw hooks
    /// with hand-written delegate types, which is also the only way to reach an
    /// <c>out</c> parameter or a return value.
    /// </summary>
    private delegate void CameraUpdateOrig(
        CameraController controller, InputDownstreamContext context, FrameDrawOptions options);

    private delegate float2 CursorPositionOrig(Viewport viewport);

    private delegate bool TileAtCursorOrig(Viewport viewport, out GlobalTileCoordinate tile_G);

    private delegate bool TileAtCursorHook(
        TileAtCursorOrig orig, Viewport viewport, out GlobalTileCoordinate tile_G);

    private delegate bool ChunkAtCursorOrig(Viewport viewport, out GlobalChunkCoordinate chunk_GC);

    private delegate bool ChunkAtCursorHook(
        ChunkAtCursorOrig orig, Viewport viewport, out GlobalChunkCoordinate chunk_GC);

    private delegate void MoveToViewportOrig(CameraController controller, IPlayerWaypoint waypoint);

    private delegate bool ResourceOverlayAvailableOrig(HUDShapeResourcesVisualization visualization);

    private delegate void ToolbarInputOrig(HUDToolbarView view, InputDownstreamContext context);

    private delegate void LayerPlanesOrig(
        IslandPlayingFieldLayersDrawer drawer, FrameDrawOptionsNoLOD options, IIslandDefinition definition,
        GlobalChunkTransform transform, LODRenderConfig lod, bool renderContours);

    private readonly ILogger Logger;
    private readonly FirstPersonCamera Camera;
    private readonly List<Hook> Hooks = new List<Hook>();
    private readonly RewirerHandle FlightResearchHandle;
    private readonly RewirerHandle TickHandle;

    public FirstPersonMod(ILogger logger)
    {
        Logger = logger;
        Camera = new FirstPersonCamera(logger);

        // A definition rather than logic: built once per scenario load, so a hot reload
        // will not pick it up and the game needs restarting after an install.
        FlightResearchHandle = GameRewirers.AddRewirer(new FirstPersonResearch(logger));

        // Keybindings are appended from a tick rather than a hook, because Keybindings is
        // built inside GlobalsInitialization - quite possibly before mods load at all, which
        // would leave a constructor hook that never fires. Ticking until Globals has data is
        // immune to load order, and one dictionary lookup a frame until then is nothing.
        TickHandle = this.OnTick(OnTick);

        // Applying several related hooks is not atomic. If a later one throws, the earlier
        // ones are live and the mod is half-patched - a camera you can enter and a cursor
        // that still points at the old mouse position is worse than nothing.
        try
        {
            Hooks.Add(new Hook(
                typeof(CameraController).GetMethod(nameof(CameraController.OnGameUpdate)),
                new Action<CameraUpdateOrig, CameraController, InputDownstreamContext, FrameDrawOptions>(
                    OnCameraUpdate)));

            Hooks.Add(new Hook(
                typeof(Viewport).GetProperty(nameof(Viewport.CursorScreenPosition)).GetGetMethod(),
                new Func<CursorPositionOrig, Viewport, float2>(OnCursorScreenPosition)));

            Hooks.Add(new Hook(
                typeof(ScreenUtils).GetMethod(nameof(ScreenUtils.TryGetTileCoordinateAtCursor)),
                new TileAtCursorHook(OnTileAtCursor)));

            Hooks.Add(new Hook(
                typeof(ScreenUtils).GetMethod(nameof(ScreenUtils.TryGetChunkCoordinateAtCursor)),
                new ChunkAtCursorHook(OnChunkAtCursor)));

            // Before the HUD parts run, so the synthetic slot press is there when
            // HUDToolbarView and the library slots look for it.
            Hooks.Add(DetourHelper.CreatePrefixHook<HUD, InputDownstreamContext, FrameDrawOptions>(
                (hud, context, options) => hud.OnGameUpdate(context, options),
                OnHudUpdate));

            Hooks.Add(new Hook(
                typeof(IslandPlayingFieldLayersDrawer).GetMethod(nameof(IslandPlayingFieldLayersDrawer.Draw)),
                new Action<LayerPlanesOrig, IslandPlayingFieldLayersDrawer, FrameDrawOptionsNoLOD,
                    IIslandDefinition, GlobalChunkTransform, LODRenderConfig, bool>(OnDrawLayerPlanes)));

            Hooks.Add(new Hook(
                typeof(CameraController).GetMethod(nameof(CameraController.RequestMoveToViewport)),
                new Action<MoveToViewportOrig, CameraController, IPlayerWaypoint>(OnRequestMoveToViewport)));

            Hooks.Add(new Hook(
                typeof(HUDShapeResourcesVisualization)
                    .GetProperty(nameof(HUDShapeResourcesVisualization.IsAvailable)).GetGetMethod(),
                new Func<ResourceOverlayAvailableOrig, HUDShapeResourcesVisualization, bool>(
                    OnResourceOverlayAvailable)));

            // After the toolbar has taken its own hotkeys, and with the view in hand - the
            // wheel navigation drives HUDToolbarView's depth-parameterised helpers directly,
            // which is what lets it reach a level the game has no hotkey for.
            Hooks.Add(DetourHelper.CreatePostfixHook<HUDToolbarView, InputDownstreamContext>(
                (view, context) => view.ProcessInput(context),
                OnToolbarInput));

            // There is no static accessor for HUDEvents, so it is captured as the HUD builds
            // itself. Fires once per HUD part - the same object every time, so overwriting is
            // harmless - and is how the mod says anything to the player at all.
            Hooks.Add(DetourHelper.CreatePostfixHook<HUDPart, HUDEvents, Player, ILogger, IAnalyticsTracker>(
                (part, events, player, log, analytics) => part.Construct(events, player, log, analytics),
                (part, events, player, log, analytics) => Camera.Notifier.Capture(events)));
        }
        catch (Exception)
        {
            Unhook();
            throw;
        }

        Logger.Info?.Log("First Person ready on " + Hooks.Count + " hooks. "
                         + FirstPersonTuning.ToggleKey + " in a session to stand up.");
    }

    private void OnCameraUpdate(
        CameraUpdateOrig orig, CameraController controller, InputDownstreamContext context, FrameDrawOptions options)
    {
        bool handled;
        try
        {
            handled = Camera.Update(controller, context, options);
        }
        catch (Exception exception)
        {
            // A throw here costs the camera for the rest of the session, and a camera that
            // has stopped updating looks like a hang. Report once and give it back.
            Logger.Exception?.LogException(exception);
            Camera.Dispose();
            handled = false;
        }

        if (!handled)
        {
            orig(controller, context, options);
        }
    }

    /// <summary>
    /// The crosshair is the cursor. Everything that asks what is under the pointer -
    /// hovering, selecting, deleting, the placement helpers - starts here, so moving this
    /// one property to the centre of the screen redirects all of them at once.
    /// </summary>
    private float2 OnCursorScreenPosition(CursorPositionOrig orig, Viewport viewport)
    {
        return Aiming ? FirstPersonTargeting.ScreenCentre : orig(viewport);
    }

    /// <summary>
    /// First person is on *and* the crosshair is the pointer. While the cursor key is held
    /// the real mouse is back, so every one of these hooks stands down - hovering a side
    /// panel has to follow the actual pointer or nothing in the HUD can be clicked.
    /// </summary>
    private bool Aiming => Camera.Active && !Camera.CursorFreed;

    private bool OnTileAtCursor(TileAtCursorOrig orig, Viewport viewport, out GlobalTileCoordinate tile_G)
    {
        if (!Camera.Active)
        {
            return orig(viewport, out tile_G);
        }

        if (Camera.CursorFreed)
        {
            // Not "pass through to the original": that is the flat-plane intersection, and
            // from head height it answers with the map origin rather than a miss. Refusing
            // is the only safe answer while the player is aiming at the HUD.
            tile_G = default;
            return false;
        }

        return Camera.Targeting.TryGetTile(viewport, out tile_G);
    }

    private bool OnChunkAtCursor(ChunkAtCursorOrig orig, Viewport viewport, out GlobalChunkCoordinate chunk_GC)
    {
        if (!Camera.Active)
        {
            return orig(viewport, out chunk_GC);
        }

        if (Camera.CursorFreed)
        {
            chunk_GC = GlobalChunkCoordinate.Origin;
            return false;
        }

        return Camera.Targeting.TryGetChunk(viewport, out chunk_GC);
    }

    /// <summary>
    /// Turns the wheel into a hotbar slot press. Has to be a prefix on the HUD rather than
    /// anything of ours in <c>PlayerInteractionOrchestrator</c>, because the slot views are
    /// HUD parts and they run first - by the time our camera hook sees the frame, they have
    /// already looked for the keybinding and moved on.
    /// </summary>
    private (InputDownstreamContext, FrameDrawOptions) OnHudUpdate(
        HUD hud, InputDownstreamContext context, FrameDrawOptions options)
    {
        // This is the whole reason the keys are registered bindings. Reading them here, ahead
        // of every HUD part, means the input system's own de-duplication does the work three
        // hand-written suppression calls used to do: consuming a binding marks every other
        // active binding sharing its key code as consumed, so Space does not also flip the
        // interaction scope, Tab does not also cycle a variant, and F6 does not also trigger
        // debug.step-speed and pause the game.
        if (FirstPersonKeybindings.Ready)
        {
            Camera.SetInput(FirstPersonInput.Read(context, Camera.Active));
        }

        return (context, options);
    }

    /// <summary>
    /// Clicking a waypoint, or the home icon, walks you there instead of pulling the map
    /// camera out to space view and leaving your body behind.
    ///
    /// One hook covers both, because both go the same way: `HUDWaypoints.JumpToWaypointInternal`
    /// fires `HUDEvents.RequestMoveToViewport`, and `GameSessionOrchestrator` registers that
    /// event straight onto this method. The home icon is not a special case either -
    /// `JumpToHubInternal` builds a `PlayerWaypoint` at the scenario's starting location and
    /// sends it through the same call.
    ///
    /// Falls through to the original whenever first person is off, and also when the travel
    /// itself declines, so a click is never silently swallowed.
    /// </summary>
    private void OnRequestMoveToViewport(
        MoveToViewportOrig orig, CameraController controller, IPlayerWaypoint waypoint)
    {
        if (Camera.Active && Camera.TravelTo(waypoint))
        {
            return;
        }

        orig(controller, waypoint);
    }

    /// <summary>
    /// Wheel navigation of the toolbar, run after the toolbar's own input so a hotkey the
    /// player pressed this frame wins over the wheel.
    /// </summary>
    private void OnToolbarInput(HUDToolbarView view, InputDownstreamContext context)
    {
        if (!Camera.Active || Camera.OverlayOpen || Camera.CursorFreed)
        {
            return;
        }

        FirstPersonHotbar.Scroll(view, context, Camera.Notifier);
    }

    /// <summary>
    /// Keeps the shape-resource overlay available while flying, so a player down here can
    /// still find the island they are looking for.
    ///
    /// Its own test is <c>BaseState == Islands &amp;&amp; Viewport.Zoom >= 450</c>, and first
    /// person reports a zoom of 80, so it is never offered otherwise. Forcing the answer is
    /// enough - the overlay defaults to on, and its markers are sized by
    /// <c>85f + zoom * 0.05f</c>, which is dominated by the constant and so lands close to
    /// what it would be at the zoom the game intends.
    ///
    /// Only while flying. Searching for a resource island is something you do in the air; on
    /// the factory floor these markers would be clutter in front of your face.
    /// </summary>
    private bool OnResourceOverlayAvailable(
        ResourceOverlayAvailableOrig orig, HUDShapeResourcesVisualization visualization)
    {
        return Camera.Flying || orig(visualization);
    }

    /// <summary>
    /// Hides the translucent layer planes while first person is on.
    ///
    /// These are the blue sheets the game floats at the current build layer so you can see
    /// which one you are on from above. From above they are a helpful overlay; at eye
    /// height the nearest one fills the screen.
    ///
    /// The alpha comes from <c>Viewport.InterpolatedBuildingLayer</c> and the loop breaks
    /// immediately when it is below 1, so the planes only exist above layer 1 in the first
    /// place. An earlier version of this mod pinned the building layer to 0 to get rid of
    /// them, which worked but cost the player every upper layer - the planes are the
    /// problem, not the layers, so suppress the drawing and leave the layers alone.
    /// </summary>
    private void OnDrawLayerPlanes(
        LayerPlanesOrig orig, IslandPlayingFieldLayersDrawer drawer, FrameDrawOptionsNoLOD options,
        IIslandDefinition definition, GlobalChunkTransform transform, LODRenderConfig lod, bool renderContours)
    {
        if (Camera.Active)
        {
            return;
        }

        orig(drawer, options, definition, transform, lod, renderContours);
    }

    private void OnTick(float deltaTime)
    {
        FirstPersonKeybindings.EnsureRegistered(Logger);
    }

    public void Dispose()
    {
        // Order matters: the camera's exit path writes state the controller reads on its
        // next update, so it has to run while our hooks are still installed.
        Camera.Dispose();
        Unhook();
        GameRewirers.RemoveRewirer(FlightResearchHandle);
        GameRewirers.RemoveRewirer(TickHandle);
        FirstPersonKeybindings.Unregister();
    }

    private void Unhook()
    {
        foreach (Hook hook in Hooks)
        {
            hook?.Dispose();
        }

        Hooks.Clear();
    }
}
