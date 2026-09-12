using Game.Content.Features.Fluids;
using Game.Core.Rendering.Trains;
using Game.Core.Trains;
using ShapezShifter.Kit;
using UnityEngine;

namespace TrainCargoTools
{
    /// Draws a cargo package using the game's own package drawers.
    ///
    /// The first version of the belt and store renderers drew `Trains.Cargo.ShapeCargoPackage`
    /// and `FluidCargoPackage` as plain meshes. That is wrong, and obviously so for fluids: a
    /// fluid package is **three** meshes, not one, and the one this mod was drawing is the empty
    /// shell. `FluidCargoContainerDrawer` draws
    ///
    ///   - `FluidCargoPackage` with the island material - the crate,
    ///   - `FluidInsideCargoCrate` through `Renderers.FluidsContainer`, which is what applies the
    ///     fluid's own colour, via `IFluidRegistry.GetFluidReference(item)`,
    ///   - `FluidCargoContainerGlassLid` with `BuildingsGlassMaterial`.
    ///
    /// Drawing only the first leaves a colourless, lidless, see-through box.
    ///
    /// Shapes were less obviously wrong but wrong too: `ShapeCargoContainerDrawer` also draws the
    /// contained shape above the crate, so a stored or travelling shape package now says *what*
    /// it is carrying rather than just that it is carrying something.
    ///
    /// So neither is drawn by hand any more. `ICargoContainerDrawer&lt;TItem&gt;.Draw` takes a
    /// `Matrix4x4`, which is the whole reason this works - vanilla calls it with positions along a
    /// train, and nothing stops a mod calling it with a position on a belt or a shelf.
    ///
    /// Public, not internal, because it appears on a protected member of the public store
    /// renderer - the renderers themselves have to be public to be found by reflection, and an
    /// internal parameter type on a protected method is CS0051.
    public sealed class CargoPackageDrawers
    {
        private readonly ICargoContainerDrawer<ShapeId> Shapes;

        /// Resolved on first use, not injected.
        ///
        /// `CreateSimulationRenderers` binds `IShapeRegistry` into its container but **not**
        /// `IFluidRegistry`, so asking for one in a renderer's constructor would fail to
        /// construct - and since every renderer is built in one pass, that would take out every
        /// other mod's renderers too. `GameHelper.Core` has it, and by the time anything is
        /// drawn the session exists.
        private ICargoContainerDrawer<FluidId> Fluids;
        private bool FluidsResolved;

        public CargoPackageDrawers(IShapeRegistry shapes)
        {
            Shapes = new ShapeCargoContainerDrawer(shapes);
        }

        /// Draws whichever kind of package this belt item is. False if it is not one.
        public bool TryDraw(FrameDrawOptions draw, IBeltItem item, Matrix4x4 trs)
        {
            if (item is PackageOnTrack<CargoPackage<ShapeId>> shape)
            {
                Shapes.Draw(draw, in shape.Container, trs, flipped: false);
                return true;
            }

            if (item is PackageOnTrack<CargoPackage<FluidId>> fluid)
            {
                ICargoContainerDrawer<FluidId> drawer = FluidDrawer();
                if (drawer == null)
                {
                    return false;
                }

                drawer.Draw(draw, in fluid.Container, trs, flipped: false);
                return true;
            }

            return false;
        }

        public void DrawShape(FrameDrawOptions draw, in CargoPackage<ShapeId> package, Matrix4x4 trs)
        {
            Shapes.Draw(draw, in package, trs, flipped: false);
        }

        public void DrawFluid(FrameDrawOptions draw, in CargoPackage<FluidId> package, Matrix4x4 trs)
        {
            FluidDrawer()?.Draw(draw, in package, trs, flipped: false);
        }

        private ICargoContainerDrawer<FluidId> FluidDrawer()
        {
            if (!FluidsResolved)
            {
                IFluidRegistry registry = GameHelper.Core?.FluidRegistry;
                if (registry != null)
                {
                    Fluids = new FluidCargoContainerDrawer(registry);
                }

                FluidsResolved = true;
            }

            return Fluids;
        }
    }
}
