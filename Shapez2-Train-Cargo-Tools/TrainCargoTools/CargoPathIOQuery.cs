using Game.Content.Features.SpacePaths.IslandIO;
using Game.Core.Coordinates;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Lets a dragged cargo belt snap to a belt-tagged *or* a pipe-tagged neighbour.
    ///
    /// A path placer works out which way to face by asking one question of whatever is already
    /// on the map: "does the island at this position have a single output, and where does it
    /// point". That question is `EntityConnectionWorldIOQuery`, and it is typed - it looks for
    /// connectors that are exactly its `TOutput`:
    ///
    /// <code>
    /// public bool TryGetUniqueOutput(...) => TryGetConnection&lt;TOutput&gt;(map, position, out direction);
    /// </code>
    ///
    /// The cargo belt placer is typed on the belt pair, because a cargo belt carries both tags and
    /// something has to be picked. The consequence is that a fluid packager, whose only output is
    /// a `SpacePipeOutputConnector`, is invisible to it: the query finds nothing, the placer has
    /// no direction to snap to, and the player has to aim the run by hand even though it connects
    /// perfectly once placed.
    ///
    /// There is no way to type the query on both. `TInput`/`TOutput` are constrained
    /// `class, IEntityConnector, new()`, so the shared interface these connectors do have -
    /// `ISpacePathOutputConnector` - cannot be used: an interface has no `new()`.
    ///
    /// So ask twice. Two vanilla queries, one per tag, and a connector of either kind counts. That
    /// keeps every rule about *what* counts as a connector inside the game's own class, which
    /// matters because `ComputeConnectors` runs the whole extender stack - notches, foundations,
    /// universal connectors - and none of that is worth reimplementing to add an `or`.
    ///
    /// Belt first, so a cargo belt meeting another cargo belt - which has both tags - resolves the
    /// same way it always did.
    internal sealed class EitherTagIOQuery : IWorldIOQuery<GlobalChunkCoordinate, ChunkDirection>
    {
        private readonly IWorldIOQuery<GlobalChunkCoordinate, ChunkDirection> Belts;
        private readonly IWorldIOQuery<GlobalChunkCoordinate, ChunkDirection> Pipes;

        public EitherTagIOQuery(IslandAccessorAdapter islands)
        {
            Belts = Query<SpaceBeltInputConnector, SpaceBeltOutputConnector>(islands);
            Pipes = Query<SpacePipeInputConnector, SpacePipeOutputConnector>(islands);
        }

        public bool TryGetUniqueInput(
            IReadOnlyMapLayoutModel map, GlobalChunkCoordinate position, out ChunkDirection direction)
        {
            return Belts.TryGetUniqueInput(map, position, out direction)
                || Pipes.TryGetUniqueInput(map, position, out direction);
        }

        public bool TryGetUniqueOutput(
            IReadOnlyMapLayoutModel map, GlobalChunkCoordinate position, out ChunkDirection direction)
        {
            return Belts.TryGetUniqueOutput(map, position, out direction)
                || Pipes.TryGetUniqueOutput(map, position, out direction);
        }

        public bool HasInput(
            IReadOnlyMapLayoutModel map, GlobalChunkCoordinate position, ChunkDirection direction)
        {
            return Belts.HasInput(map, position, direction)
                || Pipes.HasInput(map, position, direction);
        }

        public bool HasOutput(
            IReadOnlyMapLayoutModel map, GlobalChunkCoordinate position, ChunkDirection direction)
        {
            return Belts.HasOutput(map, position, direction)
                || Pipes.HasOutput(map, position, direction);
        }

        /// The generic soup in one place. Islands are chunk-space entities, so every coordinate
        /// type is the chunk one and the connector model is IslandConnector.
        private static IWorldIOQuery<GlobalChunkCoordinate, ChunkDirection> Query<TInput, TOutput>(
            IslandAccessorAdapter islands)
            where TInput : class, IEntityConnector, new()
            where TOutput : class, IEntityConnector, new()
        {
            return new EntityConnectionWorldIOQuery<
                IslandDescriptor, GlobalChunkPivot, GlobalChunkTransform, GlobalChunkCoordinate,
                ChunkVector, ChunkDirection, LocalChunkPivot, TInput, TOutput, IslandConnector>(
                islands);
        }
    }
}
