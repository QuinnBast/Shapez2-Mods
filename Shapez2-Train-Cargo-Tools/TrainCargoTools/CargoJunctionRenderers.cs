using System;
using Core.Collections.Scoped;
using Game.Content.AtomicIslands.Mergers;
using Game.Content.AtomicIslands.Splitter;
using Game.Content.Features;
using Game.Content.Features.SpacePaths;
using Game.Core.Belts.BeltPath;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Map.Transport;
using Game.Core.Simulation;
using JetBrains.Annotations;
using UnityEngine;

#pragma warning disable CS0618 // FrameDrawOptions.Theme, as CargoBeltSimulationRenderer explains.

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Shared by the splitter and merger renderers: everything that is the same whichever way
    /// the junction branches.
    ///
    /// Renderers are keyed by simulation type, so a junction needs its own even though it draws
    /// exactly what a belt draws. Without one, cargo is invisible for the chunk it spends
    /// crossing a junction - which reads as the junction eating it.
    ///
    /// Public for the same reason `CargoBeltSimulationRenderer` is: the game builds renderers by
    /// reflecting for `IIslandSimulationRenderer` and constructing through a dependency
    /// container, and a public renderer over a non-public simulation will not compile.
    public abstract class CargoJunctionRenderer<TSimulation>
        : StatelessIslandSimulationRenderer<TSimulation, IIslandCustomDrawData>
        where TSimulation : class, ISimulation
    {
        private CargoPackageMeshes Packages;
        private bool PackagesResolved;

        private readonly CargoPackageDrawers Drawers;

        protected CargoJunctionRenderer(IMapModel map, IShapeRegistry shapes)
            : base(map)
        {
            Drawers = new CargoPackageDrawers(shapes);
        }

        /// As far out as the belts, for the same reason: freight is the last thing that should
        /// vanish, and a junction that empties while the belts either side stay loaded would
        /// look like the junction had swallowed it.
        public override bool ShouldDraw(LODRenderConfig lod)
        {
            return lod.BuildingLOD <= 5;
        }

        /// One (input pivot, output pivot, lane) triple per arm, per lane and layer.
        protected abstract void Arms(
            in Entity entity, TSimulation simulation, short lane, short layer, ArmSink sink);

        public override void OnDrawDynamic(in Entity entity, FrameDrawOptions options)
        {
            if (!options.ShouldRenderPlatformContentsAtLayer(entity.Transform.Position.z))
            {
                return;
            }

            SpacePathItemRenderingConfig config =
                options.Theme.BaseResources.SpaceBelts.ItemRenderingConfig;

            if (!PackagesResolved)
            {
                Packages = new CargoPackageMeshes(
                    options.Theme.BaseResources.Trains?.Cargo,
                    CargoLanes.SlotSpacing_W * 0.97f,
                    maxHeight: 0f, maxAcross: CargoLanes.MaxAcross_W);
                PackagesResolved = true;
            }

            FrameDrawOptions drawing = options;
            LODRenderConfig clamped = options.LOD;
            clamped.IslandLOD = Math.Min(clamped.IslandLOD, 2);
            clamped.IslandMaterialLOD = Math.Min(clamped.IslandMaterialLOD, 2);
            drawing.LOD = clamped;

            ArmSink sink = new(drawing, Drawers, Packages, config);

            for (short lane = 0; lane < SpacePathConstants.NumLanes; lane++)
            {
                if (!CargoLanes.Carries(lane))
                {
                    continue;
                }

                for (short layer = 0; layer < SpacePathConstants.NumLayers; layer++)
                {
                    sink.Layer = layer;
                    Arms(in entity, entity.Simulation as TSimulation, lane, layer, sink);
                }
            }
        }

        /// Collects one arm at a time and draws it, so a subclass only has to say which pivots
        /// pair with which lane rather than repeating the geometry.
        public sealed class ArmSink
        {
            private readonly FrameDrawOptions Drawing;
            private readonly CargoPackageDrawers Drawers;
            private readonly CargoPackageMeshes Packages;
            private readonly SpacePathItemRenderingConfig Config;

            public short Layer;

            public ArmSink(
                FrameDrawOptions drawing, CargoPackageDrawers drawers,
                CargoPackageMeshes packages, SpacePathItemRenderingConfig config)
            {
                Drawing = drawing;
                Drawers = drawers;
                Packages = packages;
                Config = config;
            }

            /// A merger's arm. Its inputs are real `FastBeltPathLane`s - this mod's
            /// `CargoBeltLane` subclass of one, in fact.
            public void Draw(IChunkSimulationConnector input, IChunkSimulationConnector output,
                FastBeltPathLane lane)
            {
                if (lane == null)
                {
                    return;
                }

                using ScopedList<SpacePathTravellingItemData> items =
                    ScopedList<SpacePathTravellingItemData>.Get();

                SpacePathTravellingShapesUtils.AddPathItems(items, lane, 0, 0, 0f, 1f);
                Draw(input, output, items);
            }

            /// A splitter's arm. Its outputs are `BeltPathLane`, a different class from the
            /// belts' - `SpacePathTravellingShapesUtils.AddPathItems` is overloaded per lane
            /// type rather than taking the interface, so the two cannot share one call.
            public void Draw(IChunkSimulationConnector input, IChunkSimulationConnector output,
                BeltPathLane lane)
            {
                if (lane == null)
                {
                    return;
                }

                using ScopedList<SpacePathTravellingItemData> items =
                    ScopedList<SpacePathTravellingItemData>.Get();

                SpacePathTravellingShapesUtils.AddPathItems(items, lane, 0, 0, 0f, 1f);
                Draw(input, output, items);
            }

            private void Draw(IChunkSimulationConnector input, IChunkSimulationConnector output,
                ScopedList<SpacePathTravellingItemData> items)
            {
                if (input == null || output == null || items.Count == 0)
                {
                    return;
                }

                CargoPathArm arm = new(input.Pivot, output.Pivot, Packages.LongAxisIsX);

                float across = CargoLanes.Across_W(Layer);
                WorldVector up = new(0f, 0f,
                    CargoLanes.DeckHeight_W(Config) - Packages.Bottom);

                for (int i = 0; i < items.Count; i++)
                {
                    arm.At(items[i].Progress, across, in up,
                        out WorldCoordinate at, out Quaternion facing);

                    Drawers.TryDraw(Drawing, items[i].BeltItem, Matrix4x4.TRS(
                        (Vector3)at, facing, Vector3.one * Packages.Scale));
                }
            }
        }
    }

    /// Cargo on a splitter: one input, and the lane behind each output connector.
    ///
    /// `ConnectableIslandSimulation` adds every input connector in bundle order and then every
    /// output connector in bundle order, and a splitter has exactly one receiver bundle - so
    /// connector 0 is the input and connector 1 + n is output n.
    [UsedImplicitly]
    public sealed class CargoSplitterSimulationRenderer
        : CargoJunctionRenderer<CargoSplitterSimulation>
    {
        public CargoSplitterSimulationRenderer(IMapModel map, IShapeRegistry shapes)
            : base(map, shapes)
        {
        }

        protected override void Arms(
            in Entity entity, CargoSplitterSimulation simulation, short lane, short layer,
            ArmSink sink)
        {
            if (simulation == null
                || !(entity.LocalizedSimulation.GetConnector(0) is IChunkSimulationConnector input))
            {
                return;
            }

            PathSplitterSimulation splitter = simulation.SplitterBundle.GetSimulation(lane, layer);

            for (int output = 0; output < splitter.OutputLanes.Count; output++)
            {
                sink.Draw(
                    input,
                    entity.LocalizedSimulation.GetConnector(1 + output) as IChunkSimulationConnector,
                    splitter.OutputLanes[output]);
            }
        }
    }

    /// Cargo on a merger: the lane behind each input connector, all leading to the one output.
    ///
    /// The mirror of the splitter's indexing - inputs come first, so connector n is input n and
    /// the output is the one after the last of them.
    [UsedImplicitly]
    public sealed class CargoMergerSimulationRenderer
        : CargoJunctionRenderer<CargoMergerSimulation>
    {
        public CargoMergerSimulationRenderer(IMapModel map, IShapeRegistry shapes)
            : base(map, shapes)
        {
        }

        protected override void Arms(
            in Entity entity, CargoMergerSimulation simulation, short lane, short layer,
            ArmSink sink)
        {
            if (simulation == null)
            {
                return;
            }

            int inputs = simulation.InputPathBundles.Length;

            if (!(entity.LocalizedSimulation.GetConnector(inputs) is IChunkSimulationConnector output))
            {
                return;
            }

            for (int input = 0; input < inputs; input++)
            {
                sink.Draw(
                    entity.LocalizedSimulation.GetConnector(input) as IChunkSimulationConnector,
                    output,
                    simulation.InputPathBundles[input].GetLane(lane, layer));
            }
        }
    }
}
