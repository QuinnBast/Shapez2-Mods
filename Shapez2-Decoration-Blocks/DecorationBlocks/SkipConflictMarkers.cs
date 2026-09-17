using Game.Core.BuildingLogic.Data;
using ShapezShifter.Flow;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// Stops the converter drawing a red conflict cross while it is being placed next to redstone.
///
/// The converter is the only component here with connectors - a wire in at the back and a wire
/// out at the front - and placing one beside dust, a torch or a lamp paints a cross over the
/// connector that faces it. The placement reads as refused when it is in fact exactly the
/// placement the building exists for: redstone on one side, wire on the other.
///
/// `PlacementConnectorDrawer.DrawEntityConnectors` walks connectors one at a time and calls
/// `DrawConflict` for any connection marked conflicting, which is any connector with an entity on
/// the far side of it carrying no matching connector at that pivot. Every redstone component is
/// such an entity: they are buildings with `BuildingConnectors.SingleTile()` and no wire
/// connector at all. So the cross is *correct* by the drawer's rule and wrong about the world.
///
/// `SkipConflictingConnectorsDrawingFlag` is the game's own opt-out, checked immediately before
/// that `DrawConflict` call. Connected and not-connected markers are untouched, so the converter
/// still shows its wire connectors where they join something.
///
/// ## Why the builder chain cannot ask for this
///
/// It looks like it can, and that is the trap. `IBuildingGroupBuilder` has **two** methods whose
/// names both read like this one:
///
/// * `NotRenderingConnectorConflictIndicator()` sets `RenderConflictingConnectorIndicators`;
/// * `NotRenderingConflictingIndicatorVisualization()` sets `RenderConflictIndicatorVisualization`,
///   which is the field `BuildingDefinitionFactory` turns into this flag.
///
/// This mod was calling the first one, which is not it. But switching to the second would not
/// have worked either: **ShapezShifter never calls `BuildingDefinitionFactory`.**
/// `BuildingBuilder.WithConnectorData` news up a `BuildingDefinition` directly, so the group's
/// flag-producing fields are carried and never read, and no Shifter-built building can receive
/// this flag through the chain at any setting. Hence attaching it here, to the definition
/// `BuildAndRegister` hands back - the same seam, and the same reason, as the cargo mod's island
/// version of this file.
///
/// **Guarded, because `BuildAndRegister` runs again on every scenario load** against the same
/// definition objects, and `CustomDataHolder.AddFlag` is a plain `Attach`. A second load would
/// leave two flags of one type in the holder, and `TryGet` does not quietly pick one - it throws
/// `MultipleDataFitDataTypeQueryException` on `DataMatch.Multiple`. That would come out of
/// `Has&lt;SkipConflictingConnectorsDrawingFlag&gt;` on a placement worker thread, every frame, on
/// the second session of a run and never the first.
///
/// What it costs: the converter never shows a cross, including where one would be deserved. For
/// a building whose whole purpose is to sit between two things that do not otherwise connect,
/// that is the right trade.
internal sealed class SkipConflictMarkers : IBuildingBuilder
{
    private readonly IBuildingBuilder Inner;

    public SkipConflictMarkers(IBuildingBuilder inner)
    {
        Inner = inner;
    }

    public BuildingDefinition BuildAndRegister(BuildingDefinitionGroup group, GameBuildings gameBuildings)
    {
        BuildingDefinition definition = Inner.BuildAndRegister(group, gameBuildings);

        if (!definition.CustomData.Has<SkipConflictingConnectorsDrawingFlag>())
        {
            definition.CustomData.AddFlag<SkipConflictingConnectorsDrawingFlag>();
        }

        return definition;
    }
}
