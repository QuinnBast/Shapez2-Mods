using Core.Pooling;
using Game.Content.Features.Belts;
using Game.Content.Features.Fluids;
using Game.Content.Features.SpacePaths;
using Game.Core.Serialization;
using Game.Core.Simulation;
using Game.Core.Trains;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// One cargo store for either kind of cargo.
    ///
    /// There used to be a shape store and a fluid store, for the same reason there used to be two
    /// belts - connector tags. The belt solved that by carrying both tags, so the store may as
    /// well: a package is a package, and asking a player to choose the right store before they
    /// know what a line will carry is a choice with no interesting answer.
    ///
    /// It holds a shape half and a fluid half side by side rather than a single queue of
    /// discriminated packages. The queues never interact, `CargoStoreState&lt;TItem&gt;` already
    /// does everything needed, and a discriminated union would have meant a new serializer for no
    /// behavioural gain. One of the two halves is idle on every store anyone actually builds.
    ///
    /// **A layer takes whichever kind reaches it first** and refuses the other until it drains.
    /// That keeps capacity at 25 per layer rather than 25 of each, and keeps the rack honest -
    /// there are 25 slots drawn per shelf, so a layer that could hold 50 would under-report. Per
    /// layer rather than per store because the three queues are already independent everywhere
    /// else.
    [SyncableIdentifier("TrainCargoToolsAnyCargoStoreState")]
    public sealed class AnyCargoStoreState : ISimulationState, ISyncable
    {
        /// Nested plain objects. Their own SyncableIdentifiers are irrelevant here - nothing
        /// resolves them polymorphically, Sync is just called on each in a fixed order.
        public readonly ShapeCargoStoreState Shapes = new();
        public readonly FluidCargoStoreState Fluids = new();

        /// Partially packed cargo, one per layer, for the store's own intake - see CargoIntake.
        ///
        /// **Deliberately not serialized**, for exactly the reason the belt's equivalent is not:
        /// each simulation state sits in its own length-delimited blob, so adding a field to Sync
        /// makes every save written before it walk off the end of its blob into the next one, and
        /// the error surfaces far away as "Bad string LUT index". CargoBeltSimulationState
        /// carries the full account.
        ///
        /// The loss is at most PackageSize-1 items per layer, on a store that was mid-pack when
        /// the game was saved. Everything already packed is in the queues, which are saved.
        public readonly TrainCargoFillingContainerState<ShapeId>[] ShapeIntake;
        public readonly TrainCargoFillingContainerState<FluidId>[] FluidIntake;

        public AnyCargoStoreState()
        {
            ShapeIntake = new TrainCargoFillingContainerState<ShapeId>[SpacePathConstants.NumLayers];
            FluidIntake = new TrainCargoFillingContainerState<FluidId>[SpacePathConstants.NumLayers];
            for (int layer = 0; layer < SpacePathConstants.NumLayers; layer++)
            {
                ShapeIntake[layer] = new TrainCargoFillingContainerState<ShapeId>();
                FluidIntake[layer] = new TrainCargoFillingContainerState<FluidId>();
            }
        }

        /// Only the two queues. See the note on the intake fields for why they are left out.
        public void Sync(ISerializationVisitor visitor)
        {
            Shapes.Sync(visitor);
            Fluids.Sync(visitor);
        }
    }

    /// The simulation behind the combined store.
    ///
    /// Deliberately not built on `CargoStoreSimulation&lt;TItem, TState&gt;`: that is a
    /// `Simulation&lt;TState&gt;` and so can own exactly one state. The two legacy stores still
    /// use it, unchanged, so that old saves keep working.
    public sealed class AnyCargoStoreSimulation
        : Simulation<AnyCargoStoreState>, IItemBundleSimulation, ISimulation, IUpdatableSimulation,
          ICargoStoreView
    {
        private readonly ItemLaneBundle<DummyLane> InputBundle = ItemLaneBundle.Create<DummyLane>();
        private readonly ItemLaneBundle<DummyLane> OutputBundle = ItemLaneBundle.Create<DummyLane>();

        private readonly Pool<PackageOnTrack<CargoPackage<ShapeId>>> ShapePool;
        private readonly Pool<PackageOnTrack<CargoPackage<FluidId>>> FluidPool;

        /// Reused purely to ask CanAcceptItem, never handed over. See the packager for why the
        /// probe has to be a real PackageOnTrack rather than null.
        private readonly PackageOnTrack<CargoPackage<ShapeId>> ShapeProbe = new();
        private readonly PackageOnTrack<CargoPackage<FluidId>> FluidProbe = new();

        /// One per layer. What lets a train unloader dock straight against a store: the unloader
        /// offers loose shapes or fluid, and the store packs them itself rather than refusing
        /// them. Same class and same reasoning as the belt's - see CargoIntake.
        private readonly CargoIntake[] Intakes;

        public int CapacityPerLayer => CargoStoreState<ShapeId>.CapacityPerLayer;

        /// Two of each, both the same bundle - the same trick the belt uses. The store carries a
        /// belt tag and a pipe tag at each pivot so it joins either line, and
        /// ConnectableIslandSimulation ignores the second connector at a pivot unless the
        /// simulation claims a second bundle. See CargoBeltSimulation.
        public int NumItemReceiverBundles => 2;

        public int NumItemProviderBundles => 2;

        public AnyCargoStoreSimulation(
            ICargoContainerCapacityConfigProvider shapeCapacity,
            ICargoContainerCapacityConfigProvider fluidCapacity,
            IFluidRegistry fluids,
            AnyCargoStoreState state)
            : base(state)
        {
            ShapePool = Pool.For<PackageOnTrack<CargoPackage<ShapeId>>>();
            FluidPool = Pool.For<PackageOnTrack<CargoPackage<FluidId>>>();

            Intakes = new CargoIntake[SpacePathConstants.NumLayers];

            for (short layer = 0; layer < SpacePathConstants.NumLayers; layer++)
            {
                Intakes[layer] = new CargoIntake(
                    shapeCapacity, fluidCapacity, fluids,
                    state.ShapeIntake[layer], state.FluidIntake[layer]);

                Receiver receiver = new(this, layer);
                for (short lane = 0; lane < SpacePathConstants.NumLanes; lane++)
                {
                    InputBundle.GetSender(lane, layer).NextLane = receiver;
                }
            }
        }

        /// What one layer is holding, for the side panel and the renderer. Only one half of a
        /// layer is ever non-empty, so the sum is the count.
        public int CountAt(int layer)
        {
            return State.Shapes.CountAt(layer) + State.Fluids.CountAt(layer);
        }

        /// Whether this layer has committed to a kind, and which.
        public bool HoldsFluid(int layer)
        {
            return State.Fluids.CountAt(layer) > 0;
        }

        public CargoPackage<ShapeId> ShapeAt(int layer, int index)
        {
            return State.Shapes.PackageAt(layer, index);
        }

        public CargoPackage<FluidId> FluidAt(int layer, int index)
        {
            return State.Fluids.PackageAt(layer, index);
        }

        public void ClearContent()
        {
            State.Shapes.Clear();
            State.Fluids.Clear();

            for (int layer = 0; layer < Intakes.Length; layer++)
            {
                Intakes[layer].Clear();
            }
        }

        public void TraverseLanes<TTraverser>(TTraverser traverser) where TTraverser : IItemLaneTraverser
        {
            InputBundle.TraverseLanes(traverser);
            OutputBundle.TraverseLanes(traverser);
        }

        public void Update(Ticks startTicks, Ticks deltaTicks)
        {
            for (short layer = 0; layer < SpacePathConstants.NumLayers; layer++)
            {
                Intakes[layer].Update(startTicks, deltaTicks);
                Shelve(layer);

                if (CountAt(layer) == 0)
                {
                    continue;
                }

                Drain(layer);
            }
        }

        /// Move a package the intake has finished packing into this layer's queue.
        ///
        /// Cleared only once it is actually shelved. A finished package with nowhere to go stays
        /// in the container, which is what makes a full store stop accepting loose items instead
        /// of dropping them.
        private void Shelve(short layer)
        {
            if (!Intakes[layer].TryTakeReady(out IBeltItem ready))
            {
                return;
            }

            if (ready is PackageOnTrack<CargoPackage<ShapeId>> shape)
            {
                if (State.Shapes.TryStore(layer, shape.Container))
                {
                    Intakes[layer].ClearTaken();
                }

                ShapePool.Return(shape);
                return;
            }

            if (ready is PackageOnTrack<CargoPackage<FluidId>> fluid)
            {
                if (State.Fluids.TryStore(layer, fluid.Container))
                {
                    Intakes[layer].ClearTaken();
                }

                FluidPool.Return(fluid);
            }
        }

        /// Push this layer's backlog onto its output lanes for as long as they will take it.
        ///
        /// No rate limiting of its own: a lane refuses once its item spacing is used up, which
        /// is the same thing that paces every other belt in the game.
        private void Drain(short layer)
        {
            for (short lane = 0; lane < SpacePathConstants.NumLanes; lane++)
            {
                IItemReceiver next = OutputBundle.GetSender(lane, layer).NextLane;

                // Nothing at all, or something that would take a package and destroy it - a
                // store's output connector will connect to an ordinary belt or pipe just as a
                // belt's will. See CargoHandover.
                if (!CargoHandover.Allows(next, ShapeProbe))
                {
                    continue;
                }

                while (State.Shapes.TryPeek(layer, out CargoPackage<ShapeId> shape))
                {
                    ShapeProbe.Container = shape;
                    if (!next.CanAcceptItem(ShapeProbe))
                    {
                        break;
                    }

                    PackageOnTrack<CargoPackage<ShapeId>> wrapped = ShapePool.Retrieve();
                    wrapped.Container = shape;
                    State.Shapes.RemoveHead(layer);
                    next.HandOverItem(wrapped, Ticks.Zero);
                }

                while (State.Fluids.TryPeek(layer, out CargoPackage<FluidId> fluid))
                {
                    FluidProbe.Container = fluid;
                    if (!next.CanAcceptItem(FluidProbe))
                    {
                        break;
                    }

                    PackageOnTrack<CargoPackage<FluidId>> wrapped = FluidPool.Retrieve();
                    wrapped.Container = fluid;
                    State.Fluids.RemoveHead(layer);
                    next.HandOverItem(wrapped, Ticks.Zero);
                }
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

        /// One per layer, shared by that layer's four lanes, taking either kind of package.
        private sealed class Receiver : IItemReceiver, ICargoPackageSink
        {
            private readonly AnyCargoStoreSimulation Store;
            private readonly short Layer;

            /// What the game's own filling container reports. A receiver that is not a lane
            /// still has to tell the belt behind it how far items may advance.
            public Steps MaxStep_S => LaneConstants.ItemSpacingHalf * 12;

            public Receiver(AnyCargoStoreSimulation store, short layer)
            {
                Store = store;
                Layer = layer;
            }

            public bool CanAcceptItem(IBeltItem itemToTransfer)
            {
                if (itemToTransfer is PackageOnTrack<CargoPackage<ShapeId>>)
                {
                    return HasRoomForShapes();
                }

                if (itemToTransfer is PackageOnTrack<CargoPackage<FluidId>>)
                {
                    return HasRoomForFluid();
                }

                // A loose item, which is all a train unloader can offer. The store packs it
                // itself. Room for the *finished* package is tested here rather than when it is
                // shelved, so the back-pressure reaches the unloader at the moment it asks.
                if (!Store.Intakes[Layer].CanAccept(itemToTransfer))
                {
                    return false;
                }

                return itemToTransfer is ShapeItem ? HasRoomForShapes() : HasRoomForFluid();
            }

            /// A layer takes whichever kind reached it first, so room means both space in that
            /// queue and the other queue being empty.
            private bool HasRoomForShapes()
            {
                return !Store.State.Shapes.IsFull(Layer) && Store.State.Fluids.CountAt(Layer) == 0;
            }

            private bool HasRoomForFluid()
            {
                return !Store.State.Fluids.IsFull(Layer) && Store.State.Shapes.CountAt(Layer) == 0;
            }

            public void HandOverItem(IBeltItem itemToTransfer, Ticks remainingTicks)
            {
                // Copy before returning: Pool.Return clears the wrapper's Container.
                if (itemToTransfer is PackageOnTrack<CargoPackage<ShapeId>> shape)
                {
                    Store.State.Shapes.TryStore(Layer, shape.Container);
                    Store.ShapePool.Return(shape);
                    return;
                }

                if (itemToTransfer is PackageOnTrack<CargoPackage<FluidId>> fluid)
                {
                    Store.State.Fluids.TryStore(Layer, fluid.Container);
                    Store.FluidPool.Return(fluid);
                    return;
                }

                Store.Intakes[Layer].Accept(itemToTransfer, remainingTicks);
            }
        }
    }
}
