using System.Collections.Generic;
using Core.Collections.Scoped;
using Game.Content.Features.SpacePaths;
using Game.Core.Belts.BeltPath;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Map.Transport;
using Game.Core.Rendering.Culling;
using Game.Core.Simulation;
using JetBrains.Annotations;
using Unity.Mathematics;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Draws the shapes and fluid packages travelling across a crossing.
    ///
    /// Nothing registers this. `GameSessionOrchestrator.CreateSimulationRenderers` reflects over
    /// `AppDomain.CurrentDomain.GetAssemblies()` for every `IIslandSimulationRenderer` and builds
    /// each through a dependency container - which reaches a mod assembly as readily as the game's
    /// own, and is why vanilla's renderers are all marked `[UsedImplicitly]`. Renderers are keyed
    /// by simulation *type*, so this one covers the straight and mirrored variants of all three
    /// crossings at once.
    ///
    /// It does not derive from `SpacePathSimulationRenderer` because that class cannot express a
    /// belt crossing a pipe. It gathers every connector into per-item-type lists, then draws the
    /// whole item list once against the shape lists and once against the fluid lists, casting each
    /// item unconditionally - fine for a vanilla node, where every connector carries the same item
    /// type, and an `InvalidCastException` the first time a `FluidPackageItem` meets the shape
    /// pass. So a crossing pairs its connectors up itself, one path at a time.
    [UsedImplicitly]
    public class CrossoverSimulationRenderer
        : StatelessIslandSimulationRenderer<CrossoverSimulation, IIslandCustomDrawData>
    {
        public CrossoverSimulationRenderer(IMapModel map)
            : base(map)
        {
        }

        /// Same cutoff vanilla space paths use: past this the items are too small to matter.
        public override bool ShouldDraw(LODRenderConfig lod)
        {
            return lod.BuildingLOD <= 3;
        }

        public override void OnDrawDynamic(in Entity entity, FrameDrawOptions options)
        {
            if (!options.ShouldRenderPlatformContentsAtLayer(entity.Transform.Position.z))
            {
                return;
            }

            // ConnectableIslandSimulation adds every input in bundle order and then every output
            // in bundle order, so path n is connector n paired with connector (bundles + n). Two
            // bundles, four connectors: 0 with 2, and 1 with 3.
            for (int path = 0; path < entity.Simulation.NumItemReceiverBundles; path++)
            {
                ISimulationConnector input = entity.LocalizedSimulation.GetConnector(path);
                ISimulationConnector output = entity.LocalizedSimulation.GetConnector(
                    entity.Simulation.NumItemReceiverBundles + path);

                ItemLaneBundle<FastBeltPathLane> bundle = path == 0
                    ? entity.Simulation.PathA
                    : entity.Simulation.PathB;

                // The connector types are what say whether this path is belt or pipe, so a mixed
                // crossing needs no knowledge of its own kind here.
                if (input is IItemInputChunkConnector<ShapeItem> shapeIn &&
                    output is IItemOutputChunkConnector<ShapeItem> shapeOut)
                {
                    DrawPath(options, bundle, shapeIn, shapeOut,
                        default(ShapeSpacePathBeltItemRendererAdapter),
                        options.Theme.BaseResources.SpaceBelts.ItemRenderingConfig);
                }
                else if (input is IItemInputChunkConnector<FluidPackageItem> fluidIn &&
                         output is IItemOutputChunkConnector<FluidPackageItem> fluidOut)
                {
                    DrawPath(options, bundle, fluidIn, fluidOut,
                        default(FluidSpacePathBeltItemRendererAdapter),
                        options.Theme.BaseResources.SpacePipes.ItemRenderingConfig);
                }
            }
        }

        /// One path's twelve lanes, laid out the way SpacePathSimulationRenderer lays out a
        /// straight segment's.
        ///
        /// A crossing is always straight, so the curved case that handles a turn is not needed:
        /// the item simply runs from the midpoint of its input edge to the midpoint of its output
        /// edge, and `progress` interpolates between them.
        private static void DrawPath<TRenderer, TItem>(
            FrameDrawOptions options, ItemLaneBundle<FastBeltPathLane> bundle,
            IItemInputChunkConnector<TItem> input, IItemOutputChunkConnector<TItem> output,
            TRenderer itemRenderer, SpacePathItemRenderingConfig config)
            where TRenderer : ISpacePathBeltItemRendererAdapter<TItem>
            where TItem : IBeltItem
        {
            GlobalChunkPivot inPivot = input.Pivot;
            GlobalChunkPivot outPivot = output.Pivot;

            // The visible run is only the half-chunk either side of the centre: the neighbouring
            // segment draws the rest.
            WorldCoordinate beforeIn = (inPivot.Position + inPivot.Direction).ToCenter_W();
            WorldCoordinate centre = inPivot.Position.ToCenter_W();
            WorldCoordinate afterOut = (outPivot.Position + outPivot.Direction).ToCenter_W();

            WorldVector inLateral = WorldVector.ByDirection(
                inPivot.Direction.Opposite.ToTileDirection().Rotate(GridRotation.RotateCW));
            WorldVector outLateral = WorldVector.ByDirection(
                outPivot.Direction.ToTileDirection().Rotate(GridRotation.RotateCW));

            using ScopedList<SpacePathTravellingItemData> items =
                ScopedList<SpacePathTravellingItemData>.Get();

            for (short lane = 0; lane < SpacePathConstants.NumLanes; lane++)
            {
                for (short layer = 0; layer < SpacePathConstants.NumLayers; layer++)
                {
                    items.Clear();
                    SpacePathTravellingShapesUtils.AddPathItems(
                        items, bundle.GetLane(lane, layer), 0, 0, 0f, 1f);
                    if (items.Count == 0)
                    {
                        continue;
                    }

                    float across = (layer - 1f) * config.TracksSpacing +
                                   (lane - 1.5f) * config.TrackItemsSpacing;
                    WorldVector up = new(0f, 0f, config.LayerOffset * layer + config.Height);

                    WorldCoordinate from = WorldCoordinate.Lerp(beforeIn, centre, 0.5f)
                                           + across * inLateral + up;
                    WorldCoordinate to = WorldCoordinate.Lerp(centre, afterOut, 0.5f)
                                         + across * outLateral + up;

                    for (int i = 0; i < items.Count; i++)
                    {
                        SpacePathTravellingItemData item = items[i];
                        WorldCoordinate at = math.lerp(from, to, item.Progress);
                        itemRenderer.RenderItem(
                            options, (TItem)item.BeltItem, FastMatrix.Translate(in at));
                    }
                }
            }
        }
    }
}
