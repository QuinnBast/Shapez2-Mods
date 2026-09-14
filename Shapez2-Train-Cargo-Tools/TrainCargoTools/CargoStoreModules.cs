using System;
using System.Collections.Generic;
using Core.Localization;
using Game.Core.Coordinates;
using UnityEngine;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// What the side panel needs to know about a store, without knowing which store it is.
    ///
    /// The two store simulations differ only in item type, and the panel does not care about
    /// item type at all - it wants counts. One small interface keeps the module provider
    /// non-generic, which matters because GetModules only has an ISimulation to type-test.
    public interface ICargoStoreView
    {
        int CapacityPerLayer { get; }

        int CountAt(int layer);
    }

    /// The store's side panel: one fill bar per layer.
    ///
    /// Per layer rather than one total, because the capacity is per layer - three independent
    /// queues of 25. A single bar reading 40/75 would hide the case that actually matters,
    /// which is one layer full and backing up while the other two sit idle.
    ///
    /// The gauge is HUDSidePanelModuleRocketProgress, whose name is a lie: its prefab reference
    /// is HUDSidePanelModulesResources.GenericProgress and its Data takes a header, a colour and
    /// two Func&lt;float&gt;. It is the generic progress bar, and the converter uses it for
    /// rocket progress rather than owning it.
    internal sealed class CargoStoreModules : IIslandModuleDataProvider
    {
        private readonly Sprite Icon;

        public CargoStoreModules(Sprite icon)
        {
            Icon = icon;
        }

        /// Nothing. Deliberately.
        ///
        /// This used to return an InfoText repeating the capacity, which showed up in the panel
        /// underneath the island's own description - which already says the same thing, at the
        /// same length. Two copies of one paragraph is worse than one, so the description keeps
        /// the job and this stays empty until there is something to say that it does not.
        public IEnumerable<IHUDSidePanelModuleData> GetStats()
        {
            return Array.Empty<IHUDSidePanelModuleData>();
        }

        /// Shown on a placed store. Reached through the simulator rather than held, because the
        /// provider outlives any particular island.
        public IEnumerable<IHUDSidePanelModuleData> GetModules(IslandModel island)
        {
            GlobalChunkCoordinate position = island.Position;

            if (!island.Map.Simulator.TryFindChunkSimulation(in position, out var chunkSimulation))
            {
                yield break;
            }

            if (!(chunkSimulation.Simulation is ICargoStoreView store))
            {
                yield break;
            }

            for (int layer = 0; layer < 3; layer++)
            {
                // Captured per iteration on purpose: the funcs are called every frame, long
                // after this loop has finished, so they must not close over the loop variable.
                int captured = layer;

                // RawText, not .T(). Binding `(layer + 1).ToString().T()` made the number
                // itself a translation id, so the panel looked it up, failed, and rendered
                // LazyLocalizedText's miss marker - which is why it read "Layer ?1".
                yield return new HUDSidePanelModuleRocketProgress.Data(
                    Icon,
                    "cargo-tools.store.layer".T()
                       .Bind("layer", new RawText((captured + 1).ToString())),
                    LayerColor(captured),
                    () => store.CountAt(captured),
                    () => store.CapacityPerLayer);
            }
        }

        /// Three distinguishable colours, so a glance tells you which layer is backing up.
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
