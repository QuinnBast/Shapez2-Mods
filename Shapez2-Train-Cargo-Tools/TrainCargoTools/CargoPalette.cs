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

        /// What each role asks the palette for.
        ///
        /// **Order is load-bearing**: `Sentinel(n)` is `((n + 1) / 100, SentinelV)` and
        /// generate_meshes.py builds the same sentinels from a list in this same order. Append
        /// rather than insert, or every mesh on disk shifts a role to the left.
        ///
        /// Most roles ask for a *brightness*, because that is what the palette itself has -
        /// but the four that want colour ask the **accent palette** instead, which is where the
        /// game keeps its real colours. See AccentSlot.
        ///
        /// `_MaterialLUT` dumped out of the shipped build is 256x256, three quarters of it
        /// magenta filler, and of the 32 swatches larger than a few pixels **26 are neutral**:
        /// a ladder from white through to black. The only real colours in it are pure red, a
        /// teal and a periwinkle. There is no orange, no yellow and nothing warm at all - the
        /// orange on a vanilla platform edge does not come from here.
        ///
        /// So the separation available is light against dark, which is also how the shipped
        /// buildings read once you look for it. Roles ask for a rung on that ladder; the three
        /// that genuinely want a hue ask for one of the three that exist.
        private enum Ask
        {
            /// Nearest rung of the neutral ladder to `Target`'s brightness.
            Value,

            /// Nearest of the handful of actually-coloured swatches in the palette itself.
            Colour,

            /// A slot in the game's live accent palette - see AccentSlot. `Target.r * 255`
            /// carries the slot number, because the table is already a table of colours.
            Accent,
        }

        private static readonly (string Role, Color Target, Ask Kind)[] Wanted =
        {
            // The original five keep their positions, so a mesh generated before this change
            // still resolves to the role it was authored with.
            ("hull", Grey(0.78f), Ask.Value),       // machine body
            ("accent", Slot(0), Ask.Accent),        // the belt rail, in the game's own accent
            ("metal", Grey(0.60f), Ask.Value),      // brushed steel
            ("fluid", Grey(0.58f), Ask.Value),      // tanks and pipework
            ("cargo", Grey(0.65f), Ask.Value),      // the duct that carries packed cargo

            ("hullDark", Grey(0.48f), Ask.Value),   // the body in shade, for panel breaks
            ("deck", Grey(0.70f), Ask.Value),       // walkable surface
            ("frame", Grey(0.32f), Ask.Value),      // structural members, legs, gantries
            ("rail", Grey(0.84f), Ask.Value),       // bright metal capping
            ("trim", Grey(0.55f), Ask.Value),       // banding and edges
            ("rubber", Grey(0.13f), Ask.Value),     // belts, gaskets, tyres
            ("warn", Slot(1), Ask.Accent),          // hazard
            ("glass", Slot(2), Ask.Accent),         // windows and screens
            ("light", Slot(3), Ask.Accent),         // lit indicators
            ("collar", Grey(0.42f), Ask.Value),     // pipe joints and flanges
            ("shadow", Grey(0.20f), Ask.Value),     // deep recesses and undersides
            ("pale", Grey(0.97f), Ask.Value),       // highlights
            ("scuff", Grey(0.27f), Ask.Value),      // worn and dirtied faces
        };

        public static readonly string[] Roles = BuildRoles();

        /// The UV that paints a face in accent colour `slot`.
        ///
        /// **This is where the game keeps its colours, and it is not in the LUT.** The palette
        /// texture is a 16x16 grid, and one column of it - `u` in `[0.0625, 0.125)` - is the
        /// accent column: a face whose UV lands there is painted from `_G_AccentColorPalette`,
        /// a global array of up to 15 live colours that `AccentColorPalette.Update` pushes into
        /// every shader from `MetaAccentColorPalette`. The LUT paints that column red as a
        /// placeholder, which is why every hue found in it was a red while the buildings on
        /// screen are orange - the red is never what renders.
        ///
        /// The arithmetic is `AccentColorMeshCreator.TryGenerateUniqueAccentColoredMeshRef`'s,
        /// read backwards. It recolours a mesh by *sliding UVs down this column*:
        ///
        /// ```csharp
        /// if ((int)math.floor((vector.x + -0.0625f) * 16f) != 0) { /* not accent */ }
        /// int id = (int)((0.9375 - (double)vector.y) * 16.0);
        /// ```
        ///
        /// So the column test is on `u` alone and the slot is a row of `v`. Fifteen slots are
        /// addressable: slot 15 would sit at `v = -0.03125`, off the texture.
        ///
        /// `cargotools.accents` prints what each slot currently holds, because the entries are
        /// authored ScriptableObject data and cannot be read statically - only off the live
        /// shader global.
        public static Vector2 AccentSlot(int slot)
        {
            return new Vector2(0.09375f, 0.90625f - slot * 0.0625f);
        }

        /// A slot number wearing a `Color`, so the role table stays one shape.
        private static Color Slot(int slot)
        {
            return new Color(slot / 255f, 0f, 0f);
        }

        /// The live accent palette, or an empty array when the shader global is not set.
        ///
        /// A global vector array survives being read back, so this is the only way to find out
        /// what a slot actually looks like. The colours are pushed as `.linear`, so they are
        /// converted back for reporting rather than shown as the darker linear figures.
        public static Color[] LiveAccents()
        {
            Vector4[] raw = Shader.GetGlobalVectorArray("_G_AccentColorPalette");
            if (raw == null)
            {
                return Array.Empty<Color>();
            }

            Color[] colours = new Color[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                colours[i] = new Color(raw[i].x, raw[i].y, raw[i].z, 1f).gamma;
            }

            return colours;
        }

        /// The colour a `_MaterialLUT` cell carries when nothing is assigned to it.
        ///
        /// Three quarters of the palette is this magenta, and it is **fully opaque** - so an
        /// alpha test does not reject it and a nearest-colour search will happily answer with a
        /// hole in the atlas. It is the classic "no texture" pink, chosen to be impossible to
        /// mistake for art, which also makes it safe to reject by value.
        private static bool IsUnassigned(Color32 cell)
        {
            return cell.r > 230 && cell.g < 60 && cell.b > 190;
        }

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

        /// A rung on the neutral ladder, given as brightness.
        private static Color Grey(float value)
        {
            return new Color(value, value, value);
        }

        /// Rec. 601 luma, which is what "one of these is plainly darker" means to the eye.
        private static float Luma(Color32 cell)
        {
            return (0.30f * cell.r + 0.59f * cell.g + 0.11f * cell.b) / 255f;
        }

        private static float Luma(Color colour)
        {
            return 0.30f * colour.r + 0.59f * colour.g + 0.11f * colour.b;
        }

        /// How far a colour is from grey, as an **absolute** spread across the channels.
        ///
        /// Not `(max - min) / max`, which is the usual definition and is wrong here: on a
        /// near-black it divides by almost nothing, so `(19, 19, 30)` scores 0.37 and is filed
        /// as a hue. The palette has three of those blue-blacks, and calling them coloured both
        /// took the ladder's three darkest rungs away from the roles that wanted them and left
        /// them sitting in the pool where a role asking for a hue could have claimed one.
        ///
        /// Dividing by the full range instead asks "how far apart are the channels", which is
        /// what "is this grey" means. Measured on the shipped palette the answer separates
        /// cleanly: the three real colours score 0.30 to 1.00 and every other cell scores 0.06
        /// or less, so the threshold sits in a gap rather than on a judgement call.
        private static float Chroma(Color32 cell)
        {
            int max = Mathf.Max(cell.r, Mathf.Max(cell.g, cell.b));
            int min = Mathf.Min(cell.r, Mathf.Min(cell.g, cell.b));
            return (max - min) / 255f;
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

        /// Reads `_MaterialLUT` and gives every role a cell of its own.
        ///
        /// **Of its own** is the whole difference between this and the first attempt. Asking
        /// eighteen roles independently for their nearest cell gave ten answers: four roles
        /// shared one mid grey, and the six that wanted a hue all landed on greys, because the
        /// palette holds no orange for them to find. A machine with ten colours where the model
        /// names eighteen reads exactly as flat as one with four.
        ///
        /// So the roles are assigned greedily against a set of cells already spoken for. The
        /// ones asking for a hue go first, because only a handful of coloured cells exist while
        /// the neutral ladder has rungs to spare.
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

                List<Swatch> swatches = Swatches(atlas);
                if (swatches.Count == 0)
                {
                    Log.Info?.Log(
                        "The material palette held no usable cell; falling back to sampling "
                        + "vanilla meshes.");
                    return false;
                }

                HashSet<int> taken = new HashSet<int>();
                int matched = 0;

                // Accent asks are not a search at all - the slot names its own coordinate, and
                // nothing in the LUT has to be consulted or claimed.
                foreach ((string role, Color target, Ask ask) in Wanted)
                {
                    if (ask == Ask.Accent)
                    {
                        Resolved[role] = AccentSlot(Mathf.RoundToInt(target.r * 255f));
                        matched++;
                    }
                }

                // Colour asks before Value ones: the palette has three coloured swatches against
                // two dozen neutral, so letting a Value role take a coloured cell would cost a
                // Colour role the only cell that could have served it.
                foreach (Ask kind in new[] { Ask.Colour, Ask.Value })
                {
                    foreach ((string role, Color target, Ask ask) in Wanted)
                    {
                        if (ask != kind)
                        {
                            continue;
                        }

                        if (TryClaim(swatches, taken, target, ask, out Vector2 uv))
                        {
                            Resolved[role] = uv;
                            matched++;
                        }
                    }
                }

                if (matched == 0)
                {
                    return false;
                }

                Source = $"_MaterialLUT {atlas.width}x{atlas.height}, "
                    + $"{swatches.Count} usable cell(s)";
                Log.Info?.Log($"Resolved {matched}/{Wanted.Length} colour roles from the "
                    + $"{atlas.width}x{atlas.height} material palette "
                    + $"({swatches.Count} usable cells).");
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

        /// One usable cell of the palette: where it is, and what colour it holds.
        private readonly struct Swatch
        {
            public readonly Vector2 UV;
            public readonly Color32 Colour;
            public readonly float Luma;
            public readonly float Chroma;

            public Swatch(Vector2 uv, Color32 colour, float luma, float chroma)
            {
                UV = uv;
                Colour = colour;
                Luma = luma;
                Chroma = chroma;
            }
        }

        /// Every distinct colour the palette actually assigns, with a coordinate that samples it
        /// cleanly.
        ///
        /// Three filters, each for a failure that looks deliberate rather than broken:
        ///
        /// - **Unassigned cells are rejected by colour, not by alpha.** The filler magenta is
        ///   fully opaque, so an alpha test keeps it, and a role asking for something the
        ///   palette does not have gets answered with a hole in the atlas.
        /// - **Only cells whose four neighbours match.** A palette is blocks of flat colour and
        ///   the sampler filters bilinearly, so a coordinate on the seam between two blocks
        ///   renders as a blend of both - a colour that appears nowhere in the game. It also
        ///   discards the antialiased fringe around the magenta, which is otherwise a few
        ///   hundred near-pink cells that a warm role would leap at.
        /// - **One entry per *distinguishable* colour.** Exact-RGB deduplication is not enough:
        ///   the ladder repeats across columns and the red block carries near-identical
        ///   neighbours a channel or two apart, so the greedy assignment above would hand two
        ///   roles the same colour twice over while believing they differed.
        private static List<Swatch> Swatches(Texture2D atlas)
        {
            Color32[] pixels = atlas.GetPixels32();
            int width = atlas.width;
            int height = atlas.height;

            Dictionary<int, Vector2> where = new Dictionary<int, Vector2>();
            Dictionary<int, int> area = new Dictionary<int, int>();

            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    Color32 here = pixels[y * width + x];

                    if (here.a < 128 || IsUnassigned(here) || !Interior(pixels, width, x, y))
                    {
                        continue;
                    }

                    int key = (here.r << 16) | (here.g << 8) | here.b;

                    area.TryGetValue(key, out int seen);
                    area[key] = seen + 1;

                    // The centre of the texel, not its corner, for the same filtering reason
                    // the neighbours are checked at all.
                    if (!where.ContainsKey(key))
                    {
                        where[key] = new Vector2((x + 0.5f) / width, (y + 0.5f) / height);
                    }
                }
            }

            // Biggest block first, then keep only cells that differ visibly from one already
            // kept. Distinct-by-exact-RGB counted 36 cells in the shipped palette and there are
            // 27: the red block carries four near-identical neighbours a channel or two apart,
            // and several rungs of the ladder repeat within three levels of each other. Every
            // one of those is a cell a role can claim while believing it took a different
            // colour, which is the failure this whole pass exists to stop.
            const int Indistinguishable = 6;

            List<int> ordered = new List<int>(area.Keys);
            ordered.Sort((a, b) => area[b].CompareTo(area[a]));

            List<Swatch> kept = new List<Swatch>();

            foreach (int key in ordered)
            {
                Color32 colour = new Color32(
                    (byte)((key >> 16) & 0xFF), (byte)((key >> 8) & 0xFF), (byte)(key & 0xFF), 255);

                bool duplicate = false;
                foreach (Swatch already in kept)
                {
                    if (Mathf.Max(
                            Mathf.Abs(already.Colour.r - colour.r),
                            Mathf.Max(
                                Mathf.Abs(already.Colour.g - colour.g),
                                Mathf.Abs(already.Colour.b - colour.b)))
                        <= Indistinguishable)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    kept.Add(new Swatch(where[key], colour, Luma(colour), Chroma(colour)));
                }
            }

            return kept;
        }

        /// Takes the best unclaimed swatch for one role, or nothing if its pool is empty.
        ///
        /// A Value ask is scored on brightness alone. Scoring it on all three channels instead
        /// would let a role asking for a mid grey take the palette's teal, which is the same
        /// brightness and emphatically not the same thing.
        private static bool TryClaim(
            List<Swatch> swatches, HashSet<int> taken, Color target, Ask ask, out Vector2 uv)
        {
            uv = default;

            // The gap in the shipped palette is 0.06 to 0.30, so anywhere in between does.
            const float Chromatic = 0.12f;

            float best = float.MaxValue;
            int bestIndex = -1;

            for (int i = 0; i < swatches.Count; i++)
            {
                if (taken.Contains(i))
                {
                    continue;
                }

                Swatch swatch = swatches[i];
                bool coloured = swatch.Chroma > Chromatic;

                if (coloured != (ask == Ask.Colour))
                {
                    continue;
                }

                float distance;
                if (ask == Ask.Value)
                {
                    distance = Mathf.Abs(swatch.Luma - Luma(target));
                }
                else
                {
                    float dr = swatch.Colour.r / 255f - target.r;
                    float dg = swatch.Colour.g / 255f - target.g;
                    float db = swatch.Colour.b / 255f - target.b;
                    distance = dr * dr + dg * dg + db * db;
                }

                if (distance < best)
                {
                    best = distance;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                return false;
            }

            taken.Add(bestIndex);
            uv = swatches[bestIndex].UV;
            return true;
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
            // These four do not depend on the LUT at all, so they are right even here.
            Set("accent", AccentSlot(0));
            Set("warn", AccentSlot(1));
            Set("glass", AccentSlot(2));
            Set("light", AccentSlot(3));
            Set("collar", crateTrim);
            Set("shadow", second);
            Set("pale", body);
            Set("scuff", second);

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
