using System;
using System.Collections.Generic;
using Game.Core.Coordinates;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Deleting either station of a lift deletes the other with it.
///
/// Every player change to islands - placing, deleting, undoing either - is one
/// <c>ActionModifyIsland</c>, carrying a list of islands to place and a list to delete in its
/// public <c>Data</c>. A postfix on its constructor adds the partner of every station in the
/// delete list, so the pair goes in the same action: one undo brings both back, and nothing can
/// delete one end and leave an exit with no shaft or an entry feeding nowhere.
///
/// The partner is found from the climb the station records: an entry's exit is <c>Rise</c> levels
/// above (or below) it, an exit's entry the same distance back. It is only added if the island
/// there really is the matching station of the same path kind, and not already in the list - an
/// area deletion usually selects both.
/// </summary>
internal sealed class LiftStationPairedDeletion : IDisposable
{
    private delegate void Ctor(ActionModifyIsland self, IMapModel map, Player executor, ActionModifyIsland.Payload data);

    private delegate void CtorHook(Ctor orig, ActionModifyIsland self, IMapModel map, Player executor, ActionModifyIsland.Payload data);

    private readonly ILogger Logger;
    private readonly Hook Hook;
    private bool Complained;

    public LiftStationPairedDeletion(ILogger logger)
    {
        Logger = logger;

        // Contained: a throw out of a mod constructor is not caught by ModLoader.
        try
        {
            Hook = new Hook(
                typeof(ActionModifyIsland).GetConstructor(new[] { typeof(IMapModel), typeof(Player), typeof(ActionModifyIsland.Payload) }),
                new CtorHook(Detour));
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
            logger.Error?.Log("Extra Build Layers: could not hook island deletion; deleting one lift station leaves the other.");
        }
    }

    private void Detour(Ctor orig, ActionModifyIsland self, IMapModel map, Player executor, ActionModifyIsland.Payload data)
    {
        orig(self, map, executor, data);

        try
        {
            IReadOnlyList<ActionModifyIsland.DeletePayload> delete = self.Data.Delete;
            if (map == null || delete == null || delete.Count == 0)
            {
                return;
            }

            List<ActionModifyIsland.DeletePayload> extended = null;
            HashSet<IslandId> deleting = new HashSet<IslandId>();
            foreach (ActionModifyIsland.DeletePayload payload in delete)
            {
                deleting.Add(payload.IslandId);
            }

            foreach (ActionModifyIsland.DeletePayload payload in delete)
            {
                if (!TryPartner(map, payload.IslandId, out IslandId partner) || !deleting.Add(partner))
                {
                    continue;
                }

                extended ??= new List<ActionModifyIsland.DeletePayload>(delete);
                extended.Add(new ActionModifyIsland.DeletePayload(partner));
            }

            if (extended != null)
            {
                ActionModifyIsland.Payload old = self.Data;
                self.Data = new ActionModifyIsland.Payload(
                    old.Place, extended, old.IgnorePlacementBlueprintCost, old.RefundDeletionBlueprintCost);
            }
        }
        catch (Exception exception)
        {
            // Falls back to deleting just what was asked for.
            if (!Complained)
            {
                Complained = true;
                Logger.Exception?.LogException(exception);
            }
        }
    }

    private static bool TryPartner(IMapModel map, IslandId id, out IslandId partner)
    {
        partner = default;
        IslandModel island = map.GetIsland(id);
        if (island == IslandModel.Invalid || !LiftStationIds.TryParse(island.Definition.Id, out LiftPath path, out LiftEnd end))
        {
            return false;
        }

        int rise = LiftStationConfiguration.RiseOf(island.Configuration);
        if (rise == 0)
        {
            return false;
        }

        GlobalChunkCoordinate here = island.Transform.Position;
        int offset = end == LiftEnd.Entry ? rise : -rise;
        GlobalChunkCoordinate there = new GlobalChunkCoordinate(here.x, here.y, (short)(here.z + offset));
        LiftEnd wanted = end == LiftEnd.Entry ? LiftEnd.Exit : LiftEnd.Entry;

        if (!map.TryGetIsland(there, out IslandModel other) ||
            !LiftStationIds.TryParse(other.Definition.Id, out LiftPath otherPath, out LiftEnd otherEnd) ||
            otherPath != path || otherEnd != wanted || LiftStationConfiguration.RiseOf(other.Configuration) != rise)
        {
            return false;
        }

        partner = other.Id;
        return true;
    }

    public void Dispose()
    {
        Hook?.Dispose();
    }
}
