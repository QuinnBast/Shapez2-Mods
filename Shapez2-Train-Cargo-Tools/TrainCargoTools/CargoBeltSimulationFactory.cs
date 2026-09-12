using System.Linq;
using Core.Factory;
using Game.Core.Simulation;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;

namespace TrainCargoTools
{
    /// Gives a cargo belt the same speed as a vanilla space belt.
    ///
    /// Read off the space belt island definition rather than hard-coded, so research speed
    /// upgrades apply - SpaceConveyorSpeed is a BuffableBeltSpeed carrying its own
    /// ResearchSpeedId, and returning that configuration is what tells research this island is
    /// affected.
    internal sealed class CargoBeltSimulationFactory
        : IIslandSimulationFactoryBuilder<CargoBeltSimulation, CargoBeltSimulationState, SpacePathConfiguration>
    {
        public IFactory<CargoBeltSimulationState, IslandInstance, CargoBeltSimulation> BuildFactory(
            SimulationSystemsDependencies dependencies, out SpacePathConfiguration config)
        {
            config = dependencies.Mode.Islands.SpaceBelts.First().ConfigAs<SpacePathConfiguration>();

            // Same source as the packagers': GameMode.TrainCargoExchangeConfiguration, so a belt
            // packs to exactly the size a station would and wagon-capacity research applies.
            return new Factory(
                new CargoBeltSpeed(config.SpaceConveyorSpeed),
                new ShapeCargoContainerCapacityConfigProvider(
                    dependencies.Mode.TrainCargoExchangeConfiguration),
                new FluidCargoContainerCapacityConfigProvider(
                    dependencies.Mode.TrainCargoExchangeConfiguration),
                dependencies.FluidRegistry);
        }

        private sealed class Factory : IFactory<CargoBeltSimulationState, IslandInstance, CargoBeltSimulation>
        {
            private readonly IBeltSpeed Speed;
            private readonly ICargoContainerCapacityConfigProvider ShapeCapacity;
            private readonly ICargoContainerCapacityConfigProvider FluidCapacity;
            private readonly Game.Content.Features.Fluids.IFluidRegistry Fluids;

            public Factory(
                IBeltSpeed speed,
                ICargoContainerCapacityConfigProvider shapeCapacity,
                ICargoContainerCapacityConfigProvider fluidCapacity,
                Game.Content.Features.Fluids.IFluidRegistry fluids)
            {
                Speed = speed;
                ShapeCapacity = shapeCapacity;
                FluidCapacity = fluidCapacity;
                Fluids = fluids;
            }

            public CargoBeltSimulation Produce(CargoBeltSimulationState state, IslandInstance island)
            {
                return new CargoBeltSimulation(
                    Speed, ShapeCapacity, FluidCapacity, Fluids, state);
            }
        }
    }
}
