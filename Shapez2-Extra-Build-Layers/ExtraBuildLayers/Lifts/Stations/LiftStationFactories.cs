using System;
using System.Linq;
using Core.Factory;
using Game.Content.Features.Predictions;
using Game.Content.Features.SpacePaths;
using Game.Content.Features.SpacePaths.Prediction;
using Game.Core.Belts.BeltPath;
using Game.Core.Simulation;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;
using ShapezShifter.Hijack.Predictions;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Reads the belt (or pipe) speed off the vanilla space path definitions, so a climb keeps pace
/// with research the same way the belt either side of it does - <c>SpaceConveyorSpeed</c> is a
/// buffable speed carrying its own research id.
/// </summary>
internal static class LiftStationSpeed
{
    public static SpacePathConfiguration Config(SimulationSystemsDependencies dependencies, LiftPath path)
    {
        return (path == LiftPath.Belt ? dependencies.Mode.Islands.SpaceBelts : dependencies.Mode.Islands.SpacePipes)
            .First().ConfigAs<SpacePathConfiguration>();
    }
}

internal sealed class LiftEntrySimulationFactory
    : IIslandSimulationFactoryBuilder<LiftEntrySimulation, LiftStationSimulationState, SpacePathConfiguration>
{
    private readonly LiftPath Path;

    public LiftEntrySimulationFactory(LiftPath path)
    {
        Path = path;
    }

    public IFactory<LiftStationSimulationState, IslandInstance, LiftEntrySimulation> BuildFactory(
        SimulationSystemsDependencies dependencies, out SpacePathConfiguration config)
    {
        config = LiftStationSpeed.Config(dependencies, Path);
        return new Factory(config.SpaceConveyorSpeed);
    }

    private sealed class Factory : IFactory<LiftStationSimulationState, IslandInstance, LiftEntrySimulation>
    {
        private readonly BeltSpeed Speed;

        public Factory(BeltSpeed speed)
        {
            Speed = speed;
        }

        public LiftEntrySimulation Produce(LiftStationSimulationState state, IslandInstance island)
        {
            // One segment for the entry's own chunk and one for every level the shaft passes
            // through before the exit, which brings its own.
            int levels = Math.Abs(LiftStationConfiguration.RiseOf(island.Configuration));
            return new LiftEntrySimulation(Speed, state, levels);
        }
    }
}

internal sealed class LiftExitSimulationFactory
    : IIslandSimulationFactoryBuilder<LiftExitSimulation, LiftStationSimulationState, SpacePathConfiguration>
{
    private readonly LiftPath Path;

    public LiftExitSimulationFactory(LiftPath path)
    {
        Path = path;
    }

    public IFactory<LiftStationSimulationState, IslandInstance, LiftExitSimulation> BuildFactory(
        SimulationSystemsDependencies dependencies, out SpacePathConfiguration config)
    {
        config = LiftStationSpeed.Config(dependencies, Path);
        return new Factory(config.SpaceConveyorSpeed);
    }

    private sealed class Factory : IFactory<LiftStationSimulationState, IslandInstance, LiftExitSimulation>
    {
        private readonly BeltSpeed Speed;

        public Factory(BeltSpeed speed)
        {
            Speed = speed;
        }

        public LiftExitSimulation Produce(LiftStationSimulationState state, IslandInstance island)
        {
            return new LiftExitSimulation(Speed, state);
        }
    }
}

/// <summary>
/// What a station contributes to the prediction graph: whatever arrives is what leaves. One bundle
/// handed out as both receiver and provider, which is vanilla's straight segment exactly.
///
/// Without one the climb is a hole in the graph and everything downstream of an exit reads as
/// empty while real items flow - Crossover Platforms hit exactly that.
/// </summary>
public sealed class LiftStationPredictionSimulation : IItemBundlePredictionSimulation, ISimulation, IUpdatableSimulation
{
    public readonly ItemPredictionBundle<ItemPredictionConverter> Bundle =
        ItemPredictionBundle.Create<ItemPredictionConverter>();

    public int NumItemReceiverBundles => 1;

    public int NumItemProviderBundles => 1;

    public IItemPredictionReceiverBundle GetItemReceiverBundle(int inputIndex)
    {
        return inputIndex == 0 ? Bundle : throw new ArgumentOutOfRangeException(nameof(inputIndex));
    }

    public IItemPredictionProviderBundle GetItemProviderBundle(int outputIndex)
    {
        return outputIndex == 0 ? Bundle : throw new ArgumentOutOfRangeException(nameof(outputIndex));
    }

    public void Update(Ticks startTicks, Ticks deltaTicks)
    {
        Bundle.Update(deltaTicks);
    }
}

internal sealed class LiftStationPredictionFactory
    : IIslandPredictionFactoryBuilder<LiftStationPredictionSimulation>, IFactory<LiftStationPredictionSimulation>
{
    public IFactory<LiftStationPredictionSimulation> BuildFactory(PredictionSystemsDependencies dependencies)
    {
        return this;
    }

    public LiftStationPredictionSimulation Produce()
    {
        return new LiftStationPredictionSimulation();
    }
}
