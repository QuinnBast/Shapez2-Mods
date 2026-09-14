using Game.Core.Serialization;
using Game.Core.Simulation;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// Everything about a blackbox that has to survive a reload.
///
/// The game already saves a state object per island - <c>IslandModel.State</c> is a
/// <c>SimulationStateContainer</c> the save visitor syncs - so a blackbox does not need a store of
/// its own keyed by position or id. It writes into the same place a belt writes the items on it,
/// and the game handles identity, lifetime and deletion.
///
/// Two things go in, and they answer different questions.
///
/// The **blueprint** is what the box stands in for. Without it a reloaded box could never measure
/// again - it would be a platform with a recipe and no idea where the recipe came from, unable to
/// respond when its inputs changed. It is kept as the same exported text the blueprint library
/// uses, so it survives the mod being updated.
///
/// The **recipe** is what the box does right now. It could in principle be re-derived by measuring
/// again, and early on that was the plan - but measuring a Make Anything Machine takes minutes,
/// and a box that spends the first five minutes after every load handing back its inputs
/// unchanged is not a box anyone would trust.
/// </summary>
[SyncableIdentifier("PlatformBlackboxIslandState")]
public class BlackboxIslandState : ISimulationState
{
    /// <summary>The factory this box replaced, as exported blueprint text.</summary>
    public string Blueprint;

    /// <summary>
    /// The ingredient set the recipe below was measured against.
    ///
    /// Saved so a reloaded box knows it has already answered this question. Without it the box
    /// would look at its own stock, find a set it had no record of, and measure again -
    /// immediately, on a recipe it already had.
    /// </summary>
    public string MeasuredSignature = string.Empty;

    /// <summary>What the box is currently doing, or null if it has never been measured.</summary>
    public BlackboxRecipe Recipe;

    /// <summary>
    /// Stock, and the record of what this box has been fed.
    ///
    /// The pool is the box's contents, so leaving it out would quietly destroy whatever the box
    /// was holding on every save - a few hundred items on a busy one. What it does not cover is
    /// the handful sitting in output lanes, which are transient and would cost far more to write
    /// than they are worth.
    /// </summary>
    public readonly BlackboxPool Pool = new BlackboxPool();

    /// <summary>
    /// Takes on everything another state knew.
    ///
    /// Reconnecting a platform rebuilds its simulation, and the framework gives the new one a
    /// fresh state rather than the one the save was read into. Without this, reconnecting a box
    /// would cure its wiring and cost it its recipe - which is a worse box than the one we
    /// started with.
    /// </summary>
    public void CopyFrom(BlackboxIslandState other)
    {
        if (other == null || ReferenceEquals(other, this))
        {
            return;
        }

        Blueprint = other.Blueprint;
        MeasuredSignature = other.MeasuredSignature;
        Recipe = other.Recipe;
        Pool.CopyFrom(other.Pool);
    }

    public void Sync(ISerializationVisitor visitor)
    {
        visitor.SyncString_4(ref Blueprint);
        visitor.SyncString_4(ref MeasuredSignature);

        if (visitor.Writing)
        {
            visitor.WriteBool_1(Recipe != null);
            Recipe?.Sync(visitor);
        }
        else
        {
            Recipe = visitor.ReadBool_1() ? new BlackboxRecipe() : null;
            Recipe?.Sync(visitor);
        }

        Pool.Sync(visitor);
    }
}
