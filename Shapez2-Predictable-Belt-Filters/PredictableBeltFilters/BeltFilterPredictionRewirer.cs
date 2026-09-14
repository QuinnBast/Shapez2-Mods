using System.Collections.Generic;
using ShapezShifter.Hijack.Predictions;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.PredictableBeltFilters
{
    /// Swaps vanilla's belt filter prediction system for one that knows what a filter does.
    ///
    /// ShapezShifter's PredictionSystemsInterceptor postfixes
    /// BuiltinPredictionSimulationSystems.CreateSimulationSystems and hands every
    /// IPredictionSystemsRewirer the assembled list as a mutable ICollection. That makes removal
    /// possible, which matters: this is a *replacement*, not an addition. Two prediction systems
    /// claiming the same building would both accept it in BuildingIsOffered and both build a
    /// simulation for it.
    public class BeltFilterPredictionRewirer : IPredictionSystemsRewirer
    {
        private readonly FilterStateRegistry Registry;

        private readonly ILogger Logger;

        public BeltFilterPredictionRewirer(FilterStateRegistry registry, ILogger logger)
        {
            Registry = registry;
            Logger = logger;
        }

        public void ModifyPredictionSystems(
            ICollection<ISimulationSystem> simulationSystems, PredictionSystemsDependencies dependencies)
        {
            GameBuildings buildings = dependencies.Mode.Buildings;

            // TryGet, not Get: GameBuildings.GetDefinitionGroup is a raw dictionary indexer, so a
            // game mode without belt filters would throw KeyNotFoundException out of a postfix
            // hook during session construction.
            if (!buildings.TryGetDefinitionGroup(buildings.BeltFilterBuildingId, out IBuildingDefinitionGroup group))
            {
                Logger.Info?.Log(
                    "Predictable Belt Filters: this game mode has no belt filter; leaving predictions alone.");
                return;
            }

            foreach (IBuildingDefinition definition in group.Definitions)
            {
                // Only replace what was actually found. If vanilla's registration ever stops
                // being discoverable - a game update, or another mod that got there first - the
                // safe outcome is to leave the prediction graph exactly as it is rather than to
                // add a second system for the same building.
                if (!TryRemoveExisting(simulationSystems, definition.Id))
                {
                    Logger.Warning?.Log(
                        $"Predictable Belt Filters: found no existing prediction system for {definition.Id}, " +
                        "so none was replaced. Filter predictions stay as the base game computes them.");
                    continue;
                }

                simulationSystems.Add(new BeltFilterPredictionSystem(definition.Id, Registry, dependencies.Logger));

                Logger.Info?.Log(
                    $"Predictable Belt Filters: replaced the prediction system for {definition.Id}.");
            }
        }

        /// Drops every prediction system that claims this building.
        ///
        /// A system announces its building through ISpecializedBuildingTenantSimulationSystem,
        /// which AtomicBuildingSimulationSystem implements explicitly - so the id is reachable by
        /// casting to the interface, with no publicizer and no reflection.
        private static bool TryRemoveExisting(
            ICollection<ISimulationSystem> simulationSystems, BuildingDefinitionId definitionId)
        {
            List<ISimulationSystem> existing = new List<ISimulationSystem>();

            foreach (ISimulationSystem system in simulationSystems)
            {
                if (system is ISpecializedBuildingTenantSimulationSystem specialized
                    && Claims(specialized, definitionId))
                {
                    existing.Add(system);
                }
            }

            foreach (ISimulationSystem system in existing)
            {
                simulationSystems.Remove(system);
            }

            return existing.Count > 0;
        }

        private static bool Claims(
            ISpecializedBuildingTenantSimulationSystem system, BuildingDefinitionId definitionId)
        {
            foreach (BuildingDefinitionId claimed in system.SpecializedBuildings)
            {
                if (claimed == definitionId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
