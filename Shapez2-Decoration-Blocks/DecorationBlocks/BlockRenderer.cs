using JetBrains.Annotations;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// Draws every decoration block, with the mod's own material instead of the game's.
///
/// Why a renderer and not a mesh. A building's world visual comes from
/// BuildingDrawData.MainMeshPerLayer, which IslandChunkStaticBuildingsDrawer combines per chunk
/// and submits with one shared material - Theme.BaseResources.BuildingMaterial. Nothing in that
/// path is per definition, and colour on the shared material is a UV0 lookup into an atlas that
/// is authored Unity data. So a building cannot carry its own texture through the static path
/// at all. The blocks therefore ship an empty main mesh and are drawn here.
///
/// Why this base class and not an IMapSubDrawer. StatelessBuildingSimulationRenderer already is
/// the sub-drawer, and it already does the bookkeeping by hand would mean writing: it caches
/// entities per GlobalChunkCoordinate, registers and unregisters them with the simulation, walks
/// only the chunks the culler returned, applies the layer visibility predicate, and frustum-tests
/// each entity's bounds before calling OnDrawDynamic. It also needs no registration - renderers
/// are discovered by reflection over every loaded assembly, which reaches a mod's as readily as
/// the game's - so there is no hook to install and nothing to unwind on reload.
///
/// One renderer covers all 21 blocks because renderers are keyed by simulation type and
/// every block shares InertSimulation. Each block's own mesh arrives as entity.DrawData, which
/// is the definition's IBuildingCustomDrawData.
[UsedImplicitly]
public class BlockRenderer : StatelessBuildingSimulationRenderer<InertSimulation, BlockDrawData>
{
    public BlockRenderer(IMapModel map)
        : base(map) { }

    /// Blocks follow the *static* building distance, not the dynamic-simulation one.
    ///
    /// The default is `lod.ShouldDrawDynamicBuildingSimulations`, which is right for the thing
    /// it was written for - the items moving through a machine stop being worth drawing long
    /// before the machine does. A decoration block is not an animation on top of a building, it
    /// is the building, so it has to survive to the same distance as the machine beside it or a
    /// decorated platform dissolves while the factory around it stays.
    public override bool ShouldDraw(LODRenderConfig lod)
    {
        return lod.ShouldRenderBuildings;
    }

    public override void OnDrawDynamic(in Entity entity, FrameDrawOptions options)
    {
        BlockDrawData drawData = entity.DrawData;

        if (drawData?.Mesh == null)
        {
            return;
        }

        // options.Theme is [Obsolete("should be injected"), but it is how the game's own
        // building drawers reach the materials and there is no injected alternative a renderer
        // can ask for - CreateSimulationRenderers binds the map, the mode and the shape
        // registry, and nothing else.
#pragma warning disable CS0618
        BlockMaterials materials = BlockMaterials.Shared(options.Theme.BaseResources, Log);
#pragma warning restore CS0618

        // Always the opaque material, including for glass. Alpha clipping does not work in the
        // shipped build (see CubeMesh.Frame), so every block is now geometry with no transparent
        // texels in it - a see-through block is a frame rather than a textured cube.
        IMaterialReference material = materials.Opaque;

        if (material == null || material.Empty)
        {
            return;
        }

        // FastMatrix.ByTransform is what StaticBuildingMeshBuilder uses for a vanilla main mesh,
        // so a block lands exactly where its own collider and blueprint mesh already are - those
        // are placed by the game through that same call.
        options.Renderers.Buildings.Add(
            drawData.Mesh,
            material,
            FastMatrix.ByTransform(entity.Transform),
            options.LOD.Shadows,
            options.LOD.Shadows);
    }

    /// Set by the mod constructor. The renderer is built by the game's dependency container,
    /// which has no logger to hand it, and the material probe has something worth saying the
    /// first time it runs.
    internal static Core.Logging.ILogger Log { get; set; }
}
