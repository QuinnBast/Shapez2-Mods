using Game.Core.Trains;
using Unity.Mathematics;
using UnityEngine;

namespace TrainCargoTools
{
    /// How much to shrink a cargo package, and where its underside sits once shrunk.
    ///
    /// The packages themselves are drawn by the game's own drawers - see CargoPackageDrawers.
    /// What those cannot do is decide how big the thing should be: they are called by vanilla
    /// with positions along a train, where a package is authored wagon-sized, roughly a chunk
    /// wide. A space path carries twelve items abreast and a store shelf holds twenty-five, so
    /// both need it smaller.
    ///
    /// The scale comes from the mesh's own bounds rather than a constant, so it survives the art
    /// changing, and `Bottom` is read the same way because the package is not authored centred
    /// on its base - without it a package on a shelf sits half through the shelf.
    internal readonly struct CargoPackageMeshes
    {
        /// Uniform scale to apply to the package.
        public readonly float Scale;

        /// The package's lowest point once scaled, relative to its origin. Negative.
        public readonly float Bottom;

        /// True when the mesh's long horizontal axis is its local X.
        ///
        /// Read from the bounds rather than assumed, because this is vanilla art authored to sit
        /// in a wagon and nothing says which way round that is. A belt uses it to turn the
        /// container across the direction of travel; guessing instead would give a container
        /// lying the wrong way the day the art changes.
        public readonly bool LongAxisIsX;

        /// `maxHeight` of zero means unconstrained, which is what a belt wants - nothing is
        /// above the track. A store's shelves are about two units apart, so there it matters.
        ///
        /// Both constraints are needed and neither is guessable: the mesh's proportions are
        /// authored art, so fitting it between lanes says nothing about whether it then fits
        /// under the shelf above. Taking the smaller of the two means a change to that art
        /// cannot start poking containers through shelves.
        public CargoPackageMeshes(
            CargoExchangeVisualResources cargo, float fitWithin, float maxHeight = 0f)
        {
            Scale = 1f;
            Bottom = 0f;
            LongAxisIsX = false;

            // The shape package is measured for both kinds. The two are the same crate with
            // different lids, and one scale keeps a mixed yard looking consistent.
            if (cargo?.ShapeCargoPackage == null
                || !cargo.ShapeCargoPackage.TryGet(0, out IMeshReference reference))
            {
                return;
            }

            Mesh mesh = reference.GetMeshInternal();
            if (mesh == null)
            {
                return;
            }

            // Bounds is metadata and readable even when a mesh has no CPU-side copy, which is
            // the usual state of a shipped mesh - unlike mesh.uv, which CargoPalette has to
            // handle failing.
            Bounds bounds = mesh.bounds;
            float widest = math.max(bounds.size.x, bounds.size.z);
            float scale = widest > 0.001f ? fitWithin / widest : 1f;

            if (maxHeight > 0f && bounds.size.y > 0.001f)
            {
                scale = math.min(scale, maxHeight / bounds.size.y);
            }

            Scale = scale;
            Bottom = bounds.min.y * scale;

            // 1.02 rather than a bare > so a square-ish crate does not flip on float noise.
            LongAxisIsX = bounds.size.x > bounds.size.z * 1.02f;
        }
    }
}
