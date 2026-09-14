using System;
using System.Collections.Generic;
using System.Text;
using Game.Content.Features.Signals.Channels;
using Game.Core;
using Game.Core.Blueprint;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Trains;
using Game.Core.Map.Simulation.Clustering;
using Game.Core.Simulation;
using Game.Orchestration;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// A private world holding a copy of a selection, simulated on its own.
///
/// The game builds a second simulator itself - the prediction graph is one, over the same
/// layout with a different set of systems - so the pieces are all public: a layout, a
/// graph, a set of systems, and <c>SynchronousUpdate</c> to drive it by whatever delta is
/// wanted. This puts a copy of the selection in a layout nobody else can see and runs it
/// there.
///
/// The point is measurement, not runtime. Predictions give the shapes a blackbox would emit
/// but say nothing about how fast; a sandbox can be saturated, timed and filled without
/// disturbing the factory the player is looking at. What comes out is a recipe, and the
/// sandbox is then thrown away.
///
/// Nothing here touches the live map. The copies share coordinates with the originals, which
/// is harmless because a layout is a private dictionary keyed by position.
/// </summary>
public class BlackboxSandbox : IDisposable
{
    /// <summary>
    /// Everything in a sandbox runs every tick. There is no camera for it to be far from and
    /// no frame budget to spread the work across, so the level-of-detail rationing the live
    /// map does would only make measurements harder to interpret.
    /// </summary>
    private class UpdateEverything : ISimulationUpdateStrategy
    {
        public UpdateNeed EvaluateUpdateNeed(SimulationCluster cluster, SimulationLOD lod,
            Ticks simulationEndTime)
        {
            return simulationEndTime > cluster.ClusterTime ? UpdateNeed.Mandatory : UpdateNeed.Not;
        }
    }

    private static readonly IReadOnlyList<SimulationLOD> NoLods = new List<SimulationLOD>();

    private readonly ILogger Logger;
    private readonly UpdateEverything Strategy = new UpdateEverything();
    private readonly SimulationUpdateConfiguration Config;

    private MapLayout Layout;
    private MapLayoutModel Model;
    private SimulationGraph Graph;
    private Simulator Simulator;

    private SimulationGraph PredictionGraph;
    private Simulator Predictions;

    public int IslandCount { get; private set; }
    public int BuildingCount { get; private set; }

    /// Simulation time consumed so far, so a measurement can be divided by it.
    public Ticks Elapsed { get; private set; }

    public ISimulator Simulations
    {
        get { return Simulator; }
    }

    /// <summary>
    /// The sandbox's own prediction graph, over the same layout.
    ///
    /// The game runs one of these over the live map to work out which shapes appear where,
    /// and a sandbox needs the same answer for a different reason: to know what to feed its
    /// inputs. Building it here rather than borrowing the live one means the tiles line up by
    /// construction, and that a blueprint with no placed counterpart anywhere on the map can
    /// still be measured.
    /// </summary>
    public ISimulator PredictionSimulations
    {
        get { return Predictions; }
    }

    private BlackboxSandbox(ILogger logger)
    {
        Logger = logger;

        // One thread and a hard update: the sandbox is small, and a measurement that depends
        // on how much time the scheduler felt like giving it is not a measurement.
        Config = new SimulationUpdateConfiguration(NoLods, TimeSpan.Zero, 1);
    }

    /// <summary>
    /// Copies the selected platforms into a world of their own and stands a simulator over
    /// it. Returns false with a reason rather than throwing, because most of what can go
    /// wrong here is a session that is not ready rather than a bug.
    /// </summary>
    public static bool TryBuild(GameSessionOrchestrator orchestrator,
        IEnumerable<IslandModel> selection, ILogger logger,
        out BlackboxSandbox sandbox, out string failure)
    {
        return TryBuild(orchestrator, logger, built => built.Populate(selection), out sandbox, out failure);
    }

    /// <summary>
    /// The same, from a blueprint rather than from platforms on the map. This is the path a
    /// blackbox actually needs: a recipe has to be derivable from a blueprint alone, because
    /// a blueprint shared as a string carries buildings and no recipe.
    /// </summary>
    public static bool TryBuild(GameSessionOrchestrator orchestrator,
        IslandBlueprint blueprint, ILogger logger,
        out BlackboxSandbox sandbox, out string failure)
    {
        return TryBuild(orchestrator, logger, built => built.Populate(blueprint, logger), out sandbox, out failure);
    }

    private static bool TryBuild(GameSessionOrchestrator orchestrator, ILogger logger,
        Action<BlackboxSandbox> populate,
        out BlackboxSandbox sandbox, out string failure)
    {
        sandbox = null;
        failure = null;

        if (orchestrator == null)
        {
            failure = "No session is running.";
            return false;
        }

        BlackboxSandbox built = new BlackboxSandbox(logger);

        try
        {
            built.Layout = new MapLayout(logger);

            // The layout stores; the model is what creates into it. The simulator wants the
            // layout itself, so both are kept.
            built.Model = new MapLayoutModel(built.Layout);
            populate(built);

            if (built.IslandCount == 0)
            {
                failure = "Nothing was copied - there were no platforms to copy.";
                built.Dispose();
                return false;
            }

            ISimulationSystem[] systems = BuildSystems(orchestrator, logger);

            built.Graph = new SimulationGraph(Ticks.Zero, updateClustersBeforeModification: true, logger);
            built.Simulator = new Simulator(built.Graph, systems, built.Layout, logger);

            built.BuildPredictions(orchestrator, logger);
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
            failure = "Could not stand up a sandbox: " + exception.Message;
            built.Dispose();
            return false;
        }

        sandbox = built;
        return true;
    }

    /// <summary>
    /// The same systems the live map runs, built fresh. They cannot be shared with the real
    /// simulator - a system holds per-simulator state about the buildings offered to it.
    /// </summary>
    private static ISimulationSystem[] BuildSystems(GameSessionOrchestrator orchestrator, ILogger logger)
    {
        BuiltinSimulationSystems builder = new BuiltinSimulationSystems(
            Ticks.Zero,
            orchestrator.ShapeOperations,
            orchestrator.Mode,
            orchestrator.ShapeRegistry,
            orchestrator.ShapeIdManager,
            orchestrator.ResourcesMap,
            orchestrator.FluidRegistry,
            orchestrator.FluidPackageItemSolver,
            orchestrator.DependencyContainer.Resolve<ISignalChannelRegistry>(),
            orchestrator.Research.UnlockManager,
            logger);

        List<ISimulationSystem> systems = new List<ISimulationSystem>();
        foreach (ISimulationSystem system in builder.CreateSimulationSystems())
        {
            systems.Add(system);
        }

        return systems.ToArray();
    }

    /// <summary>
    /// Recreates each platform and each building at the coordinates they already have.
    /// Creating rather than sharing is the point: a copy gets its own state container, so
    /// running the sandbox cannot move an item in the player's factory.
    /// </summary>
    private void Populate(IEnumerable<IslandModel> selection)
    {
        foreach (IslandModel island in selection)
        {
            Model.CreateIsland(island.Definition, island.Transform, island.Configuration);
            IslandCount++;

            foreach (BuildingModel building in island.Buildings)
            {
                Model.CreateBuilding(building.Definition, building.Transform, building.Configuration);
                BuildingCount++;
            }
        }
    }

    /// <summary>
    /// A prediction graph over the same layout, built the way the session builds its own.
    /// Failing to get one is not fatal - it costs the sandbox the ability to work out what to
    /// feed itself, which the caller can report rather than crash over.
    /// </summary>
    private void BuildPredictions(GameSessionOrchestrator orchestrator, ILogger logger)
    {
        try
        {
            TrainSystem trains = Simulator.GetSystem<TrainSystem>();

            List<ISimulationSystem> systems = new List<ISimulationSystem>();
            foreach (ISimulationSystem system in new BuiltinPredictionSimulationSystems(
                orchestrator.Mode,
                orchestrator.ResourcesMap,
                orchestrator.ShapeRegistry,
                orchestrator.ShapeIdManager,
                orchestrator.Research.UnlockManager,
                orchestrator.TrainHashCalculator,
                logger).CreateSimulationSystems(
                    orchestrator.ShapeOperations,
                    orchestrator.TrainPredictionRegistry,
                    orchestrator.RailColorRegistry,
                    trains.ShapeWagonType,
                    trains.FluidWagonType))
            {
                systems.Add(system);
            }

            PredictionGraph = new SimulationGraph(Ticks.Zero, updateClustersBeforeModification: false, logger);
            Predictions = new Simulator(PredictionGraph, systems.ToArray(), Layout, logger);
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
            PredictionGraph = null;
            Predictions = null;
        }
    }

    /// <summary>
    /// Expands a blueprint into the sandbox, the same way placing it would.
    ///
    /// This is what makes a blueprint measurable without placing it. The entries carry
    /// definitions, local positions, rotations and configuration blobs; turning those into
    /// instances is the job the placement processor does, minus the parts a private world has
    /// no use for - cost, allowability, mirroring, and any rotation of the whole blueprint.
    /// Placed unrotated at the origin, so the offsets the game applies for a rotated
    /// placement do not arise.
    /// </summary>
    private void Populate(IslandBlueprint blueprint, ILogger logger)
    {
        GlobalChunkTransform origin = new GlobalChunkTransform(
            new GlobalChunkCoordinate(0, 0, 0), GridRotation.NoRotate);

        foreach (IslandBlueprint.Entry entry in blueprint.Entries)
        {
            GlobalChunkTransform transform =
                new LocalChunkTransform(entry.Chunk_L, entry.Rotation).ToGlobal(origin);

            IIslandConfiguration islandConfig;
            BuildingBlueprintProcessor.TryGetConfig(GameVersionEnvironment.CurrentVersion,
                entry.Definition, entry.Configuration, out islandConfig, logger);

            Model.CreateIsland(entry.Definition, transform, islandConfig);
            IslandCount++;

            if (entry.BuildingBlueprint == null)
            {
                continue;
            }

            GlobalTileTransform tileOrigin =
                new GlobalTileTransform(transform.Position.ToOrigin_G(), origin.Rotation);

            foreach (BuildingBlueprint.Entry building in entry.BuildingBlueprint.Entries)
            {
                GlobalTileTransform placed =
                    new LocalTileTransform(building.Tile_L, building.Rotation).ToGlobal(in tileOrigin);

                IBuildingConfiguration buildingConfig;
                BuildingBlueprintProcessor.TryGetConfig(GameVersionEnvironment.CurrentVersion,
                    building.Definition, building.AdditionalConfigData, out buildingConfig, logger);

                Model.CreateBuilding(building.Definition, placed, buildingConfig);
                BuildingCount++;
            }
        }
    }

    /// <summary>Runs the sandbox forward. Deltas are simulation time, not frames.</summary>
    public void Advance(Ticks delta)
    {
        Simulator.SynchronousUpdate(delta, Config, Strategy);
        Elapsed += delta;
    }

    /// <summary>
    /// Runs the prediction graph. Predictions propagate one simulation per update, so a graph
    /// this deep needs several passes before the shapes at its far end settle.
    /// </summary>
    public void AdvancePredictions(int passes)
    {
        if (Predictions == null)
        {
            return;
        }

        for (int i = 0; i < passes; i++)
        {
            Predictions.SynchronousUpdate(Ticks.OneSecond, Config, Strategy);
        }
    }

    /// <summary>How many simulations came into existence, which is the sign it wired up.</summary>
    public int CountSimulations()
    {
        int count = 0;
        foreach (ILocalizedSimulation localized in ((ISimulator)Simulator).Simulations)
        {
            count++;
        }

        return count;
    }

    public string Describe()
    {
        StringBuilder text = new StringBuilder();

        text.Append("copied ").Append(IslandCount)
            .Append(IslandCount == 1 ? " platform, " : " platforms, ")
            .Append(BuildingCount).Append(" buildings into a private world");

        text.Append("\nthe game wired them into ").Append(CountSimulations()).Append(" simulations");
        text.Append("\nran ").Append(Elapsed).Append(" of simulation time");

        return text.ToString();
    }

    public void Dispose()
    {
        try
        {
            Predictions?.Dispose();
            PredictionGraph?.Dispose();
            Simulator?.Dispose();
            Graph?.Dispose();
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }

        Predictions = null;
        PredictionGraph = null;
        Simulator = null;
        Graph = null;
        Model = null;
        Layout = null;
    }
}
