using System;
using UnityEngine;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// Bounds the resource-highlight sweep that runs while a miner is on the cursor.
///
/// Picking up a shape or fluid miner turns on a global placement helper -
/// <c>IslandPlacementHelperHighlightShapeResources</c> and its fluid twin - whose job is to
/// mark every patch you could drop it on. It is the only placement helper that looks at the
/// whole world rather than at the thing being placed, which is why a miner is the one island
/// that makes first person stutter and an ordinary platform does not.
///
/// Its loop is:
///
/// <code>
/// foreach (MapSuperChunk superChunk in map.SuperChunks)
///     if (!GeometryUtility.TestPlanesAABB(options.CameraPlanes, superChunkBounds)) continue;
///     foreach (IShapeMapResourceSource item in superChunk.ResourcesOfType&lt;…&gt;())
///         …
///         foreach (GlobalChunkCoordinate item2 in item.Chunks_G)   // one shape mesh + up to 11 planes
/// </code>
///
/// Every cost guard in it is derived from the camera, and first person defeats all of them
/// at once:
///
/// <list type="bullet">
/// <item><c>Viewport.Zoom > 4000</c> returns early. We report 80, so it never does.</item>
/// <item><c>InOverviewMode</c> is <c>Zoom > 1500</c> and drops the per-chunk stack of
/// indicator planes from ten to one. We are never in it.</item>
/// <item><c>CameraPlanes</c> is the real cull. A top-down camera's frustum is a short
/// pyramid that meets the ground and stops; an eye-level one is a wedge reaching to the far
/// plane, and the resource bounds it is tested against are a flat band
/// (<c>SpaceThemeBoundsProvider.ComputeResourceSourceBounds</c> overwrites their height with
/// the constants -50 and -22). So nearly every patch in the explored map passes, every
/// frame.</item>
/// </list>
///
/// Rather than reimplement the helper, narrow the frustum it is handed: swap the six camera
/// planes for a box around the player for the duration of the call and put them back after.
/// The game's own `TestPlanesAABB` then does the culling, the drawing is untouched, and the
/// bound is the one that means something to a player - **your placement reach**. A patch
/// further away than you can place on is not a hint, it is scenery.
/// </summary>
public static class FirstPersonPlacementHighlight
{
    /// <summary>
    /// Where the real planes go while ours are in place.
    ///
    /// A small stack rather than one buffer: these are used from two unrelated draw paths
    /// now - the miner highlight and the super-chunk drawer - and a single shared buffer
    /// would restore the wrong planes if they ever nested. Pre-allocated because this is a
    /// draw path and an array per call is not.
    /// </summary>
    private static readonly Plane[][] Saved =
    {
        new Plane[6], new Plane[6], new Plane[6], new Plane[6],
    };

    private static int Depth;

    /// <summary>
    /// Runs <paramref name="draw"/> with the camera frustum replaced by a box of
    /// <paramref name="radius"/> around the camera, horizontally. Vertically the box is left
    /// effectively unbounded: the resource bounds are pinned to a fixed height band that has
    /// nothing to do with where the player is standing, so clipping on height would hide
    /// patches rather than distant ones.
    /// </summary>
    public static void WithinRadius(FrameDrawOptionsNoLOD options, float radius, Action draw)
    {
        Plane[] planes = options.CameraPlanes;

        if (planes == null || planes.Length != 6 || Depth >= Saved.Length)
        {
            draw();
            return;
        }

        Plane[] saved = Saved[Depth++];
        Array.Copy(planes, saved, 6);

        Vector3 centre = (Vector3)options.CameraPosition_W;
        const float height = 100000f;

        // Unity's TestPlanesAABB treats the normals as pointing inwards and rejects a box
        // that is wholly behind any one of them, so six inward faces are a box test.
        planes[0] = new Plane(Vector3.right, centre + Vector3.left * radius);
        planes[1] = new Plane(Vector3.left, centre + Vector3.right * radius);
        planes[2] = new Plane(Vector3.forward, centre + Vector3.back * radius);
        planes[3] = new Plane(Vector3.back, centre + Vector3.forward * radius);
        planes[4] = new Plane(Vector3.up, centre + Vector3.down * height);
        planes[5] = new Plane(Vector3.down, centre + Vector3.up * height);

        try
        {
            draw();
        }
        finally
        {
            Array.Copy(saved, planes, 6);
            Depth--;
        }
    }
}
