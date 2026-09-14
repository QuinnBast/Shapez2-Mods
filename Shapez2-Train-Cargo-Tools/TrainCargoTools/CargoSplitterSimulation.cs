using System.Linq;
using Core.Factory;
using Game.Content.AtomicIslands.Mergers;
using Game.Core.Belts.BeltPath;
using Game.Content.AtomicIslands.Splitter;
using Game.Content.Features;
using Game.Content.Features.SpacePaths;
using Game.Core.Simulation;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// A cargo belt that branches: one input, two or three outputs, round-robin between them.
    ///
    /// **Almost none of this is new.** `SpaceSplitterSimulation` is the game's own space belt
    /// splitter - non-generic, with a public constructor - and it already distributes with
    /// `RoundRobinDistributionBehaviour`, which is exactly the behaviour a branching cargo line
    /// wants. So this subclasses it rather than reimplementing a distributor, the same way the
    /// packagers re-host `TrainBeltToCargoFillingContainer`.
    ///
    /// Two things have to change, and both are about cargo rather than about splitting:
    ///
    ///   - **The outputs must refuse loose items.** A vanilla splitter's lanes take anything, so
    ///     without this an ordinary space belt could feed a cargo splitter - the case cargo belts
    ///     themselves now refuse. `BeltPathLane.PreAcceptHook` is public, and `SplittingItemDistributor`
    ///     answers `CanAcceptItem` by asking the behaviour to find a lane that will take the item,
    ///     so refusing on the lanes refuses at the junction too.
    ///   - **The outputs must be guarded.** A splitter output pointed at an ordinary belt would
    ///     hand it a package, which is the loss `CargoHandover` exists to prevent; vanilla's own
    ///     provider bundle has no such guard, so each one is wrapped.
    ///
    /// The state is vanilla's `SpaceSplitterSimulationState` unchanged, under vanilla's own
    /// `SyncableIdentifier("SpaceSplitterState")`. That is deliberate and safe: `PolymorphicSerializer`
    /// walks a set of types, so one type is registered once no matter how many islands use it,
    /// and reusing the type means reusing vanilla's blob format rather than inventing one that
    /// would have to be migrated later.
    ///
    /// `IItemBundleSimulation` is restated in the base list so the explicit implementation below
    /// is legal: C# only allows one for an interface the type itself names, even when a base
    /// class already implements it.
    public sealed class CargoSplitterSimulation : SpaceSplitterSimulation, IItemBundleSimulation
    {
        private readonly CargoHandover.GuardedProviderBundle[] Outgoing;

        public CargoSplitterSimulation(
            SpaceSplitterSimulationState state, ISpaceSplitterConfiguration configuration)
            : base(state, configuration)
        {
            for (short lane = 0; lane < SpacePathConstants.NumLanes; lane++)
            {
                for (short layer = 0; layer < SpacePathConstants.NumLayers; layer++)
                {
                    PathSplitterSimulation splitter = SplitterBundle.GetSimulation(lane, layer);

                    foreach (BeltPathLane output in splitter.OutputLanes)
                    {
                        output.PreAcceptHook = CargoBeltSimulation.IsCargoPackage;
                    }
                }
            }

            CargoJunctionDiagnostics.Built("splitter", NumItemProviderBundles);

            Outgoing = new CargoHandover.GuardedProviderBundle[NumItemProviderBundles];
            for (int output = 0; output < Outgoing.Length; output++)
            {
                Outgoing[output] = new CargoHandover.GuardedProviderBundle(
                    base.GetItemProviderBundle(output));
            }
        }

        /// Explicit, so interface dispatch on this type reaches the guarded bundle while
        /// `base.GetItemProviderBundle` still returns the raw one for the wrapper to hold.
        IItemProviderBundle IItemBundleSimulation.GetItemProviderBundle(int outputIndex)
        {
            return Outgoing[outputIndex];
        }
    }

    /// Builds a cargo splitter with as many outputs as the shape has.
    ///
    /// One factory per output count rather than one per shape: a Y and a left-forward splitter
    /// differ only in where their connectors sit, which is island geometry, not simulation.
    internal sealed class CargoSplitterSimulationFactory
        : IIslandSimulationFactoryBuilder<CargoSplitterSimulation, SpaceSplitterSimulationState,
            SpacePathConfiguration>
    {
        private readonly int Outputs;

        public CargoSplitterSimulationFactory(int outputs)
        {
            Outputs = outputs;
        }

        public IFactory<SpaceSplitterSimulationState, IslandInstance, CargoSplitterSimulation>
            BuildFactory(SimulationSystemsDependencies dependencies, out SpacePathConfiguration config)
        {
            // Same configuration a cargo belt reads, so a splitter runs at belt speed and the
            // same research speeds up both. See CargoBeltSimulationFactory.
            config = dependencies.Mode.Islands.SpaceBelts.First().ConfigAs<SpacePathConfiguration>();

            return new Factory(new SpaceSplitterConfiguration(
                Outputs, new CargoBeltSpeed(config.SpaceConveyorSpeed)));
        }

        private sealed class Factory
            : IFactory<SpaceSplitterSimulationState, IslandInstance, CargoSplitterSimulation>
        {
            private readonly ISpaceSplitterConfiguration Configuration;

            public Factory(ISpaceSplitterConfiguration configuration)
            {
                Configuration = configuration;
            }

            public CargoSplitterSimulation Produce(
                SpaceSplitterSimulationState state, IslandInstance island)
            {
                return new CargoSplitterSimulation(state, Configuration);
            }
        }
    }

    /// The other half of a junction: two or three lines in, one out.
    ///
    /// A branching drag needs both. Pulling a new run *out of* an existing one is a split;
    /// dragging a new run *into* one is a merge, and with only splitters registered the
    /// definition finder picks the nearest match - a splitter, whose outputs sit where the
    /// inputs are needed - so nothing connects and the line backs up at the junction.
    ///
    /// `SpaceMergerSimulation` is vanilla's own, and easier to make cargo-only than the splitter
    /// was: its inputs are real `FastBeltPathLane`s, so they take a `PreAcceptHook` directly
    /// rather than through a distributor.
    public sealed class CargoMergerSimulation : SpaceMergerSimulation, IItemBundleSimulation
    {
        private readonly CargoHandover.GuardedProviderBundle Outgoing;

        public CargoMergerSimulation(
            SpaceMergerSimulationState state, ISpaceMergerConfiguration configuration)
            : base(state, configuration)
        {
            // The input bundles are rebuilt, not adjusted. `SpaceMergerSimulation` fills them
            // with plain `FastBeltPathLane`s, and `CargoHandover.Allows` admits `CargoBeltLane`
            // and not its base class - so a cargo belt would refuse to hand into a merger at
            // all, which is exactly how mergers first shipped: they placed, drew and connected,
            // and the line stopped dead at them.
            //
            // Loosening the guard to accept any `FastBeltPathLane` was the alternative and is
            // much worse: that is the class every vanilla space belt lane is, so it would undo
            // the whole point of the guard.
            //
            // `Create` is called at TLane = FastBeltPathLane with a factory that returns the
            // subclass, which is legal and gives a bundle of the type the base field wants while
            // every lane in it is a CargoBeltLane.
            for (int input = 0; input < InputPathBundles.Length; input++)
            {
                InputPathBundles[input] = ItemLaneBundle.Create(
                    State.InputLaneBundleStates[input],
                    (FastBeltPathLaneState laneState) =>
                    {
                        CargoBeltLane lane = new(configuration.BeltSpeed, laneState);
                        lane.PreAcceptHook = CargoBeltSimulation.IsCargoPackage;
                        return (FastBeltPathLane)lane;
                    });

                InputPathBundles[input].NextBundle = MergerBundle.GetInputBundle(input);
            }

            CargoJunctionDiagnostics.Built("merger", InputPathBundles.Length);

            Outgoing = new CargoHandover.GuardedProviderBundle(base.GetItemProviderBundle(0));
        }

        IItemProviderBundle IItemBundleSimulation.GetItemProviderBundle(int outputIndex)
        {
            return Outgoing;
        }
    }

    /// Builds a cargo merger with as many inputs as the shape has.
    internal sealed class CargoMergerSimulationFactory
        : IIslandSimulationFactoryBuilder<CargoMergerSimulation, SpaceMergerSimulationState,
            SpacePathConfiguration>
    {
        private readonly int Inputs;

        public CargoMergerSimulationFactory(int inputs)
        {
            Inputs = inputs;
        }

        public IFactory<SpaceMergerSimulationState, IslandInstance, CargoMergerSimulation>
            BuildFactory(SimulationSystemsDependencies dependencies, out SpacePathConfiguration config)
        {
            config = dependencies.Mode.Islands.SpaceBelts.First().ConfigAs<SpacePathConfiguration>();

            return new Factory(new SpaceMergerConfiguration(
                Inputs, new CargoBeltSpeed(config.SpaceConveyorSpeed)));
        }

        private sealed class Factory
            : IFactory<SpaceMergerSimulationState, IslandInstance, CargoMergerSimulation>
        {
            private readonly ISpaceMergerConfiguration Configuration;

            public Factory(ISpaceMergerConfiguration configuration)
            {
                Configuration = configuration;
            }

            public CargoMergerSimulation Produce(
                SpaceMergerSimulationState state, IslandInstance island)
            {
                return new CargoMergerSimulation(state, Configuration);
            }
        }
    }

    /// One line per junction kind, the first time one is built.
    ///
    /// A junction that is placed, drawn and connected but passes nothing looks the same whether
    /// the simulation was never constructed, or was constructed and then refused the cargo. This
    /// separates those two, which is otherwise guesswork.
    internal static class CargoJunctionDiagnostics
    {
        private static readonly System.Collections.Generic.HashSet<string> Seen = new();

        public static ILogger Log;

        public static void Built(string kind, int arms)
        {
            string key = kind + arms;
            lock (Seen)
            {
                if (!Seen.Add(key))
                {
                    return;
                }
            }

            Log?.Info?.Log($"Junction: built a cargo {kind} with {arms} arm(s).");
        }
    }
}
