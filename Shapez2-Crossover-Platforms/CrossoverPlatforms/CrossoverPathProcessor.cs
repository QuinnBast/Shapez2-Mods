using Core.Collections.Scoped;
using Game.Core.Coordinates;
using Game.Placement.Data;
using Game.Placement.MapManipulation;
using Game.Placement.Processing;
using Game.Placement.Utils;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Turns a blocked space path node into a crossing instead of leaving it for the lift.
    ///
    /// Dragging a space belt across an existing one produces an invalid node where the two meet.
    /// PathLiftingProcessor is what currently resolves that: it walks the run up a layer, over,
    /// and back down, which is why crossings cost four lift platforms and a layer of headroom.
    ///
    /// The game already knows how to do better, just not for space paths. PathCrossProcessor does
    /// exactly this for wires - a WireForward laid across a WireForward at a right angle becomes
    /// a wire bridge - and it is inserted ahead of the lifting processor so the node is no longer
    /// invalid by the time lifting looks at it. This is that idea with the two things vanilla's
    /// processor cannot express: a crossing whose two halves are different path types, and a
    /// choice between the straight and mirrored variants so the second path runs the right way.
    ///
    /// Only a plain Forward crossing a plain Forward at a right angle qualifies. A turn, a
    /// splitter, or an existing crossing falls through untouched and the lift still handles it.
    internal sealed class CrossoverPathProcessor : IPlacementProcessor
    {
        private readonly IslandPlacementAdapter PlacementAdapter = new();
        private readonly IslandAccessorAdapter AccessorAdapter = new();

        private readonly EntityPlacementIndexComparer<IslandPlacement> PlacementIndexComparer =
            new();

        private readonly IslandDefinitionId BeltForwardId;
        private readonly IslandDefinitionId PipeForwardId;

        /// Indexed by <see cref="CrossoverKind"/>, then by whether the mirrored variant is wanted.
        private readonly IIslandDefinition[,] Crossings;

        public CrossoverPathProcessor(
            IslandDefinitionId beltForwardId, IslandDefinitionId pipeForwardId,
            IIslandDefinition[,] crossings)
        {
            BeltForwardId = beltForwardId;
            PipeForwardId = pipeForwardId;
            Crossings = crossings;
        }

        public void Process(
            IPlacementData placementData, PlacementInputHolder placementInput, IMapModel realMap,
            IReadOnlyMapLayoutModel virtualMap, IPlacementErrors placementErrors)
        {
            // The nodes already accepted this drag have to count as part of the map, or a run
            // crossing two lines in a row would only see the first one.
            virtualMap = virtualMap.MapLayoutReadOnly.ExtendMapLayout(
                new PlacementAsPartOfMap(placementData, PlacementMapLayoutQuerySource.Map,
                    PlacementMapLayoutQuerySource.ValidPlacement)).ToModel();

            using ScopedList<IslandPlacement> blocked = ScopedList<IslandPlacement>.Get();
            PlacementAdapter.GetAllInvalidEntities(placementData, blocked);
            blocked.Sort(PlacementIndexComparer);

            foreach (IslandPlacement node in blocked)
            {
                if (!AccessorAdapter.TryGetEntity(virtualMap.MapLayoutReadOnly,
                        node.Descriptor.Transform.Position, out IslandDescriptor existing))
                {
                    continue;
                }

                if (!TryPickCrossing(existing, node.Descriptor, out IIslandDefinition crossing,
                        out GridRotation rotation))
                {
                    continue;
                }

                // The crossing stands where the existing segment stands; only its rotation is
                // ours to choose, and TryPickCrossing has chosen it.
                IslandDescriptor descriptor = PlacementAdapter.CreateDescriptor(
                    crossing, existing.Transform.WithRotation(rotation), existing.Configuration,
                    existing.State);

                if (PlacementAdapter.TryGetFirstValidEntityAt(placementData,
                        node.Descriptor.Transform.Position, out IslandPlacement occupant))
                {
                    PlacementAdapter.ReplaceEntity(placementData, PlacementAdapter.CreatePlacement(
                        descriptor, occupant.PlacementAllowability, occupant.PlacementIndex));
                }
                else
                {
                    PlacementAdapter.AddEntity(placementData, PlacementAdapter.CreatePlacement(
                        descriptor, PlacementAllowability.ValidPlacement, node.PlacementIndex));
                }

                PlacementAdapter.RemoveEntity(placementData, node);
            }
        }

        /// Which crossing replaces this node, and at what rotation - or none, if the two paths
        /// are not something a crossing can stand in for.
        private bool TryPickCrossing(
            in IslandDescriptor existing, in IslandDescriptor placed,
            out IIslandDefinition crossing, out GridRotation rotation)
        {
            crossing = null;
            rotation = GridRotation.NoRotate;

            bool existingIsBelt = existing.Definition.Id == BeltForwardId;
            bool existingIsPipe = existing.Definition.Id == PipeForwardId;
            bool placedIsBelt = placed.Definition.Id == BeltForwardId;
            bool placedIsPipe = placed.Definition.Id == PipeForwardId;

            if ((!existingIsBelt && !existingIsPipe) || (!placedIsBelt && !placedIsPipe))
            {
                return false;
            }

            GridRotation existingRotation = existing.Transform.Rotation;
            GridRotation placedRotation = placed.Transform.Rotation;

            // Two segments on the same axis are a head-on collision, not a crossing. The lift is
            // still the right answer there, so leave the node invalid and let it run.
            if (placedRotation == existingRotation ||
                placedRotation == existingRotation + GridRotation.Rotate180)
            {
                return false;
            }

            CrossoverKind kind = existingIsBelt
                ? (placedIsBelt ? CrossoverKind.BeltBelt : CrossoverKind.BeltPipe)
                : (placedIsPipe ? CrossoverKind.PipePipe : CrossoverKind.BeltPipe);

            // Path A is the crossing's West-to-East run, and on a mixed crossing it is the belt -
            // so a belt over a pipe has to be oriented by whichever of the two is the belt, not
            // by whichever happened to be there first.
            bool existingOnPathA = kind != CrossoverKind.BeltPipe || existingIsBelt;
            GridRotation pathA = existingOnPathA ? existingRotation : placedRotation;
            GridRotation pathB = existingOnPathA ? placedRotation : existingRotation;

            // A rotation maps the island's local East to rotation.ToChunkDirection(), and local
            // South is East turned clockwise, so an island placed at pathA runs its second path
            // towards pathA + RotateCW. When the other path runs the opposite way, the mirrored
            // variant - the one whose North-South run is reversed - is the one that fits.
            bool mirrored = pathB != pathA + GridRotation.RotateCW;

            rotation = pathA;
            crossing = Crossings[(int)kind, mirrored ? 1 : 0];
            return crossing != null;
        }
    }
}
