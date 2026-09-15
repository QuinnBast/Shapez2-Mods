using Game.Core.Coordinates;
using Unity.Mathematics;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// Finds the vortex, so entering first person puts you somewhere you recognise rather than
/// under whatever the map camera happened to be pointing at.
///
/// There is no "where is the hub" accessor to call. <c>HubObserver</c> knows, but it is
/// owned by <c>GameSessionOrchestrator</c> and reaching it from a camera hook means
/// plumbing a session hook through the mod for one coordinate. The map can answer directly:
/// islands are enumerable, and the hub is the one whose definition carries an
/// <c>IHubIslandConfiguration</c>. That is the same test
/// <c>IslandNotObliteratedByVortexDeathRay.Check</c> uses to recognise it.
/// </summary>
public static class FirstPersonSpawn
{
    /// <summary>
    /// Two island definitions answer to <c>IHubIslandConfiguration</c> - the vortex itself
    /// and the space converter hub - and <c>HubObserver</c> is constructed with both ids.
    /// The player means the vortex, so prefer that one and settle for the other only if
    /// there is no vortex in the map at all.
    /// </summary>
    public static bool TryFindVortex(IMapModel map, out double2 horizontal, out float height)
    {
        horizontal = double2.zero;
        height = 0f;

        if (map == null)
        {
            return false;
        }

        IHubIslandConfiguration found = null;
        GlobalChunkTransform foundTransform = default;

        foreach (IslandModel island in map.Islands)
        {
            if (!island.Definition.TryConfigAs<IHubIslandConfiguration>(out IHubIslandConfiguration config))
            {
                continue;
            }

            bool isVortex = config is HubDefinitionMetadata.Configuration;

            if (found == null || isVortex)
            {
                found = config;
                foundTransform = island.Transform;
            }

            if (isVortex)
            {
                break;
            }
        }

        if (found == null)
        {
            return false;
        }

        GlobalChunkCoordinate chunk = found.MainHubChunkPosition.ToGlobal(in foundTransform);

        // ToCenter_W puts the height at exactly z * 20, which is the island floor rather
        // than the middle of the chunk - so this is a standing position, and gravity only
        // has to settle the player onto whatever is built on top of it.
        float3 position = chunk.ToCenter_W();

        horizontal = new double2(position.x, position.z);
        height = position.y;
        return true;
    }
}
