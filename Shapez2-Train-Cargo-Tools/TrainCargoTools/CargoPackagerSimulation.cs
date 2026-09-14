using System;
using Core.Pooling;
using Game.Content.Features.Belts;
using Game.Content.Features.Fluids;
using Game.Content.Features.SpacePaths;
using Game.Core.Simulation;
using Game.Core.Trains;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Loose items in on an ordinary space belt or pipe, full cargo packages out onto a cargo belt.
    ///
    /// This is the train station's loader with the train removed. The packing itself is not new
    /// code: TrainBeltToCargoFillingContainer is the game's own class, it is what every cargo
    /// station already uses to turn arriving items into a CargoPackage, and it is re-hosted here
    /// unmodified. What a station does next is hand the full package to a CargoPackageTrack for a
    /// train to collect; what this does instead is wrap it in PackageOnTrack and put it on the
    /// output belt.
    ///
    /// Generic over the cargo item type because shapes and fluids differ in nothing but their
    /// converter and their capacity provider - the game itself keeps the two apart exactly this
    /// way, with one set of generic classes instantiated at ShapeId and FluidId.
    ///
    /// The input side is wired the way the vanilla loader wires it: a bundle of DummyLanes whose
    /// NextLane is the layer's filling container, so all four lanes of a layer pack into the same
    /// package. That is deliberate - one package holds one item type, so four lanes carrying four
    /// different shapes into one layer will fight each other through the filling container's
    /// subtractive penalty, exactly as they would at a station. Feed one item type per layer.
    public abstract class CargoPackagerSimulation<TItem, TState> : Simulation<TState>, IItemBundleSimulation,
        ISimulation, IUpdatableSimulation, ICargoPackagerView
        where TItem : unmanaged, IEquatable<TItem>
        where TState : CargoLayerState<TItem>, ISimulationState, new()
    {
        private readonly ItemLaneBundle<DummyLane> InputBundle = ItemLaneBundle.Create<DummyLane>();
        private readonly ItemLaneBundle<DummyLane> OutputBundle = ItemLaneBundle.Create<DummyLane>();

        private readonly TrainBeltToCargoFillingContainer<TItem>[] FillingContainers;
        private readonly ICargoContainerCapacityConfigProvider Capacity;
        private readonly Pool<PackageOnTrack<CargoPackage<TItem>>> PackagePool;

        /// Reused purely to ask CanAcceptItem, never handed over.
        ///
        /// The question "will the belt downstream take a cargo package" cannot be asked with a
        /// null or a bare IBeltItem, because a cargo belt's PreAcceptHook type-tests the item. So
        /// the probe has to be a real PackageOnTrack. Retrieving one from the pool every tick just
        /// to discard it when the belt is full would churn; one instance answers the question.
        private readonly PackageOnTrack<CargoPackage<TItem>> Probe = new();

        public int NumItemReceiverBundles => 1;

        public int NumItemProviderBundles => 1;

        /// How many items make one package. Read from the capacity provider rather than stated,
        /// because wagon-capacity research changes it mid-game - a gauge showing a fixed maximum
        /// would start lying the moment that unlocks. See ICargoPackagerView.
        public int PackageSize => Capacity.PackageSize;

        /// How far through the current package this layer is.
        public int AmountAt(int layer)
        {
            return FillingContainers[layer].Package.Amount;
        }

        protected CargoPackagerSimulation(
            ICargoContainerCapacityConfigProvider capacity,
            IBeltToCargoItemConverter<TItem> converter,
            TState state)
            : base(state)
        {
            Capacity = capacity;
            PackagePool = Pool.For<PackageOnTrack<CargoPackage<TItem>>>();

            FillingContainers = new TrainBeltToCargoFillingContainer<TItem>[SpacePathConstants.NumLayers];
            for (short layer = 0; layer < SpacePathConstants.NumLayers; layer++)
            {
                FillingContainers[layer] = new TrainBeltToCargoFillingContainer<TItem>(
                    capacity, converter, state.Layers[layer]);

                for (short lane = 0; lane < SpacePathConstants.NumLanes; lane++)
                {
                    InputBundle.GetSender(lane, layer).NextLane = FillingContainers[layer];
                }
            }
        }

        public void ClearContent()
        {
            for (int layer = 0; layer < FillingContainers.Length; layer++)
            {
                FillingContainers[layer].PrepareNewEmpty();
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
                TrainBeltToCargoFillingContainer<TItem> container = FillingContainers[layer];

                // Mirrors the vanilla loader: an emptied package still remembers which item it
                // held, and would keep refusing every other item until it is reset.
                if (container.Package.IsEmpty)
                {
                    container.PrepareNewEmpty();
                }

                if (container.Package.IsFull(Capacity) && TryEmit(layer, container.Package))
                {
                    container.PrepareNewEmpty();
                }

                container.Update(startTicks, deltaTicks);
            }
        }

        /// Hand one full package to the first output lane of this layer that will take it.
        ///
        /// Lanes are tried in order rather than round-robin because the rate makes it moot: a
        /// layer emits one package per PackageSize items received, so even four saturated input
        /// lanes cannot keep a single output lane busy. Lane 0 therefore carries the traffic in
        /// practice, and the others exist so a blockage downstream does not stall packing.
        private bool TryEmit(int layer, CargoPackage<TItem> package)
        {
            Probe.Container = package;

            for (short lane = 0; lane < SpacePathConstants.NumLanes; lane++)
            {
                IItemReceiver next = OutputBundle.GetSender(lane, (short)layer).NextLane;

                // The guard as well as the lane's own answer: a packager's output connector will
                // connect to an ordinary belt or pipe, which would take the package and lose it
                // downstream. Refusing leaves it in the filling container and stalls the
                // machine, which is visible. See CargoHandover.
                if (!CargoHandover.Allows(next, Probe) || !next.CanAcceptItem(Probe))
                {
                    continue;
                }

                PackageOnTrack<CargoPackage<TItem>> wrapped = PackagePool.Retrieve();
                wrapped.Container = package;
                next.HandOverItem(wrapped, Ticks.Zero);
                return true;
            }

            return false;
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

    /// Shapes in off a space belt, shape cargo packages out.
    public sealed class ShapeCargoPackagerSimulation
        : CargoPackagerSimulation<ShapeId, ShapeCargoPackagerState>
    {
        public ShapeCargoPackagerSimulation(
            ICargoContainerCapacityConfigProvider capacity, ShapeCargoPackagerState state)
            : base(capacity, new ShapeBeltItemToCargoConverter(), state)
        {
        }
    }

    /// Fluid in off a space pipe, fluid cargo packages out.
    ///
    /// Worth being clear about the two unrelated senses of "package" here: a FluidPackageItem is
    /// the blob that travels a space pipe, and FluidBeltItemToCargoConverter counts one of them
    /// as one unit of cargo regardless of how many litres it holds. That is vanilla's own
    /// accounting at a fluid station, not a simplification made here - which is why a packager
    /// and a station produce interchangeable cargo.
    public sealed class FluidCargoPackagerSimulation
        : CargoPackagerSimulation<FluidId, FluidCargoPackagerState>
    {
        public FluidCargoPackagerSimulation(
            ICargoContainerCapacityConfigProvider capacity, IFluidRegistry fluids,
            FluidCargoPackagerState state)
            : base(capacity, new FluidBeltItemToCargoConverter(fluids), state)
        {
        }
    }
}
