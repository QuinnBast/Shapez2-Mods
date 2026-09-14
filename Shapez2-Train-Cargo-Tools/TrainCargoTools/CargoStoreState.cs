using System;
using Game.Content.Features.Fluids;
using Game.Content.Features.SpacePaths;
using Game.Core.Serialization;
using Game.Core.Simulation;
using Game.Core.Trains;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// One queue of cargo packages per layer, in arrival order.
    ///
    /// Three independent queues rather than one shared pool. A package leaves on the layer it
    /// arrived on either way, but independence is what stops a busy layer eating the capacity a
    /// quiet one needs - the quiet layer's train still gets its buffer. It also means position
    /// encodes the layer, so nothing has to be tagged.
    ///
    /// Each queue is compacted: slots 0..Count-1 are full and the rest empty, so there is no
    /// count to serialize. An empty slot is one whose package Amount is 0, which is exactly what
    /// CargoPackageSerializer writes for a default package, so a fixed-length sync round-trips
    /// an empty store as cheaply as a full one.
    public abstract class CargoStoreState<TItem> : ISimulationState, ISyncable
        where TItem : unmanaged, IEquatable<TItem>
    {
        /// Packages held per layer. Three layers, so three times this in total.
        public const int CapacityPerLayer = 25;

        private readonly CargoPackage<TItem>[][] Packages;
        private readonly int[] Counts;

        protected CargoStoreState()
        {
            Packages = new CargoPackage<TItem>[SpacePathConstants.NumLayers][];
            Counts = new int[SpacePathConstants.NumLayers];

            for (int layer = 0; layer < Packages.Length; layer++)
            {
                Packages[layer] = new CargoPackage<TItem>[CapacityPerLayer];
            }
        }

        public int CountAt(int layer)
        {
            return Counts[layer];
        }

        /// The package in one slot of one layer's queue.
        ///
        /// Exposed for the renderer, which draws the real package rather than a stand-in crate -
        /// so a stored shape shows its shape and a stored fluid its colour. Slots are compacted,
        /// so 0..CountAt(layer)-1 are the occupied ones and anything above is a default package.
        public CargoPackage<TItem> PackageAt(int layer, int index)
        {
            return Packages[layer][index];
        }

        public bool IsFull(int layer)
        {
            return Counts[layer] >= CapacityPerLayer;
        }

        public bool IsEmpty
        {
            get
            {
                for (int layer = 0; layer < Counts.Length; layer++)
                {
                    if (Counts[layer] > 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public void Sync(ISerializationVisitor visitor)
        {
            for (int layer = 0; layer < Packages.Length; layer++)
            {
                for (int slot = 0; slot < CapacityPerLayer; slot++)
                {
                    visitor.Sync(ref Packages[layer][slot]);
                }
            }

            if (visitor.Reading)
            {
                RecomputeCounts();
            }
        }

        /// Each queue is compacted, so the first empty slot is the end of it.
        private void RecomputeCounts()
        {
            for (int layer = 0; layer < Packages.Length; layer++)
            {
                int count = 0;
                while (count < CapacityPerLayer && Packages[layer][count].Amount != 0)
                {
                    count++;
                }

                Counts[layer] = count;
            }
        }

        public bool TryStore(int layer, CargoPackage<TItem> package)
        {
            if (IsFull(layer) || package.Amount == 0)
            {
                return false;
            }

            Packages[layer][Counts[layer]] = package;
            Counts[layer]++;
            return true;
        }

        /// The oldest package on this layer, if there is one.
        public bool TryPeek(int layer, out CargoPackage<TItem> package)
        {
            if (Counts[layer] == 0)
            {
                package = default;
                return false;
            }

            package = Packages[layer][0];
            return true;
        }

        /// Drop the oldest package on this layer and close the gap, keeping the queue compacted.
        public void RemoveHead(int layer)
        {
            int count = Counts[layer];
            if (count == 0)
            {
                return;
            }

            CargoPackage<TItem>[] queue = Packages[layer];
            for (int slot = 0; slot < count - 1; slot++)
            {
                queue[slot] = queue[slot + 1];
            }

            Counts[layer] = count - 1;
            queue[Counts[layer]] = default;
        }

        public void Clear()
        {
            for (int layer = 0; layer < Packages.Length; layer++)
            {
                Array.Clear(Packages[layer], 0, CapacityPerLayer);
                Counts[layer] = 0;
            }
        }
    }

    /// See ShapeCargoPackagerState for why these are distinct types with their own identifiers.
    [SyncableIdentifier("TrainCargoToolsShapeStoreState")]
    public sealed class ShapeCargoStoreState : CargoStoreState<ShapeId>
    {
    }

    [SyncableIdentifier("TrainCargoToolsFluidStoreState")]
    public sealed class FluidCargoStoreState : CargoStoreState<FluidId>
    {
    }
}
