using System;
using System.Collections.Generic;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Works out what UV a generated mesh should carry for each of its colour roles.
    ///
    /// **This is why the machines came out bright red.** Colour on an island mesh is not a
    /// material property, it is a lookup into a shared texture atlas through UV0 - the shipped
    /// DiagonalCutter.fbx proves it, with a `base_color_texture` and 319 distinct UVs packed into
    /// a small sub-rectangle. The generated meshes were given a placeholder UV of (0.25, 0.75)
    /// because the atlas is authored Unity data that cannot be read out of the decompiled
    /// assemblies, and whatever sits at that coordinate is red.
    ///
    /// So each generated mesh is authored with a *sentinel* UV per role - see PALETTE in
    /// Tools/generate_meshes.py - and every vertex carrying a given sentinel is rewritten to a
    /// resolved coordinate. There are two ways to resolve one, tried in order.
    ///
    /// **Read the palette.** `UberBuildingShader` carries `_MaterialLUT`, a 256x256 *material
    /// palette* - not an albedo texture; the shader has no `_BaseMap` and no `_MainTex` at all,
    /// and everything else in it is procedural surface treatment layered over whatever UV0
    /// samples. A palette can be read: `Graphics.Blit` to a `RenderTexture` and `ReadPixels`
    /// back works on a texture that is not CPU-readable, which shipped textures never are. So
    /// each role names the colour it *wants* and gets the atlas cell nearest to it. That is the
    /// difference between four colours and as many as the art has.
    ///
    /// **Or copy a coordinate off a vanilla mesh.** The original method, kept as the fallback
    /// for when the atlas cannot be read: `CargoExchangerDrawer` draws `Trains.Cargo`'s packages
    /// with `Theme.BaseResources.IslandMaterial`, the same pairing used here, so whatever UV
    /// those meshes carry is by construction a sensible island colour. It only yields a handful
    /// of coordinates - and most shipped meshes are not CPU-readable either - so the extra roles
    /// alias onto the five it can find.
    ///
    /// Either way `cargotools.palette` reports what was resolved and `cargotools.uv` moves a
    /// role live, because a colour is the definition of something that has to be judged by eye.
    internal sealed class CargoPalette
    {
        /// Sentinels, matching PALETTE in Tools/generate_meshes.py. They sit in the bottom-left
        /// corner of UV space at intervals no real coordinate would land on, so matching them is
        /// unambiguous, and so a mesh that somehow escapes remapping fails visibly in one corner
        /// of the atlas rather than looking almost right.
        public const float SentinelV = 0.01f;

        /// The roles a mesh can be authored in, and the colour each one is asking the atlas for.
        ///
        /// **Order is load-bearing**: `Sentinel(n)` is `((n + 1) / 100, SentinelV)` and
        /// generate_meshes.py builds the same sentinels from a list in this same order. Append
        /// rather than insert, or every mesh on disk shifts a role to the left.
        ///
        /// The targets are asks, not results. Nothing guarantees the atlas holds a yellow, and
        /// the nearest cell to one is whatever the art actually has - which is the point: a role
        /// lands in the game's palette even when its ask was optimistic. What the roles are
        /// *named* for is the job the colour does on a machine, so reassigning geometry stays a
        /// matter of intent rather than of remembering which grey was which.
        private static readonly (string Role, Color Target)[] Wanted =
        {
            // The original five keep their positions, so a mesh generated before this change
            // still resolves to the role it was authored with.
            ("hull", Rgb(196, 198, 196)),      // machine body, light warm grey
            ("accent", Rgb(244, 158, 36)),     // the orange on a platform edge
            ("metal", Rgb(140, 146, 154)),     // brushed steel
            ("fluid", Rgb(110, 140, 168)),     // pipework blue-grey
            ("cargo", Rgb(176, 168, 148)),     // container sand

            ("hullDark", Rgb(120, 124, 130)),  // the body in shade, for panel breaks
            ("deck", Rgb(158, 160, 162)),      // walkable surface
            ("frame", Rgb(74, 78, 86)),        // structural members, legs, gantries
            ("rail", Rgb(214, 218, 224)),      // bright metal capping
            ("trim", Rgb(150, 130, 106)),      // warm mid, for worn edges and crate banding
            ("rubber", Rgb(38, 40, 46)),       // belts, gaskets, tyres
            ("warn", Rgb(238, 206, 62)),       // hazard yellow
            ("glass", Rgb(96, 190, 214)),      // windows and screens
            ("light", Rgb(70, 150, 240)),      // lit indicators
            ("copper", Rgb(186, 122, 74)),     // warm metal, pipe collars
            ("shadow", Rgb(56, 58, 64)),       // deep recesses and undersides
            ("pale", Rgb(236, 238, 240)),      // off-white highlights
            ("wear", Rgb(140, 86, 58)),        // rust and scuffing
        };

        public static readonly string[] Roles = BuildRoles();

        /// The rectangle of the atlas the art actually uses.
        ///
        /// Measured off DiagonalCutter.fbx, the shipped sample building: its 319 UVs occupy
        /// U[0.095, 0.476] V[0.587, 0.919] and nothing else. A 256x256 palette is mostly unused
        /// space, and an unused cell is usually black - so an unconstrained nearest-colour search
        /// answers "rubber" and "shadow" with a hole in the atlas rather than with a colour
        /// somebody chose. Searching only where a vanilla building sampled cannot do that.
        private const float WindowMinU = 0.09f;
        private const float WindowMaxU = 0.48f;
        private const float WindowMinV = 0.58f;
        private const float WindowMaxV = 0.93f;

        private readonly Dictionary<string, Vector2> Resolved = new Dictionary<string, Vector2>();
        private readonly ILogger Log;

        public CargoPalette(ILogger logger)
        {
            Log = logger;
        }

        public bool Ready => Resolved.Count > 0;

        public IEnumerable<KeyValuePair<string, Vector2>> All => Resolved;

        /// Where the palette came from, for `cargotools.palette` to report.
        public string Source { get; private set; } = "unresolved";

        /// The sentinel a role was authored with. Role index n sits at u = (n + 1) / 100.
        public static Vector2 Sentinel(int roleIndex)
        {
            return new Vector2((roleIndex + 1) / 100f, SentinelV);
        }

        private static Color Rgb(int r, int g, int b)
        {
            return new Color(r / 255f, g / 255f, b / 255f);
        }

        private static string[] BuildRoles()
        {
            string[] names = new string[Wanted.Length];
            for (int i = 0; i < Wanted.Length; i++)
            {
                names[i] = Wanted[i].Role;
            }

            return names;
        }

        /// Picks a UV for every role, from the material palette if it can be read and off vanilla
        /// meshes if it cannot.
        public void Resolve(VisualThemeBaseResources theme)
        {
            Resolved.Clear();
            Source = "unresolved";

            if (ResolveFromAtlas(theme))
            {
                return;
            }

            ResolveFromMeshes(theme);

            if (!Ready)
            {
                Log.Warning?.Log(
                    "Could not sample any vanilla UV; cargo machines keep their sentinel colours. "
                    + "Run cargotools.dumpatlas and set them with cargotools.uv.");
            }
        }

        // --------------------------------------------------------------- the material palette

        /// Reads `_MaterialLUT` and gives every role the cell closest to the colour it asked for.
        private bool ResolveFromAtlas(VisualThemeBaseResources theme)
        {
            Texture2D atlas = null;

            try
            {
                atlas = ReadAtlas(theme);
                if (atlas == null)
                {
                    return false;
                }

                Color32[] pixels = atlas.GetPixels32();
                int width = atlas.width;
                int height = atlas.height;

                int matched = 0;

                foreach ((string role, Color target) in Wanted)
                {
                    if (TryNearest(pixels, width, height, target, out Vector2 uv))
                    {
                        Resolved[role] = uv;
                        matched++;
                    }
                }

                if (matched == 0)
                {
                    Log.Info?.Log(
                        "The material palette held no usable cell in the window vanilla art "
                        + "samples from; falling back to sampling vanilla meshes.");
                    return false;
                }

                Source = $"_MaterialLUT {width}x{height}";
                Log.Info?.Log($"Resolved {matched}/{Wanted.Length} colour roles from the "
                    + $"{width}x{height} material palette.");
                return true;
            }
            catch (Exception exception)
            {
                // Never take the session down for a colour. Without this the meshes keep their
                // sentinels and come out wrong in one corner of the atlas, which is visible and
                // survivable; a throw here is neither.
                Log.Info?.Log($"Could not read the material palette: {exception.Message}");
                return false;
            }
            finally
            {
                if (atlas != null)
                {
                    UnityEngine.Object.Destroy(atlas);
                }
            }
        }

        /// Copies the island material's palette into a texture that can be read.
        ///
        /// Through a `RenderTexture` rather than `GetPixels` directly: a texture shipped in a
        /// build is almost never CPU-readable - Read/Write Enabled is off by default because it
        /// doubles the memory - and `GetPixels` on one throws. Blitting goes via the GPU instead,
        /// which works regardless, and is the only reason any of this is possible.
        private Texture2D ReadAtlas(VisualThemeBaseResources theme)
        {
            if (theme?.IslandMaterial == null
                || !theme.IslandMaterial.TryGet(0, out IMaterialReference reference))
            {
                return null;
            }

            Material material = reference.GetMaterialInternal();
            if (material == null)
            {
                return null;
            }

            Texture source = FindPalette(material);
            if (source == null)
            {
                Log.Info?.Log($"Shader {material.shader?.name} exposes no _MaterialLUT.");
                return null;
            }

            RenderTexture target = RenderTexture.GetTemporary(
                source.width, source.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;

            try
            {
                Graphics.Blit(source, target);
                RenderTexture.active = target;

                Texture2D readable = new(source.width, source.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                readable.Apply();
                return readable;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        /// The palette texture, by name.
        ///
        /// `GetTexturePropertyNames` returns Unity's own per-renderer globals - `unity_Lightmaps`
        /// and friends - at the *head* of the list, so "take the first texture property" picks a
        /// lightmap. Named lookup only, and no guessing: a wrong texture here would resolve every
        /// role against something that is not a palette at all, and the result would look
        /// deliberate rather than broken.
        private static Texture FindPalette(Material material)
        {
            foreach (string name in material.GetTexturePropertyNames())
            {
                if (name.StartsWith("unity_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (name.IndexOf("MaterialLUT", StringComparison.OrdinalIgnoreCase) >= 0
                    && material.GetTexture(name) is Texture texture)
                {
                    return texture;
                }
            }

            return null;
        }

        /// The UV of the atlas cell closest to `target`, searched only inside the window vanilla
        /// art uses.
        ///
        /// Cells on the boundary between two swatches are skipped. A palette is blocks of flat
        /// colour, the sampler filters bilinearly, and a coordinate landing on the seam between
        /// two blocks reads as a blend of both - which at a distance is a colour that appears
        /// nowhere in the game. Requiring the four neighbours to match means the coordinate is
        /// inside a block, so it survives filtering.
        private static bool TryNearest(
            Color32[] pixels, int width, int height, Color target, out Vector2 uv)
        {
            uv = default;

            int x0 = Mathf.Clamp(Mathf.FloorToInt(WindowMinU * width), 1, width - 2);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(WindowMaxU * width), 1, width - 2);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(WindowMinV * height), 1, height - 2);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(WindowMaxV * height), 1, height - 2);

            // Two passes over the same window: interior cells first, and if the palette turns
            // out to be finer than one cell per swatch, anything opaque on the second.
            for (int pass = 0; pass < 2; pass++)
            {
                float best = float.MaxValue;
                int bestX = -1;
                int bestY = -1;

                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        Color32 here = pixels[y * width + x];
                        if (here.a < 128)
                        {
                            continue;
                        }

                        if (pass == 0 && !Interior(pixels, width, x, y))
                        {
                            continue;
                        }

                        float distance = Distance(here, target);
                        if (distance < best)
                        {
                            best = distance;
                            bestX = x;
                            bestY = y;
                        }
                    }
                }

                if (bestX >= 0)
                {
                    // The centre of the texel, not its corner, for the same filtering reason.
                    uv = new Vector2((bestX + 0.5f) / width, (bestY + 0.5f) / height);
                    return true;
                }
            }

            return false;
        }

        /// Whether a cell's four neighbours carry the same colour it does.
        private static bool Interior(Color32[] pixels, int width, int x, int y)
        {
            Color32 here = pixels[y * width + x];

            return Same(here, pixels[y * width + x - 1])
                && Same(here, pixels[y * width + x + 1])
                && Same(here, pixels[(y - 1) * width + x])
                && Same(here, pixels[(y + 1) * width + x]);
        }

        private static bool Same(Color32 a, Color32 b)
        {
            return Mathf.Abs(a.r - b.r) <= 2 && Mathf.Abs(a.g - b.g) <= 2 && Mathf.Abs(a.b - b.b) <= 2;
        }

        /// Squared distance weighted towards how the eye reads it, so a role asking for a warm
        /// mid grey is not answered with a green of the same brightness.
        private static float Distance(Color32 pixel, Color target)
        {
            float dr = pixel.r / 255f - target.r;
            float dg = pixel.g / 255f - target.g;
            float db = pixel.b / 255f - target.b;

            return 0.30f * dr * dr + 0.59f * dg * dg + 0.11f * db * db
                // Brightness on its own as well: two colours can be close channel by channel and
                // still read as different materials if one is plainly darker, and the roles lean
                // on light-versus-dark more than on hue.
                + 0.5f * Mathf.Pow(
                    (0.30f * pixel.r / 255f + 0.59f * pixel.g / 255f + 0.11f * pixel.b / 255f)
                    - (0.30f * target.r + 0.59f * target.g + 0.11f * target.b), 2f);
        }

        // ------------------------------------------------------------------- vanilla meshes

        /// The original method: take coordinates off meshes the game draws with this material.
        ///
        /// Only a handful of coordinates come out of it, so the roles the atlas would have given
        /// their own colour share. That is a real loss of detail and is why it is the fallback.
        private void ResolveFromMeshes(VisualThemeBaseResources theme)
        {
            List<Vector2> cargo = Sample(theme.Trains?.Cargo?.ShapeCargoPackage, "ShapeCargoPackage");
            List<Vector2> fluid = Sample(theme.Trains?.Cargo?.FluidCargoPackage, "FluidCargoPackage");
            List<Vector2> belt = Sample(theme.SpaceBeltForwardStructureMesh, "SpaceBeltForwardStructureMesh");

            Vector2? body = Pick(belt, 0) ?? Pick(cargo, 0);
            Vector2? second = Pick(belt, 1) ?? body;
            Vector2? crate = Pick(cargo, 0) ?? body;
            Vector2? crateTrim = Pick(cargo, 1) ?? crate;
            Vector2? liquid = Pick(fluid, 0) ?? second;

            Set("hull", body);
            Set("accent", crateTrim);
            Set("metal", second);
            Set("fluid", liquid);
            Set("cargo", crate);

            // Everything the atlas would have separated, folded onto the nearest of the five.
            // Named so the geometry does not have to change between the two paths.
            Set("hullDark", second);
            Set("deck", body);
            Set("frame", second);
            Set("rail", body);
            Set("trim", crateTrim);
            Set("rubber", second);
            Set("warn", crateTrim);
            Set("glass", liquid);
            Set("light", liquid);
            Set("copper", crateTrim);
            Set("shadow", second);
            Set("pale", body);
            Set("wear", crateTrim);

            if (Ready)
            {
                Source = "vanilla meshes (atlas unreadable)";
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
                // not an exceptional one - hence the atlas path existing at all.
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
