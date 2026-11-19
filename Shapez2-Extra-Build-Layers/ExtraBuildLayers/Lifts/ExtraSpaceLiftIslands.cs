using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core.Content.Islands;
using Game.Core.Coordinates;
using Game.Core.Rendering.Islands;
using Game.Core.Rendering.Islands.SpacePaths;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Tall space-belt and space-pipe lifts, by restretching the vanilla ones.
///
/// A space lift is not a distinct kind of thing: <c>PathLiftingProcessor</c> raises a run of
/// path islands onto another island layer and then **upgrades the two ends** into islands whose
/// connectors sit on different layers, asking the definition finder for each:
///
/// <code>
/// TVector[] offsets = LiftVerticalOffsetProvider.GetLiftPossibilitiesFromCurrentPosition(pos, realMap).ToArray();
/// …
/// MatchingDefinitionFinder.TryMatchingEntityFromInputsAndOutputs(inEnd, outRaised, out var entity)
/// </code>
///
/// The offsets already cover the whole unlocked range - <c>LiftVerticalOffsetProvider</c>
/// enumerates <c>MinIslandLayer..MaxIslandLayer</c> ordered by distance - so what is missing at
/// three layers is only an island whose connectors span three. Same shape as the in-platform
/// case, one level up.
///
/// The candidate list is <c>GameIslands.SpaceBelts</c> / <c>.SpacePipes</c>, which
/// <c>SpaceBeltPathDefinitionFinder</c> and <c>BuiltinSimulationSystems.CreateSpacePathSystems</c>
/// both read, so extending those two arrays gives placement and simulation together.
///
/// **Islands are not buildings.** Adding one to a list is not enough:
///
/// <code>
/// // GameIslands
/// public bool TryGetDefinition(IslandDefinitionId id, out IIslandDefinition definition)
/// {
///     if (!CatalogPair.GameSessionCatalog.HasIsland(id)) { definition = null; return false; }
///     …
/// }
/// </code>
///
/// and <c>HasIsland</c> is <c>GroupMap.ContainsKey</c>. An island missing from the **catalog** is
/// invisible however it is registered. Shifter's own <c>IslandBuilder.BuildAndRegister</c> shows
/// the full set of places that have to agree, and this follows it: both catalogs, the id
/// dictionary, the flat definition list, and the group.
/// </summary>
internal class ExtraSpaceLiftIslands : IIslandsRewirer
{
    private readonly ILogger Logger;

    public ExtraSpaceLiftIslands(ILogger logger)
    {
        Logger = logger;
    }

    public GameIslands ModifyGameIslands(
        IslandDefinitionFactory factory, AuthoringIslands metaIslands, GameIslands gameIslands)
    {
        try
        {
            // The top altitude any unlock now grants. Read from the registration rather than from
            // GameMode.MaxIslandLayer, which is the unlock *count* and over-reports whenever a
            // scenario also hands out a layer below the start. The scenario rewirer runs before
            // this one, so it is populated.
            int topLayer = BuildLayers.Current?.TopSpaceLevel ?? 0;
            if (topLayer <= 2)
            {
                return gameIslands;
            }

            // The lift art comes from the authoring metadata, not from the island definitions -
            // a space path island carries no mesh drawer data of its own. This rewirer is handed
            // the metadata, which is the only place it is reachable from.
            Extend(gameIslands, "SpaceBelts", topLayer, metaIslands?.SpaceBeltIsland?.SpaceLiftDrawerData);
            Extend(gameIslands, "SpacePipes", topLayer, metaIslands?.SpacePipeIsland?.SpaceLiftDrawerData);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            Logger.Error?.Log(
                "Extra Build Layers: could not create tall space lifts; space belts will only climb " +
                "as far as vanilla's lift islands allow.");
        }

        return gameIslands;
    }

    private void Extend(GameIslands gameIslands, string fieldName, int topLayer, SpaceLiftDrawerMetaData liftDrawData)
    {
        // Both lists are `readonly IReadOnlyList<IIslandDefinition>` assigned from `ToArray()`, so
        // unlike GameBuildings there is no List to append to and the field itself has to be
        // replaced. Reflection rather than a cast for exactly that reason.
        FieldInfo field = typeof(GameIslands).GetField(
            fieldName, BindingFlags.Public | BindingFlags.Instance);

        if (field?.GetValue(gameIslands) is not IReadOnlyList<IIslandDefinition> originals || originals.Count == 0)
        {
            Logger.Error?.Log("Extra Build Layers: no " + fieldName + " to restretch.");
            return;
        }

        List<IIslandDefinition> all = new List<IIslandDefinition>(originals);
        int created = 0;

        for (int span = 3; span <= topLayer; span++)
        {
            for (int i = 0; i < originals.Count; i++)
            {
                if (originals[i] is not IslandDefinition original)
                {
                    continue;
                }

                if (!original.CustomData.TryGet(out IIslandConnectorData connectors) || !IsVertical(connectors, out int farZ))
                {
                    continue;   // a flat belt segment, not a lift
                }

                IslandDefinition stretched = Stretch(gameIslands, original, connectors, span, farZ, fieldName, i, liftDrawData);
                if (stretched != null)
                {
                    all.Add(stretched);
                    created++;
                }
            }
        }

        if (created == 0)
        {
            Logger.Info?.Log(
                "Extra Build Layers: no vertical " + fieldName + " island to restretch; " +
                "space lifts unchanged.");
            return;
        }

        field.SetValue(gameIslands, all.ToArray());

        Logger.Info?.Log(
            "Extra Build Layers: created " + created + " tall " + fieldName +
            " lift island(s) spanning 3.." + topLayer + " layers.");
    }

    /// <summary>
    /// A lift island is one whose connectors do not all sit on the same layer. Vanilla's span two,
    /// so the far end is at z +-2 and the sign says whether it climbs or descends - the same rule
    /// the in-platform version uses, because the same thing is true of both.
    /// </summary>
    private static bool IsVertical(IIslandConnectorData connectors, out int farZ)
    {
        farZ = 0;
        foreach (EntityIO<LocalChunkPivot, IEntityConnector> io in
                 ((IEntityConnectorData<LocalChunkPivot, ChunkVector, ChunkDirection>)connectors).AllConnectors)
        {
            if (io.Location.Position.z != 0)
            {
                farZ = io.Location.Position.z;
                return true;
            }
        }

        return false;
    }

    private IslandDefinition Stretch(
        GameIslands gameIslands, IslandDefinition original, IIslandConnectorData connectors,
        int span, int farZ, string fieldName, int variant, SpaceLiftDrawerMetaData liftDrawData)
    {
        IslandDefinitionId id = new IslandDefinitionId(
            "ExtraBuildLayers." + fieldName + "Lift" + span + "_" + variant);

        if (gameIslands.DefinitionsById.ContainsKey(id))
        {
            return null;
        }

        short target = (short)(farZ < 0 ? -span : span);

        List<EntityIO<LocalChunkPivot, IIslandConnector>> moved =
            new List<EntityIO<LocalChunkPivot, IIslandConnector>>();

        foreach (EntityIO<LocalChunkPivot, IIslandConnector> io in ConnectorsOf(connectors))
        {
            if (io.Location.Position.z == 0)
            {
                moved.Add(io);
                continue;
            }

            LocalChunkPivot raised = new LocalChunkPivot(
                new ChunkVector(io.Location.Position.x, io.Location.Position.y, target),
                io.Location.Direction);

            // The connector object itself is shared rather than cloned: it carries no position,
            // only the kind of thing that may connect, and IslandConnectorData rejects two
            // connectors of the same type at one pivot rather than silently merging them.
            moved.Add(new EntityIO<LocalChunkPivot, IIslandConnector>(raised, io.Connector));
        }

        List<ChunkVector> tiles = new List<ChunkVector>();
        short low = target < 0 ? target : (short)0;
        short high = target < 0 ? (short)0 : target;
        HashSet<ChunkVector> footprint = new HashSet<ChunkVector>();
        foreach (ChunkVector tile in connectors.Tiles)
        {
            footprint.Add(new ChunkVector(tile.x, tile.y, 0));
        }

        foreach (ChunkVector tile in footprint)
        {
            for (short z = low; z <= high; z++)
            {
                tiles.Add(new ChunkVector(tile.x, tile.y, z));
            }
        }

        IslandDefinition stretched = new IslandDefinition(id, original.Layout);

        // Same rule as the building case: carry everything across except the connector data, which
        // is replaced. Attaching both would make CustomDataHolder.TryGet throw on two assignable
        // matches, and keeping the original's would describe a two-layer lift.
        foreach (object data in original.CustomData.All)
        {
            // The mesh drawer data goes the same way as the connector data, and for the same
            // reason: the original's describes a two-layer shaft, and a stretched island that kept
            // it would function across twelve layers while drawing across two.
            if (data is IIslandConnectorData || data is ModularIslandMeshDrawer.Data)
            {
                continue;
            }

            stretched.CustomData.Attach(data);
        }

        stretched.CustomData.Attach(new IslandConnectorData(moved, tiles));
        AttachLiftMeshes(stretched, moved, liftDrawData);

        Register(gameIslands, stretched, original);
        return stretched;
    }

    /// <summary>
    /// Regenerates the lift's mesh modules for the new height, using the game's own algorithm.
    ///
    /// The ends are told apart by connector type rather than by height, because which end is the
    /// input decides the direction the entrance and exit art faces - a Down lift's entrance is at
    /// the top. Vanilla's generator derives everything else, including how many shaft sections to
    /// tile, from the two pivots.
    /// </summary>
    private void AttachLiftMeshes(
        IslandDefinition stretched,
        List<EntityIO<LocalChunkPivot, IIslandConnector>> moved,
        SpaceLiftDrawerMetaData liftDrawData)
    {
        if (liftDrawData == null)
        {
            return;
        }

        LocalChunkPivot? input = null;
        LocalChunkPivot? output = null;

        foreach (EntityIO<LocalChunkPivot, IIslandConnector> io in moved)
        {
            if (io.Connector is ISpacePathInputConnector)
            {
                input ??= io.Location;
            }
            else if (io.Connector is ISpacePathOutputConnector)
            {
                output ??= io.Location;
            }
        }

        if (!input.HasValue || !output.HasValue)
        {
            return;
        }

        List<ModularIslandMeshDrawer.Module> modules =
            SpaceLiftMeshModules.Create(input.Value, output.Value, liftDrawData);

        if (modules.Count > 0)
        {
            stretched.CustomData.Attach(new ModularIslandMeshDrawer.Data(modules));
        }
    }

    private static IEnumerable<EntityIO<LocalChunkPivot, IIslandConnector>> ConnectorsOf(IIslandConnectorData connectors)
    {
        // ConnectorsOfType<IIslandConnector> rather than the AllConnectors view, because the view
        // is typed to IEntityConnector and the constructor needs IIslandConnector back.
        return connectors.ConnectorsOfType<IIslandConnector>();
    }

    /// <summary>
    /// Every place that has to know the island exists. Modelled on Shifter's
    /// <c>IslandBuilder.BuildAndRegister</c>, which is the only complete list of them - miss the
    /// catalogs and <c>TryGetDefinition</c> refuses the id even though the definition is present.
    /// </summary>
    private void Register(GameIslands gameIslands, IslandDefinition stretched, IslandDefinition original)
    {
        gameIslands.DefinitionsById.Add(stretched.Id, stretched);

        if (gameIslands.AllDefinitions is List<IIslandDefinition> allDefinitions)
        {
            allDefinitions.Add(stretched);
        }

        IslandDefinitionGroupId groupId = original.DefinitionGroup().Id;

        if (gameIslands.CatalogPair.GameCatalog is IslandsCatalog gameCatalog)
        {
            gameCatalog.GroupMap.Add(stretched.Id, groupId);
            gameCatalog.GroupedMap.AddValue(groupId, stretched.Id);
        }

        if (gameIslands.CatalogPair.GameSessionCatalog is IslandsCatalog sessionCatalog)
        {
            sessionCatalog.GroupMap.Add(stretched.Id, groupId);
            sessionCatalog.GroupedMap.AddValue(groupId, stretched.Id);
        }

        if (original.DefinitionGroup() is IslandDefinitionGroup group
            && group.CustomData.TryGet(out IslandGroupCollection collection))
        {
            collection.AddIslandDefinition(stretched);
        }
    }
}
