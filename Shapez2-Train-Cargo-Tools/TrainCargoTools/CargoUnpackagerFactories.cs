using System.Linq;
using Core.Factory;
using Game.Content.Features.Fluids;
using Game.Content.Features.SpacePaths;
using Game.Core.Simulation;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Builds shape unpackagers.
    ///
    /// Unlike a packager this needs no capacity configuration - emptying a package is driven by
    /// the package's own Amount - but it does need the shape registry, because turning a ShapeId
    /// back into a belt item is a registry lookup. That is the whole of
    /// ShapeCargoToBeltItemConverter.
    internal sealed class ShapeCargoUnpackagerFactory
        : IIslandSimulationFactoryBuilder<ShapeCargoUnpackagerSimulation, ShapeCargoUnpackagerState, SpacePathConfiguration>
    {
        public IFactory<ShapeCargoUnpackagerState, IslandInstance, ShapeCargoUnpackagerSimulation> BuildFactory(
            SimulationSystemsDependencies dependencies, out SpacePathConfiguration config)
        {
            config = dependencies.Mode.Islands.SpaceBelts.First().ConfigAs<SpacePathConfiguration>();
            return new Factory(dependencies.ShapeRegistry);
        }

        private sealed class Factory
            : IFactory<ShapeCargoUnpackagerState, IslandInstance, ShapeCargoUnpackagerSimulation>
        {
            private readonly IShapeRegistry Shapes;

            public Factory(IShapeRegistry shapes)
            {
                Shapes = shapes;
            }

            public ShapeCargoUnpackagerSimulation Produce(
                ShapeCargoUnpackagerState state, IslandInstance island)
            {
                return new ShapeCargoUnpackagerSimulation(Shapes, state);
            }
        }
    }

    /// Builds fluid unpackagers.
    ///
    /// Needs one dependency more than the shape version: rebuilding a fluid blob takes both the
    /// registry (FluidId back to an IFluid) and the package solver, which is what actually mints
    /// a FluidPackageItem of a given size.
    internal sealed class FluidCargoUnpackagerFactory
        : IIslandSimulationFactoryBuilder<FluidCargoUnpackagerSimulation, FluidCargoUnpackagerState, SpacePathConfiguration>
    {
        public IFactory<FluidCargoUnpackagerState, IslandInstance, FluidCargoUnpackagerSimulation> BuildFactory(
            SimulationSystemsDependencies dependencies, out SpacePathConfiguration config)
        {
            config = dependencies.Mode.Islands.SpaceBelts.First().ConfigAs<SpacePathConfiguration>();
            return new Factory(dependencies.FluidRegistry, dependencies.FluidPackagesItem);
        }

        private sealed class Factory
            : IFactory<FluidCargoUnpackagerState, IslandInstance, FluidCargoUnpackagerSimulation>
        {
            private readonly IFluidRegistry Fluids;
            private readonly FluidPackageItemSolver Solver;

            public Factory(IFluidRegistry fluids, FluidPackageItemSolver solver)
            {
                Fluids = fluids;
                Solver = solver;
            }

            public FluidCargoUnpackagerSimulation Produce(
                FluidCargoUnpackagerState state, IslandInstance island)
            {
                return new FluidCargoUnpackagerSimulation(Fluids, Solver, state);
            }
        }
    }
}
