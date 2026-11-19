using System;
using Game.Core.Serialization;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// How far a station's climb goes: the exit's space level minus the entry's. Positive goes up,
/// negative down; both stations of a pair carry the same value, so either can find the other.
///
/// This is the per-instance height a single island definition cannot carry in its connectors.
/// An island configuration is saved with the island (<c>IslandLayoutSerializer</c> writes
/// <c>island.Configuration.Sync</c> as a blob) and handed to the simulation factory through
/// <c>IslandInstance.Configuration</c>, so it survives saving and is there before the simulation
/// exists.
///
/// Zero means "not part of a climb" - a station placed by hand from the toolbar. It simulates as
/// an inert piece of track rather than throwing.
/// </summary>
public sealed class LiftStationConfiguration : IIslandConfiguration, IEquatable<LiftStationConfiguration>
{
    public short Rise;

    public LiftStationConfiguration()
    {
    }

    public LiftStationConfiguration(int rise)
    {
        Rise = (short)rise;
    }

    public void Sync(ISerializationVisitor visitor)
    {
        visitor.SyncShort_2(ref Rise);
    }

    public bool Equals(IEntityConfiguration other)
    {
        return other is LiftStationConfiguration configuration && Equals(configuration);
    }

    public bool Equals(LiftStationConfiguration other)
    {
        return other != null && other.Rise == Rise;
    }

    public override bool Equals(object obj)
    {
        return obj is LiftStationConfiguration configuration && Equals(configuration);
    }

    public override int GetHashCode()
    {
        return Rise;
    }

    public override string ToString()
    {
        return "Rise " + Rise;
    }

    public static int RiseOf(IIslandConfiguration configuration)
    {
        return configuration is LiftStationConfiguration station ? station.Rise : 0;
    }
}
