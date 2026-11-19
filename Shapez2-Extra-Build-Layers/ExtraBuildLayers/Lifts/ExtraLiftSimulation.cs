using Core.Factory;
using Game.Core.Simulation;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// A belt lift of any height.
///
/// Vanilla's two are the same machine with the loop unrolled:
/// <c>BeltLift1LayerSimulation</c> chains <c>Input → Vertical → Output</c> and
/// <c>BeltLift2LayerSimulation</c> chains <c>Input → Vertical0 → Vertical1 → Output</c>, each
/// lane built with <c>new BeltLane(speed, state, next)</c> where <c>next</c> is the lane
/// downstream. Rolling the loop back up is the whole of this class.
///
/// Lanes are constructed **output first**, because a <c>BeltLane</c> takes its successor at
/// construction and items flow along that chain. Building input-first would leave every lane
/// pointing at null.
/// </summary>
public class ExtraLiftSimulation : Simulation<ExtraLiftSimulationState>, IItemSimulation, ISimulation, IUpdatableSimulation
{
    public readonly BeltLane InputLane;
    public readonly BeltLane[] VerticalLanes;
    public readonly BeltLane OutputLane;

    public int NumItemReceivers => 1;

    public int NumItemProviders => 1;

    public ExtraLiftSimulation(ExtraLiftSimulationState state, BeltSpeed beltSpeed)
        : base(state)
    {
        OutputLane = new BeltLane(beltSpeed, state.OutputLaneState);

        VerticalLanes = new BeltLane[state.VerticalLaneStates.Length];
        IItemReceiver next = OutputLane;
        for (int i = VerticalLanes.Length - 1; i >= 0; i--)
        {
            VerticalLanes[i] = new BeltLane(beltSpeed, state.VerticalLaneStates[i], next);
            next = VerticalLanes[i];
        }

        InputLane = new BeltLane(beltSpeed, state.InputLaneState, next);
    }

    public IItemReceiver GetItemReceiver(int index)
    {
        return InputLane;
    }

    public IItemProvider GetItemProvider(int index)
    {
        return OutputLane;
    }

    public void TraverseLanes<TTraverser>(TTraverser traverser) where TTraverser : IItemLaneTraverser
    {
        traverser.Traverse(InputLane);
        for (int i = 0; i < VerticalLanes.Length; i++)
        {
            traverser.Traverse(VerticalLanes[i]);
        }

        traverser.Traverse(OutputLane);
    }

    public void ClearContent()
    {
        TraverseLanes(ClearItemsItemLaneTraverser.Default);
    }

    /// <summary>
    /// Downstream first, matching both vanilla lifts. A lane can only hand an item on if the
    /// one after it has already made room this tick, so updating upstream first would move
    /// every item exactly one lane per tick regardless of belt speed.
    /// </summary>
    public void Update(Ticks startTicks, Ticks deltaTicks)
    {
        OutputLane.Update(deltaTicks);
        for (int i = VerticalLanes.Length - 1; i >= 0; i--)
        {
            VerticalLanes[i].Update(deltaTicks);
        }

        InputLane.Update(deltaTicks);
    }
}

/// <summary>
/// Produces lifts of one particular height. One instance per span, created in a loop - the span
/// lives here rather than in the state because <c>New&lt;TState&gt;()</c> gives the state a
/// parameterless constructor and no chance to learn it.
/// </summary>
public class ExtraLiftSimulationFactory : IFactory<ExtraLiftSimulationState, ExtraLiftSimulation>
{
    private readonly BeltSpeed BeltSpeed;
    private readonly int VerticalLanes;

    public ExtraLiftSimulationFactory(BeltSpeed beltSpeed, int verticalLanes)
    {
        BeltSpeed = beltSpeed;
        VerticalLanes = verticalLanes;
    }

    public ExtraLiftSimulation Produce(ExtraLiftSimulationState state)
    {
        // A no-op for a loaded lift, whose Sync has already sized the array from the blob, and
        // the sizing step for a freshly placed one.
        state.EnsureSize(VerticalLanes);
        return new ExtraLiftSimulation(state, BeltSpeed);
    }
}
