using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Content.Features.Belts;
using Game.Content.Features.Fluids;
using Game.Core.Trains;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Teaches every train station, shape and fluid alike, to eat cargo packages as well as
    /// loose items.
    ///
    /// A station packs arriving items into a CargoPackage and hands the full package to a train.
    /// Fed a package that is already packed, it should just take it - the work is done. It
    /// refuses, because its gate is `item is ShapeItem` (or `item is FluidPackageItem`).
    ///
    /// Five detours. Two per item type on the converter, so the station's own HandOverItem still
    /// runs and still does its bookkeeping - in particular LastTimeMatchingItemWasReceived,
    /// which IsLayerActive reads to tell the train scheduler a layer is alive. Then one shared
    /// detour on DummyLane that closes a hole which would otherwise throw.
    ///
    /// This is global on purpose: there is no separate cargo loader building, every station
    /// gains the ability. A station fed a packed line therefore ingests at PackageSize times the
    /// loose rate, which is the point of packing but is a real balance change.
    internal sealed class PackagedCargoStations : IDisposable
    {
        private readonly List<Hook> Hooks = new();

        // MonoMod needs a delegate whose first parameter is the original, then the instance,
        // then the real arguments. `out` parameters rule out the Expression-tree based helpers
        // in DetourHelper, so these are declared by hand.
        private delegate bool MatchesOrig(ShapeBeltItemToCargoConverter self, IBeltItem item);

        private delegate bool MatchesHook(MatchesOrig orig, ShapeBeltItemToCargoConverter self, IBeltItem item);

        private delegate bool ConvertOrig(
            ShapeBeltItemToCargoConverter self, IBeltItem beltItem, out ShapeId item, out int amount);

        private delegate bool ConvertHook(
            ConvertOrig orig, ShapeBeltItemToCargoConverter self, IBeltItem beltItem,
            out ShapeId item, out int amount);

        private delegate bool FluidMatchesOrig(FluidBeltItemToCargoConverter self, IBeltItem item);

        private delegate bool FluidMatchesHook(
            FluidMatchesOrig orig, FluidBeltItemToCargoConverter self, IBeltItem item);

        private delegate bool FluidConvertOrig(
            FluidBeltItemToCargoConverter self, IBeltItem beltItem, out FluidId item, out int amount);

        private delegate bool FluidConvertHook(
            FluidConvertOrig orig, FluidBeltItemToCargoConverter self, IBeltItem beltItem,
            out FluidId item, out int amount);

        private delegate bool LaneAcceptOrig(DummyLane self, IBeltItem item);

        private delegate bool LaneAcceptHook(LaneAcceptOrig orig, DummyLane self, IBeltItem item);

        public PackagedCargoStations(ILogger logger)
        {
            try
            {
                // Shape stations.
                Add(typeof(ShapeBeltItemToCargoConverter)
                       .GetMethod(nameof(ShapeBeltItemToCargoConverter.BeltItemTypeMatchesCargoItemType)),
                    new MatchesHook(Matches));

                Add(typeof(ShapeBeltItemToCargoConverter)
                       .GetMethod(nameof(ShapeBeltItemToCargoConverter.TryConvertBeltItemToCargoItem)),
                    new ConvertHook(Convert));

                // Fluid stations. Deliberately spelled out twice rather than made generic: these
                // are the parts of the mod the compiler cannot check, so being able to read
                // exactly what is hooked matters more than the duplication costs.
                Add(typeof(FluidBeltItemToCargoConverter)
                       .GetMethod(nameof(FluidBeltItemToCargoConverter.BeltItemTypeMatchesCargoItemType)),
                    new FluidMatchesHook(FluidMatches));

                Add(typeof(FluidBeltItemToCargoConverter)
                       .GetMethod(nameof(FluidBeltItemToCargoConverter.TryConvertBeltItemToCargoItem)),
                    new FluidConvertHook(FluidConvert));

                // And the guard, once, for both.
                Add(typeof(DummyLane).GetMethod(nameof(DummyLane.CanAcceptItem)),
                    new LaneAcceptHook(LaneCanAccept));
            }
            catch
            {
                // Never leave a partial set applied. The converter detours without the guard is
                // the one combination that is worse than doing nothing: stations would take
                // packages and then throw on a half-filled layer.
                Dispose();
                throw;
            }

            logger.Info?.Log($"Train stations now accept cargo packages ({Hooks.Count} hooks).");
        }

        private void Add(MethodBase target, Delegate replacement)
        {
            if (target == null)
            {
                throw new InvalidOperationException(
                    "Train cargo tools: a station method to hook was not found. The game's cargo " +
                    "classes have changed shape and PackagedCargoStations needs revisiting.");
            }

            Hooks.Add(new Hook(target, replacement));
        }

        /// A packed package is a shape as far as a station is concerned.
        private static bool Matches(MatchesOrig orig, ShapeBeltItemToCargoConverter self, IBeltItem item)
        {
            return item is PackageOnTrack<CargoPackage<ShapeId>> || orig(self, item);
        }

        /// Unwrap rather than convert: the package already holds a ShapeId and a count.
        ///
        /// Reporting the real Amount is what makes this worth doing - the station's TryGive adds
        /// the whole package in one hand-over instead of one shape at a time.
        private static bool Convert(
            ConvertOrig orig, ShapeBeltItemToCargoConverter self, IBeltItem beltItem,
            out ShapeId item, out int amount)
        {
            if (beltItem is PackageOnTrack<CargoPackage<ShapeId>> packed)
            {
                item = packed.Container.Item;
                amount = packed.Container.Amount;
                return amount > 0;
            }

            return orig(self, beltItem, out item, out amount);
        }

        /// The fluid mirror of Matches.
        private static bool FluidMatches(
            FluidMatchesOrig orig, FluidBeltItemToCargoConverter self, IBeltItem item)
        {
            return item is PackageOnTrack<CargoPackage<FluidId>> || orig(self, item);
        }

        /// The fluid mirror of Convert.
        private static bool FluidConvert(
            FluidConvertOrig orig, FluidBeltItemToCargoConverter self, IBeltItem beltItem,
            out FluidId item, out int amount)
        {
            if (beltItem is PackageOnTrack<CargoPackage<FluidId>> packed)
            {
                item = packed.Container.Item;
                amount = packed.Container.Amount;
                return amount > 0;
            }

            return orig(self, beltItem, out item, out amount);
        }

        /// The hole the converter detours cannot close on their own.
        ///
        /// TrainBeltToCargoFillingContainer.HandOverItem throws
        /// "Expected to give all when trying to give {amount}" when TryGive cannot absorb the
        /// whole amount, and TryGive clamps to PackageSize - Package.Amount. Vanilla never trips
        /// this because a loose item is always amount 1 and CanAcceptItem has already checked the
        /// container is not full. A full package is amount PackageSize, so it only fits into an
        /// empty container - and a layer holding four loose shapes that then received a package
        /// would throw. Requiring an empty container is both necessary and sufficient, because a
        /// package is full by construction: a packager and a station both only emit at PackageSize.
        ///
        /// The guard belongs on the filling container's own CanAcceptItem, and that is where it
        /// started, but **MonoMod refuses to hook any method whose declaring type is generic** -
        /// Hook.CheckSupported throws "Source method is generic, generic hooks are not supported",
        /// and TrainBeltToCargoFillingContainer&lt;T&gt; is generic. Being instantiated over a
        /// struct makes no difference.
        ///
        /// DummyLane is the non-generic choke point that every item passes through on the way in:
        /// a station's input bundle is a bundle of DummyLanes whose NextLane is the layer's
        /// filling container, so upstream belts ask this method, not the container, first. One
        /// hook here therefore covers shape and fluid stations both - and covers this mod's own
        /// packagers too, which are wired the same way and could otherwise throw the same throw.
        ///
        /// The type test runs first and fails immediately for ordinary items, which is the hot
        /// path: a loose shape costs two failed isinst checks and then the original.
        private static bool LaneCanAccept(LaneAcceptOrig orig, DummyLane self, IBeltItem item)
        {
            if (item is PackageOnTrack<CargoPackage<ShapeId>>)
            {
                if (self.NextLane is TrainBeltToCargoFillingContainer<ShapeId> shapes)
                {
                    return shapes.Package.Amount == 0 && orig(self, item);
                }
            }
            else if (item is PackageOnTrack<CargoPackage<FluidId>>)
            {
                if (self.NextLane is TrainBeltToCargoFillingContainer<FluidId> fluids)
                {
                    return fluids.Package.Amount == 0 && orig(self, item);
                }
            }

            return orig(self, item);
        }

        public void Dispose()
        {
            foreach (Hook hook in Hooks)
            {
                hook.Dispose();
            }

            Hooks.Clear();
        }
    }
}
