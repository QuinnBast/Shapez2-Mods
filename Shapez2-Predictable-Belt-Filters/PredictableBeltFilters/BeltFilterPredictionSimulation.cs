using Core.Collections.Scoped;
using Game.Content.Features.Predictions;
using Game.Content.Features.Signals;
using Game.Content.Features.Signals.Conductor;
using Game.Core.Coordinates;
using Game.Core.Simulation;

namespace QuinnBast.Shapez2.PredictableBeltFilters
{
    /// What a belt filter contributes to the item prediction graph.
    ///
    /// Vanilla registers the filter with a plain SplitterPredictionSimulationFactory(2), whose
    /// Update pushes the *same* predicted set to every output. A filter is therefore predicted as
    /// if it were a dumb splitter: both the match and the mismatch belt claim they can carry
    /// everything on the line. That is the whole defect this mod exists to fix.
    ///
    /// The real routing rule is fully determined and lives in
    /// SignalControlledDistributionBehaviour.TryFindNextLane, which this mirrors exactly:
    ///
    ///   BeltItemSignal(V)        V takes the match lane, everything else the mismatch lane
    ///   IntegerSignal, truthy    everything takes the match lane
    ///   IntegerSignal, falsy     everything takes the mismatch lane
    ///   NullSignal / Conflict    nothing moves at all
    ///
    /// Only the first case actually narrows a set, but it is the case players build - a filter
    /// wired to a constant shape signal - and narrowing it is what stops downstream cutters and
    /// painters from being handed shapes that never reach them.
    public class BeltFilterPredictionSimulation : IItemPredictionSimulation, ISimulation,
        IUpdatableSimulation
    {
        /// Lane order is the filter's own: BeltFilterSimulation exposes OutputLanes[0] as
        /// MatchOutputLane and OutputLanes[1] as MismatchOutputLane, and
        /// ConnectableBuildingPredictionSimulation pairs providers to the definition's outputs in
        /// that same declaration order. One order serves the real simulation and this one.
        private readonly ItemPredictionReceiver Input = new ItemPredictionReceiver();

        private readonly ItemPredictionProvider Match = new ItemPredictionProvider();

        private readonly ItemPredictionProvider Mismatch = new ItemPredictionProvider();

        /// Where the real simulation's state container for this tile is published. Deliberately
        /// *not* `building.State`: the BuildingInstance the prediction graph hands out is
        /// unreliable for a newly placed building - see <see cref="FilterStateRegistry"/> for the
        /// exact mechanism. The tile is the only part of that instance worth keeping.
        private readonly FilterStateRegistry Registry;

        private readonly GlobalTileCoordinate Position;

        /// The last signal actually observed, kept because SignalBuffer.GetMostRecent() returns
        /// NullSignal whenever the buffer was not written on the current start tick. The
        /// prediction graph updates on its own round-robin schedule (PredictionUpdateStrategy),
        /// not in step with the signal tick, so reading it raw makes a correctly wired filter
        /// flicker between "filtered" and "unknown" as the two schedules drift past each other.
        /// Latching the last concrete signal makes the readout stable, at the cost of a filter
        /// whose wire is later *removed* predicting its old rule until the building is rebuilt.
        private ISignal LatchedSignal;

        public int NumItemReceivers => 1;

        public int NumItemProviders => 2;

        public BeltFilterPredictionSimulation(BuildingInstance building, FilterStateRegistry registry)
        {
            Registry = registry;
            Position = building.Transform.Position;
        }

        public IItemPredictionReceiver GetItemReceiver(int index)
        {
            return Input;
        }

        public IItemPredictionProvider GetItemProvider(int index)
        {
            return index == 0 ? Match : Mismatch;
        }

        public void Update(Ticks startTicks, Ticks deltaTicks)
        {
            PredictedItem input = Input.PopPrediction();
            ISignal signal = FilterSignal();

            // Degenerated is the game's own "more possibilities than I will track". There is no
            // meaningful subset of it to hand out, so pass it along untouched and let the lanes
            // downstream collapse it to nothing the way they already do.
            if (signal == null || input == PredictedItem.Degenerated)
            {
                PushBoth(in input);
                return;
            }

            if (signal is BeltItemSignal itemSignal)
            {
                PredictedItem matched = Only(in input, itemSignal.Value);
                PredictedItem remainder = Except(in input, itemSignal.Value);

                Match.Push(in matched);
                Mismatch.Push(in remainder);
                return;
            }

            if (signal is IntegerSignal integerSignal)
            {
                PredictedItem nothing = PredictedItem.None;

                if (integerSignal.IsTruthy())
                {
                    Match.Push(in input);
                    Mismatch.Push(in nothing);
                }
                else
                {
                    Match.Push(in nothing);
                    Mismatch.Push(in input);
                }

                return;
            }

            // Unreachable today - only the two signal kinds above are ever latched - but a game
            // update that adds a signal type should degrade to vanilla's answer rather than
            // silently blank a belt.
            PushBoth(in input);
        }

        /// Vanilla's answer: every output can carry everything. Used wherever the filter's rule is
        /// not known, so this mod never removes information the base game was already showing.
        private void PushBoth(in PredictedItem input)
        {
            Match.Push(in input);
            Mismatch.Push(in input);
        }

        /// The filter's current rule, or null if it has never been observed.
        ///
        /// Two lookups, both deliberately repeated on **every** call and neither ever cached:
        ///
        /// 1. The container comes from <see cref="FilterStateRegistry"/> - published by the real
        ///    simulation side - rather than from this simulation's own BuildingInstance, because
        ///    for a newly placed filter that instance carries a placement ghost's empty container
        ///    that nothing will ever fill. The registry entry appears when the real building is
        ///    revealed, which may be after this simulation starts updating.
        /// 2. The state comes out of the container each time, because
        ///    `SimulationStateContainer.New&lt;T&gt;()` *replaces* the state object rather than
        ///    filling one in, and
        ///    `AtomicStatefulBuildingSimulationSystem.CreateConnectableSimulation` calls it every
        ///    time the real simulation is built. A reference held across that call is orphaned,
        ///    and an orphaned state either has a null conductor or - worse, because every null
        ///    check passes - a valid one that the wire network is no longer connected to, which
        ///    simply reports NullSignal forever.
        ///
        /// Both lookups are a dictionary hit and an `as` cast. That is nothing next to being
        /// silently wrong for the life of a building.
        ///
        /// Do not call BeltFilterSimulationState.CurrentSignal either: it dereferences
        /// InputConductor without a null check, and that field is null until the real
        /// BeltFilterSimulation's constructor assigns it.
        private ISignal FilterSignal()
        {
            if (!Registry.TryGet(Position, out SimulationStateContainer container)
                || !container.Is<BeltFilterSimulationState>(out BeltFilterSimulationState state))
            {
                return LatchedSignal;
            }

            SignalConductorInput conductor = state.InputConductorState.InputConductor;
            if (conductor == null)
            {
                return LatchedSignal;
            }

            ISignal signal = conductor.GetMostRecent();

            // NullSignal and ConflictSignal are deliberately not latched. Both genuinely stop the
            // filter dead, so predicting two empty lanes would be *correct* - but an unwired
            // filter is the normal state of one just placed, and blanking both its belts reads as
            // the mod breaking predictions. Falling back to vanilla is never worse than vanilla.
            if (signal is BeltItemSignal || signal is IntegerSignal)
            {
                LatchedSignal = signal;
            }

            return LatchedSignal;
        }

        /// The single item the filter lets through, if the line can carry it at all.
        ///
        /// Reference equality, because that is the comparison the real filter makes: belt items
        /// are interned, and BeltItemSignal.From keys its own cache on the instance.
        private static PredictedItem Only(in PredictedItem input, IBeltItem wanted)
        {
            int count = input.Count;
            for (int i = 0; i < count; i++)
            {
                if (ReferenceEquals(input[i], wanted))
                {
                    return new PredictedItem(input[i]);
                }
            }

            return PredictedItem.None;
        }

        /// Everything the filter rejects. Pooled the way vanilla's own prediction helpers are -
        /// this runs per filter per prediction pass, and a fresh List here would be garbage on a
        /// timer.
        private static PredictedItem Except(in PredictedItem input, IBeltItem unwanted)
        {
            using ScopedList<IItem> kept = ScopedList<IItem>.Get(PredictedItem.Capacity);

            int count = input.Count;
            for (int i = 0; i < count; i++)
            {
                if (!ReferenceEquals(input[i], unwanted))
                {
                    kept.Add(input[i]);
                }
            }

            return new PredictedItem(kept);
        }
    }
}
