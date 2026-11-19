using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Core.Collections;
using Core.Localization;
using Game.Core.Coordinates;
using MonoMod.RuntimeDetour;
using ShapezShifter.Kit;
using ShapezShifter.Textures;
using Unity.Mathematics;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Moves the view to a platform or building: its layer, the camera over it, and in to a working
/// distance if zoomed out further than that. Shared by the selection "View" action and the lift
/// stations' buttons.
/// </summary>
internal static class ViewPlatform
{
    /// <summary>A comfortable space-view distance, well inside the overview threshold of 1500.</summary>
    private const float SpaceViewZoom = 700f;

    /// <summary>Close enough to work on buildings - inside vanilla's BuildingsMaxZoom of 110.</summary>
    private const float BuildingViewZoom = 60f;

    public static void Go(IslandModel island)
    {
        if (island == IslandModel.Invalid || !TryViewport(out Viewport viewport))
        {
            return;
        }

        viewport.IslandLayer = island.Position.z;
        GlobalChunkBounds bounds = island.Bounds;
        FlyTo(WorldCoordinate.Lerp(bounds.Min.ToCenter_W(), bounds.Max.ToCenter_W(), 0.5f), viewport, SpaceViewZoom);
    }

    public static void Go(BuildingModel building)
    {
        if (!TryViewport(out Viewport viewport))
        {
            return;
        }

        // A tile's z holds both: island layer * 20 + building layer.
        GlobalTileCoordinate tile = building.Tile_G;
        viewport.IslandLayer = (short)Math.Floor(tile.z / 20f);
        viewport.BuildingLayer = tile.BuildingLayer();
        FlyTo(tile.ToCenter_W(), viewport, BuildingViewZoom);
    }

    private static void FlyTo(WorldCoordinate centre, Viewport viewport, float zoom)
    {
        CameraController camera = OverviewLayerParking.Camera;
        if (camera != null)
        {
            // Viewport.Position is Unity's x and z, not the game's x and y: the camera is placed at
            // (Position.x, height, Position.y), and Unity's z is the game's -y.
            Vector3 unity = (Vector3)centre;
            camera.TargetPosition = new double2(unity.x, unity.z);
        }

        if (viewport.TargetZoom > zoom)
        {
            viewport.TargetZoom = zoom;
        }
    }

    private static bool TryViewport(out Viewport viewport)
    {
#pragma warning disable CS0618 // Callbacks from the HUD have nothing to inject.
        viewport = StaticGameCoreAccessor.HasInstance() ? StaticGameCoreAccessor.G.Viewport : null;
#pragma warning restore CS0618
        return viewport != null;
    }
}

/// <summary>
/// A "View" action beside Copy, Save, Cut, Sweep and Delete for a selected platform or building,
/// with its own key (Z by default, rebindable in the game's settings): it takes the player to the
/// selection (<see cref="ViewPlatform"/>). With hundreds of layers, getting to what you have
/// selected - from the overview especially - is otherwise a long trip.
///
/// Those actions are what <c>HUDIslandSelectionDetails.GetActions</c> and
/// <c>HUDBuildingSelectionDetails.GetActions</c> return - a list of
/// <c>PlacementKeybindingHintData</c>, each an icon, a keybinding id and a handler, which the HUD
/// turns into both a button and a listener for the key. A postfix on each appends one more. Both
/// are declared overrides on non-generic classes, found with <c>DeclaredOnly</c>.
///
/// The key is a real keybinding in its own layer, registered the way First Person registers its
/// keys: appended to <c>Keybindings._Layers</c> and indexed in <c>_KeybindingsById</c>, which is the
/// dictionary <c>InputDownstreamContext</c> resolves ids through. That puts a rebindable row in the
/// settings screen. Z is free on its own - the game binds it only with Control, for undo.
/// </summary>
internal sealed class ViewSelectionAction : IDisposable
{
    private const string LayerId = "extra-build-layers";
    private const string KeyId = LayerId + ".view-selection";
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private delegate IEnumerable<PlacementKeybindingHintData> IslandActions(HUDIslandSelectionDetails self);

    private delegate IEnumerable<PlacementKeybindingHintData> IslandActionsHook(IslandActions orig, HUDIslandSelectionDetails self);

    private delegate IEnumerable<PlacementKeybindingHintData> BuildingActions(HUDBuildingSelectionDetails self);

    private delegate IEnumerable<PlacementKeybindingHintData> BuildingActionsHook(BuildingActions orig, HUDBuildingSelectionDetails self);

    private readonly ILogger Logger;
    private readonly string IconPath;
    private Sprite Icon;
    private readonly Hook Islands;
    private readonly Hook Buildings;
    private readonly Hook Input;

    private delegate void UpdateMethod(
        InputDownstreamContext self, IEnumerable<(Keybinding, KeySet)> last, IEnumerable<(Keybinding, KeySet)> active,
        float2 position, float2 delta, float wheel, GameObject hover);

    private delegate void UpdateHook(
        UpdateMethod orig, InputDownstreamContext self, IEnumerable<(Keybinding, KeySet)> last,
        IEnumerable<(Keybinding, KeySet)> active, float2 position, float2 delta, float wheel, GameObject hover);
    private KeybindingsLayer Registered;

    public ViewSelectionAction(ILogger logger, ModFolderLocator resources)
    {
        Logger = logger;
        IconPath = resources?.SubPath("Magnifier.png");

        // Contained: a throw out of a mod constructor is not caught by ModLoader.
        try
        {
            MethodInfo islands = typeof(HUDIslandSelectionDetails).GetMethod("GetActions", Declared);
            MethodInfo buildings = typeof(HUDBuildingSelectionDetails).GetMethod("GetActions", Declared);
            if (islands != null)
            {
                Islands = new Hook(islands, new IslandActionsHook((orig, self) =>
                    Append(orig(self), () => self.Player.InteractionState.IslandSelection.Count > 0, () =>
                    {
                        IslandModel first = self.Player.InteractionState.IslandSelection.FirstOrDefault();
                        ViewPlatform.Go(first);
                    })));
            }

            if (buildings != null)
            {
                Buildings = new Hook(buildings, new BuildingActionsHook((orig, self) =>
                    Append(orig(self), () => self.Player.InteractionState.BuildingSelection.Count > 0, () =>
                    {
                        ISelection<BuildingModel> selection = self.Player.InteractionState.BuildingSelection;
                        if (selection.Count > 0)
                        {
                            ViewPlatform.Go(selection.First());
                        }
                    })));
            }

            if (islands == null || buildings == null)
            {
                logger.Warning?.Log("Extra Build Layers: a selection action list was not found; the View action may be missing.");
            }

            MethodInfo update = typeof(InputDownstreamContext).GetMethod(nameof(InputDownstreamContext.Update), BindingFlags.Instance | BindingFlags.Public);
            if (update != null)
            {
                Input = new Hook(update, new UpdateHook(AfterInput));
            }
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
        }
    }

    private IEnumerable<PlacementKeybindingHintData> Append(
        IEnumerable<PlacementKeybindingHintData> actions, Func<bool> active, Action handler)
    {
        foreach (PlacementKeybindingHintData action in actions)
        {
            yield return action;
        }

        // Registered on first use rather than at load: Globals.Data does not exist yet while mods
        // are constructed, and the actions are only asked for once a session's HUD is up.
        if (!EnsureKeybinding())
        {
            yield break;
        }

        yield return new PlacementKeybindingHintData
        {
            OverrideTitle = "extra-build-layers.view-selection.title".T(),
            OverrideDescription = "extra-build-layers.view-selection.description".T(),
            Icon = LoadIcon(),
            KeybindingId = KeyId,
            Handler = handler,
            ActiveIf = active,

            // The button still shows the key; the key itself is read by AfterInput. The button's
            // own listener uses ConsumeWasActivated, and a plain-Z binding is pressed by Ctrl+Z
            // too - so undo also jumped the camera, and consuming the shared key could starve undo.
            DoNotListenForKeybinding = true,
        };
    }

    /// <summary>
    /// The View key, read without consuming it (<c>IsActivated</c>, so undo and anything else on
    /// the same key still get it), and ignored with Control or Command held - Ctrl+Z is undo - or
    /// while a text field has focus, where Z is just a letter.
    /// </summary>
    private void AfterInput(
        UpdateMethod orig, InputDownstreamContext context, IEnumerable<(Keybinding, KeySet)> last,
        IEnumerable<(Keybinding, KeySet)> active, float2 position, float2 delta, float wheel, GameObject hover)
    {
        orig(context, last, active, position, delta, wheel, hover);

        try
        {
            if (Registered == null || !context.AllBindings.ContainsKey(KeyId) || !context.IsActivated(KeyId) ||
                UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl) ||
                UnityEngine.Input.GetKey(KeyCode.LeftCommand) || UnityEngine.Input.GetKey(KeyCode.RightCommand) ||
                TypingInTextField())
            {
                return;
            }

#pragma warning disable CS0618 // Input has nothing to inject into.
            Player player = StaticGameCoreAccessor.HasInstance() ? StaticGameCoreAccessor.G.LocalPlayer : null;
#pragma warning restore CS0618
            if (player == null)
            {
                return;
            }

            if (player.InteractionState.BaseState == PlayerInteractionBaseState.Buildings)
            {
                if (player.InteractionState.BuildingSelection.Count > 0)
                {
                    ViewPlatform.Go(player.InteractionState.BuildingSelection.First());
                }
            }
            else if (player.InteractionState.IslandSelection.Count > 0)
            {
                ViewPlatform.Go(player.InteractionState.IslandSelection.First());
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    private static bool TypingInTextField()
    {
        GameObject focused = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
        return focused != null &&
               (focused.GetComponent<TMPro.TMP_InputField>() != null || focused.GetComponent<UnityEngine.UI.InputField>() != null);
    }

    /// <summary>
    /// A magnifying glass, drawn for this mod - the game has none outside a debug view. White on
    /// transparent like the game's own action icons, which the button tints. Loaded on first use,
    /// on the main thread, and kept; falls back to the waypoint icon if the file is missing.
    /// </summary>
    private Sprite LoadIcon()
    {
        if (Icon == null && IconPath != null)
        {
            try
            {
                Icon = FileTextureLoader.LoadTextureAsSprite(IconPath, out _);
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
            }
        }

#pragma warning disable CS0618 // Globals.Resources is obsolete as a pattern; a HUD hint has nothing else to reach icons through.
        return Icon != null ? Icon : Globals.Resources.Icons.VisualizationWaypoints;
#pragma warning restore CS0618
    }

    private bool EnsureKeybinding()
    {
        if (Registered != null)
        {
            return true;
        }

        try
        {
#pragma warning disable CS0618
            Keybindings keybindings = Globals.Data?.Keybindings;
#pragma warning restore CS0618
            if (keybindings == null)
            {
                return false;
            }

            // Still there from a previous load of this assembly (a hot reload): ids are strings,
            // so the old layer answers just as well, and a second would duplicate the settings rows.
            if (keybindings.KeybindingsById.ContainsKey(KeyId))
            {
                return true;
            }

            KeybindingsLayer layer = new KeybindingsLayer(LayerId, new List<Keybinding>
            {
                new Keybinding("view-selection", new KeySet(KeyCode.Z)),
            });

            KeybindingsLayer[] layers = keybindings._Layers;
            Array.Resize(ref layers, layers.Length + 1);
            layers[layers.Length - 1] = layer;
            keybindings._Layers = layers;
            layer.Changed.Register(keybindings._Changed.Invoke);
            foreach (Keybinding binding in layer.Bindings)
            {
                keybindings._KeybindingsById[binding.Id] = binding;
            }

            Registered = layer;
            return true;
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            return false;
        }
    }

    public void Dispose()
    {
        Islands?.Dispose();
        Buildings?.Dispose();
        Input?.Dispose();

        // Taken back out so a disabled mod leaves no dead row in the settings screen.
        KeybindingsLayer layer = Registered;
        Registered = null;
        try
        {
#pragma warning disable CS0618
            Keybindings keybindings = Globals.Data?.Keybindings;
#pragma warning restore CS0618
            if (layer == null || keybindings == null)
            {
                return;
            }

            keybindings._Layers = keybindings._Layers.Where(l => !ReferenceEquals(l, layer)).ToArray();
            foreach (Keybinding binding in layer.Bindings)
            {
                keybindings._KeybindingsById.Remove(binding.Id);
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }
}
