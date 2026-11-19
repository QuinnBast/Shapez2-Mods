using System.Collections.Generic;
using Game.Core.Coordinates;
using Game.Core.Rendering.Simulation;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Draws the items travelling through a tall in-platform lift.
///
/// Without this a tall lift moves items correctly and shows none of them: item drawing is the
/// renderer's job, and vanilla's <c>Lift2LayerSimulationRenderer</c> is typed to
/// <c>BeltLift2LayerSimulation</c>, so it never sees an <see cref="ExtraLiftSimulation"/>.
///
/// **Nothing registers this.** <c>GameSessionOrchestrator.CreateSimulationRenderers</c> finds
/// renderers by reflection over every loaded assembly:
///
/// <code>
/// Type[] first = (from type in GetAllLoadableTypes()
///     where typeof(IBuildingSimulationRenderer).IsAssignableFrom(type) &amp;&amp; !type.IsAbstract &amp;&amp; !type.IsInterface
///     select type).ToArray();
/// … renderingDependencyContainer.Create(type) …
/// </code>
///
/// so a public, non-abstract renderer in a mod assembly is picked up on its own - which also
/// means the constructor has to be one the dependency container can satisfy. <c>IMapModel</c>
/// is, because vanilla's lift renderer takes exactly that.
///
/// It reuses <c>Lift2LayerMetaBuildingDefinition.DrawData</c> because the stretched definitions
/// carry the vanilla lift's custom draw data verbatim; only the lane *geometry* is wrong for a
/// taller lift, and that is derived rather than authored - see
/// <see cref="ExtraLiftLaneGeometry"/>.
/// </summary>
public class ExtraLiftSimulationRenderer
    : StatelessBuildingSimulationRenderer<ExtraLiftSimulation, Lift2LayerMetaBuildingDefinition.DrawData>
{
    public ExtraLiftSimulationRenderer(IMapModel map)
        : base(map)
    {
    }

    // public, not protected: the publicizer raises the base method's visibility, and an override
    // cannot narrow it.
    public override void OnDrawDynamic(in Entity entity, FrameDrawOptions options)
    {
        ExtraLiftSimulation simulation = entity.Simulation;
        Lift2LayerMetaBuildingDefinition.DrawData drawData = entity.DrawData;
        int lanes = simulation.VerticalLanes.Length;

        ExtraLiftLaneGeometry geometry = ExtraLiftLaneGeometry.For(drawData, lanes);

        // Downstream first, matching vanilla's renderer. Order only matters for overdraw between
        // items that overlap at a lane boundary, but matching it keeps the look identical.
        DrawBeltItem(in entity.Transform, options, simulation.OutputLane, geometry.Output);

        for (int i = lanes - 1; i >= 0; i--)
        {
            DrawBeltItem(in entity.Transform, options, simulation.VerticalLanes[i], geometry.Vertical[i]);
        }

        DrawBeltItem(in entity.Transform, options, simulation.InputLane, drawData.InputLaneBeltDefinition);

        DrawShaft(in entity, options, lanes);
    }

    /// <summary>
    /// Fills the shaft above the lift's own body with more bodies.
    ///
    /// A tall lift's static mesh is still the authored two-layer one -
    /// <c>StaticBuildingMeshBuilder</c> draws exactly one copy, at
    /// <c>MainMeshPerLayer[tile.BuildingLayer()]</c> - so a twelve-layer lift is a two-layer body
    /// with ten layers of nothing above it and items sailing through the gap.
    ///
    /// Vanilla's in-platform lift has no separable shaft section to tile, unlike the space lift
    /// whose <c>SpaceLiftDrawerMetaData</c> ships entrance, connection and exit meshes separately.
    /// So this stacks whole bodies instead: two layers at a time, capped with a one-layer body
    /// when the height is odd. It reads as a segmented shaft rather than a seamless one, which is
    /// a deliberate compromise - a seamless version needs authored art, not code.
    ///
    /// Drawing only. Nothing here can affect the simulation or the save; the worst outcome is
    /// that it looks wrong.
    /// </summary>
    private void DrawShaft(in Entity entity, FrameDrawOptions options, int span)
    {
        // Shared with the pipe renderer and with the placement preview's stacked mesh, so the
        // three cannot lay a shaft out differently.
        TallShaft.Draw(options, in entity.Transform, entity.Definition, span, ExtraLiftBuildings.OneLayerBodies);
    }
}

/// <summary>
/// The lane positions for a lift of a given height, derived from the authored two-layer ones.
///
/// A <c>BeltLaneRenderingDefinition</c> is just a start and an end position that items are
/// lerped between, so a taller shaft is the same step repeated. The step itself is measured from
/// vanilla rather than assumed: its two vertical halves run start → middle → end, so one layer's
/// worth of climb is half of end minus start. Nothing here hardcodes how tall a layer is.
///
/// Cached because this is called per lift per frame - keyed by the lift's own draw data **and**
/// its lane count. Keyed by lane count alone it was wrong: the eight stock variants differ in
/// exactly what this derives from (an Up lift steps +1 a layer, a Down lift -1, and Forward,
/// Backward and Left exit in different directions), so every lift of a given height drew its
/// items along whichever variant happened to be drawn first. An Up lift drawn after a Down one
/// of the same height sent its items down through the floors below and they reappeared on the
/// belt at the top - which reads as "climbs a bit, then skips to the top".
/// </summary>
internal sealed class ExtraLiftLaneGeometry
{
    // Reference identity on the draw data: one instance per stock variant (and per mirrored copy),
    // shared by every stretched definition made from it.
    private static readonly Dictionary<(Lift2LayerMetaBuildingDefinition.DrawData, int), ExtraLiftLaneGeometry> Cache =
        new Dictionary<(Lift2LayerMetaBuildingDefinition.DrawData, int), ExtraLiftLaneGeometry>();

    public BeltLaneRenderingDefinition[] Vertical { get; private set; }

    public BeltLaneRenderingDefinition Output { get; private set; }

    public static ExtraLiftLaneGeometry For(Lift2LayerMetaBuildingDefinition.DrawData drawData, int lanes)
    {
        if (Cache.TryGetValue((drawData, lanes), out ExtraLiftLaneGeometry cached))
        {
            return cached;
        }

        LocalVector start = drawData.VerticalLaneFirstHalfBeltDefinition.ItemStartPos_L;
        LocalVector end = drawData.VerticalLaneSecondHalfBeltDefinition.ItemEndPos_L;

        // Vanilla's two halves cover two layers, so half the total is one layer's climb.
        LocalVector step = new LocalVector(
            (end.x - start.x) * 0.5f,
            (end.y - start.y) * 0.5f,
            (end.z - start.z) * 0.5f);

        BeltLaneRenderingDefinition[] vertical = new BeltLaneRenderingDefinition[lanes];
        for (int i = 0; i < lanes; i++)
        {
            vertical[i] = new BeltLaneRenderingDefinition
            {
                ItemStartPos_L = Offset(start, step, i),
                ItemEndPos_L = Offset(start, step, i + 1),
            };
        }

        // The exit sits at the top of the shaft, so it moves by however much taller this lift is
        // than the two-layer one the positions were authored for.
        LocalVector lift = Offset(default, step, lanes - 2);
        BeltLaneRenderingDefinition output = new BeltLaneRenderingDefinition
        {
            ItemStartPos_L = Add(drawData.OutputLaneBeltDefinition.ItemStartPos_L, lift),
            ItemEndPos_L = Add(drawData.OutputLaneBeltDefinition.ItemEndPos_L, lift),
        };

        ExtraLiftLaneGeometry geometry = new ExtraLiftLaneGeometry { Vertical = vertical, Output = output };
        Cache[(drawData, lanes)] = geometry;
        return geometry;
    }

    private static LocalVector Offset(LocalVector origin, LocalVector step, int times)
    {
        return new LocalVector(
            origin.x + step.x * times,
            origin.y + step.y * times,
            origin.z + step.z * times);
    }

    private static LocalVector Add(LocalVector a, LocalVector b)
    {
        return new LocalVector(a.x + b.x, a.y + b.y, a.z + b.z);
    }
}
