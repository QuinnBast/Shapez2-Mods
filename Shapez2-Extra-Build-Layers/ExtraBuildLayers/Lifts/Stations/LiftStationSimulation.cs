using System;
using System.Collections.Generic;
using Game.Content.Features.SpacePaths;
using Game.Core.Belts.BeltPath;
using Game.Core.Serialization;
using Game.Core.Simulation;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// The entry station of a climb, and the shaft above (or below) it.
///
/// The shaft is vanilla belt, one 16-slot <c>FastBeltPathLane</c> bundle per level climbed,
/// chained with <c>NextBundle</c> exactly as two neighbouring belt segments are chained. So an item
/// takes as long to climb a level as to cross a chunk of space belt, and the climb carries the
/// belt's full throughput: a longer chain is more of the same belt, never a slower one.
///
/// Receiver 0 is the bottom of the chain, where the entry's declared input feeds it. The top of
/// the chain is handed out as provider 0, but the island declares no output for it -
/// <see cref="LiftStationConnectors"/> adds a virtual one that pairs with the exit station's.
/// </summary>
public sealed class LiftEntrySimulation : Simulation<LiftStationSimulationState>, IItemBundleSimulation,
    ISimulation, IUpdatableSimulation
{
    public readonly List<ItemLaneBundle<FastBeltPathLane>> Shaft = new List<ItemLaneBundle<FastBeltPathLane>>();

    public ItemLaneBundle<FastBeltPathLane> Top => Shaft[Shaft.Count - 1];

    public int NumItemReceiverBundles => 1;

    public int NumItemProviderBundles => 1;

    public LiftEntrySimulation(BeltSpeed speed, LiftStationSimulationState state, int levels)
        : base(state)
    {
        state.EnsureSegments(Math.Max(1, levels));
        foreach (BundleState<FastBeltPathLaneState> segment in state.Segments)
        {
            Shaft.Add(ItemLaneBundle.Create(segment, (FastBeltPathLaneState lane) => new FastBeltPathLane(speed, lane)));
        }

        for (int i = 0; i + 1 < Shaft.Count; i++)
        {
            Shaft[i].NextBundle = Shaft[i + 1];
        }
    }

    public void ClearContent()
    {
        foreach (ItemLaneBundle<FastBeltPathLane> segment in Shaft)
        {
            segment.Clear();
        }
    }

    public void TraverseLanes<TTraverser>(TTraverser traverser) where TTraverser : IItemLaneTraverser
    {
        foreach (ItemLaneBundle<FastBeltPathLane> segment in Shaft)
        {
            segment.TraverseLanes(traverser);
        }
    }

    public void Update(Ticks startTicks, Ticks deltaTicks)
    {
        // Downstream first, so a segment has made room before the one behind it pushes into it -
        // the order the simulation graph gives separate segments.
        for (int i = Shaft.Count - 1; i >= 0; i--)
        {
            Shaft[i].Update(deltaTicks);
        }
    }

    public IItemReceiverBundle GetItemReceiverBundle(int inputIndex)
    {
        return inputIndex == 0 ? Shaft[0] : throw new ArgumentOutOfRangeException(nameof(inputIndex));
    }

    public IItemProviderBundle GetItemProviderBundle(int outputIndex)
    {
        return outputIndex == 0 ? Top : throw new ArgumentOutOfRangeException(nameof(outputIndex));
    }
}

/// <summary>
/// The exit station: one segment of belt that takes items from the shaft and sends them out of its
/// declared output, the same as a vanilla straight space belt segment. Its input is the virtual one
/// <see cref="LiftStationConnectors"/> adds.
/// </summary>
public sealed class LiftExitSimulation : Simulation<LiftStationSimulationState>, IItemBundleSimulation,
    ISimulation, IUpdatableSimulation
{
    public readonly ItemLaneBundle<FastBeltPathLane> Segment;

    public int NumItemReceiverBundles => 1;

    public int NumItemProviderBundles => 1;

    public LiftExitSimulation(BeltSpeed speed, LiftStationSimulationState state)
        : base(state)
    {
        state.EnsureSegments(1);
        Segment = ItemLaneBundle.Create(state.Segments[0], (FastBeltPathLaneState lane) => new FastBeltPathLane(speed, lane));
    }

    public void ClearContent()
    {
        Segment.Clear();
    }

    public void TraverseLanes<TTraverser>(TTraverser traverser) where TTraverser : IItemLaneTraverser
    {
        Segment.TraverseLanes(traverser);
    }

    public void Update(Ticks startTicks, Ticks deltaTicks)
    {
        Segment.Update(deltaTicks);
    }

    public IItemReceiverBundle GetItemReceiverBundle(int inputIndex)
    {
        return inputIndex == 0 ? Segment : throw new ArgumentOutOfRangeException(nameof(inputIndex));
    }

    public IItemProviderBundle GetItemProviderBundle(int outputIndex)
    {
        return outputIndex == 0 ? Segment : throw new ArgumentOutOfRangeException(nameof(outputIndex));
    }
}

/// <summary>
/// The belt segments of a station, saved as a count followed by each segment.
///
/// The count is not known when the game constructs this - states are made with <c>new()</c> and
/// then either filled from the save or handed to the simulation factory - so the simulation sizes
/// it (<see cref="EnsureSegments"/>) from the station's configuration, and a save carries its own
/// count. A save whose count disagrees with the configuration (it should not happen) keeps what it
/// saved and is trimmed or padded by the next <see cref="EnsureSegments"/>.
///
/// 16 slots per lane, as vanilla space paths and Crossover Platforms use, so a climb buffers like
/// the belt either side of it.
/// </summary>
[SyncableIdentifier("ExtraBuildLayersLiftStationState")]
public sealed class LiftStationSimulationState : ISimulationState, ISyncable
{
    private const short NumItemsPerLane = 16;

    public readonly List<BundleState<FastBeltPathLaneState>> Segments = new List<BundleState<FastBeltPathLaneState>>();

    public void EnsureSegments(int count)
    {
        while (Segments.Count < count)
        {
            Segments.Add(BundleState.Create(() => new FastBeltPathLaneState(NumItemsPerLane)));
        }

        if (Segments.Count > count)
        {
            Segments.RemoveRange(count, Segments.Count - count);
        }
    }

    public void Sync(ISerializationVisitor visitor)
    {
        int count = Segments.Count;
        visitor.SyncInt_4(ref count);
        if (visitor.Reading)
        {
            // Resized in place, never rebuilt. The game can build the simulation before it reads
            // the save into this state, and the simulation's lanes hold these very segment
            // objects - replacing them put every item in transit into objects nothing read, so
            // a reload emptied the shaft.
            EnsureSegments(Math.Max(0, count));
        }

        foreach (BundleState<FastBeltPathLaneState> segment in Segments)
        {
            segment.Sync(visitor);
        }
    }
}
