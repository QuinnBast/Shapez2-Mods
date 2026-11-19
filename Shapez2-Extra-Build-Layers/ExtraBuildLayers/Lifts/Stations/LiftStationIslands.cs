using System;
using System.Collections.Generic;
using Core.Collections;
using Core.Factory;
using Core.Localization;
using Game.Content.Features.SpacePaths.IslandIO;
using Game.Core.Content.Islands;
using Game.Core.Coordinates;
using ShapezShifter.Flow;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Flow.Research;
using ShapezShifter.Flow.Toolbar;
using ShapezShifter.Kit;
using ShapezShifter.Textures;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Registers the four lift stations - an entry and an exit for space belts and for space pipes.
///
/// A station is one fixed chunk, like a straight space path segment, with a single declared
/// connector: the entry takes items in on its West side, the exit sends them out on its East side,
/// matching vanilla's straight segments. The connection between the two is not declared - it
/// depends on the height of each climb, which no definition can hold - and is added per instance
/// by <see cref="LiftStationConnectors"/> from the <see cref="LiftStationConfiguration"/> each
/// placed station carries. That is the whole reason this replaces per-height lift definitions:
/// four definitions cover every height.
///
/// Registered the way Crossover Platforms registers its crossings, prediction included, and for
/// the same reason outside the atomic chain: a player with predictions switched off never fires
/// the prediction branch, so a chain that waits on it never re-arms.
///
/// ShapezShifter's chain always makes a placer and asks for a toolbar entry. Stations are only
/// ever placed by dragging a path up or down - one placed on its own would have no climb and do
/// nothing - so the entry is handed a location that inserts nothing (<see cref="NowhereInToolbar"/>).
/// </summary>
internal sealed class LiftStationIslands : IDisposable
{
    /// <summary>
    /// The definition objects the builders made, kept so art can be attached to them later from a
    /// rewirer without looking them up - a lookup would depend on rewirer order, which the chain's
    /// re-arming changes after the first scenario.
    /// </summary>
    public static readonly Dictionary<(LiftPath, LiftEnd), IslandDefinition> Definitions =
        new Dictionary<(LiftPath, LiftEnd), IslandDefinition>();

    private readonly List<IDisposable> ReArmed = new List<IDisposable>();
    private readonly ILogger Logger;

    public LiftStationIslands(ILogger logger, ModFolderLocator resources)
    {
        Logger = logger;
        QuinnBast.Shapez2.ToolbarKit.ToolbarKit.Log = logger;

        foreach (LiftPath path in LiftStationIds.Paths)
        {
            foreach (LiftEnd end in LiftStationIds.Ends)
            {
                try
                {
                    Add(path, end, resources);
                }
                catch (Exception exception)
                {
                    // One station failing must not take the others, or the mod, with it.
                    logger.Exception?.LogException(exception);
                    logger.Error?.Log("Extra Build Layers: could not register the " + path + " " + end + " lift station.");
                }
            }
        }
    }

    private void Add(LiftPath path, LiftEnd end, ModFolderLocator resources)
    {
        IslandDefinitionId id = LiftStationIds.Definition(path, end);
        string slug = path.ToString().ToLowerInvariant() + "-" + end.ToString().ToLowerInvariant();

        IIslandGroupBuilder group = IslandGroup.Create(LiftStationIds.Group(path, end))
            .WithTitle(("extra-build-layers.lift-station." + slug + ".title").T())
            .WithDescription(("extra-build-layers.lift-station." + slug + ".description").T())
            .WithIcon(FileTextureLoader.LoadTextureAsSprite(resources.SubPath("LiftStation.png"), out _))
            .AsNonTransportableIsland()
            .WithPreferredPlacement(DefaultPreferredPlacementMode.Single);

        ChunkLayoutLookup<ChunkVector, IslandChunkData> layout = Layout();

        IIslandBuilder island = Island.Create(id)
            .WithLayout(layout)
            .WithPerChunkColliders()
            .WithConnectorData(Connectors(path, end, layout))
            .WithInteraction(flippable: false, canHoldBuildings: false)
            .WithDefaultChunkCost()
            .WithRenderingOptions(new HomogeneousChunkDrawing(default), drawPlayingField: false);

        // WithLayout attached a configuration factory producing null. A station needs one that
        // produces a LiftStationConfiguration, so a save reads the climb back into the right type
        // and a station placed by hand gets a rise of 0 rather than no configuration at all.
        IslandDefinition definition = ((IslandBuilder)island).IslandDefinition;
        definition.CustomData.AttachOrReplace(
            new LambdaFactory<IIslandConfiguration>(() => new LiftStationConfiguration()));
        Definitions[(path, end)] = definition;

        IAtomicIslandExtender simulated = end == LiftEnd.Entry
            ? AtomicIslands.Extend().AllScenarios().WithIsland(island, group)
                .UnlockedAtMilestone(new ByIndexMilestoneSelector(0))
                .WithSimulation(new LiftEntrySimulationFactory(path))
            : AtomicIslands.Extend().AllScenarios().WithIsland(island, group)
                .UnlockedAtMilestone(new ByIndexMilestoneSelector(0))
                .WithSimulation(new LiftExitSimulationFactory(path));

        // The casts are Crossover Platforms' and for the same reason: the fluent interfaces fork,
        // and nothing in the chain returns the one WithDefaultPlacement lives on.
        IDefinedAccessibleSimulatablePlaceableIslandExtender placed =
            ((IDefinedSimulatableIslandExtender)simulated)
            .WithDefaultPlacement()
            .InToolbar(new NowhereInToolbar());

        ((IAtomicIslandExtender)placed).WithCustomModules(new LiftStationPanel(end)).Build();

        ReArmed.Add(new ReArmingRewirer(() =>
            new IslandPredictionExtender<LiftStationPredictionSimulation>(id, new LiftStationPredictionFactory(), Logger)));
    }

    /// <summary>One chunk, every tile void: a station is track, not a platform to build on.</summary>
    private static ChunkLayoutLookup<ChunkVector, IslandChunkData> Layout()
    {
        ChunkVector origin = new ChunkVector(0, 0, 0);
        IslandChunkData chunk = IslandLayoutFactory.CreateIslandChunkData(
            chunkTile: origin,
            notchDirections: Array.Empty<ChunkDirection>(),
            neighborChunks: origin.AsEnumerable(),
            isBuildable: true,
            flipped: false,
            out _);

        for (int i = 0; i < chunk.TileVoidFlags_L.Length; i++)
        {
            chunk.TileVoidFlags_L[i] = true;
        }

        return new ChunkLayoutLookup<ChunkVector, IslandChunkData>(
            new[] { new KeyValuePair<ChunkVector, IslandChunkData>(origin, chunk) });
    }

    private static IIslandConnectorData Connectors(LiftPath path, LiftEnd end, ChunkLayoutLookup<ChunkVector, IslandChunkData> layout)
    {
        bool pipe = path == LiftPath.Pipe;
        IIslandConnector connector = end == LiftEnd.Entry
            ? pipe ? new SpacePipeInputConnector() : new SpaceBeltInputConnector()
            : pipe ? new SpacePipeOutputConnector() : new SpaceBeltOutputConnector();
        ChunkDirection side = end == LiftEnd.Entry ? ChunkDirection.West : ChunkDirection.East;

        return new IslandConnectorData(
            new[] { new EntityIO<LocalChunkPivot, IIslandConnector>(new LocalChunkPivot(ChunkVector.Zero, side), connector) },
            layout.ChunkPositions);
    }

    public void Dispose()
    {
        foreach (IDisposable registration in ReArmed)
        {
            registration.Dispose();
        }

        ReArmed.Clear();
        Definitions.Clear();
    }
}

/// <summary>
/// A toolbar location that adds nothing. <c>ToolbarRewirer.ModifyToolbarData</c> does all of its
/// inserting through <c>IToolbarEntryInsertLocation.AddEntry</c> and then fires its completion
/// event regardless, so the chain still completes and re-arms - there is simply no entry.
/// </summary>
internal sealed class NowhereInToolbar : IToolbarEntryInsertLocation
{
    public void AddEntry(ToolbarData toolbarData, IToolbarElementData elementData)
    {
    }
}
