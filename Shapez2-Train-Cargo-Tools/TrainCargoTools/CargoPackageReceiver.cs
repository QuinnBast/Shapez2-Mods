using System;
using Core.Pooling;
using Game.Core.Simulation;
using Game.Core.Trains;

namespace TrainCargoTools
{
    /// Takes a cargo package off a belt and drops it into a filling container's state.
    ///
    /// The one piece the game does not already provide. Unloading at a station starts from a
    /// train, so TrainCargoToBeltFillingContainer has a LoadPackage method and no receiver side -
    /// nothing in vanilla ever accepts a package that arrived on a belt. This is that missing
    /// half: it writes into the same TrainCargoFillingContainerState the filling container reads
    /// from, so the two together are a complete unpackager.
    ///
    /// Accepting only while the state is empty is what makes an unpackager back-pressure
    /// correctly: the belt behind it holds packages until the current one has been fully drained
    /// onto the output.
    ///
    /// It also takes **loose** items and passes them straight through to the output lanes, which
    /// is what lets a train unloader dock against an unpackager with no cargo belt in between. A
    /// station's unloader offers loose shapes or fluid and cannot be told to offer anything else
    /// - the sender-side classes are all generic types, which MonoMod will not hook, so every
    /// accommodation in this mod is made on the receiving side. See CargoIntake, which is the
    /// same fix for the belt.
    ///
    /// Pass-through rather than pack-then-unpack, which is what reusing CargoIntake here would
    /// have given: an unpackager fed loose items has nothing to unpack, and routing them through
    /// a package first would hold a partial load hostage until the train happened to complete it.
    internal sealed class CargoPackageReceiver<TItem> : IItemReceiver, ICargoPackageSink
        where TItem : unmanaged, IEquatable<TItem>
    {
        private readonly TrainCargoFillingContainerState<TItem> State;
        private readonly Pool<PackageOnTrack<CargoPackage<TItem>>> PackagePool;

        /// This layer's output senders, so a loose item can be handed on without going through a
        /// package. The same objects the layer's TrainCargoToBeltFillingContainer drains onto.
        private readonly IItemProvider[] Outputs;

        /// The value the game's own filling container reports. A receiver that is not a lane still
        /// has to tell the belt behind it how far items may advance.
        public Steps MaxStep_S => LaneConstants.ItemSpacingHalf * 12;

        public CargoPackageReceiver(
            TrainCargoFillingContainerState<TItem> state,
            Pool<PackageOnTrack<CargoPackage<TItem>>> packagePool,
            IItemProvider[] outputs)
        {
            State = state;
            PackagePool = packagePool;
            Outputs = outputs;
        }

        public bool CanAcceptItem(IBeltItem itemToTransfer)
        {
            if (itemToTransfer is PackageOnTrack<CargoPackage<TItem>>)
            {
                return State.Package.IsEmpty;
            }

            // A loose item only while nothing is being unpacked, so the two sources cannot
            // interleave and hand the output belt a shuffled stream.
            return State.Package.IsEmpty && FindOutputFor(itemToTransfer) != null;
        }

        public void HandOverItem(IBeltItem itemToTransfer, Ticks remainingTicks)
        {
            if (!(itemToTransfer is PackageOnTrack<CargoPackage<TItem>> wrapper))
            {
                // Straight out the other side. FindOutputFor cannot come back null here: the
                // lane asked CanAcceptItem in the same tick and nothing else runs in between.
                FindOutputFor(itemToTransfer)?.HandOverItem(itemToTransfer, remainingTicks);
                return;
            }

            // Copy before returning: Pool.Return clears the wrapper's Container.
            State.Package = wrapper.Container;
            PackagePool.Return(wrapper);
        }

        /// The first output that will take this item, or null.
        ///
        /// Whether the item is the right *kind* is left entirely to the lane downstream - a
        /// space belt lane takes shapes and a pipe lane takes fluid packages, and asking them is
        /// both simpler and more correct than type-testing here against TItem.
        private IItemReceiver FindOutputFor(IBeltItem item)
        {
            for (int lane = 0; lane < Outputs.Length; lane++)
            {
                IItemReceiver next = Outputs[lane].NextLane;
                if (next != null && next.CanAcceptItem(item))
                {
                    return next;
                }
            }

            return null;
        }
    }
}
