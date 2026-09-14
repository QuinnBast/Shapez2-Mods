using System;
using Game.Content.Features.Fluids;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Simulation;
using Game.Core.Trains;
using JetBrains.Annotations;
using UnityEngine;

// FrameDrawOptions.Theme is obsolete in favour of injection, but the theme is not bound
// into the renderer container, so reading it off the frame is the only way in.
#pragma warning disable CS0618

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Draws everything a cargo store is holding, on the open shelves of its rack.
    ///
    /// **One container per package, twenty-five per shelf.** An earlier version drew five per
    /// shelf and let each stand for five packages. That was deliberate and it was the wrong
    /// call: a buffer's whole job is how full it is, and a display that only moves in steps of
    /// five cannot show a store filling. Twenty-five fit fine - a chunk is twenty units across
    /// and the package is scaled to the slot pitch - so each shelf is a 5x5 grid and a full
    /// store shows seventy-five containers, every one of them a real package.
    ///
    /// The rack has no roof for the same reason: the top shelf is the one you see walking past.
    ///
    /// This cannot be done with `ModularIslandMeshDrawer.Data`, which is where the static
    /// machine meshes live: that is CustomData on the *definition*, so every store on the map
    /// shares one copy and none of them can differ. Per-instance geometry has to come from a
    /// simulation renderer, which is handed the entity - and so the state - every frame.
    ///
    /// One shelf per layer, because the simulation keeps one independent queue per layer. So a
    /// glance says not just how full a store is but *which* layer is backed up, which is the
    /// thing worth knowing when a train is not clearing.
    ///
    /// Abstract, with a concrete subclass per store type, because renderers are discovered by
    /// reflection over non-abstract `IIslandSimulationRenderer` implementations and keyed by
    /// simulation type - an open generic could not be constructed.
    public abstract class CargoStoreSimulationRenderer<TItem, TState, TSimulation>
        : StatelessIslandSimulationRenderer<TSimulation, IIslandCustomDrawData>
        where TItem : unmanaged, IEquatable<TItem>
        where TState : CargoStoreState<TItem>, ISimulationState, new()
        where TSimulation : CargoStoreSimulation<TItem, TState>
    {
        // Shelf geometry lives in CargoRack, shared with the combined store's renderer.

        private CargoPackageMeshes Fit;
        private bool FitResolved;

        private readonly CargoPackageDrawers Drawers;

        protected CargoStoreSimulationRenderer(IMapModel map, IShapeRegistry shapes)
            : base(map)
        {
            Drawers = new CargoPackageDrawers(shapes);
        }

        /// Draws one stored package. The two subclasses differ only in this.
        protected abstract void Draw(
            FrameDrawOptions options, CargoPackageDrawers drawers,
            TSimulation store, int layer, int index, Matrix4x4 trs);

        /// Closer than the belts' cutoff. Belt items are a stream and their absence is
        /// conspicuous; a store's contents are detail, and seventy-five containers per store is
        /// not worth submitting once the store is a smudge on the horizon.
        public override bool ShouldDraw(LODRenderConfig lod)
        {
            return lod.BuildingLOD <= 2;
        }

        public override void OnDrawDynamic(in Entity entity, FrameDrawOptions options)
        {
            if (!options.ShouldRenderPlatformContentsAtLayer(entity.Transform.Position.z))
            {
                return;
            }

            if (!FitResolved)
            {
                Fit = new CargoPackageMeshes(
                    options.Theme.BaseResources.Trains?.Cargo,
                    CargoRack.SlotPitch * 0.88f, CargoRack.ShelfGap * 0.9f);
                FitResolved = true;
            }

            WorldCoordinate centre = entity.Transform.Position.ToCenter_W();
            GridRotation rotation = entity.Transform.Rotation;
            Quaternion spin = FastMatrix.RotateY(rotation);
            Vector3 scale = Vector3.one * Fit.Scale;

            for (int layer = 0; layer < CargoRack.Shelves.Length; layer++)
            {
                int held = Math.Min(entity.Simulation.CountAt(layer), CargoRack.SlotsPerShelf);
                for (int index = 0; index < held; index++)
                {
                    Draw(options, Drawers, entity.Simulation, layer, index,
                        Matrix4x4.TRS(
                            (Vector3)(centre + CargoRack.SlotOffset(layer, index, Fit.Bottom, rotation)),
                            spin, scale));
                }
            }
        }
    }

    [UsedImplicitly]
    public sealed class ShapeCargoStoreSimulationRenderer
        : CargoStoreSimulationRenderer<ShapeId, ShapeCargoStoreState, ShapeCargoStoreSimulation>
    {
        public ShapeCargoStoreSimulationRenderer(IMapModel map, IShapeRegistry shapes)
            : base(map, shapes)
        {
        }

        protected override void Draw(
            FrameDrawOptions options, CargoPackageDrawers drawers,
            ShapeCargoStoreSimulation store, int layer, int index, Matrix4x4 trs)
        {
            CargoPackage<ShapeId> package = store.PackageAt(layer, index);
            drawers.DrawShape(options, in package, trs);
        }
    }

    [UsedImplicitly]
    public sealed class FluidCargoStoreSimulationRenderer
        : CargoStoreSimulationRenderer<FluidId, FluidCargoStoreState, FluidCargoStoreSimulation>
    {
        public FluidCargoStoreSimulationRenderer(IMapModel map, IShapeRegistry shapes)
            : base(map, shapes)
        {
        }

        protected override void Draw(
            FrameDrawOptions options, CargoPackageDrawers drawers,
            FluidCargoStoreSimulation store, int layer, int index, Matrix4x4 trs)
        {
            CargoPackage<FluidId> package = store.PackageAt(layer, index);
            drawers.DrawFluid(options, in package, trs);
        }
    }

    /// The combined store's renderer.
    ///
    /// Separate from CargoStoreSimulationRenderer<> rather than a third subclass of it, because
    /// that base is typed on `CargoStoreSimulation<TItem, TState>` and the combined store is
    /// deliberately not one - it owns two states, which a `Simulation<TState>` cannot.
    ///
    /// A layer only ever holds one kind, so exactly one of the two loops below does anything.
    [UsedImplicitly]
    public sealed class AnyCargoStoreSimulationRenderer
        : StatelessIslandSimulationRenderer<AnyCargoStoreSimulation, IIslandCustomDrawData>
    {
        private CargoPackageMeshes Fit;
        private bool FitResolved;

        private readonly CargoPackageDrawers Drawers;

        public AnyCargoStoreSimulationRenderer(IMapModel map, IShapeRegistry shapes)
            : base(map)
        {
            Drawers = new CargoPackageDrawers(shapes);
        }

        public override bool ShouldDraw(LODRenderConfig lod)
        {
            return lod.BuildingLOD <= 2;
        }

        public override void OnDrawDynamic(in Entity entity, FrameDrawOptions options)
        {
            if (!options.ShouldRenderPlatformContentsAtLayer(entity.Transform.Position.z))
            {
                return;
            }

            if (!FitResolved)
            {
                Fit = new CargoPackageMeshes(
                    options.Theme.BaseResources.Trains?.Cargo,
                    CargoRack.SlotPitch * 0.88f, CargoRack.ShelfGap * 0.9f);
                FitResolved = true;
            }

            WorldCoordinate centre = entity.Transform.Position.ToCenter_W();
            GridRotation rotation = entity.Transform.Rotation;
            Quaternion spin = FastMatrix.RotateY(rotation);
            Vector3 scale = Vector3.one * Fit.Scale;

            for (int layer = 0; layer < CargoRack.Shelves.Length; layer++)
            {
                int held = Math.Min(entity.Simulation.CountAt(layer), CargoRack.SlotsPerShelf);
                bool fluid = entity.Simulation.HoldsFluid(layer);

                for (int index = 0; index < held; index++)
                {
                    Matrix4x4 trs = Matrix4x4.TRS(
                        (Vector3)(centre + CargoRack.SlotOffset(layer, index, Fit.Bottom, rotation)),
                        spin, scale);

                    if (fluid)
                    {
                        CargoPackage<FluidId> package = entity.Simulation.FluidAt(layer, index);
                        Drawers.DrawFluid(options, in package, trs);
                    }
                    else
                    {
                        CargoPackage<ShapeId> package = entity.Simulation.ShapeAt(layer, index);
                        Drawers.DrawShape(options, in package, trs);
                    }
                }
            }
        }
    }
}
