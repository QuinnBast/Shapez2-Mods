using Game.Core.Research;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// The two independent stacks the game calls "layers".
///
/// They are not variants of one thing. A machine level is a floor inside a platform and is
/// **positional** - unlocking entry <c>i</c> of <c>Mechanics.BuildingLayerUnlocks</c> allows
/// layer <c>i + 1</c>, so the list order is the layer order. A space level is a platform
/// altitude and carries an explicit <c>IslandLayersUnlockOrder</c> beside it, because vanilla
/// hands them out above and below the starting one interleaved rather than bottom to top.
/// </summary>
public enum LayerKind
{
    /// A building layer inside a platform. The player calls this a machine level.
    Machine,

    /// An island layer in space. The player calls this a space level.
    Space,
}

/// <summary>
/// One layer this mod created, and the research mechanic that grants it.
///
/// A layer exists precisely because a mechanic exists to unlock it - there is no separate
/// layer object to register. Handing a policy this pair is therefore handing it everything
/// there is to know about the layer.
/// </summary>
public readonly struct AddedLayer
{
    public readonly LayerKind Kind;

    /// <summary>
    /// The layer index the player gets. For <see cref="LayerKind.Machine"/> this is the
    /// building layer; for <see cref="LayerKind.Space"/> it is the value written into
    /// <c>IslandLayersUnlockOrder</c>.
    /// </summary>
    public readonly int Level;

    /// The mechanic a policy must make reachable, or the layer stays unbuyable.
    public readonly ResearchMechanicId MechanicId;

    public AddedLayer(LayerKind kind, int level, ResearchMechanicId mechanicId)
    {
        Kind = kind;
        Level = level;
        MechanicId = mechanicId;
    }

    public override string ToString()
    {
        return Kind + " level " + Level + " (" + MechanicId + ")";
    }
}
