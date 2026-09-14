using System;
using Core.Pooling;
using Game.Content.Features.Fluids;
using Game.Core.Belts.BeltPath;
using Game.Core.Simulation;
using Game.Core.Trains;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Lets a cargo belt take loose items and package them itself.
    ///
    /// **This is what makes a train unloader work.** A station's unloader decides what to *offer*
    /// its output before it is told what is downstream:
    ///
    /// <code>
    /// while (CargoConverter.PeekCargoAsBeltItem(in State.Package, out beltItem)   // what to offer
    ///        &amp;&amp; itemProvider.NextLane.CanAcceptItem(beltItem))              // asks the platform
    /// </code>
    ///
    /// The platform *is* asked - but one frame too late. `ICargoToBeltItemConverter` is handed
    /// only the package, never the lane, so it cannot offer a package to a cargo belt and loose
    /// shapes to an ordinary one. And the frame in between,
    /// `TrainCargoToBeltFillingContainer&lt;T&gt;.Update`, is on a generic type, which MonoMod
    /// refuses to hook outright. Same for `TrainCargoUnloaderSimulation&lt;T&gt;` and for
    /// `ItemLaneBundle&lt;TLane&gt;.NextBundle`, where an adapter would otherwise go.
    ///
    /// So the fix goes on the receiving side, which is where the *loading* direction was solved
    /// too - and that is the answer to why the two were not symmetric. Loading only needed a
    /// receiver made more permissive, which is safe because it cannot break anything that was
    /// not already offering. Unloading needed an adaptive *sender*, which needs knowledge of the
    /// receiver. Making the receiver permissive again sidesteps the whole problem: the unloader
    /// goes on offering loose items exactly as it always did, and the cargo belt packs them.
    ///
    /// It fixes a second thing recorded as a rough edge for several versions: a cargo belt used
    /// to connect to an ordinary space belt and then silently refuse its shapes. Now it packs
    /// them instead.
    ///
    /// The accumulation is not reimplemented. `TrainBeltToCargoFillingContainer` is the game's
    /// own class and already an `IItemReceiver`, so it is exactly what the packager uses, with
    /// vanilla's serialization and vanilla's rule that an emptied package still remembers its
    /// item until reset.
    ///
    /// One shape container and one fluid container are held side by side rather than making the
    /// belt generic over its item type. A given belt only ever sees one kind - its connector tags
    /// decide that - so one of the two is always idle. That wastes a little state per segment and
    /// buys not having to split `CargoBeltSimulation` in two, along with its state identifier,
    /// its factory and its renderer.
    internal sealed class CargoIntake
    {
        private readonly TrainBeltToCargoFillingContainer<ShapeId> Shapes;
        private readonly TrainBeltToCargoFillingContainer<FluidId> Fluids;

        private readonly ICargoContainerCapacityConfigProvider ShapeCapacity;
        private readonly ICargoContainerCapacityConfigProvider FluidCapacity;

        private readonly Pool<PackageOnTrack<CargoPackage<ShapeId>>> ShapePool;
        private readonly Pool<PackageOnTrack<CargoPackage<FluidId>>> FluidPool;

        public CargoIntake(
            ICargoContainerCapacityConfigProvider shapeCapacity,
            ICargoContainerCapacityConfigProvider fluidCapacity,
            IFluidRegistry fluids,
            TrainCargoFillingContainerState<ShapeId> shapeState,
            TrainCargoFillingContainerState<FluidId> fluidState)
        {
            ShapeCapacity = shapeCapacity;
            FluidCapacity = fluidCapacity;

            Shapes = new TrainBeltToCargoFillingContainer<ShapeId>(
                shapeCapacity, new ShapeBeltItemToCargoConverter(), shapeState);
            Fluids = new TrainBeltToCargoFillingContainer<FluidId>(
                fluidCapacity, new FluidBeltItemToCargoConverter(fluids), fluidState);

            ShapePool = Pool.For<PackageOnTrack<CargoPackage<ShapeId>>>();
            FluidPool = Pool.For<PackageOnTrack<CargoPackage<FluidId>>>();
        }

        /// The carrying lane's `PreAcceptHook`: cargo rides, loose items get packed.
        ///
        /// Note the lane checks it has room *before* consulting this, so a full belt refuses
        /// loose items even when the intake could hold more. That is the right way round - it is
        /// the back-pressure that makes a packed line queue instead of overfilling.
        public bool CanEnter(IBeltItem item)
        {
            return CargoBeltSimulation.IsCargoPackage(item) || CanAccept(item);
        }

        /// The carrying lane's `AcceptHook`.
        ///
        /// `FastBeltPathLane.HandOverItem` calls this and then returns early if `receivedItem`
        /// has been set to null, so a hook can take an item off the belt entirely. That is what
        /// lets a loose shape be swallowed into the filling container instead of riding the belt
        /// as a loose shape - which is the whole trick, and it is the game's own hook API rather
        /// than anything prised open.
        ///
        /// An earlier attempt subclassed `FastBeltPathLane` and re-implemented `IItemReceiver` to
        /// get at the hand-over, because its methods are public but not virtual. That worked but
        /// leant on interface-map rules for re-implemented interfaces, and would have broken
        /// silently had anything dispatched through `IHookableItemReceiver` instead. This needs
        /// none of that.
        public void OnAccept(IItemReceiver receiver, ref IBeltItem receivedItem, ref Ticks remainingTicks)
        {
            if (CargoBeltSimulation.IsCargoPackage(receivedItem))
            {
                return;
            }

            Accept(receivedItem, remainingTicks);

            // Swallowed. Nothing reaches the lane, so the belt never carries a loose item.
            receivedItem = null;
        }

        /// Whether a loose item can be taken for packing right now.
        ///
        /// Only loose items reach here; a cargo package goes straight onto the belt and never
        /// through the intake. Returning false when the package is already full is what makes the
        /// whole thing back-pressure properly: the container fills, the belt has nowhere to put
        /// the finished package, and the station's unloader simply stops being able to hand over.
        public bool CanAccept(IBeltItem item)
        {
            if (item is ShapeItem)
            {
                return Shapes.CanAcceptItem(item);
            }

            if (item is FluidPackageItem)
            {
                return Fluids.CanAcceptItem(item);
            }

            return false;
        }

        public void Accept(IBeltItem item, Ticks remainingTicks)
        {
            if (item is ShapeItem)
            {
                Shapes.HandOverItem(item, remainingTicks);
            }
            else if (item is FluidPackageItem)
            {
                Fluids.HandOverItem(item, remainingTicks);
            }
        }

        /// A finished package, if one is ready, leaving the container reset behind it.
        ///
        /// The reset is deliberately only done once the caller has somewhere to put the package,
        /// which is why this hands the package back rather than pushing it: if the belt is full
        /// the package has to stay put, and resetting the container first would drop it.
        public bool TryTakeReady(out IBeltItem package)
        {
            if (Shapes.Package.IsFull(ShapeCapacity))
            {
                PackageOnTrack<CargoPackage<ShapeId>> wrapped = ShapePool.Retrieve();
                wrapped.Container = Shapes.Package;
                package = wrapped;
                return true;
            }

            if (Fluids.Package.IsFull(FluidCapacity))
            {
                PackageOnTrack<CargoPackage<FluidId>> wrapped = FluidPool.Retrieve();
                wrapped.Container = Fluids.Package;
                package = wrapped;
                return true;
            }

            package = null;
            return false;
        }

        /// Called once the package handed back by TryTakeReady is safely on the belt.
        public void ClearTaken()
        {
            if (Shapes.Package.IsFull(ShapeCapacity))
            {
                Shapes.PrepareNewEmpty();
                return;
            }

            if (Fluids.Package.IsFull(FluidCapacity))
            {
                Fluids.PrepareNewEmpty();
            }
        }

        public void Update(Ticks startTicks, Ticks deltaTicks)
        {
            // Vanilla's own rule, copied from the loader: an emptied package still remembers
            // which item it held and would refuse every other item until it is reset.
            if (Shapes.Package.IsEmpty)
            {
                Shapes.PrepareNewEmpty();
            }

            if (Fluids.Package.IsEmpty)
            {
                Fluids.PrepareNewEmpty();
            }

            Shapes.Update(startTicks, deltaTicks);
            Fluids.Update(startTicks, deltaTicks);
        }

        public void Clear()
        {
            Shapes.PrepareNewEmpty();
            Fluids.PrepareNewEmpty();
        }
    }

}
