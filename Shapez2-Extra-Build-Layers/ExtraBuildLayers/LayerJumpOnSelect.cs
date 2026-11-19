using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Core.Coordinates;
using MonoMod.RuntimeDetour;
using Unity.Mathematics;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Selecting one platform or building on another layer moves the view to that layer.
///
/// With hundreds of layers, stepping to the one something is on is the slow part of working on
/// it. Selection is <c>HUDMassSelectionBase</c>, and both concrete selections -
/// <c>HUDIslandMassSelection</c> and <c>HUDBuildingMassSelection</c> - report what was just added
/// through <c>OnEntriesAddedToSelection</c>. A postfix there sets the viewport's layer when exactly
/// one thing was added, which eases the camera to it the same way the layer keys do. An area
/// selection adds many and is left alone.
///
/// Hooked on the concrete classes' own overrides, found by reflection with
/// <c>DeclaredOnly</c>. An expression-based hook would bind to the declaring method on the
/// generic base, and MonoMod cannot hook a method on a generic type.
///
/// Lift stations are skipped: a click anywhere on a shaft selects its entry station
/// (<see cref="LiftShaftPicking"/>), and dropping the player hundreds of levels to the foot of
/// the climb they clicked the top of would be the opposite of helpful.
/// </summary>
internal sealed class LayerJumpOnSelect : IDisposable
{
    private delegate void IslandsAdded(HUDIslandMassSelection self, IEnumerable<IslandModel> entries);

    private delegate void IslandsAddedHook(IslandsAdded orig, HUDIslandMassSelection self, IEnumerable<IslandModel> entries);

    private delegate void BuildingsAdded(HUDBuildingMassSelection self, IEnumerable<BuildingModel> entries);

    private delegate void BuildingsAddedHook(BuildingsAdded orig, HUDBuildingMassSelection self, IEnumerable<BuildingModel> entries);

    /// <summary>
    /// The overview threshold. Selecting there does not jump: a click in the overview is as often
    /// to copy or move a platform as to go to it, and the side panel's View button
    /// (<see cref="ViewPlatformModules"/>) is the way to go there.
    /// </summary>
    private const float OverviewZoom = 1500f;

    private const string Method = "OnEntriesAddedToSelection";
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private readonly ILogger Logger;
    private readonly Hook Islands;
    private readonly Hook Buildings;
    private bool Complained;

    public LayerJumpOnSelect(ILogger logger)
    {
        Logger = logger;

        // Contained: a throw out of a mod constructor is not caught by ModLoader.
        try
        {
            MethodInfo islands = typeof(HUDIslandMassSelection).GetMethod(Method, Declared);
            MethodInfo buildings = typeof(HUDBuildingMassSelection).GetMethod(Method, Declared);
            if (islands != null)
            {
                Islands = new Hook(islands, new IslandsAddedHook(AfterIslands));
            }

            // Buildings are not jumped to: a click on one is as often to copy or move it as to go
            // to it, and the selection's View action (ViewSelectionAction) does the going.

            if (islands == null || buildings == null)
            {
                logger.Warning?.Log("Extra Build Layers: a selection callback was not found; selecting may not jump to its layer.");
            }
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
        }
    }

    private void AfterIslands(IslandsAdded orig, HUDIslandMassSelection self, IEnumerable<IslandModel> entries)
    {
        // Materialised first: the original enumerates it too, and it may be a one-shot query.
        List<IslandModel> added = entries?.ToList();
        orig(self, added ?? entries);

        try
        {
            if (added == null || added.Count != 1 || added[0] == IslandModel.Invalid ||
                LiftStationIds.TryParse(added[0].Definition.Id, out _, out _))
            {
                return;
            }

            short layer = added[0].Position.z;
            Viewport viewport = self.Viewport;
            if (viewport == null)
            {
                return;
            }

            // Not in the overview: a click there is as often to copy or move a platform as to go to
            // it, and changing layer moves the camera under the player. The panel's View button
            // (ViewPlatformModules) is how to go there.
            if (viewport.TargetZoom >= OverviewZoom)
            {
                return;
            }

            if (viewport.IslandLayer != layer)
            {
                viewport.IslandLayer = layer;
            }


        }
        catch (Exception exception)
        {
            Complain(exception);
        }
    }

    private void AfterBuildings(BuildingsAdded orig, HUDBuildingMassSelection self, IEnumerable<BuildingModel> entries)
    {
        List<BuildingModel> added = entries?.ToList();
        orig(self, added ?? entries);

        try
        {
            if (added == null || added.Count != 1 || added[0] == null)
            {
                return;
            }

            // A tile's z holds both: island layer * 20 + building layer.
            GlobalTileCoordinate tile = added[0].Tile_G;
            short buildingLayer = tile.BuildingLayer();
            short islandLayer = (short)Math.Floor(tile.z / 20f);

            Viewport viewport = self.Viewport;
            if (viewport == null)
            {
                return;
            }

            if (viewport.IslandLayer != islandLayer)
            {
                viewport.IslandLayer = islandLayer;
            }

            if (viewport.BuildingLayer != buildingLayer)
            {
                viewport.BuildingLayer = buildingLayer;
            }
        }
        catch (Exception exception)
        {
            Complain(exception);
        }
    }

    private void Complain(Exception exception)
    {
        if (!Complained)
        {
            Complained = true;
            Logger.Exception?.LogException(exception);
        }
    }

    public void Dispose()
    {
        Islands?.Dispose();
        Buildings?.Dispose();
    }
}
