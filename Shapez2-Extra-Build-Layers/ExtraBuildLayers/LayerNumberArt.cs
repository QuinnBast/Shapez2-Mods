using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Paints the layer numbers vanilla only authored three of.
///
/// Both numbers a player reads are **pixels in authored textures**, not text and not a uniform -
/// <c>LayerVisualsDump</c> established that:
///
/// <list type="bullet">
/// <item>The platform floor's "01"/"02" is in <c>_ChunkTexture</c>
/// (<c>PlayingfieldChunkIndicatorLayer1</c>/<c>2</c>, 2048²), one material per floor in
/// <c>PlayingfieldCurrentLayerPlaneMaterialPerLayer</c>. Cycling that array (the earlier fix)
/// keeps every floor drawn but labels floor 3 "01" and floor 4 "02".</item>
/// <item>The space grid's "0"/"1"/"2" is one slice each of a <c>Texture2DArray</c>
/// (<c>IslandGridLayerIndicator</c>, 1024²). A shader sampling a slice past the end gets the last
/// one - hardware clamps the array index - which is exactly "every layer above 2 says 2".</item>
/// </list>
///
/// So the fix is more texture: one floor texture per floor, and for the grid one slice redrawn to
/// whichever layer is on screen (see <see cref="PrepareGridIndicator"/>). They are made
/// here from the vanilla ones rather than authored, because the art is simple enough to measure:
/// dumped, every one of these is **red-only** (G and B are zero everywhere, alpha opaque) - a mask
/// the shader tints - with digits in a thin rounded face. Each new image is the vanilla one with
/// its digits rubbed out and new ones stroked in at the measured height, width, stroke and
/// brightness. Single-channel R8 is therefore lossless for the content, at a quarter of RGBA's
/// memory.
///
/// Everything is read back through a blit into a **linear** RenderTexture: shipped textures are
/// not CPU-readable, and a linear target means a linear source round-trips exactly while an sRGB
/// one arrives already decoded, so the R8 result samples the same either way.
///
/// Runs inside <c>GameMode.From</c>, which <c>GameSessionOrchestrator.Init_3_1_*</c> calls on
/// the main thread - texture creation requires it. Results are cached for the process: the
/// theme's materials are shared assets that outlive a session.
/// </summary>
internal static class LayerNumberArt
{
    private const string Prefix = "ExtraBuildLayers.";

    /// <summary>Floor material index → its generated material, kept across sessions.</summary>
    private static readonly Dictionary<int, MaterialReference> FloorMaterials = new Dictionary<int, MaterialReference>();

    private static FloorLayout CachedFloorLayout;

    /// <summary>Set after one failed attempt, so a failure is logged once rather than per floor.</summary>
    private static bool FloorUnmeasurable;
    private static byte[] CachedFloorBase;

    // ------------------------------------------------------------------------------------------
    // Platform floor
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// A material for floor-plane index <paramref name="index"/> (drawn on building layer
    /// <c>index + 1</c>, labelled <c>index + 1</c> zero-padded to two digits as vanilla does), or
    /// null if the vanilla art could not be measured - the caller then falls back to cycling.
    /// </summary>
    public static MaterialReference FloorMaterial(
        int index, MaterialReference layer1, MaterialReference layer2, ILogger logger)
    {
        if (FloorMaterials.TryGetValue(index, out MaterialReference cached))
        {
            return cached;
        }

        Material template = layer2?.GetMaterialInternal();
        Texture source = template != null && template.HasProperty("_ChunkTexture") ? template.GetTexture("_ChunkTexture") : null;
        Texture other = layer1?.GetMaterialInternal() is { } m1 && m1.HasProperty("_ChunkTexture") ? m1.GetTexture("_ChunkTexture") : null;
        if (source == null || other == null)
        {
            return null;
        }

        int width = source.width;
        int height = source.height;

        if (CachedFloorBase == null || CachedFloorLayout == null)
        {
            // The "02" texture is the base, not "01": its dots are all filled. The three dots
            // under the number count the floor within vanilla's stack of three (●●○ on floor 1,
            // ●●● on floor 2) and cannot count higher, so "full" is the honest reading above it.
            if (FloorUnmeasurable)
            {
                return null;
            }

            // Measured on an sRGB read, kept from a linear one. The thresholds below were set
            // against the dump, which reads through an sRGB target; on a linear read of an sRGB
            // texture the same pixels are darker and closer together, and the search found
            // nothing in the game while finding the digits every time on the dumped PNGs. The
            // pixels that get uploaded must still be linear, because R8 has no sRGB form here.
            byte[] twoMeasured = ReadRed(source, width, height, srgb: true);
            byte[] oneMeasured = ReadRed(other, width, height, srgb: true);
            FloorLayout layout = FloorLayout.Measure(twoMeasured, oneMeasured, width, height, out string why);
            if (layout == null)
            {
                FloorUnmeasurable = true;
                logger.Error?.Log(
                    "Extra Build Layers: could not find the digits in the floor texture (" + why +
                    "); floors above 2 keep cycled labels.");
                return null;
            }

            byte[] two = ReadRed(source, width, height, srgb: false);
            layout.Brightness = layout.MaxIn(two, width);
            layout.Erase(two, width);
            CachedFloorBase = two;
            CachedFloorLayout = layout;
            logger.Info?.Log("Extra Build Layers: floor digits measured - " + layout);
        }

        byte[] pixels = (byte[])CachedFloorBase.Clone();
        CachedFloorLayout.Draw(pixels, width, (index + 1).ToString("00"));

        Texture2D texture = Upload(pixels, width, height, source, Prefix + "PlayingfieldChunkIndicatorLayer" + (index + 1));

        Material material = new Material(template) { name = Prefix + "PlayingfieldLayer" + (index + 1) };
        material.SetTexture("_ChunkTexture", texture);

        // There is no public way to point a MaterialReference at a new material: the copy
        // constructor copies the private field, and the parameterless one is private. The field
        // is publicized, and a fresh instance has not initialised its cached instance id yet, so
        // setting it before first use is the same as having been authored with it.
        MaterialReference reference = new MaterialReference(layer2);
        reference._Material = material;

        FloorMaterials[index] = reference;
        return reference;
    }

    private sealed class FloorLayout
    {
        // All in top-down image rows.
        public int Top;
        public int Bottom;
        public int FirstLeft;
        public int Pitch;
        public int GlyphWidth;
        public int EraseLeft;
        public int EraseRight;
        public float Stroke;
        public byte Brightness;

        /// <summary>
        /// Finds the number by what differs between the "01" and "02" textures. Two clusters of
        /// rows differ: the second digit and one dot. The digit is the taller run. The first digit
        /// is the same in both, so it is found by walking left from the second across the gap
        /// between them - far shorter than the gap to the grid line further left.
        /// </summary>
        public static FloorLayout Measure(byte[] two, byte[] one, int width, int height, out string why)
        {
            why = null;
            // Above 60, not merely "visibly different": the grid lines differ between the two
            // textures by exactly 60 everywhere (each floor's lines are a step brighter), and a
            // lower bar turns one long line into a "digit" a thousand rows tall.
            const int differs = 64;

            bool[] rowDiffers = new bool[height];
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (Math.Abs(two[row + x] - one[row + x]) > differs)
                    {
                        rowDiffers[y] = true;
                        break;
                    }
                }
            }

            int bestTop = -1, bestBottom = -1;
            for (int y = 0; y < height; y++)
            {
                if (!rowDiffers[y])
                {
                    continue;
                }

                // A thin horizontal stroke can leave a row or two where the two digits agree, so
                // short gaps do not end a run.
                int start = y;
                while (y + 1 < height && (rowDiffers[y + 1] ||
                                          y + 2 < height && rowDiffers[y + 2] ||
                                          y + 3 < height && rowDiffers[y + 3]))
                {
                    y++;
                }

                if (y - start > bestBottom - bestTop)
                {
                    bestTop = start;
                    bestBottom = y;
                }
            }

            if (bestTop < 0 || bestBottom - bestTop < 8)
            {
                why = bestTop < 0 ? "the two textures do not differ by more than " + differs : "tallest differing band is only " + (bestBottom - bestTop + 1) + " rows";
                return null;
            }

            int secondLeft = width, secondRight = -1;
            byte brightness = 0;
            for (int y = bestTop; y <= bestBottom; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (Math.Abs(two[i] - one[i]) > differs)
                    {
                        secondLeft = Math.Min(secondLeft, x);
                        secondRight = Math.Max(secondRight, x);
                    }
                }
            }

            for (int y = bestTop; y <= bestBottom; y++)
            {
                for (int x = secondLeft; x <= secondRight; x++)
                {
                    brightness = Math.Max(brightness, two[y * width + x]);
                }
            }

            if (secondRight < 0 || brightness < 16)
            {
                why = "second digit too dim (" + brightness + ")";
                return null;
            }

            int lit = brightness / 3;

            bool ColumnLit(int x)
            {
                for (int y = bestTop; y <= bestBottom; y++)
                {
                    if (two[y * width + x] > lit)
                    {
                        return true;
                    }
                }

                return false;
            }

            int cursor = secondLeft - 1;
            while (cursor >= 0 && !ColumnLit(cursor))
            {
                cursor--;
            }

            int firstRight = cursor;
            while (cursor >= 0 && ColumnLit(cursor))
            {
                cursor--;
            }

            int firstLeft = cursor + 1;
            int glyphWidth = firstRight - firstLeft + 1;
            if (firstRight < 0 || glyphWidth < 4 || secondLeft - firstRight > 3 * glyphWidth)
            {
                why = "no first digit left of x " + secondLeft + " (found " + firstLeft + ".." + firstRight + ", rows " + bestTop + ".." + bestBottom + ")";
                return null;
            }

            // The first digit is a "0": its left wall at mid-height is one stroke wide.
            int mid = (bestTop + bestBottom) / 2;
            int stroke = 0;
            for (int x = firstLeft; x <= firstRight && two[mid * width + x] > lit; x++)
            {
                stroke++;
            }

            return new FloorLayout
            {
                Top = bestTop,
                Bottom = bestBottom,
                FirstLeft = firstLeft,
                GlyphWidth = glyphWidth,
                // Vanilla's digits are monospaced and its "2" starts at the left of its cell (the
                // base runs the full width), so the pitch is simply the two left edges' distance.
                Pitch = secondLeft - firstLeft,
                EraseLeft = firstLeft,
                EraseRight = secondRight,
                Stroke = Math.Max(1.5f, stroke),
                Brightness = brightness,
            };
        }

        public byte MaxIn(byte[] pixels, int width)
        {
            byte max = 0;
            for (int y = Top; y <= Bottom; y++)
            {
                for (int x = EraseLeft; x <= EraseRight; x++)
                {
                    max = Math.Max(max, pixels[y * width + x]);
                }
            }

            return max;
        }

        /// <summary>The number sits on plain black, so rubbing it out is zeroing its box.</summary>
        public void Erase(byte[] pixels, int width)
        {
            const int pad = 4;
            for (int y = Math.Max(0, Top - pad); y <= Bottom + pad && y * width < pixels.Length; y++)
            {
                for (int x = Math.Max(0, EraseLeft - pad); x <= Math.Min(width - 1, EraseRight + pad); x++)
                {
                    pixels[y * width + x] = 0;
                }
            }
        }

        public void Draw(byte[] pixels, int width, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                StrokeFont.Draw(
                    pixels, width, pixels.Length / width, text[i],
                    FirstLeft + i * Pitch, Top, GlyphWidth, Bottom - Top + 1, Stroke, Brightness);
            }
        }

        public override string ToString() =>
            "rows " + Top + ".." + Bottom + ", first digit at x " + FirstLeft + " (" + GlyphWidth + " wide), pitch " +
            Pitch + ", stroke " + Stroke + ", brightness " + Brightness;
    }

    // ------------------------------------------------------------------------------------------
    // Space grid
    // ------------------------------------------------------------------------------------------

    private static Texture2DArray GridArray;
    private static Texture2DArray GridOriginal;
    private static GridBackdrop GridDigitBackdrop;

    /// <summary>The number the clamp slot currently shows; <see cref="GridBlank"/> while one is being drawn.</summary>
    private static int GridShowing = -1;

    private const int GridBlank = -2;

    /// <summary>The layer the player is looking at, which a finished background draw checks before showing itself.</summary>
    private static int GridWanted = -1;

    /// <summary>
    /// Finished and in-flight slices by layer, each a full mip chain ready to upload. Touched only
    /// on the main thread: the draws run on the pool, but their results land here through
    /// <see cref="MainThread"/>.
    /// </summary>
    private static readonly Dictionary<int, Task<byte[][]>> GridJobs = new Dictionary<int, Task<byte[][]>>();

    /// <summary>Jobs further than this from the viewed layer are dropped; about 1.4 MB each.</summary>
    private const int GridKeepWithin = 4;

    private static byte[][] GridBlankMips;
    private static TaskScheduler MainThread;

    /// <summary>
    /// Replaces the grid's layer-indicator array with a same-sized copy whose **last slice is
    /// rewritten to the layer being viewed**, and points every loaded material at it.
    ///
    /// One slice per layer is not needed, because the grid is only ever drawn for one layer:
    /// <c>HUDIslandGridVisualization.Draw</c> forces every chunk it draws to
    /// <c>Viewport.IslandLayer</c>, and <c>IslandsPreviewDrawer</c> draws at that layer too. And
    /// the array index past the end is clamped by the GPU, which used to be the bug - every layer
    /// above 2 read slice 2 - and is now the mechanism: every layer at or above the last slice
    /// reads it, so it only has to say the right number for whichever one is on screen.
    /// <see cref="ShowGridLayer"/> keeps it saying that.
    ///
    /// This replaced one generated slice per layer, at 1024² R8 with mips - about 1.4 MB each -
    /// which made the layer count a memory cost. Now it costs nothing per layer: three slices
    /// whatever the count, and one slice redrawn per layer change, off the main thread (see <see cref="ShowGridLayer"/>).
    /// </summary>
    public static void PrepareGridIndicator(VisualThemeBaseResources theme, ILogger logger)
    {
        Texture2DArray original = GridOriginal ?? FindGridIndicator(theme);
        if (original == null)
        {
            logger.Error?.Log("Extra Build Layers: no grid layer-indicator array found; space layers above 2 keep showing 2.");
            return;
        }

        GridOriginal = original;

        if (GridArray != null)
        {
            // Made in an earlier session. The theme's materials are shared assets and keep it, but
            // anything loaded since may still hold the vanilla array.
            Retarget(original, GridArray);
            return;
        }

        int width = original.width;
        int height = original.height;
        int depth = original.depth;
        byte[][] images = new byte[depth][];

        for (int i = 0; i < depth; i++)
        {
            images[i] = ReadSlice(original, i, width, height);
        }

        GridBackdrop backdrop = GridBackdrop.Measure(images[0], width, height);
        if (backdrop == null)
        {
            logger.Error?.Log("Extra Build Layers: could not find the digit in the grid indicator; space layers above " +
                              (depth - 1) + " keep showing " + (depth - 1) + ".");
            return;
        }

        logger.Info?.Log("Extra Build Layers: grid digit measured - " + backdrop);

        Texture2DArray live = new Texture2DArray(width, height, depth, TextureFormat.R8, mipChain: true, linear: true)
        {
            name = Prefix + original.name,
            wrapMode = original.wrapMode,
            filterMode = original.filterMode,
            anisoLevel = original.anisoLevel,
            mipMapBias = original.mipMapBias,
        };

        for (int i = 0; i < depth; i++)
        {
            live.SetPixelData(FlipRows(images[i], width, height), 0, i);
        }

        // Kept readable: the top slice is rewritten on every layer change, and SetPixelData needs
        // the CPU copy. Three slices of R8 is a few megabytes.
        live.Apply(updateMipmaps: true, makeNoLongerReadable: false);

        GridArray = live;
        GridDigitBackdrop = backdrop;
        GridShowing = depth - 1;
        GridJobs.Clear();

        int mips = live.mipmapCount;
        byte[][] topOriginal = MipChain(FlipRows(images[depth - 1], width, height), width, height, mips);
        GridJobs[depth - 1] = Task.FromResult(topOriginal);
        GridBlankMips = MipChain(FlipRows(backdrop.Render(string.Empty, width, height), width, height), width, height, mips);

        // Unity installs a synchronization context on the main thread, and this runs there
        // (inside GameMode.From). Without one, draws fall back to running inline.
        MainThread = SynchronizationContext.Current != null ? TaskScheduler.FromCurrentSynchronizationContext() : null;

        int retargeted = Retarget(original, live);
        logger.Info?.Log(
            "Extra Build Layers: grid layer indicator now numbered on demand (" + depth + " slices, the last " +
            "redrawn per layer), on " + retargeted + " material(s).");
    }

    /// <summary>
    /// Makes the grid read <paramref name="layer"/>. Must run on the main thread.
    ///
    /// Drawing a number is not cheap - every pixel of each digit's box is measured against every
    /// stroke of the glyph, then a mip chain is built - and doing it inline here was a visible
    /// hitch on every layer change. So it runs on the thread pool, the result is uploaded on the
    /// main thread when it lands, and the layers either side of the one on screen are drawn ahead,
    /// so stepping one layer at a time finds its number already waiting. A jump to a layer not yet
    /// drawn shows the backdrop with no number for the moment it takes.
    /// </summary>
    public static void ShowGridLayer(int layer)
    {
        if (GridArray == null || GridDigitBackdrop == null)
        {
            return;
        }

        int slot = GridArray.depth - 1;
        int wanted = Math.Max(layer, slot);
        GridWanted = wanted;

        if (wanted != GridShowing)
        {
            Task<byte[][]> job = Job(wanted);
            if (job.Status == TaskStatus.RanToCompletion)
            {
                Upload(wanted, job.Result);
            }
            else if (MainThread != null)
            {
                Upload(GridBlank, GridBlankMips);
                job.ContinueWith(done =>
                {
                    if (done.Status == TaskStatus.RanToCompletion && GridWanted == wanted && GridArray != null)
                    {
                        Upload(wanted, done.Result);
                    }
                }, MainThread);
            }
        }

        // Ahead of the player, and forget what is far behind.
        int top = Math.Max(slot, BuildLayers.Current?.TopSpaceLevel ?? slot);
        for (int near = wanted - 2; near <= wanted + 2; near++)
        {
            if (near >= slot && near <= top)
            {
                Job(near);
            }
        }

        List<int> far = null;
        foreach (KeyValuePair<int, Task<byte[][]>> entry in GridJobs)
        {
            if (entry.Key != slot && Math.Abs(entry.Key - wanted) > GridKeepWithin && entry.Value.IsCompleted)
            {
                (far ??= new List<int>()).Add(entry.Key);
            }
        }

        if (far != null)
        {
            foreach (int key in far)
            {
                GridJobs.Remove(key);
            }
        }
    }

    private static Task<byte[][]> Job(int layer)
    {
        if (GridJobs.TryGetValue(layer, out Task<byte[][]> existing) && !existing.IsFaulted)
        {
            return existing;
        }

        GridBackdrop backdrop = GridDigitBackdrop;
        int width = GridArray.width;
        int height = GridArray.height;
        int mips = GridArray.mipmapCount;
        Func<byte[][]> draw = () =>
            MipChain(FlipRows(backdrop.Render(layer.ToString(), width, height), width, height), width, height, mips);

        Task<byte[][]> job = MainThread != null ? Task.Run(draw) : Task.FromResult(draw());
        GridJobs[layer] = job;
        return job;
    }

    private static void Upload(int showing, byte[][] mips)
    {
        int slot = GridArray.depth - 1;
        for (int level = 0; level < mips.Length; level++)
        {
            GridArray.SetPixelData(mips[level], level, slot);
        }

        // Mips are already in the data; only the upload remains.
        GridArray.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        GridShowing = showing;
    }

    /// <summary>
    /// A box-filtered mip chain of <paramref name="levels"/> levels, sized as Unity sizes them
    /// (each dimension halved, never below 1). Built here rather than by <c>Apply(true)</c>
    /// because that runs on the main thread, for every slice of the array.
    /// </summary>
    private static byte[][] MipChain(byte[] level0, int width, int height, int levels)
    {
        byte[][] chain = new byte[levels][];
        chain[0] = level0;
        int w = width, h = height;
        for (int level = 1; level < levels; level++)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            byte[] src = chain[level - 1];
            byte[] dst = new byte[nw * nh];
            for (int y = 0; y < nh; y++)
            {
                int y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1);
                    int sum = src[y0 * w + x0] + src[y0 * w + x1] + src[y1 * w + x0] + src[y1 * w + x1];
                    dst[y * nw + x] = (byte)((sum + 2) / 4);
                }
            }

            chain[level] = dst;
            w = nw;
            h = nh;
        }

        return chain;
    }

    private static Texture2DArray FindGridIndicator(VisualThemeBaseResources theme)
    {
        foreach (MaterialReference reference in new[]
                 {
                     theme?.IslandGridHelperMaterial, theme?.IslandGridHelperMaterialCursor,
                     theme?.IslandGridHelperMaterialPreview, theme?.IslandGridHelperMaterialResourcePlacement,
                 })
        {
            Material material = reference?.GetMaterialInternal();
            if (material == null)
            {
                continue;
            }

            foreach (string name in material.GetTexturePropertyNames())
            {
                // Matched by what the property holds, not by its name: Shader Graph generates the
                // name (…_LayerIndicatorTexture_2_Texture2DArray) and would regenerate it on any
                // edit to the graph.
                if (material.GetTexture(name) is Texture2DArray array &&
                    array.name.IndexOf("LayerIndicator", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !array.name.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    return array;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Every loaded material, not just the theme's four: anything else that shows the indicator -
    /// a variant made at runtime, a material another mod cloned - should count past 2 as well.
    /// </summary>
    private static int Retarget(Texture2DArray from, Texture2DArray to)
    {
        int count = 0;
        foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
        {
            if (material == null)
            {
                continue;
            }

            foreach (string name in material.GetTexturePropertyNames())
            {
                if (material.GetTexture(name) == from)
                {
                    material.SetTexture(name, to);
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Slice 0 with its digit (and the small "SPZ2" label beside it) lifted off the glow behind
    /// them, plus what is needed to put a number back.
    ///
    /// The glow is not flat, so the digit cannot simply be zeroed. It is one smooth elliptical
    /// field - including inside the "0", which is not a separate fill - so the covered pixels are
    /// filled by relaxation: each repeatedly becomes the average of its neighbours, with the
    /// uncovered pixels around and inside the digit held fixed. Interpolating along rows alone was
    /// tried first and is wrong at the top and bottom of the "0", where a whole row is covered and
    /// the fill has nothing nearer than the far edges to go on; it left a visible box.
    /// "Covered" is brightness above most of the peak: the digit is at 255 and the label near 220
    /// while the glow peaks near 130.
    /// </summary>
    private sealed class GridBackdrop
    {
        private byte[] Backdrop;
        private byte[] Label;          // label pixels only, 0 elsewhere
        private int LabelLeft, LabelRight;
        private int GlyphLeft, GlyphRight, GlyphTop, GlyphBottom;
        private float Stroke;
        private byte Brightness;

        public static GridBackdrop Measure(byte[] slice, int width, int height)
        {
            byte peak = 0;
            foreach (byte b in slice)
            {
                peak = Math.Max(peak, b);
            }

            if (peak < 32)
            {
                return null;
            }

            int threshold = peak * 62 / 100;
            bool[] covered = new bool[slice.Length];
            for (int i = 0; i < slice.Length; i++)
            {
                covered[i] = slice[i] > threshold;
            }

            // Runs of covered columns. The digit is the run nearest the centre; anything else is
            // the label.
            bool[] columnCovered = new bool[width];
            for (int i = 0; i < slice.Length; i++)
            {
                if (covered[i])
                {
                    columnCovered[i % width] = true;
                }
            }

            List<(int left, int right)> runs = new List<(int, int)>();
            for (int x = 0; x < width; x++)
            {
                if (!columnCovered[x])
                {
                    continue;
                }

                int start = x;
                while (x + 1 < width && columnCovered[x + 1])
                {
                    x++;
                }

                runs.Add((start, x));
            }

            if (runs.Count == 0)
            {
                return null;
            }

            int centre = width / 2;
            (int left, int right) glyph = runs[0];
            foreach ((int left, int right) run in runs)
            {
                int distance = run.left <= centre && centre <= run.right ? 0 : Math.Min(Math.Abs(run.left - centre), Math.Abs(run.right - centre));
                int best = glyph.left <= centre && centre <= glyph.right ? 0 : Math.Min(Math.Abs(glyph.left - centre), Math.Abs(glyph.right - centre));
                if (distance < best)
                {
                    glyph = run;
                }
            }

            int top = height, bottom = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = glyph.left; x <= glyph.right; x++)
                {
                    if (covered[y * width + x])
                    {
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }

            int mid = (top + bottom) / 2;
            int stroke = 0;
            for (int x = glyph.left; x <= glyph.right && covered[mid * width + x]; x++)
            {
                stroke++;
            }

            int labelLeft = width, labelRight = -1;
            foreach ((int left, int right) run in runs)
            {
                if (run != glyph)
                {
                    labelLeft = Math.Min(labelLeft, run.left);
                    labelRight = Math.Max(labelRight, run.right);
                }
            }

            // Grow the cover by a few pixels so the anti-aliased fringe goes too.
            const int grow = 3;
            bool[] wide = new bool[slice.Length];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!covered[y * width + x])
                    {
                        continue;
                    }

                    for (int dy = -grow; dy <= grow; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= height)
                        {
                            continue;
                        }

                        for (int dx = -grow; dx <= grow; dx++)
                        {
                            int xx = x + dx;
                            if (xx >= 0 && xx < width)
                            {
                                wide[yy * width + xx] = true;
                            }
                        }
                    }
                }
            }

            byte[] backdrop = (byte[])slice.Clone();
            byte[] label = new byte[slice.Length];
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (!wide[row + x])
                    {
                        continue;
                    }

                    int start = x;
                    while (x + 1 < width && wide[row + x + 1])
                    {
                        x++;
                    }

                    int end = x;
                    float a = start > 0 ? slice[row + start - 1] : 0;
                    float b = end + 1 < width ? slice[row + end + 1] : a;
                    if (start == 0)
                    {
                        a = b;
                    }

                    for (int xx = start; xx <= end; xx++)
                    {
                        if (xx < glyph.left - grow || xx > glyph.right + grow)
                        {
                            label[row + xx] = slice[row + xx];
                        }

                        float t = (xx - start + 1f) / (end - start + 2f);
                        backdrop[row + xx] = (byte)Mathf.RoundToInt(Mathf.Lerp(a, b, t));
                    }
                }
            }

            Relax(backdrop, wide, width, height);

            // The label is kept as its brightness *over* the backdrop and added back, not pasted.
            // Pasted, it carries a rectangle of the glow from where it was to where it moves,
            // which shows as a box behind it once it is shifted to make room for two digits.
            for (int i = 0; i < label.Length; i++)
            {
                if (label[i] != 0)
                {
                    label[i] = (byte)Math.Max(0, slice[i] - backdrop[i]);
                }
            }

            return new GridBackdrop
            {
                Backdrop = backdrop,
                Label = label,
                LabelLeft = labelRight < 0 ? 0 : labelLeft,
                LabelRight = labelRight,
                GlyphLeft = glyph.left,
                GlyphRight = glyph.right,
                GlyphTop = top,
                GlyphBottom = bottom,
                Stroke = Math.Max(1.5f, stroke),
                Brightness = peak,
            };
        }

        /// <summary>
        /// A slice reading <paramref name="text"/>, centred where vanilla's digit was. One digit
        /// is drawn at vanilla's size. More are shrunk so they stay inside the glow, and the label
        /// moves left by however far the number now reaches past where the digit used to start.
        /// </summary>
        public byte[] Render(string text, int width, int height)
        {
            byte[] pixels = (byte[])Backdrop.Clone();

            int glyphWidth = GlyphRight - GlyphLeft + 1;
            int glyphHeight = GlyphBottom - GlyphTop + 1;
            float scale = text.Length <= 1 ? 1f : text.Length == 2 ? 0.8f : 0.6f;

            int w = Mathf.RoundToInt(glyphWidth * scale);
            int h = Mathf.RoundToInt(glyphHeight * scale);
            int gap = Mathf.RoundToInt(w * 0.25f);
            int total = text.Length * w + (text.Length - 1) * gap;
            int centreX = (GlyphLeft + GlyphRight) / 2;
            int centreY = (GlyphTop + GlyphBottom) / 2;
            int left = centreX - total / 2;
            int top = centreY - h / 2;

            for (int i = 0; i < text.Length; i++)
            {
                StrokeFont.Draw(pixels, width, height, text[i], left + i * (w + gap), top, w, h, Stroke * scale, Brightness);
            }

            if (LabelRight >= 0)
            {
                // Keep the label's original distance from the number's left edge.
                int shift = Math.Min(0, left - GlyphLeft);
                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    for (int x = LabelLeft - 4; x <= LabelRight + 4; x++)
                    {
                        if (x < 0 || x >= width || Label[row + x] == 0)
                        {
                            continue;
                        }

                        int to = x + shift;
                        if (to >= 0 && to < width)
                        {
                            pixels[row + to] = (byte)Math.Min(255, pixels[row + to] + Label[row + x]);
                        }
                    }
                }
            }

            return pixels;
        }

        /// <summary>
        /// Gauss-Seidel with over-relaxation, in a checkerboard so each half reads a settled other
        /// half. The row interpolation already in <paramref name="pixels"/> is the starting guess,
        /// which is close enough that a few hundred sweeps settle a stroke thirty pixels wide.
        /// Only the covered pixels' bounding box is swept, so this is a few hundred thousand
        /// operations per sweep, once per process.
        /// </summary>
        private static void Relax(byte[] pixels, bool[] covered, int width, int height)
        {
            int x0 = width, x1 = -1, y0 = height, y1 = -1;
            for (int i = 0; i < covered.Length; i++)
            {
                if (covered[i])
                {
                    int x = i % width, y = i / width;
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                }
            }

            if (x1 < 0)
            {
                return;
            }

            // Neighbours are read one pixel outside the box, so keep the box off the border.
            x0 = Math.Max(1, x0); y0 = Math.Max(1, y0);
            x1 = Math.Min(width - 2, x1); y1 = Math.Min(height - 2, y1);

            float[] field = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                field[i] = pixels[i];
            }

            const float omega = 1.9f;
            for (int sweep = 0; sweep < 400; sweep++)
            {
                for (int parity = 0; parity < 2; parity++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        int row = y * width;
                        for (int x = x0 + ((x0 + y + parity) & 1); x <= x1; x += 2)
                        {
                            int i = row + x;
                            if (!covered[i])
                            {
                                continue;
                            }

                            float average = (field[i - 1] + field[i + 1] + field[i - width] + field[i + width]) * 0.25f;
                            field[i] += omega * (average - field[i]);
                        }
                    }
                }
            }

            for (int i = 0; i < pixels.Length; i++)
            {
                if (covered[i])
                {
                    pixels[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(field[i]), 0, 255);
                }
            }
        }

        public override string ToString() =>
            "glyph x " + GlyphLeft + ".." + GlyphRight + ", y " + GlyphTop + ".." + GlyphBottom + ", stroke " + Stroke +
            ", brightness " + Brightness + (LabelRight >= 0 ? ", label x " + LabelLeft + ".." + LabelRight : ", no label");
    }

    // ------------------------------------------------------------------------------------------
    // GPU <-> CPU
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The red channel as top-down image rows. Unity's rows run bottom-up; flipping here means
    /// every measurement above reads like the dumped PNGs, and digits are drawn upright.
    /// </summary>
    private static byte[] ReadRed(Texture texture, int width, int height, bool srgb = false)
    {
        RenderTexture target = RenderTexture.GetTemporary(
            width, height, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        RenderTexture previous = RenderTexture.active;
        Texture2D readable = null;
        try
        {
            Graphics.Blit(texture, target);
            RenderTexture.active = target;
            readable = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false, linear: !srgb);
            readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            readable.Apply(false);

            Color32[] colours = readable.GetPixels32();
            byte[] red = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                int from = (height - 1 - y) * width;
                int to = y * width;
                for (int x = 0; x < width; x++)
                {
                    red[to + x] = colours[from + x].r;
                }
            }

            return red;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            if (readable != null)
            {
                UnityEngine.Object.Destroy(readable);
            }
        }
    }

    /// <summary>
    /// One slice of an array. A blit only ever reads slice 0, so the slice is first copied on the
    /// GPU into a plain texture of the array's own format - <c>CopyTexture</c> needs the formats to
    /// match, which they trivially do - and that is read back.
    /// </summary>
    private static byte[] ReadSlice(Texture2DArray array, int slice, int width, int height)
    {
        Texture2D single = new Texture2D(width, height, array.graphicsFormat, TextureCreationFlags.None);
        try
        {
            Graphics.CopyTexture(array, slice, 0, single, 0, 0);
            return ReadRed(single, width, height);
        }
        finally
        {
            UnityEngine.Object.Destroy(single);
        }
    }

    private static Texture2D Upload(byte[] topDown, int width, int height, Texture like, string name)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.R8, mipChain: true, linear: true)
        {
            name = name,
            wrapMode = like.wrapMode,
            filterMode = like.filterMode,
            anisoLevel = like.anisoLevel,
            mipMapBias = like.mipMapBias,
        };

        texture.SetPixelData(FlipRows(topDown, width, height), 0);
        texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
        return texture;
    }

    private static byte[] FlipRows(byte[] pixels, int width, int height)
    {
        byte[] flipped = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            Buffer.BlockCopy(pixels, y * width, flipped, (height - 1 - y) * width, width);
        }

        return flipped;
    }
}

/// <summary>
/// Ten digits as centre-line strokes, in the thin rounded face vanilla's numbers use: a "0" is a
/// stadium, a "2" an arc into a straight diagonal and a base. Defined in a box of the measured
/// glyph's own proportions, so arcs stay circular whatever the size, and drawn by distance to the
/// stroke with a one-pixel soft edge, brightening only - a stroke never darkens what is under it.
/// </summary>
internal static class StrokeFont
{
    public static void Draw(
        byte[] pixels, int width, int height, char digit,
        int left, int top, int glyphWidth, int glyphHeight, float stroke, byte brightness)
    {
        if (digit < '0' || digit > '9' || glyphHeight <= stroke || glyphWidth <= stroke)
        {
            return;
        }

        // The stroke's outer edge should land on the measured box, so its centre line runs half a
        // stroke inside it.
        float half = stroke / 2f;
        float scale = glyphHeight - stroke;
        float aspect = (glyphWidth - stroke) / scale;
        List<Vector2[]> lines = Glyph(digit - '0', aspect);

        List<(Vector2 a, Vector2 b)> segments = new List<(Vector2, Vector2)>();
        foreach (Vector2[] line in lines)
        {
            for (int i = 0; i + 1 < line.Length; i++)
            {
                segments.Add((
                    new Vector2(left + half + line[i].x * scale, top + half + line[i].y * scale),
                    new Vector2(left + half + line[i + 1].x * scale, top + half + line[i + 1].y * scale)));
            }
        }

        int x0 = Math.Max(0, left - 2), x1 = Math.Min(width - 1, left + glyphWidth + 2);
        int y0 = Math.Max(0, top - 2), y1 = Math.Min(height - 1, top + glyphHeight + 2);

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                float distance = float.MaxValue;
                foreach ((Vector2 a, Vector2 b) in segments)
                {
                    distance = Math.Min(distance, DistanceToSegment(p, a, b));
                }

                float coverage = Mathf.Clamp01(half + 0.5f - distance);
                if (coverage <= 0f)
                {
                    continue;
                }

                int i = y * width + x;
                byte value = (byte)Mathf.RoundToInt(coverage * brightness);
                if (value > pixels[i])
                {
                    pixels[i] = value;
                }
            }
        }
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lengthSq = ab.sqrMagnitude;
        float t = lengthSq < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / lengthSq);
        return Vector2.Distance(p, a + t * ab);
    }

    /// <summary>
    /// Centre lines in a box <paramref name="a"/> wide and 1 tall, y down. Angles are degrees in
    /// that frame: 270 is the top of a circle, 90 the bottom, 180 the left.
    /// </summary>
    private static List<Vector2[]> Glyph(int digit, float a)
    {
        float r = a / 2f;
        float upper = Math.Min(r * 0.88f, 0.24f);
        float lower = Math.Min(r, 0.28f);
        List<Vector2[]> lines = new List<Vector2[]>();

        switch (digit)
        {
            case 0:
                lines.Add(Join(
                    Arc(r, r, r, 180, 360),
                    Arc(r, 1 - r, r, 0, 180),
                    new[] { new Vector2(0, 1 - r), new Vector2(0, r) }));
                break;
            case 1:
                lines.Add(new[] { new Vector2(0.15f * a, 0.2f), new Vector2(0.62f * a, 0), new Vector2(0.62f * a, 1) });
                break;
            case 2:
            {
                Vector2[] arc = Arc(r, r, r, 180, 385);
                lines.Add(Join(arc, new[] { new Vector2(0, 1), new Vector2(a, 1) }));
                break;
            }
            case 3:
                lines.Add(Arc(r, upper, upper, 200, 450));
                lines.Add(Arc(r, 1 - lower, lower, -90, 160));
                break;
            case 4:
                lines.Add(new[] { new Vector2(0.72f * a, 1), new Vector2(0.72f * a, 0), new Vector2(0, 0.68f), new Vector2(a, 0.68f) });
                break;
            case 5:
            {
                Vector2[] bowl = Arc(r, 1 - lower, lower, -140, 150);
                lines.Add(Join(new[] { new Vector2(a, 0), new Vector2(0.04f * a, 0), new Vector2(0.02f * a, 0.45f) }, bowl));
                break;
            }
            case 6:
            {
                Vector2[] circle = Arc(r, 1 - r, r, 0, 360);
                lines.Add(circle);
                lines.Add(new[] { new Vector2(0.8f * a, 0), Point(r, 1 - r, r, 200) });
                break;
            }
            case 7:
                lines.Add(new[] { new Vector2(0, 0), new Vector2(a, 0), new Vector2(0.3f * a, 1) });
                break;
            case 8:
                lines.Add(Arc(r, upper, upper, 0, 360));
                lines.Add(Arc(r, 1 - lower, lower, 0, 360));
                break;
            case 9:
                foreach (Vector2[] line in Glyph(6, a))
                {
                    Vector2[] turned = new Vector2[line.Length];
                    for (int i = 0; i < line.Length; i++)
                    {
                        turned[i] = new Vector2(a - line[i].x, 1 - line[i].y);
                    }

                    lines.Add(turned);
                }

                break;
        }

        return lines;
    }

    private static Vector2 Point(float cx, float cy, float radius, float degrees)
    {
        float t = degrees * Mathf.Deg2Rad;
        return new Vector2(cx + radius * Mathf.Cos(t), cy + radius * Mathf.Sin(t));
    }

    private static Vector2[] Arc(float cx, float cy, float radius, float from, float to)
    {
        int steps = Math.Max(4, Mathf.CeilToInt(Math.Abs(to - from) / 10f));
        Vector2[] points = new Vector2[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            points[i] = Point(cx, cy, radius, Mathf.Lerp(from, to, i / (float)steps));
        }

        return points;
    }

    private static Vector2[] Join(params Vector2[][] parts)
    {
        List<Vector2> all = new List<Vector2>();
        foreach (Vector2[] part in parts)
        {
            all.AddRange(part);
        }

        return all.ToArray();
    }
}
