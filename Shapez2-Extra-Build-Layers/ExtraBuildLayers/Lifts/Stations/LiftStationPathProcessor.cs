using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core.Coordinates;
using Game.Placement.Data;
using Game.Placement.MapManipulation;
using Game.Placement.Processing;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Turns a dragged climb that no lift fits into an entry station at the bottom and an exit station
/// at the top.
///
/// How the drag tool builds a climb (<c>PathAnchorsAndFillingProcessor</c>): it turns the dragged
/// segments into a list of anchor pivots, places one island per consecutive pair of anchors by
/// asking the definition matcher for something with an input at the first and an output at the
/// second, and <c>FillBetweenAnchors</c> deliberately places nothing along a vertical stretch. A
/// climb is therefore exactly one matcher request, and taller than vanilla's lift it matches
/// nothing and the drag leaves a gap.
///
/// This runs right after that processor, recomputes the same anchors, and for each climb it finds
/// with no island at its foot adds the two stations - so they show in the preview, are checked
/// against the map with everything else, and are committed, undone and redone as ordinary
/// islands. A climb vanilla's own lift covers is left alone.
///
/// The anchors are recomputed with the game's own <c>DecomposeManhattanPath</c> and the same
/// pairing rules, transcribed from the private <c>ComputeAnchors</c> of a generic type this mod
/// cannot call. A climb can come out of the anchors in two shapes, and both are handled: one pair
/// spanning two levels (input below, output above), or a pair ending in a vertical direction
/// followed by a pair starting in one, with the vertical stretch between them.
/// </summary>
internal sealed class LiftStationPathProcessor : IPlacementProcessor
{
    private readonly IslandPlacementAdapter PlacementAdapter = new IslandPlacementAdapter();
    private readonly IslandAccessorAdapter AccessorAdapter = new IslandAccessorAdapter();
    private readonly ChunkSpace Space = new ChunkSpace();

    private readonly IIslandDefinition Entry;
    private readonly IIslandDefinition Exit;

    public LiftStationPathProcessor(IIslandDefinition entry, IIslandDefinition exit)
    {
        Entry = entry;
        Exit = exit;
    }

    public void Process(
        IPlacementData placementData, PlacementInputHolder placementInput, IMapModel realMap,
        IReadOnlyMapLayoutModel virtualMap, IPlacementErrors placementErrors)
    {
        IPathPlacementInput<GlobalChunkCoordinate, GlobalChunkPivot, ChunkDirection> input =
            placementInput.As<IPathPlacementInput<GlobalChunkCoordinate, GlobalChunkPivot, ChunkDirection>>();
        if (input == null || input.SegmentsStack.Count == 0)
        {
            return;
        }

        List<GlobalChunkPivot> anchors = new List<GlobalChunkPivot>();
        ComputeAnchors(anchors, input.SegmentsStack.Reverse().ToArray());

        for (int i = 0; i + 1 < anchors.Count; i += 2)
        {
            GlobalChunkPivot from = anchors[i];
            GlobalChunkPivot to = anchors[i + 1];

            // Shape 1: one pair spanning levels.
            if (from.Position.z != to.Position.z && SameColumn(from.Position, to.Position))
            {
                TryAddClimb(placementData, virtualMap, from, to);
                continue;
            }

            // Shape 2: this pair turns into the vertical, the next one turns out of it.
            if (IsVertical(to.Direction) && i + 3 < anchors.Count)
            {
                GlobalChunkPivot nextFrom = anchors[i + 2];
                GlobalChunkPivot nextTo = anchors[i + 3];
                if (IsVertical(nextFrom.Direction) && nextTo.Position.Equals(nextFrom.Position) &&
                    SameColumn(from.Position, nextTo.Position) && from.Position.z != nextTo.Position.z)
                {
                    TryAddClimb(placementData, virtualMap, from, nextTo);
                }
            }
        }
    }

    /// <summary>
    /// Adds the pair for a climb whose bottom (input side) is <paramref name="from"/> and top
    /// (output side) is <paramref name="to"/>, unless something already stands at the foot -
    /// which is vanilla's lift, matched because it fits.
    /// </summary>
    private void TryAddClimb(
        IPlacementData placementData, IReadOnlyMapLayoutModel map, GlobalChunkPivot from, GlobalChunkPivot to)
    {
        GlobalChunkCoordinate entryAt = from.Position;
        GlobalChunkCoordinate exitAt = to.Position;
        if (PlacementAdapter.HasAnyEntityAt(placementData, entryAt) || PlacementAdapter.HasAnyEntityAt(placementData, exitAt))
        {
            return;
        }

        int rise = exitAt.z - entryAt.z;

        // Items arrive travelling along from.Direction, so the entry's input faces back the
        // other way - what AddAnchor does with WithDirection(Opposite). Station inputs are
        // declared on West and outputs on East, as vanilla straight segments are.
        GridRotation entryRotation = RotationFor(ChunkDirection.West, from.Direction.Opposite);
        GridRotation exitRotation = RotationFor(ChunkDirection.East, to.Direction);

        // The shaft passes through every level between the two; another island there would sit
        // inside it, so the climb is shown as blocked rather than built through it.
        bool shaftClear = true;
        int step = Math.Sign(rise);
        for (int z = entryAt.z + step; z != exitAt.z; z += step)
        {
            GlobalChunkCoordinate through = new GlobalChunkCoordinate(entryAt.x, entryAt.y, (short)z);
            if (Occupied(placementData, map, through))
            {
                shaftClear = false;
                break;
            }
        }

        Add(placementData, map, Entry, new GlobalChunkTransform(entryAt, entryRotation), rise, shaftClear);
        Add(placementData, map, Exit, new GlobalChunkTransform(exitAt, exitRotation), rise, shaftClear);
    }

    private void Add(
        IPlacementData placementData, IReadOnlyMapLayoutModel map, IIslandDefinition definition,
        GlobalChunkTransform transform, int rise, bool shaftClear)
    {
        bool valid = shaftClear && !Occupied(placementData, map, transform.Position);
        IslandDescriptor descriptor = PlacementAdapter.CreateDescriptor(
            definition, transform, new LiftStationConfiguration(rise), new SimulationStateContainer());
        IslandPlacement placement = PlacementAdapter.CreatePlacement(
            descriptor,
            valid ? PlacementAllowability.ValidPlacement : PlacementAllowability.InvalidPlacement,
            PlacementAdapter.NextIndex(placementData));
        PlacementAdapter.AddEntity(placementData, placement);
    }

    private bool Occupied(IPlacementData placementData, IReadOnlyMapLayoutModel map, GlobalChunkCoordinate at)
    {
        return PlacementAdapter.HasAnyEntityAt(placementData, at) ||
               AccessorAdapter.TryGetEntity(map.MapLayoutReadOnly, at, out IslandDescriptor _);
    }

    private static GridRotation RotationFor(ChunkDirection declared, ChunkDirection wanted)
    {
        return declared.RotationTo(wanted).ZRotation;
    }

    private static bool SameColumn(GlobalChunkCoordinate a, GlobalChunkCoordinate b)
    {
        return a.x == b.x && a.y == b.y;
    }

    private static bool IsVertical(ChunkDirection direction)
    {
        return direction.Equals(ChunkDirection.Up) || direction.Equals(ChunkDirection.Down);
    }

    /// <summary>A transcription of <c>PathAnchorsAndFillingProcessor.ComputeAnchors</c> for chunk pivots.</summary>
    private void ComputeAnchors(
        List<GlobalChunkPivot> anchors, IList<PathPlacementInputSegment<GlobalChunkPivot, ChunkDirection>> segments)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            bool first = i == 0;
            bool last = i == segments.Count - 1;
            GlobalChunkPivot start = segments[i].StartPivot;
            GlobalChunkPivot end = segments[i].EndPivot;

            if (start.Position.Equals(end.Position))
            {
                if (first)
                {
                    anchors.Add(start);
                }

                if (last)
                {
                    anchors.Add(end);
                }

                continue;
            }

            List<ChunkVector> pieces = new List<ChunkVector>();
            end.Position.Subtract(start.Position).DecomposeManhattanPath(Space, pieces);
            PreferDirection(segments[i].PreferredDirection, pieces);

            if (pieces.Count == 1)
            {
                if (!pieces[0].IsStandardBasisScalar(out ChunkDirection direction, out uint _))
                {
                    return;
                }

                if (first)
                {
                    anchors.Add(start);
                }

                anchors.Add(new GlobalChunkPivot(start.Position, direction));
                anchors.Add(new GlobalChunkPivot(end.Position, direction));
                if (last)
                {
                    anchors.Add(end);
                }

                continue;
            }

            if (pieces.Count == 2)
            {
                if (!pieces[0].IsStandardBasisScalar(out ChunkDirection first0, out uint _) ||
                    !pieces[1].IsStandardBasisScalar(out ChunkDirection second, out uint _))
                {
                    return;
                }

                GlobalChunkCoordinate corner = start.Position.Translate(pieces[0]);
                if (first)
                {
                    anchors.Add(start);
                }

                anchors.Add(new GlobalChunkPivot(start.Position, first0));
                anchors.Add(new GlobalChunkPivot(corner, first0));
                anchors.Add(new GlobalChunkPivot(corner, second));
                anchors.Add(new GlobalChunkPivot(end.Position, second));
                if (last)
                {
                    anchors.Add(end);
                }

                continue;
            }

            // Vanilla throws here; this one gives up quietly and leaves the drag as it was.
            return;
        }
    }

    private static void PreferDirection(ChunkDirection preferred, List<ChunkVector> pieces)
    {
        if (pieces.Count != 2 || !pieces[0].IsStandardBasisScalar(out ChunkDirection direction, out uint _))
        {
            return;
        }

        if (!direction.Equals(preferred))
        {
            (pieces[0], pieces[1]) = (pieces[1], pieces[0]);
        }
    }
}
