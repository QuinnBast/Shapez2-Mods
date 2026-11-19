using System;
using System.Collections.Generic;
using Game.Core.Content.Islands;
using Game.Core.Coordinates;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>Which space path a station carries.</summary>
internal enum LiftPath
{
    Belt,
    Pipe,
}

/// <summary>Where on the climb a station stands: items go in at the entry and come out at the exit.</summary>
internal enum LiftEnd
{
    Entry,
    Exit,
}

/// <summary>
/// Names for the four station islands - one entry and one exit per path kind.
///
/// The ids are what saves record, so they are fixed strings, never derived from anything that
/// could change between versions.
/// </summary>
internal static class LiftStationIds
{
    private const string Prefix = "ExtraBuildLayers.LiftStation.";

    public static readonly LiftPath[] Paths = { LiftPath.Belt, LiftPath.Pipe };

    public static readonly LiftEnd[] Ends = { LiftEnd.Entry, LiftEnd.Exit };

    public static IslandDefinitionId Definition(LiftPath path, LiftEnd end)
    {
        return new IslandDefinitionId(Prefix + path + end);
    }

    public static IslandDefinitionGroupId Group(LiftPath path, LiftEnd end)
    {
        return new IslandDefinitionGroupId(Prefix + path + end + "Group");
    }

    /// <summary>Built once: this is asked for every island simulation the game creates.</summary>
    private static readonly Dictionary<IslandDefinitionId, (LiftPath, LiftEnd)> ById = BuildIndex();

    private static Dictionary<IslandDefinitionId, (LiftPath, LiftEnd)> BuildIndex()
    {
        Dictionary<IslandDefinitionId, (LiftPath, LiftEnd)> index = new Dictionary<IslandDefinitionId, (LiftPath, LiftEnd)>();
        foreach (LiftPath p in Paths)
        {
            foreach (LiftEnd e in Ends)
            {
                index[Definition(p, e)] = (p, e);
            }
        }

        return index;
    }

    public static bool TryParse(IslandDefinitionId id, out LiftPath path, out LiftEnd end)
    {
        if (ById.TryGetValue(id, out (LiftPath, LiftEnd) found))
        {
            (path, end) = found;
            return true;
        }

        path = default;
        end = default;
        return false;
    }

    /// <summary>
    /// Where the two virtual connectors that join an entry to its exit sit.
    ///
    /// They are ordinary space path pivots, just not on adjacent islands: the entry's output is
    /// the chunk directly before the exit along the shaft, facing it, and the exit's input is the
    /// exit facing back. <c>GlobalChunkPivot.CounterpartConnector</c> of the first is the second,
    /// which is exactly how two neighbouring belts' pivots pair - so the simulation graph links
    /// them as it would neighbours, puts both in one cluster and orders their updates.
    /// </summary>
    public static void ShaftPivots(
        GlobalChunkCoordinate exit, int rise, out GlobalChunkPivot entryOutput, out GlobalChunkPivot exitInput)
    {
        ChunkDirection along = rise > 0 ? ChunkDirection.Up : ChunkDirection.Down;
        GlobalChunkCoordinate beforeExit = new GlobalChunkCoordinate(exit.x, exit.y, (short)(exit.z - Math.Sign(rise)));
        entryOutput = new GlobalChunkPivot(beforeExit, along);
        exitInput = new GlobalChunkPivot(exit, along.Opposite);
    }

    public static IEnumerable<IslandDefinitionId> All()
    {
        foreach (LiftPath path in Paths)
        {
            foreach (LiftEnd end in Ends)
            {
                yield return Definition(path, end);
            }
        }
    }
}
