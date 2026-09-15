using System.Collections.Generic;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// Fast travel, on the waypoints the player has already placed.
///
/// No new building, no new saved state and no picker UI, because the game already has all
/// three. `IPlayerWaypoints.Waypoints` is a saved list the player fills with the
/// checkpoint key, and every entry carries position, rotation, angle, zoom, building layer
/// and island layer - which is more than enough to put a body somewhere.
///
/// A dedicated teleporter building would need a mesh, a material (buildings cannot carry
/// their own), an icon, a toolbar entry, a research unlock, its own saved network state and
/// a destination picker. It would arrive at the same place this does.
///
/// This is deliberately the simplest thing that works: the key steps to the next waypoint
/// and goes. With a handful of waypoints that is quicker than any menu; past a dozen it
/// wants a picker, and the freed cursor already gives us somewhere to put one.
/// </summary>
public sealed class FirstPersonTravel
{
    /// <summary>
    /// Survives between presses so repeated taps walk the list rather than bouncing between
    /// the same two places. Starts before the first entry so the first press goes to
    /// waypoint one.
    /// </summary>
    private int Index = -1;

    public bool TryNext(Player player, out IPlayerWaypoint waypoint, out int position, out int count)
    {
        waypoint = null;
        position = 0;
        count = 0;

        IReadOnlyList<IPlayerWaypoint> waypoints = player?.HUDData?.Waypoints?.Waypoints;
        if (waypoints == null || waypoints.Count == 0)
        {
            return false;
        }

        count = waypoints.Count;

        // Modulo rather than a reset, so deleting a waypoint between presses lands
        // somewhere valid instead of throwing.
        Index = (Index + 1) % count;
        position = Index + 1;
        waypoint = waypoints[Index];
        return true;
    }

    public void Forget()
    {
        Index = -1;
    }
}
