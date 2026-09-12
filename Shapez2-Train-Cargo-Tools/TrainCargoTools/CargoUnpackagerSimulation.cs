using System;
using Core.Collections.Scoped;
using Core.Pooling;
using Game.Content.Features.Belts;
using Game.Content.Features.Fluids;
using Game.Content.Features.SpacePaths;
using Game.Core.Simulation;
using Game.Core.Trains;

namespace TrainCargoTools
{
    /// Cargo packages in on a cargo belt, loose items out onto an ordinary space belt or pipe.
    ///
    /// The mirror of the packager, and mostly the train station's unloader with the train
    /// removed. TrainCargoToBeltFillingContainer is the game's class and does all the work: given
    /// a package and the output lanes, it pops items onto whichever lane will take one until the
    /// package is empty. What vanilla has no equivalent of is getting a package in off a belt in
    /// the first place - a station gets its packages from a wagon - so CargoPackageReceiver
    /// supplies that, writing into the very state the filling container drains - and, since a
    /// train unloader can only offer loose items, passes those straight through to the output so
    /// an unloader can dock against an unpackager with no cargo belt in between.
    public abstract class CargoUnpackagerSimulation<TItem, TState> : Simulation<TState>, IItemBundleSimulation,
        ISimulation, IUpdatableSimulation
        where TItem : unmanaged, IEquatable<TItem>
        where TState : CargoLayerState<TItem>, ISimulationState, new()
    {
        private readonly ItemLaneBundle<DummyLane> InputBundle = ItemLaneBundle.Create<DummyLane>();
        private readonly ItemLaneBundle<DummyLane> OutputBundle = ItemLaneBundle.Create<DummyLane>();

        private readonly TrainCargoToBeltFillingContainer<TItem>[] FillingContainers;

        public int NumItemReceiverBundles => 1;

        public int NumItemProviderBundles => 1;

        protected CargoUnpackagerSimulation(ICargoToBeltItemConverter<TItem> converter, TState state)
            : base(state)
        {
            Pool<PackageOnTrack<CargoPackage<TItem>>> packagePool =
                Pool.For<PackageOnTrack<CargoPackage<TItem>>>();

            FillingContainers = new TrainCargoToBeltFillingContainer<TItem>[SpacePathConstants.NumLayers];

            for (short layer = 0; layer < SpacePathConstants.NumLayers; layer++)
            {
                // Kept, not scoped: the receiver holds on to these so it can pass a loose item
                // straight through to the output, which is what lets a train unloader dock
                // against an unpackager directly. The filling container takes the same senders.
                IItemProvider[] outputs = new IItemProvider[SpacePathConstants.NumLanes];
                for (short lane = 0; lane < SpacePathConstants.NumLanes; lane++)
                {
                    outputs[lane] = OutputBundle.GetSender(lane, layer);
                }

                CargoPackageReceiver<TItem> receiver =
                    new(state.Layers[layer], packagePool, outputs);
                for (short lane = 0; lane < SpacePathConstants.NumLanes; lane++)
                {
                    InputBundle.GetSender(lane, layer).NextLane = receiver;
                }

                using ScopedList<IItemProvider> senders = ScopedList<IItemProvider>.Get();
                senders.AddRange(outputs);

                FillingContainers[layer] = new TrainCargoToBeltFillingContainer<TItem>(
                    converter, senders, state.Layers[layer]);
            }
        }

        public void ClearContent()
        {
            for (int layer = 0; layer < FillingContainers.Length; layer++)
            {
                FillingContainers[layer].ClearContent();
            }
        }

        public void TraverseLanes<TTraverser>(TTraverser traverser) where TTraverser : IItemLaneTraverser
        {
            InputBundle.TraverseLanes(traverser);
            OutputBundle.TraverseLanes(traverser);
        }

        public void Update(Ticks startTicks, Ticks deltaTicks)
        {
            for (int layer = 0; layer < FillingContainers.Length; layer++)
            {
                FillingContainers[layer].Update(deltaTicks);
            }
        }

        public IItemReceiverBundle GetItemReceiverBundle(int inputIndex)
        {
            return InputBundle;
        }

        public IItemProviderBundle GetItemProviderBundle(int outputIndex)
        {
            return OutputBundle;
        }
    }

    /// Shape cargo packages in, loose shapes out onto a space belt.
    public sealed class ShapeCargoUnpackagerSimulation
        : CargoUnpackagerSimulation<ShapeId, ShapeCargoUnpackagerState>
    {
        public ShapeCargoUnpackagerSimulation(IShapeRegistry shapes, ShapeCargoUnpackagerState state)
            : base(new ShapeCargoToBeltItemConverter(shapes), state)
        {
        }
    }

    /// Fluid cargo packages in, fluid out onto a space pipe.
    ///
    /// The 60 litres is vanilla's, not a choice made here: FluidCargoStationSimulationCreator
    /// hard-codes `FluidUnit.FromLiters(60)` when it builds a fluid station's unloader, and an
    /// unpackager has to emit the same blob size a station does or the two would disagree about
    /// what one unit of fluid cargo is worth.
    public sealed class FluidCargoUnpackagerSimulation
        : CargoUnpackagerSimulation<FluidId, FluidCargoUnpackagerState>
    {
        public static readonly FluidUnit PackageUnit = FluidUnit.FromLiters(60);

        public FluidCargoUnpackagerSimulation(
            IFluidRegistry fluids, FluidPackageItemSolver solver, FluidCargoUnpackagerState state)
            : base(new FluidCargoToBeltItemConverter(fluids, solver, PackageUnit), state)
        {
        }
    }
}
