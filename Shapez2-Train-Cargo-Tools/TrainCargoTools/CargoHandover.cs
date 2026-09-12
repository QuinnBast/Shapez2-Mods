using System.Diagnostics.CodeAnalysis;
using Game.Content.Features.Belts;
using Game.Content.Features.Fluids;
using Game.Content.Features.SpacePaths;
using Game.Core.Belts.BeltPath;
using Game.Core.Simulation;
using Game.Core.Trains;

namespace TrainCargoTools
{
    /// A cargo belt's lane, which is a `FastBeltPathLane` and nothing else.
    ///
    /// The type exists purely to be recognisable. A cargo belt and an ordinary space belt build
    /// their lanes from the same class, so once a package is in flight there is no way to ask
    /// "may this receiver hold cargo" - and the answer matters, because `CanAcceptItem` on a
    /// vanilla lane says yes to anything. See CargoHandover.
    ///
    /// Nothing is overridden. An earlier attempt at a different problem subclassed this class and
    /// re-implemented `IItemReceiver`, which worked but leant on interface-map rules for
    /// re-implemented interfaces; a subclass that adds no members cannot change dispatch at all.
    /// Marks one of this mod's own receivers as something a cargo package may be handed to.
    ///
    /// An interface rather than a type list because these are the classes this mod owns, so the
    /// declaration belongs on them; the game's classes have to be named from outside, and are.
    public interface ICargoPackageSink
    {
    }

    public sealed class CargoBeltLane : FastBeltPathLane
    {
        public CargoBeltLane(IBeltSpeed beltSpeed, FastBeltPathLaneState state)
            : base(beltSpeed, state)
        {
        }
    }

    /// Where a cargo package is allowed to go, and the wrapper that enforces it.
    ///
    /// A cargo belt's output connector is belt-tagged, because it has to be: a shape train
    /// station's input is a plain `SpaceBeltInputConnector` and there is no other way to join
    /// one. The same tag is what an ordinary space belt uses, so a cargo belt will happily
    /// connect to an ordinary belt, a space pipe, or a platform's belt port - and then hand it a
    /// `PackageOnTrack`, which travels away and is **destroyed** somewhere downstream that
    /// expected a `ShapeItem`. Silently losing a player's cargo is the worst outcome available.
    ///
    /// The connection itself cannot be blocked. `ItemOutputChunkConnector&lt;TItem&gt;.CanConnect`
    /// matches on the item type its connector maps to, and `ConnectableIslandSimulation` derives
    /// that from the connector class - `SpaceBeltOutputConnector` to `ShapeItem`, full stop. A
    /// train station and an ordinary belt present the *same* connector class, so no type-level
    /// rule can accept one and refuse the other.
    ///
    /// So refuse the hand-over instead. `FastBeltPathLane` asks `NextLane.CanAcceptItem` before
    /// passing anything on, so a receiver that answers no simply leaves the package where it is
    /// and the line backs up - which is the game's own signal for "this does not work", and
    /// costs nothing. Nothing is destroyed.
    ///
    /// The allow-list has to name what really holds the package, because the thing a sender is
    /// handed is usually a `DummyLane` - a forwarder with no storage, used by stations, by every
    /// machine in this mod, **and** by `NotchInputAdapterSimulation`, which is the platform port.
    /// Stopping at the DummyLane would therefore have allowed the very case that started this.
    /// So follow `NextLane` to whatever actually accepts, and decide there.
    public static class CargoHandover
    {
        /// Whether this receiver may be handed this item.
        ///
        /// Only cargo is restricted. Loose items pass unchanged, which matters for the
        /// unpackager: emitting shapes onto an ordinary belt is its whole job.
        public static bool Allows(IItemReceiver next, IBeltItem item)
        {
            if (!CargoBeltSimulation.IsCargoPackage(item))
            {
                return next != null;
            }

            // A DummyLane holds nothing and forwards, so what it is in front of is the answer.
            // Bounded rather than a bare loop: nothing in the game chains more than one, and a
            // cycle here would hang the simulation rather than fail.
            for (int hops = 0; hops < 4 && next is DummyLane dummy; hops++)
            {
                next = dummy.NextLane;
            }

            return next is CargoBeltLane

                // The store and the unpackager - this mod's own receivers.
                || next is ICargoPackageSink

                // A train station's loader, and this mod's packager, which re-hosts the same
                // class. Both accept a whole package because PackagedCargoStations detours the
                // converter they ask; without that detour neither would.
                || next is TrainBeltToCargoFillingContainer<ShapeId>
                || next is TrainBeltToCargoFillingContainer<FluidId>;
        }

        /// Wraps a provider bundle so everything it sends is checked.
        ///
        /// `ItemOutputChunkConnector.TryConnect` only ever assigns `ProviderBundle.NextBundle`,
        /// so wrapping that one setter catches every downstream an island can acquire - island
        /// to island, and island to a platform notch adapter alike.
        public sealed class GuardedProviderBundle : IItemProviderBundle
        {
            private readonly IItemProviderBundle Inner;

            public GuardedProviderBundle(IItemProviderBundle inner)
            {
                Inner = inner;
            }

            public IItemReceiverBundle NextBundle
            {
                get => Inner.NextBundle;
                set => Inner.NextBundle = value == null ? null : new GuardedReceiverBundle(value);
            }

            public IItemProvider GetSender(short laneIndex, short layerIndex)
            {
                return Inner.GetSender(laneIndex, layerIndex);
            }
        }

        /// The receiving half of the same wrapper, one guard per lane.
        private sealed class GuardedReceiverBundle : IItemReceiverBundle
        {
            private readonly IItemReceiverBundle Inner;
            private readonly GuardedReceiver[] Guards = new GuardedReceiver[12];

            public GuardedReceiverBundle(IItemReceiverBundle inner)
            {
                Inner = inner;
            }

            /// Cached per lane, because `ItemLaneBundle.NextBundle`'s setter calls this once for
            /// each of the twelve lanes and the guard is then held for the life of the
            /// connection - a fresh wrapper each time would be twelve allocations per reconnect
            /// and would break reference equality if anything ever compared them.
            public IItemReceiver GetReceiver(short laneIndex, short layerIndex)
            {
                int index = Bundle.ToArrayIndex(laneIndex, layerIndex);
                return Guards[index] ??= new GuardedReceiver(Inner.GetReceiver(laneIndex, layerIndex));
            }
        }

        /// Forwards everything, and answers no to cargo the receiver behind it cannot hold.
        ///
        /// Only `IItemReceiver` is forwarded, which is all `FastBeltPathLane` uses a NextLane
        /// for - `CanAcceptItem`, `HandOverItem` and `MaxStep_S`, the last through
        /// `MinStepsToEnd_S`. A receiver that is also `IHookableItemReceiver` loses that face
        /// behind the wrapper; nothing dispatches a next-lane hand-over that way, but it is the
        /// assumption this rests on.
        private sealed class GuardedReceiver : IItemReceiver
        {
            private readonly IItemReceiver Inner;

            public GuardedReceiver(IItemReceiver inner)
            {
                Inner = inner;
            }

            public Steps MaxStep_S => Inner.MaxStep_S;

            public Steps FreeStepsAtTheBeginning => Inner.FreeStepsAtTheBeginning;

            public bool CanAcceptItem([DisallowNull] IBeltItem itemToTransfer)
            {
                return Allows(Inner, itemToTransfer) && Inner.CanAcceptItem(itemToTransfer);
            }

            public void HandOverItem([DisallowNull] IBeltItem itemToTransfer, Ticks remainingTicks)
            {
                Inner.HandOverItem(itemToTransfer, remainingTicks);
            }
        }
    }
}
