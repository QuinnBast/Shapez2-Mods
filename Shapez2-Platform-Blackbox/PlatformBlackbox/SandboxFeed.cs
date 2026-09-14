using System.Collections.Generic;
using Game.Content.Features.Fluids;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// What to feed each of a sandbox's inputs, keyed by the tile the port building sits on.
///
/// A sandbox copy sits at the same coordinates as the platform it was copied from, so a tile
/// is what lines a copy up with the original - and the prediction graph already knows which
/// shape or fluid arrives there. That saves the sandbox from having to guess, and means it is
/// fed the same thing the real factory is fed.
///
/// A port simulation can span two tiles, one on each platform, so lookups try every tile the
/// simulation occupies rather than assuming the first one is the interesting end.
/// </summary>
public class SandboxFeed
{
    public readonly Dictionary<GlobalTileCoordinate, IBeltItem> Items =
        new Dictionary<GlobalTileCoordinate, IBeltItem>();

    public readonly Dictionary<GlobalTileCoordinate, IFluid> Fluids =
        new Dictionary<GlobalTileCoordinate, IFluid>();

    /// <summary>
    /// What to feed a port whose tile is not in the map above.
    ///
    /// A blueprint expanded into a private world sits at the origin rather than where the
    /// factory it came from sits, so its tiles line up with nothing. More importantly, a
    /// blueprint alone has no predicted inputs at all - predictions flow downstream from
    /// sources, and an isolated block has none. So it gets fed a probe instead, and what
    /// comes out the far side is observed rather than predicted.
    ///
    /// Only set when every input of that kind wanted the same thing. A block taking two
    /// different shapes cannot be probed with one of them without inventing which port gets
    /// which, so it is left unfed and reported.
    /// </summary>
    public IBeltItem ProbeItem;
    public IFluid ProbeFluid;

    /// <summary>
    /// What each input notch gets, when one probe is not enough.
    ///
    /// A factory taking two different shapes cannot be fed from a single probe: a stacker given
    /// the same shape on both belts stacks it with itself, which is not the recipe. Which port
    /// wants which shape is not written down anywhere, so it is found by trying - and this is
    /// where a candidate assignment is put while it is tried.
    ///
    /// A notch rather than a port, because ports on one notch are one belt and always want the
    /// same thing.
    /// </summary>
    public readonly Dictionary<NotchGrouping.Notch, IBeltItem> ByNotch =
        new Dictionary<NotchGrouping.Notch, IBeltItem>();

    /// How many different things the ports wanted, so a caller can say why there is no probe.
    public int DistinctItems;
    public int DistinctFluids;

    public IBeltItem ItemFor(ILocalizedSimulation localized)
    {
        foreach (GlobalTileCoordinate tile in TilesOf(localized))
        {
            IBeltItem item;
            if (Items.TryGetValue(tile, out item))
            {
                return item;
            }
        }

        // A per-notch assignment beats the single probe, and the probe is what is left when a
        // factory takes only one thing.
        if (ByNotch.Count > 0)
        {
            foreach (GlobalTileCoordinate tile in TilesOf(localized))
            {
                NotchGrouping.Notch notch;
                int index;
                if (!NotchGrouping.TryNotchOf(tile, out notch, out index))
                {
                    continue;
                }

                IBeltItem item;
                if (ByNotch.TryGetValue(notch, out item))
                {
                    return item;
                }
            }
        }

        return ProbeItem;
    }

    public IFluid FluidFor(ILocalizedSimulation localized)
    {
        foreach (GlobalTileCoordinate tile in TilesOf(localized))
        {
            IFluid fluid;
            if (Fluids.TryGetValue(tile, out fluid))
            {
                return fluid;
            }
        }

        return ProbeFluid;
    }

    /// <summary>
    /// Works out whether a single probe can stand in for every port of each kind, once the
    /// map has been filled in.
    /// </summary>
    public void ResolveProbes()
    {
        List<IBeltItem> items = new List<IBeltItem>();
        foreach (IBeltItem item in Items.Values)
        {
            if (!items.Contains(item))
            {
                items.Add(item);
            }
        }

        List<IFluid> fluids = new List<IFluid>();
        foreach (IFluid fluid in Fluids.Values)
        {
            if (!fluids.Contains(fluid))
            {
                fluids.Add(fluid);
            }
        }

        DistinctItems = items.Count;
        DistinctFluids = fluids.Count;

        ProbeItem = items.Count == 1 ? items[0] : null;
        ProbeFluid = fluids.Count == 1 ? fluids[0] : null;
    }

    public static IEnumerable<GlobalTileCoordinate> TilesOf(ILocalizedSimulation localized)
    {
        if (!(localized is ILocalizedTileSimulation located))
        {
            yield break;
        }

        for (int i = 0; i < located.NumOccupiedTiles; i++)
        {
            yield return located.GetOccupiedTile(i);
        }
    }
}
