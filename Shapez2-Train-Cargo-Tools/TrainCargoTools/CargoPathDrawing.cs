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

        /// Both ends facing the same way, which only a Backward lift does: in and out on the
        /// same edge of the chunk, a layer apart. `Turning` is true for it as well - the output
        /// is not opposite the input - but the corner arc cannot draw it, so it is told apart
        /// here rather than inside the arc.
        private readonly bool Hairpin;

        /// World units of rise from the input pivot's layer to the output's. Zero on flat track.
        private readonly float Climb;

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
            Hairpin = inPivot.Direction == outPivot.Direction;

            // How far the piece climbs, taken from the two pivots' own chunks.
            //
            // Not from `Exit`, which is where the first version took it and is half the answer:
            // `Exit` is the midpoint of *this* chunk's centre and the neighbour beyond the
            // output, and this chunk's centre is at the input's layer. A lift's freight
            // therefore rose ten units while its deck rose twenty, so the containers sagged
            // further below the ramp the closer they got to the top.
            Climb = outPivot.Position.ToCenter_W().z - inPivot.Position.ToCenter_W().z;

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
            WorldVector lift = up;
            position = Point(progress, across, lift);

            Quaternion yaw = Hairpin
                ? Quaternion.Slerp(EntryRotation, ExitRotation, math.saturate(progress * 2f - 0.5f))
                : Turning
                    ? Quaternion.Slerp(EntryRotation, ExitRotation, progress)
                    : EntryRotation;

            facing = Climb == 0f ? yaw : Pitched(yaw, progress, across, lift);
        }

        /// Tips the container nose-up to lie along the ramp it is on.
        ///
        /// The slope is measured off the path rather than worked out per shape: sample a short
        /// step either side and take the angle between the horizontal distance covered and the
        /// height gained. That is the one calculation that is right for a straight ramp, a
        /// turning one and the hairpin alike - a quarter circle covers about 15.7 units while
        /// climbing 20, so its slope is steeper than the straight ramp's 45 degrees, and neither
        /// number has to be written down anywhere.
        ///
        /// The axis is the sampled heading crossed with world up, **not** the arm's lateral
        /// vector. Both name the same line, but only the cross product fixes which way along it
        /// points, and the sign is the whole difference between nose-up and nose-down: a tile
        /// direction rotated clockwise in the game's frame comes out as *minus* Unity's lateral
        /// once `WorldVector`'s `(x, z, -y)` cast has been through it, so tilting about it put
        /// every container on an incline nose-first into the deck. Deriving it from the two
        /// points already sampled for the slope cannot disagree with them.
        ///
        /// Rotating in world space, before the yaw, keeps this independent of which way round
        /// the crate mesh was authored - the alternative, a rotation in the container's own
        /// frame, depends on `CargoPackageMeshes.LongAxisIsX`.
        private Quaternion Pitched(Quaternion yaw, float progress, float across, WorldVector up)
        {
            const float Step = 0.03f;

            WorldCoordinate behind = Point(math.max(progress - Step, 0f), across, up);
            WorldCoordinate ahead = Point(math.min(progress + Step, 1f), across, up);

            float rise = ahead.z - behind.z;
            float2 flat = new(ahead.x - behind.x, ahead.y - behind.y);
            float run = math.length(flat);

            if (run < 0.0001f)
            {
                return yaw;
            }

            float degrees = math.degrees(math.atan2(rise, run));

            Vector3 heading = (Vector3)new WorldVector(flat / run, 0f);
            return Quaternion.AngleAxis(degrees, Vector3.Cross(heading, Vector3.up)) * yaw;
        }

        /// The full position at `progress`, climb included. One place, so the slope measured
        /// above cannot disagree with where the container is actually drawn.
        private WorldCoordinate Point(float progress, float across, WorldVector up)
        {
            WorldCoordinate from = Entry + across * InLateral + up;
            WorldCoordinate to = Exit + across * OutLateral + up;

            WorldCoordinate at = Hairpin
                ? OnHairpin(across, up, progress)
                : Turning
                    ? OnCurve(from, progress)
                    : math.lerp(from, to, progress);

            // Horizontal shape and vertical climb are worked out separately, and that split is
            // what makes a lift animate at all. `OnCurve` adds two horizontal tile vectors to a
            // fixed pivot, so whatever height that pivot has is the height every item on the arc
            // gets - on a turning lift every container sat at one height for the whole crossing.
            at.z = from.z + Climb * progress;
            return at;
        }

        /// A quarter circle through the corner, as the vanilla renderer draws one.
        ///
        /// The centre of the arc is the midpoint of the two neighbouring chunk centres, and the
        /// item swings from one axis to the other as sin and cos of a quarter turn. Lerping
        /// straight from entry to exit instead would cut the corner visibly.
        ///
        /// Flat by construction: the caller replaces the height afterwards, so the pivot is
        /// pulled down to the entry height rather than left at whatever the two neighbours
        /// average to.
        private WorldCoordinate OnCurve(WorldCoordinate from, float progress)
        {
            WorldCoordinate pivot = WorldCoordinate.Lerp(BeforeIn, AfterOut, 0.5f);
            pivot.z = from.z;

            float radius = math.distance(from, pivot);

            math.sincos(progress * MathF.PI * 0.5f, out float s, out float c);

            WorldCoordinate at = pivot;
            at += TileVector.ByDirection(In.Direction.Opposite.ToTileDirection()).ToWorld() * (s * radius);
            at += TileVector.ByDirection(Out.Direction.Opposite.ToTileDirection()).ToWorld() * (c * radius);
            return at;
        }

        /// The path a Backward lift follows: in and out on the same edge, a layer apart.
        ///
        /// `OnCurve` cannot draw this. It swings between the input's axis and the output's, and
        /// here they are the same one, so the two sine and cosine terms collapse onto a single
        /// line - the container would slide straight in and back out through the wall it came
        /// from. It is not a corner at all but a hairpin, and the mesh is built as one.
        ///
        /// Traced with the same constants `lift_path` uses in Tools/generate_meshes.py, so the
        /// freight follows the deck it is riding on: out along one side of the centre line, half
        /// a circle, and back along the other.
        private WorldCoordinate OnHairpin(float across, in WorldVector up, float progress)
        {
            // Into the chunk is the direction the input faces away from.
            WorldVector along = TileVector.ByDirection(In.Direction.Opposite.ToTileDirection()).ToWorld();
            WorldCoordinate centre = In.Position.ToCenter_W();

            // Both legs sit one radius off the centre line, which is what keeps them from
            // lying on top of each other - and is why the ends are not laterally centred.
            //
            // Spelled out rather than put in a local function: this is a struct, and a local
            // function inside one cannot touch `this` or an `in` parameter.
            WorldVector lateral = InLateral;
            WorldVector lift = up;

            WorldCoordinate outboundStart = centre + along * -HalfChunk_W
                + lateral * (across - HairpinRadius) + lift;
            WorldCoordinate outboundEnd = centre + along * HairpinLeg
                + lateral * (across - HairpinRadius) + lift;
            WorldCoordinate returnStart = centre + along * HairpinLeg
                + lateral * (across + HairpinRadius) + lift;
            WorldCoordinate returnEnd = centre + along * -HalfChunk_W
                + lateral * (across + HairpinRadius) + lift;

            if (progress < LegShare)
            {
                return math.lerp(outboundStart, outboundEnd, progress / LegShare);
            }

            if (progress > 1f - LegShare)
            {
                return math.lerp(returnStart, returnEnd,
                    (progress - (1f - LegShare)) / LegShare);
            }

            float turn = (progress - LegShare) / (1f - 2f * LegShare);
            math.sincos(-MathF.PI * 0.5f + MathF.PI * turn, out float s, out float c);

            return centre
                + along * (HairpinLeg + HairpinRadius * c)
                + lateral * (HairpinRadius * s + across)
                + lift;
        }

        /// Matched to `lift_path`'s hairpin in Tools/generate_meshes.py. Two numbers in two
        /// languages is a seam, so they are named here rather than left as bare figures.
        private const float HairpinRadius = 3.5f;

        private const float HairpinLeg = 1.8f;

        /// How much of the crossing each straight leg takes, the arc getting the rest.
        private const float LegShare = 0.3f;

        /// A chunk is twenty units across, so its edge is ten from the centre.
        private const float HalfChunk_W = 10f;
    }
}
