using Game.Content.Features.Predictions;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.PredictableBeltFilters
{
    /// Creates one <see cref="BeltFilterPredictionSimulation"/> per placed belt filter.
    ///
    /// This subclasses AtomicBuildingSimulationSystem directly rather than vanilla's
    /// AtomicBuildingPredictionSimulationSystem, because that class builds its simulation from an
    /// IFactory&lt;TSimulation&gt; and throws the BuildingInstance away - and the BuildingInstance
    /// is the entire point here. It carries the SimulationStateContainer that the real
    /// BeltFilterSimulation also holds, which is how a prediction gets at the filter's live wire
    /// signal. Going through the factory interface would mean passing null for a factory that is
    /// never called; overriding the one hook that matters is honest about what is happening.
    ///
    /// Everything else - the tile index, the connector wiring, offer and termination, disposal -
    /// comes from the base class unchanged, and ConnectableBuildingPredictionSimulation still does
    /// the connector pairing exactly as it does for vanilla's systems.
    public class BeltFilterPredictionSystem : AtomicBuildingSimulationSystem<ConnectableBuildingPredictionSimulation>
    {
        /// Handed to each simulation so it can find the live state for its tile.
        private readonly FilterStateRegistry Registry;

        public BeltFilterPredictionSystem(
            BuildingDefinitionId buildingDefinitionId, FilterStateRegistry registry, ILogger logger)
            : base(buildingDefinitionId, logger)
        {
            Registry = registry;
        }

        protected override ConnectableBuildingPredictionSimulation CreateConnectableSimulation(
            BuildingInstance building)
        {
            return new ConnectableBuildingPredictionSimulation(
                building, new BeltFilterPredictionSimulation(building, Registry));
        }
    }
}
