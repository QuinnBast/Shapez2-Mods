using System.Linq;
using Core.Factory;
using Game.Core.Simulation;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Builds a crossing's simulation, giving each of its two paths the speed of the vanilla
    /// path type it stands in for.
    ///
    /// Speeds are read off the vanilla space belt and space pipe island definitions rather than
    /// hard-coded, so a crossing keeps pace with whatever the scenario and the player's research
    /// have made those. SpaceConveyorSpeed is a BuffableBeltSpeed carrying its own ResearchSpeedId,
    /// which is what applies the buff.
    internal sealed class CrossoverSimulationFactory
        : IIslandSimulationFactoryBuilder<CrossoverSimulation, CrossoverSimulationState, SpacePathConfiguration>
    {
        private readonly CrossoverKind Kind;

        public CrossoverSimulationFactory(CrossoverKind kind)
        {
            Kind = kind;
        }

        public IFactory<CrossoverSimulationState, IslandInstance, CrossoverSimulation> BuildFactory(
            SimulationSystemsDependencies dependencies, out SpacePathConfiguration config)
        {
            SpacePathConfiguration beltConfig =
                dependencies.Mode.Islands.SpaceBelts.First().ConfigAs<SpacePathConfiguration>();
            SpacePathConfiguration pipeConfig =
                dependencies.Mode.Islands.SpacePipes.First().ConfigAs<SpacePathConfiguration>();

            // Path A is West-to-East, path B North-to-South, matching the connector order in
            // CrossoverConnectors and the bundle order in CrossoverSimulation.
            BeltSpeed speedA = Kind == CrossoverKind.PipePipe
                ? pipeConfig.SpaceConveyorSpeed
                : beltConfig.SpaceConveyorSpeed;
            BeltSpeed speedB = Kind == CrossoverKind.BeltBelt
                ? beltConfig.SpaceConveyorSpeed
                : pipeConfig.SpaceConveyorSpeed;

            // The island carries one configuration, and it is what research looks at to decide
            // whether this island is affected by a speed upgrade. A mixed crossing has to pick
            // one; the belt side is the one players will notice stalling.
            config = Kind == CrossoverKind.PipePipe ? pipeConfig : beltConfig;

            // The side panel needs these and has no way to reach a GameMode of its own; see
            // CrossoverScenario. This runs once per scenario load, before any panel can open.
            CrossoverScenario.Publish(
                beltConfig.SpaceConveyorSpeed.ResearchId,
                pipeConfig.SpaceConveyorSpeed,
                FluidUnit.FromLiters(dependencies.Mode.Buildings.FluidPortSender
                   .ConfigAs<IFluidPortSenderConfiguration>().LaunchConfiguration
                   .PackageSizeInLiters),
                dependencies.Mode.MaxBuildingLayer);

            return new Factory(speedA, speedB);
        }

        private sealed class Factory : IFactory<CrossoverSimulationState, IslandInstance, CrossoverSimulation>
        {
            private readonly BeltSpeed SpeedA;
            private readonly BeltSpeed SpeedB;

            public Factory(BeltSpeed speedA, BeltSpeed speedB)
            {
                SpeedA = speedA;
                SpeedB = speedB;
            }

            public CrossoverSimulation Produce(CrossoverSimulationState state, IslandInstance island)
            {
                return new CrossoverSimulation(SpeedA, SpeedB, state);
            }
        }
    }
}
