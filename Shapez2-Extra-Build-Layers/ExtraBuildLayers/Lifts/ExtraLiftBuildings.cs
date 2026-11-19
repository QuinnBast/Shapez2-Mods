using System;
using System.Collections.Generic;
using Game.Core.Content.Buildings;
using Game.Core.Coordinates;
using Game.Core.Rendering.MeshGeneration;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Creates a belt lift for every height the current layer ceiling allows, by restretching
/// vanilla's two-layer one.
///
/// Nothing here is per-height. The spans come from <c>GameMode.MaxBuildingLayer</c> at runtime,
/// so raising the layer count raises the range of lifts with no code change, and the whole set
/// is one loop.
///
/// Restretching rather than authoring works because a lift is, to the placer, simply a belt
/// building whose output is some layers above its input. There is no lift-specific logic in the
/// building path placer at all - <c>BuildingPlacersCreator</c> never mentions one - so a
/// definition with the right connector geometry is a lift. Everything else, the draw data,
/// colliders, sounds and configuration factory, is carried across from the vanilla definition
/// by copying its custom data.
///
/// There is no tall mesh. The height is made of vanilla's own bodies stacked - two-layer ones,
/// capped with a one-layer one for an odd span - by <see cref="TallShaft"/> for a placed piece
/// and by <see cref="TallBlueprints"/> for the placement preview, which is a separate mesh the
/// game draws from a separate field. It reads as a segmented shaft rather than a seamless one;
/// seamless would mean authoring a tileable section, which is art rather than code.
/// </summary>
internal class ExtraLiftBuildings : IBuildingsRewirer
{
    /// <summary>Span in layers → the definition ids created for it, in source-variant order.</summary>
    internal static readonly Dictionary<int, List<BuildingDefinitionId>> CreatedBySpan =
        new Dictionary<int, List<BuildingDefinitionId>>();

    internal static readonly List<IBuildingDefinition> Created = new List<IBuildingDefinition>();

    /// <summary>
    /// The one-layer lift body for each tall lift, by definition id.
    ///
    /// Only this one is captured. A tall lift's *two*-layer body is its own
    /// <c>MainMeshPerLayer</c> entry, readable at draw time, but the one-layer body belongs to a
    /// different definition group entirely and there is no route to it from an entity - so it is
    /// picked up here, where the whole <c>GameBuildings</c> is in hand.
    ///
    /// Used to cap an odd-height shaft: a five-layer lift is two stacked two-layer bodies and one
    /// single. Null for a variant with no counterpart, in which case the shaft simply stops a
    /// layer short rather than overshooting.
    /// </summary>
    internal static readonly Dictionary<BuildingDefinitionId, ILODMesh> OneLayerBodies =
        new Dictionary<BuildingDefinitionId, ILODMesh>();

    /// <summary>
    /// How many layers each stretched definition spans, for both families.
    ///
    /// A belt lift's renderer can read this off its own simulation's lane count, but a pipe has
    /// no simulation of its own to ask - it shares the one fluid network with every other pipe -
    /// so the span has to be recorded when the definition is made.
    /// </summary>
    internal static readonly Dictionary<BuildingDefinitionId, int> SpanByDefinition =
        new Dictionary<BuildingDefinitionId, int>();

    private readonly ILogger Logger;

    public ExtraLiftBuildings(ILogger logger)
    {
        Logger = logger;
    }

    public GameBuildings ModifyGameBuildings(
        AuthoringBuildings metaBuildings, GameBuildings gameBuildings, IMeshCache meshCache, VisualThemeBaseResources theme)
    {
        // GameMode.From builds a fresh GameBuildings per session, so anything left from the last
        // one is stale ids that would be handed to a placer that cannot resolve them.
        LiftGeometryDump.Buildings = gameBuildings;
        CreatedBySpan.Clear();
        Created.Clear();
        OneLayerBodies.Clear();
        SpanByDefinition.Clear();
        TallBlueprints.StaticallyStacked.Clear();

        try
        {
            Build(gameBuildings, meshCache, theme);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            Logger.Error?.Log(
                "Extra Build Layers: could not create tall lifts; belts will only climb as far as " +
                "vanilla's two-layer lift allows.");
            CreatedBySpan.Clear();
            Created.Clear();
        }

        return gameBuildings;
    }

    private void Build(GameBuildings gameBuildings, IMeshCache meshCache, VisualThemeBaseResources theme)
    {
        // The tallest useful lift climbs from the ground floor to the top one. Vanilla covers 1
        // and 2, so anything at or below 2 is already served.
        int topLayer = Math.Min(
            BuildLayers.ExtraMachineLevels + 2, BuildLayers.MaxMachineLevel);
        if (topLayer <= 2)
        {
            return;
        }

        // Belts and pipes are the same problem with a different two-layer original, so they are
        // one loop over two families rather than two copies of this method.
        //
        // Only belts need a simulation registered for them. A pipe's comes free: FluidNetworkSystem
        // claims a building by asking
        // `building.Definition.ConnectorData.BuildingConnectorsOfType<BuildingFluidJunction>().Count > 0`
        // - by connector type, never by definition id - so a stretched pipe joins the fluid network
        // the moment it exists.
        BuildFamily(gameBuildings, meshCache, theme, gameBuildings.Lift2LayerDefinitionGroupId, "Lift", topLayer, needsSimulation: true);
        BuildFamily(gameBuildings, meshCache, theme, gameBuildings.Layer2PipeBuildingId, "Pipe", topLayer, needsSimulation: false);
    }

    private void BuildFamily(
        GameBuildings gameBuildings,
        IMeshCache meshCache,
        VisualThemeBaseResources theme,
        BuildingDefinitionGroupId groupId,
        string label,
        int topLayer,
        bool needsSimulation)
    {
        IBuildingDefinitionGroup source = gameBuildings.GetDefinitionGroup(groupId);
        if (source == null || source.Definitions.Count == 0)
        {
            Logger.Error?.Log(
                "Extra Build Layers: no two-layer " + label + " to restretch; skipping tall ones.");
            return;
        }

        // Snapshot before the loop. The definitions are added back into this very group below, so
        // reading `source.Definitions.Count` as the bound would grow the bound on every iteration
        // and never terminate.
        List<IBuildingDefinition> originals = new List<IBuildingDefinition>(source.Definitions);
        int createdHere = 0;

        // The matching one-layer group for whichever family this is, used to cap an odd shaft.
        IBuildingDefinitionGroup singles = gameBuildings.GetDefinitionGroup(
            needsSimulation ? gameBuildings.Lift1LayerDefinitionGroupId : gameBuildings.Layer1PipeBuildingId);

        for (int span = 3; span <= topLayer; span++)
        {
            List<BuildingDefinitionId> ids = new List<BuildingDefinitionId>();

            for (int variant = 0; variant < originals.Count; variant++)
            {
                IBuildingDefinition original = originals[variant];
                if (original is not BuildingDefinition concrete)
                {
                    continue;
                }

                BuildingDefinitionId id = new BuildingDefinitionId(
                    "ExtraBuildLayers." + label + span + "Layer_" + variant);

                BuildingConnectorData connectors = Stretch(concrete.ConnectorData, span);
                BuildingDefinition stretched = new BuildingDefinition(id, connectors);

                // Captured per definition rather than per variant so the renderer needs nothing
                // but the entity it is drawing. Indexed by the same variant position, which the
                // two groups share: the dump shows Lift1DownBackward… and Lift2DownBackward… in
                // matching order. Taken before the custom data is copied, because the stacked
                // blueprint mesh below caps an odd shaft with it too.
                ILODMesh single = null;
                if (singles != null && variant < singles.Definitions.Count &&
                    singles.Definitions[variant].CustomData.TryGet(out IBuildingDrawData singleDraw) &&
                    singleDraw.MainMeshPerLayer is { Length: > 0 })
                {
                    single = singleDraw.MainMeshPerLayer[0];
                    OneLayerBodies[id] = single;
                }

                // Everything that is not geometry comes across untouched: the draw data, the
                // colliders, the sound data, the configuration factory and the group. The group
                // reference matters - plenty of lookups do CustomData.Get<BuildingDefinitionGroup>()
                // and would throw without it - but the definition is deliberately NOT added to
                // that group's internal variants, because the player never picks a lift from the
                // toolbar. The path placer selects it by geometry.
                //
                // The connector data is the exception, and getting this wrong is silent. A
                // definition carries it TWICE: once as the `ConnectorData` constructor argument,
                // and once attached to CustomData by `BuildingDefinitionFactory`. The placer reads
                // the **CustomData** copy - `MatchingDefinitionFinder` opens with
                // `entityDefinition.CustomData.Get<IEntityConnectorData<…>>()` - so copying the
                // original's custom data verbatim gives a lift whose property says "n layers" and
                // whose custom data still says "two". It then matches only two-layer requests, and
                // loses even those to the real two-layer lift earlier in the list, so a tall lift
                // is registered, simulated, and never once chosen.
                //
                // Both cannot be attached: `CustomDataHolder.TryGet<T>` matches by assignable type
                // and throws `MultipleDataFitDataTypeQueryException` on two hits, which would break
                // the lookup for this definition entirely.
                foreach (object data in concrete.CustomData.All)
                {
                    if (data is IBuildingConnectorData)
                    {
                        continue;
                    }

                    // The draw data is replaced, not shared, for its blueprint meshes - see
                    // TallBlueprints. Everything else in it is still carried across by reference.
                    if (data is IBuildingDrawData drawData)
                    {
                        stretched.CustomData.Attach(TallBlueprints.WithStackedBlueprint(
                            id, drawData, single, span, connectors, meshCache, theme, Logger));
                        continue;
                    }

                    stretched.CustomData.Attach(data);
                }

                stretched.CustomData.Attach(connectors);

                gameBuildings._DefinitionsById.Add(id, stretched);

                // Registered into the stock lift's own group, not merely into the id lookup.
                //
                // Intercepting `DefinitionsFinderUtils.BeltPathDefinitionsFinder` and returning a
                // finder that also held these did not work: the hook fired hundreds of times a
                // session and not one request ever arrived at the returned finder, so whatever the
                // running placer holds is built somewhere else. Adding to the group instead means
                // every consumer that enumerates it - the path definition finder, the neighbour
                // upgrade and downgrade processors, replacement rules, the pipette map - sees them
                // without a single hook, and they cannot disagree with each other.
                //
                // Being an internal variant is not a toolbar entry: the stock lift already has
                // eight the player never cycles, because the path placer chooses among them by
                // geometry. These join that set.
                if (source is BuildingDefinitionGroup concreteGroup)
                {
                    concreteGroup.AddInternalVariant(stretched);
                }

                ids.Add(id);
                Created.Add(stretched);
                createdHere++;
                SpanByDefinition[id] = span;

            }

            // Only families that need one are recorded here; ExtraLiftSystems registers a
            // simulation per entry, and a pipe given a belt-lane simulation would be claimed twice.
            if (needsSimulation && ids.Count > 0)
            {
                CreatedBySpan[span] = ids;
            }
        }

        TallBlueprints.LogSummary(Logger, label);

        Logger.Info?.Log(
            "Extra Build Layers: created " + createdHere + " tall " + label +
            " definition(s) spanning 3.." + topLayer + " layers" +
            (needsSimulation ? "." : "; the fluid network claims them by connector type."));
    }

    /// <summary>
    /// Copies the connector data with the output connector moved to the top of the new span and
    /// the occupied tiles extended to match.
    ///
    /// The input stays where it is; on vanilla's lift it sits at the bottom and the output two
    /// layers up, so raising only the highest-z connector is what turns a two-layer lift into an
    /// n-layer one. Anything that is neither the lowest nor the highest connector is carried
    /// across unchanged - there are none on the stock lift, but a mod-supplied source could have
    /// them and dropping them silently would be worse than leaving them.
    /// </summary>
    private static BuildingConnectorData Stretch(IBuildingConnectorData source, int span)
    {
        short top = (short)span;

        // Move the connector by ROLE, not by height. An earlier version moved whichever connector
        // sat highest, which is right for an Up lift and exactly wrong for a Down one. Dumped from
        // the running game, the stock pair is:
        //
        //   Lift2UpForwardInternalVariant     in (0,0,0) West   out (0,0, 2) East
        //   Lift2DownForwardInternalVariant   in (0,0,0) West   out (0,0,-2) East
        //
        // The input sits at z 0 in both; only the output moves, and its sign is what makes the
        // lift go up or down. Picking "highest" on a Down variant moved the *input* to +span and
        // left the output at -2, producing four nonsense definitions per span.
        // Identified by height, not by type. A belt lift has a BuildingItemInput at z 0 and a
        // BuildingItemOutput at z +-2, but a pipe's two ends are both BuildingFluidJunction, so
        // there is no output to single out. What both share is that the end which moves is the one
        // that is not on the ground floor - the far end at z +-2 - and the sign of its z is what
        // makes the piece climb or descend.
        int farZ = 0;
        foreach (IBuildingIO io in source.AllBuildingConnectors)
        {
            if (io is BuildingBaseIO baseIO && baseIO.Position_L.z != 0)
            {
                farZ = baseIO.Position_L.z;
                break;
            }
        }

        // Preserve the direction of travel; a Down piece stretched downwards stays a Down piece.
        short target = (short)(farZ < 0 ? -top : top);

        List<IBuildingIO> connectors = new List<IBuildingIO>(source.AllBuildingConnectors.Length);
        foreach (IBuildingIO io in source.AllBuildingConnectors)
        {
            bool isFarEnd = io is BuildingBaseIO baseIO && baseIO.Position_L.z != 0;
            connectors.Add(isFarEnd ? MovedTo(io, target) : io);
        }

        short lowBound = target < 0 ? target : (short)0;
        short highBound = target < 0 ? (short)0 : target;

        // One tile per layer, taking the footprint from the source's own tiles so a lift that is
        // more than one tile across keeps its shape.
        HashSet<TileVector> footprint = new HashSet<TileVector>();
        foreach (TileVector tile in source.Tiles)
        {
            footprint.Add(new TileVector(tile.x, tile.y, 0));
        }

        List<TileVector> tiles = new List<TileVector>(footprint.Count * (span + 1));
        foreach (TileVector tile in footprint)
        {
            for (short z = lowBound; z <= highBound; z++)
            {
                tiles.Add(new TileVector(tile.x, tile.y, z));
            }
        }

        LocalTileBounds bounds = LocalTileBounds.From(
            new TileVector(source.TileBounds.Min.x, source.TileBounds.Min.y, lowBound),
            new TileVector(source.TileBounds.Max.x, source.TileBounds.Max.y, highBound));

        TileDimensions dimensions = new TileDimensions(
            source.TileDimensions.x, source.TileDimensions.y, span + 1);

        return new BuildingConnectorData(
            connectors, tiles, bounds, source.TileBoundsCenter, dimensions);
    }

    /// <summary>
    /// A fresh connector of the same kind at a new height. The IO types are plain
    /// <c>[Serializable]</c> classes with settable <c>Position_L</c> and <c>TileDirection</c>, so
    /// this is construction rather than reflection - but the original must not be mutated,
    /// because it belongs to the vanilla definition this one was copied from.
    /// </summary>
    private static IBuildingIO MovedTo(IBuildingIO io, short z)
    {
        if (io is not BuildingBaseIO original)
        {
            return io;
        }

        // MemberwiseClone rather than `new` on a known subtype. The IO classes carry state beyond
        // position and facing - BuildingFluidJunction has its own _IOType, and a list of concrete
        // types to construct would silently drop whatever a future one adds. Cloning keeps every
        // field and changes only the two that define where the connector is.
        if (MemberwiseCloneMethod?.Invoke(original, null) is not BuildingBaseIO moved)
        {
            return io;
        }

        moved.Position_L = new TileVector(original.Position_L.x, original.Position_L.y, z);
        moved.TileDirection = original.TileDirection;
        return moved;
    }

    /// <summary>
    /// <c>Object.MemberwiseClone</c> is protected, so it is reached by reflection rather than by
    /// inheritance. Resolved once: this runs per connector per variant per span.
    /// </summary>
    private static readonly System.Reflection.MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod(
            "MemberwiseClone",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
}
