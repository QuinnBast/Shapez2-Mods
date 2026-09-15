using System;
using System.Collections.Generic;
using Core.Localization;
using Game.Core.Coordinates;
using UnityEngine;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// What the side panel needs to know about a packager, without knowing which packager it is.
    ///
    /// The shape and fluid packagers differ in nothing the panel cares about, and GetModules has
    /// only an ISimulation to type-test, so a small non-generic interface is what makes one
    /// provider serve both.
    public interface ICargoPackagerView
    {
        /// Items to a package - `ICargoContainerCapacityConfigProvider.PackageSize`.
        ///
        /// Read rather than hardcoded so a packager always agrees with whatever a station is
        /// making, but it is a **constant** in practice: `ShapePackageSize` is 360 and nothing
        /// buffs it. Wagon-capacity research carries `[BuffInteger("_MaxPackagesPerContainer")]`,
        /// which is how many *packages* fit in a wagon's container - not how many shapes fit in
        /// a package.
        int PackageSize { get; }

        /// Items already in this layer's part-packed package.
        int AmountAt(int layer);
    }

    /// The packager's side panel: one bar per layer, filling towards the next package.
    ///
    /// A packager is the one machine in the mod whose work is invisible. A belt shows its
    /// freight and a store shows its shelves, but a packager sitting on a slow input line looks
    /// exactly like a packager that is jammed - it emits nothing for a long time either way.
    /// The bar is the difference between the two.
    ///
    /// Per layer, because the three layers pack independently and one stalled input line is
    /// precisely the case a single combined bar would hide.
    ///
    /// The gauge is HUDSidePanelModuleRocketProgress, whose name is a lie - it is the generic
    /// current/max bar, and the rocket converter is merely its best-known user. See
    /// CargoStoreModules, which uses it the same way.
    internal sealed class CargoPackagerModules : IIslandModuleDataProvider
    {
        private readonly Sprite Icon;

        public CargoPackagerModules(Sprite icon)
        {
            Icon = icon;
        }

        /// Nothing. The island description already says what a packager does, at the same
        /// length, and two copies of one paragraph is worse than one.
        public IEnumerable<IHUDSidePanelModuleData> GetStats()
        {
            return Array.Empty<IHUDSidePanelModuleData>();
        }

        /// Shown on a placed packager. Reached through the simulator rather than held, because
        /// the provider outlives any particular island.
        public IEnumerable<IHUDSidePanelModuleData> GetModules(IslandModel island)
        {
            GlobalChunkCoordinate position = island.Position;

            if (!island.Map.Simulator.TryFindChunkSimulation(in position, out var chunkSimulation))
            {
                yield break;
            }

            if (!(chunkSimulation.Simulation is ICargoPackagerView packager))
            {
                yield break;
            }

            for (int layer = 0; layer < 3; layer++)
            {
                // Captured per iteration on purpose: the funcs are called every frame, long
                // after this loop has finished, so they must not close over the loop variable.
                int captured = layer;

                // RawText, not .T(). Binding a number through .T() makes the number itself a
                // translation id, which is looked up, missed, and rendered as "?1".
                yield return new HUDSidePanelModuleRocketProgress.Data(
                    Icon,
                    "cargo-tools.packager.layer".T()
                       .Bind("layer", new RawText((captured + 1).ToString())),
                    LayerColor(captured),
                    () => packager.AmountAt(captured),
                    () => packager.PackageSize);
            }
        }

        /// The store's three layer colours, so the same layer reads the same on both panels.
        private static Color LayerColor(int layer)
        {
            switch (layer)
            {
                case 0: return new Color(0.35f, 0.78f, 0.72f);
                case 1: return new Color(0.45f, 0.62f, 0.90f);
                default: return new Color(0.80f, 0.66f, 0.35f);
            }
        }
    }
}
