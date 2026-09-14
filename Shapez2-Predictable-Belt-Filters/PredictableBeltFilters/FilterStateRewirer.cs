using System.Collections.Generic;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.PredictableBeltFilters
{
    /// Adds the real-side observer that tells the prediction side where a filter's state lives.
    ///
    /// Paired with <see cref="BeltFilterPredictionRewirer"/>, which does the prediction half. They
    /// are separate because the game builds the two system collections separately, through
    /// different interceptors, and the real one is rebuilt on a different schedule from the
    /// prediction one.
    public class FilterStateRewirer : ISimulationSystemsRewirer
    {
        private readonly FilterStateRegistry Registry;

        private readonly ILogger Logger;

        public FilterStateRewirer(FilterStateRegistry registry, ILogger logger)
        {
            Registry = registry;
            Logger = logger;
        }

        public void ModifySimulationSystems(
            ICollection<ISimulationSystem> simulationSystems, SimulationSystemsDependencies dependencies)
        {
            // The real systems being rebuilt means every container recorded so far belongs to a
            // session that is going away.
            Registry.Clear();

            GameBuildings buildings = dependencies.Mode.Buildings;

            if (!buildings.TryGetDefinitionGroup(buildings.BeltFilterBuildingId, out IBuildingDefinitionGroup group))
            {
                return;
            }

            List<BuildingDefinitionId> filters = new List<BuildingDefinitionId>();
            foreach (IBuildingDefinition definition in group.Definitions)
            {
                filters.Add(definition.Id);
            }

            if (filters.Count == 0)
            {
                return;
            }

            simulationSystems.Add(new FilterStateObserver(Registry, filters.ToArray()));

            Logger.Info?.Log(
                $"Predictable Belt Filters: observing {filters.Count} belt filter definitions for live state.");
        }
    }
}
