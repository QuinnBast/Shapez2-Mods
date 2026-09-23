using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core.Coordinates;
using Game.Core.GameData.Presets;
using Game.Core.GameData.Scenario;
using Game.Core.Rendering.Culling;
using Game.Core.Research;
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

    private delegate Sprite GetImageOrig(GameData gameData, GameImageId uniqueId);

    private delegate bool ReadScenarioOrig(
        ScenarioReader reader, string text, string source,
        out SerializedGameScenario serialized, out GameScenarioData data);

    private delegate bool ReadScenarioHook(
        ReadScenarioOrig orig, ScenarioReader reader, string text, string source,
        out SerializedGameScenario serialized, out GameScenarioData data);

    private delegate IReadOnlyList<GameScenarioParametersPreset> PresetsOrig(GameData gameData);

    private delegate void ShapeHighlightOrig(
        IslandPlacementHelperHighlightShapeResources helper, FrameDrawOptions options, IMapModel map);

    private delegate void FluidHighlightOrig(
        IslandPlacementHelperHighlightFluidResources helper, FrameDrawOptions options, IMapModel map);

    private delegate Bounds ResourceBoundsOrig(
        SpaceThemeBoundsProvider provider, IMapResourceSource mapResource);

    private delegate bool ChunkAtScreenOrig(
        Viewport viewport, in float2 screenCoordinate, out GlobalChunkCoordinate chunk_GC);

    private delegate bool ChunkAtScreenHook(
        ChunkAtScreenOrig orig, Viewport viewport, in float2 screenCoordinate,
        out GlobalChunkCoordinate chunk_GC);

    private delegate void SuperChunksDrawOrig(
        SuperChunksDrawer drawer, FrameDrawOptionsNoLOD options, MapCullResult cullResult);

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
        FirstPersonIcons.Bind(logger);

        // Attached in the constructor because it has to exist before a save is read, and
        // registered both ways round: the position is copied out of the body just before the
        // game writes, and handed to the camera once a save has been read. The camera keeps
        // the object rather than a copy, so a session that starts without one - a fresh game -
        // simply has nothing to resume from.
        this.AttachSaveData<FirstPersonSaveData>();
        this.RegisterToBeforeSaveDataSerialized<FirstPersonSaveData>(Camera.CapturePosition);
        this.RegisterToAfterSaveDataDeserialized<FirstPersonSaveData>(data => Camera.Saved = data);

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

            // Our shop icons are ids GameData has never heard of, and GetImage throws on one
            // of those rather than returning null - which takes the whole research screen
            // with it. Answering for our own prefix is all this does.
            Hooks.Add(new Hook(
                typeof(GameData).GetMethod(nameof(GameData.GetImage)),
                new Func<GetImageOrig, GameData, GameImageId, Sprite>(OnGetImage)));

            // The mod's scenario. Both of these are ordinary methods on the way to the
            // menu's scenario list, which is why the mod reaches the scenario through them
            // rather than through the constructors of ScenarioCollection and
            // ScenarioPresetCollection - those would be the obvious seams, and both are
            // constructors.
            Hooks.Add(new Hook(
                typeof(ScenarioReader).GetMethod(nameof(ScenarioReader.TryReadingScenario)),
                new ReadScenarioHook(OnReadScenario)));

            Hooks.Add(new Hook(
                typeof(GameData).GetProperty(nameof(GameData.ScenarioParameterPresets)).GetGetMethod(),
                new Func<PresetsOrig, GameData, IReadOnlyList<GameScenarioParametersPreset>>(OnPresets)));

            // Miner placement sweeps the whole map for patches to highlight. See
            // FirstPersonPlacementHighlight for why an eye-level camera makes that expensive
            // and an overhead one does not.
            Hooks.Add(new Hook(
                typeof(IslandPlacementHelperHighlightShapeResources)
                    .GetMethod(nameof(IslandPlacementHelperHighlightShapeResources.Draw)),
                new Action<ShapeHighlightOrig, IslandPlacementHelperHighlightShapeResources,
                    FrameDrawOptions, IMapModel>(OnShapeResourceHighlight)));

            Hooks.Add(new Hook(
                typeof(IslandPlacementHelperHighlightFluidResources)
                    .GetMethod(nameof(IslandPlacementHelperHighlightFluidResources.Draw)),
                new Action<FluidHighlightOrig, IslandPlacementHelperHighlightFluidResources,
                    FrameDrawOptions, IMapModel>(OnFluidResourceHighlight)));

            // `SuperChunksDrawer.Draw` starts by asking which chunk the centre of the screen
            // is over, and gives up entirely when the answer is "none" - which at eye level
            // is every frame the horizon is below the crosshair.
            Hooks.Add(new Hook(
                typeof(ScreenUtils).GetMethods()
                    .First(method => method.Name == nameof(ScreenUtils.TryGetChunkCoordinate)
                                     && method.GetParameters().Length == 3),
                new ChunkAtScreenHook(OnChunkAtScreen)));

            // ...and then culls each resource against the camera frustum, so pitch decides
            // what exists. Swap the frustum for a box around the player for the duration.
            Hooks.Add(new Hook(
                typeof(SuperChunksDrawer).GetMethod(nameof(SuperChunksDrawer.Draw)),
                new Action<SuperChunksDrawOrig, SuperChunksDrawer, FrameDrawOptionsNoLOD,
                    MapCullResult>(OnDrawSuperChunks)));

            // Asteroids appearing when you look down and vanishing when you look up. The
            // bounds they are culled against are pinned to a fixed height band that has
            // nothing to do with where they are drawn.
            Hooks.Add(new Hook(
                typeof(SpaceThemeBoundsProvider)
                    .GetMethod(nameof(SpaceThemeBoundsProvider.ComputeResourceSourceBounds)),
                new Func<ResourceBoundsOrig, SpaceThemeBoundsProvider, IMapResourceSource, Bounds>(
                    OnResourceBounds)));

            // The scenario picker has no scroll view, and this mod is what pushes it past
            // what fits. Postfix, because the cards are created inside the call.
            Hooks.Add(DetourHelper.CreatePostfixHook<HUDMenuSelectScenarioState, object>(
                (state, payload) => state.OnMenuEnterState(payload),
                OnScenarioMenuEntered));

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

        // Holding the cursor key does not change the answer, only the pointer. Refusing here
        // used to look right - the player is aiming at the HUD, not at the world - but a
        // refusal is what makes the game's path trackers `Peek` an empty stack and throw, and
        // the clamped answer is always within reach of the player, so nothing can be placed
        // anywhere surprising by it. See FirstPersonTargeting.TryReach.
        return Camera.Targeting.TryGetTile(viewport, out tile_G);
    }

    private bool OnChunkAtCursor(ChunkAtCursorOrig orig, Viewport viewport, out GlobalChunkCoordinate chunk_GC)
    {
        if (!Camera.Active)
        {
            return orig(viewport, out chunk_GC);
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
            Camera.SetInput(FirstPersonInput.Read(
                context, Camera.Active, options.Player?.InteractionState?.PlacingAnything ?? false));
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
    /// Serves the mod's own research icons; everything else is the game's.
    /// </summary>
    private Sprite OnGetImage(GetImageOrig orig, GameData gameData, GameImageId uniqueId)
    {
        if (FirstPersonIcons.TryGetSprite(uniqueId.Id, out Sprite sprite))
        {
            return sprite;
        }

        return orig(gameData, uniqueId);
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

    /// <summary>
    /// Refuses the mod's own scenario when a downstream mod has switched it off, and
    /// contains anything the mod's own scenario throws.
    ///
    /// Returning false is the reader's own "this file is not a scenario" answer -
    /// <c>ScenarioCollection</c> simply skips a promise that fails - so nothing else has to
    /// know the scenario was ever there. The test is against the raw JSON because that is
    /// all a promise carries at this point; the id does not exist as a field until after
    /// the deserialise this hook may be about to skip.
    ///
    /// The catch is the more important half. <c>TryReadingScenario</c> resolves
    /// <c>#include:</c> references **before** its try block:
    ///
    /// <code>
    /// text = new IncludePreProcessorSolver("#include:").Process(text);  // not guarded
    /// try { serializedGameScenario = …Deserialize(text); } catch { … return false; }
    /// </code>
    ///
    /// and <c>IncludePreProcessorSolver</c> throws <c>Could not resolve path …</c> for a
    /// reference it cannot find. That throw comes out through <c>GameData</c>'s constructor
    /// and <c>LoadGameDataBlindStep</c>, so a scenario file naming one path the current
    /// game version no longer ships does not fail to load - **it stops the game starting**.
    /// Our scenario is the default one with three strings changed, so it names about a
    /// dozen of the game's own resource paths, every one of which is a future game update
    /// away from moving. Catching here turns that from a dead install into a missing menu
    /// entry.
    ///
    /// Only for our own file. Swallowing another scenario's failure would hide a real bug
    /// in the game or in someone else's mod.
    /// </summary>
    private bool OnReadScenario(
        ReadScenarioOrig orig, ScenarioReader reader, string text, string source,
        out SerializedGameScenario serialized, out GameScenarioData data)
    {
        bool ours = FirstPersonScenario.IsOurs(text);

        if (ours && !FirstPersonControl.ScenarioEnabled)
        {
            serialized = null;
            data = null;
            return false;
        }

        if (!ours)
        {
            return orig(reader, text, source, out serialized, out data);
        }

        try
        {
            return orig(reader, text, source, out serialized, out data);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            Logger.Error?.Log(
                "First Person: the scenario could not be read and will not appear in the menu. "
                + "Its #include: paths are the game's own, so a game update may have moved one.");
            serialized = null;
            data = null;
            return false;
        }
    }

    /// <summary>
    /// Stamps the scenario's map generation onto its preset, and hides the preset when the
    /// scenario is switched off.
    ///
    /// This getter is the only public way to the preset list and every route into a new
    /// game goes through it, which makes it the one place both jobs can be done without
    /// hooking a constructor. Stamping is assignment, so running it per call is idempotent
    /// and cheap; the filter allocates, but only in the switched-off case.
    /// </summary>
    private IReadOnlyList<GameScenarioParametersPreset> OnPresets(PresetsOrig orig, GameData gameData)
    {
        IReadOnlyList<GameScenarioParametersPreset> presets = orig(gameData);

        if (!FirstPersonControl.ScenarioEnabled)
        {
            return FirstPersonScenario.WithoutOurPreset(presets);
        }

        bool wasApplied = FirstPersonScenario.MapGenerationApplied;
        FirstPersonScenario.ApplyMapGeneration(presets);

        if (!wasApplied && FirstPersonScenario.MapGenerationApplied)
        {
            // Said once, because the silent failure here - the JIT inlining this getter into
            // its caller, so the hook is never reached - looks exactly like nothing having
            // happened. A log line turns "the map came out vanilla" into a question with an
            // answer.
            Logger.Info?.Log("First Person: scenario map generation applied to "
                             + FirstPersonScenario.PresetId + ".");
        }

        return presets;
    }

    /// <summary>
    /// Answers "which chunk is the middle of the screen over" with the player's own chunk
    /// when the ray misses the build plane.
    ///
    /// <c>SuperChunksDrawer.Draw</c> opens with
    ///
    /// <code>
    /// if (!ScreenUtils.TryGetChunkCoordinate(viewport, in ScreenUtils.ScreenCenter, out var c))
    /// {
    ///     return;
    /// }
    /// </code>
    ///
    /// and that coordinate is only a **seed** - the method floods outward from it through
    /// neighbouring super chunks. But the lookup is the flat-plane intersection, and from
    /// eye level a ray aimed above the horizon never meets the plane, so the drawer returns
    /// having drawn nothing at all and every asteroid in the world disappears until the
    /// crosshair drops below the horizon again. Riding the underside of a track mirrors it:
    /// the plane is overhead there, and looking *down* is what empties the sky.
    ///
    /// The player's own chunk is the right seed anyway. It is where the flood should start
    /// from, and unlike the intersection it always exists.
    ///
    /// No conflict with the <c>...AtCursor</c> hook: that one replaces its own method
    /// outright while first person is on and never reaches this one.
    /// </summary>
    private bool OnChunkAtScreen(
        ChunkAtScreenOrig orig, Viewport viewport, in float2 screenCoordinate,
        out GlobalChunkCoordinate chunk_GC)
    {
        if (orig(viewport, in screenCoordinate, out chunk_GC))
        {
            return true;
        }

        if (!Camera.Active || viewport == null)
        {
            return false;
        }

        chunk_GC = Camera.PlayerChunk(viewport);
        return true;
    }

    /// <summary>
    /// Draws map resources around the player rather than only in front of them.
    ///
    /// Everything <c>SuperChunksDrawer</c> decides is a frustum test - which super chunks to
    /// visit (<c>CullChunk</c>), and each patch within them against the patch's bounds - and
    /// those bounds are a flat slab, because
    /// <c>SpaceThemeBoundsProvider.ComputeResourceSourceBounds</c> throws the patch's height
    /// away in favour of a fixed band. A slab sits at a fixed angle from an eye-level camera,
    /// so pitch decides whether the map has asteroids in it.
    ///
    /// Widening the slab was the first attempt and it did not work, for a reason worth
    /// recording: <c>SuperChunksDrawer.GetResourceBounds</c> **caches** the answer per
    /// resource for the life of the drawer, so a hook on the bounds runs once per patch and
    /// is then served from a dictionary forever.
    ///
    /// Replacing the frustum for the duration of the call sidesteps both. The drawer's own
    /// distance gates still apply - 5500 units for a resource, <c>MaxRenderDistanceSq</c> for
    /// a super chunk - so this widens *what* is considered rather than how far.
    /// </summary>
    private void OnDrawSuperChunks(
        SuperChunksDrawOrig orig, SuperChunksDrawer drawer, FrameDrawOptionsNoLOD options,
        MapCullResult cullResult)
    {
        if (!Camera.Active || FirstPersonControl.ResourceRenderRadius <= 0f)
        {
            orig(drawer, options, cullResult);
            return;
        }

        FirstPersonPlacementHighlight.WithinRadius(
            options, FirstPersonControl.ResourceRenderRadius,
            () => orig(drawer, options, cullResult));
    }

    /// <summary>
    /// Makes a resource patch's culling bounds cover where it is actually drawn.
    ///
    /// <c>SpaceThemeBoundsProvider.ComputeResourceSourceBounds</c> takes the patch's real
    /// world bounds and then throws its height away:
    ///
    /// <code>
    /// min.y = MapResourceMinHeight;   // -50
    /// max.y = MapResourceMaxHeight;   // -22
    /// result.SetMinMax(min, max);
    /// </code>
    ///
    /// From an overhead camera that is harmless - the frustum points at the ground and that
    /// band is always inside it. At eye level it is the whole bug: the band sits a fixed
    /// distance below the horizon, so looking down a few degrees brings every asteroid in
    /// the map into view at once and looking up takes them all away, while their meshes never
    /// moved. That is the popping, and it is also part of why arriving is expensive - the
    /// band is a flat slab a near-level frustum slices through for a very long way.
    ///
    /// Growing the box to include the patch's own extent is all this does, and all it may
    /// do: <c>SuperChunksDrawer.GetResourceBounds</c> caches the result per resource for the
    /// life of the drawer, so anything depending on where the player is would be frozen at
    /// whatever it was the first time that patch was seen - and would outlive first person
    /// itself. The pitch problem is solved by replacing the frustum instead, in
    /// <see cref="OnDrawSuperChunks"/>.
    ///
    /// <c>Bounds.Encapsulate</c> only ever enlarges, so nothing that was visible before can
    /// become invisible, and the game's band is left in place rather than replaced.
    /// </summary>
    private Bounds OnResourceBounds(
        ResourceBoundsOrig orig, SpaceThemeBoundsProvider provider, IMapResourceSource mapResource)
    {
        Bounds bounds = orig(provider, mapResource);

        if (!Camera.Active || mapResource == null)
        {
            return bounds;
        }

        bounds.Encapsulate(mapResource.Bounds_GC.ToWorldBounds());
        return bounds;
    }

    /// <summary>
    /// Bounds the shape-patch highlight to the distance the player could actually place at.
    /// </summary>
    private void OnShapeResourceHighlight(
        ShapeHighlightOrig orig, IslandPlacementHelperHighlightShapeResources helper,
        FrameDrawOptions options, IMapModel map)
    {
        if (!Camera.Active)
        {
            orig(helper, options, map);
            return;
        }

        FirstPersonPlacementHighlight.WithinRadius(
            options, HighlightRadius, () => orig(helper, options, map));
    }

    /// <summary>
    /// The same for fluid patches. Its loop is cheaper - no stack of ten indicator planes per
    /// chunk - but it sweeps the same super chunks through the same frustum.
    /// </summary>
    private void OnFluidResourceHighlight(
        FluidHighlightOrig orig, IslandPlacementHelperHighlightFluidResources helper,
        FrameDrawOptions options, IMapModel map)
    {
        if (!Camera.Active)
        {
            orig(helper, options, map);
            return;
        }

        FirstPersonPlacementHighlight.WithinRadius(
            options, HighlightRadius, () => orig(helper, options, map));
    }

    /// <summary>
    /// How far the resource highlight is allowed to look, in world units. Tracks the flying
    /// multiplier, so the hint reaches as far as the placement does.
    /// </summary>
    private float HighlightRadius =>
        FirstPersonTuning.ChunkReach
        * Camera.Targeting.ReachMultiplier
        * FirstPersonTuning.PlacementHighlightMargin;

    /// <summary>
    /// Adds a scroll view to the scenario picker once its cards exist.
    ///
    /// Guarded because it is surgery on the live menu hierarchy: a throw here would come out
    /// through the menu state machine, and a main menu that cannot open a page is a worse
    /// outcome than a list that does not scroll.
    /// </summary>
    private void OnScenarioMenuEntered(HUDMenuSelectScenarioState state, object payload)
    {
        try
        {
            FirstPersonScenarioMenu.EnsureScrollable(state.UIScenariosParent, Logger);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    private void OnTick(float deltaTime)
    {
        FirstPersonKeybindings.EnsureRegistered(Logger);

        // A postfix on GameSessionOrchestrator.Tick, which keeps running for the main menu's
        // background game - unlike the camera hook, which stops with the player interaction
        // when a session ends. That is the gap the watchdog covers.
        Camera.Watchdog();
    }

    public void Dispose()
    {
        // Order matters: the camera's exit path writes state the controller reads on its
        // next update, so it has to run while our hooks are still installed.
        Camera.Dispose();
        Unhook();
        this.UnregisterToBeforeSaveDataSerialized<FirstPersonSaveData>(Camera.CapturePosition);
        GameRewirers.RemoveRewirer(FlightResearchHandle);
        GameRewirers.RemoveRewirer(TickHandle);
        FirstPersonKeybindings.Unregister();
        FirstPersonIcons.Forget();
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
