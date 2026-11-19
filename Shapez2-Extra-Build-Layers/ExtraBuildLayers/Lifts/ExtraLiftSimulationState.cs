using System;
using Game.Core.Serialization;
using Game.Core.Simulation;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// One state type for a lift of any height.
///
/// The obvious design - a class per span, <c>Lift3LayerState</c>, <c>Lift4LayerState</c>… - is
/// forced on you by two constraints that look fatal together.
/// <c>AtomicStatefulBuildingSimulationSystem</c> requires
/// <c>where TSimulationState : class, ISimulationState, new()</c> and builds the state through
/// <c>building.State.New&lt;TSimulationState&gt;()</c>, so a state cannot be told its span; and
/// <c>[SyncableIdentifier]</c> is read with <c>inherit: false</c>, so a generic
/// <c>LiftState&lt;N&gt;</c> could not carry a per-span id either.
///
/// The way out is the game's own: make the state **self-describing**. The array starts empty,
/// which satisfies <c>new()</c>; the length is written into the save ahead of the elements; and
/// on read <see cref="EnsureSize"/> rebuilds the array before they are consumed.
/// <c>SpaceMergerSimulationState</c> does exactly this with its input lane bundles, which is
/// both the precedent and the proof it survives a round trip.
///
/// So there is nothing here that knows about a particular height, and nothing to add when the
/// layer count goes up.
/// </summary>
[SyncableIdentifier("ExtraBuildLayers.LiftState")]
public class ExtraLiftSimulationState : ISimulationState, ISyncable
{
    public readonly BeltLaneState InputLaneState = new BeltLaneState();

    /// <summary>
    /// One per layer the lift climbs. Empty until <see cref="EnsureSize"/> runs - on a fresh
    /// placement that is the factory, which knows the span from the definition it serves; on a
    /// load it is <see cref="Sync"/>, from the count in the blob.
    /// </summary>
    public BeltLaneState[] VerticalLaneStates = Array.Empty<BeltLaneState>();

    public readonly BeltLaneState OutputLaneState = new BeltLaneState();

    public void Sync(ISerializationVisitor visitor)
    {
        InputLaneState.Sync(visitor);

        // Int rather than the byte SpaceMergerSimulationState uses. A lift cannot currently be
        // taller than the layer ceiling, but the count costs three more bytes per lift and
        // removes a cap that would otherwise have to be rediscovered later.
        int count = VerticalLaneStates.Length;
        visitor.SyncInt_4(ref count);

        if (visitor.Reading)
        {
            EnsureSize(count);
        }

        for (int i = 0; i < VerticalLaneStates.Length; i++)
        {
            VerticalLaneStates[i].Sync(visitor);
        }

        OutputLaneState.Sync(visitor);
    }

    /// <summary>
    /// Idempotent, and deliberately so: the factory calls it for a newly placed lift, and
    /// <see cref="Sync"/> calls it for a loaded one. Whichever runs first, the second is a
    /// no-op, and the lanes the simulation wraps are never swapped out from under it.
    /// </summary>
    public void EnsureSize(int verticalLanes)
    {
        if (verticalLanes < 0)
        {
            verticalLanes = 0;
        }

        if (VerticalLaneStates.Length == verticalLanes)
        {
            return;
        }

        BeltLaneState[] resized = new BeltLaneState[verticalLanes];
        for (int i = 0; i < verticalLanes; i++)
        {
            resized[i] = i < VerticalLaneStates.Length ? VerticalLaneStates[i] : new BeltLaneState();
        }

        VerticalLaneStates = resized;
    }
}
