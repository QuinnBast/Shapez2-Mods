using System.Linq;
using Core.Factory;
using Game.Content.Features.SpacePaths;
using Game.Core.Simulation;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Builds packagers using the game's own train cargo numbers.
    ///
    /// The package size is not a mod constant: GameMode.TrainCargoExchangeConfiguration is the
    /// same ITrainCargoExchangeSimulationConfig the train stations are built from, so a package
    /// off a packager is exactly the package a station would have made, and a wagon capacity
    /// research that changes those numbers changes these too. The shape and fluid capacity
    /// providers read different fields of it (ShapePackageSize vs FluidPackageSize), which is
    /// the only reason there are two.
    ///
    /// The reported configuration is the space belt's, matching the cargo belt - it is what
    /// tells research that this island's throughput is belt-speed-affected.
    internal sealed class ShapeCargoPackagerFactory
        : IIslandSimulationFactoryBuilder<ShapeCargoPackagerSimulation, ShapeCargoPackagerState, SpacePathConfiguration>
    {
        public IFactory<ShapeCargoPackagerState, IslandInstance, ShapeCargoPackagerSimulation> BuildFactory(
            SimulationSystemsDependencies dependencies, out SpacePathConfiguration config)
        {
            config = dependencies.Mode.Islands.SpaceBelts.First().ConfigAs<SpacePathConfiguration>();

            ICargoContainerCapacityConfigProvider capacity =
                new ShapeCargoContainerCapacityConfigProvider(dependencies.Mode.TrainCargoExchangeConfiguration);

            return new Factory(capacity);
        }

        private sealed class Factory
            : IFactory<ShapeCargoPackagerState, IslandInstance, ShapeCargoPackagerSimulation>
        {
            private readonly ICargoContainerCapacityConfigProvider Capacity;

            public Factory(ICargoContainerCapacityConfigProvider capacity)
            {
                Capacity = capacity;
            }

            public ShapeCargoPackagerSimulation Produce(ShapeCargoPackagerState state, IslandInstance island)
            {
                return new ShapeCargoPackagerSimulation(Capacity, state);
            }
        }
    }

    /// As above, but reading FluidPackageSize and needing the fluid registry - the fluid
    /// converter has to turn an arriving FluidPackageItem's IFluid back into a FluidId.
    internal sealed class FluidCargoPackagerFactory
        : IIslandSimulationFactoryBuilder<FluidCargoPackagerSimulation, FluidCargoPackagerState, SpacePathConfiguration>
    {
        public IFactory<FluidCargoPackagerState, IslandInstance, FluidCargoPackagerSimulation> BuildFactory(
            SimulationSystemsDependencies dependencies, out SpacePathConfiguration config)
        {
            // Still the belt configuration, not the pipe one: a fluid packager takes fluid in but
            // emits cargo onto a cargo belt, and it is the belt side whose rate matters.
            config = dependencies.Mode.Islands.SpaceBelts.First().ConfigAs<SpacePathConfiguration>();

            ICargoContainerCapacityConfigProvider capacity =
                new FluidCargoContainerCapacityConfigProvider(dependencies.Mode.TrainCargoExchangeConfiguration);

            return new Factory(capacity, dependencies.FluidRegistry);
        }

        private sealed class Factory
            : IFactory<FluidCargoPackagerState, IslandInstance, FluidCargoPackagerSimulation>
        {
            private readonly ICargoContainerCapacityConfigProvider Capacity;
            private readonly Game.Content.Features.Fluids.IFluidRegistry Fluids;

            public Factory(
                ICargoContainerCapacityConfigProvider capacity,
                Game.Content.Features.Fluids.IFluidRegistry fluids)
            {
                Capacity = capacity;
                Fluids = fluids;
            }

            public FluidCargoPackagerSimulation Produce(FluidCargoPackagerState state, IslandInstance island)
            {
                return new FluidCargoPackagerSimulation(Capacity, Fluids, state);
            }
        }
    }
}
