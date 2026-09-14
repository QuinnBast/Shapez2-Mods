using System;
using Game.Content.Features.SpacePaths;
using Game.Core.Coordinates;
using Game.Core.Map.Transport;
using Game.Core.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Draws cargo travelling between one pivot and another.
    ///
    /// Pulled out of `CargoBeltSimulationRenderer` when junctions arrived. A belt has one input
    /// and one output and drew items between that single pair; a splitter has one input and up
    /// to three outputs, and a merger up to three inputs and one output, so the same arc has to
    /// be drawn once per arm. The maths is unchanged - it is still
    /// `SpacePathSimulationRenderer.DrawItems` ported - it just no longer assumes the pair.
    ///
    /// Without this a junction drew nothing at all: renderers are keyed by simulation type, so a
    /// `CargoSplitterSimulation` matched none and its cargo was invisible while crossing.
    internal readonly struct CargoPathArm
    {
        private readonly GlobalChunkPivot In;
        private readonly GlobalChunkPivot Out;

        private readonly WorldCoordinate BeforeIn;
        private readonly WorldCoordinate AfterOut;
        private readonly WorldCoordinate Entry;
        private readonly WorldCoordinate Exit;

        private readonly WorldVector InLateral;
        private readonly WorldVector OutLateral;

        private readonly Quaternion EntryRotation;
        private readonly Quaternion ExitRotation;

        /// A turn, as opposed to a straight run, is exactly "the output is not opposite the
        /// input" - the same test the vanilla renderer makes. On a junction every arm but the
        /// straight-through one is a turn.
        private readonly bool Turning;

        public CargoPathArm(GlobalChunkPivot inPivot, GlobalChunkPivot outPivot, bool longAxisIsX)
        {
            In = inPivot;
            Out = outPivot;

            // The visible run is the half-chunk either side of the centre; the neighbouring
            // segment draws its own half.
            BeforeIn = (inPivot.Position + inPivot.Direction).ToCenter_W();
            WorldCoordinate centre = inPivot.Position.ToCenter_W();
            AfterOut = (outPivot.Position + outPivot.Direction).ToCenter_W();

            Entry = WorldCoordinate.Lerp(BeforeIn, centre, 0.5f);
            Exit = WorldCoordinate.Lerp(centre, AfterOut, 0.5f);

            InLateral = WorldVector.ByDirection(
                inPivot.Direction.Opposite.ToTileDirection().Rotate(GridRotation.RotateCW));
            OutLateral = WorldVector.ByDirection(
                outPivot.Direction.ToTileDirection().Rotate(GridRotation.RotateCW));

            Turning = inPivot.Direction.Opposite != outPivot.Direction;

            // Containers ride lengthways along the belt, and have to *turn* through a corner:
            // one rotation for the whole segment leaves a long box lying across the track
            // halfway round a bend. See CargoPackageMeshes.LongAxisIsX for the quarter turn.
            GridRotation flowIn = inPivot.Direction.Opposite.GlobalRotationTo().ZRotation;
            GridRotation flowOut = outPivot.Direction.GlobalRotationTo().ZRotation;
            GridRotation lie = longAxisIsX ? GridRotation.NoRotate : GridRotation.RotateCW;

            EntryRotation = FastMatrix.RotateY(flowIn + lie);
            ExitRotation = FastMatrix.RotateY(flowOut + lie);
        }

        /// Where a container sits, and which way it faces, at `progress` along this arm.
        public void At(float progress, float across, in WorldVector up,
            out WorldCoordinate position, out Quaternion facing)
        {
            WorldCoordinate from = Entry + across * InLateral + up;

            position = Turning
                ? OnCurve(from, up, progress)
                : math.lerp(from, Exit + across * OutLateral + up, progress);

            // Slerp rather than working out which way the corner goes: the two ends are a
            // quarter turn apart at most, so the shortest path is the way the belt actually
            // bends, and a straight run has both the same.
            facing = Turning
                ? Quaternion.Slerp(EntryRotation, ExitRotation, progress)
                : EntryRotation;
        }

        /// A quarter circle through the corner, as the vanilla renderer draws one.
        ///
        /// The centre of the arc is the midpoint of the two neighbouring chunk centres, and the
        /// item swings from one axis to the other as sin and cos of a quarter turn. Lerping
        /// straight from entry to exit instead would cut the corner visibly.
        private WorldCoordinate OnCurve(WorldCoordinate from, in WorldVector up, float progress)
        {
            WorldCoordinate pivot = WorldCoordinate.Lerp(BeforeIn, AfterOut, 0.5f) + up;
            float radius = math.distance(from, pivot);

            math.sincos(progress * MathF.PI * 0.5f, out float s, out float c);

            WorldCoordinate at = pivot;
            at += TileVector.ByDirection(In.Direction.Opposite.ToTileDirection()).ToWorld() * (s * radius);
            at += TileVector.ByDirection(Out.Direction.Opposite.ToTileDirection()).ToWorld() * (c * radius);
            return at;
        }
    }
}
