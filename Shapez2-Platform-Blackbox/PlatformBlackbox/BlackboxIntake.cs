using Game.Core.Simulation;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// One lane into a blackbox: an item arrives and becomes stock.
///
/// An intake keeps nothing. The moment an item is handed over it goes into the box's pool and
/// the lane is free again, because a lane is a place on a belt and the inside of a box has no
/// places - only quantities. What stops the belts feeding it forever is the pool's own capacity,
/// which is where backpressure belongs now that a lane no longer holds anything.
///
/// This replaced a lane that held one item, delayed it, transformed it and passed it to the
/// notch opposite. That could stand in for a cutter, whose whole recipe is one shape in and one
/// shape out, but not for anything that combines ingredients - and it leaked structure the box
/// should not have had, because which side an item left by depended on which side it entered.
/// </summary>
public class BlackboxIntake : IItemLane, IUpdatableItemLane
{
    /// <summary>Where accepted items go. Assigned after construction, because a bundle builds
    /// its lanes through a parameterless constructor.</summary>
    public BlackboxPool Pool;

    /// <summary>
    /// Whether this lane is a pipe rather than a belt.
    ///
    /// A lane has to know, because free space is the one question it gets asked without being
    /// told what for. Answering it from the whole pool let a full shape buffer close the paint
    /// pipes, which starves a painter permanently: no paint means no cycles, no cycles means the
    /// shapes never drain, and the shapes not draining is what was closing the pipes.
    /// </summary>
    public bool Fluid;

    /// <summary>
    /// How many items this lane has taken in.
    ///
    /// A receiver bundle has no way to ask whether anything is wired to it - the connection is
    /// held by whatever is upstream - so the only honest answer to "is this notch delivering?" is
    /// to count what has arrived through it.
    /// </summary>
    public long Delivered;

    /// <summary>
    /// Unused, but part of <see cref="IItemLane"/>: an intake is the end of the line, so there
    /// is no next lane to hand anything to.
    /// </summary>
    public IItemReceiver NextLane { get; set; }

    public int ItemCount
    {
        get { return 0; }
    }

    public bool HasItem
    {
        get { return false; }
    }

    public Steps MaxStep_S
    {
        get { return LaneConstants.ItemSpacing; }
    }

    /// <summary>
    /// Room whenever the pool has room, which is what makes a starved box back its belts up
    /// rather than swallow one ingredient without limit.
    ///
    /// An intake is always empty, so this cannot be answered from the lane; it has to come from
    /// the pool. It stays the looser of the two tests - <see cref="CanAcceptItem"/> knows which
    /// item is being offered and can refuse just that one - but it is now asked about the kind of
    /// thing this particular lane carries rather than about the box as a whole.
    /// </summary>
    public Steps FreeStepsAtTheEnd
    {
        get
        {
            if (Pool == null)
            {
                return Steps.Zero;
            }

            bool crowded = Fluid ? Pool.CrowdedForFluid : Pool.CrowdedForShapes;
            return crowded ? Steps.Zero : LaneConstants.ItemSpacing;
        }
    }

    public IBeltItem GetItem(int index)
    {
        return null;
    }

    public bool CanAcceptItem(IBeltItem itemToTransfer)
    {
        return Pool != null && itemToTransfer != null && Pool.HasRoomFor(itemToTransfer);
    }

    public void HandOverItem(IBeltItem itemToTransfer, Ticks remainingTicks)
    {
        Delivered++;
        Pool?.Deposit(itemToTransfer);
    }

    public void Update(Ticks deltaTicks)
    {
        // Nothing to advance. The box's cycle does the work, on the simulation rather than
        // per lane, because one recipe is paid for out of stock gathered from every lane.
    }

    public void Clear()
    {
    }
}
