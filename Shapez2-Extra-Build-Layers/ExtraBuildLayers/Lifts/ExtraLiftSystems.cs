using System;
using System.Collections.Generic;
using System.Linq;
using Game.Content.BuildingPath.Simulation;
using Game.Core.Simulation;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Gives every tall lift a simulation, one system per definition.
///
/// That is vanilla's own shape - <c>BuiltinSimulationSystems.CreateBeltLiftSystems</c> yields an
/// <c>AtomicStatefulBuildingSimulationSystem</c> per lift definition rather than one for the
/// group - so this is the same loop over a longer list. The span lives in the factory because
/// the state cannot hold it: <c>New&lt;TState&gt;()</c> gives the state a parameterless
/// constructor and no opportunity to learn its height.
///
/// **Vanilla's two-layer system has to be removed first, or ours never runs.**
/// <c>CreateBeltLiftSystems</c> does not list lift ids - it walks
/// <c>GetDefinitionGroup(Lift2LayerDefinitionGroupId).Definitions</c>, and the tall lifts are
/// registered into that group (<c>AddInternalVariant</c>, which is what lets the path placer
/// choose them). So vanilla yields a <c>BeltLift2LayerSimulation</c> system for every tall lift
/// too, ahead of ours, and the building goes to the first system that claims its id. Every tall
/// lift then ran as a two-layer one: items climbed two floors and were handed straight to the
/// belt at the top, and <c>ExtraLiftSimulationRenderer</c> - bound to <c>ExtraLiftSimulation</c> -
/// never received one. The tell was a "renderer constructed" diagnostic with no "produced" line.
///
/// A save made while that was the case stored vanilla's state for those lifts. Loading it,
/// <c>PolymorphicSerializer</c> logs "Mismatching … type" and skips the blob, so such a lift
/// comes back empty - the items that were inside it at save time - and otherwise works.
///
/// A building with no system is not an error, incidentally - <c>Simulator.OfferBuilding</c>
/// simply returns when nothing claims the id - so a failure here produces lifts that place and
/// do nothing rather than a crash. Worth knowing when reading the log: silence is the symptom.
/// </summary>
internal class ExtraLiftSystems : ISimulationSystemsRewirer
{
    private readonly ILogger Logger;

    public ExtraLiftSystems(ILogger logger)
    {
        Logger = logger;
    }

    public void ModifySimulationSystems(
        ICollection<ISimulationSystem> simulationSystems, SimulationSystemsDependencies dependencies)
    {
        if (ExtraLiftBuildings.CreatedBySpan.Count == 0)
        {
            return;
        }

        try
        {
            // The same speed vanilla's lifts run at, taken from the forward belt's configuration
            // exactly as BuiltinSimulationSystems does. Reading it from the belt rather than
            // hardcoding one keeps tall lifts in step with belt speed research.
            BeltSpeed beltSpeed = dependencies.Mode.Buildings.ForwardBelt
                .ConfigAs<IConveyorConfiguration>().ConveyorSpeed;

            HashSet<BuildingDefinitionId> ours = new HashSet<BuildingDefinitionId>(
                ExtraLiftBuildings.CreatedBySpan.Values.SelectMany(ids => ids));

            // Snapshot: removing from the collection while enumerating it throws.
            List<ISimulationSystem> claimants = simulationSystems
                .Where(system => system is ISpecializedBuildingTenantSimulationSystem specialized &&
                                 specialized.SpecializedBuildings.Any(ours.Contains))
                .ToList();

            foreach (ISimulationSystem claimant in claimants)
            {
                simulationSystems.Remove(claimant);
                (claimant as IDisposable)?.Dispose();
            }

            Logger.Info?.Log(
                "Extra Build Layers: removed " + claimants.Count + " stock simulation system(s) that claimed tall lifts" +
                (claimants.Count > 0 ? " (" + claimants[0].GetType().Name + ")." : "."));

            int added = 0;
            foreach (KeyValuePair<int, List<BuildingDefinitionId>> entry in ExtraLiftBuildings.CreatedBySpan)
            {
                // A lift spanning n layers has n vertical lanes between its input and output.
                ExtraLiftSimulationFactory factory = new ExtraLiftSimulationFactory(beltSpeed, entry.Key);

                foreach (BuildingDefinitionId id in entry.Value)
                {
                    simulationSystems.Add(
                        new AtomicStatefulBuildingSimulationSystem<ExtraLiftSimulation, ExtraLiftSimulationState>(
                            factory, id, Logger));
                    added++;
                }
            }

            Logger.Info?.Log("Extra Build Layers: registered " + added + " tall lift simulation system(s).");
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            Logger.Error?.Log(
                "Extra Build Layers: could not register tall lift simulations; tall lifts will place " +
                "but move nothing.");
        }
    }
}
