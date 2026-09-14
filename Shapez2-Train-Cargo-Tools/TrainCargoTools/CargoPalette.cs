using System;
using System.Collections.Generic;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Works out what UV a generated mesh should carry, by reading one off a vanilla mesh.
    ///
    /// **This is why the machines came out bright red.** Colour on an island mesh is not a
    /// material property, it is a lookup into a shared texture atlas through UV0 - the shipped
    /// DiagonalCutter.fbx proves it, with a `base_color_texture` and 319 distinct UVs packed into
    /// a small sub-rectangle. The generated meshes were given a placeholder UV of (0.25, 0.75)
    /// because the atlas is authored Unity data that cannot be read out of the decompiled
    /// assemblies, and whatever sits at that coordinate is red.
    ///
    /// Guessing a second time would be no better. So instead of guessing, take the coordinate
    /// from geometry the game already draws with the same material: CargoExchangerDrawer draws
    /// `Trains.Cargo.ShapeCargoPackage` with `Theme.BaseResources.IslandMaterial`, which is
    /// exactly the mesh-and-material pairing used here. Whatever UV that mesh carries is, by
    /// construction, a coordinate that lands on a sensible island colour.
    ///
    /// So each generated mesh is authored with a *sentinel* UV per role - see PALETTE in
    /// Tools/generate_meshes.py - and every vertex carrying a given sentinel is rewritten to the
    /// coordinate sampled for that role. The result is not art-directed, but it is in-palette and
    /// in-theme rather than a guess, and `cargotools.uv` can move any role live.
    internal sealed class CargoPalette
    {
        /// Sentinels, matching PALETTE in Tools/generate_meshes.py. They sit in the bottom-left
        /// corner of UV space at intervals no real coordinate would land on, so matching them is
        /// unambiguous, and so a mesh that somehow escapes remapping fails visibly in one corner
        /// of the atlas rather than looking almost right.
        public const float SentinelV = 0.01f;

        public static readonly string[] Roles = { "hull", "accent", "metal", "fluid", "cargo" };

        private readonly Dictionary<string, Vector2> Resolved = new Dictionary<string, Vector2>();
        private readonly ILogger Log;

        public CargoPalette(ILogger logger)
        {
            Log = logger;
        }

        public bool Ready => Resolved.Count > 0;

        public IEnumerable<KeyValuePair<string, Vector2>> All => Resolved;

        /// The sentinel a role was authored with. Role index n sits at u = (n + 1) / 100.
        public static Vector2 Sentinel(int roleIndex)
        {
            return new Vector2((roleIndex + 1) / 100f, SentinelV);
        }

        /// Picks a UV for every role off vanilla meshes.
        ///
        /// Roles that want to look like cargo take the cargo package's own coordinate; the
        /// structural roles take the space belt's. Where a role needs to differ from its
        /// neighbour it takes the *second* most common coordinate on the same mesh rather than a
        /// coordinate from somewhere unrelated - two shades that the artists already put next to
        /// each other will still look like they belong together.
        public void Resolve(VisualThemeBaseResources theme)
        {
            Resolved.Clear();

            List<Vector2> cargo = Sample(theme.Trains?.Cargo?.ShapeCargoPackage, "ShapeCargoPackage");
            List<Vector2> fluid = Sample(theme.Trains?.Cargo?.FluidCargoPackage, "FluidCargoPackage");
            List<Vector2> belt = Sample(theme.SpaceBeltForwardStructureMesh, "SpaceBeltForwardStructureMesh");

            Set("cargo", Pick(cargo, 0) ?? Pick(belt, 0));
            Set("accent", Pick(cargo, 1) ?? Pick(cargo, 0) ?? Pick(belt, 0));
            Set("fluid", Pick(fluid, 0) ?? Pick(cargo, 0) ?? Pick(belt, 0));
            Set("hull", Pick(belt, 0) ?? Pick(cargo, 0));
            Set("metal", Pick(belt, 1) ?? Pick(belt, 0) ?? Pick(cargo, 0));

            if (!Ready)
            {
                Log.Warning?.Log(
                    "Could not sample any vanilla UV; cargo machines keep their sentinel colours. "
                    + "Run cargotools.dumpatlas and set them with cargotools.uv.");
            }
        }

        private void Set(string role, Vector2? uv)
        {
            if (uv.HasValue)
            {
                Resolved[role] = uv.Value;
            }
        }

        public bool TryGet(string role, out Vector2 uv)
        {
            return Resolved.TryGetValue(role, out uv);
        }

        public void Override(string role, Vector2 uv)
        {
            Resolved[role] = uv;
        }

        /// The distinct UVs on a mesh, most-used first.
        ///
        /// Ranking by how many vertices share a coordinate is a decent proxy for "the main
        /// colour of this object": the body of a crate has far more area, and so more vertices,
        /// than its trim.
        private List<Vector2> Sample(ILODMesh asset, string name)
        {
            List<Vector2> ranked = new();

            try
            {
                if (asset == null || !asset.TryGet(0, out IMeshReference reference))
                {
                    return ranked;
                }

                Mesh mesh = reference.GetMeshInternal();

                // A mesh imported without Read/Write Enabled has no CPU-side copy and throws on
                // access. Most shipped meshes are like that, so this is the expected failure,
                // not an exceptional one - hence the atlas-dump fallback existing at all.
                if (mesh == null || !mesh.isReadable)
                {
                    Log.Info?.Log($"{name} is not CPU-readable; cannot sample its UVs");
                    return ranked;
                }

                Vector2[] uvs = mesh.uv;
                if (uvs == null || uvs.Length == 0)
                {
                    return ranked;
                }

                Dictionary<Vector2, int> counts = new();
                foreach (Vector2 uv in uvs)
                {
                    // Rounded before counting. Authored UVs cluster tightly rather than
                    // repeating exactly, so counting raw floats would rank every coordinate as
                    // equally rare and pick an arbitrary one.
                    Vector2 key = new(Mathf.Round(uv.x * 400f) / 400f, Mathf.Round(uv.y * 400f) / 400f);
                    counts.TryGetValue(key, out int seen);
                    counts[key] = seen + 1;
                }

                List<KeyValuePair<Vector2, int>> ordered = new(counts);
                ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
                foreach (KeyValuePair<Vector2, int> entry in ordered)
                {
                    ranked.Add(entry.Key);
                }

                Log.Info?.Log(
                    $"Sampled {ranked.Count} distinct UVs from {name}; "
                    + $"most common {ranked[0]} on {ordered[0].Value}/{uvs.Length} vertices");
            }
            catch (Exception exception)
            {
                Log.Info?.Log($"Could not sample {name}: {exception.Message}");
            }

            return ranked;
        }

        private static Vector2? Pick(List<Vector2> ranked, int index)
        {
            return index < ranked.Count ? ranked[index] : (Vector2?)null;
        }
    }
}
