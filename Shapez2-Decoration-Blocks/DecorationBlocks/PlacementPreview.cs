using System;
using System.Collections.Generic;
using Game.Core.Coordinates;
using MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// Draws the real, textured mesh under the placement ghost.
///
/// ## The problem
///
/// `PlacementBuildingsDrawer` draws each pending building's `IsolatedBlueprintMesh` through
/// `SmartBuildingBlueprintRenderer`, in a flat themed colour - blue for "will be placed", red for
/// "cannot". That is vanilla behaviour and it is fine for the buildings it was written for,
/// because a belt or a cutter is a low, sparse shape and the ghost reads as a tint over it.
///
/// A decoration block is a **solid one by one by one cube**. Its ghost is therefore a solid blue
/// box that covers the thing entirely, and with twenty-one blocks that all ghost identically there
/// is no way to tell which one is on the cursor.
///
/// ## The two halves of the fix
///
/// The ghost itself becomes a **wireframe** - see `DecorationRegistrar`, which hands the blueprint
/// slot a frame of edge bars rather than the solid mesh. That leaves the footprint visible and the
/// middle open.
///
/// This class fills the middle: a postfix on `PlacementBuildingsDrawer.Draw` walks the same
/// pending-placement list the ghost just drew and submits the mod's own textured mesh at each
/// transform. The drawer is one of the few non-generic pieces of the placement pipeline -
/// `AreaPlacer`, `SinglePlacer` and `ModularEntityPlacer` are all generic types MonoMod cannot
/// hook - and its `RenderingData` already holds exactly what is needed: definition, transform and
/// whether the placement is allowed.
internal sealed class PlacementPreview : IDisposable
{
    /// A **function** of the pending transform rather than one mesh, because a torch's shape
    /// depends on where it is about to land: it stands upright in open air and mounts against a
    /// block behind it, and which of those it will be is only knowable once the cursor and the
    /// rotation are both known. Everything else registers a constant and never notices.
    private static readonly Dictionary<BuildingDefinitionId, Func<GlobalTileTransform, IMeshReference>>
        Meshes = new Dictionary<BuildingDefinitionId, Func<GlobalTileTransform, IMeshReference>>();

    private static ILogger Log;

    private readonly Hook Detour;

    public PlacementPreview(ILogger log)
    {
        Log = log;

        // Degrades rather than throws: a throw here comes out through ModLoadingStep.LoadMods and
        // takes the game's whole mod loading step with it, and a blue ghost is survivable.
        try
        {
            Detour = DetourHelper.CreatePostfixHook<PlacementBuildingsDrawer, FrameDrawOptions>(
                (drawer, options) => drawer.Draw(options),
                (drawer, options) => Draw(drawer, options));
        }
        catch (Exception exception)
        {
            log?.Exception?.LogException(exception);
        }
    }

    public void Dispose()
    {
        Detour?.Dispose();
    }

    /// Registered by both registrars as they build their definitions, so this needs no knowledge
    /// of what a block or a component is - only which mesh belongs to which id.
    public static void Register(BuildingDefinitionId id, IMeshReference mesh)
    {
        Meshes[id] = _ => mesh;
    }

    /// The same, for a definition whose ghost depends on where it is being placed. The resolver
    /// runs once per pending placement per frame, so it should be a lookup and not a rebuild.
    public static void Register(
        BuildingDefinitionId id, Func<GlobalTileTransform, IMeshReference> resolve)
    {
        Meshes[id] = resolve;
    }

    private static void Draw(PlacementBuildingsDrawer drawer, FrameDrawOptions options)
    {
        List<SmartBuildingBlueprintRenderer.DrawData> pending = drawer.RenderingData;

        if (pending == null || pending.Count == 0)
        {
            return;
        }

#pragma warning disable CS0618
        BlockMaterials materials = BlockMaterials.Shared(options.Theme.BaseResources, Log);
#pragma warning restore CS0618

        IMaterialReference material = materials?.Opaque;

        if (material == null || material.Empty)
        {
            return;
        }

        for (int i = 0; i < pending.Count; i++)
        {
            SmartBuildingBlueprintRenderer.DrawData entry = pending[i];

            if (entry.Definition == null
                || !Meshes.TryGetValue(entry.Definition.Id, out Func<GlobalTileTransform, IMeshReference> resolve))
            {
                continue;
            }

            IMeshReference mesh = resolve(entry.Transform);

            if (mesh == null)
            {
                continue;
            }

            // Nudged a hair toward the camera, the same trick and the same reason as
            // SmartBuildingBlueprintRenderer's own offset: the pending building sits exactly where
            // a placed one would, so without it the two z-fight wherever the cursor passes over
            // something already built.
            Matrix4x4 transform = FastMatrix.ByTransform(entry.Transform);

            options.Renderers.Buildings.Add(
                mesh, material, transform, options.LOD.Shadows, options.LOD.Shadows);
        }
    }
}
