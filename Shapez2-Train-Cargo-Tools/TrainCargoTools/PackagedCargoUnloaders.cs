using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Core.Pooling;
using Game.Content.Features.Fluids;
using Game.Core.Simulation;
using Game.Core.Trains;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Builds every wagon unloader with a converter that can emit whole packages.
    ///
    /// Two detours, one per item type, on the `Produce` overload that makes an unloader. Each
    /// rebuilds the simulation the factory would have built, with `CargoUnloadConverter` in
    /// place of the stock converter, and binds it to the finished simulation's output bundle.
    ///
    /// **The original is not called.** A prefix that let `orig` run and then swapped the
    /// converter afterwards would be tidier, but the field it lives in -
    /// `TrainCargoToBeltFillingContainer.CargoConverter` - is `readonly`, and publicizing a
    /// field does not make it assignable. Reflecting a value into a readonly field would work
    /// and is exactly the kind of thing that breaks silently on a game update; replaying one
    /// constructor call is easier to check against the decompiled source.
    ///
    /// That does mean this must be kept in step with those two `Produce` bodies. They are four
    /// lines each and have not changed shape across the versions in `decompiled/`, but if
    /// unloaders ever come up wrong after a game update, this is the first place to look.
    internal sealed class PackagedCargoUnloaders : IDisposable
    {
        private readonly List<Hook> Hooks = new();

        // Pool.For is what CargoIntake uses for exactly these wrappers; the pool is shared by
        // every unloader, which is safe because a wrapper is handed straight to a belt lane and
        // never held here.
        private readonly Pool<PackageOnTrack<CargoPackage<ShapeId>>> ShapePackages =
            Pool.For<PackageOnTrack<CargoPackage<ShapeId>>>();

        private readonly Pool<PackageOnTrack<CargoPackage<FluidId>>> FluidPackages =
            Pool.For<PackageOnTrack<CargoPackage<FluidId>>>();

        private delegate void ShapeProduceOrig(
            ShapeCargoStationSimulationFactory self, IslandInstance island,
            out TrainCargoUnloaderSimulation<ShapeId> station,
            out ConnectableIslandSimulation connectable);

        private delegate void ShapeProduceHook(
            ShapeProduceOrig orig, ShapeCargoStationSimulationFactory self, IslandInstance island,
            out TrainCargoUnloaderSimulation<ShapeId> station,
            out ConnectableIslandSimulation connectable);

        private delegate void FluidProduceOrig(
            FluidCargoStationSimulationCreator self, IslandInstance island,
            out TrainCargoUnloaderSimulation<FluidId> station,
            out ConnectableIslandSimulation connectable);

        private delegate void FluidProduceHook(
            FluidProduceOrig orig, FluidCargoStationSimulationCreator self, IslandInstance island,
            out TrainCargoUnloaderSimulation<FluidId> station,
            out ConnectableIslandSimulation connectable);

        public PackagedCargoUnloaders(ILogger logger)
        {
            try
            {
                Add(Produce(typeof(ShapeCargoStationSimulationFactory),
                        typeof(TrainCargoUnloaderSimulation<ShapeId>)),
                    new ShapeProduceHook(ProduceShape));

                Add(Produce(typeof(FluidCargoStationSimulationCreator),
                        typeof(TrainCargoUnloaderSimulation<FluidId>)),
                    new FluidProduceHook(ProduceFluid));
            }
            catch
            {
                // Half of this applied is worse than none: shape unloaders would emit packages
                // while fluid ones emitted loose items into a belt that now refuses them, which
                // reads as one kind of cargo silently not working.
                Dispose();
                throw;
            }

            logger.Info?.Log($"Wagon unloaders now emit cargo packages ({Hooks.Count} hooks).");
        }

        public void Dispose()
        {
            foreach (Hook hook in Hooks)
            {
                hook.Dispose();
            }

            Hooks.Clear();
        }

        /// The `Produce` overload whose `out` station parameter is the given type.
        ///
        /// Three overloads differ only by that parameter - loader, unloader and transferrer -
        /// so `GetMethod(name)` is ambiguous and would throw.
        private static MethodBase Produce(Type factory, Type station)
        {
            return factory
               .GetMethods(BindingFlags.Public | BindingFlags.Instance)
               .Single(m => m.Name == "Produce"
                    && m.GetParameters().Any(p => p.ParameterType == station.MakeByRefType()));
        }

        private void Add(MethodBase target, Delegate replacement)
        {
            if (target == null)
            {
                throw new InvalidOperationException(
                    "Train cargo tools: a station factory's Produce could not be found, so wagon "
                    + "unloaders cannot be taught to emit packages.");
            }

            Hooks.Add(new Hook(target, replacement));
        }

        private void ProduceShape(
            ShapeProduceOrig orig, ShapeCargoStationSimulationFactory self, IslandInstance island,
            out TrainCargoUnloaderSimulation<ShapeId> station,
            out ConnectableIslandSimulation connectable)
        {
            CargoUnloadConverter<ShapeId> converter = new(
                new ShapeCargoToBeltItemConverter(self.ShapeRegistry), ShapePackages);

            station = new TrainCargoUnloaderSimulation<ShapeId>(
                new TrainCargoExchangerState<ShapeId>(self.Config.TrainExchangerTrackSize),
                (IExchangeLayerFilterConfig)island.Configuration,
                self.Config.MaxUnloaderContainersOnTrack,
                self.BridgeSpeed,
                converter);

            converter.Bind(station.GetItemProviderBundle(0));
            connectable = new ConnectableIslandSimulation(island, station);
        }

        private void ProduceFluid(
            FluidProduceOrig orig, FluidCargoStationSimulationCreator self, IslandInstance island,
            out TrainCargoUnloaderSimulation<FluidId> station,
            out ConnectableIslandSimulation connectable)
        {
            // 60 litres is vanilla's own blob size, copied from the line this replaces. A
            // packager already carries the same literal for the same reason - a station and this
            // mod have to agree about what one unit of fluid cargo is worth.
            CargoUnloadConverter<FluidId> converter = new(
                new FluidCargoToBeltItemConverter(
                    self.FluidRegistry, self.FluidPackageItemSolver, FluidUnit.FromLiters(60)),
                FluidPackages);

            station = new TrainCargoUnloaderSimulation<FluidId>(
                new TrainCargoExchangerState<FluidId>(self.Config.TrainExchangerTrackSize),
                (IExchangeLayerFilterConfig)island.Configuration,
                self.Config.MaxUnloaderContainersOnTrack,
                self.BridgeSpeed,
                converter);

            converter.Bind(station.GetItemProviderBundle(0));
            connectable = new ConnectableIslandSimulation(island, station);
        }
    }
}
