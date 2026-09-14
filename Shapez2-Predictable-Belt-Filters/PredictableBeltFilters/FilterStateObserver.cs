using System;
using System.Collections.Generic;
using Core.Events;
using Game.Core.Map.Simulation;

namespace QuinnBast.Shapez2.PredictableBeltFilters
{
    /// Watches belt filters on the *real* simulation side and records where their state lives.
    ///
    /// An observer rather than a tenant: tenants claim a building, and the belt filter already has
    /// one (`AtomicStatefulBuildingSimulationSystem&lt;BeltFilterSimulation,
    /// BeltFilterSimulationState&gt;`). Observers are additive, so this changes nothing about how
    /// the game simulates a filter - it only notes the container.
    ///
    /// Specialized rather than generic so the simulator only calls it for belt filters, via
    /// `SpecializedBuildingObserverSystemsByType`, instead of on every building on the map.
    public class FilterStateObserver : ISpecializedBuildingObserverSimulationSystem,
        IBuildingObserverSimulationSystem, ISimulationSystem, IDisposable
    {
        private readonly FilterStateRegistry Registry;

        private readonly BuildingDefinitionId[] Filters;

        /// This system creates no simulations of its own, so both events stay empty. They exist
        /// only because ISimulationSystem requires them.
        private readonly MultiRegisterEvent<IConnectableSimulation> Created =
            new MultiRegisterEvent<IConnectableSimulation>();

        private readonly MultiRegisterEvent<IConnectableSimulation> Destroyed =
            new MultiRegisterEvent<IConnectableSimulation>();

        public IEvent<IConnectableSimulation> OnSimulationCreated => Created;

        public IEvent<IConnectableSimulation> OnBeforeSimulationDestroyed => Destroyed;

        public IEnumerable<IConnectableSimulation> ConnectableSimulations =>
            Array.Empty<IConnectableSimulation>();

        public IEnumerable<BuildingDefinitionId> SpecializedBuildings => Filters;

        public FilterStateObserver(FilterStateRegistry registry, BuildingDefinitionId[] filters)
        {
            Registry = registry;
            Filters = filters;
        }

        public void BuildingWasAdded(in BuildingInstance building, IReadOnlyMapLayout layout)
        {
            // The container, never the state: Simulator.RevealBuilding runs observers before
            // OfferBuilding, so the state sitting here now is the one the tenant system is about
            // to replace via SimulationStateContainer.New<T>().
            Registry.Record(building.Transform.Position, building.State);
        }

        public void BuildingWillBeRemoved(in BuildingInstance building, IReadOnlyMapLayout layout)
        {
            Registry.Forget(building.Transform.Position);
        }

        public void Dispose()
        {
            Created.Dispose();
            Destroyed.Dispose();
        }
    }
}
