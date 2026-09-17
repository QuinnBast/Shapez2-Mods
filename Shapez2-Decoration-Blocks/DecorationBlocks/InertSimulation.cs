using System.Collections.Generic;
using Core.Factory;
using Game.Core.Simulation;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// A simulation that does nothing, because the builder chain will not let a building have none.
///
/// The game itself is perfectly happy with a building that has no simulation - that is what
/// vanilla decorations are. Simulator.OfferBuilding ends with
/// `if (!SpecializedBuildingTenantSystemsByType.TryGetValue(id, out var value)) return;`, so a
/// definition no system claims is simply ignored, with no error and no cost.
///
/// Shifter is the part that insists. AtomicBuildingExtender.Build dereferences its simulation
/// branch unconditionally - `LazySimulationExtender.ContinueAfter(rewirerChainLink)` - while
/// the prediction branch beside it is null-checked. Omitting WithSimulation therefore does not
/// give a decoration, it gives a NullReferenceException at mod load.
///
/// So: the smallest thing that satisfies it. Implementing ISimulation and nothing else means
/// ConnectableBuildingSimulation's constructor finds no IItemSimulation, no fluid simulation
/// and no signal simulation to bind, and builds a connector list of length zero. The system is
/// not an IUpdateableSimulationSystem either, so nothing ticks. What remains is one dictionary
/// entry per placed block inside AtomicBuildingSimulationSystem, which is the price of not
/// reimplementing Shifter's chain.
///
/// The stateless overload is deliberate. WithSimulation&lt;TSimulation, TState, TConfig&gt; - the one
/// the chain's interfaces expose - drags in a serialised state class and a BuffablesExtender
/// for the configuration, neither of which a cube has any use for. The single-generic overload
/// has neither, and it exists on the concrete AtomicBuildingExtender even though no interface
/// in the chain returns it; see DecorationRegistrar for the cast that reaches it.
public sealed class InertSimulation : ISimulation
{
    /// One instance for every block on the map. It has no fields, nothing mutates it, and
    /// AtomicBuildingSimulationSystem only disposes a simulation that is IDisposable - which
    /// this deliberately is not. Sharing takes the per-block allocation to zero.
    public static readonly InertSimulation Shared = new InertSimulation();

    private InertSimulation() { }
}

internal sealed class InertSimulationFactory : IFactory<InertSimulation>
{
    public InertSimulation Produce()
    {
        return InertSimulation.Shared;
    }
}

internal sealed class InertSimulationFactoryBuilder : IBuildingSimulationFactoryBuilder<InertSimulation>
{
    private static readonly InertSimulationFactory Factory = new InertSimulationFactory();

    public IFactory<InertSimulation> BuildFactory(SimulationSystemsDependencies dependencies)
    {
        return Factory;
    }
}

/// An empty side panel. A block selected in game should show its name and nothing else - no
/// throughput row, no efficiency bar - which is exactly what DecorationBuildingModuleDataProvider
/// does for the game's own decorations.
internal sealed class NoBuildingModules : IBuildingModules
{
    public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IBuildingDefinition definition)
    {
        yield break;
    }

    public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IMapModel map, BuildingModel building)
    {
        yield break;
    }
}
