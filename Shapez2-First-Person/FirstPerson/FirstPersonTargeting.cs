using Game.Core.Coordinates;
using Game.HUD.CameraManager;
using Unity.Mathematics;
using UnityEngine;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// What the crosshair is pointing at.
///
/// The game asks "what is under the cursor" in two different ways, and only one of them
/// survives a camera at head height:
///
/// <list type="bullet">
/// <item><c>TryFindBuildingAtCursor</c> and <c>TryFindIslandAtCursor</c> walk the cursor
/// ray tile by tile and test each entity's collision boxes. No ground plane is involved,
/// so hovering, selecting and deleting are correct at any angle and need nothing from us
/// beyond pointing the cursor at the middle of the screen.</item>
/// <item><c>TryGetTileCoordinateAtCursor</c> - which every placer uses - intersects the
/// cursor ray with a flat plane at <c>Viewport.Height</c>. From head height that ray is
/// nearly parallel to the plane, and <c>GetCursorPointOnVirtualPlane</c> answers
/// <c>new double2(0)</c> on a miss, which is the map origin rather than a failure. Left
/// alone it builds at the centre of the map.</item>
/// </list>
///
/// So this class does two small things: it reports the screen centre as the cursor, and it
/// replaces the plane intersection with the same intersection plus the two guards it is
/// missing - refuse a ray that never meets the plane, and refuse one that meets it beyond
/// arm's reach. Everything downstream, including the pipette and mass selection, then
/// works unmodified.
/// </summary>
public sealed class FirstPersonTargeting
{
    /// <summary>
    /// Scales both reaches while flying. Set by the camera each frame: a placement hook is
    /// called from the game's own code and cannot reach back into the body.
    /// </summary>
    public float ReachMultiplier = 1f;

    /// Last frame the ray was solved on, and what it produced. See Refresh.
    private int CachedFrame = -1;
    private float CachedHeight = float.NaN;
    private bool CachedValid;
    private double3 CachedPoint;
    private double CachedEnter;

    public static float2 ScreenCentre => new float2(Screen.width * 0.5f, Screen.height * 0.5f);

    /// <summary>
    /// Replaces <c>ScreenUtils.TryGetTileCoordinateAtCursor</c>.
    ///
    /// The plane is at <c>Viewport.Height</c>, which is
    /// <c>islandLayer * 20 + buildingLayer</c> - the layer the player has chosen to build
    /// on, not the surface they happen to be standing on. Standing on top of a belt and
    /// building on the floor beside it is therefore normal and works.
    /// </summary>
    public bool TryGetTile(Viewport viewport, out GlobalTileCoordinate tile_G)
    {
        tile_G = default;

        if (!TryReach(viewport, FirstPersonTuning.Reach * ReachMultiplier, out double3 hit))
        {
            return false;
        }

        tile_G = ((WorldCoordinate)(float3)hit).ToGlobalTileCoordinate();
        tile_G.z = (short)(viewport.IslandLayer * 20 + viewport.BuildingLayer);
        return true;
    }

    /// <summary>
    /// Replaces <c>ScreenUtils.TryGetChunkCoordinateAtCursor</c>, which island placement
    /// uses. Placing a platform from inside one is a strange thing to want, but the
    /// unguarded version would drop it on the map origin, so it gets the same treatment
    /// with a reach measured in chunks rather than tiles.
    /// </summary>
    public bool TryGetChunk(Viewport viewport, out GlobalChunkCoordinate chunk_GC)
    {
        chunk_GC = GlobalChunkCoordinate.Origin;

        if (!TryReach(viewport, FirstPersonTuning.ChunkReach * ReachMultiplier, out double3 hit))
        {
            return false;
        }

        chunk_GC = ((WorldCoordinate)(float3)hit).ToGlobalChunkCoordinate();
        chunk_GC.z = viewport.IslandLayer;
        return true;
    }

    /// <summary>
    /// Whether the crosshair is on the build plane for real, rather than being clamped.
    /// Only the crosshair's own colour uses this - placement takes the clamped answer.
    /// </summary>
    public bool HasExactTarget(Viewport viewport, float reach)
    {
        Refresh(viewport);
        return CachedValid && CachedEnter <= reach;
    }

    /// <summary>
    /// The centre-screen ray against the build plane, **clamped** to arm's reach rather than
    /// refused when it misses or overshoots. <c>enter</c> is the distance along the ray, so
    /// the limit is arm's reach rather than ground distance - looking down steeply gives you
    /// a short reach in front of your feet, which is the behaviour you want.
    ///
    /// The clamp is not a nicety, and refusing was actively wrong. Refusing made the game
    /// throw:
    ///
    /// <code>
    /// InvalidOperationException: Stack empty.
    ///   at Stack`1[T].Peek ()
    ///   at PathNotchClampRotationPlacementTracker.UseNotchRotationTracker (…)
    /// </code>
    ///
    /// A path placer - every belt, pipe, wire and space belt - fills its `SegmentsStack` from
    /// `UpdateDraggedPosition`, which is only called when the cursor query **succeeds**. The
    /// trackers then `Peek()` that stack unconditionally, because in vanilla the query never
    /// fails: `GetCursorPointOnVirtualPlane` answers with the map origin rather than nothing.
    /// Making it honest left the stack empty and the `Peek` unguarded.
    ///
    /// The throw is caught by `EntityPlacementRunner.UpdateCurrentPlacer`, which cancels the
    /// placement - and cancelling **deselects the toolbar entry**. That is what made the
    /// wheel look broken: select a belt while not looking at the floor, the placer throws,
    /// the belt is deselected back to its category, and the next scroll starts from the
    /// category again. 606 of these in one session. It also explains why looking down at the
    /// platform made scrolling behave: with a real target the stack is filled and nothing
    /// throws.
    ///
    /// Guarding the `Peek` is not available - `PathStartRotationPlacementTracker` is a
    /// **generic type**, which MonoMod cannot hook. So the query has to answer, always.
    ///
    /// Clamping rather than returning the origin keeps the protection that made this refuse
    /// in the first place: the answer is always within reach of the player, so a level camera
    /// still cannot drop a building at the centre of the map.
    /// </summary>
    private bool TryReach(Viewport viewport, float reach, out double3 hit)
    {
        Refresh(viewport);

        if (CachedValid && CachedEnter <= reach)
        {
            hit = CachedPoint;
            return true;
        }

        hit = Clamp(viewport, reach);
        return true;
    }

    /// <summary>
    /// As far as the player can reach, in the direction they are facing, on the build plane.
    ///
    /// The look direction is flattened onto the plane rather than followed, because the cases
    /// that get here are exactly the ones where following it does not meet the plane at all.
    /// A degenerate flat direction - looking straight up - falls back to the player's own
    /// position, which is predictable and cannot be anywhere surprising.
    /// </summary>
    private static double3 Clamp(Viewport viewport, float reach)
    {
        Transform camera = viewport.MainCamera.transform;
        Vector3 eye = camera.position;
        Vector3 flat = new Vector3(camera.forward.x, 0f, camera.forward.z);

        Vector3 ground = new Vector3(eye.x, viewport.Height, eye.z);
        float length = flat.magnitude;

        if (length > 0.0001f)
        {
            ground += flat / length * reach;
        }

        return new double3(ground.x, ground.y, ground.z);
    }

    /// <summary>
    /// Recomputes the centre-screen ray against the build plane, at most once a frame.
    ///
    /// This is not premature caching. Placement asks "what is under the cursor" through
    /// <c>ScreenUtils</c> many times in a single frame - per candidate chunk while an island
    /// is being positioned, plus the trackers and the preview - and every one of those calls
    /// lands in <c>RaycastHelpers.CustomScreenPointToRayDouble</c>, which does two 4x4 matrix
    /// inversions and two native camera property reads:
    ///
    /// <code>
    /// double4x4 a  = math.inverse((double4x4)(float4x4)camera.projectionMatrix);
    /// double4x4 a2 = (float4x4)camera.worldToCameraMatrix.inverse;
    /// </code>
    ///
    /// Within a frame every one of those calls produces the same answer: the camera has
    /// already been written for this frame by the time anything asks, the cursor is fixed at
    /// the screen centre, and the plane height does not move. Placing extractors from the air
    /// was paying for that work dozens of times a frame to get one number.
    ///
    /// Keyed on the height as well as the frame because the two callers could in principle
    /// be handed different planes, and because <c>Viewport.Height</c> is animated when the
    /// layer changes - a stale hit there would put a building a layer out.
    /// </summary>
    private void Refresh(Viewport viewport)
    {
        int frame = Time.frameCount;
        float height = viewport.Height;

        if (frame == CachedFrame && height.Equals(CachedHeight))
        {
            return;
        }

        CachedFrame = frame;
        CachedHeight = height;
        CachedValid = RaycastHelpers.TryGetCursorPointOnVirtualPlane(
            ScreenCentre, height, viewport.MainCamera, out CachedPoint, out CachedEnter);
    }
}
