using Game.Core.Content.Islands;
using ShapezShifter.Flow;

namespace TrainCargoTools
{
    /// Stops a dual-connector island drawing a red conflict cross while it is being placed.
    ///
    /// A cargo belt carries a belt-tagged *and* a pipe-tagged connector at each pivot, which is
    /// what lets one belt family serve shapes and fluid alike - see TrainCargoToolsMod.Connectors.
    /// The placement preview knows nothing about that pairing. `PlacementConnectorDrawer` walks
    /// connectors one at a time, and `IslandInstanceModel.CreateConnection` marks a connector
    /// conflicting whenever there is an island on the far side of it with no matching connector
    /// at that pivot:
    ///
    /// <code>
    /// if (island2.TryGetConnector(pivot, out var found) &amp;&amp; from.ConnectsTo(found))
    ///     return new IslandConnection(from, found);
    /// return new IslandConnection(from, isConflicting: true);
    /// </code>
    ///
    /// So dragging a cargo belt onto a shape train station connects the belt-tagged connector and
    /// conflicts the pipe-tagged one, at the same pivot, and the drawer paints both. The cross
    /// wins the player's attention and says the placement is wrong when it is in fact correct -
    /// the belt works, which is the part that made this worth fixing rather than explaining.
    ///
    /// `SkipConflictingConnectorsDrawingFlag` is the game's own opt-out, read by
    /// `PlacementConnectorDrawer.DrawEntityConnectors`, and vanilla sets it from a MetaIslandDefinition
    /// field (`RenderConflictIndicatorVisualization`) that a mod-built definition never passes
    /// through. Connected and not-connected markers are untouched, so the belt still shows a green
    /// arrow where it joins something.
    ///
    /// What it costs: an island with this flag never shows a cross, including where one would be
    /// deserved - a cargo belt aimed at a blank platform edge. That is an acceptable trade only
    /// because these islands accept nearly everything, so a genuinely wrong aim is rare and the
    /// false cross was constant.
    ///
    /// Done by decorating the builder rather than reaching into ShapezShifter's `IslandBuilder`,
    /// whose definition field is private. `BuildAndRegister` hands back the very definition it
    /// registered, and the flag is read at draw time, so attaching it afterwards is enough.
    ///
    /// **Guarded, because BuildAndRegister runs again on every scenario load** against the same
    /// definition objects. `CustomDataHolder.AddFlag` is a plain `Attach`, so a second load would
    /// leave two flags of one type in the holder - and `TryGet` does not quietly pick one, it
    /// throws `MultipleDataFitDataTypeQueryException` on `DataMatch.Multiple`. That would come
    /// out of `Has&lt;SkipConflictingConnectorsDrawingFlag&gt;` on a placement worker thread,
    /// every frame, on the second session of a run and never the first.
    internal sealed class SkipConflictMarkers : IIslandBuilder
    {
        private readonly IIslandBuilder Inner;

        public SkipConflictMarkers(IIslandBuilder inner)
        {
            Inner = inner;
        }

        public IslandDefinition BuildAndRegister(IslandDefinitionGroup group, GameIslands gameIslands)
        {
            IslandDefinition definition = Inner.BuildAndRegister(group, gameIslands);

            if (!definition.CustomData.Has<SkipConflictingConnectorsDrawingFlag>())
            {
                definition.CustomData.AddFlag<SkipConflictingConnectorsDrawingFlag>();
            }

            return definition;
        }
    }
}
