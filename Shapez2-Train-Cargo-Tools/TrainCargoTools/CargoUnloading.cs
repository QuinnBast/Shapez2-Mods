using System;
using Core.Pooling;
using Game.Content.Features.SpacePaths;
using Game.Core.Simulation;
using Game.Core.Trains;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Makes a wagon unloader hand a **whole package** to a cargo belt instead of loose items.
    ///
    /// A cargo belt used to accept loose shapes and pack them itself (the old `CargoIntake` on
    /// the belt). That was the only way anything could get cargo onto a belt without a packager,
    /// and it had two consequences worth being rid of:
    ///
    ///   - An ordinary space belt or pipe could feed a cargo belt, because the belt could not
    ///     tell one sender from another - `PreAcceptHook` is `Func&lt;IBeltItem, bool&gt;` and
    ///     sees only the item, and a wagon unloader's output connector is the very same
    ///     `SpaceBeltOutputConnector` class an ordinary belt presents.
    ///   - A wagon emptied *instantly*. `TrainCargoToBeltFillingContainer.Update` is
    ///     `while (Peek(...) &amp;&amp; NextLane.CanAcceptItem(...)) HandOverItem(...)`, so it
    ///     drains the entire package inside one update for as long as the receiver keeps saying
    ///     yes - and a belt that was packing into a filling container rather than occupying belt
    ///     slots said yes to all of it.
    ///
    /// Both go away if the unloader offers the package itself. Then the belt can refuse loose
    /// items outright, and one package moves per hand-over like any other belt item.
    ///
    /// **Where this is possible at all.** `TrainCargoUnloaderSimulation&lt;T&gt;` and
    /// `TrainCargoToBeltFillingContainer&lt;T&gt;` are generic, and MonoMod will not hook a
    /// method on a generic type - which is as far as an earlier attempt got, and why DESIGN.md
    /// recorded this as impossible. The seam is one level out: the *factories* that build them,
    /// `ShapeCargoStationSimulationFactory` and `FluidCargoStationSimulationCreator`, are plain
    /// non-generic classes, and their `Produce` overload for the unloader is a plain non-generic
    /// method. Detouring that lets the mod build the unloader with a converter of its own.
    ///
    /// **Why a converter rather than a pump.** The obvious alternative - a per-tick pass that
    /// takes packages out of every unloader - would be writing another simulation's state from
    /// whatever thread happened to run the pass, and `Simulator.StartAsynchronousUpdate` means
    /// simulations run on pool threads. Replacing the converter puts every decision inside the
    /// unloader's own `Update`, on the unloader's own thread, with no shared state at all.
    internal sealed class CargoUnloadConverter<TItem> : ICargoToBeltItemConverter<TItem>
        where TItem : unmanaged, IEquatable<TItem>
    {
        private readonly ICargoToBeltItemConverter<TItem> Loose;

        private readonly Pool<PackageOnTrack<CargoPackage<TItem>>> Packages;

        /// The unloader's outputs, bound after construction because the bundle does not exist
        /// until the simulation this converter was handed to has been built.
        private IItemProviderBundle Outputs;

        /// Last answer, and the receivers it was computed from. A cargo belt's downstream does
        /// not change from tick to tick, but `Peek` is called inside a `while` loop, so the walk
        /// is worth not repeating.
        private IItemReceiver[] LastSeen;

        private bool LastAnswer;

        public CargoUnloadConverter(
            ICargoToBeltItemConverter<TItem> loose,
            Pool<PackageOnTrack<CargoPackage<TItem>>> packages)
        {
            Loose = loose;
            Packages = packages;
        }

        public void Bind(IItemProviderBundle outputs)
        {
            Outputs = outputs;
        }

        /// Offers the whole package when every connected output will take one.
        ///
        /// **Every** output, not any: the caller asks each sender in turn and this converter
        /// cannot tell which one is asking, so a station with one cargo belt and one ordinary
        /// belt on it has to pick a single answer for both. Loose is the safe one - an ordinary
        /// belt handed a package would carry it to something that cannot read it, which is the
        /// failure `CargoHandover` exists to prevent on the other side.
        public bool PeekCargoAsBeltItem(in CargoPackage<TItem> cargoPackage, out IBeltItem beltItem)
        {
            if (!cargoPackage.IsEmpty && OutputsTakePackages())
            {
                beltItem = Wrap(in cargoPackage);
                return true;
            }

            return Loose.PeekCargoAsBeltItem(in cargoPackage, out beltItem);
        }

        /// Takes the package in one piece, leaving the container empty.
        ///
        /// The caller only reaches this after its own `CanAcceptItem` said yes to what `Peek`
        /// offered, so the two must agree about which mode they are in - hence the same test
        /// rather than a remembered flag, which could go stale between the two calls.
        public bool PopCargoIntoBeltItem(ref CargoPackage<TItem> cargoPackage, out IBeltItem beltItem)
        {
            if (!cargoPackage.IsEmpty && OutputsTakePackages())
            {
                beltItem = Wrap(in cargoPackage);
                cargoPackage = default;
                return true;
            }

            return Loose.PopCargoIntoBeltItem(ref cargoPackage, out beltItem);
        }

        private IBeltItem Wrap(in CargoPackage<TItem> cargoPackage)
        {
            PackageOnTrack<CargoPackage<TItem>> wrapped = Packages.Retrieve();
            wrapped.Container = cargoPackage;
            return wrapped;
        }

        private bool OutputsTakePackages()
        {
            if (Outputs == null)
            {
                return false;
            }

            int slot = 0;
            bool connected = false;
            bool all = true;

            IItemReceiver[] seen = LastSeen ?? new IItemReceiver[
                SpacePathConstants.NumLanes * SpacePathConstants.NumLayers];
            bool same = LastSeen != null;

            for (short layer = 0; layer < SpacePathConstants.NumLayers; layer++)
            {
                for (short lane = 0; lane < SpacePathConstants.NumLanes; lane++)
                {
                    IItemReceiver next = Outputs.GetSender(lane, layer)?.NextLane;

                    same &= ReferenceEquals(seen[slot], next);
                    seen[slot++] = next;

                    if (next == null)
                    {
                        continue;
                    }

                    connected = true;
                    all &= CargoHandover.Allows(next, Probe);
                }
            }

            LastSeen = seen;

            if (same)
            {
                return LastAnswer;
            }

            LastAnswer = connected && all;
            return LastAnswer;
        }

        /// A stand-in package, only ever passed to `CargoHandover.Allows` to ask the question
        /// "would a package be accepted here" - never handed to anything.
        private static readonly PackageOnTrack<CargoPackage<TItem>> Probe = new();
    }
}
