using System;
using Game.Interaction.EntitiesPlacement;
using Game.Placement.Data;
using Game.Placement.Processing;
using ShapezShifter;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Puts <see cref="LiftStationPathProcessor"/> into the space belt and space pipe drag tools.
///
/// Both are built by <c>PlatformIslandsPlacersCreators</c> and never exposed; the way in is the
/// postfix ShapezShifter puts on <c>RegisterPlacers</c>, after which they can be resolved from the
/// registry by serial name and their processor list is still mutable. Crossover Platforms does
/// the same.
///
/// The processor goes in just before the path upgrade step - the slot straight after
/// <c>PathAnchorsAndFillingProcessor</c> - so the stations exist before anything that upgrades,
/// merges or replaces path nodes looks at the placement.
/// </summary>
internal sealed class LiftStationPlacementRewirer : IPlatformIslandPlacementRewirers
{
    private const string SpaceBeltPlacer = "SpaceBeltPlacementInitiator";
    private const string SpacePipePlacer = "SpacePipePlacementInitiator";

    private readonly ILogger Logger;

    public LiftStationPlacementRewirer(ILogger logger)
    {
        Logger = logger;
    }

    public void ModifyIslandPlacers(IslandInitiatorsParams islandInitiatorsParams, IPlacementInitiatorIdRegistry registry)
    {
        try
        {
            Insert(registry, SpaceBeltPlacer, islandInitiatorsParams.Islands, LiftPath.Belt);
            Insert(registry, SpacePipePlacer, islandInitiatorsParams.Islands, LiftPath.Pipe);
        }
        catch (Exception exception)
        {
            // Half-modified placers would throw on every path placement.
            Logger.Exception?.LogException(exception);
        }
    }

    private void Insert(IPlacementInitiatorIdRegistry registry, string serialName, GameIslands islands, LiftPath path)
    {
        if (!islands.TryGetDefinition(LiftStationIds.Definition(path, LiftEnd.Entry), out IIslandDefinition entry) ||
            !islands.TryGetDefinition(LiftStationIds.Definition(path, LiftEnd.Exit), out IIslandDefinition exit))
        {
            Logger.Error?.Log("Extra Build Layers: " + path + " lift stations are not registered in this scenario; tall climbs stay unbuildable.");
            return;
        }

        if (!registry.TryResolve(new SerializedPlacerId(serialName), out _, out IPlacementInitiator initiator) ||
            initiator is not GamePlacementInitiator game ||
            game.Placer is not ModularEntityPlacer<OverlappingPlacementData> placer)
        {
            Logger.Warning?.Log("Extra Build Layers: no modular placer registered as " + serialName + "; tall " + path + " climbs stay unbuildable.");
            return;
        }

        int upgradeIndex = placer.ProcessorIndex<IPathUpgradeProcessor>();
        if (upgradeIndex < 0)
        {
            Logger.Warning?.Log("Extra Build Layers: " + serialName + " has no path upgrade step; tall " + path + " climbs stay unbuildable.");
            return;
        }

        placer.InsertProcessor(new LiftStationPathProcessor(entry, exit), upgradeIndex);
        Logger.Info?.Log("Extra Build Layers: lift stations wired into " + serialName + ".");
    }
}
