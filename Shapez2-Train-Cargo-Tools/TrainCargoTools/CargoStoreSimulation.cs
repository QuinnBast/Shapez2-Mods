using System;
using Core.Pooling;
using Game.Content.Features.Belts;
using Game.Content.Features.Fluids;
using Game.Content.Features.SpacePaths;
using Game.Core.Simulation;
using Game.Core.Trains;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Holds cargo packages so a line does not stall when the thing at the end of it does.
    ///
    /// A cargo belt is already buffer - that was the point of the whole mod - but it is buffer
    /// you can only add by making the line longer. A store is buffer you can add in one chunk:
    /// 25 packages per layer, 75 in all, which at the game's shape package size is several
    /// thousand shapes off one platform tile.
    ///
    /// Packages leave on the layer they arrived on, and each layer has its own 25, so a busy
    /// layer cannot eat the capacity a quiet one needs. The four lanes *within* a layer are
    /// merged, which is ordinary buffer behaviour and is what lets the store fill from a
    /// partly-used line and empty onto a clear one.
    public abstract class CargoStoreSimulation<TItem, TState> : Simulation<TState>, IItemBundleSimulation,
        ISimulation, IUpdatableSimulation, ICargoStoreView
        where TItem : unmanaged, IEquatable<TItem>
        where TState : CargoStoreState<TItem>, ISimulationState, new()
    {
        private readonly ItemLaneBundle<DummyLane> InputBundle = ItemLaneBundle.Create<DummyLane>();
        private readonly ItemLaneBundle<DummyLane> OutputBundle = ItemLaneBundle.Create<DummyLane>();

        private readonly Pool<PackageOnTrack<CargoPackage<TItem>>> PackagePool;

        /// Reused to ask CanAcceptItem, never handed over - see the packager for why the probe
        /// has to be a real PackageOnTrack rather than null.
        private readonly PackageOnTrack<CargoPackage<TItem>> Probe = new();

        /// For the side panel. See ICargoStoreView.
        public int CapacityPerLayer => CargoStoreState<TItem>.CapacityPerLayer;

        public int CountAt(int layer)
        {
            return State.CountAt(layer);
        }

        /// For the renderer. See CargoStoreState.PackageAt.
        public CargoPackage<TItem> PackageAt(int layer, int index)
        {
            return State.PackageAt(layer, index);
        }

        public int NumItemReceiverBundles => 1;

        public int NumItemProviderBundles => 1;

        protected CargoStoreSimulation(TState state)
            : base(state)
        {
            PackagePool = Pool.For<PackageOnTrack<CargoPackage<TItem>>>();

            for (short layer = 0; layer < SpacePathConstants.NumLayers; layer++)
            {
                Receiver receiver = new(state, layer, PackagePool);
                for (short lane = 0; lane < SpacePathConstants.NumLanes; lane++)
                {
                    InputBundle.GetSender(lane, layer).NextLane = receiver;
                }
            }
        }

        public void ClearContent()
        {
            State.Clear();
        }

        public void TraverseLanes<TTraverser>(TTraverser traverser) where TTraverser : IItemLaneTraverser
        {
            InputBundle.TraverseLanes(traverser);
            OutputBundle.TraverseLanes(traverser);
        }

        public void Update(Ticks startTicks, Ticks deltaTicks)
        {
            if (State.IsEmpty)
            {
                return;
            }

            for (short layer = 0; layer < SpacePathConstants.NumLayers; layer++)
            {
                Drain(layer);
            }
        }

        /// Push this layer's backlog onto its output lanes for as long as they will take it.
        ///
        /// No rate limiting of its own: a lane refuses once its item spacing is used up, which
        /// is the same thing that paces every other belt in the game. The store therefore
        /// empties at belt speed and no faster.
        private void Drain(short layer)
        {
            for (short lane = 0; lane < SpacePathConstants.NumLanes; lane++)
            {
                IItemReceiver next = OutputBundle.GetSender(lane, layer).NextLane;

                // Nothing at all, or something that would take a package and destroy it. See
                // CargoHandover.
                if (!CargoHandover.Allows(next, Probe))
                {
                    continue;
                }

                while (State.TryPeek(layer, out CargoPackage<TItem> package))
                {
                    Probe.Container = package;
                    if (!next.CanAcceptItem(Probe))
                    {
                        break;
                    }

                    PackageOnTrack<CargoPackage<TItem>> wrapped = PackagePool.Retrieve();
                    wrapped.Container = package;
                    State.RemoveHead(layer);
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

        /// One per layer, shared by that layer's four lanes.
        private sealed class Receiver : IItemReceiver, ICargoPackageSink
        {
            private readonly TState Store;
            private readonly int Layer;
            private readonly Pool<PackageOnTrack<CargoPackage<TItem>>> PackagePool;

            /// What the game's own filling container reports. A receiver that is not a lane
            /// still has to tell the belt behind it how far items may advance.
            public Steps MaxStep_S => LaneConstants.ItemSpacingHalf * 12;

            public Receiver(TState store, int layer, Pool<PackageOnTrack<CargoPackage<TItem>>> packagePool)
            {
                Store = store;
                Layer = layer;
                PackagePool = packagePool;
            }

            public bool CanAcceptItem(IBeltItem itemToTransfer)
            {
                return !Store.IsFull(Layer) && itemToTransfer is PackageOnTrack<CargoPackage<TItem>>;
            }

            public void HandOverItem(IBeltItem itemToTransfer, Ticks remainingTicks)
            {
                PackageOnTrack<CargoPackage<TItem>> wrapper =
                    (PackageOnTrack<CargoPackage<TItem>>)itemToTransfer;

                // Copy before returning: Pool.Return clears the wrapper's Container.
                Store.TryStore(Layer, wrapper.Container);
                PackagePool.Return(wrapper);
            }
        }
    }

    /// Shape cargo packages in and out, 25 per layer held in between.
    public sealed class ShapeCargoStoreSimulation : CargoStoreSimulation<ShapeId, ShapeCargoStoreState>
    {
        public ShapeCargoStoreSimulation(ShapeCargoStoreState state)
            : base(state)
        {
        }
    }

    /// The pipe-tagged twin, for the fluid cargo line.
    public sealed class FluidCargoStoreSimulation : CargoStoreSimulation<FluidId, FluidCargoStoreState>
    {
        public FluidCargoStoreSimulation(FluidCargoStoreState state)
            : base(state)
        {
        }
    }
}
