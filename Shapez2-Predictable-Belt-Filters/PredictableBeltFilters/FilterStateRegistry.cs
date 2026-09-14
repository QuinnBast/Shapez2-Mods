using System.Collections.Concurrent;
using Game.Core.Coordinates;

namespace QuinnBast.Shapez2.PredictableBeltFilters
{
    /// Maps a belt filter's tile to the state container the *real* simulation uses.
    ///
    /// This exists because the prediction graph cannot be trusted to hand out the right one.
    /// Predictions run over `LazyEventMapLayout`, which queues map edits as `BuildingDescriptor`s
    /// in a `LookupQueue` - a `Dictionary`. `BuildingDescriptor.Equals` and `GetHashCode` compare
    /// definition, transform and configuration, and deliberately ignore `State`. So when a
    /// placement ghost at a tile is removed and the real building at that same tile is added in
    /// one lazy batch, `StoreBuildingAdded` finds the pending removal, treats the two as the same
    /// entity and cancels both. The runner map keeps the *ghost's* BuildingInstance, whose
    /// container is never populated by anything, and the prediction side sees an empty container
    /// for the life of that building.
    ///
    /// The real simulator has no such layer - it runs on the map layout directly - so a building
    /// observer on that side sees the genuine container. That is all this records.
    ///
    /// Containers, not states. `SimulationStateContainer.New&lt;T&gt;()` *replaces* the state, and
    /// `Simulator.RevealBuilding` runs observers *before* `OfferBuilding` creates the real
    /// simulation - so any state read at observation time is the one about to be thrown away. The
    /// container is the stable identity; resolve the state out of it at the point of use.
    ///
    /// Concurrent because the prediction pass may run asynchronously
    /// (`Simulator.StartAsynchronousUpdate`) while the real simulation mutates the map.
    public class FilterStateRegistry
    {
        private readonly ConcurrentDictionary<GlobalTileCoordinate, SimulationStateContainer> Containers =
            new ConcurrentDictionary<GlobalTileCoordinate, SimulationStateContainer>();

        public void Record(in GlobalTileCoordinate position, SimulationStateContainer container)
        {
            Containers[position] = container;
        }

        public void Forget(in GlobalTileCoordinate position)
        {
            Containers.TryRemove(position, out SimulationStateContainer _);
        }

        public bool TryGet(in GlobalTileCoordinate position, out SimulationStateContainer container)
        {
            return Containers.TryGetValue(position, out container);
        }

        /// Called when the real simulation systems are rebuilt, which is the one moment a whole
        /// session's worth of entries is known to be stale.
        public void Clear()
        {
            Containers.Clear();
        }
    }
}
