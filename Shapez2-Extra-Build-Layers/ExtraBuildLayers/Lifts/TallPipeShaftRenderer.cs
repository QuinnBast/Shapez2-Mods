using Game.Core.Coordinates;
using Game.Core.Rendering.Simulation;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Fills the shaft of a tall in-platform pipe, by wrapping the renderer the game already uses
/// for two-layer ones.
///
/// Belts got this from their own <see cref="ExtraLiftSimulationRenderer"/>, which works because
/// a tall belt lift has its own simulation type and a simulation renderer binds on exactly that.
/// A pipe cannot: it is claimed by <c>FluidNetworkSystem</c> - by connector type, so every pipe
/// in the game shares one simulation - and a renderer bound there would draw for vanilla pipes
/// too.
///
/// The seam instead is <c>FluidNetworkRenderer</c>'s per-definition map:
///
/// <code>
/// // GameSessionOrchestrator.CreateSimulationRenderers
/// var value2 = new PipeUpDownCustomFluidNetworkBuildingRenderer(SimulationSpeed);
/// foreach (id in Layer1Pipe + Layer2Pipe groups' definitions) dictionary3.Add(id, value2);
/// list2.Add(new FluidNetworkRenderer(pipeIds, …, dictionary3));
/// </code>
///
/// It is built by enumerating the **groups**, and the stretched pipes are internal variants of
/// the Layer2Pipe group - so they are already in that map, already pointing at vanilla's
/// renderer. This decorates that entry rather than replacing it: vanilla still draws the fluid,
/// and this adds the bodies above it.
/// </summary>
internal sealed class TallPipeShaftRenderer : ICustomFluidNetworkBuildingRenderer
{
    private readonly ICustomFluidNetworkBuildingRenderer Inner;
    private readonly int Span;

    public TallPipeShaftRenderer(ICustomFluidNetworkBuildingRenderer inner, int span)
    {
        Inner = inner;
        Span = span;
    }

    /// <summary>
    /// Delegated, not reimplemented. Whatever per-building state vanilla's renderer wants for a
    /// two-layer pipe it wants for a twelve-layer one, and inventing a different answer here
    /// would silently change how the fluid draws.
    /// </summary>
    public bool TryCreateCustomState(out IFluidNetworkBuildingState fluidNetworkBuildingState)
    {
        if (Inner != null)
        {
            return Inner.TryCreateCustomState(out fluidNetworkBuildingState);
        }

        fluidNetworkBuildingState = null;
        return false;
    }

    public void Draw(
        FrameDrawOptions drawOptions,
        FluidNetworkRenderer.FluidNetworkRenderingState renderingState,
        BuildingInstance buildingInstance,
        IFluidNetwork fluidNetwork)
    {
        Inner?.Draw(drawOptions, renderingState, buildingInstance, fluidNetwork);

        TallShaft.Draw(
            drawOptions,
            buildingInstance.Transform,
            buildingInstance.Definition,
            Span,
            ExtraLiftBuildings.OneLayerBodies);
    }
}

/// <summary>
/// Stacks whole bodies up a shaft, shared by the belt and pipe paths.
///
/// <c>StaticBuildingMeshBuilder</c> draws exactly one body, at
/// <c>MainMeshPerLayer[tile.BuildingLayer()]</c>, so a stretched lift or pipe is a two-layer
/// body with the rest of its height empty. Neither family has a separable shaft section to tile
/// - unlike the space lift, whose <c>SpaceLiftDrawerMetaData</c> ships entrance, connection and
/// exit meshes apart from each other - so this repeats the whole body two layers at a time and
/// caps an odd height with a one-layer one.
///
/// Drawing only: nothing here can reach the simulation or the save.
/// </summary>
internal static class TallShaft
{
    public static void Draw(
        FrameDrawOptions options,
        in GlobalTileTransform transform,
        IBuildingDefinition definition,
        int span,
        System.Collections.Generic.Dictionary<BuildingDefinitionId, ILODMesh> singles)
    {
        // The shaft is normally part of the building's static mesh (TallBlueprints). This is the
        // fallback for a definition whose stack could not be built.
        if (span <= 2 || definition == null || TallBlueprints.StaticallyStacked.Contains(definition.Id) ||
            !definition.CustomData.TryGet(out IBuildingDrawData drawData))
        {
            return;
        }

        ILODMesh[] perLayer = drawData.MainMeshPerLayer;
        if (perLayer == null || perLayer.Length == 0)
        {
            return;
        }

        // The same entry the chunk drawer picked for the body already on screen, so the stacked
        // copies match it rather than the ground-floor variant.
        int index = transform.Position.BuildingLayer();
        ILODMesh body = perLayer[index < perLayer.Length ? index : perLayer.Length - 1];

        singles.TryGetValue(definition.Id, out ILODMesh single);

        int direction = Direction(definition);
        int bodies = ExtraBodies(span);
        for (int k = 1; k <= bodies; k++)
        {
            DrawBody(options, in transform, body, BodyZ(k) * direction);
        }

        if (NeedsCap(span) && single != null)
        {
            DrawBody(options, in transform, single, CapZ(span) * direction);
        }
    }

    // The layout of a shaft, in one place, because two things draw it: the renderers above for a
    // placed building, and ExtraLiftBuildings' stacked blueprint mesh for the placement preview.
    // If the two computed it separately, the preview would stop matching the building it turns into.
    //
    // A span-n piece is the stock two-layer body at its own position, then more two-layer bodies
    // two layers apart, then a one-layer body to cap an odd height: a five-layer lift is bodies
    // at 0 and 2 and a cap at 4.

    /// <summary>Two-layer bodies beyond the one already drawn at the building's own position.</summary>
    internal static int ExtraBodies(int span) => span <= 2 ? 0 : (span - 2) / 2;

    /// <summary>Layer offset of the <paramref name="k"/>-th extra body, counting from 1.</summary>
    internal static int BodyZ(int k) => 2 * k;

    internal static bool NeedsCap(int span) => span > 2 && span % 2 == 1;

    internal static int CapZ(int span) => span - 1;

    /// <summary>
    /// +1 for a piece that climbs, -1 for one that descends.
    ///
    /// The stock Down lift has its input at z 0 and its output at z -2, so its tiles and its body
    /// run downwards from the building's position - see <c>ExtraLiftBuildings.Stretch</c>, which
    /// keeps that sign when it stretches. Stacking a Down shaft upwards would put the bodies on
    /// the floors *above* a lift that goes below. Read off the tile bounds rather than recorded,
    /// because they are what <c>Stretch</c> wrote and nothing else can disagree with them.
    /// </summary>
    internal static int Direction(IBuildingDefinition definition) =>
        definition != null &&
        definition.CustomData.TryGet(out IBuildingConnectorData connectors) &&
        connectors.TileBounds.Min.z < 0
            ? -1
            : 1;

    /// <summary>
    /// What <c>SimulationRenderer.DrawMesh</c> does, spelled out because the fluid path has no
    /// such base class to inherit it from: resolve the mesh at the frame's building LOD, take the
    /// one shared building material at its own LOD, and submit to the instanced buildings
    /// renderer. A building cannot carry its own material, so the material is not a choice.
    /// </summary>
    private static void DrawBody(FrameDrawOptions options, in GlobalTileTransform transform, ILODMesh lodMesh, int z)
    {
        if (lodMesh == null || !lodMesh.TryGet(options.LOD.BuildingLOD, out IMeshReference handle))
        {
            return;
        }

        LocalVector offset = new LocalVector(0f, 0f, z);
        WorldCoordinate translation = offset * transform;

#pragma warning disable 0618   // FrameDrawOptionsNoLOD.Theme: a static helper has no injection point.
        options.Renderers.Buildings.Add(
            handle,
            options.Theme.BaseResources.BuildingMaterial[options.LOD.BuildingMaterialLOD],
            FastMatrix.TranslateRotate(in translation, transform.Rotation));
#pragma warning restore 0618
    }
}
