using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Game.Content.Features.SpacePaths;
using Game.Core.Map.Simulation;
using Game.Core.Map.Simulation.Clustering;
using Game.Core.Simulation;
using Game.Core.Simulation.Buffers;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.TrainCargoTools;

/// Removes cargo packages that leaked onto vanilla track in an earlier version, once per
/// session, before the first simulation step.
///
/// 0.48.3's `CargoHandover.Allows` admitted every `SplittingItemDistributor`, which is also
/// what a vanilla space belt or pipe splitter receives through, so packages reached
/// vanilla lanes. Fixing the guard stops new leaks but does not remove the old ones: lane
/// contents and port buffers are **saved**, so an affected save loads them straight back.
/// There they throw every tick - `SpaceFluidPortReceiverSimulation.CanAcceptItem` casts to
/// `FluidPackageItem` inside the simulation step, which is the yellow screen - and
/// `SpacePathSimulationRenderer.DrawItems` throws drawing them. The renderer is a generic
/// type, so it cannot be hooked; the packages have to go.
///
/// **When.** `Simulator.StartAsynchronousUpdate` and `SynchronousUpdate` both open with
/// `PrepareUpdate`, which throws if the graph is still updating, and the orchestrator waits
/// on the previous step in `FinalizeLogicUpdate` before starting the next one. So a prefix on
/// either one runs on the main thread with no simulation running, after the save has
/// loaded. Once per `Simulator` is enough: with the guard fixed, nothing creates new leaks.
///
/// **What is removed.** A lane cannot drop one item - `IItemLane` offers `GetItem` and
/// `Clear`, nothing in between - so a vanilla simulation holding a package in a lane is
/// reset with its own `ClearContent`. That is the same reset removing and re-placing the
/// island gives, so the shapes or fluid on that island are lost along with the package.
/// Buffers are plain queues, so there only the packages are taken out.
///
/// Packages that *belong* are skipped: anything in a simulation from this assembly, and
/// any `CargoBeltLane`. No vanilla simulation holds a package in a lane on purpose - a
/// station's input lanes are `DummyLane`s, which hold nothing, and its tracks are not
/// `IItemLane`s.
internal sealed class CargoLeakSweep : IDisposable
{
    private readonly List<Hook> Hooks = new();

    private readonly ILogger Log;

    /// Simulators already swept. Weak, so a finished session's simulator is not kept alive.
    private readonly ConditionalWeakTable<Simulator, object> Swept = new();

    private delegate Task StartOrig(
        Simulator self, Ticks deltaTicks, SimulationUpdateConfiguration config,
        ISimulationUpdateStrategy strategy);

    private delegate Task StartHook(
        StartOrig orig, Simulator self, Ticks deltaTicks, SimulationUpdateConfiguration config,
        ISimulationUpdateStrategy strategy);

    private delegate void SyncOrig(
        Simulator self, Ticks deltaTicks, SimulationUpdateConfiguration config,
        ISimulationUpdateStrategy strategy);

    private delegate void SyncHook(
        SyncOrig orig, Simulator self, Ticks deltaTicks, SimulationUpdateConfiguration config,
        ISimulationUpdateStrategy strategy);

    public CargoLeakSweep(ILogger logger)
    {
        Log = logger;

        try
        {
            Hooks.Add(new Hook(
                Method(nameof(Simulator.StartAsynchronousUpdate)), new StartHook(Start)));
            Hooks.Add(new Hook(
                Method(nameof(Simulator.SynchronousUpdate)), new SyncHook(Sync)));
        }
        catch (Exception exception)
        {
            // A repair for old saves is not worth failing mod loading over. A throw in a mod
            // constructor is not contained by ModLoader.
            Dispose();
            Log.Exception?.LogException(exception);
        }
    }

    public void Dispose()
    {
        foreach (Hook hook in Hooks)
        {
            hook.Dispose();
        }

        Hooks.Clear();
    }

    private static MethodBase Method(string name)
    {
        return typeof(Simulator).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(Simulator), name);
    }

    private Task Start(
        StartOrig orig, Simulator self, Ticks deltaTicks, SimulationUpdateConfiguration config,
        ISimulationUpdateStrategy strategy)
    {
        SweepOnce(self);
        return orig(self, deltaTicks, config, strategy);
    }

    private void Sync(
        SyncOrig orig, Simulator self, Ticks deltaTicks, SimulationUpdateConfiguration config,
        ISimulationUpdateStrategy strategy)
    {
        SweepOnce(self);
        orig(self, deltaTicks, config, strategy);
    }

    private void SweepOnce(Simulator simulator)
    {
        if (Swept.TryGetValue(simulator, out _))
        {
            return;
        }

        Swept.Add(simulator, null);

        // Never let a repair take the simulation step down with it.
        try
        {
            Sweep(simulator);
        }
        catch (Exception exception)
        {
            Log.Exception?.LogException(exception);
        }
    }

    private void Sweep(Simulator simulator)
    {
        Assembly own = typeof(CargoLeakSweep).Assembly;
        PackageFinder finder = new();
        int islandsReset = 0;
        int buffered = 0;

        foreach (ILocalizedSimulation localized in ((ISimulator)simulator).Simulations)
        {
            ISimulation simulation = localized.Simulation;
            if (simulation == null || simulation.GetType().Assembly == own)
            {
                continue;
            }

            // Buffers first and separately: they lose only the packages.
            if (simulation is IBeltSimulationWithBufferOutput output)
            {
                buffered += RemovePackages(output.BufferOutput as SimulationBuffer<IBeltItem>);
            }

            if (simulation is IBeltSimulationWithBufferInput input)
            {
                buffered += RemovePackages(input.BufferInput as SimulationBuffer<IBeltItem>);
            }

            finder.Found = false;
            try
            {
                if (simulation is IItemBundleSimulation bundle)
                {
                    bundle.TraverseLanes(finder);
                }
                else if (simulation is IItemSimulation items)
                {
                    items.TraverseLanes(finder);
                }
            }
            catch (Exception exception)
            {
                // One simulation that cannot be walked should not stop the rest being
                // repaired.
                Log.Exception?.LogException(exception);
                continue;
            }

            if (!finder.Found)
            {
                continue;
            }

            simulation.ClearContent();
            islandsReset++;

            string where = localized.NumOccupiedChunks > 0
                ? localized.GetOccupiedChunk(0).ToString()
                : "unknown position";
            Log.Warning?.Log(
                $"Removed cargo packages from a {simulation.GetType().Name} at {where}. "
                + "They had leaked onto vanilla track through a vanilla splitter in an earlier "
                + "version; its contents were reset.");
        }

        if (islandsReset > 0 || buffered > 0)
        {
            Log.Warning?.Log(
                $"Cleaned leaked cargo: {islandsReset} vanilla islands reset, {buffered} "
                + "packages removed from port buffers.");
        }
    }

    /// Takes the packages out of a buffer's queue and leaves everything else in order.
    private static int RemovePackages(SimulationBuffer<IBeltItem> buffer)
    {
        if (buffer == null)
        {
            return 0;
        }

        Queue<SimulationTimedBufferItem<IBeltItem>> queue = buffer.State.Queue;
        lock (queue)
        {
            int before = queue.Count;
            if (before == 0)
            {
                return 0;
            }

            SimulationTimedBufferItem<IBeltItem>[] items = queue.ToArray();
            queue.Clear();
            foreach (SimulationTimedBufferItem<IBeltItem> item in items)
            {
                if (!CargoBeltSimulation.IsCargoPackage(item.Item))
                {
                    queue.Enqueue(item);
                }
            }

            return before - queue.Count;
        }
    }

    /// Whether any lane outside a cargo belt holds a package.
    private sealed class PackageFinder : IItemLaneTraverser
    {
        public bool Found;

        public void Traverse(IItemLane lane)
        {
            if (Found || lane == null || lane is CargoBeltLane)
            {
                return;
            }

            for (int i = 0; i < lane.ItemCount; i++)
            {
                if (CargoBeltSimulation.IsCargoPackage(lane.GetItem(i)))
                {
                    Found = true;
                    return;
                }
            }
        }
    }
}
