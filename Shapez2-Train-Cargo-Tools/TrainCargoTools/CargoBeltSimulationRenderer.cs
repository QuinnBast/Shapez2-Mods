using System;
using Core.Collections.Scoped;
using Game.Content.Features.SpacePaths;
using Game.Core.Belts.BeltPath;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Map.Transport;
using Game.Core.Simulation;
using JetBrains.Annotations;
using Unity.Mathematics;
using UnityEngine;

// FrameDrawOptions.Theme is obsolete in favour of injection, but the theme is not bound
// into the renderer container, so reading it off the frame is the only way in.
#pragma warning disable CS0618

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Draws the packages travelling along a cargo belt.
    ///
    /// Without this a working cargo line looks like an empty one - the track draws, the
    /// simulation moves packages along it, and nothing appears. Vanilla's renderers cannot cover
    /// it: `SpacePathSimulationRenderer` splits every connector into `ShapeItem` and
    /// `FluidPackageItem` lists and casts each item unconditionally, and a
    /// `PackageOnTrack&lt;CargoPackage&lt;ShapeId&gt;&gt;` is neither, so inheriting from it
    /// would throw an `InvalidCastException` the first time a package moved.
    ///
    /// Nothing registers this. `GameSessionOrchestrator.CreateSimulationRenderers` reflects over
    /// `AppDomain.CurrentDomain.GetAssemblies()` for every `IIslandSimulationRenderer` and builds
    /// each through a dependency container, which reaches a mod assembly as readily as the
    /// game's own - hence `[UsedImplicitly]`, which is also why every vanilla renderer carries
    /// it. Renderers are keyed by simulation type, so this one covers all six belt variants:
    /// straight and both turns, shape-tagged and pipe-tagged.
    ///
    /// The placement maths is `SpacePathSimulationRenderer.DrawItems` ported rather than
    /// invented, curve included, so a package sits exactly where a shape would on the same
    /// track. What goes at that position is the game's own package drawer - see
    /// CargoPackageDrawers, and note that drawing the package mesh by hand instead, which is
    /// what this did first, loses a fluid's colour and a shape's shape.
    [UsedImplicitly]
    public class CargoBeltSimulationRenderer
        : StatelessIslandSimulationRenderer<CargoBeltSimulation, IIslandCustomDrawData>
    {
        /// Resolved once, not per frame.
        ///
        /// Working out the scale reads two meshes' bounds, and OnDrawDynamic runs for every
        /// visible cargo belt every frame - on a long line that is thousands of redundant
        /// lookups a frame for a value that cannot change, because the theme is fixed for the
        /// session and a renderer does not outlive one.
        private CargoPackageMeshes Packages;
        private bool PackagesResolved;

        private readonly CargoPackageDrawers Drawers;

        public CargoBeltSimulationRenderer(IMapModel map, IShapeRegistry shapes)
            : base(map)
        {
            Drawers = new CargoPackageDrawers(shapes);
        }

        /// Further than vanilla space paths, which stop at BuildingLOD 3.
        ///
        /// That cutoff is tuned for shapes, which really are a few pixels by then. A cargo
        /// container is a chunk-wide crate and the last thing that should vanish - a belt whose
        /// freight disappears while the track stays reads as a broken belt, not a distant one.
        public override bool ShouldDraw(LODRenderConfig lod)
        {
            return lod.BuildingLOD <= 5;
        }

        public override void OnDrawDynamic(in Entity entity, FrameDrawOptions options)
        {
            if (!options.ShouldRenderPlatformContentsAtLayer(entity.Transform.Position.z))
            {
                return;
            }

            // ConnectableIslandSimulation adds every input in bundle order and then every output
            // in bundle order, so output n is connector (receiverBundles + n). Not simply
            // connector 1: a cargo belt claims two receiver bundles so it can carry both a belt
            // tag and a pipe tag at each pivot, which makes connector 1 the *second input*.
            //
            // Only the pivots are wanted, and both connectors at a pivot share one, so which tag
            // this picks up does not matter. IChunkSimulationConnector has the pivot without any
            // of the item-type generics that make the vanilla renderer unusable here.
            int outputIndex = entity.Simulation.NumItemReceiverBundles;
            if (!(entity.LocalizedSimulation.GetConnector(0) is IChunkSimulationConnector input)
                || !(entity.LocalizedSimulation.GetConnector(outputIndex) is IChunkSimulationConnector output))
            {
                return;
            }

            // Belt spacing for every cargo belt. One family serves both lines now, and it draws
            // belt track either way, so taking the pipe config for a fluid run would put the
            // containers slightly off the track it is actually drawn on.
            SpacePathItemRenderingConfig config =
                options.Theme.BaseResources.SpaceBelts.ItemRenderingConfig;

            if (!PackagesResolved)
            {
                // Sized to the belt, not to one lane. The four lanes span three
                // TrackItemsSpacing and an item occupies about one more, so a container filling
                // roughly 3.4 of them reads as full-width freight with a little air at the
                // edges. Taken from the theme's own spacing rather than a world constant so it
                // still lines up if a theme spaces its tracks differently.
                // Sized to the belt each container gets rather than to the belt's width, so a
                // full belt reads as a line of freight touching nose to tail. A hair under the
                // slot, so neighbours meet rather than intersect.
                float along = CargoLanes.SlotSpacing_W * 0.97f;

                // Width capped so three files fit abreast on one deck. Nothing caps the height
                // any more - there is no tier above to reach - and nothing needs to: the across
                // limit binds first for any crate squarer than its slot, which is the only way a
                // package could have come out as a tower.
                Packages = new CargoPackageMeshes(
                    options.Theme.BaseResources.Trains?.Cargo, along,
                    maxHeight: 0f, maxAcross: CargoLanes.MaxAcross_W);
                PackagesResolved = true;
            }

            GlobalChunkPivot inPivot = input.Pivot;
            GlobalChunkPivot outPivot = output.Pivot;

            // The visible run is the half-chunk either side of the centre; the neighbouring
            // segment draws its own half.
            WorldCoordinate beforeIn = (inPivot.Position + inPivot.Direction).ToCenter_W();
            WorldCoordinate centre = inPivot.Position.ToCenter_W();
            WorldCoordinate afterOut = (outPivot.Position + outPivot.Direction).ToCenter_W();

            WorldCoordinate entry = WorldCoordinate.Lerp(beforeIn, centre, 0.5f);
            WorldCoordinate exit = WorldCoordinate.Lerp(centre, afterOut, 0.5f);

            WorldVector inLateral = WorldVector.ByDirection(
                inPivot.Direction.Opposite.ToTileDirection().Rotate(GridRotation.RotateCW));
            WorldVector outLateral = WorldVector.ByDirection(
                outPivot.Direction.ToTileDirection().Rotate(GridRotation.RotateCW));

            // A turn, as opposed to a straight run, is exactly "the output is not opposite the
            // input" - the same test the vanilla renderer makes.
            bool turning = inPivot.Direction.Opposite != outPivot.Direction;

            // Containers ride *lengthways* along the belt, the way they sit on a flatbed wagon,
            // so each one covers as much of its slot as it can. The quarter turn is applied only
            // when the mesh's long axis does not already point along the flow - see
            // CargoPackageMeshes.LongAxisIsX. The drawer's rotation maps mesh local X to the
            // direction of travel, so a mesh whose long axis is X already lies right.
            //
            // Two rotations, not one, because a container has to *turn* through a corner. The
            // first version used the entry direction for every item on the segment, which is
            // fine for a shape - shapes are drawn axis-aligned and vanilla never rotates them -
            // and badly wrong for a long box: halfway round a bend it still pointed the way it
            // came in, so it lay across the track and hung off the outside of the curve.
            GridRotation flowIn = inPivot.Direction.Opposite.GlobalRotationTo().ZRotation;
            GridRotation flowOut = outPivot.Direction.GlobalRotationTo().ZRotation;

            GridRotation lie = Packages.LongAxisIsX ? GridRotation.NoRotate : GridRotation.RotateCW;
            Quaternion entryRotation = FastMatrix.RotateY(flowIn + lie);
            Quaternion exitRotation = FastMatrix.RotateY(flowOut + lie);

            // The package drawers ask their LOD meshes for options.LOD.IslandLOD, and a
            // LOD6Mesh returns nothing above the levels it was given - one mesh supplied means
            // anything past its Count draws blank. Clamping the LOD handed to them keeps a
            // container visible as far out as ShouldDraw allows, instead of the crate quietly
            // disappearing while the belt it sits on carries on drawing.
            FrameDrawOptions drawing = options;
            LODRenderConfig clamped = options.LOD;
            clamped.IslandLOD = Math.Min(clamped.IslandLOD, 2);
            clamped.IslandMaterialLOD = Math.Min(clamped.IslandMaterialLOD, 2);
            drawing.LOD = clamped;

            using ScopedList<SpacePathTravellingItemData> items =
                ScopedList<SpacePathTravellingItemData>.Get();

            for (short lane = 0; lane < SpacePathConstants.NumLanes; lane++)
            {
                // Only one lane per layer carries anything, and a full-width container has
                // nowhere to sit but the middle of the belt. See CargoLanes.
                if (!CargoLanes.Carries(lane))
                {
                    continue;
                }

                for (short layer = 0; layer < SpacePathConstants.NumLayers; layer++)
                {
                    items.Clear();
                    SpacePathTravellingShapesUtils.AddPathItems(
                        items, entity.Simulation.PathBundle.GetLane(lane, layer), 0, 0, 0f, 1f);
                    if (items.Count == 0)
                    {
                        continue;
                    }

                    // One file per layer, the three of them abreast across the deck. This is
                    // vanilla's own arrangement with the lane term dropped - see
                    // CargoLanes.Across_W - and it replaces three stacked tiers, which put the
                    // top deck in front of the other two from the game's camera angle.
                    float across = CargoLanes.Across_W(layer);

                    // The deck, plus half a container so it sits *on* it rather than half
                    // through - the package mesh is authored centred, as the store found too.
                    // All three layers share the one height now.
                    WorldVector up = new(0f, 0f,
                        CargoLanes.DeckHeight_W(config) - Packages.Bottom);

                    WorldCoordinate from = entry + across * inLateral + up;
                    WorldCoordinate to = exit + across * outLateral + up;

                    for (int i = 0; i < items.Count; i++)
                    {
                        SpacePathTravellingItemData item = items[i];

                        WorldCoordinate at = turning
                            ? OnCurve(from, beforeIn, afterOut, up, inPivot, outPivot, item.Progress)
                            : math.lerp(from, to, item.Progress);

                        // Slerp rather than working out which way the corner goes: the two
                        // ends are a quarter turn apart at most, so the shortest path is the
                        // way the belt actually bends, and a straight run has both the same.
                        Quaternion facing = turning
                            ? Quaternion.Slerp(entryRotation, exitRotation, item.Progress)
                            : entryRotation;

                        Drawers.TryDraw(drawing, item.BeltItem, Matrix4x4.TRS(
                            (Vector3)at, facing, Vector3.one * Packages.Scale));
                    }
                }
            }
        }

        /// A quarter circle through the corner, as the vanilla renderer draws one.
        ///
        /// The centre of the arc is the midpoint of the two neighbouring chunk centres, and the
        /// item swings from one axis to the other as sin and cos of a quarter turn. Lerping
        /// straight from entry to exit instead would cut the corner visibly, and on a dragged run
        /// of turns it reads as the belt being bent rather than curved.
        private static WorldCoordinate OnCurve(
            WorldCoordinate from, WorldCoordinate beforeIn, WorldCoordinate afterOut,
            WorldVector up, GlobalChunkPivot inPivot, GlobalChunkPivot outPivot, float progress)
        {
            WorldCoordinate pivot = WorldCoordinate.Lerp(beforeIn, afterOut, 0.5f) + up;
            float radius = math.distance(from, pivot);

            math.sincos(progress * MathF.PI * 0.5f, out float s, out float c);

            WorldCoordinate at = pivot;
            at += TileVector.ByDirection(inPivot.Direction.Opposite.ToTileDirection()).ToWorld() * (s * radius);
            at += TileVector.ByDirection(outPivot.Direction.Opposite.ToTileDirection()).ToWorld() * (c * radius);
            return at;
        }
    }
}
