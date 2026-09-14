using System.Collections.Generic;
using Game.Core.Simulation;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// One lane out of a blackbox: a few slots the box puts finished items into, emptied onto
/// whatever the player attached to that notch.
///
/// Deliberately not a belt. A belt models where an item physically is; a box has no inside
/// worth modelling, so an output lane needs only somewhere to put items and the willingness to
/// let go of them. The rate does not live here either - it comes from how often the box runs a
/// cycle.
///
/// It holds several rather than one, and empties as many per update as the receiver will take.
/// A single slot emptied once per update caps a lane at one item per tick however fast the belt
/// beyond it runs, which turns the box into the bottleneck for no reason - and hides that fact,
/// because the box looks busy while the belt sits half empty.
///
/// <see cref="Attached"/> is the other useful part. A blackbox declares an output connector on
/// every notch, so the definition cannot say which notches are outputs; the player says it, by
/// attaching a belt. The game wires <see cref="NextLane"/> when that happens, so the lanes worth
/// handing an item to are exactly the ones that have one.
/// </summary>
public class BlackboxLane : IItemLane, IUpdatableItemLane
{
    /// <summary>
    /// Enough to keep a belt fed across a tick without becoming a buffer in its own right.
    /// Items sitting here are not saved, so holding many would lose more on a reload.
    /// </summary>
    private const int Slots = 4;

    private readonly Queue<IBeltItem> Held = new Queue<IBeltItem>();

    public IItemReceiver NextLane { get; set; }

    /// <summary>Whether anything is hooked up to this lane, and so whether the box should
    /// bother putting items in it.</summary>
    public bool Attached
    {
        get { return NextLane != null; }
    }

    /// <summary>How many more items this lane will take.</summary>
    public int Room
    {
        get { return Slots - Held.Count; }
    }

    public int ItemCount
    {
        get { return Held.Count; }
    }

    public bool HasItem
    {
        get { return Held.Count > 0; }
    }

    public Steps MaxStep_S
    {
        get { return LaneConstants.ItemSpacing; }
    }

    public Steps FreeStepsAtTheEnd
    {
        get { return Held.Count == 0 ? LaneConstants.ItemSpacing : Steps.Zero; }
    }

    public IBeltItem GetItem(int index)
    {
        int at = 0;

        foreach (IBeltItem item in Held)
        {
            if (at++ == index)
            {
                return item;
            }
        }

        return null;
    }

    public bool CanAcceptItem(IBeltItem itemToTransfer)
    {
        return Held.Count < Slots;
    }

    public void HandOverItem(IBeltItem itemToTransfer, Ticks remainingTicks)
    {
        Held.Enqueue(itemToTransfer);
    }

    public void Update(Ticks deltaTicks)
    {
        if (NextLane == null)
        {
            return;
        }

        // As many as the receiver will take. Nowhere to go is not an error - the box simply
        // stays full, which is how a real factory behaves when the belt off its output backs up.
        while (Held.Count > 0 && NextLane.CanAcceptItem(Held.Peek()))
        {
            NextLane.HandOverItem(Held.Dequeue(), Ticks.Zero);
        }
    }

    public void Clear()
    {
        Held.Clear();
    }
}
