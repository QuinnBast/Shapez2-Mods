using System.Linq;
using Core.Factory;
using Game.Content.Features.SpacePaths;
using Game.Core.Simulation;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Builds shape cargo stores.
    ///
    /// Nothing to configure: a store neither packs nor unpacks, so it needs no converter, no
    /// capacity provider and no registry. Its one number is its own, in CargoStoreState.
    ///
    /// Reports the space belt configuration for the same reason the rest of the mod does - it
    /// is what tells research this island's throughput is belt-speed-affected.
    internal sealed class ShapeCargoStoreFactory
        : IIslandSimulationFactoryBuilder<ShapeCargoStoreSimulation, ShapeCargoStoreState, SpacePathConfiguration>
    {
        public IFactory<ShapeCargoStoreState, IslandInstance, ShapeCargoStoreSimulation> BuildFactory(
            SimulationSystemsDependencies dependencies, out SpacePathConfiguration config)
        {
            config = dependencies.Mode.Islands.SpaceBelts.First().ConfigAs<SpacePathConfiguration>();
            return new Factory();
        }

        private sealed class Factory
            : IFactory<ShapeCargoStoreState, IslandInstance, ShapeCargoStoreSimulation>
        {
            public ShapeCargoStoreSimulation Produce(ShapeCargoStoreState state, IslandInstance island)
            {
                return new ShapeCargoStoreSimulation(state);
            }
        }
    }

    /// Builds fluid cargo stores.
    internal sealed class FluidCargoStoreFactory
        : IIslandSimulationFactoryBuilder<FluidCargoStoreSimulation, FluidCargoStoreState, SpacePathConfiguration>
    {
        public IFactory<FluidCargoStoreState, IslandInstance, FluidCargoStoreSimulation> BuildFactory(
            SimulationSystemsDependencies dependencies, out SpacePathConfiguration config)
        {
            config = dependencies.Mode.Islands.SpaceBelts.First().ConfigAs<SpacePathConfiguration>();
            return new Factory();
        }

        private sealed class Factory
            : IFactory<FluidCargoStoreState, IslandInstance, FluidCargoStoreSimulation>
        {
            public FluidCargoStoreSimulation Produce(FluidCargoStoreState state, IslandInstance island)
            {
                return new FluidCargoStoreSimulation(state);
            }
        }
    }

    /// The combined store: one island for either kind of cargo. See AnyCargoStore.
    ///
    /// Reports the space belt's configuration like the rest of the family, which is what tells
    /// research this island's throughput is belt-speed-affected.
    ///
    /// It does need a capacity provider and a fluid registry, unlike the two legacy stores: a
    /// store now packs loose items itself so a train unloader can dock straight against it, and
    /// packing needs to know how big a package is. Same source as everything else in the mod -
    /// GameMode.TrainCargoExchangeConfiguration - so a store packs to exactly the size a station
    /// would.
    internal sealed class AnyCargoStoreFactory
        : IIslandSimulationFactoryBuilder<AnyCargoStoreSimulation, AnyCargoStoreState, SpacePathConfiguration>
    {
        public IFactory<AnyCargoStoreState, IslandInstance, AnyCargoStoreSimulation> BuildFactory(
            SimulationSystemsDependencies dependencies, out SpacePathConfiguration config)
        {
            config = dependencies.Mode.Islands.SpaceBelts.First().ConfigAs<SpacePathConfiguration>();

            return new Factory(
                new ShapeCargoContainerCapacityConfigProvider(
                    dependencies.Mode.TrainCargoExchangeConfiguration),
                new FluidCargoContainerCapacityConfigProvider(
                    dependencies.Mode.TrainCargoExchangeConfiguration),
                dependencies.FluidRegistry);
        }

        private sealed class Factory
            : IFactory<AnyCargoStoreState, IslandInstance, AnyCargoStoreSimulation>
        {
            private readonly ICargoContainerCapacityConfigProvider ShapeCapacity;
            private readonly ICargoContainerCapacityConfigProvider FluidCapacity;
            private readonly Game.Content.Features.Fluids.IFluidRegistry Fluids;

            public Factory(
                ICargoContainerCapacityConfigProvider shapeCapacity,
                ICargoContainerCapacityConfigProvider fluidCapacity,
                Game.Content.Features.Fluids.IFluidRegistry fluids)
            {
                ShapeCapacity = shapeCapacity;
                FluidCapacity = fluidCapacity;
                Fluids = fluids;
            }

            public AnyCargoStoreSimulation Produce(AnyCargoStoreState state, IslandInstance island)
            {
                return new AnyCargoStoreSimulation(ShapeCapacity, FluidCapacity, Fluids, state);
            }
        }
    }
}
