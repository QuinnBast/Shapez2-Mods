using System;
using Core.Collections.Scoped;
using Game.Content.Features.SpacePaths;
using Game.Core.Belts.BeltPath;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Map.Transport;
using Game.Core.Simulation;
using JetBrains.Annotations;
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

            // Found by asking each connector what it is, rather than by index arithmetic.
            //
            // This used to be `GetConnector(NumItemReceiverBundles)`, on the reasoning that
            // ConnectableIslandSimulation adds every input before every output, so output n sits
            // at receiverBundles + n. That holds only while an island declares as many input
            // connectors as the simulation claims bundles. `CargoBeltSimulation` claims two - a
            // belt tag and a pipe tag at one pivot - and a lift declares a single belt input, so
            // the loop adds one input and the output lands at index 1 while this asked for 2.
            //
            // The lookup then failed and the renderer returned before drawing anything: cargo
            // crossed a lift correctly and simply appeared on the far side, which reads as
            // teleporting rather than as a missing renderer.
            //
            // Only the pivots are wanted, and both connectors at a pivot share one, so which tag
            // this finds does not matter. IChunkSimulationConnector has the pivot without any of
            // the item-type generics that make the vanilla renderer unusable here.
            if (!TryEnds(entity, out IChunkSimulationConnector input,
                    out IChunkSimulationConnector output))
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

            // One shared arm for belts, lifts and junction arms alike - see CargoPathArm.
            //
            // This renderer carried its own copy of the placement maths until now, and that copy
            // is why two fixes written for junctions never reached lifts: the full-layer climb,
            // and pitching a container to lie along the ramp. A lift *is* a `CargoBeltSimulation`,
            // so it drew through the copy, where the rise was measured off `Exit` - the midpoint
            // of this chunk's centre and the neighbour past the output, which is half a layer, so
            // freight climbed at half the deck's gradient and stayed level while doing it.
            CargoPathArm arm = new(input.Pivot, output.Pivot, Packages.LongAxisIsX);

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

                    for (int i = 0; i < items.Count; i++)
                    {
                        SpacePathTravellingItemData item = items[i];

                        arm.At(item.Progress, across, in up,
                            out WorldCoordinate at, out Quaternion facing);

                        Drawers.TryDraw(drawing, item.BeltItem, Matrix4x4.TRS(
                            (Vector3)at, facing, Vector3.one * Packages.Scale));
                    }
                }
            }
        }

        /// The first input connector and the first output connector, whatever order they sit in.
        private static bool TryEnds(
            in Entity entity, out IChunkSimulationConnector input,
            out IChunkSimulationConnector output)
        {
            input = null;
            output = null;

            for (int i = 0; i < entity.LocalizedSimulation.NumConnectors; i++)
            {
                ISimulationConnector connector = entity.LocalizedSimulation.GetConnector(i);

                if (input == null && connector is IItemInputChunkConnector
                    && connector is IChunkSimulationConnector asInput)
                {
                    input = asInput;
                }
                else if (output == null && connector is IItemOutputChunkConnector
                    && connector is IChunkSimulationConnector asOutput)
                {
                    output = asOutput;
                }
            }

            return input != null && output != null;
        }

    }
}
