using Game.Core.Rendering.MeshGeneration;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// What one block's definition carries so the renderer can draw it.
///
/// IBuildingCustomDrawData is attached per definition and handed back to the simulation
/// renderer as `entity.DrawData`, which is the whole reason this works: one renderer class
/// serves all 21 blocks, and each block's own mesh arrives with the entity rather than
/// having to be looked up by definition id every frame.
///
/// The material is not stored here. It cannot be built until a session exists - it is cloned
/// from the visual theme, which nothing reaches at mod load - so the renderer asks
/// BlockMaterials for it on first draw and the flag below is all a definition needs to carry.
public sealed class BlockDrawData : IBuildingCustomDrawData, IBuildingMirrorableCustomDrawData
{
    public BlockDrawData(IMeshReference mesh, bool translucent)
    {
        Mesh = mesh;
        Translucent = translucent;
    }

    public IMeshReference Mesh { get; }

    public bool Translucent { get; }

    /// A cube mirrored is the same cube. Implementing the interface rather than leaving it off
    /// avoids the error BuildingDrawDataFactory logs for a mirrorable building whose custom
    /// draw data cannot mirror - which a decoration would hit the moment a player flipped a
    /// blueprint containing one.
    public IBuildingCustomDrawData Mirror(IMeshCache meshCache)
    {
        return this;
    }
}
